using System.Numerics;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's greaves and pauldrons made as worn pieces (user, 2026-10-19: "i need models made for the missing", "the
/// textures need to fit the missing models with no explosions too"): Daggerfall draws them only on its paper doll, and
/// Skyrim's bodies have no part for them. Each is made over its material's cuirass (the converted one: Skyrim's own,
/// recoloured for Daggerfall's metals) where the cuirass covers that part, else over the body (BodySleeve takes the
/// outermost surface a ray meets): a pauldron a sleeve round the upper arm from over the shoulder, greaves one round each
/// thigh down to the knee. Each point takes the weights of the surface under it (so it bends with the cuirass and body)
/// and wears the cuirass's texture: the part of it the cuirass wears there.
/// </summary>
public sealed class DfWornParts(SkyrimCharacterAssets assets, string outDir)
{
    /// <summary>The made pieces' checks (BodySleeve.Check), one line a mesh.</summary>
    public List<string> Checks { get; } = [];

    byte[]? Mesh(string path)
    {
        string rel = path.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? path : @"meshes\" + path;
        string ours = Path.Combine(outDir, rel);
        return File.Exists(ours) ? File.ReadAllBytes(ours) : assets.Read(rel);
    }

    static string Weight(string path, int weight) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"_[01]\.nif$", $"_{weight}.nif", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// <paramref name="piece"/> ("Greaves", "Left Pauldron", "Right Pauldron")'s worn meshes over the cuirass whose
    /// worn models (male, female; their _1, data-relative) are given, written under meshes\TamrielMerge\DF\Worn\
    /// <paramref name="edid"/>\, partition <paramref name="partition"/>. Paths (female, first person) to the _1 meshes,
    /// or null with the reason in <paramref name="problems"/>.
    /// </summary>
    public Dictionary<(bool Female, bool FirstPerson), string>? Make(string edid, string piece, string maleCuirass, string? femaleCuirass, int partition, List<string> problems)
    {
        string folder = $@"TamrielMerge\DF\Worn\{edid}";
        var paths = new Dictionary<(bool Female, bool FirstPerson), string>();
        foreach (bool female in new[] { false, true })
        {
            var skeleton = assets.Skeleton(female);
            Vector3 At(string bone) => skeleton.World[bone].Translation;
            // The sleeves' axes in bind space, from the bones (measured 2026-10-19: upper arms at x ±13, z 106.5, their
            // twist bones at ±15.9, 100.1; thighs at ±6.6, 68.9; calves at ±10.1, 33.5), off the joints: at the shoulder
            // and the top of the thigh a ring's rays toward the chest or the buttock run through the body, with no
            // outer surface to sit on (measured: none nearer than 2.6 units).
            var sleeves = new List<(Vector3 From, Vector3 To, float Reach, string Name, Vector3 Out, float Arc)>();
            if (piece.Contains("Pauldron"))
            {
                string s = piece.StartsWith("Left") ? "L" : "R";
                var joint = At($"NPC {s} UpperArm [{s}Uar]"); var down = At($"NPC {s} UpperarmTwist2 [{s}Ut2]");
                var dir = Vector3.Normalize(down - joint);
                sleeves.Add((joint + (down - joint) * 0.2f, joint + (down - joint) * 0.75f, 12, piece, s == "L" ? -Vector3.UnitX : Vector3.UnitX, 100));
            }
            else
                foreach (var s in new[] { "L", "R" })
                {
                    var hip = At($"NPC {s} Thigh [{s}Thg]"); var knee = At($"NPC {s} Calf [{s}Clf]");
                    float apart = MathF.Abs(hip.X) * 2;
                    sleeves.Add((hip + (knee - hip) * 0.2f, knee + Vector3.Normalize(knee - hip) * 2, apart / 2, $"Greaves {s}", Vector3.UnitY, 180));
                }
            // Both weights from the same rays (the slider blends the meshes vertex by vertex); the texture's part and its
            // repeats once, from the thin one.
            BodySleeve.TextureSpan? span = null; float repeats = 1; WornShape? look = null;
            int[]? columnsAt0 = null;
            foreach (int weight in new[] { 0, 1 })
            {
                string cuirassPath = Weight(female ? femaleCuirass ?? maleCuirass : maleCuirass, weight);
                if (Mesh(cuirassPath) is not { } bytes) { problems.Add($"{edid}: {cuirassPath} not found"); return null; }
                var cuirass = assets.Skinned(bytes, female).Where(x => !x.IsSkin && x.V.Length > 0).ToList();
                if (cuirass.Count == 0) { problems.Add($"{edid}: {cuirassPath} has no armour shapes"); return null; }
                var surfaces = cuirass.Concat(assets.Naked("body", female, weight)).ToList();
                var made = new List<WornShape>();
                var columns = new List<int>();
                int repaired = 0;
                foreach (var (from, to, reach, name, outward, arc) in sleeves)
                {
                    if (span is null || look is null)
                    {
                        // The cuirass shape nearest the sleeve, and the part of its texture its vertices there wear.
                        var mid = (from + to) / 2;
                        look = cuirass.MinBy(x => x.V.Min(p => Vector3.DistanceSquared(p, mid)))!;
                        var near = Enumerable.Range(0, look.V.Length).OrderBy(i => Vector3.DistanceSquared(look.V[i], mid)).Take(60).ToList();
                        var part = new WornShape { Name = "part", V = near.Select(i => look.V[i]).ToArray(), N = [], Uv = near.Select(i => look.Uv[i]).ToArray(), Tris = [], Weights = [] };
                        span = BodySleeve.TextureSpan.Of(part);
                        var sp = span.Value;
                        float length = Vector3.Distance(from, to);
                        float partLong = sp.AlongV ? sp.V1 - sp.V0 : sp.U1 - sp.U0, partWide = sp.AlongV ? sp.U1 - sp.U0 : sp.V1 - sp.V0;
                        float around = 2 * MathF.PI * (piece.Contains("Pauldron") ? 7 : 8);
                        repeats = partWide > 0 ? Math.Clamp(MathF.Round(around / (length * partLong / partWide)), 1, 6) : 1;
                    }
                    var sleeve = BodySleeve.Make(surfaces, from, to, outward, 4, 24, 0.6f, reach, name, look.Textures, repeats, span.Value, out int cols, arc);
                    if (sleeve is null) { problems.Add($"{edid}: no surface for {name} ({(female ? "female" : "male")} {weight}: {BodySleeve.LastFailure})"); return null; }
                    sleeve.ShaderType = look.ShaderType; sleeve.Flags1 = look.Flags1; sleeve.Flags2 = look.Flags2; sleeve.Glossiness = look.Glossiness;
                    sleeve.SpecularStrength = look.SpecularStrength; sleeve.SpecularColor = look.SpecularColor; sleeve.Clamp = 3;
                    made.Add(sleeve);
                    columns.Add(cols);
                    repaired += BodySleeve.Repaired;
                }
                if (columnsAt0 is { } c0 && !c0.SequenceEqual(columns)) { problems.Add($"{edid}: the two weights' sleeves differ"); return null; }
                columnsAt0 = columns.ToArray();
                string file = $@"{folder}\{(female ? "f" : "m")}_{weight}.nif";
                SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(outDir, "meshes", file), made, skeleton, partition));
                lock (Checks)
                    Checks.Add($"{edid} {(female ? "f" : "m")}_{weight}: {repaired} points put back on their ring, " + BodySleeve.Check(made, surfaces, skeleton,
                        t => File.Exists(Path.Combine(outDir, t)) || assets.HasTexture(t)));
            }
            paths[(female, false)] = $@"{folder}\{(female ? "f" : "m")}_1.nif";
        }
        return paths;
    }
}
