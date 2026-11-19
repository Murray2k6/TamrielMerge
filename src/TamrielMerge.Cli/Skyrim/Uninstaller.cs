using System.Text.RegularExpressions;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Takes TamrielMerge's conversions out of Skyrim's Data folder (user, 2026-11-02: "i need a uninstall button on prior to
/// older versions of tamreil merge and latest for the skyrim dir to uninstall the conversions"): what this version
/// installs and what earlier versions did, then their plugins off Skyrim's plugin list and Mod Organizer 2's.
/// <para>What is TamrielMerge's: the files the installer recorded (<see cref="RecordName"/>, installs from 2026-11-02
/// on), and by name what every version has put there: the plugins and archives it has ever made (from the source's
/// history), their sidecar files, its SKSE plugins, videos, interface, the converted scripts and their sources, its own
/// mesh, texture, sound and music folders, its worlds' distant view, the voices of its plugins, its Open Animation
/// Replacer folders and the build parts an old version left in Data. Nothing of Skyrim's or another mod's matches.</para>
/// <para>Crash Logger SSE (the game check installs it, but it may have been there before) only when asked
/// (<see cref="Find"/>'s crashLogger). Left: the Papyrus logging the game check turns on in SkyrimCustom.ini (harmless,
/// and maybe the user's own).</para>
/// </summary>
public static class Uninstaller
{
    /// <summary>The install's record of the loose files it copied into Data (paths under Data).</summary>
    public const string RecordName = "TamrielMerge Installed.json";

    const string ArchivesManifest = "TamrielMerge Archives.json";

    /// <summary>What an uninstall would remove: files (paths under Data), their bytes, and the plugins among them.</summary>
    public sealed record Plan(string Data, List<string> Files, long Bytes, List<string> Plugins)
    {
        /// <summary>Files outside Data (full paths): TamrielMerge's SKSE plugins' logs in Documents.</summary>
        public List<string> Outside { get; init; } = [];
        /// <summary>Whether Crash Logger SSE's files are among <see cref="Files"/>.</summary>
        public bool CrashLogger { get; init; }
    }

    static readonly string[] GameWords = ["Daggerfall", "Morrowind", "Oblivion", "Redguard"];

    // Every plugin name TamrielMerge has made (git history of src, 2026-11-02): a game's own, its land, navmesh, foliage,
    // towns, dungeons and creature plugins and their numbered splits and build parts, its archives' light plugins; the
    // shared ones; and the test plugins early versions installed.
    static readonly Regex GamePlugin = new(@"^(Daggerfall|Morrowind|Oblivion|Redguard)( (Land|Navmesh|Foliage|Towns|Dungeons|Creatures|Overland Doors|Assets)( \d+)?)?(\.part\d+)?\.(esm|esp|esl)$", RegexOptions.IgnoreCase);
    static readonly Regex OwnPlugin = new(@"^(TamrielMerge( (Start|Menu|Travel|Progression|Creatures|Models|Voices( \d+)?))?|TamrielMerge - Creatures|TamrielMergeLod|Daggerfall(Dungeon|Items|Spell|Town)Test)\.(esm|esp|esl)$", RegexOptions.IgnoreCase);

    /// <summary>Whether a file in Data's top folder is one of TamrielMerge's: its plugins, their archives (the plugin's name,
    /// " - Textures"), the sidecar files beside them (.creatures.json and so on), its manifests, an archive left half-written.</summary>
    public static bool IsOursTop(string name)
    {
        if (name.Equals(RecordName, StringComparison.OrdinalIgnoreCase) || name.Equals(ArchivesManifest, StringComparison.OrdinalIgnoreCase)) return true;
        // A conversion's reports, which installs before 2026-11-02 copied into Data with the plugins.
        if (DataArchives.Reports.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (DataArchives.Retired.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (GamePlugin.IsMatch(name) || OwnPlugin.IsMatch(name)) return true;
        // An archive or a sidecar: named after one of the plugins (without its extension).
        string stem = name;
        foreach (var tail in new[] { ".bsa.tmp", ".bsa", ".creatures.json", ".levelling.json", ".layout.json", ".voicemap.json" })
            if (stem.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { stem = stem[..^tail.Length]; goto named; }
        return false;
    named:
        if (stem.EndsWith(" - Textures", StringComparison.OrdinalIgnoreCase)) stem = stem[..^" - Textures".Length];
        return GamePlugin.IsMatch(stem + ".esm") || OwnPlugin.IsMatch(stem + ".esp");
    }

    static readonly Regex OurScriptSource = new(@"^(TM[A-Z]|MW_|MWR_|MWG_|OB_)\w*\.psc$");
    static readonly Regex OurVoiceFolder = new(@"^sound\\voice\\([^\\]+)\\", RegexOptions.IgnoreCase);

    /// <summary>Whether a file below Data's top folder (its path under Data) is one of TamrielMerge's.</summary>
    public static bool IsOursBelow(string rel)
    {
        rel = rel.Replace('/', '\\');
        if (DataArchives.IsOurs(rel)) return true;   // its own folders, its worlds' distant view, its compiled scripts
        string name = Path.GetFileName(rel);
        string dir = Path.GetDirectoryName(rel) ?? "";
        // Its SKSE plugins with their settings and logs (TamrielMergeEra, Native, Seamless, CrashLog, and the graphics
        // plugin of 2026-10: TamrielMergeGraphics.dll and .ini, with NVIDIA's DLSS runtime in its folder,
        // SKSE\Plugins\TamrielMergeGraphics\nvngx_dlss.dll).
        if (rel.StartsWith(@"SKSE\Plugins\TamrielMerge", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Equals("Video", StringComparison.OrdinalIgnoreCase) && name.StartsWith("TamrielMerge_", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.StartsWith(@"Interface\TamrielMerge\", StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Equals(@"Interface\Translations", StringComparison.OrdinalIgnoreCase) && name.StartsWith("TamrielMerge", StringComparison.OrdinalIgnoreCase)) return true;
        // The converted scripts' sources (Skyrim's own folder for them, and the Creation Kit's older one).
        if ((dir.Equals(@"Source\Scripts", StringComparison.OrdinalIgnoreCase) || dir.Equals(@"Scripts\Source", StringComparison.OrdinalIgnoreCase)) && OurScriptSource.IsMatch(name)) return true;
        // Now in a folder of their own, by game (Source\Scripts\TamrielMerge\Oblivion\...).
        if (rel.StartsWith(@"Source\Scripts\TamrielMerge\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"Scripts\Source\TamrielMerge\", StringComparison.OrdinalIgnoreCase)) return true;
        // The voices of its plugins (the voice generator's and the games' recordings): sound\voice\<plugin>\.
        if (OurVoiceFolder.Match(rel) is { Success: true } v && IsOursTop(v.Groups[1].Value)) return true;
        // Its plugins' start lists (SeqFiles): SEQ\<plugin>.seq.
        if (dir.Equals(SeqFiles.Folder, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".seq", StringComparison.OrdinalIgnoreCase)
            && (IsOursTop(name[..^4] + ".esm") || IsOursTop(name[..^4] + ".esp"))) return true;
        // Its people's and climbing animations, in Open Animation Replacer's folders.
        if (rel.Contains(@"\OpenAnimationReplacer\TamrielMerge\", StringComparison.OrdinalIgnoreCase)) return true;
        // Daggerfall's books as text (dump-books, run in Data on 2026-10-06).
        if (Regex.IsMatch(rel, @"^books\\BOK\d+\.md$", RegexOptions.IgnoreCase)) return true;
        // Build parts a version of 2026-10-24 wrote into Data (df-towns-23, _shared...).
        if (rel.StartsWith(@"_parts\", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(rel, @"^_parts\\(_shared|(df|mw|ob|rg)-[a-z]+(-\d+)?)\\", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    static string SkseLogs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition", "SKSE");

    /// <summary>
    /// The files in <paramref name="data"/> an uninstall removes (see the class); TamrielMerge's SKSE plugins' logs in
    /// Documents (<see cref="Plan.Outside"/>); and every TamrielMerge plugin to take off the plugin lists: those among the
    /// files, and those the lists still name whose files an older version left or took away (<paramref name="mo2"/>: Mod
    /// Organizer 2's folder, else the one the app found). With <paramref name="crashLogger"/>, Crash Logger SSE too (user,
    /// 2026-11-02: "and skse ones too"; the game check installs it): its plugin, and the msdia140.dll it brings only when
    /// it is the copy the tool installed (other SKSE plugins ship their own).
    /// </summary>
    public static Plan Find(string data, string? mo2 = null, bool crashLogger = false)
    {
        data = Path.GetFullPath(data);
        var recorded = ReadRecord(data);
        var files = new List<string>();
        long bytes = 0;
        var crash = crashLogger ? CrashLoggerFiles(data) : [];
        foreach (var f in new DirectoryInfo(data).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(data, f.FullName);
            bool top = !rel.Contains('\\');
            if (!(recorded.Contains(rel) || (top ? IsOursTop(rel) : IsOursBelow(rel)) || crash.Contains(rel) || IsOurControlMap(rel, f.FullName))) continue;
            files.Add(rel);
            bytes += f.Length;
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        var plugins = files.Where(f => !f.Contains('\\') && Regex.IsMatch(f, @"\.(esm|esp|esl)$", RegexOptions.IgnoreCase)).ToList();
        foreach (var list in Setup.PluginList.ListFiles(mo2))
            try
            {
                foreach (var line in File.ReadLines(list))
                {
                    string name = line.TrimStart('*').Trim();
                    if (name.Length > 0 && Regex.IsMatch(name, @"\.(esm|esp|esl)$", RegexOptions.IgnoreCase) && IsOursTop(name) && !plugins.Contains(name, StringComparer.OrdinalIgnoreCase))
                        plugins.Add(name);
                }
            }
            catch (IOException) { }
        var outside = Directory.Exists(SkseLogs) ? Directory.GetFiles(SkseLogs, "TamrielMerge*").Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).ToList() : [];
        bytes += outside.Sum(f => new FileInfo(f).Length);
        return new Plan(data, files, bytes, plugins) { Outside = outside, CrashLogger = crash.Count > 0 };
    }

    /// <summary>Crash Logger SSE's files in Data as the game check copies them (tools\crashlogger\SKSE\Plugins): each by
    /// name, but msdia140.dll only when it is the same file.</summary>
    public static HashSet<string> CrashLoggerFiles(string data)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string plugins = Path.Combine(data, "SKSE", "Plugins");
        string? from = Path.GetDirectoryName(Setup.Dependencies.CrashLogger);
        var names = from is not null && Directory.Exists(from) ? Directory.GetFiles(from).Select(Path.GetFileName).OfType<string>().ToList() : ["CrashLogger.dll", "CrashLogger.pdb", "CrashLogger.ini", "CrashLogger.toml"];
        foreach (var name in names)
        {
            string there = Path.Combine(plugins, name);
            if (!File.Exists(there)) continue;
            if (!name.StartsWith("CrashLogger", StringComparison.OrdinalIgnoreCase)
                && !(from is not null && File.Exists(Path.Combine(from, name)) && File.ReadAllBytes(there).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(from, name))))) continue;
            set.Add(Path.Combine("SKSE", "Plugins", name));
        }
        return set;
    }

    /// <summary>Skyrim's key list with the Tamriel menu's key added (ProgressionBuilder): Skyrim's own, in its archive,
    /// takes its place once it goes. Another mod's list (no Tamriel Menu line) stays.</summary>
    static bool IsOurControlMap(string rel, string path)
    {
        if (!rel.Equals(@"Interface\Controls\PC\controlmap.txt", StringComparison.OrdinalIgnoreCase)) return false;
        try { return File.ReadLines(path).Any(l => l.StartsWith("Tamriel Menu\t", StringComparison.Ordinal)); }
        catch (IOException) { return false; }
    }

    /// <summary>Whether Crash Logger SSE is in Data (for the question).</summary>
    public static bool HasCrashLogger(string data) => File.Exists(Path.Combine(data, "SKSE", "Plugins", "CrashLogger.dll"));

    /// <summary>A short account of <paramref name="plan"/>, by kind, for the question before removing (and the log).</summary>
    public static List<string> Describe(Plan plan)
    {
        string Kind(string rel)
        {
            string r = rel.Replace('/', '\\');
            if (!r.Contains('\\')) return Regex.IsMatch(r, @"\.(esm|esp|esl)$", RegexOptions.IgnoreCase) ? "plugins" : r.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) ? "archives" : "plugins' data files";
            string top = r.Split('\\')[0].ToLowerInvariant();
            return r.StartsWith(@"sound\voice\", StringComparison.OrdinalIgnoreCase) ? "voices"
                : top switch { "skse" => "SKSE plugins", "video" => "videos", "interface" => "interface", "source" or "scripts" => "scripts", "_parts" => "build parts left in Data", _ => "models, textures and other files" };
        }
        var lines = new List<string>();
        // Named in the plugin lists with no file in Data (an older version's): only taken off the lists.
        var listedOnly = plan.Plugins.Where(p => !plan.Files.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (listedOnly.Count > 0) lines.Add($"plugin list entries with no file in Data: {listedOnly.Count} ({string.Join(", ", listedOnly.Take(20))}{(listedOnly.Count > 20 ? ", ..." : "")})");
        if (plan.Outside.Count > 0) lines.Add($"SKSE plugins' logs in Documents: {string.Join(", ", plan.Outside.Select(Path.GetFileName))}");
        foreach (var g in plan.Files.GroupBy(Kind).OrderByDescending(g => g.Count()))
        {
            long b = g.Sum(rel => new FileInfo(Path.Combine(plan.Data, rel)).Length);
            lines.Add($"{g.Key}: {g.Count():N0} files, {Size(b)}" + (g.Key is "plugins" or "SKSE plugins" ? $" ({string.Join(", ", g.Select(Path.GetFileName).Take(30))}{(g.Count() > 30 ? ", ..." : "")})" : ""));
        }
        return lines;
    }

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";

    /// <summary>
    /// Removes <paramref name="plan"/>'s files (and the folders that leaves empty, Data's own apart), then its plugins
    /// from Skyrim's plugin list and Mod Organizer 2's (<paramref name="mo2"/>). Not while Skyrim runs: it holds its
    /// plugins and archives open. Returns the files removed and those that could not be.
    /// </summary>
    public static (int Removed, long Bytes, List<string> Failed) Run(Plan plan, string? mo2, Action<string> log)
    {
        if (System.Diagnostics.Process.GetProcessesByName("SkyrimSE").Length > 0)
            throw new InvalidOperationException("Skyrim is running: close it first (it holds the plugins and archives open)");
        int removed = 0;
        long bytes = 0;
        var failed = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in plan.Files)
        {
            string path = Path.Combine(plan.Data, rel);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                long size = fi.Length;
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                removed++;
                bytes += size;
                for (string? d = Path.GetDirectoryName(path); d is not null && d.Length > plan.Data.Length; d = Path.GetDirectoryName(d)) folders.Add(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add($"{rel} ({e.Message})"); }
        }
        foreach (var path in plan.Outside)
            try { if (File.Exists(path)) { bytes += new FileInfo(path).Length; File.Delete(path); removed++; } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add($"{path} ({e.Message})"); }
        // The folders left empty, deepest first; never Data itself.
        foreach (var d in folders.OrderByDescending(d => d.Length))
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (IOException) { }
        log($"Removed {removed:N0} files ({Size(bytes)}) from {plan.Data}{(failed.Count > 0 ? $"; {failed.Count} could not be removed" : "")}");
        foreach (var f in failed.Take(20)) log($"  not removed: {f}");
        if (plan.Plugins.Count > 0)
            foreach (var line in Setup.PluginList.Remove(plan.Plugins, mo2)) log($"  {line}");
        if (!plan.CrashLogger && HasCrashLogger(plan.Data))
            log("Left: Crash Logger SSE in SKSE\\Plugins (the game check installs it, but it may have been yours before); remove CrashLogger.dll by hand if you don't want it");
        return (removed, bytes, failed);
    }

    /// <summary>The loose files an install recorded as copied (<see cref="RecordName"/>); empty before 2026-11-02.</summary>
    static HashSet<string> ReadRecord(string data)
    {
        string path = Path.Combine(data, RecordName);
        try
        {
            return File.Exists(path)
                ? new HashSet<string>((System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []).Select(r => r.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Adds <paramref name="rels"/> (paths under Data) to the install's record, keeping what earlier installs
    /// recorded that is still there.</summary>
    public static void Record(string data, IEnumerable<string> rels)
    {
        var all = ReadRecord(data).Where(r => File.Exists(Path.Combine(data, r))).Concat(rels.Select(r => r.Replace('/', '\\'))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        File.WriteAllText(Path.Combine(data, RecordName), System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
