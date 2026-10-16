using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using EP = Mutagen.Bethesda.Skyrim.APerkEntryPointEffect.EntryType;

namespace TamrielMerge.Skyrim.Progression;

/// <summary>An actor value a skill's ability changes by <paramref name="PerLevel"/> a skill level.</summary>
public sealed record SkillAv(ActorValue Av, float PerLevel, params Cond[] Conds);
/// <summary>An entry point of a skill's own (hidden) perk: its value is <paramref name="Base"/> + <paramref name="PerLevel"/> x
/// the level, set by TMSkills with SKSE's Perk.SetNthEntryValue.</summary>
public sealed record SkillEntry(EP Entry, float Base, float PerLevel, params Cond[] Conds);
/// <summary>A rank of a skill's perk: the skill level it needs, what it says and does.</summary>
public sealed record TreeRank(int Level, string Description, params PerkFx[] Fx);
public sealed record TreePerk(string Name, params TreeRank[] Ranks);

/// <summary>A new skill (docs/progression.md "New skills"): the games it is from, how it rises and what its level does.
/// <paramref name="Use"/> is the experience one use gives (TMSkills multiplies it by the size of the use: a spell's cost,
/// the distance run, the gold changing hands); Skyrim's curve: a level L needs ImproveMult x L^1.95 + ImproveOffset.</summary>
public sealed record SkillDef(string Name, string[] Games, string Rises, string Effect, float Use, SkillAv[] Avs, SkillEntry[] Entries, TreePerk[] Tree,
    float ImproveMult = 2, float ImproveOffset = 0)
{
    public string Id => Regex.Replace(Name, "[^A-Za-z]+([a-z]?)", m => m.Groups[1].Value.ToUpperInvariant());
}

/// <summary>
/// The new skills: the ones Skyrim lacks from the games converted (Daggerfall, Morrowind, Oblivion). Each is a level
/// global (TMSkill&lt;Id&gt;Level, starting at 15 like Skyrim's untrained skills) and its experience toward the next
/// (TMSkill&lt;Id&gt;XP), an ability whose actor values grow with the level, a hidden perk whose entry points grow with it,
/// and a small perk tree of real perks taken with Skyrim's perk points, each rank gated by the skill's level. The
/// spells, spears and armour some skills are about are found in the converted plugins (their own records) and listed
/// by plugin and FormID; TMSkills puts them in this plugin's form lists at run time.
/// </summary>
public static class ProgressionSkills
{
    public const int StartLevel = 15;

    static TreeRank R(int level, string description, params PerkFx[] fx) => new(level, description, fx);
    static Cond L(string list) => new(1, "list", list);
    static Cond Fists => new(0, "fists", Op: CompareOperator.EqualTo, Value: 0);

    public static readonly SkillDef[] All =
    [
        new("Mysticism", ["DF", "MW", "OB"], "casting Mysticism spells", "Mysticism spells cost up to 40% less", 2,
            [], [new(EP.ModSpellCost, 1, -0.004f, L("Mysticism"))],
            [
                new("Mystic Adept", R(25, "Mysticism spells cost 15% less.", PerkFx.Times(EP.ModSpellCost, 0.85f, L("Mysticism"))),
                                    R(50, "Mysticism spells cost another 15% less.", PerkFx.Times(EP.ModSpellCost, 0.85f, L("Mysticism")))),
                new("Spell Ward", R(40, "A 10% chance to absorb a hostile spell's magicka.", PerkFx.Raise(ActorValue.AbsorbChance, 10))),
                new("Far Reaching", R(70, "Mysticism spells last half as long again.", PerkFx.Times(EP.ModSpellDuration, 1.5f, L("Mysticism")))),
            ]),
        new("Athletics", ["DF", "MW", "OB"], "running, sprinting and swimming", "up to 20% faster and 50% quicker stamina regeneration", 1,
            [new(ActorValue.SpeedMult, 0.2f), new(ActorValue.StaminaRateMult, 0.5f)], [],
            [
                new("Long Stride", R(25, "Stamina regenerates 25% faster.", PerkFx.Raise(ActorValue.StaminaRateMult, 25))),
                new("Pack Runner", R(50, "+25 carry weight.", PerkFx.Raise(ActorValue.CarryWeight, 25))),
                new("Fleet Foot", R(75, "10% faster.", PerkFx.Raise(ActorValue.SpeedMult, 10))),
            ]),
        new("Acrobatics", ["DF", "MW", "OB"], "jumping and falling", "jump higher and take up to 50% less damage from falls", 15,
            [new(ActorValue.JumpingBonus, 0.5f)], [new(EP.ModFallingDamage, 1, -0.005f)],
            [
                new("Cushioned", R(25, "A quarter less damage from falls.", PerkFx.Times(EP.ModFallingDamage, 0.75f)),
                                 R(50, "A third less again.", PerkFx.Times(EP.ModFallingDamage, 0.67f))),
                new("Spring Heel", R(60, "Jump much higher.", PerkFx.Raise(ActorValue.JumpingBonus, 30))),
            ]),
        new("Hand-to-Hand", ["DF", "MW", "OB"], "unarmed hits", "up to +20 unarmed damage", 25,
            [new(ActorValue.UnarmedDamage, 0.2f)], [],
            [
                new("Iron Fists", R(25, "+5 unarmed damage.", PerkFx.Raise(ActorValue.UnarmedDamage, 5)),
                                  R(50, "+5 more unarmed damage.", PerkFx.Raise(ActorValue.UnarmedDamage, 5))),
                new("Brawler's Stance", R(40, "Power attacks with fists cost 25% less stamina.", PerkFx.Times(EP.ModPowerAttackStamina, 0.75f, Fists))),
                new("Staggering Blows", R(75, "Fists stagger half as much again.", PerkFx.Times(EP.ModTargetStagger, 1.5f, Fists))),
            ]),
        new("Spear", ["MW"], "hits with spears and halberds", "up to 30% more damage with spears", 30,
            [], [new(EP.ModAttackDamage, 1, 0.003f, L("Spears"))],
            [
                new("Spearman", R(25, "Spears do 10% more damage.", PerkFx.Times(EP.ModAttackDamage, 1.1f, L("Spears"))),
                                R(50, "Spears do another 10% more.", PerkFx.Times(EP.ModAttackDamage, 1.1f, L("Spears")))),
                new("Impaling Thrust", R(50, "Power attacks with spears do 25% more damage.", PerkFx.Times(EP.ModPowerAttackDamage, 1.25f, L("Spears")))),
                new("Long Reach", R(75, "Power attacks with spears cost 25% less stamina.", PerkFx.Times(EP.ModPowerAttackStamina, 0.75f, L("Spears")))),
            ]),
        new("Medium Armor", ["MW"], "being struck while wearing medium armour", "medium armour's rating up to 40% higher", 25,
            [], [new(EP.ModArmorRating, 1, 0.004f, L("MediumArmor"))],
            [
                new("Medium Fit", R(25, "Medium armour's rating is 10% higher.", PerkFx.Times(EP.ModArmorRating, 1.1f, L("MediumArmor"))),
                                  R(50, "Another 10% higher.", PerkFx.Times(EP.ModArmorRating, 1.1f, L("MediumArmor")))),
                new("Balanced Load", R(50, "Medium armour weighs half as much.", PerkFx.Times(EP.ModArmorWeight, 0.5f, L("MediumArmor")))),
                new("Steady Guard", R(75, "Half the stagger when wearing three or more pieces of medium armour.",
                    PerkFx.Times(EP.ModIncomingStagger, 0.5f, new Cond(0, "global", "TMMediumArmorWorn", CompareOperator.GreaterThanOrEqualTo, 3)))),
            ]),
        new("Mercantile", ["DF", "MW", "OB"], "buying and selling", "prices up to 20% better", 0.36f,
            [], [new(EP.ModBuyPrices, 1, -0.002f), new(EP.ModSellPrices, 1, 0.002f)],
            [
                new("Haggler", R(25, "Buy 5% cheaper and sell 5% dearer.", PerkFx.Times(EP.ModBuyPrices, 0.95f), PerkFx.Times(EP.ModSellPrices, 1.05f)),
                               R(50, "Another 5% each way.", PerkFx.Times(EP.ModBuyPrices, 0.95f), PerkFx.Times(EP.ModSellPrices, 1.05f))),
                new("Fence's Friend", R(60, "Any merchant buys stolen goods.", PerkFx.Raise(ActorValue.BypassVendorStolenCheck, 1))),
            ]),
        new("Unarmored", ["MW"], "being struck with no armour on", "up to +150 armour rating while wearing no armour", 25,
            [new(ActorValue.DamageResist, 1.5f, new Cond(0, "global", "TMUnarmored"))], [],
            [
                new("Unarmored Defense", R(25, "+40 armour rating without armour.", PerkFx.Raise(ActorValue.DamageResist, 40, new Cond(0, "global", "TMUnarmored"))),
                                         R(50, "+40 more.", PerkFx.Raise(ActorValue.DamageResist, 40, new Cond(0, "global", "TMUnarmored")))),
                new("Evasion", R(75, "10% less damage from attacks without armour.", PerkFx.Times(EP.ModIncomingDamage, 0.9f, new Cond(0, "global", "TMUnarmored")))),
            ]),
        new("Thaumaturgy", ["DF"], "casting Thaumaturgy spells", "Thaumaturgy spells cost up to 40% less", 2,
            [], [new(EP.ModSpellCost, 1, -0.004f, L("Thaumaturgy"))],
            [
                new("Thaumaturge", R(25, "Thaumaturgy spells cost 15% less.", PerkFx.Times(EP.ModSpellCost, 0.85f, L("Thaumaturgy"))),
                                   R(50, "Another 15% less.", PerkFx.Times(EP.ModSpellCost, 0.85f, L("Thaumaturgy")))),
                new("Enduring Workings", R(50, "Thaumaturgy spells last half as long again.", PerkFx.Times(EP.ModSpellDuration, 1.5f, L("Thaumaturgy")))),
                new("Arcane Ward", R(75, "+10% magic resistance.", PerkFx.Raise(ActorValue.ResistMagic, 10))),
            ]),
        new("Swimming", ["DF"], "swimming", "swim up to 50% faster and hold your breath up to twice as long", 1.5f,
            [new(ActorValue.SpeedMult, 0.5f, new Cond(0, "swimming"))], [],
            [
                new("Strong Strokes", R(25, "Stamina regenerates half as fast again while swimming.", PerkFx.Raise(ActorValue.StaminaRateMult, 50, new Cond(0, "swimming")))),
                new("Cold Water", R(50, "+25% frost resistance.", PerkFx.Raise(ActorValue.ResistFrost, 25))),
                new("Gills", R(75, "Breathe under water.", PerkFx.Raise(ActorValue.WaterBreathing, 1))),
            ]),
        new("Backstabbing", ["DF"], "sneak attacks", "sneak attacks do up to twice the damage", 60,
            [], [new(EP.ModSneakAttackMult, 1, 0.01f)],
            [
                new("Knife in the Dark", R(25, "Sneak attacks do 25% more damage.", PerkFx.Times(EP.ModSneakAttackMult, 1.25f)),
                                         R(50, "Another 25% more.", PerkFx.Times(EP.ModSneakAttackMult, 1.25f))),
                new("Shadow Step", R(75, "20% harder to detect while sneaking.", PerkFx.Times(EP.ModDetectionSneakSkill, 1.2f))),
            ]),
        new("Critical Strike", ["DF"], "critical hits", "a hit has a level/4 % chance to strike critically for extra damage", 80,
            [], [],
            [
                new("Keen Eye", R(25, "+10% chance of Skyrim's critical hits.", PerkFx.Plus(EP.CalculateMyCriticalHitChance, 10)),
                                R(50, "+10% more.", PerkFx.Plus(EP.CalculateMyCriticalHitChance, 10))),
                new("Deadly Precision", R(75, "Critical hits do half as much damage again.", PerkFx.Times(EP.CalculateMyCriticalHitDamage, 1.5f))),
            ]),
        new("Medical", ["DF"], "recovering health", "health regenerates up to 50% faster", 0.5f,
            [new(ActorValue.HealRateMult, 0.5f)], [],
            [
                new("Field Dressing", R(25, "Restoration spells are 15% stronger.", PerkFx.Times(EP.ModSpellMagnitude, 1.15f, new Cond(1, "skill", "Restoration"))),
                                      R(50, "Another 15% stronger.", PerkFx.Times(EP.ModSpellMagnitude, 1.15f, new Cond(1, "skill", "Restoration")))),
                new("Resilience", R(75, "+50% disease and +25% poison resistance.", PerkFx.Raise(ActorValue.ResistDisease, 50), PerkFx.Raise(ActorValue.PoisonResist, 25))),
            ]),
    ];

    /// <summary>The form lists TMSkills fills at run time, in its order.</summary>
    public static readonly string[] ListNames = ["Mysticism", "Thaumaturgy", "Spears", "MediumArmor"];

    static readonly Dictionary<string, string> GameOf = new() { ["Daggerfall.esm"] = "DF", ["Morrowind.esm"] = "MW", ["Oblivion.esm"] = "OB" };

    /// <summary>The skills of the games found in <paramref name="dir"/>.</summary>
    public static List<SkillDef> Converted(string dir) =>
        [.. All.Where(s => s.Games.Any(g => GameOf.Any(kv => kv.Value == g && File.Exists(Path.Combine(dir, kv.Key)))))];

    // Mysticism's and Thaumaturgy's effects in the converted plugins, by the magic effect's EditorID (the converters'
    // "{prefix}Effect{Key}..."). Morrowind's and Oblivion's Mysticism: absorbing, detecting, dispelling, Mark and Recall,
    // the Interventions, reflecting, soul trapping, spell absorption and telekinesis (their converters give these
    // Alteration as Skyrim's school). Daggerfall's (Daggerfall Unity's MagicSkill): Mysticism the dispels, Recall
    // (Teleport), Soul Trap, Spell Reflection, Comprehend Languages and Lock; Thaumaturgy Levitate, Open, Water Walking,
    // Disintegrate and Spell Resistance.
    static readonly Regex TesMysticism = new("^(MW|OB)Effect(Absorb[A-Za-z]+?|AlmsiviIntervention|DivineIntervention|DetectLife|Dispel|Mark|Recall|Reflect|Soultrap|SpellAbsorption|Telekinesis)(Fired|Constant|Strike)");
    static readonly Regex DfMysticism = new("^DFEffect(BanishDaedra|DispelMagic|DispelUndead|Recall|SoulTrap|SpellReflection|Tongues|Lock)[A-Z]");
    static readonly Regex DfThaumaturgy = new("^DFEffect(Levitate|Open|WaterWalking|DisintegrateMagic|SpellResistance)[A-Z]");
    static readonly Regex SpearName = new(@"\b(spear|halberd)\b", RegexOptions.IgnoreCase);

    /// <summary>Morrowind's armour weights by piece (iHelmWeight...), the slot each converted piece takes, and its weight
    /// classes: light up to 0.6 of the piece's weight (fLightMaxMod), medium up to 0.9 (fMedMaxMod).</summary>
    static float? MwPieceWeight(BipedObjectFlag f) =>
        f.HasFlag(BipedObjectFlag.Body) ? 30 : f.HasFlag(BipedObjectFlag.Shield) ? 15 : f.HasFlag(BipedObjectFlag.Feet) ? 20
        : f.HasFlag(BipedObjectFlag.Head) || f.HasFlag(BipedObjectFlag.Hair) || f.HasFlag(BipedObjectFlag.Circlet) ? 5
        : f.HasFlag(BipedObjectFlag.Hands) || f.HasFlag((BipedObjectFlag)(1u << 30)) ? 5
        : f.HasFlag((BipedObjectFlag)(1u << 27)) || f.HasFlag((BipedObjectFlag)(1u << 28)) ? 10
        : f.HasFlag((BipedObjectFlag)(1u << 23)) ? 15 : null;

    public sealed record Listed(string Plugin, uint Id, int List);

    /// <summary>The records the lists hold, found in the converted plugins.</summary>
    public static List<Listed> FindListed(string dir, List<SkillDef> skills, Action<string>? log = null)
    {
        var listed = new List<Listed>();
        bool Has(string id) => skills.Any(s => s.Id == id);
        foreach (var plugin in GameOf.Keys)
        {
            string path = Path.Combine(dir, plugin);
            if (!File.Exists(path)) continue;
            using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var mystic = new HashSet<FormKey>();
            var thaum = new HashSet<FormKey>();
            foreach (var e in m.MagicEffects.Where(e => e.FormKey.ModKey == m.ModKey && e.EditorID is not null))
            {
                if (TesMysticism.IsMatch(e.EditorID!) || DfMysticism.IsMatch(e.EditorID!)) mystic.Add(e.FormKey);
                else if (DfThaumaturgy.IsMatch(e.EditorID!)) thaum.Add(e.FormKey);
            }
            int before = listed.Count;
            foreach (var s in m.Spells.Where(s => s.FormKey.ModKey == m.ModKey && s.Type == SpellType.Spell))
            {
                if (Has("Mysticism") && s.Effects.Any(e => mystic.Contains(e.BaseEffect.FormKey))) listed.Add(new(plugin, s.FormKey.ID, 0));
                else if (Has("Thaumaturgy") && s.Effects.Any(e => thaum.Contains(e.BaseEffect.FormKey))) listed.Add(new(plugin, s.FormKey.ID, 1));
            }
            if (plugin == "Morrowind.esm")
            {
                if (Has("Spear"))
                    foreach (var w in m.Weapons.Where(w => w.FormKey.ModKey == m.ModKey && SpearName.IsMatch((w.Name?.String ?? "") + " " + w.EditorID)))
                        listed.Add(new(plugin, w.FormKey.ID, 2));
                if (Has("MediumArmor"))
                    foreach (var a in m.Armors.Where(a => a.FormKey.ModKey == m.ModKey && a.BodyTemplate is { } b && b.ArmorType != ArmorType.Clothing))
                        if (MwPieceWeight(a.BodyTemplate!.FirstPersonFlags) is { } full && a.Weight > full * 0.6f && a.Weight <= full * 0.9f)
                            listed.Add(new(plugin, a.FormKey.ID, 3));
            }
            log?.Invoke($"  {plugin}: {string.Join(", ", listed.Skip(before).GroupBy(l => l.List).Select(g => $"{g.Count()} {ListNames[g.Key]}"))}");
        }
        return listed;
    }

    /// <summary>What the build made for the skills, for TMSkills' properties and the menu.</summary>
    public sealed class Built
    {
        public List<SkillDef> Skills = [];
        public List<GlobalFloat> Levels = [], Xp = [];
        public List<Spell?> Abilities = [];
        public List<Perk?> EffectPerks = [];
        public List<(Perk Perk, int Skill, int Level, int After, string Name, int Rank, int Ranks, string Description)> Tree = [];
        public Dictionary<string, FormList> Lists = [];
        public Dictionary<string, FormKey> Globals = [];
        public Perk HitPerk = null!;
        public Spell HitSpell = null!;
        public MagicEffect HitEffect = null!;
    }

    /// <summary>The globals and lists the skills' conditions name (made before any perk).</summary>
    public static Built Prepare(SkyrimMod mod, List<SkillDef> skills)
    {
        var b = new Built { Skills = skills };
        foreach (var name in ListNames)
        {
            var list = new FormList(mod) { EditorID = $"TM{name}List" };
            mod.FormLists.Add(list);
            b.Lists[name] = list;
        }
        foreach (var name in new[] { "TMMediumArmorWorn", "TMUnarmored" })
        {
            var g = new GlobalShort(mod) { EditorID = name, Data = 0 };
            mod.Globals.Add(g);
            b.Globals[name] = g.FormKey;
        }
        foreach (var s in skills)
        {
            var level = new GlobalFloat(mod) { EditorID = $"TMSkill{s.Id}Level", Data = StartLevel };
            var xp = new GlobalFloat(mod) { EditorID = $"TMSkill{s.Id}XP", Data = 0 };
            mod.Globals.Add(level);
            mod.Globals.Add(xp);
            b.Levels.Add(level);
            b.Xp.Add(xp);
            b.Globals[level.EditorID] = level.FormKey;
        }
        return b;
    }

    /// <summary>Each skill's ability, effect perk and perk tree, and the hit perk (Apply Combat Hit Spell) through which
    /// TMSkills hears of the player's hits (unarmed, spears, sneak attacks, critical strikes).</summary>
    public static void Build(SkyrimMod mod, ISkyrimModGetter skyrim, ProgressionPerks perks, Built b)
    {
        for (int i = 0; i < b.Skills.Count; i++)
        {
            var s = b.Skills[i];
            b.Abilities.Add(s.Avs.Length == 0 ? null
                : perks.Ability($"TMSkill{s.Id}Ability", s.Name, s.Avs.Select(a => new PerkFx(a.Av, a.PerLevel * StartLevel, Conds: a.Conds))));
            if (s.Entries.Length == 0) b.EffectPerks.Add(null);
            else
            {
                var p = perks.Perk($"TMSkill{s.Id}Effect", s.Name, s.Effect,
                    [.. s.Entries.Select(e => PerkFx.Times(e.Entry, e.Base + e.PerLevel * StartLevel, e.Conds))], hidden: true);
                b.EffectPerks.Add(p);
            }
            foreach (var t in s.Tree)
            {
                Perk? previous = null;
                int after = -1;
                for (int r = 0; r < t.Ranks.Length; r++)
                {
                    var rank = t.Ranks[r];
                    string id = $"TMSkill{s.Id}{Regex.Replace(t.Name, "[^A-Za-z]", "")}{(t.Ranks.Length > 1 ? r + 1 : "")}";
                    var p = perks.Perk(id, t.Ranks.Length > 1 ? $"{t.Name} {r + 1}" : t.Name, rank.Description, rank.Fx,
                        requires: [new Cond(0, "global", $"TMSkill{s.Id}Level", CompareOperator.GreaterThanOrEqualTo, rank.Level)]);
                    if (previous is not null) previous.NextPerk.SetTo(p);
                    b.Tree.Add((p, i, rank.Level, after, t.Name, r + 1, t.Ranks.Length, rank.Description));
                    after = b.Tree.Count - 1;
                    previous = p;
                }
            }
        }

        // The hit spell: a script effect on whoever the player strikes (TMSkillHitEffect calls TMSkills.PlayerHit).
        var hitEffect = skyrim.MagicEffects.First(e => e.EditorID == "WaterbreathingFFSelf").Duplicate(mod.GetNextFormKey());
        hitEffect.EditorID = "TMSkillHitEffect";
        hitEffect.Name = "Struck";
        hitEffect.Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script };
        hitEffect.CastType = CastType.FireAndForget;
        hitEffect.TargetType = TargetType.Touch;
        hitEffect.Flags = MagicEffect.Flag.NoMagnitude | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.HideInUI
            | MagicEffect.Flag.Painless | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.NoHitEffect;
        hitEffect.MagicSkill = ActorValue.None;
        hitEffect.ResistValue = ActorValue.None;
        hitEffect.CastingArt.Clear();
        hitEffect.HitEffectArt.Clear();
        hitEffect.Keywords?.Clear();
        mod.MagicEffects.Add(hitEffect);
        b.HitEffect = hitEffect;
        b.HitSpell = new Spell(mod)
        {
            EditorID = "TMSkillHitSpell",
            Name = "Struck",
            ObjectBounds = new ObjectBounds(),
            Type = SpellType.Spell,
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Touch,
            Flags = SpellDataFlag.ManualCostCalc | SpellDataFlag.NoAbsorbOrReflect,
            BaseCost = 0,
            Description = "",
            Effects = [new Effect { BaseEffect = hitEffect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
        };
        mod.Spells.Add(b.HitSpell);
        b.HitPerk = new Perk(mod) { EditorID = "TMSkillHitPerk", Name = "Skills: hits", Playable = false, Hidden = true, NumRanks = 1, Description = "", Effects = [] };
        // Only one combat hit spell applies to a hit, the highest priority's: spears' (above the axes' bleeding, which a
        // spear template from a battleaxe would otherwise take) and then any weapon's, below Skyrim's own.
        if (b.Skills.Any(s => s.Id == "Spear"))
            b.HitPerk.Effects.Add(HitEntry(perks, b.HitSpell, 11, new Cond(1, "list", "Spears")));
        b.HitPerk.Effects.Add(HitEntry(perks, b.HitSpell, 0));
        mod.Perks.Add(b.HitPerk);
    }

    static PerkEntryPointSelectSpell HitEntry(ProgressionPerks perks, Spell spell, byte priority, params Cond[] conds)
    {
        var e = new PerkEntryPointSelectSpell
        {
            EntryPoint = EP.ApplyCombatHitSpell,
            PerkConditionTabCount = 3,
            Spell = spell.ToLink<ISpellGetter>(),
            Rank = 0,
            Priority = priority,
        };
        foreach (var tab in conds.GroupBy(c => c.Tab))
        {
            var pc = new PerkCondition { RunOnTabIndex = (byte)tab.Key };
            foreach (var c in tab) pc.Conditions.Add(perks.Condition(c));
            e.Conditions.Add(pc);
        }
        return e;
    }

    /// <summary>Daggerfall's own skills (DfSenses: Etiquette, Streetwise and the nine languages, globals DFSkill&lt;name&gt; in
    /// Daggerfall.esm at fixed FormIDs, raised by TMDfSensesControl): listed in the Skills tab as they are.</summary>
    public static List<(string Name, uint Id)> DaggerfallSkills(string dir)
    {
        var list = new List<(string, uint)>();
        string path = Path.Combine(dir, "Daggerfall.esm");
        if (!File.Exists(path)) return list;
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var globals = m.Globals.Where(g => g.FormKey.ModKey == m.ModKey && g.EditorID?.StartsWith("DFSkill") == true).ToDictionary(g => g.EditorID!, g => g.FormKey.ID);
        foreach (var name in DfSenses.LanguageSkills)
            if (globals.TryGetValue("DFSkill" + name, out var id)) list.Add((name, id));
        return list;
    }
}
