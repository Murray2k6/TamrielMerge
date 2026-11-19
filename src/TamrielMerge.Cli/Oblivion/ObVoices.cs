using System.Collections.Concurrent;
using System.Text.Json;
using Mutagen.Bethesda.Skyrim;
using NAudio.Wave;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's own voice acting as the converted dialogue's voice files. Oblivion recorded each line once per race and
/// sex: sound\voice\&lt;plugin&gt;\&lt;race&gt;\&lt;m|f&gt;\&lt;quest&gt;_&lt;topic&gt;_&lt;response FormID&gt;_&lt;line&gt;.mp3 in its voice
/// archives. Each converted line (Oblivion.voicemap.json: converted response -&gt; Oblivion response) is needed in
/// the voice types of the people who can say it (VoicePlanner); each voice type takes the recording of the Oblivion
/// race and sex it stands for (MaleNord: nord\m, FemaleEvenToned: imperial\f, ...), else any race of that sex. The MP3
/// is decoded with Windows Media Foundation (44.1 kHz mono, as Skyrim's voices) and packed as a .fuz with new lip
/// data (VoiceFiles.Pack). Nothing is synthesised: lines Oblivion didn't record stay silent.
/// </summary>
public static class ObVoices
{
    /// <summary>Oblivion voice folders (race) for each Skyrim voice type, best first.</summary>
    static readonly Dictionary<string, string[]> RaceFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaleEvenToned"] = ["imperial", "breton"], ["FemaleEvenToned"] = ["imperial", "breton"],
        ["MaleNord"] = ["nord"], ["FemaleNord"] = ["nord"],
        ["MaleDarkElf"] = ["dark elf"], ["FemaleDarkElf"] = ["dark elf"],
        ["MaleElfHaughty"] = ["high elf", "wood elf"], ["FemaleElfHaughty"] = ["high elf", "wood elf"],
        ["MaleEvenTonedAccented"] = ["redguard"], ["FemaleSultry"] = ["redguard"],
        ["MaleOrc"] = ["orc", "nord"], ["FemaleOrc"] = ["orc", "nord"],
        ["MaleKhajiit"] = ["khajiit", "argonian"], ["FemaleKhajiit"] = ["khajiit", "argonian"],
        ["MaleArgonian"] = ["argonian"], ["FemaleArgonian"] = ["argonian"],
    };

    /// <summary>
    /// The Skyrim voice type that stands for an Oblivion race's voice (the race its RACE record names for the sex, by
    /// EditorID): the one <see cref="RaceFolders"/> gives that race's recordings to. Null for a race with no folder
    /// here. Oblivion's people speak in their race's voice; until 2026-11-14 a converted person kept the voice type of
    /// the Skyrim face they were given, and Baurus, a Redguard, spoke another race's recordings (user: "baurus didnt
    /// have the right voice either").
    /// </summary>
    public static string? VoiceTypeFor(string voiceRace, bool female)
    {
        string r = new string(voiceRace.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        string sex = female ? "Female" : "Male";
        if (r.Contains("redguard")) return female ? "FemaleSultry" : "MaleEvenTonedAccented";
        if (r.Contains("imperial") || r.Contains("breton")) return sex + "EvenToned";
        if (r.Contains("nord")) return sex + "Nord";
        if (r.Contains("darkelf")) return sex + "DarkElf";
        if (r.Contains("highelf") || r.Contains("woodelf")) return sex + "ElfHaughty";
        if (r.Contains("orc")) return sex + "Orc";
        if (r.Contains("khajiit")) return sex + "Khajiit";
        if (r.Contains("argonian")) return sex + "Argonian";
        return null;
    }

    static readonly string[] AnyRace = ["imperial", "nord", "breton", "redguard", "high elf", "dark elf", "wood elf", "orc", "khajiit", "argonian"];

    sealed record MapEntry(uint Info, string SourcePlugin, string OriginPlugin, uint OriginId, List<MapLine> Lines);
    sealed record MapLine(int Number, string Text);

    static long decodeTicks, readTicks;
    static int decodes;
    /// <summary>Seconds (summed over every thread) spent reading recordings out of Oblivion's archives and decoding
    /// them, and how many were decoded.</summary>
    public static (double Read, double Decode, int Decoded) Timing =>
        (Interlocked.Read(ref readTicks) / (double)System.Diagnostics.Stopwatch.Frequency, Interlocked.Read(ref decodeTicks) / (double)System.Diagnostics.Stopwatch.Frequency, Volatile.Read(ref decodes));

    /// <param name="limit">Only the first so many voice files with a recording (a timing run); 0 for all.</param>
    public static (int Packed, int Existing, int Unrecorded) Build(string skyrimData, ObData data, string pluginPath, string mapPath, string outDir, int limit = 0, int threads = 0)
    {
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var jobs = Skyrim.VoicePlanner.Plan(plugin, skyrimData, out _);
        var sources = Sources(data, jobs, mapPath);
        if (limit > 0) jobs = [.. jobs.Where(j => sources.ContainsKey(j.File)).Take(limit)];

        string work = Path.Combine(Path.GetTempPath(), "TamrielMergeObVoices");
        Directory.CreateDirectory(work);
        var decoded = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Each recording read out of the archive once (several voice types share one).
        var raw = new ConcurrentDictionary<string, Lazy<byte[]?>>(StringComparer.OrdinalIgnoreCase);
        int packed = 0, existing = 0, unrecorded = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount }, job =>
        {
            string fuz = Path.Combine(outDir, job.File);
            if (Skyrim.VoiceFiles.Current(fuz)) { Interlocked.Increment(ref existing); return; }
            if (!sources.TryGetValue(job.File, out var mp3)) { Interlocked.Increment(ref unrecorded); return; }
            // From the cache by the recording as it lies in the archive, nothing decoded.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var bytes = raw.GetOrAdd(mp3, path => new Lazy<byte[]?>(() => data.Read(path))).Value;
            Interlocked.Add(ref readTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            if (bytes is null) { Interlocked.Increment(ref unrecorded); return; }
            string sourceKey = Skyrim.VoiceFiles.SourceKey(bytes, job.Text);
            if (Skyrim.VoiceFiles.FromCache(sourceKey, fuz)) { Interlocked.Increment(ref packed); return; }
            string? wav = decoded.GetOrAdd(mp3, path =>
            {
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return Decode(bytes, work); }
                finally { Interlocked.Add(ref decodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t1); Interlocked.Increment(ref decodes); }
            });
            if (wav is null) { Interlocked.Increment(ref unrecorded); return; }
            // Pack from a copy: several voice types can share one recording.
            string copy = Path.Combine(work, Guid.NewGuid().ToString("N") + ".wav");
            File.Copy(wav, copy);
            try { if (Skyrim.VoiceFiles.Pack(skyrimData, copy, job.Text, fuz, sourceKey)) Interlocked.Increment(ref packed); }
            finally { File.Delete(copy); }
        });
        foreach (var wav in decoded.Values.OfType<string>()) File.Delete(wav);
        return (packed, existing, unrecorded);
    }

    /// <summary>
    /// Oblivion's recording (its path in the voice archives) for each voice file it recorded: the converted response's
    /// Oblivion response (the voicemap), said by the Oblivion race and sex the voice type stands for, else any race of
    /// that sex. Voice files not in it are lines Oblivion never recorded, which the voice generator speaks.
    /// </summary>
    public static Dictionary<string, string> Sources(ObData data, IEnumerable<Skyrim.VoicePlanner.Job> jobs, string mapPath)
    {
        var map = JsonSerializer.Deserialize<List<MapEntry>>(File.ReadAllText(mapPath))!.ToDictionary(e => e.Info);
        var recordings = Recordings(data);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            // ..._<info FormID>_<line>.fuz
            var bits = Path.GetFileNameWithoutExtension(job.File).Split('_');
            if (bits.Length < 2 || !uint.TryParse(bits[^2], System.Globalization.NumberStyles.HexNumber, null, out uint info) || !int.TryParse(bits[^1], out int line)
                || !map.TryGetValue(info & 0xFFFFFF, out var source)) continue;
            if (Recording(recordings, job.VoiceType, source.OriginId, line) is { } mp3) found[job.File] = mp3;
        }
        return found;
    }

    /// <summary>
    /// The voice files Oblivion has no recording for, with why: "not Oblivion's line" (the response isn't in the
    /// voicemap: one the conversion wrote), "no words" (an empty line), "other sex only" (Oblivion recorded it, but in
    /// no voice of this sex), "never recorded" (in no voice at all).
    /// </summary>
    public static List<(Skyrim.VoicePlanner.Job Job, string Why, string Origin)> Unrecorded(string skyrimData, ObData data, string pluginPath, string mapPath)
    {
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var jobs = Skyrim.VoicePlanner.Plan(plugin, skyrimData, out _);
        var map = JsonSerializer.Deserialize<List<MapEntry>>(File.ReadAllText(mapPath))!.ToDictionary(e => e.Info);
        var recordings = Recordings(data);
        var anySex = recordings.Keys.Select(k => (k.Id, k.Line)).ToHashSet();
        var result = new List<(Skyrim.VoicePlanner.Job, string, string)>();
        foreach (var job in jobs)
        {
            var bits = Path.GetFileNameWithoutExtension(job.File).Split('_');
            if (bits.Length < 2 || !uint.TryParse(bits[^2], System.Globalization.NumberStyles.HexNumber, null, out uint info) || !int.TryParse(bits[^1], out int line)
                || !map.TryGetValue(info & 0xFFFFFF, out var source)) { result.Add((job, "not Oblivion's line", "")); continue; }
            if (Recording(recordings, job.VoiceType, source.OriginId, line) is not null) continue;
            string origin = $"{source.OriginPlugin}:{source.OriginId & 0xFFFFFF:X6}";
            if (string.IsNullOrWhiteSpace(job.Text)) result.Add((job, "no words", origin));
            else if (anySex.Contains((source.OriginId & 0xFFFFFF, line))) result.Add((job, "other sex only", origin));
            else result.Add((job, "never recorded", origin));
        }
        return result;
    }

    /// <summary>Every Oblivion recording by (race, sex, response FormID's low 24 bits, line).</summary>
    public static Dictionary<(string Race, string Sex, uint Id, int Line), string> Recordings(ObData data)
    {
        var recordings = new Dictionary<(string Race, string Sex, uint Id, int Line), string>();
        foreach (var path in data.List(@"sound\voice").Where(p => p.EndsWith(".mp3")))
        {
            var parts = path.Split('\\');
            if (parts.Length < 6) continue;
            string race = parts[3], sex = parts[4], name = Path.GetFileNameWithoutExtension(parts[^1]);
            var bits = name.Split('_');
            if (bits.Length < 3 || !int.TryParse(bits[^1], out int line)) continue;
            if (!uint.TryParse(bits[^2], System.Globalization.NumberStyles.HexNumber, null, out uint id)) continue;
            recordings.TryAdd((race, sex, id & 0xFFFFFF, line), path);
        }
        return recordings;
    }

    /// <summary>The voice types Oblivion's recordings are given to, in a fixed order (TMObState.VoiceTypes).</summary>
    public static IReadOnlyList<string> VoiceTypes { get; } = [.. RaceFolders.Keys];

    /// <summary>The recording a voice type says an Oblivion response's line with: the race and sex it stands for, else
    /// any race of that sex.</summary>
    public static string? Recording(Dictionary<(string Race, string Sex, uint Id, int Line), string> recordings, string voiceType, uint originId, int line)
    {
        string sex = voiceType.StartsWith("Female", StringComparison.OrdinalIgnoreCase) ? "f" : "m";
        var races = (RaceFolders.TryGetValue(voiceType, out var r) ? r : []).Concat(AnyRace);
        return races.Select(race => recordings.GetValueOrDefault((race, sex, originId & 0xFFFFFF, line))).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// An MP3's length in seconds, from its frames (each MPEG audio frame's header gives its size; a layer III frame
    /// holds 1152 samples in MPEG-1, 576 in MPEG-2 and 2.5), after any ID3v2 tag; null if it has no frames.
    /// </summary>
    public static double? Mp3Seconds(byte[] b)
    {
        int at = 0;
        if (b.Length > 10 && b[0] == (byte)'I' && b[1] == (byte)'D' && b[2] == (byte)'3')
            at = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        int[] mpeg1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        int[] mpeg2L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
        double seconds = 0;
        int frames = 0;
        while (at + 4 <= b.Length)
        {
            if (b[at] != 0xFF || (b[at + 1] & 0xE0) != 0xE0) { at++; continue; }
            int version = (b[at + 1] >> 3) & 3;   // 0 MPEG-2.5, 2 MPEG-2, 3 MPEG-1
            int layer = (b[at + 1] >> 1) & 3;     // 1 layer III
            int bitrateIndex = b[at + 2] >> 4, rateIndex = (b[at + 2] >> 2) & 3, padding = (b[at + 2] >> 1) & 1;
            if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) { at++; continue; }
            int rate = version switch
            {
                3 => rateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 },
                2 => rateIndex switch { 0 => 22050, 1 => 24000, _ => 16000 },
                _ => rateIndex switch { 0 => 11025, 1 => 12000, _ => 8000 },
            };
            int bitrate = (version == 3 ? mpeg1L3 : mpeg2L3)[bitrateIndex] * 1000;
            int samples = version == 3 ? 1152 : 576;
            int length = samples / 8 * bitrate / rate + padding;
            if (length < 4) { at++; continue; }
            // The encoder's Xing/Info frame, before the sound, holds none.
            int side = version == 3 ? ((b[at + 3] >> 6) == 3 ? 17 : 32) : ((b[at + 3] >> 6) == 3 ? 9 : 17);
            bool info = frames == 0 && at + 4 + side + 4 <= b.Length && System.Text.Encoding.ASCII.GetString(b, at + 4 + side, 4) is "Xing" or "Info";
            if (!info) { seconds += (double)samples / rate; frames++; }
            at += length;
        }
        return frames > 0 ? seconds : null;
    }

    /// <summary>An Oblivion MP3 as a 44.1 kHz mono 16-bit .wav (Windows Media Foundation), or null if it can't be read.</summary>
    public static string? Decode(ObData data, string path, string work) => data.Read(path) is { } bytes ? Decode(bytes, work) : null;

    /// <summary>An MP3's bytes decoded to a .wav under <paramref name="work"/> (44.1 kHz, 16 bit, mono).</summary>
    public static string? Decode(byte[] bytes, string work)
    {
        string mp3 = Path.Combine(work, Guid.NewGuid().ToString("N") + ".mp3"), wav = Path.ChangeExtension(mp3, ".wav");
        try
        {
            File.WriteAllBytes(mp3, bytes);
            using var reader = new MediaFoundationReader(mp3);
            using var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 1)) { ResamplerQuality = 60 };
            WaveFileWriter.CreateWaveFile(wav, resampler);
            return wav;
        }
        catch { return null; }
        finally { File.Delete(mp3); }
    }
}
