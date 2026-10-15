using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim.Models;

/// <summary>
/// The model viewer and replacer (user, 2026-10-08: "a model viewer and model replacer by viewing models in the tool";
/// replacements "from any of the user's games"). Lists every model the converted plugins use, every model the user's
/// games offer in Skyrim's form (Skyrim's own, and Daggerfall's, Redguard's, Morrowind's and Oblivion's as converted),
/// keeps the user's choices (model-overrides.json beside the plugins), and turns them into a small plugin
/// (TamrielMerge Models.esp, ESL) that points every record using a replaced model at its replacement: no file is
/// overwritten, and leaving the plugin out undoes it.
/// </summary>
public static class ModelCatalog
{
    public const string PluginName = "TamrielMerge Models.esp";
    public const string OverridesFile = "model-overrides.json";

    public sealed record UsedModel(string Model, string Game, string Kind, int Records, string Example, string Plugin);
    public sealed record AvailableModel(string Model, string Game);

    /// <summary>A model path as records hold it (relative to meshes\, backslashes, lower case for comparing).</summary>
    public static string Key(string model)
    {
        string m = model.Replace('/', '\\').TrimStart('\\');
        if (m.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) m = m[7..];
        return m.ToLowerInvariant();
    }

    /// <summary>The game a model belongs to, by where the converter puts each game's meshes.</summary>
    public static string GameOf(string model)
    {
        string k = Key(model);
        if (!k.StartsWith(@"tamrielmerge\")) return "Skyrim";
        string sub = k[13..];
        return sub.StartsWith(@"mw\") ? "Morrowind" : sub.StartsWith(@"ob\") ? "Oblivion" : sub.StartsWith(@"rg\") ? "Redguard"
            : sub.StartsWith(@"df\") || sub.StartsWith(@"towns\") || sub.StartsWith(@"dungeons\") ? "Daggerfall" : "TamrielMerge";
    }

    static string GameOfPlugin(string plugin) =>
        plugin.StartsWith("Daggerfall", StringComparison.OrdinalIgnoreCase) ? "Daggerfall"
        : plugin.StartsWith("Redguard", StringComparison.OrdinalIgnoreCase) ? "Redguard"
        : plugin.StartsWith("Morrowind", StringComparison.OrdinalIgnoreCase) ? "Morrowind"
        : plugin.StartsWith("Oblivion", StringComparison.OrdinalIgnoreCase) ? "Oblivion" : "TamrielMerge";

    /// <summary>The converted plugins in <paramref name="dir"/> (not build parts, not the models plugin itself).</summary>
    public static List<string> Plugins(string dir) =>
        Directory.GetFiles(dir, "*.es?").Where(p => p.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).Contains(".part", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).Equals(PluginName, StringComparison.OrdinalIgnoreCase)
                        && !Terrain.OverlandTiles.LandPluginNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p).ToList();

    /// <summary>
    /// Every model the converted plugins in <paramref name="dir"/> use: the game whose plugin uses it, the kind of
    /// record (Static, Door, Container...), how many records use it and one of their names. Cached in
    /// models-index.json beside the plugins until a plugin changes.
    /// </summary>
    public static List<UsedModel> Used(string dir, Action<string>? progress = null)
    {
        var plugins = Plugins(dir);
        string cache = Path.Combine(dir, "models-index.json");
        string stamp = string.Join("|", plugins.Select(p => $"{Path.GetFileName(p)}:{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}"));
        if (File.Exists(cache))
            try
            {
                var c = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(cache));
                if (c?.Stamp == stamp) return c.Models;
            }
            catch { }
        var found = new Dictionary<string, (string Model, string Game, HashSet<string> Kinds, int Count, string Example, string Plugin)>();
        foreach (var p in plugins)
        {
            progress?.Invoke($"reading {Path.GetFileName(p)}");
            using var mod = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
            string game = GameOfPlugin(Path.GetFileName(p));
            foreach (var r in mod.EnumerateMajorRecords())
            {
                if (r is not IModeledGetter { Model.File: var file } || string.IsNullOrEmpty(file.DataRelativePath.Path)) continue;
                string model = file.DataRelativePath.Path;
                string k = Key(model);
                string kind = r.GetType().Name.Replace("BinaryOverlay", "");
                if (found.TryGetValue(k, out var e)) { e.Kinds.Add(kind); found[k] = e with { Count = e.Count + 1 }; }
                else found[k] = (model, game, [kind], 1, r.EditorID ?? r.FormKey.ToString(), Path.GetFileName(p));
            }
        }
        var list = found.Values.Select(e => new UsedModel(e.Model, e.Game, string.Join(", ", e.Kinds.Order()), e.Count, e.Example, e.Plugin))
            .OrderBy(m => m.Game).ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase).ToList();
        try { File.WriteAllText(cache, JsonSerializer.Serialize(new IndexCache(stamp, list))); } catch { }
        return list;
    }

    /// <summary>A model's extent (loose files first, then the archives), as a record's object bounds; null if unreadable.</summary>
    public static ObjectBounds? BoundsOf(string model, IEnumerable<string> looseMeshDirs, IReadOnlyList<IArchiveReader> archives)
    {
        string k = Key(model);
        byte[]? bytes = null;
        foreach (var d in looseMeshDirs)
            if (File.Exists(Path.Combine(d, k))) { bytes = File.ReadAllBytes(Path.Combine(d, k)); break; }
        if (bytes is null)
            foreach (var a in archives)
                if (a.Files.FirstOrDefault(f => f.Path.Equals(@"meshes\" + k, StringComparison.OrdinalIgnoreCase)) is { } f) { bytes = f.GetBytes(); break; }
        if (bytes is null) return null;
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var nif = new nifly.NifFile();
            if (nif.Load(tmp) != 0) return null;
            float[] lo = [float.MaxValue, float.MaxValue, float.MaxValue], hi = [float.MinValue, float.MinValue, float.MinValue];
            foreach (var shape in nif.GetShapes())
            {
                var chain = new List<nifly.MatTransform> { shape.transform };
                for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent)) chain.Add(parent.transform);
                foreach (var v in nif.GetVertsForShape(shape))
                {
                    var g = v;
                    foreach (var t in chain) g = t.ApplyTransform(g);
                    lo[0] = Math.Min(lo[0], g.x); lo[1] = Math.Min(lo[1], g.y); lo[2] = Math.Min(lo[2], g.z);
                    hi[0] = Math.Max(hi[0], g.x); hi[1] = Math.Max(hi[1], g.y); hi[2] = Math.Max(hi[2], g.z);
                }
            }
            if (lo[0] > hi[0]) return null;
            static short S(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
            return new ObjectBounds { First = new Noggog.P3Int16(S(lo[0]), S(lo[1]), S(lo[2])), Second = new Noggog.P3Int16(S(hi[0]), S(hi[1]), S(hi[2])) };
        }
        catch { return null; }
        finally { File.Delete(tmp); }
    }

    sealed record IndexCache(string Stamp, List<UsedModel> Models);

    /// <summary>
    /// Every model the user's games offer in Skyrim's form: Skyrim's archives and loose meshes in Data (Skyrim's own,
    /// and the converted games' installed under meshes\TamrielMerge), and the converted games' meshes in
    /// <paramref name="dir"/> not installed yet.
    /// </summary>
    public static List<AvailableModel> Available(string skyrimData, string dir)
    {
        var seen = new Dictionary<string, AvailableModel>();
        void Add(string model)
        {
            if (!model.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) return;
            string k = Key(model);
            if (k.StartsWith(@"interface\") || k.Contains(@"\lod\") || k.StartsWith(@"terrain\")) return;
            seen.TryAdd(k, new AvailableModel(model.Replace('/', '\\'), GameOf(model)));
        }
        foreach (var a in Directory.GetFiles(skyrimData, "*.bsa"))
        {
            try { foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, a).Files) if (f.Path.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) Add(f.Path[7..]); }
            catch { }
        }
        foreach (var root in new[] { Path.Combine(skyrimData, "meshes"), Path.Combine(dir, "meshes") })
            if (Directory.Exists(root))
                foreach (var f in Directory.EnumerateFiles(root, "*.nif", SearchOption.AllDirectories)) Add(Path.GetRelativePath(root, f));
        return seen.Values.OrderBy(m => m.Game == "Skyrim" ? 1 : 0).ThenBy(m => m.Game).ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The user's replacements: used model (as records hold it) -> replacement model.</summary>
    public static Dictionary<string, string> LoadOverrides(string dir)
    {
        string f = Path.Combine(dir, OverridesFile);
        if (!File.Exists(f)) return new(StringComparer.OrdinalIgnoreCase);
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) ?? [];
        return new(d, StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveOverrides(string dir, Dictionary<string, string> overrides) =>
        File.WriteAllText(Path.Combine(dir, OverridesFile), JsonSerializer.Serialize(overrides.OrderBy(k => k.Key).ToDictionary(), new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Writes TamrielMerge Models.esp into <paramref name="dir"/>: every record of the converted plugins whose model is
    /// replaced, overridden with the replacement's path and bounds, and every record whose (final) model has texture
    /// swaps (TextureTools), given a texture set per swapped part as Skyrim's alternate textures. Deleted when nothing
    /// is replaced. Returns how many records it changes.
    /// </summary>
    public static int Apply(string dir, string skyrimData)
    {
        string path = Path.Combine(dir, PluginName);
        var overrides = LoadOverrides(dir).ToDictionary(kv => Key(kv.Key), kv => Key(kv.Value));
        var textures = TextureTools.LoadOverrides(dir);
        if (overrides.Count == 0 && textures.Count == 0) { if (File.Exists(path)) File.Delete(path); return 0; }
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var files = TextureTools.For(dir, skyrimData);
        // One texture set per (model, part) with swaps: the part's own textures, the chosen slots replaced.
        var sets = new Dictionary<(string, int), TextureSet>();
        var shapesOf = new Dictionary<string, List<TextureTools.ShapeInfo>>();
        List<AlternateTexture>? Swaps(string model)
        {
            if (!textures.TryGetValue(model, out var parts) || parts.Count == 0) return null;
            if (!shapesOf.TryGetValue(model, out var shapes)) shapesOf[model] = shapes = TextureTools.Shapes(model, files);
            var list = new List<AlternateTexture>();
            foreach (var (index, slots) in parts)
            {
                if (index >= shapes.Count || slots.Count == 0) continue;
                if (!sets.TryGetValue((model, index), out var set))
                {
                    var t = (string[])shapes[index].Textures.Clone();
                    foreach (var (slot, tex) in slots) if (slot is >= 0 and < 8) t[slot] = tex;
                    string Tx(int k)
                    {
                        string v = t[k].Replace('/', '\\');
                        return v.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? v[9..] : v;
                    }
                    set = mod.TextureSets.AddNew($"TMSwap{sets.Count:0000}");
                    set.ObjectBounds = new ObjectBounds();
                    // A texture set's order differs from a model's: env mask 2, glow 3, height 4, cube 5.
                    if (t[0].Length > 0) set.Diffuse = Tx(0);
                    if (t[1].Length > 0) set.NormalOrGloss = Tx(1);
                    if (t[5].Length > 0) set.EnvironmentMaskOrSubsurfaceTint = Tx(5);
                    if (t[2].Length > 0) set.GlowOrDetailMap = Tx(2);
                    if (t[3].Length > 0) set.Height = Tx(3);
                    if (t[4].Length > 0) set.Environment = Tx(4);
                    if (t[6].Length > 0) set.Multilayer = Tx(6);
                    if (t[7].Length > 0) set.BacklightMaskOrSpecular = Tx(7);
                    sets[(model, index)] = set;
                }
                list.Add(new AlternateTexture { Name = shapes[index].Name, Index = index, NewTexture = set.ToLink() });
            }
            return list.Count > 0 ? list : null;
        }
        var bounds = new Dictionary<string, ObjectBounds?>();
        List<IArchiveReader>? archives = null;
        int n = 0;
        var sources = new List<ISkyrimModDisposableGetter>();
        try
        {
            foreach (var p in Plugins(dir))
            {
                var src = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
                sources.Add(src);
                foreach (var r in src.EnumerateMajorRecords())
                {
                    if (r is not IModeledGetter { Model.File: var file }) continue;
                    string from = Key(file.DataRelativePath.Path);
                    bool replaced = overrides.TryGetValue(from, out var to);
                    var swaps = Swaps(replaced ? to! : from);
                    if (!replaced && swaps is null) continue;
                    var copy = (IMajorRecord)((IMajorRecordGetter)r).DeepCopy();
                    if (copy is not IModeled { Model: { } model }) continue;
                    if (replaced) model.File = to!;
                    // The old model's texture swaps don't fit a replacement; the user's swaps for the final model do.
                    model.AlternateTextures = swaps is null ? null : new Noggog.ExtendedList<AlternateTexture>(swaps);
                    // The record's bounds (culling, placing) from the replacement's own size.
                    if (replaced && copy is IObjectBounded ob)
                    {
                        if (!bounds.TryGetValue(to!, out var b))
                            bounds[to!] = b = BoundsOf(to!, [Path.Combine(dir, "meshes"), Path.Combine(skyrimData, "meshes")],
                                archives ??= Directory.GetFiles(skyrimData, "*.bsa").Select(a => { try { return Archive.CreateReader(GameRelease.SkyrimSE, a); } catch { return null; } }).OfType<IArchiveReader>().ToList());
                        if (b is not null) ob.ObjectBounds = b;
                    }
                    mod.GetTopLevelGroup(copy.GetType()).AddUntyped(copy);
                    n++;
                }
            }
            if (n == 0) { if (File.Exists(path)) File.Delete(path); return 0; }
            mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
            TopicSubtypes.Fill(mod);
            mod.WriteToBinary(path);
        }
        finally { foreach (var s in sources) s.Dispose(); }
        return n;
    }
}
