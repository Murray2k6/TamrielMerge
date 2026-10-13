using System.Numerics;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Converts a Morrowind model (NIF 4.0.0.2) to a Skyrim SE static model, keeping Morrowind's own geometry and
/// textures. The node tree is flattened (transforms baked into the vertices); each visible NiTriShape becomes a
/// BSTriShape with a lighting shader (diffuse + flat normal), its alpha property, vertex colours and material
/// alpha/emissive. Collision follows Morrowind's rules: the RootCollisionNode's geometry when the model has one,
/// otherwise all of its geometry, and none when the model is tagged NCO. Units are the same in both games.
/// </summary>
public sealed class MwMeshConverter(Tes3Data data, MwTextures textures)
{
    /// <summary>Static collision with stairs as Skyrim's (the stair material on steps, a ramp over each flight); moving
    /// objects keep theirs as is.</summary>
    static List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> Stairs(
        List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> solid, bool moving) =>
        moving ? solid : Skyrim.Meshes.BlockMeshBuilder.StairCollision(solid);

    public MwTextures Textures => textures;
    public const string OutFolder = @"meshes\tamrielmerge\mw";

    /// <summary>A converted model: its path, counts, bounds and its collision triangles in model space.</summary>
    public sealed record Result(string Path, int Shapes, int CollisionPolys, Vector3 Min, Vector3 Max, bool Animated, bool Skinned, List<Vector3[]> Solid, List<Vector3[]> Visible)
    {
        /// <summary>The model's particle emitters (flames, smoke, mist), which Skyrim gets as vanilla effects placed there.</summary>
        public List<Skyrim.EffectStandIns.Emitter> Emitters { get; init; } = [];
    }

    /// <summary>The Skyrim data-relative path a Morrowind model converts to; <paramref name="moving"/> variants (keyframed
    /// collision, for objects scripts move such as swinging doors) get a "_moving" suffix.</summary>
    public static string OutputPath(string model, bool moving = false, float? itemMass = null)
    {
        string rel = $@"{OutFolder}\{Tes3Bsa.Normalize(model).Replace(@"meshes\", "")}";
        return itemMass is not null ? Path.ChangeExtension(rel, null) + "_item.nif" : moving ? Path.ChangeExtension(rel, null) + "_moving.nif" : rel;
    }

    sealed class Piece
    {
        public required string Name;
        public required List<Vector3> Verts, Normals;
        public required List<Vector2> Uvs;
        public required List<Vector4> Colors;
        public required List<(ushort, ushort, ushort)> Tris;
        public NiTexturingProperty? Texturing;
        public string? TextureFile;
        public NiMaterialProperty? Material;
        public NiAlphaProperty? Alpha;
        public NiVertexColorProperty? VertexColor;
    }

    /// <param name="moving">Keyframed collision, for objects scripts move (swinging doors).</param>
    /// <param name="itemMass">A loose item: dynamic physics (a convex box around the model with this mass) instead
    /// of fixed collision, so it can be picked up, dropped and knocked over.</param>
    public Result Convert(string model, string modOutDir, bool moving = false, float? itemMass = null)
    {
        string source = model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var nif = Nif4.Read(data.Read(source) ?? throw new FileNotFoundException(source), source);
        var pieces = new List<Piece>();
        var collision = new List<(Vector3[] Pts, Vector3 N)>();
        var allGeometry = new List<(Vector3[] Pts, Vector3 N)>();
        bool hasCollisionNode = nif.Blocks.Any(b => b.Type == "RootCollisionNode");
        bool noCollision = ExtraStrings(nif, nif.Root).Any(s => s is "NCO" or "NCC");
        bool animated = nif.Blocks.Any(b => b is NiController c && b.Type is "NiKeyframeController" or "NiGeomMorpherController" or "NiVisController" or "NiUVController" or "NiAlphaController");
        bool skinned = nif.Blocks.Any(b => b is NiSkinInstance);

        void Visit(int index, NifTransform parent, List<NiFlagProperty> inherited, bool inCollision)
        {
            if (nif.Get<NiNode>(index) is not { } node) return;
            var world = node.Transform.Then(parent);
            var props = inherited.Concat(node.Properties.Select(nif.Get<NiFlagProperty>).OfType<NiFlagProperty>()).ToList();
            bool collisionHere = inCollision || node.Type == "RootCollisionNode";
            if (node is NiTriShape shape and not NiParticles)
            {
                if (nif.Get<NiTriShapeData>(shape.Data) is not { } d || d.Vertices.Length == 0) return;
                var tris = new List<(Vector3[] Pts, Vector3 N)>();
                for (int i = 0; i + 2 < d.Triangles.Length; i += 3)
                {
                    var p = new[] { world.Apply(d.Vertices[d.Triangles[i]]), world.Apply(d.Vertices[d.Triangles[i + 1]]), world.Apply(d.Vertices[d.Triangles[i + 2]]) };
                    var c = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
                    if (c.LengthSquared() > 1e-6f) tris.Add((p, Vector3.Normalize(c)));
                }
                if (collisionHere) { collision.AddRange(tris); return; }
                if (IsMarker(shape.Name)) return;
                allGeometry.AddRange(tris);
                if (node.Hidden || d.Triangles.Length == 0) return;
                pieces.Add(MakePiece(nif, shape, d, world, props));
                return;
            }
            if (node.Hidden && !collisionHere) { HiddenSubtree(node, world); return; }
            foreach (var child in node.Children) Visit(child, world, props, collisionHere);
        }

        // Hidden subtrees aren't drawn but still collide when the model has no RootCollisionNode.
        void HiddenSubtree(NiNode node, NifTransform world)
        {
            foreach (var child in node.Children)
                if (nif.Get<NiNode>(child) is { } n)
                {
                    var w = n.Transform.Then(world);
                    if (n is NiTriShape s && nif.Get<NiTriShapeData>(s.Data) is { } d && !IsMarker(s.Name))
                        for (int i = 0; i + 2 < d.Triangles.Length; i += 3)
                        {
                            var p = new[] { w.Apply(d.Vertices[d.Triangles[i]]), w.Apply(d.Vertices[d.Triangles[i + 1]]), w.Apply(d.Vertices[d.Triangles[i + 2]]) };
                            var c = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
                            if (c.LengthSquared() > 1e-6f) allGeometry.Add((p, Vector3.Normalize(c)));
                        }
                    else HiddenSubtree(n, w);
                }
        }

        Visit(nif.Roots.Count > 0 ? nif.Roots[0] : 0, NifTransform.Identity, [], false);
        var solid = noCollision ? [] : hasCollisionNode ? collision : allGeometry;

        string rel = OutputPath(model, moving, itemMass);
        string outPath = Path.Combine(modOutDir, rel);
        Write(pieces, solid, outPath, moving, itemMass);
        var pts = pieces.SelectMany(p => p.Verts).Concat(solid.SelectMany(s => s.Pts)).ToList();
        return new Result(rel, pieces.Count, solid.Count,
            pts.Count > 0 ? pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min) : Vector3.Zero,
            pts.Count > 0 ? pts.Aggregate(new Vector3(float.MinValue), Vector3.Max) : Vector3.Zero, animated, skinned, solid.Select(t => t.Pts).ToList(), allGeometry.Select(t => t.Pts).ToList())
        { Emitters = Emitters(nif) };
    }

    /// <summary>
    /// Particle emitters: each particle system controller's emitter node, placed by the node transforms down from the
    /// root, with the controller's particle size and rise; the texture is the one on the particle geometry it drives.
    /// </summary>
    static List<Skyrim.EffectStandIns.Emitter> Emitters(Nif4 nif)
    {
        var controllers = nif.Blocks.OfType<NiParticleSystemController>().ToList();
        if (controllers.Count == 0) return [];
        var world = new Dictionary<int, NifTransform>();
        void Walk(int index, NifTransform parent)
        {
            if (nif.Get<NiNode>(index) is not { } node || world.ContainsKey(index)) return;
            var w = node.Transform.Then(parent);
            world[index] = w;
            foreach (var child in node.Children) Walk(child, w);
        }
        foreach (var root in nif.Roots.Count > 0 ? nif.Roots : [0]) Walk(root, NifTransform.Identity);
        var result = new List<Skyrim.EffectStandIns.Emitter>();
        foreach (var c in controllers)
        {
            if (!world.TryGetValue(c.Emitter, out var at)) continue;
            // The particle geometry this controller drives (its target), for the texture.
            string texture = "";
            if (nif.Get<NiNode>(c.Target) is { } g)
                foreach (var prop in g.Properties.Select(nif.Get<NifBlock>))
                    if (prop is NiTexturingProperty { Base: { } b } && nif.Get<NiSourceTexture>(b.Source) is { } src) texture = Path.GetFileName(src.FileName ?? "");
            result.Add(new Skyrim.EffectStandIns.Emitter(at.Apply(Vector3.Zero), c.InitialSize * at.Scale, c.Speed * c.Lifetime * at.Scale, texture));
        }
        return result;
    }


    /// <summary>Editor-only geometry: Morrowind's editor markers and shadow placeholders.</summary>
    static bool IsMarker(string name) =>
        name.StartsWith("Tri EditorMarker", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Tri Shadow", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> ExtraStrings(Nif4 nif, NifBlock? block)
    {
        int next = (block as NiObjectNET)?.ExtraData ?? -1;
        for (int guard = 0; next >= 0 && guard < 64; guard++)
        {
            var extra = nif.Get<NiExtraData>(next);
            if (extra is NiStringExtraData s) yield return s.Value;
            next = extra?.Next ?? -1;
        }
    }

    static Piece MakePiece(Nif4 nif, NiTriShape shape, NiTriShapeData d, NifTransform world, List<NiFlagProperty> props)
    {
        T? Last<T>() where T : NiFlagProperty => props.OfType<T>().LastOrDefault();
        var texturing = Last<NiTexturingProperty>();
        int uvSet = (int)(texturing?.Base?.UVSet ?? 0);
        var uvs = d.UVSets.Length > uvSet ? d.UVSets[uvSet] : d.UVSets.FirstOrDefault();
        var piece = new Piece
        {
            Name = string.IsNullOrEmpty(shape.Name) ? $"Shape{shape.Index}" : shape.Name,
            Verts = d.Vertices.Select(world.Apply).ToList(),
            Normals = d.Normals.Length == d.Vertices.Length
                ? d.Normals.Select(n => { var r = world.Rotate(n); return r.LengthSquared() > 0 ? Vector3.Normalize(r) : Vector3.UnitZ; }).ToList()
                : [],
            Uvs = uvs?.ToList() ?? Enumerable.Repeat(Vector2.Zero, d.Vertices.Length).ToList(),
            Colors = d.Colors.ToList(),
            Tris = [],
            Texturing = texturing,
            TextureFile = texturing?.Base is { } b ? nif.Get<NiSourceTexture>(b.Source)?.FileName : null,
            Material = Last<NiMaterialProperty>(),
            Alpha = Last<NiAlphaProperty>(),
            VertexColor = Last<NiVertexColorProperty>(),
        };
        // A mirrored transform (negative determinant) reverses the winding.
        bool mirrored = world.Rotation.GetDeterminant() * world.Scale < 0;
        for (int i = 0; i + 2 < d.Triangles.Length; i += 3)
            piece.Tris.Add(mirrored ? (d.Triangles[i], d.Triangles[i + 2], d.Triangles[i + 1]) : (d.Triangles[i], d.Triangles[i + 1], d.Triangles[i + 2]));
        return piece;
    }

    void Write(List<Piece> pieces, List<(Vector3[] Pts, Vector3 N)> solid, string outPath, bool moving, float? itemMass = null)
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
            var (diffuse, texAlpha) = BaseTexture(p);
            // Untextured shapes draw in their material colour in Morrowind (white when vertex colours carry it).
            diffuse ??= textures.Solid(p.Colors.Count > 0 ? Vector3.One : p.Material?.Diffuse ?? Vector3.One);
            nif.SetTextureSlot(shape, diffuse, 0);
            nif.SetTextureSlot(shape, diffuse.Contains(@"\solid\") ? MwTextures.FlatNormal : textures.NormalFor(diffuse), 1);
            // Morrowind multiplies a material's emissive colour into the texture (self-lit lanterns, flames, lava);
            // Skyrim's own-emit adds it as a flat tint, so those surfaces use the glow shader with their own texture
            // as the glow map instead. Other surfaces emit nothing.
            var emissive = p.Material?.Emissive ?? Vector3.Zero;
            bool glows = emissive.X + emissive.Y + emissive.Z > 0.05f;
            if (glows) nif.SetTextureSlot(shape, diffuse, 2);
            if (p.Normals.Count > 0) nif.CalcTangentsForShape(shape);

            bool colors = p.Colors.Count == p.Verts.Count && p.Colors.Any(c => c != Vector4.One);
            if (colors)
            {
                using var cols = new vectorColor4();
                foreach (var c in p.Colors) cols.Add(new Color4(c.X, c.Y, c.Z, c.W));
                nif.SetColorsForShape(shape, cols);
            }
            if (p.Alpha is { } a)
            {
                var alpha = BlockMeshBuilder.Owned(new nifly.NiAlphaProperty { flags = a.Flags, threshold = a.Threshold });
                nif.AssignAlphaProperty(shape, alpha);
            }
            var m = p.Material;
            bool vertexAlpha = colors && p.Alpha is not null && p.Colors.Any(c => c.W < 1);
            uint clamp = p.Texturing?.Base?.Clamp ?? 3;
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), s =>
            {
                // ZBuffer test, remappable textures, own emit, cast + receive shadows; vertex alpha when used.
                s.shaderFlags1 = 0x82400300u | (vertexAlpha ? 0x8u : 0);
                // ZBuffer write (off for blended surfaces, as Morrowind), vertex colours when present.
                bool blended = p.Alpha is { } al && (al.Flags & 1) != 0 && (al.Flags & 0x200) == 0;
                s.shaderFlags2 = (blended ? 0u : 0x1u) | (colors ? 0x20u : 0) | (glows ? 0x40u : 0) | 0x8000u;
                if (glows) s.SetShaderType(2u);   // Glow shader: emit = glow map x emissive colour
                s.glossiness = Math.Max(1, m?.Glossiness ?? 10);
                s.specularStrength = 0;
                s.alpha = m?.Alpha ?? 1;
                s.emissiveColor = glows ? new nifly.Vector3(emissive.X, emissive.Y, emissive.Z) : new nifly.Vector3(0, 0, 0);
                s.emissiveMultiple = glows ? 1 : 0;
                s.textureClampMode = (TexClampMode)clamp;
            });
        }
        if (itemMass is { } mass)
        {
            var pts = pieces.SelectMany(p => p.Verts).ToList();
            if (pts.Count > 0)
            {
                BlockMeshBuilder.AddDynamicCollision(nif, root, pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min), pts.Aggregate(new Vector3(float.MinValue), Vector3.Max), mass, BlockMeshBuilder.WoodMaterial);
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

    (string? Path, bool Alpha) BaseTexture(Piece p) => p.TextureFile is { } file ? textures.Ensure(file) : (null, false);
}
