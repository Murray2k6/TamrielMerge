using System.Numerics;
using System.Text.Json;
using HKX2;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

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
        Dictionary<int, Matrix4x4>? SkinToBone = null, bool Hidden = false);
    /// <summary>A clip: frames of each bone's local translation and rotation, and its scale per frame and bone when the source
    /// animates scale (null: all 1).</summary>
    public sealed record Clip(float Fps, (Vector3 T, Quaternion Q)[][] Frames, float[][]? Scales = null);

    public List<Bone> Bones { get; } = [];
    public List<Mesh> Meshes { get; } = [];
    public Dictionary<string, Clip> Clips { get; } = [];

    /// <summary>The weapon styles it holds (MwCreatureSource.WeaponStyles keys: "1h", "2c"...), whose clips its graph picks by
    /// the weapon in its hand; none for a creature that fights with its own body.</summary>
    public List<string> WeaponStyles { get; } = [];

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
    static Matrix4x4 QsTransform(Vector3 t, Quaternion q, float s = 1)
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

    public float Duration(string clip) => Math.Max(Clips[clip].Frames.Length - 1, 1) / Clips[clip].Fps;

    /// <summary>
    /// A death for a model that came without one (user, 2026-10-10: "fix the deaths"): from its first idle pose the whole
    /// body topples onto its left side about the left edge of its footprint, falling faster as it goes (0.8 s), then lies
    /// still (0.5 s). The engine plays it on "DeathAnimation" and the creature stays where it fell.
    /// </summary>
    public void EnsureDeath()
    {
        if (Clips.ContainsKey("death")) return;
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

    static void WriteHavok(string path, hkaAnimationContainer container)
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
        nif.CreateNamedBSFadeNode(NiVersion.getSSE(), id);
        var hdr = nif.GetHeader();
        var nodes = AddBones(nif);
        var restWorld = RestWorld();
        string texDir = Path.Combine(dataDir, TextureFolder(id));
        Directory.CreateDirectory(texDir);
        var meshIds = new uint[Meshes.Count];
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
            string shapeName = FileSafe($"{id}_{m.Name}");
            var shape = nif.CreateShapeFromData(shapeName, vv, tt, uu, nn);
            meshIds[mi] = hdr.GetBlockID(shape);
            string? diffuse = Texture(m.Textures.GetValueOrDefault("diffuse"), texDir, FileSafe($"{id}_{m.Name}"), "", alpha: false);
            string? normal = Texture(m.Textures.GetValueOrDefault("normal"), texDir, FileSafe($"{id}_{m.Name}"), "_n", alpha: false);
            if (diffuse is not null) nif.SetTextureSlot(shape, Path.GetRelativePath(dataDir, diffuse), 0);
            nif.SetTextureSlot(shape, normal is not null ? Path.GetRelativePath(dataDir, normal) : DfTextureExport.FlatNormal, 1);
            // Tangents for drawn shapes (a hidden emitter shape has no texture coordinates; nifly crashes on them).
            if (!m.Hidden) nif.CalcTangentsForShape(shape);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                // Matte (the models are never shiny; Morrowind's creatures have no specular): skinned, cast + receive shadows.
                sh.shaderFlags1 = 0x80400302u;
                sh.shaderFlags2 = 0x8001u;       // ZBuffer write, vertex animation off
                sh.glossiness = 1;
                sh.specularStrength = 0;
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
            nif.UpdateSkinPartitions(shape);
            nif.SetDefaultPartition(shape);
            // Not drawn (Morrowind's hidden emitter shapes: a particle array still emits from it); flagged last, once skinned.
            if (m.Hidden) BlockMeshBuilder.EditBlock<BSTriShape>(nif, meshIds[mi], s => s.flags |= 1);
        }
        var boneIds = nodes.Select(n => hdr.GetBlockID(n)).ToArray();
        foreach (var extra in BodyExtras) extra(nif, dataDir, boneIds, meshIds);
        if (BodyExtras.Count > 0) BlockMeshBuilder.AddBsxFlags(nif, nif.GetRootNode(), 1);   // animated: its effects run
        nif.Save(path);
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
    static string FileSafe(string name) => new string(name.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('_', ' ');

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
