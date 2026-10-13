using System.Numerics;
using System.Text.Json;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// A creature's body remastered (user, 2026-10-10: "the guar model since it exist can we reimagine it", chose "remaster
/// it"): its meshes subdivided and smoothed in Blender (tools/creatures/remaster_mesh.py: seams merged, Catmull-Clark,
/// UVs and weights carried), and each texture made into a matte Skyrim pair by the PBR tool (tools/textures/pbr.py:
/// colour with occlusion, normals from DeepBump with the specular amount in their alpha). Bones and animations stay.
/// Morrowind's textures arrive already upscaled 4x when the upscale cache is there (MwTextures.UpscaleCache).
/// </summary>
public static class CreatureRemaster
{
    /// <summary>Matte, no metal, no parallax or reflections (the models are to be smooth and matte, never shiny).</summary>
    const string PbrParams = """{"rough_base": 0.8, "metal": 0, "spec_strength": 0.5, "parallax": false, "reflections": false, "ao_strength": 0.6}""";

    public static void Run(CreatureRig rig, string workDir, int levels, List<string> problems)
    {
        Directory.CreateDirectory(workDir);
        string before = Path.Combine(workDir, "remaster_in.json"), after = Path.Combine(workDir, "remaster_out.json");
        SaveMeshes(rig, before);
        string blender = Setup.Dependencies.Blender ?? throw new InvalidOperationException("Blender is needed: run setup");
        int code = Setup.ToolScripts.Run(blender, ["-b", "--disable-autoexec", "--python", Path.Combine(Setup.Dependencies.Root, "creatures", "remaster_mesh.py"), "--", before, after, levels.ToString()]);
        if (code != 0 || !File.Exists(after)) { problems.Add($"remaster: Blender failed ({code})"); return; }
        var meshes = LoadMeshes(after);

        // Textures: one PBR pair per distinct colour texture.
        string py = Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
        var made = new Dictionary<string, (string D, string N)?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < meshes.Count; i++)
        {
            var m = meshes[i];
            if (!m.Textures.TryGetValue("diffuse", out var diffuse) || !File.Exists(diffuse)) continue;
            if (!made.TryGetValue(diffuse, out var pair))
            {
                string dir = Path.Combine(workDir, "pbr", Path.GetFileNameWithoutExtension(diffuse));
                int rc = Setup.ToolScripts.Run(py, ["-I", Setup.ToolScripts.Path("textures", "pbr.py"), "make", diffuse, dir, "--params", PbrParams, "--model", Setup.Dependencies.NormalsModel]);
                pair = rc == 0 && File.Exists(Path.Combine(dir, "baked_d.dds")) ? (Path.Combine(dir, "baked_d.dds"), Path.Combine(dir, "baked_n.dds")) : null;
                if (pair is null) problems.Add($"remaster: no PBR set for {Path.GetFileName(diffuse)} ({rc})");
                made[diffuse] = pair;
            }
            if (pair is { } p)
            {
                var tex = new Dictionary<string, string>(m.Textures) { ["diffuse"] = p.D, ["normal"] = p.N };
                meshes[i] = m with { Textures = tex };
            }
        }
        rig.Meshes.Clear();
        rig.Meshes.AddRange(meshes);
    }

    static void SaveMeshes(CreatureRig rig, string path)
    {
        var doc = new
        {
            meshes = rig.Meshes.Select(m => new
            {
                name = m.Name,
                vertices = m.V.Select(v => new[] { v.X, v.Y, v.Z }),
                normals = m.N.Select(n => new[] { n.X, n.Y, n.Z }),
                uvs = m.Uv.Select(u => new[] { u.X, u.Y }),
                weights = m.Weights.Select(w => w.Select(p => new object[] { p.Bone, p.W })),
                triangles = m.Tris,
                textures = m.Textures,
            }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
    }

    static List<CreatureRig.Mesh> LoadMeshes(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        static Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        return doc.RootElement.GetProperty("meshes").EnumerateArray().Select(m => new CreatureRig.Mesh(
            m.GetProperty("name").GetString()!,
            m.GetProperty("vertices").EnumerateArray().Select(V3).ToArray(),
            m.GetProperty("normals").EnumerateArray().Select(V3).ToArray(),
            m.GetProperty("uvs").EnumerateArray().Select(e => new Vector2(e[0].GetSingle(), e[1].GetSingle())).ToArray(),
            m.GetProperty("weights").EnumerateArray().Select(e => e.EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetSingle())).ToArray()).ToArray(),
            m.GetProperty("triangles").EnumerateArray().Select(t => t.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToArray(),
            m.GetProperty("textures").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!))).ToList();
    }
}
