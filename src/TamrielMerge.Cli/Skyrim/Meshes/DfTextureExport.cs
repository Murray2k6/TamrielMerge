using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Daggerfall's own textures as Skyrim's: the towns' buildings, inside and out (TownTextures), and in dungeons the
/// surfaces Skyrim has nothing like (the stained-glass and leaded windows, tapestries and banners; the rest there uses
/// Skyrim textures, BlockMeshBuilder.PickTexture). Each is four times its size: from the upscale cache when it is
/// there (tools/upscale/out-df: Real-ESRGAN, from df-upscale-export and tools/upscale/upscale.py), else Scale2x twice,
/// which keeps the pixel art's edges crisp; written as an uncompressed DDS with mipmaps under textures\tamrielmerge\df\.
/// </summary>
public sealed class DfTextureExport(string arena2, string modOutDir)
{
    /// <summary>Upscaled textures ({archive}_{record}.rgba): the tool's own (upscale-textures, Program.UpscaleFolder), else
    /// found by looking up from the tool for tools\upscale\out-df.</summary>
    public static string? UpscaleCache { get; set; } = FindCache();

    static string? FindCache()
    {
        if (Program.UpscaleFolder(Game.Daggerfall) is var own && Directory.Exists(own) && Directory.EnumerateFiles(own, "*.rgba").Any()) return own;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Path.Combine(dir.FullName, "tools", "upscale", "out-df") is var p && Directory.Exists(p)) return p;
        return null;
    }

    /// <summary>
    /// Every record of Daggerfall's wall, floor and building texture archives (below 500: the rest are billboards) as raw
    /// pixels ({archive}_{record}.rgba: int32 width, height, then 0xAARRGGBB) for tools/upscale/upscale.py. Returns the count.
    /// </summary>
    public static int ExportAll(string arena2, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var palette = DfTexture.Palette(arena2);
        int n = 0;
        for (int archive = 0; archive < 500; archive++)
        {
            var tex = DfTexture.Load(arena2, archive);
            if (tex is null) continue;
            for (int record = 0; record < tex.Count; record++)
            {
                DfImage img;
                try { img = tex.Image(record); }
                catch (ArgumentException) { continue; }   // a record this reader can't decode: it keeps Scale2x
                if (img.Width == 0 || img.Height == 0) continue;
                using var bw = new BinaryWriter(File.Create(Path.Combine(outDir, $"{archive}_{record}.rgba")));
                bw.Write(img.Width); bw.Write(img.Height);
                foreach (var p in Colours(img.Pixels, palette)) bw.Write(p);
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Daggerfall sprites (billboards: grass) for tools/upscale/upscale.py like <see cref="ExportAll"/>, but see-through
    /// where Daggerfall's are (palette index 0) and with the colour of the nearest drawn pixel there, so the upscaler
    /// doesn't pull a dark fringe in from the black around them. Returns the count.
    /// </summary>
    public static int ExportSprites(string arena2, string outDir, IEnumerable<(int Archive, int Record)> sprites)
    {
        Directory.CreateDirectory(outDir);
        var palette = DfTexture.Palette(arena2);
        int n = 0;
        foreach (var (archive, record) in sprites)
        {
            if (DfTexture.Load(arena2, archive) is not { } tex || record >= tex.Count) continue;
            var img = tex.Image(record);
            using var bw = new BinaryWriter(File.Create(Path.Combine(outDir, $"{archive}_{record}.rgba")));
            bw.Write(img.Width); bw.Write(img.Height);
            foreach (var p in SpriteColours(img, palette)) bw.Write(p);
            n++;
        }
        return n;
    }

    /// <summary>A sprite's pixels, see-through at palette index 0, those coloured as the nearest drawn pixel.</summary>
    static uint[] SpriteColours(DfImage img, byte[] palette)
    {
        var px = Colours(img.Pixels, palette);
        var known = new bool[px.Length];
        for (int i = 0; i < px.Length; i++)
            if (img.Pixels[i] != 0) known[i] = true;
            else px[i] &= 0x00FFFFFF;
        // Grown a ring at a time: each see-through pixel takes the average of its drawn neighbours.
        for (bool grew = true; grew;)
        {
            grew = false;
            var next = (bool[])known.Clone();
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    int i = y * img.Width + x;
                    if (known[i]) continue;
                    int r = 0, g = 0, b = 0, k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= img.Width || ny >= img.Height || !known[ny * img.Width + nx]) continue;
                            uint c = px[ny * img.Width + nx];
                            r += (int)(c >> 16 & 0xFF); g += (int)(c >> 8 & 0xFF); b += (int)(c & 0xFF); k++;
                        }
                    if (k == 0) continue;
                    px[i] = (uint)(r / k) << 16 | (uint)(g / k) << 8 | (uint)(b / k);   // alpha stays 0
                    next[i] = grew = true;
                }
            known = next;
        }
        return px;
    }

    /// <summary>The DDS (data-relative path) for a Daggerfall sprite, see-through where it is, and its size in
    /// Daggerfall texels: upscaled from the cache (<see cref="ExportSprites"/>) when there, else Scale2x twice.</summary>
    public (string Path, int W, int H) EnsureSprite(int archive, int record)
    {
        if (done.TryGetValue((archive, record), out var hit)) return hit;
        palette ??= DfTexture.Palette(arena2);
        var tex = DfTexture.Load(arena2, archive) ?? throw new FileNotFoundException($"TEXTURE.{archive:D3}");
        var img = tex.Image(record);
        string rel = $@"textures\tamrielmerge\df\{archive}_{record}.dds";
        string full = Path.Combine(modOutDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var (big, w, h) = Upscaled(archive, record) ?? Scale2x(Scale2x((SpriteColours(img, palette), img.Width, img.Height)));
        WriteDds(full, big, w, h);
        string normal = Path.Combine(modOutDir, FlatNormal);
        if (!File.Exists(normal)) WriteDds(normal, Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4);
        return done[(archive, record)] = (rel, img.Width, img.Height);
    }

    static uint[] Colours(byte[] indices, byte[] palette)
    {
        // Palette index 0 is see-through in Daggerfall; kept opaque here (dark), as the panels are solid.
        var px = new uint[indices.Length];
        for (int i = 0; i < px.Length; i++)
        {
            int c = indices[i] * 3;
            px[i] = 0xFF000000u | (uint)palette[c] << 16 | (uint)palette[c + 1] << 8 | palette[c + 2];
        }
        return px;
    }
    /// <summary>Window textures: temple stained glass (163.0, 163.2), leaded arched window (63.2), barred temple window (161.3).</summary>
    static readonly HashSet<(int, int)> Windows = [(163, 0), (163, 2), (63, 2), (161, 3)];
    /// <summary>Whole archives: Banners, Tapestries, Generic Tapestries, Generic banners.</summary>
    static readonly HashSet<int> ArtArchives = [55, 57, 449, 450];

    public const string FlatNormal = @"textures\tamrielmerge\df\flat_n.dds";

    public static bool IsKept(int archive, int record) => ArtArchives.Contains(archive) || Windows.Contains((archive, record));
    public static bool IsWindow(int archive, int record) => Windows.Contains((archive, record));

    readonly Dictionary<(int, int), (string Path, int W, int H)> done = new();
    byte[]? palette;

    /// <summary>The DDS (data-relative path) for a Daggerfall texture and its size in Daggerfall texels.</summary>
    public (string Path, int W, int H) Ensure(int archive, int record)
    {
        if (done.TryGetValue((archive, record), out var hit)) return hit;
        palette ??= DfTexture.Palette(arena2);
        var tex = DfTexture.Load(arena2, archive) ?? throw new FileNotFoundException($"TEXTURE.{archive:D3}");
        var img = tex.Image(Math.Min(record, tex.Count - 1));
        string rel = $@"textures\tamrielmerge\df\{archive}_{record}.dds";
        string full = Path.Combine(modOutDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var (big, w, h) = Upscaled(archive, Math.Min(record, tex.Count - 1)) ?? Scale2x(Scale2x((Colours(img.Pixels, palette), img.Width, img.Height)));
        WriteDds(full, big, w, h);
        // Flat normal; alpha 0 = no specular (the alpha of a Skyrim normal map is its shine mask).
        string normal = Path.Combine(modOutDir, FlatNormal);
        if (!File.Exists(normal)) WriteDds(normal, Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4);
        return done[(archive, record)] = (rel, img.Width, img.Height);
    }

    /// <summary>
    /// A solid-colour surface (archive 0 "Solid Colors A": palette entry = record; archive 1 "B": 128 + record) as a
    /// small DDS of that colour (Daggerfall Unity TextureFile.ReadSolid).
    /// </summary>
    public string EnsureSolid(int archive, int record)
    {
        int index = Math.Clamp((archive == 1 ? 128 : 0) + record, 0, 255);
        if (done.TryGetValue((-1, index), out var hit)) return hit.Path;
        palette ??= DfTexture.Palette(arena2);
        string rel = $@"textures\tamrielmerge\df\solid_{index}.dds";
        string full = Path.Combine(modOutDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        uint c = 0xFF000000u | (uint)palette[index * 3] << 16 | (uint)palette[index * 3 + 1] << 8 | palette[index * 3 + 2];
        WriteDds(full, Enumerable.Repeat(c, 16).ToArray(), 4, 4);
        string normal = Path.Combine(modOutDir, FlatNormal);
        if (!File.Exists(normal)) WriteDds(normal, Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4);
        done[(-1, index)] = (rel, 4, 4);
        return rel;
    }

    /// <summary>The upscaled texture from the cache, or null.</summary>
    static (uint[] Px, int W, int H)? Upscaled(int archive, int record)
    {
        if (UpscaleCache is null) return null;
        string p = Path.Combine(UpscaleCache, $"{archive}_{record}.rgba");
        if (!File.Exists(p)) return null;
        var b = File.ReadAllBytes(p);
        int w = BitConverter.ToInt32(b, 0), h = BitConverter.ToInt32(b, 4);
        if (w <= 0 || h <= 0 || b.Length < 8 + w * h * 4) return null;
        var px = new uint[w * h];
        Buffer.BlockCopy(b, 8, px, 0, w * h * 4);
        return (px, w, h);
    }

    /// <summary>Scale2x (EPX): doubles pixel art, rounding diagonal edges without blurring.</summary>
    static (uint[] Px, int W, int H) Scale2x((uint[] Px, int W, int H) src)
    {
        var (s, w, h) = src;
        var d = new uint[w * 2 * h * 2];
        uint At(int x, int y) => s[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = At(x, y), a = At(x, y - 1), b = At(x + 1, y), c = At(x - 1, y), dd = At(x, y + 1);
                uint e0 = c == a && c != dd && a != b ? a : p;
                uint e1 = a == b && a != c && b != dd ? b : p;
                uint e2 = dd == c && dd != b && c != a ? c : p;
                uint e3 = b == dd && b != a && dd != c ? dd : p;
                int o = y * 2 * w * 2 + x * 2;
                d[o] = e0; d[o + 1] = e1; d[o + w * 2] = e2; d[o + w * 2 + 1] = e3;
            }
        return (d, w * 2, h * 2);
    }

    /// <summary>Uncompressed 32-bit DDS (A8R8G8B8) with a full mip chain (box filtered).</summary>
    internal static void WriteDds(string path, uint[] px, int w, int h)
    {
        var mips = new List<(uint[] Px, int W, int H)> { (px, w, h) };
        while (mips[^1].W > 1 || mips[^1].H > 1)
        {
            var (p, pw, ph) = mips[^1];
            int nw = Math.Max(1, pw / 2), nh = Math.Max(1, ph / 2);
            var n = new uint[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    uint r = 0, g = 0, b = 0, a = 0; int k = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(x * 2 + dx, pw - 1), sy = Math.Min(y * 2 + dy, ph - 1);
                            uint c = p[sy * pw + sx];
                            a += c >> 24; r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; k++;
                        }
                    n[y * nw + x] = (uint)(a / k) << 24 | (uint)(r / k) << 16 | (uint)(g / k) << 8 | (uint)(b / k);
                }
            mips.Add((n, nw, nh));
        }
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(0x20534444u);                                   // "DDS "
        bw.Write(124u);                                          // header size
        bw.Write(0x1u | 0x2u | 0x4u | 0x8u | 0x1000u | 0x20000u); // caps, height, width, pitch, pixelformat, mipmapcount
        bw.Write((uint)h); bw.Write((uint)w); bw.Write((uint)(w * 4));
        bw.Write(0u); bw.Write((uint)mips.Count);
        for (int i = 0; i < 11; i++) bw.Write(0u);
        bw.Write(32u); bw.Write(0x1u | 0x40u);                   // pixel format: alpha pixels + RGB
        bw.Write(0u); bw.Write(32u);
        bw.Write(0x00FF0000u); bw.Write(0x0000FF00u); bw.Write(0x000000FFu); bw.Write(0xFF000000u);
        bw.Write(0x1000u | 0x8u | 0x400000u);                    // texture, complex, mipmap
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        foreach (var (p, _, _) in mips) foreach (var c in p) bw.Write(c);
    }
}
