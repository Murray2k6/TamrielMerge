using System.Numerics;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's space and Skyrim's. Redguard scenes (and their models) are right-handed with x east, y down and z north,
/// in units about Skyrim's size (Cyrus stands ~128 tall; Skyrim's people ~128); terrain grid row r lies at
/// z = 65536 - 256 r. Skyrim is x east, y north, z up, so a point maps to (x, z, -y), centred on the 65536-unit square.
/// </summary>
public static class RgSpace
{
    public const float Half = 32768;

    /// <summary>A scene position as a Skyrim position.</summary>
    public static Vector3 Point(Vector3 o) => new(o.X - Half, o.Z - Half, -o.Y);
    /// <summary>A model-space vector (vertex, normal) as Skyrim's.</summary>
    public static Vector3 Dir(Vector3 o) => new(o.X, o.Z, -o.Y);
    static Vector3 FromDir(Vector3 s) => new(s.X, -s.Z, s.Y);

    /// <summary>
    /// A scene rotation (Vector3.Transform(v, rotation) turns model space into scene space) as Skyrim's reference
    /// angles: Skyrim turns a model by v * RotX(-x) * RotY(-y) * RotZ(-z) (row vectors), so the matrix's rows are the
    /// images of Skyrim's axes, from which the angles follow.
    /// </summary>
    public static Vector3 Euler(Matrix4x4 rotation)
    {
        Vector3 Row(Vector3 axis) => Dir(Vector3.Transform(FromDir(axis), rotation));
        var r0 = Row(Vector3.UnitX); var r1 = Row(Vector3.UnitY); var r2 = Row(Vector3.UnitZ);
        float b = MathF.Asin(Math.Clamp(r0.Z, -1, 1));
        float a, c;
        if (MathF.Abs(MathF.Cos(b)) > 1e-4f) { a = MathF.Atan2(-r1.Z, r2.Z); c = MathF.Atan2(-r0.Y, r0.X); }
        else { a = 0; c = MathF.Atan2(r1.X, r1.Y); }
        return new Vector3(Wrap(a), Wrap(b), Wrap(c));
    }

    static float Wrap(float r) { r %= MathF.Tau; return r < 0 ? r + MathF.Tau : r; }

    /// <summary>Skyrim's rotation for reference angles (row vectors), to check Euler.</summary>
    public static Matrix4x4 SkyrimMatrix(Vector3 angles) =>
        Matrix4x4.CreateRotationX(-angles.X) * Matrix4x4.CreateRotationY(-angles.Y) * Matrix4x4.CreateRotationZ(-angles.Z);
}

/// <summary>
/// A Redguard model (first frame) as a Skyrim SE static NIF: one BSTriShape per texture (polygons fanned, winding kept —
/// Redguard's faces are counter-clockwise from outside, as Skyrim's), normals smoothed across edges under 45 degrees
/// as Redguard's Gouraud shading did, UVs from the face texel coordinates over the image size, see-through textures
/// alpha-tested. Shapes are two-sided, as the original renderer drew single-sided planks, cloth and leaves from both
/// sides. Collision is a convex slab per opaque polygon (BlockMeshBuilder.AddCollision).
/// </summary>
public sealed class RgMeshConverter(RgArt art)
{
    public const string OutFolder = @"meshes\tamrielmerge\rg";

    /// <param name="Path">The model, or "" when it is all water.</param>
    /// <param name="Water">Flat water faces (Skyrim model space, facing up), for Skyrim water in their place.</param>
    /// <param name="Falls">Steep water faces (falling water), for Skyrim's waterfall sheet in their place.</param>
    public sealed record Result(string Path, Vector3 Min, Vector3 Max, List<Vector3[]> Solid, int Faces, List<Vector3[]> Water, List<Vector3[]> Falls);

    /// <summary>
    /// Redguard's water images (bank, image), left out of the models and replaced by Skyrim's water (user, 2026-10-09:
    /// "redguards water needs replaced with skyrims and daggerfalls"): the blue ripples of the pools, wells and fountains
    /// (134/6), the island's water tile (302/0), N'Gasta's lake (267/4), the caverns' green water (165/0), the palace
    /// fountain's (47/0) and the waterfall's white water (3/0, 3/1).
    /// </summary>
    public static readonly HashSet<(int Bank, int Image)> WaterImages = [(134, 6), (302, 0), (267, 4), (165, 0), (47, 0), (3, 0), (3, 1)];

    /// <param name="moving">A variant whose collision moves with the object (keyframed), for scripted props.</param>
    /// <param name="itemMass">A loose item's variant (_item): a dynamic box body of this mass instead of fixed collision.</param>
    public Result? Convert(RgModel model, string name, string palette, string modOutDir, bool moving = false, float? itemMass = null)
    {
        var verts = model.Vertices.Select(RgSpace.Dir).ToList();
        // Face normals (Skyrim space) and, per vertex, the faces using it, for smoothing.
        var faceNormals = model.Faces.Select(f =>
        {
            var n = Vector3.Zero;
            for (int t = 1; t + 1 < f.Vertices.Length; t++)
                n += Vector3.Cross(verts[f.Vertices[t]] - verts[f.Vertices[0]], verts[f.Vertices[t + 1]] - verts[f.Vertices[0]]);
            return n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }).ToList();
        var facesOf = new Dictionary<int, List<int>>();
        for (int f = 0; f < model.Faces.Count; f++)
            foreach (var v in model.Faces[f].Vertices.Distinct())
                (facesOf.TryGetValue(v, out var l) ? l : facesOf[v] = []).Add(f);
        const float smooth = 0.707f;   // cos 45
        Vector3 NormalAt(int face, int vertex)
        {
            var n = Vector3.Zero;
            foreach (var g in facesOf[vertex])
                if (Vector3.Dot(faceNormals[g], faceNormals[face]) >= smooth) n += faceNormals[g];
            return n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : faceNormals[face];
        }

        var groups = new Dictionary<RgArt.Texture, List<int>>();
        var water = new List<Vector3[]>();
        var falls = new List<Vector3[]>();
        var seenWater = new HashSet<(int, int, int)>();
        for (int f = 0; f < model.Faces.Count; f++)
        {
            var face = model.Faces[f];
            if (face.Vertices.Length < 3) continue;
            if (!face.Solid && WaterImages.Contains((face.TextureBank, face.ImageId)))
            {
                var poly = face.Vertices.Select(i => verts[i]).ToArray();
                bool flat = MathF.Abs(faceNormals[f].Z) >= 0.9f;
                if (flat && faceNormals[f].Z < 0) Array.Reverse(poly);   // the surface, seen from above
                // Two-sided water is often stored twice; keep one.
                var mid = poly.Aggregate(Vector3.Zero, (a, p) => a + p) / poly.Length;
                if (seenWater.Add(((int)MathF.Round(mid.X / 4), (int)MathF.Round(mid.Y / 4), (int)MathF.Round(mid.Z / 4)))) (flat ? water : falls).Add(poly);
                continue;
            }
            var tex = face.Solid ? art.Solid(palette, face.ColorIndex) : art.Ensure(palette, face.TextureBank, face.ImageId) ?? art.Solid(palette, 0);
            (groups.TryGetValue(tex, out var l) ? l : groups[tex] = []).Add(f);
        }
        if (groups.Count == 0)
        {
            if (water.Count == 0 && falls.Count == 0) return null;
            var wv = water.Concat(falls).SelectMany(p => p).ToList();
            return new Result("", wv.Aggregate(Vector3.Min), wv.Aggregate(Vector3.Max), [], model.Faces.Count, water, falls);
        }

        string rel = $@"{OutFolder}\{palette.ToLowerInvariant()}\{name.ToLowerInvariant()}{(itemMass is not null ? "_item" : moving ? "_moving" : "")}.nif";
        string outPath = Path.Combine(modOutDir, rel);
        var solid = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>();
        using (var nif = new NifFile())
        {
            nif.Create(NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var fade = BlockMeshBuilder.Owned(new BSFadeNode());
            fade.name = new NiStringRef(name);
            hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
            var root = nif.GetRootNode();
            int shapeNo = 0;
            foreach (var (tex, faces) in groups)
            {
                using var vv = new vectorVector3();
                using var uv = new vectorVector2();
                using var nn = new vectorVector3();
                using var tris = new vectorTriangle();
                foreach (int f in faces)
                {
                    var face = model.Faces[f];
                    if (vv.Count + face.Vertices.Length > 65000) break;
                    ushort start = (ushort)vv.Count;
                    for (int k = 0; k < face.Vertices.Length; k++)
                    {
                        var p = verts[face.Vertices[k]];
                        var n = NormalAt(f, face.Vertices[k]);
                        vv.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                        nn.Add(new nifly.Vector3(n.X, n.Y, n.Z));
                        var t = face.Solid ? Vector2.Zero : new Vector2(face.Uv[k].X / tex.Width, face.Uv[k].Y / tex.Height);
                        uv.Add(new nifly.Vector2(t.X, t.Y));
                    }
                    for (int k = 1; k + 1 < face.Vertices.Length; k++)
                        tris.Add(new Triangle(start, (ushort)(start + k), (ushort)(start + k + 1)));
                    if (!tex.Alpha) solid.Add((face.Vertices.Select(i => verts[i]).ToArray(), faceNormals[f], new BlockMeshBuilder.Tex("", "", false)));
                }
                if (tris.Count == 0) continue;
                var shape = nif.CreateShapeFromData($"{name}_{shapeNo++}", vv, tris, uv, nn);
                nif.SetTextureSlot(shape, tex.Diffuse, 0);
                nif.SetTextureSlot(shape, tex.Normal, 1);
                nif.CalcTangentsForShape(shape);
                if (tex.Alpha)
                    nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x12EC, threshold = 128 }));
                BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), s =>
                {
                    s.shaderFlags1 = 0x82400300u;
                    s.shaderFlags2 = 0x1u | 0x10u | 0x8000u;   // ZBuffer write, two-sided, ...
                    s.glossiness = 10;
                    s.specularStrength = 0.3f;
                    s.textureClampMode = TexClampMode.WRAP_S_WRAP_T;
                });
            }
            if (itemMass is float mass && verts.Count > 0)
                BlockMeshBuilder.AddDynamicCollision(nif, root, verts.Aggregate(Vector3.Min), verts.Aggregate(Vector3.Max), mass, BlockMeshBuilder.WoodMaterial);
            else if (solid.Count > 0) BlockMeshBuilder.AddCollision(nif, root, moving ? solid : BlockMeshBuilder.StairCollision(solid), moving);
            BlockMeshBuilder.AddBsxFlags(nif, root);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
        }
        var all = verts.Count > 0 ? verts : [Vector3.Zero];
        return new Result(rel, all.Aggregate(new Vector3(float.MaxValue), Vector3.Min), all.Aggregate(new Vector3(float.MinValue), Vector3.Max),
            solid.Select(s => s.Pts).ToList(), model.Faces.Count, water, falls);
    }
}
