using System.Numerics;
using TamrielMerge.Util;

namespace TamrielMerge.Daggerfall;

/// <summary>Text summary and a top-down floor render of a Daggerfall dungeon, for planning its conversion.</summary>
public static class DungeonPreview
{
    public static void Report(DfDungeon dungeon, IReadOnlyDictionary<uint, DfModel> models, string pngPath)
    {
        var loc = dungeon.Location;
        Console.WriteLine($"{loc.Name} ({loc.Type}/{loc.DungeonType}) {dungeon.Blocks.Count} blocks:");
        foreach (var (b, p) in dungeon.Blocks)
            Console.WriteLine($"  {b.Name} at ({p.X},{p.Z}){(p.IsStart ? " START" : "")}: {b.Objects.Count(o => o.Type == DfRdbObjectType.Model)} models, {b.Objects.Count(o => o.Type == DfRdbObjectType.Flat)} flats, {b.Objects.Count(o => o.Type == DfRdbObjectType.Light)} lights, {b.Objects.Count(o => o.HasAction)} with actions");

        var objs = dungeon.Blocks.SelectMany(x => x.Block.Objects).ToList();
        Console.WriteLine("models (count, id, size x*y*z):");
        foreach (var g in objs.Where(o => o.Type == DfRdbObjectType.Model).GroupBy(o => o.ModelId).OrderByDescending(g => g.Count()).Take(40))
        {
            var m = models.GetValueOrDefault(g.Key);
            var size = m is null ? Vector3.Zero : m.Max - m.Min;
            Console.WriteLine($"  {g.Count(),3} x {g.Key,6}  {size.X:F0}*{size.Y:F0}*{size.Z:F0}  rot {string.Join(",", g.Select(o => $"{o.Rotation.X:F0}/{o.Rotation.Y:F0}/{o.Rotation.Z:F0}").Distinct().Take(4))}");
        }
        Console.WriteLine("flats (archive.record count): " + string.Join(" ", objs.Where(o => o.Type == DfRdbObjectType.Flat).GroupBy(o => $"{o.TextureArchive}.{o.TextureRecord}").OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}")));

        var polys = dungeon.Polygons(models).ToList();
        Console.WriteLine("texture archives on polygons: " + string.Join(" ", polys.GroupBy(p => p.TextureArchive).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}")));
        var min = polys.SelectMany(p => p.Points).Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = polys.SelectMany(p => p.Points).Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Console.WriteLine($"extent DF units: x {min.X:F0}..{max.X:F0}  y(up) {min.Y:F0}..{max.Y:F0}  z {min.Z:F0}..{max.Z:F0}");

        // Top-down render: floors coloured by height (blue low .. red high), the highest floor wins.
        const float unitsPerPixel = 8;
        int w = (int)((max.X - min.X) / unitsPerPixel) + 1, h = (int)((max.Z - min.Z) / unitsPerPixel) + 1;
        var top = new float[w * h];
        Array.Fill(top, float.NaN);
        foreach (var p in polys.Where(p => p.Normal.Y > 0.7f))
            for (int i = 1; i + 1 < p.Points.Length; i++)
                FillTriangle(p.Points[0], p.Points[i], p.Points[i + 1], min, unitsPerPixel, w, h, top);
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < top.Length; i++)
        {
            int px = i % w, py = h - 1 - i / w; // north up
            int o = (py * w + px) * 3;
            if (float.IsNaN(top[i])) { rgb[o] = rgb[o + 1] = rgb[o + 2] = 20; continue; }
            float t = (top[i] - min.Y) / Math.Max(1, max.Y - min.Y);
            rgb[o] = (byte)(60 + 195 * t); rgb[o + 1] = (byte)(90 + 60 * (1 - Math.Abs(t - 0.5f) * 2)); rgb[o + 2] = (byte)(60 + 195 * (1 - t));
        }
        // Mark the start (green), enter (yellow) markers and the exit door (red) in the start block.
        foreach (var (block, placement) in dungeon.Blocks.Where(b => b.Placement.IsStart))
        {
            var origin = DfDungeon.BlockOrigin(placement);
            foreach (var o in block.Objects)
            {
                (byte r, byte g, byte b)? color =
                    o.Type == DfRdbObjectType.Flat && o.TextureArchive == 199 && o.TextureRecord == 10 ? ((byte)0, (byte)255, (byte)0) :
                    o.Type == DfRdbObjectType.Flat && o.TextureArchive == 199 && o.TextureRecord == 8 ? ((byte)255, (byte)255, (byte)0) :
                    o.Type == DfRdbObjectType.Model && o.ModelId == 70300 ? ((byte)255, (byte)0, (byte)0) : null;
                if (color is not { } c) continue;
                var p = origin + o.Position;
                int cx = (int)((p.X - min.X) / unitsPerPixel), cz = h - 1 - (int)((p.Z - min.Z) / unitsPerPixel);
                int ti = Math.Clamp((int)((p.Z - min.Z) / unitsPerPixel), 0, h - 1) * w + Math.Clamp(cx, 0, w - 1);
                Console.WriteLine($"  marker {(o.Type == DfRdbObjectType.Model ? "exit door" : $"199.{o.TextureRecord}")} at DF ({p.X:F0},{p.Y:F0},{p.Z:F0}) rotY={o.Rotation.Y:F0}, top floor there {top[ti]:F0}");
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = cx + dx, y = cz + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int o2 = (y * w + x) * 3;
                        (rgb[o2], rgb[o2 + 1], rgb[o2 + 2]) = c;
                    }
            }
        }
        Png.WriteRgb(pngPath, w, h, rgb);
        Console.WriteLine($"render: {Path.GetFullPath(pngPath)} ({w}x{h}, {unitsPerPixel} DF units/pixel)");
    }

    static void FillTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 min, float upp, int w, int h, float[] top)
    {
        float ax = (a.X - min.X) / upp, az = (a.Z - min.Z) / upp, bx = (b.X - min.X) / upp, bz = (b.Z - min.Z) / upp, cx = (c.X - min.X) / upp, cz = (c.Z - min.Z) / upp;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int z0 = Math.Max(0, (int)MathF.Floor(MathF.Min(az, MathF.Min(bz, cz)))), z1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(az, MathF.Max(bz, cz))));
        float area = (bx - ax) * (cz - az) - (cx - ax) * (bz - az);
        if (MathF.Abs(area) < 1e-6f) return;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, pz = z + 0.5f;
                float w0 = ((bx - px) * (cz - pz) - (cx - px) * (bz - pz)) / area;
                float w1 = ((cx - px) * (az - pz) - (ax - px) * (cz - pz)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float y = w0 * a.Y + w1 * b.Y + w2 * c.Y;
                int i = z * w + x;
                if (float.IsNaN(top[i]) || y > top[i]) top[i] = y;
            }
    }
}
