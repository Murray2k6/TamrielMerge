using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim.Render;

/// <summary>
/// The main menu and its music as a part of their own (user, 2026-10-10: the tool's own mod organizer, "TamrielMerge's
/// parts"): they replace Skyrim's own files (the logo meshes, the emblem's textures, the menu fog, music\special\
/// mus_maintheme.xwm, the main menu's music), so loose they could only be removed, not switched off. Packed into the archives of an empty light
/// plugin, "TamrielMerge Menu.esp" (+ ".bsa", and " - Textures.bsa" past <see cref="Limit"/>), loaded after Skyrim's own
/// archives: on, ours; off, Skyrim's menu and theme. The loose copies in the output are removed once packed.
/// </summary>
public static class MenuArchive
{
    public const string PluginName = "TamrielMerge Menu.esp";
    const long Limit = 1_900_000_000;

    static readonly string[] Patterns =
    [
        @"meshes\interface\logo\logo.nif", @"meshes\interface\logo\logo01ae.nif", @"meshes\interface\logo\logo01ae_animated.nif",
        @"meshes\interface\intmenufogparticles.nif",
        @"textures\interface\objects\logo01_d.dds", @"textures\interface\objects\logo01ae_d.dds", @"textures\interface\objects\logo02_d.dds",
        @"music\special\mus_maintheme.xwm", @"music\special\mus_maintheme.wav",
    ];

    /// <summary>
    /// Loose copies of the menu's files in <paramref name="dataDir"/> (Skyrim's Data, or an output folder that is it):
    /// Skyrim keeps these only inside its own archives, so loose ones are ours, from an older menu (2026-10-14: a hand
    /// install's loose flyby of 10-10 kept overriding the archive installed on 10-14, so the game showed the old flyby and
    /// switching the menu part off did nothing). Removed once the archive replaces them; returns what was removed.
    /// </summary>
    public static List<string> RemoveLoose(string dataDir)
    {
        var loose = Patterns.Select(p => Path.Combine(dataDir, p)).Where(File.Exists).ToList();
        string menuTextures = Path.Combine(dataDir, MenuBuilder.TextureDir);
        if (Directory.Exists(menuTextures)) loose.AddRange(Directory.EnumerateFiles(menuTextures, "*.dds"));
        var removed = new List<string>();
        foreach (var f in loose)
            try { File.Delete(f); removed.Add(Path.GetRelativePath(dataDir, f)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return removed;
    }

    /// <summary>Packs the menu's files in <paramref name="outDir"/>; returns how many (0: nothing to pack, the plugin left as it was).</summary>
    public static int Build(string outDir)
    {
        var files = Patterns.Select(p => Path.Combine(outDir, p)).Where(File.Exists).ToList();
        string menuTextures = Path.Combine(outDir, MenuBuilder.TextureDir);
        if (Directory.Exists(menuTextures)) files.AddRange(Directory.EnumerateFiles(menuTextures, "*.dds"));
        if (files.Count == 0) return 0;
        string stem = Path.GetFileNameWithoutExtension(PluginName);
        foreach (var old in new[] { stem + ".bsa", stem + " - Textures.bsa" }.Select(f => Path.Combine(outDir, f)).Where(File.Exists)) File.Delete(old);
        var entries = files.Select(f => (InArchive: Path.GetRelativePath(outDir, f), Source: f, Size: new FileInfo(f).Length)).OrderBy(f => f.InArchive).ToList();
        var first = new List<(string, string)>();
        var second = new List<(string, string)>();
        long size = 0;
        foreach (var e in entries)
        {
            (size + e.Size <= Limit ? first : second).Add((e.InArchive, e.Source));
            size += e.Size;
        }
        if (second.Count > 0 && second.Sum(e => new FileInfo(e.Item2).Length) > Limit) throw new InvalidOperationException("the menu's files need more than two archives");
        BsaWriter.Write(Path.Combine(outDir, stem + ".bsa"), first);
        if (second.Count > 0) BsaWriter.Write(Path.Combine(outDir, stem + " - Textures.bsa"), second);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        mod.WriteToBinary(Path.Combine(outDir, PluginName));
        foreach (var f in files) File.Delete(f);
        return files.Count;
    }
}
