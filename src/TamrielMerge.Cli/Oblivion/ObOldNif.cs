using System.Numerics;
using System.Text;
using V3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Models in NetImmerse 10.0.1.2, older than the rest of Oblivion's (user, 2026-10-11: "do the grass" - every grass
/// model Oblivion's land grows, Plants\GroundCover*.NIF, is one, and nifly can't read the version, so converted Cyrodiil
/// grew no grass). Reads the blocks those use - nodes, shapes, texturing, source texture, alpha, vertex colour,
/// material and stencil properties, shape and strip data - into pieces for the usual writer; null for anything else.
/// This version writes a zero word before every block and has no "has triangles" flag in its shape data.
/// </summary>
public sealed partial class ObMeshConverter
{
    const string OldHeader = "NetImmerse File Format, Version 10.0.1.2\n";

    static bool IsOldNif(byte[] b) => b.Length > OldHeader.Length && Encoding.ASCII.GetString(b, 0, OldHeader.Length) == OldHeader;

    sealed class OldReader(byte[] b)
    {
        public int Pos;
        public byte U8() => b[Pos++];
        public ushort U16() { var v = BitConverter.ToUInt16(b, Pos); Pos += 2; return v; }
        public uint U32() { var v = BitConverter.ToUInt32(b, Pos); Pos += 4; return v; }
        public int I32() { var v = BitConverter.ToInt32(b, Pos); Pos += 4; return v; }
        public float F() { var v = BitConverter.ToSingle(b, Pos); Pos += 4; return v; }
        public V3 V() => new(F(), F(), F());
        public string S() { int n = (int)U32(); var v = Encoding.Latin1.GetString(b, Pos, n); Pos += n; return v; }
        public void ShortS() { int n = U8(); Pos += n; }
        public void Skip(int n) => Pos += n;
    }

    sealed class OldBlock
    {
        public string Type = "", Name = "";
        public ushort Flags;
        public V3 T; public Matrix4x4 R = Matrix4x4.Identity; public float Scale = 1;
        public List<int> Props = [], Children = [];
        public int Data = -1, Base = -1;
        public string? File;
        public byte Threshold;
        public float Glossiness = 10, Alpha = 1;
        public V3 Emissive;
        public uint DrawMode;
        public List<V3> Verts = [], Normals = [];
        public List<Vector4> Colors = [];
        public List<Vector2> Uvs = [];
        public List<(ushort, ushort, ushort)> Tris = [];
    }

    /// <summary>The pieces of a 10.0.1.2 model, or null when it uses a block this doesn't read.</summary>
    static List<Piece>? ReadOldNif(byte[] bytes)
    {
        var r = new OldReader(bytes) { Pos = OldHeader.Length };
        r.U32();                                   // version
        int count = (int)r.U32();
        r.U32();                                   // unknown
        for (int i = 0; i < 3; i++) r.ShortS();   // export info: creator, two lines
        var types = Enumerable.Range(0, r.U16()).Select(_ => r.S()).ToList();
        var index = Enumerable.Range(0, count).Select(_ => (int)r.U16()).ToList();
        r.U32();                                   // groups
        var blocks = new List<OldBlock>();
        void ObjectNet(OldBlock d) { d.Name = r.S(); r.Skip(4 * (int)r.U32()); r.I32(); }
        void AvObject(OldBlock d)
        {
            ObjectNet(d);
            d.Flags = r.U16();
            d.T = r.V();
            var m = new float[9];
            for (int i = 0; i < 9; i++) m[i] = r.F();
            d.R = new Matrix4x4(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, 0, 0, 0, 1);
            d.Scale = r.F();
            d.Props = Enumerable.Range(0, (int)r.U32()).Select(_ => r.I32()).ToList();
            r.I32();                               // collision
        }
        int TexDesc() { int src = r.I32(); r.Skip(4 * 3 + 2 * 2); return src; }   // clamp, filter, UV set; PS2 L, K
        for (int k = 0; k < count; k++)
        {
            var d = new OldBlock { Type = types[index[k]] };
            r.U32();                               // the zero word before each block
            switch (d.Type)
            {
                case "NiNode":
                    AvObject(d);
                    d.Children = Enumerable.Range(0, (int)r.U32()).Select(_ => r.I32()).ToList();
                    r.Skip(4 * (int)r.U32());      // effects
                    break;
                case "NiTriShape" or "NiTriStrips":
                    AvObject(d);
                    d.Data = r.I32(); r.I32();     // data, skin
                    if (r.U8() != 0) { r.S(); r.I32(); }   // shader
                    break;
                case "NiTexturingProperty":
                {
                    ObjectNet(d); r.U16(); r.U32();
                    uint texCount = r.U32();
                    if (r.U8() != 0) d.Base = TexDesc();
                    for (int i = 0; i < 4; i++) if (r.U8() != 0) TexDesc();          // dark, detail, gloss, glow
                    if (r.U8() != 0) { TexDesc(); r.Skip(4 * 6); }                    // bump: luma scale, offset, matrix
                    if (r.U8() != 0) TexDesc();                                       // decal 0
                    for (uint i = 8; i <= 10; i++) if (texCount >= i && r.U8() != 0) TexDesc();
                    for (uint i = r.U32(); i > 0; i--) if (r.U8() != 0) { TexDesc(); r.U32(); }   // shader textures
                    break;
                }
                case "NiSourceTexture":
                    ObjectNet(d);
                    if (r.U8() != 0) d.File = r.S();
                    else { r.U8(); r.I32(); }
                    r.Skip(4 * 3 + 1);             // pixel layout, mipmaps, alpha format; static
                    break;
                case "NiAlphaProperty":
                    ObjectNet(d); d.Flags = r.U16(); d.Threshold = r.U8();
                    break;
                case "NiVertexColorProperty":
                    ObjectNet(d); r.U16(); r.Skip(8);
                    break;
                case "NiMaterialProperty":
                    ObjectNet(d); r.U16(); r.Skip(4 * 9);   // ambient, diffuse, specular
                    d.Emissive = r.V(); d.Glossiness = r.F(); d.Alpha = r.F();
                    break;
                case "NiStencilProperty":
                    ObjectNet(d); r.U16(); r.U8(); r.Skip(4 * 6);   // function, ref, mask, fail, z fail, pass
                    d.DrawMode = r.U32();
                    break;
                case "NiTriShapeData" or "NiTriStripsData":
                {
                    int nv = r.U16();
                    if (r.U8() != 0) for (int i = 0; i < nv; i++) d.Verts.Add(r.V());
                    ushort flags = r.U16();
                    if (r.U8() != 0) for (int i = 0; i < nv; i++) d.Normals.Add(r.V());
                    r.Skip(16);                    // centre, radius
                    if (r.U8() != 0) for (int i = 0; i < nv; i++) d.Colors.Add(new Vector4(r.F(), r.F(), r.F(), r.F()));
                    for (int set = 0; set < (flags & 63); set++)
                        for (int i = 0; i < nv; i++) { var uv = new Vector2(r.F(), r.F()); if (set == 0) d.Uvs.Add(uv); }
                    r.U16();                       // consistency
                    int nt = r.U16();
                    if (d.Type == "NiTriShapeData")
                    {
                        r.U32();                   // points
                        for (int i = 0; i < nt; i++) d.Tris.Add((r.U16(), r.U16(), r.U16()));
                        for (int g = r.U16(); g > 0; g--) r.Skip(2 * r.U16());   // match groups
                    }
                    else
                    {
                        var lens = Enumerable.Range(0, r.U16()).Select(_ => (int)r.U16()).ToList();
                        foreach (int n in lens)
                        {
                            var st = Enumerable.Range(0, n).Select(_ => r.U16()).ToList();
                            for (int i = 0; i + 2 < st.Count; i++)
                                if (st[i] != st[i + 1] && st[i + 1] != st[i + 2] && st[i] != st[i + 2])
                                    d.Tris.Add(i % 2 == 0 ? (st[i], st[i + 1], st[i + 2]) : (st[i], st[i + 2], st[i + 1]));
                        }
                    }
                    break;
                }
                default:
                    return null;
            }
            blocks.Add(d);
        }

        // Each shape placed by its own transform and its parents' (a node holds its children).
        var parent = new Dictionary<int, int>();
        for (int i = 0; i < blocks.Count; i++)
            foreach (int c in blocks[i].Children) if (c >= 0) parent[c] = i;
        var pieces = new List<Piece>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var s = blocks[i];
            if (s.Type is not ("NiTriShape" or "NiTriStrips") || (s.Flags & 1) != 0 || s.Data < 0) continue;
            var chain = new List<OldBlock> { s };
            for (int p = i; parent.TryGetValue(p, out int up); p = up) chain.Add(blocks[up]);
            if (chain.Any(c => (c.Flags & 1) != 0 || c.Name.Contains("EditorMarker", StringComparison.OrdinalIgnoreCase))) continue;
            V3 Place(V3 v) { foreach (var c in chain) v = V3.Transform(v * c.Scale, Matrix4x4.Transpose(c.R)) + c.T; return v; }
            V3 Turn(V3 v) { foreach (var c in chain) v = V3.TransformNormal(v, Matrix4x4.Transpose(c.R)); return v.LengthSquared() > 0 ? V3.Normalize(v) : V3.UnitZ; }
            var data = blocks[s.Data];
            if (data.Verts.Count == 0 || data.Tris.Count == 0) continue;
            var props = s.Props.Where(p => p >= 0 && p < blocks.Count).Select(p => blocks[p]).ToList();
            var texturing = props.FirstOrDefault(p => p.Type == "NiTexturingProperty");
            var alpha = props.FirstOrDefault(p => p.Type == "NiAlphaProperty");
            var material = props.FirstOrDefault(p => p.Type == "NiMaterialProperty");
            var piece = new Piece
            {
                Name = s.Name.Length > 0 ? s.Name : $"Shape{pieces.Count}",
                Verts = data.Verts.Select(Place).ToList(),
                Normals = data.Normals.Count == data.Verts.Count ? data.Normals.Select(Turn).ToList() : [],
                Uvs = data.Uvs.Count == data.Verts.Count ? data.Uvs : Enumerable.Repeat(Vector2.Zero, data.Verts.Count).ToList(),
                Colors = data.Colors,
                Tris = data.Tris,
                Texture = texturing is { Base: >= 0 } t && t.Base < blocks.Count ? blocks[t.Base].File : null,
                // Oblivion's stencil property here only says to draw both sides.
                TwoSided = props.Any(p => p.Type == "NiStencilProperty"),
            };
            if (alpha is not null) { piece.AlphaFlags = alpha.Flags; piece.AlphaThreshold = alpha.Threshold; }
            if (material is not null) { piece.Glossiness = material.Glossiness; piece.Alpha = material.Alpha; piece.Emissive = material.Emissive; }
            pieces.Add(piece);
        }
        return pieces;
    }
}
