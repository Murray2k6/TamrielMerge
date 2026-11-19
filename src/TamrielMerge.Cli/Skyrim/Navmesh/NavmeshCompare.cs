using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>
/// Dev aid (navmesh-compare): two builds of the same land's navmeshes measured against each other, cell by cell, over
/// the cells of the second: the ground each covers (sampled every 32 units, a cell at a time and let go, a point being the same ground when a
/// triangle of the other lies over it within 64 units in height), and each door's nearest navmesh. Written to judge a
/// change of Recast settings by what it does to the walkable ground rather than by its triangle count (2026-10-31:
/// the open-country setting against the fine one).
/// </summary>
public static class NavmeshCompare
{
    const int Step = 32;
    const float SameFloor = 64, DoorReach = 128, DoorHeight = 128;

    sealed class Cover
    {
        public readonly Dictionary<(int X, int Y), List<float>> Points = [];
        public int Triangles;
        public int Samples => Points.Values.Sum(l => l.Count);
    }

    static void Add(Cover c, INavigationMeshGetter navm)
    {
        if (navm.Data is not { } d) return;
        var v = d.Vertices;
        foreach (var t in d.Triangles)
        {
            c.Triangles++;
            var (a, b, e) = (v[t.Vertices.X], v[t.Vertices.Y], v[t.Vertices.Z]);
            float det = (b.Y - e.Y) * (a.X - e.X) + (e.X - b.X) * (a.Y - e.Y);
            if (MathF.Abs(det) < 1e-3f) continue;
            int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, e.X)) / Step), x1 = (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, e.X)) / Step);
            int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, e.Y)) / Step), y1 = (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, e.Y)) / Step);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x * Step, py = y * Step;
                    float l0 = ((b.Y - e.Y) * (px - e.X) + (e.X - b.X) * (py - e.Y)) / det;
                    float l1 = ((e.Y - a.Y) * (px - e.X) + (a.X - e.X) * (py - e.Y)) / det;
                    float l2 = 1 - l0 - l1;
                    if (l0 < 0 || l1 < 0 || l2 < 0) continue;
                    float z = l0 * a.Z + l1 * b.Z + l2 * e.Z;
                    if (!c.Points.TryGetValue((x, y), out var list)) c.Points[(x, y)] = list = [];
                    list.Add(z);
                }
        }
    }

    /// <summary>Samples of <paramref name="from"/> with no ground of <paramref name="other"/> at them.</summary>
    static long Lost(Cover from, Cover other)
    {
        long lost = 0;
        foreach (var (key, zs) in from.Points)
        {
            other.Points.TryGetValue(key, out var there);
            foreach (float z in zs)
                if (there is null || !there.Any(o => MathF.Abs(o - z) <= SameFloor)) lost++;
        }
        return lost;
    }

    /// <summary>Of <see cref="Lost"/>, the samples on the lowest floor there (the ground people walk; the rest have a floor
    /// of the same build under them, so they are roofs and upper floors).</summary>
    static long LostGround(Cover from, Cover other)
    {
        long lost = 0;
        foreach (var (key, zs) in from.Points)
        {
            float lowest = zs.Min();
            other.Points.TryGetValue(key, out var there);
            foreach (float z in zs)
                if (z <= lowest + SameFloor && (there is null || !there.Any(o => MathF.Abs(o - z) <= SameFloor))) lost++;
        }
        return lost;
    }

    /// <summary>Whether a navmesh sample lies within reach of a door.</summary>
    static bool Reaches(Cover c, System.Numerics.Vector3 door)
    {
        int reach = (int)(DoorReach / Step), dx = (int)MathF.Round(door.X / Step), dy = (int)MathF.Round(door.Y / Step);
        for (int y = dy - reach; y <= dy + reach; y++)
            for (int x = dx - reach; x <= dx + reach; x++)
                if (c.Points.TryGetValue((x, y), out var zs) && zs.Any(z => MathF.Abs(z - door.Z) <= DoorHeight)) return true;
        return false;
    }

    static IEnumerable<ICellGetter> CellsOf(IWorldspaceGetter w) => w.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items);

    /// <summary>The cells of a worldspace in the grid blocks (32 cells a side) and sub-blocks (8) holding
    /// <paramref name="wanted"/>: nothing else of it is opened, a land plugin holding 670,000 cells (2026-10-31: walking
    /// them all ran out of 12 GB).</summary>
    static IEnumerable<ICellGetter> CellsNear(IWorldspaceGetter w, IReadOnlySet<(int X, int Y)> wanted)
    {
        var blocks = wanted.Select(c => (c.X >> 5, c.Y >> 5)).ToHashSet();
        var subs = wanted.Select(c => (c.X >> 3, c.Y >> 3)).ToHashSet();
        foreach (var b in w.SubCells)
            if (blocks.Contains((b.BlockNumberX, b.BlockNumberY)))
                foreach (var sub in b.Items)
                    if (subs.Contains((sub.BlockNumberX, sub.BlockNumberY)))
                        foreach (var c in sub.Items) yield return c;
    }

    public static int Run(IReadOnlyList<string> first, IReadOnlyList<string> second, IReadOnlyList<string> placed, Action<string> log, int worst = 12)
    {
        var aMods = first.Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        var bMods = second.Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        try
        {
            // Each build's navmeshes by cell, the samples made a cell at a time and let go: kept for every cell they
            // would be some 65,000 a cell (2026-10-31: 12,288 cells of two builds at 16 units ran past 46 GB).
            var a = new Dictionary<(FormKey, int, int), List<INavigationMeshGetter>>();
            var b = new Dictionary<(FormKey, int, int), List<INavigationMeshGetter>>();
            foreach (var w in bMods.SelectMany(m => m.Worldspaces))
                foreach (var cell in CellsOf(w))
                    if (cell.Grid is { } g && cell.NavigationMeshes.Count > 0)
                    {
                        var key = (w.FormKey, (int)g.Point.X, (int)g.Point.Y);
                        if (!b.TryGetValue(key, out var l)) b[key] = l = [];
                        l.AddRange(cell.NavigationMeshes);
                    }
            IReadOnlySet<(int X, int Y)> Wanted(FormKey world) => b.Keys.Where(k => k.Item1 == world).Select(k => (k.Item2, k.Item3)).ToHashSet();
            foreach (var w in aMods.SelectMany(m => m.Worldspaces))
                foreach (var cell in CellsNear(w, Wanted(w.FormKey)))
                    if (cell.Grid is { } g && b.ContainsKey((w.FormKey, g.Point.X, g.Point.Y)))
                    {
                        var key = (w.FormKey, (int)g.Point.X, (int)g.Point.Y);
                        if (!a.TryGetValue(key, out var l)) a[key] = l = [];
                        l.AddRange(cell.NavigationMeshes);
                    }
            log($"cells: {b.Count:N0} in the second, {a.Count:N0} of them in the first");
            // Doors in those cells, when asked for: a few cells' references at a time.
            var doorsAt = new Dictionary<(FormKey, int, int), List<System.Numerics.Vector3>>();
            foreach (var path in placed)
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                var bases = mod.Doors.Select(d => d.FormKey).ToHashSet();
                foreach (var w in mod.Worldspaces)
                {
                    var wanted = Wanted(w.FormKey);
                    if (wanted.Count == 0) continue;
                    IEnumerable<ICellGetter> cells = CellsNear(w, wanted);
                    if (w.TopCell is { } top) cells = cells.Append(top);
                    foreach (var cell in cells)
                        foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
                        {
                            if (r.Placement is not { } pl || (r.TeleportDestination is null && !bases.Contains(r.Base.FormKey))) continue;
                            var at = new System.Numerics.Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
                            var key = (w.FormKey, (int)MathF.Floor(at.X / 4096), (int)MathF.Floor(at.Y / 4096));
                            if (!b.ContainsKey(key)) continue;
                            if (!doorsAt.TryGetValue(key, out var l)) doorsAt[key] = l = [];
                            l.Add(at);
                        }
                }
            }
            log($"doors found in those cells: {doorsAt.Values.Sum(l => l.Count):N0}");

            long aSamples = 0, bSamples = 0, aLost = 0, bNew = 0, aLostGround = 0;
            int aTris = 0, bTris = 0, compared = 0, noneInA = 0, doors = 0, aReach = 0, bReach = 0, lostDoors = 0;
            var perCell = new List<(double Share, long Lost, long Samples, (FormKey, int, int) Key)>();
            var lostAt = new List<string>();
            foreach (var (key, bMeshes) in b)
            {
                var bc = new Cover();
                foreach (var n in bMeshes) Add(bc, n);
                bTris += bc.Triangles;
                bSamples += bc.Samples;
                if (!a.TryGetValue(key, out var aMeshes)) { noneInA++; continue; }
                var ac = new Cover();
                foreach (var n in aMeshes) Add(ac, n);
                compared++;
                long s = ac.Samples, l = Lost(ac, bc);
                long lg = LostGround(ac, bc);
                aSamples += s; aTris += ac.Triangles; aLost += l; bNew += Lost(bc, ac); aLostGround += lg;
                if (s > 0) perCell.Add((lg / (double)s, lg, s, key));
                if (!doorsAt.TryGetValue(key, out var here)) continue;
                foreach (var at in here)
                {
                    doors++;
                    bool inA = Reaches(ac, at), inB = Reaches(bc, at);
                    if (inA) aReach++;
                    if (inB) bReach++;
                    if (inA && !inB) { lostDoors++; if (lostAt.Count < worst) lostAt.Add($"{key.Item2},{key.Item3} at {at.X:F0},{at.Y:F0},{at.Z:F0}"); }
                }
            }
            log($"compared {compared:N0} cells, {noneInA:N0} with nothing in the first");
            log($"triangles: first {aTris:N0}, second {bTris:N0} ({(aTris == 0 ? 0 : 100.0 * bTris / aTris):F1}%)");
            log($"ground samples (every {Step} units): first {aSamples:N0}, second {bSamples:N0}");
            log($"ground of the first lost in the second: {aLost:N0} ({(aSamples == 0 ? 0 : 100.0 * aLost / aSamples):F2}%); ground new in the second: {bNew:N0} ({(bSamples == 0 ? 0 : 100.0 * bNew / bSamples):F2}%)");
            log($"of what was lost, on the lowest floor there: {aLostGround:N0} ({(aSamples == 0 ? 0 : 100.0 * aLostGround / aSamples):F2}% of the first's ground); the rest roofs and upper floors");
            int[] over = [1, 5, 10, 25, 50];
            log("cells losing lowest-floor ground over: " + string.Join(", ", over.Select(p => $"{p}%: {perCell.Count(c => c.Share * 100 > p):N0}")));
            foreach (var c in perCell.OrderByDescending(c => c.Lost).Take(worst))
                log($"  worst cell {c.Key.Item2},{c.Key.Item3}: {c.Lost:N0} of {c.Samples:N0} lowest-floor samples lost ({c.Share * 100:F1}%)");
            log($"doors compared: {doors:N0}; navmesh within {DoorReach} units: first {aReach:N0}, second {bReach:N0}; reached in the first and not the second: {lostDoors:N0}");
            foreach (var s in lostAt) log($"  door lost: {s}");
            return 0;
        }
        finally
        {
            foreach (var m in aMods.Concat(bMods)) (m as IDisposable)?.Dispose();
        }
    }
}
