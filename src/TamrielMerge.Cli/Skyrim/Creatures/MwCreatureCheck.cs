using System.Numerics;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// Whether a Morrowind creature built with its own skeleton (<see cref="MwCreatureSource"/>) is the original (user,
/// 2026-10-21: "the cliffracers need to be identical to the originals in morrowind"): every animation, frame by frame, the
/// model skinned the way Morrowind skins it (its own node tree, the .kf's keys, NiSkinData, the movement root held in X
/// and Y as the engine holds it) against the written Skyrim files skinned the way Skyrim skins them (skeleton.hkx,
/// the animation .hkx, the body NIF's skin binds), vertex by vertex; and the textures each part wears. Written with its
/// own pose and skin arithmetic, not the converter's, so a wrong turn or order there shows. `mw-creature-check`.
/// </summary>
public static class MwCreatureCheck
{
    public sealed record Result(string Clip, string Group, int Frames, float Worst, float Mean, int WorstFrame, string WorstWhere = "");

    /// <param name="world">The game's records: with them a biped's humanoid animations and weapon styles are checked too.</param>
    /// <param name="probe">A clip and frame whose bones to compare one by one (the first where Morrowind's and ours part).</param>
    public static List<Result> Run(Tes3Data data, string model, string skyrimDataDir, string id, Action<string> say, MwWorld? world = null, (string Clip, int Frame)? probe = null)
    {
        model = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var nif = Nif4.Read(data.Read(model) ?? throw new FileNotFoundException(model), model);
        // The clips as the converter plans them (its own .kf, a biped's humanoid one, weapon styles), each against its source.
        var (biped, styles) = world is not null ? MwCreatureSource.Kind(world, model) : (false, []);
        var sources = MwCreatureSource.Sources(data, model, biped);
        var plan = MwCreatureSource.Plan(sources, styles);
        var root = nif.Root as NiNode ?? throw new InvalidDataException($"{model}: no root node");

        // The node tree, parents first; the first node of a name is the one a track drives.
        var parent = new Dictionary<int, int>();
        var order = new List<NiNode>();
        // Depth first, children in order: the order Morrowind's engine meets them in, so of two nodes with one name the first
        // here is the one its animation tracks drive (Almalexia's two "Dummy07": breadth first took the other, 80 units off).
        void Walk(NiNode n)
        {
            foreach (int c in n.Children)
                if (nif.Get<NiNode>(c) is { } child && child is not NiTriShape && child.Type != "RootCollisionNode")
                {
                    parent[child.Index] = n.Index;
                    order.Add(child);
                    Walk(child);
                }
        }
        Walk(root);
        var trackOfSource = sources.Select(src =>
        {
            var t = new Dictionary<int, NiKeyframeData>();
            foreach (var (name, kd) in src.Tracks)
                if (order.FirstOrDefault(n => n.Name == name) is { } node) t[node.Index] = kd;
            return t;
        }).ToList();
        var trackOf = new Dictionary<int, NiKeyframeData>();
        int movementRoot = MwCreatureSource.MovementRoots.Select(m => order.FirstOrDefault(n => n.Name.Equals(m, StringComparison.OrdinalIgnoreCase))?.Index ?? -1).FirstOrDefault(i => i >= 0, -1);

        // Visible skinned shapes, in the order the converter writes them.
        var shapeParent = new Dictionary<int, int>();
        foreach (var b in nif.Blocks.OfType<NiNode>())
            foreach (int c in b.Children) if (nif.Get<NiTriShape>(c) is { } s) shapeParent[s.Index] = b.Index;
        var shapes = nif.Blocks.OfType<NiTriShape>().Where(s => s is not NiParticles && !s.Hidden && shapeParent.ContainsKey(s.Index)
            && nif.Get<NiTriShapeData>(s.Data) is { Vertices.Length: > 0 }).ToList();
        int overFour = 0; float worstDropped = 0;
        foreach (var s in shapes)
        {
            if (nif.Get<NiSkinInstance>(s.Skin) is not { } si || nif.Get<NiSkinData>(si.Data) is not { } sd) continue;
            if (!IsIdentity(sd.Transform)) say($"  {s.Name}: skin data's own transform is not identity ({sd.Transform.Translation}, scale {sd.Transform.Scale})");
            // Skyrim skins a vertex with at most four bones: how many of Morrowind's use more, and the most weight left off.
            var per = new Dictionary<int, List<float>>();
            foreach (var b in sd.Bones) foreach (var (vi, w) in b.Weights) if (w > 0) (per.TryGetValue(vi, out var l) ? l : per[vi] = []).Add(w);
            foreach (var ws in per.Values.Where(l => l.Count > 4))
            {
                overFour++;
                var sorted = ws.OrderByDescending(w => w).ToList();
                worstDropped = MathF.Max(worstDropped, sorted.Skip(4).Sum() / sorted.Sum());
            }
        }
        say($"  Morrowind's skin: {overFour} vertices on more than four bones{(overFour > 0 ? $" (Skyrim's limit; at most {worstDropped:P1} of a vertex's weight left off)" : "")}");

        string BonesOf(int s, int v) => nif.Get<NiSkinInstance>(shapes[s].Skin) is { } inst && nif.Get<NiSkinData>(inst.Data) is { } sdat
            ? string.Join(", ", sdat.Bones.Select((b, bi) => (b, bi)).Where(x => x.b.Weights.Any(w => w.Vertex == v && w.Weight > 0)).Select(x => $"{nif.Get<NiNode>(inst.Bones[x.bi])?.Name} {x.b.Weights.First(w => w.Vertex == v).Weight:F2}"))
            : "(not skinned)";

        Dictionary<int, Matrix4x4> lastWorld = [];
        Vector3[][] Morrowind(float time, float groupStart)
        {
            var world = lastWorld = new Dictionary<int, Matrix4x4> { [root.Index] = Local(root.Transform) };
            foreach (var n in order)
            {
                var t = n.Transform;
                var rot = Mat3(t.Rotation); var tr = t.Translation; float scale = t.Scale;
                if (trackOf.TryGetValue(n.Index, out var kd))
                {
                    if (kd.Rotations.Count > 0) rot = FromQuaternion(Key(kd.Rotations, time, Nlerp));
                    if (kd.Translations.Count > 0) tr = Key(kd.Translations, time, Vector3.Lerp);
                    if (kd.Scales.Count > 0) scale = Key(kd.Scales, time, Vector3.Lerp).X;
                    if (n.Index == movementRoot && kd.Translations.Count > 0)
                    {
                        var at = Key(kd.Translations, groupStart, Vector3.Lerp);
                        tr = new Vector3(at.X, at.Y, tr.Z);
                    }
                }
                world[n.Index] = Compose(rot, tr, scale) * world[parent[n.Index]];
            }
            var result = new Vector3[shapes.Count][];
            for (int si = 0; si < shapes.Count; si++)
            {
                var s = shapes[si];
                var d = nif.Get<NiTriShapeData>(s.Data)!;
                var v = new Vector3[d.Vertices.Length];
                if (nif.Get<NiSkinInstance>(s.Skin) is { } inst && nif.Get<NiSkinData>(inst.Data) is { } sd)
                {
                    var wsum = new float[v.Length];
                    for (int b = 0; b < inst.Bones.Length && b < sd.Bones.Count; b++)
                    {
                        var m = Local(sd.Bones[b].Transform) * world[inst.Bones[b]];
                        foreach (var (vi, w) in sd.Bones[b].Weights)
                            if (vi < v.Length && w > 0) { v[vi] += Vector3.Transform(d.Vertices[vi], m) * w; wsum[vi] += w; }
                    }
                    for (int i = 0; i < v.Length; i++) if (wsum[i] > 0) v[i] /= wsum[i];
                }
                else
                {
                    var m = Local(s.Transform) * world[shapeParent[s.Index]];
                    for (int i = 0; i < v.Length; i++) v[i] = Vector3.Transform(d.Vertices[i], m);
                }
                result[si] = v;
            }
            return result;
        }

        var results = new List<Result>();
        string folder = Path.Combine(skyrimDataDir, CreatureRig.MeshFolder(id));
        using var body = new SkyrimBody(folder, id);
        if (body.ShapeVertexCounts.Count != shapes.Count)
            say($"  shapes: Morrowind {shapes.Count}, Skyrim {body.ShapeVertexCounts.Count}");
        for (int i = 0; i < Math.Min(shapes.Count, body.ShapeVertexCounts.Count); i++)
        {
            int mw = nif.Get<NiTriShapeData>(shapes[i].Data)!.Vertices.Length, sk = body.ShapeVertexCounts[i];
            int mwTris = nif.Get<NiTriShapeData>(shapes[i].Data)!.Triangles.Length / 3;
            if (mw != sk || mwTris != body.ShapeTriangleCounts[i]) say($"  {shapes[i].Name}: Morrowind {mw} vertices {mwTris} triangles, Skyrim {sk} vertices {body.ShapeTriangleCounts[i]} triangles");
        }
        foreach (var (clip, source, group, start, stop, _, _, _) in plan)
        {
            trackOf = trackOfSource[source];
            string file = Path.Combine(folder, "animations", clip + ".hkx");
            if (!File.Exists(file)) { say($"  {clip}: no {file}"); continue; }
            var anim = body.Animation(file);
            float worst = 0, sum = 0; int count = 0, worstFrame = 0; string worstWhere = "";
            for (int f = 0; f < anim.Frames; f++)
            {
                float t = Math.Min(start + f / anim.Fps, stop);
                var reference = Morrowind(t, start);
                var ours = body.Pose(anim, f);
                if (probe is { } pr && pr.Clip == clip && pr.Frame == f)
                {
                    // Each bone's origin, Morrowind's against ours, by name (the first of a name, as the converter names them).
                    var oursWorld = body.BoneWorlds(anim, f);
                    var rows = new List<(float D, string Line)>();
                    foreach (var n in order)
                    {
                        int bi = body.BoneNames.IndexOf(MwCreatureSource.BoneName(n.Name));
                        if (bi < 0 || !lastWorld.TryGetValue(n.Index, out var mw)) continue;
                        float d = Vector3.Distance(mw.Translation, oursWorld[bi].Translation);
                        // The angle between the two frames' rotations.
                        // The angle between the two rotations from the off-diagonal part of one times the other's inverse (exact for
                        // small angles, where the cosine's float steps alone are 0.04 degrees).
                        var dm = Normalized(mw) * Matrix4x4.Transpose(Normalized(oursWorld[bi]));
                        double sx = dm.M32 - dm.M23, sy = dm.M13 - dm.M31, sz = dm.M21 - dm.M12;
                        float angle = (float)(Math.Asin(Math.Min(1, 0.5 * Math.Sqrt(sx * sx + sy * sy + sz * sz))) * 180 / Math.PI);
                        var r0 = n.Transform.Rotation; var rtr = Matrix4x4.Transpose(r0) * r0;
                        float skew = MathF.Max(MathF.Max(MathF.Abs(rtr.M11 - 1), MathF.Abs(rtr.M22 - 1)), MathF.Max(MathF.Abs(rtr.M33 - 1), MathF.Max(MathF.Abs(rtr.M12), MathF.Max(MathF.Abs(rtr.M13), MathF.Abs(rtr.M23)))));
                        string keys = trackOf.TryGetValue(n.Index, out var tk) ? $"{tk.Rotations.Count} rotation keys (type {tk.RotationType}), {tk.Translations.Count} translation" : "no track";
                        rows.Add((d + angle, $"    {n.Name} (under {order.FirstOrDefault(p => p.Index == parent[n.Index])?.Name ?? "root"}): {d:F4} apart, turned {angle:F4} degrees apart; {mw.Translation.Length():F1} from the root; {keys}; rest matrix off a rotation by {skew:G3}"));
                    }
                    // The worst-turned keyed bone's own local rotation both ways, and the keys around the time.
                    foreach (var n in order.Where(n => trackOf.TryGetValue(n.Index, out var k) && k.Rotations.Count > 0).Take(6))
                    {
                        var kd = trackOf[n.Index]; int bi = body.BoneNames.IndexOf(MwCreatureSource.BoneName(n.Name));
                        var refQ = Key(kd.Rotations, t, Nlerp);
                        var qs = anim.Transforms[f * anim.Tracks + bi];
                        int k1 = kd.Rotations.FindIndex(k => k.Time >= t);
                        say($"    {n.Name} keys within 0.05 s of t={t:R}: {string.Join(" | ", kd.Rotations.Where(k => MathF.Abs(k.Time - t) < 0.05f).Select(k => $"{k.Time:R} {k.Value}"))}");
                        say($"    {n.Name} at t={t:F5}: Morrowind key {refQ}, ours {new Quaternion(qs.M21, qs.M22, qs.M23, qs.M24)}; keys around: {(k1 > 0 ? $"{kd.Rotations[k1 - 1].Time:F5} {kd.Rotations[k1 - 1].Value}" : "-")} | {(k1 >= 0 ? $"{kd.Rotations[k1].Time:F5} {kd.Rotations[k1].Value}" : "-")}");
                    }
                    say($"  {clip} frame {f}: the bones furthest apart");
                    foreach (var r in rows.OrderByDescending(r => r.D).Take(16)) say(r.Line);
                    say($"  {clip} frame {f}: the first bones, in the tree's order");
                    foreach (var r in rows.Take(14)) say(r.Line);
                }
                for (int s = 0; s < Math.Min(reference.Length, ours.Length); s++)
                    for (int i = 0; i < Math.Min(reference[s].Length, ours[s].Length); i++)
                    {
                        float dist = Vector3.Distance(reference[s][i], ours[s][i]);
                        sum += dist; count++;
                        if (dist > worst) { worst = dist; worstFrame = f; worstWhere = $"{shapes[s].Name} vertex {i} on {BonesOf(s, i)}: Morrowind {reference[s][i]}, ours {ours[s][i]}"; }
                    }
            }
            results.Add(new Result(clip, group, anim.Frames, worst, count > 0 ? sum / count : 0, worstFrame, worstWhere));
        }
        return results;
    }

    /// <summary>A transform's rotation alone (its axes made unit length, translation dropped).</summary>
    static Matrix4x4 Normalized(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13)); var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23)); var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }

    static bool IsIdentity(NifTransform t) => t.Translation.Length() < 1e-3f && MathF.Abs(t.Scale - 1) < 1e-4f
        && MathF.Abs(t.Rotation.M11 - 1) < 1e-4f && MathF.Abs(t.Rotation.M22 - 1) < 1e-4f && MathF.Abs(t.Rotation.M33 - 1) < 1e-4f;

    static T Key<T>(List<(float Time, T Value)> keys, float t, Func<T, T, float, T> lerp)
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

    /// <summary>Shortest-path normalised blend (the reference's own; the converter uses Slerp).</summary>
    static Quaternion Nlerp(Quaternion a, Quaternion b, float u)
    {
        if (Quaternion.Dot(a, b) < 0) b = -b;
        // Close keys blend linearly (then made unit length): their angle's cosine rounds to 1 in floats (2026-10-21: returning
        // the first key there put the clannfear's spine 0.046 degrees off at a key's own time; the check's fault, not the converter's).
        double dot = Math.Clamp((double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W, -1, 1);
        float angle = (float)Math.Acos(dot);
        if (angle < 1e-4f) return Quaternion.Normalize(new Quaternion(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Z + (b.Z - a.Z) * u, a.W + (b.W - a.W) * u));
        float s = MathF.Sin(angle);
        float wa = MathF.Sin((1 - u) * angle) / s, wb = MathF.Sin(u * angle) / s;
        return Quaternion.Normalize(new Quaternion(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb));
    }

    /// <summary>A NIF key quaternion as the column-vector rotation matrix NIF stores (rows: R·v), by the textbook formula.</summary>
    static float[,] FromQuaternion(Quaternion q)
    {
        float w = q.W, x = q.X, y = q.Y, z = q.Z;
        return new float[,]
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y) },
            { 2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x) },
            { 2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y) },
        };
    }

    static float[,] Mat3(Matrix4x4 r) => new float[,] { { r.M11, r.M12, r.M13 }, { r.M21, r.M22, r.M23 }, { r.M31, r.M32, r.M33 } };

    /// <summary>p' = T + s·R·p as a row-vector matrix (p' = p·M).</summary>
    static Matrix4x4 Compose(float[,] r, Vector3 t, float s) => new(
        r[0, 0] * s, r[1, 0] * s, r[2, 0] * s, 0,
        r[0, 1] * s, r[1, 1] * s, r[2, 1] * s, 0,
        r[0, 2] * s, r[1, 2] * s, r[2, 2] * s, 0,
        t.X, t.Y, t.Z, 1);

    static Matrix4x4 Local(NifTransform t) => Compose(Mat3(t.Rotation), t.Translation, t.Scale);

    /// <summary>
    /// A creature's written Skyrim files, skinned as the engine skins them: each bone's pose to the skeleton root from the
    /// animation's local transforms (hkQsTransform: translation, quaternion), times the body NIF's skin-to-bone transform,
    /// weighted.
    /// </summary>
    public sealed class SkyrimBody : IDisposable
    {
        readonly nifly.NifFile nif = new();
        readonly HKX2.hkaSkeleton skeleton;
        readonly List<(int[] Bones, Matrix4x4[] SkinToBone, (int Vertex, int Bone, float W)[] Weights, Vector3[] V)> shapes = [];
        public List<int> ShapeVertexCounts { get; } = [];
        public List<int> ShapeTriangleCounts { get; } = [];
        /// <summary>Each shape's diffuse texture as written.</summary>
        public List<string> ShapeTextures { get; } = [];

        public sealed record Anim(int Tracks, int Frames, float Fps, IList<Matrix4x4> Transforms);

        public SkyrimBody(string folder, string id)
        {
            var skelRoot = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(Path.Combine(folder, "character assets", "skeleton.hkx"))));
            skeleton = ((HKX2.hkaAnimationContainer)skelRoot.m_namedVariants[0]!.m_variant!).m_skeletons[0];
            var boneIndex = skeleton.m_bones.Select((b, i) => (b.m_name, i)).ToDictionary(x => x.m_name, x => x.i);
            nif.Load(Path.Combine(folder, id + ".nif"));
            using var cache = new nifly.niflycpp.BlockCache(nif.GetHeader());
            foreach (var shape in nif.GetShapes())
            {
                var verts = nif.GetVertsForShape(shape);
                using var names = new nifly.vectorstring();
                nif.GetShapeBoneList(shape, names);
                var inst = cache.EditableBlockById<nifly.NiSkinInstance>(shape.SkinInstanceRef().index);
                var sd = cache.EditableBlockById<nifly.NiSkinData>(inst.dataRef.index);
                var bones = new int[names.Count]; var stb = new Matrix4x4[names.Count];
                var weights = new List<(int, int, float)>();
                for (int i = 0; i < names.Count; i++)
                {
                    bones[i] = boneIndex[names[i]];
                    var t = new nifly.MatTransform();
                    nif.GetShapeTransformSkinToBone(shape, (uint)i, t);
                    var r = t.rotation;
                    var cx = r.opMult(new nifly.Vector3(1, 0, 0)); var cy = r.opMult(new nifly.Vector3(0, 1, 0)); var cz = r.opMult(new nifly.Vector3(0, 0, 1));
                    stb[i] = new Matrix4x4(cx.x * t.scale, cx.y * t.scale, cx.z * t.scale, 0, cy.x * t.scale, cy.y * t.scale, cy.z * t.scale, 0, cz.x * t.scale, cz.y * t.scale, cz.z * t.scale, 0, t.translation.x, t.translation.y, t.translation.z, 1);
                    foreach (var sw in sd.bones[i].vertexWeights) weights.Add((sw.index, i, sw.weight));
                }
                var v = verts.Select(p => new Vector3(p.x, p.y, p.z)).ToArray();
                shapes.Add((bones, stb, weights.ToArray(), v));
                ShapeVertexCounts.Add(v.Length);
                using var tris = new nifly.vectorTriangle();
                shape.GetTriangles(tris);
                ShapeTriangleCounts.Add(tris.Count);
                ShapeTextures.Add(nif.GetTexturePathByIndex(shape, 0) ?? "");
            }
        }

        public Anim Animation(string file)
        {
            var root = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(file)));
            var a = (HKX2.hkaInterleavedUncompressedAnimation)((HKX2.hkaAnimationContainer)root.m_namedVariants[0]!.m_variant!).m_animations[0];
            int tracks = a.m_numberOfTransformTracks, frames = a.m_transforms.Count / tracks;
            return new Anim(tracks, frames, frames > 1 ? (frames - 1) / a.m_duration : 30, a.m_transforms);
        }

        public List<string> BoneNames => skeleton.m_bones.Select(b => b.m_name).ToList();

        public Matrix4x4[] BoneWorlds(Anim anim, int frame)
        {
            var world = new Matrix4x4[anim.Tracks];
            for (int b = 0; b < anim.Tracks; b++)
            {
                var qs = anim.Transforms[frame * anim.Tracks + b];
                var local = Matrix4x4.CreateScale(qs.M31) * Matrix4x4.CreateFromQuaternion(new Quaternion(qs.M21, qs.M22, qs.M23, qs.M24)) * Matrix4x4.CreateTranslation(qs.M11, qs.M12, qs.M13);
                world[b] = skeleton.m_parentIndices[b] < 0 ? local : local * world[skeleton.m_parentIndices[b]];
            }
            return world;
        }

        public Vector3[][] Pose(Anim anim, int frame)
        {
            var world = BoneWorlds(anim, frame);
            var result = new Vector3[shapes.Count][];
            for (int s = 0; s < shapes.Count; s++)
            {
                var (bones, stb, weights, v) = shapes[s];
                var full = bones.Select((b, i) => stb[i] * world[b]).ToArray();
                var p = new Vector3[v.Length];
                foreach (var (vi, bi, w) in weights) p[vi] += Vector3.Transform(v[vi], full[bi]) * w;
                result[s] = p;
            }
            return result;
        }

        public void Dispose() => nif.Dispose();
    }
}
