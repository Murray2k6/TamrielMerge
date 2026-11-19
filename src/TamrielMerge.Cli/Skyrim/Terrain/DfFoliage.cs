using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// Daggerfall's nature on its overland (user, 2026-10-25: "daggerfall has foliage"; every nature flat as an object, at
/// Daggerfall's own density): its trees, bushes, rocks and flowers as Daggerfall Unity lays them
/// (DaggerfallTerrain.LayoutNature), as the models made from its sprites (Daggerfall.esm's DFSpriteModel statics,
/// <see cref="SpriteModels"/>). One chance a ground tile (<see cref="TileUnits"/>, kept at Daggerfall's size as the
/// ground's patches are): 0.9 on grass, 0.2 on dirt, 0.05 on stone (<see cref="TerrainBuilder.Ground"/>: the desert's
/// a quarter), times the map pixel's height over 128 (0.4 to 1: sparser lowland, denser highland); none on ground
/// steeper than 45 degrees, at the shore or inside a location's own area; the record one of the climate's nature
/// archive's 1 to 31 at random (records 7 and 8, its grass tufts, are the land's grass: <see cref="DfGrass"/>), sunk by
/// its slope. Every tile's draws come from the tile itself, so a cell's foliage is the same however the land is built.
/// Written as overrides of the land plugins' cells, in plugins of their own (<see cref="PluginNames"/>), built a piece
/// at a time and joined (<see cref="PluginMerger"/>). The overland's navmesh isn't cut round it (OverlandNavmesh): as
/// Skyrim's tree mods, the actors walk the land's own and step round what they meet.
/// </summary>
public static class DfFoliage
{
    /// <summary>The foliage's plugins, in load order; more than one when it is too big for one.</summary>
    public static readonly string[] PluginNames = ["Daggerfall Foliage.esm", "Daggerfall Foliage 2.esm", "Daggerfall Foliage 3.esm", "Daggerfall Foliage 4.esm"];

    /// <summary>A Daggerfall ground tile: Daggerfall Unity's 819.2 m map pixel is 128 tiles, 6.4 m each.</summary>
    const float TileUnits = 6.4f * 70;
    const float GrassChance = 0.9f;
    const float MaxSlopeDegrees = 45;
    /// <summary>How far round its foot the ground is looked at for where it stands (units), and how far into it.</summary>
    const float FootReach = 96, FootSink = 8;
    /// <summary>Below this height (units) is the shore: the beach's dirt begins (TerrainBuilder.Weights), no flats.</summary>
    const float Shore = 60;
    /// <summary>A location's own area is cleared this much wider each side (Daggerfall Unity expands it a little).</summary>
    const float ClearMargin = TileUnits;
    /// <summary>The most objects a piece holds (its FormIDs come from a range of its own) and a plugin: the FormIDs below
    /// the shared band (PluginMerger.SharedStart, 15.7 million) bound a plugin, and at about 70 bytes a reference 15
    /// million is about 1 GB, under the 2 GB Mutagen reads (2026-10-28: three plugins of about 585 MB became two; user:
    /// as few plugins as the engine's slots allow).</summary>
    const int PieceLimit = 300_000, PluginLimit = 15_000_000;

    public readonly record struct Flat(int Archive, int Record, Vector3 Pos, float Heading, int Pick);
    /// <summary>A sprite's object: a model made from it (scale 1), or Skyrim stand-ins fitted to its height.</summary>
    public sealed record Model(FormKey Static, Vector3 Min, Vector3 Max, SpriteModels.Kind Kind, float Scale = 1);

    /// <summary>Nature sprites without a model of their own (none made: the haunted woodland's ribcage; or turned down
    /// on review: the swamp's black dead tree, SpriteModelImporter.Rejected), as the Skyrim statics nearest them.</summary>
    static readonly Dictionary<(int Archive, int Record), (SpriteModels.Kind Kind, string[] Ids)> StandIns = new()
    {
        [(508, 11)] = (SpriteModels.Kind.Prop, ["BoneMammothRibs", "BoneMammothRibsBroken"]),
        [(502, 30)] = (SpriteModels.Kind.Tree, ["TreePineForest01Dead", "TreePineForest02Dead"]),
    };

    /// <summary>
    /// The objects for the nature sprites, by sprite: the models made from them in <paramref name="master"/>
    /// (Daggerfall.esm), else their stand-ins from <paramref name="skyrim"/> scaled to the sprite's height in the world
    /// (its size in <paramref name="arena2"/> at the blocks' scale, as DfGrass sizes the grass).
    /// </summary>
    public static Dictionary<(int Archive, int Record), Model[]> Models(ISkyrimModGetter master, ISkyrimModGetter skyrim, string arena2)
    {
        var byName = SpriteModels.Targets.Where(t => t.Archive is >= 500 and <= 511)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<(int, int), Model[]>();
        static (Vector3, Vector3) Bounds(IObjectBoundsGetter ob) => (new Vector3(ob.First.X, ob.First.Y, ob.First.Z), new Vector3(ob.Second.X, ob.Second.Y, ob.Second.Z));
        foreach (var st in master.Statics)
        {
            if (st.EditorID is not { } e || !e.StartsWith("DFSpriteModel", StringComparison.Ordinal) || st.ObjectBounds is not { } ob) continue;
            if (!byName.TryGetValue(e["DFSpriteModel".Length..], out var targets)) continue;
            var (min, max) = Bounds(ob);
            foreach (var t in targets) models[(t.Archive, t.Record)] = [new Model(st.FormKey, min, max, t.Kind)];
        }
        // Statics or trees (Skyrim's dead pines are trees).
        var statics = skyrim.Statics.Select(s => (s.EditorID, s.FormKey, s.ObjectBounds))
            .Concat(skyrim.Trees.Select(t => (t.EditorID, t.FormKey, t.ObjectBounds)))
            .Where(s => s.EditorID is { } id && StandIns.Values.Any(v => v.Ids.Contains(id)) && s.ObjectBounds is not null)
            .GroupBy(s => s.EditorID!).ToDictionary(g => g.Key, g => g.First());
        foreach (var ((archive, record), (kind, ids)) in StandIns)
        {
            if (models.ContainsKey((archive, record)) || DfTexture.Load(arena2, archive) is not { } tex || record >= tex.Count) continue;
            float height = tex.Image(record).WorldSize.Height * Meshes.BlockMeshBuilder.Scale;
            var list = new List<Model>();
            foreach (var id in ids)
            {
                if (!statics.TryGetValue(id, out var st)) continue;
                var (min, max) = Bounds(st.ObjectBounds!);
                float own = max.Z - min.Z;
                list.Add(new Model(st.FormKey, min, max, kind, own > 1 ? Math.Clamp(height / own, 0.4f, 3f) : 1));
            }
            if (list.Count > 0) models[(archive, record)] = [.. list];
        }
        return models;
    }

    /// <summary>Each location's ground on the overland (global units: x0, y0, x1, y1): its footprint where the layout put
    /// it (LocationLayout: its blocks at full size), <see cref="ClearMargin"/> wider.</summary>
    public static List<(float X0, float Y0, float X1, float Y1)> Clearings(LocationLayout layout)
    {
        const float cell = OverlandTiles.CellUnits;
        return layout.Sites.Select(s => (s.CellX * cell - ClearMargin, s.CellY * cell - ClearMargin,
            (s.CellX + s.CellsW) * cell + ClearMargin, (s.CellY + s.CellsH) * cell + ClearMargin)).ToList();
    }

    /// <summary>The clearings by cell, for a quick look-up.</summary>
    public static Dictionary<(int X, int Y), List<(float X0, float Y0, float X1, float Y1)>> ByCell(List<(float X0, float Y0, float X1, float Y1)> clearings)
    {
        var by = new Dictionary<(int, int), List<(float, float, float, float)>>();
        foreach (var c in clearings)
            for (int gx = (int)MathF.Floor(c.X0 / OverlandTiles.CellUnits); gx <= (int)MathF.Floor(c.X1 / OverlandTiles.CellUnits); gx++)
                for (int gy = (int)MathF.Floor(c.Y0 / OverlandTiles.CellUnits); gy <= (int)MathF.Floor(c.Y1 / OverlandTiles.CellUnits); gy++)
                {
                    if (!by.TryGetValue((gx, gy), out var l)) by[(gx, gy)] = l = [];
                    l.Add(c);
                }
        return by;
    }

    /// <summary>
    /// The flats of global cell (<paramref name="gx"/>, <paramref name="gy"/>), positions global: one chance for each
    /// tile whose corner lies in the cell (Daggerfall Unity places a flat at its tile's corner).
    /// </summary>
    public static void InCell(HeightField f, DfWorldMap map, int gx, int gy, IReadOnlyList<(float X0, float Y0, float X1, float Y1)>? clear, List<Flat> into)
    {
        const float cell = OverlandTiles.CellUnits;
        int tx0 = (int)MathF.Ceiling(gx * cell / TileUnits), tx1 = (int)MathF.Ceiling((gx + 1) * cell / TileUnits) - 1;
        int ty0 = (int)MathF.Ceiling(gy * cell / TileUnits), ty1 = (int)MathF.Ceiling((gy + 1) * cell / TileUnits) - 1;
        Span<float> w = stackalloc float[TerrainBuilder.Ground.Length];
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                float x = tx * TileUnits, y = ty * TileUnits;
                ulong r = Mix((ulong)(uint)tx << 32 | (uint)ty);
                // Field vertex coordinates (the land's grid from its south-west corner).
                double vx = x / HeightField.UnitsPerQuad - f.MinCellX * HeightField.QuadsPerCell;
                double vy = y / HeightField.UnitsPerQuad - f.MinCellY * HeightField.QuadsPerCell;
                if (vx < 0 || vy < 0 || vx > f.VertsX - 1 || vy > f.VertsY - 1) continue;
                float h = Height(f, vx, vy);
                if (h < Shore) continue;
                // The slope (degrees) from the height's change over a quad each way.
                float dx = (Height(f, vx + 1, vy) - Height(f, vx - 1, vy)) / (2f * HeightField.UnitsPerQuad);
                float dy = (Height(f, vx, vy + 1) - Height(f, vx, vy - 1)) / (2f * HeightField.UnitsPerQuad);
                float slope = MathF.Atan(MathF.Sqrt(dx * dx + dy * dy)) * 180 / MathF.PI;
                if (slope > MaxSlopeDegrees) continue;
                if (clear is not null && clear.Any(c => x >= c.X0 && x <= c.X1 && y >= c.Y0 && y <= c.Y1)) continue;
                // The ground there: the land texture with the most weight at the nearest vertex (as the land is painted).
                int ivx = (int)Math.Round(vx), ivy = (int)Math.Round(vy);
                TerrainBuilder.Weights(f, map, ivx, ivy, 0, w);
                int tex = 0;
                for (int i = 1; i < w.Length; i++) if (w[i] > w[tex]) tex = i;
                var (u, v) = f.PixelCoord(ivx, ivy);
                int px = (int)Math.Floor(u), py = (int)Math.Floor(v);
                float elevation = Math.Clamp(map.ElevationAt(px, py) / 128f, 0.4f, 1f);
                float chance = GrassChance * TerrainBuilder.Ground[tex].Grass * elevation;
                if (Unit(ref r) > chance) continue;
                int record = 1 + (int)(Unit(ref r) * 31) % 31;
                float heading = Unit(ref r) * MathF.Tau;
                int pick = (int)(Unit(ref r) * 1024);
                if (record is 7 or 8) continue;   // grass tufts: the land's own grass
                int archive = DfTownLayout.NatureArchiveOf(map.ClimateAt(px, py));
                // On the lowest ground round its foot (a trunk, a rock's base), so no side of it hangs over a slope (user,
                // 2026-10-25: "trees were floating"; Daggerfall Unity sinks a flat by its slope), a little into it.
                float reach = FootReach / HeightField.UnitsPerQuad;
                float low = MathF.Min(MathF.Min(Height(f, vx - reach, vy), Height(f, vx + reach, vy)), MathF.Min(Height(f, vx, vy - reach), Height(f, vx, vy + reach)));
                into.Add(new Flat(archive, record, new Vector3(x, y, MathF.Min(h, low) - FootSink), heading, pick));
            }
    }

    static float Height(HeightField f, double vx, double vy)
    {
        int x0 = (int)Math.Floor(vx), y0 = (int)Math.Floor(vy);
        double fx = vx - x0, fy = vy - y0;
        return (float)((f.At(x0, y0) * (1 - fx) + f.At(x0 + 1, y0) * fx) * (1 - fy) + (f.At(x0, y0 + 1) * (1 - fx) + f.At(x0 + 1, y0 + 1) * fx) * fy);
    }

    /// <summary>SplitMix64: the tile's own random numbers, the same in every run and process.</summary>
    static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static float Unit(ref ulong r)
    {
        r = Mix(r);
        return (r >> 40) / (float)(1UL << 24);
    }

    /// <summary>
    /// How much foliage the overland gets: flats over every cell with land, by kind of model, and the sprites without
    /// a model (left out). Measured before building (user rule: numbers, not guesses).
    /// </summary>
    public static void Count(HeightField f, DfWorldMap map, LocationLayout layout, IReadOnlyDictionary<(int, int), Model[]> models, Action<string> log)
    {
        var clear = ByCell(Clearings(layout));
        long cells = 0, flats = 0;
        var kinds = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var missing = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), long>();
        Parallel.For(0, f.CellsY, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, cy =>
        {
            var list = new List<Flat>();
            long c = 0, n = 0;
            for (int cx = 0; cx < f.CellsX; cx++)
            {
                if (!TerrainBuilder.HasLand(f, cx, cy)) continue;
                c++;
                int gx = cx + f.MinCellX, gy = cy + f.MinCellY;
                list.Clear();
                InCell(f, map, gx, gy, clear.GetValueOrDefault((gx, gy)), list);
                foreach (var fl in list)
                {
                    if (models.TryGetValue((fl.Archive, fl.Record), out var m)) { kinds.AddOrUpdate(m[0].Kind.ToString(), 1, (_, v) => v + 1); n++; }
                    else missing.AddOrUpdate((fl.Archive, fl.Record), 1, (_, v) => v + 1);
                }
            }
            Interlocked.Add(ref cells, c); Interlocked.Add(ref flats, n);
        });
        log($"{flats:N0} foliage objects over {cells:N0} cells with land ({flats / (double)Math.Max(1, cells):F1} a cell): {string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:N0}"))}");
        log($"Sprites without a model, left out: {missing.Values.Sum():N0} flats of {missing.Count} sprites{(missing.IsEmpty ? "" : ": " + string.Join(", ", missing.OrderByDescending(m => m.Value).Take(20).Select(m => $"{m.Key.Item1}.{m.Key.Item2} x{m.Value:N0}")))}");
        log($"Estimated {flats * 70 / 1048576.0:N0} MB in {Math.Max(1, (flats + PluginLimit - 1) / PluginLimit)} plugins");
    }

    /// <summary>
    /// Builds the foliage plugins in <paramref name="dir"/> over its land plugins and Daggerfall.esm: each land cell
    /// that gets foliage overridden (its own record copied, without its land and navmesh) with the foliage as its
    /// references, in the land's tiles' coordinates. Returns the plugins' names, in load order.
    /// </summary>
    public static List<string> Build(string dir, string skyrimData, string arena2, HeightField f, DfWorldMap map, LocationLayout layout, Action<string> log)
    {
        var landNames = OverlandTiles.LandPluginsIn(dir);
        if (landNames.Count == 0) throw new InvalidOperationException($"no Daggerfall land plugins in {dir}");
        using var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, "Daggerfall.esm"), SkyrimRelease.SkyrimSE);
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var models = Models(master, skyrim, arena2);
        if (models.Count == 0) throw new InvalidOperationException("Daggerfall.esm has no models made from the nature sprites (DFSpriteModel statics)");
        var clear = ByCell(Clearings(layout));
        var tiles = new OverlandTiles(f);
        var lands = landNames.Select(n => SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, n), SkyrimRelease.SkyrimSE)).ToList();
        try
        {
            // Every land cell, the last plugin's record winning (the last holds the navmeshes over the others' cells),
            // with its world and its global grid.
            var cells = new Dictionary<(FormKey World, int X, int Y), (ICellGetter Cell, IWorldspaceGetter World, int Gx, int Gy)>();
            var origins = Enumerable.Range(0, tiles.Count).ToDictionary(tiles.EditorId, tiles.Origin);
            foreach (var m in lands)
                foreach (var w in m.Worldspaces)
                {
                    if (w.EditorID is not { } id || !origins.TryGetValue(id, out var o)) continue;
                    foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                        if (c.Grid is { } g) cells[(w.FormKey, g.Point.X, g.Point.Y)] = (c, w, g.Point.X + o.X, g.Point.Y + o.Y);
                }
            var order = cells.Values.OrderBy(c => c.World.EditorID).ThenBy(c => c.Gy).ThenBy(c => c.Gx).ToList();
            log($"{order.Count:N0} land cells in {landNames.Count} land plugins");

            // How many objects each cell gets (on all but two cores), to cut the work into pieces and plugins.
            var counts = new int[order.Count];
            Parallel.For(0, order.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) },
                () => new List<Flat>(), (i, _, list) =>
                {
                    list.Clear();
                    InCell(f, map, order[i].Gx, order[i].Gy, clear.GetValueOrDefault((order[i].Gx, order[i].Gy)), list);
                    counts[i] = list.Count(fl => models.ContainsKey((fl.Archive, fl.Record)));
                    return list;
                }, _ => { });
            long total = counts.Sum(c => (long)c);
            int plugins = (int)Math.Clamp((total + PluginLimit - 1) / PluginLimit, 1, PluginNames.Length);
            if (total > (long)plugins * PluginLimit) throw new InvalidOperationException($"{total:N0} foliage objects need more than {PluginNames.Length} plugins");
            // Each plugin a run of cells with an equal share; each plugin's pieces of up to PieceLimit.
            var runs = new List<(int Plugin, int From, int To)>();
            {
                long share = (total + plugins - 1) / plugins, before = 0;
                int p = 0, from = 0;
                for (int i = 0; i < order.Count; i++)
                {
                    before += counts[i];
                    if (p < plugins - 1 && before >= share * (p + 1)) { runs.Add((p, from, i + 1)); from = i + 1; p++; }
                }
                runs.Add((p, from, order.Count));
            }
            var names = new List<string>();
            foreach (var (plugin, from, to) in runs)
            {
                string name = PluginNames[plugin];
                var pieces = new List<(int From, int To)>();
                for (int i = from, start = from, n = 0; i < to; i++)
                {
                    if (n + counts[i] > PieceLimit && i > start) { pieces.Add((start, i)); start = i; n = 0; }
                    n += counts[i];
                    if (i == to - 1) pieces.Add((start, to));
                }
                var masters = PluginMerger.Masters(["Daggerfall.esm", .. landNames]);
                var written = new List<string>();
                for (int k = 0; k < pieces.Count; k++)
                {
                    var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
                    mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
                    PluginMerger.PreparePart(mod, k + 1, pieces.Count);
                    var worlds = new Dictionary<FormKey, Worldspace>();
                    var list = new List<Flat>();
                    int objects = 0;
                    for (int i = pieces[k].From; i < pieces[k].To; i++)
                    {
                        if (counts[i] == 0) continue;
                        var (cell, world, gx, gy) = order[i];
                        if (!worlds.TryGetValue(world.FormKey, out var ws))
                        {
                            ws = world.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
                            ws.SubCells.Clear();
                            ws.TopCell = null;
                            mod.Worldspaces.Add(ws);
                            worlds[world.FormKey] = ws;
                        }
                        var copy = cell.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Persistent = false, Temporary = false, Landscape = false, NavigationMeshes = false });
                        copy.Persistent.Clear(); copy.Temporary.Clear(); copy.NavigationMeshes.Clear(); copy.Landscape = null;
                        var (ox, oy) = origins[world.EditorID!];
                        float sx = -ox * OverlandTiles.CellUnits, sy = -oy * OverlandTiles.CellUnits;
                        list.Clear();
                        InCell(f, map, gx, gy, clear.GetValueOrDefault((gx, gy)), list);
                        foreach (var fl in list)
                        {
                            if (!models.TryGetValue((fl.Archive, fl.Record), out var choices)) continue;
                            var m = choices[fl.Pick % choices.Length];
                            // Its base on the ground (the made models stand on z = 0, SpriteModelImporter), sunk by the slope.
                            copy.Temporary.Add(new PlacedObject(mod)
                            {
                                Base = m.Static.ToNullableLink<IPlaceableObjectGetter>(),
                                Placement = new Placement { Position = new P3Float(fl.Pos.X + sx, fl.Pos.Y + sy, fl.Pos.Z - m.Min.Z * m.Scale), Rotation = new P3Float(0, 0, fl.Heading) },
                                Scale = m.Scale == 1 ? null : m.Scale,
                            });
                            objects++;
                        }
                        TerrainBuilder.AddCell(ws, copy);
                    }
                    string part = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}.part{k + 1}.esm");
                    PluginMerger.WritePart(mod, part, masters, k + 1, pieces.Count);
                    written.Add(part);
                    Progress.Report((k + 1) / (double)pieces.Count, $"Foliage: {name} piece {k + 1} of {pieces.Count}");
                    log($"  {name} piece {k + 1} of {pieces.Count}: {objects:N0} objects in {pieces[k].To - pieces[k].From:N0} cells");
                }
                long records = PluginMerger.Merge(written, Path.Combine(dir, name));
                foreach (var p in written) File.Delete(p);
                log($"{name}: {records:N0} records and groups, {new FileInfo(Path.Combine(dir, name)).Length / 1048576} MB");
                names.Add(name);
            }
            // Foliage plugins an earlier build split differently.
            foreach (var old in PluginNames.Except(names).Select(n => Path.Combine(dir, n)).Where(File.Exists)) File.Delete(old);
            log($"{total:N0} foliage objects in {names.Count} plugins");
            return names;
        }
        finally { foreach (var m in lands) m.Dispose(); }
    }
}
