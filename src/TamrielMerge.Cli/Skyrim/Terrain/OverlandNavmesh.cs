using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Navmesh;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// Navmeshes for Daggerfall's overland (user, 2026-10-09: "the overland needs done"): every land cell of every tile of
/// "Daggerfall Land.esm" gets one, from its own heights (Recast, the open-country setting: coarse voxels and long edges,
/// as vanilla's wilds are meshed), underwater ground left out, the cells' edges joined within each tile (each tile is a
/// worldspace of its own). Built a tile at a time from the finished land plugin - its cells and heights read from the
/// plugin, nothing else rebuilt - each tile written as a part of the same plugin (the cells again, now carrying their
/// navmeshes, with FormIDs above those the land used), the navmesh map (NAVI) written once in a last part with every
/// tile's entries, and the parts merged into the land plugin (a cell in several parts is kept once, its children
/// joined). What stands on the overland (town gates, dungeon entrances) isn't in the meshes yet.
/// </summary>
public static class OverlandNavmesh
{
    public static (int Meshes, int Triangles, int Links) Build(string landPlugin, Action<string>? log = null) => Build([landPlugin], log);

    /// <summary>
    /// The navmeshes of a land split into several plugins (<see cref="OverlandTiles.LandPluginNames"/>, in load order):
    /// all of them go in the last, with the cells of the others it covers as overrides there (their land left in their
    /// own plugin), so the links between neighbouring cells' navmeshes, and the navmesh map, are all in one plugin.
    /// </summary>
    public static (int Meshes, int Triangles, int Links) Build(IReadOnlyList<string> landPlugins, Action<string>? log = null)
    {
        string landPlugin = landPlugins[^1];
        string dir = Path.GetDirectoryName(Path.GetFullPath(landPlugin))!;
        string work = Path.Combine(dir, "Daggerfall Land.navmesh");
        Directory.CreateDirectory(work);
        var parts = new List<string>();
        var infos = new List<NavigationMapInfo>();
        int meshes = 0, triangles = 0, links = 0;
        var earlier = landPlugins.SkipLast(1).Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        try
        {
        using (var land = SkyrimMod.CreateFromBinaryOverlay(landPlugin, SkyrimRelease.SkyrimSE))
        {
            // The parts list the plugin's own masters (Daggerfall.esm, and the land plugins before it).
            var masters = land.ModHeader.MasterReferences.Select(m => m.Master).ToArray();
            // Every plugin's copies of each worldspace (each holds its share of the cells).
            var copies = earlier.Append(land).SelectMany(p => p.Worldspaces).GroupBy(w => w.FormKey).ToList();
            var worlds = copies.Select(g => g.Last()).ToList();
            bool already = land.Worldspaces.SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(sb => sb.Items).Take(4000).Any(c => c.NavigationMeshes.Count > 0);
            if (already) { log?.Invoke($"{Path.GetFileName(landPlugin)} has navmeshes already"); return (0, 0, 0); }
            // New FormIDs above every one the land used, a range per tile and one for the map.
            uint used = land.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == land.ModKey).Select(r => r.FormKey.ID).DefaultIfEmpty(0x800u).Max();
            uint per = (0xFFFFFFu - used - 1) / (uint)(worlds.Count + 1);
            for (int t = 0; t < worlds.Count; t++)
            {
                var source = worlds[t];
                var mod = NewPart(land.ModKey, used + 1 + (uint)t * per);
                var ws = source.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
                ws.SubCells.Clear();
                ws.TopCell = null;
                mod.Worldspaces.Add(ws);
                // The land cells, read from the plugin as they are needed: a chunk of them (OverlandTiles.ChunkCells
                // square) at a time with a ring of neighbours, each chunk's land let go once its navmeshes are made, so
                // a worldspace of the whole map (1000 x 500 cells) never holds all its land at once.
                var sources = new Dictionary<(int X, int Y), ICellGetter>();
                foreach (var cell in copies[t].SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                    if (cell.Grid is not null && cell.Landscape is not null) sources[(cell.Grid.Point.X, cell.Grid.Point.Y)] = cell;
                const int k = OverlandTiles.ChunkCells;
                var chunks = sources.Keys.GroupBy(c => (FloorDiv(c.X, k), FloorDiv(c.Y, k))).OrderBy(g => g.Key.Item2).ThenBy(g => g.Key.Item1).ToList();
                var builder = new NavmeshBuilder(mod);
                var problems = new List<string>();
                var withNavmesh = new List<Cell>();
                int m = 0, n = 0, done = 0;
                foreach (var chunk in chunks)
                {
                    var (kx, ky) = chunk.Key;
                    var cells = new Dictionary<(int X, int Y), Cell>();
                    for (int y = ky * k - 1; y <= ky * k + k; y++)
                        for (int x = kx * k - 1; x <= kx * k + k; x++)
                            if (sources.TryGetValue((x, y), out var cell))
                            {
                                var copy = cell.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Temporary = false, Persistent = false, NavigationMeshes = false });
                                copy.Temporary.Clear();
                                copy.Persistent.Clear();
                                cells[(x, y)] = copy;
                            }
                    var (cm, cn) = builder.BuildExteriorCells(ws, cells, (_, _) => [],
                        c => c.Flags.HasFlag(Cell.Flag.HasWater) ? c.WaterHeight ?? 0f : null, [], problems, open: true,
                        only: (x, y) => FloorDiv(x, k) == kx && FloorDiv(y, k) == ky);
                    m += cm; n += cn;
                    // Only the chunk's cells with a navmesh, without their land (the land part has it).
                    foreach (var ((x, y), cell) in cells)
                    {
                        cell.Landscape = null;
                        if (FloorDiv(x, k) == kx && FloorDiv(y, k) == ky && cell.NavigationMeshes.Count > 0) withNavmesh.Add(cell);
                    }
                    done++;
                    if (chunks.Count > 1) Progress.Report((t + done / (double)chunks.Count) / (worlds.Count + 1), $"Overland navmesh: {done} of {chunks.Count} pieces");
                }
                int l = builder.LinkExteriorEdges();
                infos.AddRange(builder.TakeInfos());
                foreach (var cell in withNavmesh) TerrainBuilder.AddCell(ws, cell);
                if (m > 0)
                {
                    string part = Path.Combine(work, $"tile{t + 1}.esm");
                    Write(mod, part, masters);
                    parts.Add(part);
                }
                meshes += m; triangles += n; links += l;
                log?.Invoke($"{source.EditorID}: {m:N0} navmeshes ({n:N0} triangles, {l:N0} links between cells) over {sources.Count:N0} land cells{(problems.Count > 0 ? $"; problems: {string.Join("; ", problems.Take(3))}" : "")}");
                Progress.Report((t + 1) / (double)(worlds.Count + 1), $"Overland navmesh: {t + 1} of {worlds.Count} tiles");
            }
            if (parts.Count == 0) { Directory.Delete(work, true); return (0, 0, 0); }
            var map = NewPart(land.ModKey, used + 1 + (uint)worlds.Count * per);
            map.NavigationMeshInfoMaps.Add(NavmeshBuilder.InfoMapWith(infos));
            string mapPart = Path.Combine(work, "map.esm");
            Write(map, mapPart, masters);
            parts.Add(mapPart);
        }
        }
        finally { foreach (var e in earlier) e.Dispose(); }
        // The land plugin with the navmeshes joined in.
        string merged = Path.Combine(work, "merged.esm");
        long records = PluginMerger.Merge([landPlugin, .. parts], merged);
        File.Move(merged, landPlugin, overwrite: true);
        Directory.Delete(work, true);
        log?.Invoke($"{Path.GetFileName(landPlugin)}: {records:N0} records and groups with {meshes:N0} navmeshes");
        return (meshes, triangles, links);
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    static SkyrimMod NewPart(Mutagen.Bethesda.Plugins.ModKey key, uint firstId)
    {
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        mod.ModHeader.Stats.NextFormID = firstId;
        return mod;
    }

    static void Write(SkyrimMod mod, string path, Mutagen.Bethesda.Plugins.ModKey[] masters)
    {
        mod.ModHeader.MasterReferences.Clear();
        foreach (var m in masters) mod.ModHeader.MasterReferences.Add(new MasterReference { Master = m });
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            MastersListContent = MastersListContentOption.NoCheck,
            ModKey = ModKeyOption.NoCheck,
            MastersListOrdering = new MastersListOrderingByLoadOrder(masters),
        });
    }
}
