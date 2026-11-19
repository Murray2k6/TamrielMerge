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
    public static readonly string[] Retired = ["TamrielMerge Creatures.esp", .. Creatures.CreatureRecords.OldEspNames];

    /// <summary>A conversion's reports in its output's top folder: for reading there, not for Skyrim (installs before
    /// 2026-11-02 copied them into Data).</summary>
    public static readonly string[] Reports = ["problems.txt", PluginPack.ReportName, "conditions-untested.txt", "dialogue-dropped.txt", "results-unattached.txt",
        "scripts-compile.txt", "scripts-gaps.txt", "scripts-missing.txt", "scripts-unfilled.txt"];

    /// <summary>The games' archive names (their plugins' names), and the shared one's.</summary>
    static readonly (string Game, string Plugin)[] Games =
        [("Daggerfall", "Daggerfall.esm"), ("Morrowind", "Morrowind.esm"), ("Oblivion", "Oblivion.esm"), ("Redguard", "Redguard.esm"), ("TamrielMerge Start", "TamrielMerge Start.esp")];

    /// <summary>Whether a file (path under Data) goes into an archive. Open Animation Replacer's folders stay loose: it finds them by
    /// listing Data's folders, which can't see into an archive.</summary>
    public static bool IsPacked(string rel) => PackedFolders.Contains(Top(rel), StringComparer.OrdinalIgnoreCase)
        && !rel.Contains("OpenAnimationReplacer", StringComparison.OrdinalIgnoreCase);

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
    /// With <paramref name="also"/> (the output folder, when <paramref name="outDir"/> is the build staged on a
    /// solid-state drive), what is only there (the recorded voices, the videos) comes from it, the rest from the stage.
    /// </summary>
    public static Result Install(string outDir, string data, IReadOnlyList<string> plugins, Action<string> log, string? also = null)
    {
        var infos = also is not null && Directory.Exists(also) ? Listing(also) : new Dictionary<string, (long Size, long Ticks)>(StringComparer.OrdinalIgnoreCase);
        var source = infos.Keys.ToDictionary(k => k, k => Path.Combine(also!, k), StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, info) in Listing(outDir)) { infos[rel] = info; source[rel] = Path.Combine(outDir, rel); }
        var files = infos.Keys.ToList();
        var packed = files.Where(IsPacked).ToList();
        var loose = files.Where(f => !IsPacked(f)).ToList();
        var packedSet = packed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A game whose archives in Data were made from exactly these files (paths, sizes, times: the manifest) and are
        // still as made is kept as it is (user, 2026-10-25: "it shouldnt take that long"): every install had read back and
        // packed all 35 GB, 2.4 hours on a hard disk that the reading kept busy, when only some games' files had changed.
        var manifest = ReadManifest(data);
        var signatures = packed.GroupBy(GroupOf).ToDictionary(g => g.Key, g => Signature(infos, g, HasPlugin(g.Key)), StringComparer.OrdinalIgnoreCase);
        bool HasPlugin(string game) { var plugin = Games.First(x => x.Game == game).Plugin; return plugins.Contains(plugin, StringComparer.OrdinalIgnoreCase) || File.Exists(Path.Combine(data, plugin)); }
        var kept = signatures.Where(s => manifest.TryGetValue(s.Key, out var m) && m.Inputs == s.Value && m.Archives.All(a => Unchanged(data, a)))
            .Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Data is made to hold this conversion (user, 2026-10-16: "when deployed with the tool to skyrim it needs to remove
        // stale installation"): TamrielMerge's plugins and archives there that it doesn't have go (an earlier split of
        // the land into more plugins, the archives of an earlier install, which are remade below, retired plugins).
        var topHere = files.Where(f => !f.Contains('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = new List<string>();
        // Archives an install stopped part-way through was still writing.
        foreach (var f in Directory.EnumerateFiles(data, "*.bsa.tmp").Where(f => OursTop.IsMatch(Path.GetFileNameWithoutExtension(f))).ToList()) File.Delete(f);
        foreach (var f in Directory.EnumerateFiles(data).Where(f => OursTop.IsMatch(Path.GetFileName(f)) || Retired.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList())
        {
            string name = Path.GetFileName(f);
            if (topHere.Contains(name)) continue;
            // A game's archives and their light plugins stay until its new ones are made (then those it no longer has
            // go): 2026-10-25, the PC hung part-way through an install that had deleted them first, and Data was left
            // without Oblivion's, Redguard's and the shared archives.
            if (signatures.Keys.Any(game => IsArchiveOf(name, game))) continue;
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

        // The loose rest, but what Data has already as it is (same size and time: a copy keeps its source's time).
        int sameAlready = 0;
        foreach (var rel in loose)
        {
            string target = Path.Combine(data, rel);
            var t = new FileInfo(target);
            if (t.Exists && t.Length == infos[rel].Size && t.LastWriteTimeUtc.Ticks == infos[rel].Ticks) { sameAlready++; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source[rel], target, overwrite: true);
        }
        log($"  {loose.Count - sameAlready:N0} files copied as they are (plugins, archives made already, SKSE plugins, videos, interface), {sameAlready:N0} there already");
        // What was put there, for the uninstaller (the archives are named in the manifest and by their game).
        Uninstaller.Record(data, loose);

        // The archives, a game at a time.
        var extras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var archives = new List<string>();
        long raw = 0, stored = 0;
        var reading = new System.Diagnostics.Stopwatch(); var packing = new System.Diagnostics.Stopwatch(); var writing = new System.Diagnostics.Stopwatch();
        var made = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in packed.GroupBy(GroupOf))
        {
            var (game, plugin) = Games.First(g => g.Game == group.Key);
            if (kept.Contains(game))
            {
                var m = manifest[game];
                archives.AddRange(m.Archives.Select(a => a.Name));
                if (m.Extras.Count > 0) (extras.TryGetValue(plugin, out var l) ? l : extras[plugin] = []).AddRange(m.Extras);
                made[game] = m;
                log($"  {game}: its {m.Archives.Count} archives kept (its {group.Count():N0} files unchanged since they were made)");
                continue;
            }
            int archivesBefore = archives.Count;
            int extrasBefore = extras.TryGetValue(plugin, out var eb) ? eb.Count : 0;
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
                // Written whole under another name, then put in place of the old one (which stays usable till then).
                BsaWriter.Write(Path.Combine(data, name + ".tmp"), current, currentCompressed);
                File.Move(Path.Combine(data, name + ".tmp"), Path.Combine(data, name), overwrite: true);
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
                // Read in the order the files lie on their disk (user, 2026-10-25: "how long"): read by path, each of the
                // 413,000 files of Daggerfall's and Oblivion's archives on the output's hard disk cost a seek, about 55 a
                // second (1.3 MB/s); by where they start on the disk the reading runs on. The archive sorts its own
                // entries (by hash), so which file is read when changes nothing in it.
                var set = DiskOrder(group.Where(rel => IsAudio(rel) != compress).Order(StringComparer.OrdinalIgnoreCase), source);
                // Read in order (the output may be on a hard disk), packed on every core.
                for (int at = 0; at < set.Count;)
                {
                    var batch = new List<(string Rel, byte[] Raw)>();
                    long batchBytes = 0;
                    reading.Start();
                    while (at < set.Count && batchBytes < 256L << 20)
                    {
                        var bytes = File.ReadAllBytes(source[set[at]]);
                        batch.Add((set[at], bytes));
                        batchBytes += bytes.Length;
                        at++;
                    }
                    reading.Stop();
                    var done = new BsaWriter.Stored[batch.Count];
                    packing.Start();
                    Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i => done[i] = compress ? BsaWriter.Pack(batch[i].Rel, batch[i].Raw) : new BsaWriter.Stored(batch[i].Rel, batch[i].Raw, false));
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
            var newExtras = extras.TryGetValue(plugin, out var ea) ? ea.Skip(extrasBefore).ToList() : [];
            var newArchives = archives.Skip(archivesBefore).ToList();
            // The game's old archives and light plugins its new set no longer has (fewer than before).
            foreach (var old in Directory.EnumerateFiles(data).Select(Path.GetFileName).OfType<string>()
                         .Where(n => IsArchiveOf(n, game) && !newArchives.Contains(n, StringComparer.OrdinalIgnoreCase) && !newExtras.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                File.Delete(Path.Combine(data, old));
                if (old.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) gone.Add(old);
            }
            made[game] = new ManifestEntry(signatures[game], newArchives.Select(a => Stamp(data, a)).ToList(), newExtras);
            // Recorded game by game, the games not reached yet as they were: an install stopped part-way leaves Data and its
            // manifest saying the same.
            WriteManifest(data, manifest.Where(m => !made.ContainsKey(m.Key)).Concat(made).ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase));
        }
        WriteManifest(data, made);
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

    /// <summary>
    /// <paramref name="rels"/> (in path order) in the order their files lie on disk where that disk seeks: by drive, then
    /// by the first cluster each starts at (FSCTL_GET_RETRIEVAL_POINTERS); files kept in their MFT record (tiny) and those
    /// on solid-state drives keep their path order, first.
    /// </summary>
    static List<string> DiskOrder(IEnumerable<string> rels, Dictionary<string, string> source)
    {
        var seeks = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var list = rels.Select((rel, i) => (Rel: rel, Index: i, Path: source[rel])).ToList();
        var keyed = new (string Rel, string Root, long Lcn, int Index)[list.Count];
        Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var (rel, index, path) = list[i];
            string root = Path.GetPathRoot(path) ?? "";
            bool onDisk = seeks.GetOrAdd(root, r => Util.Drives.Seeks(r) == true);
            keyed[i] = (rel, root, onDisk ? FirstCluster(path) : -1, index);
        });
        return keyed.OrderBy(k => k.Lcn < 0 ? 0 : 1).ThenBy(k => k.Root, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Lcn).ThenBy(k => k.Index).Select(k => k.Rel).ToList();
    }

    /// <summary>The cluster a file's data starts at on its volume; -1 when it has none of its own (kept in its MFT record) or
    /// it can't be asked.</summary>
    static long FirstCluster(string path)
    {
        using var h = CreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return -1;
        long startVcn = 0;
        var outBuf = new byte[64];
        bool ok = DeviceIoControl(h, 0x00090073 /* FSCTL_GET_RETRIEVAL_POINTERS */, ref startVcn, 8, outBuf, outBuf.Length, out int got, IntPtr.Zero);
        if (!ok && System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */) return -1;
        // RETRIEVAL_POINTERS_BUFFER: ExtentCount (4, padded to 8), StartingVcn (8), then { NextVcn (8), Lcn (8) }...
        if (got < 32 || BitConverter.ToInt32(outBuf, 0) == 0) return -1;
        return BitConverter.ToInt64(outBuf, 24);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, ref long input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>An archive as made: its name in Data, size and write time.</summary>
    public sealed record ArchiveStamp(string Name, long Size, long Ticks);

    /// <summary>A game's archives in Data and what they were made from (<see cref="Signature"/>), with the light plugins
    /// carrying the archives past its first two.</summary>
    public sealed record ManifestEntry(string Inputs, List<ArchiveStamp> Archives, List<string> Extras);

    const string ManifestName = "TamrielMerge Archives.json";

    static ArchiveStamp Stamp(string data, string name)
    {
        var fi = new FileInfo(Path.Combine(data, name));
        return new ArchiveStamp(name, fi.Length, fi.LastWriteTimeUtc.Ticks);
    }

    static bool Unchanged(string data, ArchiveStamp a)
    {
        var fi = new FileInfo(Path.Combine(data, a.Name));
        return fi.Exists && fi.Length == a.Size && fi.LastWriteTimeUtc.Ticks == a.Ticks;
    }

    /// <summary>The output's files (paths under it) with their sizes and write times, from one listing of its folders (a
    /// look at each file on its own seeks the hard disk 166,000 times); not the run's reports.</summary>
    static Dictionary<string, (long Size, long Ticks)> Listing(string outDir) =>
        new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(outDir, f.FullName), f.Length, f.LastWriteTimeUtc.Ticks))
            .Where(f => !Reports.Contains(f.Rel, StringComparer.OrdinalIgnoreCase) && !f.Rel.Equals(PluginPack.ManifestName, StringComparison.OrdinalIgnoreCase) && !f.Rel.EndsWith(".voicemap.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Rel, f => (f.Length, f.Ticks), StringComparer.OrdinalIgnoreCase);

    /// <summary>A game's packed files as their archives are made from them: every path, size and write time (a file built
    /// again has a new time), and whether the game's plugin is there (which names its archives).</summary>
    static string Signature(Dictionary<string, (long Size, long Ticks)> infos, IEnumerable<string> rels, bool hasPlugin)
    {
        var sb = new System.Text.StringBuilder(hasPlugin ? "plugin\n" : "no plugin\n");
        foreach (var rel in rels.Order(StringComparer.OrdinalIgnoreCase))
            sb.Append(rel.ToLowerInvariant()).Append('|').Append(infos[rel].Size).Append('|').Append(infos[rel].Ticks).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// Records the archives in <paramref name="data"/> as made from <paramref name="outDir"/>'s files now (the manifest a
    /// later install keeps unchanged games' archives by), for an install made before the manifest: right after one, when
    /// they are. Returns each game's archive count.
    /// </summary>
    public static Dictionary<string, int> RecordInstalled(string outDir, string data, IReadOnlyList<string> plugins)
    {
        var infos = Listing(outDir);
        var made = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in infos.Keys.Where(IsPacked).GroupBy(GroupOf))
        {
            var (game, plugin) = Games.First(g => g.Game == group.Key);
            bool hasPlugin = plugins.Contains(plugin, StringComparer.OrdinalIgnoreCase) || File.Exists(Path.Combine(data, plugin));
            var names = Directory.EnumerateFiles(data).Select(Path.GetFileName).OfType<string>().Where(n => IsArchiveOf(n, game)).ToList();
            var bsas = names.Where(n => n.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).Select(n => Stamp(data, n)).ToList();
            if (bsas.Count == 0) continue;
            made[game] = new ManifestEntry(Signature(infos, group, hasPlugin), bsas, names.Where(n => n.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
        WriteManifest(data, made);
        return made.ToDictionary(m => m.Key, m => m.Value.Archives.Count);
    }

    static Dictionary<string, ManifestEntry> ReadManifest(string data)
    {
        try
        {
            string path = Path.Combine(data, ManifestName);
            return File.Exists(path)
                ? new Dictionary<string, ManifestEntry>(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(File.ReadAllText(path)) ?? [], StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    static void WriteManifest(string data, Dictionary<string, ManifestEntry> made) =>
        File.WriteAllText(Path.Combine(data, ManifestName), System.Text.Json.JsonSerializer.Serialize(made, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

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
