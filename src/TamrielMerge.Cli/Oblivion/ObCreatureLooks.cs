using System.Numerics;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using nifly;
using TamrielMerge.Skyrim;
using TamrielMerge.Skyrim.Meshes;
using Ob = Mutagen.Bethesda.Oblivion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// An Oblivion creature's body as shapes on its own skeleton, in the pose it was modelled in. A CREA names its skeleton
/// (MODL) and the parts it wears (NIFZ, files beside the skeleton). The pose is the skin data's, not skeleton.nif's rest:
/// skeleton.nif can stand far from it (the bear's stands upright, head 129 up, where a bear is on all fours), and skinning a
/// body to a pose far from its own would smear it before it is ever re-rigged. Each bone takes its bind from the largest
/// shape bound to it; a bone no skin uses keeps its rest offset from its parent. Parts bound in slightly different poses
/// are carried to that one through their skin data (a vertex is v * skin-to-bone * the bone's pose, by weight); rigid parts
/// ride the bone their model hangs from ("Prn" extra data, or the node above them), and shapes in skeleton.nif itself ride
/// their nodes (the storm atronach's rocks are all there). Particle emitters, their invisible boxes and editor markers
/// are no body.
/// </summary>
public sealed class ObCreatureSource(ObData data, ObTextures textures)
{
    static T WithNif<T>(byte[] bytes, Func<NifFile, T> use)
    {
        // nifly isn't thread-safe: one lock for every use, shared with the worn writers.
        T result = default!;
        SkyrimCharacterAssets.Locked(() =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_obcrea_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) throw new InvalidDataException("nifly can't read it");
                result = use(nif);
            }
            finally { File.Delete(tmp); }
        });
        return result;
    }

    /// <summary>A skeleton's rest pose with each node's scale kept (Oblivion's skin data can carry scale that cancels it).</summary>
    static BindSkeleton Rest(NifFile nif)
    {
        var sk = new BindSkeleton();
        var hdr = nif.GetHeader();
        for (uint id = 0; id < hdr.GetNumBlocks(); id++)
        {
            if (hdr.GetBlockById(id) is not NiNode node || node.name.get() is not { Length: > 0 } name) continue;
            var m = WornMesh.ToMatrix(node.transform);
            for (var parent = nif.GetParentNode(node); parent is not null; parent = nif.GetParentNode(parent)) m *= WornMesh.ToMatrix(parent.transform);
            sk.World[name] = m;
            if (nif.GetParentNode(node) is { } up && up.name.get() is { Length: > 0 } upName) sk.Parent[name] = upName;
        }
        return sk;
    }

    /// <summary>The skeleton's rest pose alone (dev aid: <see cref="ObPluginBuilder.CreatureLookList"/>).</summary>
    public BindSkeleton Skeleton(string skeletonFile)
    {
        string path = ObData.Normalize(skeletonFile.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? skeletonFile : @"meshes\" + skeletonFile);
        return WithNif(data.Read(path) ?? throw new FileNotFoundException(path), Rest);
    }

    /// <summary>Notes each bone's bind in a file's skinned shapes (the skin-to-bone inverted, carried by the global-to-skin
    /// transform inverted), keeping the largest shape's, and the parent of a bone skeleton.nif doesn't have.</summary>
    static void CollectBinds(NifFile nif, BindSkeleton rest, Dictionary<string, (Matrix4x4 Bind, int Verts)> binds)
    {
        var hdr = nif.GetHeader();
        using var all = nif.GetShapes();
        for (int si = 0; si < all.Count; si++)
        {
            var shape = all[si];
            if (hdr.GetBlockTypeStringById(hdr.GetBlockID(shape)) is not ("NiTriStrips" or "NiTriShape") || (shape.flags & 1) != 0) continue;
            using var names = new vectorstring();
            nif.GetShapeBoneList(shape, names);
            var bones = names.ToList();
            if (bones.Count == 0) continue;
            int verts = nif.GetVertsForShape(shape)?.Count ?? 0;
            var g2s = new MatTransform();
            var toGlobal = nif.GetShapeTransformGlobalToSkin(shape, g2s) && Matrix4x4.Invert(WornMesh.ToMatrix(g2s), out var inv) ? inv : Matrix4x4.Identity;
            for (int b = 0; b < bones.Count; b++)
            {
                var stb = new MatTransform();
                nif.GetShapeTransformSkinToBone(shape, (uint)b, stb);
                if (!Matrix4x4.Invert(WornMesh.ToMatrix(stb), out var boneInSkin)) continue;
                if (!binds.TryGetValue(bones[b], out var known) || known.Verts < verts) binds[bones[b]] = (boneInSkin * toGlobal, verts);
            }
        }
        for (uint id = 0; id < hdr.GetNumBlocks(); id++)
            if (hdr.GetBlockById(id) is NiNode node && node.name.get() is { Length: > 0 } name && !rest.Has(name) && binds.ContainsKey(name)
                && nif.GetParentNode(node) is { } up && up.name.get() is { Length: > 0 } upName)
                rest.Parent[name] = upName;
    }

    /// <summary>The pose the body was modelled in: <paramref name="binds"/> where a skin has them, else each bone's rest offset
    /// from its parent's pose (<paramref name="rest"/> keeps skeleton.nif's). Bones carry no scale here but what they had.</summary>
    static BindSkeleton Pose(BindSkeleton rest, Dictionary<string, (Matrix4x4 Bind, int Verts)> binds)
    {
        var pose = new BindSkeleton();
        foreach (var (k, v) in rest.Parent) pose.Parent[k] = v;
        Matrix4x4 Of(string bone, int depth)
        {
            if (pose.World.TryGetValue(bone, out var hit)) return hit;
            Matrix4x4 m;
            if (binds.TryGetValue(bone, out var b)) m = b.Bind;
            else if (depth < 64 && rest.Has(bone) && rest.Parent.TryGetValue(bone, out var up) && rest.Has(up) && Matrix4x4.Invert(rest.World[up], out var inverse))
                m = rest.World[bone] * inverse * Of(up, depth + 1);
            else m = rest.Has(bone) ? rest.World[bone] : Matrix4x4.Identity;
            return pose.World[bone] = m;
        }
        foreach (var bone in rest.World.Keys.Concat(binds.Keys).ToList()) Of(bone, 0);
        return pose;
    }

    /// <summary>The creature's shapes in the pose it was modelled in (weights by its bones), that pose, and how many
    /// vertices had no weight in their skin data (they ride their shape's first bone).</summary>
    public (List<WornShape> Shapes, BindSkeleton Pose, int Unweighted) Read(string skeletonFile, IEnumerable<string> parts, List<string> problems)
    {
        string path = ObData.Normalize(skeletonFile.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? skeletonFile : @"meshes\" + skeletonFile);
        var bytes = data.Read(path) ?? throw new FileNotFoundException(path);
        var rest = WithNif(bytes, Rest);
        var files = new List<(byte[] Bytes, string Name)> { (bytes, path) };
        string folder = Path.GetDirectoryName(path)!;
        foreach (var part in parts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.Combine(folder, ObData.Normalize(part));
            // Stale records name the Knights of the Nine's parts without their prefix (Ayleid.NIF for NDAyleid.NIF, in ND04Minion and its
            // kind): the same creature in every other record, so the file with it.
            if ((data.Read(file) ?? data.Read(Path.Combine(folder, "nd" + ObData.Normalize(part)))) is { } b) files.Add((b, file));
            else problems.Add($"{path}: part {part} isn't in the game's data");
        }
        var binds = new Dictionary<string, (Matrix4x4 Bind, int Verts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in files)
            if (!Older(file)) WithNif(file, nif => { CollectBinds(nif, rest, binds); return 0; });
        var pose = Pose(rest, binds);
        var shapes = new List<WornShape>();
        int unweighted = 0;
        foreach (var (file, name) in files)
            if (Older(file)) shapes.AddRange(ReadOlder(file, name, pose, ref unweighted, problems));
            else WithNif(file, nif => { shapes.AddRange(ReadShapes(nif, name, pose, ref unweighted, problems)); return 0; });
        return (shapes, pose, unweighted);
    }

    List<WornShape> ReadShapes(NifFile nif, string file, BindSkeleton rest, ref int unweighted, List<string> problems)
    {
        var result = new List<WornShape>();
        var hdr = nif.GetHeader();
        string? prn = null;
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
            if (hdr.GetBlockById(i) is NiStringExtraData sx && sx.name.get() == "Prn") prn = sx.stringData.get();
        using var all = nif.GetShapes();
        for (int si = 0; si < all.Count; si++)
        {
            var shape = all[si];
            string type = hdr.GetBlockTypeStringById(hdr.GetBlockID(shape));
            if (type is not ("NiTriStrips" or "NiTriShape") || (shape.flags & 1) != 0) continue;
            string shapeName = shape.name.get() ?? "";
            string texture = nif.GetTexturePathByIndex(shape, 0) ?? "";
            string lowTexture = ObData.Normalize(texture);
            if (shapeName.Contains("box", StringComparison.OrdinalIgnoreCase) || shapeName.Contains("marker", StringComparison.OrdinalIgnoreCase)
                || lowTexture.EndsWith("invisible.dds") || lowTexture.EndsWith(@"\null.dds")) continue;
            var verts = nif.GetVertsForShape(shape);
            if (verts is null || verts.Count == 0) continue;
            var normals = nif.GetNormalsForShape(shape);
            var uvs = nif.GetUvsForShape(shape);
            using var t3 = new vectorTriangle();
            shape.GetTriangles(t3);
            if (t3.Count == 0) continue;
            int n = verts.Count;
            var v = new Vector3[n]; var nr = new Vector3[n];
            var weights = new (string, float)[n][];
            bool hasNormals = normals is { Count: > 0 };
            Vector3 Vert(int i) => new(verts[i].x, verts[i].y, verts[i].z);
            Vector3 Norm(int i) => hasNormals && i < normals!.Count ? new Vector3(normals[i].x, normals[i].y, normals[i].z) : Vector3.UnitZ;
            using var names = new vectorstring();
            nif.GetShapeBoneList(shape, names);
            var bones = names.ToList();
            bool mirrored = false;
            if (bones.Count > 0)
            {
                var toRest = new Matrix4x4[bones.Count];
                for (int b = 0; b < bones.Count; b++)
                {
                    var stb = new MatTransform();
                    nif.GetShapeTransformSkinToBone(shape, (uint)b, stb);
                    toRest[b] = WornMesh.ToMatrix(stb) * rest.World[bones[b]];
                }
                var acc = new List<(int Bone, float W)>[n];
                for (int i = 0; i < n; i++) acc[i] = [];
                using (var cache = new niflycpp.BlockCache(nif.GetHeader()))
                {
                    uint skinId = shape.SkinInstanceRef().index;
                    var inst = cache.EditableBlockById<NiSkinInstance>(skinId);
                    var sd = inst is null ? null : cache.EditableBlockById<NiSkinData>(inst.dataRef.index);
                    if (sd is not null)
                        for (int b = 0; b < bones.Count && b < sd.bones.Count; b++)
                            foreach (var sw in sd.bones[b].vertexWeights)
                                if (sw.index < n && sw.weight > 0) acc[sw.index].Add((b, sw.weight));
                }
                for (int i = 0; i < n; i++)
                {
                    var top = acc[i].OrderByDescending(x => x.W).Take(4).ToList();
                    float total = top.Sum(x => x.W);
                    if (total <= 0) { unweighted++; top = [(0, 1f)]; total = 1; }
                    Vector3 pos = Vector3.Zero, nrm = Vector3.Zero;
                    foreach (var (b, w) in top)
                    {
                        pos += Vector3.Transform(Vert(i), toRest[b]) * (w / total);
                        nrm += Vector3.TransformNormal(Norm(i), toRest[b]) * (w / total);
                    }
                    v[i] = pos;
                    nr[i] = nrm.LengthSquared() > 1e-10f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                    weights[i] = top.Select(x => (bones[x.Bone], x.W / total)).ToArray();
                }
            }
            else
            {
                // Rigid: its transforms down to the nearest node of the skeleton, else the bone "Prn" names. The file's root
                // ("Scene Root" in every model) is never the bone it rides.
                var local = WornMesh.ToMatrix(shape.transform);
                string? holder = null;
                for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent))
                {
                    string pn = parent.name.get() ?? "";
                    if (nif.GetParentNode(parent) is not null && rest.Has(pn)) { holder = pn; break; }
                    local *= WornMesh.ToMatrix(parent.transform);
                }
                holder ??= prn is not null && rest.Has(prn) ? prn : null;
                if (holder is null) { problems.Add($"{file}: rigid shape {shapeName} names no bone it rides"); continue; }
                var place = local * rest.World[holder];
                mirrored = place.GetDeterminant() < 0;
                for (int i = 0; i < n; i++)
                {
                    v[i] = Vector3.Transform(Vert(i), place);
                    var turned = Vector3.TransformNormal(Norm(i), place);
                    nr[i] = turned.LengthSquared() > 1e-10f ? Vector3.Normalize(turned) : Vector3.UnitZ;
                    weights[i] = [(holder, 1f)];
                }
            }
            var tris = new int[t3.Count * 3];
            for (int k = 0; k < t3.Count; k++)
            {
                tris[k * 3] = t3[k].p1;
                tris[k * 3 + 1] = mirrored ? t3[k].p3 : t3[k].p2;
                tris[k * 3 + 2] = mirrored ? t3[k].p2 : t3[k].p3;
            }
            (float Gloss, float Alpha, Vector3 Emissive)? material = null;
            if (nif.GetMaterialProperty(shape) is { } mat) { var e = mat.GetEmissiveColor(); material = (mat.GetGlossiness(), mat.GetAlpha(), new Vector3(e.r, e.g, e.b)); }
            var uv = Enumerable.Range(0, n).Select(i => uvs is { Count: > 0 } && i < uvs.Count ? new Vector2(uvs[i].u, uvs[i].v) : Vector2.Zero).ToArray();
            (ushort, byte)? blend = nif.GetAlphaProperty(shape) is { } alpha ? (alpha.flags, alpha.threshold) : null;
            result.Add(Dress(shapeName.Length > 0 ? shapeName : $"Shape{si}", v, hasNormals ? nr : [], uv, tris, weights, texture, material, blend));
        }
        return result;
    }

    /// <summary>Files nifly can't read, 48 of Oblivion's creature parts: Gamebryo 10.2.0.0 (the goblin's helmets, the rat's mange),
    /// 10.1.0.106 (the minotaur's, goblin's and mountain lion's heads, the horse's bridle) and NetImmerse 10.0.1.x (the
    /// minotaur's horns and hair).</summary>
    static bool Older(byte[] file) => ObMeshConverter.IsOldNif(file) || Ob10Nif.Header(file) is { Version: < 0x14000004 };

    /// <summary>A part read by <see cref="Ob10Nif"/>, or <see cref="ObMeshConverter.OldParts"/> for the oldest. Nearly all are rigid
    /// (their morphs are faces, which Skyrim's animations don't play: the base shape is used), each riding the bone its "Prn"
    /// names; the rat's mange is skinned as any body.</summary>
    List<WornShape> ReadOlder(byte[] bytes, string file, BindSkeleton pose, ref int unweighted, List<string> problems)
    {
        var result = new List<WornShape>();
        if (ObMeshConverter.IsOldNif(bytes))
        {
            var (oldParts, oldPrn) = ObMeshConverter.OldParts(bytes) ?? throw new InvalidDataException($"{file} doesn't read");
            if (oldPrn is null || !pose.Has(oldPrn)) { problems.Add($"{file}: names no bone it rides ({oldPrn})"); return result; }
            var ride = pose.World[oldPrn];
            bool flipped = ride.GetDeterminant() < 0;
            foreach (var p in oldParts)
            {
                if (p.Verts.Count == 0 || p.Tris.Count == 0) continue;
                var nv = p.Normals.Count == p.Verts.Count ? p.Normals.Select(x => Vector3.TransformNormal(x, ride)).Select(x => x.LengthSquared() > 1e-10f ? Vector3.Normalize(x) : Vector3.UnitZ).ToArray() : [];
                result.Add(Dress(p.Name, p.Verts.Select(x => Vector3.Transform(x, ride)).ToArray(), nv,
                    Enumerable.Range(0, p.Verts.Count).Select(i => i < p.Uvs.Count ? p.Uvs[i] : Vector2.Zero).ToArray(),
                    p.Tris.SelectMany(t => flipped ? new int[] { t.Item1, t.Item3, t.Item2 } : [t.Item1, t.Item2, t.Item3]).ToArray(),
                    Enumerable.Range(0, p.Verts.Count).Select(_ => new[] { (oldPrn, 1f) }).ToArray(), p.Texture ?? "",
                    (p.Glossiness, p.Alpha, p.Emissive), p.AlphaFlags is { } f ? (f, p.AlphaThreshold) : null));
            }
            return result;
        }
        var nif = Ob10Nif.Read(bytes, file);
        string? prn = nif.Blocks.OfType<Ob10Nif.Block>().FirstOrDefault(b => b.Type == "NiStringExtraData" && b.Name == "Prn")?.Value;
        var parents = new Dictionary<int, Ob10Nif.Node>();
        foreach (var node in nif.Blocks.OfType<Ob10Nif.Node>()) foreach (int child in node.Children) parents.TryAdd(child, node);
        // Gamebryo's rotation matrix is applied to column vectors, ours to row vectors.
        static Matrix4x4 Local(Ob10Nif.AvObject o) => Matrix4x4.CreateScale(o.Scale) * Matrix4x4.Transpose(o.R) * Matrix4x4.CreateTranslation(o.T);
        foreach (var shape in nif.Blocks.OfType<Ob10Nif.Shape>())
        {
            if (shape.Type is not ("NiTriStrips" or "NiTriShape") || (shape.Flags & 1) != 0 || nif.Get<Ob10Nif.GeomData>(shape.Data) is not { Verts.Count: > 0, Tris.Count: > 0 } geom) continue;
            if (nif.Get<Ob10Nif.SkinInstance>(shape.Skin) is { } skin && nif.Get<Ob10Nif.SkinData>(skin.Data) is { } skinData)
            {
                result.Add(SkinnedOlder(nif, shape, geom, skin, skinData, pose, ref unweighted));
                continue;
            }
            var place = Local(shape);
            string? holder = null;
            for (var at = shape.Index; parents.TryGetValue(at, out var parent); at = parent.Index)
            {
                if (parents.ContainsKey(parent.Index) && pose.Has(parent.Name)) { holder = parent.Name; break; }
                place *= Local(parent);
            }
            holder ??= prn is not null && pose.Has(prn) ? prn : null;
            if (holder is null) { problems.Add($"{file}: rigid shape {shape.Name} names no bone it rides"); continue; }
            place *= pose.World[holder];
            bool mirrored = place.GetDeterminant() < 0;
            int n = geom.Verts.Count;
            var v = geom.Verts.Select(p => Vector3.Transform(p, place)).ToArray();
            var nr = geom.Normals.Count == n ? geom.Normals.Select(x => Vector3.TransformNormal(x, place)).Select(x => x.LengthSquared() > 1e-10f ? Vector3.Normalize(x) : Vector3.UnitZ).ToArray() : [];
            var uv = Enumerable.Range(0, n).Select(i => i < geom.Uvs.Count ? geom.Uvs[i] : Vector2.Zero).ToArray();
            var tris = geom.Tris.SelectMany(t => mirrored ? new int[] { t.Item1, t.Item3, t.Item2 } : [t.Item1, t.Item2, t.Item3]).ToArray();
            var props = shape.Props.Select(i => nif.Get<Ob10Nif.Property>(i)).OfType<Ob10Nif.Property>().ToList();
            var tex = props.FirstOrDefault(p => p.Type == "NiTexturingProperty") is { } tp ? nif.Get<Ob10Nif.Property>(tp.BaseTexture)?.File ?? "" : "";
            var mp = props.FirstOrDefault(p => p.Type == "NiMaterialProperty");
            var ap = props.FirstOrDefault(p => p.Type == "NiAlphaProperty");
            string bone = holder;
            result.Add(Dress(shape.Name.Length > 0 ? shape.Name : $"Shape{shape.Index}", v, nr, uv, tris, Enumerable.Range(0, n).Select(_ => new[] { (bone, 1f) }).ToArray(), tex,
                mp is null ? null : (mp.Glossiness, mp.Alpha, mp.Emissive), ap is null ? null : (ap.Flags, ap.Threshold)));
        }
        return result;
    }

    /// <summary>A skinned shape of a file nifly can't read, in the same pose as the rest: v * skin-to-bone * the bone's pose, by weight.</summary>
    WornShape SkinnedOlder(Ob10Nif nif, Ob10Nif.Shape shape, Ob10Nif.GeomData geom, Ob10Nif.SkinInstance skin, Ob10Nif.SkinData data, BindSkeleton pose, ref int unweighted)
    {
        int n = geom.Verts.Count;
        var names = skin.Bones.Select(b => nif.Get<Ob10Nif.Node>(b)?.Name ?? "").ToList();
        var acc = new List<(int Bone, float W)>[n];
        for (int i = 0; i < n; i++) acc[i] = [];
        for (int b = 0; b < data.Bones.Count && b < names.Count; b++)
            if (pose.Has(names[b]))
                foreach (var (vertex, weight) in data.Bones[b].Weights)
                    if (vertex < n && weight > 0) acc[vertex].Add((b, weight));
        var v = new Vector3[n]; var nr = new Vector3[n];
        var weights = new (string, float)[n][];
        bool normals = geom.Normals.Count == n;
        for (int i = 0; i < n; i++)
        {
            var top = acc[i].OrderByDescending(x => x.W).Take(4).ToList();
            float total = top.Sum(x => x.W);
            if (total <= 0) { unweighted++; top = [(names.FindIndex(pose.Has), 1f)]; total = 1; }
            Vector3 at = Vector3.Zero, turned = Vector3.Zero;
            foreach (var (b, w) in top)
            {
                var m = data.Bones[b].SkinToBone * pose.World[names[b]];
                at += Vector3.Transform(geom.Verts[i], m) * (w / total);
                if (normals) turned += Vector3.TransformNormal(geom.Normals[i], m) * (w / total);
            }
            v[i] = at;
            nr[i] = turned.LengthSquared() > 1e-10f ? Vector3.Normalize(turned) : Vector3.UnitZ;
            weights[i] = top.Select(x => (names[x.Bone], x.W / total)).ToArray();
        }
        var props = shape.Props.Select(i => nif.Get<Ob10Nif.Property>(i)).OfType<Ob10Nif.Property>().ToList();
        var tex = props.FirstOrDefault(p => p.Type == "NiTexturingProperty") is { } tp ? nif.Get<Ob10Nif.Property>(tp.BaseTexture)?.File ?? "" : "";
        var mp = props.FirstOrDefault(p => p.Type == "NiMaterialProperty");
        var ap = props.FirstOrDefault(p => p.Type == "NiAlphaProperty");
        return Dress(shape.Name.Length > 0 ? shape.Name : $"Shape{shape.Index}", v, normals ? nr : [], Enumerable.Range(0, n).Select(i => i < geom.Uvs.Count ? geom.Uvs[i] : Vector2.Zero).ToArray(),
            geom.Tris.SelectMany(t => new int[] { t.Item1, t.Item2, t.Item3 }).ToArray(), weights, tex,
            mp is null ? null : (mp.Glossiness, mp.Alpha, mp.Emissive), ap is null ? null : (ap.Flags, ap.Threshold));
    }

    /// <summary>A shape's textures and material, as Oblivion's statics and worn models are converted: specular from the normal
    /// map's alpha, a glow map as a glow map; a ghost's and a wraith's glow is their material's (no glow map).</summary>
    WornShape Dress(string name, Vector3[] v, Vector3[] n, Vector2[] uv, int[] tris, (string, float)[][] weights, string texture,
        (float Gloss, float Alpha, Vector3 Emissive)? material, (ushort Flags, byte Threshold)? alpha)
    {
        string? diffuse = texture.Length > 0 ? textures.Ensure(texture) : null;
        string? glow = texture.Length > 0 ? textures.Glow(texture) : null;
        var ws = new WornShape
        {
            Name = name, V = v, N = n, Uv = uv, Tris = tris, Weights = weights,
            Textures = glow is not null
                ? [diffuse ?? textures.BlankDiffuse(), texture.Length > 0 ? textures.Normal(texture) : textures.Flat(), glow]
                : [diffuse ?? textures.BlankDiffuse(), texture.Length > 0 ? textures.Normal(texture) : textures.Flat()],
            Flags1 = 0x82400303,
            Flags2 = 0x8001u | (glow is not null ? 0x40u : 0),
            ShaderType = glow is not null ? 2u : 0u,
            SpecularStrength = 1,
            EmissiveMultiple = glow is not null ? 1 : 0,
            Emissive = glow is not null ? Vector3.One : Vector3.Zero,
        };
        if (material is { } m)
        {
            ws.Glossiness = Math.Max(1, m.Gloss);
            ws.Alpha = m.Alpha;
            if (glow is null && m.Emissive.X + m.Emissive.Y + m.Emissive.Z > 0.03f) { ws.Emissive = m.Emissive; ws.EmissiveMultiple = 1; }
        }
        if (alpha is { } a)
        {
            ws.AlphaFlags = a.Flags; ws.AlphaThreshold = a.Threshold;
            if ((a.Flags & 1) != 0 && (a.Flags & 0x200) == 0) ws.Flags2 &= ~0x1u;   // blended: no depth write
        }
        return ws;
    }
}

/// <summary>
/// Moves a creature's shapes from its Oblivion skeleton onto a Skyrim one (<see cref="SkyrimRig"/>), so Skyrim's
/// animations drive them. All of Oblivion's creatures are 3ds Max Biped rigs ("Bip01 ..", the horse's "Bip02 ..", a
/// quadruped's forelegs its arms), so a skeleton's roles come from its bones' names.
/// <list type="bullet">
/// <item>Bone map: each role's chain of source bones onto the target's. Equal counts pair bone to bone; otherwise both
/// chains are measured by length from their start and each source bone covers the stretch of the target chain at the
/// same fractions, its vertices weighted to the target bones by how far along it they lie, crossfading over a fifth of a
/// bone at each target joint. A leg's extra toes are dropped from the end first. Bones with no role (ears, wings, cloth,
/// the spider daedra's whole lower half) follow their nearest mapped ancestor rigidly; a bone with none, the pelvis.</item>
/// <item>Geometry: the whole body is first scaled by the ratio of the two skeletons' pelvis-to-head paths (mean distance
/// of the matched joints from the pelvis where one has no head). Each bone then moves rigidly so its start lands on the
/// target joint: turned as <see cref="WornMesh"/>'s worn bones are (the difference of the two bones' anatomical frames)
/// and stretched along itself to the target's length. An end bone turns with the one above it; the head keeps its
/// own facing, since both games' heads face +Y at rest and a neck's lean is not the head's. Vertices blend their bones'
/// moves by weight, as skinning does.</item>
/// <item>The jaw moves with the head (they must stay one head) and only its weights go to the target's jaw.</item>
/// </list>
/// </summary>
public static class ObCreatureRetarget
{
    /// <summary>A bone's name without its Biped prefix, in lower case ("Bip02 L Thigh" is "l thigh"; "Bip01Calf L1" "calf l1").</summary>
    public static string Normal(string bone) => Regex.Replace(bone, @"^Bip0\d\s*", "", RegexOptions.IgnoreCase).Trim().ToLowerInvariant();

    public sealed record Result(List<WornShape> Shapes, float Scale, float MinStretch, float MaxStretch, IReadOnlyList<string> Pairs, IReadOnlyList<string> Rigid, IReadOnlyList<string> MissingTarget);

    sealed class Move
    {
        public Matrix4x4 Map, Turn;
        public string[] Bones = [];
        /// <summary>A long source bone over several target bones: the target chain's fractions, and this bone's.</summary>
        public float[] Knots = [];
        public float U0, U1;
        public Vector3 A, B;
        public bool Along;
    }

    const float CrossFade = 0.2f;

    /// <summary>Lengths along a polyline from its start, as fractions of the whole.</summary>
    static float[] Arc(IReadOnlyList<Vector3> p)
    {
        var u = new float[p.Count];
        for (int i = 1; i < p.Count; i++) u[i] = u[i - 1] + Vector3.Distance(p[i - 1], p[i]);
        float total = u[^1];
        for (int i = 0; i < u.Length; i++) u[i] = total > 1e-5f ? u[i] / total : (p.Count > 1 ? i / (float)(p.Count - 1) : 0);
        return u;
    }

    static Vector3 At(IReadOnlyList<Vector3> p, float[] u, float x)
    {
        for (int i = 0; i + 1 < p.Count; i++)
            if (x <= u[i + 1] || i + 2 == p.Count)
                return Vector3.Lerp(p[i], p[i + 1], u[i + 1] - u[i] > 1e-6f ? Math.Clamp((x - u[i]) / (u[i + 1] - u[i]), 0, 1) : 0);
        return p[^1];
    }

    /// <summary>The target bones a vertex at <paramref name="p"/> on this source bone goes to, with their shares.</summary>
    static (string Bone, float Share)[] Targets(Move m, Vector3 p)
    {
        if (!m.Along || m.Bones.Length == 1) return [(m.Bones[0], 1)];
        var d = m.B - m.A;
        float t = d.LengthSquared() > 1e-8f ? Math.Clamp(Vector3.Dot(p - m.A, d) / d.LengthSquared(), 0, 1) : 0;
        float u = m.U0 + (m.U1 - m.U0) * t;
        int n = m.Bones.Length;
        int k = 0;
        while (k + 1 < n && k + 1 < m.Knots.Length && u >= m.Knots[k + 1]) k++;
        float lo = m.Knots[k], hi = k + 1 < m.Knots.Length ? m.Knots[k + 1] : 1;
        float f = CrossFade * (hi - lo);
        if (f > 1e-6f)
        {
            if (u > hi - f && k + 1 < n) { float w = Math.Clamp((u - (hi - f)) / (2 * f), 0, 0.5f); return [(m.Bones[k], 1 - w), (m.Bones[k + 1], w)]; }
            if (u < lo + f && k > 0) { float w = Math.Clamp((lo + f - u) / (2 * f), 0, 0.5f); return [(m.Bones[k], 1 - w), (m.Bones[k - 1], w)]; }
        }
        return [(m.Bones[k], 1)];
    }

    /// <summary>Where a head points from its joint: to its foremost tenth of vertices, the snout. Their mean rather than the whole
    /// head's: the clannfear's great frill runs back from its head, and the head's mean pointed behind it and flipped it.</summary>
    public static Vector3 Snout(IReadOnlyList<Vector3> fromJoint)
    {
        int count = Math.Max(1, fromJoint.Count / 10);
        var mean = fromJoint.OrderByDescending(v => v.Y).Take(count).Aggregate(Vector3.Zero, (a, v) => a + v) / count;
        return mean.LengthSquared() > 1e-6f ? Vector3.Normalize(mean) : Vector3.UnitY;
    }

    /// <summary>The turn that points the source head where the vanilla head points (<see cref="Snout"/>): the shortest one. A neck's
    /// lean says nothing of the head on it (Oblivion's bear looks up at the sky at rest, Skyrim's straight ahead). Only the shape
    /// with the most head vertices counts: a horn or a helmet isn't the face.</summary>
    static Matrix4x4 HeadTurn(IReadOnlyList<WornShape> shapes, string head, string? jaw, BindSkeleton from, Vector3? vanilla)
    {
        if (vanilla is not { } wanted) return Matrix4x4.Identity;
        var joint = from.Position(head);
        bool OnHead(WornShape s, int i)
        {
            var top = s.Weights[i].MaxBy(w => w.W).Bone;
            return top.Equals(head, StringComparison.OrdinalIgnoreCase) || (jaw is not null && top.Equals(jaw, StringComparison.OrdinalIgnoreCase));
        }
        var best = shapes.Select(s => (Shape: s, Count: Enumerable.Range(0, s.V.Length).Count(i => OnHead(s, i)))).MaxBy(x => x.Count);
        if (best.Count == 0) return Matrix4x4.Identity;
        var d = Snout(Enumerable.Range(0, best.Shape.V.Length).Where(i => OnHead(best.Shape, i)).Select(i => best.Shape.V[i] - joint).ToList());
        var axis = Vector3.Cross(d, wanted);
        if (axis.LengthSquared() < 1e-8f) return Matrix4x4.Identity;
        float angle = MathF.Atan2(axis.Length(), Vector3.Dot(d, wanted));
        axis = Vector3.Normalize(axis);
        var a1 = Matrix4x4.CreateFromAxisAngle(axis, angle);
        var a2 = Matrix4x4.CreateFromAxisAngle(axis, -angle);
        return Vector3.Dot(Vector3.TransformNormal(d, a1), wanted) >= Vector3.Dot(Vector3.TransformNormal(d, a2), wanted) ? a1 : a2;
    }

    public static Result Run(IReadOnlyList<WornShape> shapes, BindSkeleton from, BindSkeleton to, SkyrimRig rig, string folder, Vector3? vanillaHead)
    {
        // ---- the two skeletons by role ----
        var byNorm = new Dictionary<string, string>();
        foreach (var bone in from.World.Keys) byNorm.TryAdd(Normal(bone), bone);
        string? N(params string[] names) => names.Select(x => byNorm.GetValueOrDefault(x)).FirstOrDefault(x => x is not null);
        string[] Seq(params string?[] names) => names.OfType<string>().ToArray();
        var missing = new List<string>();
        string[] T(string[] names)
        {
            var found = new List<string>();
            foreach (var x in names)
                if (SkyrimRig.Find(to, x) is { } b) found.Add(b); else missing.Add(x);
            return [.. found];
        }
        string? T1(string? name) => name is null ? null : T([name]).FirstOrDefault();

        string pelvisTarget = T1(rig.Pelvis) ?? throw new InvalidDataException($"the {rig.Key} rig's pelvis {rig.Pelvis} isn't on the skeleton");
        string pelvisSource = N("pelvis", "spine0") ?? throw new InvalidDataException("no pelvis or Spine0 in its skeleton");
        string[] srcSpine = byNorm.ContainsKey("spine01") ? Seq(N("spine01"), N("spine02"), N("spine03")) : Seq(N("spine"), N("spine1"), N("spine2"), N("spine3"));
        string[] srcNeck = Seq(N("neck"), N("neck1"), N("neck2"), N("neck3"), N("neck4"));
        string? srcHead = N("head", "skull"), tgtHead = T1(rig.Head);
        string? srcJaw = N("jaw", "jawbone", "jawbase", "mouth bottom", "mouth", "jaw01", "chinbone01"), tgtJaw = T1(rig.Jaw);
        string[] tgtSpine = T(rig.Spine), tgtNeck = T(rig.Neck), tgtTail = T(rig.Tail);
        string[] srcTail = byNorm.ContainsKey("tail01") ? Seq(N("tail01")) : Seq(N("tail"), N("tail1"), N("tail2"), N("tail3"), N("tail4"), N("tail5"));
        string[] Arm(string s, int targetCount)
        {
            var arm = Seq(N($"{s} clavicle", $"{s}clavicle"), N($"{s} upperarm", $"{s}upperarm"), N($"{s} forearm", $"{s} forarm", $"{s}forearm"), N($"{s} hand", $"{s}hand"));
            return targetCount > 4 ? [.. arm, .. Seq(N($"{s} finger0"), N($"{s} finger01"))] : arm;
        }
        string[] Leg(string s, int targetCount)
        {
            var leg = Seq(N($"{s} thigh"), N($"{s} calf"), N($"{s} horselink"), N($"{s} foot"), N($"{s} toe0"), N($"{s} toe01"), N($"{s} toe02")).ToList();
            while (leg.Count > Math.Max(1, targetCount) && Normal(leg[^1]).Contains("toe")) leg.RemoveAt(leg.Count - 1);
            return [.. leg];
        }
        var chains = new List<(string Role, string[] Src, string[] Tgt, string? SrcEnd, string? TgtEnd)>
        {
            ("spine", srcSpine, tgtSpine, srcNeck.FirstOrDefault() ?? srcHead, tgtNeck.FirstOrDefault() ?? tgtHead),
            ("neck", srcNeck, tgtNeck, srcHead, tgtHead),
            ("tail", srcTail, tgtTail, null, null),
        };
        foreach (var (side, armT, legT) in new[] { ("l", T(rig.ArmL), T(rig.LegL)), ("r", T(rig.ArmR), T(rig.LegR)) })
        {
            chains.Add(("arm " + side, Arm(side, armT.Length), armT, null, null));
            if (legT.Length > 0) chains.Add(("leg " + side, Leg(side, legT.Length), legT, null, null));
            if (rig.Fingers.Length > 0)
                for (int f = 0; f < 5; f++)
                {
                    var src = Seq(N($"{side} finger{f}"), N($"{side} finger{f}1"), N($"{side} finger{f}2"));
                    var tgt = Enumerable.Range(0, 3).Select(j => SkyrimRig.Find(to, $"{rig.Fingers}{side.ToUpperInvariant()} Finger{f}{j}")).OfType<string>().ToArray();
                    if (src.Length > 0 && tgt.Length > 0) chains.Add(($"finger {side}{f}", src, tgt, null, null));
                }
            // Many legs: front to back on both, spread evenly over the target's.
            var tgtLegs = (side == "l" ? rig.LegsL : rig.LegsR).Select(T).Where(l => l.Length > 0).OrderByDescending(l => to.Position(l[0]).Y).ToList();
            if (tgtLegs.Count > 0 && SkyrimRig.SourceLegs.TryGetValue(folder, out var legs))
            {
                var srcLegs = legs(side).Select(names => names.Select(x => N(x)).OfType<string>().ToArray()).Where(l => l.Length > 0).OrderByDescending(l => from.Position(l[0]).Y).ToList();
                for (int i = 0; i < srcLegs.Count; i++)
                    chains.Add(($"leg {side}{i}", srcLegs[i], tgtLegs[srcLegs.Count == 1 ? 0 : (int)MathF.Round(i * (tgtLegs.Count - 1) / (float)(srcLegs.Count - 1))], null, null));
            }
        }
        foreach (var (role, names) in rig.Extra)
        {
            var src = role switch
            {
                "Ponytail1" => Seq(N("ponytail1"), N("ponytail11"), N("ponytail12")),
                "Ponytail2" => Seq(N("ponytail2"), N("ponytail21"), N("ponytail22")),
                _ => [],
            };
            chains.Add((role, src, T(names), null, null));
        }
        chains.RemoveAll(c => c.Src.Length == 0 || c.Tgt.Length == 0);
        // A many-legged creature's arm can claim the bones of no chain; a bone is only ever one chain's.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < chains.Count; i++)
        {
            var c = chains[i];
            chains[i] = (c.Role, c.Src.Where(taken.Add).ToArray(), c.Tgt, c.SrcEnd, c.TgtEnd);
        }
        chains.RemoveAll(c => c.Src.Length == 0);

        // ---- the size ----
        Vector3 pS = from.Position(pelvisSource), pT = to.Position(pelvisTarget);
        float Path(Vector3 start, IEnumerable<Vector3> joints)
        {
            float len = 0; var at = start;
            foreach (var j in joints) { len += Vector3.Distance(at, j); at = j; }
            return len;
        }
        float scale;
        if (srcHead is not null && tgtHead is not null && (srcSpine.Length + srcNeck.Length) > 0 && (tgtSpine.Length + tgtNeck.Length) > 0)
            scale = Path(pT, tgtSpine.Concat(tgtNeck).Append(tgtHead).Select(to.Position)) / Path(pS, srcSpine.Concat(srcNeck).Append(srcHead).Select(from.Position));
        else
        {
            float sum = 0, tum = 0;
            foreach (var c in chains)
                for (int i = 0; i < Math.Min(c.Src.Length, c.Tgt.Length); i++) { sum += Vector3.Distance(from.Position(c.Src[i]), pS); tum += Vector3.Distance(to.Position(c.Tgt[i]), pT); }
            scale = sum > 1e-3f ? tum / sum : 1;
        }
        if (!(scale > 1e-3f) || float.IsInfinity(scale)) throw new InvalidDataException($"its size against the {rig.Key} rig came to {scale}");

        // ---- the moves ----
        var moves = new Dictionary<string, Move>(StringComparer.OrdinalIgnoreCase);
        float minStretch = 1, maxStretch = 1;
        var pairs = new List<string>();
        var uniform = Matrix4x4.CreateScale(scale);
        Move Still(string source, Vector3 a, Vector3 A, Matrix4x4 turn, string[] bones, float[]? knots = null) => new()
        {
            Map = Matrix4x4.CreateTranslation(-a) * uniform * turn * Matrix4x4.CreateTranslation(A), Turn = turn, Bones = bones, Knots = knots ?? [], A = a, B = a,
        };
        moves[pelvisSource] = Still(pelvisSource, pS, pT, Matrix4x4.Identity, [pelvisTarget]);
        pairs.Add($"{pelvisSource} -> {pelvisTarget}");
        foreach (var (role, src, tgt, srcEnd, tgtEnd) in chains)
        {
            var sp = src.Select(from.Position).ToList();
            var tp = tgt.Select(to.Position).ToList();
            bool ended = srcEnd is not null && tgtEnd is not null;
            if (ended) { sp.Add(from.Position(srcEnd!)); tp.Add(to.Position(tgtEnd!)); }
            bool same = src.Length == tgt.Length;
            float[] us = Arc(sp), ut = Arc(tp);
            Matrix4x4? previous = null;
            for (int i = 0; i < src.Length; i++)
            {
                Vector3 a = sp[i];
                bool hasNext = i + 1 < sp.Count;
                Vector3 A = same ? tp[i] : At(tp, ut, us[i]);
                string[] bones = same ? [tgt[i]] : tgt;
                if (!hasNext)
                {
                    // An end bone: no next joint to run to, so it turns with the bone above it.
                    moves[src[i]] = Still(src[i], a, A, previous ?? Matrix4x4.Identity, same ? bones : [tgt[^1]]);
                    pairs.Add($"{src[i]} -> {string.Join("+", moves[src[i]].Bones)} (end bone)");
                    continue;
                }
                Vector3 b = sp[i + 1];
                Vector3 B = same ? tp[i + 1] : At(tp, ut, us[i + 1]);
                Vector3 d = b - a, D = B - A;
                if (d.LengthSquared() < 1e-6f || D.LengthSquared() < 1e-6f)
                {
                    moves[src[i]] = Still(src[i], a, A, previous ?? Matrix4x4.Identity, bones, same ? null : ut);
                    moves[src[i]].Along = !same; moves[src[i]].U0 = us[i]; moves[src[i]].U1 = us[i + 1]; moves[src[i]].B = b;
                    continue;
                }
                bool up = MathF.Abs(Vector3.Dot(Vector3.Normalize(d), Vector3.UnitY)) > 0.7f || MathF.Abs(Vector3.Dot(Vector3.Normalize(D), Vector3.UnitY)) > 0.7f;
                var turn = Matrix4x4.Transpose(WornMesh.Frame(d, up)) * WornMesh.Frame(D, up);
                // Along the bone to the target's length (a bone's own shape isn't to be lost to a degenerate ratio).
                float stretch = Math.Clamp(D.Length() / (d.Length() * scale), 0.25f, 4);
                minStretch = MathF.Min(minStretch, stretch); maxStretch = MathF.Max(maxStretch, stretch);
                var axis = Vector3.Normalize(d);
                // A limb drawn out keeps some of its body: across it by the root of the stretch, at most half as much again (the
                // clannfear's little arms, drawn to a big creature's reach, were threads).
                float across = role.StartsWith("arm") || role.StartsWith("leg") ? Math.Clamp(MathF.Sqrt(stretch), 1, 1.5f) : 1;
                var along = Matrix4x4.CreateScale(across) + new Matrix4x4(axis.X * axis.X, axis.X * axis.Y, axis.X * axis.Z, 0, axis.Y * axis.X, axis.Y * axis.Y, axis.Y * axis.Z, 0, axis.Z * axis.X, axis.Z * axis.Y, axis.Z * axis.Z, 0, 0, 0, 0, 0) * (stretch - across);
                moves[src[i]] = new Move
                {
                    Map = Matrix4x4.CreateTranslation(-a) * uniform * along * turn * Matrix4x4.CreateTranslation(A),
                    Turn = turn, Bones = bones, Knots = same ? [] : ut, U0 = us[i], U1 = us[i + 1], A = a, B = b, Along = !same,
                };
                pairs.Add($"{src[i]} -> {string.Join("+", bones)} x{stretch:F2}");
                previous = turn;
            }
        }
        if (srcHead is not null && tgtHead is not null)
        {
            moves[srcHead] = Still(srcHead, from.Position(srcHead), to.Position(tgtHead), HeadTurn(shapes, srcHead, srcJaw, from, vanillaHead), [tgtHead]);
            pairs.Add($"{srcHead} -> {tgtHead}");
            if (srcJaw is not null && tgtJaw is not null)
            {
                moves[srcJaw] = new Move { Map = moves[srcHead].Map, Turn = Matrix4x4.Identity, Bones = [tgtJaw], Knots = [] };
                pairs.Add($"{srcJaw} -> {tgtJaw}");
            }
        }

        // ---- the vertices ----
        var rigid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Move MoveOf(string bone)
        {
            string at = bone;
            string? wingRoot = null;
            for (int guard = 0; guard < 64; guard++)
            {
                if (moves.TryGetValue(at, out var m))
                {
                    if (guard > 0) rigid.Add($"{bone} -> {string.Join("+", m.Bones)}");
                    if (wingRoot is null) return m;
                    // Wings stay on the spine without its stoop: the imp's, carried by the riekling's bent back, swung forward over
                    // its head. Where the root lands is the bone's business; which way the wing points stays its own.
                    var root = from.Position(wingRoot);
                    moves[wingRoot] = Still(wingRoot, root, Vector3.Transform(root, m.Map), Matrix4x4.Identity, m.Bones, m.Knots);
                    return moves[wingRoot];
                }
                if (Regex.IsMatch(Normal(at), @"^(wing |[lr]wing)")) wingRoot = at;
                if (!from.Parent.TryGetValue(at, out var up)) break;
                at = up;
            }
            rigid.Add($"{bone} -> {pelvisTarget}");
            return moves[pelvisSource];
        }
        var result = new List<WornShape>();
        foreach (var s in shapes)
        {
            var v = new Vector3[s.V.Length];
            var n = new Vector3[s.V.Length];
            var weights = new (string, float)[s.V.Length][];
            bool normals = s.N.Length == s.V.Length;
            for (int i = 0; i < s.V.Length; i++)
            {
                Vector3 pos = Vector3.Zero, nrm = Vector3.Zero;
                var acc = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                float total = 0;
                foreach (var (bone, w) in s.Weights[i])
                {
                    if (w <= 0) continue;
                    var m = MoveOf(bone);
                    pos += Vector3.Transform(s.V[i], m.Map) * w;
                    if (normals) nrm += Vector3.TransformNormal(s.N[i], m.Turn) * w;
                    foreach (var (t, share) in Targets(m, s.V[i])) acc[t] = acc.GetValueOrDefault(t) + w * share;
                    total += w;
                }
                v[i] = pos / total;
                n[i] = nrm.LengthSquared() > 1e-10f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                var top = acc.OrderByDescending(kv => kv.Value).Take(4).ToList();
                float sum = top.Sum(kv => kv.Value);
                weights[i] = top.Select(kv => (kv.Key, kv.Value / sum)).ToArray();
            }
            var moved = s.With(v, normals ? n : []);
            moved.Weights = weights;
            result.Add(moved);
        }
        return new Result(result, scale, minStretch, maxStretch, pairs, [.. rigid.Order(StringComparer.OrdinalIgnoreCase)], missing.Distinct().ToList());
    }
}

/// <summary>
/// Oblivion's creatures with their own look (ledger O67): each distinct set of body parts re-rigged onto the Skyrim
/// creature skeleton of the nearest shape (<see cref="CreatureLook.Table"/>). The actor stays a copy of a vanilla creature;
/// its race is a copy of that creature's race, so behaviour, animations, attack data and body part data stay Skyrim's,
/// sized to Oblivion's through its height; its skin is a copy of the vanilla skin armour with one addon, wearing the new
/// NIF. A creature that can't be re-rigged keeps its stand-in, with the reason in Problems.
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>One converted body: the file, the vanilla actor and race it is built on, and what it took.</summary>
    public sealed record CreatureBody(string Nif, INpcGetter Template, INpcGetter Actor, IRaceGetter Race, string SkeletonFile, BindSkeleton Target, SkyrimRig Rig, ObCreatureRetarget.Result Retarget, int Unweighted, string Folder);

    ObCreatureSource? creatureSource;
    readonly Dictionary<string, CreatureBody?> creatureBodies = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> creatureFailures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, BindSkeleton> creatureSkeletons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Race> creatureRaces = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Armor> creatureSkins = new(StringComparer.OrdinalIgnoreCase);
    public int CreatureLooksMade { get; private set; }
    public int CreatureStandInsKept { get; private set; }

    /// <summary>The actor a template really takes its traits from (through templates and leveled lists).</summary>
    INpcGetter TraitsActor(INpcGetter npc)
    {
        for (int guard = 0; guard < 16; guard++)
        {
            if (npc.Template.IsNull || !npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) return npc;
            var key = npc.Template.FormKey;
            for (int inner = 0; inner < 16 && Vanilla.ByKey<ILeveledNpcGetter>(key) is { } list; inner++)
                key = list.Entries?.FirstOrDefault()?.Data?.Reference.FormKey ?? FormKey.Null;
            if (Vanilla.ByKey<INpcGetter>(key) is not { } next) return npc;
            npc = next;
        }
        return npc;
    }

    static string MeshPath(string file)
    {
        string p = ObData.Normalize(file);
        return p.StartsWith(@"meshes\") ? p : @"meshes\" + p;
    }

    /// <summary>The first three steps of a skeleton's path ("meshes\actors\canine"): the same family of bones.</summary>
    static string SkeletonFamily(string file) => string.Join('\\', MeshPath(file).Split('\\').Take(3));

    string RaceSkeleton(IRaceGetter race) => MeshPath(race.SkeletalModel?.Male?.File.DataRelativePath.Path ?? race.SkeletalModel?.Female?.File.DataRelativePath.Path
        ?? throw new InvalidDataException($"race {race.EditorID} names no skeleton"));

    BindSkeleton TargetSkeleton(string path)
    {
        if (creatureSkeletons.TryGetValue(path, out var hit)) return hit;
        var bytes = characterAssets!.Read(path) ?? throw new FileNotFoundException(path);
        BindSkeleton result = null!;
        SkyrimCharacterAssets.Locked(() =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_obcrea_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) throw new InvalidDataException($"nifly can't read {path}");
                result = BindSkeleton.FromNif(nif);
            }
            finally { File.Delete(tmp); }
        });
        return creatureSkeletons[path] = result;
    }

    readonly Dictionary<string, Vector3?> vanillaHeads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which way the vanilla body's head points from its joint (<see cref="ObCreatureRetarget.Snout"/>), from the vertices
    /// weighted mostly to the head, in the shape's global space, as the bone's bind (both through the global-to-skin transform: many
    /// creature bodies have one).</summary>
    Vector3? VanillaHead(INpcGetter actor, IRaceGetter race, BindSkeleton target, SkyrimRig rig, string skeleton)
    {
        string key = $"{skeleton}|{race.FormKey}";
        if (vanillaHeads.TryGetValue(key, out var known)) return known;
        Vector3? result = null;
        string? head = rig.Head is null ? null : SkyrimRig.Find(target, rig.Head);
        var addon = SkinAddons(VanillaSkin(actor, race), race).FirstOrDefault();
        if (head is not null && addon?.WorldModel?.Male?.File.DataRelativePath.Path is { Length: > 0 } file && characterAssets!.Read(MeshPath(file)) is { } bytes)
            SkyrimCharacterAssets.Locked(() =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"tm_obcrea_{Guid.NewGuid():N}.nif");
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    using var nif = new NifFile();
                    if (nif.Load(tmp) != 0) return;
                    var hdr = nif.GetHeader();
                    using var all = nif.GetShapes();
                    using var cache = new niflycpp.BlockCache(nif.GetHeader());
                    var onHead = new List<Vector3>();
                    for (int si = 0; si < all.Count; si++)
                    {
                        var shape = all[si];
                        using var names = new vectorstring();
                        nif.GetShapeBoneList(shape, names);
                        int bone = names.ToList().FindIndex(b => b.Equals(head, StringComparison.OrdinalIgnoreCase));
                        var tri = cache.EditableBlockById<BSTriShape>(hdr.GetBlockID(shape));
                        var verts = nif.GetVertsForShape(shape);
                        if (bone < 0 || tri is null || verts is null || tri.vertData.Count != verts.Count) continue;
                        var stb = new MatTransform();
                        nif.GetShapeTransformSkinToBone(shape, (uint)bone, stb);
                        var g2s = new MatTransform();
                        var toGlobal = nif.GetShapeTransformGlobalToSkin(shape, g2s) && Matrix4x4.Invert(WornMesh.ToMatrix(g2s), out var inv) ? inv : Matrix4x4.Identity;
                        if (!Matrix4x4.Invert(WornMesh.ToMatrix(stb), out var bind)) continue;
                        var joint = (bind * toGlobal).Translation;
                        for (int i = 0; i < verts.Count; i++)
                        {
                            var w = tri.vertData[i].weights.ToArray(); var wb = tri.vertData[i].weightBones.ToArray();
                            int top = Array.IndexOf(w, w.Max());
                            if (wb[top] != bone) continue;
                            onHead.Add(Vector3.Transform(new Vector3(verts[i].x, verts[i].y, verts[i].z), toGlobal) - joint);
                        }
                    }
                    if (onHead.Count > 0) result = ObCreatureRetarget.Snout(onHead);
                }
                finally { File.Delete(tmp); }
            });
        return vanillaHeads[key] = result;
    }

    /// <summary>The parts of another creature on the same skeleton, for a record whose own name nothing in the game's data or nothing
    /// at all: 20 Grummite Whelp patrol templates name the goblin's parts in the grummite folder, Steel Fang has no NIFZ. Its
    /// namesake's first, else the first on that skeleton with every part there.</summary>
    List<string>? TwinParts(Ob.ICreatureGetter c)
    {
        string skeleton = ObData.Normalize(c.Model?.File ?? "");
        string folder = Path.GetDirectoryName(MeshPath(skeleton))!;
        return records.Values.OfType<Ob.ICreatureGetter>()
            .Where(o => ObData.Normalize(o.Model?.File ?? "") == skeleton && o.Models is { Count: > 0 } parts && parts.All(m => data.Exists(Path.Combine(folder, ObData.Normalize(m.ToString() ?? "")))))
            .OrderBy(o => o.Name == c.Name ? 0 : 1).ThenBy(o => o.EditorID, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Models?.Select(m => m.ToString()).OfType<string>().ToList();
    }

    /// <summary>A creature's body on its Skyrim skeleton, written once per distinct set of parts; null (the reason in
    /// Problems) when it can't be made. <paramref name="standIn"/>: the creature's stand-in, its actor used instead of the
    /// row's when it stands on the same family of skeleton (a grey horse, a dog among the wolves).</summary>
    CreatureBody? CreatureBodyOf(Ob.ICreatureGetter c, CreatureLook look, string? standIn)
    {
        string skeletonFile = c.Model?.File ?? "";
        var parts = c.Models?.Select(m => m.ToString()).OfType<string>().Where(m => m.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        string key = $"{ObData.Normalize(skeletonFile)}|{string.Join(";", parts).ToLowerInvariant()}|{standIn}";
        if (creatureBodies.TryGetValue(key, out var made))
        {
            if (made is null) Problems.Add($"creature {c.EditorID} ({c.Name}): can't re-rig: {creatureFailures[key]} (stand-in kept)");
            return made;
        }
        try
        {
            characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
            creatureSource ??= new ObCreatureSource(data, meshes.Textures);
            var rig = SkyrimRig.All[look.Rig];
            var template = Vanilla.Get<INpcGetter>(look.Template) ?? throw new InvalidDataException($"template {look.Template} not found");
            var actor = TraitsActor(template);
            var race = Vanilla.ByKey<IRaceGetter>(actor.Race.FormKey) ?? throw new InvalidDataException($"{look.Template}'s race isn't in the vanilla masters");
            string skeleton = RaceSkeleton(race);
            if (standIn is not null && Vanilla.Get<INpcGetter>(standIn) is { } other && TraitsActor(other) is var otherActor
                && Vanilla.ByKey<IRaceGetter>(otherActor.Race.FormKey) is { } otherRace && SkeletonFamily(RaceSkeleton(otherRace)) == SkeletonFamily(skeleton))
            {
                (template, actor, race) = (other, otherActor, otherRace);
                skeleton = RaceSkeleton(race);
            }
            var problems = new List<string>();
            var (source, rest, unweighted) = creatureSource.Read(skeletonFile, parts, problems);
            if (source.Count == 0 && TwinParts(c) is { } twin) (source, rest, unweighted) = creatureSource.Read(skeletonFile, twin, problems);
            Problems.AddRange(problems.Select(p => $"creature {c.EditorID}: {p}"));
            if (source.Count == 0) throw new InvalidDataException("its model has no body to re-rig");
            var target = TargetSkeleton(skeleton);
            var result = ObCreatureRetarget.Run(source, rest, target, rig, look.Folder, VanillaHead(actor, race, target, rig, skeleton));
            if (result.MissingTarget.Count > 0) Problems.Add($"creature {c.EditorID}: the {rig.Key} rig's {string.Join(", ", result.MissingTarget)} aren't on {skeleton}");
            string stem = Sanitize(Path.GetFileNameWithoutExtension(parts.FirstOrDefault() ?? "body")).ToLowerInvariant();
            string nif = $@"{ObMeshConverter.OutFolder}\creatures\{look.Folder}\{stem}{VanillaActors.StableHash(key) % 100000:D5}.nif";
            int partition = SkinPartition(actor, race);
            SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, nif), result.Shapes, target, partition));
            return creatureBodies[key] = new CreatureBody(nif, template, actor, race, skeleton, target, rig, result, unweighted, look.Folder);
        }
        catch (Exception e)
        {
            Problems.Add($"creature {c.EditorID} ({c.Name}): can't re-rig: {e.Message} (stand-in kept)");
            creatureFailures[key] = e.Message;
            return creatureBodies[key] = null;
        }
    }

    IArmorGetter? VanillaSkin(INpcGetter actor, IRaceGetter race) =>
        (actor.WornArmor.IsNull ? null : Vanilla.ByKey<IArmorGetter>(actor.WornArmor.FormKey)) ?? (race.Skin.IsNull ? null : Vanilla.ByKey<IArmorGetter>(race.Skin.FormKey));

    /// <summary>The addons of a skin that dress this race: one skin armour can serve many races with an addon each (the fish's and the
    /// horse's first addons are a person's and a wolf's), and the engine picks by the race, or the race's armor race.</summary>
    IReadOnlyList<IArmorAddonGetter> SkinAddons(IArmorGetter? skin, IRaceGetter race)
    {
        var all = skin?.Armature.Select(l => Vanilla.ByKey<IArmorAddonGetter>(l.FormKey)).OfType<IArmorAddonGetter>().ToList() ?? [];
        var keys = new[] { race.FormKey, race.ArmorRace.FormKey }.Where(k => !k.IsNull).ToList();
        var own = all.Where(a => keys.Contains(a.Race.FormKey) || a.AdditionalRaces.Any(r => keys.Contains(r.FormKey))).ToList();
        return own.Count > 0 ? own : all;
    }

    /// <summary>The slots a creature's skin claims: every addon's, with the armour's (one addon wears the whole body).</summary>
    BipedObjectFlag SkinSlots(IArmorGetter? skin, IRaceGetter race)
    {
        BipedObjectFlag flags = 0;
        foreach (var a in SkinAddons(skin, race)) flags |= a.BodyTemplate?.FirstPersonFlags ?? 0;
        return flags == 0 ? BipedObjectFlag.Body : flags;
    }

    /// <summary>The dismember partition of the body: the body slot, else the lowest slot the skin claims (biped slot n is bit n - 30).</summary>
    int SkinPartition(INpcGetter actor, IRaceGetter race)
    {
        var flags = (uint)SkinSlots(VanillaSkin(actor, race), race);
        if ((flags & (uint)BipedObjectFlag.Body) != 0) return 32;
        for (int bit = 0; bit < 32; bit++) if ((flags & (1u << bit)) != 0) return 30 + bit;
        return 32;
    }

    /// <summary>The race copy for a creature skeleton on a vanilla race: Skyrim's behaviour and animations, Oblivion's size
    /// (the mesh was sized to the Skyrim skeleton, so the race's height takes the actor back to Oblivion's: 1 / scale).</summary>
    Race CreatureRace(Ob.ICreatureGetter c, CreatureBody body)
    {
        string key = $"{body.Race.FormKey}|{body.Folder}";
        if (creatureRaces.TryGetValue(key, out var hit)) return hit;
        var race = mod.Races.DuplicateInAsNewRecord(body.Race);
        race.EditorID = "OBRace" + Sanitize(body.Folder) + Sanitize(body.Race.EditorID ?? "");
        // Its own addons are its own: a race's armor race is who addons are looked up for.
        race.ArmorRace.Clear();
        // Wide limits: Dagon stands 1,129 to the head, several giants.
        float height = Math.Clamp(1 / body.Retarget.Scale, 0.1f, 8);
        race.Height = new GenderedItem<float>(height, height);
        return creatureRaces[key] = race;
    }

    /// <summary>A copy of the vanilla skin armour with one addon (a copy of its first, footsteps and all) wearing the body.</summary>
    Armor CreatureSkin(CreatureBody body, Race race)
    {
        if (creatureSkins.TryGetValue(body.Nif, out var hit)) return hit;
        var vanilla = VanillaSkin(body.Actor, body.Race);
        var addons = SkinAddons(vanilla, body.Race);
        var slots = SkinSlots(vanilla, body.Race);
        string id = "OBSkin" + Sanitize(Path.GetFileNameWithoutExtension(body.Nif));
        var model = new GenderedItem<Model?>(new Model { File = SkyrimModelPath(body.Nif) }, new Model { File = SkyrimModelPath(body.Nif) });
        ArmorAddon addon;
        if (addons.Count > 0)
        {
            addon = mod.ArmorAddons.DuplicateInAsNewRecord(addons[0]);
            addon.EditorID = id + "AA";
            addon.AdditionalRaces.Clear();
            addon.FirstPersonModel = null;
            addon.SkinTexture = null;
            addon.TextureSwapList = null;
        }
        else
        {
            addon = new ArmorAddon(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = id + "AA", Priority = new GenderedItem<byte>(0, 0) };
            mod.ArmorAddons.Add(addon);
        }
        addon.Race = race.ToNullableLink<IRaceGetter>();
        addon.WorldModel = model;
        addon.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, Flags = BodyTemplate.Flag.NonPlayable, ArmorType = ArmorType.Clothing };
        Armor skin;
        if (vanilla is not null) skin = mod.Armors.DuplicateInAsNewRecord(vanilla);
        else
        {
            skin = new Armor(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { MajorFlags = Armor.MajorFlag.NonPlayable };
            mod.Armors.Add(skin);
        }
        skin.EditorID = id;
        skin.Race = race.ToNullableLink<IRaceGetter>();
        skin.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, Flags = BodyTemplate.Flag.NonPlayable, ArmorType = ArmorType.Clothing };
        skin.Armature.Clear();
        skin.Armature.Add(addon.ToLink<IArmorAddonGetter>());
        race.Skin ??= new FormLinkNullable<IArmorGetter>();
        race.Skin.SetTo(skin);
        return creatureSkins[body.Nif] = skin;
    }

    /// <summary>Gives an actor its creature's body: its own race (no template's) and skin, and Oblivion's base scale.</summary>
    void ApplyCreatureLook(Npc npc, Ob.ICreatureGetter c, CreatureBody body)
    {
        var race = CreatureRace(c, body);
        var skin = CreatureSkin(body, race);
        npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
        npc.Race.SetTo(race);
        npc.WornArmor.SetTo(skin);
        npc.AttackRace.SetTo(race);
        if (npc.Voice.IsNull && !body.Actor.Voice.IsNull) npc.Voice.SetTo(body.Actor.Voice.FormKey);
        npc.Height = c.BaseScale is > 0 ? c.BaseScale.Value : 1;
        CreatureLooksMade++;
    }

    // ---- dev aids (ob-creature-test) ----

    /// <summary>The stand-in <see cref="CreatureTable"/> names for a creature, as <c>Creature</c> finds it.</summary>
    static string? CreatureStandIn(Ob.ICreatureGetter c)
    {
        string id = (c.EditorID ?? "").ToLowerInvariant(), name = (c.Name ?? "").ToLowerInvariant();
        return CreatureTable.FirstOrDefault(t => id.Contains(t.Match) || name.Contains(t.Match)).Template;
    }

    IEnumerable<Ob.ICreatureGetter> CreaturesMatching(Regex match) =>
        records.Values.OfType<Ob.ICreatureGetter>().Where(c => match.IsMatch(c.EditorID ?? "") || match.IsMatch(c.Name ?? "") || match.IsMatch(c.Model?.File ?? ""));

    /// <summary>Every skeleton Oblivion's creatures use: how many creatures and part sets, its row, and how it stands (the
    /// pelvis, hand, head and lowest bone's height, and how far forward the head is), for choosing a shape by stance.</summary>
    public IEnumerable<string> CreatureLookList()
    {
        creatureSource ??= new ObCreatureSource(data, meshes.Textures);
        foreach (var g in records.Values.OfType<Ob.ICreatureGetter>().GroupBy(c => ObData.Normalize(c.Model?.File ?? "")).OrderBy(g => g.Key))
        {
            int sets = g.Select(c => string.Join(";", (c.Models?.Select(m => m.ToString()) ?? []).Order())).Distinct().Count();
            string stance = "";
            try
            {
                var s = creatureSource.Skeleton(g.Key);
                var norm = s.World.Keys.GroupBy(ObCreatureRetarget.Normal).ToDictionary(x => x.Key, x => s.Position(x.First()));
                Vector3? P(params string[] n) => n.Where(norm.ContainsKey).Select(x => (Vector3?)norm[x]).FirstOrDefault();
                float ground = s.World.Keys.Where(k => !ObCreatureRetarget.Normal(k).Contains("scene") && s.Parent.ContainsKey(k)).Min(k => s.Position(k).Z);
                stance = $"pelvis z {P("pelvis", "spine0")?.Z - ground:F0} hand z {P("l hand", "lhand")?.Z - ground:F0} foot z {P("l foot")?.Z - ground:F0} head z {P("head", "skull")?.Z - ground:F0} head y {P("head", "skull")?.Y:F0} pelvis y {P("pelvis", "spine0")?.Y:F0} left x {P("l thigh", "l clavicle")?.X:F0}";
            }
            catch (Exception e) { stance = e.Message; }
            yield return $"{g.Key,-48} {g.Count(),3} creatures {sets,3} sets  {CreatureLook.For(g.Key)?.Rig ?? "(no row)",-16} {stance}";
        }
    }

    /// <summary>A written NIF read back through nifly and <see cref="WornMesh.ReadSkinned"/>: shapes, vertices, bones, bounds, the
    /// sum of each vertex's raw weights, bones outside the skeleton, and the shapes for drawing.</summary>
    public static (int Shapes, int Verts, int Bones, Vector3 Lo, Vector3 Hi, float MinSum, float MaxSum, int Foreign, List<WornShape> Drawn) ReadBack(string file, BindSkeleton skeleton)
    {
        var r = (Shapes: 0, Verts: 0, Bones: 0, Lo: new Vector3(float.MaxValue), Hi: new Vector3(float.MinValue), MinSum: float.MaxValue, MaxSum: float.MinValue, Foreign: 0, Drawn: new List<WornShape>());
        SkyrimCharacterAssets.Locked(() =>
        {
            using var nif = new NifFile();
            if (nif.Load(file) != 0) throw new InvalidDataException($"nifly can't read back {file}");
            r.Drawn = WornMesh.ReadSkinned(nif, skeleton);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in r.Drawn)
            {
                r.Shapes++; r.Verts += s.V.Length;
                foreach (var p in s.V) { r.Lo = Vector3.Min(r.Lo, p); r.Hi = Vector3.Max(r.Hi, p); }
                foreach (var w in s.Weights) foreach (var (b, _) in w) used.Add(b);
            }
            r.Bones = used.Count;
            r.Foreign = used.Count(b => !skeleton.Has(b));
            var hdr = nif.GetHeader();
            using var all = nif.GetShapes();
            using var cache = new niflycpp.BlockCache(nif.GetHeader());
            for (int si = 0; si < all.Count; si++)
                if (cache.EditableBlockById<BSTriShape>(hdr.GetBlockID(all[si])) is { } tri)
                    for (int i = 0; i < tri.vertData.Count; i++)
                    {
                        float sum = tri.vertData[i].weights.ToArray().Sum();
                        r.MinSum = MathF.Min(r.MinSum, sum); r.MaxSum = MathF.Max(r.MaxSum, sum);
                    }
        });
        return r;
    }

    /// <summary>How far a vanilla body's own bind (its skin-to-bone inverted) is from skeleton.nif's rest, in units at the worst
    /// bone, and the bones and shapes of that body. The new bodies are bound at skeleton.nif's rest.</summary>
    (float Worst, string Bone, float Median, List<WornShape> Shapes) VanillaBody(CreatureBody body)
    {
        (float, string, float, List<WornShape>) r = (0, "", 0, []);
        var addon = SkinAddons(VanillaSkin(body.Actor, body.Race), body.Race).FirstOrDefault();
        if (addon?.WorldModel?.Male?.File.DataRelativePath.Path is not { Length: > 0 } file || characterAssets!.Read(MeshPath(file)) is not { } bytes) return r;
        SkyrimCharacterAssets.Locked(() =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_obcrea_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) return;
                using var all = nif.GetShapes();
                float worst = 0; string where = ""; var offs = new List<float>();
                foreach (var shape in all)
                {
                    using var names = new vectorstring();
                    nif.GetShapeBoneList(shape, names);
                    var bones = names.ToList();
                    for (int b = 0; b < bones.Count; b++)
                    {
                        var stb = new MatTransform();
                        nif.GetShapeTransformSkinToBone(shape, (uint)b, stb);
                        if (!body.Target.Has(bones[b]) || !Matrix4x4.Invert(WornMesh.ToMatrix(stb), out var bind)) continue;
                        var g2s = new MatTransform();
                        if (nif.GetShapeTransformGlobalToSkin(shape, g2s) && Matrix4x4.Invert(WornMesh.ToMatrix(g2s), out var toGlobal)) bind *= toGlobal;
                        float d = Vector3.Distance(bind.Translation, body.Target.Position(bones[b]));
                        if (d > worst) (worst, where) = (d, bones[b]);
                        offs.Add(d);
                    }
                }
                r = (worst, where, offs.Count > 0 ? offs.Order().ElementAt(offs.Count / 2) : 0, WornMesh.ReadSkinned(nif, body.Target));
            }
            finally { File.Delete(tmp); }
        });
        return r;
    }

    /// <summary>Dev aid (ob-creature-test): the creatures whose EditorID, name or model matches, each distinct body once:
    /// made, read back and measured against the vanilla body it replaces; <paramref name="pngDir"/>: drawn beside it.</summary>
    public IEnumerable<string> CreatureLookReport(Regex match, int take, string? pngDir, bool pairs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int bad = 0, made = 0, kept = 0;
        foreach (var c in CreaturesMatching(match).OrderBy(c => c.EditorID, StringComparer.OrdinalIgnoreCase).Take(take).ToList())
        {
            var look = CreatureLook.For(c.Model?.File);
            string? standIn = CreatureStandIn(c);
            if (look is null) { kept++; yield return $"{c.EditorID} ({c.Model?.File}): stand-in {standIn ?? "none"} (no row)"; continue; }
            int before = Problems.Count;
            var body = CreatureBodyOf(c, look, standIn);
            foreach (var p in Problems.Skip(before)) yield return "   problem: " + p;
            if (body is null) { kept++; continue; }
            made++;
            if (!seen.Add(body.Nif)) continue;
            var back = ReadBack(Path.Combine(modOutDir, body.Nif), body.Target);
            var vanilla = VanillaBody(body);
            var lo = vanilla.Shapes.SelectMany(s => s.V).DefaultIfEmpty().Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var hi = vanilla.Shapes.SelectMany(s => s.V).DefaultIfEmpty().Aggregate(new Vector3(float.MinValue), Vector3.Max);
            bool ok = back.Foreign == 0 && back.MinSum > 0.99f && back.MaxSum < 1.01f;
            if (!ok) bad++;
            yield return $"{c.EditorID} ({c.Name}): {look.Folder} on {body.Rig.Key} ({body.Actor.EditorID}, {body.Race.EditorID}, {body.SkeletonFile}) scale {body.Retarget.Scale:F2} race height {1 / body.Retarget.Scale:F2} stretch {body.Retarget.MinStretch:F2}..{body.Retarget.MaxStretch:F2}";
            yield return $"   {back.Shapes} shapes {back.Verts} verts {back.Bones} bones ({back.Foreign} not on the skeleton), weight sums {back.MinSum:F4}..{back.MaxSum:F4}, {body.Unweighted} unweighted in the source, bounds {back.Lo:F0} .. {back.Hi:F0}{(ok ? "" : "  <-- BAD")}";
            yield return $"   vanilla body bounds {lo:F0} .. {hi:F0} ({SkinAddons(VanillaSkin(body.Actor, body.Race), body.Race).FirstOrDefault()?.WorldModel?.Male?.File.DataRelativePath.Path}), its bind off skeleton.nif by {vanilla.Median:F2} (median), {vanilla.Worst:F2} at {vanilla.Bone}; {Path.GetFileName(body.Nif)}";
            if (pairs)
            {
                foreach (var p in body.Retarget.Pairs) yield return "     " + p;
                foreach (var p in body.Retarget.Rigid) yield return "     rigid: " + p;
            }
            if (pngDir is not null) Draw(Path.Combine(pngDir, Path.GetFileNameWithoutExtension(body.Nif) + ".png"), back.Drawn, vanilla.Shapes);
        }
        yield return $"{made} creatures re-rigged ({seen.Count} bodies), {kept} kept their stand-in, {bad} bodies failed the read-back";
    }

    /// <summary>Dev aid: actors of the creatures made through the plugin's own path, with their records.</summary>
    public IEnumerable<string> CreatureLookRecords(Regex match, int take)
    {
        foreach (var c in CreaturesMatching(match).OrderBy(c => c.EditorID, StringComparer.OrdinalIgnoreCase).Take(take).ToList())
        {
            int before = Problems.Count;
            if (Actor(c.FormKey) is Npc npc)
            {
                var race = mod.Races.FirstOrDefault(r => r.FormKey == npc.Race.FormKey);
                var skin = mod.Armors.FirstOrDefault(a => a.FormKey == npc.WornArmor.FormKey);
                var addon = skin is null ? null : mod.ArmorAddons.FirstOrDefault(a => skin.Armature.Any(l => l.FormKey == a.FormKey));
                yield return $"{c.EditorID}: {npc.EditorID} race {race?.EditorID ?? npc.Race.FormKey.ToString()} height {race?.Height.Male:F2} x {npc.Height:F2}, skin {skin?.EditorID ?? "(stand-in's)"} slots {skin?.BodyTemplate?.FirstPersonFlags}, addon {addon?.EditorID} race {(addon?.Race.FormKey == npc.Race.FormKey ? "own" : "OTHER")} {addon?.WorldModel?.Male?.File.DataRelativePath.Path}, armor race {race?.ArmorRace.FormKey}, template flags {npc.Configuration.TemplateFlags}";
            }
            else yield return $"{c.EditorID}: no actor";
            foreach (var p in Problems.Skip(before)) yield return "   problem: " + p;
        }
    }

    /// <summary>The two bodies flat-shaded from the side and the front: ours on the top row, vanilla's below.</summary>
    static void Draw(string path, List<WornShape> ours, List<WornShape> vanilla)
    {
        const int size = 420;
        var rgb = new byte[size * 2 * size * 2 * 3];
        var depth = new float[size * 2 * size * 2];
        Array.Fill(depth, float.MaxValue);
        Array.Fill(rgb, (byte)40);
        var all = ours.Concat(vanilla).SelectMany(s => s.V).ToList();
        if (all.Count == 0) return;
        var lo = all.Aggregate(Vector3.Min); var hi = all.Aggregate(Vector3.Max);
        float extent = MathF.Max(MathF.Max(hi.X - lo.X, hi.Y - lo.Y), hi.Z - lo.Z) * 1.04f;
        var mid = (lo + hi) / 2;
        void Scene(List<WornShape> shapes, int row)
        {
            foreach (var s in shapes)
                for (int view = 0; view < 2; view++)
                {
                    // Side: forward (+Y) to the right; front: the actor's left (-X) to the right, seen from ahead.
                    Vector3 Eye(Vector3 p) => view == 0 ? new Vector3(p.Y - mid.Y, p.Z - mid.Z, p.X) : new Vector3(p.X - mid.X, p.Z - mid.Z, -p.Y);
                    for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                    {
                        Vector3 a = Eye(s.V[s.Tris[t]]), b = Eye(s.V[s.Tris[t + 1]]), c = Eye(s.V[s.Tris[t + 2]]);
                        var fn = Vector3.Cross(b - a, c - a);
                        if (fn.LengthSquared() < 1e-10f) continue;
                        float light = 0.25f + 0.75f * MathF.Abs(Vector3.Normalize(fn).Z);
                        float ox = view * size, oy = row * size, k = size / extent;
                        float X(Vector3 p) => ox + size / 2f + p.X * k;
                        float Y(Vector3 p) => oy + size / 2f - p.Y * k;
                        int x0 = (int)MathF.Floor(MathF.Min(X(a), MathF.Min(X(b), X(c)))), x1 = (int)MathF.Ceiling(MathF.Max(X(a), MathF.Max(X(b), X(c))));
                        int y0 = (int)MathF.Floor(MathF.Min(Y(a), MathF.Min(Y(b), Y(c)))), y1 = (int)MathF.Ceiling(MathF.Max(Y(a), MathF.Max(Y(b), Y(c))));
                        float area = (X(b) - X(a)) * (Y(c) - Y(a)) - (X(c) - X(a)) * (Y(b) - Y(a));
                        if (MathF.Abs(area) < 1e-6f) continue;
                        for (int y = Math.Max(y0, (int)oy); y <= Math.Min(y1, (int)oy + size - 1); y++)
                            for (int x = Math.Max(x0, (int)ox); x <= Math.Min(x1, (int)ox + size - 1); x++)
                            {
                                float w0 = ((X(b) - x) * (Y(c) - y) - (X(c) - x) * (Y(b) - y)) / area;
                                float w1 = ((X(c) - x) * (Y(a) - y) - (X(a) - x) * (Y(c) - y)) / area;
                                float w2 = 1 - w0 - w1;
                                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                                int at = y * size * 2 + x;
                                if (z >= depth[at]) continue;
                                depth[at] = z;
                                var colour = (row == 0 ? new Vector3(0.75f, 0.62f, 0.5f) : new Vector3(0.5f, 0.62f, 0.75f)) * light;
                                rgb[at * 3] = (byte)(colour.X * 255); rgb[at * 3 + 1] = (byte)(colour.Y * 255); rgb[at * 3 + 2] = (byte)(colour.Z * 255);
                            }
                    }
                }
        }
        Scene(ours, 0);
        Scene(vanilla, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Util.Png.WriteRgb(path, size * 2, size * 2, rgb);
    }
}
