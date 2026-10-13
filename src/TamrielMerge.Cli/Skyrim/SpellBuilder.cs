using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's standard spells (SPELLS.STD) as Skyrim spells with spell tomes, remastered with Skyrim's look: each
/// Daggerfall effect becomes a magic effect copied from the Skyrim spell it most resembles (projectiles, casting and
/// hit art, sounds; the spell's element for its damage, drain and transfer; Oakflesh to Dragonhide for Shield by
/// strength; the elemental cloaks for resistances; the fortify and restore spells by what they raise; Skyrim's area
/// versions for area spells),
/// with its actor value, school and delivery adjusted. Magnitudes and durations use Daggerfall's base + per-level
/// formulas at a reference caster level; one Daggerfall magic round (a game minute) counts as five seconds.
/// Spell traps get their own variant at a lower level, cast from the trap at the player (or on the player for
/// caster-only spells, which Daggerfall readies for the player for free).
/// </summary>
public sealed class SpellBuilder
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    const int PlayerSpellLevel = 10, TrapLevel = 5;
    const float SecondsPerRound = 5f;

    public enum Delivery { Self, Aimed, AimedArea, SelfArea, TargetActor }

    /// <summary>One Skyrim magic effect to make for a Daggerfall effect.</summary>
    sealed record Plan(string Key, string Label, string Template, MagicEffectArchetype.TypeEnum Type, ActorValue Av,
        ActorValue School, ActorValue Resist, Role Role, float Magnitude, float Seconds, bool KeepTemplateKeywords, Delivery? ForceDelivery = null, float Area = 0);

    enum Role { Harm, Debuff, Buff, Restore, Special }

    readonly SkyrimMod mod;
    readonly Dictionary<string, IMagicEffectGetter> vanillaEffects;
    readonly Dictionary<ActorValue, IBookGetter> tomeTemplates = new();
    readonly IFormLinkNullable<IEquipTypeGetter> eitherHand;
    readonly Dictionary<(string Key, Delivery Delivery), MagicEffect> effects = new();

    /// <summary>Player spells by Daggerfall spell index.</summary>
    public Dictionary<int, Spell> Spells { get; } = new();
    /// <summary>Trap variants by Daggerfall spell index, and whether each is cast on the player rather than at them.</summary>
    public Dictionary<int, (Spell Spell, bool OnPlayer)> Traps { get; } = new();
    public Dictionary<int, Book> Tomes { get; } = new();
    public LeveledItem TomeList { get; }
    public FormList DoorBases { get; }
    public Global RecallSet { get; }
    public MagicEffect? OpenEffect { get; private set; }
    /// <summary>Daggerfall effects that had no Skyrim counterpart, for the build report.</summary>
    public SortedSet<string> Skipped { get; } = new();

    public SpellBuilder(SkyrimMod mod, ISkyrimModGetter skyrim, IEnumerable<FormKey> doorBases)
    {
        this.mod = mod;
        vanillaEffects = skyrim.MagicEffects.Where(m => m.EditorID is not null).GroupBy(m => m.EditorID!).ToDictionary(g => g.Key, g => g.First());
        var firebolt = skyrim.Spells.First(s => s.EditorID == "Firebolt");
        eitherHand = firebolt.EquipmentType.FormKey.ToNullableLink<IEquipTypeGetter>();
        // One vanilla tome per school to copy (model, inventory art, pickup sound).
        var spells = skyrim.Spells.ToDictionary(s => s.FormKey);
        var effectSchools = skyrim.MagicEffects.ToDictionary(m => m.FormKey, m => m.MagicSkill);
        foreach (var book in skyrim.Books.Where(b => b.Teaches is IBookSpellGetter && (b.EditorID ?? "").StartsWith("SpellTome")))
            if (spells.TryGetValue(((IBookSpellGetter)book.Teaches!).Spell.FormKey, out var s) && s.Effects.FirstOrDefault() is { } e
                && effectSchools.TryGetValue(e.BaseEffect.FormKey, out var school))
                tomeTemplates.TryAdd(school, book);

        TomeList = new LeveledItem(mod) { EditorID = "TMDfSpellTomes", Entries = [] };
        mod.LeveledItems.Add(TomeList);
        DoorBases = new FormList(mod) { EditorID = "TMDfDoorBases" };
        foreach (var d in doorBases) DoorBases.Items.Add(d.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(DoorBases);
        RecallSet = new GlobalShort(mod) { EditorID = "TMDfRecallAnchorSet", Data = 0 };
        mod.Globals.Add(RecallSet);
    }

    /// <summary>Converts every learnable standard spell. Spells named with '!' are Daggerfall's potion recipes and are skipped.</summary>
    public void Build(IEnumerable<DfSpell> dfSpells, PlacedObject recallAnchor)
    {
        foreach (var s in dfSpells)
        {
            if (Spells.ContainsKey(s.Index)) continue;   // index 58 appears twice (Holy Word, Holy Touch)
            var plans = s.Effects.Select(e => PlanFor(s, e, PlayerSpellLevel)).OfType<Plan>().ToList();
            if (plans.Count == 0) continue;
            var delivery = plans.Select(p => p.ForceDelivery).FirstOrDefault(d => d is not null) ?? DeliveryFor(s.Range, plans[0]);
            var trapPlans = s.Effects.Select(e => PlanFor(s, e, TrapLevel)).OfType<Plan>().ToList();
            var trapDelivery = delivery switch { Delivery.Self or Delivery.SelfArea => delivery, _ => delivery == Delivery.AimedArea ? Delivery.AimedArea : Delivery.Aimed };
            Traps[s.Index] = (MakeSpell(s, $"DFTrapSpell{s.Index:D2}", trapPlans, trapDelivery, recallAnchor, trap: true), trapDelivery is Delivery.Self or Delivery.SelfArea);
            if (s.Name.StartsWith('!')) continue;
            var spell = MakeSpell(s, $"DFSpell{s.Index:D2}{Clean(s.Name)}", plans, delivery, recallAnchor, trap: false);
            Spells[s.Index] = spell;
            Tomes[s.Index] = MakeTome(s, spell, plans[0].School);
        }
    }

    static string Clean(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Points the trap spells at the same-named spells of an already built plugin (Daggerfall.esm), for plugins that
    /// extend it (the dungeon batches); build into a plugin that is thrown away first.
    /// </summary>
    public void UseMaster(ISkyrimModGetter master)
    {
        var theirs = master.Spells.Where(s => s.EditorID is not null).GroupBy(s => s.EditorID!).ToDictionary(g => g.Key, g => g.First().FormKey);
        foreach (var key in Traps.Keys.ToList())
        {
            var (spell, onPlayer) = Traps[key];
            if (spell.EditorID is { } e && theirs.TryGetValue(e, out var k))
                Traps[key] = (new Spell(k, SkyrimRelease.SkyrimSE) { EditorID = e }, onPlayer);
        }
    }

    static Delivery DeliveryFor(DfSpellRange range, Plan first) => range switch
    {
        DfSpellRange.Caster => Delivery.Self,
        DfSpellRange.AreaAroundCaster => first.Role is Role.Buff or Role.Restore ? Delivery.Self : Delivery.SelfArea,
        DfSpellRange.AreaAtRange => Delivery.AimedArea,
        _ => first.Role is Role.Buff or Role.Restore ? Delivery.Self : Delivery.Aimed,   // touch/target; Skyrim's touch delivery is melee-only
    };

    static ActorValue Attribute(int sub) => sub switch
    {
        0 => ActorValue.Stamina,          // Strength
        1 => ActorValue.Magicka,          // Intelligence
        2 => ActorValue.MagickaRateMult,  // Willpower
        3 => ActorValue.Archery,          // Agility
        4 => ActorValue.Health,           // Endurance
        5 => ActorValue.Speech,           // Personality
        6 => ActorValue.SpeedMult,        // Speed
        7 => ActorValue.CriticalChance,   // Luck
        8 => ActorValue.Health,
        _ => ActorValue.Stamina,          // 9: fatigue
    };

    static readonly string[] AttributeNames = ["Strength", "Intelligence", "Willpower", "Agility", "Endurance", "Personality", "Speed", "Luck", "Health", "Fatigue"];

    static ActorValue ResistFor(DfElement e) => e switch
    {
        DfElement.Fire => ActorValue.ResistFire,
        DfElement.Cold => ActorValue.ResistFrost,
        DfElement.Shock => ActorValue.ResistShock,
        DfElement.Poison => ActorValue.PoisonResist,
        _ => ActorValue.ResistMagic,
    };

    /// <summary>Vanilla effect to copy for harm of an element, by delivery (aimed, aimed with area, area around caster).
    /// Daggerfall's "magic" element at a single target takes Skyrim's absorb beam for the pool it hits (red health, blue
    /// magicka, green stamina); Skyrim has no other plain magic bolt.</summary>
    static string HarmTemplate(DfElement e, DfSpellRange range, ActorValue pool = ActorValue.Health) => (e, range) switch
    {
        (DfElement.Magic, DfSpellRange.Caster or DfSpellRange.Touch or DfSpellRange.Target) =>
            pool == ActorValue.Magicka ? "AbsorbMagickaConcAimed" : pool == ActorValue.Stamina ? "AbsorbStaminaConcAimed" : "AbsorbHealthConcAimed",
        (DfElement.Fire, DfSpellRange.AreaAtRange) => "FireDamageFFAimedArea",
        (DfElement.Fire, DfSpellRange.AreaAroundCaster) => "FireDamageFFSelfArea100",
        (DfElement.Fire, _) => "FireDamageFFAimed",
        (DfElement.Cold, DfSpellRange.AreaAtRange) => "FrostDamageFFAimedArea",
        (DfElement.Cold, DfSpellRange.AreaAroundCaster) => "FrostDamageFFSelfArea100",
        (DfElement.Cold, _) => "FrostDamageFFAimed",
        (DfElement.Shock, DfSpellRange.AreaAtRange or DfSpellRange.AreaAroundCaster) => "ShockDamageFFAimedArea",
        (DfElement.Shock, _) => "ShockDamageFFAimed",
        (DfElement.Poison, DfSpellRange.AreaAroundCaster) => "ParalysisFFSelfArea",
        (DfElement.Poison, _) => "ParalysisFFAimed",
        _ => "TurnUndeadFFAimed50",
    };

    static Plan? PlanFor(DfSpell s, DfSpellEffect e, int level)
    {
        float mag = e.MagnitudeAt(level);
        float seconds = Math.Clamp(e.DurationAt(level) * SecondsPerRound, 5, 600);
        float chance = Math.Min(e.ChanceAt(level), 100);
        string attr = e.SubType is >= 0 and <= 9 ? AttributeNames[e.SubType] : "";
        var ev = MagicEffectArchetype.TypeEnum.ValueModifier;
        var peak = MagicEffectArchetype.TypeEnum.PeakValueModifier;
        var none = ActorValue.None;
        var A = ActorValue.Alteration; var C = ActorValue.Conjuration; var D = ActorValue.Destruction; var I = ActorValue.Illusion; var R = ActorValue.Restoration;
        bool elemental = s.Element is DfElement.Fire or DfElement.Cold or DfElement.Shock;
        switch (e.Type)
        {
            case 0:   // Paralysis
                return new("Paralysis", "Paralysis", s.Range == DfSpellRange.AreaAroundCaster ? "ParalysisFFSelfArea" : "ParalysisFFAimed",
                    MagicEffectArchetype.TypeEnum.Paralysis, ActorValue.Paralysis, A, none, Role.Special, 0, Math.Min(seconds, 15), true);
            case 1:   // Continuous damage: Daggerfall's per-round damage spread over the duration, per second.
            case 4:   // Damage
            {
                var av = e.SubType switch { 1 => ActorValue.Stamina, 2 => ActorValue.Magicka, _ => ActorValue.Health };
                string what = e.SubType switch { 1 => "Fatigue", 2 => "Magicka", _ => "Health" };
                bool over = e.Type == 1;
                return new($"{(over ? "Cont" : "")}Damage{what}{s.Element}", $"{(over ? "Continuous " : "")}Damage {what}", HarmTemplate(s.Element, s.Range, av),
                    ev, av, D, ResistFor(s.Element), Role.Harm, over ? Math.Max(1, mag / SecondsPerRound) : mag, over ? Math.Min(seconds, 15) : 0, elemental && av == ActorValue.Health);
            }
            case 3:   // Cure: Skyrim's healing look (its cures are potions only, with no visuals).
                return e.SubType switch
                {
                    0 => new("CureDisease", "Cure Disease", "RestoreHealthFFSelf", MagicEffectArchetype.TypeEnum.CureDisease, none, R, none, Role.Special, 0, 0, false),
                    1 => new("CurePoison", "Cure Poison", "RestoreHealthFFSelf", MagicEffectArchetype.TypeEnum.CurePoison, none, R, none, Role.Special, 0, 0, false),
                    _ => new("CureParalysis", "Cure Paralysis", "RestoreHealthFFSelf", MagicEffectArchetype.TypeEnum.CureParalysis, none, R, none, Role.Special, 0, 0, false),
                };
            case 5:   // Disintegrate: Daggerfall destroys the target outright; a heavy magical blow here.
                return new($"Disintegrate{s.Element}", "Disintegrate", HarmTemplate(s.Element, s.Range), ev, ActorValue.Health, D, ResistFor(s.Element), Role.Harm, Math.Max(60, chance), 0, false);
            case 6:   // Dispel
                return e.SubType switch
                {
                    1 => new("DispelUndead", "Dispel Undead", "TurnUndeadFFAimed50", MagicEffectArchetype.TypeEnum.TurnUndead, ActorValue.Confidence, R, none, Role.Special, 30, 30, true),
                    2 => new("BanishDaedra", "Banish Daedra", "BanishFFActor50", MagicEffectArchetype.TypeEnum.Banish, ActorValue.Confidence, C, none, Role.Special, 30, 0, true, Delivery.TargetActor),
                    _ => new("DispelMagic", "Dispel Magic", "TurnUndeadFFAimed50", MagicEffectArchetype.TypeEnum.Dispel, none, A, none, Role.Special, 100, 0, false),
                };
            case 7:   // Drain attribute (temporary loss for the duration)
                return new($"Drain{attr}{s.Element}", $"Drain {attr}", HarmTemplate(s.Element, s.Range), peak, Attribute(e.SubType), D, ResistFor(s.Element), Role.Debuff, mag, seconds, false);
            case 8:   // Elemental resistance
            {
                var (av, el) = e.SubType switch
                {
                    0 => (ActorValue.ResistFire, "Fire"), 1 => (ActorValue.ResistFrost, "Frost"), 2 => (ActorValue.PoisonResist, "Poison"),
                    3 => (ActorValue.ResistShock, "Shock"), _ => (ActorValue.ResistMagic, "Magicka"),
                };
                // Skyrim's elemental cloaks for the look (their damage isn't kept); Stoneflesh for poison and magic.
                string look = e.SubType switch { 0 => "FireCloakFFSelf", 1 => "FrostCloakFFSelf", 3 => "ShockCloakFFSelf", _ => "ArmorFFSelf25" };
                return new($"Resist{el}", $"Resist {el}", look, peak, av, A, none, Role.Buff, Math.Min(chance, 75), seconds, false);
            }
            case 9:   // Fortify attribute
            {
                var av = Attribute(e.SubType);
                string look = av == ActorValue.Health ? "FortifyHealthFFSelf" : av is ActorValue.Magicka or ActorValue.MagickaRateMult ? "FortifyMagickaFFSelf" : "FortifyStaminaFFSelf";
                return new($"Fortify{attr}", $"Fortify {attr}", look, peak, av, R, none, Role.Buff, mag, seconds, false);
            }
            case 10:  // Heal attribute / health / fatigue
            {
                var av = Attribute(e.SubType);
                string look = av == ActorValue.Stamina ? "RestoreStaminaFFSelf" : av == ActorValue.Magicka ? "RestoreMagickaFFSelf" : "RestoreHealthFFSelf";
                return new($"Heal{attr}", e.SubType >= 8 ? $"Restore {(e.SubType == 8 ? "Health" : "Stamina")}" : $"Heal {attr}", look,
                    ev, av, R, none, Role.Restore, mag, 0, e.SubType == 8);
            }
            case 11:  // Transfer: take from the target, give to the caster
                return new($"Transfer{attr}{s.Element}", e.SubType == 8 ? "Vampiric Touch" : $"Transfer {attr}", HarmTemplate(s.Element, s.Range, Attribute(e.SubType)),
                    MagicEffectArchetype.TypeEnum.Absorb, Attribute(e.SubType), D, ResistFor(s.Element), Role.Harm, mag, 0, false);
            case 12:  // Soul trap
                return new("SoulTrap", "Soul Trap", "SoulTrapFFActor", MagicEffectArchetype.TypeEnum.Script, none, C, none, Role.Special, 0, Math.Min(seconds, 60), true, Delivery.TargetActor);
            case 13:
            case 23:  // Chameleon
            case 24:  // Shadow
                return new("Invisibility", e.Type == 13 ? "Invisibility" : e.Type == 23 ? "Chameleon" : "Shadow", "InvisibillityFFSelf",
                    MagicEffectArchetype.TypeEnum.Invisibility, ActorValue.Invisibility, I, none, Role.Buff, 0, seconds, true);
            case 14:  // Levitate: Skyrim has no flight; a great leap reaches the same ledges.
                return new("Levitate", "Levitate", "WaterbreathingFFSelf", peak, ActorValue.JumpingBonus, A, none, Role.Buff, 300, seconds, false);
            case 25:  // Slowfall
                return new("Slowfall", "Slowfall", "WaterbreathingFFSelf", peak, ActorValue.JumpingBonus, A, none, Role.Buff, 50, seconds, false);
            case 27:
            case 32:  // Jumping (the classic file uses group 32)
                return new("Jumping", "Jumping", "WaterbreathingFFSelf", peak, ActorValue.JumpingBonus, A, none, Role.Buff, 100, seconds, false);
            case 15:
                return new("Light", "Light", "LightFFSelf", MagicEffectArchetype.TypeEnum.Light, none, I, none, Role.Buff, 0, Math.Max(seconds, 60), true);
            case 16:
            case 17:  // Lock / Open: the nearest Daggerfall door, with Daggerfall's chance as the magnitude.
                return new(e.Type == 16 ? "Lock" : "Open", e.Type == 16 ? "Wizard Lock" : "Open", "WaterbreathingFFSelf",
                    MagicEffectArchetype.TypeEnum.Script, none, A, none, Role.Special, e.Type == 16 ? 100 : Math.Max(chance, 1), 0, false);
            case 18:  // Regenerate: Daggerfall heals per round; Skyrim per second.
                return new("Regenerate", "Regenerate", "RestoreHealthFFSelf", ev, ActorValue.Health, R, none, Role.Restore, Math.Max(1, mag / SecondsPerRound), seconds, true);
            case 19:  // Silence: no magicka regeneration and a drained pool.
                return new("Silence", "Silence", "InfluenceConfDownFFAimed", peak, ActorValue.MagickaRateMult, I, ActorValue.ResistMagic, Role.Debuff, 100, seconds, false);
            case 20:
                return new("SpellAbsorption", "Spell Absorption", "ArmorFFSelf50", peak, ActorValue.AbsorbChance, R, none, Role.Buff, Math.Min(chance, 50), seconds, false);
            case 21:
            case 22:  // Spell reflection / resistance: Skyrim has no reflection value; both resist magic.
                return new(e.Type == 21 ? "SpellReflection" : "SpellResistance", e.Type == 21 ? "Spell Reflection" : "Spell Resistance", "ArmorFFSelf50",
                    peak, ActorValue.ResistMagic, A, none, Role.Buff, Math.Min(chance, 75), seconds, false);
            case 30:
                return new("WaterBreathing", "Water Breathing", "WaterbreathingFFSelf", peak, ActorValue.WaterBreathing, A, none, Role.Buff, 1, seconds, true);
            case 31:
                return new("WaterWalking", "Water Walking", "WaterbreathingFFSelf", peak, ActorValue.WaterWalking, A, none, Role.Buff, 1, seconds, false);
            case 33:
            case 34:  // Calm (animal/undead/humanoid) and Charm
                return new("Calm", e.Type == 34 ? "Charm" : "Calm", "InfluenceAggDownFFAimed", MagicEffectArchetype.TypeEnum.Calm, ActorValue.Aggression,
                    I, none, Role.Special, 10 + chance / 4, Math.Min(seconds, 60), true);
            case 35:  // Shield: Daggerfall's absorbed damage as armour.
            {
                // Skyrim's armour spells by strength: Oakflesh, Stoneflesh, Ironflesh, Ebonyflesh, Dragonhide.
                int tier = Math.Clamp((int)(mag * 3 / 40), 0, 4);
                return new($"Shield{tier}", "Shield", $"ArmorFFSelf{new[] { 0, 25, 50, 75, 100 }[tier]}", peak, ActorValue.DamageResist, A, none, Role.Buff, mag * 3, seconds, true);
            }
            case 43:  // Recall: the first cast sets the anchor, the next returns to it.
                return new("Recall", "Recall", "WaterbreathingFFSelf", MagicEffectArchetype.TypeEnum.Script, none, A, none, Role.Special, 0, 0, false);
            case 44:  // Comprehend languages: Daggerfall's creatures stop fighting to talk.
                return new("Tongues", "Comprehend Languages", "InfluenceAggDownFFSelfArea250", MagicEffectArchetype.TypeEnum.Calm, ActorValue.Aggression,
                    I, none, Role.Special, 10 + chance / 4, Math.Min(seconds, 60), true, Area: 50);
            default:
                return null;
        }
    }

    MagicEffect EffectFor(Plan p, Delivery delivery, PlacedObject recallAnchor)
    {
        if (effects.TryGetValue((p.Key, delivery), out var existing)) return existing;
        var template = vanillaEffects.GetValueOrDefault(Variant(p.Template, delivery)) ?? vanillaEffects[p.Template];
        LookOf[$"DFEffect{p.Key}{delivery}"] = template.EditorID!;
        var m = template.Duplicate(mod.GetNextFormKey());
        m.EditorID = $"DFEffect{p.Key}{delivery}";
        m.Name = p.Label;
        m.CastType = CastType.FireAndForget;
        m.TargetType = delivery switch
        {
            Delivery.Self or Delivery.SelfArea => TargetType.Self,
            Delivery.TargetActor => TargetType.TargetActor,
            _ => TargetType.Aimed,
        };
        m.MagicSkill = p.School;
        m.ResistValue = p.Resist;
        // Frost's dual modifier (health and stamina) still counts as health damage.
        var oldType = TypeOf(m.Archetype);
        bool sameKind = oldType == p.Type || (oldType == MagicEffectArchetype.TypeEnum.DualValueModifier && p.Type == MagicEffectArchetype.TypeEnum.ValueModifier);
        if (true || (p.Av != ActorValue.None && m.Archetype.ActorValue != p.Av))
        {
            // A different kind of effect on the template's visuals: drop anything specific to the template's archetype.
            m.Archetype = p.Type == MagicEffectArchetype.TypeEnum.PeakValueModifier
                ? new MagicEffectPeakValueModArchetype { ActorValue = p.Av }
                : new MagicEffectArchetype { Type = p.Type, ActorValue = p.Av };
            m.SecondActorValue = ActorValue.None;
            m.SecondActorValueWeight = 0;
            m.Conditions.Clear();
            if (p.Type != MagicEffectArchetype.TypeEnum.Script) m.VirtualMachineAdapter = null;
        }
        if (!p.KeepTemplateKeywords) m.Keywords = null;
        var flags = m.Flags & ~(MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental | MagicEffect.Flag.Recover | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoMagnitude);
        flags |= p.Role switch
        {
            Role.Harm => MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental,
            Role.Debuff => MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental | MagicEffect.Flag.Recover,
            Role.Buff => MagicEffect.Flag.Recover,
            _ => 0,
        };
        if (p.Type is MagicEffectArchetype.TypeEnum.Paralysis or MagicEffectArchetype.TypeEnum.Calm or MagicEffectArchetype.TypeEnum.TurnUndead or MagicEffectArchetype.TypeEnum.Banish)
            flags |= template.Flags & (MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental);
        if (p.Magnitude == 0) flags |= MagicEffect.Flag.NoMagnitude;
        if (p.Seconds == 0 && p.Role != Role.Debuff && p.Role != Role.Buff) flags |= MagicEffect.Flag.NoDuration;
        m.Flags = flags;
        if (p.Key is "Open" or "Lock") AttachScript(m, "TMDfDoorSpellEffect",
            new ScriptObjectProperty { Name = "DoorBases", Object = DoorBases.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptBoolProperty { Name = "LockDoor", Data = p.Key == "Lock" },
            new ScriptFloatProperty { Name = "Chance", Data = p.Magnitude });
        if (p.Key == "Recall") AttachScript(m, "TMDfRecallEffect",
            new ScriptObjectProperty { Name = "Anchor", Object = recallAnchor.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptObjectProperty { Name = "AnchorSet", Object = RecallSet.ToLink<ISkyrimMajorRecordGetter>() });
        if (p.Key == "Open") OpenEffect = m;
        mod.MagicEffects.Add(m);
        effects[(p.Key, delivery)] = m;
        return m;
    }

    /// <summary>Skyrim's own area version of an effect, for spells cast as an area at range or around the caster.</summary>
    static string Variant(string template, Delivery delivery) => (template, delivery) switch
    {
        ("InfluenceAggDownFFAimed", Delivery.AimedArea) => "InfluenceAggDownFFAimedArea",
        ("InfluenceAggDownFFAimed", Delivery.SelfArea) => "InfluenceAggDownFFSelfArea250",
        ("InfluenceConfDownFFAimed", Delivery.AimedArea) => "InfluenceConfDownFFAimedArea",
        ("InfluenceConfDownFFAimed", Delivery.SelfArea) => "InfluenceConfDownFFSelfArea250",
        ("TurnUndeadFFAimed50", Delivery.AimedArea) => "TurnUndeadFFAimedArea50",
        ("TurnUndeadFFAimed50", Delivery.SelfArea) => "TurnUndeadFFMassSelfArea",
        ("RestoreHealthFFSelf", Delivery.SelfArea) => "RestoreHealthFFSelfArea",
        ("ParalysisFFAimed", Delivery.SelfArea) => "ParalysisFFSelfArea",
        _ => template,
    };

    /// <summary>The archetype kind, whichever Mutagen archetype class carries it.</summary>
    static MagicEffectArchetype.TypeEnum TypeOf(AMagicEffectArchetype a) => (MagicEffectArchetype.TypeEnum)(int)((dynamic)a).Type;

    static void AttachScript(MagicEffect m, string name, params ScriptProperty[] props)
    {
        m.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [new ScriptEntry { Name = name, Properties = [.. props] }] };
        m.Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script };
        m.Projectile.Clear();
    }

    Spell MakeSpell(DfSpell s, string editorId, List<Plan> plans, Delivery delivery, PlacedObject recallAnchor, bool trap)
    {
        float area = plans.Max(p => p.Area) is > 0 and var fixedArea ? fixedArea : delivery switch { Delivery.AimedArea => 15, Delivery.SelfArea => 20, _ => 0 };
        var spell = new Spell(mod)
        {
            EditorID = editorId,
            Name = s.Name.TrimStart('!'),
            ObjectBounds = new ObjectBounds(),
            Type = SpellType.Spell,
            CastType = CastType.FireAndForget,
            TargetType = delivery switch
            {
                Delivery.Self or Delivery.SelfArea => TargetType.Self,
                Delivery.TargetActor => TargetType.TargetActor,
                _ => TargetType.Aimed,
            },
            EquipmentType = eitherHand,
            ChargeTime = 0.5f,
            Description = "",
            Effects = [],
        };
        float cost = 0;
        foreach (var p in plans)
        {
            var effect = EffectFor(p, delivery, recallAnchor);
            spell.Effects.Add(new Effect
            {
                BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(),
                Data = new EffectData { Magnitude = p.Magnitude, Duration = (int)p.Seconds, Area = (int)area },
            });
            // Skyrim's own cost formula, per effect.
            // Damage and healing use Skyrim's cost formula; other effects (whose vanilla base costs are tuned to vanilla
            // magnitudes and durations, not Daggerfall's) cost what their nearest vanilla spell does.
            // Damage is priced on its total at vanilla Firebolt's rate (41 magicka for 25 damage); over time it costs less.
            if (p.Role == Role.Harm)
                cost += 1.19f * MathF.Pow(Math.Max(p.Magnitude * Math.Max(p.Seconds, 1), 1), 1.1f) * (p.Seconds > 0 ? 0.7f : 1) * (area > 0 ? 1 + area / 60f : 1);
            else cost += FixedCost(p.Key) is { } fixedCost
                ? fixedCost * (area > 0 ? 1.5f : 1)
                : effect.BaseCost * MathF.Pow(Math.Max(p.Magnitude, 1), 1.1f) * MathF.Pow(Math.Clamp(p.Seconds, 10, 60) / 10f, 1.1f) * (area > 0 ? 1 + area / 60f : 1);
        }
        RawCosts[editorId] = cost;
        int level = LevelFor(cost);
        spell.HalfCostPerk.SetTo(HalfCostPerk(plans[0].School, level));
        spell.Flags = SpellDataFlag.ManualCostCalc;
        spell.BaseCost = trap ? 0 : (uint)Math.Round(Math.Clamp(cost, 5, 500));
        if (!trap) Levels[s.Index] = level;
        mod.Spells.Add(spell);
        return spell;
    }

    static float? FixedCost(string key)
    {
        (string Prefix, float Cost)[] table =
        [
            ("Lock", 40), ("Open", 60), ("Recall", 80), ("Levitate", 120), ("Slowfall", 40), ("Jumping", 40),
            ("WaterWalking", 40), ("WaterBreathing", 40), ("Light", 15), ("Invisibility", 200), ("Paralysis", 150),
            ("Calm", 120), ("Tongues", 150), ("Resist", 90), ("SpellResistance", 120), ("SpellReflection", 150),
            ("SpellAbsorption", 150), ("Shield", 100), ("Silence", 120), ("DispelUndead", 150), ("DispelMagic", 150),
            ("BanishDaedra", 200), ("SoulTrap", 100), ("Cure", 25), ("Regenerate", 60), ("Fortify", 80), ("Drain", 70),
        ];
        foreach (var (prefix, c) in table) if (key.StartsWith(prefix)) return c;
        return null;
    }

    /// <summary>Skyrim spell level (novice..master) from the spell's magicka cost, in vanilla's cost bands.</summary>
    static int LevelFor(float cost) => cost switch { < 45 => 0, < 100 => 1, < 200 => 2, < 400 => 3, _ => 4 };

    public Dictionary<int, int> Levels { get; } = new();
    /// <summary>Each made effect's Skyrim look: the vanilla effect it was copied from (for checking).</summary>
    public Dictionary<string, string> LookOf { get; } = new();
    public Dictionary<string, float> RawCosts { get; } = new();

    static FormKey HalfCostPerk(ActorValue school, int level)
    {
        int column = school switch { ActorValue.Alteration => 0, ActorValue.Conjuration => 1, ActorValue.Destruction => 2, ActorValue.Illusion => 3, _ => 4 };
        uint[] novice = [0xF2CA6, 0xF2CA7, 0xF2CA8, 0xF2CA9, 0xF2CAA];
        uint[] apprentice = [0xC44B7, 0xC44BB, 0xC44BF, 0xC44C3, 0xC44C7];
        return Sk(level == 0 ? novice[column] : apprentice[column] + (uint)(level - 1));
    }

    Book MakeTome(DfSpell s, Spell spell, ActorValue school)
    {
        var template = tomeTemplates.GetValueOrDefault(school) ?? tomeTemplates.Values.First();
        var tome = template.Duplicate(mod.GetNextFormKey());
        tome.EditorID = $"DFSpellTome{s.Index:D2}{Clean(s.Name)}";
        tome.Name = $"Spell Tome: {s.Name}";
        tome.Teaches = new BookSpell { Spell = spell.ToLink<ISpellGetter>() };
        tome.Value = (uint)(new[] { 50, 120, 300, 500, 800 }[Levels[s.Index]]);
        mod.Books.Add(tome);
        TomeList.Entries!.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = (short)(1 + Levels[s.Index] * 10), Count = 1, Reference = tome.ToLink<IItemGetter>() } });
        return tome;
    }
}
