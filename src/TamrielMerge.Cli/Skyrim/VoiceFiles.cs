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

    /// <summary>A .wav (and its text, for the lip sync) as a .fuz at <paramref name="fuzPath"/>. False when the sound
    /// can't be encoded or no lip sync can be made for it (no file is written then).</summary>
    public static bool Pack(string skyrimData, string wav, string text, string fuzPath)
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
        foreach (var (name, attempt) in attempts.DistinctBy(a => a.Text))
        {
            if (File.Exists(lip)) File.Delete(lip);
            bool finished = Run(lipGen, $"\"{wav}\" \"{attempt}\"", Path.GetDirectoryName(lipGen), 60_000);
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
        int already = allFiles.Count(f => File.Exists(Path.Combine(dataDir, f)));
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
            var todo = targets.Where(f => !File.Exists(f)).ToList();
            Interlocked.Add(ref existing, targets.Count - todo.Count);
            if (todo.Count == 0) return;
            string wav = Path.Combine(wavDir, line.Key + ".wav");
            if (!File.Exists(wav)) { Interlocked.Add(ref missing, todo.Count); return; }
            // A finished voice file of this line, else one packed now from a copy of its .wav.
            string? made = targets.FirstOrDefault(File.Exists);
            if (made is null)
            {
                string work = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".wav");
                File.Copy(wav, work);
                try { if (Pack(skyrimData, work, line.First().Text, todo[0])) { made = todo[0]; Interlocked.Increment(ref packed); } }
                finally { File.Delete(work); }
                if (made is null) return;
            }
            foreach (var fuz in todo.Where(f => !File.Exists(f)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fuz)!);
                File.Copy(made, fuz + ".tmp", true);   // renamed when whole, as WriteFuz does
                File.Move(fuz + ".tmp", fuz, true);
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
