using System.Numerics;
using System.Text.Json;
using nifly;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Realistic trees and plants in place of the models made from Daggerfall's tree and plant sprites (user, 2026-11-04:
/// "the foliage is ugly ... they need realism and redone in the same way they look"; "find free models online and use
/// skyrims if cant find any"; "these free models need to be part of the tool"). The models are free ones from Sketchfab
/// (CC-BY 4.0, one CC0; tools/foliage/CREDITS.md), split into single plants by tools/foliage/export.py into
/// tools/foliage/models/&lt;uid&gt;/&lt;n&gt;/ (plant.json, a part per material: OBJ and PNG textures).
/// tools/foliage/mapping.json names the plant for each sprite (by eye, from their pictures). A sprite without one keeps
/// the model made from it (sprite2mesh), else its vanilla stand-in.
/// </summary>
public static class FoliageModels
{
    /// <summary>A sprite's plant: its folder (models/&lt;uid&gt;/&lt;n&gt;), or null.</summary>
    public static string? PlantFor(string spriteName)
    {
        var map = Mapping();
        return map.TryGetValue(spriteName, out var rel) && Directory.Exists(Path.Combine(Root, "models", rel)) ? Path.Combine(Root, "models", rel) : null;
    }

    static string Root => Path.Combine(Setup.Dependencies.Root, "foliage");
    static Dictionary<string, string>? mapping;
    static readonly object Gate = new();

    static Dictionary<string, string> Mapping()
    {
        lock (Gate)
        {
            if (mapping is not null) return mapping;
            string file = Path.Combine(Root, "mapping.json");
            mapping = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];
            return mapping;
        }
    }

    sealed record Part(string Obj, string? Diffuse, string? Normal, bool Cutout);
    sealed record Plant(float Width, float Height, int Triangles, List<Part> Parts);

    /// <summary>The largest texture side kept (the downloads come at 2048-4096).</summary>
    const int MaxTexture = 1024;

    /// <summary>Textures written by this conversion (shared by the sprites with the same plant).</summary>
    static readonly HashSet<string> written = [];

    /// <summary>
    /// The plant's NIF for a sprite: <paramref name="height"/> tall (the sprite's world height), no wider than
    /// <paramref name="maxWidth"/> (shrunk whole, not narrowed, so it keeps its shape), standing on z = 0; its parts a
    /// shape each, leaves alpha-tested and two-sided as Skyrim's own trees; trees (and plants a person's height or more)
    /// on a trunk column, smaller plants walked through. Returns its bounds.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) Write(string plantDir, string modOutDir, string meshPath, string textureStem, float height, float maxWidth, bool tree, object niflyLock)
    {
        var plant = JsonSerializer.Deserialize<Plant>(File.ReadAllText(Path.Combine(plantDir, "plant.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var parts = plant.Parts.Select(p => (Part: p, Mesh: SpriteModelImporter.ReadObj(Path.Combine(plantDir, p.Obj)))).ToList();
        var all = parts.SelectMany(p => p.Mesh.V).ToList();
        var lo = all.Aggregate(Vector3.Min); var hi = all.Aggregate(Vector3.Max);
        float s = height / Math.Max(hi.Z - lo.Z, 1e-4f);
        float spread = Math.Max(hi.X - lo.X, hi.Y - lo.Y) * s;
        if (maxWidth > 1 && spread > maxWidth) s *= maxWidth / spread;
        var centre = new Vector3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, lo.Z);
        Vector3 Place(Vector3 v) => (v - centre) * s;
        var min = Place(lo); var max = Place(hi);

        // Textures: each part's colour (its alpha kept for cut-out leaves) and normal map, at most MaxTexture across.
        string texDir = Path.Combine(modOutDir, "textures", "tamrielmerge", "df", "foliage");
        Directory.CreateDirectory(texDir);
        (string? D, string? N) Textures(Part p, int k)
        {
            string? d = null, n = null;
            if (p.Diffuse is { } dif)
            {
                d = $@"textures\tamrielmerge\df\foliage\{textureStem}_{k}.dds";
                Once(d, () =>
                {
                    var (px, w, h) = Fit(Util.Png.Read(Path.Combine(plantDir, dif)));
                    if (!p.Cutout) for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u;
                    Morrowind.MwTextures.WriteBcDds(Path.Combine(modOutDir, d), px, w, h, p.Cutout);
                });
            }
            if (p.Normal is { } nor)
            {
                n = $@"textures\tamrielmerge\df\foliage\{textureStem}_{k}_n.dds";
                Once(n, () =>
                {
                    var (px, w, h) = Fit(Util.Png.Read(Path.Combine(plantDir, nor)));
                    // glTF's normal maps are OpenGL's (green up), Skyrim's DirectX's: green turned over. Alpha (Skyrim's
                    // shine mask) none: matte, as plants are.
                    for (int i = 0; i < px.Length; i++) { uint c = px[i]; uint g = 255 - ((c >> 8) & 0xFF); px[i] = (c & 0x00FF00FFu) | (g << 8); }
                    Morrowind.MwTextures.WriteBcDds(Path.Combine(modOutDir, n), px, w, h, false);
                });
            }
            return (d, n);
        }

        lock (niflyLock)
        {
            using var nif = new NifFile();
            nif.Create(NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var fade = BlockMeshBuilder.Owned(new BSFadeNode());
            fade.name = new NiStringRef(Path.GetFileNameWithoutExtension(meshPath));
            hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
            var root = nif.GetRootNode();
            for (int k = 0; k < parts.Count; k++)
            {
                var (part, (v, uv, nrm, tris)) = parts[k];
                if (tris.Count == 0 || v.Count > 65535) continue;
                using var nv = new vectorVector3(); using var nu = new vectorVector2(); using var nn = new vectorVector3(); using var nt = new vectorTriangle();
                for (int i = 0; i < v.Count; i++)
                {
                    var w = Place(v[i]);
                    nv.Add(new nifly.Vector3(w.X, w.Y, w.Z));
                    var u = i < uv.Count ? uv[i] : Vector2.Zero;
                    nu.Add(new nifly.Vector2(u.X, u.Y));   // ReadObj has turned OBJ's v (up) into the NIF's (down)
                    var n = i < nrm.Count ? nrm[i] : Vector3.UnitZ;
                    nn.Add(new nifly.Vector3(n.X, n.Y, n.Z));
                }
                foreach (var (a, b, c) in tris) nt.Add(new Triangle((ushort)a, (ushort)b, (ushort)c));
                var shape = nif.CreateShapeFromData($"{Path.GetFileNameWithoutExtension(meshPath)}_{k}", nv, nt, nu, nn);
                var (d, nm) = Textures(part, k);
                if (d is not null) nif.SetTextureSlot(shape, d, 0);
                nif.SetTextureSlot(shape, nm ?? DfTextureExport.FlatNormal, 1);
                nif.CalcTangentsForShape(shape);
                if (part.Cutout) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x12EC, threshold = 128 }));
                BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
                {
                    sh.shaderFlags1 = 0x82400300u;                                   // z-test, remappable, cast + receive shadows
                    sh.shaderFlags2 = 0x1u | 0x8000u | (part.Cutout ? 0x10u : 0u);   // z-write, ...; leaves two-sided
                    sh.glossiness = 10;
                    sh.specularStrength = 0;
                });
            }
            // A trunk column for trees and tall plants, so crowns can be walked under; smaller plants none.
            if (tree || max.Z >= 150)
            {
                float r = Math.Max(8, Math.Min(max.X - min.X, max.Y - min.Y) / 10);
                var none = new BlockMeshBuilder.Tex("", "", false);
                var box = Box(new Vector3(-r, -r, 0), new Vector3(r, r, max.Z * 0.6f), none);
                BlockMeshBuilder.AddCollision(nif, root, box, moving: false);
            }
            string full = Path.Combine(modOutDir, "Meshes", meshPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (nif.Save(full) != 0) throw new IOException($"nifly failed to save {full}");
        }
        return (min, max);
    }

    static void Once(string rel, Action make)
    {
        lock (written) { if (!written.Add(rel)) return; }
        make();
    }

    static (uint[] Px, int W, int H) Fit((uint[] Px, int W, int H) img)
    {
        while (img.W > MaxTexture || img.H > MaxTexture) img = Morrowind.MwTextures.Half(img);
        return img;
    }

    static List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> Box(Vector3 lo, Vector3 hi, BlockMeshBuilder.Tex tex)
    {
        Vector3 P(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
        return
        [
            ([P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1)], -Vector3.UnitY, tex),
            ([P(1, 1, 0), P(0, 1, 0), P(0, 1, 1), P(1, 1, 1)], Vector3.UnitY, tex),
            ([P(0, 1, 0), P(0, 0, 0), P(0, 0, 1), P(0, 1, 1)], -Vector3.UnitX, tex),
            ([P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1)], Vector3.UnitX, tex),
            ([P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1)], Vector3.UnitZ, tex),
            ([P(0, 1, 0), P(1, 1, 0), P(1, 0, 0), P(0, 0, 0)], -Vector3.UnitZ, tex),
        ];
    }
}
