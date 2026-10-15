using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// The Iliac Bay overland split into tiles, each its own worldspace centred on its own origin, when it is too big for
/// one. Skyrim's physics ends 64 cells from a worldspace's centre (its Havok broad phase box), which TamrielMergeSeamless
/// (our SKSE plugin) widens to 512 cells at load ([World] iPhysicsCells), so since 2026-10-12 a map up to 1024 cells a
/// side is one worldspace (user: travel seen "without loading", the map not made smaller): Daggerfall's 1000 x 500 at
/// one map pixel a cell is one, built in chunks (<see cref="Chunks"/>) so a build holds only a chunk of land at a time.
/// Positions keep Skyrim's float precision: about 2-4 mm 500 cells out, against 0.5 mm at the vanilla map's edges. Each tile holds a core of cells plus <see cref="Overlap"/> cells of its neighbours' land,
/// so the ground ahead is there when the player nears the core's edge; walking <see cref="Hysteresis"/> units past the
/// edge moves the player into the neighbouring tile at the same spot (TMDfTiles). Everything is built in global
/// coordinates (the whole map centred on 0,0, as <see cref="HeightField"/>) and moved into its tile afterwards.
/// A map that fits within the precision limit is one tile, "DFIliacBay", as before.
/// </summary>
public sealed class OverlandTiles
{
    public const int CellUnits = HeightField.QuadsPerCell * HeightField.UnitsPerQuad;
    /// <summary>Largest map (cells per side) kept as one worldspace: ±512 cells from its centre, the physics box
    /// TamrielMergeSeamless makes (was 256: ±128, with tiles past it).</summary>
    public const int SingleLimit = 1024;
    /// <summary>A big worldspace's land, distant terrain and navmesh are built this many cells square at a time (a
    /// multiple of the distant terrain's largest chunk, so none of its chunks is split).</summary>
    public const int ChunkCells = 128;
    /// <summary>Largest tile core: with the overlap a tile reaches ±120 cells from its origin.</summary>
    public const int MaxCore = 224;
    /// <summary>Neighbours' cells each tile also has land for (and distant terrain beyond it, <see cref="LodMargin"/>).</summary>
    public const int Overlap = 8;
    public const int LodMargin = 32;
    /// <summary>How far past the core's edge (units) the player is moved on, so standing on the edge doesn't flip back and forth.</summary>
    public const float Hysteresis = 2048;

    public int MinCellX { get; }
    public int MinCellY { get; }
    public int CellsX { get; }
    public int CellsY { get; }
    public int Columns { get; }
    public int Rows { get; }
    /// <summary>Core size of every tile, in cells (the last column and row may run past the map's edge).</summary>
    public int CoreX { get; }
    public int CoreY { get; }
    public bool Single => Columns == 1 && Rows == 1;
    public int Count => Columns * Rows;

    public OverlandTiles(HeightField f) : this(f.MinCellX, f.MinCellY, f.CellsX, f.CellsY) { }

    public OverlandTiles(int minCellX, int minCellY, int cellsX, int cellsY)
    {
        MinCellX = minCellX; MinCellY = minCellY; CellsX = cellsX; CellsY = cellsY;
        bool single = cellsX <= SingleLimit && cellsY <= SingleLimit && -minCellX <= SingleLimit / 2 && -minCellY <= SingleLimit / 2;
        Columns = single ? 1 : (cellsX + MaxCore - 1) / MaxCore;
        Rows = single ? 1 : (cellsY + MaxCore - 1) / MaxCore;
        CoreX = (cellsX + Columns - 1) / Columns;
        CoreY = (cellsY + Rows - 1) / Rows;
    }

    public int Index(int col, int row) => row * Columns + col;

    /// <summary>The south-west cell and stride (cells, a power of two from 32) the distant terrain of a tile covers.</summary>
    public (int SwX, int SwY, int Stride) LodFrame(int tile, int margin)
    {
        var (x0, y0, x1, y1) = Extent(tile, margin);
        var (ox, oy) = Origin(tile);
        int swX = FloorDiv(x0 - ox, TerrainLod.MaxLevel) * TerrainLod.MaxLevel, swY = FloorDiv(y0 - oy, TerrainLod.MaxLevel) * TerrainLod.MaxLevel;
        int stride = TerrainLod.MaxLevel;
        while (swX + stride <= x1 - ox || swY + stride <= y1 - oy) stride *= 2;
        return (swX, swY, stride);
    }

    /// <summary>
    /// The pieces a tile's land is built in, global grid, inclusive: the whole tile (with <paramref name="margin"/>
    /// cells round it for distant terrain) when it has no more than ChunkCells x ChunkCells cells, else squares of
    /// ChunkCells on the distant terrain's grid (from its south-west corner) covering it.
    /// </summary>
    public List<(int X0, int Y0, int X1, int Y1)> Chunks(int tile, int margin)
    {
        var (x0, y0, x1, y1) = Extent(tile, margin);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) <= (long)ChunkCells * ChunkCells) return [(x0, y0, x1, y1)];
        var (ox, oy) = Origin(tile);
        var (swX, swY, _) = LodFrame(tile, margin);
        var list = new List<(int, int, int, int)>();
        for (int cy = swY + oy; cy <= y1; cy += ChunkCells)
            for (int cx = swX + ox; cx <= x1; cx += ChunkCells)
            {
                int a = Math.Max(cx, x0), b = Math.Max(cy, y0), c = Math.Min(cx + ChunkCells - 1, x1), d = Math.Min(cy + ChunkCells - 1, y1);
                if (a <= c && b <= d) list.Add((a, b, c, d));
            }
        return list;
    }
    public (int Col, int Row) ColRow(int tile) => (tile % Columns, tile / Columns);

    public string EditorId(int tile) => Single ? DaggerfallPluginBuilder.WorldEditorId : $"{DaggerfallPluginBuilder.WorldEditorId}_{tile % Columns}_{tile / Columns}";

    /// <summary>Global grid of a tile's origin cell (its local 0,0). A single tile keeps the global grid.</summary>
    public (int X, int Y) Origin(int tile)
    {
        if (Single) return (0, 0);
        var (c, r) = ColRow(tile);
        return (MinCellX + c * CoreX + CoreX / 2, MinCellY + r * CoreY + CoreY / 2);
    }

    /// <summary>A tile's core cells, global grid, inclusive.</summary>
    public (int X0, int Y0, int X1, int Y1) Core(int tile)
    {
        var (c, r) = ColRow(tile);
        return (MinCellX + c * CoreX, MinCellY + r * CoreY, MinCellX + (c + 1) * CoreX - 1, MinCellY + (r + 1) * CoreY - 1);
    }

    /// <summary>The cells a tile has land for (core and overlap, within the map), global grid, inclusive.</summary>
    public (int X0, int Y0, int X1, int Y1) Extent(int tile, int margin = Overlap)
    {
        var (x0, y0, x1, y1) = Core(tile);
        return (Math.Max(x0 - margin, MinCellX), Math.Max(y0 - margin, MinCellY), Math.Min(x1 + margin, MinCellX + CellsX - 1), Math.Min(y1 + margin, MinCellY + CellsY - 1));
    }

    public int TileOfCell(int gx, int gy) =>
        Index(Math.Clamp(FloorDiv(gx - MinCellX, CoreX), 0, Columns - 1), Math.Clamp(FloorDiv(gy - MinCellY, CoreY), 0, Rows - 1));

    public int TileAt(P3Float global) => TileOfCell((int)MathF.Floor(global.X / CellUnits), (int)MathF.Floor(global.Y / CellUnits));

    /// <summary>Units added to a global position to make it local to the tile.</summary>
    public P3Float Shift(int tile) { var (ox, oy) = Origin(tile); return new P3Float(-ox * CellUnits, -oy * CellUnits, 0); }

    public P3Float ToLocal(P3Float global, int tile) => global + Shift(tile);

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>
    /// Makes the tiles' worldspaces from <paramref name="world"/> (built in global coordinates, its persistent cell
    /// holding everything placed on the overland) and moves each placed reference into the tile it stands in. Doors
    /// anywhere in <paramref name="mod"/> leading to a moved reference arrive at its new position. A single tile is
    /// <paramref name="world"/> itself. The tiles' land is built separately (<see cref="LandPlugins"/>).
    /// </summary>
    public List<Worldspace> Split(SkyrimMod mod, Worldspace world)
    {
        if (Single)
        {
            SetBounds(world, 0);
            return [world];
        }
        var tiles = new List<Worldspace>();
        for (int t = 0; t < Count; t++)
        {
            var ws = world.Duplicate(mod.GetNextFormKey(), new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
            ws.EditorID = EditorId(t);
            ws.SubCells.Clear();
            ws.TopCell = new Cell(mod) { Flags = world.TopCell?.Flags ?? 0, SkyrimMajorRecordFlags = world.TopCell?.SkyrimMajorRecordFlags ?? 0, Persistent = [], Temporary = [] };
            SetBounds(ws, t);
            mod.Worldspaces.Add(ws);
            tiles.Add(ws);
        }
        var placed = world.TopCell?.Persistent.ToList() ?? [];
        world.TopCell?.Persistent.Clear();
        MoveInto(mod, placed, (ref_, t) => tiles[t].TopCell!.Persistent.Add(ref_));
        mod.Worldspaces.Remove(world);
        return tiles;
    }

    /// <summary>
    /// Moves references placed in global coordinates into tiles (<paramref name="add"/> puts one in its tile) and
    /// fixes the arrival point of every door in <paramref name="mod"/> that leads to one of them.
    /// </summary>
    public void MoveInto(SkyrimMod mod, IEnumerable<IPlaced> placed, Action<IPlaced, int> add)
    {
        var shifts = new Dictionary<FormKey, P3Float>();
        foreach (var r in placed)
        {
            int t = 0;
            if (r is IPlaced p && Position(p) is { } pos)
            {
                t = TileAt(pos);
                var s = Shift(t);
                SetPosition(p, pos + s);
                shifts[r.FormKey] = s;
            }
            add(r, t);
        }
        if (shifts.Count == 0 || Single) return;
        foreach (var door in AllPlacedObjects(mod))
            if (door.TeleportDestination is { } d && shifts.TryGetValue(d.Door.FormKey, out var s))
                d.Position += s;
    }

    /// <summary>
    /// For a plugin built on Daggerfall.esm (dungeon and town batches): a scratch worldspace standing for the whole
    /// overland in global coordinates, to place entrances on (with <paramref name="master"/>'s climate, water and music),
    /// and a function moving what was placed on it into overrides of the master's tiles (persistent cells only).
    /// </summary>
    public (Worldspace Scratch, Action MoveIntoTiles) OverrideTiles(SkyrimMod mod, ISkyrimModGetter master)
    {
        var first = master.Worldspaces.First(w => w.EditorID == EditorId(0));
        var scratch = new Worldspace(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            Climate = first.Climate.AsNullable(), Water = first.Water.AsNullable(), LodWater = first.LodWater.AsNullable(),
            Music = first.Music.AsNullable(), Location = first.Location.AsNullable(),
            TopCell = new Cell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Persistent = [], Temporary = [] },
        };
        var overrides = new Dictionary<int, Worldspace>();
        Worldspace Tile(int t)
        {
            if (overrides.TryGetValue(t, out var ws)) return ws;
            var masterWorld = master.Worldspaces.First(w => w.EditorID == EditorId(t));
            ws = masterWorld.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
            ws.SubCells.Clear();
            var top = masterWorld.TopCell!.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Temporary = false, Persistent = false, Landscape = false, NavigationMeshes = false });
            top.Persistent.Clear();
            top.Temporary.Clear();
            ws.TopCell = top;
            mod.Worldspaces.Add(ws);
            return overrides[t] = ws;
        }
        return (scratch, () =>
        {
            MoveInto(mod, scratch.TopCell!.Persistent.ToList(), (r, t) => Tile(t).TopCell!.Persistent.Add(r));
            scratch.TopCell!.Persistent.Clear();
        });
    }

    static P3Float? Position(IPlaced p) => p switch
    {
        PlacedObject o => o.Placement?.Position,
        PlacedNpc n => n.Placement?.Position,
        APlacedTrap tr => tr.Placement?.Position,
        _ => null,
    };

    static void SetPosition(IPlaced p, P3Float pos)
    {
        switch (p)
        {
            case PlacedObject o when o.Placement is { } pl: pl.Position = pos; break;
            case PlacedNpc n when n.Placement is { } pl: pl.Position = pos; break;
            case APlacedTrap tr when tr.Placement is { } pl: pl.Position = pos; break;
        }
    }

    /// <summary>Every placed object in the plugin's cells: interiors, worldspace cells and persistent cells.</summary>
    static IEnumerable<PlacedObject> AllPlacedObjects(SkyrimMod mod)
    {
        IEnumerable<Cell> cells = mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells);
        cells = cells.Concat(mod.Worldspaces.SelectMany(w => (w.TopCell is { } top ? new[] { top } : []).Concat(w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))));
        foreach (var c in cells)
            foreach (var r in c.Persistent.Concat(c.Temporary))
                if (r is PlacedObject o) yield return o;
    }

    void SetBounds(Worldspace ws, int tile)
    {
        var (x0, y0, x1, y1) = Extent(tile);
        var (ox, oy) = Origin(tile);
        x0 -= ox; x1 -= ox; y0 -= oy; y1 -= oy;
        // NAM0/NAM9 are in cell coordinates, not world units.
        ws.ObjectBoundsMin = new P2Float(x0, y0);
        ws.ObjectBoundsMax = new P2Float(x1 + 1, y1 + 1);
        if (ws.MapData is { } map)
        {
            map.NorthwestCellCoords = new P2Int16((short)x0, (short)y1);
            map.SoutheastCellCoords = new P2Int16((short)x1, (short)y0);
        }
    }

    /// <summary>"Daggerfall Land.esm": every tile's terrain (their cells and land), with Daggerfall.esm as master; the
    /// first of the land's plugins (<see cref="LandPluginNames"/>).</summary>
    public const string LandPluginName = "Daggerfall Land.esm";
    /// <summary>
    /// The land's plugins, in load order: "Daggerfall Land.esm", then "Daggerfall Land 2.esm" and on when the land is
    /// split. Mutagen's binary overlay reads a plugin only up to 2 GB, and the whole land with its navmeshes came to
    /// 2.01 GB on 2026-10-15 (LAND 1,627 MB, NAVM 225, CELL 27, NAVI 19, compressed), so it is split before it gets
    /// there (<see cref="LandPluginsWanted"/>): each plugin holds a run of the land's pieces, each one after the first has
    /// the ones before it as masters (the land textures are in the first), and the last also holds every navmesh, over
    /// the other plugins' cells, so the navmeshes' links between cells all stay in one plugin.
    /// </summary>
    public static readonly string[] LandPluginNames = [LandPluginName, "Daggerfall Land 2.esm", "Daggerfall Land 3.esm", "Daggerfall Land 4.esm"];
    /// <summary>The land's plugins found in <paramref name="dir"/>, in load order.</summary>
    public static List<string> LandPluginsIn(string dir) => LandPluginNames.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
    /// <summary>The land plugin with the overland's navmeshes (and the navmesh map): the last of them.</summary>
    public static string? NavmeshPluginIn(string dir) => LandPluginsIn(dir).LastOrDefault();
    /// <summary>How many plugins the land is split into; null: as many as <see cref="PluginBudget"/> needs.</summary>
    public static int? LandPluginsWanted { get; set; }
    /// <summary>Bytes each land cell adds, joined and compressed (2026-10-15: 2,009,995,802 for 399,800 cells): its land,
    /// and its share of the navmeshes and their map.</summary>
    const double LandBytesPerCell = (1627.2 + 26.7) * 1048576 / 399_800, NavmeshBytesPerCell = (225.4 + 19.1) * 1048576 / 399_800;
    /// <summary>The most a land plugin is planned to hold, well under the 2 GB Mutagen reads (room for a finer navmesh).</summary>
    const double PluginBudget = 1.6e9;
    static string LandPartPath(string dir, int part) => Path.Combine(dir, $"Daggerfall Land.part{part + 1}.esm");

    /// <summary>
    /// Writes the tiles' land as one plugin (<see cref="LandPluginName"/>), or split into several (<see cref="LandPluginNames"/>)
    /// when it would be too big for one, built a tile at a time so only one tile's land
    /// is ever in memory (each a part, joined by <see cref="PluginMerger"/>), and each tile's distant terrain. Cells all below <see cref="TerrainBuilder.SkipBelow"/> get none (open sea).
    /// <paramref name="regionOf"/> gives the weather region for a tile's cell (global grid), if any. Returns the land
    /// plugins' file names, in load order.
    /// </summary>
    public List<string> LandPlugins(string mainPlugin, string skyrimData, HeightField f, Daggerfall.DfWorldMap map,
        Func<int, int, int, FormKey?> regionOf, Action<string>? log = null, IReadOnlyCollection<int>? onlyTiles = null)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(mainPlugin))!;
        using var master = SkyrimMod.CreateFromBinaryOverlay(mainPlugin, SkyrimRelease.SkyrimSE);
        float peak = f.Peak();
        var written = new List<string>();
        // Daggerfall's own ground (TerrainBuilder.AddDaggerfallTextures), made in the first part built.
        using var skyrimEsm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        string? arena2 = GameInstalls.DataDir(Game.Daggerfall);
        bool textured = arena2 is null;
        // The work: each tile whole (with its distant terrain's margin), or a big tile chunk by chunk (one part each).
        var units = new List<(int Tile, int X0, int Y0, int X1, int Y1, bool Whole)>();
        for (int t = 0; t < Count; t++)
        {
            if (onlyTiles is not null && !onlyTiles.Contains(t)) continue;
            var chunks = Chunks(t, LodMargin);
            foreach (var (a, b, c, d) in chunks) units.Add((t, a, b, c, d, chunks.Count == 1));
        }
        // Which plugin each piece goes in: the land cells counted first (as BuildCell finds them), the plugins as many
        // as the estimate needs (or LandPluginsWanted), the pieces in order, each plugin a run of them with an equal
        // share of the bytes; the last plugin also takes the navmeshes, so it gets less land.
        var landIn = new int[units.Count];
        Parallel.For(0, units.Count, u =>
        {
            var (t, ux0, uy0, ux1, uy1, _) = units[u];
            var (x0, y0, x1, y1) = Extent(t);
            int n = 0;
            for (int gy = Math.Max(uy0, y0); gy <= Math.Min(uy1, y1); gy++)
                for (int gx = Math.Max(ux0, x0); gx <= Math.Min(ux1, x1); gx++)
                    if (TerrainBuilder.HasLand(f, gx - f.MinCellX, gy - f.MinCellY)) n++;
            landIn[u] = n;
        });
        long totalLand = landIn.Sum(n => (long)n);
        int plugins = Math.Clamp(LandPluginsWanted ?? (int)Math.Ceiling(totalLand * (LandBytesPerCell + NavmeshBytesPerCell) / PluginBudget), 1, LandPluginNames.Length);
        var pluginOf = new int[units.Count];
        {
            double share = (LandBytesPerCell + NavmeshBytesPerCell) / plugins / LandBytesPerCell;   // of the land, each plugin but the last
            long before = 0;
            for (int u = 0; u < units.Count; u++)
            {
                // By the piece's middle, so a piece goes where most of it falls.
                pluginOf[u] = totalLand == 0 ? 0 : Math.Min(plugins - 1, (int)((before + landIn[u] / 2.0) / (totalLand * share)));
                before += landIn[u];
            }
        }
        var landMasters = Enumerable.Range(0, plugins).Select(p => PluginMerger.Masters([Path.GetFileName(mainPlugin), .. LandPluginNames.Take(p)])).ToArray();
        if (plugins > 1) log?.Invoke($"{totalLand:N0} land cells, estimated {totalLand * (LandBytesPerCell + NavmeshBytesPerCell) / 1e9:F2} GB with the navmeshes: split into {plugins} plugins ({string.Join(", ", Enumerable.Range(0, plugins).Select(p => $"{LandPluginNames[p]} {Enumerable.Range(0, units.Count).Where(u => pluginOf[u] == p).Sum(u => landIn[u]):N0} cells"))})");
        var writtenBy = Enumerable.Range(0, plugins).Select(_ => new List<string>()).ToArray();
        var lodFramed = new HashSet<int>();
        for (int u = 0; u < units.Count; u++)
        {
            var (t, ux0, uy0, ux1, uy1, whole) = units[u];
            var masterWorld = master.Worldspaces.FirstOrDefault(w => w.EditorID == EditorId(t));
            if (masterWorld is null) continue;
            // The land textures go in the first plugin, the one every other has as master.
            int plugin = textured ? pluginOf[u] : 0;
            var mod = new SkyrimMod(ModKey.FromFileName(LandPluginNames[plugin]), SkyrimRelease.SkyrimSE);
            mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
            PluginMerger.PreparePart(mod, u + 1, units.Count);
            var ws = masterWorld.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
            ws.SubCells.Clear();
            ws.TopCell = null;
            mod.Worldspaces.Add(ws);
            bool holdsTextures = false;
            if (!textured)
            {
                TerrainBuilder.AddDaggerfallTextures(mod, skyrimEsm, arena2!, outDir);
                textured = holdsTextures = true;
            }

            var (ox, oy) = Origin(t);
            var (x0, y0, x1, y1) = Extent(t);
            int w = ux1 - ux0 + 1, h = uy1 - uy0 + 1;
            // Land for the piece; a whole tile's also for the cells round it, for its distant terrain only.
            var built = new Cell?[w * h];
            Parallel.For(0, h, j =>
            {
                for (int i = 0; i < w; i++)
                {
                    int gx = ux0 + i, gy = uy0 + j;
                    built[j * w + i] = TerrainBuilder.BuildCell(mod, f, map, gx - f.MinCellX, gy - f.MinCellY, peak, gx - ox, gy - oy);
                }
            });
            var lodCells = new Dictionary<(int, int), Cell>();
            int land = 0;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    if (built[j * w + i] is not { } cell) continue;
                    int gx = ux0 + i, gy = uy0 + j;
                    lodCells[(gx - ox, gy - oy)] = cell;
                    if (gx < x0 || gx > x1 || gy < y0 || gy > y1) continue;
                    if (regionOf(t, gx, gy) is { } region) cell.Regions = [region.ToLink<IRegionGetter>()];
                    TerrainBuilder.AddCell(ws, cell);
                    land++;
                }
            string path = LandPartPath(outDir, u);
            string label = whole ? EditorId(t) : $"{EditorId(t)} cells {ux0},{uy0}..{ux1},{uy1}";
            if (land == 0 && !holdsTextures) { log?.Invoke($"{label}: open sea, no land"); continue; }
            PluginMerger.WritePart(mod, path, landMasters[plugin], u + 1, units.Count);
            var lod = new TerrainLod(mod, outDir, skyrimData);
            if (whole) lod.Build(EditorId(t), lodCells, c => c.Flags.HasFlag(Cell.Flag.HasWater) ? 0f : null, TerrainBuilder.DefaultLandHeight);
            else
            {
                // A chunk: the whole world's distant terrain settings once, then the chunks of its square.
                var frame = LodFrame(t, LodMargin);
                if (lodFramed.Add(t)) lod.WriteSettings(EditorId(t), frame.SwX, frame.SwY, frame.Stride);
                lod.Build(EditorId(t), lodCells, c => c.Flags.HasFlag(Cell.Flag.HasWater) ? 0f : null, TerrainBuilder.DefaultLandHeight,
                    (ux0 - ox, uy0 - oy, ux1 - ox, uy1 - oy));
            }
            log?.Invoke($"{label}: {land} cells with land, {lod.Chunks} distant terrain chunks{(lod.Problems.Count > 0 ? ", problems: " + string.Join("; ", lod.Problems.Take(3)) : "")}");
            Progress.Report((u + 1) / (double)units.Count, $"Land: {u + 1} of {units.Count}");
            written.Add(path);
            writtenBy[plugin].Add(path);
        }
        // Each plugin from its pieces.
        var names = new List<string>();
        for (int p = 0; p < plugins; p++)
        {
            if (writtenBy[p].Count == 0) continue;
            long records = PluginMerger.Merge(writtenBy[p], Path.Combine(outDir, LandPluginNames[p]));
            foreach (var part in writtenBy[p]) File.Delete(part);
            log?.Invoke($"{LandPluginNames[p]}: {records} records and groups from {writtenBy[p].Count} pieces, {new FileInfo(Path.Combine(outDir, LandPluginNames[p])).Length / 1048576} MB");
            names.Add(LandPluginNames[p]);
        }
        // Land plugins an earlier build split differently (and the one-plugin-per-tile files of older ones).
        foreach (var old in Directory.GetFiles(outDir, "Daggerfall Land *.esm").Where(o => !names.Contains(Path.GetFileName(o), StringComparer.OrdinalIgnoreCase))) File.Delete(old);
        return names;
    }

    /// <summary>The land plugins over <see cref="SafeSize"/> (Mutagen reads up to 2 GB), with their sizes in MB.</summary>
    public static List<string> Oversized(string dir) =>
        LandPluginsIn(dir).Where(n => new FileInfo(Path.Combine(dir, n)).Length >= SafeSize).Select(n => $"{n} ({new FileInfo(Path.Combine(dir, n)).Length / 1048576} MB)").ToList();
    public const long SafeSize = 1_900_000_000;
}
