using System.Numerics;
using TamrielMerge.Util;

namespace TamrielMerge.Daggerfall;

/// <summary>Renders Daggerfall models as shaded isometric thumbnails on a contact sheet, for identifying props.</summary>
public static class ModelSheet
{
    public static void Render(IReadOnlyList<DfModel> models, string path, int columns = 8, int cell = 160)
    {
        int rows = (models.Count + columns - 1) / columns;
        int w = columns * cell, h = rows * cell;
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)32);
        var light = Vector3.Normalize(new Vector3(0.4f, 0.8f, -0.45f));
        // Isometric-ish view: yaw 35 degrees, pitch 30 degrees, Y up.
        var view = Matrix4x4.CreateRotationY(35 * MathF.PI / 180) * Matrix4x4.CreateRotationX(-30 * MathF.PI / 180);
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var pts = m.Polygons.SelectMany(p => p.Points).Select(p => Vector3.Transform(p - (m.Min + m.Max) / 2, view)).ToList();
            if (pts.Count == 0) continue;
            float extent = MathF.Max(pts.Max(p => MathF.Abs(p.X)), pts.Max(p => MathF.Abs(p.Y))) + 1;
            float scale = (cell / 2f - 8) / extent;
            int ox = (i % columns) * cell + cell / 2, oy = (i / columns) * cell + cell / 2;
            var depth = new float[cell * cell];
            Array.Fill(depth, float.MaxValue);
            foreach (var poly in m.Polygons)
            {
                var vp = poly.Points.Select(p => Vector3.Transform(p - (m.Min + m.Max) / 2, view)).ToArray();
                float shade = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(Vector3.Normalize(poly.Normal), light));
                // Tint by Daggerfall texture family so wood, metal and stone read differently.
                var tint = (poly.TextureArchive % 100) switch
                {
                    67 or 90 or 92 => new Vector3(170, 120, 70),
                    93 => new Vector3(150, 150, 170),
                    0 or 1 => new Vector3(110, 110, 110),
                    _ => new Vector3(190, 185, 170),
                };
                for (int t = 1; t + 1 < vp.Length; t++)
                    Fill(vp[0], vp[t], vp[t + 1], scale, ox, oy, cell, (i % columns) * cell, (i / columns) * cell, depth, rgb, w, tint * shade);
            }
        }
        Png.WriteRgb(path, w, h, rgb);
    }

    static void Fill(Vector3 a, Vector3 b, Vector3 c, float s, int ox, int oy, int cell, int cx0, int cy0, float[] depth, byte[] rgb, int w, Vector3 col)
    {
        float ax = ox + a.X * s, ay = oy - a.Y * s, bx = ox + b.X * s, by = oy - b.Y * s, cx = ox + c.X * s, cy = oy - c.Y * s;
        float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (MathF.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(cx0, (int)MathF.Min(ax, MathF.Min(bx, cx))), x1 = Math.Min(cx0 + cell - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int y0 = Math.Max(cy0, (int)MathF.Min(ay, MathF.Min(by, cy))), y1 = Math.Min(cy0 + cell - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area, w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area, w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int di = (y - cy0) * cell + (x - cx0);
                if (z >= depth[di]) continue;
                depth[di] = z;
                int o = (y * w + x) * 3;
                rgb[o] = (byte)Math.Clamp(col.X, 0, 255); rgb[o + 1] = (byte)Math.Clamp(col.Y, 0, 255); rgb[o + 2] = (byte)Math.Clamp(col.Z, 0, 255);
            }
    }
}
