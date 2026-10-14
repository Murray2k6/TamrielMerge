using System.Collections.Concurrent;
using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim.Distant;

/// <summary>
/// The converted worlds' distant view (user, 2026-10-13: "need a generate lod button for the tool too", "distant lod",
/// everything: land, objects and trees), from the converted plugins in a folder, for every exterior worldspace they
/// add:
/// <list type="bullet">
/// <item>Land: Skyrim/TerrainLod.cs from the cells' landscape (the overland a region at a time).</item>
/// <item>Objects: every placed static big enough to see from afar (a building, a wall, a big rock) as level 4, 8 and
/// 16 chunks (BtoWriter): its base's own distant meshes when it has them (a Skyrim static's), else its model made
/// coarser for the level (DistantModel.Simplified), textured from the world's sheet (DistantAtlas).</item>
/// <item>Trees: in the same chunks (so a loaded cell hides its trees' distant copies with its objects', with no
/// load-order-bound tree list), each as two crossed cards of its picture from the side; fewer at the far levels.</item>
/// </list>
/// Written into --out as Skyrim reads them (meshes\terrain\&lt;world&gt;\objects, textures\terrain\&lt;world&gt;\objects,
/// lodsettings), built on a solid-state drive when --out is on a hard disk (Util.Drives).
/// </summary>
public static class DistantLod
{
    public sealed record What(bool Terrain, bool Objects, bool Trees);

    /// <summary>The least size (half the largest extent, in units) an object needs at each level, and the share of
    /// trees kept.</summary>
    static readonly int[] Levels = [4, 8, 16];
    static float MinObject(int level) => level switch { 4 => 220, 8 => 450, _ => 900 };
    static int TreeEvery(int level) => level switch { 4 => 1, 8 => 2, _ => 4 };
    const float MinTree = 120;

    sealed class Base
    {
        public required string Model;
        public string?[] Lod = new string?[3];
        public bool Tree;
        public float Size;
    }

    readonly record struct Placed(int Base, Vector3 Pos, Vector3 Rot, float Scale);

    public static int Run(string dir, string outDir, string skyrimData, What what, IReadOnlyCollection<string>? onlyWorlds, Action<string> log,
        IReadOnlyCollection<string>? onlyPlugins = null, IReadOnlyCollection<string>? assets = null)
    {
        var plugins = Setup.Parts.All.Take(4).SelectMany(p => Setup.Parts.Files(p, dir)).Select(f => Path.Combine(dir, f)).Where(File.Exists)
            .Where(p => onlyPlugins is null || onlyPlugins.Count == 0 || onlyPlugins.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToList();
        if (plugins.Count == 0) { log($"No converted game plugins in {dir}"); return 1; }
        var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
            .Select(m => Path.Combine(skyrimData, m)).Where(File.Exists).ToList();
        var mods = masters.Concat(plugins).Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        var ours = plugins.Select(p => ModKey.FromFileName(Path.GetFileName(p))).ToHashSet();
        string writeRoot = Util.Drives.FastStaging(outDir, 8L << 30, Path.Combine("TamrielMergeLod", Guid.NewGuid().ToString("N"))) ?? outDir;
        try
        {
            var files = new Files(skyrimData, [outDir, dir, .. assets ?? []]);
            // Every static, tree and movable static, the last plugin's winning.
            var bases = new List<Base>();
            var baseOf = new Dictionary<FormKey, int>();
            void AddBase(FormKey key, string? model, IObjectBoundsGetter? bounds, bool tree, string?[]? lod)
            {
                if (string.IsNullOrEmpty(model)) return;
                float size = bounds is null ? 0 : MathF.Max(bounds.Second.X - bounds.First.X, MathF.Max(bounds.Second.Y - bounds.First.Y, bounds.Second.Z - bounds.First.Z)) / 2f;
                var b = new Base { Model = Mesh(model), Tree = tree || IsTreeModel(model), Size = size };
                if (lod is not null) for (int i = 0; i < 3; i++) b.Lod[i] = string.IsNullOrWhiteSpace(lod[i]) ? null : Mesh(lod[i]!);
                baseOf[key] = bases.Count;
                bases.Add(b);
            }
            foreach (var mod in mods)
            {
                foreach (var s in mod.Statics) AddBase(s.FormKey, s.Model?.File.DataRelativePath.Path, s.ObjectBounds, false, StaticLods(s));
                foreach (var t in mod.Trees) AddBase(t.FormKey, t.Model?.File.DataRelativePath.Path, t.ObjectBounds, true, null);
                foreach (var m in mod.MoveableStatics) AddBase(m.FormKey, m.Model?.File.DataRelativePath.Path, m.ObjectBounds, false, null);
            }
            log($"{bases.Count} statics and trees in {mods.Count} plugins");

            var models = new ConcurrentDictionary<string, DistantModel?>(StringComparer.OrdinalIgnoreCase);
            DistantModel? ModelOf(string path) => models.GetOrAdd(path, p => files.Find(p) is { } bytes ? DistantModel.Load(bytes) : null);
            float SizeOf(Base b)
            {
                if (b.Size <= 0) b.Size = ModelOf(b.Model)?.Radius ?? 0;
                return b.Size;
            }

            var worlds = mods.Where(m => ours.Contains(m.ModKey)).SelectMany(m => m.Worldspaces)
                .Where(w => ours.Contains(w.FormKey.ModKey) && w.EditorID is { Length: > 0 })
                .GroupBy(w => w.FormKey).Select(g => g.First())
                .Where(w => onlyWorlds is null || onlyWorlds.Count == 0 || onlyWorlds.Contains(w.EditorID!, StringComparer.OrdinalIgnoreCase)).ToList();
            // Each worldspace's copies in the plugins, found once (Daggerfall has 3,444 town worldspaces).
            var copies = new Dictionary<FormKey, List<IWorldspaceGetter>>();
            foreach (var mod in mods.Where(m => ours.Contains(m.ModKey)))
                foreach (var w in mod.Worldspaces)
                {
                    if (!copies.TryGetValue(w.FormKey, out var l)) copies[w.FormKey] = l = [];
                    l.Add(w);
                }
            // First, on one thread (the plugins' readers aren't safe to share), each world's land and placements; the
            // land is made world by world (its own work runs on all cores).
            var jobs = new List<(string Name, string Sheet, List<Placed> Placed, IEnumerable<(int X, int Y)> Cells)>();
            int done = 0;
            foreach (var world in worlds)
            {
                string name = world.EditorID!;
                Progress.Report(0.5 * done++ / worlds.Count, $"Distant LOD: reading {name}");
                var land = new Dictionary<(int X, int Y), ICellGetter>();
                var placed = new Dictionary<FormKey, Placed>();
                foreach (var w in copies.GetValueOrDefault(world.FormKey) ?? [])
                {
                    var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                    if (w.TopCell is { } top) cells.Add(top);
                    foreach (var cell in cells)
                    {
                        if (what.Terrain && cell.Landscape is not null && cell.Grid is { } g) land[(g.Point.X, g.Point.Y)] = cell;
                        if (!what.Objects && !what.Trees) continue;
                        foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
                        {
                            // Deleted or initially disabled, or shown by a script's choice (an enable parent): not kept.
                            if ((r.MajorRecordFlagsRaw & 0x820) != 0 || r.EnableParent is not null || r.Placement is not { } pl) { placed.Remove(r.FormKey); continue; }
                            if (!baseOf.TryGetValue(r.Base.FormKey, out int bi)) continue;
                            var b = bases[bi];
                            if (b.Tree ? !what.Trees : !what.Objects) continue;
                            float scale = r.Scale ?? 1;
                            if (SizeOf(b) * scale < (b.Tree ? MinTree : MinObject(4))) continue;
                            placed[r.FormKey] = new Placed(bi, new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z), new Vector3(pl.Rotation.X, pl.Rotation.Y, pl.Rotation.Z), scale);
                        }
                    }
                }
                if (what.Terrain && land.Count > 0) Terrain(name, land, writeRoot, mods, skyrimData, log);
                if ((what.Objects || what.Trees) && placed.Count > 0)
                    jobs.Add((name, SheetOf(world.FormKey.ModKey.FileName), [.. placed.Values], land.Count > 0 ? [.. land.Keys] : placed.Values.Select(p => Cell(p.Pos)).Distinct().ToList()));
            }
            // Then the objects and trees, worlds in parallel, on one texture sheet per game (2026-10-14: a sheet of its own
            // for each of Daggerfall's 3,444 town worlds, one world at a time, took two hours and tens of gigabytes).
            var sheets = jobs.Select(j => j.Sheet).Distinct().ToDictionary(n => n, _ => new DistantAtlas(files.Find));
            int built = 0;
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, job =>
            {
                EnsureSettings(job.Name, job.Cells, outDir, writeRoot);
                Objects(job.Name, job.Placed, bases, ModelOf, sheets[job.Sheet], job.Sheet, writeRoot, outDir, log);
                int n = Interlocked.Increment(ref built);
                Progress.Report(0.5 + 0.5 * n / jobs.Count, $"Distant LOD: {n} of {jobs.Count} worlds");
            });
            foreach (var (sheet, atlas) in sheets)
            {
                foreach (var root in new[] { outDir, writeRoot }.Distinct())
                    if (Path.Combine(root, "textures", "terrain", sheet, "objects") is var sheetDir && Directory.Exists(sheetDir)) Directory.Delete(sheetDir, true);
                var pages = atlas.Write(writeRoot, sheet);
                log($"{sheet}: {pages.Count} texture sheets for its worlds' distant objects and trees");
            }
            if (writeRoot != outDir)
            {
                log($"Moving the distant LOD into {outDir}...");
                Util.Drives.MoveInto(writeRoot, outDir);
            }
            return 0;
        }
        finally
        {
            foreach (var m in mods) m.Dispose();
        }
    }

    static string Mesh(string model)
    {
        string m = model.Replace('/', '\\').TrimStart('\\');
        return m.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? m : "meshes\\" + m;
    }

    /// <summary>A static's own distant meshes (its LOD levels 0-2, as the Creation Kit's LOD generation uses).</summary>
    static string?[]? StaticLods(IStaticGetter s) => s.Lod is { } lod ? [lod.Level0, lod.Level1, lod.Level2] : null;

    /// <summary>A model that reads as a tree from afar: Skyrim's trees, Morrowind's flora_tree_* and parasols,
    /// Oblivion's trees, the made models' trees; not stumps, logs, branch piles or driftwood.</summary>
    static readonly HashSet<string> SpriteTrees = SpriteModels.Targets.Where(t => t.Kind == SpriteModels.Kind.Tree)
        .Select(t => t.Name.ToLowerInvariant()).ToHashSet();

    /// <summary>The most triangles a distant model keeps: a heavier one, even a base's own distant mesh, is made coarser.</summary>
    const int MaxDistantTriangles = 2000;

    static bool IsTreeModel(string model)
    {
        var low = model.Replace('/', '\\').ToLowerInvariant();
        var file = Path.GetFileNameWithoutExtension(low);
        if (file.Contains("stump") || file.Contains("driftwood") || file.Contains("branchpile") || file.Contains("log") || file.Contains("root")) return false;
        // Daggerfall's trees made from its sprites (Scenery504_12 and the like; 2026-10-14 they went in whole, 8,000
        // triangles each, two million in Daggerfall city's nearest distant chunks).
        if (SpriteTrees.Contains(file)) return true;
        return file.Contains("tree") || file.Contains("emp_parasol") || file.Contains("palm") || low.Contains("\\trees\\");
    }

    static (int X, int Y) Cell(Vector3 p) => ((int)MathF.Floor(p.X / 4096f), (int)MathF.Floor(p.Y / 4096f));

    /// <summary>The worldspace's distant settings (lodsettings), when its land didn't write them.</summary>
    static void EnsureSettings(string world, IEnumerable<(int X, int Y)> cells, string outDir, string writeRoot)
    {
        string rel = Path.Combine("lodsettings", world.ToLowerInvariant() + ".lod");
        if (File.Exists(Path.Combine(writeRoot, rel)) || File.Exists(Path.Combine(outDir, rel))) return;
        var list = cells.ToList();
        int FloorTo(int v, int m) => (int)Math.Floor((double)v / m) * m;
        int swX = FloorTo(list.Min(c => c.X), TerrainLod.MaxLevel), swY = FloorTo(list.Min(c => c.Y), TerrainLod.MaxLevel);
        int maxX = list.Max(c => c.X), maxY = list.Max(c => c.Y), stride = TerrainLod.MaxLevel;
        while (swX + stride <= maxX || swY + stride <= maxY) stride *= 2;
        var empty = new SkyrimMod(ModKey.FromFileName("TamrielMergeLod.esp"), SkyrimRelease.SkyrimSE);
        new TerrainLod(empty, writeRoot, "").WriteSettings(world, swX, swY, stride);
    }

    /// <summary>The land, from the winning cells: a big world a 128-cell region at a time (its settings written once).</summary>
    static void Terrain(string world, Dictionary<(int X, int Y), ICellGetter> land, string writeRoot, List<ISkyrimModDisposableGetter> mods, string skyrimData, Action<string> log)
    {
        // Land textures and their texture sets from every plugin, for the land's colours.
        var lookup = new SkyrimMod(ModKey.FromFileName("TamrielMergeLod.esp"), SkyrimRelease.SkyrimSE);
        foreach (var m in mods)
        {
            foreach (var lt in m.LandscapeTextures) lookup.LandscapeTextures.GetOrAddAsOverride(lt);
            foreach (var ts in m.TextureSets) lookup.TextureSets.GetOrAddAsOverride(ts);
        }
        static float? Water(ICellGetter c) => c.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Cell.Flag.HasWater) ? 0f : null;
        var lod = new TerrainLod(lookup, writeRoot, skyrimData);
        const int Region = 128;
        if (land.Count <= Region * Region * 2) lod.Build(world, land, Water, -2048);
        else
        {
            int FloorTo(int v, int m) => (int)Math.Floor((double)v / m) * m;
            int minX = land.Keys.Min(k => k.X), minY = land.Keys.Min(k => k.Y), maxX = land.Keys.Max(k => k.X), maxY = land.Keys.Max(k => k.Y);
            int swX = FloorTo(minX, TerrainLod.MaxLevel), swY = FloorTo(minY, TerrainLod.MaxLevel), stride = TerrainLod.MaxLevel;
            while (swX + stride <= maxX || swY + stride <= maxY) stride *= 2;
            lod.WriteSettings(world, swX, swY, stride);
            for (int x0 = swX; x0 <= maxX; x0 += Region)
                for (int y0 = swY; y0 <= maxY; y0 += Region)
                {
                    var part = new Dictionary<(int X, int Y), ICellGetter>();
                    for (int x = x0 - 1; x <= x0 + Region; x++)
                        for (int y = y0 - 1; y <= y0 + Region; y++)
                            if (land.TryGetValue((x, y), out var c)) part[(x, y)] = c;
                    if (part.Keys.Any(k => k.X >= x0 && k.X < x0 + Region && k.Y >= y0 && k.Y < y0 + Region))
                        lod.Build(world, part, Water, -2048, (x0, y0, x0 + Region - 1, y0 + Region - 1));
                }
        }
        log($"  {world}: {lod.Chunks} land chunks{(lod.Problems.Count > 0 ? $"; {lod.Problems.Count} problems, first: {lod.Problems[0]}" : "")}");
    }

    /// <summary>A model's shapes at one level, on the sheet: positions, normals, sheet UVs and triangles per (page, cut-out).</summary>
    sealed class Ready
    {
        public readonly List<(int Page, bool Cutout, Vector3[] P, Vector3[] N, Vector2[] UV, int[] T)> Parts = [];
    }

    /// <summary>The texture sheet a world's distant objects share: one per game, named for it.</summary>
    static string SheetOf(string plugin)
    {
        foreach (var game in new[] { "Daggerfall", "Redguard", "Morrowind", "Oblivion" })
            if (plugin.StartsWith(game, StringComparison.OrdinalIgnoreCase)) return "TamrielMerge" + game;
        return "TamrielMerge" + Path.GetFileNameWithoutExtension(plugin).Replace(" ", "");
    }

    static void Objects(string world, List<Placed> placed, List<Base> bases, Func<string, DistantModel?> modelOf, DistantAtlas atlas, string sheet, string writeRoot, string outDir, Action<string> log)
    {
        // The world's old chunks, and a sheet of its own from before the sheets were shared.
        foreach (var sub in new[] { Path.Combine("meshes", "terrain", world, "objects"), Path.Combine("textures", "terrain", world, "objects") })
            foreach (var root in new[] { outDir, writeRoot }.Distinct())
                if (Directory.Exists(Path.Combine(root, sub))) Directory.Delete(Path.Combine(root, sub), true);
        var ready = new Dictionary<(int Base, int Level), Ready?>();

        Ready? Prepare(int bi, int li)
        {
            if (ready.TryGetValue((bi, li), out var hit)) return hit;
            var b = bases[bi];
            Ready? r = null;
            if (b.Tree)
            {
                // One picture for all levels.
                if (li > 0) return ready[(bi, li)] = Prepare(bi, 0);
                if (modelOf(b.Model) is { } m)
                {
                    var px = atlas.Billboard(m, DistantAtlas.Inner, out float w, out float z0, out float z1, out var c);
                    var (page, map) = atlas.Picture(px, DistantAtlas.Inner, DistantAtlas.Inner);
                    r = new Ready();
                    var P = new List<Vector3>(); var UV = new List<Vector2>(); var T = new List<int>();
                    foreach (var (dx, dy) in new[] { (1f, 0f), (0f, 1f) })
                    {
                        int k = P.Count;
                        var half = new Vector3(dx, dy, 0) * w / 2;
                        var mid = new Vector3(c.X, c.Y, 0);
                        P.Add(mid - half + new Vector3(0, 0, z0)); UV.Add(map(new Vector2(0, 1)));
                        P.Add(mid + half + new Vector3(0, 0, z0)); UV.Add(map(new Vector2(1, 1)));
                        P.Add(mid + half + new Vector3(0, 0, z1)); UV.Add(map(new Vector2(1, 0)));
                        P.Add(mid - half + new Vector3(0, 0, z1)); UV.Add(map(new Vector2(0, 0)));
                        T.AddRange([k, k + 1, k + 2, k, k + 2, k + 3]);
                    }
                    r.Parts.Add((page, true, [.. P], Enumerable.Repeat(new Vector3(0, 0, 1), P.Count).ToArray(), [.. UV], [.. T]));
                }
                return ready[(bi, li)] = r;
            }
            // The base's own distant mesh for the level when it has one, else its model made coarser.
            DistantModel? model = b.Lod[li] is { } own ? modelOf(own) : null;
            float grid(DistantModel m) => m.Radius * (Levels[li] switch { 4 => 1 / 14f, 8 => 1 / 8f, _ => 1 / 5f });
            if (model is null && modelOf(b.Model) is { } full)
                model = full.Simplified(grid(full), dropCutouts: li > 0);
            // Still too heavy (a mesh made from a sprite, a detailed own distant mesh): coarser until it isn't.
            for (int pass = 0; model is not null && pass < 4 && model.Parts.Sum(pt => pt.T.Length / 3) > MaxDistantTriangles; pass++)
                model = model.Simplified(grid(model) * (pass + 2), dropCutouts: li > 0);
            if (model is not null)
            {
                r = new Ready();
                foreach (var part in model.Parts)
                {
                    if (part.T.Length == 0) continue;
                    bool inside = part.UV.All(u => u.X >= -0.02f && u.X <= 1.02f && u.Y >= -0.02f && u.Y <= 1.02f);
                    if (inside && atlas.Image(part.Texture) is { } img)
                        r.Parts.Add((img.Page, part.Cutout, part.P, part.N, part.UV.Select(img.Map).ToArray(), part.T));
                    else if (!part.Cutout)
                    {
                        var (page, uv) = atlas.Colour(part.Texture);
                        r.Parts.Add((page, false, part.P, part.N, Enumerable.Repeat(uv, part.P.Length).ToArray(), part.T));
                    }
                }
                if (r.Parts.Count == 0) r = null;
            }
            return ready[(bi, li)] = r;
        }

        // Which placements go at each level.
        bool Keeps(Placed p, int li, int index)
        {
            var b = bases[p.Base];
            if (b.Tree) return index % TreeEvery(Levels[li]) == 0;
            return b.Size * p.Scale >= MinObject(Levels[li]);
        }
        // Everything onto the sheet first (the chunks name its pages; the sheet is written once all worlds are done).
        for (int i = 0; i < placed.Count; i++)
            for (int li = 0; li < Levels.Length; li++)
                if (Keeps(placed[i], li, i)) Prepare(placed[i].Base, li);

        // TAMRIELMERGE_LOD_STATS=1: which models make the most triangles, level by level.
        if (Environment.GetEnvironmentVariable("TAMRIELMERGE_LOD_STATS") == "1")
            for (int li = 0; li < Levels.Length; li++)
            {
                var tris = new Dictionary<int, (long Tris, int Count)>();
                for (int i = 0; i < placed.Count; i++)
                    if (Keeps(placed[i], li, i) && ready.GetValueOrDefault((placed[i].Base, li)) is { } r)
                    {
                        var t = tris.GetValueOrDefault(placed[i].Base);
                        tris[placed[i].Base] = (t.Tris + r.Parts.Sum(pt => pt.T.Length / 3), t.Count + 1);
                    }
                log($"  {world} level {Levels[li]}: {tris.Values.Sum(t => t.Tris):N0} triangles; most: " +
                    string.Join(", ", tris.OrderByDescending(kv => kv.Value.Tris).Take(8).Select(kv => $"{Path.GetFileName(bases[kv.Key].Model)}{(bases[kv.Key].Tree ? " (tree)" : "")} x{kv.Value.Count} = {kv.Value.Tris:N0}")));
            }

        int chunks = 0;
        var errors = new ConcurrentBag<string>();
        for (int li = 0; li < Levels.Length; li++)
        {
            int level = Levels[li];
            var byChunk = new Dictionary<(int X, int Y), List<int>>();
            for (int i = 0; i < placed.Count; i++)
            {
                if (!Keeps(placed[i], li, i) || ready.GetValueOrDefault((placed[i].Base, li)) is null) continue;
                var (cx, cy) = Cell(placed[i].Pos);
                var key = ((int)Math.Floor((double)cx / level) * level, (int)Math.Floor((double)cy / level) * level);
                if (!byChunk.TryGetValue(key, out var list)) byChunk[key] = list = [];
                list.Add(i);
            }
            Parallel.ForEach(byChunk, kv =>
            {
                var (x0, y0) = kv.Key;
                var shapes = new Dictionary<(int Page, bool Cutout), BtoWriter.Shape>();
                foreach (int i in kv.Value)
                {
                    var p = placed[i];
                    var r = ready[(p.Base, li)]!;
                    var (cx, cy) = Cell(p.Pos);
                    int segment = level == 4 ? (cx - x0) * 4 + (cy - y0) : 0;
                    var rot = bases[p.Base].Tree
                        ? Matrix4x4.CreateRotationZ(-p.Rot.Z)
                        : Matrix4x4.CreateRotationX(-p.Rot.X) * Matrix4x4.CreateRotationY(-p.Rot.Y) * Matrix4x4.CreateRotationZ(-p.Rot.Z);
                    var m = rot * Matrix4x4.CreateScale(p.Scale);
                    var origin = new Vector3(x0 * 4096f, y0 * 4096f, 0);
                    foreach (var part in r.Parts)
                    {
                        if (!shapes.TryGetValue((part.Page, part.Cutout), out var s)) shapes[(part.Page, part.Cutout)] = s = new BtoWriter.Shape { Page = part.Page, Cutout = part.Cutout };
                        int k = s.P.Count;
                        for (int v = 0; v < part.P.Length; v++)
                        {
                            var w = Vector3.Transform(part.P[v], m) + p.Pos;
                            s.P.Add((w - origin) / level);
                            var n = Vector3.TransformNormal(part.N[v], rot);
                            s.N.Add(n.LengthSquared() > 0 ? Vector3.Normalize(n) : new Vector3(0, 0, 1));
                            s.UV.Add(part.UV[v]);
                        }
                        for (int t = 0; t < part.T.Length; t += 3)
                        {
                            s.T.Add(k + part.T[t]); s.T.Add(k + part.T[t + 1]); s.T.Add(k + part.T[t + 2]);
                            s.Segment.Add(segment);
                        }
                    }
                }
                string path = Path.Combine(writeRoot, "meshes", "terrain", world, "objects", $"{world}.{level}.{x0}.{y0}.bto");
                try { BtoWriter.Write(path, sheet, level, [.. shapes.Values]); Interlocked.Increment(ref chunks); }
                catch (Exception e) { errors.Add($"{Path.GetFileName(path)}: {e.Message}"); }
            });
        }
        log($"  {world}: {chunks} object chunks, {ready.Count(r => r.Value is not null)} distant models{(errors.IsEmpty ? "" : $"; {errors.Count} failed, first: {errors.First()}")}");
    }

    /// <summary>Files by Data-relative path: the first loose copy (the output, then Skyrim's Data), else Skyrim's archives.</summary>
    sealed class Files
    {
        readonly string[] loose;
        readonly Dictionary<string, IArchiveFile> archived = new(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new();

        public Files(string skyrimData, IEnumerable<string> looseDirs)
        {
            loose = [.. looseDirs.Distinct(StringComparer.OrdinalIgnoreCase), skyrimData];
            var names = new[] { "Skyrim - ", "Update", "Dawnguard", "HearthFires", "Dragonborn" };
            foreach (var p in Directory.GetFiles(skyrimData, "*.bsa").Where(p => names.Any(n => Path.GetFileName(p).StartsWith(n, StringComparison.OrdinalIgnoreCase))))
                foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, p).Files) archived.TryAdd(f.Path.Replace('/', '\\'), f);
        }

        public byte[]? Find(string dataRelative)
        {
            string rel = dataRelative.Replace('/', '\\').TrimStart('\\');
            foreach (var dir in loose)
            {
                string p = Path.Combine(dir, rel);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            lock (gate) return archived.TryGetValue(rel, out var f) ? f.GetBytes() : null;
        }
    }
}
