using System.IO.MemoryMappedFiles;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Terrain;

public sealed record TerrainSettings
{
    /// <summary>
    /// Daggerfall map pixels per Skyrim cell edge. 4 => 250x125 cells (~1.9x Skyrim's land area, one worldspace);
    /// 2 => 500x250 (~8x, in tiles: OverlandTiles); 1 => 1000x500 (~30x, 15 tiles). Daggerfall's own scale would be
    /// 1/16. 1 since 2026-10-12 (user: "daggerfall has a big map"; "Twice as big"): the land's slopes half as steep
    /// for the same heights (it was 2).
    /// </summary>
    public int PixelsPerCell { get; init; } = 1;
    /// <summary>Skyrim units per Daggerfall Unity "scaled height" unit (max land ~1500 scaled).</summary>
    public float VerticalScale { get; init; } = 10f;
    /// <summary>Height multiplier grows by 1 per this many scaled units above sea level. 2000 since 2026-10-12 (it was
    /// 1100): with the map twice as big, peaks ~22000 units and 1% of the land steeper than 26 degrees (was 29000 and 42),
    /// nearer Daggerfall's broad mountains (user: "the landscape needs worked on" - terrain shape).</summary>
    public float MountainExaggeration { get; init; } = 2000f;
    /// <summary>Scaled height treated as sea level (DFU clamps the ocean floor at 3.4 * 8 = 27.2).</summary>
    public float SeaLevelScaled { get; init; } = 30f;
}

/// <summary>
/// Global terrain heights for the whole Daggerfall map on Skyrim's vertex grid (32 quads of 128 units per cell).
/// Row 0 is the southern edge; Skyrim's Y axis points north while Daggerfall's map rows run north to south.
/// </summary>
public sealed unsafe class HeightField
{
    public const int QuadsPerCell = 32, UnitsPerQuad = 128;

    public required TerrainSettings Settings { get; init; }
    public required int CellsX { get; init; }
    public required int CellsY { get; init; }
    public int VertsX => CellsX * QuadsPerCell + 1;
    public int VertsY => CellsY * QuadsPerCell + 1;
    /// <summary>Skyrim grid coordinate of the south-west cell.</summary>
    public int MinCellX => -CellsX / 2;
    public int MinCellY => -CellsY / 2;
    /// <summary>Heights in Skyrim units, sea level at 0, indexed [vy * VertsX + vx]; null when read from a shared file
    /// (<see cref="Shared"/>), then <see cref="At"/> reads them.</summary>
    public float[]? Heights { get; init; }
    float* mapped;
    MemoryMappedFile? mappedFile;
    MemoryMappedViewAccessor? mappedView;

    public float At(int vx, int vy)
    {
        long i = (long)Math.Clamp(vy, 0, VertsY - 1) * VertsX + Math.Clamp(vx, 0, VertsX - 1);
        return Heights is { } h ? h[i] : mapped[i];
    }

    /// <summary>The highest point.</summary>
    public float Peak()
    {
        if (Heights is { } h) return h.Max();
        float peak = float.MinValue;
        for (long i = 0, n = (long)VertsX * VertsY; i < n; i++) peak = Math.Max(peak, mapped[i]);
        return peak;
    }

    /// <summary>
    /// The heights for Daggerfall's town and dungeon parts: each part made them itself, 22 seconds and 2 GB of memory
    /// each, 124 times a conversion (2026-10-14, user: "should be a 1 hour conversion"). The conversion writes them once
    /// (<see cref="Save"/>) to the file TAMRIELMERGE_HEIGHTS names, and every part reads that file in place: the
    /// system keeps one copy in memory for all of them. Made here when there is no file or it is for other settings.
    /// </summary>
    public static HeightField Shared(DfWorldMap map, TerrainSettings s)
    {
        if (Environment.GetEnvironmentVariable("TAMRIELMERGE_HEIGHTS") is { Length: > 0 } path && File.Exists(path))
        {
            try
            {
                var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                p += view.PointerOffset;
                var head = System.Text.Encoding.UTF8.GetString(p, HeaderSize).TrimEnd('\0').Split('\n');
                int cellsX = (DfWorldMap.Width + s.PixelsPerCell - 1) / s.PixelsPerCell, cellsY = (DfWorldMap.Height + s.PixelsPerCell - 1) / s.PixelsPerCell;
                if (head.Length >= 4 && head[0] == Magic && head[1] == cellsX.ToString() && head[2] == cellsY.ToString() && head[3] == s.ToString())
                    return new HeightField { Settings = s, CellsX = cellsX, CellsY = cellsY, mapped = (float*)(p + HeaderSize), mappedFile = file, mappedView = view };
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose(); file.Dispose();
                Console.WriteLine($"Heights: {Path.GetFileName(path)} is for other settings; making them here");
            }
            catch (IOException e) { Console.WriteLine($"Heights: {path} unreadable ({e.Message}); making them here"); }
        }
        return Generate(map, s);
    }

    const string Magic = "TamrielMerge heights 1";
    const int HeaderSize = 4096;

    /// <summary>Writes the heights for <see cref="Shared"/>: a header naming their size and settings, then the floats.</summary>
    public void Save(string path)
    {
        if (Heights is not { } h) throw new InvalidOperationException("Only made heights are saved");
        string temp = path + ".tmp" + Environment.ProcessId;
        using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var head = new byte[HeaderSize];
            System.Text.Encoding.UTF8.GetBytes($"{Magic}\n{CellsX}\n{CellsY}\n{Settings}").CopyTo(head, 0);
            f.Write(head);
            f.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(h.AsSpan()));
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Continuous Daggerfall map-pixel coordinate (x east, y south) of a global vertex.</summary>
    public (double u, double v) PixelCoord(int vx, int vy)
    {
        double perVert = (double)Settings.PixelsPerCell / QuadsPerCell;
        return (vx * perVert, CellsY * Settings.PixelsPerCell - vy * perVert);
    }

    public static HeightField Generate(DfWorldMap map, TerrainSettings s)
    {
        int cellsX = (DfWorldMap.Width + s.PixelsPerCell - 1) / s.PixelsPerCell;
        int cellsY = (DfWorldMap.Height + s.PixelsPerCell - 1) / s.PixelsPerCell;
        var field = new HeightField { Settings = s, CellsX = cellsX, CellsY = cellsY, Heights = new float[(cellsX * QuadsPerCell + 1) * (cellsY * QuadsPerCell + 1)] };
        var coastDistance = CoastDistance(map, s);
        // WOODS.WLD's fine grid is the coarse height plus +-2 jitter per sample. At Daggerfall's own scale
        // (819 m per pixel) that reads as gentle rolling ground; compressed to this scale it becomes spikes,
        // so keep only its broad shape.
        const int fw = DfWorldMap.Width * DfWorldMap.FineScale, fh = DfWorldMap.Height * DfWorldMap.FineScale;
        // The coarse map is whole numbers, which bicubic sampling turns into terraces; soften the steps.
        var coarseSmooth = BoxBlur(map.Elevation.Select(b => (float)b).ToArray(), DfWorldMap.Width, DfWorldMap.Height, 1);
        var fineSmooth = BoxBlur(BoxBlur(map.FineElevation.Select(b => (float)b).ToArray(), fw, fh, 3), fw, fh, 3);
        double unitsPerPixel = (double)QuadsPerCell * UnitsPerQuad / s.PixelsPerCell;

        Parallel.For(0, field.VertsY, vy =>
        {
            for (int vx = 0; vx < field.VertsX; vx++)
            {
                var (u, v) = field.PixelCoord(vx, vy);
                // Pixel centres sit at +0.5; the fine map has 3 samples per pixel.
                double coarse = Bicubic((x, y) => coarseSmooth[Math.Clamp(y, 0, DfWorldMap.Height - 1) * DfWorldMap.Width + Math.Clamp(x, 0, DfWorldMap.Width - 1)], u - 0.5, v - 0.5);
                double fine = Bicubic((x, y) => fineSmooth[Math.Clamp(y, 0, fh - 1) * fw + Math.Clamp(x, 0, fw - 1)], u * DfWorldMap.FineScale - 0.5, v * DfWorldMap.FineScale - 0.5);
                double scaled = coarse * 8 + fine * 4;

                // Small-scale relief Daggerfall never had at this resolution, stronger in high country.
                double wx = u * unitsPerPixel, wy = v * unitsPerPixel;
                double detail = Noise.Fbm(wx / 5000, wy / 5000, 5) * (14 + scaled / 25);
                // Exaggerate with altitude so mountain ranges reach Skyrim-like heights while lowlands stay gentle.
                double above = scaled + detail - s.SeaLevelScaled;
                double h = above * s.VerticalScale * (above > 0 ? 1 + above / s.MountainExaggeration : 1);

                // Daggerfall's sea floor is flat; slope it away from the coast so the sea has depth.
                // Ramp the extra depth in over the first 150 units below sea level so the seabed stays continuous.
                if (h < 0)
                {
                    double d = Bilinear(coastDistance, DfWorldMap.Width, DfWorldMap.Height, u - 0.5, v - 0.5);
                    double ramp = Math.Clamp(-h / 150, 0, 1);
                    h -= Math.Min(d, 10) * 350 * ramp * ramp * (3 - 2 * ramp);
                }
                field.Heights![vy * field.VertsX + vx] = (float)h;
            }
        });
        LimitSlope(field.Heights!, field.VertsX, field.VertsY, MaxStep);
        return field;
    }

    /// <summary>LAND heights are signed-byte deltas of 8 units, so neighbours may differ by at most 127 * 8.</summary>
    public const float MaxStep = 1000;

    /// <summary>
    /// Raise-only slope limiter: sweep each axis both ways lifting any vertex more than <paramref name="limit"/>
    /// below its neighbour, until stable. Raising fills pits and sea floor rather than cutting down land.
    /// </summary>
    static void LimitSlope(float[] h, int w, int rows, float limit)
    {
        for (int pass = 0; pass < 20; pass++)
        {
            int changed = 0;
            Parallel.For(0, rows, () => 0, (y, _, local) =>
            {
                int o = y * w;
                for (int x = 1; x < w; x++) if (h[o + x] < h[o + x - 1] - limit) { h[o + x] = h[o + x - 1] - limit; local++; }
                for (int x = w - 2; x >= 0; x--) if (h[o + x] < h[o + x + 1] - limit) { h[o + x] = h[o + x + 1] - limit; local++; }
                return local;
            }, local => Interlocked.Add(ref changed, local));
            Parallel.For(0, w, () => 0, (x, _, local) =>
            {
                for (int y = 1; y < rows; y++) if (h[y * w + x] < h[(y - 1) * w + x] - limit) { h[y * w + x] = h[(y - 1) * w + x] - limit; local++; }
                for (int y = rows - 2; y >= 0; y--) if (h[y * w + x] < h[(y + 1) * w + x] - limit) { h[y * w + x] = h[(y + 1) * w + x] - limit; local++; }
                return local;
            }, local => Interlocked.Add(ref changed, local));
            if (changed == 0) return;
        }
        throw new InvalidOperationException("Slope limiter did not converge");
    }

    /// <summary>Distance in map pixels from each sea pixel to the nearest land pixel (two-pass chamfer).</summary>
    static float[] CoastDistance(DfWorldMap map, TerrainSettings s)
    {
        const int w = DfWorldMap.Width, h = DfWorldMap.Height;
        var d = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double scaled = map.ElevationAt(x, y) * 8 + map.FineAt(x * DfWorldMap.FineScale + 1, y * DfWorldMap.FineScale + 1) * 4;
                d[y * w + x] = scaled > s.SeaLevelScaled ? 0 : float.MaxValue / 2;
            }
        const float diag = 1.4142135f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                ref float c = ref d[y * w + x];
                if (x > 0) c = Math.Min(c, d[y * w + x - 1] + 1);
                if (y > 0) c = Math.Min(c, d[(y - 1) * w + x] + 1);
                if (x > 0 && y > 0) c = Math.Min(c, d[(y - 1) * w + x - 1] + diag);
                if (x < w - 1 && y > 0) c = Math.Min(c, d[(y - 1) * w + x + 1] + diag);
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                ref float c = ref d[y * w + x];
                if (x < w - 1) c = Math.Min(c, d[y * w + x + 1] + 1);
                if (y < h - 1) c = Math.Min(c, d[(y + 1) * w + x] + 1);
                if (x < w - 1 && y < h - 1) c = Math.Min(c, d[(y + 1) * w + x + 1] + diag);
                if (x > 0 && y < h - 1) c = Math.Min(c, d[(y + 1) * w + x - 1] + diag);
            }
        return d;
    }

    static double Bilinear(float[] data, int w, int h, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        float S(int xx, int yy) => data[Math.Clamp(yy, 0, h - 1) * w + Math.Clamp(xx, 0, w - 1)];
        return (S(x0, y0) * (1 - fx) + S(x0 + 1, y0) * fx) * (1 - fy) + (S(x0, y0 + 1) * (1 - fx) + S(x0 + 1, y0 + 1) * fx) * fy;
    }

    static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += src[y * w + Math.Clamp(x + k, 0, w - 1)];
                tmp[y * w + x] = sum / (2 * r + 1);
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -r; k <= r; k++) sum += tmp[Math.Clamp(y + k, 0, h - 1) * w + x];
                dst[y * w + x] = sum / (2 * r + 1);
            }
        });
        return dst;
    }

    static double Bicubic(Func<int, int, double> sample, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        Span<double> col = stackalloc double[4];
        for (int j = -1; j <= 2; j++)
            col[j + 1] = Cubic(sample(x0 - 1, y0 + j), sample(x0, y0 + j), sample(x0 + 1, y0 + j), sample(x0 + 2, y0 + j), fx);
        return Cubic(col[0], col[1], col[2], col[3], fy);
    }

    /// <summary>
    /// Uniform cubic B-spline: smooth through every sample (curvature too), never overshooting. Until 2026-10-12 this
    /// was Daggerfall Unity's TerrainHelper.CubicInterpolator, whose slope at each sample is twice a Catmull-Rom's: every
    /// step between the coarse map's whole-number heights became an S, steep at the pixel's edge and flat in its middle
    /// - a terrace per map pixel, a grid across the land in shading (user: "the landscape needs worked on").
    /// </summary>
    static double Cubic(double v0, double v1, double v2, double v3, double t)
    {
        double t2 = t * t, t3 = t2 * t, u = 1 - t;
        return (u * u * u * v0 + (3 * t3 - 6 * t2 + 4) * v1 + (-3 * t3 + 3 * t2 + 3 * t + 1) * v2 + t3 * v3) / 6;
    }
}

/// <summary>Deterministic 2D gradient noise.</summary>
static class Noise
{
    static readonly byte[] Perm = BuildPerm();

    static byte[] BuildPerm()
    {
        var p = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        new Random(0xDA66E5).Shuffle(p);
        return [.. p, .. p];
    }

    static double Grad(int hash, double x, double y) => (hash & 3) switch { 0 => x + y, 1 => -x + y, 2 => x - y, _ => -x - y };
    static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    public static double Perlin(double x, double y)
    {
        int xi = (int)Math.Floor(x) & 255, yi = (int)Math.Floor(y) & 255;
        double xf = x - Math.Floor(x), yf = y - Math.Floor(y);
        double u = Fade(xf), v = Fade(yf);
        int aa = Perm[Perm[xi] + yi], ab = Perm[Perm[xi] + yi + 1], ba = Perm[Perm[xi + 1] + yi], bb = Perm[Perm[xi + 1] + yi + 1];
        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    public static double Fbm(double x, double y, int octaves)
    {
        double sum = 0, amp = 1, norm = 0;
        for (int i = 0; i < octaves; i++, x *= 2.03, y *= 2.03, amp *= 0.5)
        {
            sum += Perlin(x, y) * amp;
            norm += amp;
        }
        return sum / norm;
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
