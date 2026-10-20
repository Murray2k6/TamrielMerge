using System.Text.Json;
using System.Text.Json.Serialization;

namespace TamrielMerge.Skyrim.Progression;

/// <summary>
/// Each converted game's own levelling (docs/progression.md "The games' levelling on the stones"): its classes, how
/// fast their skills rise, its level-up rule, its races' attributes and its birthsigns, written by the game's builder
/// beside its plugin (&lt;plugin&gt;.levelling.json) and read by the progression build, which puts them on the Mundus
/// stones: the web's signs carry each owned game's version of the sign, and the Character view of the Passives tab its
/// classes, the eight attributes and their gains at each level. Only the games converted have a file, so only their
/// rules are offered.
/// </summary>
public sealed record GameLevelling
{
    /// <summary>The eight attributes, in Morrowind's and Oblivion's order (Daggerfall's is mapped to it).</summary>
    public static readonly string[] Attributes = ["Strength", "Intelligence", "Willpower", "Agility", "Speed", "Endurance", "Personality", "Luck"];
    public const int Strength = 0, Intelligence = 1, Willpower = 2, Agility = 3, Speed = 4, Endurance = 5, Personality = 6, Luck = 7;

    /// <summary>The skills a class can name: Skyrim's eighteen (actor value names) and the progression's own
    /// (<see cref="ProgressionSkills"/> ids). Each game's skills map to these.</summary>
    public static readonly string[] SkyrimSkills = ["OneHanded", "TwoHanded", "Archery", "Block", "Smithing", "HeavyArmor", "LightArmor", "Pickpocket",
        "Lockpicking", "Sneak", "Alchemy", "Speech", "Alteration", "Conjuration", "Destruction", "Enchanting", "Illusion", "Restoration"];

    /// <summary>A skill's attribute when the game has no skill of its own for it (Skyrim's Pickpocket and Enchanting in
    /// Oblivion, the progression's skills a game lacks): the nearest the games give.</summary>
    public static readonly Dictionary<string, int> DefaultGoverning = new()
    {
        ["OneHanded"] = Strength, ["TwoHanded"] = Strength, ["Archery"] = Agility, ["Block"] = Endurance, ["Smithing"] = Endurance,
        ["HeavyArmor"] = Endurance, ["LightArmor"] = Speed, ["Pickpocket"] = Agility, ["Lockpicking"] = Agility, ["Sneak"] = Agility,
        ["Alchemy"] = Intelligence, ["Speech"] = Personality, ["Alteration"] = Willpower, ["Conjuration"] = Intelligence, ["Destruction"] = Willpower,
        ["Enchanting"] = Intelligence, ["Illusion"] = Personality, ["Restoration"] = Willpower, ["Mysticism"] = Intelligence, ["Athletics"] = Speed,
        ["Acrobatics"] = Speed, ["HandToHand"] = Strength, ["Spear"] = Endurance, ["MediumArmor"] = Endurance, ["Mercantile"] = Personality,
        ["Unarmored"] = Speed, ["Thaumaturgy"] = Willpower, ["Swimming"] = Endurance, ["Backstabbing"] = Agility, ["CriticalStrike"] = Agility,
        ["Medical"] = Intelligence, ["Dodging"] = Speed,
    };

    /// <summary>"DF", "MW" or "OB" (as <see cref="ProgressionSkills"/> names the games).</summary>
    public required string Game { get; init; }
    /// <summary>The game's plugin ("Morrowind.esm"), where its birthsign lists are.</summary>
    public required string Plugin { get; init; }
    public required List<GameClass> Classes { get; init; }
    /// <summary>"pick3": three attributes each raised by its multiplier (Morrowind, Oblivion); "pool": points to share
    /// out (Daggerfall).</summary>
    public required string Rule { get; init; }
    /// <summary>Pick3: the multiplier by the number of skill rises the attribute governed this level (index 0..10, 10
    /// or more the last).</summary>
    public int[] LevelUp { get; init; } = [];
    /// <summary>Pick3: Luck's gain each level (it governs no skill).</summary>
    public int LuckGain { get; init; } = 1;
    /// <summary>Pool: the fewest and most points a level brings.</summary>
    public int PoolMin { get; init; }
    public int PoolMax { get; init; }
    /// <summary>How much faster a class's skills rise than a skill of no standing (1): major, minor, other, and in its
    /// specialization (on top).</summary>
    public float MajorRate { get; init; } = 1;
    public float MinorRate { get; init; } = 1;
    public float MiscRate { get; init; } = 1;
    public float SpecialRate { get; init; } = 1;
    /// <summary>What a class's skills start at in the game (Skyrim's start at 15: the difference is the class's
    /// fortification): primary (Daggerfall), major, minor, and the specialization's bonus.</summary>
    public int PrimaryStart { get; init; }
    public int MajorStart { get; init; }
    public int MinorStart { get; init; }
    public int SpecialBonus { get; init; }
    /// <summary>The class's two favoured attributes' bonus.</summary>
    public int FavoredBonus { get; init; }
    /// <summary>Health each level: Endurance / 10 (Morrowind, Oblivion), or Daggerfall's roll of half to all the class's
    /// hit points per level plus Endurance / 10 - 5.</summary>
    public string HealthRule { get; init; } = "endurance";
    /// <summary>Each skill's governing attribute and specialization (0 combat, 1 magic, 2 stealth) in this game.</summary>
    public required Dictionary<string, int> Governing { get; init; }
    public Dictionary<string, int> Specialization { get; init; } = [];
    /// <summary>The races' attributes (Skyrim race EditorID: male, female); none for Daggerfall, whose classes set them.</summary>
    public Dictionary<string, int[][]> Races { get; init; } = [];
    public List<GameSign> Signs { get; init; } = [];
    /// <summary>Each race's own spells (abilities and powers: its resistances and racial powers), as a form list in the
    /// game's plugin, by Skyrim race EditorID.</summary>
    public Dictionary<string, string> RaceSpells { get; init; } = [];
    /// <summary>Each race's skill bonuses (Morrowind's RADT, Oblivion's skill boosts), by Skyrim race and skill.</summary>
    public Dictionary<string, Dictionary<string, int>> RaceSkills { get; init; } = [];

    public static string PathFor(string plugin) => Path.ChangeExtension(plugin, null) + ".levelling.json";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public void Save(string pluginPath) => File.WriteAllText(PathFor(pluginPath), JsonSerializer.Serialize(this, Json));

    public static GameLevelling? Load(string pluginPath) =>
        File.Exists(PathFor(pluginPath)) ? JsonSerializer.Deserialize<GameLevelling>(File.ReadAllText(PathFor(pluginPath)), Json) : null;

    /// <summary>The levelling of each converted game in <paramref name="dir"/> with a file (Daggerfall, Morrowind,
    /// Oblivion, in that order).</summary>
    public static List<GameLevelling> Converted(string dir) =>
        [.. new[] { "Daggerfall.esm", "Morrowind.esm", "Oblivion.esm" }.Select(p => Path.Combine(dir, p)).Where(File.Exists).Select(Load).OfType<GameLevelling>()];
}

/// <summary>A class: its name and description, specialization (-1 none), two favoured attributes, its skills by
/// standing (as <see cref="GameLevelling.SkyrimSkills"/> or progression skill ids; Daggerfall's primary too), and for
/// Daggerfall its advancement (how many times the skill uses a rise needs), hit points per level and traits.</summary>
public sealed record GameClass(string Id, string Name, string Description, int Specialization, int[] Favored, string[] Major, string[] Minor,
    string[]? Primary = null, float Advancement = 1, int HitPointsPerLevel = 0, int[]? Attributes = null, DfTraits? Traits = null);

/// <summary>A birthsign: the web sign it is (<see cref="PassiveWeb.Signs"/> index), its name, and the form list in the
/// game's plugin holding its converted spells.</summary>
public sealed record GameSign(int Sign, string Name, string Description, string SpellList);

/// <summary>
/// A Daggerfall class's advantages and disadvantages (CLASS*.CFG, as Daggerfall Unity's DFCareer reads it):
/// tolerances by effect (paralysis, magic, poison, fire, frost, shock, disease: 0 normal, 1 resistant, 2 immune,
/// 3 low tolerance, 4 critical weakness), spell points as a multiple of Intelligence, magery tied to light or darkness
/// (0 none, 1 unable, 2 reduced), rapid healing / spell absorption (0 none, 1 in light, 2 in darkness, 3 always),
/// regeneration (0 none, 1 in light, 2 in darkness, 3 in water, 4 always), the special abilities, the bonus to hit or
/// phobia against undead, daedra, humanoids and animals (1 bonus, -1 phobia), expertise and forbidden weapon groups
/// (short blades, long blades, hand-to-hand, axes, blunt, missile: bits 1..32), forbidden armour (leather 1, chain 2,
/// plate 4), shields (buckler 1, round 2, kite 4, tower 8) and materials (iron 1 ... daedric 512).
/// </summary>
public sealed record DfTraits(int[] Tolerance, float SpellPoints, int DarknessMagery, int LightMagery, int RapidHealing, int Regeneration, int Absorption,
    bool AcuteHearing, bool Athleticism, bool AdrenalineRush, bool NoMagickaRegen, bool SunDamage, bool HolyDamage, int[] Against,
    int Expert, int ForbiddenWeapons, int ForbiddenArmor, int ForbiddenShields, int ForbiddenMaterials);
