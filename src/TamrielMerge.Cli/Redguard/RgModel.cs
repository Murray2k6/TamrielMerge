using System.Numerics;

namespace TamrielMerge.Redguard;

/// <summary>
/// A Redguard model (.3D, .3DC, or a segment embedded in a .ROB), its first frame. Formats from the
/// redguard-preservation notes (docs/formats/models) and RGUnity's RG3DFile.cs (MIT):
/// <list type="bullet">
/// <item>64-byte header: version ("v2.6", "v2.7", "v4.0", "v5.0"), vertex and face counts at 4 and 8, frame count
/// and frame table offset at 0x10/0x14, vertex coordinates at 0x30, face data at 0x3C (0x40 from v4.0).</item>
/// <item>Faces: vertex count (u8), a byte, texture (u16 before v4.0, u32 after), 4 unused bytes, then per vertex a
/// u32 vertex reference (a byte offset, /12, before v4.0) and U/V as i16 in 1/16 texels (see FaceUv).</item>
/// <item>Texture before v4.0: id = value &gt;&gt; 7 (the TEXTURE.### bank; below 2 the face is a flat palette colour,
/// value &amp; 0xFF), image = value &amp; 0x7F. From v4.0: a BCD-like TEXBSI number (see Faces).</item>
/// <item>Vertices: 3 x i32 / 256. Animated v2.x files keep frame 0's vertices right after the face data (plus a
/// gap from the frame table) rather than at the header's offset.</item>
/// </list>
/// </summary>
public sealed class RgModel
{
    public sealed record Face(int[] Vertices, Vector2[] Uv, bool Solid, int ColorIndex, int TextureBank, int ImageId)
    {
        /// <summary>The stored U/V values (in texels), before accumulation.</summary>
        public Vector2[] Raw { get; init; } = [];
    }

    public string Version { get; private init; } = "";
    public List<Vector3> Vertices { get; } = [];
    public List<Face> Faces { get; } = [];
    /// <summary>The frames after the first of an animated v2.x model, each every vertex's place (measured 2026-11-10: 110
    /// of the game's 3DC files are stored this way, SEAGA001 with 18 frames of 120 vertices). The header's frame offset
    /// leads to 24 bytes: the frame directory's offset, the face data's size, the gap, flags (u16, bit 0: compressed),
    /// the reference frame (u16), and a shift in its last byte; the directory holds 12 bytes per frame, the first its
    /// vertices' offset. The reference frame is whole (3 x i32); the others are 3 x i16 per vertex added to it, shifted
    /// (Redguard Preservation, formats/models/3dc.html; the seagull's largest delta is 25,175 = 98 units at shift 0).</summary>
    public List<Vector3[]> Frames { get; } = [];

    public static RgModel? Load(byte[] b, int at = 0)
    {
        if (b.Length < at + 64) return null;
        string version = System.Text.Encoding.ASCII.GetString(b, at, 4);
        bool v2 = version is "v2.6" or "v2.7";
        if (!v2 && !version.StartsWith("v4") && !version.StartsWith("v5")) return null;
        int U32(int o) => BitConverter.ToInt32(b, at + o);
        int numVertices = U32(4), numFaces = U32(8), numFrames = U32(0x10), frameTable = U32(0x14);
        int vertexOffset = U32(0x30), faceOffset = U32(0x3C);
        if (numVertices <= 0 || numFaces <= 0 || numVertices > 100000 || numFaces > 100000) return null;
        var model = new RgModel { Version = version };
        int p = at + faceOffset;
        for (int f = 0; f < numFaces; f++)
        {
            if (p + 8 > b.Length) return null;
            int count = b[p];
            uint tex = v2 ? BitConverter.ToUInt16(b, p + 2) : BitConverter.ToUInt32(b, p + 2);
            p += v2 ? 8 : 10;
            var verts = new int[count];
            var uv = new Vector2[count];
            var raw = new Vector2[count];
            short u = 0, v = 0;
            for (int k = 0; k < count; k++)
            {
                if (p + 8 > b.Length) return null;
                uint index = BitConverter.ToUInt32(b, p);
                raw[k] = new Vector2(BitConverter.ToInt16(b, p + 4) / 16f, BitConverter.ToInt16(b, p + 6) / 16f);
                u += BitConverter.ToInt16(b, p + 4);
                v += BitConverter.ToInt16(b, p + 6);
                p += 8;
                verts[k] = (int)(v2 ? index / 12 : index);
                uv[k] = new Vector2(u / 16f, v / 16f);   // texels
            }
            bool solid; int color = 0, bank = 0, image = 0;
            if (v2)
            {
                bank = (int)(tex >> 7);
                solid = bank < 2;
                if (solid) color = (int)(tex & 0xFF); else image = (int)(tex & 0x7F);
            }
            else if (tex >> 20 == 0x0FFF) { solid = true; color = (int)((tex >> 8) & 0xFF); }
            else
            {
                solid = false;
                uint tmp = (tex >> 8) - 4000000;
                uint ones = tmp / 250 % 40, tens = (tmp - ones * 250) / 1000 % 100, hundreds = (tmp - ones * 250 - tens * 1000) / 4000;
                bank = (int)(ones + tens + hundreds);
                image = (int)((tex & 0xFF) % 10 + (tex & 0xFF) / 40 * 10);
            }
            model.Faces.Add(new Face(verts, uv, solid, color, bank, image) { Raw = raw });
        }
        int faceEnd = p - at;
        // Animated v2.x models: frame 0's vertices follow the face data (after the frame table's gap).
        if (v2 && numFrames > 1 && frameTable > 0 && at + frameTable + 16 <= b.Length)
        {
            int gap = BitConverter.ToInt32(b, at + frameTable + 8);
            vertexOffset = faceEnd + Math.Max(0, gap);
            if (!Plausible(b, at + vertexOffset, numVertices)) vertexOffset = faceEnd;
        }
        if (at + vertexOffset + numVertices * 12 > b.Length) return null;
        for (int i = 0; i < numVertices; i++)
        {
            int o = at + vertexOffset + i * 12;
            model.Vertices.Add(new Vector3(BitConverter.ToInt32(b, o), BitConverter.ToInt32(b, o + 4), BitConverter.ToInt32(b, o + 8)) / 256f);
        }
        if (model.Faces.Any(f => f.Vertices.Any(i => i < 0 || i >= numVertices))) return null;
        if (v2 && numFrames > 1 && frameTable > 0 && at + frameTable + 24 <= b.Length)
        {
            int table = U32(frameTable), flags = BitConverter.ToUInt16(b, at + frameTable + 12), reference = BitConverter.ToUInt16(b, at + frameTable + 14);
            int shift = b[at + frameTable + 0x17] & 31;
            // Only the layout measured: compressed, the first frame the whole one, and the directory agreeing with where
            // the first frame's vertices were found.
            if ((flags & 1) != 0 && reference == 0 && table > 0 && at + table + numFrames * 12 <= b.Length && U32(table) == vertexOffset)
                for (int f = 1; f < numFrames; f++)
                {
                    int o = at + U32(table + f * 12);
                    if (o < at || o + numVertices * 6 > b.Length) { model.Frames.Clear(); break; }
                    var frame = new Vector3[numVertices];
                    for (int i = 0; i < numVertices; i++)
                    {
                        int q = at + vertexOffset + i * 12, d = o + i * 6;
                        frame[i] = new Vector3(BitConverter.ToInt32(b, q) + (BitConverter.ToInt16(b, d) << shift),
                            BitConverter.ToInt32(b, q + 4) + (BitConverter.ToInt16(b, d + 2) << shift),
                            BitConverter.ToInt32(b, q + 8) + (BitConverter.ToInt16(b, d + 4) << shift)) / 256f;
                    }
                    model.Frames.Add(frame);
                }
        }
        foreach (var f in model.Faces) FaceUv(model, f);
        return model;
    }

    /// <summary>
    /// A face's texture coordinates. Only the first three stored values follow the running-delta rule (the first
    /// absolute, then two deltas); a quad's fourth is stored absolute and larger faces' later values don't fit any
    /// rule — the original renderer takes the texture's plane from the first three vertices. So every vertex past the
    /// third gets the UV of that affine mapping (in the face's plane); when the first three are collinear, a quad's
    /// fourth value is used as stored.
    /// </summary>
    static void FaceUv(RgModel m, Face f)
    {
        int n = f.Vertices.Length;
        if (n < 3) return;
        var uv = f.Uv;
        uv[0] = f.Raw[0];
        uv[1] = uv[0] + f.Raw[1];
        uv[2] = uv[1] + f.Raw[2];
        if (n == 3) return;
        var p0 = m.Vertices[f.Vertices[0]];
        var e1 = m.Vertices[f.Vertices[1]] - p0; var e2 = m.Vertices[f.Vertices[2]] - p0;
        float d11 = Vector3.Dot(e1, e1), d12 = Vector3.Dot(e1, e2), d22 = Vector3.Dot(e2, e2);
        float det = d11 * d22 - d12 * d12;
        // The first three in a line (a wall's corner run before its notch) can't give the texture's plane. The fourth
        // value is stored absolute in larger faces as in quads (rg-uvtest 2026-11-11: in the five-corner faces it is
        // the first three's mapping at the fourth corner to a tenth of a texel, "(0,16)" and "(27.3,4.7)" against 0,16
        // and 27.2,4.8), so the plane is taken from the first corner, the farther of the next two and the fourth
        // (the Draggin Tale's wall: 7 corners over 345,426 square units, drawn as a smear of a few texels).
        if (MathF.Abs(det) < 1e-3f * MathF.Max(1, d11 * d22))
        {
            int mid = d11 >= d22 ? 1 : 2;
            var f1 = m.Vertices[f.Vertices[mid]] - p0; var f2 = m.Vertices[f.Vertices[3]] - p0;
            float g11 = Vector3.Dot(f1, f1), g12 = Vector3.Dot(f1, f2), g22 = Vector3.Dot(f2, f2);
            float gdet = g11 * g22 - g12 * g12;
            if (MathF.Abs(gdet) >= 1e-3f * MathF.Max(1, g11 * g22))
            {
                var uvMid = uv[mid]; var uv3 = f.Raw[3];
                for (int i = 1; i < n; i++)
                {
                    if (i == mid) continue;
                    if (i == 3) { uv[3] = uv3; continue; }
                    var q = m.Vertices[f.Vertices[i]] - p0;
                    float q1 = Vector3.Dot(q, f1), q2 = Vector3.Dot(q, f2);
                    float a = (q1 * g22 - q2 * g12) / gdet, c = (q2 * g11 - q1 * g12) / gdet;
                    uv[i] = uv[0] + a * (uvMid - uv[0]) + c * (uv3 - uv[0]);
                }
                FacesByFourth++;
                return;
            }
        }
        for (int i = 3; i < n; i++)
        {
            if (MathF.Abs(det) < 1e-4f * MathF.Max(1, d11 * d22)) { uv[i] = i == 3 ? f.Raw[3] : uv[i - 1] + f.Raw[i]; continue; }
            var q = m.Vertices[f.Vertices[i]] - p0;
            float q1 = Vector3.Dot(q, e1), q2 = Vector3.Dot(q, e2);
            float a = (q1 * d22 - q2 * d12) / det, c = (q2 * d11 - q1 * d12) / det;
            uv[i] = uv[0] + a * (uv[1] - uv[0]) + c * (uv[2] - uv[0]);
        }
    }

    /// <summary>Faces whose first three corners are in a line, mapped from the first, one of the next two and the fourth.</summary>
    public static int FacesByFourth;

    /// <summary>Faces whose second or third corner's texture place was brought back by whole textures (<see cref="Unwrap"/>).</summary>
    public static int FacesUnwrapped;

    /// <summary>The texture's stretch over a face's first three corners: texels a unit along its most stretched
    /// direction over those along its least (1: even; 0 when the corners enclose nothing).</summary>
    static float Stretch(RgModel m, Face f, Vector2 uv0, Vector2 uv1, Vector2 uv2)
    {
        var p0 = m.Vertices[f.Vertices[0]];
        var e1 = m.Vertices[f.Vertices[1]] - p0; var e2 = m.Vertices[f.Vertices[2]] - p0;
        var normal = Vector3.Cross(e1, e2);
        if (normal.Length() < 1e-3f) return 0;
        var ax = Vector3.Normalize(e1); var ay = Vector3.Normalize(Vector3.Cross(normal, e1));
        float x1 = e1.Length(), x2 = Vector3.Dot(e2, ax), y2 = Vector3.Dot(e2, ay);
        if (MathF.Abs(y2) < 1e-4f) return 0;
        var d1 = uv1 - uv0; var d2 = uv2 - uv0;
        float a11 = d1.X / x1, a21 = d1.Y / x1, a12 = (d2.X - a11 * x2) / y2, a22 = (d2.Y - a21 * x2) / y2;
        float t = a11 * a11 + a12 * a12 + a21 * a21 + a22 * a22, det = MathF.Abs(a11 * a22 - a12 * a21);
        float s1 = MathF.Sqrt((t + MathF.Sqrt(MathF.Max(0, t * t - 4 * det * det))) / 2);
        return det < 1e-9f ? float.MaxValue : s1 * s1 / det;
    }

    /// <summary>
    /// A face whose texture is stretched more than 4 to 1 over its first three corners, when moving its second and
    /// third corners' texture places by whole textures (<paramref name="width"/>, <paramref name="height"/> texels)
    /// makes it even (2 to 1 or less): those corners are stored a whole number of textures away, the same texel but a
    /// run of repeats across the face when the places between are worked out (the Draggin Tale's wall triangles,
    /// 2026-11-11: "(128,0) (0,0) (0,767)" on a 128-texel texture where (0,127) is even, "(128,23) (0,-1280) (2,-34)"
    /// where (0,0) is; drawn as a wall of close stripes). The rest of the face follows from the three as before. A face
    /// that stays uneven is left as stored: a wall its texture really runs along several times.
    /// </summary>
    public static bool Unwrap(RgModel m, Face f, int width, int height)
    {
        if (f.Solid || f.Vertices.Length < 3 || f.Raw.Length < 3 || width <= 0 || height <= 0) return false;
        var uv0 = f.Raw[0]; var uv1 = uv0 + f.Raw[1]; var uv2 = uv1 + f.Raw[2];
        float now = Stretch(m, f, uv0, uv1, uv2);
        if (now <= 4) return false;
        // Each corner's nearest whole-texture moves towards the first corner's place, and none.
        static IEnumerable<float> Moves(float d, int size)
        {
            yield return 0;
            int k = (int)MathF.Round(d / size);
            if (k == 0) yield break;
            for (int j = k - 1; j <= k + 1; j++) if (j != 0) yield return -j * size;
        }
        float best = now; Vector2 best1 = uv1, best2 = uv2; float bestMoved = 0;
        foreach (float u1 in Moves(uv1.X - uv0.X, width))
            foreach (float v1 in Moves(uv1.Y - uv0.Y, height))
                foreach (float u2 in Moves(uv2.X - uv0.X, width))
                    foreach (float v2 in Moves(uv2.Y - uv0.Y, height))
                    {
                        if (u1 == 0 && v1 == 0 && u2 == 0 && v2 == 0) continue;
                        var c1 = uv1 + new Vector2(u1, v1); var c2 = uv2 + new Vector2(u2, v2);
                        float s = Stretch(m, f, uv0, c1, c2);
                        if (s <= 0) continue;
                        float moved = MathF.Abs(u1) + MathF.Abs(v1) + MathF.Abs(u2) + MathF.Abs(v2);
                        if (s < best - 1e-3f || (MathF.Abs(s - best) <= 1e-3f && moved < bestMoved)) { best = s; best1 = c1; best2 = c2; bestMoved = moved; }
                    }
        if (best > 2) return false;
        f.Raw[1] = best1 - uv0;
        f.Raw[2] = best2 - best1;
        FaceUv(m, f);
        FacesUnwrapped++;
        return true;
    }

    static bool Plausible(byte[] b, int o, int n)
    {
        if (o < 0 || o + n * 12 > b.Length) return false;
        for (int i = 0; i < Math.Min(n * 3, 30); i++)
            if (Math.Abs(BitConverter.ToInt32(b, o + i * 4)) > 1_090_519_040) return false;
        return true;
    }
}

/// <summary>
/// A Redguard object archive (.ROB): "OARC", a big-endian 4, the segment count (LE), "OARD", the payload size (BE),
/// then per segment an 80-byte header — total size (LE u32), name (8 bytes), type (u16: 0 or 256 embedded model,
/// 512 a reference to name.3DC), flags, bounding box, data size (u32 at 0x4C) — and its data.
/// </summary>
public sealed class RgRob
{
    public sealed record Segment(string Name, int Type, int DataOffset, int DataSize);

    public List<Segment> Segments { get; } = [];
    public byte[] Data { get; private init; } = [];

    public static RgRob? Load(byte[] b)
    {
        if (b.Length < 24 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "OARC") return null;
        int count = BitConverter.ToInt32(b, 8);
        var rob = new RgRob { Data = b };
        int p = 20;
        for (int i = 0; i < count && p + 80 <= b.Length; i++)
        {
            int total = BitConverter.ToInt32(b, p);
            string name = System.Text.Encoding.ASCII.GetString(b, p + 4, 8).TrimEnd('\0', ' ');
            int type = BitConverter.ToUInt16(b, p + 12);
            int size = BitConverter.ToInt32(b, p + 0x4C);
            rob.Segments.Add(new Segment(name, type, p + 80, size));
            p += total > 0 ? total : 80 + size;
        }
        return rob;
    }
}

/// <summary>
/// A Redguard world (.WLD): a 1184-byte header, then four sections (2 x 2 world quarters) of a 22-byte header and four
/// 128 x 128 byte maps — heights (low 7 bits index a 128-entry height table), unused, terrain texture (low 6 bits:
/// tile in TEXTURE.302, high 2 bits: quarter turns), unused. Grid cells are 256 units; height = -table[index].
/// </summary>
public sealed class RgWorld
{
    public const int Size = 256, Cell = 256;
    public readonly byte[,] Height = new byte[Size, Size], Texture = new byte[Size, Size];

    static readonly int[] HeightTable =
    [
        0, 40, 40, 40, 80, 80, 80, 120, 120, 120, 160, 160, 160, 200, 200, 200, 240, 240, 240, 280,
        280, 320, 320, 320, 360, 360, 400, 400, 400, 440, 440, 480, 480, 480, 520, 520, 560, 560, 600, 600,
        600, 640, 640, 680, 680, 720, 720, 760, 760, 800, 800, 840, 840, 880, 880, 920, 920, 960, 1000, 1000,
        1040, 1040, 1080, 1120, 1120, 1160, 1160, 1200, 1240, 1240, 1280, 1320, 1320, 1360, 1400, 1440, 1440, 1480, 1520, 1560,
        1600, 1600, 1640, 1680, 1720, 1760, 1800, 1840, 1880, 1920, 1960, 2000, 2040, 2080, 2120, 2200, 2240, 2280, 2320, 2400,
        2440, 2520, 2560, 2640, 2680, 2760, 2840, 2920, 3000, 3080, 3160, 3240, 3360, 3440, 3560, 3680, 3800, 3960, 4080, 4280,
        4440, 4680, 4920, 5200, 5560, 6040, 6680, 7760,
    ];

    /// <summary>Height (up, Redguard units) at a grid vertex.</summary>
    public float HeightAt(int x, int z) => HeightTable[Height[Math.Clamp(x, 0, Size - 1), Math.Clamp(z, 0, Size - 1)] & 0x7F];

    public static RgWorld? Load(byte[] b)
    {
        if (b.Length < 1184 + 4 * 65558) return null;
        var w = new RgWorld();
        for (int s = 0; s < 4; s++)
        {
            int section = 1184 + s * 65558 + 22;
            int ox = (s % 2) * 128, oz = (s / 2) * 128;
            for (int z = 0; z < 128; z++)
                for (int x = 0; x < 128; x++)
                {
                    w.Height[ox + x, oz + z] = b[section + z * 128 + x];
                    w.Texture[ox + x, oz + z] = b[section + 2 * 16384 + z * 128 + x];
                }
        }
        return w;
    }
}

/// <summary>
/// A Redguard scene (.RGM): sections of a 4-character tag and a big-endian length, until "END ". Here: static objects
/// (MPSO: 66-byte records — id, 12-byte model name, i24 x/y/z each padded to 4 bytes, a 3 x 3 Q4.28 rotation stored
/// column-major) and objects (MPOB: 66-byte records — id, type, active, script name (9), model name (9), static flag,
/// i24 x/y/z padded, Euler angles in 2048ths of a turn).
/// </summary>
public sealed class RgScene
{
    public sealed record Placed(string Model, string Script, Vector3 Position, Matrix4x4 Rotation, bool IsStatic);

    public List<Placed> Statics { get; } = [];
    public List<Placed> Objects { get; } = [];
    /// <summary>Arrival points (MPMK: the count, then x/y/z (i32) of each marker, then a type byte for each), which
    /// LoadWorld names by index.</summary>
    public List<Vector3> Markers { get; } = [];
    /// <summary>Raw MPOB fields the builders use: object type (1 actor/prop, 2 sound, 3 effect, 4 torch or lamp, 6 local light: ObjectLights), by object.</summary>
    public List<int> ObjectTypes { get; } = [];
    /// <summary>Lights (MPSL: 42-byte records; position as 3 x i32 at 8, range (i32) at 0x14, colour as 3 x i16 at 0x18 —
    /// signed strengths, a negative channel subtracting light (Redguard Preservation, formats/RGM.html; 760 of the
    /// 1,441 active lights are all negative, darkening) — and the light channel map at 0x1E, all zero for unused
    /// records, which are skipped).</summary>
    public List<(int R, int G, int B, Vector3 Position, int Range)> Lights { get; } = [];
    /// <summary>Each object's light fields (MPOB at 0x34 intensity, 0x36 radius, 0x3C/0x3E/0x40 signed red, green,
    /// blue as i16): a type-6 object is a local light of that radius and colour (formats/RGM.html).</summary>
    public List<(int Intensity, int Radius, int R, int G, int B)> ObjectLights { get; } = [];
    /// <summary>Flat textured quads (MPSF: 24-byte records): position and texture (bank, image).</summary>
    public List<(Vector3 Position, int Bank, int Image)> Flats { get; } = [];
    /// <summary>A rope or chain (MPRP: 80-byte records — its top as 3 x i32 in 256ths at 4, the number of links (i16) at
    /// 32, what hangs on its end (9 chars, a model, often none) at 34 and the link's model at 43, ROPELINK or
    /// CHAINLNK). The player swings and climbs on ropes; chains carry chandeliers and cages.</summary>
    public sealed record Rope(Vector3 Top, int Links, string Hang, string Link);
    public List<Rope> Ropes { get; } = [];
    public Dictionary<string, (int Offset, int Length)> Sections { get; } = [];

    static int I24(byte[] b, int o) { int v = b[o] | b[o + 1] << 8 | b[o + 2] << 16; return (v & 0x800000) != 0 ? v - 0x1000000 : v; }

    public static RgScene? Load(byte[] b)
    {
        var scene = new RgScene();
        int p = 0;
        while (p + 8 <= b.Length)
        {
            string tag = System.Text.Encoding.ASCII.GetString(b, p, 4);
            int len = b[p + 4] << 24 | b[p + 5] << 16 | b[p + 6] << 8 | b[p + 7];
            if (tag == "END ") break;
            if (len < 0 || p + 8 + len > b.Length) break;
            scene.Sections.TryAdd(tag, (p + 8, len));
            p += 8 + len;
        }
        if (scene.Sections.TryGetValue("MPSO", out var mpso))
        {
            int n = BitConverter.ToInt32(b, mpso.Offset);
            for (int i = 0; i < n; i++)
            {
                int o = mpso.Offset + 4 + i * 66;
                if (o + 66 > mpso.Offset + mpso.Length) break;
                string model = System.Text.Encoding.ASCII.GetString(b, o + 4, 12).Split('\0')[0].Trim();
                var pos = new Vector3(I24(b, o + 0x10), I24(b, o + 0x14), I24(b, o + 0x18) & 0xFFFFFF);
                float M(int k) => BitConverter.ToInt32(b, o + 0x1C + k * 4) / (float)(1 << 28);
                // Stored column-major: rows are (m0, m3, m6), (m1, m4, m7), (m2, m5, m8).
                var rot = new Matrix4x4(M(0), M(3), M(6), 0, M(1), M(4), M(7), 0, M(2), M(5), M(8), 0, 0, 0, 0, 1);
                scene.Statics.Add(new Placed(model, "", pos, rot, true));
            }
        }
        if (scene.Sections.TryGetValue("MPOB", out var mpob))
        {
            int n = BitConverter.ToInt32(b, mpob.Offset);
            for (int i = 0; i < n; i++)
            {
                int o = mpob.Offset + 4 + i * 66;
                if (o + 66 > mpob.Offset + mpob.Length) break;
                string script = System.Text.Encoding.ASCII.GetString(b, o + 6, 9).Split('\0')[0].Trim();
                string model = System.Text.Encoding.ASCII.GetString(b, o + 0x0F, 9).Split('\0')[0].Trim();
                var pos = new Vector3(I24(b, o + 0x1B), I24(b, o + 0x1F), I24(b, o + 0x23) & 0xFFFFFF);
                float A(int off) => (BitConverter.ToUInt32(b, o + off) % 2048) * MathF.PI / 1024f;
                // Roll, then pitch, then yaw, each the opposite way to the right-hand rule (checked against a shelf placed
                // both as an object, yaw 512, and as a static with its matrix; and rope-bridge planks' tilt).
                var rot = Matrix4x4.CreateRotationZ(-A(0x2E)) * Matrix4x4.CreateRotationX(-A(0x26)) * Matrix4x4.CreateRotationY(-A(0x2A));
                scene.Objects.Add(new Placed(model, script, pos, rot, b[o + 0x18] != 0));
                scene.ObjectTypes.Add(b[o + 4]);
                scene.ObjectLights.Add((BitConverter.ToInt16(b, o + 0x34), BitConverter.ToInt16(b, o + 0x36), BitConverter.ToInt16(b, o + 0x3C), BitConverter.ToInt16(b, o + 0x3E), BitConverter.ToInt16(b, o + 0x40)));
            }
        }
        if (scene.Sections.TryGetValue("MPMK", out var mpmk))
        {
            int n = BitConverter.ToInt32(b, mpmk.Offset);
            // 13 bytes per marker, but stored as all positions (3 x i32) first, then one type byte per marker.
            for (int i = 0; i < n && 4 + n * 13 <= mpmk.Length; i++)
            {
                int o = mpmk.Offset + 4 + i * 12;
                scene.Markers.Add(new Vector3(BitConverter.ToInt32(b, o), BitConverter.ToInt32(b, o + 4), BitConverter.ToInt32(b, o + 8)));
            }
        }
        if (scene.Sections.TryGetValue("MPSL", out var mpsl))
        {
            int n = BitConverter.ToInt32(b, mpsl.Offset);
            for (int i = 0; i < n && mpsl.Offset + 4 + (i + 1) * 42 <= mpsl.Offset + mpsl.Length; i++)
            {
                int o = mpsl.Offset + 4 + i * 42;
                if (b[o + 0x1F] != 1 || b[o + 0x25] != 7) continue;   // channel map 0..7: an active light
                int C(int at) => Math.Clamp((int)BitConverter.ToInt16(b, o + at), -255, 255);
                scene.Lights.Add((C(0x18), C(0x1A), C(0x1C), new Vector3(BitConverter.ToInt32(b, o + 8), BitConverter.ToInt32(b, o + 12), BitConverter.ToInt32(b, o + 16)), BitConverter.ToInt32(b, o + 0x14)));
            }
        }
        if (scene.Sections.TryGetValue("MPRP", out var mprp))
        {
            int n = BitConverter.ToInt32(b, mprp.Offset);
            for (int i = 0; i < n && mprp.Offset + 4 + (i + 1) * 80 <= mprp.Offset + mprp.Length; i++)
            {
                int o = mprp.Offset + 4 + i * 80;
                string S(int at) => System.Text.Encoding.ASCII.GetString(b, o + at, 9).Split('\0')[0].Trim();
                scene.Ropes.Add(new Rope(new Vector3(BitConverter.ToInt32(b, o + 4), BitConverter.ToInt32(b, o + 8), BitConverter.ToInt32(b, o + 12)) / 256f,
                    BitConverter.ToInt16(b, o + 32), S(34), S(43)));
            }
        }
        if (scene.Sections.TryGetValue("MPSF", out var mpsf))
        {
            int n = BitConverter.ToInt32(b, mpsf.Offset);
            for (int i = 0; i < n && mpsf.Offset + 4 + (i + 1) * 24 <= mpsf.Offset + mpsf.Length; i++)
            {
                int o = mpsf.Offset + 4 + i * 24;
                int tex = BitConverter.ToUInt16(b, o + 0x14);
                scene.Flats.Add((new Vector3(I24(b, o + 8), I24(b, o + 12), I24(b, o + 16) & 0xFFFFFF), tex >> 7, tex & 0x7F));
            }
        }
        return scene;
    }
}

/// <summary>
/// WORLD.INI: Redguard's maps by id (world_map[n] = scene .RGM, world_world[n] = terrain .WLD for outdoor maps,
/// world_palette[n], sun/ambient/fog settings) and the start (start_world, start_marker). Several ids can share one
/// scene. LoadWorld(id, marker, facing) moves the player between them.
/// </summary>
public sealed class RgWorldIni
{
    public sealed record Map(int Id, string Scene, string? World, string Palette, Dictionary<string, string> Settings);

    public Dictionary<int, Map> Maps { get; } = [];
    public int StartWorld { get; private set; }
    public int StartMarker { get; private set; }

    public static RgWorldIni Load(string path)
    {
        var ini = new RgWorldIni();
        var byId = new Dictionary<int, Dictionary<string, string>>();
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Split(';')[0].Trim();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim().ToLowerInvariant(), value = line[(eq + 1)..].Trim();
            if (key == "start_world") ini.StartWorld = int.Parse(value);
            else if (key == "start_marker") ini.StartMarker = int.Parse(value);
            int lb = key.IndexOf('['), rb = key.IndexOf(']');
            if (!key.StartsWith("world_") || lb < 0 || rb < lb || !int.TryParse(key[(lb + 1)..rb], out int id)) continue;
            if (!byId.TryGetValue(id, out var d)) byId[id] = d = new(StringComparer.OrdinalIgnoreCase);
            d[key[6..lb]] = value;
        }
        static string Stem(string p) => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')).ToUpperInvariant();
        foreach (var (id, d) in byId)
            if (d.TryGetValue("map", out var map))
                ini.Maps[id] = new Map(id, Stem(map), d.TryGetValue("world", out var w) ? Stem(w) : null,
                    d.TryGetValue("palette", out var pal) ? Stem(pal) : "REDGUARD", d);
        return ini;
    }
}
