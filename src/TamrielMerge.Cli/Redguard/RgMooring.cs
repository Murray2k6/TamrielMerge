using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Redguard;

/// <summary>
/// Ships moored at Redguard's docks, with gangplanks up to their decks (user, 2026-11-06: "the redguards ships are too far
/// away from the docks and too close", "need ramps for the ships too as they are tall"). The tool's ship stands in for each
/// of Redguard's boats at its footprint (PlaceShip), scaled to its length; its hull is wider and its deck far higher than
/// the boat's, so beside a dock it stood off (the harbour's arrival ship 620 units, the lighthouse's 469) or through it (a
/// harbour dock 185 units inside a hull, the hideout's posts 114). `ship-gaps` measures it.
/// <list type="bullet">
/// <item>Each ship with a dock beside its hull (a placed model's collision between the water and its deck, alongside its
/// length, within <see cref="DockReach"/>) slides sideways, its heading kept, until its hull stands
/// <see cref="MooringGap"/> from the dock on the nearer side, and no nearer than that to anything on the other (the other
/// ships too); and along its length as little as it takes for a gangplank to fit by its middle.</item>
/// <item>A gangplank then runs from the dock's top up to the top of the ship's side, and a short one from there down onto
/// its deck, at the middle of the stretch where the dock is alongside: planks of Skyrim's wood with collision, each its own
/// model sized to its run, no steeper than <see cref="MaxSlopeDegrees"/>, and part of the navmesh.</item>
/// </list>
/// Moored before anyone is placed, so the people over a ship stand on its deck where it now lies.
/// </summary>
public sealed partial class RgPluginBuilder
{
    /// <summary>How far a moored ship's hull stands from its dock.</summary>
    const float MooringGap = 24;
    /// <summary>How far from a hull a dock is looked for.</summary>
    const float DockReach = 1200;
    /// <summary>The steepest a gangplank climbs (Skyrim's people walk a 50-degree navmesh; a plank this steep reads as one).</summary>
    const float MaxSlopeDegrees = 30;
    /// <summary>The steepest a gangplank climbs where the dock is too narrow for <see cref="MaxSlopeDegrees"/> (the navmesh
    /// takes 50).</summary>
    const float MaxNarrowSlopeDegrees = 45;
    /// <summary>A plank's width: Recast keeps an actor's radius (24) off a plank's open sides, and a 96-wide plank laid off the
    /// grid came out in bits too small to keep (navmesh-ramp-test: 8% of it at 6.5 degrees, a piece left out); 128 holds at
    /// every angle tried (33-46%, one piece with the dock).</summary>
    const float PlankWidth = 128, PlankThickness = 8;
    static readonly BlockMeshBuilder.Tex PlankTex = new(@"textures\architecture\riften\riftencanalplanks01.dds", @"textures\architecture\riften\riftencanalplanks01_n.dds", true);

    /// <summary>How far each moored ship was moved from where Redguard's boat lay (what stood on the boat goes with it).</summary>
    readonly Dictionary<FormKey, Vector2> shipMoves = [];
    public int ShipsMoored { get; private set; }
    public int Gangplanks { get; private set; }
    int gangplankCount;

    void MoorShips(Scene scene, Action<string>? log)
    {
        // The opening's ships are at sea, as Redguard has them.
        if (ship is null || scene.Name == OpeningScene) return;
        var ships = placedShips.Where(s => s.Scene == scene.Name).Select(s => s.Ref).ToList();
        if (ships.Count == 0) return;
        // The ship's hull below its main deck, from above (model space), and the top of its side by the deck.
        var hullModel = ConvexHull(ship.Solid.SelectMany(t => t).Where(v => v.Z > 0 && v.Z <= ship.Deck + 16).Select(v => new Vector2(v.X, v.Y)).ToList());
        // Every other model's collision in the scene, placed: what a ship may lie against.
        var others = scene.Cells.Values.SelectMany(c => c.Temporary.OfType<PlacedObject>())
            .Concat(scene.Top?.Persistent.OfType<PlacedObject>() ?? [])
            .Where(p => p.Placement is not null && !ships.Contains(p) && meshOf.TryGetValue(p.FormKey, out var m)
                && !m.Path.Contains("_moving", StringComparison.OrdinalIgnoreCase) && !m.Path.Contains("_item", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Every solid point round about (each edge every 16 units), and whether it is a dock's: a ship keeps clear of all of
        // them (a post with no flat top stood 50 units inside a hull when only floors were looked at) and moors to a dock.
        var dockPoints = new List<(Vector3 P, bool Floor)>();
        var dockFloors = new List<Vector3[]>();   // the docks' floors (level faces), placed
        foreach (var p in others)
        {
            var mesh = meshOf[p.FormKey];
            bool isDock = Path.GetFileNameWithoutExtension(mesh.Path).Contains("dock", StringComparison.OrdinalIgnoreCase);
            var m = RgSpace.SkyrimMatrix(new Vector3(p.Placement!.Rotation.X, p.Placement.Rotation.Y, p.Placement.Rotation.Z));
            var pos = new Vector3(p.Placement.Position.X, p.Placement.Position.Y, p.Placement.Position.Z);
            float sc = p.Scale ?? 1;
            foreach (var poly in mesh.Solid)
            {
                // A dock is a floor: level faces only (not a wall, a post's side, a shark's fin).
                if (poly.Length < 3) continue;
                var wn = Vector3.Cross(Vector3.Transform(poly[1], m) - Vector3.Transform(poly[0], m), Vector3.Transform(poly[2], m) - Vector3.Transform(poly[0], m));
                // ...and big enough to stand on (a pole's top, about 1,000, is not a dock: the hideout's took a foot 101 off the navmesh;
                // the lighthouse dock's planks are about 4,000 each).
                float area = 0;
                for (int k = 1; k + 1 < poly.Length; k++) area += Vector3.Cross(poly[k] * sc - poly[0] * sc, poly[k + 1] * sc - poly[0] * sc).Length() / 2;
                // ...and a dock's (Redguard's dock models: HBDOCK, LHDOCK, HIDOCK): a ship once moored to a building's floor in
                // the hideout's bay, 1,013 units from where Redguard anchored it.
                bool floor = isDock && wn.LengthSquared() >= 1e-6f && MathF.Abs(Vector3.Normalize(wn).Z) >= 0.7f && area >= 2048;
                if (floor) dockFloors.Add(poly.Select(v => Vector3.Transform(v * sc, m) + pos).ToArray());
                for (int k = 0; k < poly.Length; k++)
                {
                    var a = Vector3.Transform(poly[k] * sc, m) + pos;
                    var b = Vector3.Transform(poly[(k + 1) % poly.Length] * sc, m) + pos;
                    int n = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 16));
                    for (int s = 0; s < n; s++) dockPoints.Add((Vector3.Lerp(a, b, s / (float)n), floor));
                }
            }
        }

        // A ship's hull from above, placed: its outline (each edge every 16 units) and inside it every 32 (the other ships
        // are in a ship's way too, and a smaller one fitted wholly inside a bigger one's outline, 2026-11-06).
        List<Vector2> Outline(PlacedObject s)
        {
            float sc = s.Scale ?? 1, h = s.Placement!.Rotation.Z;
            var f = new Vector2(MathF.Sin(h), MathF.Cos(h)); var sd = new Vector2(f.Y, -f.X);
            var at = new Vector2(s.Placement.Position.X, s.Placement.Position.Y);
            var pts = new List<Vector2>();
            for (int k = 0; k < hullModel.Count; k++)
            {
                var a = hullModel[k]; var b = hullModel[(k + 1) % hullModel.Count];
                int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) * sc / 16));
                for (int i = 0; i < n; i++) { var v = Vector2.Lerp(a, b, i / (float)n) * sc; pts.Add(at + sd * v.X + f * v.Y); }
            }
            float lo = hullModel.Min(v => v.Y), hi = hullModel.Max(v => v.Y);
            for (float y = lo; y <= hi; y += 32 / sc)
            {
                float port = HalfWidth(hullModel, y, -1), star = HalfWidth(hullModel, y, 1);
                for (float x = -port; x <= star; x += 32 / sc) pts.Add(at + sd * (x * sc) + f * (y * sc));
            }
            return pts;
        }

        foreach (var r in ships)
        {
            float scale = r.Scale ?? 1;
            var c = new Vector2(r.Placement!.Position.X, r.Placement.Position.Y);
            float heading = r.Placement.Rotation.Z;
            // The ship's bow (+Y in its model) and its starboard (+X), placed.
            var fwd = new Vector2(MathF.Sin(heading), MathF.Cos(heading));
            var side = new Vector2(fwd.Y, -fwd.X);
            float deckZ = r.Placement.Position.Z + ship.Deck * scale;
            // A dock may stand higher than a small ship's deck now that the ships sit in the water to their waterline
            // (2026-11-10: the hideout's two moored ships lost their docks and gangplanks, their decks at 103 and 155 under
            // docks at 163 and 195): docks and what is in the hull's way count up to where the deck stood before.
            float reachZ = deckZ + ShipModel.Draught(ship.Deck) * scale;
            // The hull's half width at a place along it, each side (+1 starboard, -1 port), and its length.
            float minAlong = hullModel.Min(h => h.Y) * scale, maxAlong = hullModel.Max(h => h.Y) * scale, length = maxAlong - minAlong;
            float Half(float along, int sideSign) => HalfWidth(hullModel, along / scale, sideSign) * scale;
            // What may stand by it wherever it goes (slid up to its length along and DockReach across): solid points between
            // the water and a little over the deck, the other ships' hulls among them (where they lie now), and the docks' floors.
            float range = length / 2 + length + DockReach + 400;
            var nearPoints = dockPoints.Where(p => p.P.Z >= 32 && p.P.Z <= reachZ + 64 && Vector2.Distance(new Vector2(p.P.X, p.P.Y), c) <= range)
                .Concat(ships.Where(o => o != r).SelectMany(Outline).Where(q => Vector2.Distance(q, c) <= range).Select(q => (P: new Vector3(q.X, q.Y, 64), Floor: false)))
                .ToList();
            var nearFloors = dockFloors.Where(f => f.Average(v => v.Z) is var z && z >= 32 && z <= reachZ && f.Any(v => Vector2.Distance(new Vector2(v.X, v.Y), c) <= range + 2048)).ToList();
            // The top of the docks' floors at a point (NaN: no dock there).
            float FloorZAt(Vector2 q) => nearFloors.Where(f => InPolygon(q, f)).Select(f => f.Average(v => v.Z)).DefaultIfEmpty(float.NaN).Max();
            float middle = length / 10;   // the middle four fifths of its length hold a gangplank
            // Where a gangplank's foot stands at a place along a ship lying at m with the dock on one side: the first dock
            // floor out from the hull (within 320) with a plank's width and an actor's radius of it either way at one height
            // (2026-11-06: the hideout's was put by the end of its pier, past its last floor, and had no navmesh under it).
            (float Z, float Gap)? PlankFoot(Vector2 m, int dockSide, float al)
            {
                var outward = side * dockSide;
                var edge = m + fwd * al + outward * Half(al, dockSide);
                for (float g = 0; g <= 320; g += 16)
                {
                    var q = edge + outward * (g + 40);
                    float z0 = FloorZAt(q);
                    if (float.IsNaN(z0)) continue;
                    float za = FloorZAt(q + fwd * (PlankWidth / 2 + 24)), zb = FloorZAt(q - fwd * (PlankWidth / 2 + 24));
                    if (MathF.Abs(za - z0) <= 24 && MathF.Abs(zb - z0) <= 24) return (z0, g);
                }
                return null;
            }
            // A gangplank's place at a place along a ship lying at m: its foot's height and gap, the rail's height and how far
            // out it is, the height over the rail to clear, and the ship's own points in the way of its path outboard. What
            // stands on the ship there is the same wherever it lies (kept, per place along and side).
            var railAt = new Dictionary<(float, int), (float RailTop, float RailAcross, float Over, List<Vector3> Out)>();
            (float Along, float DockZ, float RailZ, float Over, float RailAcross, int InWay, float Gap)? Spot(Vector2 m, int dockSide, float al)
            {
                // Its foot on a dock's floor with the plank's width of it (the floor right by the hull, not a wall's top further
                // off: the lighthouse's put a foot 125 off the navmesh).
                if (PlankFoot(m, dockSide, al) is not { } foot0) return null;
                if (!railAt.TryGetValue((al, dockSide), out var ra))
                {
                    float halfHere = Half(al, dockSide);
                    float railTopModel = RailTop(al / scale, dockSide, halfHere / scale) ?? ship.Deck;
                    float railAcrossModel = RailOuter(al / scale, dockSide, railTopModel) ?? halfHere / scale;
                    // The lowest height from the rail's top up with a body's height (128) clear over it there, a plank's width
                    // (a bar 46 units over the big ship's rail left no headroom over a landing on it, and with the landing over
                    // the bar its shrouds were still over it: the navmesh stopped short of it, 2026-11-06).
                    float overModel = ClearOver(al / scale, dockSide, railAcrossModel, railTopModel, (PlankWidth / 2 + 16) / scale, 136 / scale);
                    var outPts = ShipPoints().Where(v => MathF.Abs(v.Y - al / scale) < (PlankWidth / 2 + 16) / scale && v.X * dockSide > railAcrossModel).ToList();
                    railAt[(al, dockSide)] = ra = (railTopModel, railAcrossModel, overModel, outPts);
                }
                // What stands in the way over the plank's path outboard of the rail (the ship's fittings outside its side):
                // points within a body's height over a plank going down at 35 degrees from there.
                float tan = MathF.Tan(35 * MathF.PI / 180), over = ra.Over, railAcross = ra.RailAcross;
                float runModel = (over + PlankThickness / scale - (foot0.Z - r.Placement.Position.Z) / scale) / tan;
                int inWay = ra.Out.Count(v => v.X * dockSide < railAcross + runModel && v.Z > over - (v.X * dockSide - railAcross) * tan
                    && v.Z < over - (v.X * dockSide - railAcross) * tan + 136 / scale);
                return (al, foot0.Z, r.Placement.Position.Z + ra.RailTop * scale, (over - ra.RailTop) * scale, railAcross * scale, inWay, foot0.Gap);
            }
            // Whether a ship lying at m has a place for a gangplank in the middle of its length (clear: with nothing of the
            // ship in its way).
            bool HasPlank(Vector2 m, int dockSide, bool clear)
            {
                for (float al = minAlong + middle; al <= maxAlong - middle; al += 16)
                    if (Spot(m, dockSide, al) is { } sp && (!clear || sp.InWay == 0)) return true;
                return false;
            }
            // Lying with its middle at cu (its heading kept), where it moors: the side it moves to and how far.
            // Where it may lie: moved t toward its starboard, a dock point at a across sits inside it, or nearer than the gap,
            // while a - halfStarboard - gap < t < a + halfPort + gap. Of the places outside all of those, the nearest where it
            // touches a dock at the gap (an end of one): moored, and off any pier it lay across (2026-11-06: one lay over the
            // harbour pier its boat was moored beside, the pier 261-281 units inside it on both sides).
            (float Shift, int DockSide, List<(float Along, float Across, Vector3 P, bool Floor)> Spots)? Solve(Vector2 cu)
            {
                var spots = new List<(float Along, float Across, Vector3 P, bool Floor)>();
                foreach (var (p, isFloor) in nearPoints)
                {
                    var d = new Vector2(p.X, p.Y) - cu;
                    float along = Vector2.Dot(d, fwd), across = Vector2.Dot(d, side);
                    // Its whole length, ends too (a pier post at the bow was 50 units in when the last 32 were left out).
                    if (along < minAlong || along > maxAlong) continue;
                    if (MathF.Abs(across) - Half(along, across >= 0 ? 1 : -1) <= DockReach) spots.Add((along, across, p, isFloor));
                }
                if (!spots.Any(s => s.Floor)) return null;
                var bad = spots.Select(s => (Lo: s.Across - Half(s.Along, 1) - MooringGap, Hi: s.Across + Half(s.Along, -1) + MooringGap, s.Floor)).OrderBy(i => i.Lo).ToList();
                var merged = new List<(float Lo, float Hi, bool Floor)>();
                foreach (var i in bad)
                    if (merged.Count > 0 && i.Lo <= merged[^1].Hi) merged[^1] = (merged[^1].Lo, MathF.Max(merged[^1].Hi, i.Hi), merged[^1].Floor || i.Floor);
                    else merged.Add(i);
                // Against a dock only (what it touches there holds a dock's floor): pushed off things that aren't docks, a ship
                // went out to sea (2026-11-06, the hideout's: 946 units further off).
                var touching = merged.Where(mg => mg.Floor).SelectMany(mg => new[] { (T: mg.Lo, Side: 1), (T: mg.Hi, Side: -1) })
                    .Where(x => !merged.Any(o => x.T > o.Lo && x.T < o.Hi)).ToList();
                if (touching.Count == 0) return null;
                var (t, sd) = touching.MinBy(x => MathF.Abs(x.T));
                return (t, sd, spots);
            }
            // Slid along its length as little as it takes too (moved the least in all), when a dock is beside it where it lies:
            // sideways only, the hideout's ship lay with just its stern by the end of the pier and the lighthouse's by the
            // dock's corner, no gangplank at either (2026-11-06). Of the places it can moor, the nearest with room for a
            // gangplank by its middle with nothing of the ship in its way; else the nearest with room for one; else the nearest.
            // A ship with no dock beside it stays at sea (one 1,824 units off the end of the hideout's pier slid onto it, over
            // the ship moored there).
            var here = Solve(c);
            if (here is not { } h0)
            {
                log?.Invoke(nearPoints.Any(p => p.Floor && Vector2.Distance(new Vector2(p.P.X, p.P.Y), c) <= length / 2 + DockReach)
                    ? $"ship at {r.Placement.Position}: no place against a dock clear of everything, left where it lay"
                    : $"ship at {r.Placement.Position}: no dock beside it, left at sea");
                continue;
            }
            (float Slide, float Shift, int DockSide, List<(float Along, float Across, Vector3 P, bool Floor)> Spots)? clearPick = null, anyPick = null;
            float clearMoved = float.MaxValue, anyMoved = float.MaxValue;
            for (int step = 0; step * 16 <= length; step++)
            {
                if (step * 16 >= clearMoved) break;
                foreach (float u in step == 0 ? new[] { 0f } : new[] { step * 16f, -step * 16f })
                {
                    var cu = c + fwd * u;
                    if ((u == 0 ? here : Solve(cu)) is not { } sol) continue;
                    float total = MathF.Abs(u) + MathF.Abs(sol.Shift);
                    var at = cu + side * sol.Shift;
                    if (total < clearMoved && HasPlank(at, sol.DockSide, true)) { clearPick = (u, sol.Shift, sol.DockSide, sol.Spots); clearMoved = total; }
                    if (total < anyMoved && HasPlank(at, sol.DockSide, false)) { anyPick = (u, sol.Shift, sol.DockSide, sol.Spots); anyMoved = total; }
                }
            }
            var chosen = clearPick ?? anyPick ?? (0, h0.Shift, h0.DockSide, h0.Spots);
            var (slide, shift, dockSide, spots) = chosen;
            float before = spots.Where(s => s.Floor).Select(s => dockSide * s.Across - Half(s.Along, dockSide)).Where(g => g > -DockReach).DefaultIfEmpty(0).Min();
            var moved = c + fwd * slide + side * shift;
            r.Placement.Position = new P3Float(moved.X, moved.Y, r.Placement.Position.Z);
            shipMoves[r.FormKey] = moved - c;
            ShipsMoored++;
            log?.Invoke($"ship at ({c.X:F0},{c.Y:F0}): dock {before:F0} off its {(dockSide > 0 ? "starboard" : "port")} side; moved {MathF.Abs(shift):F0} to {MooringGap:F0} from it"
                + (slide != 0 ? $" and {MathF.Abs(slide):F0} {(slide > 0 ? "ahead" : "astern")} to lie along it" : ""));
            c = moved;

            // The gangplank: in the middle of the stretch the dock runs along (within 320 of the hull now), kept to the middle
            // four fifths of the ship.
            var alongside = spots.Where(s => s.Floor && dockSide * (s.Across - shift) - Half(s.Along, dockSide) is var g && g >= 0 && g <= 320)
                .Select(s => (s.Along, s.P, Gap: dockSide * (s.Across - shift) - Half(s.Along, dockSide))).ToList();
            if (alongside.Count == 0) { log?.Invoke($"  no dock within 320 of its side: no gangplank"); continue; }
            float median = alongside.Select(b => b.Along).OrderBy(a => a).ElementAt(alongside.Count / 2);
            // The middle four fifths of its length (in the middle two thirds, the lighthouse's dock was never alongside).
            float third = middle;
            var outward = side * dockSide;
            // Where along the side to put it: in the middle two thirds, where the dock is alongside, the place with the least
            // over the rail (a bar 46 units over this ship's rail, on most of its length, left no headroom over a landing on
            // the rail: the navmesh broke there, 2026-11-06); of equals, the one nearest the middle of the dock's stretch.
            (float Along, float DockZ, float RailZ, float Over, float RailAcross, int InWay, float Gap)? best = null;
            for (float al = minAlong + third; al <= maxAlong - third; al += 16)
            {
                if (Spot(c, dockSide, al) is not { } cand) continue;
                if (best is not { } bb || cand.InWay < bb.InWay || (cand.InWay == bb.InWay && (cand.Over < bb.Over - 1
                    || (MathF.Abs(cand.Over - bb.Over) <= 1 && MathF.Abs(al - median) < MathF.Abs(bb.Along - median))))) best = cand;
            }
            if (best is not { } g0) { log?.Invoke($"  no dock top by the ship's middle: no gangplank"); continue; }
            // How far off the dock's floor is there (Redguard's own gangway can stand between them: the harbour slip's dock is
            // 225 off): 40 past it is on the floor.
            var (mid, dockZ, railZ, overRail, railAcross, inWayHere, gapHere) = g0;
            if (inWayHere > 0) log?.Invoke($"  the gangplank's way has {inWayHere} points of the ship over it at its best place");
            float half = Half(mid, dockSide);
            var edge = c + fwd * mid + side * dockSide * half;
            var railEdge = c + fwd * mid + side * dockSide * railAcross;
            // Over the rail at its top, or over what stands on it when that leaves no room to pass under.
            float landZ = (overRail > 4 ? railZ + overRail : railZ) + PlankThickness;
            // Its foot on the dock: no further out than the dock's floor at its height reaches (a foot past it hung over the
            // water and the plank crossed over the dock instead of resting on it, with no navmesh on it), steeper where the
            // dock is narrow, up to MaxNarrowSlopeDegrees: measured from the plank's top, just outside the rail.
            bool OnDock(Vector2 q) => dockFloors.Any(f => MathF.Abs(f.Average(v => v.Z) - dockZ) <= 24 && InPolygon(q, f));
            float reach = gapHere + 24;
            while (reach < 800 && OnDock(edge + outward * (reach + 16))) reach += 16;
            float edgeOut = half - railAcross - 16;   // from the plank's top out to the hull's widest
            float rise = landZ - dockZ;
            float run = MathF.Max(edgeOut + gapHere + 64, rise / MathF.Tan(MaxSlopeDegrees * MathF.PI / 180));
            run = MathF.Min(run, MathF.Max(edgeOut + gapHere + 64, edgeOut + reach - 16));
            run = MathF.Max(run, rise / MathF.Tan(MaxNarrowSlopeDegrees * MathF.PI / 180));
            var outTop = new Vector3(railEdge.X + outward.X * 16, railEdge.Y + outward.Y * 16, landZ);
            var foot = new Vector3(outTop.X + outward.X * run, outTop.Y + outward.Y * run, dockZ);
            if (landZ <= deckZ + 16)
            {
                PlaceGangplank(scene, foot, new Vector3(railEdge.X - outward.X * 32, railEdge.Y - outward.Y * 32, deckZ + PlankThickness), log);
                continue;
            }
            // The outer plank up to just outside the rail, a landing over it, and a plank down onto the deck (2026-11-06:
            // ending where the hull is widest, the planks crossed the rail, its sides leaning in, and the navmesh broke).
            var inTop = new Vector3(railEdge.X - outward.X * 32, railEdge.Y - outward.Y * 32, landZ);
            PlaceGangplank(scene, foot, outTop, log);
            PlaceGangplank(scene, outTop, inTop, log);
            // Down from the landing onto the deck, as steep as the outer plank at most.
            float down = landZ - PlankThickness - deckZ;
            float inRun = MathF.Max(48, down / MathF.Tan(MaxSlopeDegrees * MathF.PI / 180));
            var inner = new Vector3(inTop.X - outward.X * inRun, inTop.Y - outward.Y * inRun, deckZ + PlankThickness);
            PlaceGangplank(scene, inner, inTop, log);
        }
    }

    /// <summary>A plank from <paramref name="low"/> to <paramref name="high"/> (its top face through both, Skyrim units):
    /// its own model, placed at <paramref name="low"/>, a static with collision, and its faces in the navmesh's input.</summary>
    void PlaceGangplank(Scene scene, Vector3 low, Vector3 high, Action<string>? log)
    {
        var dir = Vector3.Normalize(high - low);
        var across = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitZ));
        var up = Vector3.Cross(across, dir);
        if (up.Z < 0) up = -up;
        Vector3 P(float t, float s, float h) => (high - low) * t + across * (s * PlankWidth / 2) - up * (h * PlankThickness);
        Vector3[] Q(params Vector3[] v) => v;
        var polys = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>
        {
            (Q(P(0, -1, 0), P(1, -1, 0), P(1, 1, 0), P(0, 1, 0)), up, PlankTex),
            (Q(P(0, -1, 1), P(0, 1, 1), P(1, 1, 1), P(1, -1, 1)), -up, PlankTex),
            (Q(P(0, -1, 0), P(0, -1, 1), P(1, -1, 1), P(1, -1, 0)), -across, PlankTex),
            (Q(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1)), across, PlankTex),
            (Q(P(0, -1, 0), P(0, 1, 0), P(0, 1, 1), P(0, -1, 1)), -dir, PlankTex),
            (Q(P(1, -1, 0), P(1, -1, 1), P(1, 1, 1), P(1, 1, 0)), dir, PlankTex),
        };
        string rel = $@"tamrielmerge\rg\gangplank{++gangplankCount}.nif";
        var made = BlockMeshBuilder.BuildPolygons(polys, Path.Combine(modOutDir, "meshes", rel));
        var stat = new Static(mod)
        {
            EditorID = $"RGGangplank{gangplankCount}",
            Model = new Model { File = rel },
            ObjectBounds = new ObjectBounds
            {
                First = new P3Int16((short)MathF.Floor(made.Min.X), (short)MathF.Floor(made.Min.Y), (short)MathF.Floor(made.Min.Z)),
                Second = new P3Int16((short)MathF.Ceiling(made.Max.X), (short)MathF.Ceiling(made.Max.Y), (short)MathF.Ceiling(made.Max.Z)),
            },
        };
        mod.Statics.Add(stat);
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(stat.FormKey),
            Placement = new Placement { Position = new P3Float(low.X, low.Y, low.Z), Rotation = new P3Float(0, 0, 0) },
        };
        CellFor(scene, low).Temporary.Add(placed);
        // Its faces for the navmesh (placed unturned at its low end, as Solids places them).
        // Each wound to face its own way (Recast takes a face's side from its winding: listed clockwise, the plank's top was
        // a ceiling to it and had no navmesh, 2026-11-06).
        var faces = polys.Select(p => Vector3.Dot(Vector3.Cross(p.Pts[1] - p.Pts[0], p.Pts[2] - p.Pts[0]), p.N) < 0 ? p.Pts.Reverse().ToArray() : p.Pts).ToList();
        meshOf[placed.FormKey] = new RgMeshConverter.Result(rel, made.Min, made.Max, faces, faces.Count, [], [], faces);
        Gangplanks++;
        float len = Vector3.Distance(low, high), slope = MathF.Asin(MathF.Abs(high.Z - low.Z) / len) * 180 / MathF.PI;
        log?.Invoke($"  gangplank {gangplankCount}: {len:F0} long from {low.Z:F0} up to {high.Z:F0} ({slope:F0} degrees)");
    }

    /// <summary>The top of the ship's side (model space) at a place along it on one side: its collision's highest point
    /// within 64 of there along, in the outer 48 units of its width, between the deck and 120 over it (higher is its rigging:
    /// at 200 the shrouds were taken for its side, 130 units over the deck); null without one.</summary>
    List<Vector3>? shipPoints;
    /// <summary>The ship's collision as points (model space): its corners and its edges every 16 units (a long bar has
    /// corners only at its ends).</summary>
    List<Vector3> ShipPoints()
    {
        if (shipPoints is not null) return shipPoints;
        shipPoints = [];
        foreach (var t in ship!.Solid)
            for (int k = 0; k < t.Length; k++)
            {
                var a = t[k]; var b = t[(k + 1) % t.Length];
                int n = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 16));
                for (int i = 0; i < n; i++) shipPoints.Add(Vector3.Lerp(a, b, i / (float)n));
            }
        return shipPoints;
    }

    /// <summary>The lowest height (model space) from the rail's top up with <paramref name="clear"/> free above it at a place
    /// along the side (its collision's points within <paramref name="halfLength"/> along, between the rail's outside and 64 in
    /// from it): the rail's top when nothing stands within that over it, else the top of the stack in the way.</summary>
    float ClearOver(float along, int sideSign, float railAcross, float railTop, float halfLength, float clear)
    {
        var zs = ShipPoints().Where(v => MathF.Abs(v.Y - along) < halfLength && v.X * sideSign > railAcross - 64 && v.X * sideSign <= railAcross + 8 && v.Z > railTop + 4)
            .Select(v => v.Z).OrderBy(z => z).ToList();
        float level = railTop;
        foreach (float z in zs)
        {
            if (z > level + clear) break;
            level = MathF.Max(level, z);
        }
        return level;
    }

    /// <summary>The top (model space) of what stands over the ship's rail at a place along it: its collision's points within
    /// <paramref name="halfLength"/> along, between the rail's outside and 64 in from it, over the rail's top by up to
    /// <paramref name="headroom"/>; null when nothing does.</summary>
    float? OverRail(float along, int sideSign, float railAcross, float railTop, float halfLength, float headroom)
    {
        var pts = ShipPoints().Where(v => MathF.Abs(v.Y - along) < halfLength && v.X * sideSign > railAcross - 64
            && v.X * sideSign <= railAcross + 8 && v.Z > railTop + 4 && v.Z <= railTop + headroom).ToList();
        return pts.Count == 0 ? null : pts.Max(v => v.Z);
    }

    /// <summary>How far out the ship's rail is (model space, across) at a place along it: its collision's outermost point
    /// within 24 under the rail's top (<paramref name="railTop"/>, over the keel); null without one.</summary>
    float? RailOuter(float along, int sideSign, float railTop)
    {
        var pts = ShipPoints().Where(v => MathF.Abs(v.Y - along) < 64 && v.X * sideSign > 0 && v.Z >= railTop - 24 && v.Z <= railTop + 4).ToList();
        return pts.Count == 0 ? null : pts.Max(v => v.X * sideSign);
    }

    float? RailTop(float along, int sideSign, float half)
    {
        var pts = ShipPoints()
            .Where(v => MathF.Abs(v.Y - along) < 64 && v.X * sideSign > half - 48 && v.Z >= ship!.Deck - 4 && v.Z <= ship.Deck + 120).ToList();
        return pts.Count == 0 ? null : pts.Max(v => v.Z);
    }

    /// <summary>A convex outline's half width at a place along its Y (its X on one side: +1 starboard, -1 port).</summary>
    static float HalfWidth(List<Vector2> hull, float y, int sideSign)
    {
        float best = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Count];
            if ((a.Y - y) * (b.Y - y) > 0 || a.Y == b.Y) continue;
            float x = a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y);
            if (x * sideSign > best) best = x * sideSign;
        }
        return best;
    }

    /// <summary>Whether a point lies inside a polygon seen from above (crossings).</summary>
    static bool InPolygon(Vector2 q, Vector3[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    static List<Vector2> ConvexHull(List<Vector2> pts)
    {
        var p = pts.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<Vector2>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            int start = h.Count;
            foreach (var v in pass) { while (h.Count >= start + 2 && Cross(h[^2], h[^1], v) <= 0) h.RemoveAt(h.Count - 1); h.Add(v); }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }
}
