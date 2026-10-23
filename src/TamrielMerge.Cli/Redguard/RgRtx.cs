namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's dialogue file (ENGLISH.RTX): every spoken line and message by a 4-character tag (scripts name them, e.g.
/// RTX("sh2a")). An index at the end (footer "RNAV", index offset, count; 12-byte entries: tag, payload offset, size)
/// points at payloads of: kind (u8), 0 for text or 1 for a voiced line (u8), text length (u16), u16, the text, and for
/// voiced lines a 27-byte audio header (8/16-bit mono flag at 4, sample rate at 8, data length at 0x16) and raw PCM.
/// </summary>
public sealed class RgRtx
{
    public sealed record Entry(string Tag, string Text, int SampleRate, int Bits, int AudioOffset, int AudioLength);

    public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
    readonly byte[] data;

    RgRtx(byte[] data) { this.data = data; }

    public static RgRtx? Load(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 12 || System.Text.Encoding.ASCII.GetString(b, b.Length - 12, 4) != "RNAV") return null;
        var rtx = new RgRtx(b);
        int index = BitConverter.ToInt32(b, b.Length - 8), count = BitConverter.ToInt32(b, b.Length - 4);
        for (int i = 0; i < count; i++)
        {
            int o = index + i * 12;
            string tag = System.Text.Encoding.Latin1.GetString(b, o, 4);
            int at = BitConverter.ToInt32(b, o + 4), size = BitConverter.ToInt32(b, o + 8);
            if (at < 0 || at + size > b.Length || size < 6) continue;
            bool voiced = b[at + 1] == 1;
            int len = BitConverter.ToUInt16(b, at + 2);
            string text = System.Text.Encoding.Latin1.GetString(b, at + 6, Math.Min(len, size - 6)).TrimEnd('\0');
            int rate = 0, bits = 0, audioAt = 0, audioLen = 0;
            if (voiced && 6 + len + 27 <= size)
            {
                int h = at + 6 + len;
                bits = BitConverter.ToInt32(b, h + 4) == 1 ? 16 : 8;
                rate = BitConverter.ToInt32(b, h + 8);
                audioLen = BitConverter.ToInt32(b, h + 0x16);
                audioAt = h + 27;
                if (audioAt + audioLen > at + size) audioLen = Math.Max(0, at + size - audioAt);
            }
            rtx.Entries[tag] = new Entry(tag, text, rate, bits, audioAt, audioLen);
        }
        return rtx;
    }

    /// <summary>A voiced line's audio as a .wav (the original 8/16-bit mono PCM), or false when it has none.</summary>
    public bool WriteWav(Entry e, string path)
    {
        if (e.AudioLength <= 0 || e.SampleRate <= 0) return false;
        using var w = new BinaryWriter(File.Create(path));
        int bytesPerSample = e.Bits / 8;
        w.Write("RIFF"u8); w.Write(36 + e.AudioLength); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(e.SampleRate); w.Write(e.SampleRate * bytesPerSample);
        w.Write((short)bytesPerSample); w.Write((short)e.Bits);
        w.Write("data"u8); w.Write(e.AudioLength); w.Write(data, e.AudioOffset, e.AudioLength);
        return true;
    }
}

/// <summary>
/// Redguard's sound effects bank (sound\MAIN.SFX): "FXHD" (big-endian length: a 32-byte description and the effect
/// count), then "FXDT" (big-endian length) holding the effects one after another, each a 27-byte header and its PCM
/// (Redguard Preservation, formats/SFX.html): the type at 0 (0 8-bit mono, 1 16-bit mono, 2 8-bit stereo, 3 16-bit
/// stereo), the bits flag at 4 (1 for 16-bit), the sample rate at 8, the base volume at 0x0C (Miles' 0-127; 100 in
/// every shipped effect), the loop flag at 0x0D (0 once, 0xFF until stopped, else Miles' loop count: effect 117 plays
/// 225 times) and the data length at 0x16. Scripts play them by index (Sound(n), FlatSound(n), AmbientSound(n)).
/// </summary>
public sealed class RgSfx
{
    public sealed record Effect(int Type, int Bits, int SampleRate, int Offset, int Length, int BaseVolume, int Loop)
    {
        public int Channels => Type >= 2 ? 2 : 1;

        /// <summary>One play's length in seconds at the given rate (its own when 0).</summary>
        public double Seconds(int rate = 0) => Length / (double)(Bits / 8 * Channels) / (rate > 0 ? rate : Math.Max(1, SampleRate));
    }

    public List<Effect> Effects { get; } = [];
    readonly byte[] data;

    RgSfx(byte[] data) { this.data = data; }

    public static RgSfx? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        int BE(int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];
        if (b.Length < 16 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "FXHD") return null;
        int p = 8 + BE(4);
        if (p + 8 > b.Length || System.Text.Encoding.ASCII.GetString(b, p, 4) != "FXDT") return null;
        int end = Math.Min(b.Length, p + 8 + BE(p + 4));
        p += 8;
        var sfx = new RgSfx(b);
        while (p + 27 <= end)
        {
            int len = BitConverter.ToInt32(b, p + 0x16);
            if (len < 0 || p + 27 + len > end) break;
            sfx.Effects.Add(new Effect(BitConverter.ToInt32(b, p), BitConverter.ToInt32(b, p + 4) == 1 ? 16 : 8, BitConverter.ToInt32(b, p + 8), p + 27, len, b[p + 0x0C], b[p + 0x0D]));
            p += 27 + len;
        }
        return sfx;
    }

    /// <summary>
    /// An effect as a mono .wav (a stereo one's interleaved channels averaged, so Skyrim can place it), its samples
    /// played at the given rate (Redguard's rate offset: the effect's rate plus the offset, 512-44000 Hz; its own when
    /// 0), or false for an empty one.
    /// </summary>
    public bool WriteWav(int index, string path, int rate = 0)
    {
        if (index < 0 || index >= Effects.Count) return false;
        var e = Effects[index];
        if (e.Length <= 0 || e.SampleRate <= 0) return false;
        int bytesPerSample = e.Bits / 8;
        byte[] pcm = new byte[e.Length];
        Buffer.BlockCopy(data, e.Offset, pcm, 0, e.Length);
        if (e.Channels == 2)
        {
            int frames = e.Length / (2 * bytesPerSample);
            var mono = new byte[frames * bytesPerSample];
            for (int i = 0; i < frames; i++)
                if (bytesPerSample == 2)
                {
                    int l = BitConverter.ToInt16(pcm, i * 4), r = BitConverter.ToInt16(pcm, i * 4 + 2);
                    BitConverter.GetBytes((short)((l + r) / 2)).CopyTo(mono, i * 2);
                }
                else mono[i] = (byte)((pcm[i * 2] + pcm[i * 2 + 1]) / 2);
            pcm = mono;
        }
        int hz = rate > 0 ? rate : e.SampleRate;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(hz); w.Write(hz * bytesPerSample);
        w.Write((short)bytesPerSample); w.Write((short)e.Bits);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return true;
    }
}
