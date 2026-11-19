namespace TamrielMerge.Setup;

/// <summary>
/// How much room a conversion takes, by the games converted and the parts chosen (user, 2026-10-25: "i need the tool to
/// say on what games people own and what they tick it will show how much total space conversion can take"): the output
/// folder, Skyrim's Data once installed, and the solid-state drive the build is staged on. <see cref="Measure"/> reads a
/// finished conversion (the size-report command); <see cref="Measured"/> holds its measurements, <see cref="SpaceMeter"/>
/// measures the room a conversion needs while it runs.
/// </summary>
public static class ConversionSize
{
    /// <summary>The parts of a game's conversion that can be left out.</summary>
    public enum Part { Base, Voices, Videos, Models, Menu }

    /// <summary>A part's size: in the output folder (as built, loose) and in Data (archives, compressed where Skyrim's are).</summary>
    public sealed record Size(long Output, long Installed);

    const long GB = 1L << 30;

    /// <summary>
    /// The sizes measured with <see cref="Measure"/> (size-report) on the 2026-11-01 conversion of all four games, in GB
    /// (output folder, Skyrim's Data). Morrowind's voices weren't generated then: theirs is its 129,558 voice lines at the
    /// 41.4 KB a voice file of Oblivion's packed voices (79,195 files, 3.05 GB), an estimate until they are measured.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Game, Part Part), Size> Measured = new Dictionary<(string, Part), Size>
    {
        [("Daggerfall", Part.Base)] = Gb(41.36, 26.14), [("Daggerfall", Part.Videos)] = Gb(0.19, 0.19), [("Daggerfall", Part.Models)] = Gb(6.22, 3.05),
        [("Morrowind", Part.Base)] = Gb(3.27, 2.41), [("Morrowind", Part.Videos)] = Gb(0.09, 0.09), [("Morrowind", Part.Voices)] = Gb(4.99, 4.99),
        [("Oblivion", Part.Base)] = Gb(5.53, 4.15), [("Oblivion", Part.Voices)] = Gb(3.05, 3.05), [("Oblivion", Part.Videos)] = Gb(0.25, 0.25),
        [("Redguard", Part.Base)] = Gb(0.99, 0.50), [("Redguard", Part.Voices)] = Gb(0.19, 0.19), [("Redguard", Part.Videos)] = Gb(2.02, 2.02),
        [("Shared", Part.Base)] = Gb(3.21, 1.94), [("Shared", Part.Videos)] = Gb(0.55, 0.55), [("Shared", Part.Menu)] = Gb(1.18, 1.18),
    };

    static Size Gb(double output, double installed) => new((long)(output * GB), (long)(installed * GB));

    /// <summary>What converting <paramref name="games"/> with the parts chosen takes: the output folder, and Skyrim's Data
    /// once installed (<see cref="Measured"/>; what every conversion shares counted once).</summary>
    public static Size Estimate(IEnumerable<string> games, bool voices, bool videos, bool models)
    {
        var list = games.ToList();
        if (list.Count == 0) return new Size(0, 0);
        long output = 0, installed = 0;
        foreach (var ((game, part), size) in Measured)
        {
            if (game != "Shared" && !list.Contains(game, StringComparer.OrdinalIgnoreCase)) continue;
            if ((part == Part.Voices && !voices) || (part == Part.Videos && !videos) || (part == Part.Models && !models)) continue;
            output += size.Output;
            installed += size.Installed;
        }
        return new Size(output, installed);
    }

    /// <summary>The peak a conversion took on its drives beyond the output it left (build parts, staging, temporary files),
    /// as the last conversion of these games measured it (<see cref="RecordPeak"/>), or null before one has.</summary>
    public static long? MeasuredOverhead(IEnumerable<string> games)
    {
        var want = games.Select(g => g.ToLowerInvariant()).Order().ToList();
        try
        {
            if (!File.Exists(PeakFile)) return null;
            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(PeakFile)) ?? [];
            // This set of games, else a run of more games (its overhead is at least this set's).
            if (all.TryGetValue(string.Join(",", want), out var exact)) return exact;
            var bigger = all.Where(kv => want.All(kv.Key.Split(',').Contains)).Select(kv => (long?)kv.Value).Min();
            return bigger;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    static string PeakFile => Path.Combine(AppContext.BaseDirectory, "space-peak.json");

    /// <summary>Records a conversion's measured overhead (<see cref="SpaceMeter"/>) for <see cref="MeasuredOverhead"/>.</summary>
    public static void RecordPeak(IEnumerable<string> games, long overhead)
    {
        try
        {
            var all = File.Exists(PeakFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(PeakFile)) ?? [] : [];
            all[string.Join(",", games.Select(g => g.ToLowerInvariant()).Order())] = overhead;
            File.WriteAllText(PeakFile, System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Measures how much a conversion takes on its drives at its peak: each drive's free space when it starts, then the
    /// lowest it falls to (looked at every 20 seconds). Less what the output holds at the end, that is the room a
    /// conversion needs beyond its result (build parts, staging, temporary files). Other programs writing to the same
    /// drives meanwhile count too, so it errs on the side of more.
    /// </summary>
    public sealed class SpaceMeter : IDisposable
    {
        readonly Dictionary<string, (long Start, long Low)> drives = new(StringComparer.OrdinalIgnoreCase);
        readonly Timer timer;
        public SpaceMeter(IEnumerable<string> folders)
        {
            foreach (var f in folders.Where(f => !string.IsNullOrEmpty(f)))
                if (Path.GetPathRoot(Path.GetFullPath(f)) is { Length: > 0 } root && !drives.ContainsKey(root))
                    try { long free = new DriveInfo(root).AvailableFreeSpace; drives[root] = (free, free); } catch (Exception e) when (e is IOException or ArgumentException) { }
            timer = new Timer(_ => Sample(), null, 20_000, 20_000);
        }

        void Sample()
        {
            lock (drives)
                foreach (var root in drives.Keys.ToList())
                    try { long free = new DriveInfo(root).AvailableFreeSpace; if (free < drives[root].Low) drives[root] = (drives[root].Start, free); } catch (IOException) { }
        }

        /// <summary>The most taken at once, over all the drives (bytes).</summary>
        public long Peak { get { Sample(); lock (drives) return drives.Values.Sum(d => Math.Max(0, d.Start - d.Low)); } }

        /// <summary>What is taken now, over all the drives, since the start (bytes): the result, at the end.</summary>
        public long TakenNow
        {
            get
            {
                lock (drives)
                    return drives.Sum(d => { try { return Math.Max(0, d.Value.Start - new DriveInfo(d.Key).AvailableFreeSpace); } catch (IOException) { return 0L; } });
            }
        }

        public void Dispose() => timer.Dispose();
    }

    /// <summary>The game ("Shared" for what every conversion has) and part a file of a conversion belongs to.</summary>
    public static (string Game, Part Part) Classify(string rel)
    {
        string r = rel.Replace('/', '\\');
        string lower = r.ToLowerInvariant();
        static string GameOfName(string name) =>
            name.StartsWith("daggerfall", StringComparison.OrdinalIgnoreCase) ? "Daggerfall"
            : name.StartsWith("morrowind", StringComparison.OrdinalIgnoreCase) ? "Morrowind"
            : name.StartsWith("oblivion", StringComparison.OrdinalIgnoreCase) ? "Oblivion"
            : name.StartsWith("redguard", StringComparison.OrdinalIgnoreCase) ? "Redguard" : "Shared";
        var parts = r.Split('\\');
        if (parts.Length == 1)
            return lower.StartsWith("tamrielmerge menu") ? ("Shared", Part.Menu) : (GameOfName(parts[0]), Part.Base);
        if (parts.Length > 3 && lower.StartsWith(@"sound\voice\")) return (GameOfName(parts[2]), Part.Voices);
        if (lower.StartsWith(@"video\")) return (lower.Contains("daggerfall") ? "Daggerfall" : lower.Contains("morrowind") ? "Morrowind" : lower.Contains("oblivion") ? "Oblivion" : lower.Contains("redguard") ? "Redguard" : "Shared", Part.Videos);
        if (lower.Contains(@"\tamrielmerge\df\models\")) return ("Daggerfall", Part.Models);
        if (lower.StartsWith(@"meshes\interface\logo\") || lower.StartsWith(@"meshes\interface\intmenufog") || lower.StartsWith(@"textures\interface\objects\logo")
            || lower.StartsWith(@"textures\tamrielmerge\menu\") || lower.StartsWith(@"music\special\")) return ("Shared", Part.Menu);
        if (Skyrim.DataArchives.IsPacked(r))
        {
            string g = Skyrim.DataArchives.GroupOf(r);
            return (g == "TamrielMerge Start" ? "Shared" : g, Part.Base);
        }
        return ("Shared", Part.Base);
    }

    /// <summary>
    /// A finished conversion's sizes by game and part: its output folder's files, and in <paramref name="data"/> what the
    /// install made of them (each game's archives, by its share of them; audio is stored as it is, the rest as packed).
    /// </summary>
    public static Dictionary<(string Game, Part Part), Size> Measure(string outDir, string data)
    {
        var raw = new Dictionary<(string, Part), long>();
        var packedRaw = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);       // a game's archived files, as they are
        var packedAudio = new Dictionary<(string, Part), long>();                              // of them, audio (stored as is)
        var looseOrPlugin = new Dictionary<(string, Part), long>();                            // copied to Data as they are
        foreach (var f in new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(outDir, f.FullName);
            if (rel.Equals("problems.txt", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".voicemap.json", StringComparison.OrdinalIgnoreCase)) continue;
            var key = Classify(rel);
            raw[key] = raw.GetValueOrDefault(key) + f.Length;
            if (Skyrim.DataArchives.IsPacked(rel))
            {
                string group = Skyrim.DataArchives.GroupOf(rel);
                packedRaw[group] = packedRaw.GetValueOrDefault(group) + f.Length;
                string top = rel.Split('\\', '/')[0];
                if (top.Equals("sound", StringComparison.OrdinalIgnoreCase) || top.Equals("music", StringComparison.OrdinalIgnoreCase))
                    packedAudio[key] = packedAudio.GetValueOrDefault(key) + f.Length;
            }
            else looseOrPlugin[key] = looseOrPlugin.GetValueOrDefault(key) + f.Length;
        }
        // Each game's archives in Data: audio as is; the rest of the archive bytes shared out by the parts' raw sizes.
        var archived = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var bsa in Directory.EnumerateFiles(data, "*.bsa"))
        {
            string name = Path.GetFileName(bsa);
            if (name.StartsWith("TamrielMerge Menu", StringComparison.OrdinalIgnoreCase)) continue;   // copied as it is (above)
            foreach (var game in new[] { "Daggerfall", "Morrowind", "Oblivion", "Redguard", "TamrielMerge Start" })
                if (name.Equals(game + ".bsa", StringComparison.OrdinalIgnoreCase) || name.Equals(game + " - Textures.bsa", StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(game) + @" Assets \d+( - Textures)?\.bsa$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    archived[game] = archived.GetValueOrDefault(game) + new FileInfo(bsa).Length;
        }
        var result = new Dictionary<(string, Part), Size>();
        foreach (var (key, bytes) in raw)
        {
            long installed = looseOrPlugin.GetValueOrDefault(key);
            // This part's packed files' share of its group's archives (audio counted as is).
            long audio = packedAudio.GetValueOrDefault(key);
            long packedHere = bytes - looseOrPlugin.GetValueOrDefault(key);
            if (packedHere > 0)
            {
                string group = key.Item1 == "Shared" ? "TamrielMerge Start" : key.Item1;
                long groupRaw = packedRaw.GetValueOrDefault(group), groupAudio = packedAudio.Where(a => (a.Key.Item1 == "Shared" ? "TamrielMerge Start" : a.Key.Item1) == group).Sum(a => a.Value);
                long groupArchived = archived.GetValueOrDefault(group);
                double ratio = groupRaw - groupAudio > 0 ? Math.Max(0, groupArchived - groupAudio) / (double)(groupRaw - groupAudio) : 1;
                installed += audio + (long)((packedHere - audio) * ratio);
            }
            result[key] = new Size(bytes, installed);
        }
        return result;
    }
}
