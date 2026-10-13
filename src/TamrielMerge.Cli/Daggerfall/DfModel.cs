using System.Numerics;

namespace TamrielMerge.Daggerfall;

/// <summary>One polygon of a Daggerfall 3D model, vertices in Daggerfall units with Y pointing up; Uvs its texture
/// coordinates in texels, one per point.</summary>
public sealed record DfPolygon(Vector3[] Points, Vector3 Normal, int TextureArchive, int TextureRecord, Vector2[]? Uvs = null);

/// <summary>A model from ARCH3D.BSA. Format reference: Daggerfall Unity Arch3dFile.cs.</summary>
public sealed class DfModel
{
    public required uint Id { get; init; }
    public required IReadOnlyList<DfPolygon> Polygons { get; init; }
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }

    const float PointDivisor = 256f;

    public static Dictionary<uint, DfModel> LoadAll(string arena2)
    {
        var bsa = new DfBsa(Path.Combine(arena2, "ARCH3D.BSA"));
        var models = new Dictionary<uint, DfModel>();
        for (int i = 0; i < bsa.Entries.Count; i++)
        {
            var model = Parse(bsa.Entries[i].Id, bsa.Record(i).ToArray());
            if (model is not null) models[model.Id] = model;
        }
        return models;
    }

    static DfModel? Parse(uint id, byte[] d)
    {
        string version = System.Text.Encoding.ASCII.GetString(d, 0, 4);
        if (version is not ("v2.5" or "v2.6" or "v2.7")) return null;
        int planeCount = BitConverter.ToInt32(d, 8);
        int pointList = BitConverter.ToInt32(d, 48), normalList = BitConverter.ToInt32(d, 52), planeList = BitConverter.ToInt32(d, 60);
        int pos = planeList;
        var polys = new List<DfPolygon>(planeCount);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int p = 0; p < planeCount; p++)
        {
            int count = d[pos];
            ushort texture = BitConverter.ToUInt16(d, pos + 2);
            pos += 8;
            int n = normalList + p * 12;
            // Daggerfall's Y axis points down; flip it so +Y is up.
            var normal = Vector3.Normalize(new Vector3(BitConverter.ToInt32(d, n), -BitConverter.ToInt32(d, n + 4), BitConverter.ToInt32(d, n + 8)));
            var pts = new Vector3[count];
            var raw = new Vector2[count];
            for (int k = 0; k < count; k++)
            {
                int offset = BitConverter.ToInt32(d, pos);
                raw[k] = new Vector2(BitConverter.ToInt16(d, pos + 4), BitConverter.ToInt16(d, pos + 6));
                pos += 8; // offset + u + v
                int pp = pointList + (version == "v2.5" ? offset * 3 : offset);
                pts[k] = new Vector3(BitConverter.ToInt32(d, pp), -BitConverter.ToInt32(d, pp + 4), BitConverter.ToInt32(d, pp + 8)) / PointDivisor;
                min = Vector3.Min(min, pts[k]); max = Vector3.Max(max, pts[k]);
            }
            polys.Add(new DfPolygon(pts, normal, texture >> 7, texture & 0x7f, Uvs(pts, raw)));
        }
        return new DfModel { Id = id, Polygons = polys, Min = min, Max = max };
    }

    /// <summary>
    /// A polygon's texture coordinates in texels (Daggerfall Unity Arch3dFile): stored in sixteenths of a texel, the
    /// first point's absolute and the next two each a step from the point before; the rest lie on the plane those three
    /// define, mapped the same way. Null when the first three points are in a line.
    /// </summary>
    static Vector2[]? Uvs(Vector3[] pts, Vector2[] raw)
    {
        if (pts.Length < 3) return null;
        var uv0 = raw[0] / 16f;
        var uv1 = uv0 + raw[1] / 16f;
        var uv2 = uv1 + raw[2] / 16f;
        Vector3 e1 = pts[1] - pts[0], e2 = pts[2] - pts[0];
        float a = Vector3.Dot(e1, e1), b = Vector3.Dot(e1, e2), c = Vector3.Dot(e2, e2), det = a * c - b * b;
        if (MathF.Abs(det) < 1e-6f * MathF.Max(a * c, 1e-6f)) return null;
        var uvs = new Vector2[pts.Length];
        uvs[0] = uv0; uvs[1] = uv1; uvs[2] = uv2;
        for (int k = 3; k < pts.Length; k++)
        {
            // pts[k] - pts[0] as s e1 + t e2 (least squares within the plane).
            var r = pts[k] - pts[0];
            float rd1 = Vector3.Dot(r, e1), rd2 = Vector3.Dot(r, e2);
            float s = (c * rd1 - b * rd2) / det, t = (a * rd2 - b * rd1) / det;
            uvs[k] = uv0 + s * (uv1 - uv0) + t * (uv2 - uv0);
        }
        return uvs;
    }
}
