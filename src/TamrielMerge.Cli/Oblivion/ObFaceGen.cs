using System.Numerics;
using System.Text;
using TamrielMerge.Util;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's FaceGen files beside a head part's model (user, 2026-11-14: "facegen as well"; 2026-11-15: "ureil does
/// not look like uriel from the intro"): what makes each person's own face out of the race's head.
/// <list type="bullet">
/// <item>.tri (FRTRI003): the part's vertices in the model's order, then the vertices its "stat" morphs move to
/// (blinks, looks, squints: whole targets, not offsets), its triangles, a UV per vertex, and its expression and
/// lip morphs as offsets (a scale and three int16 a vertex).</item>
/// <item>.egm (FREGM002): 50 symmetric and 30 asymmetric shape modes over those vertices (a scale and three int16 a
/// vertex each): a person's shape is the model plus each mode times the person's coefficient (the NPC's FGGS and
/// FGGA).</item>
/// <item>.egt (FREGT003): 50 texture modes (a scale and a plane of int8 for red, green and blue each), rows bottom
/// up against the texture: a person's skin is the part's texture plus each mode times the coefficient (FGTS).</item>
/// </list>
/// Measured and proven 2026-11-15 by rendering Uriel Septim's head from these alone (the old, pale Emperor of the
/// intro came out; with the texture's rows not turned over, purple smears across the cheeks).
/// </summary>
public sealed class ObFaceGenPart
{
    /// <summary>The model's own vertices (the .tri's first; 0 when the part has no .tri).</summary>
    public int V { get; private set; }
    /// <summary>The .tri's positions: the model's V, then the stat morphs' targets.</summary>
    public Vector3[] Base { get; private set; } = [];
    public int[] Tris { get; private set; } = [];
    public Vector2[] Uv { get; private set; } = [];
    /// <summary>The morphs by Oblivion's names, as offsets over the model's vertices (stat morphs: target minus base).</summary>
    public List<(string Name, Vector3[] Delta)> Morphs { get; } = [];
    /// <summary>The shape modes, each over <see cref="ModeVertices"/> vertices.</summary>
    public Vector3[][] Symmetric { get; private set; } = [];
    public Vector3[][] Asymmetric { get; private set; } = [];
    public int ModeVertices { get; private set; }
    public bool HasTri => V > 0;

    public static string Beside(string model, string extension)
    {
        string m = ObData.Normalize(model);
        if (!m.StartsWith(@"meshes\")) m = @"meshes\" + m;
        return Path.ChangeExtension(m, extension);
    }

    public static ObFaceGenPart Read(ObData data, string model)
    {
        var part = new ObFaceGenPart();
        if (data.Read(Beside(model, ".tri")) is { Length: > 64 } t && Encoding.ASCII.GetString(t, 0, 8) == "FRTRI003") part.ReadTri(t);
        if (data.Read(Beside(model, ".egm")) is { Length: > 64 } e && Encoding.ASCII.GetString(e, 0, 8) == "FREGM002") part.ReadEgm(e);
        return part;
    }

    void ReadTri(byte[] t)
    {
        int v = BitConverter.ToInt32(t, 8), tris = BitConverter.ToInt32(t, 12), quads = BitConverter.ToInt32(t, 16);
        int uvs = BitConverter.ToInt32(t, 28), diff = BitConverter.ToInt32(t, 36), stat = BitConverter.ToInt32(t, 40), extra = BitConverter.ToInt32(t, 44);
        if (v <= 0 || tris <= 0 || uvs != v) return;   // a UV per vertex, as every head part of Oblivion's has
        int p = 64;
        var pos = new Vector3[v + extra];
        for (int i = 0; i < pos.Length; i++, p += 12) pos[i] = new Vector3(BitConverter.ToSingle(t, p), BitConverter.ToSingle(t, p + 4), BitConverter.ToSingle(t, p + 8));
        var tri = new int[tris * 3];
        for (int i = 0; i < tri.Length; i++, p += 4) tri[i] = BitConverter.ToInt32(t, p);
        p += quads * 16;
        var uv = new Vector2[uvs];
        for (int i = 0; i < uvs; i++, p += 8) uv[i] = new Vector2(BitConverter.ToSingle(t, p), BitConverter.ToSingle(t, p + 4));
        p += tris * 12;   // the triangles' UV indices: the vertices' own
        for (int m = 0; m < diff; m++)
        {
            int n = BitConverter.ToInt32(t, p);
            string name = Encoding.ASCII.GetString(t, p + 4, n).TrimEnd('\0');
            p += 4 + n;
            float scale = BitConverter.ToSingle(t, p); p += 4;
            var d = new Vector3[v];
            for (int i = 0; i < v; i++, p += 6) d[i] = new Vector3(BitConverter.ToInt16(t, p), BitConverter.ToInt16(t, p + 2), BitConverter.ToInt16(t, p + 4)) * scale;
            Morphs.Add((name, d));
        }
        int next = v;
        for (int m = 0; m < stat; m++)
        {
            int n = BitConverter.ToInt32(t, p);
            string name = Encoding.ASCII.GetString(t, p + 4, n).TrimEnd('\0');
            p += 4 + n;
            int count = BitConverter.ToInt32(t, p); p += 4;
            var d = new Vector3[v];
            for (int i = 0; i < count; i++, p += 4)
            {
                int at = BitConverter.ToInt32(t, p);
                if (at >= 0 && at < v && next + i < pos.Length) d[at] = pos[next + i] - pos[at];
            }
            next += count;
            Morphs.Add((name, d));
        }
        V = v; Base = pos; Tris = tri; Uv = uv;
    }

    void ReadEgm(byte[] e)
    {
        int n = BitConverter.ToInt32(e, 8), sym = BitConverter.ToInt32(e, 12), asym = BitConverter.ToInt32(e, 16);
        if (n <= 0 || 64L + (sym + asym) * (4L + n * 6L) > e.Length) return;
        int p = 64;
        Vector3[] Mode()
        {
            float scale = BitConverter.ToSingle(e, p); p += 4;
            var d = new Vector3[n];
            for (int i = 0; i < n; i++, p += 6) d[i] = new Vector3(BitConverter.ToInt16(e, p), BitConverter.ToInt16(e, p + 2), BitConverter.ToInt16(e, p + 4)) * scale;
            return d;
        }
        var s = new Vector3[sym][]; for (int i = 0; i < sym; i++) s[i] = Mode();
        var a = new Vector3[asym][]; for (int i = 0; i < asym; i++) a[i] = Mode();
        Symmetric = s; Asymmetric = a; ModeVertices = n;
    }

    /// <summary>How far a person's face moves the part's first <paramref name="count"/> vertices from the model
    /// (in the model's own space): each mode times its coefficient.</summary>
    public Vector3[] Offsets(ReadOnlySpan<float> symmetric, ReadOnlySpan<float> asymmetric, int count)
    {
        var o = new Vector3[count];
        int n = Math.Min(count, ModeVertices);
        for (int m = 0; m < Symmetric.Length && m < symmetric.Length; m++)
        {
            float c = symmetric[m];
            if (c == 0) continue;
            var mode = Symmetric[m];
            for (int i = 0; i < n; i++) o[i] += mode[i] * c;
        }
        for (int m = 0; m < Asymmetric.Length && m < asymmetric.Length; m++)
        {
            float c = asymmetric[m];
            if (c == 0) continue;
            var mode = Asymmetric[m];
            for (int i = 0; i < n; i++) o[i] += mode[i] * c;
        }
        return o;
    }

    /// <summary>A person's coefficients from the record's bytes (floats).</summary>
    public static float[] Coefficients(Noggog.ReadOnlyMemorySlice<byte>? bytes)
    {
        if (bytes is not { } b) return [];
        var span = b.Span;
        var c = new float[span.Length / 4];
        for (int i = 0; i < c.Length; i++) c[i] = BitConverter.ToSingle(span.Slice(4 * i, 4));
        return c;
    }
}

/// <summary>The texture modes of a head part (.egt) and the skin they make of the part's texture.</summary>
public sealed class ObFaceTexture
{
    public int Rows { get; private set; }
    public int Columns { get; private set; }
    /// <summary>Per mode: red, green and blue planes (rows as the file has them: bottom up), already scaled.</summary>
    float[][] modes = [];

    public static ObFaceTexture? Read(ObData data, string model)
    {
        if (data.Read(ObFaceGenPart.Beside(model, ".egt")) is not { Length: > 64 } e || Encoding.ASCII.GetString(e, 0, 8) != "FREGT003") return null;
        int rows = BitConverter.ToInt32(e, 8), cols = BitConverter.ToInt32(e, 12), sym = BitConverter.ToInt32(e, 16);
        int plane = rows * cols;
        if (rows <= 0 || cols <= 0 || 64L + sym * (4L + 3L * plane) > e.Length) return null;
        var t = new ObFaceTexture { Rows = rows, Columns = cols, modes = new float[sym][], modeMeans = new Vector3[sym] };
        int p = 64;
        for (int m = 0; m < sym; m++)
        {
            float scale = BitConverter.ToSingle(e, p); p += 4;
            var f = new float[3 * plane];
            for (int i = 0; i < f.Length; i++) f[i] = (sbyte)e[p + i] * scale;
            p += 3 * plane;
            t.modes[m] = f;
            // The mode's mean over the texture, per channel: a face's mean colour is then a sum of 50 products.
            double r = 0, g = 0, bl = 0;
            for (int i = 0; i < plane; i++) { r += f[i]; g += f[plane + i]; bl += f[2 * plane + i]; }
            t.modeMeans[m] = new Vector3((float)(r / plane), (float)(g / plane), (float)(bl / plane));
        }
        return t;
    }

    Vector3[] modeMeans = [];

    /// <summary>How far a person's face moves the skin's mean colour from the part's texture's (red, green, blue).</summary>
    public Vector3 MeanShift(ReadOnlySpan<float> coefficients)
    {
        var sum = Vector3.Zero;
        for (int m = 0; m < modeMeans.Length && m < coefficients.Length; m++) sum += modeMeans[m] * coefficients[m];
        return sum;
    }

    /// <summary>
    /// A person's skin: the part's texture (any size: sampled to the modes' size) plus each mode times the person's
    /// coefficient, as red, green, blue per pixel, top row first, not clamped (Uriel's runs to 280 in red).
    /// </summary>
    public float[] Skin(uint[] basePixels, int width, int height, ReadOnlySpan<float> coefficients)
    {
        int plane = Rows * Columns;
        var sum = new float[3 * plane];
        for (int m = 0; m < modes.Length && m < coefficients.Length; m++)
        {
            float c = coefficients[m];
            if (c == 0) continue;
            var mode = modes[m];
            for (int i = 0; i < sum.Length; i++) sum[i] += mode[i] * c;
        }
        var skin = new float[3 * plane];
        for (int y = 0; y < Rows; y++)
        {
            int from = (Rows - 1 - y) * Columns;   // the modes' rows run bottom up
            for (int x = 0; x < Columns; x++)
            {
                var (r, g, b) = Sample(basePixels, width, height, (x + 0.5f) / Columns, (y + 0.5f) / Rows);
                int o = 3 * (y * Columns + x);
                skin[o] = r + sum[from + x];
                skin[o + 1] = g + sum[plane + from + x];
                skin[o + 2] = b + sum[2 * plane + from + x];
            }
        }
        return skin;
    }

    /// <summary>A bilinear sample of an ARGB image at (u, v) in 0-1, as red, green, blue.</summary>
    public static (float R, float G, float B) Sample(uint[] px, int w, int h, float u, float v)
    {
        float fx = Math.Clamp(u * w - 0.5f, 0, w - 1), fy = Math.Clamp(v * h - 0.5f, 0, h - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - x0, ty = fy - y0;
        static Vector3 C(uint c) => new((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
        var top = Vector3.Lerp(C(px[y0 * w + x0]), C(px[y0 * w + x1]), tx);
        var bottom = Vector3.Lerp(C(px[y1 * w + x0]), C(px[y1 * w + x1]), tx);
        var c = Vector3.Lerp(top, bottom, ty);
        return (c.X, c.Y, c.Z);
    }
}

/// <summary>A BC1 (DXT1) texture file with its mip chain: a person's own face is one of these each, and uncompressed
/// they would be 0.7 MB apiece over some 2,600 people.</summary>
public static class Bc1Dds
{
    /// <summary>Writes opaque ARGB pixels (width and height multiples of 4) as BC1 with box-filtered mips.</summary>
    public static void Write(string path, uint[] px, int w, int h)
    {
        // Every level down to 1 x 1, as the converter's other textures have them: the first faces' files stopped at
        // 8 x 4 (and 4 x 4), and the game drew every one of those faces plain blue (run r74, 2026-11-15).
        var levels = new List<(uint[] Px, int W, int H)> { (px, w, h) };
        while (levels[^1].W > 1 || levels[^1].H > 1)
        {
            var (p, pw, ph) = levels[^1];
            int nw = Math.Max(1, pw / 2), nh = Math.Max(1, ph / 2);
            var n = new uint[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(2 * x, pw - 1), x1 = Math.Min(2 * x + 1, pw - 1), y0 = Math.Min(2 * y, ph - 1), y1 = Math.Min(2 * y + 1, ph - 1);
                    uint a = p[y0 * pw + x0], b = p[y0 * pw + x1], c = p[y1 * pw + x0], d = p[y1 * pw + x1];
                    uint r = (((a >> 16) & 0xFF) + ((b >> 16) & 0xFF) + ((c >> 16) & 0xFF) + ((d >> 16) & 0xFF) + 2) / 4;
                    uint g = (((a >> 8) & 0xFF) + ((b >> 8) & 0xFF) + ((c >> 8) & 0xFF) + ((d >> 8) & 0xFF) + 2) / 4;
                    uint bl = ((a & 0xFF) + (b & 0xFF) + (c & 0xFF) + (d & 0xFF) + 2) / 4;
                    n[y * nw + x] = 0xFF000000u | r << 16 | g << 8 | bl;
                }
            levels.Add((n, nw, nh));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(0x20534444u);                 // "DDS "
        bw.Write(124u);
        bw.Write(0x000A1007u);                 // caps, height, width, pixel format, mip count, linear size
        bw.Write((uint)h); bw.Write((uint)w);
        bw.Write((uint)(Math.Max(1, w / 4) * Math.Max(1, h / 4) * 8));
        bw.Write(0u);
        bw.Write((uint)levels.Count);
        for (int i = 0; i < 11; i++) bw.Write(0u);
        bw.Write(32u); bw.Write(4u);           // pixel format: FourCC
        bw.Write(0x31545844u);                 // "DXT1"
        for (int i = 0; i < 5; i++) bw.Write(0u);
        bw.Write(0x00401008u);                 // complex, texture, mipmap
        for (int i = 0; i < 4; i++) bw.Write(0u);
        foreach (var (p, lw, lh) in levels)
            for (int by = 0; by < lh; by += 4)
                for (int bx = 0; bx < lw; bx += 4)
                    WriteBlock(bw, p, lw, lh, bx, by);
    }

    /// <summary>One 4x4 block: the two end colours along the block's widest spread of colour (its principal axis,
    /// by its colours' covariance), each pixel the nearest of the four on the line between them.</summary>
    static void WriteBlock(BinaryWriter bw, uint[] px, int w, int h, int bx, int by)
    {
        Span<Vector3> c = stackalloc Vector3[16];
        var mean = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            uint p = px[Math.Min(by + i / 4, h - 1) * w + Math.Min(bx + i % 4, w - 1)];
            c[i] = new Vector3((p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF);
            mean += c[i];
        }
        mean /= 16;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var v in c)
        {
            var d = v - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        var axis = new Vector3(1, 1, 1);
        for (int k = 0; k < 6; k++)
        {
            var n = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float len = n.Length();
            if (len < 1e-6f) break;
            axis = n / len;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in c) { float t = Vector3.Dot(v - mean, axis); lo = Math.Min(lo, t); hi = Math.Max(hi, t); }
        ushort c0 = To565(mean + axis * hi), c1 = To565(mean + axis * lo);
        if (c0 < c1) (c0, c1) = (c1, c0);
        uint bits = 0;
        if (c0 != c1)
        {
            Vector3 e0 = From565(c0), e1 = From565(c1);
            Span<Vector3> palette = [e0, e1, (2 * e0 + e1) / 3, (e0 + 2 * e1) / 3];
            for (int i = 0; i < 16; i++)
            {
                int best = 0; float bestD = float.MaxValue;
                for (int k = 0; k < 4; k++) { float d = Vector3.DistanceSquared(c[i], palette[k]); if (d < bestD) { bestD = d; best = k; } }
                bits |= (uint)best << (2 * i);
            }
        }
        bw.Write(c0); bw.Write(c1); bw.Write(bits);
    }

    static ushort To565(Vector3 c)
    {
        int r = (int)MathF.Round(Math.Clamp(c.X, 0, 255) * 31 / 255), g = (int)MathF.Round(Math.Clamp(c.Y, 0, 255) * 63 / 255), b = (int)MathF.Round(Math.Clamp(c.Z, 0, 255) * 31 / 255);
        return (ushort)(r << 11 | g << 5 | b);
    }

    static Vector3 From565(ushort c) => new(((c >> 11) & 31) * 255f / 31, ((c >> 5) & 63) * 255f / 63, (c & 31) * 255f / 31);
}

/// <summary>A FaceGen morph file (FRTRI003) as Skyrim's head parts have beside their models: the part's vertices,
/// triangles and UVs, and its expression and lip morphs as offsets (a scale and three int16 a vertex each). Skyrim's
/// own heads' are this format (measured 2026-11-15: malehead.tri, 898 vertices, 44 morphs, no stat morphs).</summary>
public static class FaceTri
{
    public static void Write(string path, Vector3[] verts, int[] tris, Vector2[] uv, IReadOnlyList<(string Name, Vector3[] Delta)> morphs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(Encoding.ASCII.GetBytes("FRTRI003"));
        bw.Write(verts.Length); bw.Write(tris.Length / 3); bw.Write(0); bw.Write(0); bw.Write(0);
        bw.Write(verts.Length);   // a UV per vertex
        bw.Write(1);              // has UVs
        bw.Write(morphs.Count); bw.Write(0); bw.Write(0);
        bw.Write(new byte[16]);
        foreach (var v in verts) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); }
        foreach (int t in tris) bw.Write(t);
        for (int i = 0; i < verts.Length; i++) { var t = i < uv.Length ? uv[i] : Vector2.Zero; bw.Write(t.X); bw.Write(t.Y); }
        foreach (int t in tris) bw.Write(t);
        foreach (var (name, delta) in morphs)
        {
            var label = Encoding.ASCII.GetBytes(name + "\0");
            bw.Write(label.Length); bw.Write(label);
            float most = 0;
            foreach (var d in delta) most = MathF.Max(most, MathF.Max(MathF.Abs(d.X), MathF.Max(MathF.Abs(d.Y), MathF.Abs(d.Z))));
            float scale = most > 0 ? most / 32767f : 1f / 32767f;
            bw.Write(scale);
            for (int i = 0; i < verts.Length; i++)
            {
                var d = i < delta.Length ? delta[i] : Vector3.Zero;
                bw.Write((short)MathF.Round(d.X / scale)); bw.Write((short)MathF.Round(d.Y / scale)); bw.Write((short)MathF.Round(d.Z / scale));
            }
        }
    }

    /// <summary>
    /// Oblivion's morph names as Skyrim's heads have them. The lip shapes and brows carry the same names in both
    /// games (FaceGen's); Oblivion's five emotions stand for Skyrim's dialogue, mood and combat ones; its blinks,
    /// looks and squints (stat morphs there) are plain morphs here.
    /// </summary>
    public static IEnumerable<string> SkyrimNames(string oblivion) => oblivion switch
    {
        "Anger" => ["DialogueAnger", "MoodAnger", "CombatAnger"],
        "Fear" => ["DialogueFear", "MoodFear"],
        "Happy" => ["DialogueHappy", "MoodHappy"],
        "Sad" => ["DialogueSad", "MoodSad"],
        "Surprise" => ["DialogueSurprise", "MoodSurprise"],
        "BigAah" => ["BigAah", "CombatShout"],
        _ when oblivion.StartsWith("Disp", StringComparison.OrdinalIgnoreCase) => [],
        _ => [oblivion],
    };
}
