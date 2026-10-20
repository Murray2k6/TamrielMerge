using nifly;
using V3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Skyrim SE's own collision, read from a model (user, 2026-10-20: "make a collision reader"): what the game's physics
/// stands on, as triangles in the model's space. Every solid collision object (bhkCollisionObject) on its node: its
/// rigid body (bhkRigidBodyT's translation and rotation too), its shape tree walked through MOPP trees, lists and
/// (convex) transform shapes down to compressed meshes (bhkCompressedMeshShapeData: its big triangles, and its chunks:
/// quantized vertices x the data's error from the chunk's translation, through the chunk's transform, read as triangle
/// strips then plain triangles), convex hulls (their faces, from the hull's own planes), boxes, spheres and capsules (as
/// boxes). Havok units are Skyrim units / 69.99125. Shapes it can't read are counted, never guessed at.
/// </summary>
public static class CollisionReader
{
    /// <summary>Skyrim SE's Havok scale: game units per Havok unit.</summary>
    public const float HavokScale = 69.99125f;

    public sealed class Result
    {
        /// <summary>The triangles, in the model's space, with the collision layer of the body they are of.</summary>
        public List<(V3[] Tri, uint Layer)> Triangles { get; } = [];
        /// <summary>Shape kinds found and not read (by block type).</summary>
        public Dictionary<string, int> Unread { get; } = [];
    }

    public static Result Read(string path) => Read(File.ReadAllBytes(path));

    public static Result Read(byte[] bytes)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_collision_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var nif = new NifFile();
            if (nif.Load(tmp) != 0) return new Result();
            return Read(nif, Blocks(bytes));
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>Each block's own bytes, by block index (null if the header can't be read).</summary>
    public static ArraySegment<byte>[]? Blocks(byte[] b)
    {
        try
        {
            int p = Array.IndexOf(b, (byte)'\n') + 1;
            uint U32() { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; }
            ushort U16() { ushort v = BitConverter.ToUInt16(b, p); p += 2; return v; }
            void Short() => p += 1 + b[p];
            void Long() => p += 4 + (int)BitConverter.ToUInt32(b, p);
            U32(); p++; U32();                   // version, endian, user version
            uint n = U32();
            uint bs = U32();
            Short();                             // author
            if (bs > 130) U32();
            Short(); Short();                    // process script, export script
            if (bs == 130) Short();
            int types = U16();
            for (int i = 0; i < types; i++) Long();
            p += 2 * (int)n;                     // block type indices
            var sizes = new uint[n];
            for (int i = 0; i < n; i++) sizes[i] = U32();
            uint strings = U32(); U32();
            for (int i = 0; i < strings; i++) Long();
            uint groups = U32();
            p += 4 * (int)groups;
            var blocks = new ArraySegment<byte>[n];
            for (int i = 0; i < n; i++)
            {
                if (p + sizes[i] > b.Length) return null;
                blocks[i] = new ArraySegment<byte>(b, p, (int)sizes[i]);
                p += (int)sizes[i];
            }
            return blocks;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static Result Read(NifFile nif, ArraySegment<byte>[]? raw)
    {
        var result = new Result();
        var hdr = nif.GetHeader();
        uint count = hdr.GetNumBlocks();
        for (uint i = 0; i < count; i++)
        {
            string type = hdr.GetBlockTypeStringById(i);
            if (type is not ("bhkCollisionObject" or "bhkBlendCollisionObject")) continue;
            if (hdr.GetBlockById(i) is not bhkNiCollisionObject col) continue;
            if (hdr.GetBlockById(col.bodyRef.index) is not bhkRigidBody body)
            {
                Count(result, hdr.GetBlockTypeStringById(col.bodyRef.index));
                continue;
            }
            bool hasT = hdr.GetBlockTypeStringById(col.bodyRef.index) == "bhkRigidBodyT";
            var q = new Quat(body.rotation.x, body.rotation.y, body.rotation.z, body.rotation.w);
            if (q.LengthSquared() < 1e-6f) q = Quat.Identity;
            var offset = new V3(body.translation.x, body.translation.y, body.translation.z) * HavokScale;
            uint target = col.targetRef.index;
            uint layer = body.collisionFilter.layer;
            V3 ToModel(V3 p) => NodeToModel(nif, target, hasT ? V3.Transform(p, q) + offset : p);
            Shape(nif, hdr, raw, body.shapeRef.index, ToModel, layer, result);
        }
        return result;
    }

    /// <summary>A point in a node's space into the model's (through the node and each parent).</summary>
    static V3 NodeToModel(NifFile nif, uint node, V3 p)
    {
        var g = new nifly.Vector3(p.X, p.Y, p.Z);
        for (NiNode? n = nif.GetHeader().GetBlockById(node) as NiNode; n is not null; n = nif.GetParentNode(n)) g = n.transform.ApplyTransform(g);
        return new V3(g.x, g.y, g.z);
    }

    /// <summary>
    /// A Havok transform (bhkTransformShape's, a phantom's) as it is stored: its four rows are the rotation's columns
    /// and then the translation, in Havok units. (nifly's own Matrix4 * vector leaves that translation out.)
    /// </summary>
    public static System.Numerics.Matrix4x4 HavokMatrix(Matrix4 m)
    {
        var rows = new V3[4];
        for (int r = 0; r < 4; r++) { var v = new nifly.Vector3(); m.GetRow(r, v); rows[r] = new V3(v.x, v.y, v.z); }
        return new(rows[0].X, rows[0].Y, rows[0].Z, 0, rows[1].X, rows[1].Y, rows[1].Z, 0, rows[2].X, rows[2].Y, rows[2].Z, 0, rows[3].X, rows[3].Y, rows[3].Z, 1);
    }

    /// <summary>A point in game units through a Havok transform (<paramref name="scale"/>: game units per Havok unit).</summary>
    public static V3 HavokTransform(System.Numerics.Matrix4x4 m, V3 p, float scale) => V3.Transform(p / scale, m) * scale;

    static void Count(Result r, string type) => r.Unread[type] = r.Unread.GetValueOrDefault(type) + 1;

    static void Shape(NifFile nif, NiHeader hdr, ArraySegment<byte>[]? raw, uint id, Func<V3, V3> xf, uint layer, Result result)
    {
        if (id == 0xFFFFFFFF || id >= hdr.GetNumBlocks()) return;
        string type = hdr.GetBlockTypeStringById(id);
        var block = hdr.GetBlockById(id);
        var output = result.Triangles;
        void Box(V3 lo, V3 hi)
        {
            var c = new V3[8];
            for (int k = 0; k < 8; k++) c[k] = xf(new V3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z));
            int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
            foreach (var f in faces) { output.Add(([c[f[0]], c[f[1]], c[f[2]]], layer)); output.Add(([c[f[0]], c[f[2]], c[f[3]]], layer)); }
        }
        switch (type)
        {
            case "bhkMoppBvTreeShape":
                Shape(nif, hdr, raw, ((bhkMoppBvTreeShape)block).shapeRef.index, xf, layer, result);
                return;
            case "bhkListShape":
            {
                var list = (bhkListShape)block;
                for (uint k = 0; k < list.subShapeRefs.GetSize(); k++) Shape(nif, hdr, raw, list.subShapeRefs.GetBlockRef(k), xf, layer, result);
                return;
            }
            case "bhkTransformShape" or "bhkConvexTransformShape":
            {
                var t = (bhkTransformShape)block;
                var m = HavokMatrix(t.xform);
                Shape(nif, hdr, raw, t.shapeRef.index, p => xf(HavokTransform(m, p, HavokScale)), layer, result);
                return;
            }
            case "bhkBoxShape":
            {
                var b = (bhkBoxShape)block;
                var d = new V3(b.dimensions.x, b.dimensions.y, b.dimensions.z) * HavokScale;
                Box(-d, d);
                return;
            }
            case "bhkSphereShape":
            {
                float r = ((bhkConvexShape)block).radius * HavokScale;
                Box(new V3(-r), new V3(r));
                return;
            }
            case "bhkCapsuleShape":
            {
                var c = (bhkCapsuleShape)block;
                var p1 = new V3(c.point1.x, c.point1.y, c.point1.z) * HavokScale; var p2 = new V3(c.point2.x, c.point2.y, c.point2.z) * HavokScale;
                float r = c.radius1 * HavokScale;
                Box(V3.Min(p1, p2) - new V3(r), V3.Max(p1, p2) + new V3(r));
                return;
            }
            case "bhkConvexVerticesShape":
            {
                var cv = (bhkConvexVerticesShape)block;
                var pts = cv.verts.items().Select(v => new V3(v.x, v.y, v.z)).ToList();
                var planes = cv.normals.items().Select(n => (N: new V3(n.x, n.y, n.z), D: n.w)).ToList();
                // Each face: the hull's points on its plane (n.p + w = 0), in order around their middle.
                foreach (var (n, d) in planes)
                {
                    if (n.LengthSquared() < 1e-8f) continue;
                    var on = pts.Where(p => MathF.Abs(V3.Dot(n, p) + d) < 1e-3f).ToList();
                    if (on.Count < 3) continue;
                    var mid = on.Aggregate(V3.Zero, (a, b) => a + b) / on.Count;
                    var u = V3.Normalize(on[0] - mid);
                    var w = V3.Cross(V3.Normalize(n), u);
                    var ring = on.OrderBy(p => MathF.Atan2(V3.Dot(p - mid, w), V3.Dot(p - mid, u))).Select(p => xf(p * HavokScale)).ToList();
                    for (int k = 1; k + 1 < ring.Count; k++) output.Add(([ring[0], ring[k], ring[k + 1]], layer));
                }
                return;
            }
            case "bhkNiTriStripsShape":
            {
                // Triangle strips on NiTriStripsData parts, in the model's own units, by the shape's scale.
                var s = (bhkNiTriStripsShape)block;
                var sc = new V3(s.scale.x == 0 ? 1 : s.scale.x, s.scale.y == 0 ? 1 : s.scale.y, s.scale.z == 0 ? 1 : s.scale.z);
                for (uint k = 0; k < s.partRefs.GetSize(); k++)
                {
                    uint part = s.partRefs.GetBlockRef(k);
                    if (part >= hdr.GetNumBlocks() || hdr.GetBlockById(part) is not NiTriStripsData data) { Count(result, "NiTriStripsData (missing)"); continue; }
                    var verts = data.vertices.Select(v => xf(new V3(v.x * sc.X, v.y * sc.Y, v.z * sc.Z))).ToList();
                    using var tris = new vectorTriangle();
                    data.GetTriangles(tris);
                    foreach (var t in tris)
                        if (t.p1 < verts.Count && t.p2 < verts.Count && t.p3 < verts.Count) output.Add(([verts[t.p1], verts[t.p2], verts[t.p3]], layer));
                }
                return;
            }
            case "bhkCompressedMeshShape":
                Shape(nif, hdr, raw, ((bhkCompressedMeshShape)block).dataRef.index, xf, layer, result);
                return;
            case "bhkCompressedMeshShapeData":
                // niflysharp gives no access to this block's chunk and big-triangle lists, so it is read from the file's
                // own bytes (the layout tools/ingame/cmsd.py reads Skyrim's stairs with).
                if (raw is not null && id < raw.Length) CompressedMesh(raw[id], xf, layer, output);
                else Count(result, type);
                return;
            default:
                Count(result, type);
                return;
        }
    }

    /// <summary>
    /// A bhkCompressedMeshShapeData block's triangles (Skyrim's NIF layout): its big triangles on their own vertices, and
    /// each chunk's: its quantized vertices x the data's error from the chunk's translation, turned by its transform's
    /// rotation and moved by its translation, then its triangle strips and, after them, its plain triangles.
    /// </summary>
    static void CompressedMesh(ArraySegment<byte> block, Func<V3, V3> xf, uint layer, List<(V3[] Tri, uint Layer)> output)
    {
        var b = block.Array!;
        int p = block.Offset;
        uint U32() { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; }
        ushort U16() { ushort v = BitConverter.ToUInt16(b, p); p += 2; return v; }
        float F() { float v = BitConverter.ToSingle(b, p); p += 4; return v; }
        (float X, float Y, float Z, float W) V4() => (F(), F(), F(), F());
        V3 Hk(float x, float y, float z) => xf(new V3(x, y, z) * HavokScale);
        void Skip(int size) { int n = (int)U32(); p += size * n; }   // a counted list (p moves past the count first)
        U32(); U32(); U32(); U32();              // bits per index, per W index, mask W index, mask index
        float error = F();
        V4(); V4();                              // bounds
        p += 2;                                  // welding type, material type
        for (int k = 0; k < 3; k++) Skip(4);   // materials 32, 16, 8
        Skip(8);                                 // chunk materials
        U32();                                   // named materials
        int nt = (int)U32();
        var transforms = new (V3 T, Quat R)[nt];
        for (int k = 0; k < nt; k++)
        {
            var t = V4(); var r = V4();
            var q = new Quat(r.X, r.Y, r.Z, r.W);
            transforms[k] = (new V3(t.X, t.Y, t.Z), q.LengthSquared() < 1e-6f ? Quat.Identity : q);
        }
        int nb = (int)U32();
        var big = new V3[nb];
        for (int k = 0; k < nb; k++) { var v = V4(); big[k] = Hk(v.X, v.Y, v.Z); }
        int nbt = (int)U32();
        for (int k = 0; k < nbt; k++)
        {
            int a = U16(), c1 = U16(), c2 = U16(); U32(); U16();
            if (a < nb && c1 < nb && c2 < nb) output.Add(([big[a], big[c1], big[c2]], layer));
        }
        int nc = (int)U32();
        for (int k = 0; k < nc; k++)
        {
            var t = V4(); U32(); U16(); int ti = U16();
            var (move, rot) = ti < nt ? transforms[ti] : (V3.Zero, Quat.Identity);
            int nv = (int)U32();
            var verts = new V3[nv / 3];
            for (int i = 0; i < nv / 3; i++)
            {
                var local = new V3(t.X + U16() * error, t.Y + U16() * error, t.Z + U16() * error);
                var w = V3.Transform(local, rot) + move;
                verts[i] = Hk(w.X, w.Y, w.Z);
            }
            p += 2 * (nv % 3);
            int ni = (int)U32();
            var idx = new int[ni];
            for (int i = 0; i < ni; i++) idx[i] = U16();
            int ns = (int)U32();
            var strips = new int[ns];
            for (int i = 0; i < ns; i++) strips[i] = U16();
            Skip(2);                             // welding
            void Tri(int x, int y, int z)
            {
                if (x < verts.Length && y < verts.Length && z < verts.Length && x != y && y != z && x != z) output.Add(([verts[x], verts[y], verts[z]], layer));
            }
            int at = 0;
            // The strips (every other triangle turned so they all wind the same way), then plain triangles.
            foreach (int length in strips)
            {
                for (int i = 0; i + 2 < length && at + i + 2 < ni; i++)
                {
                    if (i % 2 == 0) Tri(idx[at + i], idx[at + i + 1], idx[at + i + 2]);
                    else Tri(idx[at + i], idx[at + i + 2], idx[at + i + 1]);
                }
                at += length;
            }
            for (; at + 2 < ni; at += 3) Tri(idx[at], idx[at + 1], idx[at + 2]);
        }
    }
}
