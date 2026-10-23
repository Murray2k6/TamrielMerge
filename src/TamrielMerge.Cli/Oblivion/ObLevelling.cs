using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Progression;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's levelling for the Mundus stones (<see cref="GameLevelling"/>). Oblivion keeps its rules in code, not
/// game settings (UESP Oblivion:Increasing_Skills, Leveling, Character_Creation): a skill in the class's specialization
/// needs 0.75 of the experience, a major skill 0.6 (both 0.45), so they rise 1/0.75 and 1/0.6 as fast; major skills
/// start at 25 (minor 5), the specialization's skills +5 and the two favoured attributes +5 (the manual's +10 is wrong);
/// each level raises three attributes by 1 for no skill rises they govern, 2 for 1-4, 3 for 5-7, 4 for 8-9, 5 for 10 or
/// more, and Luck by 1; health rises by a tenth of Endurance. The classes (CLAS, playable), the skills' governing
/// attributes and specializations (SKIL), the races' attributes and the birthsigns come from the plugins.
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>Oblivion's skills (actor value names) as Skyrim's or the progression's skills.</summary>
    static readonly Dictionary<string, string> ObSkillIds = new()
    {
        ["Armorer"] = "Smithing", ["Athletics"] = "Athletics", ["Blade"] = "OneHanded", ["Block"] = "Block", ["Blunt"] = "TwoHanded",
        ["HandToHand"] = "HandToHand", ["HeavyArmor"] = "HeavyArmor", ["Alchemy"] = "Alchemy", ["Alteration"] = "Alteration",
        ["Conjuration"] = "Conjuration", ["Destruction"] = "Destruction", ["Illusion"] = "Illusion", ["Mysticism"] = "Mysticism",
        ["Restoration"] = "Restoration", ["Acrobatics"] = "Acrobatics", ["LightArmor"] = "LightArmor", ["Marksman"] = "Archery",
        ["Mercantile"] = "Mercantile", ["Security"] = "Lockpicking", ["Sneak"] = "Sneak", ["Speechcraft"] = "Speech",
        // Mutagen's name for it in some enums (books' Teaches: "Speechraft").
        ["Speechraft"] = "Speech",
    };

    /// <summary>The progression's skills an Oblivion skill is: Skyrim's or the progression's, and for Blade and Blunt (which
    /// Skyrim folds into One- and Two-handed) also its hidden skill of the same name (ProgressionSkills.WeaponSkills).</summary>
    static IEnumerable<string> ObSkillTargets(string skill) =>
        !ObSkillIds.TryGetValue(skill, out var id) ? [] : skill is "Blade" or "Blunt" ? [id, skill] : [id];

    /// <summary>Oblivion's playable races (EditorIDs) as Skyrim's.</summary>
    static readonly Dictionary<string, string> ObRaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nord"] = "NordRace", ["DarkElf"] = "DarkElfRace", ["HighElf"] = "HighElfRace", ["WoodElf"] = "WoodElfRace", ["Breton"] = "BretonRace",
        ["Imperial"] = "ImperialRace", ["Khajiit"] = "KhajiitRace", ["Argonian"] = "ArgonianRace", ["Orc"] = "OrcRace", ["Redguard"] = "RedguardRace",
    };

    /// <summary>Oblivion's trainers, as their actors are made (<see cref="Skyrim.GameTrainers"/>).</summary>
    readonly List<Skyrim.GameTrainers.Trainer> obTrainers = [];
    public int TrainerSkills { get; private set; }

    /// <summary>The trainers' topics and classes (fTrainingCostMult gold a level for the progression's skills).</summary>
    void BuildTrainers() => TrainerSkills = Skyrim.GameTrainers.Build(mod, Vanilla.Skyrim, "OB", obTrainers, 10);

    static int ObAttribute(string av) => Array.FindIndex(GameLevelling.Attributes, a => a.Equals(av, StringComparison.OrdinalIgnoreCase));

    /// <summary>Oblivion's levelling, any birthsign spell list the scripts didn't make added to the plugin (call before
    /// writing it).</summary>
    public GameLevelling Levelling()
    {
        var governing = new Dictionary<string, int>();
        var specialization = new Dictionary<string, int>();
        foreach (var skill in plugins.SelectMany(p => p.Skills).GroupBy(s => s.FormKey).Select(g => g.Last()))
            if (skill.Data is { } d)
                foreach (var id in ObSkillTargets(skill.Skill?.ToString() ?? ""))
                {
                    if (ObAttribute(d.Attribute.ToString()) is >= 0 and var a) governing.TryAdd(id, a);
                    specialization.TryAdd(id, (int)d.Specialization);
                }
        var classes = new List<GameClass>();
        foreach (var c in plugins.SelectMany(p => p.Classes).GroupBy(c => c.FormKey).Select(g => g.Last()).OrderBy(c => c.Name ?? c.EditorID, StringComparer.OrdinalIgnoreCase))
        {
            if (c.Data is not { } d || !d.Flags.ToString().Contains("Playable")) continue;
            int[] favored = [.. d.PrimaryAttributes.Select(a => ObAttribute(a.ToString())).Where(a => a >= 0)];
            string[] major = [.. d.SecondaryAttributes.SelectMany(s => ObSkillTargets(s.ToString())).Distinct()];
            classes.Add(new GameClass(c.EditorID ?? c.FormKey.ToString(), c.Name ?? c.EditorID ?? "", c.Description ?? "", (int)d.Specialization, favored, major, []));
        }
        var races = new Dictionary<string, int[][]>();
        var raceSpells = new Dictionary<string, string>();
        var raceSkills = new Dictionary<string, Dictionary<string, int>>();
        foreach (var race in plugins.SelectMany(p => p.Races).GroupBy(r => r.FormKey).Select(g => g.Last()))
        {
            if (race.EditorID is not { } e || !ObRaces.TryGetValue(e, out var skyrim)) continue;
            if (race.RaceStats is { } stats)
            {
                static int[] Of(Ob.IRaceStatsGetter s) => [s.Strength, s.Intelligence, s.Willpower, s.Agility, s.Speed, s.Endurance, s.Personality, s.Luck];
                races[skyrim] = [Of(stats.Male), Of(stats.Female)];
            }
            // Its skill boosts, and its spells (the racial abilities and powers: Nords' frost resistance, Woad...).
            var bonus = new Dictionary<string, int>();
            foreach (var boost in new[] { race.Data?.SkillBoost0, race.Data?.SkillBoost1, race.Data?.SkillBoost2, race.Data?.SkillBoost3, race.Data?.SkillBoost4, race.Data?.SkillBoost5, race.Data?.SkillBoost6 }.OfType<Ob.ISkillBoostGetter>())
                if (boost.Boost > 0)
                    foreach (var id in ObSkillTargets(boost.Skill.ToString())) bonus[id] = Math.Max(bonus.GetValueOrDefault(id), (int)boost.Boost);
            raceSkills[skyrim] = bonus;
            var list = new FormList(mod) { EditorID = "TMObRace" + Sanitize(e) };
            foreach (var spell in race.Spells)
                if (ObSpell(spell.FormKey) is { } converted) list.Items.Add(converted.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            raceSpells[skyrim] = list.EditorID;
        }
        var signs = new List<GameSign>();
        foreach (var b in plugins.SelectMany(p => p.Birthsigns).GroupBy(b => b.FormKey).Select(g => g.Last()).OrderBy(b => b.EditorID, StringComparer.OrdinalIgnoreCase))
        {
            string name = b.Name ?? b.EditorID ?? "";
            int sign = Array.FindIndex(PassiveWeb.Signs, s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (sign < 0) { Problems.Add($"birthsign {b.EditorID} ({name}): no Mundus stone of that name"); continue; }
            string edid = "TMObBirthsign" + Sanitize(b.EditorID ?? "");
            // The script state quest's list (ObScriptRecords.AddBirthsigns), or one of its own without converted scripts.
            if (!mod.FormLists.Any(l => l.EditorID == edid))
            {
                var list = new FormList(mod) { EditorID = edid };
                foreach (var spell in b.Spells)
                    if (ObSpell(spell.FormKey) is { } converted) list.Items.Add(converted.ToLink<ISkyrimMajorRecordGetter>());
                mod.FormLists.Add(list);
            }
            signs.Add(new GameSign(sign, name, b.Description ?? "", edid));
        }
        return new GameLevelling
        {
            Game = "OB",
            Plugin = PluginName,
            Classes = classes,
            Rule = "pick3",
            LevelUp = [1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 5],
            LuckGain = 1,
            MajorRate = 1 / 0.6f,
            MinorRate = 1,
            MiscRate = 1,
            SpecialRate = 1 / 0.75f,
            MajorStart = 25,
            MinorStart = 5,
            SpecialBonus = 5,
            FavoredBonus = 5,
            HealthRule = "endurance",
            Governing = governing,
            Specialization = specialization,
            Races = races,
            RaceSpells = raceSpells,
            RaceSkills = raceSkills,
            Signs = signs,
        };
    }
}
