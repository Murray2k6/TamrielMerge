namespace TamrielMerge.Daggerfall;

/// <summary>
/// Daggerfall's videos (ARENA2\*.VID), decoded to constant-rate frames and a WAV (format as Daggerfall Unity's VidFile,
/// MIT): "VID", u16, frame count, width, height, delay, u16; a palette block (type 2, 768 bytes of 6-bit RGB); then
/// blocks by type: 124 audio start (u16, rate byte 166 = 11025 Hz, u16 length, 8-bit unsigned mono samples), 125 audio
/// (u16 length, samples), 3 full frame (u16 delay, RLE: n &gt;= 0x80 repeats the next index n - 0x80 times, 1..0x7F
/// copies n indices, 0 ends), 1 partial frame (same, but n &gt;= 0x80 skips pixels), 4 partial frame from a row (u16
/// delay, u16 row), 2 palette (skipped), 0 null, 20 end. Each frame lasts as long as the audio block before it plays
/// (at least 740 samples), which is how the original paced playback.
/// </summary>
public static class DfVid
{
    const int SampleRate = 11025;

    /// <summary>Writes the video as PNG frames (frame00001.png, ...) at <paramref name="fps"/> and audio.wav into
    /// <paramref name="outDir"/>; returns (frames written, width, height, seconds).</summary>
    public static (int Frames, int Width, int Height, double Seconds) Export(string path, string outDir, double fps = 30)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 15 || b[0] != 'V' || b[1] != 'I' || b[2] != 'D') throw new InvalidDataException($"{path}: not a VID file");
        int w = BitConverter.ToUInt16(b, 7), h = BitConverter.ToUInt16(b, 9);
        int p = 15;
        if (b[p++] != 2) throw new InvalidDataException($"{path}: no palette after the header");
        var palette = new uint[256];
        for (int i = 0; i < 256; i++) palette[i] = (uint)(Math.Min(255, b[p + i * 3] * 4) << 16 | Math.Min(255, b[p + i * 3 + 1] * 4) << 8 | Math.Min(255, b[p + i * 3 + 2] * 4));
        p += 768;
        var frame = new byte[w * h];
        var audio = new List<byte>();
        Directory.CreateDirectory(outDir);
        int written = 0, lastAudio = 0;
        double clock = 0;   // seconds of video shown so far
        void Emit(double duration)
        {
            clock += duration;
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < frame.Length; i++) { uint c = palette[frame[i]]; rgb[i * 3] = (byte)(c >> 16); rgb[i * 3 + 1] = (byte)(c >> 8); rgb[i * 3 + 2] = (byte)c; }
            string png = null!;
            while (written < (int)Math.Round(clock * fps))
            {
                written++;
                string file = Path.Combine(outDir, $"frame{written:D5}.png");
                if (png is null) { Util.Png.WriteRgb(file, w, h, rgb); png = file; }
                else File.Copy(png, file, true);
            }
        }
        void Rle(int pos, bool full)
        {
            while (pos < frame.Length && p < b.Length)
            {
                byte n = b[p++];
                if (n == 0) break;
                if (n >= 0x80)
                {
                    int count = n - 0x80;
                    if (full) { byte v = b[p++]; for (int i = 0; i < count && pos < frame.Length; i++) frame[pos++] = v; }
                    else pos += count;
                }
                else for (int i = 0; i < n && pos < frame.Length; i++) frame[pos++] = b[p++];
            }
        }
        while (p < b.Length)
        {
            int type = b[p++];
            switch (type)
            {
                case 0: continue;
                case 2: p += 768; continue;
                case 20: p = b.Length; continue;
                case 124: { p += 3; int len = BitConverter.ToUInt16(b, p); p += 2; audio.AddRange(b.AsSpan(p, Math.Min(len, b.Length - p)).ToArray()); p += len; lastAudio = len; continue; }
                case 125: { int len = BitConverter.ToUInt16(b, p); p += 2; audio.AddRange(b.AsSpan(p, Math.Min(len, b.Length - p)).ToArray()); p += len; lastAudio = len; continue; }
                case 3: p += 2; Rle(0, true); break;
                case 1: p += 2; Rle(0, false); break;
                case 4: { p += 2; int row = BitConverter.ToUInt16(b, p); p += 2; Rle(row * w, false); break; }
                default: throw new InvalidDataException($"{path}: unknown block {type} at {p - 1}");
            }
            Emit(Math.Max(lastAudio, 740) / (double)SampleRate);
        }
        // The audio, 8-bit unsigned mono.
        using (var wav = new BinaryWriter(File.Create(Path.Combine(outDir, "audio.wav"))))
        {
            wav.Write("RIFF"u8); wav.Write(36 + audio.Count); wav.Write("WAVE"u8);
            wav.Write("fmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)1); wav.Write(SampleRate); wav.Write(SampleRate); wav.Write((short)1); wav.Write((short)8);
            wav.Write("data"u8); wav.Write(audio.Count); wav.Write(audio.ToArray());
        }
        return (written, w, h, clock);
    }
}
