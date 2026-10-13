using System.Diagnostics;

namespace TamrielMerge.Daggerfall;

/// <summary>
/// Daggerfall's soundtrack: HMI songs in MIDI.BSA, the General MIDI playlists Daggerfall uses (from Daggerfall Unity
/// SongManager), and a pipeline that renders them for Skyrim: HMI -> MIDI (WildMIDI), MIDI repair and looping,
/// MIDI -> WAV (FluidSynth + GeneralUser GS), fade and normalise, WAV -> xWMA (Creation Kit xWMAEncode).
/// </summary>
public static class DfMusic
{
    /// <summary>General MIDI playlists, as Daggerfall Unity's SongManager defines them.</summary>
    public static readonly Dictionary<string, string[]> Playlists = new()
    {
        ["Dungeon"] = ["DUNGEON", "DUNGEON5", "DUNGEON6", "DUNGEON7", "DUNGEON8", "DUNGEON9", "GDNGN10", "GDNGN11", "GDUNGN4", "GDUNGN9", "04", "05", "07", "15", "28"],
        ["Sunny"] = ["GDAY___D", "SWIMMING", "GSUNNY2", "SUNNYDAY", "02", "03", "22"],
        ["Cloudy"] = ["GDAY___D", "SWIMMING", "GSUNNY2", "SUNNYDAY", "02", "03", "22", "29", "12"],
        ["Overcast"] = ["29", "12", "13", "GPALAC", "OVERCAST"],
        ["Rain"] = ["OVERLONG", "RAINING", "08"],
        ["Snow"] = ["20", "GSNOW__B", "OVERSNOW", "SNOWING"],
        ["Night"] = ["10", "11", "GCURSE", "GEERIE", "GRUINS", "18", "21"],
        ["Sneaking"] = ["GSNEAK2", "SNEAKING", "SNEAKNG2", "16", "09", "25", "30"],
        ["TempleGood"] = ["GGOOD"],
        ["TempleNeutral"] = ["GNEUT"],
        ["TempleBad"] = ["GBAD"],
        ["Tavern"] = ["SQUARE_2", "TAVERN", "FOLK1", "FOLK2", "FOLK3"],
        ["Shop"] = ["GSHOP"],
        ["MagesGuild"] = ["GMAGE_3", "MAGIC_2"],
        ["Interior"] = ["23"],
        ["Knight"] = ["17"],
        ["Palace"] = ["06"],
        ["Castle"] = ["GPALAC"],
        // Battle music. Daggerfall has none of its own; this is the Dynamic Music mod's default combat playlist for
        // Daggerfall Unity: 17 (the fighters' trainers theme) and 30 (an unused sneaking theme).
        ["Combat"] = ["17", "30"],
    };

    /// <summary>
    /// Uncut content: songs in MIDI.BSA that Daggerfall (and Daggerfall Unity) never play. D1-D10 are unused dungeon
    /// music; the sneaking songs have a playlist in Daggerfall Unity's SongManager that nothing selects. Included
    /// unless the build is run with --no-uncut.
    /// </summary>
    public static readonly Dictionary<string, string[]> UncutPlaylists = new()
    {
        ["UncutDungeon"] = ["D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10"],
        ["UncutSneaking"] = ["GSNEAK2", "SNEAKING", "SNEAKNG2", "16", "09", "25", "30"],
    };

    /// <summary>Every playlist the build uses: the standard ones, plus the uncut ones when wanted.</summary>
    public static IEnumerable<KeyValuePair<string, string[]>> AllPlaylists(bool uncut) => uncut ? Playlists.Concat(UncutPlaylists) : Playlists;

    public sealed record Tools(string WildMidi, string FluidSynth, string SoundFont, string XwmaEncode);

    /// <summary>Renders every playlist song to outDir/NAME.xwm (skipping ones already rendered). Returns name -> seconds.</summary>
    public static Dictionary<string, double> RenderAll(string arena2, Tools tools, string outDir, string workDir, bool uncut = true, string? only = null)
    {
        outDir = Path.GetFullPath(outDir);
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(workDir);
        var bsa = new DfBsa(Path.Combine(arena2, "MIDI.BSA"));
        var lengths = new Dictionary<string, double>();
        foreach (var name in AllPlaylists(uncut).SelectMany(p => p.Value).Distinct().Order().Where(n => only is null || n == only))
        {
            string xwm = Path.Combine(outDir, name + ".xwm");
            string lenFile = Path.Combine(workDir, name + ".seconds");
            if (File.Exists(xwm) && File.Exists(lenFile)) { lengths[name] = double.Parse(File.ReadAllText(lenFile)); continue; }

            string hmi = Path.Combine(workDir, name + ".hmi"), mid = Path.Combine(workDir, name + ".mid"), fixedMid = Path.Combine(workDir, name + ".loop.mid"), wav = Path.Combine(workDir, name + ".wav");
            File.WriteAllBytes(hmi, bsa.Record(name + ".HMI"));
            Run(tools.WildMidi, workDir, $"--tomidi={mid}", hmi);
            double seconds = Midi.RepairAndLoop(mid, fixedMid, minSeconds: 150);
            Run(tools.FluidSynth, workDir, "-ni", "-q", "-g", "0.6", "-r", "44100", "-R", "1", "-C", "1", "-F", wav, tools.SoundFont, fixedMid);
            Wav.FadeAndNormalise(wav, fadeSeconds: 6, seconds + 4);
            Run(tools.XwmaEncode, workDir, "-b", "192000", wav, xwm);
            File.Delete(wav);
            File.WriteAllText(lenFile, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lengths[name] = seconds;
            Console.WriteLine($"  rendered {name}: {seconds:F0}s");
        }
        return lengths;
    }

    static void Run(string exe, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}):\n{output}");
    }
}

/// <summary>Minimal standard MIDI file reader/writer for repairing converted HMI songs.</summary>
static class Midi
{
    sealed record Event(long Tick, byte[] Bytes, bool IsEndOfTrack);

    /// <summary>
    /// WildMIDI's HMI conversion can give a track's end-of-track a huge bogus delta (hours of silence). Clamp
    /// every event to the song's last note, then repeat the song until it lasts at least minSeconds (Daggerfall
    /// loops its songs). Returns the resulting length in seconds.
    /// </summary>
    public static double RepairAndLoop(string inPath, string outPath, double minSeconds)
    {
        var d = File.ReadAllBytes(inPath);
        int format = BE16(d, 8), trackCount = BE16(d, 10), division = BE16(d, 12);
        if ((division & 0x8000) != 0) throw new NotSupportedException("SMPTE timing");
        var tracks = new List<List<Event>>();
        int pos = 14;
        for (int t = 0; t < trackCount; t++)
        {
            int len = (int)BE32(d, pos + 4);
            tracks.Add(ReadTrack(d, pos + 8, pos + 8 + len));
            pos += 8 + len;
        }
        long end = tracks.SelectMany(t => t).Where(e => !e.IsEndOfTrack && e.Bytes.Length > 0 && (e.Bytes[0] & 0xF0) is 0x80 or 0x90).Select(e => e.Tick).DefaultIfEmpty(0).Max();
        // Let the last notes ring for a beat.
        end += division;
        int tempo = tracks.SelectMany(t => t).Where(e => e.Bytes.Length == 6 && e.Bytes[0] == 0xFF && e.Bytes[1] == 0x51).Select(e => (e.Bytes[3] << 16) | (e.Bytes[4] << 8) | e.Bytes[5]).DefaultIfEmpty(500000).First();
        double songSeconds = end / (double)division * tempo / 1e6;
        int repeats = Math.Max(1, (int)Math.Ceiling(minSeconds / Math.Max(songSeconds, 1)));

        using var ms = new MemoryStream();
        ms.Write("MThd"u8); WriteBE32(ms, 6); WriteBE16(ms, Math.Max(format, 1)); WriteBE16(ms, tracks.Count); WriteBE16(ms, division);
        foreach (var track in tracks)
        {
            using var body = new MemoryStream();
            long last = 0;
            var events = track.Where(e => !e.IsEndOfTrack).Select(e => e with { Tick = Math.Min(e.Tick, end) }).ToList();
            // Tempo and other setup events repeat harmlessly; notes repeat each loop.
            for (int r = 0; r < repeats; r++)
                foreach (var e in events)
                {
                    if (r > 0 && e.Bytes[0] == 0xFF && e.Bytes[1] != 0x51) continue;
                    long tick = e.Tick + r * end;
                    WriteVlq(body, tick - last); body.Write(e.Bytes); last = tick;
                }
            WriteVlq(body, Math.Max(0, repeats * end - last)); body.Write([0xFF, 0x2F, 0x00]);
            ms.Write("MTrk"u8); WriteBE32(ms, (uint)body.Length); body.Position = 0; body.CopyTo(ms);
        }
        File.WriteAllBytes(outPath, ms.ToArray());
        return songSeconds * repeats;
    }

    static List<Event> ReadTrack(byte[] d, int p, int end)
    {
        var events = new List<Event>();
        long tick = 0; byte status = 0;
        while (p < end)
        {
            tick += ReadVlq(d, ref p);
            byte b = d[p];
            int start = p;
            if (b == 0xFF)
            {
                p += 2; long len = ReadVlq(d, ref p); p += (int)len;
                events.Add(new Event(tick, d[start..p], d[start + 1] == 0x2F));
            }
            else if (b is 0xF0 or 0xF7)
            {
                p++; long len = ReadVlq(d, ref p); p += (int)len;
                events.Add(new Event(tick, d[start..p], false));
            }
            else
            {
                bool running = (b & 0x80) == 0;
                if (!running) { status = b; p++; }
                int n = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                var bytes = new byte[n + 1]; bytes[0] = status; Array.Copy(d, p, bytes, 1, n); p += n;
                events.Add(new Event(tick, bytes, false));
            }
        }
        return events;
    }

    static long ReadVlq(byte[] d, ref int p) { long v = 0; byte b; do { b = d[p++]; v = (v << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0); return v; }
    static void WriteVlq(Stream s, long v)
    {
        var bytes = new Stack<byte>(); bytes.Push((byte)(v & 0x7F)); v >>= 7;
        while (v > 0) { bytes.Push((byte)((v & 0x7F) | 0x80)); v >>= 7; }
        foreach (var b in bytes) s.WriteByte(b);
    }
    static int BE16(byte[] d, int p) => (d[p] << 8) | d[p + 1];
    static uint BE32(byte[] d, int p) => (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
    static void WriteBE16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void WriteBE32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
}

/// <summary>16-bit PCM WAV post-processing.</summary>
static class Wav
{
    /// <summary>Trims to maxSeconds, fades out the end, and normalises the peak to -1 dBFS.</summary>
    public static void FadeAndNormalise(string path, double fadeSeconds, double maxSeconds)
    {
        var d = File.ReadAllBytes(path);
        int p = 12, dataPos = -1, dataLen = 0, channels = 2, rate = 44100;
        while (p + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, p, 4); int len = BitConverter.ToInt32(d, p + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(d, p + 10); rate = BitConverter.ToInt32(d, p + 12); }
            if (id == "data") { dataPos = p + 8; dataLen = len; break; }
            p += 8 + len;
        }
        if (dataPos < 0) throw new InvalidDataException("no data chunk");
        int frames = Math.Min(dataLen / (2 * channels), (int)(maxSeconds * rate));
        var samples = new short[frames * channels];
        Buffer.BlockCopy(d, dataPos, samples, 0, samples.Length * 2);
        int fadeFrames = (int)(fadeSeconds * rate);
        int peak = 1;
        for (int i = 0; i < samples.Length; i++)
        {
            int frame = i / channels;
            if (frame > frames - fadeFrames) samples[i] = (short)(samples[i] * (frames - frame) / (double)fadeFrames);
            peak = Math.Max(peak, Math.Abs((int)samples[i]));
        }
        double gain = 32767 * 0.891 / peak; // -1 dBFS
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        var bytes = new byte[samples.Length * 2]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); w.Write(bytes);
    }
}
