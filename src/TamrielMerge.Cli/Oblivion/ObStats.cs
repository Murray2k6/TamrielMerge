using Mutagen.Bethesda.Plugins;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's own attributes and skills for each converted person and creature (user, 2026-10-23: "Keep original stats"),
/// which the translated scripts read and change (TMObState.GetStat, TMObActor.StatBase / StatRate): 0-7 the attributes
/// (Strength .. Luck), 8-28 the 21 skills in Oblivion's order (<see cref="StatSkills"/>), 29-32 its AI values
/// (Aggression, Confidence, Energy, Responsibility, 0-100; the record's AIDT).
/// <list type="bullet">
/// <item>A person with its stats set (most named people): the record's.</item>
/// <item>One whose stats Oblivion works out (Auto calc stats; UESP Oblivion:NPCs): at level 1 those of a player of its
/// race, sex and class (the race's attributes, +5 to the class's two; every skill 5, a major one 25, one of the
/// specialization +5, the race's skill bonuses), then each level after the first a major skill +1 (+1.5 in the
/// specialization), another of the specialization +0.6, any other +0.1, and each attribute but Personality and Luck +0.6,
/// +1.4, +2.2 or +3 for none, one, two or three (or more) of the major skills it governs; rounded to whole numbers, at
/// most 100. The level is the actor's as it is (it may follow the player's), so the rates go with the base.</item>
/// <item>A creature: its attributes, and its combat, magic or stealth skill for each skill of that specialization.</item>
/// </list>
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>Oblivion's skills (actor value names), in its order: stat 8 + the index.</summary>
    public static readonly string[] StatSkills =
    [
        "Armorer", "Athletics", "Blade", "Block", "Blunt", "HandToHand", "HeavyArmor", "Alchemy", "Alteration", "Conjuration",
        "Destruction", "Illusion", "Mysticism", "Restoration", "Acrobatics", "LightArmor", "Marksman", "Mercantile", "Security",
        "Sneak", "Speechcraft",
    ];

    /// <summary>Oblivion's AI values, stat 29 + the index.</summary>
    public static readonly string[] StatAi = ["Aggression", "Confidence", "Energy", "Responsibility"];
    public const int StatCount = 33;

    /// <summary>A stat's number by its actor value name (an attribute, skill or AI value), or -1.</summary>
    public static int StatIndex(string av)
    {
        int a = Array.FindIndex(Skyrim.Progression.GameLevelling.Attributes, x => x.Equals(av, StringComparison.OrdinalIgnoreCase));
        if (a >= 0) return a;
        int ai = Array.FindIndex(StatAi, x => x.Equals(av, StringComparison.OrdinalIgnoreCase));
        if (ai >= 0) return 29 + ai;
        if (av.Equals("Speechraft", StringComparison.OrdinalIgnoreCase)) av = "Speechcraft";
        int s = Array.FindIndex(StatSkills, x => x.Equals(av, StringComparison.OrdinalIgnoreCase));
        return s >= 0 ? 8 + s : -1;
    }

    Dictionary<string, (int Attribute, int Specialization)>? skillRules;

    /// <summary>Each skill's governing attribute and specialization (0 combat, 1 magic, 2 stealth), from the plugins' SKIL.</summary>
    Dictionary<string, (int Attribute, int Specialization)> SkillRules()
    {
        if (skillRules is not null) return skillRules;
        skillRules = new(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in plugins.SelectMany(p => p.Skills).GroupBy(s => s.FormKey).Select(g => g.Last()))
            if (skill.Data is { } d && skill.Skill?.ToString() is { } name)
                skillRules[name.Equals("Speechraft", StringComparison.OrdinalIgnoreCase) ? "Speechcraft" : name] = (ObAttribute(d.Attribute.ToString()), (int)d.Specialization);
        return skillRules;
    }

    /// <summary>Oblivion's stats of an NPC or creature record: the base (level 1 for a worked-out one) and the rise a level.</summary>
    (int[] Base, float[] Rate)? ObStats(Ob.IOblivionMajorRecordGetter rec)
    {
        var stats = new int[StatCount];
        var rate = new float[StatCount];
        // The AI values, the record's whatever the stats.
        if ((rec as Ob.INpcGetter)?.AIData is { } npcAi)
            new int[] { npcAi.Aggression, npcAi.Confidence, npcAi.EnergyLevel, npcAi.Responsibility }.CopyTo(stats, 29);
        else if ((rec as Ob.ICreatureGetter)?.AIData is { } creatureAi)
            new int[] { creatureAi.Aggression, creatureAi.Confidence, creatureAi.EnergyLevel, creatureAi.Responsibility }.CopyTo(stats, 29);
        switch (rec)
        {
            case Ob.INpcGetter n:
            {
                bool auto = n.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.AutoCalcStats) == true;
                if (!auto)
                {
                    if (n.Stats is not { } d) return null;
                    int[] attributes = [d.Strength, d.Intelligence, d.Willpower, d.Agility, d.Speed, d.Endurance, d.Personality, d.Luck];
                    int[] skills = [d.Armorer, d.Athletics, d.Blade, d.Block, d.Blunt, d.HandToHand, d.HeavyArmor, d.Alchemy, d.Alteration, d.Conjuration,
                        d.Destruction, d.Illusion, d.Mysticism, d.Restoration, d.Acrobatics, d.LightArmor, d.Marksman, d.Mercantile, d.Security, d.Sneak, d.Speechcraft];
                    attributes.CopyTo(stats, 0);
                    skills.CopyTo(stats, 8);
                    return (stats, rate);
                }
                bool female = n.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.Female) == true;
                // The race: its attributes for the sex, its skill bonuses.
                if (records.GetValueOrDefault(n.Race.FormKey) is Ob.IRaceGetter race)
                {
                    if (race.RaceStats is { } rs && (female ? rs.Female : rs.Male) is { } s)
                        new int[] { s.Strength, s.Intelligence, s.Willpower, s.Agility, s.Speed, s.Endurance, s.Personality, s.Luck }.CopyTo(stats, 0);
                    foreach (var boost in new[] { race.Data?.SkillBoost0, race.Data?.SkillBoost1, race.Data?.SkillBoost2, race.Data?.SkillBoost3, race.Data?.SkillBoost4, race.Data?.SkillBoost5, race.Data?.SkillBoost6 }.OfType<Ob.ISkillBoostGetter>())
                        if (boost.Boost > 0 && StatIndex(boost.Skill.ToString()) is >= 8 and var k) stats[k] += boost.Boost;
                }
                var rules = SkillRules();
                var cls = records.GetValueOrDefault(n.Class.FormKey) as Ob.IClassGetter;
                int specialization = cls?.Data is { } cd ? (int)cd.Specialization : -1;
                var favored = cls?.Data?.PrimaryAttributes.Select(a => ObAttribute(a.ToString())).Where(a => a >= 0).ToHashSet() ?? [];
                var major = cls?.Data?.SecondaryAttributes.Select(s => s.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                foreach (int a in favored) stats[a] += 5;
                var governedMajors = new int[8];
                for (int i = 0; i < StatSkills.Length; i++)
                {
                    string skill = StatSkills[i];
                    bool isMajor = major.Contains(skill) || skill == "Speechcraft" && major.Contains("Speechraft");
                    bool special = rules.TryGetValue(skill, out var rule) && rule.Specialization == specialization;
                    stats[8 + i] += (isMajor ? 25 : 5) + (special ? 5 : 0);
                    rate[8 + i] = isMajor ? (special ? 1.5f : 1f) : special ? 0.6f : 0.1f;
                    if (isMajor && rule.Attribute is >= 0 and < 8) governedMajors[rule.Attribute]++;
                }
                float[] attributeRate = [0.6f, 1.4f, 2.2f, 3f];
                for (int a = 0; a < 8; a++)
                    rate[a] = a is 6 or 7 ? 0 : attributeRate[Math.Min(governedMajors[a], 3)];
                return (stats, rate);
            }
            case Ob.ICreatureGetter c:
            {
                if (c.Data is not { } d) return null;
                new int[] { d.Strength, d.Intelligence, d.Willpower, d.Agility, d.Speed, d.Endurance, d.Personality, d.Luck }.CopyTo(stats, 0);
                var rules = SkillRules();
                for (int i = 0; i < StatSkills.Length; i++)
                    stats[8 + i] = (rules.TryGetValue(StatSkills[i], out var rule) ? rule.Specialization : 0) switch { 1 => d.MagicSkill, 2 => d.StealthSkill, _ => d.CombatSkill };
                return (stats, rate);
            }
        }
        return null;
    }

    /// <summary>The player's: each skill's Skyrim actor value where Skyrim keeps it, else its progression skill (TMSkills
    /// id; Blade and Blunt the hidden ones of those names).</summary>
    /// <summary>The AI values' Skyrim actor values (for the player, and for actors that aren't Oblivion's).</summary>
    public static readonly string[] SkyrimAiAv = ["Aggression", "Confidence", "Energy", "Morality"];

    static readonly (string Av, string Id)[] PlayerStatHome =
    [
        ("Smithing", ""), ("", "Athletics"), ("", "Blade"), ("Block", ""), ("", "Blunt"), ("", "HandToHand"), ("HeavyArmor", ""),
        ("Alchemy", ""), ("Alteration", ""), ("Conjuration", ""), ("Destruction", ""), ("Illusion", ""), ("", "Mysticism"), ("Restoration", ""),
        ("", "Acrobatics"), ("LightArmor", ""), ("Marksman", ""), ("", "Mercantile"), ("Lockpicking", ""), ("Sneak", ""), ("Speechcraft", ""),
        // The player's AI values: Skyrim's own.
        ("Aggression", ""), ("Confidence", ""), ("Energy", ""), ("Morality", ""),
    ];
}
