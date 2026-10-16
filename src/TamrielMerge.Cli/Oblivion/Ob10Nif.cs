using System.Numerics;
using System.Text;
using V3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Gamebryo 10.1.0.106 and 10.2.0.0 models: Oblivion's older effects (dust, fireflies, plasma, the Ayleid welkynd
/// glows, two traps), which nifly can't read. nifly ends the process on them (a fail-fast, not an exception .NET can
/// catch), so they are recognised from their header first (<see cref="IsUnreadableByNifly"/>) and read here.
/// These versions keep no block sizes, so every block type in them is read field by field (nif.xml's layout for
/// these versions); the read is checked by landing exactly on the file's footer. What the conversion uses is kept:
/// the node tree, shapes with their data and properties, particle systems with their modifiers and emitter
/// controllers, the float and bool interpolators and keys that drive them, and the controller sequences that set
/// those (an effect's Idle loop sets its birth rate).
/// </summary>
public sealed class Ob10Nif
{
    /// <summary>The header's version and block types, or null for another kind of file.</summary>
    public static (uint Version, List<string> Types, List<int> BlockTypes)? Header(byte[] b)
    {
        int nl = Array.IndexOf(b, (byte)10);
        if (nl < 0 || nl > 60 || !Encoding.ASCII.GetString(b, 0, nl).StartsWith("Gamebryo File Format")) return null;
        int p = nl + 1;
        uint version = BitConverter.ToUInt32(b, p); p += 4;
        if (version >= 0x14000003) p++;   // endianness
        if (version < 0x0A000108) return (version, [], []);
        p += 4;   // user version
        int blocks = BitConverter.ToInt32(b, p); p += 4;
        p += 4;   // Bethesda stream version
        for (int k = 0; k < 3; k++) p += 1 + b[p];   // creator, export info
        int typeCount = BitConverter.ToUInt16(b, p); p += 2;
        var types = new List<string>();
        for (int k = 0; k < typeCount; k++) { int l = BitConverter.ToInt32(b, p); p += 4; types.Add(Encoding.ASCII.GetString(b, p, l)); p += l; }
        var index = new List<int>();
        for (int k = 0; k < blocks; k++) { index.Add(BitConverter.ToUInt16(b, p) & 0x7FFF); p += 2; }
        return (version, types, index);
    }

    /// <summary>
    /// Whether nifly would fail on the file, and why. Found with ob-nif10-bisect (each model cut after 1, 2, ... blocks,
    /// loaded by nifly in a child process) and confirmed by splicing a block that reads fine elsewhere right after the
    /// suspect: nifly reads NiPSysData in its Gamebryo 20.0 layout, so in a 10.1 or 10.2 file it loses its place there
    /// and fails on the next block (NiPSysAgeDeathModifier in every effect here); and it can't read a 10.1.0.106
    /// NiTransformInterpolator (the 10.1 layout, with its three "valid" flags). It ends the process (a fail-fast,
    /// 0xC0000409), which .NET can't catch, so it must not be given these files.
    /// </summary>
    public static bool IsUnreadableByNifly(byte[] b, out string why)
    {
        why = "";
        if (Header(b) is not { } h || h.Version >= 0x14000004) return false;
        string v = VersionString(h.Version);
        if (h.Types.Contains("NiPSysData")) { why = $"nifly can't read a Gamebryo {v} NiPSysData (particle data)"; return true; }
        if (h.Version <= V10_1_0_110 && h.Types.Contains("NiTransformInterpolator")) { why = $"nifly can't read a Gamebryo {v} NiTransformInterpolator"; return true; }
        return false;
    }

    static string VersionString(uint v) => $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    // ---- the blocks kept ----

    public class Block { public string Type = ""; public int Index; public string Name = ""; public int Controller = -1; public List<int> Extra = []; }
    public class AvObject : Block { public ushort Flags; public V3 T; public Matrix4x4 R = Matrix4x4.Identity; public float Scale = 1; public List<int> Props = []; public int Collision = -1; }
    public sealed class Node : AvObject { public List<int> Children = []; }
    public sealed class Shape : AvObject { public int Data = -1; public bool WorldSpace; public List<int> Modifiers = []; }
    public sealed class GeomData : Block
    {
        public List<V3> Verts = [], Normals = [];
        public List<Vector4> Colors = [];
        public List<Vector2> Uvs = [];
        public List<(ushort, ushort, ushort)> Tris = [];
        public int ParticleCount;
    }
    public sealed class Property : Block
    {
        public ushort Flags; public byte Threshold; public int BaseTexture = -1; public string? File;
        public float Glossiness = 10, Alpha = 1; public V3 Emissive, Diffuse = V3.One;
    }
    public sealed class Modifier : Block
    {
        public uint Order; public int Target = -1; public bool Active = true;
        public float Speed, SpeedVar, Declination, DeclinationVar, PlanarAngle, PlanarAngleVar, Radius, LifeSpan, LifeSpanVar;
        public Vector4 Color = Vector4.One;
        public int EmitterObject = -1;
        public V3 Size;                 // box: width, height, depth; sphere: radius; cylinder: radius, height
        public float GrowTime, FadeTime;
        public int ColorData = -1;
        public V3 Axis; public float Decay, Strength, Turbulence, TurbulenceScale = 1; public uint ForceType; public int GravityObject = -1;
        public ushort SpawnGenerations, MinSpawn = 1, MaxSpawn = 1; public float PercentSpawned = 1, SpawnSpeedVar, SpawnDirVar, SpawnLife, SpawnLifeVar;
        public float DragPercent, DragRange, DragFalloff;
        public float Bounce; public bool DieOnCollide; public float ColliderWidth, ColliderHeight; public V3 XAxis, YAxis;
    }
    public sealed class Controller : Block
    {
        public int Next = -1; public ushort Flags; public float Frequency = 1, Phase, Start, Stop; public int Target = -1;
        public int Interpolator = -1, VisInterpolator = -1; public string ModifierName = "";
        public List<int> Sequences = [];
    }
    public sealed class Interpolator : Block { public float Value; public bool BoolValue; public int Data = -1; }
    public sealed class KeyData : Block { public List<(float Time, Vector4 Value)> Keys = []; }
    public sealed class Sequence : Block
    {
        public List<(int Interpolator, int Controller, string Node, string ControllerType, string ControllerId, string InterpolatorId)> Links = [];
        public uint Cycle; public float Frequency = 1, Start, Stop;
        /// <summary>Gamebryo 10.2: the links' names as offsets into a string palette read later in the file.</summary>
        internal List<(int Interp, int Ctlr, uint[] Offsets)> Pending = [];
        internal int Palette = -1;
    }

    public uint Version { get; private set; }
    public List<Block?> Blocks { get; } = [];
    public List<int> Roots { get; } = [];
    /// <summary>Where each block starts (its zero word, in versions that have one) and where the blocks end.</summary>
    public List<int> Starts { get; } = [];
    public int BlocksEnd { get; private set; }
    int indexAt;

    public T? Get<T>(int index) where T : Block => index >= 0 && index < Blocks.Count ? Blocks[index] as T : null;

    // ---- reading ----

    sealed class Reader(byte[] b)
    {
        public int Pos;
        public int Length => b.Length;
        public byte U8() => b[Pos++];
        public bool Bool() => b[Pos++] != 0;
        public ushort U16() { var v = BitConverter.ToUInt16(b, Pos); Pos += 2; return v; }
        public uint U32() { var v = BitConverter.ToUInt32(b, Pos); Pos += 4; return v; }
        public int I32() { var v = BitConverter.ToInt32(b, Pos); Pos += 4; return v; }
        public float F() { var v = BitConverter.ToSingle(b, Pos); Pos += 4; return v; }
        public V3 V() => new(F(), F(), F());
        public Vector4 V4() => new(F(), F(), F(), F());
        public string S()
        {
            int n = I32();
            if (n < 0 || Pos + n > b.Length) throw new InvalidDataException($"bad string length {n} at {Pos - 4}");
            var v = Encoding.Latin1.GetString(b, Pos, n); Pos += n; return v;
        }
        public void Skip(int n) { if (n < 0 || Pos + n > b.Length) throw new InvalidDataException($"bad skip {n} at {Pos}"); Pos += n; }
        public List<int> Refs() { int n = I32(); if (n < 0 || n > 100000) throw new InvalidDataException($"bad count {n} at {Pos - 4}"); return Enumerable.Range(0, n).Select(_ => I32()).ToList(); }
    }

    /// <summary>Reads the file; throws with the block's type and position when a block doesn't read.</summary>
    public static Ob10Nif Read(byte[] bytes, string name)
    {
        var h = Header(bytes) ?? throw new InvalidDataException($"{name}: not a Gamebryo file");
        var nif = new Ob10Nif { Version = h.Version };
        var r = new Reader(bytes);
        // Past the header: the line, version, user version, block count, stream version, export info, types, index.
        r.Pos = Array.IndexOf(bytes, (byte)10) + 1 + 4 + 4 + 4 + 4;
        for (int k = 0; k < 3; k++) r.Skip(1 + bytes[r.Pos]);
        int typeCount = r.U16();
        for (int k = 0; k < typeCount; k++) r.S();
        nif.indexAt = r.Pos;
        r.Skip(2 * h.BlockTypes.Count);
        r.U32();   // groups
        for (int i = 0; i < h.BlockTypes.Count; i++)
        {
            string type = h.Types[h.BlockTypes[i]];
            nif.Starts.Add(r.Pos);
            // Up to 10.1.0.106 every block is preceded by a zero word.
            if (h.Version <= V10_1_0_106) r.U32();
            int at = r.Pos;
            try { nif.Blocks.Add(nif.ReadBlock(r, type, i)); }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
            {
                throw new InvalidDataException($"{name}: block {i} {type} at {at} doesn't read as Gamebryo {VersionString(h.Version)} ({e.Message})");
            }
        }
        nif.BlocksEnd = r.Pos;
        // Gamebryo 10.2 sequences name their links through a palette that comes after them.
        foreach (var s in nif.Blocks.OfType<Sequence>())
        {
            foreach (var (interp, ctlr, o) in s.Pending)
                s.Links.Add((interp, ctlr, nif.PaletteString(s.Palette, o[0]), nif.PaletteString(s.Palette, o[2]), nif.PaletteString(s.Palette, o[3]), nif.PaletteString(s.Palette, o[4])));
            s.Pending.Clear();
        }
        int roots = r.I32();
        for (int k = 0; k < roots; k++) nif.Roots.Add(r.I32());
        if (r.Pos != bytes.Length || roots < 1)
            throw new InvalidDataException($"{name}: blocks read to {r.Pos} of {bytes.Length} bytes ({roots} roots): a block's layout is off");
        return nif;
    }

    bool V(uint since) => Version >= since;
    const uint V10_1_0_104 = 0x0A010068, V10_1_0_106 = 0x0A01006A, V10_1_0_108 = 0x0A01006C, V10_1_0_110 = 0x0A01006E,
        V10_1_0_112 = 0x0A010070, V10_1_0_113 = 0x0A010071, V10_1_0_114 = 0x0A010072, V10_2_0_0 = 0x0A020000;

    void ObjectNet(Reader r, Block d) { d.Name = r.S(); d.Extra = r.Refs(); d.Controller = r.I32(); }

    void Av(Reader r, AvObject d)
    {
        ObjectNet(r, d);
        d.Flags = r.U16();
        d.T = r.V();
        var m = new float[9];
        for (int i = 0; i < 9; i++) m[i] = r.F();
        d.R = new Matrix4x4(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, 0, 0, 0, 1);
        d.Scale = r.F();
        d.Props = r.Refs();
        d.Collision = r.I32();
    }

    void TimeController(Reader r, Controller c)
    {
        c.Next = r.I32(); c.Flags = r.U16(); c.Frequency = r.F(); c.Phase = r.F(); c.Start = r.F(); c.Stop = r.F(); c.Target = r.I32();
    }

    /// <summary>NiInterpController's "manager controlled" (10.1.0.104 to 10.1.0.108) and NiSingleInterpController's
    /// interpolator.</summary>
    void SingleInterp(Reader r, Controller c)
    {
        TimeController(r, c);
        if (Version is >= V10_1_0_104 and <= V10_1_0_108) r.Bool();
        c.Interpolator = r.I32();
    }

    void Keys<T>(Reader r, KeyData d, int size, Func<Vector4> value)
    {
        uint n = r.U32();
        if (n == 0) return;
        uint type = r.U32();
        for (uint k = 0; k < n; k++)
        {
            float t = r.F();
            var v = value();
            if (type == 2) r.Skip(2 * size);        // quadratic: forward and backward tangents
            else if (type == 3) r.Skip(12);         // TBC
            d.Keys.Add((t, v));
        }
    }

    void TexDesc(Reader r, Property d, bool isBase)
    {
        int src = r.I32();
        if (isBase) d.BaseTexture = src;
        r.Skip(4 * 3 + 2 * 2);   // clamp, filter, UV set; PS2 L, K
        if (r.Bool()) r.Skip(8 + 8 + 4 + 4 + 8);   // texture transform: translation, scale, rotation, method, centre
    }

    void ModifierBase(Reader r, Modifier m) { m.Name = r.S(); m.Order = r.U32(); m.Target = r.I32(); m.Active = r.Bool(); }

    void Emitter(Reader r, Modifier m)
    {
        ModifierBase(r, m);
        m.Speed = r.F(); m.SpeedVar = r.F(); m.Declination = r.F(); m.DeclinationVar = r.F(); m.PlanarAngle = r.F(); m.PlanarAngleVar = r.F();
        m.Color = r.V4(); m.Radius = r.F(); m.LifeSpan = r.F(); m.LifeSpanVar = r.F();
    }

    void BlendInterpolator(Reader r)
    {
        if (V(V10_1_0_112))
        {
            byte flags = r.U8();
            int size = r.U8();
            r.F();   // weight threshold
            if ((flags & 1) == 0)
            {
                r.Skip(1 + 1 + 1 + 1 + 4 * 4);   // interp count, single index, high priority, next, single time, sums, spinner
                r.Skip(size * (4 + 4 + 4 + 1 + 4));
            }
            return;
        }
        int arraySize = r.U16(); r.U16();   // size, grow by (until 10.1.0.109)
        r.Skip(arraySize * (4 + 4 + 4 + 4 + 4));   // items: interpolator, weight, normalized, priority (int), ease spinner
        r.Bool(); r.F(); r.Bool();          // manager controlled, weight threshold, only highest
        r.U16(); r.U16();                   // interp count, single index
        if (V(V10_1_0_108)) { r.I32(); r.F(); }   // single interpolator, single time
        r.I32(); r.I32();                   // high and next high priority
    }

    Block? ReadBlock(Reader r, string type, int index)
    {
        switch (type)
        {
            case "NiNode" or "NiBillboardNode":
            {
                var n = new Node { Type = type, Index = index };
                Av(r, n);
                n.Children = r.Refs();
                r.Refs();   // effects
                if (type == "NiBillboardNode") r.U16();
                return n;
            }
            case "NiTriShape" or "NiTriStrips" or "NiParticleSystem":
            {
                var s = new Shape { Type = type, Index = index };
                Av(r, s);
                s.Data = r.I32(); r.I32();   // data, skin
                if (r.Bool()) { r.S(); r.I32(); }   // shader
                if (type == "NiParticleSystem") { s.WorldSpace = r.Bool(); s.Modifiers = r.Refs(); }
                return s;
            }
            case "NiTriShapeData" or "NiTriStripsData" or "NiPSysData":
                return GeometryData(r, type, index);
            case "NiAlphaProperty":
            {
                var p = new Property { Type = type, Index = index }; ObjectNet(r, p); p.Flags = r.U16(); p.Threshold = r.U8(); return p;
            }
            case "NiMaterialProperty":
            {
                var p = new Property { Type = type, Index = index };
                ObjectNet(r, p);
                r.V(); p.Diffuse = r.V(); r.V();   // ambient, diffuse, specular
                p.Emissive = r.V(); p.Glossiness = r.F(); p.Alpha = r.F();
                return p;
            }
            case "NiTexturingProperty":
            {
                var p = new Property { Type = type, Index = index };
                ObjectNet(r, p);
                r.U32();   // apply mode
                uint count = r.U32();
                if (r.Bool()) TexDesc(r, p, true);
                for (int i = 0; i < 4; i++) if (r.Bool()) TexDesc(r, p, false);   // dark, detail, gloss, glow
                if (r.Bool()) { TexDesc(r, p, false); r.Skip(4 * 6); }            // bump: luma scale, offset, matrix
                if (r.Bool()) TexDesc(r, p, false);                                // decal 0
                for (uint i = 8; i <= 10; i++) if (count >= i && r.Bool()) TexDesc(r, p, false);
                for (uint i = r.U32(); i > 0; i--) if (r.Bool()) { TexDesc(r, p, false); r.U32(); }
                return p;
            }
            case "NiSourceTexture":
            {
                var p = new Property { Type = type, Index = index };
                ObjectNet(r, p);
                if (r.U8() != 0) { p.File = r.S(); r.I32(); }
                else { r.S(); r.I32(); }   // internal: file name, pixel data
                r.Skip(4 * 3 + 1 + 1);     // pixel layout, mipmaps, alpha format; static, direct render
                return p;
            }
            case "NiVertexColorProperty":
            {
                var p = new Property { Type = type, Index = index }; ObjectNet(r, p); p.Flags = r.U16(); r.Skip(8); return p;
            }
            case "NiZBufferProperty":
            {
                var p = new Property { Type = type, Index = index }; ObjectNet(r, p); p.Flags = r.U16(); r.U32(); return p;
            }
            case "NiStencilProperty":
            {
                var p = new Property { Type = type, Index = index }; ObjectNet(r, p); r.U8(); r.Skip(4 * 7); return p;
            }
            case "NiStringExtraData":
            {
                var d = new Block { Type = type, Index = index }; d.Name = r.S(); r.S(); return d;
            }
            case "BSXFlags":
            {
                var d = new Block { Type = type, Index = index }; d.Name = r.S(); r.U32(); return d;
            }
            case "NiBinaryExtraData":
            {
                var d = new Block { Type = type, Index = index }; d.Name = r.S(); r.Skip(r.I32()); return d;
            }
            case "NiTextKeyExtraData":
            {
                var d = new Block { Type = type, Index = index }; d.Name = r.S();
                for (uint n = r.U32(); n > 0; n--) { r.F(); r.S(); }
                return d;
            }
            case "NiStringPalette":
            {
                var d = new Block { Type = type, Index = index };
                d.Name = r.S();   // the palette: null-separated strings
                r.U32();
                return d;
            }
            case "NiDefaultAVObjectPalette":
            {
                r.I32();   // scene
                for (uint n = r.U32(); n > 0; n--) { r.S(); r.I32(); }
                return new Block { Type = type, Index = index };
            }
            case "NiControllerManager":
            {
                var c = new Controller { Type = type, Index = index };
                TimeController(r, c);
                r.Bool();   // cumulative
                c.Sequences = r.Refs();
                r.I32();    // object palette
                return c;
            }
            case "NiMultiTargetTransformController":
            {
                var c = new Controller { Type = type, Index = index };
                TimeController(r, c);
                if (Version is >= V10_1_0_104 and <= V10_1_0_108) r.Bool();
                r.Skip(4 * r.U16());
                return c;
            }
            case "NiTransformController" or "NiVisController" or "NiAlphaController" or "NiFlipController" or "NiTextureTransformController":
            {
                var c = new Controller { Type = type, Index = index };
                SingleInterp(r, c);
                return c;
            }
            case "NiMaterialColorController":
            {
                var c = new Controller { Type = type, Index = index };
                SingleInterp(r, c);
                r.U16();   // target colour
                return c;
            }
            case "NiPSysEmitterCtlr":
            {
                var c = new Controller { Type = type, Index = index };
                SingleInterp(r, c);
                c.ModifierName = r.S();
                c.VisInterpolator = r.I32();
                return c;
            }
            case "NiPSysEmitterSpeedCtlr" or "NiPSysGravityStrengthCtlr" or "NiPSysModifierActiveCtlr" or "NiPSysEmitterInitialRadiusCtlr"
                or "NiPSysEmitterLifeSpanCtlr" or "NiPSysEmitterDeclinationVarCtlr" or "NiPSysEmitterDeclinationCtlr" or "NiPSysEmitterPlanarAngleCtlr":
            {
                var c = new Controller { Type = type, Index = index };
                SingleInterp(r, c);
                c.ModifierName = r.S();
                return c;
            }
            case "NiPSysUpdateCtlr" or "NiPSysResetOnLoopCtlr":
            {
                var c = new Controller { Type = type, Index = index };
                TimeController(r, c);
                return c;
            }
            case "NiFloatInterpolator":
            {
                var i = new Interpolator { Type = type, Index = index }; i.Value = r.F(); i.Data = r.I32(); return i;
            }
            case "NiBoolInterpolator" or "NiBoolTimelineInterpolator":
            {
                var i = new Interpolator { Type = type, Index = index }; i.BoolValue = r.Bool(); i.Data = r.I32(); return i;
            }
            case "NiPoint3Interpolator":
            {
                var i = new Interpolator { Type = type, Index = index }; r.V(); i.Data = r.I32(); return i;
            }
            case "NiTransformInterpolator":
            {
                var i = new Interpolator { Type = type, Index = index };
                r.V(); r.V4(); r.F();   // translation, rotation, scale
                if (Version <= V10_1_0_110) r.Skip(3);   // which of them are valid
                i.Data = r.I32();
                return i;
            }
            case "NiPathInterpolator":
            {
                var i = new Interpolator { Type = type, Index = index };
                r.U16(); r.I32(); r.F(); r.F(); r.U16(); i.Data = r.I32(); r.I32();
                return i;
            }
            case "NiBlendFloatInterpolator":
                BlendInterpolator(r); r.F(); return new Interpolator { Type = type, Index = index };
            case "NiBlendBoolInterpolator":
                BlendInterpolator(r); r.U8(); return new Interpolator { Type = type, Index = index };
            case "NiBlendPoint3Interpolator":
                BlendInterpolator(r); r.V(); return new Interpolator { Type = type, Index = index };
            case "NiBlendTransformInterpolator":
                BlendInterpolator(r);
                if (Version <= V10_1_0_110) r.Skip(32 + 3);   // its value (translation, rotation, scale) and which are valid
                return new Interpolator { Type = type, Index = index };
            case "NiFloatData":
            {
                var d = new KeyData { Type = type, Index = index }; Keys<float>(r, d, 4, () => new Vector4(r.F(), 0, 0, 0)); return d;
            }
            case "NiBoolData":
            {
                var d = new KeyData { Type = type, Index = index }; Keys<byte>(r, d, 1, () => new Vector4(r.U8(), 0, 0, 0)); return d;
            }
            case "NiPosData":
            {
                var d = new KeyData { Type = type, Index = index }; Keys<V3>(r, d, 12, () => { var v = r.V(); return new Vector4(v, 0); }); return d;
            }
            case "NiColorData":
            {
                var d = new KeyData { Type = type, Index = index }; Keys<Vector4>(r, d, 16, () => r.V4()); return d;
            }
            case "NiTransformData" or "NiKeyframeData":
            {
                uint n = r.U32();
                if (n > 0)
                {
                    uint rot = r.U32();
                    if (rot != 4) r.Skip((int)n * (4 + 16 + (rot == 3 ? 12 : 0)));
                    else for (int k = 0; k < 3; k++) Keys<float>(r, new KeyData(), 4, () => new Vector4(r.F(), 0, 0, 0));
                }
                Keys<V3>(r, new KeyData(), 12, () => { var v = r.V(); return new Vector4(v, 0); });
                Keys<float>(r, new KeyData(), 4, () => new Vector4(r.F(), 0, 0, 0));
                return new Block { Type = type, Index = index };
            }
            case "NiControllerSequence":
            {
                var s = new Sequence { Type = type, Index = index };
                s.Name = r.S();
                uint count = r.U32();
                r.U32();   // array grow by
                var raw = new List<(int Interp, int Ctlr, string[] Names, uint[] Offsets)>();
                for (uint k = 0; k < count; k++)
                {
                    int interp = r.I32(), ctlr = r.I32();
                    if (Version <= V10_1_0_110) { r.I32(); r.U16(); }   // blend interpolator, blend index
                    r.U8();   // priority
                    if (Version <= V10_1_0_113) raw.Add((interp, ctlr, [r.S(), r.S(), r.S(), r.S(), r.S()], []));
                    else { r.I32(); raw.Add((interp, ctlr, [], [r.U32(), r.U32(), r.U32(), r.U32(), r.U32()])); }
                }
                r.F();                 // weight
                r.I32();               // text keys
                s.Cycle = r.U32(); s.Frequency = r.F();
                r.F();                 // phase
                s.Start = r.F(); s.Stop = r.F();
                if (Version == V10_1_0_106) r.Bool();   // play backwards
                r.I32();               // manager
                r.S();                 // accum root name
                s.Palette = Version >= V10_1_0_113 ? r.I32() : -1;
                foreach (var (interp, ctlr, names, offsets) in raw)
                    if (names.Length == 5) s.Links.Add((interp, ctlr, names[0], names[2], names[3], names[4]));
                    else s.Pending.Add((interp, ctlr, offsets));
                return s;
            }
            case "NiPSysAgeDeathModifier":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); r.Bool(); r.I32(); return m;
            }
            case "NiPSysSpawnModifier":
            {
                var m = new Modifier { Type = type, Index = index };
                ModifierBase(r, m);
                m.SpawnGenerations = r.U16(); m.PercentSpawned = r.F(); m.MinSpawn = r.U16(); m.MaxSpawn = r.U16();
                m.SpawnSpeedVar = r.F(); m.SpawnDirVar = r.F(); m.SpawnLife = r.F(); m.SpawnLifeVar = r.F();
                return m;
            }
            case "NiPSysGrowFadeModifier":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); m.GrowTime = r.F(); r.U16(); m.FadeTime = r.F(); r.U16(); return m;
            }
            case "NiPSysColorModifier":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); m.ColorData = r.I32(); return m;
            }
            case "NiPSysGravityModifier":
            {
                var m = new Modifier { Type = type, Index = index };
                ModifierBase(r, m);
                m.GravityObject = r.I32(); m.Axis = r.V(); m.Decay = r.F(); m.Strength = r.F(); m.ForceType = r.U32();
                if (V(V10_2_0_0)) { m.Turbulence = r.F(); m.TurbulenceScale = r.F(); }
                return m;
            }
            case "NiPSysPositionModifier":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); return m;
            }
            case "NiPSysBoundUpdateModifier":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); r.U16(); return m;
            }
            case "NiPSysRotationModifier":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); r.F(); r.Bool(); r.V(); return m;
            }
            case "NiPSysDragModifier":
            {
                var m = new Modifier { Type = type, Index = index };
                ModifierBase(r, m); r.I32(); m.Axis = r.V(); m.DragPercent = r.F(); m.DragRange = r.F(); m.DragFalloff = r.F();
                return m;
            }
            case "NiPSysColliderManager":
            {
                var m = new Modifier { Type = type, Index = index }; ModifierBase(r, m); m.ColorData = r.I32(); return m;   // its collider
            }
            case "NiPSysPlanarCollider":
            {
                var m = new Modifier { Type = type, Index = index };
                m.Bounce = r.F(); r.Bool(); m.DieOnCollide = r.Bool(); r.I32(); r.I32(); r.I32(); m.EmitterObject = r.I32();
                m.ColliderWidth = r.F(); m.ColliderHeight = r.F(); m.XAxis = r.V(); m.YAxis = r.V();
                return m;
            }
            case "NiPSysBoxEmitter":
            {
                var m = new Modifier { Type = type, Index = index }; Emitter(r, m); m.EmitterObject = r.I32(); m.Size = r.V(); return m;
            }
            case "NiPSysSphereEmitter":
            {
                var m = new Modifier { Type = type, Index = index }; Emitter(r, m); m.EmitterObject = r.I32(); m.Size = new V3(r.F(), 0, 0); return m;
            }
            case "NiPSysCylinderEmitter":
            {
                var m = new Modifier { Type = type, Index = index }; Emitter(r, m); m.EmitterObject = r.I32(); m.Size = new V3(r.F(), r.F(), 0); return m;
            }
            case "NiPSysMeshEmitter":
            {
                var m = new Modifier { Type = type, Index = index };
                Emitter(r, m);
                var meshes = r.Refs();
                m.EmitterObject = meshes.FirstOrDefault(-1);
                r.U32(); r.U32(); m.Axis = r.V();   // velocity type, emission type, axis
                return m;
            }
            case "bhkCollisionObject":
                r.I32(); r.U16(); r.I32();   // target, flags, body
                return new Block { Type = type, Index = index };
            case "bhkRigidBody" or "bhkRigidBodyT":
                // World object (shape, filter, info), entity info, Oblivion's rigid body info (196 bytes), constraints,
                // body flags. Collision is rebuilt from the visible geometry.
                r.Skip(4 + 4 + 20 + 4 + 196);
                r.Skip(4 * r.I32());
                r.U32();
                return new Block { Type = type, Index = index };
            case "bhkBoxShape":
                r.Skip(4 + 4 + 8 + 12 + 4);
                return new Block { Type = type, Index = index };
            case "bhkSphereShape":
                r.Skip(8);
                return new Block { Type = type, Index = index };
            case "bhkCapsuleShape":
                r.Skip(8 + 8 + 16 + 16);
                return new Block { Type = type, Index = index };
            case "bhkConvexVerticesShape":
                r.Skip(8 + 12 + 12);
                r.Skip(16 * r.I32());
                r.Skip(16 * r.I32());
                return new Block { Type = type, Index = index };
            default:
                throw new InvalidDataException($"block type {type} isn't read");
        }
    }

    /// <summary>
    /// The file cut after its first <paramref name="keep"/> blocks (the block count and type index shortened, the
    /// first block the root): for finding the block nifly fails on (ob-nif10-bisect).
    /// </summary>
    public byte[] Truncated(byte[] file, int keep)
    {
        int countAt = Array.IndexOf(file, (byte)10) + 1 + 4 + 4;
        int blocksEnd = keep < Starts.Count ? Starts[keep] : BlocksEnd;
        var output = new List<byte>();
        output.AddRange(file.AsSpan(0, countAt).ToArray());
        output.AddRange(BitConverter.GetBytes(keep));
        output.AddRange(file.AsSpan(countAt + 4, indexAt + 2 * keep - countAt - 4).ToArray());
        int afterIndex = indexAt + 2 * Starts.Count;
        output.AddRange(file.AsSpan(afterIndex, blocksEnd - afterIndex).ToArray());
        output.AddRange(BitConverter.GetBytes(1));
        output.AddRange(BitConverter.GetBytes(0));
        return output.ToArray();
    }

    string PaletteString(int palette, uint offset)
    {
        if (Get<Block>(palette) is not { Type: "NiStringPalette" } p || offset >= p.Name.Length) return "";
        int end = p.Name.IndexOf('\0', (int)offset);
        return p.Name[(int)offset..(end < 0 ? p.Name.Length : end)];
    }

    GeomData GeometryData(Reader r, string type, int index)
    {
        var d = new GeomData { Type = type, Index = index };
        if (V(V10_1_0_114)) r.I32();   // group ID
        int nv = r.U16();
        r.U8(); r.U8();                 // keep, compress flags
        if (r.Bool()) for (int i = 0; i < nv; i++) d.Verts.Add(r.V());
        ushort flags = r.U16();
        if (r.Bool())
        {
            for (int i = 0; i < nv; i++) d.Normals.Add(r.V());
            if ((flags & 0x1000) != 0) r.Skip(nv * 24);   // tangents, bitangents
        }
        r.Skip(16);                     // centre, radius
        if (r.Bool()) for (int i = 0; i < nv; i++) d.Colors.Add(r.V4());
        for (int set = 0; set < (flags & 63); set++)
            for (int i = 0; i < nv; i++) { var uv = new Vector2(r.F(), r.F()); if (set == 0) d.Uvs.Add(uv); }
        r.U16();                        // consistency
        switch (type)
        {
            case "NiTriShapeData":
            {
                int nt = r.U16();
                r.U32();                // points
                if (r.Bool()) for (int i = 0; i < nt; i++) d.Tris.Add((r.U16(), r.U16(), r.U16()));
                for (int g = r.U16(); g > 0; g--) r.Skip(2 * r.U16());   // match groups
                break;
            }
            case "NiTriStripsData":
            {
                r.U16();                // triangles
                var lens = Enumerable.Range(0, r.U16()).Select(_ => (int)r.U16()).ToList();
                if (r.Bool())
                    foreach (int n in lens)
                    {
                        var st = Enumerable.Range(0, n).Select(_ => r.U16()).ToList();
                        for (int i = 0; i + 2 < st.Count; i++)
                            if (st[i] != st[i + 1] && st[i + 1] != st[i + 2] && st[i] != st[i + 2])
                                d.Tris.Add(i % 2 == 0 ? (st[i], st[i + 1], st[i + 2]) : (st[i], st[i + 2], st[i + 1]));
                    }
                break;
            }
            case "NiPSysData":
            {
                d.ParticleCount = nv;
                if (r.Bool()) r.Skip(4 * nv);   // radii
                r.U16();                        // active
                if (r.Bool()) r.Skip(4 * nv);   // sizes
                if (r.Bool()) r.Skip(16 * nv);  // rotations
                r.Skip(nv * (12 + 12 + 4 + 4 + 4 + 2 + 2));   // particle info: velocity, rotation axis, age, life span, last update, generation, code
                r.U16(); r.U16();               // added particles, base
                break;
            }
        }
        return d;
    }
}
