using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using EP = Mutagen.Bethesda.Skyrim.APerkEntryPointEffect.EntryType;

namespace TamrielMerge.Skyrim.Progression;

/// <summary>
/// The games' levelling on the Mundus stones (docs/progression.md "The games' levelling on the stones", user
/// 2026-10-20: "we add them based on what game the users own and to the mundus stones for perks also based on game
/// owned"). From each converted game's <see cref="GameLevelling"/>:
/// <list type="bullet">
/// <item>The web's thirteen signs carry the owned games' own versions of the sign: taking the start gives their
/// spells (Morrowind's and Oblivion's birthsign abilities and powers).</item>
/// <item>A class from any owned game, taken in the Character view (free, one at a time): a perk whose Mod Skill Use
/// entries make its skills rise as the game's do (the Guardian Stones' own entry point), whose ability fortifies them
/// to what the game starts them at, and for Daggerfall its advantages and disadvantages; its favoured attributes.</item>
/// <item>The eight attributes, from the race (Morrowind's or Oblivion's tables) or the Daggerfall class, the class's
/// favoured bonus and each level's gains (the class's game's rule: three picks by the skills raised, or Daggerfall's
/// points), working through one hidden ability; and health each level by Endurance (or Daggerfall's roll).</item>
/// </list>
/// Only the games converted have levelling files, so nothing of a game not owned appears.
/// </summary>
public static class GameStones
{
    /// <summary>What each attribute does, per point over or under the game's average (40 in Morrowind and Oblivion, 50
    /// in Daggerfall): Strength carries and hits (Morrowind 5 x Strength in weight, Daggerfall (Strength - 50) / 5
    /// damage), Intelligence holds magicka, Willpower restores it and resists magic (Daggerfall Willpower / 10), Agility
    /// aims and sneaks, Speed runs (Daggerfall (Speed + 150) / 39.5), Endurance gives health and heals, Personality
    /// bargains, Luck finds the critical blow and the lock's give. Skyrim's own actor values, as its Fortify enchantments
    /// use them (the PowerModifier values are percentages).</summary>
    public static readonly (int Attribute, ActorValue Av, float PerPoint)[] AttributeEffects =
    [
        (GameLevelling.Strength, ActorValue.CarryWeight, 2), (GameLevelling.Strength, ActorValue.OneHandedPowerModifier, 0.3f),
        (GameLevelling.Strength, ActorValue.TwoHandedPowerModifier, 0.3f), (GameLevelling.Intelligence, ActorValue.Magicka, 1),
        (GameLevelling.Willpower, ActorValue.MagickaRateMult, 0.5f), (GameLevelling.Willpower, ActorValue.ResistMagic, 0.1f),
        (GameLevelling.Agility, ActorValue.MarksmanPowerModifier, 0.5f), (GameLevelling.Agility, ActorValue.SneakingPowerModifier, 0.3f),
        (GameLevelling.Speed, ActorValue.SpeedMult, 0.4f), (GameLevelling.Endurance, ActorValue.Health, 0.5f),
        (GameLevelling.Endurance, ActorValue.HealRateMult, 0.5f), (GameLevelling.Personality, ActorValue.SpeechcraftPowerModifier, 0.5f),
        (GameLevelling.Luck, ActorValue.CriticalChance, 0.1f), (GameLevelling.Luck, ActorValue.LockpickingPowerModifier, 0.2f),
        (GameLevelling.Luck, ActorValue.PickpocketPowerModifier, 0.2f),
    ];

    /// <summary>The skills whose rises a level counts: Skyrim's eighteen, then the progression's (as converted).</summary>
    public static string[] CountedSkills(IReadOnlyList<SkillDef> skills) => [.. GameLevelling.SkyrimSkills, .. skills.Select(s => s.Id)];

    /// <summary>The Skyrim races the games' race tables are for, with their vampire forms (same attributes).</summary>
    public static readonly string[] Races = ["NordRace", "DarkElfRace", "HighElfRace", "WoodElfRace", "BretonRace", "ImperialRace", "KhajiitRace",
        "ArgonianRace", "OrcRace", "RedguardRace"];

    public sealed record ClassEntry(int Game, GameClass Class, Perk Perk, string Text, FormList Forbidden, string ForbiddenWords, float[] SkillRates, int[] SkillBonus);

    public sealed class Built
    {
        public List<GameLevelling> Games = [];
        public List<ClassEntry> Classes = [];
        /// <summary>The hidden ability: for each <see cref="AttributeEffects"/> an up and a down effect, then the
        /// level-up health.</summary>
        public Spell Attributes = null!;
        /// <summary>Each web sign's game versions: the plugin and FormID of the form list of its spells, and a line
        /// saying what it is.</summary>
        public List<(int Sign, string Plugin, uint Id, string Line)> SignLists = [];
        /// <summary>The game whose rule a character without a class follows (Oblivion's, else Morrowind's, else
        /// Daggerfall's; -1 none converted).</summary>
        public int DefaultGame = -1;
        public Spell? AcuteHearing;
        /// <summary>Each owned game's version of each race (<see cref="Races"/> index): its spells' form list.</summary>
        public List<(int Race, string Plugin, uint Id)> RaceLists = [];
        /// <summary>For each game and race (game x 10 + race), an ability of its skill bonuses to Skyrim's skills.</summary>
        public Spell?[] RaceSkillAbility = [];
        /// <summary>For each game and race, its bonuses to the progression's skills.</summary>
        public int[] RaceSkillBonus = [];
        /// <summary>Daggerfall's racial abilities, a perk per race (null none), the divisor of its level-growing weapon
        /// damage (0 none) and that entry's index in the perk.</summary>
        public Perk?[] DfRacePerks = new Perk?[10];
        public int[] DfRaceDivisor = new int[10];
        public int[] DfRaceEntry = new int[10];
        /// <summary>The races that climb better (Daggerfall's Khajiit) and by how much.</summary>
        public List<(string Race, int Bonus)> ClimbBonus = [];
        /// <summary>What each race is in the owned games, line by line (the Character view).</summary>
        public List<string>[] RaceText = [.. Enumerable.Range(0, 10).Select(_ => new List<string>())];
    }

    public static Built Build(SkyrimMod mod, ISkyrimModGetter skyrim, ProgressionPerks perks, string dir, IReadOnlyList<SkillDef> skills, Action<string>? log)
    {
        var built = new Built { Games = GameLevelling.Converted(dir) };
        if (built.Games.Count == 0) return built;
        built.DefaultGame = new[] { "OB", "MW", "DF" }.Select(g => built.Games.FindIndex(x => x.Game == g)).FirstOrDefault(i => i >= 0, -1);

        // The attributes' hidden ability (magnitudes set by TMPassives).
        var fx = new List<PerkFx>();
        foreach (var (_, av, _) in AttributeEffects) { fx.Add(PerkFx.Raise(av, 0)); fx.Add(PerkFx.Lower(av, 0)); }
        // Then each level's health beside Skyrim's own +10 (more or less by Endurance against the game's average).
        fx.Add(PerkFx.Raise(ActorValue.Health, 0));
        fx.Add(PerkFx.Lower(ActorValue.Health, 0));
        built.Attributes = perks.Ability("TMAttributes", "Attributes", fx, hidden: true);

        // Acute Hearing (a Daggerfall advantage): Skyrim's own Detect Life (enemies), always on.
        if (built.Games.Any(g => g.Game == "DF")) built.AcuteHearing = AcuteHearing(mod, skyrim, log);

        var counted = CountedSkills(skills);
        for (int g = 0; g < built.Games.Count; g++)
        {
            var game = built.Games[g];
            foreach (var c in game.Classes)
                built.Classes.Add(ClassPerk(mod, perks, game, g, c, skills, built.AcuteHearing));
            // The birthsigns: the game plugin's form lists, found by EditorID.
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, game.Plugin), SkyrimRelease.SkyrimSE);
            var lists = plugin.FormLists.Where(l => l.EditorID is not null).GroupBy(l => l.EditorID!).ToDictionary(x => x.Key, x => x.First());
            foreach (var s in game.Signs)
            {
                if (!lists.TryGetValue(s.SpellList, out var list)) { log?.Invoke($"levelling: {game.Plugin} has no {s.SpellList} ({s.Name})"); continue; }
                string what = string.IsNullOrWhiteSpace(s.Description) ? "" : ": " + s.Description.Trim();
                built.SignLists.Add((s.Sign, game.Plugin, list.FormKey.ID, $"{GameName(game.Game)}'s {s.Name}{what}"));
            }
            // The game's version of each race: its spells (named from the converted plugin) and its skill bonuses.
            var spellNames = plugin.Spells.ToDictionary(sp => sp.FormKey, sp => sp.Name?.String ?? sp.EditorID ?? "");
            for (int r = 0; r < Races.Length; r++)
            {
                var lines = new List<string>();
                if (game.RaceSpells.TryGetValue(Races[r], out var edid) && lists.TryGetValue(edid, out var list))
                {
                    built.RaceLists.Add((r, game.Plugin, list.FormKey.ID));
                    var names = list.Items.Select(i => spellNames.GetValueOrDefault(i.FormKey, "")).Where(n => n.Length > 0).ToList();
                    if (names.Count > 0) lines.Add(string.Join(", ", names));
                }
                if (game.RaceSkills.TryGetValue(Races[r], out var bonus) && bonus.Count > 0)
                    lines.Add("skills " + string.Join(", ", bonus.Select(b => $"{SkillName(b.Key)} +{b.Value}")) + " (with its rules)");
                if (lines.Count > 0) built.RaceText[r].Add($"{GameName(game.Game)}: {string.Join("; ", lines)}");
            }
        }
        // Each game's race skill bonuses: an ability per game and race for Skyrim's skills, numbers for the progression's.
        built.RaceSkillAbility = new Spell?[built.Games.Count * 10];
        built.RaceSkillBonus = new int[built.Games.Count * 10 * skills.Count];
        for (int g = 0; g < built.Games.Count; g++)
            for (int r = 0; r < Races.Length; r++)
            {
                if (!built.Games[g].RaceSkills.TryGetValue(Races[r], out var bonus) || bonus.Count == 0) continue;
                var fortify = bonus.Where(b => GameLevelling.SkyrimSkills.Contains(b.Key)).Select(b => PerkFx.Raise(Enum.Parse<ActorValue>(b.Key), b.Value)).ToList();
                if (fortify.Count > 0)
                    built.RaceSkillAbility[g * 10 + r] = perks.Ability($"TMRace{built.Games[g].Game}{Races[r].Replace("Race", "")}Skills",
                        $"{Races[r].Replace("Race", "")} ({GameName(built.Games[g].Game)})", fortify);
                for (int k = 0; k < skills.Count; k++) built.RaceSkillBonus[(g * 10 + r) * skills.Count + k] = bonus.GetValueOrDefault(skills[k].Id);
            }
        // Daggerfall's racial abilities (DfLevelling.Races).
        if (built.Games.Any(g => g.Game == "DF"))
            foreach (var race in DfLevelling.Races)
            {
                int r = Array.IndexOf(Races, race.Race);
                if (r < 0) continue;
                built.RaceText[r].Add($"Daggerfall: {race.Text}");
                if (race.ClimbBonus > 0) built.ClimbBonus.Add((race.Race, race.ClimbBonus));
                var raceFx = new List<PerkFx>();
                if (race.Resist is { } av) raceFx.Add(PerkFx.Raise(Enum.Parse<ActorValue>(av), race.ResistAmount));
                if (race.ParalysisImmune && perks.HasKeyword("MagicParalysis"))
                    raceFx.Add(PerkFx.Times(EP.ModIncomingSpellMagnitude, 0, new Cond(2, "keyword", "MagicParalysis")));
                if (race.DamageDivisor > 0)
                {
                    // Flat damage added to each hit (Daggerfall's level / 3 or / 4 points; TMPassives sets it each level).
                    var notFists = new Cond(0, "fists", Op: CompareOperator.NotEqualTo, Value: 0);
                    Cond[] conds = race.Weapons switch
                    {
                        "bow" => [new Cond(1, "keyword", "WeapTypeBow")],
                        "melee" => [notFists, new Cond(1, "keyword", "WeapTypeBow", Value: 0)],
                        _ => [notFists],
                    };
                    raceFx.Add(PerkFx.Plus(EP.ModAttackDamage, 0, conds));
                    built.DfRaceDivisor[r] = race.DamageDivisor;
                    // The perk's own ability (for actor values) comes first, then its entry points in order.
                    built.DfRaceEntry[r] = raceFx.Any(f => f.Av is not null) ? raceFx.Count(f => f.Entry is not null) : raceFx.Count(f => f.Entry is not null) - 1;
                }
                if (raceFx.Count > 0)
                    built.DfRacePerks[r] = perks.Perk($"TMRaceDF{race.Name.Replace(" ", "")}", $"{race.Name} (Daggerfall)", race.Text, raceFx);
            }
        log?.Invoke($"levelling: {built.Games.Count} games ({string.Join(", ", built.Games.Select(g => $"{GameName(g.Game)} {g.Classes.Count} classes, {g.Signs.Count} signs"))}); "
            + $"{built.SignLists.Count} sign versions on the stones; {counted.Length} skills counted at each level");
        return built;
    }

    public static string GameName(string game) => game switch { "DF" => "Daggerfall", "MW" => "Morrowind", "OB" => "Oblivion", _ => game };

    static string SkillName(string id) => id.StartsWith("DF:") ? id[3..] : id switch
    {
        "OneHanded" => "One-Handed", "TwoHanded" => "Two-Handed", "HeavyArmor" => "Heavy Armor", "LightArmor" => "Light Armor",
        "HandToHand" => "Hand-to-Hand", "MediumArmor" => "Medium Armor", "CriticalStrike" => "Critical Strike", _ => id,
    };

    /// <summary>A class's perk and what TMPassives and TMSkills need of it.</summary>
    static ClassEntry ClassPerk(SkyrimMod mod, ProgressionPerks perks, GameLevelling game, int g, GameClass c, IReadOnlyList<SkillDef> skills, Spell? acuteHearing)
    {
        var fx = new List<PerkFx>();
        var lines = new List<string>();
        bool df = game.Game == "DF";
        int Tier(string id) => (c.Primary ?? []).Contains(id) ? 3 : c.Major.Contains(id) ? 2 : c.Minor.Contains(id) ? 1 : 0;
        bool Special(string id) => c.Specialization >= 0 && game.Specialization.TryGetValue(id, out int s) && s == c.Specialization;
        float Rate(string id) => df ? 1 / Math.Max(0.05f, c.Advancement)
            : Tier(id) switch { 2 => game.MajorRate, 1 => game.MinorRate, _ => game.MiscRate } * (Special(id) ? game.SpecialRate : 1);
        int Bonus(string id) => Math.Max(0, Tier(id) switch { 3 => game.PrimaryStart, 2 => game.MajorStart, 1 => game.MinorStart, _ => 15 } - 15) + (Special(id) ? game.SpecialBonus : 0);

        foreach (var id in GameLevelling.SkyrimSkills)
        {
            var av = Enum.Parse<ActorValue>(id);
            float rate = Rate(id);
            if (MathF.Abs(rate - 1) > 0.005f) fx.Add(PerkFx.Times(EP.ModSkillUse, MathF.Round(rate, 3), new Cond(0, "advskill", id)));
            if (Bonus(id) is > 0 and var b) fx.Add(PerkFx.Raise(av, b));
        }
        var skillRates = skills.Select(s => Rate(s.Id)).ToArray();
        var skillBonus = skills.Select(s => Bonus(s.Id)).ToArray();

        string List(IEnumerable<string> ids) => string.Join(", ", ids.Select(SkillName));
        if (c.Primary is { Length: > 0 }) lines.Add($"Primary: {List(c.Primary)}.");
        lines.Add($"Major: {List(c.Major)}.");
        if (c.Minor.Length > 0) lines.Add($"Minor: {List(c.Minor)}.");
        if (df) lines.Add($"Skills rise x{1 / Math.Max(0.05f, c.Advancement):0.##} as fast (advancement x{c.Advancement}); {c.HitPointsPerLevel} hit points a level.");
        else
        {
            string spec = c.Specialization switch { 0 => "Combat", 1 => "Magic", 2 => "Stealth", _ => "" };
            lines.Add($"Specialization: {spec}. Major skills rise x{game.MajorRate * 1:0.##} as fast{(MathF.Abs(game.MiscRate - 1) > 0.005f ? $", others x{game.MiscRate:0.##}" : "")}, "
                + $"{spec} skills x{game.SpecialRate:0.##} more; favoured {string.Join(" and ", c.Favored.Select(a => GameLevelling.Attributes[a]))} +{game.FavoredBonus}.");
        }

        var forbidden = new FormList(mod) { EditorID = "TMClassForbid" + game.Game + Regex.Replace(c.Id, "[^A-Za-z0-9]", "") };
        string words = "";
        if (c.Traits is { } t)
        {
            var (traitFx, traitLines, kw, w) = Traits(perks, t, acuteHearing);
            fx.AddRange(traitFx);
            lines.AddRange(traitLines);
            foreach (var k in kw) forbidden.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(k));
            words = w;
        }
        mod.FormLists.Add(forbidden);
        string text = string.Join(" ", lines);
        var perk = perks.Perk($"TMClass{game.Game}{Regex.Replace(c.Id, "[^A-Za-z0-9]", "")}", $"{c.Name} ({GameName(game.Game)})",
            (string.IsNullOrWhiteSpace(c.Description) ? "" : c.Description.Trim() + " ") + text, fx);
        if (c.Traits is { AcuteHearing: true } && acuteHearing is not null)
            perk.Effects.Add(new PerkAbilityEffect { Ability = acuteHearing.ToLink<ISpellGetter>(), Rank = 0, Priority = 0 });
        return new ClassEntry(g, c, perk, text, forbidden, words, skillRates, skillBonus);
    }

    /// <summary>Daggerfall's advantages and disadvantages in Skyrim's terms (Daggerfall Unity's numbers where it has
    /// them): tolerances as resistances (resistant +25, immune +50, low tolerance -25, critical weakness -50: its saving
    /// throw adjustments), spell points as magicka (x1 is the average; each step of Intelligence's multiple 40 magicka),
    /// magery by light (spells half as strong, or ten times dearer, outside by day; or inside and by night), rapid
    /// healing, regeneration, spell absorption (by day outside, inside or at night, in water, always), acute hearing
    /// (Detect Life), athleticism (its 0.9 fatigue), adrenaline rush (below an eighth of health), no magicka
    /// regeneration, bonus to hit and phobia by kind of foe, expertise with weapon groups, and the forbidden weapons,
    /// armour, shields and materials (a form list of keywords and a list of words TMPassives unequips).</summary>
    static (List<PerkFx> Fx, List<string> Lines, List<FormKey> Forbidden, string Words) Traits(ProgressionPerks perks, DfTraits t, Spell? acuteHearing)
    {
        var fx = new List<PerkFx>();
        var lines = new List<string>();
        var forbidden = new List<FormKey>();
        var words = new List<string>();
        string[] effects = ["paralysis", "magic", "poison", "fire", "frost", "shock", "disease"];
        ActorValue?[] resist = [null, ActorValue.ResistMagic, ActorValue.PoisonResist, ActorValue.ResistFire, ActorValue.ResistFrost, ActorValue.ResistShock, ActorValue.ResistDisease];
        string[] tolName = ["", "Resistant to", "Immune to", "Low tolerance to", "Critically weak to"];
        int[] tolAmount = [0, 25, 50, -25, -50];
        for (int e = 0; e < 7; e++)
        {
            if (t.Tolerance[e] == 0) continue;
            int amount = tolAmount[t.Tolerance[e]];
            if (resist[e] is { } av) fx.Add(amount > 0 ? PerkFx.Raise(av, amount) : PerkFx.Lower(av, -amount));
            else if (perks.HasKeyword("MagicParalysis"))
                fx.Add(PerkFx.Times(EP.ModIncomingSpellMagnitude, 1 - amount / 100f, new Cond(2, "keyword", "MagicParalysis")));
            lines.Add($"{tolName[t.Tolerance[e]]} {effects[e]} ({(amount > 0 ? "+" : "")}{amount}%).");
        }
        if (MathF.Abs(t.SpellPoints - 1) > 0.01f)
        {
            float magicka = (t.SpellPoints - 1) * 40;
            fx.Add(magicka > 0 ? PerkFx.Raise(ActorValue.Magicka, magicka) : PerkFx.Lower(ActorValue.Magicka, -magicka));
            lines.Add($"Spell points {t.SpellPoints:0.##} x Intelligence ({(magicka > 0 ? "+" : "")}{magicka:0} magicka).");
        }
        // Light: outside between 6 and 20 hours; darkness: inside, or at night.
        Cond[] Light() => [new(0, "interior", Value: 0), new(0, "hour", Op: CompareOperator.GreaterThanOrEqualTo, Value: 6), new(0, "hour", Op: CompareOperator.LessThan, Value: 20)];
        Cond[] Dark() => [new(0, "interior", Value: 1, Or: true), new(0, "hour", Op: CompareOperator.LessThan, Value: 6, Or: true), new(0, "hour", Op: CompareOperator.GreaterThanOrEqualTo, Value: 20)];
        void Magery(int kind, Cond[] when, string where)
        {
            if (kind == 1) { fx.Add(PerkFx.Times(EP.ModSpellCost, 10, when)); lines.Add($"Unable to cast {where} (spells cost ten times as much)."); }
            else if (kind == 2) { fx.Add(PerkFx.Times(EP.ModSpellMagnitude, 0.5f, when)); lines.Add($"Spells half as strong {where}."); }
        }
        Magery(t.DarknessMagery, Light(), "in daylight");
        Magery(t.LightMagery, Dark(), "in darkness");
        Cond[]? When(int kind, bool water = false) => kind switch
        {
            1 => Light(), 2 => Dark(), 3 when water => [new(0, "swimming")], _ => [],
        };
        string WhenText(int kind, bool water = false) => kind switch { 1 => " in daylight", 2 => " in darkness", 3 when water => " in water", _ => "" };
        if (t.RapidHealing > 0)
        {
            fx.Add(PerkFx.Raise(ActorValue.HealRateMult, 100, When(t.RapidHealing)!));
            lines.Add($"Rapid healing{WhenText(t.RapidHealing)} (health regenerates twice as fast).");
        }
        if (t.Regeneration > 0)
        {
            int kind = t.Regeneration == 4 ? 4 : t.Regeneration;
            Cond[] when = kind == 4 ? [] : When(kind, water: kind == 3)!;
            fx.Add(PerkFx.Raise(ActorValue.HealRateMult, 300, when));
            fx.Add(PerkFx.Raise(ActorValue.CombatHealthRegenMultiply, 1, when));
            lines.Add($"Regenerates health{WhenText(kind, water: true)} (four times as fast, in combat too).");
        }
        if (t.Absorption > 0)
        {
            fx.Add(PerkFx.Raise(ActorValue.AbsorbChance, 50, When(t.Absorption)!));
            lines.Add($"Spell absorption{WhenText(t.Absorption)} (50%).");
        }
        if (t.AcuteHearing && acuteHearing is not null) lines.Add("Acute hearing (senses living foes nearby).");
        if (t.Athleticism)
        {
            fx.Add(PerkFx.Raise(ActorValue.StaminaRateMult, 10));
            fx.Add(PerkFx.Times(EP.ModPowerAttackStamina, 0.9f));
            lines.Add("Athleticism (tires a tenth less).");
        }
        if (t.AdrenalineRush)
        {
            fx.Add(PerkFx.Times(EP.ModAttackDamage, 1.25f, new Cond(0, "healthpct", Op: CompareOperator.LessThan, Value: 0.125f)));
            lines.Add("Adrenaline rush (25% more damage below an eighth of health).");
        }
        if (t.NoMagickaRegen) { fx.Add(PerkFx.Lower(ActorValue.MagickaRateMult, 100)); lines.Add("Magicka does not regenerate."); }
        if (t.SunDamage) lines.Add("Damaged by sunlight.");
        if (t.HolyDamage) lines.Add("Damaged in holy places.");
        string[] foes = ["undead", "daedra", "humanoids", "animals"];
        string[] foeKeywords = ["ActorTypeUndead", "ActorTypeDaedra", "ActorTypeNPC", "ActorTypeAnimal"];
        for (int k = 0; k < 4; k++)
        {
            if (t.Against[k] == 0) continue;
            var target = new Cond(2, "keyword", foeKeywords[k]);
            if (t.Against[k] > 0) { fx.Add(PerkFx.Times(EP.ModAttackDamage, 1.15f, target)); lines.Add($"Bonus to hit {foes[k]} (15% more damage)."); }
            else
            {
                fx.Add(PerkFx.Times(EP.ModAttackDamage, 0.85f, target));
                fx.Add(PerkFx.Times(EP.ModIncomingDamage, 1.15f, new Cond(1, "keyword", foeKeywords[k])));
                lines.Add($"Phobia of {foes[k]} (15% less damage to them, 15% more from them).");
            }
        }
        // Daggerfall's weapon groups: short blades, long blades, hand-to-hand, axes, blunt, missile.
        string[] groups = ["short blades", "long blades", "hand-to-hand", "axes", "blunt weapons", "missile weapons"];
        string[][] groupKeywords = [["WeapTypeDagger"], ["WeapTypeSword", "WeapTypeGreatsword"], [], ["WeapTypeWarAxe", "WeapTypeBattleaxe"],
            ["WeapTypeMace", "WeapTypeWarhammer"], ["WeapTypeBow"]];
        for (int k = 0; k < 6; k++)
        {
            if ((t.Expert & (1 << k)) != 0)
            {
                if (k == 2) fx.Add(PerkFx.Times(EP.ModAttackDamage, 1.15f, new Cond(0, "fists", Value: 0)));
                else foreach (var kw in groupKeywords[k]) fx.Add(PerkFx.Times(EP.ModAttackDamage, 1.15f, new Cond(1, "keyword", kw)));
                lines.Add($"Expertise in {groups[k]} (15% more damage).");
            }
            if ((t.ForbiddenWeapons & (1 << k)) != 0)
            {
                if (k == 2) fx.Add(PerkFx.Times(EP.ModAttackDamage, 0, new Cond(0, "fists", Value: 0)));
                foreach (var kw in groupKeywords[k]) if (perks.KeywordKey(kw) is { } key) forbidden.Add(key);
                lines.Add($"Forbidden: {groups[k]}.");
            }
        }
        // Armour: leather (Skyrim's leather and hide), chain (Daggerfall's chain is Imperial heavy; scaled), plate (heavy).
        string[] armor = ["leather", "chain", "plate"];
        string[][] armorKeywords = [["ArmorMaterialLeather", "ArmorMaterialHide"], ["ArmorMaterialImperialHeavy", "ArmorMaterialScaled"], ["ArmorHeavy"]];
        for (int k = 0; k < 3; k++)
            if ((t.ForbiddenArmor & (1 << k)) != 0)
            {
                foreach (var kw in armorKeywords[k]) if (perks.KeywordKey(kw) is { } key) forbidden.Add(key);
                lines.Add($"Forbidden: {armor[k]} armour.");
            }
        // Shields: bucklers and round shields light, kite and tower heavy (Skyrim's shields have no shapes).
        if ((t.ForbiddenShields & 0x0f) != 0)
        {
            bool light = (t.ForbiddenShields & 3) != 0, heavy = (t.ForbiddenShields & 12) != 0;
            words.Add(light && heavy ? "shield:any" : light ? "shield:light" : "shield:heavy");
            lines.Add($"Forbidden: {(light && heavy ? "" : light ? "light " : "heavy ")}shields.");
        }
        string[] materials = ["Iron", "Steel", "Silver", "Elven", "Dwarven", "Mithril", "Adamantium", "Ebony", "Orcish", "Daedric"];
        for (int k = 0; k < 10; k++)
            if ((t.ForbiddenMaterials & (1 << k)) != 0)
            {
                foreach (var kw in new[] { "ArmorMaterial" + materials[k], "WeapMaterial" + materials[k] })
                    if (perks.KeywordKey(kw) is { } key) forbidden.Add(key);
                // Mithril, Adamantium and Silver armour are Daggerfall's own, tinted Skyrim steel: known by their names.
                words.Add(materials[k]);
                lines.Add($"Forbidden: {materials[k].ToLowerInvariant()}.");
            }
        return (fx, lines, forbidden, string.Join("|", words));
    }

    /// <summary>A constant Detect Life (enemies): Skyrim's own Detect Life effects (exterior and interior), on always,
    /// with the area of vanilla's Detect Life spell.</summary>
    static Spell? AcuteHearing(SkyrimMod mod, ISkyrimModGetter skyrim, Action<string>? log)
    {
        var exterior = skyrim.MagicEffects.FirstOrDefault(e => e.EditorID == "DetectLifeEnemyExteriorSelfFF");
        var interior = skyrim.MagicEffects.FirstOrDefault(e => e.EditorID == "DetectLifeEnemyInteriorSelfFF");
        if (exterior is null || interior is null) { log?.Invoke("levelling: Skyrim.esm has no DetectLifeEnemy effects (Acute Hearing left out)"); return null; }
        var vanillaSpell = skyrim.Spells.FirstOrDefault(s => s.Effects.Any(e => e.BaseEffect.FormKey == exterior.FormKey));
        var data = vanillaSpell?.Effects.First(e => e.BaseEffect.FormKey == exterior.FormKey).Data;
        var spell = new Spell(mod)
        {
            EditorID = "TMClassAcuteHearing", Name = "Acute Hearing", ObjectBounds = new ObjectBounds(), Type = SpellType.Ability,
            CastType = CastType.ConstantEffect, TargetType = TargetType.Self, Description = "", Effects = [],
        };
        foreach (var (vanilla, inside) in new[] { (exterior, 0f), (interior, 1f) })
        {
            var m = vanilla.Duplicate(mod.GetNextFormKey());
            m.EditorID = "TMClassAcuteHearing" + (inside == 1 ? "Interior" : "Exterior") + "Effect";
            m.CastType = CastType.ConstantEffect;
            m.Flags |= MagicEffect.Flag.HideInUI;
            mod.MagicEffects.Add(m);
            var effect = new Effect
            {
                BaseEffect = m.ToNullableLink<IMagicEffectGetter>(),
                Data = new EffectData { Magnitude = data?.Magnitude ?? 0, Area = data?.Area ?? 0, Duration = 0 },
            };
            effect.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = inside, Data = new IsInInteriorConditionData() });
            spell.Effects.Add(effect);
        }
        mod.Spells.Add(spell);
        return spell;
    }
}
