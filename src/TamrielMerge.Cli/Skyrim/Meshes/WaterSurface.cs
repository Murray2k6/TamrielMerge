using System.Numerics;
using Mutagen.Bethesda.Plugins;
using nifly;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Skyrim water in any outline (user, 2026-10-09: "redguards water needs replaced with skyrims and daggerfalls"): a copy
/// of vanilla's placed-water mesh (meshes\water\water2048.nif, the Water2048DefaultIntWaistStill activator's) with its
/// water-shaded surface remade as the given polygons (flat, at the model's z = 0) and its water volume (a phantom box,
/// what tells the game you are in water) and culling bound fitted round them. Placed as an activator with a water type,
/// as vanilla places water in caves and ponds, so it renders, ripples and swims as Skyrim's own.
/// </summary>
public static class WaterSurface
{
    /// <summary>Vanilla's still interior water type (DefaultIntWaterWaistHeightStill), as the 2048 water planes use.</summary>
    public static readonly FormKey InteriorWaterType = new(ModKey.FromFileName("Skyrim.esm"), 0x0CC7B4);
    const string Template = @"meshes\water\water2048.nif";
    const float HavokScale = 69.99125f;
    // Block indices in the template (checked by Describe): box shape, its transform, the surface, the culling box.
    const uint Box = 2, BoxTransform = 3, Surface = 7, CullBox = 10;
    static byte[]? template;

    static string TemplateFile(string skyrimData)
    {
        if (template is null)
        {
            foreach (var bsa in Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa"))
                if (Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, bsa).Files
                        .FirstOrDefault(f => f.Path.Equals(Template, StringComparison.OrdinalIgnoreCase)) is { } file)
                { template = file.GetBytes(); break; }
            if (template is null) throw new FileNotFoundException($"{Template} isn't in Skyrim's mesh archives");
        }
        string path = Path.Combine(Path.GetTempPath(), "tamrielmerge_water2048.nif");
        if (!File.Exists(path) || new FileInfo(path).Length != template.Length) File.WriteAllBytes(path, template);
        return path;
    }

    /// <summary>Dev aid: the template's blocks and the values Write changes.</summary>
    public static void Describe(string skyrimData)
    {
        using var nif = new NifFile();
        nif.Load(TemplateFile(skyrimData));
        var hdr = nif.GetHeader();
        for (uint i = 0; i < hdr.GetNumBlocks(); i++) Console.WriteLine($"[{i}] {hdr.GetBlockTypeStringById(i)}");
        using var cache = new niflycpp.BlockCache(hdr);
        var box = cache.EditableBlockById<bhkBoxShape>(Box);
        Console.WriteLine($"box half size {box.dimensions.x * HavokScale}, {box.dimensions.y * HavokScale}, {box.dimensions.z * HavokScale} (radius {box.radius2})");
        var xf = cache.EditableBlockById<bhkTransformShape>(BoxTransform).xform;
        for (int r = 0; r < 4; r++) { var v = new nifly.Vector3(); xf.GetRow(r, v); Console.WriteLine($"box transform row {r}: {v.x}, {v.y}, {v.z}"); }
        var obb = cache.EditableBlockById<BSMultiBoundOBB>(CullBox);
        Console.WriteLine($"cull box centre {obb.center.x}, {obb.center.y}, {obb.center.z}; size {obb.size.x}, {obb.size.y}, {obb.size.z}");
        var shape = cache.EditableBlockById<BSTriShape>(Surface);
        var verts = shape.UpdateRawVertices();
        Console.WriteLine($"surface {verts.Count} vertices, x {verts.Min(v => v.x)}..{verts.Max(v => v.x)}, y {verts.Min(v => v.y)}..{verts.Max(v => v.y)}, z {verts.Min(v => v.z)}..{verts.Max(v => v.z)}; shader {shape.ShaderPropertyRef().index}; uvs {shape.HasUVs()} normals {shape.HasNormals()} colours {shape.HasVertexColors()}");
        var uvs = shape.UpdateRawUvs();
        Console.WriteLine($"uv u {uvs.Min(v => v.u)}..{uvs.Max(v => v.u)}, v {uvs.Min(v => v.v)}..{uvs.Max(v => v.v)}");
    }

    /// <summary>
    /// Writes a water mesh whose surface is <paramref name="polygons"/> (convex, counter-clockwise from above, in the
    /// model's x/y at z = 0) and whose water reaches <paramref name="depth"/> below it.
    /// </summary>
    public static void Write(string skyrimData, string outPath, IReadOnlyList<Vector2[]> polygons, float depth)
    {
        using var nif = new NifFile();
        nif.Load(TemplateFile(skyrimData));
        var hdr = nif.GetHeader();
        using var vv = new vectorVector3();
        using var uv = new vectorVector2();
        using var nn = new vectorVector3();
        using var tris = new vectorTriangle();
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var poly in polygons)
        {
            if (poly.Length < 3 || vv.Count + poly.Length > 65000) continue;
            ushort start = (ushort)vv.Count;
            foreach (var p in poly)
            {
                vv.Add(new nifly.Vector3(p.X, p.Y, 0));
                nn.Add(new nifly.Vector3(0, 0, 1));
                uv.Add(new nifly.Vector2(p.X / 2048f, p.Y / 2048f));   // the template's scale: one repeat per 2048 units
                lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p);
            }
            for (int k = 1; k + 1 < poly.Length; k++) tris.Add(new Triangle(start, (ushort)(start + k), (ushort)(start + k + 1)));
        }
        if (tris.Count == 0) throw new ArgumentException("no water polygons");
        Meshes.BlockMeshBuilder.EditBlock<BSTriShape>(nif, Surface, s =>
        {
            uint shader = s.ShaderPropertyRef().index;
            s.Create(NiVersion.getSSE(), vv, tris, uv, nn);
            s.SetShaderPropertyRef(shader);
            s.UpdateBounds();
        });
        var centre = (lo + hi) / 2;
        var half = (hi - lo) / 2;
        Meshes.BlockMeshBuilder.EditBlock<bhkBoxShape>(nif, Box, b => b.dimensions = new nifly.Vector3(half.X / HavokScale, half.Y / HavokScale, depth / 2 / HavokScale));
        Meshes.BlockMeshBuilder.EditBlock<bhkTransformShape>(nif, BoxTransform, t =>
        {
            var xf = t.xform;
            xf.SetRow(3, new nifly.Vector3(centre.X / HavokScale, centre.Y / HavokScale, -depth / 2 / HavokScale));
            t.xform = xf;
        });
        Meshes.BlockMeshBuilder.EditBlock<BSMultiBoundOBB>(nif, CullBox, o =>
        {
            o.center = new nifly.Vector3(centre.X, centre.Y, 0);
            o.size = new nifly.Vector3(half.X, half.Y, 1);
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
    }

    const string FallTemplate = @"meshes\effects\fxwaterfallthin512x128.nif";
    static byte[]? fallTemplate;

    /// <summary>
    /// Falling water in any shape: a copy of vanilla's thin waterfall (its scrolling fluid sheet, effect shader and
    /// controllers kept, its spray shape dropped) with the sheet remade as <paramref name="polygons"/> (model space,
    /// convex), textured as vanilla's: about one repeat per 200 units, running down the fall.
    /// </summary>
    public static void WriteFalls(string skyrimData, string outPath, IReadOnlyList<Vector3[]> polygons)
    {
        if (fallTemplate is null)
        {
            foreach (var bsa in Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa"))
                if (Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, bsa).Files
                        .FirstOrDefault(f => f.Path.Equals(FallTemplate, StringComparison.OrdinalIgnoreCase)) is { } file)
                { fallTemplate = file.GetBytes(); break; }
            if (fallTemplate is null) throw new FileNotFoundException($"{FallTemplate} isn't in Skyrim's mesh archives");
        }
        string tmp = Path.Combine(Path.GetTempPath(), "tamrielmerge_waterfall.nif");
        if (!File.Exists(tmp) || new FileInfo(tmp).Length != fallTemplate.Length) File.WriteAllBytes(tmp, fallTemplate);
        using var nif = new NifFile();
        nif.Load(tmp);
        var shapes = nif.GetShapes();
        var sheet = shapes[0];
        for (int i = shapes.Count - 1; i >= 1; i--) nif.DeleteShape(shapes[i]);
        using var vv = new vectorVector3();
        using var uv = new vectorVector2();
        using var nn = new vectorVector3();
        using var tris = new vectorTriangle();
        foreach (var poly in polygons)
        {
            if (poly.Length < 3 || vv.Count + poly.Length > 65000) continue;
            var n = Vector3.Cross(poly[1] - poly[0], poly[2] - poly[0]);
            n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitX;
            // Across the fall: horizontal and in the face; down the fall: world z.
            var across = Vector3.Cross(Vector3.UnitZ, n);
            across = across.LengthSquared() > 1e-6f ? Vector3.Normalize(across) : Vector3.UnitX;
            ushort start = (ushort)vv.Count;
            foreach (var p in poly)
            {
                vv.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                nn.Add(new nifly.Vector3(n.X, n.Y, n.Z));
                uv.Add(new nifly.Vector2(Vector3.Dot(p, across) / 200f, -p.Z / 200f));
            }
            for (int k = 1; k + 1 < poly.Length; k++) tris.Add(new Triangle(start, (ushort)(start + k), (ushort)(start + k + 1)));
        }
        if (tris.Count == 0) throw new ArgumentException("no falling-water polygons");
        var hdr = nif.GetHeader();
        Meshes.BlockMeshBuilder.EditBlock<BSTriShape>(nif, hdr.GetBlockID(sheet), s =>
        {
            uint shader = s.ShaderPropertyRef().index, alpha = s.AlphaPropertyRef().index;
            s.Create(NiVersion.getSSE(), vv, tris, uv, nn);
            s.SetShaderPropertyRef(shader);
            s.SetAlphaPropertyRef(alpha);
            s.UpdateBounds();
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
    }
}
