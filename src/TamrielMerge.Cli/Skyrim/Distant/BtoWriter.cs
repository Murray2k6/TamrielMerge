using System.Text;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Distant;

/// <summary>
/// One distant-object chunk (meshes\terrain\&lt;world&gt;\objects\&lt;world&gt;.&lt;level&gt;.&lt;x&gt;.&lt;y&gt;.bto) as Skyrim's
/// own (tamriel.4.0.0.bto, tamriel.8.0.0.bto, read 2026-10-13): a NiNode "Obj" over a BSMultiBoundNode with its AABB,
/// holding BSSubIndexTriShapes named "Obj" in the chunk's local units (0-4096 across, scaled by the level; heights too),
/// shaded by the distant-object shader (flags 0x80400000 / 0x5: z-test and write, LOD objects) with the sheet and its
/// normals. Each level-4 shape's triangles are in runs, one per cell of the chunk (segment (x - x0) * 4 + (y - y0),
/// the cells' own references): the game hides a cell's run when the cell itself is loaded. Higher levels have one run.
/// nifly writes no sub-index segments (its BSSubIndexTriShape loses its geometry on save, Skyrim/TerrainLod.cs), so the
/// chunk is saved with plain BSTriShapes and its file then patched: the type renamed and the segments appended to each
/// shape (Skyrim SE's form: a count, then per segment a flags byte, its first index and its triangle count).
/// </summary>
public static class BtoWriter
{
    public class Shape
    {
        public int Page;
        public bool Cutout;
        public readonly List<Vector3> P = [];
        public readonly List<Vector3> N = [];
        public readonly List<Vector2> UV = [];
        public readonly List<int> T = [];
        /// <summary>Per triangle, its run (cell segment).</summary>
        public readonly List<int> Segment = [];
    }

    static readonly object NifLock = new();

    public static void Write(string path, string world, int level, IReadOnlyList<Shape> shapes)
    {
        int segments = level == 4 ? 16 : 1;
        var pieces = new List<(Shape Shape, List<(int Start, int Count)> Runs)>();
        lock (NifLock)
        {
            using var nif = new NifFile();
            nif.Create(NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var root = nif.GetRootNode();
            root.name = new NiStringRef("Obj");
            root.flags = 14;
            float zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var s in shapes) foreach (var p in s.P) { zMin = MathF.Min(zMin, p.Z * level); zMax = MathF.Max(zMax, p.Z * level); }
            var bound = MultiBound(hdr, level * 4096f, zMin, zMax);
            uint boundId = hdr.AddBlock(bound);
            var boundNode = (NiNode)hdr.GetBlockById(boundId);
            nif.SetParentNode(boundNode, root);

            foreach (var s in shapes)
                foreach (var piece in Split(s, segments))
                {
                    using var verts = new vectorVector3();
                    using var normals = new vectorVector3();
                    using var uvs = new vectorVector2();
                    using var tris = new vectorTriangle();
                    foreach (var p in piece.P) verts.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                    foreach (var n in piece.N) normals.Add(new nifly.Vector3(n.X, n.Y, n.Z));
                    foreach (var u in piece.UV) uvs.Add(new nifly.Vector2(u.X, u.Y));
                    for (int t = 0; t < piece.T.Count; t += 3) tris.Add(new Triangle((ushort)piece.T[t], (ushort)piece.T[t + 1], (ushort)piece.T[t + 2]));
                    var shape = nif.CreateShapeFromData("Obj", verts, tris, uvs, normals);
                    nif.SetParentNode(shape, boundNode);
                    nif.SetTextureSlot(shape, DistantAtlas.PagePath(world, piece.Page), 0);
                    nif.SetTextureSlot(shape, DistantAtlas.NormalPath(world), 1);
                    nif.CalcTangentsForShape(shape);
                    if (piece.Cutout) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x12EC, threshold = 128 }));
                    BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
                    {
                        sh.shaderFlags1 = 0x80400000u;
                        sh.shaderFlags2 = piece.Cutout ? 0x00000015u : 0x00000005u;   // + double-sided for the trees' cards
                        sh.glossiness = 1;
                        sh.specularStrength = 1;
                    });
                    // Last: replacing the shape block (its scale, the level) after everything that looks it up.
                    BlockMeshBuilder.EditBlock<BSTriShape>(nif, hdr.GetBlockID(shape), t =>
                    {
                        var tr = t.transform; tr.scale = level; t.transform = tr;
                        t.flags = 14;
                    });
                    pieces.Add((piece, piece.Runs));
                }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (nif.Save(path) != 0) throw new IOException($"nifly failed to save {path}");
        }
        Patch(path, pieces.Select(p => p.Runs).ToList());
    }

    sealed class Piece : Shape { public readonly List<(int Start, int Count)> Runs = []; }

    /// <summary>A shape cut to fit 16-bit indices, each piece's triangles in segment order, with its runs.</summary>
    static IEnumerable<Piece> Split(Shape s, int segments)
    {
        var order = Enumerable.Range(0, s.T.Count / 3).OrderBy(t => s.Segment[t]).ToList();
        Piece? cur = null;
        Dictionary<int, int> remap = new();
        var counts = new int[segments];
        Piece Finish(Piece p)
        {
            int start = 0;
            int last = Array.FindLastIndex(counts, c => c > 0);
            for (int g = 0; g <= last; g++) { p.Runs.Add((counts[g] > 0 ? start : 0, counts[g])); start += counts[g] * 3; }
            Array.Clear(counts);
            return p;
        }
        foreach (int t in order)
        {
            if (cur is null || cur.P.Count > 65000)
            {
                if (cur is not null) yield return Finish(cur);
                cur = new Piece { Page = s.Page, Cutout = s.Cutout };
                remap = new();
            }
            for (int k = 0; k < 3; k++)
            {
                int v = s.T[t * 3 + k];
                if (!remap.TryGetValue(v, out int nv))
                {
                    remap[v] = nv = cur.P.Count;
                    cur.P.Add(s.P[v]); cur.N.Add(s.N[v]); cur.UV.Add(s.UV[v]);
                }
                cur.T.Add(nv);
            }
            counts[Math.Clamp(s.Segment[t], 0, segments - 1)]++;
        }
        if (cur is not null && cur.T.Count > 0) yield return Finish(cur);
    }

    static BSMultiBoundNode MultiBound(NiHeader hdr, float size, float zMin, float zMax)
    {
        var aabb = BlockMeshBuilder.Owned(new BSMultiBoundAABB());
        aabb.center = new nifly.Vector3(size / 2, size / 2, (zMin + zMax) / 2);
        aabb.halfExtent = new nifly.Vector3(size / 2, size / 2, MathF.Max((zMax - zMin) / 2, 1));
        uint aabbId = hdr.AddBlock(aabb);
        var mb = BlockMeshBuilder.Owned(new BSMultiBound());
        var dataRef = mb.dataRef; dataRef.index = aabbId; mb.dataRef = dataRef;
        uint mbId = hdr.AddBlock(mb);
        var node = BlockMeshBuilder.Owned(new BSMultiBoundNode());
        node.flags = 14;
        node.cullingMode = BSCPCullingType.BSCP_CULL_ALLPASS;
        var r = node.multiBoundRef; r.index = mbId; node.multiBoundRef = r;
        return node;
    }

    /// <summary>
    /// Turns the saved file's BSTriShapes into BSSubIndexTriShapes with the given runs (in file order, as the shapes
    /// were added): the header's type name changed, and each shape block's segments appended, its size grown.
    /// </summary>
    static void Patch(string path, List<List<(int Start, int Count)>> runs)
    {
        var b = File.ReadAllBytes(path);
        int i = Array.IndexOf(b, (byte)'\n') + 1;
        int head = i;
        i += 4 + 1 + 4;                                     // version, endian, user version
        uint blocks = BitConverter.ToUInt32(b, i); i += 4;
        uint bsver = BitConverter.ToUInt32(b, i); i += 4;
        for (int k = 0; k < 3; k++) i += 1 + b[i];          // author, process and export scripts
        if (bsver >= 103) i += 1 + b[i];
        int typesAt = i;
        int types = BitConverter.ToUInt16(b, i); i += 2;
        var names = new List<string>();
        for (int k = 0; k < types; k++) { int n = BitConverter.ToInt32(b, i); i += 4; names.Add(Encoding.ASCII.GetString(b, i, n)); i += n; }
        int indexAt = i;
        var index = new ushort[blocks];
        for (int k = 0; k < blocks; k++) { index[k] = BitConverter.ToUInt16(b, i); i += 2; }
        int sizesAt = i;
        var sizes = new uint[blocks];
        for (int k = 0; k < blocks; k++) { sizes[k] = BitConverter.ToUInt32(b, i); i += 4; }
        int restAt = i;                                     // strings, groups, then the blocks
        int strings = BitConverter.ToInt32(b, i); i += 8;
        for (int k = 0; k < strings; k++) i += 4 + BitConverter.ToInt32(b, i);
        int groups = BitConverter.ToInt32(b, i); i += 4 + 4 * groups;
        int blocksAt = i;

        int tri = names.IndexOf("BSTriShape");
        if (tri < 0) return;
        names[tri] = "BSSubIndexTriShape";
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write(b, 0, typesAt);
        w.Write((ushort)names.Count);
        foreach (var n in names) { w.Write(n.Length); w.Write(Encoding.ASCII.GetBytes(n)); }
        w.Write(b, indexAt, sizesAt - indexAt);
        var tails = new byte[blocks][];
        int shapeNo = 0;
        int blockAt = blocksAt;
        for (int k = 0; k < blocks; k++)
        {
            int start0 = blockAt;
            blockAt += (int)sizes[k];
            if ((index[k] & 0x7FFF) != tri) { tails[k] = []; continue; }
            var r = shapeNo < runs.Count ? runs[shapeNo] : [];
            shapeNo++;
            // Its triangle count (after name, extra data, controller, flags, transform, collision, bound, three
            // references and the vertex description) must be the runs' total: the shapes are in the order written.
            int p = start0 + 4;
            p += 4 + 4 * BitConverter.ToInt32(b, p);
            p += 4 + 4 + 52 + 4 + 16 + 12 + 8;
            int fileTris = BitConverter.ToUInt16(b, p);
            if (fileTris != r.Sum(x => x.Count)) throw new InvalidDataException($"{path}: shape {shapeNo} has {fileTris} triangles, its runs {r.Sum(x => x.Count)}");
            using var t = new MemoryStream();
            using var tw = new BinaryWriter(t);
            tw.Write(r.Count);
            foreach (var (start, count) in r) { tw.Write((byte)0); tw.Write(start); tw.Write(count); }
            tails[k] = t.ToArray();
        }
        if (shapeNo != runs.Count) throw new InvalidDataException($"{path}: {shapeNo} shapes in the file, {runs.Count} written");
        for (int k = 0; k < blocks; k++) w.Write(sizes[k] + (uint)tails[k].Length);
        w.Write(b, restAt, blocksAt - restAt);
        int at = blocksAt;
        for (int k = 0; k < blocks; k++)
        {
            w.Write(b, at, (int)sizes[k]); at += (int)sizes[k];
            w.Write(tails[k]);
        }
        w.Write(b, at, b.Length - at);                      // the footer (root references)
        File.WriteAllBytes(path, o.ToArray());
        _ = head;
    }
}
