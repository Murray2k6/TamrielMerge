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

    /// <summary>The menu's music (music\special): made on its own, apart from the flyby (everything else).</summary>
    static bool IsMusic(string relative) => relative.Replace('/', '\\').StartsWith(@"music\special\", StringComparison.OrdinalIgnoreCase);

    static bool IsMenuFile(string relative)
    {
        string rel = relative.Replace('/', '\\');
        return Patterns.Contains(rel, StringComparer.OrdinalIgnoreCase)
            || (rel.StartsWith(MenuBuilder.TextureDir + "\\", StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The menu's files loose in <paramref name="dir"/>, relative to it.</summary>
    static List<string> Loose(string dir)
    {
        var files = Patterns.Where(p => File.Exists(Path.Combine(dir, p))).ToList();
        string menuTextures = Path.Combine(dir, MenuBuilder.TextureDir);
        if (Directory.Exists(menuTextures)) files.AddRange(Directory.EnumerateFiles(menuTextures, "*.dds").Select(f => Path.GetRelativePath(dir, f)));
        return files;
    }

    /// <summary>
    /// What an earlier menu archive (in <paramref name="earlierDirs"/>, first found first) holds that
    /// <paramref name="outDir"/> has no new copy of, written out loose there to be packed again: the flyby as a whole (a run
    /// without --gpu renders none) and the music as a whole, each only when none of it was made new, so an old frame never
    /// joins a new render. 2026-10-24: a build staged on the solid-state drive packed the music alone, that archive replaced
    /// the one in --out and in Data, and the menu lost its flyby. Returns what was taken from where.
    /// </summary>
    public static List<string> CarryEarlier(string outDir, IEnumerable<string> earlierDirs)
    {
        var loose = Loose(outDir);
        bool needFlyby = !loose.Any(f => !IsMusic(f)), needMusic = !loose.Any(IsMusic);
        var taken = new List<string>();
        string stem = Path.GetFileNameWithoutExtension(PluginName);
        // Its own folder's archive first (Build replaces it), then the others.
        foreach (var dir in new[] { outDir }.Concat(earlierDirs).Select(d => Path.GetFullPath(d).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!needFlyby && !needMusic) break;
            var archives = new[] { stem + ".bsa", stem + " - Textures.bsa" }.Select(f => Path.Combine(dir, f)).Where(File.Exists).ToList();
            var held = archives.SelectMany(a => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, a).Files).Where(f => IsMenuFile(f.Path)).ToList();
            foreach (var (need, part, what) in new[] { (needFlyby, (Func<string, bool>)(p => !IsMusic(p)), "flyby"), (needMusic, IsMusic, "music") })
            {
                var files = held.Where(f => part(f.Path)).ToList();
                if (!need || files.Count == 0) continue;
                foreach (var f in files)
                {
                    string target = Path.Combine(outDir, f.Path.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, f.GetBytes());
                }
                taken.Add($"the {what} ({files.Count} files) from {Path.Combine(dir, stem + ".bsa")}");
                if (what == "flyby") needFlyby = false; else needMusic = false;
            }
        }
        return taken;
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
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small | SkyrimModHeader.HeaderFlag.Master;
        mod.WriteToBinary(Path.Combine(outDir, PluginName));
        foreach (var f in files) File.Delete(f);
        return files.Count;
    }
}
