using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Installs a conversion into Skyrim's Data folder with its assets in archives, not loose (user, 2026-10-16: "compress
/// some files into bsa files so the conversion is smaller"; "the voice can be in a bsa file" as Skyrim's own
/// Voices_en0.bsa). The output folder keeps its loose files (resuming, the distant LOD, the checks and the model viewer
/// read them); Data gets:
/// <list type="bullet">
/// <item>meshes, textures, scripts and the distant view's settings compressed (LZ4), sounds, music and voices as they
/// are (Skyrim's own archives don't compress them), in archives of each game (<see cref="GroupOf"/>), named after its
/// plugin so Skyrim loads them with it ("Daggerfall.bsa", "Daggerfall - Textures.bsa"); the shared ones (creatures,
/// props, the ship, TamrielMerge's own scripts) with "TamrielMerge Start.esp". Skyrim loads those two names a plugin;
/// past them an empty light plugin carries two more ("Daggerfall Assets 2.esp"), each archive under 1.9 GB.</item>
/// <item>the rest loose: plugins and archives made already (the menu's, the voices'), SKSE's plugins, the videos
/// (played from the folder), the interface and the scripts' sources.</item>
/// </list>
/// Loose files beat archives, so the loose copies an earlier install left of what is now archived are removed, and
/// with them what earlier conversions left in TamrielMerge's own folders that this one no longer makes (stale: the
/// tiled overland's terrain, renamed models), and plugins the tool no longer makes (<see cref="Retired"/>). The
/// generated voices' folder is left alone (the voice generator installs there).
/// </summary>
public static class DataArchives
{
    const long Limit = 1_900_000_000;
    static readonly string[] PackedFolders = ["meshes", "textures", "scripts", "sound", "music", "lodsettings"];

    /// <summary>Plugins earlier versions of the tool made and this one doesn't (2026-10-14: the creatures became a
    /// plugin per game).</summary>
    public static readonly string[] Retired = ["TamrielMerge Creatures.esp"];

    /// <summary>The games' archive names (their plugins' names), and the shared one's.</summary>
    static readonly (string Game, string Plugin)[] Games =
        [("Daggerfall", "Daggerfall.esm"), ("Morrowind", "Morrowind.esm"), ("Oblivion", "Oblivion.esm"), ("Redguard", "Redguard.esm"), ("TamrielMerge Start", "TamrielMerge Start.esp")];

    /// <summary>Whether a file (path under Data) goes into an archive.</summary>
    public static bool IsPacked(string rel) => PackedFolders.Contains(Top(rel), StringComparer.OrdinalIgnoreCase);

    static string Top(string rel) => rel.Split('\\', '/')[0];
    static bool IsAudio(string rel) => Top(rel).Equals("sound", StringComparison.OrdinalIgnoreCase) || Top(rel).Equals("music", StringComparison.OrdinalIgnoreCase);

    static readonly Regex WorldPrefix = new(@"^(df|mw|ob|rg)", RegexOptions.IgnoreCase);

    /// <summary>The game whose archives hold a file, or "TamrielMerge Start" for what the games share.</summary>
    public static string GroupOf(string rel)
    {
        var parts = rel.Replace('/', '\\').Split('\\');
        string lower = rel.Replace('/', '\\').ToLowerInvariant();
        static string ByPrefix(string name) => WorldPrefix.Match(name) is { Success: true } m ? m.Value.ToLowerInvariant() switch
        {
            "df" => "Daggerfall", "mw" => "Morrowind", "ob" => "Oblivion", _ => "Redguard",
        } : "TamrielMerge Start";
        // A game's voices: sound\voice\<its plugin>\...
        if (parts.Length > 3 && parts[0].Equals("sound", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("voice", StringComparison.OrdinalIgnoreCase))
            return Games.FirstOrDefault(g => parts[2].StartsWith(g.Game, StringComparison.OrdinalIgnoreCase)).Game ?? "TamrielMerge Start";
        // The distant view, by its world's editor ID (DFIliacBay, MWVvardenfell, OBTamriel, RGISLAND).
        if (parts.Length > 3 && parts[1].Equals("terrain", StringComparison.OrdinalIgnoreCase)) return ByPrefix(parts[2]);
        if (parts.Length == 2 && parts[0].Equals("lodsettings", StringComparison.OrdinalIgnoreCase)) return ByPrefix(parts[1]);
        // Scripts by their converter's prefix (case matters: SKSE's ObjectReference.pex is no Oblivion script).
        if (parts.Length == 2 && parts[0].Equals("scripts", StringComparison.OrdinalIgnoreCase))
            return parts[1].StartsWith("MW") ? "Morrowind" : parts[1].StartsWith("OB_") ? "Oblivion" : "TamrielMerge Start";
        foreach (var (seg, game) in new[] { (@"\tamrielmerge\df\", "Daggerfall"), (@"\tamrielmerge\dungeons\", "Daggerfall"), (@"\tamrielmerge\towns\", "Daggerfall"),
                     (@"\tamrielmerge\daggerfall\", "Daggerfall"), (@"\tamrielmerge\mw\", "Morrowind"), (@"\tamrielmerge\ob\", "Oblivion"), (@"\tamrielmerge\rg\", "Redguard") })
            if (lower.Contains(seg)) return game;
        return "TamrielMerge Start";
    }

    static readonly Regex OursScript = new(@"^(TM[A-Z]|MW_|MWR_|MWG_|OB_)\w*\.pex$");
    static readonly Regex OursTerrain = new(@"^(meshes|textures)\\terrain\\(df|mw|ob|rg)[^\\]*\\", RegexOptions.IgnoreCase);
    static readonly Regex OursLod = new(@"^lodsettings\\(df|mw|ob|rg)[^\\]*\.lod$", RegexOptions.IgnoreCase);
    static readonly string[] OursFolders = [@"meshes\tamrielmerge\", @"textures\tamrielmerge\", @"meshes\actors\tamrielmerge\", @"textures\actors\tamrielmerge\", @"sound\fx\tamrielmerge\", @"music\tamrielmerge\"];

    /// <summary>Whether a loose file in Data is one of TamrielMerge's in the folders now archived (its own folders, its
    /// worlds' distant view, its scripts). Voices aren't: the voice generator installs those.</summary>
    public static bool IsOurs(string rel)
    {
        rel = rel.Replace('/', '\\');
        if (OursFolders.Any(f => rel.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return true;
        if (OursTerrain.IsMatch(rel) || OursLod.IsMatch(rel)) return true;
        return rel.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase) && rel.IndexOf('\\', 8) < 0 && OursScript.IsMatch(rel[8..]);
    }

    /// <summary>What an install did: the plugins to enable, in order, and those to take off the plugin lists (gone from
    /// Data: stale or retired).</summary>
    public sealed record Result(List<string> Plugins, List<string> Gone, List<string> Archives, long Raw, long Stored, int Copied, int LooseRemoved);

    /// <summary>TamrielMerge's plugins and archives in Data's top folder, by name.</summary>
    static readonly Regex OursTop = new(@"^(TamrielMerge|Daggerfall|Morrowind|Oblivion|Redguard)\b.*\.(esm|esp|esl|bsa)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Installs <paramref name="outDir"/> into <paramref name="data"/>: archives, the loose rest, the stale removed.
    /// Returns the plugins to enable in order (<paramref name="plugins"/> with each game's extra light plugins after it).
    /// </summary>
    public static Result Install(string outDir, string data, IReadOnlyList<string> plugins, Action<string> log)
    {
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(outDir, f))
            .Where(rel => !rel.Equals("problems.txt", StringComparison.OrdinalIgnoreCase) && !rel.EndsWith(".voicemap.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var packed = files.Where(IsPacked).ToList();
        var loose = files.Where(f => !IsPacked(f)).ToList();
        var packedSet = packed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Data is made to hold this conversion (user, 2026-10-16: "when deployed with the tool to skyrim it needs to remove
        // stale installation"): TamrielMerge's plugins and archives there that it doesn't have go (an earlier split of
        // the land into more plugins, the archives of an earlier install, which are remade below, retired plugins).
        var topHere = files.Where(f => !f.Contains('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = new List<string>();
        foreach (var f in Directory.EnumerateFiles(data).Where(f => OursTop.IsMatch(Path.GetFileName(f)) || Retired.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList())
        {
            string name = Path.GetFileName(f);
            if (topHere.Contains(name)) continue;
            File.Delete(f);
            if (!name.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)) gone.Add(name);
            if (!Games.Any(g => IsArchiveOf(name, g.Game))) log($"  removed {name} (not part of this conversion)");
        }
        // Loose files that would hide the new archives, or are stale.
        int removed = 0;
        foreach (var top in PackedFolders.Select(t => Path.Combine(data, t)).Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).ToList())
            {
                string rel = Path.GetRelativePath(data, f);
                if (packedSet.Contains(rel) || IsOurs(rel)) { File.Delete(f); removed++; }
            }
        foreach (var top in PackedFolders.Select(t => Path.Combine(data, t)).Where(Directory.Exists)) RemoveEmptyFolders(top);
        log($"  {removed:N0} loose files removed from {data} (older copies of what the archives hold, and stale ones)");

        // The loose rest.
        foreach (var rel in loose)
        {
            string target = Path.Combine(data, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(outDir, rel), target, overwrite: true);
        }
        log($"  {loose.Count:N0} files copied as they are (plugins, archives made already, SKSE plugins, videos, interface)");

        // The archives, a game at a time.
        var extras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var archives = new List<string>();
        long raw = 0, stored = 0;
        var reading = new System.Diagnostics.Stopwatch(); var packing = new System.Diagnostics.Stopwatch(); var writing = new System.Diagnostics.Stopwatch();
        foreach (var group in packed.GroupBy(GroupOf))
        {
            var (game, plugin) = Games.First(g => g.Game == group.Key);
            bool hasPlugin = plugins.Contains(plugin, StringComparer.OrdinalIgnoreCase) || File.Exists(Path.Combine(data, plugin));
            int index = 0;
            var current = new List<BsaWriter.Stored>();
            long size = 0;
            bool currentCompressed = true;
            void Flush()
            {
                if (current.Count == 0) return;
                int pair = index / 2 + (hasPlugin ? 0 : 1);
                string stem = pair == 0 ? game : $"{game} Assets {pair + 1}";
                if (pair > 0 && index % 2 == 0)
                {
                    var mod = new SkyrimMod(ModKey.FromFileName(stem + ".esp"), SkyrimRelease.SkyrimSE);
                    mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;   // light: no load-order slot
                    mod.WriteToBinary(Path.Combine(data, stem + ".esp"));
                    (extras.TryGetValue(plugin, out var l) ? l : extras[plugin] = []).Add(stem + ".esp");
                }
                string name = stem + (index % 2 == 0 ? ".bsa" : " - Textures.bsa");
                writing.Start();
                BsaWriter.Write(Path.Combine(data, name), current, currentCompressed);
                writing.Stop();
                long bytes = new FileInfo(Path.Combine(data, name)).Length;
                log($"  {name}: {current.Count:N0} files, {bytes / 1048576.0:N0} MB{(currentCompressed ? " compressed" : "")}");
                archives.Add(name);
                index++;
                current = [];
                size = 0;
            }
            foreach (bool compress in new[] { true, false })
            {
                Flush();
                currentCompressed = compress;
                var set = group.Where(rel => IsAudio(rel) != compress).Order(StringComparer.OrdinalIgnoreCase).ToList();
                // Read in order (the output may be on a hard disk), packed on every core.
                for (int at = 0; at < set.Count;)
                {
                    var batch = new List<(string Rel, byte[] Raw)>();
                    long batchBytes = 0;
                    reading.Start();
                    while (at < set.Count && batchBytes < 256L << 20)
                    {
                        var bytes = File.ReadAllBytes(Path.Combine(outDir, set[at]));
                        batch.Add((set[at], bytes));
                        batchBytes += bytes.Length;
                        at++;
                    }
                    reading.Stop();
                    var done = new BsaWriter.Stored[batch.Count];
                    packing.Start();
                    Parallel.For(0, batch.Count, i => done[i] = compress ? BsaWriter.Pack(batch[i].Rel, batch[i].Raw) : new BsaWriter.Stored(batch[i].Rel, batch[i].Raw, false));
                    packing.Stop();
                    foreach (var (s, b) in done.Zip(batch))
                    {
                        long cost = s.Data.Length + 16 + Path.GetFileName(s.InArchive).Length + 1;
                        if (current.Count > 0 && size + cost > Limit) Flush();
                        current.Add(s);
                        size += cost;
                        raw += b.Raw.Length;
                        stored += s.Data.Length;
                    }
                }
            }
            Flush();
        }
        log($"  time: reading {reading.Elapsed.TotalSeconds:F0} s, compressing {packing.Elapsed.TotalSeconds:F0} s, writing {writing.Elapsed.TotalSeconds:F0} s");
        // The light plugins carrying the extra archives, after their game's plugin; ones an earlier install had and
        // this one doesn't, off the lists.
        var order = new List<string>();
        foreach (var p in plugins)
        {
            order.Add(p);
            if (extras.TryGetValue(p, out var more)) order.AddRange(more);
        }
        foreach (var (p, more) in extras.Where(e => !plugins.Contains(e.Key, StringComparer.OrdinalIgnoreCase))) order.AddRange(more);
        gone = gone.Where(g => !order.Contains(g, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Result(order, gone, archives, raw, stored, loose.Count, removed);
    }

    /// <summary>Whether a file in Data is one of a game's archives or their light plugins as <see cref="Install"/> names them.</summary>
    static bool IsArchiveOf(string file, string game) =>
        file.Equals(game + ".bsa", StringComparison.OrdinalIgnoreCase) || file.Equals(game + " - Textures.bsa", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(file, "^" + Regex.Escape(game) + @" Assets \d+( - Textures)?\.(bsa|esp)$", RegexOptions.IgnoreCase);

    static void RemoveEmptyFolders(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir)) RemoveEmptyFolders(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
