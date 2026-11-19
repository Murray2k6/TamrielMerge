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
    /// angles (<see cref="Skyrim.SkyrimRotation"/>): the rotation in Skyrim's axes as a row-vector matrix, whose rows are
    /// the images of Skyrim's axes, from which the angles follow.
    /// </summary>
    public static Vector3 Euler(Matrix4x4 rotation)
    {
        Vector3 Row(Vector3 axis) => Dir(Vector3.Transform(FromDir(axis), rotation));
        var r0 = Row(Vector3.UnitX); var r1 = Row(Vector3.UnitY); var r2 = Row(Vector3.UnitZ);
        return Skyrim.SkyrimRotation.Angles(new Matrix4x4(r0.X, r0.Y, r0.Z, 0, r1.X, r1.Y, r1.Z, 0, r2.X, r2.Y, r2.Z, 0, 0, 0, 0, 1));
    }

    /// <summary>Skyrim's rotation for reference angles (row vectors), to check Euler.</summary>
    public static Matrix4x4 SkyrimMatrix(Vector3 angles) => Skyrim.SkyrimRotation.Matrix(angles);
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
    /// <param name="Walk">What its navmesh is made from (2026-11-06: Redguard's dungeons came out in pieces, 204 in the
    /// catacombs and 117 of 162 interior people apart from every load door): its collision's stairs as ramps (as the player
    /// walks them: Redguard's risers of 16-32 units sat on Recast's 32-unit step), and for a prop that moves (a door, a gate,
    /// a lift) only its level faces, so a door shut where the map leaves it doesn't cut the way, and a lift's floor stays.
    /// Null: <paramref name="Solid"/>.</param>
    public sealed record Result(string Path, Vector3 Min, Vector3 Max, List<Vector3[]> Solid, int Faces, List<Vector3[]> Water, List<Vector3[]> Falls,
        List<Vector3[]>? Walk = null);

    /// <summary>
    /// Redguard's water images (bank, image), left out of the models and replaced by Skyrim's water (user, 2026-10-09:
    /// "redguards water needs replaced with skyrims and daggerfalls"): the blue ripples of the pools, wells and fountains
    /// (134/6), the island's water tile (302/0), N'Gasta's lake (267/4), the caverns' green water (165/0), the palace
    /// fountain's (47/0) and the waterfall's white water (3/0, 3/1).
    /// </summary>
    public static readonly HashSet<(int Bank, int Image)> WaterImages = [(134, 6), (302, 0), (267, 4), (165, 0), (47, 0), (3, 0), (3, 1)];

    /// <summary>Models shown with their frames playing over and over (a gull's wings, the parrot on its perch), one
    /// set of shapes per frame shown in its turn (NiVisController, as the menu's flyby): Skyrim's BSTriShape has no vertex
    /// animation. User, 2026-11-10: "the seaguls in redguard act weird" (one rigid frame slid along by its script).</summary>
    public static readonly HashSet<string> Looping = new(StringComparer.OrdinalIgnoreCase) { "SEAGA001", "PRRTA001" };
    /// <summary>Frames a second: Redguard's tick (12.14 a second, the rate its scripts count animation frames in).</summary>
    public const float FrameRate = 12.14f;

    /// <param name="moving">A variant whose collision moves with the object (keyframed), for scripted props.</param>
    /// <param name="itemMass">A loose item's variant (_item): a dynamic box body of this mass instead of fixed collision.</param>
    /// <param name="collide">False: no collision (a rope the player swings on).</param>
    public Result? Convert(RgModel model, string name, string palette, string modOutDir, bool moving = false, float? itemMass = null, bool collide = true)
    {
        var verts = model.Vertices.Select(RgSpace.Dir).ToList();
        var firstVerts = verts;
        // Face normals (Skyrim space) and, per vertex, the faces using it, for smoothing.
        List<Vector3> FaceNormals() => model.Faces.Select(f =>
        {
            var n = Vector3.Zero;
            for (int t = 1; t + 1 < f.Vertices.Length; t++)
                n += Vector3.Cross(verts[f.Vertices[t]] - verts[f.Vertices[0]], verts[f.Vertices[t + 1]] - verts[f.Vertices[0]]);
            return n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }).ToList();
        var faceNormals = FaceNormals();
        var firstNormals = faceNormals;
        int frames = Looping.Contains(name) ? 1 + model.Frames.Count : 1;
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
            float loop = frames / FrameRate;
            for (int frame = 0; frame < frames; frame++)
            {
            if (frame > 0) { verts = model.Frames[frame - 1].Select(RgSpace.Dir).ToList(); faceNormals = FaceNormals(); }
            foreach (var (tex, faces) in groups)
            {
                // Corners stored whole textures away brought back first (RgModel.Unwrap), once a model.
                if (frame == 0) foreach (int f in faces) RgModel.Unwrap(model, model.Faces[f], tex.Width, tex.Height);
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
                    if (!tex.Alpha && frame == 0) solid.Add((face.Vertices.Select(i => verts[i]).ToArray(), faceNormals[f], new BlockMeshBuilder.Tex("", "", false)));
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
                if (frames == 1) continue;
                // Shown for its tick of the loop.
                uint shapeId = hdr.GetBlockID(shape);
                var vis = new NiBoolData();
                vis.data.SetInterpolationType(NiKeyType.CONST_KEY);
                NifKeys.Add(vis.data, 0, frame == 0);
                if (frame > 0) NifKeys.Add(vis.data, frame / FrameRate, true);
                if (frame < frames - 1) NifKeys.Add(vis.data, (frame + 1) / FrameRate, false);
                NifKeys.Add(vis.data, loop, frame == 0);
                uint visData = hdr.AddBlock(BlockMeshBuilder.Owned(vis));
                var visInterp = new NiBoolInterpolator { boolValue = 2 };
                visInterp.dataRef.index = visData;
                uint visInterpId = hdr.AddBlock(BlockMeshBuilder.Owned(visInterp));
                var visCtlr = new NiVisController { flags = 0x48, frequency = 1, phase = 0, startTime = 0, stopTime = loop };
                visCtlr.targetRef.index = shapeId;
                visCtlr.interpolatorRef.index = visInterpId;
                uint visCtlrId = hdr.AddBlock(BlockMeshBuilder.Owned(visCtlr));
                bool hidden = frame != 0;
                BlockMeshBuilder.EditBlock<BSTriShape>(nif, shapeId, t =>
                {
                    t.controllerRef.index = visCtlrId;
                    if (hidden) t.flags |= 1;
                });
            }
            }
            verts = firstVerts; faceNormals = firstNormals;
            if (itemMass is float mass && verts.Count > 0)
                BlockMeshBuilder.AddDynamicCollision(nif, root, verts.Aggregate(Vector3.Min), verts.Aggregate(Vector3.Max), mass, BlockMeshBuilder.WoodMaterial);
            else if (collide && solid.Count > 0) BlockMeshBuilder.AddCollision(nif, root, moving ? solid : BlockMeshBuilder.StairCollision(solid), moving);
            // Animated (bit 0), so the scene runs the frames' controllers.
            BlockMeshBuilder.AddBsxFlags(nif, root, frames > 1 ? 11 : 10);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
        }
        var all = verts.Count > 0 ? verts : [Vector3.Zero];
        // A rope (no collision) and a loose item (it can be pushed and taken) leave the navmesh as it is.
        var walk = !collide || itemMass is not null ? [] : moving
            ? solid.Where(s => MathF.Abs(s.N.Z) > 0.7f).Select(s => s.Pts).ToList()
            : BlockMeshBuilder.StairCollision(solid).Select(s => s.Pts).ToList();
        return new Result(rel, all.Aggregate(new Vector3(float.MaxValue), Vector3.Min), all.Aggregate(new Vector3(float.MinValue), Vector3.Max),
            solid.Select(s => s.Pts).ToList(), model.Faces.Count, water, falls, walk);
    }
}
