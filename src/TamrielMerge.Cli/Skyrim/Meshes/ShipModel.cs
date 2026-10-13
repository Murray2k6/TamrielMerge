using System.Numerics;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using Noggog;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// The ship that stands in for Redguard's and Daggerfall's ships (user, 2026-10-09: "redguards and daggerfalls ship need
/// replaced with this", a free CGTrader model; "ship.fbx and tex.zip ... need to be included in the tool"): tools/ship
/// holds ship.fbx, its textures (tex/) and links.json (each material's texture files, read from the FBX's own
/// connections by fbx_links.py, as Blender's importer misses them). Blender (export.py) writes the model's parts, one
/// OBJ per material, and a simplified hull for collision; this writes one NIF (a shape per material, its diffuse, normal
/// and specular maps; sails, rigging and windows two-sided) Length long with the bow along +Y and the keel at z = 0, and
/// a static for it. Placements scale it to the ship it replaces.
/// </summary>
public static class ShipModel
{
    /// <summary>The model's length (Skyrim units, bow along +Y).</summary>
    public const float Length = 3000;
    const string MeshRel = @"meshes\tamrielmerge\ship\ship.nif", TexFolder = @"textures\tamrielmerge\ship";

    /// <param name="Deck">The main deck's height above the keel (model units, at scale 1).</param>
    /// <param name="Solid">The collision's triangles (model space), for navmeshes on its decks.</param>
    public sealed record Made(Static Static, Vector3 Min, Vector3 Max, float Deck, List<Vector3[]> Solid);

    static readonly Dictionary<string, (Vector3 Min, Vector3 Max, float Deck, List<Vector3[]> Solid)> written = [];

    /// <summary>The ship's static in <paramref name="mod"/> (its mesh and textures written to <paramref name="modOutDir"/> once).</summary>
    public static Made? Build(SkyrimMod mod, string modOutDir, string editorId, List<string> problems)
    {
        try
        {
            if (!written.TryGetValue(Path.GetFullPath(modOutDir), out var bounds))
                written[Path.GetFullPath(modOutDir)] = bounds = Write(modOutDir);
            short C(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
            var stat = new Static(mod)
            {
                EditorID = editorId,
                ObjectBounds = new ObjectBounds { First = new P3Int16(C(bounds.Min.X), C(bounds.Min.Y), C(bounds.Min.Z)), Second = new P3Int16(C(bounds.Max.X), C(bounds.Max.Y), C(bounds.Max.Z)) },
                Model = new Model { File = MeshRel[7..] },
            };
            mod.Statics.Add(stat);
            return new Made(stat, bounds.Min, bounds.Max, bounds.Deck, bounds.Solid);
        }
        catch (Exception e) { problems.Add($"ship: {e.Message}"); return null; }
    }

    static (Vector3 Min, Vector3 Max, float Deck, List<Vector3[]> Solid) Write(string modOutDir)
    {
        string dir = Path.GetDirectoryName(Setup.ToolScripts.Path("ship", "ship.fbx"))!;
        if (!File.Exists(Path.Combine(dir, "ship.fbx"))) throw new FileNotFoundException("tools\\ship\\ship.fbx is missing");
        string parts = Path.Combine(dir, "parts");
        if (!Directory.Exists(parts) || !File.Exists(Path.Combine(dir, "collision.obj")))
        {
            string blender = Setup.Dependencies.Blender ?? throw new InvalidOperationException("Blender is needed once to read ship.fbx: run setup");
            var psi = new System.Diagnostics.ProcessStartInfo(blender) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-b", "-P", Path.Combine(dir, "export.py"), "--", dir }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!Directory.Exists(parts)) throw new InvalidOperationException("Blender didn't write the ship's parts");
        }
        var links = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(dir, "links.json")))!;

        var pieces = Directory.GetFiles(parts, "*.obj").OrderBy(f => f).Select(f => (Name: Path.GetFileNameWithoutExtension(f), Mesh: SpriteModelImporter.ReadObj(f))).ToList();
        // Blender's frame: x across, y along, z up. Which end is the bow: the hull's narrower end.
        var hull = pieces.Where(p => p.Name.StartsWith("korpus", StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Mesh.V).ToList();
        var all = pieces.SelectMany(p => p.Mesh.V).ToList();
        float y0 = all.Min(v => v.Y), y1 = all.Max(v => v.Y), span = y1 - y0;
        float Width(Func<Vector3, bool> where) { var w = hull.Where(where).ToList(); return w.Count == 0 ? 0 : w.Max(v => v.X) - w.Min(v => v.X); }
        bool bowAtMinY = Width(v => v.Y < y0 + span * 0.12f) < Width(v => v.Y > y1 - span * 0.12f);
        float s = Length / span;
        var lo3 = all.Aggregate(Vector3.Min); var hi3 = all.Aggregate(Vector3.Max);
        var shift = new Vector3(-(lo3.X + hi3.X) / 2, -(lo3.Y + hi3.Y) / 2, -lo3.Z);
        Vector3 Place(Vector3 v) { v = (v + shift) * s; return bowAtMinY ? new Vector3(-v.X, -v.Y, v.Z) : v; }
        Vector3 Turn(Vector3 n) => bowAtMinY ? new Vector3(-n.X, -n.Y, n.Z) : n;

        Directory.CreateDirectory(Path.Combine(modOutDir, TexFolder));
        string? Texture(string? file, Func<uint[], int, int, uint[]>? edit, bool alpha, string suffix, string material)
        {
            if (file is null || !File.Exists(Path.Combine(dir, "tex", file))) return null;
            var (px, w, h) = Util.Png.Read(Path.Combine(dir, "tex", file));
            if (edit is not null) px = edit(px, w, h);
            string rel = $@"{TexFolder}\{material.Replace('.', '_').ToLowerInvariant()}{suffix}.dds";
            Morrowind.MwTextures.WriteBcDds(Path.Combine(modOutDir, rel), px, w, h, alpha);
            return rel;
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var hdr = nif.GetHeader();
        var fade = BlockMeshBuilder.Owned(new BSFadeNode());
        fade.name = new NiStringRef("TMShip");
        hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        foreach (var (name, (v, uv, n, t)) in pieces)
        {
            if (v.Count > 65535) throw new InvalidDataException($"ship part {name}: {v.Count} vertices (a NIF shape holds 65535)");
            links.TryGetValue(name, out var maps);
            maps ??= [];
            // Diffuse: its own alpha only where it has some (the windows' glass).
            bool cutout = false;
            string? diffuse = Texture(maps.GetValueOrDefault("DiffuseColor"), (px, w, h) =>
            {
                cutout = px.Any(c => c >> 24 < 250);
                return px;
            }, alpha: true, "", name);
            if (!cutout && diffuse is not null) Texture(maps.GetValueOrDefault("DiffuseColor"), (px, w, h) => { for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u; return px; }, alpha: false, "", name);
            // Normal map with the specular map's brightness in its alpha (Skyrim's specular mask).
            string? specFile = maps.GetValueOrDefault("SpecularColor");
            string? normal = Texture(maps.GetValueOrDefault("NormalMap"), (px, w, h) =>
            {
                uint[]? spec = specFile is not null && File.Exists(Path.Combine(dir, "tex", specFile)) ? Util.Png.Read(Path.Combine(dir, "tex", specFile)).Argb : null;
                for (int i = 0; i < px.Length; i++)
                {
                    uint a = 0;
                    if (spec is not null && spec.Length == px.Length)
                    {
                        uint c = spec[i];
                        a = (uint)Math.Clamp((0.299f * ((c >> 16) & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * (c & 0xFF)) * 0.6f, 0, 255);
                    }
                    px[i] = (px[i] & 0xFFFFFFu) | a << 24;
                }
                return px;
            }, alpha: true, "_n", name);

            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            for (int i = 0; i < v.Count; i++)
            {
                var p = Place(v[i]);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                vv.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                uu.Add(new nifly.Vector2(uv[i].X, uv[i].Y));
                var q = Turn(n[i]);
                nn.Add(new nifly.Vector3(q.X, q.Y, q.Z));
            }
            foreach (var (a, b, c) in t) tt.Add(new Triangle((ushort)a, (ushort)b, (ushort)c));
            var shape = nif.CreateShapeFromData("TMShip_" + name, vv, tt, uu, nn);
            if (diffuse is not null) nif.SetTextureSlot(shape, diffuse, 0);
            nif.SetTextureSlot(shape, normal ?? DfTextureExport.FlatNormal, 1);
            nif.CalcTangentsForShape(shape);
            if (cutout) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x12ED, threshold = 0 }));   // glass: blended
            bool thin = name.Contains("sail", StringComparison.OrdinalIgnoreCase) || name.Contains("cable", StringComparison.OrdinalIgnoreCase) || name.Contains("window", StringComparison.OrdinalIgnoreCase);
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), sh =>
            {
                sh.shaderFlags1 = 0x82400301u;                          // specular, cast + receive shadows, ...
                sh.shaderFlags2 = 0x1u | 0x8000u | (thin ? 0x10u : 0); // ZBuffer write, ..., two-sided for thin parts
                sh.glossiness = 40;
                sh.specularStrength = 1;
            });
        }

        // Collision: the simplified hull, one convex slab per triangle.
        var (cv, _, _, ct) = SpriteModelImporter.ReadObj(Path.Combine(dir, "collision.obj"));
        var none = new BlockMeshBuilder.Tex("", "", false);
        var solid = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>();
        foreach (var (a, b, c) in ct)
        {
            var pa = Place(cv[a]); var pb = Place(cv[b]); var pc = Place(cv[c]);
            var nrm = Vector3.Cross(pb - pa, pc - pa);
            if (nrm.LengthSquared() < 1e-4f) continue;
            solid.Add(([pa, pb, pc], Vector3.Normalize(nrm), none));
        }
        if (solid.Count > 0) BlockMeshBuilder.AddCollision(nif, root, solid, moving: false);
        BlockMeshBuilder.AddBsxFlags(nif, root);
        string outPath = Path.Combine(modOutDir, MeshRel);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
        // The deck: the most common height of the upward faces the middle third of the ship is walked on (below the
        // tops and crow's nests: the lower half of its height).
        var walk = solid.Where(t => t.N.Z > 0.8f && t.Pts.All(p => MathF.Abs(p.Y) < Length / 3 && p.Z < (max.Z - min.Z) / 2))
            .Select(t => t.Pts.Average(p => p.Z)).ToList();
        float deck = walk.Count == 0 ? (max.Z - min.Z) * 0.3f : walk.GroupBy(z => MathF.Round(z / 16)).OrderByDescending(g => g.Count()).First().Average();
        return (min, max, deck, solid.Select(t => t.Pts).ToList());
    }
}
