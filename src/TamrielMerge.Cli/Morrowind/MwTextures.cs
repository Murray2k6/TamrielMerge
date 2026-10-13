using System.Numerics;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind textures for Skyrim, under textures\tamrielmerge\mw\. A model names its texture with any extension
/// (usually .tga); like Morrowind itself, a .dds of the same name is preferred, then the named file, then .tga/.bmp.
/// DDS files (DXT1/DXT3/uncompressed) are copied unchanged; TGA and BMP are decoded and written as BC1 (opaque) or
/// BC3 (with alpha) DDS with a full mip chain.
/// </summary>
public sealed class MwTextures(Tes3Data data, string modOutDir)
{
    public const string OutFolder = @"textures\tamrielmerge\mw";
    /// <summary>Flat normal map, no specular (Skyrim reads the normal map's alpha as its shine mask).</summary>
    public const string FlatNormal = @"textures\tamrielmerge\mw\flat_n.dds";

    readonly Dictionary<string, (string? Path, bool Alpha)> done = new(StringComparer.OrdinalIgnoreCase);
    public int Converted { get; private set; }
    public List<string> Missing { get; } = [];

    /// <summary>The Skyrim data-relative DDS for a Morrowind texture name, and whether it has an alpha channel.
    /// Null path when the texture can't be found.</summary>
    public (string? Path, bool Alpha) Ensure(string name)
    {
        string key = Path.ChangeExtension(Tes3Bsa.Normalize(name).Replace(@"textures\", ""), null);
        if (done.TryGetValue(key, out var hit)) return hit;
        EnsureFlatNormal();
        string? source = new[] { ".dds", Path.GetExtension(name), ".tga", ".bmp" }
            .Select(ext => @"textures\" + key + ext).FirstOrDefault(data.Exists);
        if (source is null) { Missing.Add(name); return done[key] = (null, false); }
        string rel = $@"{OutFolder}\{key}.dds";
        string full = Path.Combine(modOutDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = data.Read(source)!;
        bool alpha;
        // An upscaled version (tools/upscale, from mw-upscale-export) replaces the original when there is one.
        if (UpscaleCache is { } cache && ReadRaw(Path.Combine(cache, FlatName(key) + ".rgba")) is { } up)
        {
            alpha = up.Px.Any(c => c >> 24 != 0xFF);
            WriteBcDds(full, up.Px, up.W, up.H, alpha);
        }
        else if (source.EndsWith(".dds"))
        {
            File.WriteAllBytes(full, bytes);
            alpha = DdsHasAlpha(bytes);
        }
        else
        {
            var (px, w, h) = source.EndsWith(".tga") ? DecodeTga(bytes) : DecodeBmp(bytes);
            alpha = px.Any(c => c >> 24 != 0xFF);
            WriteBcDds(full, px, w, h, alpha);
        }
        Converted++;
        return done[key] = (rel, alpha);
    }

    /// <summary>Folder of upscaled textures (&lt;flat name&gt;.rgba) to use instead of the originals, if set.</summary>
    public static string? UpscaleCache { get; set; }

    /// <summary>A texture key as one file name (subfolders joined with "__").</summary>
    public static string FlatName(string key) => key.Replace("\\", "__").Replace("/", "__");

    /// <summary>Raw pixels as the upscaler reads and writes them: int32 width, int32 height, 0xAARRGGBB pixels.</summary>
    public static (uint[] Px, int W, int H)? ReadRaw(string path)
    {
        if (!File.Exists(path)) return null;
        using var r = new BinaryReader(File.OpenRead(path));
        int w = r.ReadInt32(), h = r.ReadInt32();
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = r.ReadUInt32();
        return (px, w, h);
    }

    public static void WriteRaw(string path, uint[] px, int w, int h)
    {
        using var wr = new BinaryWriter(File.Create(path));
        wr.Write(w); wr.Write(h);
        foreach (var p in px) wr.Write(p);
    }

    /// <summary>Every Morrowind texture (DDS preferred over TGA/BMP of the same name) decoded into outDir as raw
    /// pixels for the upscaler. Returns how many were written.</summary>
    public static int ExportAll(Tes3Data data, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var keys = data.List("textures").Where(n => n.EndsWith(".dds") || n.EndsWith(".tga") || n.EndsWith(".bmp"))
            .GroupBy(n => Path.ChangeExtension(n.Replace(@"textures\", ""), null), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var g in keys)
        {
            string target = Path.Combine(outDir, FlatName(g.Key) + ".rgba");
            if (File.Exists(target)) { n++; continue; }
            string source = g.OrderBy(f => f.EndsWith(".dds") ? 0 : f.EndsWith(".tga") ? 1 : 2).First();
            var bytes = data.Read(source)!;
            (uint[] Px, int W, int H)? img = source.EndsWith(".dds") ? Util.DdsImage.Decode(bytes) : source.EndsWith(".tga") ? DecodeTga(bytes) : DecodeBmp(bytes);
            if (img is not { } i || i.W < 4 || i.H < 4) continue;
            WriteRaw(target, i.Px, i.W, i.H);
            n++;
        }
        return n;
    }

    readonly Dictionary<string, string> normals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A normal map for a converted texture, so Morrowind's art takes Skyrim's lighting (it had none: every surface
    /// was lit flat). The relief comes from the texture's own brightness (lightly blurred, darker = deeper), its
    /// slopes scaled to the texture's size; the alpha, Skyrim's shine mask, is a faint sheen on the brighter parts.
    /// Written next to the texture as &lt;name&gt;_n.dds; the flat normal when the texture can't be read.
    /// </summary>
    public string NormalFor(string diffuseRel)
    {
        if (normals.TryGetValue(diffuseRel, out var hit)) return hit;
        string rel = Path.ChangeExtension(diffuseRel, null) + "_n.dds";
        string full = Path.Combine(modOutDir, rel);
        if (File.Exists(full)) return normals[diffuseRel] = rel;
        string source = Path.Combine(modOutDir, diffuseRel);
        if (!File.Exists(source) || Util.DdsImage.Decode(File.ReadAllBytes(source)) is not { } img || img.W < 8 || img.H < 8)
            return normals[diffuseRel] = FlatNormal;
        WriteNormalMap(img.Px, img.W, img.H, full);
        return normals[diffuseRel] = rel;
    }

    /// <summary>The relief normal map (see NormalFor) of a texture's pixels, written as a BC3 DDS.</summary>
    public static void WriteNormalMap(uint[] px, int w, int h, string full)
    {
        while (Math.Max(w, h) > 512) (px, w, h) = Half((px, w, h));   // relief needs less detail than colour
        var lum = new float[w * h];
        for (int i = 0; i < px.Length; i++) lum[i] = ((px[i] >> 16 & 255) * 0.299f + (px[i] >> 8 & 255) * 0.587f + (px[i] & 255) * 0.114f) / 255f;
        // 3x3 box blur (wrapping, as textures tile): relief from shapes, not from pixel noise.
        var height = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        sum += lum[(y + dy + h) % h * w + (x + dx + w) % w];
                height[y * w + x] = sum / 9;
            }
        float strength = 2.5f * Math.Max(w, h) / 256f;
        var outPx = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float gx = (height[y * w + (x + 1) % w] - height[y * w + (x - 1 + w) % w]) * 0.5f * strength;
                float gy = (height[(y + 1) % h * w + x] - height[(y - 1 + h) % h * w + x]) * 0.5f * strength;
                var n = Vector3.Normalize(new Vector3(-gx, -gy, 1));   // rows run down, as the DirectX convention Skyrim reads
                uint spec = (uint)Math.Clamp(lum[y * w + x] * 60, 0, 255);
                outPx[y * w + x] = spec << 24 | (uint)((n.X * 0.5f + 0.5f) * 255) << 16 | (uint)((n.Y * 0.5f + 0.5f) * 255) << 8 | (uint)((n.Z * 0.5f + 0.5f) * 255);
            }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WriteBcDds(full, outPx, w, h, true);
    }

    /// <summary>A small solid-colour texture (for Morrowind's untextured shapes, drawn in their material colour).</summary>
    public string Solid(System.Numerics.Vector3 rgb)
    {
        uint r = (uint)Math.Clamp(rgb.X * 255, 0, 255), g = (uint)Math.Clamp(rgb.Y * 255, 0, 255), b = (uint)Math.Clamp(rgb.Z * 255, 0, 255);
        string rel = $@"{OutFolder}\solid\{r:X2}{g:X2}{b:X2}.dds";
        string full = Path.Combine(modOutDir, rel);
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            WriteBcDds(full, Enumerable.Repeat(0xFF000000u | r << 16 | g << 8 | b, 16).ToArray(), 4, 4, false);
        }
        EnsureFlatNormal();
        return rel;
    }

    void EnsureFlatNormal()
    {
        string full = Path.Combine(modOutDir, FlatNormal);
        if (File.Exists(full)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WriteBcDds(full, Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4, true);
    }

    static bool DdsHasAlpha(byte[] dds)
    {
        uint flags = BitConverter.ToUInt32(dds, 80);
        if ((flags & 0x4) != 0) return BitConverter.ToUInt32(dds, 84) != 0x31545844;      // anything but DXT1
        return (flags & 0x1) != 0;
    }

    // ---- decoders: pixels as 0xAARRGGBB, top row first ----

    public static (uint[] Px, int W, int H) DecodeTga(byte[] b)
    {
        int idLen = b[0], cmapType = b[1], type = b[2];
        int cmapFirst = BitConverter.ToUInt16(b, 3), cmapLen = BitConverter.ToUInt16(b, 5), cmapBits = b[7];
        int w = BitConverter.ToUInt16(b, 12), h = BitConverter.ToUInt16(b, 14), bpp = b[16], desc = b[17];
        int pos = 18 + idLen;
        uint[]? palette = null;
        if (cmapType == 1)
        {
            palette = new uint[cmapFirst + cmapLen];
            for (int i = 0; i < cmapLen; i++) { palette[cmapFirst + i] = ReadPixel(b, pos, cmapBits); pos += (cmapBits + 7) / 8; }
        }
        var px = new uint[w * h];
        int bytesPer = (bpp + 7) / 8;
        bool rle = type >= 9;
        int n = 0;
        uint Pixel(int at) => type is 1 or 9 ? palette![b[at] | (bpp == 16 ? b[at + 1] << 8 : 0)]
                            : type is 3 or 11 ? 0xFF000000u | (uint)b[at] * 0x010101u
                            : ReadPixel(b, at, bpp);
        while (n < px.Length)
        {
            if (!rle) { px[n++] = Pixel(pos); pos += bytesPer; continue; }
            int head = b[pos++], count = (head & 0x7F) + 1;
            if ((head & 0x80) != 0) { uint c = Pixel(pos); pos += bytesPer; for (int i = 0; i < count && n < px.Length; i++) px[n++] = c; }
            else for (int i = 0; i < count && n < px.Length; i++) { px[n++] = Pixel(pos); pos += bytesPer; }
        }
        if ((desc & 0x20) == 0) FlipRows(px, w, h);     // stored bottom row first
        return (px, w, h);
    }

    static uint ReadPixel(byte[] b, int at, int bits) => bits switch
    {
        32 => (uint)(b[at + 3] << 24 | b[at + 2] << 16 | b[at + 1] << 8 | b[at]),
        24 => 0xFF000000u | (uint)(b[at + 2] << 16 | b[at + 1] << 8 | b[at]),
        16 or 15 => From555(BitConverter.ToUInt16(b, at), bits == 16),
        _ => throw new InvalidDataException($"TGA {bits}-bit pixels"),
    };

    static uint From555(ushort v, bool alphaBit)
    {
        uint r = (uint)(v >> 10 & 31) * 255 / 31, g = (uint)(v >> 5 & 31) * 255 / 31, bl = (uint)(v & 31) * 255 / 31;
        uint a = alphaBit && (v & 0x8000) == 0 ? 0u : 255u;
        return a << 24 | r << 16 | g << 8 | bl;
    }

    public static (uint[] Px, int W, int H) DecodeBmp(byte[] b)
    {
        int dataOffset = BitConverter.ToInt32(b, 10), headerSize = BitConverter.ToInt32(b, 14);
        int w = BitConverter.ToInt32(b, 18), hRaw = BitConverter.ToInt32(b, 22), bpp = BitConverter.ToUInt16(b, 28);
        int compression = BitConverter.ToInt32(b, 30), colors = BitConverter.ToInt32(b, 46);
        if (compression is not (0 or 3)) throw new InvalidDataException($"BMP compression {compression}");
        int h = Math.Abs(hRaw);
        uint[] palette = [];
        if (bpp <= 8)
        {
            int count = colors != 0 ? colors : 1 << bpp;
            palette = new uint[count];
            for (int i = 0; i < count; i++) palette[i] = 0xFF000000u | BitConverter.ToUInt32(b, 14 + headerSize + 4 * i) & 0xFFFFFF;
        }
        int stride = (w * bpp + 31) / 32 * 4;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = dataOffset + y * stride, dst = (hRaw > 0 ? h - 1 - y : y) * w;
            for (int x = 0; x < w; x++)
                px[dst + x] = bpp switch
                {
                    8 => palette[b[row + x]],
                    4 => palette[b[row + x / 2] >> (x % 2 == 0 ? 4 : 0) & 0xF],
                    24 => 0xFF000000u | (uint)(b[row + 3 * x + 2] << 16 | b[row + 3 * x + 1] << 8 | b[row + 3 * x]),
                    32 => 0xFF000000u | BitConverter.ToUInt32(b, row + 4 * x) & 0xFFFFFF,
                    16 => From555(BitConverter.ToUInt16(b, row + 2 * x), false),
                    _ => throw new InvalidDataException($"BMP {bpp}-bit"),
                };
        }
        return (px, w, h);
    }

    static void FlipRows(uint[] px, int w, int h)
    {
        var tmp = new uint[w];
        for (int y = 0; y < h / 2; y++)
        {
            Array.Copy(px, y * w, tmp, 0, w);
            Array.Copy(px, (h - 1 - y) * w, px, y * w, w);
            Array.Copy(tmp, 0, px, (h - 1 - y) * w, w);
        }
    }

    // ---- BC1/BC3 encoding ----

    /// <summary>A BC1 (DXT1) or BC3 (DXT5) DDS with mipmaps down to 1x1 (box filtered).</summary>
    public static void WriteBcDds(string path, uint[] px, int w, int h, bool alpha)
    {
        var levels = new List<(uint[] Px, int W, int H)> { (px, w, h) };
        while (levels[^1].W > 1 || levels[^1].H > 1) levels.Add(Half(levels[^1]));
        int blockBytes = alpha ? 16 : 8;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(0x20534444u); bw.Write(124u);
        bw.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u);   // caps, height, width, pixel format, mip count, linear size
        bw.Write((uint)h); bw.Write((uint)w);
        bw.Write((uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes));
        bw.Write(0u); bw.Write((uint)levels.Count);
        for (int i = 0; i < 11; i++) bw.Write(0u);
        bw.Write(32u); bw.Write(0x4u); bw.Write(alpha ? 0x35545844u : 0x31545844u);   // FOURCC DXT5 / DXT1
        for (int i = 0; i < 5; i++) bw.Write(0u);
        bw.Write(0x1000u | 0x8u | 0x400000u);                            // texture, complex, mipmap
        for (int i = 0; i < 4; i++) bw.Write(0u);
        var block = new uint[16];
        foreach (var (p, lw, lh) in levels)
            for (int by = 0; by < lh; by += 4)
                for (int bx = 0; bx < lw; bx += 4)
                {
                    for (int i = 0; i < 16; i++) block[i] = p[Math.Min(by + i / 4, lh - 1) * lw + Math.Min(bx + i % 4, lw - 1)];
                    if (alpha) EncodeAlpha(bw, block);
                    EncodeColor(bw, block);
                }
    }

    static (uint[] Px, int W, int H) Half((uint[] Px, int W, int H) s)
    {
        int nw = Math.Max(1, s.W / 2), nh = Math.Max(1, s.H / 2);
        var n = new uint[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                uint a = 0, r = 0, g = 0, b = 0;
                for (int k = 0; k < 4; k++)
                {
                    uint c = s.Px[Math.Min(y * 2 + k / 2, s.H - 1) * s.W + Math.Min(x * 2 + k % 2, s.W - 1)];
                    a += c >> 24; r += c >> 16 & 0xFF; g += c >> 8 & 0xFF; b += c & 0xFF;
                }
                n[y * nw + x] = (a / 4) << 24 | (r / 4) << 16 | (g / 4) << 8 | b / 4;
            }
        return (n, nw, nh);
    }

    /// <summary>BC1 colour block: endpoints at the extremes of the block's colours along their principal axis,
    /// always in four-colour mode (c0 &gt; c1).</summary>
    static void EncodeColor(BinaryWriter bw, uint[] block)
    {
        var cols = block.Select(c => new Vector3(c >> 16 & 0xFF, c >> 8 & 0xFF, c & 0xFF)).ToArray();
        var mean = cols.Aggregate(Vector3.Zero, (a, b) => a + b) / 16;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var c in cols) { var d = c - mean; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
        var axis = new Vector3(1, 1, 1);
        for (int i = 0; i < 8; i++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float len = axis.Length();
            if (len < 1e-6f) { axis = new Vector3(1, 1, 1); break; }
            axis /= len;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var c in cols) { float t = Vector3.Dot(c - mean, axis); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
        ushort c0 = To565(mean + axis * hi), c1 = To565(mean + axis * lo);
        if (c0 < c1) (c0, c1) = (c1, c0);
        uint indices = 0;
        if (c0 != c1)
        {
            var e0 = From565(c0); var e1 = From565(c1);
            var pal = new[] { e0, e1, (2 * e0 + e1) / 3, (e0 + 2 * e1) / 3 };
            for (int i = 0; i < 16; i++)
            {
                int best = 0; float bestD = float.MaxValue;
                for (int k = 0; k < 4; k++) { float d = Vector3.DistanceSquared(cols[i], pal[k]); if (d < bestD) { bestD = d; best = k; } }
                indices |= (uint)best << (2 * i);
            }
        }
        bw.Write(c0); bw.Write(c1); bw.Write(indices);
    }

    static ushort To565(Vector3 c)
    {
        int r = Math.Clamp((int)MathF.Round(c.X * 31 / 255), 0, 31), g = Math.Clamp((int)MathF.Round(c.Y * 63 / 255), 0, 63), b = Math.Clamp((int)MathF.Round(c.Z * 31 / 255), 0, 31);
        return (ushort)(r << 11 | g << 5 | b);
    }
    static Vector3 From565(ushort v) => new((v >> 11 & 31) * 255f / 31, (v >> 5 & 63) * 255f / 63, (v & 31) * 255f / 31);

    /// <summary>BC3 alpha block: eight interpolated values between the block's min and max alpha.</summary>
    static void EncodeAlpha(BinaryWriter bw, uint[] block)
    {
        int lo = 255, hi = 0;
        foreach (var c in block) { int a = (int)(c >> 24); lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
        bw.Write((byte)hi); bw.Write((byte)lo);
        ulong bits = 0;
        if (hi != lo)
            for (int i = 0; i < 16; i++)
            {
                int a = (int)(block[i] >> 24);
                // Palette: 0 = hi, 1 = lo, 2..7 = hi→lo in sevenths.
                int step = (int)MathF.Round((hi - a) * 7f / (hi - lo));
                int idx = step == 0 ? 0 : step == 7 ? 1 : step + 1;
                bits |= (ulong)idx << (3 * i);
            }
        for (int i = 0; i < 6; i++) bw.Write((byte)(bits >> (8 * i)));
    }
}
