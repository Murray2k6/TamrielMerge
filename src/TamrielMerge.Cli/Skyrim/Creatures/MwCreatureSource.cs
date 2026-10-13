using System.Numerics;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// A Morrowind creature as a <see cref="CreatureRig"/> (user, 2026-10-10: "do the type of stuff we are doing for the
/// enemies be the same for morrowind"), from the game's own files: its model (meshes\r\X.nif: the Bip01 skeleton and
/// skinned NiTriShapes, NiSkinData per-bone skin-to-bone transforms and weights) and its animations
/// (meshes\r\xX.kf: NiSequenceStreamHelper with one NiKeyframeController per bone, named by NiStringExtraData in
/// order, and NiTextKeyExtraData group keys "Idle: Start" .. "Death1: Stop"), each wanted group sampled at 30 frames a
/// second. Units stay Morrowind's (one to one, as the rest of the conversion). Textures go through
/// <see cref="MwTextures"/> (DDS already).
/// </summary>
public static class MwCreatureSource
{
    /// <summary>Skyrim clip names and the Morrowind animation groups that give them (first found wins).</summary>
    static readonly (string Clip, string[] Groups)[] Wanted =
    [
        ("idle", ["Idle", "Idle1", "Idle2"]),
        ("walk", ["WalkForward", "Walkforward", "SwimWalkForward"]),
        ("run", ["RunForward", "Runforward", "SwimRunForward"]),
        ("attack", ["Attack1", "Attack2", "Attack3"]),
        ("attack2", ["Attack2"]),
        ("hit", ["Hit1", "Hit2", "Hit3"]),
        ("death", ["Death1", "Death2", "Death3"]),
        ("knockdown", ["KnockDown"]),
    ];
    const float Fps = 30;

    public static CreatureRig Build(Tes3Data data, string model, MwTextures textures, string modOutDir, List<string> problems)
    {
        model = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var nif = Nif4.Read(data.Read(model) ?? throw new FileNotFoundException(model), model);
        string kfPath = Path.Combine(Path.GetDirectoryName(model)!, "x" + Path.GetFileNameWithoutExtension(model) + ".kf");
        var kf = data.Read(kfPath) is { } kb ? Nif4.Read(kb, kfPath) : null;
        if (kf is null) problems.Add($"{model}: no {kfPath}");

        var root = nif.Root as NiNode ?? throw new InvalidDataException($"{model}: no root node");
        // Nodes parents-first; shapes and collision left out of the skeleton.
        var parent = new Dictionary<int, int>();
        var order = new List<NiNode>();
        var queue = new Queue<NiNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            foreach (int c in n.Children)
                if (nif.Get<NiNode>(c) is { } child && child is not NiTriShape && child.Type != "RootCollisionNode")
                {
                    parent[child.Index] = n.Index;
                    order.Add(child);
                    queue.Enqueue(child);
                }
        }
        var world = new Dictionary<int, NifTransform> { [root.Index] = root.Transform };
        foreach (var n in order) world[n.Index] = n.Transform.Then(world[parent[n.Index]]);

        var rig = new CreatureRig();
        rig.Bones.Add(new CreatureRig.Bone(CreatureRig.RootBone, -1, Vector3.Zero, Quaternion.Identity));
        var boneOf = new Dictionary<int, int>();
        var restLocal = new List<NifTransform> { NifTransform.Identity };
        foreach (var n in order)
        {
            int p = parent[n.Index];
            // Bones right under the model's root carry the root's own transform (the root becomes "NPC Root [Root]").
            var local = p == root.Index ? n.Transform.Then(root.Transform) : n.Transform;
            boneOf[n.Index] = rig.Bones.Count;
            // Skyrim binds bones by name: a repeated name (Morrowind's "Dummy15" twice) gets a number.
            string name = n.Name;
            for (int k = 2; rig.Bones.Any(b => b.Name == name); k++) name = $"{n.Name} {k}";
            rig.Bones.Add(new CreatureRig.Bone(name, p == root.Index ? 0 : boneOf[p], local.Translation, Rot(local.Rotation)));
            restLocal.Add(local);
            if (MathF.Abs(n.Transform.Scale - 1) > 1e-3f) problems.Add($"{model}: bone {n.Name} scale {n.Transform.Scale} (taken as 1)");
        }

        // Meshes: skinned shapes from their bind, the rest riding their parent node.
        var shapeParent = new Dictionary<int, int>();
        foreach (var b in nif.Blocks.OfType<NiNode>())
            foreach (int c in b.Children) if (nif.Get<NiTriShape>(c) is { } s) shapeParent[s.Index] = b.Index;
        foreach (var shape in nif.Blocks.OfType<NiTriShape>().Where(s => s is not NiParticles && !s.Hidden && shapeParent.ContainsKey(s.Index)))
        {
            if (nif.Get<NiTriShapeData>(shape.Data) is not { } d || d.Vertices.Length == 0) continue;
            int nv = d.Vertices.Length;
            var v = new Vector3[nv]; var nrm = new Vector3[nv];
            var weights = new (int Bone, float W)[nv][];
            if (nif.Get<NiSkinInstance>(shape.Skin) is { } skin && nif.Get<NiSkinData>(skin.Data) is { } sd)
            {
                var acc = new Dictionary<int, float>[nv];
                for (int i = 0; i < nv; i++) acc[i] = [];
                var sum = new Vector3[nv]; var nsum = new Vector3[nv]; var wsum = new float[nv];
                for (int bi = 0; bi < skin.Bones.Length && bi < sd.Bones.Count; bi++)
                {
                    int node = skin.Bones[bi];
                    if (!boneOf.TryGetValue(node, out int bone)) continue;
                    var toWorld = sd.Bones[bi].Transform.Then(world[node]);
                    foreach (var (vi, w) in sd.Bones[bi].Weights)
                    {
                        if (vi >= nv || w <= 0) continue;
                        sum[vi] += toWorld.Apply(d.Vertices[vi]) * w;
                        if (d.Normals.Length == nv) nsum[vi] += toWorld.Rotate(d.Normals[vi]) * w;
                        wsum[vi] += w;
                        acc[vi][bone] = acc[vi].GetValueOrDefault(bone) + w;
                    }
                }
                for (int i = 0; i < nv; i++)
                {
                    v[i] = wsum[i] > 0 ? sum[i] / wsum[i] : world[shapeParent[shape.Index]].Apply(d.Vertices[i]);
                    nrm[i] = nsum[i].LengthSquared() > 0 ? Vector3.Normalize(nsum[i]) : Vector3.UnitZ;
                    var top = acc[i].OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float total = top.Sum(kv => kv.Value);
                    weights[i] = top.Count == 0 ? [(0, 1f)] : top.Select(kv => (kv.Key, kv.Value / total)).ToArray();
                }
            }
            else
            {
                int holder = shapeParent[shape.Index];
                var toWorld = shape.Transform.Then(world[holder]);
                int bone = boneOf.GetValueOrDefault(holder, 0);
                for (int i = 0; i < nv; i++)
                {
                    v[i] = toWorld.Apply(d.Vertices[i]);
                    nrm[i] = d.Normals.Length == nv ? Vector3.Normalize(toWorld.Rotate(d.Normals[i])) : Vector3.UnitZ;
                    weights[i] = [(bone, 1f)];
                }
            }
            var uv = d.UVSets.Length > 0 ? d.UVSets[0] : new Vector2[nv];
            var tris = Enumerable.Range(0, d.Triangles.Length / 3).Select(t => new[] { (int)d.Triangles[t * 3], d.Triangles[t * 3 + 1], d.Triangles[t * 3 + 2] }).ToArray();
            var tex = new Dictionary<string, string>();
            foreach (int pi in shape.Properties)
                if (nif.Get<NiTexturingProperty>(pi)?.Base is { } bt && nif.Get<NiSourceTexture>(bt.Source)?.FileName is { } file)
                {
                    var (rel, _) = textures.Ensure(file);
                    if (rel is not null) tex["diffuse"] = Path.Combine(modOutDir, rel);
                }
            rig.Meshes.Add(new CreatureRig.Mesh(shape.Name.Length > 0 ? shape.Name : $"shape{shape.Index}", v, nrm, uv, weights, tris, tex));
        }

        // Animations from the .kf: controllers in order, each named by the next string extra data after the text keys.
        if (kf is not null && kf.Root is NiObjectNET helper)
        {
            var names = new List<string>();
            NiTextKeyExtraData? keys = null;
            for (int e = helper.ExtraData; e >= 0;)
            {
                var x = kf.Get<NiExtraData>(e);
                if (x is NiTextKeyExtraData tk) keys = tk;
                else if (x is NiStringExtraData se) names.Add(se.Value);
                e = x?.Next ?? -1;
            }
            var tracks = new Dictionary<int, NiKeyframeData>();
            int k = 0;
            for (int c = helper.Controller; c >= 0; k++)
            {
                var ctl = kf.Get<NiController>(c);
                if (ctl is null) break;
                // By the model's node name (the first node of that name, as Morrowind binds them).
                if (k < names.Count && kf.Get<NiKeyframeData>(ctl.Data) is { } kd
                    && order.FirstOrDefault(n => n.Name == names[k]) is { } node && boneOf.TryGetValue(node.Index, out int bi))
                    tracks[bi] = kd;
                c = ctl.Next;
            }
            var groups = new Dictionary<string, (float Start, float Stop)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (time, text) in keys?.Keys ?? [])
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string g = line[..colon].Trim(), what = line[(colon + 1)..].Trim().ToLowerInvariant();
                    var cur = groups.GetValueOrDefault(g, (float.NaN, float.NaN));
                    if (what == "start") groups[g] = (time, cur.Item2);
                    else if (what == "stop") groups[g] = (cur.Item1, time);
                }
            foreach (var (clip, candidates) in Wanted)
            {
                if (rig.Clips.ContainsKey(clip)) continue;
                var g = candidates.Select(c => (c, groups.GetValueOrDefault(c, (float.NaN, float.NaN)))).FirstOrDefault(x => !float.IsNaN(x.Item2.Item1) && !float.IsNaN(x.Item2.Item2));
                if (g.c is null) continue;
                var (start, stop) = g.Item2;
                int frames = Math.Max(2, (int)MathF.Round((stop - start) * Fps) + 1);
                var clipFrames = new (Vector3, Quaternion)[frames][];
                for (int f = 0; f < frames; f++)
                {
                    float t = Math.Min(start + f / Fps, stop);
                    var pose = new (Vector3, Quaternion)[rig.Bones.Count];
                    pose[0] = (Vector3.Zero, Quaternion.Identity);
                    for (int b = 1; b < rig.Bones.Count; b++)
                    {
                        var rest = restLocal[b];
                        var tr = rest.Translation; var q = Rot(rest.Rotation);
                        if (tracks.TryGetValue(b, out var kd))
                        {
                            if (kd.Translations.Count > 0) tr = Sample(kd.Translations, t, Vector3.Lerp);
                            if (kd.Rotations.Count > 0) q = Sample(kd.Rotations, t, Quaternion.Slerp);
                            // Keys are relative to the node's parent; under the model root they carry its transform too.
                            if (rig.Bones[b].Parent == 0)
                            {
                                var lt = new NifTransform(Matrix(q), tr, 1).Then(root.Transform);
                                tr = lt.Translation; q = Rot(lt.Rotation);
                            }
                        }
                        pose[b] = (tr, q);
                    }
                    clipFrames[f] = pose;
                }
                rig.Clips[clip] = new CreatureRig.Clip(Fps, clipFrames);
            }
            if (rig.Clips.Count == 0) problems.Add($"{model}: no animation groups found ({string.Join(", ", groups.Keys.Take(12))})");
        }
        return rig;
    }

    static T Sample<T>(List<(float Time, T Value)> keys, float t, Func<T, T, float, T> lerp)
    {
        if (t <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Count; i++)
            if (t <= keys[i].Time)
            {
                float span = keys[i].Time - keys[i - 1].Time;
                return lerp(keys[i - 1].Value, keys[i].Value, span > 1e-6f ? (t - keys[i - 1].Time) / span : 1);
            }
        return keys[^1].Value;
    }

    /// <summary>A NIF rotation (column-vector convention, M_ij = row i, column j) as a quaternion.</summary>
    static Quaternion Rot(Matrix4x4 r) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(r)));

    static Matrix4x4 Matrix(Quaternion q) => Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(q));
}
