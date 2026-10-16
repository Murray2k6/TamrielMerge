using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim.Progression;

/// <summary>A condition on a perk entry (Tab: 0 the perk's owner, 1 the weapon / spell / item, 2 the target) or on an
/// ability's effect (Tab ignored).</summary>
public sealed record Cond(int Tab, string Kind, string? Arg = null, CompareOperator Op = CompareOperator.EqualTo, float Value = 1);

/// <summary>
/// One effect of a perk: an actor value it raises (or lowers, <paramref name="Detrimental"/>) while owned, through an
/// ability the perk carries; or a perk entry point (Skyrim's own: weapon damage, spell cost, prices...) changing a value.
/// </summary>
public sealed record PerkFx(ActorValue? Av = null, float Amount = 0, bool Detrimental = false,
    APerkEntryPointEffect.EntryType? Entry = null, PerkEntryPointModifyValue.ModificationType Mod = PerkEntryPointModifyValue.ModificationType.Multiply,
    Cond[]? Conds = null)
{
    public static PerkFx Raise(ActorValue av, float amount, params Cond[] conds) => new(av, amount, Conds: conds);
    public static PerkFx Lower(ActorValue av, float amount, params Cond[] conds) => new(av, amount, Detrimental: true, Conds: conds);
    public static PerkFx Times(APerkEntryPointEffect.EntryType entry, float value, params Cond[] conds) => new(Entry: entry, Amount: value, Conds: conds);
    public static PerkFx Plus(APerkEntryPointEffect.EntryType entry, float value, params Cond[] conds) =>
        new(Entry: entry, Amount: value, Mod: PerkEntryPointModifyValue.ModificationType.Add, Conds: conds);
}

/// <summary>
/// Makes the progression plugin's perks, abilities and their magic effects: one value-modifier effect per actor value
/// (shared by every ability), entry points with the condition tabs Skyrim's own perks use (read from Skyrim.esm's perks,
/// e.g. Mod Spell Cost has two: owner and spell).
/// </summary>
public sealed class ProgressionPerks
{
    readonly SkyrimMod mod;
    readonly IMagicEffectGetter template;
    readonly Dictionary<(ActorValue, bool, bool), MagicEffect> effects = new();
    readonly Dictionary<string, FormKey> lists;
    readonly Dictionary<string, FormKey> globals;
    readonly Dictionary<string, FormKey> keywords;

    /// <summary>How many condition tabs each entry point has (Skyrim's own perks; Perk Owner first).</summary>
    public static byte Tabs(APerkEntryPointEffect.EntryType e) => e switch
    {
        APerkEntryPointEffect.EntryType.ModAttackDamage or APerkEntryPointEffect.EntryType.CalculateWeaponDamage
            or APerkEntryPointEffect.EntryType.CalculateMyCriticalHitChance or APerkEntryPointEffect.EntryType.CalculateMyCriticalHitDamage
            or APerkEntryPointEffect.EntryType.ModSneakAttackMult or APerkEntryPointEffect.EntryType.ModPowerAttackDamage
            or APerkEntryPointEffect.EntryType.ModSpellMagnitude or APerkEntryPointEffect.EntryType.ModSpellDuration
            or APerkEntryPointEffect.EntryType.ModIncomingDamage or APerkEntryPointEffect.EntryType.ApplyCombatHitSpell
            or APerkEntryPointEffect.EntryType.ModPickpocketChance or APerkEntryPointEffect.EntryType.ModPoisonDoseCount
            or APerkEntryPointEffect.EntryType.ModTargetDamageResistance => 3,
        APerkEntryPointEffect.EntryType.ModFallingDamage or APerkEntryPointEffect.EntryType.ModSkillUse
            or APerkEntryPointEffect.EntryType.ModRecoverArrowChance => 1,
        _ => 2,   // spell cost, prices, armour rating and weight, power attack stamina, stagger, lockpicking, detection
    };

    public ProgressionPerks(SkyrimMod mod, ISkyrimModGetter skyrim, Dictionary<string, FormKey> lists, Dictionary<string, FormKey> globals)
    {
        this.mod = mod;
        this.lists = lists;
        this.globals = globals;
        template = skyrim.MagicEffects.First(e => e.EditorID == "AbFortifyHealth");
        keywords = skyrim.Keywords.Where(k => k.EditorID is { } e && (e.StartsWith("WeapType") || e.StartsWith("Armor")))
            .GroupBy(k => k.EditorID!).ToDictionary(g => g.Key, g => g.First().FormKey);
    }

    /// <summary>A constant self effect changing <paramref name="av"/> (shared by every ability that changes it).</summary>
    public MagicEffect Effect(ActorValue av, bool detrimental, bool hidden)
    {
        if (effects.TryGetValue((av, detrimental, hidden), out var hit)) return hit;
        var m = template.Duplicate(mod.GetNextFormKey());
        m.EditorID = $"TMProgression{av}{(detrimental ? "Down" : "Up")}{(hidden ? "Hidden" : "")}Effect";
        m.Name = AvName(av);
        bool pool = av is ActorValue.Health or ActorValue.Magicka or ActorValue.Stamina;
        m.Archetype = new MagicEffectArchetype { Type = pool ? MagicEffectArchetype.TypeEnum.PeakValueModifier : MagicEffectArchetype.TypeEnum.ValueModifier, ActorValue = av };
        m.Flags = MagicEffect.Flag.Recover | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.Painless;
        if (detrimental) m.Flags |= MagicEffect.Flag.Detrimental;
        if (hidden) m.Flags |= MagicEffect.Flag.HideInUI;
        m.MagicSkill = ActorValue.None;
        m.ResistValue = ActorValue.None;
        m.Keywords?.Clear();
        m.CastingArt.Clear();
        m.HitEffectArt.Clear();
        m.Description = "";
        mod.MagicEffects.Add(m);
        return effects[(av, detrimental, hidden)] = m;
    }

    /// <summary>An ability (constant, on self) with one effect per actor value change.</summary>
    public Spell Ability(string editorId, string name, IEnumerable<PerkFx> fx, bool hidden = false)
    {
        var spell = new Spell(mod)
        {
            EditorID = editorId,
            Name = name,
            ObjectBounds = new ObjectBounds(),
            Type = SpellType.Ability,
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
            Description = "",
            Effects = [],
        };
        foreach (var f in fx)
        {
            var effect = new Effect
            {
                BaseEffect = Effect(f.Av!.Value, f.Detrimental, hidden).ToNullableLink<IMagicEffectGetter>(),
                Data = new EffectData { Magnitude = f.Amount, Area = 0, Duration = 0 },
            };
            foreach (var c in f.Conds ?? []) effect.Conditions.Add(Condition(c));
            spell.Effects.Add(effect);
        }
        mod.Spells.Add(spell);
        return spell;
    }

    /// <summary>A perk with entry points and (for its actor values) an ability it carries. <paramref name="requires"/>
    /// are the conditions for taking it (Skyrim checks them in its own perk menus; the Tamriel menu checks the same).</summary>
    public Perk Perk(string editorId, string name, string description, IReadOnlyList<PerkFx> fx, bool hidden = false, IEnumerable<Cond>? requires = null)
    {
        var perk = new Perk(mod)
        {
            EditorID = editorId,
            Name = name,
            Description = description,
            Playable = !hidden,
            Hidden = hidden,
            NumRanks = 1,
            Effects = [],
        };
        foreach (var c in requires ?? []) perk.Conditions.Add(Condition(c));
        var avs = fx.Where(f => f.Av is not null).ToList();
        if (avs.Count > 0)
        {
            var ability = Ability(editorId + "Ability", name, avs);
            perk.Effects.Add(new PerkAbilityEffect { Ability = ability.ToLink<ISpellGetter>(), Rank = 0, Priority = 0 });
        }
        foreach (var f in fx.Where(f => f.Entry is not null))
            perk.Effects.Add(Entry(f));
        mod.Perks.Add(perk);
        return perk;
    }

    public PerkEntryPointModifyValue Entry(PerkFx f, byte priority = 0)
    {
        var e = new PerkEntryPointModifyValue
        {
            EntryPoint = f.Entry!.Value,
            PerkConditionTabCount = Tabs(f.Entry.Value),
            Modification = f.Mod,
            Value = f.Amount,
            Rank = 0,
            Priority = priority,
        };
        foreach (var tab in (f.Conds ?? []).GroupBy(c => c.Tab).OrderBy(g => g.Key))
        {
            var pc = new PerkCondition { RunOnTabIndex = (byte)tab.Key };
            foreach (var c in tab) pc.Conditions.Add(Condition(c));
            e.Conditions.Add(pc);
        }
        return e;
    }

    /// <summary>A condition: "list" (the subject is in one of our form lists), "global" (one of our globals compared
    /// with Value), "skill" (EPMagic_SpellHasSkill), "keyword" (HasKeyword), "fists" (nothing in the right hand),
    /// "swimming", "undead", "sneaking".</summary>
    public Condition Condition(Cond c)
    {
        ConditionData data;
        switch (c.Kind)
        {
            case "list":
            {
                var d = new IsInListConditionData();
                d.FormList = new FormLinkOrIndex<IFormListGetter>(d, lists[c.Arg!]);
                data = d;
                break;
            }
            case "global":
            {
                var d = new GetGlobalValueConditionData();
                d.Global = new FormLinkOrIndex<IGlobalGetter>(d, globals[c.Arg!]);
                data = d;
                break;
            }
            case "skill":
                data = new EPMagic_SpellHasSkillConditionData { ActorValue = Enum.Parse<ActorValue>(c.Arg!) };
                break;
            case "keyword":
            {
                var d = new HasKeywordConditionData();
                d.Keyword = new FormLinkOrIndex<IKeywordGetter>(d, keywords[c.Arg!]);
                data = d;
                break;
            }
            case "fists":
                data = new GetEquippedItemTypeConditionData { ItemSource = CastSource.Right };
                break;
            case "swimming": data = new IsSwimmingConditionData(); break;
            case "undead": data = new IsUndeadConditionData(); break;
            case "sneaking": data = new IsSneakingConditionData(); break;
            default: throw new ArgumentException($"condition kind {c.Kind}");
        }
        return new ConditionFloat { CompareOperator = c.Op, ComparisonValue = c.Value, Data = data };
    }

    public static string AvName(ActorValue av) => av switch
    {
        ActorValue.HealRateMult => "Health Regeneration",
        ActorValue.MagickaRateMult => "Magicka Regeneration",
        ActorValue.StaminaRateMult => "Stamina Regeneration",
        ActorValue.CarryWeight => "Carry Weight",
        ActorValue.SpeedMult => "Speed",
        ActorValue.DamageResist => "Armor",
        ActorValue.ResistMagic => "Resist Magic",
        ActorValue.ResistFire => "Resist Fire",
        ActorValue.ResistFrost => "Resist Frost",
        ActorValue.ResistShock => "Resist Shock",
        ActorValue.PoisonResist => "Resist Poison",
        ActorValue.ResistDisease => "Resist Disease",
        ActorValue.OneHanded => "One-Handed",
        ActorValue.TwoHanded => "Two-Handed",
        ActorValue.HeavyArmor => "Heavy Armor",
        ActorValue.LightArmor => "Light Armor",
        ActorValue.UnarmedDamage => "Unarmed Damage",
        ActorValue.JumpingBonus => "Jumping",
        ActorValue.AbsorbChance => "Spell Absorption",
        ActorValue.WaterBreathing => "Water Breathing",
        ActorValue.BypassVendorStolenCheck => "Fence",
        ActorValue.MovementNoiseMult => "Movement Noise",
        _ => System.Text.RegularExpressions.Regex.Replace(av.ToString(), "(?<=[a-z])(?=[A-Z])", " "),
    };
}
