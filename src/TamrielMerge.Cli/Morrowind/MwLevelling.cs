using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Progression;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's levelling for the Mundus stones (<see cref="GameLevelling"/>): its playable classes (CLAS), how its
/// skills rise (fMajorSkillBonus, fMinorSkillBonus, fMiscSkillBonus, fSpecialSkillBonus: the progress a rise needs, so
/// a rate is one over it), its level-up multipliers (iLevelUp01Mult..iLevelUp10Mult), its skills' governing attributes
/// and specializations (SKIL), its races' attributes (RADT) and its birthsigns (BSGN), whose spells go into form lists
/// TMMwBirthsign&lt;id&gt; here for the progression build to give.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>Morrowind's 27 skills (SKIL INDX order) as Skyrim's or the progression's skills.</summary>
    static readonly string[] MwSkillIds =
    [
        "Block", "Smithing", "MediumArmor", "HeavyArmor", "TwoHanded", "OneHanded", "TwoHanded", "Spear", "Athletics", "Enchanting", "Destruction",
        "Alteration", "Illusion", "Conjuration", "Mysticism", "Restoration", "Alchemy", "Unarmored", "Lockpicking", "Sneak", "Acrobatics", "LightArmor",
        "OneHanded", "Archery", "Mercantile", "Speech", "HandToHand",
    ];

    /// <summary>Morrowind's weapon skills Skyrim folds into One- and Two-handed, also kept as the progression's hidden
    /// skills (ProgressionSkills.WeaponSkills): a class or race naming one names both.</summary>
    static readonly Dictionary<int, string> MwHiddenSkills = new() { [4] = "BluntWeapon", [5] = "LongBlade", [6] = "Axe", [22] = "ShortBlade" };

    /// <summary>The progression's skills a Morrowind skill is: Skyrim's or the progression's, and its hidden one.</summary>
    static IEnumerable<string> MwSkillTargets(int index) =>
        MwHiddenSkills.TryGetValue(index, out var hidden) ? [MwSkillIds[index], hidden] : [MwSkillIds[index]];

    /// <summary>Morrowind's races (RACE ids) as Skyrim's.</summary>
    static readonly (string Mw, string Skyrim)[] LevellingRaces =
    [
        ("Nord", "NordRace"), ("Dark Elf", "DarkElfRace"), ("High Elf", "HighElfRace"), ("Wood Elf", "WoodElfRace"), ("Breton", "BretonRace"),
        ("Imperial", "ImperialRace"), ("Khajiit", "KhajiitRace"), ("Argonian", "ArgonianRace"), ("Orc", "OrcRace"), ("Redguard", "RedguardRace"),
    ];

    /// <summary>The plugins' records of a type by ID, the last plugin's winning and deleted ones gone: read from the
    /// plugins, as the world's objects keep one record per ID whatever its type ("Lady's Favor" is a birthsign and a
    /// spell).</summary>
    Dictionary<string, Tes3Record> OwnRecords(string type)
    {
        var records = new Dictionary<string, Tes3Record>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type == type && r.Id.Length > 0))
                if (r.Deleted) records.Remove(r.Id); else records[r.Id] = r;
        return records;
    }

    float Setting(string name, float fallback) =>
        world.Settings.TryGetValue(name, out var g) ? (g.Get("FLTV")?.Float() ?? g.Get("INTV")?.Int() ?? fallback) : fallback;

    /// <summary>Morrowind's levelling, its birthsigns' spell lists added to the plugin (call before writing it).</summary>
    public GameLevelling Levelling()
    {
        // Skills: governing attribute and specialization (SKDT), from the plugins (SKIL has no NAME to key it by).
        var governing = new Dictionary<string, int>();
        var specialization = new Dictionary<string, int>();
        var skillSpec = new Dictionary<int, int>();
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type == "SKIL"))
                if (r.Get("INDX") is { Data.Length: >= 4 } i && i.Int() is >= 0 and < 27 and var index && r.Get("SKDT") is { Data.Length: >= 8 } d)
                {
                    skillSpec[index] = d.Int(4);
                    // The first Morrowind skill a shared one stands for decides (Long Blade before Short Blade for One-Handed).
                    foreach (var id in MwSkillTargets(index))
                    {
                        governing.TryAdd(id, d.Int(0));
                        specialization.TryAdd(id, d.Int(4));
                    }
                }
        var classes = new List<GameClass>();
        foreach (var rec in OwnRecords("CLAS").Values.OrderBy(o => o.Str("FNAM") ?? o.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (rec.Get("CLDT") is not { Data.Length: >= 56 } d || (d.Int(52) & 1) == 0) continue;   // playable classes only
            var major = Enumerable.Range(0, 5).Select(k => d.Int(16 + k * 8)).Where(s => s is >= 0 and < 27).SelectMany(MwSkillTargets).Distinct().ToArray();
            var minor = Enumerable.Range(0, 5).Select(k => d.Int(12 + k * 8)).Where(s => s is >= 0 and < 27).SelectMany(MwSkillTargets).Distinct().Except(major).ToArray();
            classes.Add(new GameClass(rec.Id, rec.Str("FNAM") ?? rec.Id, rec.Str("DESC") ?? "", d.Int(8), [d.Int(0), d.Int(4)], major, minor));
        }
        var races = new Dictionary<string, int[][]>();
        var raceRecords = OwnRecords("RACE");
        var raceSpells = new Dictionary<string, string>();
        var raceSkills = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (mw, skyrim) in LevellingRaces)
        {
            if (!raceRecords.TryGetValue(mw, out var race)) continue;
            if (race.Get("RADT") is { Data.Length: >= 120 } r)
            {
                races[skyrim] = [[.. Enumerable.Range(0, 8).Select(a => r.Int(56 + a * 8))], [.. Enumerable.Range(0, 8).Select(a => r.Int(60 + a * 8))]];
                // Its seven skill bonuses (skill, bonus); two Morrowind skills that are one Skyrim skill give the larger.
                var bonus = new Dictionary<string, int>();
                for (int i = 0; i < 7; i++)
                    if (r.Int(i * 8) is >= 0 and < 27 and var s && r.Int(i * 8 + 4) is > 0 and var b)
                        foreach (var id in MwSkillTargets(s)) bonus[id] = Math.Max(bonus.GetValueOrDefault(id), b);
                raceSkills[skyrim] = bonus;
            }
            // Its spells (NPCS): the racial abilities (resistances, the Dunmer's and Nords' powers...).
            var list = new FormList(mod) { EditorID = "TMMwRace" + Sanitize(mw) };
            foreach (var spellId in race.All("NPCS").Select(f => f.Text))
                if (MwSpell(spellId) is { } spell) list.Items.Add(spell.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            raceSpells[skyrim] = list.EditorID;
        }
        return new GameLevelling
        {
            Game = "MW",
            Plugin = PluginName,
            Classes = classes,
            Rule = "pick3",
            // 0 rises: x1; then iLevelUp01Mult..iLevelUp10Mult.
            LevelUp = [1, .. Enumerable.Range(1, 10).Select(n => (int)Setting($"iLevelUp{n:D2}Mult", n switch { <= 4 => 2, <= 7 => 3, <= 9 => 4, _ => 5 }))],
            LuckGain = 1,
            MajorRate = 1 / Setting("fMajorSkillBonus", 0.75f),
            MinorRate = 1 / Setting("fMinorSkillBonus", 1f),
            MiscRate = 1 / Setting("fMiscSkillBonus", 1.25f),
            SpecialRate = 1 / Setting("fSpecialSkillBonus", 0.8f),
            // A major skill starts at 30 (25 over the misc 5), a minor at 15, a specialization's +5 (Morrowind's manual).
            MajorStart = 30,
            MinorStart = 15,
            SpecialBonus = 5,
            FavoredBonus = 10,
            HealthRule = "endurance",
            Governing = governing,
            Specialization = specialization,
            Races = races,
            RaceSpells = raceSpells,
            RaceSkills = raceSkills,
            Signs = BirthsignLists(),
        };
    }

    /// <summary>Who offers training (AIDT services 0x4000), as their actors are made.</summary>
    readonly List<(Npc Npc, string Id)> trainerNpcs = [];

    /// <summary>Trainer skills made (<see cref="Skyrim.GameTrainers"/>).</summary>
    public int TrainerSkills { get; private set; }

    /// <summary>Morrowind's trainers: each teaches its three best skills, up to its own level in each (Morrowind's
    /// rule), at iTrainingMod gold a level for the progression's skills.</summary>
    void BuildTrainers()
    {
        var stats = new MwStats(world);
        var trainers = new List<Skyrim.GameTrainers.Trainer>();
        foreach (var (npc, id) in trainerNpcs)
        {
            if (stats.SkillsOf(id) is not { } skills) continue;
            // Their three best Morrowind skills; two that are the same Skyrim skill teach it once, to the higher.
            foreach (var g in Enumerable.Range(0, 27).OrderByDescending(s => skills[s]).ThenBy(s => s).Take(3).GroupBy(s => MwSkillIds[s]))
                trainers.Add(new(npc, g.Key, g.Max(s => skills[s])));
        }
        TrainerSkills = Skyrim.GameTrainers.Build(mod, SkyrimEsm, "MW", trainers, (int)Setting("iTrainingMod", 10));
    }

    /// <summary>Each birthsign's spells (abilities, powers, spells) as a form list TMMwBirthsign&lt;id&gt;.</summary>
    List<GameSign> BirthsignLists()
    {
        var signs = new List<GameSign>();
        // From the plugins themselves: a birthsign's ID can be a spell's too ("Lady's Favor"), and the world's objects
        // keep one record per ID (MwStats.Birthsigns reads them the same way).
        foreach (var rec in OwnRecords("BSGN").Values.OrderBy(o => o.Id, StringComparer.OrdinalIgnoreCase))
        {
            string name = rec.Str("FNAM") ?? rec.Id;
            int sign = Array.FindIndex(PassiveWeb.Signs, s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (sign < 0) { Problems.Add($"birthsign {rec.Id} ({name}): no Mundus stone of that name"); continue; }
            var list = new FormList(mod) { EditorID = "TMMwBirthsign" + Sanitize(rec.Id) };
            foreach (var spellId in rec.All("NPCS").Select(f => f.Text))
                if (MwSpell(spellId) is { } spell) list.Items.Add(spell.ToLink<ISkyrimMajorRecordGetter>());
                else Problems.Add($"birthsign {rec.Id}: spell {spellId} not converted");
            mod.FormLists.Add(list);
            signs.Add(new GameSign(sign, name, rec.Str("DESC") ?? "", list.EditorID));
        }
        return signs;
    }
}
