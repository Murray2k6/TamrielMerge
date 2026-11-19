using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Progression;
using Ob = Mutagen.Bethesda.Oblivion;
using EP = Mutagen.Bethesda.Skyrim.APerkEntryPointEffect.EntryType;
using ModType = Mutagen.Bethesda.Skyrim.PerkEntryPointModifyValue.ModificationType;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's skill mastery perks (docs/script-gaps.md O66) and its alchemy apparatus (O64).
/// <para>
/// <b>Mastery</b>: each of Oblivion's 21 skills gives a fixed perk at Apprentice (25), Journeyman (50), Expert (75) and
/// Master (100) (UESP "Oblivion:Skills" and each skill's page; the numbers from Oblivion.esm's game settings where it
/// keeps them: fDamagePowerAttackStandBonus 3.0 / the directional 2.5, fKnockdownChance 0.3, iPerkBlockStaggerChance 25,
/// fPerkSneakAttackMarksman{Novice 2, Apprentice.. 3}Mult, fSneakRunningMult 1.3, fLockPickAuto*). Each rank is a hidden
/// Skyrim perk <c>OBMastery&lt;Skill&gt;&lt;Rank&gt;</c> with the entry points Skyrim has for the rule; where Skyrim has no
/// way to express it the perk is a marker only (its description says what Oblivion gave; TMObLock reads the Security
/// marks). TMObMastery (a quest in this plugin) gives and takes them as the tracked skill crosses the thresholds:
/// Skyrim's own actor value where Skyrim keeps the player's skill (Block, Sneak...), the progression's skill where it
/// keeps it (Athletics, Mysticism, Blade...; ObStats.PlayerStatHome). Rules the table marks "uncertain" are from memory
/// of UESP, not measured: the wording may differ from Oblivion's own.
/// </para>
/// <para>
/// <b>Apparatus</b>: Oblivion's mortar &amp; pestle, alembic, calcinator and retort become usable items (an ingestible
/// whose one effect runs TMObApparatus, the way Redguard's usable items do), so they stay in inventories and shops as
/// before. Using a mortar asks TMObMastery to place Skyrim's alchemy workbench at the player and open it (as
/// TMObState.ShowEnchanting places the enchanting table); while it is open a Fortify Alchemy ability (OBApparatusBoost)
/// stands for the apparatus quality. Oblivion's quality numbers (Oblivion.esm APPA DATA): Novice 10, Apprentice 25,
/// Journeyman 50, Expert 75, Master 100. The boost is (mortar quality - 10) / 2 % plus a quarter of the best carried
/// calcinator's and retort's quality (Oblivion's fPotionT1CalDurMult / fPotionT1RetDurMult 0.01 x quality: those two
/// lengthen potions; the mortar's part of Oblivion's strength formula is in its code and not measured, so the halving is
/// an assumption). The alembic, which weakens a potion's harmful effects (fPotionT1AleMagMult -0.02 x quality), has no
/// Skyrim expression (Fortify Alchemy raises everything) and only counts as an apparatus.
/// </para>
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>Oblivion's mastery ranks (the skill level each needs), in rank order.</summary>
    static readonly (string Name, int Level)[] MasteryRanks = [("Apprentice", 25), ("Journeyman", 50), ("Expert", 75), ("Master", 100)];

    /// <summary>The skills' names as Oblivion shows them (StatSkills order).</summary>
    static readonly string[] MasterySkillNames =
    [
        "Armorer", "Athletics", "Blade", "Block", "Blunt", "Hand to Hand", "Heavy Armor", "Alchemy", "Alteration", "Conjuration",
        "Destruction", "Illusion", "Mysticism", "Restoration", "Acrobatics", "Light Armor", "Marksman", "Mercantile", "Security",
        "Sneak", "Speechcraft",
    ];

    /// <summary>One rank's rule and how it is made: <paramref name="Build"/> adds the perk's effects (null: a marker only,
    /// Skyrim has no entry point for it; the description still says what Oblivion gave).</summary>
    sealed record MasteryRule(string Rule, Action<Perk>? Build);

    ProgressionPerks? masteryPerks;
    Quest? masteryQuest;
    Spell? masteryDisarm, masteryParalyse;
    readonly List<Perk> masteryPerkList = [];
    readonly List<string> masteryRules = [];
    public int MasteryPerkCount => masteryPerkList.Count;

    ProgressionPerks MasteryPerks => masteryPerks ??= new ProgressionPerks(mod, Vanilla.Skyrim, new(), new());

    // ---- conditions and entries (Skyrim's condition functions as Mutagen names them) ----

    static Condition Cnd(ConditionData data, float value = 1, CompareOperator op = CompareOperator.EqualTo, bool or = false) =>
        new ConditionFloat { CompareOperator = op, ComparisonValue = value, Data = data, Flags = or ? Condition.Flag.OR : 0 };

    FormKey SkyrimKeyword(string editorId) =>
        Vanilla.Skyrim.Keywords.FirstOrDefault(k => k.EditorID == editorId)?.FormKey ?? throw new InvalidOperationException($"Skyrim.esm has no keyword {editorId}");

    /// <summary>HasKeyword on the subject (the item tab's weapon or armour piece).</summary>
    Condition HasKw(string keyword, bool or = false, bool has = true)
    {
        var d = new HasKeywordConditionData();
        d.Keyword = new FormLinkOrIndex<IKeywordGetter>(d, SkyrimKeyword(keyword));
        return Cnd(d, has ? 1 : 0, CompareOperator.EqualTo, or);
    }

    /// <summary>WornHasKeyword on the perk's owner (something worn has the keyword).</summary>
    Condition WornKw(string keyword, bool worn = true, bool or = false)
    {
        var d = new WornHasKeywordConditionData();
        d.Keyword = new FormLinkOrIndex<IKeywordGetter>(d, SkyrimKeyword(keyword));
        return Cnd(d, worn ? 1 : 0, CompareOperator.EqualTo, or);
    }

    static Condition PowerAttacking() => Cnd(new IsPowerAttackingConditionData());
    /// <summary>GetMovementDirection: 0 standing, 1 forward, 2 right, 3 back, 4 left (Creation Kit wiki).</summary>
    static Condition Moving(int direction, bool or = false) => Cnd(new GetMovementDirectionConditionData(), direction, CompareOperator.EqualTo, or);
    static Condition Fists() => Cnd(new GetEquippedItemTypeConditionData { ItemSource = CastSource.Right }, 0);
    static Condition Chance(float percent) => Cnd(new GetRandomPercentConditionData(), percent, CompareOperator.LessThan);
    static Condition Running() => Cnd(new IsRunningConditionData());
    static Condition Swimming() => Cnd(new IsSwimmingConditionData());

    /// <summary>Oblivion's Blade weapons on the item tab: swords and daggers (Skyrim's one- and two-handed blades).</summary>
    Condition[] BladeWeapons() => [HasKw("WeapTypeSword", true), HasKw("WeapTypeDagger", true), HasKw("WeapTypeGreatsword")];
    /// <summary>Oblivion's Blunt weapons: maces, hammers and axes.</summary>
    Condition[] BluntWeapons() => [HasKw("WeapTypeMace", true), HasKw("WeapTypeWarAxe", true), HasKw("WeapTypeWarhammer", true), HasKw("WeapTypeBattleaxe")];
    Condition[] Bows() => [HasKw("WeapTypeBow")];

    static PerkCondition Tab(int tab, params Condition[] conds)
    {
        var pc = new PerkCondition { RunOnTabIndex = (byte)tab };
        foreach (var c in conds) pc.Conditions.Add(c);
        return pc;
    }

    /// <summary>An entry point changing a value, with its condition tabs (0 the owner, 1 the weapon / item, 2 the target).</summary>
    static PerkEntryPointModifyValue ModEntry(EP entry, ModType how, float value, params PerkCondition[] tabs)
    {
        var e = new PerkEntryPointModifyValue { EntryPoint = entry, PerkConditionTabCount = ProgressionPerks.Tabs(entry), Modification = how, Value = value, Rank = 0, Priority = 0 };
        foreach (var t in tabs) e.Conditions.Add(t);
        return e;
    }

    /// <summary>Apply Combat Hit Spell: the spell lands on whoever the owner strikes when the tabs pass. Priority 5: above
    /// the progression's skill-use hit spell (0; only the highest applies to a hit) and below Skyrim's own perks'.</summary>
    static PerkEntryPointSelectSpell HitSpell(FormKey spell, params PerkCondition[] tabs)
    {
        var e = new PerkEntryPointSelectSpell { EntryPoint = EP.ApplyCombatHitSpell, PerkConditionTabCount = 3, Spell = new FormLink<ISpellGetter>(spell), Rank = 0, Priority = 5 };
        foreach (var t in tabs) e.Conditions.Add(t);
        return e;
    }

    /// <summary>A constant ability on self from Skyrim's or our magic effects, each effect with its conditions.</summary>
    Spell AbilityOf(string editorId, string name, params (FormKey Effect, float Magnitude, Condition[] Conds)[] effects)
    {
        var spell = new Spell(mod)
        {
            EditorID = editorId, Name = name, ObjectBounds = new ObjectBounds(), Type = SpellType.Ability, CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self, Description = "", Effects = [],
        };
        foreach (var (effect, magnitude, conds) in effects)
        {
            var fx = new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(effect), Data = new EffectData { Magnitude = magnitude, Area = 0, Duration = 0 } };
            foreach (var c in conds) fx.Conditions.Add(c);
            spell.Effects.Add(fx);
        }
        mod.Spells.Add(spell);
        return spell;
    }

    static PerkAbilityEffect Carry(Spell ability) => new() { Ability = ability.ToLink<ISpellGetter>(), Rank = 0, Priority = 0 };

    /// <summary>A hit spell (touch, fire and forget) of one of Skyrim's effects.</summary>
    Spell HitSpellOf(string editorId, string name, FormKey effect, float magnitude, int duration)
    {
        var spell = new Spell(mod)
        {
            EditorID = editorId, Name = name, ObjectBounds = new ObjectBounds(), Type = SpellType.Spell, CastType = CastType.FireAndForget,
            TargetType = TargetType.Touch, Flags = SpellDataFlag.ManualCostCalc | SpellDataFlag.NoAbsorbOrReflect, BaseCost = 0, Description = "",
            Effects = [new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(effect), Data = new EffectData { Magnitude = magnitude, Area = 0, Duration = duration } }],
        };
        mod.Spells.Add(spell);
        return spell;
    }

    /// <summary>Disarms (Skyrim's PerkDisarmEffect, the Disarming Bash perk's). Oblivion's own disarm chance from a sideways
    /// power attack is in its code (no game setting): 25% here, uncertain.</summary>
    Spell MasteryDisarm => masteryDisarm ??= HitSpellOf("OBMasteryDisarm", "Disarm", Sk(0x09CD4B), 0, 0);
    /// <summary>Paralyses 3 seconds (Skyrim's ParalysisFFContact). Oblivion's forward power attack paralyses briefly; the
    /// length is in its code (uncertain).</summary>
    Spell MasteryParalyse => masteryParalyse ??= HitSpellOf("OBMasteryParalyse", "Paralyze", Sk(0x06F702), 0, 3);
    /// <summary>Knocks down: Skyrim's own Warmaster knockdown spell (PerkWarmasterKnockdown).</summary>
    static readonly FormKey SkyrimKnockdown = Sk(0x106254);
    static readonly FormKey SkyrimMerchantPerk = Sk(0x058F7A), SkyrimInvestorPerk = Sk(0x058F7B), SkyrimMasterTraderPerk = Sk(0x1090A5);
    static readonly FormKey SkyrimAlchemyWorkbench = Sk(0x0BAD0C);
    static readonly FormKey SkyrimFortifyAlchemyConstant = Sk(0x08B65C), SkyrimMuffleConstant = Sk(0x09379B);

    /// <summary>The weapon-skill ranks Blade, Blunt and Hand to Hand share (UESP Oblivion:Blade / Blunt / Hand_to_Hand): the
    /// standing power attack at Apprentice (3x damage, fDamagePowerAttackStandBonus; Skyrim's power attacks do 2x, so x1.5),
    /// the sideways one at Journeyman (2.5x, may disarm), backwards at Expert (2.5x, may knock down, fKnockdownChance 30%),
    /// forward at Master (2.5x, may paralyse). Which rank the standing attack belongs to is uncertain (Novice may have it).
    /// <paramref name="weapon"/>: the item-tab conditions (null for fists, which the owner tab checks).</summary>
    MasteryRule[] WeaponRules(Condition[]? weapon)
    {
        PerkCondition[] Tabs(Condition[] owner) => weapon is null ? [Tab(0, [.. owner, Fists()])] : [Tab(0, owner), Tab(1, weapon)];
        return
        [
            new("Standing power attack: 3x damage (Skyrim's power attacks do 2x: x1.5 while standing still).",
                p => p.Effects.Add(ModEntry(EP.ModPowerAttackDamage, ModType.Multiply, 1.5f, Tabs([PowerAttacking(), Moving(0)])))),
            new("Sideways power attack: 2.5x damage (x1.25) and a 25% chance to disarm (chance uncertain).",
                p =>
                {
                    p.Effects.Add(ModEntry(EP.ModPowerAttackDamage, ModType.Multiply, 1.25f, Tabs([PowerAttacking(), Moving(2, true), Moving(4)])));
                    p.Effects.Add(HitSpell(MasteryDisarm.FormKey, Tabs([PowerAttacking(), Moving(2, true), Moving(4), Chance(25)])));
                }),
            new("Backwards power attack: 2.5x damage (x1.25) and a 30% chance to knock down (fKnockdownChance).",
                p =>
                {
                    p.Effects.Add(ModEntry(EP.ModPowerAttackDamage, ModType.Multiply, 1.25f, Tabs([PowerAttacking(), Moving(3)])));
                    p.Effects.Add(HitSpell(SkyrimKnockdown, Tabs([PowerAttacking(), Moving(3), Chance(30)])));
                }),
            new("Forward power attack: 2.5x damage (x1.25) and a 30% chance to paralyse for 3 seconds (chance and length uncertain).",
                p =>
                {
                    p.Effects.Add(ModEntry(EP.ModPowerAttackDamage, ModType.Multiply, 1.25f, Tabs([PowerAttacking(), Moving(1)])));
                    p.Effects.Add(HitSpell(MasteryParalyse.FormKey, Tabs([PowerAttacking(), Moving(1), Chance(30)])));
                }),
        ];
    }

    /// <summary>The armour skills' ranks (UESP Oblivion:Heavy_Armor / Light_Armor). Apprentice: the armour degrades half as
    /// fast (a marker TMWearOb reads). Journeyman: it weighs half (Mod Armor Weight x0.5, the item tab, as
    /// Skyrim's Conditioning / Unhindered do). Expert: it doesn't slow the wearer (in Skyrim only weight slows: weightless,
    /// x0). Master: heavy armour weighs nothing already (a marker), light armour +50% rating when nothing heavy is worn
    /// (Mod Armor Rating x1.5). The order of the Journeyman and Expert rules is uncertain.</summary>
    MasteryRule[] ArmorRules(bool heavy)
    {
        string kw = heavy ? "ArmorHeavy" : "ArmorLight", kind = heavy ? "Heavy" : "Light";
        return
        [
            new($"{kind} armor degrades 50% slower.", null),
            new($"{kind} armor weighs 50% less.", p => p.Effects.Add(ModEntry(EP.ModArmorWeight, ModType.Multiply, 0.5f, Tab(1, HasKw(kw))))),
            new($"{kind} armor no longer slows you: worn, it weighs nothing (Skyrim slows by weight alone).", p => p.Effects.Add(ModEntry(EP.ModArmorWeight, ModType.Multiply, 0, Tab(1, HasKw(kw))))),
            heavy
                ? new("Heavy armor weighs nothing when worn (given by the Expert rank already).", null)
                : new("Light armor gives 50% more protection when no heavy armor is worn.",
                    p => p.Effects.Add(ModEntry(EP.ModArmorRating, ModType.Multiply, 1.5f, Tab(0, WornKw("ArmorHeavy", false)), Tab(1, HasKw("ArmorLight"))))),
        ];
    }

    /// <summary>A magic school's ranks: Oblivion lets a caster cast only spells up to their rank's level (Novice,
    /// Apprentice... by magicka cost; UESP Oblivion:Spells "Spell Level"). Skyrim casts any spell, with no entry point to
    /// forbid one: markers.</summary>
    static MasteryRule[] SchoolRules(string school) =>
    [
        new($"Can cast Apprentice-level {school} spells (Skyrim never forbids a spell by level: nothing to do).", null),
        new($"Can cast Journeyman-level {school} spells (as above).", null),
        new($"Can cast Expert-level {school} spells (as above).", null),
        new($"Can cast Master-level {school} spells (as above).", null),
    ];

    /// <summary>The full table: each skill's four ranks (StatSkills order).</summary>
    MasteryRule[][] MasteryTable()
    {
        var perks = MasteryPerks;
        var stamina = perks.Effect(ActorValue.StaminaRateMult, false, hidden: true).FormKey;
        return
        [
            // Armorer (UESP Oblivion:Armorer): markers that TMWearOb reads (its HeldRank), Oblivion.exe's rules there.
            [
                new("Repair hammers break half as often.", null),
                new("Can repair magic items.", null),
                new("Can repair items to 125% of their health.", null),
                new("Repair hammers never break.", null),
            ],
            // Athletics (UESP Oblivion:Athletics): fatigue comes back faster while swimming, then while running, then
            // faster still. The order of swimming and running and the amounts are uncertain (Oblivion keeps them in code):
            // +50% then +100% here.
            [
                new("Fatigue regenerates 50% faster while swimming.", p => p.Effects.Add(Carry(AbilityOf("OBMasteryAthleticsApprenticeAbility", "Athletics: Apprentice", (stamina, 50, [Swimming()]))))),
                new("Fatigue regenerates 50% faster while running.", p => p.Effects.Add(Carry(AbilityOf("OBMasteryAthleticsJourneymanAbility", "Athletics: Journeyman", (stamina, 50, [Running()]))))),
                new("Fatigue regenerates 100% faster while swimming.", p => p.Effects.Add(Carry(AbilityOf("OBMasteryAthleticsExpertAbility", "Athletics: Expert", (stamina, 50, [Swimming()]))))),
                new("Fatigue regenerates 100% faster while running.", p => p.Effects.Add(Carry(AbilityOf("OBMasteryAthleticsMasterAbility", "Athletics: Master", (stamina, 50, [Running()]))))),
            ],
            WeaponRules(BladeWeapons()),
            // Block (UESP Oblivion:Block). Skyrim has no entry point for a block's cost, for a blocked attacker's recoil
            // (iPerkBlockStaggerChance 25) or for disarming on a block (only bashes carry a spell): markers. The rank
            // order of the fatigue and durability rules is uncertain.
            [
                new("Blocking costs no fatigue (Skyrim has no entry point for a block's stamina: nothing to do).", null),
                new("A blocked attacker may recoil, 25% of the time (iPerkBlockStaggerChance; Skyrim staggers only through bashes: nothing to do).", null),
                new("Shields and weapons take no damage when blocking (TMWearOb reads this).", null),
                new("A recoiling attacker may be disarmed (Skyrim has no on-block spell: nothing to do).", null),
            ],
            WeaponRules(BluntWeapons()),
            WeaponRules(null),
            ArmorRules(heavy: true),
            // Alchemy (UESP Oblivion:Alchemy): an ingredient shows 1, 2, 3, 4 of its effects by rank (Skyrim's Experimenter
            // entry, Mod Initial Ingredient Effects Learned, when eaten); a Master brews from a single ingredient (Skyrim's
            // alchemy needs two: nothing to do).
            [
                new("Ingredients reveal two of their effects.", p => p.Effects.Add(ModEntry(EP.ModInitialIngredientEffectsLearned, ModType.Set, 2))),
                new("Ingredients reveal three of their effects.", p => p.Effects.Add(ModEntry(EP.ModInitialIngredientEffectsLearned, ModType.Set, 3))),
                new("Ingredients reveal all four of their effects.", p => p.Effects.Add(ModEntry(EP.ModInitialIngredientEffectsLearned, ModType.Set, 4))),
                new("Can make a potion from a single ingredient (Skyrim's alchemy needs two: nothing to do).", null),
            ],
            SchoolRules("Alteration"),
            SchoolRules("Conjuration"),
            SchoolRules("Destruction"),
            SchoolRules("Illusion"),
            SchoolRules("Mysticism"),
            SchoolRules("Restoration"),
            // Acrobatics (UESP Oblivion:Acrobatics). Skyrim's jumps cost no stamina, it has no dodge roll, can't attack in
            // the air and can't jump on water: markers (the Apprentice rule is uncertain).
            [
                new("Jumping costs no fatigue (Skyrim's jumps cost none: nothing to do).", null),
                new("Can dodge: jump while blocking (Skyrim has no dodge: nothing to do).", null),
                new("Can attack while in the air (Skyrim can't: nothing to do).", null),
                new("Can jump across the surface of water (Skyrim can't: nothing to do).", null),
            ],
            ArmorRules(heavy: false),
            // Marksman (UESP Oblivion:Marksman): zoom at Journeyman (Skyrim's Eagle Eye: Mod Bow Zoom), a 30% knockdown
            // chance at Expert (fKnockdownChance) and paralysis at Master, as Skyrim's Bullseye lands a spell on a bow hit.
            // The Apprentice rule (drawing costs no fatigue) is uncertain; Skyrim's bows cost no stamina to draw.
            [
                new("Drawing a bow costs no fatigue (Skyrim's bows cost none: nothing to do).", null),
                new("Can zoom in while aiming a bow.", p => p.Effects.Add(ModEntry(EP.ModBowZoom, ModType.Add, 1, Tab(1, Bows())))),
                new("Arrows have a 30% chance to knock the target down (fKnockdownChance).", p => p.Effects.Add(HitSpell(SkyrimKnockdown, Tab(0, Chance(30)), Tab(1, Bows())))),
                new("Arrows have a 30% chance to paralyse the target for 3 seconds (chance and length uncertain).", p => p.Effects.Add(HitSpell(MasteryParalyse.FormKey, Tab(0, Chance(30)), Tab(1, Bows())))),
            ],
            // Mercantile (UESP Oblivion:Mercantile): Journeyman sells anything to any merchant, Expert invests 500 gold in a
            // shop, Master gives every merchant 500 more gold. Skyrim's own Merchant, Investor and Master Trader perks do
            // these (the vendor code checks those perks, not entry points), given by TMObMastery with the rank; Master
            // Trader adds 1,000, not 500. The Apprentice rule (haggling a stack at one price) is uncertain and has no
            // Skyrim counterpart.
            [
                new("Buying or selling a stack of items is haggled as one (Skyrim prices each: nothing to do).", null),
                new("Can sell any item to any merchant (Skyrim's Merchant perk).", null),
                new("Can invest 500 gold in a merchant's shop (Skyrim's Investor perk).", null),
                new("Every merchant has 500 more gold to barter with (Skyrim's Master Trader perk: 1,000).", null),
            ],
            // Security (UESP Oblivion:Security): tumblers fall more slowly at Apprentice (the nearest Skyrim has: a wider
            // sweet spot, Mod Lockpick Sweet Spot x1.25); a broken pick leaves set tumblers set: one at Journeyman, two at
            // Expert, all at Master (TMObLock's Auto Attempt reads the marks; the Journeyman / Expert counts are uncertain).
            [
                new("Tumblers fall more slowly (Skyrim: the lockpick's sweet spot is 25% wider).", p => p.Effects.Add(ModEntry(EP.ModLockpickSweetSpot, ModType.Multiply, 1.25f))),
                new("When a lockpick breaks, one set tumbler stays set.", null),
                new("When a lockpick breaks, two set tumblers stay set.", null),
                new("When a lockpick breaks, every set tumbler stays set.", null),
            ],
            // Sneak (UESP Oblivion:Sneak): Apprentice raises sneak attacks from 4x to 6x with a melee weapon and from 2x to
            // 3x with a bow (fPerkSneakAttackMarksman*Mult: Novice 2, the rest 3; the melee numbers are in Oblivion's code).
            // Skyrim's are 2x, so x3 melee and x1.5 bows (Novice's 4x is not given). Journeyman: boots no longer count
            // (fSneakBootWeight*; Skyrim's boots never do). Expert: running no longer counts (fSneakRunningMult 1.3: a
            // Muffle of 0.23 while running, the share of noise running added). Master: armour no longer counts (uncertain;
            // a Muffle of 0.5 while armour is worn, as Skyrim's Muffled Movement).
            [
                new("Sneak attacks do 6x damage with a melee weapon and 3x with a bow (Skyrim's 2x: x3 and x1.5).",
                    p =>
                    {
                        p.Effects.Add(ModEntry(EP.ModSneakAttackMult, ModType.Multiply, 1.5f, Tab(1, Bows())));
                        p.Effects.Add(ModEntry(EP.ModSneakAttackMult, ModType.Multiply, 3f, Tab(1, HasKw("WeapTypeBow", has: false))));
                    }),
                new("Boots no longer make you easier to detect (Skyrim's never do: nothing to do).", null),
                new("Running while sneaking no longer makes you easier to detect.",
                    p => p.Effects.Add(Carry(AbilityOf("OBMasterySneakExpertAbility", "Sneak: Expert", (SkyrimMuffleConstant, 0.23f, [Running()]))))),
                new("Armor no longer makes you easier to detect (uncertain).",
                    p => p.Effects.Add(Carry(AbilityOf("OBMasterySneakMasterAbility", "Sneak: Master", (SkyrimMuffleConstant, 0.5f, [WornKw("ArmorHeavy", true, true), WornKw("ArmorLight")]))))),
            ],
            // Speechcraft (UESP Oblivion:Speechcraft): the persuasion wheel's rules have no wheel here (persuasion is a
            // topic, docs/game-systems.md). Journeyman's cheaper bribes: Skyrim's Mod Bribe Amount (x0.5), which only
            // Skyrim's own bribe dialogue reads; Oblivion's Bribe topic pays its translated result scripts' price. The rank
            // order of the four rules is uncertain.
            [
                new("Turning the persuasion wheel no longer lowers disposition (no wheel here: nothing to do).", null),
                new("Bribes cost half as much (Skyrim's bribe amount halved; Oblivion's Bribe topic is unchanged).", p => p.Effects.Add(ModEntry(EP.ModBribeAmount, ModType.Multiply, 0.5f))),
                new("The hated wedge of the wheel no longer lowers disposition (no wheel here: nothing to do).", null),
                new("Disposition never falls below where the persuasion began (no wheel here: nothing to do).", null),
            ],
        ];
    }

    /// <summary>The TMObMastery quest (made once; TMObLock asks it how many tumblers a broken pick leaves set, the
    /// apparatus ask it to brew). Its perks and their script are filled in <see cref="FinishMastery"/>.</summary>
    Quest MasteryQuest
    {
        get
        {
            if (masteryQuest is not null) return masteryQuest;
            masteryQuest = new Quest(mod)
            {
                EditorID = "TMObMastery",
                Name = "Oblivion's skill mastery",
                Flags = Quest.Flag.StartGameEnabled,
                Priority = 50,
                Type = Quest.TypeEnum.None,
            };
            mod.Quests.Add(masteryQuest);
            return masteryQuest;
        }
    }

    /// <summary>Makes the 84 mastery perks and the TMObMastery quest's script; sorts the apparatus lists (Write).</summary>
    void FinishMastery()
    {
        var quest = MasteryQuest;
        var table = MasteryTable();
        masteryPerkList.Clear();
        masteryRules.Clear();
        for (int s = 0; s < StatSkills.Length; s++)
        {
            for (int r = 0; r < MasteryRanks.Length; r++)
            {
                var rule = table[s][r];
                var perk = new Perk(mod)
                {
                    EditorID = $"OBMastery{StatSkills[s]}{MasteryRanks[r].Name}",
                    Name = $"{MasterySkillNames[s]}: {MasteryRanks[r].Name}",
                    Description = rule.Rule,
                    Playable = false,
                    Hidden = true,
                    NumRanks = 1,
                    Effects = [],
                };
                rule.Build?.Invoke(perk);
                mod.Perks.Add(perk);
                masteryPerkList.Add(perk);
                masteryRules.Add(rule.Rule);
            }
        }
        var player = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x14);
        ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        var entry = new ScriptEntry { Name = "TMObMastery" };
        entry.Properties.Add(Obj("PlayerRef", player));
        entry.Properties.Add(new ScriptObjectListProperty { Name = "Perks", Objects = [.. masteryPerkList.Select(p => new ScriptObjectProperty { Object = p.ToLink<ISkyrimMajorRecordGetter>() })] });
        entry.Properties.Add(new ScriptStringListProperty { Name = "Rules", Data = [.. masteryRules] });
        entry.Properties.Add(new ScriptStringListProperty { Name = "SkillNames", Data = [.. MasterySkillNames] });
        entry.Properties.Add(new ScriptStringListProperty { Name = "PlayerStatAv", Data = [.. PlayerStatHome.Take(StatSkills.Length).Select(h => h.Av)] });
        entry.Properties.Add(new ScriptStringListProperty { Name = "PlayerStatId", Data = [.. PlayerStatHome.Take(StatSkills.Length).Select(h => h.Id)] });
        entry.Properties.Add(Obj("MerchantPerk", SkyrimMerchantPerk));
        entry.Properties.Add(Obj("InvestorPerk", SkyrimInvestorPerk));
        entry.Properties.Add(Obj("MasterTraderPerk", SkyrimMasterTraderPerk));
        entry.Properties.Add(Obj("Lab", SkyrimAlchemyWorkbench));
        entry.Properties.Add(Obj("Boost", ApparatusBoost.FormKey));
        quest.Aliases.Clear();
        quest.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(player) });
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = { entry },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMObMasteryPlayer", Properties = [] }],
                },
            ],
        };
        FinishApparatus();
    }

    // ---- the alchemy apparatus ----

    /// <summary>Oblivion's apparatus kinds (APPA DATA type): 0 mortar &amp; pestle, 1 alembic, 2 calcinator, 3 retort.</summary>
    readonly List<(Ingestible Item, MagicEffect Effect, int Kind, float Quality)> apparatus = [];
    Spell? apparatusBoost;
    public int ApparatusCount => apparatus.Count;

    /// <summary>The Fortify Alchemy ability a mortar gives while its workbench is open (its magnitude set by TMObMastery for
    /// the apparatus in hand: SKSE Spell.SetNthEffectMagnitude).</summary>
    Spell ApparatusBoost => apparatusBoost ??= AbilityOf("OBApparatusBoost", "Alchemy apparatus", (SkyrimFortifyAlchemyConstant, 0, []));

    /// <summary>An Oblivion apparatus as a usable item: an ingestible whose effect's script (TMObApparatus) gives it back
    /// and, for a mortar, has TMObMastery open an alchemy workbench with the quality's boost.</summary>
    Ingestible ApparatusItem(string edid, Ob.IAlchemicalApparatusGetter a)
    {
        int kind = a.Data is null ? 0 : (int)a.Data.Type;
        float quality = a.Data?.Quality ?? 10;
        var mesh = ItemMesh(a.Model?.File, a.Data?.Weight ?? 0);
        var mgef = new MagicEffect(mod)
        {
            EditorID = edid + "Use",
            Name = a.Name,
            Flags = MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoDuration | MagicEffect.Flag.Painless,
            Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script },
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            MenuDisplayObject = new FormLinkNullable<IStaticGetter>(),
            Description = "",
        };
        mod.MagicEffects.Add(mgef);
        var item = new Ingestible(mod)
        {
            EditorID = edid, Name = a.Name, Weight = a.Data?.Weight ?? 0, Value = a.Data?.Value ?? 0, ObjectBounds = Bounds(mesh),
            // The potion-use sound Oblivion's potions get here (PotionItem); using an apparatus opens the workbench.
            ConsumeSound = new FormLink<ISoundDescriptorGetter>(Sk(0x0B6435)),
            Flags = Ingestible.Flag.NoAutoCalc | Ingestible.Flag.Medicine,
            Effects = [new Effect { BaseEffect = mgef.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = 0, Area = 0, Duration = 0 } }],
        };
        item.Model = new Model { File = mesh is not null ? SkyrimModelPath(mesh.Path) : StandIns.Misc(a.Name, edid) };
        mod.Ingestibles.Add(item);
        mgef.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts =
            [
                new ScriptEntry
                {
                    Name = "TMObApparatus",
                    Properties =
                    [
                        new ScriptObjectProperty { Name = "Item", Object = item.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "Mastery", Object = MasteryQuest.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptIntProperty { Name = "Kind", Data = kind },
                        new ScriptFloatProperty { Name = "Quality", Data = quality },
                    ],
                },
            ],
        };
        apparatus.Add((item, mgef, kind, quality));
        return item;
    }

    /// <summary>The calcinators and retorts, best first, on every apparatus script (the mortar's boost counts the best of
    /// each the player carries).</summary>
    void FinishApparatus()
    {
        FormList ListOf(string edid, int kind, out float[] qualities)
        {
            var sorted = apparatus.Where(x => x.Kind == kind).OrderByDescending(x => x.Quality).ToList();
            var list = new FormList(mod) { EditorID = edid };
            foreach (var x in sorted) list.Items.Add(x.Item.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            qualities = [.. sorted.Select(x => x.Quality)];
            return list;
        }
        var calcinators = ListOf("OBApparatusCalcinators", 2, out var calcinatorQualities);
        var retorts = ListOf("OBApparatusRetorts", 3, out var retortQualities);
        foreach (var (_, mgef, _, _) in apparatus)
        {
            if (mgef.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObApparatus") is not { } entry) continue;
            entry.Properties.Add(new ScriptObjectProperty { Name = "Calcinators", Object = calcinators.ToLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(new ScriptFloatListProperty { Name = "CalcinatorQualities", Data = [.. calcinatorQualities] });
            entry.Properties.Add(new ScriptObjectProperty { Name = "Retorts", Object = retorts.ToLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(new ScriptFloatListProperty { Name = "RetortQualities", Data = [.. retortQualities] });
        }
    }
}
