using System.Numerics;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's body parts (BODY records: the models under meshes\a\ and meshes\c\ that armour and clothing put on)
/// as shapes on Morrowind's own Biped skeleton, ready for <see cref="WornMesh.Retarget"/>. Morrowind puts a part on
/// in one of two ways (as OpenMW does): a skinned model brings its own copy of the Bip01 skeleton in its bind pose and
/// only its shapes under the node named for the part ("Chest", "Left Hand" ...) are used; any other model is rigid and
/// rides the part's attachment node in the character skeleton (base_anim.nif, base_anim_female.nif: "Right Foot" under
/// "Bip01 R Foot", "Left Clavicle" ... ; the left nodes mirror the right-hand models).
/// </summary>
public sealed class MwWornSource(Tes3Data data, MwTextures textures)
{
    /// <summary>The attachment node of each body part index (BYDT / ARMO INDX), as Morrowind names them.</summary>
    public static readonly string[] PartNodes =
    [
        "Head", "Head", "Neck", "Chest", "Groin", "Groin", "Right Hand", "Left Hand", "Right Wrist", "Left Wrist",
        "Shield Bone", "Right Forearm", "Left Forearm", "Right Upper Arm", "Left Upper Arm", "Right Foot", "Left Foot",
        "Right Ankle", "Left Ankle", "Right Knee", "Left Knee", "Right Upper Leg", "Left Upper Leg", "Right Clavicle",
        "Left Clavicle", "Weapon Bone", "Tail",
    ];

    sealed record Skeleton(BindSkeleton Bind, Dictionary<string, NifTransform> Nodes, Dictionary<string, string> BoneOf);
    readonly Dictionary<bool, Skeleton> bases = [];

    /// <summary>A NIF transform as a row-vector matrix (v * M).</summary>
    static Matrix4x4 Row(NifTransform t)
    {
        var r = t.Rotation; float s = t.Scale;
        return new Matrix4x4(r.M11 * s, r.M21 * s, r.M31 * s, 0, r.M12 * s, r.M22 * s, r.M32 * s, 0, r.M13 * s, r.M23 * s, r.M33 * s, 0, t.Translation.X, t.Translation.Y, t.Translation.Z, 1);
    }

    /// <summary>A model's node tree: every node's world transform by name (first of a name wins, as Morrowind binds),
    /// its Bip01 bones as a bind skeleton, and for each node the Bip01 bone it hangs from.</summary>
    static Skeleton Walk(Nif4 nif)
    {
        var bind = new BindSkeleton();
        var nodes = new Dictionary<string, NifTransform>(StringComparer.OrdinalIgnoreCase);
        var boneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Visit(int index, NifTransform parent, string? bone, string? parentBone)
        {
            if (nif.Get<NiNode>(index) is not { } node || node is NiTriShape) return;
            var world = node.Transform.Then(parent);
            string name = node.Name;
            bool isBone = name.StartsWith("Bip01", StringComparison.OrdinalIgnoreCase);
            if (name.Length > 0 && !nodes.ContainsKey(name))
            {
                nodes[name] = world;
                if (isBone)
                {
                    bind.World[name] = WornMesh.Orthonormal(Row(world));
                    if (parentBone is not null) bind.Parent[name] = parentBone;
                }
                if ((isBone ? name : bone) is { } b) boneOf[name] = b;
            }
            foreach (var c in node.Children) Visit(c, world, isBone ? name : bone, isBone ? name : parentBone);
        }
        foreach (var r in nif.Roots.Count > 0 ? nif.Roots : [0]) Visit(r, NifTransform.Identity, null, null);
        return new Skeleton(bind, nodes, boneOf);
    }

    Skeleton Base(bool female)
    {
        if (bases.TryGetValue(female, out var hit)) return hit;
        string path = female ? @"meshes\base_anim_female.nif" : @"meshes\base_anim.nif";
        var bytes = data.Read(path) ?? data.Read(@"meshes\base_anim.nif") ?? throw new FileNotFoundException(path);
        return bases[female] = Walk(Nif4.Read(bytes, path));
    }

    /// <summary>The character skeleton (base_anim) as a bind skeleton, for the bones rigid parts ride.</summary>
    public BindSkeleton CharacterSkeleton(bool female) => Base(female).Bind;

    /// <summary>
    /// The shapes of one body part (its model and part index), in the bind space of the skeleton returned with them:
    /// the model's own Bip01 copy for skinned parts, the character skeleton for rigid ones. Weights are by Bip01 bone.
    /// </summary>
    public (List<WornShape> Shapes, BindSkeleton Skeleton) Part(string model, int partIndex, bool female, List<string> problems)
    {
        string source = model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var nif = Nif4.Read(data.Read(source) ?? throw new FileNotFoundException(source), source);
        string attach = partIndex >= 0 && partIndex < PartNodes.Length ? PartNodes[partIndex] : "Chest";
        var own = Walk(nif);
        var character = Base(female);
        bool skinned = nif.Blocks.OfType<NiTriShape>().Any(s => s is not NiParticles && s.Skin >= 0);
        var result = new List<WornShape>();
        bool wantAll = false;

        // Shapes with the node chain above them (names, for the part filter) and their world transform in the file.
        void Visit(int index, NifTransform parent, List<string> chain, List<NiFlagProperty> props)
        {
            if (nif.Get<NiNode>(index) is not { } node || node.Hidden) return;
            var world = node.Transform.Then(parent);
            var here = props.Concat(node.Properties.Select(nif.Get<NiFlagProperty>).OfType<NiFlagProperty>()).ToList();
            if (node is NiTriShape shape)
            {
                if (node is NiParticles || IsMarker(shape.Name) || nif.Get<NiTriShapeData>(shape.Data) is not { Vertices.Length: > 0 } d || d.Triangles.Length < 3) return;
                bool underPart = chain.Any(n => n.Equals(attach, StringComparison.OrdinalIgnoreCase));
                if (skinned && !underPart && !wantAll) return;
                if (Shape(nif, shape, d, world, here, own, character, attach) is { } ws) result.Add(ws);
                return;
            }
            if (node.Type == "RootCollisionNode") return;
            var next = new List<string>(chain) { node.Name };
            foreach (var c in node.Children) Visit(c, world, next, here);
        }
        foreach (var r in nif.Roots.Count > 0 ? nif.Roots : [0]) Visit(r, NifTransform.Identity, [], []);
        if (skinned && result.Count == 0)
        {
            // A skinned model without a node for this part: all of it, as Morrowind draws it.
            wantAll = true;
            foreach (var r in nif.Roots.Count > 0 ? nif.Roots : [0]) Visit(r, NifTransform.Identity, [], []);
        }
        if (result.Count == 0) problems.Add($"{model}: no visible shapes for part {attach}");
        // Skinned shapes are bound in the model's own skeleton; rigid ones were placed on the character skeleton.
        // Both are made one bind space by keeping skinned shapes on the model's bones (the character skeleton fills
        // in any bone the model lacks).
        var bind = new BindSkeleton();
        foreach (var (k, v) in character.Bind.World) bind.World[k] = v;
        foreach (var (k, v) in character.Bind.Parent) bind.Parent[k] = v;
        if (own.Bind.World.Count > 0)
        {
            foreach (var (k, v) in own.Bind.World) bind.World[k] = v;
            foreach (var (k, v) in own.Bind.Parent) bind.Parent[k] = v;
            // Rigid shapes in a skinned model sit on the model's bones too (Shape placed them in the model's space).
        }
        return (result, bind);
    }

    static bool IsMarker(string name) =>
        name.StartsWith("Tri EditorMarker", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Tri Shadow", StringComparison.OrdinalIgnoreCase);

    /// <summary>One NiTriShape as a worn shape: skinned through its skin data and the model's bones, or rigid on the
    /// Bip01 bone above it in the model, else on the part's attachment node in the character skeleton.</summary>
    WornShape? Shape(Nif4 nif, NiTriShape shape, NiTriShapeData d, NifTransform world, List<NiFlagProperty> props, Skeleton own, Skeleton character, string attach)
    {
        int n = d.Vertices.Length;
        var v = new Vector3[n];
        var nrm = new Vector3[n];
        var weights = new (string, float)[n][];
        bool hasNormals = d.Normals.Length == n;
        bool mirrored;
        if (nif.Get<NiSkinInstance>(shape.Skin) is { } skin && nif.Get<NiSkinData>(skin.Data) is { } sd)
        {
            var acc = new Dictionary<string, float>[n];
            for (int i = 0; i < n; i++) acc[i] = new(StringComparer.OrdinalIgnoreCase);
            var sum = new Vector3[n]; var nsum = new Vector3[n]; var wsum = new float[n];
            for (int bi = 0; bi < skin.Bones.Length && bi < sd.Bones.Count; bi++)
            {
                if (nif.Get<NiNode>(skin.Bones[bi]) is not { } boneNode || !own.Nodes.TryGetValue(boneNode.Name, out var boneWorld)) continue;
                var toWorld = sd.Bones[bi].Transform.Then(boneWorld);
                string bone = own.BoneOf.GetValueOrDefault(boneNode.Name, boneNode.Name);
                foreach (var (vi, w) in sd.Bones[bi].Weights)
                {
                    if (vi >= n || w <= 0) continue;
                    sum[vi] += toWorld.Apply(d.Vertices[vi]) * w;
                    if (hasNormals) nsum[vi] += toWorld.Rotate(d.Normals[vi]) * w;
                    wsum[vi] += w;
                    acc[vi][bone] = acc[vi].GetValueOrDefault(bone) + w;
                }
            }
            for (int i = 0; i < n; i++)
            {
                v[i] = wsum[i] > 0 ? sum[i] / wsum[i] : world.Apply(d.Vertices[i]);
                nrm[i] = nsum[i].LengthSquared() > 0 ? Vector3.Normalize(nsum[i]) : Vector3.UnitZ;
                var top = acc[i].OrderByDescending(kv => kv.Value).Take(4).ToList();
                float total = top.Sum(kv => kv.Value);
                weights[i] = top.Count == 0 ? [("Bip01 Pelvis", 1f)] : top.Select(kv => (kv.Key, kv.Value / total)).ToArray();
            }
            mirrored = false;
        }
        else
        {
            // Rigid: on the Bip01 bone above it in this model, else riding the part's node in the character skeleton.
            NifTransform place; string bone;
            string? holder = own.BoneOf.Keys.Count > 0 ? HolderBone(nif, shape, own) : null;
            if (holder is not null) { place = world; bone = holder; }
            else
            {
                if (!character.Nodes.TryGetValue(attach, out var at)) return null;
                // Morrowind draws the right-hand model on the left side mirrored across the part's own x (OpenMW
                // attach.cpp: a scale of (-1, 1, 1) under every "Left ..." attachment node).
                if (attach.StartsWith("Left", StringComparison.OrdinalIgnoreCase))
                    world = world.Then(new NifTransform(new Matrix4x4(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), Vector3.Zero, 1));
                place = world.Then(at);
                bone = character.BoneOf.GetValueOrDefault(attach, "Bip01 Pelvis");
            }
            for (int i = 0; i < n; i++)
            {
                v[i] = place.Apply(d.Vertices[i]);
                nrm[i] = hasNormals && place.Rotate(d.Normals[i]) is { } r && r.LengthSquared() > 0 ? Vector3.Normalize(r) : Vector3.UnitZ;
                weights[i] = [(bone, 1f)];
            }
            mirrored = place.Rotation.GetDeterminant() * place.Scale < 0;
        }
        var tris = new int[d.Triangles.Length / 3 * 3];
        for (int t = 0; t + 2 < d.Triangles.Length; t += 3)
        {
            tris[t] = d.Triangles[t];
            tris[t + 1] = mirrored ? d.Triangles[t + 2] : d.Triangles[t + 1];
            tris[t + 2] = mirrored ? d.Triangles[t + 1] : d.Triangles[t + 2];
        }
        T? Last<T>() where T : NiFlagProperty => props.OfType<T>().LastOrDefault();
        var texturing = Last<NiTexturingProperty>();
        int uvSet = (int)(texturing?.Base?.UVSet ?? 0);
        var uvs = d.UVSets.Length > uvSet ? d.UVSets[uvSet] : d.UVSets.FirstOrDefault() ?? new Vector2[n];
        var material = Last<NiMaterialProperty>();
        var alpha = Last<NiAlphaProperty>();
        string? file = texturing?.Base is { } b ? nif.Get<NiSourceTexture>(b.Source)?.FileName : null;
        string diffuse = (file is not null ? textures.Ensure(file).Path : null) ?? textures.Solid(material?.Diffuse ?? Vector3.One);
        var emissive = material?.Emissive ?? Vector3.Zero;
        bool glows = emissive.X + emissive.Y + emissive.Z > 0.05f;
        var ws = new WornShape
        {
            Name = string.IsNullOrEmpty(shape.Name) ? $"Shape{shape.Index}" : shape.Name,
            V = v, N = hasNormals ? nrm : [], Uv = uvs, Tris = tris, Weights = weights,
            Textures = glows ? [diffuse, diffuse.Contains(@"\solid\") ? MwTextures.FlatNormal : textures.NormalFor(diffuse), diffuse]
                             : [diffuse, diffuse.Contains(@"\solid\") ? MwTextures.FlatNormal : textures.NormalFor(diffuse)],
            // Matte, as Morrowind's (its materials have no shine): Skyrim's armour flags without specular.
            Flags1 = 0x82400302,
            Flags2 = (alpha is { } al && (al.Flags & 1) != 0 && (al.Flags & 0x200) == 0 ? 0u : 0x1u) | (glows ? 0x40u : 0) | 0x8000u,
            ShaderType = glows ? 2u : 0u,
            Glossiness = Math.Max(1, material?.Glossiness ?? 10),
            SpecularStrength = 0,
            Alpha = material?.Alpha ?? 1,
            Emissive = glows ? emissive : Vector3.Zero,
            EmissiveMultiple = glows ? 1 : 0,
            Clamp = texturing?.Base?.Clamp ?? 3,
            AlphaFlags = alpha?.Flags,
            AlphaThreshold = alpha?.Threshold ?? 0,
        };
        return ws;
    }

    /// <summary>The Bip01 bone a rigid shape hangs under in its own model (null when it hangs from no bone).</summary>
    static string? HolderBone(Nif4 nif, NiTriShape shape, Skeleton own)
    {
        foreach (var node in nif.Blocks.OfType<NiNode>())
            if (node.Children.Contains(shape.Index))
                return node.Name.Length > 0 && own.BoneOf.TryGetValue(node.Name, out var b) ? b : null;
        return null;
    }
}
