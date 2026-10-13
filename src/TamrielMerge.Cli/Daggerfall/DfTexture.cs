namespace TamrielMerge.Daggerfall;

/// <summary>One decoded image: palette indices (0 = transparent), plus Daggerfall's in-world scale adjustment.</summary>
public sealed record DfImage(int Width, int Height, byte[] Pixels, int ScaleX, int ScaleY)
{
    /// <summary>Size in Daggerfall world units (Daggerfall Unity: pixels enlarged by scale/256).</summary>
    public (float Width, float Height) WorldSize => (Width * (1 + ScaleX / 256f), Height * (1 + ScaleY / 256f));
}

/// <summary>
/// A TEXTURE.nnn archive: wall textures and the 2D "flat" sprites used for clutter, lights and NPCs.
/// Format reference: Daggerfall Unity TextureFile.cs (uncompressed rows of stride 256, multi-frame column runs,
/// and the RLE row formats).
/// </summary>
public sealed class DfTexture
{
    const short Uncompressed = 0, RecordRle = 0x1108, ImageRle = 0x108;

    readonly byte[] d;
    readonly int[] recordPositions;
    public int Archive { get; }
    public string Name { get; }
    public int Count => recordPositions.Length;

    DfTexture(int archive, byte[] data)
    {
        Archive = archive;
        d = data;
        int count = BitConverter.ToInt16(d, 0);
        Name = System.Text.Encoding.ASCII.GetString(d, 2, 24).Split('\0')[0].Trim();
        recordPositions = new int[count];
        for (int r = 0; r < count; r++) recordPositions[r] = BitConverter.ToInt32(d, 26 + r * 20 + 2);
    }

    public static DfTexture? Load(string arena2, int archive)
    {
        string path = Path.Combine(arena2, $"TEXTURE.{archive:D3}");
        return File.Exists(path) ? new DfTexture(archive, File.ReadAllBytes(path)) : null;
    }

    public int FrameCount(int record) => BitConverter.ToUInt16(d, recordPositions[record] + 20);

    public DfImage Image(int record, int frame = 0)
    {
        int pos = recordPositions[record];
        int width = BitConverter.ToInt16(d, pos + 4), height = BitConverter.ToInt16(d, pos + 6);
        short compression = BitConverter.ToInt16(d, pos + 8);
        int dataOffset = BitConverter.ToInt32(d, pos + 14);
        int frames = BitConverter.ToUInt16(d, pos + 20);
        int scaleX = BitConverter.ToInt16(d, pos + 24), scaleY = BitConverter.ToInt16(d, pos + 26);
        var px = new byte[Math.Max(0, width * height)];
        int start = pos + dataOffset;
        if (compression is RecordRle or ImageRle)
        {
            int headers = start + height * frame * 4, dst = 0;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = BitConverter.ToInt16(d, headers + y * 4);
                bool rle = BitConverter.ToUInt16(d, headers + y * 4 + 2) == 0x8000;
                int src = pos + rowOffset;
                if (!rle) { Array.Copy(d, src, px, dst, width); dst += width; continue; }
                int rowWidth = BitConverter.ToUInt16(d, src); src += 2;
                for (int x = 0; x < rowWidth;)
                {
                    int probe = BitConverter.ToInt16(d, src); src += 2;
                    if (probe < 0) { byte p = d[src++]; for (int i = 0; i < -probe; i++, x++) px[dst++] = p; }
                    else { Array.Copy(d, src, px, dst, probe); src += probe; dst += probe; x += probe; }
                }
            }
        }
        else if (frames <= 1)
        {
            for (int y = 0; y < height; y++) Array.Copy(d, start + y * 256, px, y * width, width);
        }
        else
        {
            // Multi-frame: per-frame offsets, then (width, height) and rows of (transparent run, opaque run, pixels).
            int src = start + BitConverter.ToInt32(d, start + frame * 4);
            int cx = BitConverter.ToInt16(d, src), cy = BitConverter.ToInt16(d, src + 2);
            src += 4;
            width = cx; height = cy; px = new byte[cx * cy];
            int dst = 0;
            for (int y = 0; y < cy; y++)
                for (int x = 0; x < cx;)
                {
                    int skip = d[src++]; dst += skip; x += skip;
                    int run = d[src++];
                    Array.Copy(d, src, px, dst, run); src += run; dst += run; x += run;
                }
        }
        return new DfImage(width, height, px, scaleX, scaleY);
    }

    /// <summary>ART_PAL.COL (8-byte header, 256 RGB entries in 0-255) as RGB triples.</summary>
    public static byte[] Palette(string arena2, string file = "ART_PAL.COL")
    {
        var p = File.ReadAllBytes(Path.Combine(arena2, file));
        return p.Length >= 776 ? p[8..776] : p[..768];
    }

    /// <summary>A labelled contact sheet of each record's first frame, for identifying sprites.</summary>
    /// <summary>One sprite as an RGBA PNG (palette index 0 transparent), for making a model from it.</summary>
    public static bool SaveSprite(string arena2, int archive, int record, string path, byte[]? palette = null)
    {
        if (Load(arena2, archive) is not { } t || record >= t.Count) return false;
        var img = t.Image(record);
        if (img.Width == 0) return false;
        palette ??= Palette(arena2);
        var rgba = new byte[img.Width * img.Height * 4];
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            byte p = img.Pixels[i];
            if (p == 0) continue;
            rgba[i * 4] = palette[p * 3]; rgba[i * 4 + 1] = palette[p * 3 + 1]; rgba[i * 4 + 2] = palette[p * 3 + 2]; rgba[i * 4 + 3] = 255;
        }
        Util.Png.WriteRgba(path, img.Width, img.Height, rgba);
        return true;
    }

    public static void Sheet(string arena2, IEnumerable<int> archives, string path, int cell = 96, int columns = 12)
    {
        var palette = Palette(arena2);
        var items = new List<(string Label, DfImage Image)>();
        foreach (int a in archives)
            if (Load(arena2, a) is { } t)
                for (int r = 0; r < t.Count; r++)
                    try { items.Add(($"{a}.{r}", t.Image(r))); } catch (Exception) { items.Add(($"{a}.{r}", new DfImage(0, 0, [], 0, 0))); }
        int rows = (items.Count + columns - 1) / columns, w = columns * cell, h = rows * (cell + 14);
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 48; rgb[i + 1] = 40; rgb[i + 2] = 56; }
        for (int i = 0; i < items.Count; i++)
        {
            var (label, img) = items[i];
            int ox = i % columns * cell, oy = i / columns * (cell + 14);
            float s = img.Width == 0 ? 1 : Math.Min(1f * (cell - 4) / img.Width, 1f * (cell - 4) / img.Height);
            s = Math.Min(s, 3);
            int dw = (int)(img.Width * s), dh = (int)(img.Height * s);
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    byte p = img.Pixels[(int)(y / s) * img.Width + (int)(x / s)];
                    if (p == 0) continue;
                    int o = ((oy + cell - 2 - dh + y) * w + ox + (cell - dw) / 2 + x) * 3;
                    rgb[o] = palette[p * 3]; rgb[o + 1] = palette[p * 3 + 1]; rgb[o + 2] = palette[p * 3 + 2];
                }
            DrawText(rgb, w, ox + 2, oy + cell + 2, label);
        }
        Util.Png.WriteRgb(path, w, h, rgb);
    }

    // 3x5 digits and '.', drawn at 2x.
    static readonly string[] Glyphs = ["111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111", "000000000000010"];

    static void DrawText(byte[] rgb, int w, int x0, int y0, string text)
    {
        foreach (char c in text)
        {
            int g = c == '.' ? 10 : c - '0';
            if (g is < 0 or > 10) { x0 += 8; continue; }
            for (int i = 0; i < 15; i++)
                if (Glyphs[g][i] == '1')
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int o = ((y0 + i / 3 * 2 + dy) * w + x0 + i % 3 * 2 + dx) * 3;
                            if (o >= 0 && o + 2 < rgb.Length) { rgb[o] = 255; rgb[o + 1] = 230; rgb[o + 2] = 120; }
                        }
            x0 += 8;
        }
    }
}
