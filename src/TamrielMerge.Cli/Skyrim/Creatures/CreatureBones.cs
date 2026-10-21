using System.Numerics;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// Whether a built creature's bones hold together (user, 2026-10-21: "we need a bone tool too"): its skeleton.hkx (what the
/// engine animates), skeleton.nif (what it attaches), its body's skin binds and weights, its animations' tracks, its graph's
/// look-at bone, the bones its records name (head, torso), and, for one made from a Morrowind model, each bone against the
/// node it came from. Every difference is reported with its size. `creature-bones`.
/// </summary>
public static class CreatureBones
{
    public sealed record Report(int Bones, int Issues, List<string> Lines);

    /// <param name="named">Bones its records or config name (head, torso), which must exist.</param>
    /// <param name="mwModel">The Morrowind model it was made from, to compare bone by bone (null: none).</param>
    public static Report Run(string dataDir, string id, IEnumerable<string> named, Tes3Data? mwData = null, string? mwModel = null)
    {
        var lines = new List<string>();
        int issues = 0;
        void Issue(string s) { issues++; lines.Add("  ISSUE " + s); }
        string folder = Path.Combine(dataDir, CreatureRig.MeshFolder(id));

        // The Havok skeleton: names, parents first, the root.
        var skelRoot = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(Path.Combine(folder, "character assets", "skeleton.hkx"))));
        var skel = ((HKX2.hkaAnimationContainer)skelRoot.m_namedVariants[0]!.m_variant!).m_skeletons[0];
        int n = skel.m_bones.Count;
        var names = skel.m_bones.Select(b => b.m_name).ToList();
        if (names.Count != names.Distinct().Count()) Issue($"repeated bone names: {string.Join(", ", names.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))}");
        if (names.Count == 0 || names[0] != CreatureRig.RootBone) Issue($"the first bone is {names.FirstOrDefault()}, not {CreatureRig.RootBone}");
        for (int i = 0; i < n; i++)
            if (skel.m_parentIndices[i] >= i) Issue($"bone {names[i]} has its parent {skel.m_parentIndices[i]} after it");
        if (skel.m_referencePose.Count != n) Issue($"reference pose has {skel.m_referencePose.Count} transforms for {n} bones");
        var world = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            var qs = skel.m_referencePose[i];
            var local = Matrix4x4.CreateFromQuaternion(new Quaternion(qs.M21, qs.M22, qs.M23, qs.M24)) * Matrix4x4.CreateTranslation(qs.M11, qs.M12, qs.M13);
            world[i] = skel.m_parentIndices[i] < 0 ? local : local * world[skel.m_parentIndices[i]];
        }
        lines.Add($"  skeleton.hkx: {n} bones");

        // skeleton.nif: the same bones, the same parents, the same places.
        using (var nif = new nifly.NifFile())
        {
            nif.Load(Path.Combine(folder, "character assets", "skeleton.nif"));
            var hdr = nif.GetHeader();
            var nodes = new Dictionary<string, nifly.NiNode>();
            for (uint b = 0; b < hdr.GetNumBlocks(); b++)
                if (hdr.GetBlockById(b) is nifly.NiNode node && node.name.get() is { Length: > 0 } nm) nodes.TryAdd(nm, node);
            float worstMove = 0; string worstBone = "";
            for (int i = 0; i < n; i++)
            {
                if (!nodes.TryGetValue(names[i], out var node)) { Issue($"skeleton.nif has no {names[i]}"); continue; }
                string? parentName = nif.GetParentNode(node)?.name.get();
                string? want = skel.m_parentIndices[i] < 0 ? null : names[skel.m_parentIndices[i]];
                if (want is not null && parentName != want) Issue($"skeleton.nif: {names[i]} hangs under {parentName}, the .hkx under {want}");
                var t = new nifly.MatTransform();
                if (nif.GetNodeTransformToGlobal(names[i], t))
                {
                    float d = Vector3.Distance(new Vector3(t.translation.x, t.translation.y, t.translation.z), world[i].Translation);
                    if (d > worstMove) { worstMove = d; worstBone = names[i]; }
                }
            }
            lines.Add($"  skeleton.nif: {nodes.Count} nodes; bones placed as the .hkx's within {worstMove:F3} (worst {worstBone})");
            if (worstMove > 0.05f) Issue($"skeleton.nif and skeleton.hkx put {worstBone} {worstMove:F3} apart");
        }

        // The body: its bones exist, each skin bind is the source's own (a Morrowind model's NiSkinData, shape by shape) or else
        // the bone's rest inverted, weights are whole.
        List<Dictionary<string, Matrix4x4>>? sourceBinds = null;
        if (mwData is not null && mwModel is not null)
        {
            string mp = mwModel.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? mwModel : @"meshes\" + mwModel;
            var src = Nif4.Read(mwData.Read(mp)!, mp);
            var holder = new HashSet<int>();
            foreach (var b in src.Blocks.OfType<NiNode>()) foreach (int c in b.Children) if (src.Get<NiTriShape>(c) is { } s) holder.Add(s.Index);
            sourceBinds = src.Blocks.OfType<NiTriShape>().Where(s => s is not NiParticles && !s.Hidden && holder.Contains(s.Index) && src.Get<NiTriShapeData>(s.Data) is { Vertices.Length: > 0 })
                .Select(s => src.Get<NiSkinInstance>(s.Skin) is { } inst && src.Get<NiSkinData>(inst.Data) is { } sd
                    ? inst.Bones.Select((node, bi) => (Name: src.Get<NiNode>(node)?.Name ?? "", bi)).Where(x => x.bi < sd.Bones.Count).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => MwCreatureSource.RowMatrix(sd.Bones[g.First().bi].Transform))
                    : []).ToList();
        }
        using (var body = new nifly.NifFile())
        {
            body.Load(Path.Combine(folder, id + ".nif"));
            using var cache = new nifly.niflycpp.BlockCache(body.GetHeader());
            int shapes = 0, vertices = 0, overFour = 0, unweighted = 0, notWhole = 0; float worstBind = 0; string worstBindBone = "";
            var usedBones = new HashSet<string>();
            foreach (var shape in body.GetShapes())
            {
                shapes++;
                using var bl = new nifly.vectorstring();
                body.GetShapeBoneList(shape, bl);
                var verts = body.GetVertsForShape(shape);
                vertices += verts.Count;
                var inst = cache.EditableBlockById<nifly.NiSkinInstance>(shape.SkinInstanceRef().index);
                if (inst is null) { Issue($"{shape.name.get()} is not skinned"); continue; }
                var sd = cache.EditableBlockById<nifly.NiSkinData>(inst.dataRef.index);
                var sum = new float[verts.Count]; var count = new int[verts.Count];
                for (int b = 0; b < bl.Count; b++)
                {
                    usedBones.Add(bl[b]);
                    int bi = names.IndexOf(bl[b]);
                    if (bi < 0) { Issue($"{shape.name.get()} is skinned to {bl[b]}, which the skeleton hasn't"); continue; }
                    var t = new nifly.MatTransform();
                    body.GetShapeTransformSkinToBone(shape, (uint)b, t);
                    var r = t.rotation;
                    var cx = r.opMult(new nifly.Vector3(1, 0, 0)); var cy = r.opMult(new nifly.Vector3(0, 1, 0)); var cz = r.opMult(new nifly.Vector3(0, 0, 1));
                    var stb = new Matrix4x4(cx.x, cx.y, cx.z, 0, cy.x, cy.y, cy.z, 0, cz.x, cz.y, cz.z, 0, t.translation.x, t.translation.y, t.translation.z, 1);
                    // Against the source's bind for this shape and bone, else the bone's rest inverted: points go where they should.
                    Matrix4x4 expected;
                    if (sourceBinds is not null && shapes - 1 < sourceBinds.Count && sourceBinds[shapes - 1].TryGetValue(bl[b], out var own)) expected = own;
                    else Matrix4x4.Invert(world[bi], out expected);
                    float err = new[] { Vector3.Zero, Vector3.UnitX * 50, Vector3.UnitY * 50, Vector3.UnitZ * 50 }.Max(p => Vector3.Distance(Vector3.Transform(p, Matrix4x4.CreateScale(t.scale) * stb), Vector3.Transform(p, expected)));
                    if (err > worstBind) { worstBind = err; worstBindBone = bl[b]; }
                    foreach (var sw in sd.bones[b].vertexWeights) { sum[sw.index] += sw.weight; count[sw.index]++; }
                }
                for (int v = 0; v < verts.Count; v++)
                {
                    if (count[v] > 4) overFour++;
                    if (count[v] == 0) unweighted++;
                    else if (MathF.Abs(sum[v] - 1) > 0.01f) notWhole++;
                }
            }
            lines.Add($"  body: {shapes} shapes, {vertices} vertices on {usedBones.Count} bones; skin binds within {worstBind:F3} of {(sourceBinds is null ? "the bones' rest" : "the source's own binds")} (worst {worstBindBone})");
            if (worstBind > 0.05f) Issue($"the body's bind for {worstBindBone} is {worstBind:F3} off {(sourceBinds is null ? "its rest" : "the source's")}");
            if (overFour > 0) Issue($"{overFour} vertices on more than four bones");
            if (unweighted > 0) Issue($"{unweighted} vertices on no bone");
            if (notWhole > 0) Issue($"{notWhole} vertices whose weights don't add to 1");
        }

        // Animations: a track per bone, bound in order.
        int anims = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "animations"), "*.hkx"))
        {
            anims++;
            var root = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(file)));
            var c = (HKX2.hkaAnimationContainer)root.m_namedVariants[0]!.m_variant!;
            var a = c.m_animations[0];
            if (a.m_numberOfTransformTracks != n) Issue($"{Path.GetFileName(file)}: {a.m_numberOfTransformTracks} tracks for {n} bones");
            var binding = c.m_bindings.FirstOrDefault();
            if (binding is null || binding.m_transformTrackToBoneIndices.Count != a.m_numberOfTransformTracks || binding.m_transformTrackToBoneIndices.Where((b, i) => b != i).Any())
                Issue($"{Path.GetFileName(file)}: tracks not bound one to one to the bones");
            if (a.m_annotationTracks.Count != n) Issue($"{Path.GetFileName(file)}: {a.m_annotationTracks.Count} annotation tracks for {n} bones");
        }
        lines.Add($"  animations: {anims}, each checked against the {n} bones");

        // The graph's look-at bone.
        foreach (var behavior in Directory.GetFiles(Path.Combine(folder, "Behaviors"), "*.hkx"))
        {
            var root = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(behavior)));
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Walk(object? o)
            {
                if (o is null || o is string || o.GetType().IsValueType || !seen.Add(o)) return;
                if (o is System.Collections.IEnumerable list) { foreach (var x in list) Walk(x); return; }
                if (o is HKX2.BSLookAtModifierBoneData lb)
                {
                    if (lb.m_index < 0 || lb.m_index >= n) Issue($"look-at bone index {lb.m_index} is outside the {n} bones");
                    else lines.Add($"  look-at bone: {names[lb.m_index]}");
                }
                foreach (var p in o.GetType().GetProperties().Where(p => p.Name.StartsWith("m_") && p.GetIndexParameters().Length == 0))
                {
                    object? v; try { v = p.GetValue(o); } catch { continue; }
                    Walk(v);
                }
            }
            Walk(root);
        }

        foreach (var b in named.Where(b => b.Length > 0))
            if (!names.Contains(b)) Issue($"named bone {b} is not in the skeleton");
            else lines.Add($"  named bone {b}: there");

        // Against the Morrowind model's nodes at rest.
        if (mwData is not null && mwModel is not null)
        {
            string path = mwModel.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? mwModel : @"meshes\" + mwModel;
            var src = Nif4.Read(mwData.Read(path)!, path);
            var mwRoot = (NiNode)src.Root!;
            var mwWorld = new Dictionary<string, NifTransform>();
            void Down(NiNode node, NifTransform parent)
            {
                var w = node.Transform.Then(parent);
                mwWorld.TryAdd(node.Name, w);
                foreach (int c in node.Children) if (src.Get<NiNode>(c) is { } ch && ch is not NiTriShape) Down(ch, w);
            }
            foreach (int c in mwRoot.Children) if (src.Get<NiNode>(c) is { } ch && ch is not NiTriShape) Down(ch, mwRoot.Transform);
            float worst = 0; string worstName = ""; int compared = 0, missing = 0;
            for (int i = 1; i < n; i++)
            {
                if (!mwWorld.TryGetValue(names[i], out var w)) { missing++; continue; }
                compared++;
                float d = Vector3.Distance(w.Translation, world[i].Translation);
                if (d > worst) { worst = d; worstName = names[i]; }
            }
            lines.Add($"  against {mwModel}: {compared} bones where the model's nodes are, within {worst:F3} (worst {worstName}); {missing} renamed (repeated names numbered)");
            if (worst > 0.05f) Issue($"{worstName} is {worst:F3} from the Morrowind node it came from");
        }
        return new Report(n, issues, lines);
    }
}
