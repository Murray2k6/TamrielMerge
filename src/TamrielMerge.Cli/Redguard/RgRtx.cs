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
