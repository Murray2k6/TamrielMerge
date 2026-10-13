namespace TamrielMerge.Redguard;

/// <summary>A Redguard palette (.COL): an 8-byte header (file size 776, magic 0xB123) and 256 RGB triplets.</summary>
public sealed class RgPalette
{
    public readonly uint[] Colors = new uint[256];   // 0xAARRGGBB

    public static RgPalette Load(byte[] b)
    {
        var p = new RgPalette();
        for (int i = 0; i < 256; i++)
            p.Colors[i] = 0xFF000000u | (uint)b[8 + i * 3] << 16 | (uint)b[9 + i * 3] << 8 | b[10 + i * 3];
        return p;
    }
}

/// <summary>
/// A Redguard texture bank of the software-renderer release (3dart\TEXTURE.###; the Glide release has TEXBSI.### in
/// fxart instead), worked out from the files themselves:
/// <list type="bullet">
/// <item>Header: u16 image count at 0, a name from 2 (e.g. "Small landscape"), then from 0x1B one 20-byte entry per
/// image: a representative palette index (u8), the offset of the image's record (u32, 0 = no image) and the end of
/// the 256-wide band its pixels sit in (u32, 0 for images stored on their own, such as animated water).</item>
/// <item>Record (28 bytes): u16, u16, width (u16 at 4), height (u16 at 6), u16, data size (u32 at 10) and the offset
/// from the record to the image's first pixel (u32 at 14). Images in a band (band end non-zero) have rows 256 bytes
/// apart (four 64-wide tiles side by side, small mipmaps after the band); others are width-wide rows, and animated
/// ones store their frames one after another (the first is used).</item>
/// <item>Pixels are palette indices; 0 is transparent.</item>
/// </list>
/// TEXTURE.302 holds the 64 terrain tiles WLD files index (entry = tile number).
/// </summary>
public sealed class RgTextureBank
{
    public sealed record Image(int Index, int Width, int Height, byte[] Pixels, byte AverageColor);

    public string Name { get; private init; } = "";
    public Dictionary<int, Image> Images { get; } = [];

    public static RgTextureBank? Load(byte[] d)
    {
        if (d.Length < 0x1B) return null;
        int count = BitConverter.ToUInt16(d, 0);
        int nameEnd = Array.IndexOf(d, (byte)0, 3);
        var bank = new RgTextureBank { Name = System.Text.Encoding.ASCII.GetString(d, 2, Math.Max(0, Math.Min(nameEnd, 0x1B) - 2)).Trim('\t', ' ', '\0') };
        for (int e = 0; e < count; e++)
        {
            int o = 0x1B + e * 20;
            if (o + 9 > d.Length) return null;
            byte color = d[o];
            uint a = BitConverter.ToUInt32(d, o + 1), bandEnd = BitConverter.ToUInt32(d, o + 5);
            if (a == 0) continue;
            if (a + 28 > d.Length) return null;
            int w = BitConverter.ToUInt16(d, (int)a + 4), h = BitConverter.ToUInt16(d, (int)a + 6);
            uint first = a + BitConverter.ToUInt32(d, (int)a + 14);
            int stride = bandEnd != 0 ? 256 : w;
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024 || first + (long)(h - 1) * stride + w > d.Length) return null;
            var px = new byte[w * h];
            for (int y = 0; y < h; y++) Array.Copy(d, first + y * stride, px, y * w, w);
            bank.Images[e] = new Image(e, w, h, px, color);
        }
        return bank;
    }

    /// <summary>An image's pixels as 0xAARRGGBB through a palette (index 0 transparent).</summary>
    public static uint[] ToArgb(Image img, RgPalette palette) =>
        img.Pixels.Select(p => p == 0 ? 0u : palette.Colors[p]).ToArray();
}
