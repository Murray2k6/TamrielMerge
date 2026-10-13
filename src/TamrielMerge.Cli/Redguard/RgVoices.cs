using System.Text.Json;
using Mutagen.Bethesda.Skyrim;
using NAudio.Wave;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's own voice acting as the converted dialogue's voice files. Redguard.voicemap.json (written by the build)
/// gives, for each voiced response line, the ENGLISH.RTX entry it came from; the line's recording (8- or 16-bit mono
/// PCM at 11 or 22 kHz) is resampled to 44.1 kHz 16-bit with Windows Media Foundation, as Skyrim's voices are, and
/// packed as a .fuz with new lip data (VoiceFiles.Pack) for every voice type that can say it (VoicePlanner).
/// </summary>
public static class RgVoices
{
    public static (int Packed, int Existing, int Missing) Build(string skyrimData, string rgDir, string pluginPath, string mapPath, string outDir)
    {
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var jobs = Skyrim.VoicePlanner.Plan(plugin, skyrimData, out _);
        var map = JsonSerializer.Deserialize<List<RgPluginBuilder.VoiceLine>>(File.ReadAllText(mapPath))!
            .GroupBy(v => (v.Info & 0xFFFFFF, v.Number)).ToDictionary(g => g.Key, g => g.First().Tag);
        var rtx = RgRtx.Load(Path.Combine(rgDir, "ENGLISH.RTX")) ?? throw new FileNotFoundException("ENGLISH.RTX");
        string work = Path.Combine(Path.GetTempPath(), "TamrielMergeRgVoices");
        Directory.CreateDirectory(work);
        int packed = 0, existing = 0, missing = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
        {
            string fuz = Path.Combine(outDir, job.File);
            if (File.Exists(fuz)) { Interlocked.Increment(ref existing); return; }
            var bits = Path.GetFileNameWithoutExtension(job.File).Split('_');
            if (!uint.TryParse(bits[^2], System.Globalization.NumberStyles.HexNumber, null, out uint info) || !int.TryParse(bits[^1], out int line)
                || !map.TryGetValue((info & 0xFFFFFF, line), out var tag) || !rtx.Entries.TryGetValue(tag, out var entry))
            { Interlocked.Increment(ref missing); return; }
            string raw = Path.Combine(work, Guid.NewGuid().ToString("N") + ".raw.wav"), wav = Path.Combine(work, Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                if (!rtx.WriteWav(entry, raw)) { Interlocked.Increment(ref missing); return; }
                using (var reader = new WaveFileReader(raw))
                using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 1)) { ResamplerQuality = 60 })
                    WaveFileWriter.CreateWaveFile(wav, resampler);
                if (Skyrim.VoiceFiles.Pack(skyrimData, wav, job.Text, fuz)) Interlocked.Increment(ref packed);
                else Interlocked.Increment(ref missing);
            }
            catch { Interlocked.Increment(ref missing); }
            finally { File.Delete(raw); File.Delete(wav); }
        });
        return (packed, existing, missing);
    }
}
