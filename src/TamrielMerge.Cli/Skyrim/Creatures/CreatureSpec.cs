using System.Text.Json;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// One creature with its own skeleton to build: from its config (tools\creatures\&lt;id&gt;\creature.json), or, for
/// Morrowind, made for every model the game's creatures are drawn with that no config covers (user, 2026-10-21: "all
/// creatures": each its own model, animations, sounds, numbers and behaviour, none borrowed). Id "mw_" + the model's
/// name; name, actors (every creature drawn with it, as the conversion names them) and bones from the game.
/// </summary>
public sealed record CreatureSpec(string Id, string Game, string? Model, string Name, string? Head, string? Torso, List<string> Actors,
    string? Template, string? TemplateRace, int? Remaster, JsonElement? Stats, int? DfMonster = null, string? SameAs = null)
{
    public static string ConfigDir => Path.Combine(Setup.Dependencies.Root, "creatures");

    public static CreatureSpec? FromConfig(string id)
    {
        string path = Path.Combine(ConfigDir, id, "creature.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) ? v.GetString() : null;
        return new CreatureSpec(id, S("game") ?? "daggerfall", S("model"), S("name") ?? id, S("head"), S("torso"),
            r.TryGetProperty("actors", out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
            S("template"), S("templateRace"), r.TryGetProperty("remaster", out var rm) ? rm.GetInt32() : null,
            r.TryGetProperty("stats", out var st) ? st.Clone() : null,
            r.TryGetProperty("dfMonster", out var dm) ? dm.GetInt32() : null,
            S("sameAs"));
    }

    /// <summary>A fingerprint of what a config's Blender export reads (its source, armature, meshes, height, facing, actions,
    /// textures, materials, body): the export is made again only when that changes, not for a new name, actors, numbers or
    /// behaviour.</summary>
    public static string ExportKey(string configPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var read = new[] { "source", "armature", "meshes", "height", "facing", "forward", "actions", "textures", "materials", "body", "scale", "rotate" };
        var parts = doc.RootElement.EnumerateObject().Where(p => read.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Value.GetRawText());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }

    /// <summary>Every configured creature.</summary>
    public static List<CreatureSpec> Configured() =>
        Directory.Exists(ConfigDir)
            ? Directory.GetDirectories(ConfigDir).Order(StringComparer.OrdinalIgnoreCase).Select(d => FromConfig(Path.GetFileName(d))).OfType<CreatureSpec>().ToList()
            : [];

    /// <summary>The automatic id for a Morrowind model: "mw_" and its file name's letters and digits, lower case.</summary>
    public static string MorrowindId(string model) => "mw_" + new string(Path.GetFileNameWithoutExtension(model).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A creature for every model Morrowind's creatures are drawn with that no config covers.</summary>
    public static List<CreatureSpec> MorrowindAuto(MwWorld world, IEnumerable<CreatureSpec> configured)
    {
        var covered = configured.Where(c => c.Game == "morrowind" && c.Model is not null).Select(c => c.Model!.ToLowerInvariant()).ToHashSet();
        return world.Objects.Values.Where(o => o.Type == "CREA" && o.Str("MODL") is { Length: > 0 })
            .GroupBy(o => o.Str("MODL")!.ToLowerInvariant())
            .Where(g => !covered.Contains(g.Key))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var original = g.OrderBy(o => o.Str("CNAM") is null ? 0 : 1).ThenBy(o => o.Id, StringComparer.OrdinalIgnoreCase).First();
                return new CreatureSpec(MorrowindId(g.Key), "morrowind", g.Key, original.Str("FNAM") is { Length: > 0 } n ? n : original.Id, null, null,
                    g.Select(o => "MW" + MwPluginBuilder.Sanitize(o.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), null, null, null, null);
            }).ToList();
    }

    /// <summary>The spec for an id: its config, else Morrowind's automatic one.</summary>
    public static CreatureSpec? Find(string id, Func<MwWorld> world) =>
        FromConfig(id) ?? (id.StartsWith("mw_") ? MorrowindAuto(world(), Configured()).FirstOrDefault(s => s.Id == id) : null);

    /// <summary>The head and torso among a skeleton's bones, when the spec names none: the first "head" bone, and the spine,
    /// body or pelvis bone nearest the root (else the root's first child).</summary>
    public static (string? Head, string Torso) GuessBones(IReadOnlyList<string> bones)
    {
        string? head = bones.FirstOrDefault(b => b.EndsWith(" Head", StringComparison.OrdinalIgnoreCase) || b.Equals("Head", StringComparison.OrdinalIgnoreCase))
            ?? bones.FirstOrDefault(b => b.Contains("head", StringComparison.OrdinalIgnoreCase) && !b.Contains("cap", StringComparison.OrdinalIgnoreCase));
        string torso = new[] { "Bip01 Spine1", "Bip01 Spine", "Body Bone", "Bip01 Pelvis" }.FirstOrDefault(t => bones.Contains(t))
            ?? bones.FirstOrDefault(b => b.Contains("spine", StringComparison.OrdinalIgnoreCase) || b.Contains("body", StringComparison.OrdinalIgnoreCase) || b.Contains("pelvis", StringComparison.OrdinalIgnoreCase))
            ?? (bones.Count > 1 ? bones[1] : CreatureRig.RootBone);
        return (head, torso);
    }
}
