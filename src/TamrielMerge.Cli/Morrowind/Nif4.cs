using System.Numerics;
using System.Text;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Reader for Morrowind's NIF files (NetImmerse 4.0.0.2). The format stores no block sizes, so every block type is
/// parsed field by field (layouts from nif.xml and OpenMW's Morrowind reader); a file is accepted only when parsing
/// ends exactly at its footer. Geometry, materials, textures, skinning, extra data and animation keys are kept;
/// particle systems are kept as their raw settings.
/// </summary>
public sealed class Nif4
{
    public List<NifBlock> Blocks { get; } = [];
    public List<int> Roots { get; } = [];

    public NifBlock? Root => Roots.Count > 0 && Roots[0] >= 0 ? Blocks[Roots[0]] : Blocks.FirstOrDefault();
    public T? Get<T>(int index) where T : NifBlock => index >= 0 && index < Blocks.Count ? Blocks[index] as T : null;

    public static Nif4 Read(byte[] data, string? name = null)
    {
        var r = new NifReader(data);
        string header = r.Line();
        if (!header.StartsWith("NetImmerse File Format, Version 4.0.0.2")) throw new InvalidDataException($"{name}: unsupported NIF '{header}'");
        if (r.U32() != 0x04000002) throw new InvalidDataException($"{name}: version mismatch");
        int count = r.I32();
        var nif = new Nif4();
        for (int i = 0; i < count; i++)
        {
            string type = r.Str();
            int start = r.Pos;
            NifBlock block;
            try { block = ReadBlock(r, type); }
            catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException($"{name}: block {i} {type} at {start}: {e.Message}", e); }
            block.Type = type;
            block.Index = i;
            nif.Blocks.Add(block);
        }
        int roots = r.I32();
        for (int i = 0; i < roots; i++) nif.Roots.Add(r.I32());
        if (r.Pos != data.Length) throw new InvalidDataException($"{name}: {data.Length - r.Pos} bytes left after footer (last block {nif.Blocks.LastOrDefault()?.Type})");
        return nif;
    }

    static NifBlock ReadBlock(NifReader r, string type)
    {
        switch (type)
        {
            case "NiNode": case "RootCollisionNode": case "NiBSAnimationNode": case "NiBSParticleNode": case "AvoidNode": case "NiBillboardNode":
                return NiNode.ReadNode(r, new NiNode());
            case "NiTriShape":
                return NiTriShape.ReadGeom(r, new NiTriShape());
            case "NiRotatingParticles": case "NiAutoNormalParticles":
                return NiTriShape.ReadGeom(r, new NiParticles());
            case "NiTriShapeData":
            {
                var d = new NiTriShapeData();
                d.ReadGeomData(r);
                int tris = r.U16();
                int points = r.I32();
                d.Triangles = new ushort[points];
                for (int i = 0; i < points; i++) d.Triangles[i] = r.U16();
                if (points != tris * 3) throw new InvalidDataException($"triangle points {points} != 3 x {tris}");
                int groups = r.U16();
                for (int i = 0; i < groups; i++) r.Skip(2 * r.U16());
                return d;
            }
            case "NiRotatingParticlesData": case "NiAutoNormalParticlesData":
            {
                var d = new NiParticlesData();
                d.ReadGeomData(r);
                r.U16();            // particle count
                r.F32();            // radius
                r.U16();            // active count
                if (r.Bool()) r.Skip(4 * d.Vertices.Length);           // sizes
                if (type == "NiRotatingParticlesData" && r.Bool()) r.Skip(16 * d.Vertices.Length); // rotations
                return d;
            }
            case "NiMaterialProperty":
            {
                var m = new NiMaterialProperty();
                m.ReadNet(r);
                m.Flags = r.U16();
                m.Ambient = r.V3(); m.Diffuse = r.V3(); m.Specular = r.V3(); m.Emissive = r.V3();
                m.Glossiness = r.F32(); m.Alpha = r.F32();
                return m;
            }
            case "NiAlphaProperty":
            {
                var a = new NiAlphaProperty();
                a.ReadNet(r);
                a.Flags = r.U16();
                a.Threshold = r.U8();
                return a;
            }
            case "NiVertexColorProperty":
            {
                var v = new NiVertexColorProperty();
                v.ReadNet(r);
                v.Flags = r.U16();
                v.VertexMode = r.U32(); v.LightingMode = r.U32();
                return v;
            }
            case "NiZBufferProperty": case "NiWireframeProperty": case "NiDitherProperty": case "NiShadeProperty": case "NiSpecularProperty":
            {
                var p = new NiFlagProperty();
                p.ReadNet(r);
                p.Flags = r.U16();
                return p;
            }
            case "NiTexturingProperty":
            {
                var t = new NiTexturingProperty();
                t.ReadNet(r);
                t.Flags = r.U16();
                t.ApplyMode = r.U32();
                int n = r.I32();
                t.Textures = new TexDesc?[n];
                for (int i = 0; i < n; i++)
                {
                    if (!r.Bool()) continue;
                    t.Textures[i] = new TexDesc(r.I32(), r.U32(), r.U32(), r.U32());
                    r.Skip(6);                  // PS2 L/K, unknown short
                    if (i == 5) r.Skip(4 * 6);  // bump map luma scale/offset and 2x2 matrix
                }
                return t;
            }
            case "NiSourceTexture":
            {
                var s = new NiSourceTexture();
                s.ReadNet(r);
                if (r.U8() != 0) s.FileName = r.Str();
                else { r.U8(); s.PixelData = r.I32(); }
                r.Skip(12);                     // pixel layout, mipmaps, alpha format
                r.U8();                         // is static
                return s;
            }
            case "NiPixelData":
            {
                var p = new NiRaw();
                r.Skip(4 + 16 + 4 + 8);         // format, masks, bpp, compare bits
                r.I32();                        // palette
                int mips = r.I32();
                r.Skip(4 + 12 * mips);          // bytes per pixel, mip sizes/offsets
                r.Skip(r.I32());
                return p;
            }
            case "NiStringExtraData":
            {
                var e = new NiStringExtraData();
                e.ReadExtra(r);
                e.Value = r.Str();
                return e;
            }
            case "NiTextKeyExtraData":
            {
                var e = new NiTextKeyExtraData();
                e.ReadExtra(r);
                int n = r.I32();
                for (int i = 0; i < n; i++) e.Keys.Add((r.F32(), r.Str()));
                return e;
            }
            case "NiVertWeightsExtraData":
            {
                var e = new NiRawExtra();
                e.ReadExtra(r);
                r.Skip(e.RecordSize);
                return e;
            }
            case "NiSkinInstance":
            {
                var s = new NiSkinInstance { Data = r.I32(), SkeletonRoot = r.I32() };
                int n = r.I32();
                s.Bones = new int[n];
                for (int i = 0; i < n; i++) s.Bones[i] = r.I32();
                return s;
            }
            case "NiSkinData":
            {
                var s = new NiSkinData { Transform = r.Transform() };
                int n = r.I32();
                r.I32();                        // skin partition
                for (int i = 0; i < n; i++)
                {
                    var bone = new SkinBone { Transform = r.Transform(), BoundCenter = r.V3(), BoundRadius = r.F32() };
                    int verts = r.U16();
                    bone.Weights = new (ushort, float)[verts];
                    for (int v = 0; v < verts; v++) bone.Weights[v] = (r.U16(), r.F32());
                    s.Bones.Add(bone);
                }
                return s;
            }
            case "NiKeyframeController": case "NiAlphaController": case "NiVisController":
            {
                var c = new NiController();
                c.ReadController(r);
                c.Data = r.I32();
                return c;
            }
            case "NiMaterialColorController":
            {
                var c = new NiController();
                c.ReadController(r);
                c.Data = r.I32();
                return c;
            }
            case "NiUVController":
            {
                var c = new NiController();
                c.ReadController(r);
                r.U16();                        // UV set
                c.Data = r.I32();
                return c;
            }
            case "NiGeomMorpherController":
            {
                var c = new NiController();
                c.ReadController(r);
                c.Data = r.I32();
                r.U8();                         // always update
                return c;
            }
            case "NiPathController":
            {
                var c = new NiController();
                c.ReadController(r);
                r.Skip(4 + 4 + 4 + 2);          // bank direction, max bank angle, smoothing, follow axis
                c.Data = r.I32();               // path data
                r.I32();                        // percent data
                return c;
            }
            case "NiParticleSystemController": case "NiBSPArrayController":
            {
                var c = new NiParticleSystemController();
                c.ReadController(r);
                c.Speed = r.F32(); c.SpeedVariation = r.F32();
                c.Declination = r.F32(); c.DeclinationVariation = r.F32();
                c.PlanarAngle = r.F32(); c.PlanarAngleVariation = r.F32();
                c.InitialNormal = r.V3(); c.InitialColor = r.V4(); c.InitialSize = r.F32();
                c.EmitStart = r.F32(); c.EmitStop = r.F32();
                r.U8();                         // reset particle system
                c.BirthRate = r.F32(); c.Lifetime = r.F32(); c.LifetimeVariation = r.F32();
                c.EmitFlags = r.U16(); c.EmitterDimensions = r.V3(); c.Emitter = r.I32();
                r.U16(); r.F32(); r.U16(); r.F32(); r.F32();    // spawn generations, percentage, multiplier, speed/dir chaos
                int particles = r.U16();
                c.NumParticles = particles;
                r.U16();                        // valid particles
                r.Skip(particles * 40);
                r.I32();                        // emitter modifier
                c.Modifier = r.I32();
                c.Collider = r.I32();
                r.U8();                         // static target bound
                return c;
            }
            case "NiGravity":
            {
                var m = new NiGravity(); m.ReadModifier(r);
                m.Decay = r.F32(); m.Force = r.F32(); m.ForceType = r.I32(); m.Position = r.V3(); m.Direction = r.V3();
                return m;
            }
            case "NiParticleGrowFade":
            {
                var m = new NiParticleGrowFade(); m.ReadModifier(r);
                m.Grow = r.F32(); m.Fade = r.F32();
                return m;
            }
            case "NiParticleColorModifier":
            {
                var m = new NiParticleColorModifier(); m.ReadModifier(r);
                m.ColorData = r.I32();
                return m;
            }
            case "NiParticleRotation":
            {
                var m = new NiParticleRotation(); m.ReadModifier(r);
                m.RandomAxis = r.U8() != 0; m.Axis = r.V3(); m.Speed = r.F32();
                return m;
            }
            case "NiPlanarCollider":
            {
                var m = new NiParticleModifier(); m.ReadModifier(r);
                r.Skip(4 + 8 + 12 * 4 + 4);     // bounce, extents, position, x/y vectors, normal, distance
                return m;
            }
            case "NiKeyframeData":
            {
                var d = new NiKeyframeData();
                int rotKeys = r.I32();
                if (rotKeys > 0)
                {
                    d.RotationType = r.I32();
                    for (int i = 0; i < rotKeys; i++)
                    {
                        float t = r.F32();
                        if (d.RotationType == 4) continue;
                        var q = r.V4();
                        d.Rotations.Add((t, new Quaternion(q.Y, q.Z, q.W, q.X)));   // stored w, x, y, z
                        if (d.RotationType == 3) r.Skip(12);
                    }
                    if (d.RotationType == 4)
                    {
                        r.F32();                // axis order
                        for (int axis = 0; axis < 3; axis++) ReadKeys(r, 1, null);
                    }
                }
                ReadKeys(r, 3, d.Translations);
                ReadKeys(r, 1, d.Scales);
                return d;
            }
            case "NiColorData":
            {
                var d = new NiColorData();
                int n = r.I32();
                if (n > 0)
                {
                    int interp = r.I32();
                    for (int i = 0; i < n; i++)
                    {
                        float time = r.F32();
                        d.Keys.Add((time, r.V4()));
                        if (interp == 2) r.Skip(32);
                        else if (interp == 3) r.Skip(12);
                    }
                }
                return d;
            }
            case "NiPosData": case "NiFloatData":
            {
                ReadKeys(r, type == "NiPosData" ? 3 : 1, null);
                return new NiRaw();
            }
            case "NiVisData":
            {
                int n = r.I32();
                r.Skip(n * 5);
                return new NiRaw();
            }
            case "NiUVData":
            {
                for (int i = 0; i < 4; i++) ReadKeys(r, 1, null);
                return new NiRaw();
            }
            case "NiMorphData":
            {
                var d = new NiMorphData();
                int morphs = r.I32(), verts = r.I32();
                r.U8();                         // relative targets
                for (int m = 0; m < morphs; m++)
                {
                    int keys = r.I32();
                    int interp = r.I32();
                    for (int k = 0; k < keys; k++) r.Skip(4 + 4 + (interp == 2 ? 8 : interp == 3 ? 12 : 0));
                    var v = new Vector3[verts];
                    for (int i = 0; i < verts; i++) v[i] = r.V3();
                    d.Morphs.Add(v);
                }
                return d;
            }
            case "NiSequenceStreamHelper":
            {
                var b = new NiObjectNET(); b.ReadNet(r);
                return b;
            }
            case "NiTextureEffect":
            {
                var e = new NiNode(); e.ReadAV(r);
                r.Skip(4 * r.I32());            // affected nodes
                r.Skip(36 + 12 + 4 * 4);        // projection matrix/translation, filter, clamp, type, coordinate generation
                r.I32();                        // source texture
                r.U8();
                r.Skip(16 + 4 + 2);             // plane, PS2 L/K, unknown short
                return e;
            }
            case "NiCamera":
            {
                var c = new NiNode(); c.ReadAV(r);
                r.Skip(6 * 4 + 4 * 4 + 4);      // frustum, viewport, LOD adjust
                r.I32();                        // scene
                r.I32();                        // unused
                return c;
            }
            default:
                throw new InvalidDataException($"unknown block type {type} at {r.Pos}");
        }
    }

    /// <summary>A key group: count, interpolation (when keys exist), then keys of time + value (+ tangents for
    /// quadratic, + tension/bias/continuity for TBC). <paramref name="width"/> is the value's float count.</summary>
    static void ReadKeys(NifReader r, int width, List<(float Time, Vector3 Value)>? into)
    {
        int n = r.I32();
        if (n == 0) return;
        int interp = r.I32();
        for (int i = 0; i < n; i++)
        {
            float t = r.F32();
            var v = new float[width];
            for (int k = 0; k < width; k++) v[k] = r.F32();
            if (interp == 2) r.Skip(8 * width);
            else if (interp == 3) r.Skip(12);
            into?.Add((t, width >= 3 ? new Vector3(v[0], v[1], v[2]) : new Vector3(v[0], 0, 0)));
        }
    }
}

public sealed class NifReader(byte[] data)
{
    public int Pos;
    public byte U8() => data[Pos++];
    public ushort U16() { var v = BitConverter.ToUInt16(data, Pos); Pos += 2; return v; }
    public int I32() { var v = BitConverter.ToInt32(data, Pos); Pos += 4; return v; }
    public uint U32() { var v = BitConverter.ToUInt32(data, Pos); Pos += 4; return v; }
    public float F32() { var v = BitConverter.ToSingle(data, Pos); Pos += 4; return v; }
    /// <summary>NIF 4.0.0.2 booleans are 32-bit.</summary>
    public bool Bool() => I32() != 0;
    public Vector3 V3() => new(F32(), F32(), F32());
    public Vector4 V4() => new(F32(), F32(), F32(), F32());
    public void Skip(int n) { if (n < 0 || Pos + n > data.Length) throw new EndOfStreamException($"skip {n} at {Pos}"); Pos += n; }
    public string Str()
    {
        int n = I32();
        if (n < 0 || Pos + n > data.Length) throw new EndOfStreamException($"string of {n} at {Pos}");
        var s = Tes3Plugin.Encoding.GetString(data, Pos, n);
        Pos += n;
        return s;
    }
    public string Line()
    {
        int e = Array.IndexOf(data, (byte)'\n', Pos);
        var s = Encoding.ASCII.GetString(data, Pos, e - Pos);
        Pos = e + 1;
        return s;
    }
    /// <summary>A 3x3 rotation, row-major.</summary>
    public Matrix4x4 Matrix33()
    {
        var m = new float[9];
        for (int i = 0; i < 9; i++) m[i] = F32();
        return new Matrix4x4(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, 0, 0, 0, 1);
    }
    /// <summary>Rotation (3x3, row-major), translation, scale — the order skin data stores them in.</summary>
    public NifTransform Transform() => new(Matrix33(), V3(), F32());
}

/// <summary>A NIF transform. <see cref="Rotation"/> holds the rotation as stored (row-major, rows are the rotated
/// axes as used by NIF: v' = R·v), translation and uniform scale.</summary>
public readonly record struct NifTransform(Matrix4x4 Rotation, Vector3 Translation, float Scale)
{
    public static readonly NifTransform Identity = new(Matrix4x4.Identity, Vector3.Zero, 1);

    /// <summary>Applies the transform to a point: T + s·(R·p).</summary>
    public Vector3 Apply(Vector3 p) => Translation + Scale * Rotate(p);
    public Vector3 Rotate(Vector3 v) => new(
        Rotation.M11 * v.X + Rotation.M12 * v.Y + Rotation.M13 * v.Z,
        Rotation.M21 * v.X + Rotation.M22 * v.Y + Rotation.M23 * v.Z,
        Rotation.M31 * v.X + Rotation.M32 * v.Y + Rotation.M33 * v.Z);

    /// <summary>parent ∘ this: maps this node's space into the parent's parent space.</summary>
    public NifTransform Then(NifTransform parent)
    {
        // R = Rp·Rc (column-vector convention, matrices stored row-major as M_ij = row i, column j).
        var rp = parent.Rotation; var rc = Rotation;
        var r = new Matrix4x4(
            rp.M11 * rc.M11 + rp.M12 * rc.M21 + rp.M13 * rc.M31, rp.M11 * rc.M12 + rp.M12 * rc.M22 + rp.M13 * rc.M32, rp.M11 * rc.M13 + rp.M12 * rc.M23 + rp.M13 * rc.M33, 0,
            rp.M21 * rc.M11 + rp.M22 * rc.M21 + rp.M23 * rc.M31, rp.M21 * rc.M12 + rp.M22 * rc.M22 + rp.M23 * rc.M32, rp.M21 * rc.M13 + rp.M22 * rc.M23 + rp.M23 * rc.M33, 0,
            rp.M31 * rc.M11 + rp.M32 * rc.M21 + rp.M33 * rc.M31, rp.M31 * rc.M12 + rp.M32 * rc.M22 + rp.M33 * rc.M32, rp.M31 * rc.M13 + rp.M32 * rc.M23 + rp.M33 * rc.M33, 0,
            0, 0, 0, 1);
        return new NifTransform(r, parent.Apply(Translation), parent.Scale * Scale);
    }
}

public class NifBlock
{
    public string Type { get; set; } = "";
    public int Index { get; set; }
}

public sealed class NiRaw : NifBlock;

public class NiObjectNET : NifBlock
{
    public string Name = "";
    public int ExtraData = -1, Controller = -1;
    public void ReadNet(NifReader r) { Name = r.Str(); ExtraData = r.I32(); Controller = r.I32(); }
}

public class NiNode : NiObjectNET
{
    public ushort Flags;
    public NifTransform Transform = NifTransform.Identity;
    public int[] Properties = [];
    public int[] Children = [];

    /// <summary>NiAVObject flag 1: hidden.</summary>
    public bool Hidden => (Flags & 1) != 0;

    public void ReadAV(NifReader r)
    {
        ReadNet(r);
        Flags = r.U16();
        var translation = r.V3();
        var rotation = r.Matrix33();
        Transform = new NifTransform(rotation, translation, r.F32());
        r.V3();                    // velocity
        int props = r.I32();
        Properties = new int[props];
        for (int i = 0; i < props; i++) Properties[i] = r.I32();
        if (r.Bool()) ReadBound(r);
    }

    static void ReadBound(NifReader r)
    {
        switch (r.I32())
        {
            case 0: r.Skip(16); break;                  // sphere
            case 1: r.Skip(12 + 36 + 12); break;        // box: center, axes, extents
            case 2: r.Skip(12 + 12 + 4 + 4); break;     // capsule
            case 4: { int n = r.I32(); for (int i = 0; i < n; i++) ReadBound(r); break; }
            case 5: r.Skip(16 + 12); break;             // half space
            default: throw new InvalidDataException("unknown bounding volume");
        }
    }

    public static NiNode ReadNode(NifReader r, NiNode n)
    {
        n.ReadAV(r);
        int kids = r.I32();
        n.Children = new int[kids];
        for (int i = 0; i < kids; i++) n.Children[i] = r.I32();
        r.Skip(4 * r.I32());       // effects
        return n;
    }
}

public class NiTriShape : NiNode
{
    public int Data = -1, Skin = -1;
    public static NiTriShape ReadGeom(NifReader r, NiTriShape g)
    {
        g.ReadAV(r);
        g.Data = r.I32();
        g.Skin = r.I32();
        return g;
    }
}

public sealed class NiParticles : NiTriShape;

public class NiGeometryData : NifBlock
{
    public Vector3[] Vertices = [], Normals = [];
    public Vector4[] Colors = [];
    public Vector2[][] UVSets = [];
    public Vector3 Center;
    public float Radius;

    public void ReadGeomData(NifReader r)
    {
        int n = r.U16();
        if (r.Bool()) { Vertices = new Vector3[n]; for (int i = 0; i < n; i++) Vertices[i] = r.V3(); }
        if (r.Bool()) { Normals = new Vector3[n]; for (int i = 0; i < n; i++) Normals[i] = r.V3(); }
        Center = r.V3(); Radius = r.F32();
        if (r.Bool()) { Colors = new Vector4[n]; for (int i = 0; i < n; i++) Colors[i] = r.V4(); }
        int sets = r.U16();
        if (r.Bool())
        {
            UVSets = new Vector2[sets][];
            for (int s = 0; s < sets; s++)
            {
                UVSets[s] = new Vector2[n];
                for (int i = 0; i < n; i++) UVSets[s][i] = new Vector2(r.F32(), r.F32());
            }
        }
    }
}

public sealed class NiTriShapeData : NiGeometryData { public ushort[] Triangles = []; }
public sealed class NiParticlesData : NiGeometryData;

public class NiFlagProperty : NiObjectNET { public ushort Flags; }
public sealed class NiMaterialProperty : NiFlagProperty
{
    public Vector3 Ambient, Diffuse, Specular, Emissive;
    public float Glossiness, Alpha;
}
public sealed class NiAlphaProperty : NiFlagProperty { public byte Threshold; }
public sealed class NiVertexColorProperty : NiFlagProperty { public uint VertexMode, LightingMode; }

/// <summary>A texture slot: source texture block, clamp mode (0 clamp both .. 3 wrap both), filter, UV set.</summary>
public readonly record struct TexDesc(int Source, uint Clamp, uint Filter, uint UVSet);

public sealed class NiTexturingProperty : NiFlagProperty
{
    public uint ApplyMode;
    /// <summary>Slots: 0 base, 1 dark, 2 detail, 3 gloss, 4 glow, 5 bump, 6 decal.</summary>
    public TexDesc?[] Textures = [];
    public TexDesc? Base => Textures.Length > 0 ? Textures[0] : null;
    public TexDesc? Glow => Textures.Length > 4 ? Textures[4] : null;
}

public sealed class NiSourceTexture : NiObjectNET
{
    public string? FileName;
    public int PixelData = -1;
}

public class NiExtraData : NifBlock
{
    public int Next = -1, RecordSize;
    public void ReadExtra(NifReader r) { Next = r.I32(); RecordSize = r.I32(); }
}
public sealed class NiStringExtraData : NiExtraData { public string Value = ""; }
public sealed class NiTextKeyExtraData : NiExtraData { public List<(float Time, string Text)> Keys = []; }
public sealed class NiRawExtra : NiExtraData;

public sealed class NiSkinInstance : NifBlock
{
    public int Data = -1, SkeletonRoot = -1;
    public int[] Bones = [];
}
public sealed class SkinBone
{
    public NifTransform Transform;
    public Vector3 BoundCenter;
    public float BoundRadius;
    public (ushort Vertex, float Weight)[] Weights = [];
}
public sealed class NiSkinData : NifBlock
{
    public NifTransform Transform;
    public List<SkinBone> Bones = [];
}

public class NiController : NifBlock
{
    public int Next = -1, Target = -1, Data = -1;
    public ushort Flags;
    public float Frequency, Phase, Start, Stop;
    public void ReadController(NifReader r)
    {
        Next = r.I32(); Flags = r.U16();
        Frequency = r.F32(); Phase = r.F32(); Start = r.F32(); Stop = r.F32();
        Target = r.I32();
    }
}

public sealed class NiParticleSystemController : NiController
{
    public float Speed, SpeedVariation, Declination, DeclinationVariation, PlanarAngle, PlanarAngleVariation;
    public Vector3 InitialNormal, EmitterDimensions;
    public Vector4 InitialColor;
    public float InitialSize, EmitStart, EmitStop, BirthRate, Lifetime, LifetimeVariation;
    public ushort EmitFlags;
    public int Emitter = -1, Modifier = -1, Collider = -1;
    /// <summary>The most particles alive at once.</summary>
    public int NumParticles;
    /// <summary>Emit flags bit 0: particles come at the birth rate (else all at once).</summary>
    public bool UseBirthRate => (EmitFlags & 1) != 0;
}

/// <summary>Gravity on particles: planar (type 0, along Direction) or spherical (type 1, towards Position).</summary>
public sealed class NiGravity : NiParticleModifier
{
    public float Decay, Force;
    public int ForceType;
    public Vector3 Position, Direction;
}
public sealed class NiParticleGrowFade : NiParticleModifier { public float Grow, Fade; }
public sealed class NiParticleColorModifier : NiParticleModifier { public int ColorData = -1; }
public sealed class NiParticleRotation : NiParticleModifier
{
    public bool RandomAxis;
    public Vector3 Axis;
    public float Speed;
}
/// <summary>Colour keys (RGBA) over a particle's life, time 0 to 1.</summary>
public sealed class NiColorData : NifBlock { public List<(float Time, Vector4 Color)> Keys = []; }

public class NiParticleModifier : NifBlock
{
    public int Next = -1, Controller = -1;
    public void ReadModifier(NifReader r) { Next = r.I32(); Controller = r.I32(); }
}

public sealed class NiKeyframeData : NifBlock
{
    public int RotationType;
    public List<(float Time, Quaternion Value)> Rotations = [];
    public List<(float Time, Vector3 Value)> Translations = [];
    /// <summary>Scale keys (value in X).</summary>
    public List<(float Time, Vector3 Value)> Scales = [];
}

public sealed class NiMorphData : NifBlock { public List<Vector3[]> Morphs = []; }
