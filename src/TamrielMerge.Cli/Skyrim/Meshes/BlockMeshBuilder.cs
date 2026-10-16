using System.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using nifly;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Builds one Skyrim SE NIF per Daggerfall dungeon block: the block's static models merged into block space,
/// re-textured with Skyrim's Imperial dungeon textures, with convex-slab collision per polygon.
/// Settings mirror vanilla meshes\dungeons\imperial\largehall\implhall1way256short01.nif.
/// </summary>
public static class BlockMeshBuilder
{
    /// <summary>
    /// Skyrim units per Daggerfall unit. True scale is 1.75 (2.5 cm vs ~1.43 cm), which felt too small in game, and
    /// 2.5 too big; at 2.0 a Daggerfall corridor is 256 x 256, like Skyrim's small halls.
    /// </summary>
    public const float Scale = 2.0f;
    const float HavokScale = 69.99125f;
    internal const uint StoneMaterial = 3741512247; // SKY_HAV_MAT_STONE
    internal const uint WoodMaterial = 500811281;   // SKY_HAV_MAT_WOOD
    /// <summary>SKY_HAV_MAT_STAIRS_STONE: what vanilla's stairs give their steps (treads and risers), which Skyrim's
    /// character controller climbs (the Ratway's 16-22 unit steps have no ramp); plain stone steps stop it.</summary>
    internal const uint StairsStoneMaterial = 899511101;
    internal const uint StairsWoodMaterial = 1461712277; // SKY_HAV_MAT_STAIRS_WOOD

    static uint MaterialOf(Tex t) => t.Stairs ? (t.Wood ? StairsWoodMaterial : StairsStoneMaterial) : t.Wood ? WoodMaterial : StoneMaterial;
    const int MaxShapesPerList = 200;
    const float SlabThickness = 12f;
    const float UvTile = 512f;

    /// <summary>
    /// Hinged doors, by model reference tag (Daggerfall Unity RDBLayout.IsActionDoor): door, double door, the
    /// NEW-tagged doors and cave-wall doors. The red brick door (72100) is scenery despite its tag.
    /// </summary>
    public static bool IsDoor(DfRdbObject o) => o.Tag is "DOR" or "DDR" or "NEW" or "CAV" && o.ModelId != 72100;
    /// <summary>The dungeon exit, replaced by a Skyrim load door (Daggerfall Unity RDBLayout.exitDoorModelID).</summary>
    public const uint ExitDoorModel = 70300;
    /// <summary>Tapestries and banners stand off the wall; Daggerfall Unity gives them no collider so players can't snag.</summary>
    static bool IsTapestry(uint id) => id is >= 42500 and <= 42571;

    /// <summary>A texture; ImageW/H (Daggerfall texels) mark a Daggerfall picture laid across its panel rather than tiled by world position.</summary>
    /// <param name="Stairs">Collision only: a step (tread or riser), given Skyrim's stair material.</param>
    internal sealed record Tex(string Diffuse, string Normal, bool Wood, int ImageW = 0, int ImageH = 0, bool Stairs = false);

    /// <summary>Exports Daggerfall's own windows, tapestries and banners; set by the dungeon builder. Without it they get Skyrim walls.</summary>
    public static DfTextureExport? Export { get; set; }
    static Tex T(string name, bool wood = false) => new($"textures\\dungeons\\{name}.dds", $"textures\\dungeons\\{name}_n.dds", wood);
    static readonly Tex Wall = T("imperial\\ImpWall01"), Arch = T("imperial\\ImpWall02"), Ceiling = T("imperial\\ImpWall03"),
        Dark = T("imperial\\ImpWall06"), Floor = T("imperial\\ImpFloorStone01"), Wood = T("imperial\\ImpWood01", true),
        Metal = T("imperial\\ImpMetal01"), SewerFloor = T("imperial\\ImpFloorMuddy01"), SewerWall = T("imperial\\ImpWall05"),
        CaveWall = T("caves\\CaveBaseWall01"), CaveFloor = T("caves\\CaveBaseGround01");

    /// <summary>
    /// Daggerfall texture archives are climate variants (+0/100/300/400) of one family; the family plus the
    /// polygon's facing picks the Skyrim texture.
    /// </summary>
    static Tex PickTexture(int archive, int record, Vector3 normalUp)
    {
        // Daggerfall's own textures on every surface (user, 2026-10-12: "now for the daggerfall textures for the game
        // itself"); its wooden families keep Skyrim's wood stair material. The Skyrim stand-ins below only without the
        // exporter.
        if (Export is not null)
        {
            if (archive is 0 or 1) return new Tex(Export.EnsureSolid(archive, record), DfTextureExport.FlatNormal, false);
            var (path, w, h) = Export.Ensure(archive, record);
            return new Tex(path, DfTextureExport.FlatNormal, (archive % 100) is 67 or 90 or 92 or 49 or 50 or 74 or 95, w, h);
        }
        bool floor = normalUp.Y > 0.7f, ceiling = normalUp.Y < -0.7f;
        if (archive is 0 or 1) return Dark; // solid colours
        return (archive % 100) switch
        {
            67 or 90 or 92 or 49 or 50 or 74 or 95 => Wood,
            93 => Metal,
            47 => floor ? CaveFloor : CaveWall,
            68 => floor ? SewerFloor : SewerWall,
            21 or 46 => floor ? Floor : Arch,
            _ => floor ? Floor : ceiling ? Ceiling : Wall,
        };
    }

    /// <summary>Daggerfall (x, y up, z north) to Skyrim (x east, y north, z up), scaled.</summary>
    static Vector3 ToSkyrim(Vector3 df) => new(df.X * Scale, df.Z * Scale, df.Y * Scale);
    static Vector3 NormalToSkyrim(Vector3 n) => Vector3.Normalize(new Vector3(n.X, n.Z, n.Y));

    public sealed record Result(string Path, int Polygons, int Shapes, int CollisionShapes, Vector3 Min, Vector3 Max);

    /// <summary>
    /// The block's collision polygons in block space (Skyrim units, z up), wound counter-clockwise around their
    /// normals: the solid polygons with stair risers replaced by ramps. Also the navmesh input.
    /// </summary>
    public static List<(Vector3[] Points, Vector3 Normal)> SolidPolygons(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models) =>
        SolidPolygons(block, models, null);

    internal static List<(Vector3[] Points, Vector3 Normal)> SolidPolygons(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models, Picker? picker) =>
        CollisionPolygons(Polygons(block, models, picker)).Select(p => (Wind(p.Pts, p.N), p.N)).ToList();

    /// <summary>
    /// Stairs as Skyrim's own (user, 2026-10-09: "find skyrims stairs and see how they work"; measured in game the same
    /// day): vanilla gives its steps' treads and risers the stair material, which the player climbs up to about 24
    /// units a step (plain stone stops at the first), and lays a plain ramp over steeper flights, never over 47 degrees
    /// (the player walks 45, not 50). Daggerfall's steps rise 40 units, its dungeon flights about 68 degrees: so every
    /// step keeps its own collision in the stair material, and each flight (steps facing the same way, each starting
    /// where the one above ends) gets one smooth ramp from its top nosing down to the floor below, along its nosings or
    /// at MaxRampDegrees if that is gentler, running out past the foot of the stairs where it must (user: "use the one
    /// that was smooth it will just make stairs longer"). The visible steps are unchanged; the navmesh follows the ramps.
    /// </summary>
    const float MaxRampDegrees = 44f;
    /// <summary>How far below a flight's foot its ramp runs on, under the floor (lowest treads fall up to 24).</summary>
    const float RampSink = 24f;
    static readonly float RampRunPerRise = 1 / MathF.Tan(MaxRampDegrees * MathF.PI / 180);

    /// <summary>A step down: its top edge (A, B), the way down (Out), its top and foot heights, and what it is made of.</summary>
    sealed record Step(Vector3 A, Vector3 B, Vector2 Out, float Top, float Foot, Tex Tex)
    {
        public Vector2 Mid => new((A.X + B.X) / 2, (A.Y + B.Y) / 2);
        public Vector2 Side => new(-Out.Y, Out.X);
        public float Rise => Top - Foot;
    }

    /// <summary>
    /// What counts as stairs. Daggerfall's dungeon blocks: any riser 8-20 Daggerfall units tall (floor edges dropping up
    /// to 22), even one step alone. Other games' models (user, 2026-10-09: "do other games need the stair fix" - yes:
    /// Morrowind's steps rise 16, Oblivion's ~18, Redguard's 16-32, and plain-stone steps stop the player): risers
    /// 8-40 units with a tread on top, in flights of two or more, so a table's or crate's side isn't taken for a step.
    /// </summary>
    internal sealed record StairRules(float MinRise, float MaxRise, float MaxEdgeDrop, int MinFlight, bool TreadOnTop);
    internal static readonly StairRules DaggerfallStairs = new(8 * Scale, 20 * Scale, 22 * Scale, 1, false);
    internal static readonly StairRules ModelStairs = new(8, 40, 40, 2, true);

    /// <summary>Stairs as Skyrim's for a model's solid polygons (Morrowind, Oblivion, Redguard): the collision to give
    /// AddCollision instead of the polygons themselves.</summary>
    internal static List<(Vector3[] Pts, Vector3 N, Tex Tex)> StairCollision(List<(Vector3[] Pts, Vector3 N, Tex Tex)> solid) =>
        CollisionPolygons(solid.Select(q => (q.Pts, q.N, q.Tex, true)).ToList(), ModelStairs).Select(q => (q.Pts, q.N, q.Tex)).ToList();

    static List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> CollisionPolygons(List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> polys, StairRules? rules = null)
    {
        rules ??= DaggerfallStairs;
        var result = new List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)>();
        var solid = polys.Where(p => p.Solid).ToList();
        var treads = solid.Where(p => p.N.Z > 0.95f).ToList();
        var steps = new List<Step>();
        var riserOf = new Dictionary<Step, int>();
        for (int i = 0; i < solid.Count; i++)
        {
            var p = solid[i];
            float rise = p.Pts.Max(v => v.Z) - p.Pts.Min(v => v.Z);
            var flat = new Vector3(p.N.X, p.N.Y, 0);
            if (!(MathF.Abs(p.N.Z) < 0.3f && rise >= rules.MinRise && rise <= rules.MaxRise && flat.LengthSquared() > 0.5f)) continue;
            var outward = Vector3.Normalize(flat);
            var side = Vector3.Cross(outward, Vector3.UnitZ);
            var top = p.Pts.OrderByDescending(v => v.Z).Take(2).OrderBy(v => Vector3.Dot(v, side)).ToArray();
            var st = new Step(top[0], top[1], new Vector2(outward.X, outward.Y), p.Pts.Max(v => v.Z), p.Pts.Min(v => v.Z), p.Tex);
            if (rules.TreadOnTop && !treads.Any(t => FloorHeight(t.Pts, st.Mid - st.Out * 6) is { } h && MathF.Abs(h - st.Top) < 3)) continue;
            steps.Add(st);
            riserOf[st] = i;
        }
        steps.AddRange(EdgeSteps(treads, rules));
        var flights = Flights(steps).Where(f => f.Count >= rules.MinFlight).ToList();
        var kept = flights.SelectMany(f => f).ToList();
        var keptRisers = kept.Where(riserOf.ContainsKey).Select(st => riserOf[st]).ToHashSet();
        // Steps' risers, and the treads at a kept step's top or foot, are stairs (the stair material); the rest stays.
        for (int i = 0; i < solid.Count; i++)
        {
            var p = solid[i];
            bool stair = keptRisers.Contains(i) || p.N.Z > 0.95f && kept.Any(st =>
                FloorHeight(p.Pts, st.Mid - st.Out * 6) is { } up && MathF.Abs(up - st.Top) < 3 ||
                FloorHeight(p.Pts, st.Mid + st.Out * 6) is { } down && MathF.Abs(down - st.Foot) < 3);
            result.Add(stair ? (p.Pts, p.N, p.Tex with { Stairs = true }, true) : p);
        }
        foreach (var flight in flights) result.Add(FlightRamp(flight, treads));
        return result;
    }

    /// <summary>Dev aid: each stair riser's rise and the tread depth in front of it (Skyrim units; 0 when no tread is found).</summary>
    internal static IEnumerable<(float Rise, float Tread)> StairStats(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models)
    {
        var polys = Polygons(block, models, null);
        var treads = polys.Where(p => p.Solid && p.N.Z > 0.95f).ToList();
        foreach (var p in polys.Where(p => p.Solid))
        {
            float rise = p.Pts.Max(v => v.Z) - p.Pts.Min(v => v.Z);
            var flat = new Vector3(p.N.X, p.N.Y, 0);
            if (!(MathF.Abs(p.N.Z) < 0.3f && rise >= 8 * Scale && rise <= 20 * Scale && flat.LengthSquared() > 0.5f)) continue;
            var outward = Vector3.Normalize(flat);
            var bottom = p.Pts.OrderBy(v => v.Z).Take(2).ToArray();
            var foot = (bottom[0] + bottom[1]) / 2;
            float t = 2;
            for (; t <= 200; t += 2)
                if (!treads.Any(f => FloorHeight(f.Pts, new Vector2(foot.X, foot.Y) + new Vector2(outward.X, outward.Y) * t) is { } h && MathF.Abs(h - foot.Z) < 3)) break;
            yield return (rise, t - 2);
        }
    }

    /// <summary>
    /// Many Daggerfall staircases have no riser polygons, only treads at different heights: wherever a floor's edge
    /// drops 8-22 Daggerfall units onto another floor just beyond it, that edge is a step.
    /// </summary>
    static IEnumerable<Step> EdgeSteps(List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> floors, StairRules rules)
    {
        foreach (var upper in floors)
        {
            var centre = upper.Pts.Aggregate(Vector3.Zero, (a, b) => a + b) / upper.Pts.Length;
            for (int i = 0; i < upper.Pts.Length; i++)
            {
                var (a, b) = (upper.Pts[i], upper.Pts[(i + 1) % upper.Pts.Length]);
                var along = new Vector2(b.X - a.X, b.Y - a.Y);
                if (along.Length() < 16) continue;
                var outward = Vector2.Normalize(new Vector2(along.Y, -along.X));
                var mid = new Vector2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (Vector2.Dot(outward, mid - new Vector2(centre.X, centre.Y)) < 0) outward = -outward;
                // What is just beyond the edge: another floor at the same height means it isn't an edge at all.
                var probe = mid + outward * 4;
                float top = (a.Z + b.Z) / 2;
                float? below = null; bool level = false;
                foreach (var f in floors)
                {
                    if (FloorHeight(f.Pts, probe) is not { } z) continue;
                    if (MathF.Abs(z - top) < 4) { level = true; break; }
                    float drop = top - z;
                    if (drop >= rules.MinRise && drop <= rules.MaxEdgeDrop && (below is null || z > below)) below = z;
                }
                if (level || below is not { } low) continue;
                var side = new Vector2(-outward.Y, outward.X);
                var (l, r) = Vector2.Dot(new Vector2(a.X, a.Y), side) <= Vector2.Dot(new Vector2(b.X, b.Y), side) ? (a, b) : (b, a);
                yield return new Step(l, r, outward, top, low, upper.Tex);
            }
        }
    }

    /// <summary>
    /// The flights: steps chained top to bottom, each next one facing the same way, starting at the height the one above
    /// ends, out in front of it (within three rises) and alongside it. Duplicates (a riser and its tread's edge) merge.
    /// </summary>
    /// <summary>
    /// Dev aid (user, 2026-10-10: "the first flight of stairs in the dungeon need the stair fix"): each flight of a
    /// Daggerfall dungeon block as the converter sees it - its top step's middle (block mesh units), the way down,
    /// steps, height, the ramp's run, and the free floor beyond its last step before a wall at knee height.
    /// </summary>
    public static List<(Vector3 Top, Vector2 Down, int Steps, float Height, float RampRun, float Free)> FlightReport(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models)
    {
        var polys = Polygons(block, models);
        var solid = polys.Where(p => p.Solid).ToList();
        var treads = solid.Where(p => p.N.Z > 0.95f).ToList();
        var rules = DaggerfallStairs;
        var steps = new List<Step>();
        foreach (var p in solid)
        {
            float rise = p.Pts.Max(v => v.Z) - p.Pts.Min(v => v.Z);
            var flat = new Vector3(p.N.X, p.N.Y, 0);
            if (!(MathF.Abs(p.N.Z) < 0.3f && rise >= rules.MinRise && rise <= rules.MaxRise && flat.LengthSquared() > 0.5f)) continue;
            var outward = Vector3.Normalize(flat);
            var side = Vector3.Cross(outward, Vector3.UnitZ);
            var top = p.Pts.OrderByDescending(v => v.Z).Take(2).OrderBy(v => Vector3.Dot(v, side)).ToArray();
            steps.Add(new Step(top[0], top[1], new Vector2(outward.X, outward.Y), p.Pts.Max(v => v.Z), p.Pts.Min(v => v.Z), p.Tex));
        }
        steps.AddRange(EdgeSteps(treads, rules));
        var walls = solid.Where(p => MathF.Abs(p.N.Z) < 0.5f).ToList();
        var report = new List<(Vector3, Vector2, int, float, float, float)>();
        foreach (var flight in Flights(steps).Where(f => f.Count >= rules.MinFlight))
        {
            var ramp = FlightRamp(flight, treads);
            float run = Vector2.Dot(new Vector2(ramp.Pts[0].X, ramp.Pts[0].Y) - flight[0].Mid, flight[0].Out);
            var last = flight[^1];
            var foot = last.Mid + last.Out * 2;
            float free = WallDistance(walls, foot, last.Out, last.Foot + 40, 4000) + Vector2.Dot(foot - flight[0].Mid, flight[0].Out);
            report.Add((new Vector3(flight[0].Mid, flight[0].Top), flight[0].Out, flight.Count, flight[0].Top - last.Foot, run, free));
        }
        return report;
    }

    /// <summary>How far along a level ray (from, dir, at height z) the first wall polygon is, up to max.</summary>
    static float WallDistance(List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> walls, Vector2 from, Vector2 dir, float z, float max)
    {
        var o = new Vector3(from, z);
        var d = new Vector3(dir, 0);
        float best = max;
        foreach (var w in walls)
        {
            float denom = Vector3.Dot(w.N, d);
            if (MathF.Abs(denom) < 1e-4f) continue;
            float t = Vector3.Dot(w.N, w.Pts[0] - o) / denom;
            if (t <= 0.5f || t >= best) continue;
            var h = o + d * t;
            // Inside the polygon: the same side of every edge, seen along the normal.
            bool inside = true; float sign = 0;
            for (int i = 0; i < w.Pts.Length && inside; i++)
            {
                var e = w.Pts[(i + 1) % w.Pts.Length] - w.Pts[i];
                float c = Vector3.Dot(Vector3.Cross(e, h - w.Pts[i]), w.N);
                if (MathF.Abs(c) < 1e-3f) continue;
                if (sign == 0) sign = MathF.Sign(c); else if (MathF.Sign(c) != sign) inside = false;
            }
            if (inside) best = t;
        }
        return best;
    }

    static List<List<Step>> Flights(List<Step> all)
    {
        var steps = new List<Step>();
        foreach (var st in all.OrderByDescending(x => x.Top))
            if (!steps.Any(o => Vector2.Dot(o.Out, st.Out) > 0.95f && MathF.Abs(o.Top - st.Top) < 3 && MathF.Abs(o.Foot - st.Foot) < 3 && Vector2.Distance(o.Mid, st.Mid) < 12))
                steps.Add(st);
        Step? Next(Step s0)
        {
            Step? best = null; float bestD = float.MaxValue;
            float half = Vector2.Distance(new Vector2(s0.A.X, s0.A.Y), new Vector2(s0.B.X, s0.B.Y)) / 2;
            foreach (var n in steps)
            {
                // The next riser starts where this one ends, or lower by a sloping tread between them (Daggerfall's own
                // stair models: treads falling 8 to a 24 riser; user, 2026-10-10: "the first flight of stairs in the
                // dungeon need the stair fix" - those steps were each taken as a flight of one).
                float drop = s0.Foot - n.Top;
                if (n == s0 || Vector2.Dot(n.Out, s0.Out) < 0.95f || drop < -4 || drop > MathF.Max(4, s0.Rise / 2)) continue;
                var off = n.Mid - s0.Mid;
                float d = Vector2.Dot(off, s0.Out), lateral = MathF.Abs(Vector2.Dot(off, s0.Side));
                if (d < 2 || d > 3 * s0.Rise || lateral > half + 16) continue;
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }
        var next = steps.ToDictionary(x => x, Next);
        var hasAbove = next.Values.Where(v => v is not null).ToHashSet();
        var flights = new List<List<Step>>();
        foreach (var top in steps.Where(x => !hasAbove.Contains(x)))
        {
            var chain = new List<Step> { top };
            for (var n = next[top]; n is not null && !chain.Contains(n) && chain.Count < 64; n = next[n]) chain.Add(n);
            flights.Add(chain);
        }
        return flights;
    }

    /// <summary>
    /// One ramp for a flight: from the top step's edge (widened to every step of the flight) down to the floor below its
    /// last step, along the flight's nosings (or, for one step, to the end of the tread below it, one to two rises out),
    /// or at MaxRampDegrees if that is gentler. Plain material, as vanilla's ramps.
    /// </summary>
    static (Vector3[] Pts, Vector3 N, Tex Tex, bool Solid) FlightRamp(List<Step> flight, List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> treads)
    {
        var top = flight[0]; var last = flight[^1];
        float height = top.Top - last.Foot;
        float natural;
        if (flight.Count == 1)
            natural = RampRun(treads, last.Mid, last.Out, last.Foot, last.Rise);
        else
        {
            float along = Vector2.Dot(last.Mid - top.Mid, top.Out);
            float slope = (top.Top - last.Top) / MathF.Max(along, 1);   // rise per unit along the nosings
            natural = height / MathF.Max(slope, 1e-3f);
        }
        float run = MathF.Max(natural, height * RampRunPerRise);
        // Across: as wide as the widest step of the flight.
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var st in flight)
            foreach (var v in new[] { st.A, st.B })
            {
                float t = Vector2.Dot(new Vector2(v.X, v.Y) - top.Mid, top.Side);
                lo = MathF.Min(lo, t); hi = MathF.Max(hi, t);
            }
        Vector3 At(float lateral, float forward, float z)
        {
            var q = top.Mid + top.Side * lateral + top.Out * forward;
            return new Vector3(q.X, q.Y, z);
        }
        // The foot carried on under the floor (user, 2026-10-11: "can't walk up"): a Daggerfall flight's lowest tread falls
        // 8 to the floor, so a ramp ending at its last step's foot left a plain lip 8 high where the floor meets it, and the
        // player doesn't step up plain edges. Continued at its own slope until RampSink below the foot, the part under the
        // floor hidden in it, the floor runs into the ramp.
        float sinkRun = RampSink * run / MathF.Max(height, 1);
        Vector3[] ramp = [At(lo, run + sinkRun, last.Foot - RampSink), At(hi, run + sinkRun, last.Foot - RampSink), At(hi, 0, top.Top), At(lo, 0, top.Top)];
        var n = Vector3.Normalize(Vector3.Cross(ramp[1] - ramp[0], ramp[2] - ramp[0]));
        if (n.Z < 0) n = -n;
        return (ramp, n, top.Tex with { Stairs = false }, true);
    }

    /// <summary>
    /// How far a one-step ramp runs out from its foot: to the end of the lower tread (where the next step down begins),
    /// between one and two rises.
    /// </summary>
    static float RampRun(List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> treads, Vector2 foot, Vector2 outward, float z, float rise)
    {
        float t = 2;
        for (; t <= rise * 2; t += 4)
            if (!treads.Any(f => FloorHeight(f.Pts, foot + outward * t) is { } h && MathF.Abs(h - z) < 3)) break;
        return Math.Clamp(t, rise, rise * 2);
    }

    /// <summary>Height of a (convex, near-horizontal) polygon at a point in plan view, or null outside it.</summary>
    static float? FloorHeight(Vector3[] pts, Vector2 p)
    {
        for (int i = 1; i + 1 < pts.Length; i++)
        {
            var (a, b, c) = (pts[0], pts[i], pts[i + 1]);
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-6f) continue;
            float w0 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / d, w1 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / d, w2 = 1 - w0 - w1;
            if (w0 >= 0 && w1 >= 0 && w2 >= 0) return w0 * a.Z + w1 * b.Z + w2 * c.Z;
        }
        return null;
    }

    static Vector3[] Wind(Vector3[] pts, Vector3 n) =>
        Vector3.Dot(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]), n) >= 0 ? pts : pts.Reverse().ToArray();


    /// <summary>Picks the Skyrim texture for a Daggerfall surface (archive, record, normal in Daggerfall's frame, Y up).</summary>
    internal delegate Tex Picker(int archive, int record, Vector3 normalUp);

    /// <summary>
    /// Each placed polygon's texture coordinates in Daggerfall texels (DfPolygon.Uvs), by its points array, from when
    /// its polygons are made until <see cref="Write"/> writes them (on the same thread), which takes them out. It was a
    /// ConditionalWeakTable holding every polygon made, the collision and navmesh ones too: millions of dependent handles
    /// that every garbage collection walked, and the town parts spent 73% of their time paused (2026-10-15).
    /// </summary>
    [ThreadStatic] static Dictionary<Vector3[], Vector2[]>? texelUvs;
    static Dictionary<Vector3[], Vector2[]> TexelUvs => texelUvs ??= new(ReferenceEqualityComparer.Instance);

    /// <summary>Texture coordinates (Daggerfall texels) for a polygon built elsewhere (a town's ground tiles), to be
    /// written on this thread.</summary>
    internal static void SetTexelUvs(Vector3[] pts, Vector2[] uvs) => TexelUvs[pts] = uvs;

    public static Result Build(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models, string outPath) => Build(block, models, outPath, null);

    /// <summary>A static NIF straight from polygons (Skyrim units, z up), each with its texture; all solid.</summary>
    internal static Result BuildPolygons(List<(Vector3[] Pts, Vector3 N, Tex Tex)> polys, string outPath) =>
        Write(polys.Select(p => (p.Pts, p.N, p.Tex, true)).ToList(), outPath, moving: false);

    /// <summary>Dev aid (stair test): a static NIF from polygons with collision exactly as given (no stair ramps).</summary>
    internal static void BuildRaw(List<(Vector3[] Pts, Vector3 N, Tex Tex)> polys, string outPath, uint? material = null)
    {
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var hdr = nif.GetHeader();
        var fade = Owned(new BSFadeNode());
        fade.name = new NiStringRef("StairTest");
        hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        using var verts = new vectorVector3(); using var uvs = new vectorVector2(); using var norms = new vectorVector3(); using var tris = new vectorTriangle();
        foreach (var (pts, n, _) in polys)
        {
            ushort start = (ushort)verts.Count;
            foreach (var v in pts)
            {
                Add(verts, v.X, v.Y, v.Z);
                Add(norms, n.X, n.Y, n.Z);
                var (u, w) = PlanarUv(v, n);
                Add(uvs, u, w);
            }
            for (int i = 1; i + 1 < pts.Length; i++)
            {
                bool flip = Vector3.Dot(Vector3.Cross(pts[i] - pts[0], pts[i + 1] - pts[0]), n) < 0;
                Add(tris, start, (ushort)(start + (flip ? i + 1 : i)), (ushort)(start + (flip ? i : i + 1)));
            }
        }
        var shape = nif.CreateShapeFromData("StairTest", verts, tris, uvs, norms);
        nif.SetTextureSlot(shape, polys[0].Tex.Diffuse, 0);
        nif.SetTextureSlot(shape, polys[0].Tex.Normal, 1);
        AddCollision(nif, root, polys, moving: false, material);
        AddBsxFlags(nif, root);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
    }

    internal static Result Build(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models, string outPath, Picker? picker)
    {
        var polys = Polygons(block, models, picker, texels: true);
        return Write(polys, outPath, moving: false);
    }

    /// <summary>
    /// One Daggerfall model on its own, in its own space (pivot at the model origin, as Daggerfall rotates it), for
    /// action objects the converter moves with scripts. Collision is keyframed so it follows the object.
    /// </summary>
    /// <summary>One Daggerfall model as a still object of its own (a chair, chest or bookcase): its polygons in its own
    /// space, Daggerfall's textures, static collision.</summary>
    /// <param name="place">Where each point (Skyrim units, the model's own space) goes, and how each normal turns, when
    /// the mesh is built in another object's space (furniture built where its Skyrim stand-in's markers fit).</param>
    public static Result BuildPropModel(DfModel model, string outPath, Func<Vector3, Vector3>? place = null, Func<Vector3, Vector3>? turn = null)
    {
        var polys = new List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)>();
        foreach (var p in model.Polygons.Where(p => p.Points.Length >= 3))
        {
            var pts = p.Points.Select(v => place is null ? ToSkyrim(v) : place(ToSkyrim(v))).ToArray();
            if (p.Uvs is not null) TexelUvs[pts] = p.Uvs;
            var n = NormalToSkyrim(p.Normal);
            polys.Add((pts, turn is null ? n : Vector3.Normalize(turn(n)), PickTexture(p.TextureArchive, p.TextureRecord, p.Normal), true));
        }
        return Write(polys, outPath, moving: false);
    }

    public static Result BuildMovingModel(DfModel model, string outPath)
    {
        var polys = new List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)>();
        foreach (var p in model.Polygons.Where(p => p.Points.Length >= 3))
        {
            var pts = p.Points.Select(ToSkyrim).ToArray();
            if (p.Uvs is not null) TexelUvs[pts] = p.Uvs;
            polys.Add((pts, NormalToSkyrim(p.Normal), PickTexture(p.TextureArchive, p.TextureRecord, p.Normal), true));
        }
        return Write(polys, outPath, moving: true);
    }

    /// <summary>
    /// The block's action models (platforms, lifts, drawbridges, raised floors, moving walls) as they rest, as solid
    /// polygons in block space for the navmesh and for what props rest on (user, 2026-10-13: "i need the platforms,
    /// lifts, drawbridges, and raised floors"): they are separate moving objects (ActionObjectBuilder), so the block's
    /// own polygons leave them out, and people standing on them had no navmesh under them (37 in Castle Daggerfall).
    /// </summary>
    public static List<(Vector3[] Points, Vector3 Normal)> ActionSolids(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models)
    {
        var polys = new List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)>();
        var none = new Tex("", "", false);
        foreach (var o in block.Objects.Where(ActionObjectBuilder.IsActionObject))
        {
            if (!models.TryGetValue(o.ModelId, out var model)) continue;
            var m = DfDungeon.ObjectTransform(o, Vector3.Zero);
            foreach (var p in model.Polygons)
            {
                if (p.Points.Length < 3) continue;
                var n = Vector3.Normalize(Vector3.TransformNormal(p.Normal, m));
                polys.Add((p.Points.Select(v => ToSkyrim(Vector3.Transform(v, m))).ToArray(), NormalToSkyrim(n), none, true));
            }
        }
        return CollisionPolygons(polys).Select(p => (Wind(p.Pts, p.N), p.N)).ToList();
    }

    /// <summary>The block's static polygons in block space (Skyrim units).</summary>
    /// <param name="texels">Keep each polygon's Daggerfall texture coordinates for <see cref="Write"/> (only for polygons
    /// that are written: collision and navmeshes don't need them).</param>
    static List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> Polygons(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models, Picker? picker = null, bool texels = false)
    {
        picker ??= PickTexture;
        var polys = new List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)>();
        foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model && !IsDoor(o) && o.ModelId != ExitDoorModel && !o.HasAction && !PropReplacer.IsReplaced(o.ModelId)))
        {
            if (!models.TryGetValue(o.ModelId, out var model)) continue;
            var m = DfDungeon.ObjectTransform(o, Vector3.Zero);
            foreach (var p in model.Polygons)
            {
                if (p.Points.Length < 3) continue;
                var n = Vector3.Normalize(Vector3.TransformNormal(p.Normal, m));
                var pts = p.Points.Select(v => ToSkyrim(Vector3.Transform(v, m))).ToArray();
                if (texels && p.Uvs is not null) TexelUvs[pts] = p.Uvs;
                polys.Add((pts, NormalToSkyrim(n), picker(p.TextureArchive, p.TextureRecord, n), !IsTapestry(o.ModelId)));
            }
        }

        return polys;
    }

    static Result Write(List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> polys, string outPath, bool moving)
    {
        try { return WriteMesh(polys, outPath, moving); }
        finally { if (texelUvs is { Count: > 0 } t) foreach (var p in polys) t.Remove(p.Pts); }
    }

    static Result WriteMesh(List<(Vector3[] Pts, Vector3 N, Tex Tex, bool Solid)> polys, string outPath, bool moving)
    {
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        // Vanilla static architecture uses a BSFadeNode root.
        var hdr0 = nif.GetHeader();
        var fade = Owned(new BSFadeNode());
        // Names must be set as text: nifly rebuilds the string table from names' text on save, so an index alone is
        // lost (the BSX block then has no name and Skyrim ignores all of the mesh's collision).
        fade.name = new NiStringRef(Path.GetFileNameWithoutExtension(outPath));
        hdr0.ReplaceBlock(hdr0.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        int shapes = 0;
        foreach (var group in polys.GroupBy(p => p.Tex))
        {
            using var verts = new vectorVector3();
            using var uvs = new vectorVector2();
            using var norms = new vectorVector3();
            using var tris = new vectorTriangle();
            foreach (var (pts, n, _, _) in group)
            {
                ushort start = (ushort)verts.Count;
                // A Daggerfall texture laid as Daggerfall lays it (the model's own texel coordinates), else across its panel.
                var texel = group.Key.ImageW > 0 && TexelUvs.TryGetValue(pts, out var t) && t.Length == pts.Length ? t : null;
                var panelUv = texel is null && group.Key.ImageW > 0 ? PanelUv(pts, n, group.Key) : null;
                for (int k = 0; k < pts.Length; k++)
                {
                    var v = pts[k];
                    Add(verts, v.X, v.Y, v.Z);
                    Add(norms, n.X, n.Y, n.Z);
                    var (u, w) = texel is not null ? (texel[k].X / group.Key.ImageW, texel[k].Y / group.Key.ImageH) : panelUv?[k] ?? PlanarUv(v, n);
                    Add(uvs, u, w);
                }
                // Fan-triangulate, wound counter-clockwise around the polygon normal.
                for (int i = 1; i + 1 < pts.Length; i++)
                {
                    var c = Vector3.Cross(pts[i] - pts[0], pts[i + 1] - pts[0]);
                    bool flip = Vector3.Dot(c, n) < 0;
                    Add(tris, start, (ushort)(start + (flip ? i + 1 : i)), (ushort)(start + (flip ? i : i + 1)));
                }
                if (verts.Count > 60000) break; // BSTriShape uses 16-bit indices
            }
            if (tris.Count == 0) continue;
            var shape = nif.CreateShapeFromData($"DF_{Path.GetFileNameWithoutExtension(group.Key.Diffuse)}_{shapes}", verts, tris, uvs, norms);
            nif.SetTextureSlot(shape, group.Key.Diffuse, 0);
            nif.SetTextureSlot(shape, group.Key.Normal, 1);
            nif.CalcTangentsForShape(shape);
            ConfigureShader(nif, shape);
            shapes++;
        }

        int collisionShapes = AddCollision(nif, root, CollisionPolygons(polys).Select(p => (p.Pts, p.N, p.Tex)).ToList(), moving);
        AddBsxFlags(nif, root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");

        var all = polys.SelectMany(p => p.Pts).ToList();
        return new Result(outPath, polys.Count, shapes, collisionShapes,
            all.Aggregate(new Vector3(float.MaxValue), Vector3.Min), all.Aggregate(new Vector3(float.MinValue), Vector3.Max));
    }

    /// <summary>
    /// A Daggerfall picture laid across its panel: across the panel horizontally and from its top edge down, once per
    /// texture size (Daggerfall texels, scaled), so a window panel shows one window and a long wall repeats it.
    /// </summary>
    static (float u, float v)[] PanelUv(Vector3[] pts, Vector3 n, Tex tex)
    {
        bool flat = MathF.Abs(n.Z) > 0.7f;
        var across = flat ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n));
        var down = flat ? -Vector3.UnitY : -Vector3.UnitZ;
        float u0 = pts.Min(p => Vector3.Dot(p, across)), v0 = pts.Min(p => Vector3.Dot(p, down));
        float uw = pts.Max(p => Vector3.Dot(p, across)) - u0, vh = pts.Max(p => Vector3.Dot(p, down)) - v0;
        // Whole repeats of the picture across the panel (at least one), so it is never cut part-way.
        float ru = MathF.Max(1, MathF.Round(uw / (tex.ImageW * Scale))), rv = MathF.Max(1, MathF.Round(vh / (tex.ImageH * Scale)));
        return pts.Select(p => ((Vector3.Dot(p, across) - u0) / MathF.Max(uw, 1e-3f) * ru, (Vector3.Dot(p, down) - v0) / MathF.Max(vh, 1e-3f) * rv)).ToArray();
    }

    static (float u, float v) PlanarUv(Vector3 p, Vector3 n)
    {
        var a = Vector3.Abs(n);
        if (a.Z >= a.X && a.Z >= a.Y) return (p.X / UvTile, -p.Y / UvTile);
        if (a.X >= a.Y) return (p.Y / UvTile, -p.Z / UvTile);
        return (p.X / UvTile, -p.Z / UvTile);
    }

    static void ConfigureShader(NifFile nif, NiShape shape)
    {
        var hdr = nif.GetHeader();
        // Vanilla Imperial architecture values; flags2 adds Double_Sided (0x10) so Daggerfall's mixed winding
        // never leaves holes.
        EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), shader =>
        {
            shader.shaderFlags1 = 0x82400301;
            shader.shaderFlags2 = 0x00008021 | 0x10;
            shader.glossiness = 300;
            shader.specularStrength = 0.7f;
        });
        EditBlock<BSTriShape>(nif, hdr.GetBlockID(shape), tri => tri.flags = 524302);
    }

    /// <summary>BlockCache hands out a copy of the block; write the edited copy back in place.</summary>
    internal static void EditBlock<T>(NifFile nif, uint id, Action<T> edit) where T : NiObject
    {
        var hdr = nif.GetHeader();
        using var cache = new niflycpp.BlockCache(hdr);
        var block = cache.EditableBlockById<T>(id);
        edit(block);
        hdr.ReplaceBlock(id, Owned(block));
    }

    /// <summary>
    /// Physics for a loose item, as vanilla clutter has it (e.g. GlazedGoblet01): one dynamic rigid body on the root
    /// (layer 4 clutter, box inertia, moving quality) whose shape is a convex box around the model, with its centre
    /// of mass at the box centre and a box inertia tensor for its mass; BSX 194 (havok, dynamic, articulated).
    /// </summary>
    internal static void AddDynamicCollision(NifFile nif, NiNode root, Vector3 min, Vector3 max, float mass, uint material)
    {
        var hdr = nif.GetHeader();
        // A few units of thickness so flat items (plates, coins, papers) still have a solid body.
        var lo = Vector3.Min(min, max - new Vector3(2)); var hi = Vector3.Max(max, min + new Vector3(2));
        var shape = Owned(new bhkConvexVerticesShape { radius = 0.01f });
        shape.vertsProp.capacityAndFlags = 0x80000000;
        shape.normalsProp.capacityAndFlags = 0x80000000;
        shape.SetMaterial(material);
        using (var sv = shape.verts)
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) != 0 ? hi.X : lo.X, (i & 2) != 0 ? hi.Y : lo.Y, (i & 4) != 0 ? hi.Z : lo.Z);
                using var v4 = new nifly.Vector4(c.X / HavokScale, c.Y / HavokScale, c.Z / HavokScale, 0);
                sv.push_back(v4);
            }
        using (var sn = shape.normals)
            foreach (var (n, p) in new[] { (Vector3.UnitX, hi), (-Vector3.UnitX, lo), (Vector3.UnitY, hi), (-Vector3.UnitY, lo), (Vector3.UnitZ, hi), (-Vector3.UnitZ, lo) })
            {
                var pl = Plane(n, p);
                using var v4 = new nifly.Vector4(pl.X, pl.Y, pl.Z, pl.W);
                sn.push_back(v4);
            }
        uint shapeId = hdr.AddBlock(shape);

        var size = (hi - lo) / HavokScale;
        var centre = (hi + lo) / 2 / HavokScale;
        var body = Owned(new bhkRigidBody());
        body.rotation = new QuaternionXYZW { x = 0, y = 0, z = 0, w = 1 };
        body.broadPhaseType = 1;
        body.unkShorts2 = new arrayuint16_6(new ushort[] { 0, 0, 0, 0, 1, 0xFFFF });
        body.shapeRef.index = shapeId;
        body.collisionFilter.layer = 4;
        body.collisionFilterCopy.layer = 4;
        body.collisionResponse = hkResponseType.RESPONSE_SIMPLE_CONTACT;
        body.processContactCallbackDelay = 0xFFFF;
        body.motionSystem = 3;
        body.deactivatorType = 1;
        body.solverDeactivation = 2;
        body.qualityType = 4;
        body.mass = mass;
        body.center = new nifly.Vector4(centre.X, centre.Y, centre.Z, 0);
        // Box inertia: m/12 (b² + c²) about each axis (Havok units).
        var inertia = new float[12];
        inertia[0] = mass / 12 * (size.Y * size.Y + size.Z * size.Z);
        inertia[5] = mass / 12 * (size.X * size.X + size.Z * size.Z);
        inertia[10] = mass / 12 * (size.X * size.X + size.Y * size.Y);
        body.inertiaMatrix = new arrayfloat_12(inertia);
        body.rollingFrictionMult = 0;
        body.friction = 0.5f;
        body.restitution = 0.4f;
        body.linearDamping = 0.099609375f;
        body.angularDamping = 0.049804688f;
        body.timeFactor = 1;
        body.gravityFactor = 1;
        body.maxLinearVelocity = 104.4f;
        body.maxAngularVelocity = 31.57f;
        body.penetrationDepth = 0.05f;
        body.numShapeKeysInContactPointProps = 3;
        uint bodyId = hdr.AddBlock(body);
        var col = Owned(new bhkCollisionObject { flags = 129 });
        col.bodyRef.index = bodyId;
        col.targetRef.index = hdr.GetBlockID(root);
        root.collisionRef.index = hdr.AddBlock(col);
    }

    internal static void AddBsxFlags(NifFile nif, NiNode root, int flags = 10)
    {
        var hdr = nif.GetHeader();
        // 0x2 Havok + 0x8 Complex: collision lives on several child nodes, which vanilla flags exactly this way
        // (BSX 10, e.g. SLowerWall01). Not 0x80 Articulated: that is for single-body meshes like the Imperial halls.
        var bsx = Owned(new BSXFlags { integerData = (uint)flags });
        bsx.name = new NiStringRef("BSX");
        nif.AssignExtraData(root, bsx);
    }

    /// <summary>
    /// Each polygon becomes a thin convex slab extruded behind its face. Groups of up to 200 slabs share a
    /// bhkListShape under a fixed bhkRigidBody on a child NiNode (vanilla statics use layer 1, motion fixed).
    /// </summary>
    /// <param name="onTarget">The first body goes on <paramref name="root"/> itself (a moving part's node, as vanilla
    /// animated doors carry theirs), the rest on child nodes.</param>
    internal static int AddCollision(NifFile nif, NiNode root, List<(Vector3[] Pts, Vector3 N, Tex Tex)> polys, bool moving, uint? materialOverride = null, bool onTarget = false)
    {
        var hdr = nif.GetHeader();
        int total = 0, chunk = 0;
        foreach (var batch in polys.Chunk(MaxShapesPerList))
        {
            var list = Owned(new bhkListShape());
            uint material = materialOverride ?? batch.GroupBy(p => MaterialOf(p.Tex)).OrderByDescending(g => g.Count()).First().Key;
            list.SetMaterial(material);
            // Vanilla list shapes mark their property arrays 0x80000000 and carry one zeroed filter per child.
            list.childShapeProp.capacityAndFlags = 0x80000000;
            list.childFilterProp.capacityAndFlags = 0x80000000;
            foreach (var (pts, n, tex) in batch)
            {
                var slab = ConvexSlab(pts, n, materialOverride ?? MaterialOf(tex));
                if (slab is null) continue;
                list.subShapeRefs.AddBlockRef(hdr.AddBlock(slab));
                using (var filters = list.filters)
                using (var filter = new HavokFilter { layer = 0, flagsAndParts = 0, group = 0 })
                    filters.push_back(filter);
                total++;
            }
            uint listId = hdr.AddBlock(list);

            var body = Owned(new bhkRigidBody());
            body.rotation = new QuaternionXYZW { x = 0, y = 0, z = 0, w = 1 };
            // Broadphase type 1 (entity), as every vanilla body has. nifly's default 0 (invalid) keeps the body out
            // of Havok's world entirely: nothing ever collides with it (the long-standing fall-through-the-floor bug).
            body.broadPhaseType = 1;
            // The 2010 rigid-body info repeats collision response and contact-callback delay; vanilla: 1, 0xFFFF.
            body.unkShorts2 = new arrayuint16_6(new ushort[] { 0, 0, 0, 0, 1, 0xFFFF });
            body.shapeRef.index = listId;
            // Static: layer 1, fixed (vanilla Imperial halls). Moving: layer 2 animated static, keyframed (vanilla
            // animated architecture such as Nocturnal's chamber pieces), so collision follows the object.
            body.collisionFilter.layer = (byte)(moving ? 2 : 1);
            body.collisionFilterCopy.layer = (byte)(moving ? 2 : 1);
            body.collisionResponse = hkResponseType.RESPONSE_SIMPLE_CONTACT;
            body.processContactCallbackDelay = 0xFFFF;
            body.motionSystem = (byte)(moving ? 4 : 5);
            body.deactivatorType = 1;
            body.solverDeactivation = (byte)(moving ? 2 : 1);
            body.qualityType = (byte)(moving ? 1 : 0);
            body.mass = 0;
            body.rollingFrictionMult = 0;
            body.friction = 0.5f;
            body.restitution = 0.4f;
            body.linearDamping = 0.099609375f;
            body.angularDamping = 0.049804688f;
            body.timeFactor = 1;
            body.gravityFactor = 1;
            body.maxLinearVelocity = moving ? 1000002f : 104.4f;
            body.maxAngularVelocity = moving ? 1000002f : 31.57f;
            body.penetrationDepth = moving ? 0.15f : 0.1f;
            body.numShapeKeysInContactPointProps = 3;
            uint bodyId = hdr.AddBlock(body);

            var node = onTarget && chunk++ == 0 && root.collisionRef.index == uint.MaxValue ? root : nif.AddNode($"Collision{chunk++}", new MatTransform(), root);
            var col = Owned(new bhkCollisionObject { flags = (ushort)(moving ? 137 : 129) });
            col.bodyRef.index = bodyId;
            col.targetRef.index = hdr.GetBlockID(node);
            node.collisionRef.index = hdr.AddBlock(col);
        }
        return total;
    }

    /// <summary>
    /// A polygon as a thin convex prism behind its face. Built from the polygon's convex hull in its own plane, so
    /// every face plane is valid even for Daggerfall's slightly concave, collinear or not-quite-flat polygons
    /// (Havok needs every vertex on the inner side of every plane).
    /// </summary>
    static bhkConvexVerticesShape? ConvexSlab(Vector3[] input, Vector3 n, uint material)
    {
        // Hull in the polygon's plane: basis (u, v) perpendicular to n.
        var u = Vector3.Normalize(MathF.Abs(n.Z) < 0.9f ? Vector3.Cross(n, Vector3.UnitZ) : Vector3.Cross(n, Vector3.UnitX));
        var v = Vector3.Cross(n, u);
        var pts = Hull2D(input, u, v);
        if (pts.Length < 3) return null;
        // Front plane through the outermost point, back plane a slab thickness behind the innermost.
        float front = input.Max(p => Vector3.Dot(n, p)), backD = input.Min(p => Vector3.Dot(n, p)) - SlabThickness;
        pts = pts.Select(p => p + n * (front - Vector3.Dot(n, p))).ToArray();
        var back = pts.Select(p => p - n * (front - backD)).ToArray();
        var planes = new List<System.Numerics.Vector4>
        {
            Plane(n, pts[0]),
            Plane(-n, back[0]),
        };
        var centroid = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Length;
        for (int i = 0; i < pts.Length; i++)
        {
            var e = pts[(i + 1) % pts.Length] - pts[i];
            var s = Vector3.Normalize(Vector3.Cross(e, n));
            if (Vector3.Dot(s, pts[i] - centroid) < 0) s = -s;
            planes.Add(Plane(s, pts[i]));
        }
        var shape = Owned(new bhkConvexVerticesShape { radius = 0.01f });
        shape.vertsProp.capacityAndFlags = 0x80000000;
        shape.normalsProp.capacityAndFlags = 0x80000000;
        shape.SetMaterial(material);
        using (var sv = shape.verts)
            foreach (var p in pts.Concat(back))
            {
                using var v4 = new nifly.Vector4(p.X / HavokScale, p.Y / HavokScale, p.Z / HavokScale, 0);
                sv.push_back(v4);
            }
        using (var sn = shape.normals)
            foreach (var pl in planes)
            {
                using var v4 = new nifly.Vector4(pl.X, pl.Y, pl.Z, pl.W);
                sn.push_back(v4);
            }
        return shape;
    }

    /// <summary>Convex hull (counter-clockwise, no collinear points) of points projected on the (u, v) plane.</summary>
    static Vector3[] Hull2D(Vector3[] pts, Vector3 u, Vector3 v)
    {
        var sorted = pts.Select(p => (P: p, X: Vector3.Dot(p, u), Y: Vector3.Dot(p, v)))
            .GroupBy(q => (MathF.Round(q.X, 2), MathF.Round(q.Y, 2))).Select(g => g.First())
            .OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (sorted.Count < 3) return [];
        static float Cross((Vector3 P, float X, float Y) o, (Vector3 P, float X, float Y) a, (Vector3 P, float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(Vector3 P, float X, float Y)>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            int start = hull.Count;
            foreach (var q in pass)
            {
                while (hull.Count - start >= 2 && Cross(hull[^2], hull[^1], q) <= 1e-3f) hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull.Count >= 3 ? hull.Select(q => q.P).ToArray() : [];
    }

    /// <summary>
    /// nifly takes ownership of blocks handed to AddBlock/ReplaceBlock/AssignExtraData; stop the .NET
    /// finalizer from freeing them a second time.
    /// </summary>
    internal static T Owned<T>(T block) where T : class { GC.SuppressFinalize(block); return block; }

    // nifly's small values (vectors, triangles) are copied into its lists, so each wrapper is freed as soon as it is
    // added rather than left to the finalizer (2026-10-15: town part 9's finalizer thread was busy for 111 of its 127
    // seconds freeing them one by one, under a lock, and every one of them outlived a collection first).
    internal static void Add(vectorVector3 list, float x, float y, float z) { using var v = new nifly.Vector3(x, y, z); list.Add(v); }
    internal static void Add(vectorVector2 list, float u, float v) { using var w = new nifly.Vector2(u, v); list.Add(w); }
    internal static void Add(vectorTriangle list, ushort a, ushort b, ushort c) { using var t = new Triangle(a, b, c); list.Add(t); }

    /// <summary>Havok plane equation in Havok units: n·x + w = 0, positive outside.</summary>
    static System.Numerics.Vector4 Plane(Vector3 n, Vector3 p) => new(n.X, n.Y, n.Z, -Vector3.Dot(n, p) / HavokScale);
}
