using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using nifly;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim.Models;

/// <summary>
/// The model viewer's texture selector and PBR tool (user, 2026-10-08): each part of a model with its texture slots;
/// any slot can take a texture from any of the user's games (kept in texture-overrides.json and applied by
/// ModelCatalog.Apply as texture sets on the records, Skyrim's own per-record swap: no file changes); and a part's
/// colour texture can be made into a PBR set (tools/textures/pbr.py) baked for Skyrim's own shaders onto a copy of the
/// model (meshes\TamrielMerge\pbr), which the record then uses.
/// </summary>
public static class TextureTools
{
    public const string OverridesFile = "texture-overrides.json";

    /// <summary>Skyrim's texture slots, by their place in a model's texture set.</summary>
    public static readonly string[] SlotNames = ["Colour", "Normals (specular in alpha)", "Glow", "Height (parallax)", "Environment cube", "Environment mask", "Multilayer / tint", "Backlight / specular"];

    public sealed record ShapeInfo(int Index, string Name, uint ShaderType, string[] Textures);
    public sealed record TextureEntry(string Texture, string Game);

    /// <summary>Where files are looked for: loose folders first (meshes\ or textures\ under each), then the archives.</summary>
    public sealed class Files(IEnumerable<string> looseRoots, string skyrimData)
    {
        readonly List<string> roots = looseRoots.ToList();
        List<IArchiveReader>? archives;
        IReadOnlyList<IArchiveReader> Archives => archives ??= Directory.GetFiles(skyrimData, "*.bsa")
            .Select(a => { try { return Archive.CreateReader(GameRelease.SkyrimSE, a); } catch { return null; } }).OfType<IArchiveReader>().ToList();

        /// <summary>A Data-relative file (e.g. textures\x.dds), or null.</summary>
        public byte[]? Read(string dataRelative)
        {
            string rel = dataRelative.Replace('/', '\\').TrimStart('\\');
            foreach (var r in roots)
                if (File.Exists(Path.Combine(r, rel))) return File.ReadAllBytes(Path.Combine(r, rel));
            foreach (var a in Archives)
                if (a.Files.FirstOrDefault(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)) is { } f) return f.GetBytes();
            return null;
        }

        public IReadOnlyList<IArchiveReader> AllArchives => Archives;
    }

    public static Files For(string dir, string skyrimData) => new([dir, skyrimData], skyrimData);

    static string Rel(string path, string folder)
    {
        string p = path.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(folder, p);
    }

    /// <summary>A model loaded from wherever it is (the caller disposes it and deletes the temporary file).</summary>
    static (NifFile Nif, string Temp)? Load(string model, Files files)
    {
        var bytes = files.Read(Rel(model, "meshes"));
        if (bytes is null) return null;
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        var nif = new NifFile();
        if (nif.Load(tmp) != 0) { nif.Dispose(); File.Delete(tmp); return null; }
        return (nif, tmp);
    }

    /// <summary>The model's parts (in its own order, the index records use) with their eight texture slots.</summary>
    public static List<ShapeInfo> Shapes(string model, Files files)
    {
        var loaded = Load(model, files);
        if (loaded is not { } l) return [];
        try
        {
            var list = new List<ShapeInfo>();
            int i = 0;
            foreach (var shape in l.Nif.GetShapes())
            {
                uint type = 0;
                var hdr = l.Nif.GetHeader();
                // nifly hands shaders back as their base type: the block's type name tells which.
                if (l.Nif.GetShader(shape) is { } sh && hdr.GetBlockTypeStringById(hdr.GetBlockID(sh)) == "BSLightingShaderProperty")
                    BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(l.Nif, hdr.GetBlockID(sh), sp => type = sp.GetShaderType());
                var tex = Enumerable.Range(0, 8).Select(k => l.Nif.GetTexturePathByIndex(shape, (uint)k) ?? "").ToArray();
                list.Add(new ShapeInfo(i++, shape.name.get() ?? "", type, tex));
            }
            return list;
        }
        finally { l.Nif.Dispose(); File.Delete(l.Temp); }
    }

    /// <summary>Every colour/detail texture the user's games offer (Skyrim's archives and Data, the converted games'),
    /// with its game. Normal maps and other helper maps are listed too, marked by their suffix.</summary>
    public static List<TextureEntry> Available(string skyrimData, string dir)
    {
        var seen = new Dictionary<string, TextureEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string t)
        {
            if (!t.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return;
            string k = t.Replace('/', '\\');
            if (k.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase)) k = k[9..];
            if (k.StartsWith(@"interface\", StringComparison.OrdinalIgnoreCase) || k.Contains(@"\lod\", StringComparison.OrdinalIgnoreCase) || k.StartsWith(@"terrain\", StringComparison.OrdinalIgnoreCase)) return;
            seen.TryAdd(k, new TextureEntry(k, ModelCatalog.GameOf(k)));
        }
        foreach (var a in Directory.GetFiles(skyrimData, "*.bsa"))
            try { foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, a).Files) if (f.Path.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase)) Add(f.Path); }
            catch { }
        foreach (var root in new[] { Path.Combine(skyrimData, "textures"), Path.Combine(dir, "textures") })
            if (Directory.Exists(root))
                foreach (var f in Directory.EnumerateFiles(root, "*.dds", SearchOption.AllDirectories)) Add(Path.GetRelativePath(root, f));
        return seen.Values.OrderBy(t => t.Game == "Skyrim" ? 1 : 0).ThenBy(t => t.Game).ThenBy(t => t.Texture, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A texture written out as a file (for pictures and the PBR tool); false if it can't be found.</summary>
    public static bool Extract(string texture, Files files, string outFile)
    {
        var bytes = files.Read(Rel(texture, "textures"));
        if (bytes is null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllBytes(outFile, bytes);
        return true;
    }

    /// <summary>The user's texture swaps: model -> part index -> slot -> texture (textures\-relative).</summary>
    public static Dictionary<string, Dictionary<int, Dictionary<int, string>>> LoadOverrides(string dir)
    {
        string f = Path.Combine(dir, OverridesFile);
        if (!File.Exists(f)) return new(StringComparer.OrdinalIgnoreCase);
        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, Dictionary<int, string>>>>(File.ReadAllText(f)) ?? [];
        return new(raw.ToDictionary(kv => ModelCatalog.Key(kv.Key), kv => kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveOverrides(string dir, Dictionary<string, Dictionary<int, Dictionary<int, string>>> o) =>
        File.WriteAllText(Path.Combine(dir, OverridesFile), JsonSerializer.Serialize(
            o.Where(kv => kv.Value.Any(s => s.Value.Count > 0)).ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => s.Value.Count > 0).ToDictionary()),
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Bakes a PBR material (pbr.py's output folder: baked_d/n/p/m.dds and material.json) onto part
    /// <paramref name="shapeIndex"/> of <paramref name="model"/>: a copy of the model under meshes\TamrielMerge\pbr in
    /// <paramref name="dir"/> (a copy made before is built on, so several parts can be done), the textures beside it
    /// under textures\TamrielMerge\pbr, the part's shader switched to Skyrim's environment-map shader (reflections) or
    /// parallax shader (height) with its glossiness and specular from the roughness. Returns the copy's model path,
    /// which <paramref name="original"/> is then replaced with (model-overrides.json).
    /// </summary>
    public static string BakePbr(string original, string model, int shapeIndex, string materialDir, string dir, Files files)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(ModelCatalog.Key(original))))[..12].ToLowerInvariant();
        string copyRel = $@"tamrielmerge\pbr\{hash}.nif";
        string copyFile = Path.Combine(dir, "meshes", copyRel);
        // Build on an earlier PBR copy of this model (other parts done before), else on the model itself.
        string source = File.Exists(copyFile) ? copyRel : model;
        var loaded = (File.Exists(copyFile) ? Load(copyRel, new Files([dir], "")) : Load(model, files)) ?? throw new FileNotFoundException($"can't read {source}");
        var mat = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(materialDir, "material.json")));
        bool parallax = mat.GetProperty("parallax").GetBoolean(), reflections = mat.GetProperty("reflections").GetBoolean();
        float gloss = mat.GetProperty("glossiness").GetSingle(), spec = mat.GetProperty("specular_strength").GetSingle();
        string texDir = Path.Combine(dir, "textures", "tamrielmerge", "pbr");
        Directory.CreateDirectory(texDir);
        string Tex(string suffix)
        {
            string name = $"{hash}_{shapeIndex}_{suffix}.dds";
            File.Copy(Path.Combine(materialDir, $"baked_{suffix}.dds"), Path.Combine(texDir, name), true);
            return $@"textures\tamrielmerge\pbr\{name}";
        }
        var (nif, temp) = loaded;
        try
        {
            var hdr = nif.GetHeader();
            var shape = nif.GetShapes().ElementAtOrDefault(shapeIndex) ?? throw new ArgumentException($"{source} has no part {shapeIndex}");
            var shader = nif.GetShader(shape);
            if (shader is null || hdr.GetBlockTypeStringById(hdr.GetBlockID(shader)) != "BSLightingShaderProperty")
                throw new InvalidDataException("that part doesn't use Skyrim's lighting shader (glows, effects and water can't take a PBR material)");
            nif.SetTextureSlot(shape, Tex("d"), 0);
            nif.SetTextureSlot(shape, Tex("n"), 1);
            // One of Skyrim's two: reflections (metal, polish) win over parallax when both are asked for.
            bool env = reflections && File.Exists(Path.Combine(materialDir, "baked_m.dds"));
            bool par = !env && parallax && File.Exists(Path.Combine(materialDir, "baked_p.dds"));
            if (env)
            {
                bool metal = mat.GetProperty("params").GetProperty("metal").GetSingle() > 0.3f;
                nif.SetTextureSlot(shape, metal ? @"textures\cubemaps\shinycontrast_e.dds" : @"textures\cubemaps\shinydullds_e.dds", 4);
                nif.SetTextureSlot(shape, Tex("m"), 5);
                nif.SetTextureSlot(shape, "", 3);
            }
            else if (par)
            {
                nif.SetTextureSlot(shape, Tex("p"), 3);
                nif.SetTextureSlot(shape, "", 4); nif.SetTextureSlot(shape, "", 5);
            }
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(shader), s =>
            {
                s.SetShaderType(env ? 1u : par ? 3u : 0u);
                // Specular on; environment mapping (bit 7) or parallax (bit 11) as chosen; model-space normals off.
                uint f1 = (s.shaderFlags1 | 0x1u) & ~0x1000u;
                f1 = env ? f1 | 0x80u : f1 & ~0x80u;
                f1 = par ? f1 | 0x800u : f1 & ~0x800u;
                s.shaderFlags1 = f1;
                s.glossiness = gloss;
                s.specularStrength = spec;
                s.specularColor = new nifly.Vector3(1, 1, 1);
                if (env) s.environmentMapScale = 1;
            });
            Directory.CreateDirectory(Path.GetDirectoryName(copyFile)!);
            string tmpOut = copyFile + ".tmp";
            if (nif.Save(tmpOut) != 0) throw new IOException($"couldn't save {copyFile}");
            File.Move(tmpOut, copyFile, true);
        }
        finally { nif.Dispose(); File.Delete(temp); }
        return copyRel;
    }
}
