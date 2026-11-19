using System.Numerics;
using System.Text.Json;
using HKX2;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// A creature with its own skeleton (user, 2026-10-10: "just make a new skeleton", "we use skyrim se not le"): from a
/// model exported by tools/creatures/export_creature.py (creature.json: bones, skinned meshes, baked animations, in
/// Skyrim's frame), the Skyrim SE files under meshes\actors\tamrielmerge\&lt;id&gt;\: character assets\skeleton.hkx and
/// skeleton.nif, the body (&lt;id&gt;.nif, skinned to those bones), and animations\&lt;name&gt;.hkx (uncompressed,
/// interleaved), with its textures under textures\actors\tamrielmerge\&lt;id&gt;\. Havok files are written with HKX2
/// (MIT, src\ThirdParty), NIFs with nifly. Every bone hangs under "NPC Root [Root]", which Skyrim looks for.
/// </summary>
public sealed class CreatureRig
{
    public const string RootBone = "NPC Root [Root]";

    public sealed record Bone(string Name, int Parent, Vector3 T, Quaternion Q);
    /// <summary>A skinned mesh: vertices at its bind, and (<paramref name="SkinToBone"/>) the source's own skin-to-bone transform per bone
    /// (row vectors: p_bone = p * M) when it was bound in another pose than the skeleton's rest (Morrowind's creatures are: a
    /// vertex between two far bones, as a robe's between the calves, comes out right only so); else each bone's rest inverted.</summary>
    public sealed record Mesh(string Name, Vector3[] V, Vector3[] N, Vector2[] Uv, (int Bone, float W)[][] Weights, int[][] Tris, Dictionary<string, string> Textures,
        Dictionary<int, Matrix4x4>? SkinToBone = null, bool Hidden = false, MeshLook? Look = null);

    /// <summary>
    /// How a mesh is drawn beyond its textures (a Morrowind shape's material, alpha and vertex colours; null: opaque, matte,
    /// white): its alpha property (blend and test flags, test threshold) as the source has it, the material's alpha, the
    /// colour each vertex is tinted (the source's vertex colours, or its material colour), the colour it gives off (the glow
    /// shader, its texture or "glow" texture the glow map), whether it tests and writes the depth buffer, and the texture's
    /// wrapping (0 clamp both .. 3 wrap both).
    /// </summary>
    public sealed record MeshLook(ushort? AlphaFlags, byte AlphaThreshold, float Alpha, Vector4[]? Colors, Vector3 Emissive, bool ZTest, bool ZWrite, uint Clamp);

    /// <summary>A shader value a clip animates on a mesh: the lighting shader's variable (12 alpha, 20 U offset, 22 V offset,
    /// as vanilla's BSLightingShaderPropertyFloatController numbers them) and its keys (seconds from the clip's start).</summary>
    public sealed record LookTrack(int Mesh, int Variable, (float Time, float Value)[] Keys);

    /// <summary>A clip's look: every animated shader value as it is through the clip, and whether it repeats.</summary>
    public sealed record ClipLook(bool Loops, List<LookTrack> Tracks);

    /// <summary>
    /// The shader values each clip animates (Morrowind's alpha and texture-scroll controllers, which run on its animation
    /// groups' time: the ancestor ghost's flicker when hit, a specter's fade in death, the Heart of Akulakhan's scrolling
    /// fire): written as vanilla animates a body's materials (the chaurus flyer's, the spriggan matron's) - the body's
    /// controller manager holds one sequence per clip (<see cref="LookEvent"/>), its own behaviour graph
    /// (BSBehaviorGraphExtraData) plays the one its clip's event names, and each clip sends that event as it starts.
    /// Every clip's sequence sets every animated value, so each clip shows its own.
    /// </summary>
    public Dictionary<string, ClipLook> Looks { get; } = [];

    /// <summary>The event (and body sequence) for a clip's look.</summary>
    public static string LookEvent(string clip) => "TMLook_" + clip;

    /// <summary>The behaviour project that plays a body's looks, relative to meshes\ (what the body's BGED names).</summary>
    public static string LookProject(string id) => $@"actors\tamrielmerge\{id}\looks\{CreatureBehavior.ProjectName(id)[..^"Project".Length]}Looks.hkx";
    /// <summary>A clip: frames of each bone's local translation and rotation, and its scale per frame and bone when the source
    /// animates scale (null: all 1).</summary>
    public sealed record Clip(float Fps, (Vector3 T, Quaternion Q)[][] Frames, float[][]? Scales = null);

    public List<Bone> Bones { get; } = [];
    public List<Mesh> Meshes { get; } = [];
    public Dictionary<string, Clip> Clips { get; } = [];

    /// <summary>The weapon styles it holds (MwCreatureSource.WeaponStyles keys: "1h", "2c"...), whose clips its graph picks by
    /// the weapon in its hand; none for a creature that fights with its own body.</summary>
    public List<string> WeaponStyles { get; } = [];

    /// <summary>Whether it is a biped (Morrowind's FLAG 0x01): only a biped turns with its turning animations.</summary>
    public bool Biped { get; set; }

    /// <summary>How its wander package picks idles, one set per distinct set of chances among the creatures drawn with it
    /// (MwCreatureSource.IdleOdds: [0] no idle, [1..8] Idle2..Idle9 after each roll); none: it never picks one. Its graph
    /// plays the set the variable iTMIdleSet names (set per actor as its 3D loads: CreatureRecords).</summary>
    public List<float[]> IdleSets { get; } = [];

    /// <summary>Per bone, the part of the body it is in Morrowind's blend masks: 0 lower body, 1 torso, 2 left arm, 3 right
    /// arm (empty: no such parts).</summary>
    public List<int> BlendMasks { get; } = [];

    /// <summary>What else goes into the body as it is written (a Morrowind creature's particle systems): called with the
    /// body, the data folder, each bone's node block id (by bone index) and each mesh's shape block id (by mesh index).</summary>
    public List<Action<NifFile, string, uint[], uint[]>> BodyExtras { get; } = [];

    /// <summary>How many particle systems the body got (set as it is written).</summary>
    public int ParticleSystems { get; set; }

    /// <summary>Bones drawn at scale 0 (sheath nodes: what hangs there is out of sight).</summary>
    public HashSet<int> HiddenBones { get; } = [];

    /// <summary>The creature's own animation events per clip (time in seconds, text): "SoundPlay.&lt;sound&gt;" for its own
    /// sounds (<see cref="Sounds"/>), "HitFrame" where its attack lands. A clip with any replaces the borrowed graph's
    /// sounds and hit timing with these.</summary>
    public Dictionary<string, List<(float Time, string Text)>> Events { get; } = [];

    /// <summary>The creature's own sound files by sound name (the "&lt;sound&gt;" its events play): written under
    /// sound\fx\tamrielmerge\creatures\&lt;id&gt;\ as &lt;name&gt;.wav, one sound descriptor each (<see cref="CreatureRecords"/>).</summary>
    public Dictionary<string, byte[]> Sounds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The actor's collision box (centre, half extents) when the source game gives one (Morrowind's "Bounding Box"
    /// node); else it is the body's at rest.</summary>
    public (Vector3 Center, Vector3 HalfExtents)? Box { get; set; }

    /// <summary>How fast the creature moves (units a second) in its walk and run clips when the source game says (Morrowind's
    /// speed settings and the creature's Speed): their motion, and the movement type's speeds.</summary>
    public Dictionary<string, float> MoveSpeeds { get; } = [];

    /// <summary>Which way a moving clip carries it (+Y forward, -Y back, ∓X left and right); forward when not given.</summary>
    public Dictionary<string, Vector2> MoveWays { get; } = [];

    /// <summary>The pace (units a second) a moving clip is played by: its graph plays it at the actor's speed over this, at
    /// most 10 times (Morrowind's: MwCreatureSource.Velocity); a clip without one plays as it is.</summary>
    public Dictionary<string, float> MovePaces { get; } = [];

    /// <summary>Where sounds go: sound\fx\tamrielmerge\creatures\&lt;id&gt;.</summary>
    public static string SoundFolder(string id) => $@"sound\fx\tamrielmerge\creatures\{id}";

    /// <summary>Reads creature.json; every bone without a parent goes under <see cref="RootBone"/> (bone 0).</summary>
    public static CreatureRig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = doc.RootElement;
        var rig = new CreatureRig();
        rig.Bones.Add(new Bone(RootBone, -1, Vector3.Zero, Quaternion.Identity));
        var names = new Dictionary<string, int> { [RootBone] = 0 };
        static Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        static Quaternion Q(JsonElement e) => Quaternion.Normalize(new Quaternion(e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle(), e[0].GetSingle()));   // stored w, x, y, z
        foreach (var b in r.GetProperty("bones").EnumerateArray())
        {
            string name = b.GetProperty("name").GetString()!;
            int parent = b.GetProperty("parent").ValueKind == JsonValueKind.Null ? 0 : names[b.GetProperty("parent").GetString()!];
            names[name] = rig.Bones.Count;
            rig.Bones.Add(new Bone(name, parent, V3(b.GetProperty("t")), Q(b.GetProperty("q"))));
        }
        foreach (var m in r.GetProperty("meshes").EnumerateArray())
        {
            var v = m.GetProperty("vertices").EnumerateArray().Select(V3).ToArray();
            var n = m.GetProperty("normals").EnumerateArray().Select(V3).ToArray();
            var uv = m.GetProperty("uvs").EnumerateArray().Select(e => new Vector2(e[0].GetSingle(), e[1].GetSingle())).ToArray();
            // Exported bone indices count from the first exported bone; ours from the root before it.
            var w = m.GetProperty("weights").EnumerateArray().Select(e => e.EnumerateArray().Select(p => (p[0].GetInt32() + 1, p[1].GetSingle())).ToArray()).ToArray();
            var tris = m.GetProperty("triangles").EnumerateArray().Select(t => t.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToArray();
            // Texture paths relative to the export (its tex folder: what the tool ships), absolute in older exports.
            string here = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var tex = m.GetProperty("textures").EnumerateObject().ToDictionary(p => p.Name,
                p => Path.IsPathRooted(p.Value.GetString()!) ? p.Value.GetString()! : Path.GetFullPath(Path.Combine(here, p.Value.GetString()!)));
            rig.Meshes.Add(new Mesh(m.GetProperty("name").GetString()!, v, n, uv, w, tris, tex));
        }
        foreach (var a in r.GetProperty("animations").EnumerateObject())
        {
            float fps = a.Value.GetProperty("fps").GetSingle();
            var frames = a.Value.GetProperty("frames").EnumerateArray().Select(f =>
                new[] { (Vector3.Zero, Quaternion.Identity) }.Concat(f.EnumerateArray().Select(b => (V3(b.GetProperty("t")), Q(b.GetProperty("q"))))).ToArray()).ToArray();
            rig.Clips[a.Name] = new Clip(fps, frames);
        }
        return rig;
    }

    /// <summary>Each bone's rest transform to the root (translation, rotation), parents first.</summary>
    public (Vector3 T, Quaternion Q)[] RestWorld()
    {
        var world = new (Vector3 T, Quaternion Q)[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            var b = Bones[i];
            world[i] = b.Parent < 0 ? (b.T, b.Q) : (world[b.Parent].T + Vector3.Transform(b.T, world[b.Parent].Q), world[b.Parent].Q * b.Q);
        }
        return world;
    }

    public static string MeshFolder(string id) => $@"meshes\actors\tamrielmerge\{id}";
    public static string TextureFolder(string id) => $@"textures\actors\tamrielmerge\{id}";

    /// <summary>Writes every file under <paramref name="dataDir"/>; returns their paths (relative to it).</summary>
    public List<string> Write(string dataDir, string id)
    {
        var written = new List<string>();
        string meshDir = Path.Combine(dataDir, MeshFolder(id));
        // A rebuild starts clean (a new behaviour template names its animations differently).
        if (Directory.Exists(meshDir)) Directory.Delete(meshDir, recursive: true);
        Directory.CreateDirectory(Path.Combine(meshDir, "character assets"));
        Directory.CreateDirectory(Path.Combine(meshDir, "animations"));

        string skeletonHkx = Path.Combine(meshDir, "character assets", "skeleton.hkx");
        WriteHavok(skeletonHkx, new hkaAnimationContainer { m_skeletons = [Skeleton()] });
        written.Add(skeletonHkx);
        string skeletonNif = Path.Combine(meshDir, "character assets", "skeleton.nif");
        WriteSkeletonNif(skeletonNif);
        written.Add(skeletonNif);
        string body = Path.Combine(meshDir, id + ".nif");
        WriteBody(body, dataDir, id);
        written.Add(body);
        foreach (var (name, clip) in Clips)
        {
            string file = Path.Combine(meshDir, "animations", name + ".hkx");
            WriteHavok(file, Animation(clip, name, Events.GetValueOrDefault(name)));
            written.Add(file);
        }
        string soundDir = Path.Combine(dataDir, SoundFolder(id));
        if (Directory.Exists(soundDir)) Directory.Delete(soundDir, recursive: true);
        foreach (var (name, wav) in Sounds)
        {
            Directory.CreateDirectory(soundDir);
            string file = Path.Combine(soundDir, name + ".wav");
            File.WriteAllBytes(file, wav);
            written.Add(file);
        }
        return written.Select(f => Path.GetRelativePath(dataDir, f)).ToList();
    }

    // ---- Havok ----

    /// <summary>A Havok QS transform; the rotation made unit length (an interpolated quaternion that isn't turns a bone by
    /// the wrong angle: 2026-10-21, the clannfear's spine 0.046 degrees off from System.Numerics Slerp's unnormalised blend).</summary>
    internal static Matrix4x4 QsTransform(Vector3 t, Quaternion q, float s = 1)
    {
        q = Quaternion.Normalize(q);
        return new(
        t.X, t.Y, t.Z, 0,
        q.X, q.Y, q.Z, q.W,
        s, s, s, 0,
        0, 0, 0, 0);
    }

    hkaSkeleton Skeleton() => new()
    {
        m_name = RootBone,
        m_parentIndices = Bones.Select(b => (short)b.Parent).ToList(),
        m_bones = Bones.Select(b => new hkaBone { m_name = b.Name, m_lockTranslation = false }).ToList(),
        m_referencePose = Bones.Select((b, i) => QsTransform(b.T, b.Q, HiddenBones.Contains(i) ? 0 : 1)).ToList(),
        m_referenceFloats = [],
        m_floatSlots = [],
        m_localFrames = [],
    };

    /// <param name="annotations">Events on the first track (HitFrame, footsteps...), as Havok annotations.</param>
    hkaAnimationContainer Animation(Clip clip, string name, IReadOnlyList<(float Time, string Text)>? annotations = null)
    {
        int tracks = Bones.Count, frames = clip.Frames.Length;
        var transforms = new List<Matrix4x4>(frames * tracks);
        for (int fi = 0; fi < frames; fi++)
            for (int b = 0; b < tracks; b++) transforms.Add(QsTransform(clip.Frames[fi][b].T, clip.Frames[fi][b].Q, HiddenBones.Contains(b) ? 0 : clip.Scales?[fi][b] ?? 1));
        var anim = new hkaInterleavedUncompressedAnimation
        {
            m_type = 1,   // HK_INTERLEAVED_ANIMATION
            m_duration = Math.Max(frames - 1, 1) / clip.Fps,
            m_numberOfTransformTracks = tracks,
            m_numberOfFloatTracks = 0,
            m_annotationTracks = Bones.Select((b, i) => new hkaAnnotationTrack
            {
                m_trackName = b.Name,
                m_annotations = i == 0 && annotations is not null
                    ? annotations.Select(a => new hkaAnnotationTrackAnnotation { m_time = a.Time, m_text = a.Text }).ToList()
                    : [],
            }).ToList(),
            m_transforms = transforms,
            m_floats = [],
        };
        var binding = new hkaAnimationBinding
        {
            m_originalSkeletonName = RootBone,
            m_animation = anim,
            m_transformTrackToBoneIndices = Enumerable.Range(0, tracks).Select(i => (short)i).ToList(),
            m_floatTrackToFloatSlotIndices = [],
            m_blendHint = 0,
        };
        return new hkaAnimationContainer { m_animations = [anim], m_bindings = [binding] };
    }

    /// <summary>One of the creature's clips as an animation file at <paramref name="path"/> (a behaviour graph borrowed from
    /// another creature plays its clips by its own file names); returns the clip's length in seconds.</summary>
    public float WriteClip(string path, string clip, IReadOnlyList<(float Time, string Text)>? annotations = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = Clips[clip];
        WriteHavok(path, Animation(c, clip, annotations));
        return Math.Max(c.Frames.Length - 1, 1) / c.Fps;
    }

    /// <summary>A clip it dies with ("death", "death2", "deathknockdown", "swimdeath"...).</summary>
    public static bool IsDeath(string clip) => clip.StartsWith("death", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("swimdeath", StringComparison.OrdinalIgnoreCase);

    public float Duration(string clip) => Math.Max(Clips[clip].Frames.Length - 1, 1) / Clips[clip].Fps;

    /// <summary>
    /// A death for a model that came without one (user, 2026-10-10: "fix the deaths"): from its first idle pose the whole
    /// body topples onto its left side about the left edge of its footprint, falling faster as it goes (0.8 s), then lies
    /// still (0.5 s). The engine plays it on "DeathAnimation" and the creature stays where it fell.
    /// </summary>
    public void EnsureDeath()
    {
        if (Clips.Keys.Any(IsDeath)) return;
        var start = (Clips.GetValueOrDefault("idle") ?? Clips.Values.First()).Frames[0];
        var world = RestWorld();
        float halfWidth = Meshes.Count > 0 ? Meshes.SelectMany(m => m.V).Max(v => MathF.Abs(v.X)) : world.Max(w => MathF.Abs(w.T.X));
        var pivot = new Vector3(-halfWidth, 0, 0);
        const float Fps = 30;
        int fall = 24, lie = 15;
        (Vector3 T, Quaternion Q)[] Fallen(float u, float drop)
        {
            float angle = -MathF.PI / 2 * 0.95f * u * u;          // about +Y (forward): the top goes to -X
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
            var pose = ((Vector3 T, Quaternion Q)[])start.Clone();
            // Bones under the root carry the fall (the root itself stays where the engine puts the actor).
            for (int b = 1; b < Bones.Count; b++)
                if (Bones[b].Parent == 0)
                    pose[b] = (pivot + Vector3.Transform(start[b].T - pivot, turn) - new Vector3(0, 0, drop * u), Quaternion.Normalize(turn * start[b].Q));
            return pose;
        }
        // Where the body comes to rest: the fallen pose, skinned, brought down (or up) onto the ground.
        float drop = LowestPoint(Fallen(1, 0));
        var frames = new (Vector3 T, Quaternion Q)[fall + lie + 1][];
        for (int f = 0; f <= fall + lie; f++) frames[f] = Fallen(Math.Min(f, fall) / (float)fall, drop);
        Clips["death"] = new Clip(Fps, frames);
    }

    /// <summary>The lowest point of the body in <paramref name="pose"/> (local transforms), skinned as the engine does:
    /// each vertex by its bones' pose-to-root times rest-to-root inverted, weighted.</summary>
    public float LowestPoint((Vector3 T, Quaternion Q)[] pose)
    {
        var rest = RestWorld();
        var posed = new (Vector3 T, Quaternion Q)[Bones.Count];
        for (int b = 0; b < Bones.Count; b++)
            posed[b] = Bones[b].Parent < 0 ? pose[b] : (posed[Bones[b].Parent].T + Vector3.Transform(pose[b].T, posed[Bones[b].Parent].Q), posed[Bones[b].Parent].Q * pose[b].Q);
        float lowest = float.MaxValue;
        foreach (var m in Meshes)
            for (int i = 0; i < m.V.Length; i++)   // every vertex (user, 2026-10-21: "dont skip vertices")
            {
                var p = Vector3.Zero;
                foreach (var (b, w) in m.Weights[i])
                {
                    var local = Vector3.Transform(m.V[i], SkinToBoneOf(m, b, rest));
                    p += (posed[b].T + Vector3.Transform(local, posed[b].Q)) * w;
                }
                lowest = MathF.Min(lowest, p.Z);
            }
        return lowest == float.MaxValue ? 0 : lowest;
    }

    internal static void WriteHavok(string path, hkaAnimationContainer container)
    {
        var root = new hkRootLevelContainer
        {
            m_namedVariants =
            [
                new hkRootLevelContainerNamedVariant { m_name = "Merged Animation Container", m_className = "hkaAnimationContainer", m_variant = container },
            ],
        };
        using var fs = File.Create(path);
        new PackFileSerializer().Serialize(root, new BinaryWriterEx(fs), HKXHeader.SkyrimSE());
    }

    // ---- NIF ----

    /// <summary>nifly's rotation (column vectors: R * axis is the axis turned) from a quaternion.</summary>
    static Matrix3 Rotation(Quaternion q)
    {
        var x = Vector3.Transform(Vector3.UnitX, q); var y = Vector3.Transform(Vector3.UnitY, q); var z = Vector3.Transform(Vector3.UnitZ, q);
        return new Matrix3(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
    }

    static MatTransform Transform(Vector3 t, Quaternion q, float s = 1) => new() { translation = new nifly.Vector3(t.X, t.Y, t.Z), rotation = Rotation(q), scale = s };

    /// <summary>A mesh's skin-to-bone transform for a bone: the source's own, else the bone's rest inverted.</summary>
    Matrix4x4 SkinToBoneOf(Mesh m, int bone, (Vector3 T, Quaternion Q)[] restWorld)
    {
        if (m.SkinToBone is { } own && own.TryGetValue(bone, out var mm)) return mm;
        var (wt, wq) = restWorld[bone];
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(wq) * Matrix4x4.CreateTranslation(wt), out var inv);
        return inv;
    }

    /// <summary>The bones as nodes under the file's root, in order (index i = Bones[i]).</summary>
    List<NiNode> AddBones(NifFile nif)
    {
        var nodes = new List<NiNode>();
        foreach (var b in Bones)
            nodes.Add(b.Parent < 0 ? nif.AddNode(b.Name, Transform(b.T, b.Q)) : nif.AddNode(b.Name, Transform(b.T, b.Q, HiddenBones.Contains(nodes.Count) ? 0 : 1), nodes[b.Parent]));
        return nodes;
    }

    /// <summary>
    /// The skeleton, with the root's extra data every vanilla skeleton has: "BBX" (BSBound, the actor's bounding box:
    /// without it the game drew none of the creatures; user, 2026-10-11: "some creatures are invisible in dungeons", "all
    /// that we replaced") from the bodies at rest, standing on the ground as vanilla's do, and "BSX" (vanilla 198 is Havok,
    /// Ragdoll, Dynamic and Articulated: without the first two, as these skeletons have no ragdoll bodies).
    /// </summary>
    void WriteSkeletonNif(string path)
    {
        using var nif = new NifFile();
        nif.CreateNamedBSFadeNode(NiVersion.getSSE(), "skeleton.nif");
        AddRootData(nif, Meshes.SelectMany(m => m.V).ToList(), Box);
        AddBones(nif);
        nif.Save(path);
    }

    static void AddRootData(NifFile nif, IReadOnlyList<Vector3> bodyAtRest, (Vector3 Center, Vector3 HalfExtents)? box = null)
    {
        var root = nif.GetRootNode();
        if (box is { } b)
        {
            // The source game's own collision box for the actor.
            var bound = BlockMeshBuilder.Owned(new BSBound
            {
                center = new nifly.Vector3(b.Center.X, b.Center.Y, b.Center.Z),
                halfExtents = new nifly.Vector3(MathF.Max(b.HalfExtents.X, 8), MathF.Max(b.HalfExtents.Y, 8), MathF.Max(b.HalfExtents.Z, 8)),
            });
            bound.name = new NiStringRef("BBX");
            nif.AssignExtraData(root, bound);
        }
        else if (bodyAtRest.Count > 0)
        {
            float halfX = bodyAtRest.Max(v => MathF.Abs(v.X)), halfY = bodyAtRest.Max(v => MathF.Abs(v.Y));
            float bottom = MathF.Min(0, bodyAtRest.Min(v => v.Z)), top = bodyAtRest.Max(v => v.Z);
            var bound = BlockMeshBuilder.Owned(new BSBound
            {
                center = new nifly.Vector3(0, 0, (bottom + top) / 2),
                halfExtents = new nifly.Vector3(MathF.Max(halfX, 8), MathF.Max(halfY, 8), MathF.Max((top - bottom) / 2, 8)),
            });
            bound.name = new NiStringRef("BBX");
            nif.AssignExtraData(root, bound);
        }
        BlockMeshBuilder.AddBsxFlags(nif, root, 64 | 128);
    }

    /// <summary>
    /// A creature already built (meshes\actors\tamrielmerge\&lt;id&gt; under <paramref name="meshesDir"/>) given the
    /// skeleton root data <see cref="WriteSkeletonNif"/> now writes, from its body's vertices; false when it has it or
    /// isn't there. For creatures built before 2026-10-11 (`creature-skeleton-fix`).
    /// </summary>
    public static bool AddMissingRootData(string meshesDir, string id)
    {
        string folder = Path.Combine(meshesDir, "actors", "tamrielmerge", id);
        string skeleton = Path.Combine(folder, "character assets", "skeleton.nif"), body = Path.Combine(folder, id + ".nif");
        if (!File.Exists(skeleton) || !File.Exists(body)) return false;
        using var nif = new NifFile();
        if (nif.Load(skeleton) != 0) return false;
        var hdr = nif.GetHeader();
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
            if (hdr.GetBlockById(i).GetBlockName() == "BSBound") return false;
        var verts = new List<Vector3>();
        using (var b = new NifFile())
        {
            if (b.Load(body) != 0) return false;
            foreach (var shape in b.GetShapes())
                foreach (var v in b.GetVertsForShape(shape)) verts.Add(new Vector3(v.x, v.y, v.z));
        }
        AddRootData(nif, verts);
        nif.Save(skeleton);
        return true;
    }

    void WriteBody(string path, string dataDir, string id)
    {
        using var nif = new NifFile();
        // A plain NiNode root, as vanilla's creature bodies and our worn meshes have: under a BSFadeNode root the game
        // draws nothing of a skinned body (seen in game 2026-11-09: imp, bat, guar, scorpion and lich all invisible, their
        // skeletons and behaviour working; with only the root's type changed to NiNode the imp and the bat drew).
        nif.Create(NiVersion.getSSE());
        nif.GetRootNode().name = new NiStringRef(id);
        var hdr = nif.GetHeader();
        var nodes = AddBones(nif);
        var restWorld = RestWorld();
        string texDir = Path.Combine(dataDir, TextureFolder(id));
        Directory.CreateDirectory(texDir);
        SplitWideMeshes();
        var meshIds = new uint[Meshes.Count];
        var madeTextures = new Dictionary<(string?, string, string), string?>();
        var shapeNames = new HashSet<string>(StringComparer.Ordinal);
        for (int mi = 0; mi < Meshes.Count; mi++)
        {
            var m = Meshes[mi];
            if (m.V.Length > 65535) throw new InvalidDataException($"{id} {m.Name}: {m.V.Length} vertices (a NIF shape holds 65535)");
            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            for (int i = 0; i < m.V.Length; i++)
            {
                vv.Add(new nifly.Vector3(m.V[i].X, m.V[i].Y, m.V[i].Z));
                uu.Add(new nifly.Vector2(m.Uv[i].X, m.Uv[i].Y));
                nn.Add(new nifly.Vector3(m.N[i].X, m.N[i].Y, m.N[i].Z));
            }
            foreach (var t in m.Tris) tt.Add(new Triangle((ushort)t[0], (ushort)t[1], (ushort)t[2]));
            // nifly finds a shape by its name when it sets weights: a name another shape already has (the bear's two
            // "Tri bearclaw_L07") put the second one's weights on the first and left it on none (2026-11-06). Such a
            // shape is skinned under a name of its own and gets its real name back after.
            string realName = FileSafe($"{id}_{m.Name}");
            string shapeName = shapeNames.Add(realName) ? realName : $"{realName} #{mi}";
            shapeNames.Add(shapeName);
            var shape = nif.CreateShapeFromData(shapeName, vv, tt, uu, nn);
            meshIds[mi] = hdr.GetBlockID(shape);
            // A tint copy (MwCreatureSource: "<shape> [tint k]") or a part (SplitWideMeshes: "<shape> [part k]") draws with
            // its shape's own textures, made once.
            string texStem = FileSafe($"{id}_{PartOf(m.Name)}");
            string? Made(string? source, string suffix)
            {
                var key = (source, texStem, suffix);
                if (!madeTextures.TryGetValue(key, out var path)) madeTextures[key] = path = Texture(source, texDir, texStem, suffix, alpha: false);
                return path;
            }
            string? diffuse = Made(m.Textures.GetValueOrDefault("diffuse"), "");
            string? normal = Made(m.Textures.GetValueOrDefault("normal"), "_n");
            string? glowMap = Made(m.Textures.GetValueOrDefault("glow"), "_g");
            if (diffuse is not null) nif.SetTextureSlot(shape, Path.GetRelativePath(dataDir, diffuse), 0);
            nif.SetTextureSlot(shape, normal is not null ? Path.GetRelativePath(dataDir, normal) : DfTextureExport.FlatNormal, 1);
            var look = m.Look;
            // The colour it gives off: its own glow map, else (Morrowind multiplies a material's emissive colour into the
            // texture) its own texture as the glow map, tinted by that colour, as MwMeshConverter draws Morrowind's.
            var emissive = look?.Emissive ?? Vector3.Zero;
            bool glows = glowMap is not null || (diffuse is not null && emissive.X + emissive.Y + emissive.Z > 0.05f);
            if (glows) nif.SetTextureSlot(shape, Path.GetRelativePath(dataDir, glowMap ?? diffuse!), 2);
            // Tangents for drawn shapes (a hidden emitter shape has no texture coordinates; nifly crashes on them).
            if (!m.Hidden) nif.CalcTangentsForShape(shape);
            bool colors = look?.Colors is { } cs && cs.Length == m.V.Length;
            if (colors)
            {
                using var cols = new vectorColor4();
                foreach (var c in look!.Colors!) cols.Add(new Color4(c.X, c.Y, c.Z, c.W));
                nif.SetColorsForShape(shape, cols);
            }
            if (look?.AlphaFlags is { } af) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = af, threshold = look.AlphaThreshold }));
            bool blended = look?.AlphaFlags is { } bf && (bf & 1) != 0 && (bf & 0x200) == 0;
            bool vertexAlpha = colors && look!.AlphaFlags is not null && look.Colors!.Any(c => c.W < 1);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                // Matte (the models are never shiny; Morrowind's creatures have no specular): skinned, own emit, cast + receive
                // shadows, depth test as the source has it; vertex alpha when its colours carry some.
                sh.shaderFlags1 = (look is null || look.ZTest ? 0x80000000u : 0) | 0x400302u | (vertexAlpha ? 0x8u : 0);
                // Depth write (off for blended surfaces, as MwMeshConverter draws Morrowind's, or where the source turns it
                // off), vertex colours when tinted, glow map when it glows.
                sh.shaderFlags2 = (blended || look is { ZWrite: false } ? 0u : 0x1u) | (colors ? 0x20u : 0) | (glows ? 0x40u : 0) | 0x8000u;
                if (glows) sh.SetShaderType(2u);
                sh.glossiness = 1;
                sh.specularStrength = 0;
                sh.alpha = look?.Alpha ?? 1;
                sh.emissiveColor = glows ? (glowMap is not null ? new nifly.Vector3(1, 1, 1) : new nifly.Vector3(emissive.X, emissive.Y, emissive.Z)) : new nifly.Vector3(0, 0, 0);
                sh.emissiveMultiple = glows ? 1 : 0;
                sh.textureClampMode = (TexClampMode)(look?.Clamp ?? 3);
            });

            // Skinning: only the bones this shape uses, each with its bind (bone-to-root inverted).
            var used = m.Weights.SelectMany(w => w.Select(p => p.Bone)).Distinct().OrderBy(b => b).ToList();
            var slot = used.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            nif.CreateSkinning(shape);
            using (var ids = new vectorint()) { foreach (int b in used) ids.Add((int)hdr.GetBlockID(nodes[b])); nif.SetShapeBoneIDList(shape, ids); }
            nif.SetShapeTransformGlobalToSkin(shape, new MatTransform { translation = new nifly.Vector3(0, 0, 0), rotation = Rotation(Quaternion.Identity), scale = 1 });
            for (int i = 0; i < used.Count; i++)
            {
                Matrix4x4.Decompose(SkinToBoneOf(m, used[i], restWorld), out var sc, out var sq, out var st);
                nif.SetShapeTransformSkinToBone(shape, (uint)i, Transform(st, sq, sc.X));
            }
            for (int v = 0; v < m.V.Length; v++)
            {
                using var bids = new vectoruchar(); using var ws = new vectorfloat();
                foreach (var (b, w) in m.Weights[v]) { bids.Add((byte)slot[b]); ws.Add(w); }
                nif.SetShapeVertWeights(shapeName, (ushort)v, bids, ws);
            }
            // The skin data's per-bone weights and bounds (in the bone's own space).
            var skinData = SkinData(nif, shape);
            if (skinData is not null)
            {
                BlockMeshBuilder.EditBlock<NiSkinData>(nif, skinData.Value, sd =>
                {
                    sd.hasVertWeights = 1;
                    var boneList = sd.bones;
                    for (int i = 0; i < used.Count && i < boneList.Count; i++)
                    {
                        var bd = boneList[i];
                        var list = new vectorSkinWeight();
                        var toBone = SkinToBoneOf(m, used[i], restWorld);
                        var pts = new List<Vector3>();
                        for (int v = 0; v < m.V.Length; v++)
                            foreach (var (b, w) in m.Weights[v])
                                if (b == used[i] && w > 0)
                                {
                                    list.Add(new SkinWeight { index = (ushort)v, weight = w });
                                    pts.Add(Vector3.Transform(m.V[v], toBone));
                                }
                        bd.vertexWeights = list;
                        bd.numVertices = (ushort)list.Count;
                        var c = pts.Count > 0 ? pts.Aggregate(Vector3.Zero, (a, p) => a + p) / pts.Count : Vector3.Zero;
                        bd.bounds = new BoundingSphere { center = new nifly.Vector3(c.X, c.Y, c.Z), radius = pts.Count > 0 ? pts.Max(p => Vector3.Distance(p, c)) : 0 };
                        boneList[i] = bd;
                    }
                    sd.bones = boneList;
                });
            }
            // The body part (one partition, 32: the body), then the partitions the game skins with: each one's bones, its
            // vertices' weights and bone indices, its triangles. In that order: SetDefaultPartition puts back a bare
            // partition (vertices only, no bones), and called after the update it left every creature body with nothing
            // to skin it, so the game drew none of them (2026-11-06, user: "bats and imps in daggerfall as for scorpions";
            // nif-partitions: 0 bones in all 100 creature models, where vanilla's 4,054 have 1 to 60).
            nif.SetDefaultPartition(shape);
            SplitPartitions(nif, shape, m, slot);
            // Not drawn (Morrowind's hidden emitter shapes: a particle array still emits from it); flagged last, once skinned.
            if (m.Hidden) BlockMeshBuilder.EditBlock<BSTriShape>(nif, meshIds[mi], s => s.flags |= 1);
            if (shapeName != realName) BlockMeshBuilder.EditBlock<BSTriShape>(nif, meshIds[mi], s => s.name = new NiStringRef(realName));
        }
        var boneIds = nodes.Select(n => hdr.GetBlockID(n)).ToArray();
        foreach (var extra in BodyExtras) extra(nif, dataDir, boneIds, meshIds);
        if (Looks.Count > 0) AddLooks(nif, id, meshIds);
        if (BodyExtras.Count > 0 || Looks.Count > 0) BlockMeshBuilder.AddBsxFlags(nif, nif.GetRootNode(), 1);   // animated: its effects run
        nif.Save(path);
        // Read back: a body the game can't skin is drawn as nothing, so it is an error here, not a creature.
        var (_, partitionIssues) = CreatureBones.Partitions(path);
        if (partitionIssues.Count > 0)
            throw new InvalidDataException($"{Path.GetFileName(path)}: its skin partitions can't draw it: {string.Join("; ", partitionIssues.Take(4))}");
    }

    /// <summary>
    /// The clips' looks (<see cref="Looks"/>) in the body, laid out as vanilla's (charurusflyer.nif): on each animated mesh's
    /// shader one float controller per variable (manager controlled, a blend interpolator the manager sets); under the root
    /// a controller manager with one sequence per clip (its keys for every animated value, "start" and "end" text keys,
    /// looping for a repeating clip), an object palette naming the meshes, and the behaviour graph that plays the sequences
    /// (<see cref="LookProject"/>, not driving the skeleton).
    /// </summary>
    void AddLooks(NifFile nif, string id, uint[] meshIds)
    {
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        uint rootId = hdr.GetBlockID(root);
        uint Add(NiObject block) => hdr.AddBlock(BlockMeshBuilder.Owned(block));
        const uint NoBlock = uint.MaxValue;

        // The controllers: one per animated (mesh, variable), chained on the mesh's shader.
        var controllerOf = new Dictionary<(int Mesh, int Variable), uint>();
        float longest = Looks.Values.Max(l => l.Tracks.Count == 0 ? 0 : l.Tracks.Max(t => t.Keys.Length == 0 ? 0 : t.Keys[^1].Time));
        foreach (var group in Looks.Values.SelectMany(l => l.Tracks).Select(t => (t.Mesh, t.Variable)).Distinct().GroupBy(t => t.Mesh))
        {
            var shape = (NiShape)hdr.GetBlockById(meshIds[group.Key]);
            uint shaderId = hdr.GetBlockID(nif.GetShader(shape));
            uint next = NoBlock;
            foreach (var (mesh, variable) in group.Reverse())
            {
                var blend = new NiBlendFloatInterpolator { value = float.MinValue };
                blend.flags = InterpBlendFlags.INTERP_BLEND_MANAGER_CONTROLLED; blend.arraySize = 2; blend.weightThreshold = 0; blend.singleIndex = 255;
                blend.highPriority = (char)0xFFFD; blend.nextHighPriority = (char)0xFFFD; blend.singleTime = float.MinValue;
                uint blendId = Add(blend);
                var c = new BSLightingShaderPropertyFloatController { flags = 0x6C, frequency = 1, phase = 0, startTime = 0, stopTime = longest, typeOfControlledVariable = (uint)variable };
                c.targetRef.index = shaderId;
                c.interpolatorRef.index = blendId;
                c.nextControllerRef.index = next;
                next = controllerOf[(mesh, variable)] = Add(c);
            }
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, shaderId, sh => sh.controllerRef.index = next);
        }

        var manager = new NiControllerManager { flags = 0x4C, frequency = 1, phase = 0, startTime = float.MaxValue, stopTime = float.MinValue, cumulative = false };
        manager.targetRef.index = rootId;
        uint managerId = Add(manager);
        var seqIds = new List<uint>();
        foreach (var (clip, look) in Looks)
        {
            float length = Duration(clip);
            var seq = NiflyRaw.NewSequence(look.Tracks.Count);
            var slots = NiflyRaw.Links(seq);
            for (int k = 0; k < look.Tracks.Count; k++)
            {
                var t = look.Tracks[k];
                var data = new NiFloatData();
                data.data.SetInterpolationType(NiKeyType.LINEAR_KEY);
                foreach (var (time, value) in t.Keys) NifKeys.Add(data.data, time, value);
                var interp = new NiFloatInterpolator { floatValue = float.MinValue };
                interp.dataRef.index = Add(data);
                slots[k].interpolatorRef.index = Add(interp);
                slots[k].controllerRef.index = controllerOf[(t.Mesh, t.Variable)];
                slots[k].priority = 0;
                slots[k].nodeName = new NiStringRef(((NiShape)hdr.GetBlockById(meshIds[t.Mesh])).name.get());
                slots[k].propType = new NiStringRef("BSLightingShaderProperty");
                slots[k].ctrlType = new NiStringRef("BSLightingShaderPropertyFloatController");
                slots[k].ctrlID = new NiStringRef(t.Variable.ToString(System.Globalization.CultureInfo.InvariantCulture));
                slots[k].interpID = new NiStringRef("");
                NiflyRaw.Ref(slots[k].stringPaletteRef).index = NoBlock;
            }
            seq.name = new NiStringRef(LookEvent(clip));
            seq.cycleType = look.Loops ? CycleType.CYCLE_LOOP : CycleType.CYCLE_CLAMP;
            seq.frequency = 1;
            seq.startTime = 0;
            seq.stopTime = length;
            seq.weight = 1;
            seq.managerRef.index = managerId;
            seq.accumRootName = new NiStringRef(root.name.get());
            seq.textKeyRef.index = Add(NiflyRaw.NewTextKeys([(0f, "start"), (length, "end")]));
            NiflyRaw.Ref(seq.stringPaletteRef).index = NoBlock;
            seqIds.Add(Add(seq));
        }

        // The object palette: the root and the animated meshes by name, for the sequences to find them.
        var named = Looks.Values.SelectMany(l => l.Tracks).Select(t => t.Mesh).Distinct().Select(mi => (Name: ((NiShape)hdr.GetBlockById(meshIds[mi])).name.get(), Id: meshIds[mi])).ToList();
        var palette = NiflyRaw.NewPalette(named.Count + 1);
        var entries = NiflyRaw.PaletteObjects(palette);
        entries[0].name = new NiString(root.name.get()); entries[0].objectRef.index = rootId;
        for (int k = 0; k < named.Count; k++) { entries[k + 1].name = new NiString(named[k].Name); entries[k + 1].objectRef.index = named[k].Id; }
        palette.sceneRef.index = rootId;
        uint paletteId = Add(palette);

        var m = (NiControllerManager)hdr.GetBlockById(managerId);
        foreach (var s in seqIds) m.controllerSequenceRefs.AddBlockRef(s);
        m.objectPaletteRef.index = paletteId;
        root.controllerRef.index = managerId;

        var graph = BlockMeshBuilder.Owned(new BSBehaviorGraphExtraData { controlsBaseSkel = false });
        graph.name = new NiStringRef("BGED");
        graph.behaviorGraphFile = new NiStringRef(LookProject(id));
        nif.AssignExtraData(root, graph);
    }

    /// <summary>The most bones one skin partition holds: the most any of vanilla Skyrim SE's 4,054 skinned actor models
    /// uses (nif-partitions, 2026-11-06: 23 models at 60, none above; the game skins a partition's bones at once).</summary>
    public const int MaxPartitionBones = 60;

    /// <summary>The most bones one shape can be skinned to: its vertices name their bones in one byte each (the shared
    /// vertex data's bone numbers), so bone 256 came out as bone 0 (2026-11-06: 16 Morrowind creatures whose heads morph,
    /// a morph bone for each set of vertices moving alike: the goblin's head on 262 bones).</summary>
    public const int MaxShapeBones = 256;

    /// <summary>A mesh name without what <see cref="SplitWideMeshes"/> and MwCreatureSource's tint copies add to it
    /// (" [part k]", " [tint k]"): the shape it came from.</summary>
    public static string PartOf(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"( \[(tint|part) \d+\])+$", "");

    /// <summary>
    /// Each mesh skinned to more than <see cref="MaxShapeBones"/> bones as parts that each stay within it: triangles in the
    /// order of their bones, each into the part it adds the fewest bones to; a part has the vertices its triangles use
    /// (their positions, normals, UVs, weights, colours as the mesh's) and the mesh's textures and look. The first part
    /// stays where the mesh was (what the particle systems and looks name), the others ("&lt;name&gt; [part k]") are added
    /// after the meshes, and every look animating the mesh animates them too.
    /// </summary>
    void SplitWideMeshes()
    {
        int count = Meshes.Count;
        for (int mi = 0; mi < count; mi++)
        {
            var m = Meshes[mi];
            int meshBones = m.Weights.SelectMany(w => w.Select(p => p.Bone)).Distinct().Count();
            if (meshBones <= MaxShapeBones) continue;
            var triBones = m.Tris.Select(t => t.SelectMany(v => m.Weights[v].Select(p => p.Bone)).Distinct().Order().ToArray()).ToArray();
            static int Compare(int[] a, int[] b)
            {
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return a.Length.CompareTo(b.Length);
            }
            var order = Enumerable.Range(0, m.Tris.Length).ToArray();
            Array.Sort(order, (x, y) => Compare(triBones[x], triBones[y]));
            var partBones = new List<HashSet<int>>();
            var partTris = new List<List<int>>();
            foreach (int t in order)
            {
                int best = -1, bestAdded = int.MaxValue;
                for (int p = 0; p < partBones.Count && bestAdded > 0; p++)
                {
                    int added = triBones[t].Count(b => !partBones[p].Contains(b));
                    if (partBones[p].Count + added <= MaxShapeBones && added < bestAdded) { best = p; bestAdded = added; }
                }
                if (best < 0) { partBones.Add([]); partTris.Add([]); best = partBones.Count - 1; }
                partBones[best].UnionWith(triBones[t]);
                partTris[best].Add(t);
            }
            var tracksOf = Looks.Values.Select(l => (Look: l, Tracks: l.Tracks.Where(tr => tr.Mesh == mi).ToList())).Where(x => x.Tracks.Count > 0).ToList();
            for (int p = 0; p < partTris.Count; p++)
            {
                var tris = partTris[p].Order().ToList();
                var verts = tris.SelectMany(t => m.Tris[t]).Distinct().Order().ToArray();
                var newIndex = new Dictionary<int, int>();
                for (int i = 0; i < verts.Length; i++) newIndex[verts[i]] = i;
                var look = m.Look is { Colors: { } colors } ? m.Look with { Colors = verts.Select(v => colors[v]).ToArray() } : m.Look;
                var part = m with
                {
                    Name = p == 0 ? m.Name : $"{m.Name} [part {p + 1}]",
                    V = verts.Select(v => m.V[v]).ToArray(),
                    N = verts.Select(v => m.N[v]).ToArray(),
                    Uv = verts.Select(v => m.Uv[v]).ToArray(),
                    Weights = verts.Select(v => m.Weights[v]).ToArray(),
                    Tris = tris.Select(t => m.Tris[t].Select(v => newIndex[v]).ToArray()).ToArray(),
                    SkinToBone = m.SkinToBone?.Where(kv => partBones[p].Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                    Look = look,
                };
                if (p == 0) { Meshes[mi] = part; continue; }
                int at = Meshes.Count;
                Meshes.Add(part);
                foreach (var (l, tracks) in tracksOf)
                    foreach (var tr in tracks) l.Tracks.Add(tr with { Mesh = at });
            }
            Console.WriteLine($"  {m.Name}: {meshBones} bones, written as {partTris.Count} shapes of at most {MaxShapeBones} ({string.Join(", ", partBones.Select(b => b.Count))})");
        }
    }

    /// <summary>
    /// The shape's skin partitions built from its weights (NiSkinPartition: each partition's bones, its vertices' weights
    /// and bone indices, its triangles; what the game skins the body with). A body whose triangles reach more bones than
    /// <see cref="MaxPartitionBones"/> is cut into partitions of the body part that each stay within it, the first starting
    /// the body part's set as vanilla's split bodies do: triangles in the order of the bones they use (so a strand of hair,
    /// a finger, keeps together), each put in the partition it adds the fewest bones to, a new one when none can take it
    /// (the lamia's 174-bone body: 351 partitions taken in the file's order). <paramref name="slot"/>: rig bone -> the
    /// shape's bone index.
    /// </summary>
    static void SplitPartitions(NifFile nif, NiShape shape, Mesh m, IReadOnlyDictionary<int, int> slot)
    {
        using var tris = new vectorTriangle();
        shape.GetTriangles(tris);
        var triBones = new int[tris.Count][];
        var allBones = new HashSet<int>();
        for (int t = 0; t < tris.Count; t++)
        {
            var tri = tris[t];
            triBones[t] = new[] { (int)tri.p1, tri.p2, tri.p3 }.SelectMany(v => m.Weights[v].Where(w => w.W > 0).Select(w => slot[w.Bone])).Distinct().Order().ToArray();
            if (triBones[t].Length > MaxPartitionBones)
                throw new InvalidDataException($"{m.Name}: a triangle weighted to {triBones[t].Length} bones, more than a partition holds ({MaxPartitionBones})");
            allBones.UnionWith(triBones[t]);
        }
        var triPart = new int[tris.Count];
        var partBones = new List<HashSet<int>>();
        if (allBones.Count > MaxPartitionBones)
        {
            static int Compare(int[] a, int[] b)
            {
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return a.Length.CompareTo(b.Length);
            }
            var order = Enumerable.Range(0, tris.Count).ToArray();
            Array.Sort(order, (x, y) => Compare(triBones[x], triBones[y]));
            foreach (int t in order)
            {
                int best = -1, bestAdded = int.MaxValue;
                for (int p = 0; p < partBones.Count && bestAdded > 0; p++)
                {
                    int added = triBones[t].Count(b => !partBones[p].Contains(b));
                    if (partBones[p].Count + added <= MaxPartitionBones && added < bestAdded) { best = p; bestAdded = added; }
                }
                if (best < 0) { partBones.Add([]); best = partBones.Count - 1; }
                partBones[best].UnionWith(triBones[t]);
                triPart[t] = best;
            }
        }
        if (partBones.Count > 1)
        {
            using var parts = new NiVectorBSDismemberSkinInstancePartitionInfo();
            using var triParts = new vectorint();
            for (int p = 0; p < partBones.Count; p++)
                parts.push_back(new BSDismemberSkinInstance.PartitionInfo
                {
                    partID = 32,
                    flags = p == 0 ? PartitionFlags.PF_EDITOR_VISIBLE | PartitionFlags.PF_START_NET_BONESET : PartitionFlags.PF_EDITOR_VISIBLE,
                });
            foreach (int p in triPart) triParts.Add(p);
            nif.SetShapePartitions(shape, parts, triParts, true);
        }
        nif.UpdateSkinPartitions(shape);
    }

    /// <summary>The block ID of the shape's NiSkinData (through its skin instance), or null.</summary>
    static uint? SkinData(NifFile nif, NiShape shape)
    {
        var hdr = nif.GetHeader();
        uint skinId = shape.SkinInstanceRef().index;
        if (skinId == uint.MaxValue) return null;
        using var cache = new niflycpp.BlockCache(hdr);
        var inst = cache.EditableBlockById<NiSkinInstance>(skinId);
        uint data = inst?.dataRef.index ?? uint.MaxValue;
        return data == uint.MaxValue ? null : data;
    }

    /// <summary>A name made fit for a file: what a file name can't hold (a Morrowind shape's trailing tab) as "_", trimmed.</summary>
    internal static string FileSafe(string name) => new string(name.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('_', ' ');

    /// <summary>A source picture as a DDS beside the creature's others; null without one.</summary>
    static string? Texture(string? source, string dir, string stem, string suffix, bool alpha)
    {
        if (source is null || !File.Exists(source)) return null;
        if (source.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            // Already a Skyrim texture (Morrowind's through MwTextures): copied beside the creature's others.
            string copy = Path.Combine(dir, stem.ToLowerInvariant() + suffix + ".dds");
            File.Copy(source, copy, overwrite: true);
            return copy;
        }
        var (px, w, h) = Util.Png.Read(source);
        string path = Path.Combine(dir, stem.ToLowerInvariant() + suffix + ".dds");
        Morrowind.MwTextures.WriteBcDds(path, px, w, h, alpha);
        return path;
    }
}
