using System.Globalization;
using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using Noggog;
using TamrielMerge.Daggerfall;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Turns the models made from Daggerfall sprites (tools/model3d/sprite2mesh.py: TripoSR mesh.obj + baked texture.png,
/// see <see cref="SpriteModels"/>) into Skyrim NIFs, DDS textures and statics. TripoSR's frame is z up, y across the
/// picture, x towards the viewer; each model is turned into Skyrim's frame, stood on z = 0, centred, and scaled to
/// its sprite's height in the world. Collision follows each model's shape (its simplified collision mesh, one convex
/// slab per triangle); trees stand on a trunk column so their crowns can be walked under.
/// </summary>
public sealed class SpriteModelImporter(SkyrimMod mod, string modOutDir, string arena2)
{
    public sealed record Made(FormKey Static, Vector3 Min, Vector3 Max, SpriteModels.Kind Kind);

    /// <summary>Cores the models' files are made on: all but two.</summary>
    public static int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>
    /// Imports every finished model in <paramref name="workDir"/>/models; returns them by sprite. The models' files are
    /// made on all but two of the processor's cores (user, 2026-10-13: "use the cores"; every town batch imports all
    /// 604, one at a time that was most of a batch's first minutes), their records then added one by one in the
    /// models' order, so the plugin comes out the same as one made in turn.
    /// </summary>
    public Dictionary<(int Archive, int Record), Made> ImportAll(string workDir, List<string> problems)
    {
        var made = new Dictionary<(int, int), Made>();
        var targets = new List<(SpriteModels.Target T, string Dir)>();
        foreach (var t in SpriteModels.Targets)
        {
            string dir = Path.Combine(workDir, "models", t.Name);
            if (!File.Exists(Path.Combine(dir, "mesh.obj")) || !File.Exists(Path.Combine(dir, "texture.png"))) continue;
            if (Fidelity(dir) is { } score && score < MinFidelity) continue;   // TripoSR couldn't make it: the stand-in stays
            if (Rejected.Contains(t.Name)) continue;
            targets.Add((t, dir));
        }
        var files = new (string MeshRel, Vector3 Min, Vector3 Max)?[targets.Count];
        var errors = new string?[targets.Count];
        Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = Threads }, i =>
        {
            try { files[i] = MakeFiles(targets[i].T, targets[i].Dir); }
            catch (Exception e) { errors[i] = $"{targets[i].T.Name}: {e.Message}"; }
        });
        for (int i = 0; i < targets.Count; i++)
        {
            if (errors[i] is { } error) { problems.Add(error); continue; }
            var t = targets[i].T;
            var (meshRel, min, max) = files[i]!.Value;
            made[(t.Archive, t.Record)] = AddStatic(t, meshRel, min, max);
        }
        return made;
    }

    /// <summary>A model whose outline matches its sprite's less than this (sprite2mesh.py's score.txt, intersection over
    /// union through the sprite's view) is not used (2026-10-12: a spider statue's legs, a cage's bars, 2-3 px in the
    /// sprite, are thinner than TripoSR's grid; it made a mangled body in floating pieces).</summary>
    public const double MinFidelity = 0.65;

    /// <summary>Models looked over and left out though their outline scores pass (review of all 604, 2026-10-12): a black
    /// swamp tree in floating pieces, the bloody stake (three copies) made a flat board, a ghost statue speckled black
    /// where its sprite has holes. Their stand-ins stay.</summary>
    static readonly HashSet<string> Rejected = ["Scenery502_30", "Scenery100_13", "Scenery206_27", "Scenery211_29", "Scenery98_1"];

    static double? Fidelity(string dir) =>
        File.Exists(Path.Combine(dir, "score.txt")) && double.TryParse(File.ReadAllText(Path.Combine(dir, "score.txt")).Trim(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;

    /// <summary>A model's mesh and textures written (safe to run for several models at once): its mesh path and bounds.</summary>
    (string MeshRel, Vector3 Min, Vector3 Max) MakeFiles(SpriteModels.Target t, string dir)
    {
        var (verts, uvs, normals, tris) = ReadObj(Path.Combine(dir, "mesh.obj"));
        if (verts.Count > 65535) throw new InvalidDataException($"{verts.Count} vertices (a NIF shape holds 65535)");
        // TripoSR (x towards the viewer, y across, z up) -> Skyrim (x across, y away, z up): a turn about z.
        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i]; verts[i] = new Vector3(v.Y, -v.X, v.Z);
            var n = normals[i]; normals[i] = new Vector3(n.Y, -n.X, n.Z);
        }
        // Standing on z = 0, centred, as tall as the sprite stands in Daggerfall (its world height x the build scale).
        var min = verts.Aggregate(Vector3.Min);
        var max = verts.Aggregate(Vector3.Max);
        var image = DfTexture.Load(arena2, t.Archive)!.Image(t.Record);
        float height = image.WorldSize.Height * BlockMeshBuilder.Scale;
        float s = height / Math.Max(max.Z - min.Z, 1e-4f);
        var shift = new Vector3(-(min.X + max.X) / 2, -(min.Y + max.Y) / 2, t.Kind == SpriteModels.Kind.Hanging ? -max.Z : -min.Z);
        for (int i = 0; i < verts.Count; i++) verts[i] = (verts[i] + shift) * s;
        min = verts.Aggregate(Vector3.Min);
        max = verts.Aggregate(Vector3.Max);

        // Collision follows the model's own shape: its simplified collision mesh (collision.obj), placed exactly as the
        // visible mesh, one convex slab per triangle. Trees stand on a trunk column instead, so their crowns can be
        // walked under; without a collision mesh, a box.
        List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> solid;
        string collisionObj = Path.Combine(dir, "collision.obj");
        if (t.Kind == SpriteModels.Kind.Tree)
            solid = BoxPolygons(new Vector3(-Trunk(min, max), -Trunk(min, max), 0), new Vector3(Trunk(min, max), Trunk(min, max), max.Z * 0.6f));
        else if (File.Exists(collisionObj))
        {
            var (cv, _, _, ct) = ReadObj(collisionObj);
            for (int i = 0; i < cv.Count; i++) cv[i] = (new Vector3(cv[i].Y, -cv[i].X, cv[i].Z) + shift) * s;
            var none = new BlockMeshBuilder.Tex("", "", false);
            solid = [];
            foreach (var (a, b, c) in ct)
            {
                var n = Vector3.Cross(cv[b] - cv[a], cv[c] - cv[a]);
                if (n.LengthSquared() < 1e-6f) continue;
                solid.Add(([cv[a], cv[b], cv[c]], Vector3.Normalize(n), none));
            }
        }
        else solid = BoxPolygons(min, max);

        // Texture: the painted atlas as a DDS with mips. Plants and trees keep its alpha (their cut-out gaps); the rest
        // are solid. A normal map made from the texture's own detail, with no specular (matte).
        bool cutout = t.Kind is SpriteModels.Kind.Plant or SpriteModels.Kind.Tree;
        string texRel = $@"textures\tamrielmerge\df\models\{t.Name.ToLowerInvariant()}.dds";
        string normalRel = $@"textures\tamrielmerge\df\models\{t.Name.ToLowerInvariant()}_n.dds";
        var (argb, w, h) = Util.Png.Read(Path.Combine(dir, "texture.png"));
        MatchSpriteColour(argb, Path.Combine(dir, "input.png"));
        if (!cutout) for (int i = 0; i < argb.Length; i++) argb[i] |= 0xFF000000u;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(modOutDir, texRel))!);
        DfTextureExport.WriteDds(Path.Combine(modOutDir, texRel), argb, w, h);
        DfTextureExport.WriteDds(Path.Combine(modOutDir, normalRel), NormalMap(argb, w, h), w, h);

        string meshRel = $@"TamrielMerge\DF\Models\{t.Name}.nif";
        WriteNif(Path.Combine(modOutDir, "Meshes", meshRel), t, verts, uvs, normals, tris, texRel, normalRel, cutout, solid);
        return (meshRel, min, max);
    }

    /// <summary>A model's static record (one at a time, in the models' order).</summary>
    Made AddStatic(SpriteModels.Target t, string meshRel, Vector3 min, Vector3 max)
    {
        var stat = new Static(mod)
        {
            EditorID = "DFSpriteModel" + t.Name,
            Model = new Model { File = meshRel },
            ObjectBounds = new ObjectBounds
            {
                First = new P3Int16((short)MathF.Floor(min.X), (short)MathF.Floor(min.Y), (short)MathF.Floor(min.Z)),
                Second = new P3Int16((short)MathF.Ceiling(max.X), (short)MathF.Ceiling(max.Y), (short)MathF.Ceiling(max.Z)),
            },
        };
        mod.Statics.Add(stat);
        return new Made(stat.FormKey, min, max, t.Kind);
    }

    internal static (List<Vector3> V, List<Vector2> Uv, List<Vector3> N, List<(int, int, int)> T) ReadObj(string path)
    {
        var v = new List<Vector3>(); var vt = new List<Vector2>(); var vn = new List<Vector3>();
        var faces = new List<(int V, int T, int N)[]>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
            switch (p[0])
            {
                case "v": v.Add(new Vector3(F(1), F(2), F(3))); break;
                case "vt": vt.Add(new Vector2(F(1), F(2))); break;
                case "vn": vn.Add(new Vector3(F(1), F(2), F(3))); break;
                case "f":
                    faces.Add(p.Skip(1).Select(c =>
                    {
                        var k = c.Split('/');
                        int Idx(int j) => k.Length > j && k[j].Length > 0 ? int.Parse(k[j]) - 1 : -1;
                        return (Idx(0), Idx(1), Idx(2));
                    }).ToArray());
                    break;
            }
        }
        // One output vertex per distinct (position, uv, normal) corner.
        var outV = new List<Vector3>(); var outUv = new List<Vector2>(); var outN = new List<Vector3>();
        var index = new Dictionary<(int, int, int), int>();
        var tris = new List<(int, int, int)>();
        int Corner((int V, int T, int N) c)
        {
            if (index.TryGetValue(c, out int i)) return i;
            outV.Add(v[c.V]);
            // OBJ texture v runs up; NIF UVs run down.
            outUv.Add(c.T >= 0 ? new Vector2(vt[c.T].X, 1 - vt[c.T].Y) : Vector2.Zero);
            outN.Add(c.N >= 0 ? Vector3.Normalize(vn[c.N]) : Vector3.UnitZ);
            return index[c] = outV.Count - 1;
        }
        foreach (var f in faces)
            for (int i = 1; i + 1 < f.Length; i++)
                tris.Add((Corner(f[0]), Corner(f[i]), Corner(f[i + 1])));
        return (outV, outUv, outN, tris);
    }

    static readonly object NiflyLock = new();

    void WriteNif(string path, SpriteModels.Target t, List<Vector3> verts, List<Vector2> uvs, List<Vector3> normals, List<(int A, int B, int C)> tris, string texture,
        string normalMap, bool cutout, List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> solid)
    {
        lock (NiflyLock)
        {
            using var nif = new NifFile();
            nif.Create(NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var fade = BlockMeshBuilder.Owned(new BSFadeNode());
            fade.name = new NiStringRef(t.Name);
            hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
            var root = nif.GetRootNode();
            NiShape AddShape(string name, List<int> keep)
            {
                // The triangles kept, with their own vertex list.
                var map = new Dictionary<int, int>();
                using var nv = new vectorVector3(); using var nu = new vectorVector2(); using var nn = new vectorVector3(); using var nt = new vectorTriangle();
                int Vertex(int i)
                {
                    if (map.TryGetValue(i, out int k)) return k;
                    nv.Add(new nifly.Vector3(verts[i].X, verts[i].Y, verts[i].Z));
                    nu.Add(new nifly.Vector2(uvs[i].X, uvs[i].Y));
                    nn.Add(new nifly.Vector3(normals[i].X, normals[i].Y, normals[i].Z));
                    return map[i] = map.Count;
                }
                foreach (int f in keep) { var (a, b, c) = tris[f]; nt.Add(new Triangle((ushort)Vertex(a), (ushort)Vertex(b), (ushort)Vertex(c))); }
                var shape = nif.CreateShapeFromData(name, nv, nt, nu, nn);
                nif.SetTextureSlot(shape, texture, 0);
                nif.SetTextureSlot(shape, normalMap, 1);
                nif.CalcTangentsForShape(shape);
                // Cut-out foliage: alpha-tested (no blending, so no sorting trouble), as Skyrim's own trees.
                if (cutout) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new nifly.NiAlphaProperty { flags = 0x12EC, threshold = 128 }));
                BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), s =>
                {
                    s.shaderFlags1 = 0x82400300u;               // ZBuffer test, remappable, cast + receive shadows
                    s.shaderFlags2 = 0x1u | 0x10u | 0x8000u;    // ZBuffer write, double-sided (thin leaves and fronds), ...
                    s.glossiness = 10;
                    s.specularStrength = 0;
                });
                return shape;
            }
            var all = Enumerable.Range(0, tris.Count).ToList();
            bool animated = false;
            if (t.Motion == SpriteModels.Motion.Perched && Perch(verts) is { } split)
            {
                // The creature (every triangle above the perch's top) on a node of its own, pivoting on the perch.
                var above = all.Where(f => (verts[tris[f].A].Z + verts[tris[f].B].Z + verts[tris[f].C].Z) / 3 > split).ToList();
                var below = all.Except(above).ToList();
                var low = below.SelectMany(f => new[] { tris[f].A, tris[f].B, tris[f].C }).Select(i => verts[i]).ToList();
                var pivot = new Vector3(low.Average(v => v.X), low.Average(v => v.Y), split);
                if (below.Count > 0) AddShape(t.Name + "Perch", below);
                for (int i = 0; i < verts.Count; i++) verts[i] -= pivot;   // the creature's vertices about its pivot
                var xf = new MatTransform { translation = new nifly.Vector3(pivot.X, pivot.Y, pivot.Z) };
                var node = nif.AddNode(t.Name + "Idle", xf, root);
                nif.SetParentNode(AddShape(t.Name, above), node);
                for (int i = 0; i < verts.Count; i++) verts[i] += pivot;
                PerchedIdle(nif, hdr.GetBlockID(node), pivot, Math.Max(1, split * 0.012f));
                animated = true;
            }
            else AddShape(t.Name, all);
            if (solid.Count > 0) BlockMeshBuilder.AddCollision(nif, root, solid, moving: false);
            // 0x1 Animated: the idle's controller runs; 0x2 Havok + 0x8 Complex as for every collision here.
            BlockMeshBuilder.AddBsxFlags(nif, root, animated ? 11 : 10);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (nif.Save(path) != 0) throw new IOException($"nifly failed to save {path}");
        }
    }

    /// <summary>
    /// Where a perch ends and its creature begins: going up, the first height (past a third of the way) where the
    /// shape's spread jumps to well over the perch's own (a bird's body over its post: its belly, so the legs stay
    /// planted); null when there is no such step.
    /// </summary>
    static float? Perch(List<Vector3> verts)
    {
        float lo = verts.Min(v => v.Z), hi = verts.Max(v => v.Z), step = (hi - lo) / 25;
        float Spread(float z0) => verts.Where(v => v.Z >= z0 && v.Z < z0 + step) is var slice && slice.Any()
            ? Math.Max(slice.Max(v => v.X) - slice.Min(v => v.X), slice.Max(v => v.Y) - slice.Min(v => v.Y)) : 0;
        var spreads = Enumerable.Range(0, 25).Select(i => Spread(lo + i * step)).ToArray();
        float post = spreads.Take(10).Where(s => s > 0).DefaultIfEmpty(0).OrderBy(s => s).ElementAt(spreads.Take(10).Count(s => s > 0) / 2);
        for (int i = 8; i < 25; i++)
            if (post > 0 && spreads[i] > post * 1.6f) return lo + (i + 1) * step;   // one slice up: above the feet
        return null;
    }

    /// <summary>
    /// A creature's idle on its perch, looping every 12 s: breathing (a small bob of <paramref name="bob"/> units), a
    /// look to one side then the other, and a dip of the head; keyed on its node (NiTransformController, loop).
    /// </summary>
    static void PerchedIdle(NifFile nif, uint nodeId, Vector3 pivot, float bob)
    {
        var hdr = nif.GetHeader();
        var data = new NiTransformData();
        data.translations.SetInterpolationType(NiKeyType.LINEAR_KEY);
        for (float time = 0; time <= 12.001f; time += 0.75f)
            NifKeys.Add(data.translations, time, pivot + new Vector3(0, 0, (int)Math.Round(time / 0.75f) % 2 == 1 ? bob : 0));
        data.rotationType = NiKeyType.XYZ_ROTATION_KEY;
        foreach (var g in new[] { data.xRotations, data.yRotations, data.zRotations }) g.SetInterpolationType(NiKeyType.LINEAR_KEY);
        // Looking about: still, a turn one way, held, the other way, held, back.
        foreach (var (time, turn) in new[] { (0f, 0f), (2.5f, 0f), (3.0f, 0.4f), (5.5f, 0.4f), (6.2f, -0.35f), (8.5f, -0.35f), (9.0f, 0f), (12f, 0f) })
            NifKeys.Add(data.zRotations, time, turn);
        // A dip of the head (pecking at its feathers) near the end.
        foreach (var (time, dip) in new[] { (0f, 0f), (10.0f, 0f), (10.3f, 0.18f), (10.7f, 0f), (12f, 0f) })
            NifKeys.Add(data.yRotations, time, dip);
        foreach (var time in new[] { 0f, 12f }) NifKeys.Add(data.xRotations, time, 0);
        uint dataId = hdr.AddBlock(BlockMeshBuilder.Owned(data));
        var interp = new NiTransformInterpolator { scale = float.MinValue };
        interp.translation = new nifly.Vector3(float.MinValue, float.MinValue, float.MinValue);
        interp.dataRef.index = dataId;
        uint interpId = hdr.AddBlock(BlockMeshBuilder.Owned(interp));
        // Active, looping (0x48 = active + Skyrim's scaled time).
        var ctlr = new NiTransformController { flags = 0x48, frequency = 1, phase = 0, startTime = 0, stopTime = 12 };
        ctlr.targetRef.index = nodeId;
        ctlr.interpolatorRef.index = interpId;
        uint ctlrId = hdr.AddBlock(BlockMeshBuilder.Owned(ctlr));
        BlockMeshBuilder.EditBlock<nifly.NiNode>(nif, nodeId, n => n.controllerRef.index = ctlrId);
    }

    /// <summary>
    /// The baked texture comes out darker than the sprite (TripoSR shades the sides it imagines): each channel is
    /// brightened (1x to 3x) so the painted texels' mean matches the sprite's foreground mean on TripoSR's input.
    /// </summary>
    static void MatchSpriteColour(uint[] argb, string inputPng)
    {
        if (!File.Exists(inputPng)) return;
        var (input, _, _) = Util.Png.Read(inputPng);
        double[] Mean(IEnumerable<uint> px)
        {
            double r = 0, g = 0, b = 0; long n = 0;
            foreach (var c in px) { r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; n++; }
            return n == 0 ? [0, 0, 0] : [r / n, g / n, b / n];
        }
        // The sprite: what differs from TripoSR's grey background; the texture: what the bake painted (alpha > 0).
        static bool Grey(uint c) => Math.Abs((int)((c >> 16) & 0xFF) - 127) + Math.Abs((int)((c >> 8) & 0xFF) - 127) + Math.Abs((int)(c & 0xFF) - 127) <= 12;
        var want = Mean(input.Where(c => !Grey(c)));
        var have = Mean(argb.Where(c => c >> 24 != 0));
        var gain = Enumerable.Range(0, 3).Select(k => have[k] < 1 ? 1 : Math.Clamp(want[k] / have[k], 1, 3)).ToArray();
        for (int i = 0; i < argb.Length; i++)
        {
            uint c = argb[i];
            uint r = (uint)Math.Min(255, ((c >> 16) & 0xFF) * gain[0]), g = (uint)Math.Min(255, ((c >> 8) & 0xFF) * gain[1]), b = (uint)Math.Min(255, (c & 0xFF) * gain[2]);
            argb[i] = (c & 0xFF000000u) | r << 16 | g << 8 | b;
        }
    }

    /// <summary>
    /// A tangent-space normal map from the texture's own detail: its brightness as height (softened a little), slopes
    /// to normals. Alpha (Skyrim's specular mask) is 0, so the surface stays matte. Unpainted texels are flat.
    /// </summary>
    static uint[] NormalMap(uint[] argb, int w, int h, float strength = 2.5f)
    {
        var height = new float[w * h];
        for (int i = 0; i < argb.Length; i++)
        {
            uint c = argb[i];
            height[i] = (0.299f * ((c >> 16) & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * (c & 0xFF)) / 255f;
        }
        // Soften (3x3 box) so single pixels don't become spikes.
        var soft = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                        sum += height[yy * w + xx]; n++;
                    }
                soft[y * w + x] = sum / n;
            }
        var normal = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (argb[i] >> 24 == 0 && (argb[i] & 0xFFFFFF) == 0) { normal[i] = 0x008080FFu; continue; }
                float sx = soft[y * w + Math.Min(x + 1, w - 1)] - soft[y * w + Math.Max(x - 1, 0)];
                float sy = soft[Math.Min(y + 1, h - 1) * w + x] - soft[Math.Max(y - 1, 0) * w + x];
                var n = Vector3.Normalize(new Vector3(-sx * strength, sy * strength, 1));
                uint r = (uint)Math.Clamp((n.X * 0.5f + 0.5f) * 255, 0, 255), g = (uint)Math.Clamp((n.Y * 0.5f + 0.5f) * 255, 0, 255), b = (uint)Math.Clamp((n.Z * 0.5f + 0.5f) * 255, 0, 255);
                normal[i] = r << 16 | g << 8 | b;   // alpha 0: no specular
            }
        return normal;
    }

    /// <summary>A tree's trunk: a fifth of its spread, at least 8 units.</summary>
    static float Trunk(Vector3 min, Vector3 max) => Math.Max(8, Math.Min(max.X - min.X, max.Y - min.Y) / 10);

    static List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> BoxPolygons(Vector3 lo, Vector3 hi)
    {
        var none = new BlockMeshBuilder.Tex("", "", false);
        Vector3 P(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
        return
        [
            ([P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1)], Vector3.UnitZ, none),
            ([P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0)], -Vector3.UnitZ, none),
            ([P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1)], -Vector3.UnitY, none),
            ([P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0)], Vector3.UnitY, none),
            ([P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0)], -Vector3.UnitX, none),
            ([P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1)], Vector3.UnitX, none),
        ];
    }
}
