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

    static readonly string[] AnyRace = ["imperial", "nord", "breton", "redguard", "high elf", "dark elf", "wood elf", "orc", "khajiit", "argonian"];

    sealed record MapEntry(uint Info, string SourcePlugin, string OriginPlugin, uint OriginId, List<MapLine> Lines);
    sealed record MapLine(int Number, string Text);

    public static (int Packed, int Existing, int Unrecorded) Build(string skyrimData, ObData data, string pluginPath, string mapPath, string outDir)
    {
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var jobs = Skyrim.VoicePlanner.Plan(plugin, skyrimData, out _);
        var map = JsonSerializer.Deserialize<List<MapEntry>>(File.ReadAllText(mapPath))!.ToDictionary(e => e.Info);

        // Every Oblivion recording by (race, sex, response FormID's low 24 bits, line).
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

        string work = Path.Combine(Path.GetTempPath(), "TamrielMergeObVoices");
        Directory.CreateDirectory(work);
        var decoded = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int packed = 0, existing = 0, unrecorded = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
        {
            string fuz = Path.Combine(outDir, job.File);
            if (File.Exists(fuz)) { Interlocked.Increment(ref existing); return; }
            // ..._<info FormID>_<line>.fuz
            var bits = Path.GetFileNameWithoutExtension(job.File).Split('_');
            if (!uint.TryParse(bits[^2], System.Globalization.NumberStyles.HexNumber, null, out uint info) || !int.TryParse(bits[^1], out int line)
                || !map.TryGetValue(info & 0xFFFFFF, out var source)) { Interlocked.Increment(ref unrecorded); return; }
            string sex = job.VoiceType.StartsWith("Female", StringComparison.OrdinalIgnoreCase) ? "f" : "m";
            var races = (RaceFolders.TryGetValue(job.VoiceType, out var r) ? r : []).Concat(AnyRace);
            string? mp3 = races.Select(race => recordings.GetValueOrDefault((race, sex, source.OriginId & 0xFFFFFF, line))).FirstOrDefault(p => p is not null);
            if (mp3 is null) { Interlocked.Increment(ref unrecorded); return; }
            string? wav = decoded.GetOrAdd(mp3, path => Decode(data, path, work));
            if (wav is null) { Interlocked.Increment(ref unrecorded); return; }
            // Pack from a copy: several voice types can share one recording.
            string copy = Path.Combine(work, Guid.NewGuid().ToString("N") + ".wav");
            File.Copy(wav, copy);
            try { if (Skyrim.VoiceFiles.Pack(skyrimData, copy, job.Text, fuz)) Interlocked.Increment(ref packed); }
            finally { File.Delete(copy); }
        });
        foreach (var wav in decoded.Values.OfType<string>()) File.Delete(wav);
        return (packed, existing, unrecorded);
    }

    /// <summary>An Oblivion MP3 as a 44.1 kHz mono 16-bit .wav (Windows Media Foundation), or null if it can't be read.</summary>
    public static string? Decode(ObData data, string path, string work)
    {
        var bytes = data.Read(path);
        if (bytes is null) return null;
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
