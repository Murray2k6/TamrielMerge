using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Navmesh;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// Navmeshes for Daggerfall's overland (user, 2026-10-09: "the overland needs done"): every land cell of every tile of
/// the land plugins gets one, from its own heights (Recast, the open-country setting: coarse voxels and long edges,
/// as vanilla's wilds are meshed), under water too and flagged Water there as vanilla's is, the cells' edges joined within each tile (each tile is a
/// worldspace of its own). Built a tile at a time from the finished land plugins - their cells and heights read from
/// them, nothing else rebuilt - each tile into plugins of its own (<see cref="OverlandTiles.NavmeshPluginName"/>: the
/// land's cells again, carrying their navmeshes, and the navmesh map's entries for them, NAVI), written a piece at a
/// time as each piece's neighbours are made (only about a row of pieces held at once) and the pieces merged (a cell
/// in several is kept once, its children joined). A tile's navmeshes go into several plugins by size
/// (<see cref="PluginBudget"/>, user 2026-10-28: "make a plugin"), each later one with the earlier ones as masters; a
/// navmesh linking across into a later plugin is carried whole there as an override, its own plugin's copy without
/// those links (Mutagen reads a plugin only up to 2 GB; the game loads any size). What the town and dungeon plugins stand on the overland (the placed plugins given to
/// <see cref="Build(IReadOnlyList{string}, IReadOnlyList{string}?, string?, Action{string}?)"/>: Daggerfall's towns and
/// every location's outside, at full size on the land, docs/overland-locations.md) is in the meshes, from its models'
/// own collision (Meshes/CollisionReader), in the open-country setting as the land round them (Daggerfall's streets and
/// alleys are 256 units wide and more; measured 2026-10-26, the fine setting, on those cells and the bare land beside
/// them, put 80% of a tile's 180 million triangles in 198,000 navmeshes of 400 to 1,600 each, 5.7 GB). The foliage isn't: its 7.8 million trees, rocks and plants (<see cref="DfFoliage"/>) would split the land's grid round
/// each one, about 190 million more triangles (2026-10-25: a whole Recast build of every cell instead ran 17 minutes a
/// 128-cell piece of 31).
/// </summary>
public static class OverlandNavmesh
{
    public static (int Meshes, int Triangles, int Links, List<string> Plugins) Build(string landPlugin, Action<string>? log = null) => Build([landPlugin], null, null, log);

    /// <summary>Cells a side of the pieces a tile's navmeshes are made in.</summary>
    const int NavmeshChunk = 64;

    /// <summary>Collision layers left out: non-collidable (15) and triggers (12).</summary>
    static bool Solid(uint layer) => layer is not (15 or 12);

    /// <summary>Most collision triangles kept read at once (the models' cache is let go past it).</summary>
    const long CacheTriangles = 20_000_000;

    /// <summary>
    /// The navmeshes of a land in one or several plugins (<see cref="OverlandTiles.LandPluginNames"/>, in load order),
    /// a navmesh plugin a tile (<see cref="OverlandTiles.NavmeshPluginNames"/>, beside them), its cells overrides of the
    /// land plugins' (their land left there). <paramref name="placedPlugins"/>: plugins whose references on the overland
    /// tiles stand in the meshes (by their models' collision, found in the plugins' folder, then
    /// <paramref name="skyrimData"/> and its archives). Returns the navmesh plugins' names too; a tile whose plugin is
    /// in the folder already is kept and the others made (a stopped run's finished tiles).
    /// </summary>
    /// <param name="onlyChunks">Pieces (chunk coordinates, cells / <see cref="NavmeshChunk"/>) to make alone, for measuring; null: all.</param>
    /// <param name="navmeshDir">Where the navmesh plugins go; null: beside the land plugins.</param>
    public static (int Meshes, int Triangles, int Links, List<string> Plugins) Build(IReadOnlyList<string> landPlugins, IReadOnlyList<string>? placedPlugins, string? skyrimData, Action<string>? log = null,
        IReadOnlySet<(int X, int Y)>? onlyChunks = null, string? navmeshDir = null)
    {
        placedPlugins ??= [];
        if (placedPlugins.Count > 0 && skyrimData is null) throw new ArgumentException("the placed plugins' models are looked for in Skyrim's Data", nameof(skyrimData));
        string landPlugin = landPlugins[^1];
        string dir = Path.GetDirectoryName(Path.GetFullPath(landPlugin))!;
        // A tile whose plugin is there already is kept (a stopped run's finished tiles: --resume); the rest are made.
        string outDir = navmeshDir ?? dir;
        Directory.CreateDirectory(outDir);
        // The parts' folder, beside the plugins: a run's parts (2026-10-28: a test run beside the land folder deleted a
        // conversion's parts four hours into its second tile). One written to in the last ten minutes is another run's.
        string work = Path.Combine(outDir, "Daggerfall Land.navmesh");
        if (Directory.Exists(work))
        {
            var newest = Directory.EnumerateFiles(work).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            if (DateTime.UtcNow - newest < TimeSpan.FromMinutes(10)) throw new InvalidOperationException($"{work} is being written by another run (a part {(DateTime.UtcNow - newest).TotalMinutes:F0} min old): not started");
            Directory.Delete(work, true);
        }
        Directory.CreateDirectory(work);
        if (OverlandTiles.NavmeshPluginsIn(outDir) is { Count: > 0 } there) log?.Invoke($"{string.Join(", ", there)} there already: kept, the other tiles made");
        var navPlugins = new List<string>();
        int meshes = 0, triangles = 0, links = 0;
        var earlier = landPlugins.SkipLast(1).Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        var placedMods = placedPlugins.Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        var placed = placedMods.Count > 0 ? new Standing(placedMods, skyrimData!, dir) : null;
        try
        {
        using (var land = SkyrimMod.CreateFromBinaryOverlay(landPlugin, SkyrimRelease.SkyrimSE))
        {
            // The navmesh plugins' masters: the last land plugin's (Daggerfall.esm and the land plugins before it) and it.
            var masters = land.ModHeader.MasterReferences.Select(m => m.Master).Append(land.ModKey).ToArray();
            // Every plugin's copies of each worldspace (each holds its share of the cells).
            var copies = earlier.Append(land).SelectMany(p => p.Worldspaces).GroupBy(w => w.FormKey).ToList();
            var worlds = copies.Select(g => g.Last()).ToList();
            if (worlds.Count > OverlandTiles.NavmeshTiles)
                throw new InvalidOperationException($"{worlds.Count} tiles, navmesh plugins for {OverlandTiles.NavmeshTiles}");
            for (int t = 0; t < worlds.Count; t++)
            {
                var source = worlds[t];
                if (File.Exists(Path.Combine(outDir, OverlandTiles.NavmeshPluginName(t + 1, 1))))
                {
                    // The tile's plugins, there already (a stopped run's finished tile).
                    for (int part = 1; part <= OverlandTiles.NavmeshPartsPerTile; part++)
                        if (File.Exists(Path.Combine(outDir, OverlandTiles.NavmeshPluginName(t + 1, part)))) navPlugins.Add(OverlandTiles.NavmeshPluginName(t + 1, part));
                    continue;
                }
                // The tile's plugins: one to begin with, another whenever the one being written passes PluginBudget (raw
                // parts; the merge compresses them about 2.2x). Each later plugin has the earlier ones as masters, so its
                // navmeshes may link to theirs; a navmesh of an earlier plugin that links to a later one's (the pieces'
                // borders) can't name it, so the earlier plugin's copy loses those links and the later plugin carries the
                // whole navmesh as an override (the cell's children merge across plugins; a later NAVM replaces the earlier).
                var tilePlugins = new List<TilePlugin>();
                TilePlugin NewPlugin()
                {
                    string name = OverlandTiles.NavmeshPluginName(t + 1, tilePlugins.Count + 1);
                    if (tilePlugins.Count >= OverlandTiles.NavmeshPartsPerTile) throw new InvalidOperationException($"tile {t + 1}'s navmesh wants more than {OverlandTiles.NavmeshPartsPerTile} plugins");
                    var key = ModKey.FromFileName(name);
                    var pm = NewPart(key, 0x800);
                    var pws = source.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
                    pws.SubCells.Clear();
                    pws.TopCell = null;
                    pm.Worldspaces.Add(pws);
                    var tp = new TilePlugin { Name = name, Key = key, Mod = pm, Ws = pws, Index = tilePlugins.Count };
                    tilePlugins.Add(tp);
                    return tp;
                }
                ModKey[] MastersOf(TilePlugin tp) => [.. masters, .. tilePlugins.Take(tp.Index).Select(x => x.Key)];
                int IndexOf(ModKey key) => tilePlugins.FindIndex(x => x.Key == key);
                var current = NewPlugin();
                // The land cells, read from the plugin as they are needed: a piece of them (NavmeshChunk square) at a
                // time with a ring of neighbours, each chunk's land let go once its navmeshes are made, so
                // a worldspace of the whole map (1000 x 500 cells) never holds all its land at once.
                var sources = new Dictionary<(int X, int Y), ICellGetter>();
                foreach (var cell in copies[t].SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                    if (cell.Grid is not null && cell.Landscape is not null) sources[(cell.Grid.Point.X, cell.Grid.Point.Y)] = cell;
                // Pieces of NavmeshChunk cells (half the land's): what is held to be linked is about a row of them across
                // the tile, so the smaller the piece the less (2026-10-26: rows of 128 cells held 130,000 navmeshes, the
                // towns' among them made in the fine setting).
                const int k = NavmeshChunk;
                var chunks = sources.Keys.GroupBy(c => (FloorDiv(c.X, k), FloorDiv(c.Y, k))).Where(g => onlyChunks is null || onlyChunks.Contains(g.Key)).OrderBy(g => g.Key.Item2).ThenBy(g => g.Key.Item1).ToList();
                var builder = new NavmeshBuilder(current.Mod);
                var problems = new List<string>();
                int m = 0, n = 0, done = 0, l = 0, written = 0, crossLinks = 0, overrides = 0;
                // Each chunk's cells with their navmeshes, until written. A chunk is written (a part of its own) once the
                // chunks east and north of it are made too, so its cells' links to every neighbour are in (the cells
                // west and south of it were made before it): only about a row of chunks is held at once (2026-10-26:
                // holding a tile's 800,000 navmeshes to its end went past 18 GB a third of the way through).
                var keys = chunks.Select(c => c.Key).ToHashSet();
                var made = new HashSet<(int, int)>();
                var pending = new List<((int X, int Y) Key, List<Cell> Cells, TilePlugin Plugin)>();
                void TakeInfos()
                {
                    foreach (var info in builder.TakeInfos())
                    {
                        int idx = IndexOf(info.NavigationMesh.FormKey.ModKey);
                        (idx >= 0 ? tilePlugins[idx] : current).Infos.Add(info);
                    }
                }
                void WriteFinished(bool all)
                {
                    bool Done((int X, int Y) c) => !keys.Contains(c) || made.Contains(c);
                    var ready = pending.Where(p => all || (Done((p.Key.X + 1, p.Key.Y)) && Done((p.Key.X, p.Key.Y + 1)))).ToList();
                    if (ready.Count == 0 && !(all && tilePlugins.Any(tp => tp.Overrides.Count > 0))) return;
                    // Links into a later plugin: the whole navmesh to that plugin as an override, the links out of this one's copy.
                    var touched = new HashSet<TilePlugin>();
                    foreach (var (_, chunkCells, plugin) in ready)
                    {
                        touched.Add(plugin);
                        foreach (var cell in chunkCells)
                            foreach (var navm in cell.NavigationMeshes)
                            {
                                if (navm.Data is not { } data || data.EdgeLinks.Count == 0) continue;
                                int latest = -1;
                                foreach (var link in data.EdgeLinks)
                                {
                                    int idx = IndexOf(link.Mesh.FormKey.ModKey);
                                    if (idx > plugin.Index && idx > latest) latest = idx;
                                }
                                if (latest < 0) continue;
                                var copyCell = cell.DeepCopy(new Cell.TranslationMask(defaultOn: true) { NavigationMeshes = false, Temporary = false, Persistent = false, Landscape = false });
                                copyCell.NavigationMeshes.Clear();
                                copyCell.Temporary.Clear();
                                copyCell.Persistent.Clear();
                                copyCell.Landscape = null;
                                copyCell.NavigationMeshes.Add(navm.DeepCopy());
                                tilePlugins[latest].Overrides.Add(copyCell);
                                overrides++;
                                crossLinks += StripLinks(data, key => IndexOf(key.ModKey) > plugin.Index);
                            }
                    }
                    foreach (var (_, chunkCells, plugin) in ready)
                        foreach (var cell in chunkCells) TerrainBuilder.AddCell(plugin.Ws, cell);
                    // The navmesh map's entries so far out of the parts (each plugin's map is written once, last, with them all).
                    TakeInfos();
                    foreach (var tp in tilePlugins)
                    {
                        if (!touched.Contains(tp) && !(all && tp.Overrides.Count > 0)) continue;
                        foreach (var c in tp.Overrides) TerrainBuilder.AddCell(tp.Ws, c);
                        tp.Overrides.Clear();
                        string part = Path.Combine(work, $"tile{t + 1}-{tp.Index + 1}-{++written}.esm");
                        Write(tp.Mod, part, MastersOf(tp));
                        tp.Parts.Add(part);
                        tp.Raw += new FileInfo(part).Length;
                        tp.Ws.SubCells.Clear();
                    }
                    builder.ForgetExteriors(ready.SelectMany(p => p.Cells).SelectMany(c => c.NavigationMeshes).ToHashSet());
                    pending.RemoveAll(p => ready.Contains(p));
                    // What they held given back to the system: the large-object heap compacted and the free memory
                    // returned (measured 2026-10-26: 6 GB more private memory than live after a full collection).
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    // The plugin being written past its budget: the next pieces go into a new one.
                    if (!all && current.Raw >= PluginBudget)
                    {
                        log?.Invoke($"  {current.Name}: {current.Raw / 1048576:N0} MB of raw parts, past the budget of {PluginBudget / 1048576:N0}: the next pieces go to {OverlandTiles.NavmeshPluginName(t + 1, tilePlugins.Count + 1)}");
                        current = NewPlugin();
                        builder.Target = current.Mod;
                    }
                }
                // What stands on this tile, by the cell its references are in.
                var refsAt = placed?.Index(source.FormKey) ?? [];
                // The cells at doors keep the fine setting: the open one's 32-unit voxels eat the narrow floor before a
                // door (measured 2026-10-31 on 12,288 cells against the fine setting: 7 of 54 doors left with no navmesh
                // within 128 units, for 0.50% of the street-level ground everywhere else).
                var doorCells = placed?.DoorCells(source.FormKey) ?? [];
                long standingTriangles = 0;
                foreach (var chunk in chunks)
                {
                    var (kx, ky) = chunk.Key;
                    // Its solids (the chunk and the ring of cells round it the builder looks into), by the cell each
                    // triangle's middle is in.
                    var solids = placed?.Solids(refsAt, kx * k - 1, ky * k - 1, kx * k + k, ky * k + k) ?? [];
                    foreach (var list in solids.Values) standingTriangles += list.Count;
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
                    var pieceOwner = current;
                    var (cm, cn) = builder.BuildExteriorCells(pieceOwner.Ws, cells, (x, y) => solids.TryGetValue((x, y), out var s) ? s : [],
                        c => c.Flags.HasFlag(Cell.Flag.HasWater) ? c.WaterHeight ?? 0f : null, [], problems, open: true,
                        only: (x, y) => FloorDiv(x, k) == kx && FloorDiv(y, k) == ky, fineAt: (x, y) => doorCells.Contains((x, y)));
                    m += cm; n += cn;
                    // Only the chunk's cells with a navmesh, without their land (the land part has it).
                    var withNavmesh = new List<Cell>();
                    foreach (var ((x, y), cell) in cells)
                    {
                        cell.Landscape = null;
                        if (FloorDiv(x, k) == kx && FloorDiv(y, k) == ky && cell.NavigationMeshes.Count > 0) withNavmesh.Add(cell);
                    }
                    pending.Add(((kx, ky), withNavmesh, pieceOwner));
                    made.Add((kx, ky));
                    // Joined to the cells made before it, and what is now whole written out.
                    l += builder.LinkExteriorEdges();
                    WriteFinished(all: false);
                    done++;
                    // What is held after each piece (2026-10-26: a tile's memory rose a third of a gigabyte a piece
                    // with its finished pieces written out): the live heap after a full collection, the process's own,
                    // and what the builder, the pieces waiting and the collision keep.
                    {
                        long live = GC.GetTotalMemory(false);
                        var (held, cached, cachedTris) = builder.ExteriorHeld();
                        using var self = System.Diagnostics.Process.GetCurrentProcess();
                        log?.Invoke($"  {source.EditorID} piece {done} of {chunks.Count} ({kx},{ky}): {m:N0} navmeshes so far, {n:N0} triangles ({(m == 0 ? 0 : n / (double)m):F0} a navmesh); {live / 1073741824.0:F1} GB heap, {self.PrivateMemorySize64 / 1073741824.0:F1} GB private; "
                            + $"{held:N0} navmeshes held for links, {pending.Sum(p => p.Cells.Count):N0} cells waiting in {pending.Count} pieces, {cached:N0} meshes kept for reuse ({cachedTris:N0} triangles), "
                            + $"{(placed?.CachedTriangles ?? 0):N0} collision triangles cached; {current.Name} {current.Raw / 1048576:N0} MB raw");
                    }
                    if (chunks.Count > 1) Progress.Report((t + done / (double)chunks.Count) / (worlds.Count + 1), $"Overland navmesh: {done} of {chunks.Count} pieces");
                }
                WriteFinished(all: true);
                TakeInfos();
                meshes += m; triangles += n; links += l;
                log?.Invoke($"{source.EditorID}: {m:N0} navmeshes ({n:N0} triangles, {l:N0} links between cells) over {sources.Count:N0} land cells"
                    + (placed is null ? "" : $"; {refsAt.Values.Sum(r => r.Count):N0} references standing on it in {refsAt.Count:N0} cells, {standingTriangles:N0} collision triangles, {builder.ExteriorReused:N0} cells' meshes reused")
                    + $"; {tilePlugins.Count(tp => tp.Parts.Count > 0)} plugins, {overrides:N0} navmeshes overridden in a later plugin for {crossLinks:N0} links across"
                    + (problems.Count > 0 ? $"; {problems.Count} problems: {string.Join("; ", problems.Take(3))}" : ""));
                // Each plugin's navmesh map entries (an override of Skyrim's map with only these, as each DLC's is: the
                // game joins every plugin's), and the plugin from its pieces.
                foreach (var tp in tilePlugins.Where(tp => tp.Parts.Count > 0))
                {
                    var map = NewPart(tp.Key, 0x800);
                    map.NavigationMeshInfoMaps.Add(NavmeshBuilder.InfoMapWith(tp.Infos));
                    string mapPart = Path.Combine(work, $"tile{t + 1}-{tp.Index + 1}-map.esm");
                    Write(map, mapPart, MastersOf(tp));
                    tp.Parts.Add(mapPart);
                    string navPath = Path.Combine(outDir, tp.Name);
                    long records = PluginMerger.Merge(tp.Parts, navPath);
                    foreach (var part in tp.Parts) File.Delete(part);
                    navPlugins.Add(tp.Name);
                    long bytes = new FileInfo(navPath).Length;
                    log?.Invoke($"{tp.Name}: {records:N0} records and groups, {bytes / 1048576:N0} MB ({tp.Raw / 1048576:N0} MB of raw parts, {tp.Infos.Count:N0} map entries)");
                    // Past what Mutagen reads the doors step and the install would fail hours later (2026-10-27: a 2,669 MB
                    // tile): the run stops here instead.
                    if (bytes >= OverlandTiles.SafeSize)
                        throw new InvalidOperationException($"{tp.Name} is {bytes / 1048576:N0} MB, past the {OverlandTiles.SafeSize / 1048576:N0} MB Mutagen reads: PluginBudget (OverlandNavmesh) must be lower");
                }
                if (NavmeshBuilder.TriesThrownSummary is { Length: > 0 } threw) log?.Invoke(threw);
                Progress.Report((t + 1) / (double)worlds.Count, $"Overland navmesh: {t + 1} of {worlds.Count} tiles");
            }
        }
        }
        finally
        {
            foreach (var e in earlier) e.Dispose();
            foreach (var p in placedMods) p.Dispose();
        }
        if (placed is not null) log?.Invoke(placed.Summary());
        Directory.Delete(work, true);
        return (meshes, triangles, links, navPlugins);
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>Raw bytes of parts a tile's navmesh plugin takes before the next pieces go to another plugin: the merge
    /// compresses the parts 2.2x (2026-10-28: the first tile's 3,260 MB of raw parts, 88 million triangles, merged to
    /// 1,482 MB), so 3,900 MB raw is about 1,770 MB written, under the 1,900 MB Mutagen reads (the merge stops the run
    /// past it), and a tile the first's size stays one plugin (user: as few plugins as the engine's slots allow).
    /// TM_NAV_PLUGINBUDGET (MB) overrides it, for testing the split on a few pieces.</summary>
    static readonly long PluginBudget = long.TryParse(Environment.GetEnvironmentVariable("TM_NAV_PLUGINBUDGET"), out var mb) ? mb << 20 : 3_900L << 20;

    /// <summary>One of a tile's navmesh plugins while it is written: its parts so far and their raw size, its map entries,
    /// and navmeshes of earlier plugins that link into it, waiting to go into its next part as overrides.</summary>
    sealed class TilePlugin
    {
        public required string Name { get; init; }
        public required ModKey Key { get; init; }
        public required SkyrimMod Mod { get; init; }
        public required Worldspace Ws { get; init; }
        public required int Index { get; init; }
        public List<string> Parts { get; } = [];
        public long Raw { get; set; }
        public List<NavigationMapInfo> Infos { get; } = [];
        public List<Cell> Overrides { get; } = [];
    }

    /// <summary>Takes the edge links to navmeshes <paramref name="foreign"/> out of <paramref name="data"/>: the triangles'
    /// link indices renumbered, an edge whose link went left unlinked (a border edge again). Returns how many went.</summary>
    static int StripLinks(NavigationMeshData data, Func<FormKey, bool> foreign)
    {
        var keep = new List<EdgeLink>();
        var map = new short[data.EdgeLinks.Count];
        int gone = 0;
        for (int i = 0; i < data.EdgeLinks.Count; i++)
        {
            var link = data.EdgeLinks[i];
            if (foreign(link.Mesh.FormKey)) { map[i] = -1; gone++; }
            else { map[i] = (short)keep.Count; keep.Add(link); }
        }
        if (gone == 0) return 0;
        foreach (var tr in data.Triangles)
        {
            if (tr.Flags.HasFlag(NavmeshTriangle.Flag.EdgeLink_0_1) && tr.EdgeLink_0_1 >= 0 && tr.EdgeLink_0_1 < map.Length)
            {
                tr.EdgeLink_0_1 = map[tr.EdgeLink_0_1];
                if (tr.EdgeLink_0_1 < 0) tr.Flags &= ~NavmeshTriangle.Flag.EdgeLink_0_1;
            }
            if (tr.Flags.HasFlag(NavmeshTriangle.Flag.EdgeLink_1_2) && tr.EdgeLink_1_2 >= 0 && tr.EdgeLink_1_2 < map.Length)
            {
                tr.EdgeLink_1_2 = map[tr.EdgeLink_1_2];
                if (tr.EdgeLink_1_2 < 0) tr.Flags &= ~NavmeshTriangle.Flag.EdgeLink_1_2;
            }
            if (tr.Flags.HasFlag(NavmeshTriangle.Flag.EdgeLink_2_0) && tr.EdgeLink_2_0 >= 0 && tr.EdgeLink_2_0 < map.Length)
            {
                tr.EdgeLink_2_0 = map[tr.EdgeLink_2_0];
                if (tr.EdgeLink_2_0 < 0) tr.Flags &= ~NavmeshTriangle.Flag.EdgeLink_2_0;
            }
        }
        data.EdgeLinks.Clear();
        data.EdgeLinks.AddRange(keep);
        return gone;
    }

    /// <summary>A reference standing on the overland: its base's model (an index into <see cref="Standing"/>'s), where
    /// it stands, its turn and its size.</summary>
    readonly record struct Ref(int Model, System.Numerics.Vector3 Position, Noggog.P3Float Rotation, float Scale);

    /// <summary>
    /// What the placed plugins stand on the overland tiles, as collision: their references by tile and cell, and their
    /// models' collision triangles (CollisionReader, the solid layers), read once each and kept while there is room.
    /// </summary>
    sealed class Standing(IReadOnlyList<ISkyrimModGetter> plugins, string skyrimData, string dir)
    {
        readonly DungeonCheck.FileFinder files = new(skyrimData, dir);
        readonly List<string> modelPaths = [];
        readonly Dictionary<string, int> modelIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<FormKey, int>? modelOf;
        readonly Dictionary<int, float[]> collision = [];
        long cached;
        public long CachedTriangles => cached;
        int read, missing, empty, refsWithout;
        readonly Dictionary<string, int> unread = [];

        /// <summary>The cells within <see cref="DoorRoom"/> units of a door standing on a tile.</summary>
        public HashSet<(int X, int Y)> DoorCells(FormKey world)
        {
            var doors = plugins.SelectMany(p => p.Doors).Select(d => d.FormKey).ToHashSet();
            var near = new HashSet<(int, int)>();
            foreach (var w in plugins.SelectMany(p => p.Worldspaces).Where(w => w.FormKey == world))
            {
                var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items);
                if (w.TopCell is { } top) cells = cells.Append(top);
                foreach (var cell in cells)
                    foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
                    {
                        if (r.Placement is not { } pl || (r.TeleportDestination is null && !doors.Contains(r.Base.FormKey))) continue;
                        int x0 = (int)MathF.Floor((pl.Position.X - DoorRoom) / OverlandTiles.CellUnits), x1 = (int)MathF.Floor((pl.Position.X + DoorRoom) / OverlandTiles.CellUnits);
                        int y0 = (int)MathF.Floor((pl.Position.Y - DoorRoom) / OverlandTiles.CellUnits), y1 = (int)MathF.Floor((pl.Position.Y + DoorRoom) / OverlandTiles.CellUnits);
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++) near.Add((x, y));
                    }
            }
            return near;
        }

        const float DoorRoom = 256;

        /// <summary>Each base record's model (Skyrim's, its DLC's and the plugins' own: whatever a reference stands).</summary>
        Dictionary<FormKey, int> Models()
        {
            if (modelOf is not null) return modelOf;
            modelOf = [];
            var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
                .Select(m => Path.Combine(skyrimData, m)).Where(File.Exists)
                .Select(p => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
            try
            {
                foreach (var mod in masters.Concat(plugins))
                    foreach (var rec in mod.EnumerateMajorRecords())
                        if (rec is IModeledGetter { Model: { } mo } && rec is not INpcGetter && rec is not IPlacedGetter && mo.File.DataRelativePath.Path is { Length: > 0 } path)
                        {
                            if (!modelIndex.TryGetValue(path, out int i)) { modelIndex[path] = i = modelPaths.Count; modelPaths.Add(path); }
                            modelOf[rec.FormKey] = i;
                        }
            }
            finally { foreach (var m in masters) ((IDisposable)m).Dispose(); }
            return modelOf;
        }

        /// <summary>A tile's references (by its worldspace's key) in every placed plugin, by the grid of the cell they are
        /// in (the persistent cell's by their position). Deleted, initially disabled or enable-parented ones are left out
        /// (the overland's own navmesh can't depend on a script's choice).</summary>
        public Dictionary<(int X, int Y), List<Ref>> Index(FormKey world)
        {
            var models = Models();
            var at = new Dictionary<(int, int), List<Ref>>();
            foreach (var w in plugins.SelectMany(p => p.Worldspaces).Where(w => w.FormKey == world))
            {
                var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                if (w.TopCell is { } top) cells.Add(top);
                foreach (var cell in cells)
                    foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
                    {
                        if ((r.MajorRecordFlagsRaw & 0x820) != 0 || r.EnableParent is not null || r.Placement is not { } pl) continue;
                        if (!models.TryGetValue(r.Base.FormKey, out int model)) { refsWithout++; continue; }
                        var p = new System.Numerics.Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
                        var key = ((int)MathF.Floor(p.X / OverlandTiles.CellUnits), (int)MathF.Floor(p.Y / OverlandTiles.CellUnits));
                        if (!at.TryGetValue(key, out var list)) at[key] = list = [];
                        list.Add(new Ref(model, p, pl.Rotation, r.Scale ?? 1));
                    }
            }
            return at;
        }

        /// <summary>A model's solid collision read (none of what it touches is shared but the file finder, taken in turn):
        /// nine floats a triangle in its own space, empty when it has none.</summary>
        (float[] Triangles, bool Found, Meshes.CollisionReader.Result? Result) ReadModel(int model, object finding)
        {
            byte[]? bytes;
            lock (finding) bytes = files.Find(modelPaths[model]);
            if (bytes is null) return ([], false, null);
            Meshes.CollisionReader.Result result;
            try { result = Meshes.CollisionReader.Read(bytes); }
            catch (ArgumentOutOfRangeException) { result = new Meshes.CollisionReader.Result(); }
            var tris = result.Triangles.Where(t => Solid(t.Layer)).ToList();
            var c = new float[tris.Count * 9];
            for (int i = 0; i < tris.Count; i++)
                for (int v = 0; v < 3; v++) { c[i * 9 + v * 3] = tris[i].Tri[v].X; c[i * 9 + v * 3 + 1] = tris[i].Tri[v].Y; c[i * 9 + v * 3 + 2] = tris[i].Tri[v].Z; }
            return (c, true, result);
        }

        /// <summary>These models' collision in the cache, those not there yet read on all cores (measured 2026-10-31: read
        /// one after another they took 358 of 470 s of three town pieces, the Recast work 104).</summary>
        void Fetch(IEnumerable<int> models)
        {
            if (cached > CacheTriangles) { collision.Clear(); cached = 0; }
            var wanted = models.Distinct().Where(m => !collision.ContainsKey(m)).ToList();
            var results = new (float[] Triangles, bool Found, Meshes.CollisionReader.Result? Result)[wanted.Count];
            var finding = new object();
            Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i => results[i] = ReadModel(wanted[i], finding));
            for (int i = 0; i < wanted.Count; i++)
            {
                var (c, found, result) = results[i];
                if (!found) missing++;
                else
                {
                    foreach (var (type, count) in result!.Unread) unread[type] = unread.GetValueOrDefault(type) + count;
                    read++;
                    if (c.Length == 0) empty++;
                }
                cached += c.Length / 9;
                collision[wanted[i]] = c;
            }
        }

        /// <summary>The world-space triangles of what stands round cells <paramref name="x0"/>..<paramref name="x1"/>,
        /// <paramref name="y0"/>..<paramref name="y1"/> (inclusive), each by the cell its middle is in, for those cells.
        /// References are taken from <see cref="Reach"/> cells further out: a block's static stands at its corner and
        /// reaches two cells on.</summary>
        public Dictionary<(int X, int Y), List<System.Numerics.Vector3[]>> Solids(Dictionary<(int X, int Y), List<Ref>> refsAt, int x0, int y0, int x1, int y1)
        {
            // The models read, then every reference's triangles turned into place on all cores, each into its own list and
            // those joined in the references' order, so a cell's input is the same as one thread's and its mesh with it.
            var near = new List<Ref>();
            for (int cy = y0 - Reach; cy <= y1 + Reach; cy++)
                for (int cx = x0 - Reach; cx <= x1 + Reach; cx++)
                    if (refsAt.TryGetValue((cx, cy), out var refs)) near.AddRange(refs);
            Fetch(near.Select(r => r.Model));
            var all = new List<(Ref R, float[] C)>();
            foreach (var r in near)
                if (collision[r.Model] is { Length: > 0 } c) all.Add((r, c));
            var placed = new List<((int X, int Y) Cell, System.Numerics.Vector3[] Tri)>[all.Count];
            Parallel.For(0, all.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, n =>
            {
                var (r, c) = all[n];
                var rot = SkyrimRotation.Matrix(r.Rotation);
                var mine = new List<((int X, int Y), System.Numerics.Vector3[])>(c.Length / 9);
                for (int i = 0; i < c.Length; i += 9)
                {
                    var tri = new System.Numerics.Vector3[3];
                    for (int v = 0; v < 3; v++)
                        tri[v] = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(c[i + v * 3], c[i + v * 3 + 1], c[i + v * 3 + 2]) * r.Scale, rot) + r.Position;
                    var mid = (tri[0] + tri[1] + tri[2]) / 3;
                    int gx = (int)MathF.Floor(mid.X / OverlandTiles.CellUnits), gy = (int)MathF.Floor(mid.Y / OverlandTiles.CellUnits);
                    if (gx < x0 || gx > x1 || gy < y0 || gy > y1) continue;
                    mine.Add(((gx, gy), tri));
                }
                placed[n] = mine;
            });
            var result = new Dictionary<(int, int), List<System.Numerics.Vector3[]>>();
            foreach (var mine in placed)
                foreach (var (cell, tri) in mine)
                {
                    if (!result.TryGetValue(cell, out var list)) result[cell] = list = [];
                    list.Add(tri);
                }
            return result;
        }

        /// <summary>Cells round a chunk whose references are looked at.</summary>
        const int Reach = 3;

        public string Summary() =>
            $"standing: {modelPaths.Count:N0} models known, {read:N0} read ({empty:N0} with no solid collision), {missing:N0} not found, {refsWithout:N0} references whose base has no model"
            + (unread.Count > 0 ? $"; shapes not read: {string.Join(", ", unread.OrderByDescending(u => u.Value).Take(6).Select(u => $"{u.Key} {u.Value:N0}"))}" : "");
    }

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
