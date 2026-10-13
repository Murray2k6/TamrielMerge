using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace TamrielMerge.Skyrim;

/// <summary>
/// What a converted game offers procedural creatures (written beside its plugin as &lt;plugin&gt;.creatures.json, read by
/// the "creatures" step): its ordinary creatures — not bosses, uniques or quest creatures — with their level and
/// habitats; the spots where the game places ordinary creatures (which become spawners); and its respawn times.
/// Respawn hours below 0 mean never (the game doesn't bring them back).
/// </summary>
public sealed class CreatureCatalog
{
    public string Game { get; set; } = "";
    public string Plugin { get; set; } = "";
    /// <summary>Respawn for new wilderness and dungeon points in this game's worlds.</summary>
    public float ExteriorRespawnHours { get; set; } = -1;
    public float InteriorRespawnHours { get; set; } = -1;
    public List<Creature> Creatures { get; set; } = [];
    public List<Spot> Spots { get; set; } = [];

    public sealed record Creature(string FormKey, string Name, int Level, string[] Habitats);
    /// <summary>An ordinary creature's placement: the reference, its interior cell or worldspace, position, the habitat
    /// it stands for, its level and how soon it comes back.</summary>
    public sealed record Spot(string Ref, string Cell, string Worldspace, float X, float Y, float Z, string Habitat, int Level, float RespawnHours);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string PathFor(string pluginPath) => Path.ChangeExtension(pluginPath, ".creatures.json");
    public void Save(string pluginPath) => File.WriteAllText(PathFor(pluginPath), JsonSerializer.Serialize(this, Json));
    public static CreatureCatalog? Load(string pluginPath) =>
        File.Exists(PathFor(pluginPath)) ? JsonSerializer.Deserialize<CreatureCatalog>(File.ReadAllText(PathFor(pluginPath)), Json) : null;

    public static string Key(FormKey k) => k.ToString();
    public static FormKey Key(string s) => FormKey.Factory(s);

    /// <summary>Where a creature belongs, from its name (the same words across the games): the first is its main one.</summary>
    public static string[] HabitatsOf(string name)
    {
        string n = name.ToLowerInvariant();
        var h = new List<string>();
        bool Has(params string[] words) => words.Any(n.Contains);
        if (Has("slaughterfish", "dreugh", "lamia", "fish", "eel", "mudcrab", "baliwog")) h.Add("Water");
        if (Has("skeleton", "skeletal", "zombie", "ghost", "wraith", "mummy", "lich", "vampire", "bonewalker", "bonelord", "ancestor", "draugr", "spectre", "spirit", "apparition", "shambles", "bone"))
            h.Add("Undead");
        if (Has("daedra", "daedroth", "atronach", "scamp", "clannfear", "dremora", "xivilai", "golden saint", "winged twilight", "hunger", "seducer", "spider daedra", "imp", "gargoyle", "storm"))
            h.Add("Daedra");
        if (Has("centurion", "sphere", "steam", "fabricant", "automaton", "dwarven", "dwemer", "iron atronach")) h.Add("Construct");
        if (Has("ash ", "ash_", "ash zombie", "ash ghoul", "ash slave", "ascended", "dagoth", "corprus", "sleeper")) h.Add("Ash");
        if (Has("ice", "frost", "snow", "riekling", "horker", "wolf", "bear")) h.Add("Cold");
        if (Has("rat", "spider", "troll", "goblin", "scorpion", "kwama", "scrib", "bat", "grummite", "ogre", "minotaur", "orc")) h.Add("Cave");
        if (Has("wolf", "bear", "boar", "tiger", "sabre", "guar", "kagouti", "alit", "nix", "cliff racer", "netch", "durzog", "cougar", "lion", "spriggan", "centaur", "nymph",
                "harpy", "werewolf", "wereboar", "giant", "elytra", "gnarl", "hound", "deer", "elk", "dog", "horse", "unicorn", "shalk"))
            h.Add("Beast");
        if (h.Count == 0) h.Add("Beast");
        return [.. h.Distinct()];
    }
}
