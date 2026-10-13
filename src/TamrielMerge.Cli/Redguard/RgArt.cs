using TamrielMerge.Morrowind;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's textures for Skyrim, under textures\tamrielmerge\rg\&lt;palette&gt;\. Redguard draws the same texture banks
/// (3dart\TEXTURE.###) through a different palette per map (WORLD.INI world_palette), so each palette gets its own
/// copies. Each image is enlarged — from the AI upscaler's cache (&lt;flat name&gt;.rgba, as Morrowind's) when it has the
/// image, else 4x with Scale2x twice, which keeps the pixel art's edges — and written as a BC1/BC3 DDS with a relief
/// normal map from its own shading (MwTextures.WriteNormalMap), so the art keeps its look under Skyrim's lighting.
/// Palette index 0 is see-through. Faces drawn in a flat palette colour get a small solid texture.
/// </summary>
public sealed class RgArt(string artDir, string modOutDir)
{
    public const string OutFolder = @"textures\tamrielmerge\rg";
    public const string FlatNormal = @"textures\tamrielmerge\rg\flat_n.dds";

    /// <summary>Folder of upscaled images (&lt;flat name&gt;.rgba) to use instead of Scale2x, if set.</summary>
    public static string? UpscaleCache { get; set; }

    public sealed record Texture(string Diffuse, string Normal, bool Alpha, int Width, int Height);

    readonly Dictionary<string, RgPalette> palettes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, RgTextureBank?> banks = new();
    readonly Dictionary<(string, int, int), Texture?> done = new();
    public List<string> Problems { get; } = [];

    public RgPalette Palette(string name)
    {
        if (palettes.TryGetValue(name, out var hit)) return hit;
        string path = Directory.GetFiles(artDir, name + ".col", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault()
            ?? throw new FileNotFoundException($"palette {name}.COL");
        return palettes[name] = RgPalette.Load(File.ReadAllBytes(path));
    }

    public RgTextureBank? Bank(int id)
    {
        if (banks.TryGetValue(id, out var hit)) return hit;
        string path = Path.Combine(artDir, $"TEXTURE.{id:D3}");
        return banks[id] = File.Exists(path) ? RgTextureBank.Load(File.ReadAllBytes(path)) : null;
    }

    /// <summary>The image's key for the upscaler cache.</summary>
    public static string FlatName(string palette, int bank, int image) => $"rg__{palette.ToLowerInvariant()}__{bank}_{image}";

    /// <summary>A bank image through a palette, as Skyrim textures; null when the bank or image doesn't exist.</summary>
    public Texture? Ensure(string palette, int bank, int image)
    {
        var key = (palette.ToUpperInvariant(), bank, image);
        if (done.TryGetValue(key, out var hit)) return hit;
        done[key] = null;
        if (Bank(bank) is not { } b || !b.Images.TryGetValue(image, out var img)) { Problems.Add($"texture {bank}/{image} not found"); return null; }
        var px = RgTextureBank.ToArgb(img, Palette(palette));
        bool alpha = px.Any(p => p >> 24 == 0);
        if (alpha) Bleed(px, img.Width, img.Height);
        string rel = $@"{OutFolder}\{palette.ToLowerInvariant()}\{bank}_{image}.dds";
        string full = Path.Combine(modOutDir, rel), normal = Path.ChangeExtension(full, null) + "_n.dds";
        if (!File.Exists(full) || !File.Exists(normal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var (big, w, h) = Enlarge(px, img.Width, img.Height, FlatName(palette, bank, image), alpha);
            MwTextures.WriteBcDds(full, big, w, h, alpha);
            MwTextures.WriteNormalMap(big, w, h, normal);
        }
        return done[key] = new Texture(rel, Path.ChangeExtension(rel, null) + "_n.dds", alpha, img.Width, img.Height);
    }

    /// <summary>A small solid texture in a palette colour (faces Redguard draws flat-shaded).</summary>
    public Texture Solid(string palette, int colorIndex)
    {
        uint c = Palette(palette).Colors[colorIndex & 0xFF];
        string rel = $@"{OutFolder}\solid\{c & 0xFFFFFF:X6}.dds";
        string full = Path.Combine(modOutDir, rel);
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            MwTextures.WriteBcDds(full, Enumerable.Repeat(0xFF000000u | c, 16).ToArray(), 4, 4, false);
        }
        string flat = Path.Combine(modOutDir, FlatNormal);
        if (!File.Exists(flat)) MwTextures.WriteBcDds(flat, Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4, true);
        return new Texture(rel, FlatNormal, false, 4, 4);
    }

    static (uint[] Px, int W, int H) Enlarge(uint[] px, int w, int h, string flatName, bool alpha)
    {
        if (!alpha && UpscaleCache is { } cache && MwTextures.ReadRaw(Path.Combine(cache, flatName + ".rgba")) is { } up) return up;
        return Scale2x(Scale2x((px, w, h)));
    }

    /// <summary>See-through pixels take the colour of a visible neighbour, so filtering and mipmaps don't darken edges.</summary>
    static void Bleed(uint[] px, int w, int h)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var copy = (uint[])px.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (copy[y * w + x] >> 24 != 0 || (copy[y * w + x] & 0xFFFFFF) != 0) continue;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        uint n = copy[ny * w + nx];
                        if ((n & 0xFFFFFF) != 0) { px[y * w + x] = n & 0xFFFFFF; break; }
                    }
                }
        }
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
                int o = y * 2 * w * 2 + x * 2;
                d[o] = c == a && c != dd && a != b ? a : p;
                d[o + 1] = a == b && a != c && b != dd ? b : p;
                d[o + w * 2] = dd == c && dd != b && c != a ? c : p;
                d[o + w * 2 + 1] = b == dd && b != a && dd != c ? dd : p;
            }
        return (d, w * 2, h * 2);
    }

    /// <summary>Every image of every bank through each palette, as raw pixels for the upscaler (opaque images only).</summary>
    public int ExportAll(IEnumerable<string> paletteNames, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var file in Directory.GetFiles(artDir, "TEXTURE.*"))
        {
            if (!int.TryParse(Path.GetExtension(file).TrimStart('.'), out int id) || Bank(id) is not { } bank) continue;
            foreach (var pal in paletteNames)
                foreach (var img in bank.Images.Values)
                {
                    var px = RgTextureBank.ToArgb(img, Palette(pal));
                    if (px.Any(p => p >> 24 == 0)) continue;
                    MwTextures.WriteRaw(Path.Combine(outDir, FlatName(pal, id, img.Index) + ".rgba"), px, img.Width, img.Height);
                    n++;
                }
        }
        return n;
    }
}
