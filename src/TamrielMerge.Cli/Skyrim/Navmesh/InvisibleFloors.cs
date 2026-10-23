using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>
/// A floor for an interior its game left without one (user, 2026-10-21: "the interiors have navmeshes for the test
/// cells", "it needs to be all 100 percent"; Oblivion.esm's test cells and leftovers, Bloodmoon's "Solstheim" cell, hold
/// their contents over nothing): along the cell's own pathgrid where it has one (a 96-unit pad at each point, a 96-unit
/// walk along each connection, where the game's AI walked), else under what stands in it (actors, doors, furniture,
/// containers, anything 40 or more tall): by height, those within 96 of each other one level, its floor at the lowest of
/// them, under all of that level's contents and 128 round them, 512 each way at least. It faces up and is collided with
/// (an invisible model of collision only), and the navmesh is made on it.
/// </summary>
public static class InvisibleFloors
{
    /// <param name="wide">Under the pathgrid's points as places stood at (each level 512 each way at least) rather than along
    /// its walks: for a pathgrid whose pads and walks were too small for a navmesh to be kept on them (2026-10-22: Oblivion's
    /// "Dummy cell"s, four points each).</param>
    public static List<(Vector3[] Points, Vector3 Normal)> Plan(IReadOnlyList<Vector3> gridPoints, IReadOnlyList<(int A, int B)> gridLinks,
        IReadOnlyList<Vector3> stands, IReadOnlyList<Vector3> rests, out string how, bool wide = false)
    {
        if (wide && gridPoints.Count > 0)
        {
            var plan = Plan([], [], gridPoints.Concat(stands).ToList(), rests, out string under);
            how = $"under its pathgrid's {gridPoints.Count} points, wide ({under})";
            return plan;
        }
        var floor = new List<(Vector3[] Points, Vector3 Normal)>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            // Wound to face up whichever way the corners come.
            if (Vector3.Cross(b - a, c - a).Z < 0) (b, d) = (d, b);
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            floor.Add(([a, b, c], n));
            floor.Add(([a, c, d], n));
        }
        if (gridPoints.Count > 0)
        {
            foreach (var p in gridPoints)
                Quad(p + new Vector3(-48, -48, 0), p + new Vector3(48, -48, 0), p + new Vector3(48, 48, 0), p + new Vector3(-48, 48, 0));
            int walks = 0;
            foreach (var (i, j) in gridLinks)
            {
                if (i < 0 || j < 0 || i >= gridPoints.Count || j >= gridPoints.Count) continue;
                var a = gridPoints[i]; var b = gridPoints[j];
                var along = new Vector3(b.X - a.X, b.Y - a.Y, 0);
                if (along.LengthSquared() < 1) continue;
                var side = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitZ)) * 48;
                Quad(a - side, b - side, b + side, a + side);
                walks++;
            }
            how = $"along its pathgrid ({gridPoints.Count} points, {walks} walks)";
            return floor;
        }
        var standing = stands.Count > 0 ? stands : rests;
        if (standing.Count == 0) { how = "nothing to stand under"; return floor; }
        var levels = new List<List<Vector3>>();
        foreach (var p in standing.OrderBy(p => p.Z))
            if (levels.Count > 0 && p.Z - levels[^1][^1].Z <= 96) levels[^1].Add(p); else levels.Add([p]);
        var heights = new List<string>();
        foreach (var level in levels)
        {
            float z = level[0].Z;
            float top = level[^1].Z + 300;
            var all = level.Concat(rests.Where(r => r.Z >= z - 32 && r.Z <= top)).ToList();
            float x0 = all.Min(p => p.X) - 128, x1 = all.Max(p => p.X) + 128, y0 = all.Min(p => p.Y) - 128, y1 = all.Max(p => p.Y) + 128;
            // At least 512 each way: smaller, the navmesh made on it was too small to keep (TestParticles' 256, one static).
            if (x1 - x0 < 512) { float m = (x0 + x1) / 2; x0 = m - 256; x1 = m + 256; }
            if (y1 - y0 < 512) { float m = (y0 + y1) / 2; y0 = m - 256; y1 = m + 256; }
            Quad(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));
            heights.Add($"{z:F0} ({x1 - x0:F0} x {y1 - y0:F0})");
        }
        how = $"under its contents, {levels.Count} level{(levels.Count == 1 ? "" : "s")}: {string.Join(", ", heights)}";
        return floor;
    }

    /// <summary>The floor as an invisible model of collision only (meshes\tamrielmerge\<paramref name="game"/>\floors), placed in the cell.</summary>
    public static void Place(SkyrimMod mod, string modOutDir, string game, string prefix, Cell cell, List<(Vector3[] Points, Vector3 Normal)> floor)
    {
        string rel = $@"meshes\tamrielmerge\{game}\floors\{cell.EditorID}.nif";
        using (var nif = new nifly.NifFile())
        {
            nif.Create(nifly.NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var fade = Meshes.BlockMeshBuilder.Owned(new nifly.BSFadeNode());
            fade.name = new nifly.NiStringRef(Path.GetFileNameWithoutExtension(rel));
            hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
            var root = nif.GetRootNode();
            Meshes.BlockMeshBuilder.AddCollision(nif, root, floor.Select(f => (f.Points, f.Normal, new Meshes.BlockMeshBuilder.Tex("", "", false))).ToList(), false);
            Meshes.BlockMeshBuilder.AddBsxFlags(nif, root);
            string path = Path.Combine(modOutDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (nif.Save(path) != 0) throw new IOException($"nifly failed to save {path}");
        }
        var pts = floor.SelectMany(f => f.Points).ToList();
        var lo = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var hi = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        static short C(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
        // A cell's floor planned again (a wider one when the first kept no navmesh) replaces its first: one static, one
        // reference (2026-10-22: two of each, under one EditorID, in the Oblivion dummy cells floored twice).
        string id = prefix + "TMFloor" + cell.EditorID;
        var bounds = new ObjectBounds { First = new P3Int16(C(lo.X), C(lo.Y), C(lo.Z - 12)), Second = new P3Int16(C(hi.X), C(hi.Y), C(hi.Z)) };
        if (mod.Statics.FirstOrDefault(s => s.EditorID == id) is { } had)
        {
            had.ObjectBounds = bounds;
            return;
        }
        var stat = new Static(mod) { EditorID = id, ObjectBounds = bounds, Model = new Model { File = rel[7..] } };
        mod.Statics.Add(stat);
        cell.Temporary.Add(new PlacedObject(mod) { Base = stat.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) } });
    }
}
