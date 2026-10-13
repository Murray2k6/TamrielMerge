using TamrielMerge.Daggerfall;
using TamrielMerge.Util;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>Statistics and a shaded-relief preview image of a generated height field, for tuning scale.</summary>
public static class TerrainPreview
{
    public static void Report(HeightField f, DfWorldMap map, string pngPath, string? locationsArena2 = null)
    {
        int landCells = 0, coastCells = 0;
        for (int cy = 0; cy < f.CellsY; cy++)
            for (int cx = 0; cx < f.CellsX; cx++)
            {
                float max = float.MinValue;
                for (int y = 0; y <= HeightField.QuadsPerCell; y += 4)
                    for (int x = 0; x <= HeightField.QuadsPerCell; x += 4)
                        max = Math.Max(max, f.At(cx * HeightField.QuadsPerCell + x, cy * HeightField.QuadsPerCell + y));
                if (max > 0) landCells++;
                else if (max > TerrainBuilder.SkipBelow) coastCells++;
            }

        // Slope per vertex (degrees), sampled on a stride to keep it quick.
        var slopes = new List<float>();
        for (int vy = 1; vy < f.VertsY - 1; vy += 3)
            for (int vx = 1; vx < f.VertsX - 1; vx += 3)
            {
                if (f.At(vx, vy) <= 0) continue;
                double dx = (f.At(vx + 1, vy) - f.At(vx - 1, vy)) / (2.0 * HeightField.UnitsPerQuad);
                double dy = (f.At(vx, vy + 1) - f.At(vx, vy - 1)) / (2.0 * HeightField.UnitsPerQuad);
                slopes.Add((float)(Math.Atan(Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI));
            }
        slopes.Sort();
        // VHGT deltas are signed bytes of 8 units: neighbours more than 1016 units apart can't be encoded.
        var cliffs = new List<(int vx, int vy, float d)>();
        for (int vy = 0; vy < f.VertsY; vy++)
            for (int vx = 1; vx < f.VertsX; vx++)
            {
                float d = Math.Max(Math.Abs(f.At(vx, vy) - f.At(vx - 1, vy)), Math.Abs(f.At(vx, vy) - f.At(vx, vy - 1)));
                if (d > 1016) cliffs.Add((vx, vy, d));
            }
        Console.WriteLine($"unencodable steps (>1016 units between neighbours): {cliffs.Count}");
        foreach (var c in cliffs.OrderByDescending(c => c.d).Take(5))
        {
            var (u, v) = f.PixelCoord(c.vx, c.vy);
            Console.WriteLine($"  step {c.d:F0} at DF pixel ({u:F1},{v:F1}) climate {map.ClimateAt((int)u, (int)v)} h={f.At(c.vx, c.vy):F0}");
        }
        float P(double q) => slopes[(int)(q * (slopes.Count - 1))];
        Console.WriteLine($"cells: {f.CellsX}x{f.CellsY}, land {landCells} (Skyrim's Tamriel has 11186), coastal/shallow {coastCells}");
        Console.WriteLine($"height: max {f.Heights.Max():F0} min {f.Heights.Min():F0} units (Skyrim peaks ~32000)");
        Console.WriteLine($"land slope deg: p50 {P(0.5):F1}  p90 {P(0.9):F1}  p99 {P(0.99):F1}  max {slopes[^1]:F1}  >45deg {slopes.Count(s => s > 45) * 100.0 / slopes.Count:F2}%");

        // Shaded relief, one image pixel per 8 vertices (=> 1000x500 for the default scale), north up.
        const int step = 8;
        int w = (f.VertsX - 1) / step, h = (f.VertsY - 1) / step;
        var rgb = new byte[w * h * 3];
        float peak = Math.Max(1, f.Heights.Max());
        for (int iy = 0; iy < h; iy++)
            for (int ix = 0; ix < w; ix++)
            {
                int vx = ix * step, vy = (h - 1 - iy) * step;
                float z = f.At(vx, vy);
                double dzx = (f.At(vx + 1, vy) - f.At(vx - 1, vy)) / (2.0 * HeightField.UnitsPerQuad);
                double dzy = (f.At(vx, vy + 1) - f.At(vx, vy - 1)) / (2.0 * HeightField.UnitsPerQuad);
                double shade = Math.Clamp(0.75 + (-dzx + dzy) * 0.6, 0.25, 1.3);
                double r, g, b;
                if (z <= 0) { double t = Math.Clamp(-z / 3500, 0, 1); r = 40 - 25 * t; g = 90 - 50 * t; b = 160 - 60 * t; shade = 1; }
                else
                {
                    double t = z / peak;
                    (r, g, b) = t < 0.35 ? (90 + 120 * t, 140 + 60 * t, 70) : t < 0.7 ? (150 + 50 * t, 140, 90) : (220, 220, 225);
                }
                int o = (iy * w + ix) * 3;
                rgb[o] = (byte)Math.Clamp(r * shade, 0, 255);
                rgb[o + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                rgb[o + 2] = (byte)Math.Clamp(b * shade, 0, 255);
            }
        // Overlay towns (red) and cities (yellow) to check they land on the coastline/land where expected.
        int inWater = 0, total = 0;
        foreach (var loc in DfLocations.LoadAll(locationsArena2 ?? ""))
        {
            var p = LocationPlacer.WorldPosition(f, loc.PixelX + 0.5, loc.PixelY + 0.5);
            total++;
            if (p.Z <= 0) inWater++;
            if (loc.Type is not (DfLocationType.TownCity or DfLocationType.TownHamlet)) continue;
            int r = loc.Type == DfLocationType.TownCity ? 2 : 1;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int ix = loc.PixelX + dx, iy = loc.PixelY + dy;
                    if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                    int o = (iy * w + ix) * 3;
                    (rgb[o], rgb[o + 1], rgb[o + 2]) = loc.Type == DfLocationType.TownCity ? ((byte)255, (byte)230, (byte)0) : ((byte)220, (byte)30, (byte)30);
                }
        }
        Console.WriteLine($"locations: {total}, at or below sea level: {inWater}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
        Png.WriteRgb(pngPath, w, h, rgb);
        Console.WriteLine($"preview: {Path.GetFullPath(pngPath)} ({w}x{h})");
    }
}
