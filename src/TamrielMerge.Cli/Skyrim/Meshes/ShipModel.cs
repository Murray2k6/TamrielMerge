using System.Numerics;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using Noggog;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// The ship that stands in for Redguard's and Daggerfall's ships (user, 2026-10-09: "redguards and daggerfalls ship need
/// replaced with this", a free CGTrader model; "ship.fbx and tex.zip ... need to be included in the tool"): tools/ship
/// holds ship.fbx, its textures (tex/) and links.json (each material's texture files, read from the FBX's own
/// connections by fbx_links.py, as Blender's importer misses them). Blender (export.py) writes the model's parts, one
/// OBJ per material, and a simplified hull for collision; this writes one NIF (a shape per material, its diffuse, normal
/// and specular maps; sails, rigging and windows two-sided) Length long with the bow along +Y and the keel at z = 0, and
/// a static for it. Placements scale it to the ship it replaces.
/// </summary>
public static class ShipModel
{
    /// <summary>The model's length (Skyrim units, bow along +Y).</summary>
    public const float Length = 3000;
    /// <summary>How deep the ship sits in the water (model units over the keel, at scale 1), from its main deck's height:
    /// 0.45 of the way from the keel up to the deck, under where its hull reaches its full width (the collision hull's
    /// half width is 434 at 320 and 514 from 416 up, the deck at 448).</summary>
    public static float Draught(float deck) => deck * 0.45f;
    const string MeshRel = @"meshes\tamrielmerge\ship\ship.nif", TexFolder = @"textures\tamrielmerge\ship";

    // ---- the captain's quarters and the wheel deck over them ----
    // The model's stern is an open deck between high bulwarks with the wheel standing on it: no cabin, no deck for the
    // wheel (user, 2026-11-12, on Brennan's ship: "the ship does not have a captain's quarter and wheel deck with walls
    // to have it"; tools/ship measured: the deck at 470 from stem to stern, the bulwarks' tops 700 to 716 from y -800
    // aft against 520 amidships, a small platform at 585 to 692 abaft y -1400). Built on: a wall across the deck where
    // the high bulwarks begin, a deck from it aft on the bulwarks' tops with a rail round it, steps up to it from the
    // main deck, and the wheel stood on it. The quarters themselves are the interior behind the wall's door (RgHolds).
    /// <summary>The quarters' front wall (y, model space) and the wheel deck's height.</summary>
    public const float QuartersWallY = -830, WheelDeckZ = 705;
    const float QuartersAftY = -1400, QuartersFrontHalf = 480, QuartersAftHalf = 345, VisualDeckZ = 470, StairDepth = 90, StairFromX = 150, StairToX = 438, RailHeight = 90;
    /// <summary>Where the quarters' door stands: in the front wall to port of the mast (which stands before the wall's
    /// middle: a door there was hidden behind it, seen in game 2026-11-12), on the main deck, facing the bow (+Y).</summary>
    public const float QuartersDoorX = -230;
    public static Vector3 QuartersDoor(float deck) => new(QuartersDoorX, QuartersWallY, deck);

    /// <summary>The quarters' and wheel deck's faces: what is drawn (flat faces, each with its normal) and what is solid.</summary>
    static (List<(Vector3[] Pts, Vector3 N)> Drawn, List<(Vector3[] Pts, Vector3 N)> Solid) Quarters(float deck)
    {
        var drawn = new List<(Vector3[] Pts, Vector3 N)>();
        var solid = new List<(Vector3[] Pts, Vector3 N)>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, bool collide = true)
        {
            // Wound so the face's own normal is n.
            var pts = Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0 ? new[] { a, b, c, d } : new[] { d, c, b, a };
            drawn.Add((pts, n));
            if (collide) solid.Add((pts, n));
        }
        void Box(Vector3 lo, Vector3 hi, bool collide)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
            Quad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), Vector3.UnitZ, collide);
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1), -Vector3.UnitX, collide);
            Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1), Vector3.UnitX, collide);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), -Vector3.UnitY, collide);
            Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1), Vector3.UnitY, collide);
        }
        float fy = QuartersWallY, ay = QuartersAftY, fh = QuartersFrontHalf, ah = QuartersAftHalf, top = WheelDeckZ;
        // The front wall, from the main deck (the collision's and the drawn one, a little apart) to the wheel deck.
        Quad(new(-fh, fy, deck), new(fh, fy, deck), new(fh, fy, top), new(-fh, fy, top), Vector3.UnitY);
        // The wheel deck, and its end over the stern platform.
        Quad(new(-fh, fy, top), new(fh, fy, top), new(ah, ay, top), new(-ah, ay, top), Vector3.UnitZ);
        Quad(new(-ah, ay, 585), new(ah, ay, 585), new(ah, ay, top), new(-ah, ay, top), -Vector3.UnitY);
        // The steps up, along the wall to starboard: 12 of them, their treads and risers drawn, a ramp to walk on.
        const int steps = 12;
        float run = (StairToX - StairFromX) / steps, rise = (top - deck) / steps, y0 = fy, y1 = fy + StairDepth;
        for (int i = 0; i < steps; i++)
        {
            float x0 = StairFromX + run * i, x1 = x0 + run, z0 = deck + rise * i, z1 = z0 + rise;
            Quad(new(x0, y0, z0), new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), -Vector3.UnitX, collide: false);
            Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), Vector3.UnitZ, collide: false);
            // The side towards the bow, closed down to the deck.
            Quad(new(x0, y1, deck), new(x1, y1, deck), new(x1, y1, z1), new(x0, y1, z1), Vector3.UnitY, collide: false);
        }
        var up = Vector3.Normalize(new Vector3(-(top - deck), 0, StairToX - StairFromX));
        solid.Add((new[] { new Vector3(StairFromX, y0, deck), new Vector3(StairToX, y0, top), new Vector3(StairToX, y1, top), new Vector3(StairFromX, y1, deck) }, up));
        solid.Add((new[] { new Vector3(StairFromX, y1, deck), new Vector3(StairToX, y1, deck), new Vector3(StairToX, y1, top) }, Vector3.UnitY));
        // The steps' head: a landing as wide as the steps, out to the bulwark.
        Quad(new(StairToX, y0, top), new(fh, y0, top), new(fh, y1, top), new(StairToX, y1, top), Vector3.UnitZ);
        Quad(new(StairToX, y1, deck), new(fh, y1, deck), new(fh, y1, top), new(StairToX, y1, top), Vector3.UnitY);
        // The rail: posts and a bar along the wheel deck's front (open at the steps' head), its sides and its end.
        void Rail(Vector2 from, Vector2 to)
        {
            var along = to - from; float len = along.Length();
            if (len < 1) return;
            var dir = along / len; var across = new Vector2(-dir.Y, dir.X) * 4;
            int posts = Math.Max(2, (int)MathF.Round(len / 96) + 1);
            for (int i = 0; i < posts; i++)
            {
                var c = from + dir * (len * i / (posts - 1));
                Box(new Vector3(c.X - 5, c.Y - 5, top), new Vector3(c.X + 5, c.Y + 5, top + RailHeight - 8), collide: false);
            }
            // The bar, a slim box along the run (drawn as its four long faces).
            Vector3 A(Vector2 q, float z) => new(q.X, q.Y, z);
            float b0 = top + RailHeight - 8, b1 = top + RailHeight;
            var n2 = Vector2.Normalize(across); var n3 = new Vector3(n2.X, n2.Y, 0);
            Quad(A(from - across, b1), A(to - across, b1), A(to + across, b1), A(from + across, b1), Vector3.UnitZ, collide: false);
            Quad(A(from - across, b0), A(to - across, b0), A(to - across, b1), A(from - across, b1), -n3, collide: false);
            Quad(A(from + across, b0), A(to + across, b0), A(to + across, b1), A(from + across, b1), n3, collide: false);
            Quad(A(from - across, b0), A(to - across, b0), A(to + across, b0), A(from + across, b0), -Vector3.UnitZ, collide: false);
            // Solid as one upright face each way.
            solid.Add((new[] { A(from, top), A(to, top), A(to, b1), A(from, b1) }, n3));
        }
        Rail(new Vector2(-fh + 6, fy - 6), new Vector2(StairToX - 12, fy - 6));
        Rail(new Vector2(-fh + 6, fy - 6), new Vector2(-ah + 6, ay + 6));
        Rail(new Vector2(fh - 6, fy + StairDepth - 6), new Vector2(fh - 6, fy - 6));
        Rail(new Vector2(fh - 6, fy - 6), new Vector2(ah - 6, ay + 6));
        Rail(new Vector2(-ah + 6, ay + 6), new Vector2(ah - 6, ay + 6));
        return (drawn, solid);
    }

    // ---- the deck guns ----
    // The model has no guns on its deck: what it calls "window" and "window2" are the lids and rings of eight ports in
    // the hull's side below the main deck (their middles 258 to 476 above the keel against the deck's 470; tools/ship,
    // measured 2026-11-12), and seen from the deck there was nothing (user, watching the opening: "the cannons werent
    // showing either"; the black discs taken for guns were those lids seen from inside the hold by a free camera).
    // Built on: three guns a side in the waist, each a tapered iron barrel with a muzzle swell and a bore, lying
    // over the bulwark on a wooden carriage with four trucks. In the waist, y -400 to -20, the bulwark is low (its top
    // 519 to 546 over a deck at 470; measured, scratch gunplace.py): before y 200 and abaft y -500 it stands 700 and
    // more, and at the bow the hull narrows to 353 a side.
    static readonly float[] GunYs = [-360, -210, -60];
    const float GunLength = 166, GunTexture = 64;
    const int GunAround = 14;
    const string GunTex = TexFolder + @"\gun.dds";

    /// <summary>The guns: the barrels (smooth, one mesh), the carriages' flat faces and what is solid.</summary>
    static (List<Vector3> V, List<Vector3> N, List<Vector2> Uv, List<(int, int, int)> T, List<(Vector3[] Pts, Vector3 N)> Wood, List<(Vector3[] Pts, Vector3 N)> Solid, int Count)
        Guns(float deck, List<Vector3> hull)
    {
        var V = new List<Vector3>(); var N = new List<Vector3>(); var Uv = new List<Vector2>(); var T = new List<(int, int, int)>();
        var wood = new List<(Vector3[] Pts, Vector3 N)>(); var solid = new List<(Vector3[] Pts, Vector3 N)>();
        int count = 0;
        void Face(List<(Vector3[] Pts, Vector3 N)> to, Vector3 n, params Vector3[] q)
            => to.Add((Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), n) >= 0 ? q : q.Reverse().ToArray(), n));
        void Box(List<(Vector3[] Pts, Vector3 N)> to, Vector3 lo, Vector3 hi)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
            Face(to, Vector3.UnitZ, P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));
            Face(to, -Vector3.UnitX, P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1));
            Face(to, Vector3.UnitX, P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));
            Face(to, -Vector3.UnitY, P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
            Face(to, Vector3.UnitY, P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
        }
        // The barrel's outline from the breech's middle round to the bore's end: (along, radius).
        (float T, float R)[] outline = [(-9, 0), (-9, 5), (-3, 6), (0, 9), (0, 17), (6, 18.5f), (22, 18), (26, 16), (150, 11.5f), (154, 14.5f), (164, 14.5f), (GunLength, 12), (GunLength, 7), (60, 7)];
        const int around = GunAround;
        foreach (float y in GunYs)
            foreach (int side in new[] { -1, 1 })
            {
                // The bulwark here: how far out the hull stands at the deck, and how high.
                // (Both sides read together: the hull is the same either side, its vertices not.)
                var near = hull.Where(v => MathF.Abs(v.Y - y) < 80 && v.Z > deck - 10 && v.Z < deck + 110).ToList();
                if (near.Count == 0) continue;
                float half = near.Max(v => MathF.Abs(v.X));
                float top = near.Where(v => MathF.Abs(v.X) > half - 60).Max(v => v.Z);
                float axisZ = MathF.Max(deck + 52, top + 17);
                float muzzle = half + 36, breech = muzzle - GunLength;
                var a = new Vector3(side, 0, 0); var w = new Vector3(0, 1, 0); var u = Vector3.UnitZ;
                var from = new Vector3(side * breech, y, axisZ);
                int at = V.Count;
                count++;
                for (int i = 0; i < outline.Length; i++)
                {
                    // The outline's outward side: its direction turned a quarter (the mean of the two runs meeting here).
                    Vector2 Run(int k) { var d = new Vector2(outline[k + 1].T - outline[k].T, outline[k + 1].R - outline[k].R); return d.LengthSquared() < 1e-6f ? Vector2.Zero : Vector2.Normalize(new Vector2(-d.Y, d.X)); }
                    var n2 = (i > 0 ? Run(i - 1) : Vector2.Zero) + (i + 1 < outline.Length ? Run(i) : Vector2.Zero);
                    n2 = n2.LengthSquared() < 1e-6f ? new Vector2(-1, 0) : Vector2.Normalize(n2);
                    for (int j = 0; j <= around; j++)
                    {
                        float th = MathF.Tau * j / around;
                        var radial = w * MathF.Cos(th) + u * MathF.Sin(th);
                        V.Add(from + a * outline[i].T + radial * outline[i].R);
                        N.Add(Vector3.Normalize(a * n2.X + radial * n2.Y));
                        Uv.Add(new Vector2(outline[i].T / GunTexture, (float)j / around * 2));
                    }
                }
                for (int i = 0; i + 1 < outline.Length; i++)
                    for (int j = 0; j < around; j++)
                    {
                        int p0 = at + i * (around + 1) + j, p1 = p0 + 1, q0 = p0 + around + 1, q1 = q0 + 1;
                        // Wound so each face looks the way its corners' normals do.
                        var fn = Vector3.Cross(V[q0] - V[p0], V[p1] - V[p0]);
                        if (fn.LengthSquared() < 1e-6f) fn = Vector3.Cross(V[q1] - V[q0], V[p1] - V[q0]);
                        bool keep = Vector3.Dot(fn, N[p0] + N[p1] + N[q0] + N[q1]) >= 0;
                        if (keep) { T.Add((p0, q0, p1)); T.Add((p1, q0, q1)); } else { T.Add((p0, p1, q0)); T.Add((p1, q1, q0)); }
                    }
                // The carriage: a bed on the deck, a cheek each side of the barrel up to its trunnions, an axle's truck at
                // each corner. It ends inside the bulwark.
                float inner = MathF.Min(breech + 104, half - 16), c0 = breech - 6;
                float X(float along) => side * along;
                Vector3 Lo(float x0, float x1, float y0, float z0) => new(MathF.Min(X(x0), X(x1)), y0, z0);
                Vector3 Hi(float x0, float x1, float y1, float z1) => new(MathF.Max(X(x0), X(x1)), y1, z1);
                Box(wood, Lo(c0, inner, y - 23, deck + 12), Hi(c0, inner, y + 23, deck + 22));
                foreach (int cs in new[] { -1, 1 })
                {
                    float y0 = y + cs * 19 - 5, y1 = y + cs * 19 + 5;
                    Box(wood, Lo(c0, inner, y0, deck + 22), Hi(c0, inner, y1, axisZ - 8));
                    // The cheek's step up at the trunnions (the forward half stands higher).
                    Box(wood, Lo((c0 + inner) / 2, inner, y0, axisZ - 8), Hi((c0 + inner) / 2, inner, y1, axisZ + 3));
                    foreach (float ax in new[] { c0 + 16, inner - 16 })
                    {
                        // A truck: a wooden disc on the carriage's side, ten-sided.
                        const int sides = 10; const float r = 13;
                        float yIn = y + cs * 24, yOut = y + cs * 32;
                        var rimIn = new Vector3[sides]; var rimOut = new Vector3[sides];
                        for (int k = 0; k < sides; k++)
                        {
                            float th = MathF.Tau * k / sides;
                            var off = new Vector3(MathF.Cos(th) * r, 0, MathF.Sin(th) * r);
                            rimIn[k] = new Vector3(X(ax), yIn, deck + r) + off; rimOut[k] = new Vector3(X(ax), yOut, deck + r) + off;
                        }
                        Face(wood, new Vector3(0, cs, 0), rimOut);
                        for (int k = 0; k < sides; k++)
                        {
                            int k1 = (k + 1) % sides;
                            float mid = MathF.Tau * (k + 0.5f) / sides;
                            Face(wood, new Vector3(MathF.Cos(mid), 0, MathF.Sin(mid)), rimIn[k], rimIn[k1], rimOut[k1], rimOut[k]);
                        }
                    }
                }
                // Solid: the carriage and the barrel inboard of the bulwark as one block.
                Box(solid, Lo(breech - 9, half, y - 32, deck), Hi(breech - 9, half, y + 32, axisZ + 18));
            }
        return (V, N, Uv, T, wood, solid, count);
    }

    /// <summary>The guns' iron: dark, a little uneven, drawn once.</summary>
    static void WriteGunTexture(string path)
    {
        const int size = 64;
        var px = new uint[size * size];
        var rng = new Random(1701);
        for (int i = 0; i < px.Length; i++)
        {
            int g = 62 + rng.Next(-7, 8);
            px[i] = 0xFF000000u | (uint)g << 16 | (uint)(g + 2) << 8 | (uint)(g + 6);
        }
        Morrowind.MwTextures.WriteBcDds(path, px, size, size, false);
    }

    /// <param name="Deck">The main deck's height above the keel (model units, at scale 1).</param>
    /// <param name="Solid">The collision's triangles (model space), for navmeshes on its decks.</param>
    public sealed record Made(Static Static, Vector3 Min, Vector3 Max, float Deck, List<Vector3[]> Solid)
    {
        /// <summary>Where the captain's quarters' door stands (model space, at scale 1): on the main deck at the middle of
        /// the quarters' front wall, the way out of it along +Y.</summary>
        public Vector3 QuartersDoorAt => QuartersDoor(Deck);
    }

    static readonly Dictionary<string, (Vector3 Min, Vector3 Max, float Deck, List<Vector3[]> Solid)> written = [];

    /// <summary>The ship's static in <paramref name="mod"/> (its mesh and textures written to <paramref name="modOutDir"/> once).</summary>
    public static Made? Build(SkyrimMod mod, string modOutDir, string editorId, List<string> problems)
    {
        try
        {
            if (!written.TryGetValue(Path.GetFullPath(modOutDir), out var bounds))
                written[Path.GetFullPath(modOutDir)] = bounds = Write(modOutDir);
            short C(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
            var stat = new Static(mod)
            {
                EditorID = editorId,
                ObjectBounds = new ObjectBounds { First = new P3Int16(C(bounds.Min.X), C(bounds.Min.Y), C(bounds.Min.Z)), Second = new P3Int16(C(bounds.Max.X), C(bounds.Max.Y), C(bounds.Max.Z)) },
                Model = new Model { File = MeshRel[7..] },
            };
            mod.Statics.Add(stat);
            return new Made(stat, bounds.Min, bounds.Max, bounds.Deck, bounds.Solid);
        }
        catch (Exception e) { problems.Add($"ship: {e.Message}"); return null; }
    }

    /// <summary>Dev aid (the ship-nif command): the ship's mesh and textures written to a folder, nothing else.</summary>
    public static void WriteOnly(string modOutDir) => Write(modOutDir);

    static (Vector3 Min, Vector3 Max, float Deck, List<Vector3[]> Solid) Write(string modOutDir)
    {
        string dir = Path.GetDirectoryName(Setup.ToolScripts.Path("ship", "ship.fbx"))!;
        if (!File.Exists(Path.Combine(dir, "ship.fbx"))) throw new FileNotFoundException("tools\\ship\\ship.fbx is missing");
        string parts = Path.Combine(dir, "parts");
        if (!Directory.Exists(parts) || !File.Exists(Path.Combine(dir, "collision.obj")))
        {
            string blender = Setup.Dependencies.Blender ?? throw new InvalidOperationException("Blender is needed once to read ship.fbx: run setup");
            var psi = new System.Diagnostics.ProcessStartInfo(blender) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-b", "-P", Path.Combine(dir, "export.py"), "--", dir }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!Directory.Exists(parts)) throw new InvalidOperationException("Blender didn't write the ship's parts");
        }
        var links = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(dir, "links.json")))!;

        var pieces = Directory.GetFiles(parts, "*.obj").OrderBy(f => f).Select(f => (Name: Path.GetFileNameWithoutExtension(f), Mesh: SpriteModelImporter.ReadObj(f))).ToList();
        // Dev test (TM_SHIP_TEST=1, the ship-nif command): the eight port lids as eight shapes, each drawn another way, to
        // see in game which of them is not black.
        bool shipTest = Environment.GetEnvironmentVariable("TM_SHIP_TEST") == "1";
        if (shipTest)
        {
            var win = pieces.First(q => q.Name == "window");
            pieces.Remove(win);
            int Band(float y) => y < -0.05f ? 0 : y < 0.13f ? 1 : y < 0.31f ? 2 : 3;
            for (int k = 0; k < 8; k++)
            {
                var map = new Dictionary<int, int>();
                var m = (V: new List<Vector3>(), Uv: new List<Vector2>(), N: new List<Vector3>(), T: new List<(int, int, int)>());
                int Ix(int i) { if (!map.TryGetValue(i, out int j)) { map[i] = j = m.V.Count; m.V.Add(win.Mesh.V[i]); m.Uv.Add(win.Mesh.Uv[i]); m.N.Add(win.Mesh.N[i]); } return j; }
                foreach (var (a, b, c) in win.Mesh.T)
                {
                    var p0 = win.Mesh.V[a];
                    if ((p0.X > 0 ? 4 : 0) + Band(p0.Y) == k) m.T.Add((Ix(a), Ix(b), Ix(c)));
                }
                pieces.Add(("window#" + k, m));
            }
            // The rings (window2) of the model's -x side left out.
            var ring = pieces.First(q => q.Name == "window2");
            pieces.Remove(ring);
            var kept = (V: ring.Mesh.V, Uv: ring.Mesh.Uv, N: ring.Mesh.N, T: ring.Mesh.T.Where(tr => ring.Mesh.V[tr.Item1].X > 0).ToList());
            pieces.Add(("window2", kept));
        }
        // The model's port lids and their rings ("window", "window2": eight drums 96 across lying through the bulwark,
        // 51 of their length inboard on the deck) are left out. In the game they drew black from the deck under every
        // light (noon sun, a Candlelight beside them at night: 0, 0, 4 of 255) and with every material tried on them
        // one by one (their own, the guns' texture, a flat normal map, new texture coordinates, two-sided, no specular,
        // normals made again; test builds of 2026-11-12, tools/ingame/pan_all.png, pbn_all.png): why is not found, the
        // ship-nif test above is kept for it. They stood on the deck as big as a man and Cyrus walked into one and
        // stuck (dk2.png; user: "black something"); the hull behind them is whole (a ray from the deck's middle meets
        // the lid, then the hull's inner and outer faces), and the deck's guns are Guns().
        else pieces.RemoveAll(q => q.Name is "window" or "window2");
        var testIntoGuns = new List<(Vector3 P, Vector3 N)[]>();
        // Blender's frame: x across, y along, z up. Which end is the bow: the hull's narrower end.
        var hull = pieces.Where(p => p.Name.StartsWith("korpus", StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Mesh.V).ToList();
        var all = pieces.SelectMany(p => p.Mesh.V).ToList();
        float y0 = all.Min(v => v.Y), y1 = all.Max(v => v.Y), span = y1 - y0;
        float Width(Func<Vector3, bool> where) { var w = hull.Where(where).ToList(); return w.Count == 0 ? 0 : w.Max(v => v.X) - w.Min(v => v.X); }
        bool bowAtMinY = Width(v => v.Y < y0 + span * 0.12f) < Width(v => v.Y > y1 - span * 0.12f);
        float s = Length / span;
        var lo3 = all.Aggregate(Vector3.Min); var hi3 = all.Aggregate(Vector3.Max);
        var shift = new Vector3(-(lo3.X + hi3.X) / 2, -(lo3.Y + hi3.Y) / 2, -lo3.Z);
        Vector3 Place(Vector3 v) { v = (v + shift) * s; return bowAtMinY ? new Vector3(-v.X, -v.Y, v.Z) : v; }
        Vector3 Turn(Vector3 n) => bowAtMinY ? new Vector3(-n.X, -n.Y, n.Z) : n;

        Directory.CreateDirectory(Path.Combine(modOutDir, TexFolder));
        string? Texture(string? file, Func<uint[], int, int, uint[]>? edit, bool alpha, string suffix, string material)
        {
            if (file is null || !File.Exists(Path.Combine(dir, "tex", file))) return null;
            var (px, w, h) = Util.Png.Read(Path.Combine(dir, "tex", file));
            if (edit is not null) px = edit(px, w, h);
            string rel = $@"{TexFolder}\{material.Replace('.', '_').ToLowerInvariant()}{suffix}.dds";
            Morrowind.MwTextures.WriteBcDds(Path.Combine(modOutDir, rel), px, w, h, alpha);
            return rel;
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var hdr = nif.GetHeader();
        var fade = BlockMeshBuilder.Owned(new BSFadeNode());
        fade.name = new NiStringRef("TMShip");
        hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        var deckV = new List<Vector3>(); var deckN = new List<Vector3>(); var deckT = new List<(int, int, int)>();
        foreach (var (name, (v, uv, n, t)) in pieces)
        {
            if (v.Count > 65535) throw new InvalidDataException($"ship part {name}: {v.Count} vertices (a NIF shape holds 65535)");
            // (The test's shapes "window#k": the part "window" drawn the k-th way.)
            string part = name.Split('#')[0];
            int variant = name.Contains('#') ? int.Parse(name[(name.IndexOf('#') + 1)..]) : -1;
            links.TryGetValue(part, out var maps);
            maps ??= [];
            // Diffuse: its own alpha only where it has some (the windows' glass).
            bool cutout = false;
            // Near-black iron (the cannons: the model's texture averages 12, 13, 14 of 255, measured 2026-11-10, and in
            // game they were featureless black shapes on the deck): lifted to dark iron, so the light shows their form.
            // Parts that are wholly black (inside the gun ports, behind the windows) stay black.
            bool iron = false;
            // The parts the model calls "window" and "window2" are the lids and rings of the eight ports in the hull's
            // side below the deck, their textures 5, 8, 14 with a glass's alpha and plain black (tools/ship, measured
            // 2026-11-12): iron like the bow guns, lifted the same way, and opaque. (The guns on the deck are Guns().)
            bool gun = name.Contains("window", StringComparison.OrdinalIgnoreCase);
            uint[] Iron(uint[] px)
            {
                if (gun)
                {
                    for (int i = 0; i < px.Length; i++)
                    {
                        uint c = px[i];
                        uint G(int sh) => (uint)Math.Min(255, 62 + (int)((c >> sh) & 0xFF) * 2);
                        px[i] = 0xFF000000u | G(16) << 16 | G(8) << 8 | G(0);
                    }
                    return px;
                }
                if (!iron) return px;
                for (int i = 0; i < px.Length; i++)
                {
                    uint c = px[i];
                    // 55 + 1.8 c: the texture's mean of 13 becomes 78, its brightest 78 becomes 195. The first lift (30 + 1.5 c,
                    // a mean of 49) still drew as black shapes in the game (user, 2026-11-12, watching the opening on
                    // Brennan's deck: "the cannons werent showing either").
                    uint L(int sh) => (uint)Math.Min(255, 55 + (int)((c >> sh) & 0xFF) * 9 / 5);
                    px[i] = (c & 0xFF000000u) | L(16) << 16 | L(8) << 8 | L(0);
                }
                return px;
            }
            string? diffuse = Texture(maps.GetValueOrDefault("DiffuseColor"), (px, w, h) =>
            {
                cutout = !gun && px.Any(c => c >> 24 < 250);
                long sum = 0; uint top = 0;
                foreach (uint c in px) { uint m = Math.Max((c >> 16) & 0xFF, Math.Max((c >> 8) & 0xFF, c & 0xFF)); sum += m; top = Math.Max(top, m); }
                iron = px.Length > 0 && sum / px.Length < 24 && top > 16;
                return Iron(px);
            }, alpha: true, "", part);
            if (!cutout && diffuse is not null) Texture(maps.GetValueOrDefault("DiffuseColor"), (px, w, h) => { for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u; return Iron(px); }, alpha: false, "", part);
            // The hull's level wood (decks, the tops of rails and steps) drawn with Skyrim's planks, laid along the ship:
            // the model's own texture is one 2048 sheet for a whole part, the deck a small piece of it, and up close it
            // was a smear (seen in game 2026-11-10). Left as it is where a part is all level.
            var rest = t.ToList();
            if (name.StartsWith("korpus", StringComparison.OrdinalIgnoreCase) && !iron && !cutout)
            {
                var level = rest.Where(tri => n[tri.Item1].Z > 0.95f && n[tri.Item2].Z > 0.95f && n[tri.Item3].Z > 0.95f).ToList();
                if (level.Count > 0 && level.Count < rest.Count)
                {
                    rest = rest.Except(level).ToList();
                    foreach (var tri in level)
                    {
                        int at = deckV.Count;
                        if (at + 3 > 65535) break;
                        foreach (int i in new[] { tri.Item1, tri.Item2, tri.Item3 })
                        {
                            var pp = Place(v[i]);
                            deckV.Add(pp); deckN.Add(Turn(n[i]));
                        }
                        deckT.Add((at, at + 1, at + 2));
                    }
                }
            }
            // Normal map with the specular map's brightness in its alpha (Skyrim's specular mask).
            string? specFile = maps.GetValueOrDefault("SpecularColor");
            string? normal = Texture(maps.GetValueOrDefault("NormalMap"), (px, w, h) =>
            {
                uint[]? spec = specFile is not null && File.Exists(Path.Combine(dir, "tex", specFile)) ? Util.Png.Read(Path.Combine(dir, "tex", specFile)).Argb : null;
                for (int i = 0; i < px.Length; i++)
                {
                    uint a = 0;
                    if (spec is not null && spec.Length == px.Length)
                    {
                        uint c = spec[i];
                        a = (uint)Math.Clamp((0.299f * ((c >> 16) & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * (c & 0xFF)) * 0.6f, 0, 255);
                    }
                    px[i] = (px[i] & 0xFFFFFFu) | a << 24;
                }
                return px;
            }, alpha: true, "_n", part);

            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            // The wheel stands on the wheel deck (it stood on the main deck where the quarters now are).
            float lift = name.Equals("wheel", StringComparison.OrdinalIgnoreCase) ? WheelDeckZ - VisualDeckZ : 0;
            // Test 7: each vertex's normal made again from the faces that meet at it.
            Vector3[]? again = null;
            if (variant == 7)
            {
                again = new Vector3[v.Count];
                foreach (var (a, b, c) in rest) { var fn = Vector3.Cross(v[b] - v[a], v[c] - v[a]); again[a] += fn; again[b] += fn; again[c] += fn; }
                for (int i = 0; i < again.Length; i++) again[i] = again[i].LengthSquared() > 0 ? Vector3.Normalize(again[i]) : n[i];
            }
            if (variant == 1)
            {
                // Test 1: drawn as part of the guns' own shape (below), not as a shape of its own.
                foreach (var (a, b, c) in rest) testIntoGuns.Add([(Place(v[a]), Turn(n[a])), (Place(v[b]), Turn(n[b])), (Place(v[c]), Turn(n[c]))]);
                var mid1 = v.Aggregate(Vector3.Zero, (acc, q) => acc + Place(q)) / v.Count;
                Console.WriteLine($"  ship test: lid 1 at {mid1.X:0}, {mid1.Y:0}, {mid1.Z:0}: inside the guns' shape");
                continue;
            }
            for (int i = 0; i < v.Count; i++)
            {
                var p = Place(v[i]);
                p.Z += lift;
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                vv.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                if (variant == 4) uu.Add(new nifly.Vector2(p.Y / 64f, p.Z / 64f)); else uu.Add(new nifly.Vector2(uv[i].X, uv[i].Y));
                var q = Turn(again is not null ? again[i] : n[i]);
                if (variant == 5) q = -q;
                nn.Add(new nifly.Vector3(q.X, q.Y, q.Z));
            }
            foreach (var (a, b, c) in rest) tt.Add(variant == 5 ? new Triangle((ushort)a, (ushort)c, (ushort)b) : new Triangle((ushort)a, (ushort)b, (ushort)c));
            var shape = nif.CreateShapeFromData("TMShip_" + name, vv, tt, uu, nn);
            if (variant == 4) { diffuse = GunTex; normal = null; }
            if (variant >= 0)
            {
                var mid = v.Aggregate(Vector3.Zero, (acc, q) => acc + Place(q)) / v.Count;
                Console.WriteLine($"  ship test: lid {variant} at {mid.X:0}, {mid.Y:0}, {mid.Z:0} (model space): " + variant switch
                {
                    0 => "as it is", 1 => "flat normal map", 2 => "no shadows cast or received", 3 => "lit by itself (red)", 4 => "new texture coordinates, the guns' texture, flat normal map",
                    5 => "winding and normals turned round", 6 => "no specular", _ => "normals made again from the faces",
                });
            }
            if (diffuse is not null) nif.SetTextureSlot(shape, diffuse, 0);
            nif.SetTextureSlot(shape, normal ?? DfTextureExport.FlatNormal, 1);
            nif.CalcTangentsForShape(shape);
            if (cutout) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x12ED, threshold = 0 }));   // glass: blended
            // Two-sided: the sails and the rigging (the guns, the model's "window" parts, are closed bodies).
            bool thin = name.Contains("sail", StringComparison.OrdinalIgnoreCase) || name.Contains("cable", StringComparison.OrdinalIgnoreCase);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                sh.shaderFlags1 = variant == 6 ? 0x82400300u : variant == 2 ? 0x82400001u : 0x82400301u;   // specular, cast + receive shadows, ...
                sh.shaderFlags2 = 0x1u | 0x8000u | (thin ? 0x10u : 0); // ZBuffer write, ..., two-sided for thin parts
                if (variant == 3) { sh.emissiveColor = new nifly.Vector3(1, 0, 0); sh.emissiveMultiple = 2; }
                sh.glossiness = 40;
                sh.specularStrength = variant == 6 ? 0 : 1;
            });
        }

        if (deckT.Count > 0)
        {
            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            for (int i = 0; i < deckV.Count; i++)
            {
                var p = deckV[i]; var dn = deckN[i];
                vv.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                // A plank's length along the ship (the texture's planks run along its u).
                uu.Add(new nifly.Vector2(p.Y / 256f, p.X / 256f));
                nn.Add(new nifly.Vector3(dn.X, dn.Y, dn.Z));
            }
            foreach (var (a, b, c) in deckT) tt.Add(new Triangle((ushort)a, (ushort)b, (ushort)c));
            var shape = nif.CreateShapeFromData("TMShip_planks", vv, tt, uu, nn);
            nif.SetTextureSlot(shape, @"textures\architecture\riften\riftencanalplanks01.dds", 0);
            nif.SetTextureSlot(shape, @"textures\architecture\riften\riftencanalplanks01_n.dds", 1);
            nif.CalcTangentsForShape(shape);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                sh.shaderFlags1 = 0x82400301u;
                sh.shaderFlags2 = 0x1u | 0x8000u;
                sh.glossiness = 20;
                sh.specularStrength = 0.3f;
                sh.textureClampMode = TexClampMode.WRAP_S_WRAP_T;
            });
        }

        // Collision: the simplified hull, one convex slab per triangle.
        var (cv, _, _, ct) = SpriteModelImporter.ReadObj(Path.Combine(dir, "collision.obj"));
        var none = new BlockMeshBuilder.Tex("", "", false);
        var solid = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>();
        foreach (var (a, b, c) in ct)
        {
            var pa = Place(cv[a]); var pb = Place(cv[b]); var pc = Place(cv[c]);
            var nrm = Vector3.Cross(pb - pa, pc - pa);
            if (nrm.LengthSquared() < 1e-4f) continue;
            solid.Add(([pa, pb, pc], Vector3.Normalize(nrm), none));
        }
        // The main deck as the collision has it (see below), for the quarters to stand on.
        var walk0 = solid.Where(t => t.N.Z > 0.8f && t.Pts.All(p => MathF.Abs(p.Y) < Length / 3 && p.Z < (max.Z - min.Z) / 2))
            .Select(t => (Z: t.Pts.Average(p => p.Z), Area: Vector3.Cross(t.Pts[1] - t.Pts[0], t.Pts[2] - t.Pts[0]).Length() / 2)).ToList();
        float deck0 = walk0.Count == 0 ? VisualDeckZ : walk0.GroupBy(w => MathF.Round(w.Z / 16)).OrderByDescending(g => g.Sum(w => w.Area)).First() is var plane0 ? plane0.Sum(w => w.Z * w.Area) / plane0.Sum(w => w.Area) : VisualDeckZ;
        var (quartersDrawn, quartersSolid) = Quarters(deck0);
        var guns = Guns(deck0, hull.Select(Place).ToList());
        quartersDrawn.AddRange(guns.Wood);
        quartersSolid.AddRange(guns.Solid);
        if (guns.T.Count > 0)
        {
            WriteGunTexture(Path.Combine(modOutDir, GunTex));
            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            for (int i = 0; i < guns.V.Count; i++)
            {
                var q = guns.V[i]; var gn = guns.N[i];
                vv.Add(new nifly.Vector3(q.X, q.Y, q.Z)); nn.Add(new nifly.Vector3(gn.X, gn.Y, gn.Z)); uu.Add(new nifly.Vector2(guns.Uv[i].X, guns.Uv[i].Y));
                min = Vector3.Min(min, q); max = Vector3.Max(max, q);
            }
            foreach (var (a, b, c) in guns.T) tt.Add(new Triangle((ushort)a, (ushort)b, (ushort)c));
            foreach (var tri in testIntoGuns)
            {
                int at = vv.Count;
                foreach (var (q, gn) in tri) { vv.Add(new nifly.Vector3(q.X, q.Y, q.Z)); nn.Add(new nifly.Vector3(gn.X, gn.Y, gn.Z)); uu.Add(new nifly.Vector2(q.Y / 64f, q.Z / 64f)); }
                tt.Add(new Triangle((ushort)at, (ushort)(at + 1), (ushort)(at + 2)));
            }
            var shape = nif.CreateShapeFromData("TMShip_guns", vv, tt, uu, nn);
            nif.SetTextureSlot(shape, GunTex, 0);
            nif.SetTextureSlot(shape, DfTextureExport.FlatNormal, 1);
            nif.CalcTangentsForShape(shape);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                sh.shaderFlags1 = 0x82400301u;
                sh.shaderFlags2 = 0x1u | 0x8000u;
                sh.glossiness = 60;
                sh.specularStrength = 0.6f;
                sh.textureClampMode = TexClampMode.WRAP_S_WRAP_T;
            });
            Console.WriteLine($"  ship: {guns.Count} deck guns built ({guns.V.Count} vertices, {guns.T.Count} triangles), their carriages {guns.Wood.Count} faces");
        }
        {
            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            foreach (var (pts, fn) in quartersDrawn)
            {
                int at = vv.Count;
                foreach (var q in pts)
                {
                    vv.Add(new nifly.Vector3(q.X, q.Y, q.Z));
                    nn.Add(new nifly.Vector3(fn.X, fn.Y, fn.Z));
                    // Planks 256 units a texture, laid along the face's longer way: level faces along the ship, walls level.
                    var t2 = MathF.Abs(fn.Z) > 0.7f ? new Vector2(q.Y, q.X) : MathF.Abs(fn.Y) > 0.7f ? new Vector2(q.X, q.Z) : new Vector2(q.Y, q.Z);
                    uu.Add(new nifly.Vector2(t2.X / 256f, t2.Y / 256f));
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                }
                for (int k = 1; k + 1 < pts.Length; k++) tt.Add(new Triangle((ushort)at, (ushort)(at + k), (ushort)(at + k + 1)));
            }
            var shape = nif.CreateShapeFromData("TMShip_quarters", vv, tt, uu, nn);
            nif.SetTextureSlot(shape, @"textures\architecture\riften\riftencanalplanks01.dds", 0);
            nif.SetTextureSlot(shape, @"textures\architecture\riften\riftencanalplanks01_n.dds", 1);
            nif.CalcTangentsForShape(shape);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                sh.shaderFlags1 = 0x82400301u;
                sh.shaderFlags2 = 0x1u | 0x8000u;
                sh.glossiness = 20;
                sh.specularStrength = 0.3f;
                sh.textureClampMode = TexClampMode.WRAP_S_WRAP_T;
            });
        }
        foreach (var (pts, fn) in quartersSolid) solid.Add((pts, fn, none));
        if (solid.Count > 0) BlockMeshBuilder.AddCollision(nif, root, solid, moving: false);
        BlockMeshBuilder.AddBsxFlags(nif, root);
        string outPath = Path.Combine(modOutDir, MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
        // The deck: the height with the most upward-facing area in the middle third of the ship (below the tops and crow's
        // nests: the lower half of its height). By area, not by count: the hold's floor is many small faces and won by
        // count (z 64-96 against the deck's 448; 2026-10-25, Redguard's opening stood Cyrus in the hold).
        var walk = solid.Where(t => t.N.Z > 0.8f && t.Pts.All(p => MathF.Abs(p.Y) < Length / 3 && p.Z < (max.Z - min.Z) / 2))
            .Select(t => (Z: t.Pts.Average(p => p.Z), Area: Vector3.Cross(t.Pts[1] - t.Pts[0], t.Pts[2] - t.Pts[0]).Length() / 2)).ToList();
        float deck = walk.Count == 0 ? (max.Z - min.Z) * 0.3f
            : walk.GroupBy(w => MathF.Round(w.Z / 16)).OrderByDescending(g => g.Sum(w => w.Area)).First() is var plane ? plane.Sum(w => w.Z * w.Area) / plane.Sum(w => w.Area) : 0;
        return (min, max, deck, solid.Select(t => t.Pts).ToList());
    }
}
