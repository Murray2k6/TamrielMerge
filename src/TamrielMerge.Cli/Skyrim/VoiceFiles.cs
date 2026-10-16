using System.Diagnostics;
using Mutagen.Bethesda;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Skyrim voice files. A .fuz is "FUZE", version 1, the lip data's size, the lip data (.lip, mouth movement), then
/// the sound as xWMA (.xwm, a RIFF file). The Creation Kit's tools make the parts: Tools\Audio\xWMAEncode.exe turns
/// a .wav into .xwm (and back), Tools\LipGen\LipGenerator\LipGenerator.exe makes a .lip from a .wav and its text.
/// </summary>
public static class VoiceFiles
{
    static string Tools(string skyrimData) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!, "Tools");

    /// <summary>
    /// Reference recordings for voice cloning: for each voice type, a few of its vanilla lines of middling length from
    /// the voice archives, decoded to .wav in refDir\&lt;voice type&gt;\. Returns how many files were written.
    /// </summary>
    public static int ExtractReferences(string skyrimData, IEnumerable<string> voiceTypes, string refDir, int perVoice = 4)
    {
        var archives = Directory.GetFiles(skyrimData, "Skyrim - Voices*.bsa").Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, b)).ToList();
        var files = archives.SelectMany(a => a.Files).Where(f => f.Path.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase)).ToList();
        string encoder = Path.Combine(Tools(skyrimData), "Audio", "xWMAEncode.exe");
        int written = 0;
        foreach (var voice in voiceTypes)
        {
            string prefix = $@"sound\voice\skyrim.esm\{voice.ToLowerInvariant()}\";
            // Middling files (about 4-8 seconds of speech) are full sentences with little silence.
            var picks = files.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.Size is > 45_000 and < 90_000)
                .OrderBy(f => VanillaActors.StableHash(f.Path)).Take(perVoice).ToList();
            string dir = Path.Combine(refDir, voice);
            Directory.CreateDirectory(dir);
            foreach (var f in picks)
            {
                string wav = Path.Combine(dir, Path.GetFileNameWithoutExtension(f.Path) + ".wav");
                if (File.Exists(wav)) { written++; continue; }
                var fuz = f.GetBytes();
                int lipSize = BitConverter.ToInt32(fuz, 8);
                string xwm = Path.ChangeExtension(wav, ".xwm");
                File.WriteAllBytes(xwm, fuz[(12 + lipSize)..]);
                Run(encoder, $"\"{xwm}\" \"{wav}\"");
                File.Delete(xwm);
                if (File.Exists(wav)) written++;
            }
        }
        return written;
    }

    /// <summary>
    /// Packed voice files kept by what they are made of (user, 2026-10-15: "fix everything and the gaps"; a voice
    /// file is named by its line's FormID, so when the FormIDs shift every line was lip-synced again by LipGenerator,
    /// over an hour for Oblivion's 15,000 lines): tools\lipcache\&lt;key[..2]&gt;\&lt;key&gt;.fuz, the key a hash of the
    /// recording's sound and the line's text (<see cref="CacheKey"/>). It outlives the conversions; a renumbered line,
    /// or the same recording and text in another voice type, takes its .fuz from here.
    /// </summary>
    public static string CacheDir => CacheDirOverride ?? Path.Combine(Setup.Dependencies.Root, "lipcache");
    /// <summary>Another cache folder (tests).</summary>
    public static string? CacheDirOverride { get; set; }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> KeyLocks = new();
    static int cacheHits, cacheMisses;
    /// <summary>Voice files taken from the cache, and packed anew, since the program started.</summary>
    public static (int Hits, int Packed) CacheStats => (Volatile.Read(ref cacheHits), Volatile.Read(ref cacheMisses));

    /// <summary>
    /// When lip sync became trustworthy: before it, LipGenerator ran side by side in one working folder and lines took
    /// each other's sound (2026-10-16: 16 lines at once came out 10-40% off from the same lines alone, two of them
    /// alike). A voice file (or cache entry) written earlier is packed again.
    /// </summary>
    public static readonly DateTime LipSyncFixed = new DateTime(2026, 10, 16, 7, 0, 0, DateTimeKind.Local).ToUniversalTime();

    /// <summary>A voice file that is there and was packed after <see cref="LipSyncFixed"/>.</summary>
    public static bool Current(string fuz) => File.Exists(fuz) && File.GetLastWriteTimeUtc(fuz) >= LipSyncFixed;

    /// <summary>The cache's key for a recording and its text: the first 32 hex digits of SHA-256 over the .wav's bytes,
    /// a zero byte and the text (UTF-8).</summary>
    public static string CacheKey(string wav, string text)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(wav));
        hash.AppendData([0]);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.GetHashAndReset())[..32].ToLowerInvariant();
    }

    /// <summary>A .wav (and its text, for the lip sync) as a .fuz at <paramref name="fuzPath"/>, from the cache
    /// (<see cref="CacheDir"/>) when this recording and text were packed before. False when the sound can't be encoded
    /// or no lip sync can be made for it (no file is written then).</summary>
    public static bool Pack(string skyrimData, string wav, string text, string fuzPath)
    {
        string key = CacheKey(wav, text);
        string cached = Path.Combine(CacheDir, key[..2], key + ".fuz");
        // One line at a time per key: the same recording and text asked for at once (several voice types) is packed
        // once and the others take it from the cache.
        lock (KeyLocks.GetOrAdd(key, _ => new object()))
        {
            if (Current(cached))
            {
                Place(cached, fuzPath);
                Interlocked.Increment(ref cacheHits);
                return true;
            }
            if (!PackNew(skyrimData, wav, text, fuzPath)) return false;
            Interlocked.Increment(ref cacheMisses);
            try { Place(fuzPath, cached); }
            catch (IOException) { }   // a full or read-only cache only costs the next run its time
            catch (UnauthorizedAccessException) { }
            return true;
        }
    }

    /// <summary>A whole copy of <paramref name="from"/> at <paramref name="to"/>: a hard link when both are on one
    /// NTFS volume (no room taken twice; every writer here replaces a file by renaming, never rewrites it in place),
    /// else a copy. Made under a temporary name and renamed, so a stopped run never leaves a partial file.</summary>
    static void Place(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        string tmp = to + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        if (!CreateHardLink(tmp, from, IntPtr.Zero)) File.Copy(from, tmp, true);
        File.Move(tmp, to, true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    static bool PackNew(string skyrimData, string wav, string text, string fuzPath)
    {
        string tools = Tools(skyrimData);
        wav = Path.GetFullPath(wav);   // LipGenerator runs from its own folder
        string xwm = Path.ChangeExtension(wav, ".xwm");
        Run(Path.Combine(tools, "Audio", "xWMAEncode.exe"), $"-b 48000 \"{wav}\" \"{xwm}\"");
        if (!File.Exists(xwm)) return false;
        try
        {
            if (MakeLip(skyrimData, wav, text, out _) is not { } lipData) return false;
            WriteFuz(fuzPath, lipData, File.ReadAllBytes(xwm));
            return true;
        }
        finally { File.Delete(xwm); }
    }

    static void WriteFuz(string fuzPath, byte[] lipData, byte[] xwmData)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fuzPath))!);
        // Written under a temporary name and renamed, so a stopped run never leaves a partial file that looks done.
        // A stopped run's temporary file goes first: it may be a hard link to the cache (Place), which writing into
        // would change.
        File.Delete(fuzPath + ".tmp");
        using (var w = new BinaryWriter(File.Create(fuzPath + ".tmp")))
        {
            w.Write("FUZE"u8.ToArray()); w.Write(1); w.Write(lipData.Length); w.Write(lipData); w.Write(xwmData);
        }
        File.Move(fuzPath + ".tmp", fuzPath, true);
    }

    /// <summary>
    /// The lip sync (.lip) for a .wav and its text, from the Creation Kit's LipGenerator. It analyses the speech against
    /// the words; on some lines it never finishes (unusual characters, punctuation it can't place) or writes nothing,
    /// so each attempt has a time limit and the text is simplified step by step: as written, then plain ASCII, then
    /// words only, then the words of each sentence on their own. Null only when no attempt gives a .lip.
    /// </summary>
    public static byte[]? MakeLip(string skyrimData, string wav, string text, out string how)
    {
        string lipGen = Path.Combine(Tools(skyrimData), "LipGen", "LipGenerator", "LipGenerator.exe");
        wav = Path.GetFullPath(wav);
        string lip = Path.ChangeExtension(wav, ".lip");
        string ascii = new string(text.Normalize(System.Text.NormalizationForm.FormKD).Where(c => c < 128).ToArray())
            .Replace("\"", "'").Replace("...", ".").Replace("--", ", ");
        string words = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(ascii, @"[^A-Za-z0-9' ]+", " "), @"\s+", " ").Trim();
        string plainWords = System.Text.RegularExpressions.Regex.Replace(words, @"'", "");
        var attempts = new (string How, string Text)[] { ("text", text.Replace("\"", "'")), ("ascii", ascii), ("words", words), ("plain words", plainWords) };
        // A copy of LipGenerator of its own (LipSlot), run in its own folder.
        var slot = TakeLipSlot(lipGen);
        try
        {
            foreach (var (name, attempt) in attempts.DistinctBy(a => a.Text))
            {
                if (File.Exists(lip)) File.Delete(lip);
                // A line takes about a twentieth of its length; one that runs past this has looped.
                bool finished = Run(slot.Exe, $"\"{wav}\" \"{attempt}\"", slot.Dir, LipTimeLimitMs(wav));
                if (finished && File.Exists(lip) && new FileInfo(lip).Length > 0)
                {
                    var data = File.ReadAllBytes(lip);
                    File.Delete(lip);
                    how = name;
                    return data;
                }
            }
            if (File.Exists(lip)) File.Delete(lip);
            how = "failed";
            return null;
        }
        finally { LipSlots.Add(slot); }
    }

    /// <summary>
    /// A LipGenerator to run and the folder to run it in, used by one run at a time. Two things kept runs from going
    /// side by side (2026-10-16): LipGenerator resamples the sound into tmp16khz.wav in its working folder, and every
    /// run used LipGenerator's own folder, so runs read each other's sound (two different lines came out byte for byte
    /// the same); and the Fonix library inside takes a named mutex, FonixMemoryMutex, for the whole analysis, so all
    /// of them on the machine went one at a time (16 alive, 5 lines a second, the CPU a fifth busy). So each slot is a
    /// copy (tools\lipgen\NN: LipGenerator.exe with the mutex named FonixMemoryMutNN, and FonixData.cdf) in a folder of
    /// its own, held by this process through a lock file. A LipGenerator whose mutex name isn't found (another build)
    /// runs as it is, each run in a folder of its own: correct, one at a time.
    /// </summary>
    sealed record LipSlot(string Exe, string Dir, FileStream? Hold);

    static readonly System.Collections.Concurrent.ConcurrentBag<LipSlot> LipSlots = [];
    static int lipSlotNext = -1;
    static readonly byte[] FonixMutex = "FonixMemoryMutex"u8.ToArray();

    static LipSlot TakeLipSlot(string lipGen)
    {
        if (LipSlots.TryTake(out var free)) return free;
        string root = Path.Combine(Setup.Dependencies.Root, "lipgen");
        // Slot numbers no other process holds (two of ours at once, a conversion and the voice generator).
        for (int n = Interlocked.Increment(ref lipSlotNext); n < 100; n = Interlocked.Increment(ref lipSlotNext))
        {
            string dir = Path.Combine(root, n.ToString("D2"));
            try
            {
                Directory.CreateDirectory(dir);
                var hold = new FileStream(Path.Combine(dir, "in-use.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                if (CopyLipGenerator(lipGen, dir, n) is { } exe) return new LipSlot(exe, dir, hold);
                hold.Dispose();
                break;   // no mutex name to change: the original, below
            }
            catch (IOException) { }   // held by another process: the next number
            catch (UnauthorizedAccessException) { break; }
        }
        string own = Path.Combine(Path.GetTempPath(), "TamrielMergeLipGen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(own);
        return new LipSlot(lipGen, own, null);
    }

    /// <summary>LipGenerator.exe (and its FonixData.cdf) copied into <paramref name="dir"/> with its mutex named
    /// FonixMemoryMut<paramref name="n"/>; null when the name isn't in it exactly once.</summary>
    static string? CopyLipGenerator(string lipGen, string dir, int n)
    {
        string exe = Path.Combine(dir, "LipGenerator.exe"), data = Path.Combine(dir, "FonixData.cdf");
        string sourceData = Path.Combine(Path.GetDirectoryName(lipGen)!, "FonixData.cdf");
        var source = new FileInfo(lipGen);
        if (File.Exists(exe) && File.Exists(data) && new FileInfo(exe).Length == source.Length && File.GetLastWriteTimeUtc(exe) >= source.LastWriteTimeUtc
            && new FileInfo(data).Length == new FileInfo(sourceData).Length)
            return exe;
        var bytes = File.ReadAllBytes(lipGen);
        int at = bytes.AsSpan().IndexOf(FonixMutex);
        if (at < 0 || bytes.AsSpan(at + 1).IndexOf(FonixMutex) >= 0) return null;
        var tag = System.Text.Encoding.ASCII.GetBytes(n.ToString("D2"));
        bytes[at + FonixMutex.Length - 2] = tag[0];
        bytes[at + FonixMutex.Length - 1] = tag[1];
        File.Copy(sourceData, data, true);
        File.WriteAllBytes(exe + ".tmp", bytes);
        File.Move(exe + ".tmp", exe, true);
        return exe;
    }

    /// <summary>A LipGenerator run's time limit: 15 s, plus 2 s for each second of sound (a 16-bit .wav's length from
    /// its size and rate), at most a minute.</summary>
    static int LipTimeLimitMs(string wav)
    {
        double seconds = 30;
        try
        {
            using var r = new BinaryReader(File.OpenRead(wav));
            r.BaseStream.Position = 24;
            int rate = r.ReadInt32(), bytesPerSecond = r.ReadInt32();
            if (rate > 0 && bytesPerSecond > 0) seconds = (r.BaseStream.Length - 44) / (double)bytesPerSecond;
        }
        catch (IOException) { }   // EndOfStreamException too: a file too short to be a .wav
        return (int)Math.Clamp(15_000 + seconds * 2_000, 15_000, 60_000);
    }

    /// <summary>
    /// Gives every .fuz under <paramref name="dataDir"/> that has no lip sync one: its sound decoded back to .wav,
    /// LipGenerator run with its line's text (<paramref name="textOf"/>: .fuz path relative to dataDir -&gt; text),
    /// and the file rewritten. Returns (fixed, still without lip sync, the ways that worked).
    /// </summary>
    public static (int Fixed, List<string> Failed, Dictionary<string, int> How) Relip(string skyrimData, string dataDir, Func<string, string?> textOf)
    {
        string encoder = Path.Combine(Tools(skyrimData), "Audio", "xWMAEncode.exe");
        string work = Path.Combine(Path.GetTempPath(), "TamrielMergeRelip");
        Directory.CreateDirectory(work);
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var how = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int fixedCount = 0;
        var lipless = Directory.EnumerateFiles(dataDir, "*.fuz", SearchOption.AllDirectories).Where(f =>
        {
            using var r = new BinaryReader(File.OpenRead(f));
            return r.BaseStream.Length >= 12 && r.ReadBytes(8).Length == 8 && r.ReadInt32() == 0;
        }).ToList();
        Parallel.ForEach(lipless, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, fuz =>
        {
            string rel = Path.GetRelativePath(dataDir, fuz);
            if (textOf(rel) is not { } text) { failed.Add(rel + " (no text)"); return; }
            var bytes = File.ReadAllBytes(fuz);
            byte[] xwmData = bytes[12..];
            string id = Guid.NewGuid().ToString("N"), xwm = Path.Combine(work, id + ".xwm"), wav = Path.Combine(work, id + ".wav");
            try
            {
                File.WriteAllBytes(xwm, xwmData);
                Run(encoder, $"\"{xwm}\" \"{wav}\"");
                if (!File.Exists(wav)) { failed.Add(rel + " (sound not decodable)"); return; }
                if (MakeLip(skyrimData, wav, text, out var way) is not { } lipData) { failed.Add($"{rel}: \"{text}\""); return; }
                WriteFuz(fuz, lipData, xwmData);
                how.AddOrUpdate(way, 1, (_, n) => n + 1);
                Interlocked.Increment(ref fixedCount);
            }
            finally { File.Delete(xwm); File.Delete(wav); }
        });
        return (fixedCount, failed.ToList(), new Dictionary<string, int>(how));
    }

    /// <summary>The generator's name for a (voice type, text) .wav: the first 20 hex digits of SHA-1 of the lower-case
    /// voice type, a newline and the text (as tools/voice/generate.py names them).</summary>
    public static string WavKey(string voice, string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(voice.ToLowerInvariant() + "\n" + text)))[..20].ToLowerInvariant();

    /// <summary>
    /// Every planned voice file whose line has been spoken (wavDir\&lt;key&gt;.wav) packed as a .fuz under dataDir at
    /// its planned path; existing .fuz files are kept. Returns (packed now, already there, not spoken yet).
    /// </summary>
    public static (int Packed, int Existing, int Missing) PackAll(string skyrimData, List<VoicePlanner.Job> jobs, string wavDir, string dataDir,
        Action<int, int>? progress = null)
    {
        int packed = 0, existing = 0, missing = 0;
        // progress(voice files ready, voice files in all), at most every few seconds.
        var allFiles = jobs.Select(j => j.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int all = allFiles.Count;
        // Those already there counted first, so the reports climb at the packing's own pace from the start.
        int already = allFiles.Count(f => Current(Path.Combine(dataDir, f)));
        long lastReport = 0;
        void Tick()
        {
            long now = Environment.TickCount64;
            if (progress is null || now - Interlocked.Read(ref lastReport) < 5000) return;
            Interlocked.Exchange(ref lastReport, now);
            progress(already + Volatile.Read(ref packed), all);
        }
        var tmpDir = Path.Combine(wavDir, "pack");
        Directory.CreateDirectory(tmpDir);
        // One spoken line (voice type and text) packed once, then copied to its other voice files (user, 2026-10-10:
        // voices too slow - a quarter of Morrowind's 130,844 voice files repeat another's line, and each was encoded and
        // lip-synced again).
        var byLine = jobs.GroupBy(j => WavKey(j.Speech, j.Text)).ToList();
        Parallel.ForEach(byLine, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, line =>
        {
            var targets = line.Select(j => Path.Combine(dataDir, j.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var todo = targets.Where(f => !Current(f)).ToList();
            Interlocked.Add(ref existing, targets.Count - todo.Count);
            if (todo.Count == 0) return;
            string wav = Path.Combine(wavDir, line.Key + ".wav");
            if (!File.Exists(wav)) { Interlocked.Add(ref missing, todo.Count); return; }
            // A finished voice file of this line, else one packed now from a copy of its .wav.
            string? made = targets.FirstOrDefault(Current);
            if (made is null)
            {
                string work = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".wav");
                File.Copy(wav, work);
                try { if (Pack(skyrimData, work, line.First().Text, todo[0])) { made = todo[0]; Interlocked.Increment(ref packed); } }
                finally { File.Delete(work); }
                if (made is null) return;
            }
            foreach (var fuz in todo.Where(f => !Current(f)))
            {
                Place(made, fuz);   // renamed when whole, as WriteFuz does
                Interlocked.Increment(ref packed);
            }
            Tick();
        });
        return (packed, existing, missing);
    }

    /// <summary>Runs a tool; false when it hadn't finished within the time limit (it is then stopped).</summary>
    static bool Run(string exe, string args, string? workDir = null, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir ?? Environment.CurrentDirectory };
        using var p = Process.Start(psi)!;
        // Output is drained asynchronously so the time limit applies: LipGenerator can loop forever on some lines.
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (p.WaitForExit(timeoutMs)) { p.WaitForExit(); return true; }
        try { p.Kill(entireProcessTree: true); } catch { }
        p.WaitForExit(5_000);
        return false;
    }
}
