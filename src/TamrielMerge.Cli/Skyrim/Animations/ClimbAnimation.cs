using System.Numerics;
using HKX2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;

namespace TamrielMerge.Skyrim.Animations;

/// <summary>
/// The climbing animation (user, 2026-10-20: "if climbing is in we need a animation for it too"; played through Open
/// Animation Replacer, "oar but make sure to add the credits"). Skyrim has no climbing animation for people, so it is
/// made here on Skyrim's own skeletons (the third-person skeleton.hkx and the first-person skeletonfirst.hkx, read from
/// the player's Skyrim - Animations.bsa): a 1.2-second loop of climbing a wall. The body leans in to the wall and looks
/// up; the hands are on the wall, one reaching up as the other pulls down to the shoulder; the opposite foot steps up
/// as its hand reaches. Arms and legs are placed by two-bone IK (each bone turned in model space so it points from its
/// joint to the next, elbows down and out, knees forward), so no bone's own axes are assumed. Written uncompressed and
/// interleaved with HKX2, as the creatures' animations are.
/// OAR swaps it in for the unarmed idle, walk, run, jump, fall and land animations (third and first person) while the
/// player has the climbing effect (TMClimbingEffect, TMClimbing), and back the moment it ends.
/// </summary>
public static class ClimbAnimation
{
    /// <summary>The unarmed movement animations the climb stands in for (whichever of them the game has).</summary>
    public static readonly string[] Replaces = ["mt_idle.hkx", "mt_walkforward.hkx", "mt_runforward.hkx", "mt_jump.hkx", "mt_jumpfall.hkx",
        "mt_jumpfallleft.hkx", "mt_jumpfallright.hkx", "mt_jumpland.hkx", "mt_jumplandleft.hkx", "mt_jumplandright.hkx"];

    const string ThirdSkeleton = @"meshes\actors\character\character assets\skeleton.hkx";
    const string FirstSkeleton = @"meshes\actors\character\_1stperson\characterassets\skeletonfirst.hkx";
    const string ThirdAnimations = @"meshes\actors\character\animations\";
    const string FirstAnimations = @"meshes\actors\character\_1stperson\animations\";
    public const float Fps = 30;
    public const int Frames = 36;

    /// <summary>A file of Skyrim's: loose in Data, else from its archives.</summary>
    static byte[]? Vanilla(string skyrimData, string path, string[] archives)
    {
        string loose = Path.Combine(skyrimData, path);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        foreach (var a in archives)
        {
            string bsa = Path.Combine(skyrimData, a);
            if (!File.Exists(bsa)) continue;
            var file = Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file is not null) return file.GetBytes();
        }
        return null;
    }

    /// <summary>The vanilla files under <paramref name="folder"/> the archives hold (to replace only those there are).</summary>
    static HashSet<string> VanillaFiles(string skyrimData, string folder)
    {
        string bsa = Path.Combine(skyrimData, "Skyrim - Animations.bsa");
        if (!File.Exists(bsa)) return [];
        return Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files.Select(f => f.Path).Where(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[folder.Length..]).Where(p => !p.Contains('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Skeleton(string Name, string[] Bones, int[] Parents, Vector3[] T, Quaternion[] Q);

    public static Skeleton ReadSkeleton(byte[] hkx)
    {
        var root = (hkRootLevelContainer)new PackFileDeserializer().Deserialize(new BinaryReaderEx(hkx));
        var container = root.m_namedVariants.Select(v => v.m_variant).OfType<hkaAnimationContainer>().First();
        var s = container.m_skeletons[0];
        return new Skeleton(s.m_name, [.. s.m_bones.Select(b => b.m_name)], [.. s.m_parentIndices.Select(p => (int)p)],
            [.. s.m_referencePose.Select(m => new Vector3(m.M11, m.M12, m.M13))],
            [.. s.m_referencePose.Select(m => Quaternion.Normalize(new Quaternion(m.M21, m.M22, m.M23, m.M24)))]);
    }

    /// <summary>The loop's frames as each bone's local transform (translation, rotation).</summary>
    public static (Vector3 T, Quaternion Q)[][] Climb(Skeleton s)
    {
        int n = s.Bones.Length;
        int Find(string part) => Array.FindIndex(s.Bones, b => b.Contains(part, StringComparison.OrdinalIgnoreCase));
        var frames = new (Vector3 T, Quaternion Q)[Frames][];
        for (int f = 0; f < Frames; f++)
        {
            float t = f / (float)Frames;
            // The rest pose to the root (world), then turned bone by bone.
            var wt = new Vector3[n];
            var wq = new Quaternion[n];
            for (int b = 0; b < n; b++)
            {
                int p = s.Parents[b];
                wt[b] = p < 0 ? s.T[b] : wt[p] + Vector3.Transform(s.T[b], wq[p]);
                wq[b] = p < 0 ? s.Q[b] : Quaternion.Normalize(wq[p] * s.Q[b]);
            }
            bool Below(int b, int top)
            {
                for (int x = b; x >= 0; x = s.Parents[x]) if (x == top) return true;
                return false;
            }
            // Turns bone `top` and everything under it by `r` about its joint, in model space.
            void Turn(int top, Quaternion r)
            {
                if (top < 0) return;
                var pivot = wt[top];
                for (int b = 0; b < n; b++)
                    if (Below(b, top))
                    {
                        wt[b] = pivot + Vector3.Transform(wt[b] - pivot, r);
                        wq[b] = Quaternion.Normalize(r * wq[b]);
                    }
            }
            static Quaternion FromTo(Vector3 a, Vector3 b)
            {
                a = Vector3.Normalize(a); b = Vector3.Normalize(b);
                float d = Vector3.Dot(a, b);
                if (d > 0.99999f) return Quaternion.Identity;
                var axis = Vector3.Cross(a, b);
                if (axis.LengthSquared() < 1e-8f) axis = MathF.Abs(a.X) < 0.9f ? Vector3.Cross(a, Vector3.UnitX) : Vector3.Cross(a, Vector3.UnitY);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(d, -1, 1)));
            }
            // Two bones from joint `upper` through `lower` to `end`: the end at `target`, bending towards `pole`.
            void Reach(int upper, int lower, int end, Vector3 target, Vector3 pole)
            {
                if (upper < 0 || lower < 0 || end < 0) return;
                float a = Vector3.Distance(wt[upper], wt[lower]), b = Vector3.Distance(wt[lower], wt[end]);
                var root = wt[upper];
                var toTarget = target - root;
                float d = Math.Clamp(toTarget.Length(), MathF.Abs(a - b) + 0.01f, a + b - 0.01f);
                var dir = Vector3.Normalize(toTarget);
                float cos = Math.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
                var bend = pole - dir * Vector3.Dot(pole, dir);
                bend = bend.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(bend);
                var elbow = root + dir * a * cos + bend * a * MathF.Sqrt(1 - cos * cos);
                Turn(upper, FromTo(wt[lower] - wt[upper], elbow - wt[upper]));
                Turn(lower, FromTo(wt[end] - wt[lower], root + dir * d - wt[lower]));
            }
            float Wave(float phase) => 0.5f * (1 + MathF.Cos(2 * MathF.PI * (t + phase)));
            // Skyrim's frame: Z up, the actor faces +Y. Lean in to the wall and look up the climb.
            Turn(Find("[Spn1]"), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -10 * MathF.PI / 180));
            Turn(Find("[Head]"), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 15 * MathF.PI / 180));
            // The wall: one plane in front of the chest that the hands and the feet are on.
            int chest = Find("[Spn2]");
            int anyUpper = Find("NPC R UpperArm ["), anyLower = Find("NPC R Forearm ["), anyHand = Find("NPC R Hand [");
            float reach = anyUpper >= 0 && anyLower >= 0 && anyHand >= 0
                ? Vector3.Distance(wt[anyUpper], wt[anyLower]) + Vector3.Distance(wt[anyLower], wt[anyHand]) : 45;
            float wall = (chest >= 0 ? wt[chest].Y : 0) + 0.55f * reach;
            foreach (var (side, sign, phase) in new[] { ("R", 1f, 0f), ("L", -1f, 0.5f) })
            {
                // Whole names up to the tag: "NPC L UpperarmTwist1" would match a bare "L UpperArm".
                int upper = Find($"NPC {side} UpperArm ["), lower = Find($"NPC {side} Forearm ["), hand = Find($"NPC {side} Hand [");
                if (upper >= 0 && lower >= 0 && hand >= 0)
                {
                    float arm = Vector3.Distance(wt[upper], wt[lower]) + Vector3.Distance(wt[lower], wt[hand]);
                    var shoulder = wt[upper];
                    // On the wall, a little in from the shoulder: from shoulder height (pulled down) to well overhead.
                    float up = Wave(phase);
                    var target = new Vector3(shoulder.X * 0.85f, wall, shoulder.Z + arm * (0.2f + 0.6f * up));
                    // Elbows bend out and down, back towards the body: never into the wall (the hand is up and ahead of the shoulder, so a pole straight down would leave only "out and forward").
                    Reach(upper, lower, hand, target, new Vector3(sign * 0.8f, -0.9f, -0.7f));
                }
                int thigh = Find($"NPC {side} Thigh ["), calf = Find($"NPC {side} Calf ["), foot = Find($"NPC {side} Foot [");
                if (thigh >= 0 && calf >= 0 && foot >= 0)
                {
                    float leg = Vector3.Distance(wt[thigh], wt[calf]) + Vector3.Distance(wt[calf], wt[foot]);
                    var hip = wt[thigh];
                    // The foot opposite the reaching hand steps up the wall; a high foot's knee turns out, not into it.
                    float lift = Wave(phase + 0.5f);
                    var target = new Vector3(hip.X + sign * 0.1f * leg, wall - 0.12f * leg, hip.Z - leg * (0.97f - 0.5f * lift));
                    Reach(thigh, calf, foot, target, new Vector3(sign * (0.25f + 0.45f * lift), 0.6f - 0.6f * lift, 0));
                }
            }
            // Back to local transforms.
            var frame = new (Vector3 T, Quaternion Q)[n];
            for (int b = 0; b < n; b++)
            {
                int p = s.Parents[b];
                if (p < 0) { frame[b] = (wt[b], wq[b]); continue; }
                var inv = Quaternion.Inverse(wq[p]);
                frame[b] = (Vector3.Transform(wt[b] - wt[p], inv), Quaternion.Normalize(inv * wq[b]));
            }
            frames[f] = frame;
        }
        return frames;
    }

    static Matrix4x4 Qs(Vector3 t, Quaternion q) => new(t.X, t.Y, t.Z, 0, q.X, q.Y, q.Z, q.W, 1, 1, 1, 0, 0, 0, 0, 0);

    public static void WriteHkx(string path, Skeleton s, (Vector3 T, Quaternion Q)[][] frames)
    {
        int tracks = s.Bones.Length;
        var anim = new hkaInterleavedUncompressedAnimation
        {
            m_type = 1,   // HK_INTERLEAVED_ANIMATION
            m_duration = frames.Length / Fps,
            m_numberOfTransformTracks = tracks,
            m_numberOfFloatTracks = 0,
            m_annotationTracks = s.Bones.Select(b => new hkaAnnotationTrack { m_trackName = b, m_annotations = [] }).ToList(),
            // The loop closes on its first frame.
            m_transforms = [.. frames.Append(frames[0]).SelectMany(f => f.Select(x => Qs(x.T, x.Q)))],
            m_floats = [],
        };
        var binding = new hkaAnimationBinding
        {
            m_originalSkeletonName = s.Name,
            m_animation = anim,
            m_transformTrackToBoneIndices = Enumerable.Range(0, tracks).Select(i => (short)i).ToList(),
            m_floatTrackToFloatSlotIndices = [],
            m_blendHint = 0,
        };
        var root = new hkRootLevelContainer
        {
            m_namedVariants = [new hkRootLevelContainerNamedVariant { m_name = "Merged Animation Container", m_className = "hkaAnimationContainer",
                m_variant = new hkaAnimationContainer { m_animations = [anim], m_bindings = [binding] } }],
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        new PackFileSerializer().Serialize(root, new BinaryWriterEx(fs), HKXHeader.SkyrimSE());
    }

    /// <summary>
    /// The OAR replacer under <paramref name="dataDir"/>: for third and first person, the mod's config, the submod
    /// "Climbing" (on while the player has <paramref name="climbingEffect"/>, interruptible so the moment it ends the
    /// game's own animations come back) and the climb at the path of each animation it replaces. Returns the files
    /// written (relative to <paramref name="dataDir"/>).
    /// </summary>
    public static List<string> WriteOar(string dataDir, string skyrimData, FormKey climbingEffect, Action<string>? log = null)
    {
        var written = new List<string>();
        string[] archives = ["Skyrim - Animations.bsa"];
        foreach (var (skeletonPath, folder) in new[] { (ThirdSkeleton, ThirdAnimations), (FirstSkeleton, FirstAnimations) })
        {
            if (Vanilla(skyrimData, skeletonPath, archives) is not { } bytes) { log?.Invoke($"climbing animation: {skeletonPath} not found"); continue; }
            var skeleton = ReadSkeleton(bytes);
            var frames = Climb(skeleton);
            var vanilla = VanillaFiles(skyrimData, folder);
            string oar = Path.Combine(dataDir, folder, "OpenAnimationReplacer", "TamrielMerge");
            Directory.CreateDirectory(Path.Combine(oar, "Climbing"));
            File.WriteAllText(Path.Combine(oar, "config.json"), """
                {
                    "name": "TamrielMerge",
                    "author": "TamrielMerge",
                    "description": "TamrielMerge's own animations: climbing walls (Daggerfall's Climbing skill)."
                }
                """);
            // OAR reads a form as its plugin and the FormID within it (hex), and swaps on the active effect.
            File.WriteAllText(Path.Combine(oar, "Climbing", "config.json"), $$"""
                {
                    "name": "Climbing",
                    "description": "While climbing a wall (TMClimbing gives the hidden climbing effect).",
                    "priority": 2000000000,
                    "interruptible": true,
                    "replaceOnLoop": true,
                    "conditions": [
                        {
                            "condition": "HasMagicEffect",
                            "Magic effect": { "pluginName": "{{climbingEffect.ModKey.FileName}}", "formID": "{{climbingEffect.ID:X}}" },
                            "Active effects only": true
                        }
                    ]
                }
                """);
            written.Add(Path.GetRelativePath(dataDir, Path.Combine(oar, "config.json")));
            written.Add(Path.GetRelativePath(dataDir, Path.Combine(oar, "Climbing", "config.json")));
            foreach (var name in Replaces.Where(vanilla.Contains))
            {
                string file = Path.Combine(oar, "Climbing", name);
                WriteHkx(file, skeleton, frames);
                written.Add(Path.GetRelativePath(dataDir, file));
            }
            log?.Invoke($"climbing animation: {skeleton.Bones.Length} bones ({Path.GetFileName(skeletonPath)}), {Frames} frames at {Fps} fps, "
                + $"standing in for {Replaces.Count(vanilla.Contains)} of the game's animations under {folder}");
        }
        return written;
    }

    /// <summary>
    /// Proof offline (the game isn't launched): Skyrim's own male body skinned to the climb at <paramref name="frames"/>
    /// (the engine's linear blend: bone pose x skin-to-bone x vertex), drawn from the side, front and above, to a PNG.
    /// Returns, per frame, each hand's and foot's position (for the log).
    /// </summary>
    public static List<string> Preview(string skyrimData, string png, int[] frames)
    {
        var skeleton = ReadSkeleton(Vanilla(skyrimData, ThirdSkeleton, ["Skyrim - Animations.bsa"]) ?? throw new FileNotFoundException(ThirdSkeleton));
        var climb = Climb(skeleton);
        var body = Vanilla(skyrimData, @"meshes\actors\character\character assets\malebody_1.nif", ["Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa"])
            ?? throw new FileNotFoundException("malebody_1.nif");
        string tmp = Path.Combine(Path.GetTempPath(), "tm-climb-body.nif");
        File.WriteAllBytes(tmp, body);
        var lines = new List<string>();
        using var nif = new nifly.NifFile();
        nif.Load(tmp);
        using var cache = new nifly.niflycpp.BlockCache(nif.GetHeader());
        var index = skeleton.Bones.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        const int W = 360;
        var rgb = new byte[W * 3 * W * frames.Length * 3];
        for (int fi = 0; fi < frames.Length; fi++)
        {
            var pose = climb[Math.Clamp(frames[fi], 0, Frames - 1)];
            var world = new Matrix4x4[pose.Length];
            for (int b = 0; b < pose.Length; b++)
            {
                var local = Matrix4x4.CreateFromQuaternion(pose[b].Q) * Matrix4x4.CreateTranslation(pose[b].T);
                world[b] = skeleton.Parents[b] < 0 ? local : local * world[skeleton.Parents[b]];
            }
            var pts = new List<Vector3>();
            foreach (var shape in nif.GetShapes())
            {
                var verts = nif.GetVertsForShape(shape);
                using var names = new nifly.vectorstring();
                nif.GetShapeBoneList(shape, names);
                var posed = new Vector3[verts.Count];
                var inst = cache.EditableBlockById<nifly.NiSkinInstance>(shape.SkinInstanceRef().index);
                var sd = cache.EditableBlockById<nifly.NiSkinData>(inst.dataRef.index);
                for (int i = 0; i < names.Count; i++)
                {
                    if (!index.TryGetValue(names[i], out int bone)) continue;
                    var stb = new nifly.MatTransform();
                    nif.GetShapeTransformSkinToBone(shape, (uint)i, stb);
                    var r = stb.rotation;
                    var cx = r.opMult(new nifly.Vector3(1, 0, 0)); var cy = r.opMult(new nifly.Vector3(0, 1, 0)); var cz = r.opMult(new nifly.Vector3(0, 0, 1));
                    var skin = new Matrix4x4(cx.x, cx.y, cx.z, 0, cy.x, cy.y, cy.z, 0, cz.x, cz.y, cz.z, 0, stb.translation.x, stb.translation.y, stb.translation.z, 1);
                    var full = skin * world[bone];
                    foreach (var sw in sd.bones[i].vertexWeights)
                    {
                        var v = verts[sw.index];
                        posed[sw.index] += Vector3.Transform(new Vector3(v.x, v.y, v.z), full) * sw.weight;
                    }
                }
                pts.AddRange(posed);
            }
            var lo = pts.Aggregate(Vector3.Min); var hi = pts.Aggregate(Vector3.Max);
            float span = MathF.Max(hi.X - lo.X, MathF.Max(hi.Y - lo.Y, hi.Z - lo.Z)) * 1.1f;
            var centre = (lo + hi) / 2;
            for (int view = 0; view < 3; view++)
                foreach (var p in pts)
                {
                    var d = p - centre;
                    (float u, float v, float depth) = view switch { 0 => (d.Y, d.Z, d.X), 1 => (d.X, d.Z, -d.Y), _ => (d.X, d.Y, d.Z) };
                    int px = (int)((u / span + 0.5f) * W), py = (int)((0.5f - v / span) * W);
                    if (px < 0 || py < 0 || px >= W || py >= W) continue;
                    byte shade = (byte)Math.Clamp(140 + depth / span * 200, 30, 255);
                    int q = ((fi * W + py) * W * 3 + view * W + px) * 3;
                    if (rgb[q] < shade) { rgb[q] = shade; rgb[q + 1] = (byte)(shade * 0.85f); rgb[q + 2] = (byte)(shade * 0.6f); }
                }
            string At(string part) => index.FirstOrDefault(kv => kv.Key.Contains(part)).Key is { } k ? $"{part} {world[index[k]].Translation:F0}" : "";
            lines.Add($"frame {frames[fi]}: {pts.Count} vertices, bounds {lo:F0} .. {hi:F0}; {At("R UpperArm [")}; {At("R Forearm [")}; {At("R Hand [")}; {At("L UpperArm [")}; {At("L Forearm [")}; {At("L Hand [")}; {At("R Calf [")}; {At("R Foot [")}");
        }
        Util.Png.WriteRgb(png, W * 3, W * frames.Length, rgb);
        File.Delete(tmp);
        return lines;
    }
}
