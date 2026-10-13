using nifly;
using TamrielMerge.Skyrim.Meshes;
using V3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion textures for Skyrim under textures\tamrielmerge\ob\, copied unchanged: Oblivion's DDS files (DXT1/3/5)
/// load in Skyrim as they are, and its normal maps already keep specular strength in their alpha, as Skyrim's do.
/// Each diffuse brings its "_n" normal map and "_g" glow map when the game has them.
/// </summary>
public sealed class ObTextures(ObData data, string modOutDir)
{
    public const string OutFolder = @"textures\tamrielmerge\ob";
    public const string FlatNormal = @"textures\tamrielmerge\ob\flat_n.dds";
    readonly Dictionary<string, string?> done = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Missing { get; } = [];
    public int Copied { get; private set; }

    /// <summary>The Skyrim data-relative copy of an Oblivion texture, or null when the game doesn't have it.</summary>
    public string? Ensure(string texture)
    {
        string source = ObData.Normalize(texture);
        if (!source.StartsWith(@"textures\")) source = @"textures\" + source;
        if (done.TryGetValue(source, out var hit)) return hit;
        var bytes = data.Read(source);
        if (bytes is null) { Missing.Add(texture); return done[source] = null; }
        string rel = $@"{OutFolder}\{source[9..]}";
        string full = Path.Combine(modOutDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        Copied++;
        return done[source] = rel;
    }

    /// <summary>The texture's normal map (name_n.dds), or a flat one without specular.</summary>
    public string Normal(string diffuse)
    {
        string n = Path.ChangeExtension(diffuse, null) + "_n.dds";
        if (data.Exists(n.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? n : @"textures\" + n) && Ensure(n) is { } path) return path;
        string flat = Path.Combine(modOutDir, FlatNormal);
        if (!File.Exists(flat))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(flat)!);
            Morrowind.MwTextures.WriteBcDds(flat, Enumerable.Repeat(0x008080FFu, 16).ToArray(), 4, 4, true);
        }
        return FlatNormal;
    }

    public string? Glow(string diffuse)
    {
        string g = Path.ChangeExtension(diffuse, null) + "_g.dds";
        return data.Exists(g.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? g : @"textures\" + g) ? Ensure(g) : null;
    }
}

/// <summary>
/// Converts an Oblivion model to a Skyrim SE static model with Oblivion's own geometry and textures. nifly reads the
/// Oblivion NIF; each shape (strips are read as triangles) is baked into model space and written as a BSTriShape with
/// a lighting shader (diffuse, normal map, specular from the normal map's alpha), vertex colours, alpha and material.
/// A model with Havok collision gets collision from its visible geometry; one without (cobwebs, light beams) gets none.
/// Units are the same in both games.
/// </summary>
public sealed partial class ObMeshConverter(ObData data, ObTextures textures)
{
    /// <summary>Static collision with stairs as Skyrim's (the stair material on steps, a ramp over each flight); moving
    /// objects keep theirs as is.</summary>
    static List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> Stairs(
        List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> solid, bool moving) =>
        moving ? solid : Skyrim.Meshes.BlockMeshBuilder.StairCollision(solid);

    public ObTextures Textures => textures;
    public const string OutFolder = @"meshes\tamrielmerge\ob";

    /// <summary>A converted model: path, counts, bounds, and its collision triangles in model space.</summary>
    public sealed record Result(string Path, int Shapes, int CollisionPolys, V3 Min, V3 Max, bool Animated, List<V3[]> Solid)
    {
        /// <summary>The model's particle emitters (flames, smoke, mist), which Skyrim gets as vanilla effects placed there.</summary>
        public List<Skyrim.EffectStandIns.Emitter> Emitters { get; init; } = [];
    }

    /// <summary>Output path; <paramref name="moving"/> variants (keyframed collision, for swinging doors) get "_moving".</summary>
    public static string OutputPath(string model, bool moving = false, float? itemMass = null)
    {
        string rel = $@"{OutFolder}\{ObData.Normalize(model).Replace(@"meshes\", "")}";
        return itemMass is not null ? Path.ChangeExtension(rel, null) + "_item.nif" : moving ? Path.ChangeExtension(rel, null) + "_moving.nif" : rel;
    }

    sealed class Piece
    {
        public required string Name;
        public required List<V3> Verts, Normals;
        public required List<System.Numerics.Vector2> Uvs;
        public required List<System.Numerics.Vector4> Colors;
        public required List<(ushort, ushort, ushort)> Tris;
        public string? Texture;
        public ushort? AlphaFlags;
        public byte AlphaThreshold;
        public bool TwoSided;
        public float Glossiness = 10, Alpha = 1;
        public V3 Emissive;
    }

    /// <param name="itemMass">A loose item: dynamic physics (a convex box around the model with this mass).</param>
    public Result Convert(string model, string modOutDir, bool moving = false, float? itemMass = null)
    {
        string source = model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var bytes = data.Read(source) ?? throw new FileNotFoundException(source);
        if (IsOldNif(bytes))
        {
            // NetImmerse 10.0.1.2 (Oblivion's grass), which nifly can't read: still geometry, no collision.
            var old = ReadOldNif(bytes) ?? throw new InvalidDataException($"{source}: a NetImmerse 10.0.1.2 block this can't read");
            string oldRel = OutputPath(model, moving, itemMass);
            Write(old, [], Path.Combine(modOutDir, oldRel), moving, itemMass);
            var pts = old.SelectMany(p => p.Verts).ToList();
            return new Result(oldRel, old.Count, 0,
                pts.Count > 0 ? pts.Aggregate(new V3(float.MaxValue), V3.Min) : V3.Zero,
                pts.Count > 0 ? pts.Aggregate(new V3(float.MinValue), V3.Max) : V3.Zero, false, []);
        }
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_ob_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        var pieces = new List<Piece>();
        bool hasHavok, animated;
        List<V3[]>? havokTris = null;
        var emitters = new List<Skyrim.EffectStandIns.Emitter>();
        try
        {
            using var nif = new NifFile();
            if (nif.Load(tmp) != 0) throw new InvalidDataException($"nifly could not read {source}");
            var hdr = nif.GetHeader();
            var types = Enumerable.Range(0, (int)hdr.GetNumBlocks()).Select(i => hdr.GetBlockTypeStringById((uint)i)).ToList();
            hasHavok = types.Any(t => t == "bhkCollisionObject");
            if (hasHavok) havokTris = HavokTriangles(nif);
            animated = types.Any(t => t.EndsWith("Controller"));
            try { emitters = Emitters(nif); }
            catch (Exception) { /* particle data nifly can't follow: no stand-ins */ }
            foreach (var shape in nif.GetShapes())
            {
                // Only plain geometry: particle systems and other shape kinds are effects, converted later.
                if (hdr.GetBlockTypeStringById(hdr.GetBlockID(shape)) is not ("NiTriStrips" or "NiTriShape")) continue;
                if ((shape.flags & 1) != 0) continue;   // hidden
                var chain = new List<MatTransform> { shape.transform };
                bool editorOnly = IsEditorMarker(shape.name.get());
                for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent))
                {
                    chain.Add(parent.transform);
                    editorOnly |= IsEditorMarker(parent.name.get()) || (parent.flags & 1) != 0;
                }
                // Geometry under an "EditorMarker" node only shows in the Construction Set (collision boxes, trigger
                // zones, havok roots' outlines).
                if (editorOnly) continue;
                using var tris = new vectorTriangle();
                shape.GetTriangles(tris);
                if (tris.Count == 0) continue;
                var verts = nif.GetVertsForShape(shape)?.ToList() ?? [];
                if (verts.Count == 0) continue;
                var normals = nif.GetNormalsForShape(shape)?.ToList() ?? [];
                var p = new Piece
                {
                    Name = shape.name.get() is { Length: > 0 } n ? n : $"Shape{pieces.Count}",
                    Verts = verts.Select(v => { var g = v; foreach (var t in chain) g = t.ApplyTransform(g); return new V3(g.x, g.y, g.z); }).ToList(),
                    Normals = normals.Count == verts.Count
                        ? normals.Select(v => { var g = v; foreach (var t in chain) g = t.ApplyTransformToDir(g); var r = new V3(g.x, g.y, g.z); return r.LengthSquared() > 0 ? V3.Normalize(r) : V3.UnitZ; }).ToList()
                        : [],
                    Uvs = nif.GetUvsForShape(shape)?.Select(t => new System.Numerics.Vector2(t.u, t.v)).ToList() ?? [],
                    Colors = nif.GetColorsForShape(shape)?.Select(c => new System.Numerics.Vector4(c.r, c.g, c.b, c.a)).ToList() ?? [],
                    Tris = tris.Select(t => (t.p1, t.p2, t.p3)).ToList(),
                    Texture = nif.GetTexturePathByIndex(shape, 0) is { Length: > 0 } tex ? tex : null,
                    TwoSided = nif.GetStencilProperty(shape) is not null,
                };
                if (nif.GetAlphaProperty(shape) is { } alpha) { p.AlphaFlags = alpha.flags; p.AlphaThreshold = alpha.threshold; }
                if (nif.GetMaterialProperty(shape) is { } mat)
                {
                    p.Glossiness = mat.GetGlossiness();
                    p.Alpha = mat.GetAlpha();
                    var e = mat.GetEmissiveColor();
                    p.Emissive = new V3(e.r, e.g, e.b);
                }
                if (p.Uvs.Count != p.Verts.Count) p.Uvs = Enumerable.Repeat(System.Numerics.Vector2.Zero, p.Verts.Count).ToList();
                pieces.Add(p);
            }
        }
        finally { File.Delete(tmp); }

        var solid = new List<(V3[] Pts, V3 N)>();
        if (havokTris is not null)
            foreach (var t in havokTris)
            {
                var cr = V3.Cross(t[1] - t[0], t[2] - t[0]);
                if (cr.LengthSquared() > 1e-4f) solid.Add((t, V3.Normalize(cr)));
            }
        else if (hasHavok)
            foreach (var p in pieces)
                foreach (var (a, b, c) in p.Tris)
                {
                    var pts = new[] { p.Verts[a], p.Verts[b], p.Verts[c] };
                    var cr = V3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
                    if (cr.LengthSquared() > 1e-6f) solid.Add((pts, V3.Normalize(cr)));
                }

        string rel = OutputPath(model, moving, itemMass);
        Write(pieces, solid, Path.Combine(modOutDir, rel), moving, itemMass);
        var all = pieces.SelectMany(p => p.Verts).ToList();
        return new Result(rel, pieces.Count, solid.Count,
            all.Count > 0 ? all.Aggregate(new V3(float.MaxValue), V3.Min) : V3.Zero,
            all.Count > 0 ? all.Aggregate(new V3(float.MinValue), V3.Max) : V3.Zero, animated, solid.Select(t => t.Pts).ToList())
        { Emitters = emitters };
    }

    /// <summary>
    /// Particle emitters: each particle system's emitters (NiPSysEmitter modifiers), at their emitter node (volume
    /// emitters) or the system itself, placed by the transforms up to the root; size from the particle radius (as a
    /// diameter), rise from speed x life span, texture from the system's texturing property.
    /// </summary>
    static List<Skyrim.EffectStandIns.Emitter> Emitters(NifFile nif)
    {
        var hdr = nif.GetHeader();
        var result = new List<Skyrim.EffectStandIns.Emitter>();
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
        {
            if (hdr.GetBlockById(i) is not NiParticleSystem ps) continue;
            string texture = "";
            var props = ps.propertyRefs;
            for (uint k = 0; k < props.GetSize(); k++)
                if (hdr.GetBlockById(props.GetBlockRef(k)) is NiTexturingProperty { hasBaseTex: true } tp && hdr.GetBlockById(tp.baseTex.sourceRef.index) is NiSourceTexture src)
                    texture = Path.GetFileName(src.fileName.get() ?? "");
            var mods = ps.modifierRefs;
            for (uint k = 0; k < mods.GetSize(); k++)
            {
                if (hdr.GetBlockById(mods.GetBlockRef(k)) is not NiPSysEmitter e) continue;
                NiAVObject at = ps;
                if (e is NiPSysVolumeEmitter v && hdr.GetBlockById(v.emitterNodeRef.index) is NiNode n) at = n;
                var g = new nifly.Vector3(0, 0, 0);
                for (NiAVObject? o = at; o is not null; o = nif.GetParentNode(o)) g = o.transform.ApplyTransform(g);
                result.Add(new Skyrim.EffectStandIns.Emitter(new V3(g.x, g.y, g.z), e.radius * 2, e.speed * e.lifeSpan, texture));
            }
        }
        return result;
    }

    /// <summary>Oblivion's Havok units are 7 game units.</summary>
    const float HavokScale = 7f;

    /// <summary>
    /// The model's own collision as triangles in model space: every solid collision object's rigid body (with its
    /// translation and, for bhkRigidBodyT, rotation) on its target node, its shape tree walked through MOPP, list and
    /// transform shapes down to triangle strips (read through a temporary NiTriStrips, as nifly reads geometry through
    /// shapes), packed triangles, boxes, spheres, capsules and convex hulls. Phantoms (triggers) are skipped. Null when
    /// a shape kind isn't handled, so the caller falls back to the visible geometry.
    /// </summary>
    static List<V3[]>? HavokTriangles(NifFile nif)
    {
        var hdr = nif.GetHeader();
        var tris = new List<V3[]>();
        var pending = new List<(uint Data, Func<V3, V3> Xf, V3 Scale)>();
        bool ok = true;
        uint count = hdr.GetNumBlocks();
        for (uint i = 0; i < count; i++)
        {
            if (hdr.GetBlockTypeStringById(i) != "bhkCollisionObject") continue;
            if (hdr.GetBlockById(i) is not bhkCollisionObject col) continue;
            var target = hdr.GetBlockById(col.targetRef.index) as NiNode;
            var chain = new List<MatTransform>();
            for (NiNode? n = target; n is not null; n = nif.GetParentNode(n)) chain.Add(n.transform);
            if (hdr.GetBlockById(col.bodyRef.index) is not bhkRigidBody body) { ok = false; continue; }
            bool hasT = hdr.GetBlockTypeStringById(col.bodyRef.index) == "bhkRigidBodyT";
            var q = new System.Numerics.Quaternion(body.rotation.x, body.rotation.y, body.rotation.z, body.rotation.w);
            if (q.LengthSquared() < 1e-6f) q = System.Numerics.Quaternion.Identity;
            var offset = new V3(body.translation.x, body.translation.y, body.translation.z) * HavokScale;
            V3 ToModel(V3 p)
            {
                if (hasT) p = V3.Transform(p, q) + offset;
                var g = new nifly.Vector3(p.X, p.Y, p.Z);
                foreach (var t in chain) g = t.ApplyTransform(g);
                return new V3(g.x, g.y, g.z);
            }
            if (!Shape(nif, hdr, body.shapeRef.index, ToModel, tris, pending)) ok = false;
        }
        if (ok && pending.Count > 0) ok = ReadStrips(nif, pending, tris);
        return ok ? tris : null;
    }

    /// <summary>
    /// Reads collision triangle strips (NiTriStripsData). nifly reads geometry through shapes, and a strips shape's
    /// triangles through a link built when the file loads; so a temporary NiTriStrips per strips block is attached to
    /// the root, the model saved and reloaded, and the temporary shapes read there.
    /// </summary>
    static bool ReadStrips(NifFile nif, List<(uint Data, Func<V3, V3> Xf, V3 Scale)> pending, List<V3[]> output)
    {
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        for (int k = 0; k < pending.Count; k++)
        {
            var probe = new NiTriStrips();
            GC.SuppressFinalize(probe);
            probe.SetDataRef(pending[k].Data);
            probe.name = new NiStringRef($"TMProbe{k}");
            uint id = hdr.AddBlock(probe);
            nif.SetParentNode(hdr.GetBlockById(id), root);
        }
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_strips_{Guid.NewGuid():N}.nif");
        try
        {
            if (nif.Save(tmp) != 0) return false;
            using var reloaded = new NifFile();
            if (reloaded.Load(tmp) != 0) return false;
            var shapes = reloaded.GetShapes().Where(sh => sh.name.get()?.StartsWith("TMProbe") == true).ToDictionary(sh => sh.name.get(), sh => sh);
            for (int k = 0; k < pending.Count; k++)
            {
                if (!shapes.TryGetValue($"TMProbe{k}", out var shape)) return false;
                var (_, xf, sc) = pending[k];
                var verts = reloaded.GetVertsForShape(shape)?.Select(v => xf(new V3(v.x * sc.X, v.y * sc.Y, v.z * sc.Z))).ToList() ?? [];
                using var t = new vectorTriangle();
                shape.GetTriangles(t);
                foreach (var tri in t)
                    if (tri.p1 < verts.Count && tri.p2 < verts.Count && tri.p3 < verts.Count) output.Add([verts[tri.p1], verts[tri.p2], verts[tri.p3]]);
            }
            return true;
        }
        finally { File.Delete(tmp); }
    }

    static bool Shape(NifFile nif, NiHeader hdr, uint id, Func<V3, V3> xf, List<V3[]> output, List<(uint Data, Func<V3, V3> Xf, V3 Scale)> pending)
    {
        if (id == 0xFFFFFFFF || id >= hdr.GetNumBlocks()) return true;
        string type = hdr.GetBlockTypeStringById(id);
        var block = hdr.GetBlockById(id);
        void Box(V3 lo, V3 hi)
        {
            var c = new V3[8];
            for (int k = 0; k < 8; k++) c[k] = xf(new V3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z));
            int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
            foreach (var f in faces) { output.Add([c[f[0]], c[f[1]], c[f[2]]]); output.Add([c[f[0]], c[f[2]], c[f[3]]]); }
        }
        switch (type)
        {
            case "bhkMoppBvTreeShape":
                return Shape(nif, hdr, ((bhkMoppBvTreeShape)block).shapeRef.index, xf, output, pending);
            case "bhkListShape":
            {
                var list = (bhkListShape)block;
                bool all = true;
                for (uint k = 0; k < list.subShapeRefs.GetSize(); k++) all &= Shape(nif, hdr, list.subShapeRefs.GetBlockRef(k), xf, output, pending);
                return all;
            }
            case "bhkTransformShape" or "bhkConvexTransformShape":
            {
                var t = (bhkTransformShape)block;
                var m = t.xform;   // in Havok units
                V3 Child(V3 p)
                {
                    var r = m.opMult(new nifly.Vector3(p.X / HavokScale, p.Y / HavokScale, p.Z / HavokScale));
                    return xf(new V3(r.x, r.y, r.z) * HavokScale);
                }
                return Shape(nif, hdr, t.shapeRef.index, Child, output, pending);
            }
            case "bhkBoxShape":
            {
                var b = (bhkBoxShape)block;
                var d = new V3(b.dimensions.x, b.dimensions.y, b.dimensions.z) * HavokScale;
                Box(-d, d);
                return true;
            }
            case "bhkSphereShape":
            {
                float r = ((bhkConvexShape)block).radius * HavokScale;
                Box(new V3(-r), new V3(r));
                return true;
            }
            case "bhkCapsuleShape":
            {
                var c = (bhkCapsuleShape)block;
                var p1 = new V3(c.point1.x, c.point1.y, c.point1.z) * HavokScale; var p2 = new V3(c.point2.x, c.point2.y, c.point2.z) * HavokScale;
                float r = c.radius1 * HavokScale;
                Box(V3.Min(p1, p2) - new V3(r), V3.Max(p1, p2) + new V3(r));
                return true;
            }
            case "bhkConvexVerticesShape":
            {
                var cv = (bhkConvexVerticesShape)block;
                var pts = cv.verts.items().Select(v => new V3(v.x, v.y, v.z) * HavokScale).ToList();
                if (pts.Count < 4) return true;
                Box(pts.Aggregate(V3.Min), pts.Aggregate(V3.Max));   // the hull's bounds: a convex block
                return true;
            }
            case "hkPackedNiTriStripsData":
            {
                var data = (hkPackedNiTriStripsData)block;
                var verts = data.compressedVertData.Select(v => xf(new V3(v.x, v.y, v.z) * HavokScale)).ToList();
                // Oblivion's (uncompressed) packed data keeps its triangles with their normals (triNormData).
                var triangles = data.triData.Count > 0 ? data.triData.Select(t => t.tri).ToList() : data.triNormData.Select(t => t.tri).ToList();
                foreach (var t in triangles)
                    if (t.p1 < verts.Count && t.p2 < verts.Count && t.p3 < verts.Count)
                        output.Add([verts[t.p1], verts[t.p2], verts[t.p3]]);
                return true;
            }
            case "bhkPackedNiTriStripsShape":
                return Shape(nif, hdr, ((bhkPackedNiTriStripsShape)block).dataRef.index, xf, output, pending);
            case "bhkNiTriStripsShape":
            {
                var s = (bhkNiTriStripsShape)block;
                var sc = new V3(s.scale.x == 0 ? 1 : s.scale.x, s.scale.y == 0 ? 1 : s.scale.y, s.scale.z == 0 ? 1 : s.scale.z);
                for (uint k = 0; k < s.partRefs.GetSize(); k++) pending.Add((s.partRefs.GetBlockRef(k), xf, sc));   // read later (ReadStrips)
                return true;
            }
            default:
                return false;   // anything else: fall back to the visible geometry
        }
    }

    static bool IsEditorMarker(string? name) => name?.StartsWith("EditorMarker", StringComparison.OrdinalIgnoreCase) == true;

    void Write(List<Piece> pieces, List<(V3[] Pts, V3 N)> solid, string outPath, bool moving, float? itemMass = null)
    {
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var hdr = nif.GetHeader();
        var fade = BlockMeshBuilder.Owned(new BSFadeNode());
        fade.name = new NiStringRef(Path.GetFileNameWithoutExtension(outPath));
        hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pieces)
        {
            if (p.Verts.Count > 65535) continue;
            using var verts = new vectorVector3();
            using var uvs = new vectorVector2();
            using var norms = new vectorVector3();
            using var tris = new vectorTriangle();
            foreach (var v in p.Verts) verts.Add(new nifly.Vector3(v.X, v.Y, v.Z));
            foreach (var t in p.Uvs) uvs.Add(new nifly.Vector2(t.X, t.Y));
            foreach (var n in p.Normals) norms.Add(new nifly.Vector3(n.X, n.Y, n.Z));
            foreach (var (i0, i1, i2) in p.Tris) tris.Add(new Triangle(i0, i1, i2));
            string name = p.Name;
            for (int k = 2; !names.Add(name); k++) name = $"{p.Name}:{k}";
            var shape = p.Normals.Count > 0 ? nif.CreateShapeFromData(name, verts, tris, uvs, norms) : nif.CreateShapeFromData(name, verts, tris, uvs);
            string? diffuse = p.Texture is null ? null : textures.Ensure(p.Texture);
            nif.SetTextureSlot(shape, diffuse ?? @"textures\tamrielmerge\ob\missing.dds", 0);
            nif.SetTextureSlot(shape, p.Texture is null ? ObTextures.FlatNormal : textures.Normal(p.Texture), 1);
            // Oblivion multiplies emissive colour into the texture; Skyrim's own-emit adds a flat tint. So an emissive
            // surface glows through a glow map: Oblivion's own (_g) when it has one, else its diffuse texture.
            bool emissive = p.Emissive.X + p.Emissive.Y + p.Emissive.Z > 0.05f;
            string? glow = p.Texture is null ? null : textures.Glow(p.Texture) ?? (emissive ? diffuse : null);
            if (glow is not null) nif.SetTextureSlot(shape, glow, 2);
            if (p.Normals.Count > 0) nif.CalcTangentsForShape(shape);

            bool colors = p.Colors.Count == p.Verts.Count && p.Colors.Any(c => c != System.Numerics.Vector4.One);
            if (colors)
            {
                using var cols = new vectorColor4();
                foreach (var c in p.Colors) cols.Add(new Color4(c.X, c.Y, c.Z, c.W));
                nif.SetColorsForShape(shape, cols);
            }
            if (p.AlphaFlags is { } af)
                nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new nifly.NiAlphaProperty { flags = af, threshold = p.AlphaThreshold }));
            bool vertexAlpha = colors && p.AlphaFlags is not null && p.Colors.Any(c => c.W < 1);
            bool blended = p.AlphaFlags is { } fl && (fl & 1) != 0 && (fl & 0x200) == 0;
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), s =>
            {
                // Specular (from the normal map's alpha), receive/cast shadows, own emit, remappable textures,
                // ZBuffer test; vertex alpha when the colours carry it; glow map when the texture has one.
                s.shaderFlags1 = 0x82400301u | (vertexAlpha ? 0x8u : 0);
                s.shaderFlags2 = (blended ? 0u : 0x1u) | (colors ? 0x20u : 0) | (p.TwoSided ? 0x10u : 0) | (glow is not null ? 0x40u : 0) | 0x8000u;
                if (glow is not null) s.SetShaderType(2u);   // Glow shader
                s.glossiness = Math.Max(1, p.Glossiness);
                s.specularStrength = 1;
                s.alpha = p.Alpha;
                s.emissiveColor = glow is null ? new nifly.Vector3(0, 0, 0) : emissive ? new nifly.Vector3(p.Emissive.X, p.Emissive.Y, p.Emissive.Z) : new nifly.Vector3(1, 1, 1);
                s.emissiveMultiple = glow is null ? 0 : 1;
            });
        }
        if (itemMass is { } mass)
        {
            var pts = pieces.SelectMany(p => p.Verts).ToList();
            if (pts.Count > 0)
            {
                BlockMeshBuilder.AddDynamicCollision(nif, root, pts.Aggregate(new V3(float.MaxValue), V3.Min), pts.Aggregate(new V3(float.MinValue), V3.Max), mass, BlockMeshBuilder.WoodMaterial);
                BlockMeshBuilder.AddBsxFlags(nif, root, 194);
            }
        }
        else
        {
            if (solid.Count > 0)
                BlockMeshBuilder.AddCollision(nif, root, Stairs(solid.Select(s => (s.Pts, s.N, new BlockMeshBuilder.Tex("", "", false))).ToList(), moving), moving);
            BlockMeshBuilder.AddBsxFlags(nif, root);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
    }
}
