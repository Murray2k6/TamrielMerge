using TamrielMerge.Util;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Distant;

/// <summary>
/// A worldspace's distant-object texture sheets, as Skyrim's own (textures\terrain\&lt;world&gt;\objects\&lt;world&gt;.objects.dds):
/// 4096-pixel pages of 128-pixel slots (each texture at 120 pixels inside a 4-pixel border of its own edge, so the
/// smaller mipmaps don't bleed), plus 16-pixel swatches of one colour. A shape whose texture repeats across it (a wall,
/// a road) can't use a slot, so it takes its texture's average colour from a swatch, as distant buildings read anyway;
/// trees take a slot with their picture from the side (<see cref="Billboard"/>).
/// </summary>
public sealed class DistantAtlas(Func<string, byte[]?> find)
{
    public const int PageSize = 4096, Slot = 128, Pad = 4, Inner = Slot - 2 * Pad, Swatch = 16;
    const int SlotsPerRow = PageSize / Slot, SlotsPerPage = SlotsPerRow * SlotsPerRow, SwatchesPerSlot = (Slot / Swatch) * (Slot / Swatch);

    readonly List<uint[]> pages = [];
    readonly Dictionary<string, (int Page, int X, int Y)?> images = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (int Page, Vector2 UV)> swatches = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (uint[] Px, int W, int H)?> decoded = new(StringComparer.OrdinalIgnoreCase);
    int slots;
    (int Page, int X, int Y, int Used)? swatchSlot;
    readonly object gate = new();

    public int Pages => pages.Count;

    /// <summary>A texture's pixels near 128 across (a smaller mipmap read straight from the file), or null.</summary>
    public (uint[] Px, int W, int H)? Pixels(string texture)
    {
        lock (gate)
        {
            if (decoded.TryGetValue(texture, out var hit)) return hit;
            (uint[] Px, int W, int H)? result = null;
            if (texture.Length > 0 && find(texture) is { Length: > 128 } bytes)
            {
                int h = BitConverter.ToInt32(bytes, 12), w = BitConverter.ToInt32(bytes, 16);
                int mip = 0;
                while ((Math.Max(w, h) >> (mip + 1)) >= Slot) mip++;
                try { result = DdsImage.Decode(bytes, mip); } catch { result = null; }
            }
            return decoded[texture] = result;
        }
    }

    (int Page, int X, int Y) NewSlot()
    {
        int page = slots / SlotsPerPage, index = slots % SlotsPerPage;
        slots++;
        while (pages.Count <= page) pages.Add(new uint[PageSize * PageSize]);
        return (page, index % SlotsPerRow * Slot, index / SlotsPerRow * Slot);
    }

    /// <summary>Where a texture's own UVs (0-1) land on the sheets, or null when it can't be read.</summary>
    public (int Page, Func<Vector2, Vector2> Map)? Image(string texture)
    {
        (int Page, int X, int Y)? at;
        lock (gate)
        {
            if (!images.TryGetValue(texture, out at))
            {
                at = null;
                if (Pixels(texture) is { } img)
                {
                    var s = NewSlot();
                    Blit(pages[s.Page], s.X, s.Y, img.Px, img.W, img.H);
                    at = s;
                }
                images[texture] = at;
            }
        }
        if (at is not { } a) return null;
        float u0 = (a.X + Pad) / (float)PageSize, v0 = (a.Y + Pad) / (float)PageSize, span = Inner / (float)PageSize;
        return (a.Page, uv => new Vector2(u0 + Math.Clamp(uv.X, 0, 1) * span, v0 + Math.Clamp(uv.Y, 0, 1) * span));
    }

    /// <summary>The middle of a swatch of the texture's average colour (grey when it can't be read).</summary>
    public (int Page, Vector2 UV) Colour(string texture)
    {
        lock (gate)
        {
            if (swatches.TryGetValue(texture, out var hit)) return hit;
            uint c = 0xFF807870;
            if (Pixels(texture) is { } img)
            {
                double r = 0, g = 0, b = 0, n = 0;
                foreach (var p in img.Px)
                {
                    double a = (p >> 24) / 255.0;
                    r += (p >> 16 & 255) * a; g += (p >> 8 & 255) * a; b += (p & 255) * a; n += a;
                }
                if (n > 0) c = 0xFF000000u | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
            }
            if (swatchSlot is not { } s || s.Used == SwatchesPerSlot) { var ns = NewSlot(); s = (ns.Page, ns.X, ns.Y, 0); }
            int sx = s.X + s.Used % (Slot / Swatch) * Swatch, sy = s.Y + s.Used / (Slot / Swatch) * Swatch;
            var page = pages[s.Page];
            for (int y = 0; y < Swatch; y++)
                for (int x = 0; x < Swatch; x++) page[(sy + y) * PageSize + sx + x] = c;
            swatchSlot = (s.Page, s.X, s.Y, s.Used + 1);
            return swatches[texture] = (s.Page, new Vector2((sx + Swatch / 2f) / PageSize, (sy + Swatch / 2f) / PageSize));
        }
    }

    /// <summary>A picture (a tree's billboard) in a slot of its own; the map from its 0-1 UVs.</summary>
    public (int Page, Func<Vector2, Vector2> Map) Picture(uint[] px, int w, int h)
    {
        (int Page, int X, int Y) s;
        lock (gate)
        {
            s = NewSlot();
            Blit(pages[s.Page], s.X, s.Y, px, w, h);
        }
        float u0 = (s.X + Pad) / (float)PageSize, v0 = (s.Y + Pad) / (float)PageSize, span = Inner / (float)PageSize;
        return (s.Page, uv => new Vector2(u0 + Math.Clamp(uv.X, 0, 1) * span, v0 + Math.Clamp(uv.Y, 0, 1) * span));
    }

    /// <summary>An image resized (bilinear) into a slot's inside, its edge carried out into the border.</summary>
    static void Blit(uint[] page, int sx, int sy, uint[] px, int w, int h)
    {
        for (int y = -Pad; y < Inner + Pad; y++)
            for (int x = -Pad; x < Inner + Pad; x++)
            {
                float fx = (Math.Clamp(x, 0, Inner - 1) + 0.5f) / Inner * w - 0.5f, fy = (Math.Clamp(y, 0, Inner - 1) + 0.5f) / Inner * h - 0.5f;
                page[(sy + Pad + y) * PageSize + sx + Pad + x] = Bilinear(px, w, h, fx, fy);
            }
    }

    static uint Bilinear(uint[] px, int w, int h, float fx, float fy)
    {
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        uint At(int x, int y) => px[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];
        uint a = At(x0, y0), b = At(x0 + 1, y0), c = At(x0, y0 + 1), d = At(x0 + 1, y0 + 1);
        uint Ch(int s) => (uint)Math.Clamp((int)MathF.Round(((a >> s & 255) * (1 - tx) + (b >> s & 255) * tx) * (1 - ty) + ((c >> s & 255) * (1 - tx) + (d >> s & 255) * tx) * ty), 0, 255);
        return Ch(24) << 24 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    /// <summary>Writes the pages (DXT5 with mipmaps) and one flat normal map; returns the page paths (Data-relative).</summary>
    public List<string> Write(string outDir, string world)
    {
        var rels = new List<string>();
        string dir = Path.Combine("textures", "terrain", world, "objects");
        Directory.CreateDirectory(Path.Combine(outDir, dir));
        for (int i = 0; i < pages.Count; i++)
        {
            string rel = Path.Combine(dir, PageName(world, i) + ".dds");
            Morrowind.MwTextures.WriteBcDds(Path.Combine(outDir, rel), pages[i], PageSize, PageSize, true);
            rels.Add(rel);
        }
        // Flat normals; alpha 0 = no shine.
        Morrowind.MwTextures.WriteBcDds(Path.Combine(outDir, NormalPath(world)), Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4, false);
        return rels;
    }

    public static string PageName(string world, int page) => page == 0 ? $"{world}.objects" : $"{world}.objects{page}";
    public static string PagePath(string world, int page) => Path.Combine("textures", "terrain", world, "objects", PageName(world, page) + ".dds");
    public static string NormalPath(string world) => Path.Combine("textures", "terrain", world, "objects", $"{world}.objects_n.dds");

    /// <summary>
    /// A tree from the side (looking north, east to the right): its shapes drawn orthographically into
    /// <paramref name="size"/> pixels, cut-out leaves at their alpha, lit from above and the south-west, the see-through
    /// pixels then given the colour beside them so the smaller mipmaps keep no dark fringe. Also its width (the larger
    /// of its two sides) and its height span.
    /// </summary>
    public uint[] Billboard(DistantModel m, int size, out float width, out float z0, out float z1, out Vector2 centre)
    {
        var px = new uint[size * size];
        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);
        centre = new Vector2((m.Min.X + m.Max.X) / 2, (m.Min.Y + m.Max.Y) / 2);
        width = MathF.Max(m.Max.X - m.Min.X, m.Max.Y - m.Min.Y);
        z0 = m.Min.Z; z1 = m.Max.Z;
        float height = MathF.Max(z1 - z0, 1), wd = MathF.Max(width, 1);
        var light = Vector3.Normalize(new Vector3(-0.4f, -0.6f, 0.7f));
        foreach (var part in m.Parts)
        {
            var tex = Pixels(part.Texture);
            var screen = new Vector3[part.P.Length];
            for (int i = 0; i < part.P.Length; i++)
            {
                var v = part.P[i];
                screen[i] = new Vector3((v.X - centre.X) / wd * size + size / 2f, (z1 - v.Z) / height * size, v.Y);
            }
            for (int t = 0; t < part.T.Length; t += 3)
            {
                int ia = part.T[t], ib = part.T[t + 1], ic = part.T[t + 2];
                Vector3 a = screen[ia], b = screen[ib], c = screen[ic];
                float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(area) < 1e-6f) continue;
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        float qx = x + 0.5f, qy = y + 0.5f;
                        float w0 = ((b.X - qx) * (c.Y - qy) - (b.Y - qy) * (c.X - qx)) / area;
                        float w1 = ((c.X - qx) * (a.Y - qy) - (c.Y - qy) * (a.X - qx)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                        int i = y * size + x;
                        if (z >= depth[i]) continue;
                        var uv = part.UV[ia] * w0 + part.UV[ib] * w1 + part.UV[ic] * w2;
                        uint col = 0xFF6E6450;
                        if (tex is { } tx)
                        {
                            float fu = uv.X - MathF.Floor(uv.X), fv = uv.Y - MathF.Floor(uv.Y);
                            col = tx.Px[Math.Clamp((int)(fv * tx.H), 0, tx.H - 1) * tx.W + Math.Clamp((int)(fu * tx.W), 0, tx.W - 1)];
                        }
                        if (part.Cutout && (col >> 24) < 128) continue;
                        var n = Vector3.Normalize(part.N[ia] * w0 + part.N[ib] * w1 + part.N[ic] * w2 + new Vector3(0, 0, 1e-4f));
                        float shade = 0.6f + 0.4f * MathF.Max(0, MathF.Abs(Vector3.Dot(n, light)));
                        uint Sh(int s) => (uint)Math.Clamp((int)((col >> s & 255) * shade), 0, 255);
                        px[i] = 0xFF000000u | Sh(16) << 16 | Sh(8) << 8 | Sh(0);
                        depth[i] = z;
                    }
            }
        }
        // See-through pixels take the colour beside them (alpha stays 0).
        for (int pass = 0; pass < 8; pass++)
        {
            var next = (uint[])px.Clone();
            bool grew = false;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    if (px[i] != 0) continue;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size || px[ny * size + nx] == 0) continue;
                        next[i] = px[ny * size + nx] & 0x00FFFFFFu | 0x00000001u;   // a colour with alpha 0 (the 1 marks it filled)
                        grew = true;
                        break;
                    }
                }
            px = next;
            if (!grew) break;
        }
        for (int i = 0; i < px.Length; i++) if ((px[i] >> 24) == 0) px[i] &= 0x00FFFFFEu;
        return px;
    }
}
