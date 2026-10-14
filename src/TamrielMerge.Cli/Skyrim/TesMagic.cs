using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>An effect's range in Morrowind and Oblivion.</summary>
public enum TesRange { Self, Touch, Target }

/// <summary>How a spell or enchantment is used, in Morrowind's and Oblivion's terms.</summary>
public enum TesCast { Spell, Ability, Disease, Power, LesserPower, CastOnce, WhenStrikes, WhenUsed, Constant, Staff }

/// <summary>
/// One Morrowind or Oblivion magic effect, named as Morrowind names its effects (Oblivion's four-letter codes are
/// translated to these names, see <see cref="OblivionName"/>). Sub is the attribute or skill for effects that take
/// one; Assoc the Skyrim actor a summon calls or the weapon a bound effect gives.
/// </summary>
/// <param name="Label">A scripted effect's name (Name "ScriptEffect", Sub the Papyrus script it runs).</param>
/// <param name="Hostile">A scripted effect that harms (Oblivion's script effect "hostile" flag).</param>
public sealed record TesEffect(string Name, string? Sub, TesRange Range, int Area, int Duration, float Magnitude, FormKey? Assoc = null, string? Label = null, bool Hostile = false);

/// <summary>
/// Morrowind's and Oblivion's magic (they share one set of effects) as Skyrim's: spells, abilities, powers, diseases,
/// scrolls and enchantments. Each effect becomes a magic effect copied from the closest vanilla one (projectiles, hit
/// art and resistances are Skyrim's) with its actor value, school and kind set from the original, per use: cast
/// (fire and forget), constant (abilities, diseases, worn enchantments) or on strike (weapon enchantments). Effects
/// Skyrim has no engine counterpart for are scripts: Open and Lock (the converted locks), Mark and Recall, Divine and
/// Almsivi Intervention; a cast-when-used item grants a lesser power while worn. Skipped effects are counted.
/// </summary>
public sealed class TesMagic
{
    enum Role { Harm, Debuff, Buff, Restore, Special }
    enum Family { Fired, Constant, Strike }
    enum Delivery { Self, Aimed, AimedArea, SelfArea, Template }

    /// <summary>One Skyrim magic effect to make. Element picks the elemental templates; Keep keeps the template's
    /// archetype (and script); Script makes a scripted effect.</summary>
    sealed record Plan(string Key, string Label, MagicEffectArchetype.TypeEnum Type, ActorValue Av, ActorValue School, ActorValue Resist,
        Role Role, float Magnitude, string Fired = "TurnUndeadFFAimed50", string? Strike = null, string? Constant = null, string? Element = null,
        bool Keep = false, string? Script = null, FormKey? Assoc = null, bool KeepDelivery = false);

    readonly SkyrimMod mod;
    readonly ISkyrimModGetter skyrim;
    readonly string prefix;
    readonly Dictionary<string, IMagicEffectGetter> vanilla;
    readonly IFormLinkNullable<IEquipTypeGetter> eitherHand, voice;
    readonly Dictionary<string, MagicEffect> effects = new();
    readonly Dictionary<string, Spell> powers = new();

    public SortedDictionary<string, int> Skipped { get; } = new();
    public int SpellCount { get; private set; }
    public int EnchantmentCount { get; private set; }

    /// <summary>Where Mark sets the spot Recall returns to: a persistent marker the game's builder places in a cell.</summary>
    public PlacedObject? RecallAnchor { get; private set; }
    GlobalShort? markSet;
    /// <summary>Divine Intervention's (Imperial Cult) and Almsivi Intervention's (Temple) destinations; filled by the
    /// game's builder with persistent markers.</summary>
    public FormList DivineMarkers { get; }
    public FormList AlmsiviMarkers { get; }
    /// <summary>The marker base an interior's "where am I in the world" marker uses (linked to the outside door
    /// leading to it), for Intervention cast indoors.</summary>
    public Static WorldSpot { get; }

    /// <summary>How an effect looks and sounds in its own game: casting art (in the hand while casting), hit art (on
    /// the target, the caster for self effects, or the summoned creature), and its sounds. Null keeps Skyrim's.</summary>
    public sealed record EffectLook(FormKey? CastingArt, FormKey? HitArt, IReadOnlyList<(string Type, FormKey Sound)> Sounds);
    /// <summary>The game's own look for an effect (set by the game's builder), applied when its magic effect is made.</summary>
    public Func<TesEffect, EffectLook?>? Look { get; set; }

    public TesMagic(SkyrimMod mod, ISkyrimModGetter skyrim, string prefix)
    {
        this.mod = mod;
        this.skyrim = skyrim;
        this.prefix = prefix;
        vanilla = skyrim.MagicEffects.Where(m => m.EditorID is not null).GroupBy(m => m.EditorID!).ToDictionary(g => g.Key, g => g.First());
        eitherHand = skyrim.Spells.First(s => s.EditorID == "Firebolt").EquipmentType.FormKey.ToNullableLink<IEquipTypeGetter>();
        voice = skyrim.Spells.First(s => s.Type == SpellType.Power && !s.EquipmentType.IsNull).EquipmentType.FormKey.ToNullableLink<IEquipTypeGetter>();
        DivineMarkers = new FormList(mod) { EditorID = prefix + "DivineMarkers" };
        AlmsiviMarkers = new FormList(mod) { EditorID = prefix + "AlmsiviMarkers" };
        mod.FormLists.Add(DivineMarkers);
        mod.FormLists.Add(AlmsiviMarkers);
        WorldSpot = new Static(mod) { EditorID = prefix + "WorldSpot", ObjectBounds = new ObjectBounds(), Model = new Model { File = "marker_x.nif" } };
        mod.Statics.Add(WorldSpot);
    }

    // ---- attributes and skills ----

    /// <summary>An attribute or skill (Morrowind's or Oblivion's name) as the Skyrim actor value standing for it,
    /// and how many Skyrim points one point is worth.</summary>
    static (ActorValue Av, float Scale)? Stat(string? sub) => sub switch
    {
        "Strength" => (ActorValue.CarryWeight, 5),
        "Intelligence" => (ActorValue.Magicka, 1),
        "Willpower" => (ActorValue.MagickaRateMult, 1),
        "Agility" => (ActorValue.Archery, 1),
        "Speed" => (ActorValue.SpeedMult, 1),
        "Endurance" => (ActorValue.Health, 1),
        "Personality" => (ActorValue.Speech, 1),
        "Luck" => (ActorValue.CriticalChance, 1),
        "Block" => (ActorValue.Block, 1),
        "Armorer" => (ActorValue.Smithing, 1),
        "MediumArmor" or "HeavyArmor" => (ActorValue.HeavyArmor, 1),
        "LightArmor" => (ActorValue.LightArmor, 1),
        "BluntWeapon" or "Blunt" or "LongBlade" or "Blade" or "Axe" or "ShortBlade" => (ActorValue.OneHanded, 1),
        "Spear" => (ActorValue.TwoHanded, 1),
        "Athletics" => (ActorValue.StaminaRate, 1),
        "Enchant" => (ActorValue.Enchanting, 1),
        "Destruction" => (ActorValue.Destruction, 1),
        "Alteration" or "Mysticism" => (ActorValue.Alteration, 1),
        "Illusion" => (ActorValue.Illusion, 1),
        "Conjuration" => (ActorValue.Conjuration, 1),
        "Restoration" => (ActorValue.Restoration, 1),
        "Alchemy" => (ActorValue.Alchemy, 1),
        "Unarmored" => (ActorValue.DamageResist, 2),
        "Security" => (ActorValue.Lockpicking, 1),
        "Sneak" => (ActorValue.Sneak, 1),
        "Acrobatics" => (ActorValue.JumpingBonus, 2),
        "Marksman" => (ActorValue.Archery, 1),
        "Mercantile" or "Speechcraft" or "Speechraft" => (ActorValue.Speech, 1),   // Mutagen spells Oblivion's "Speechraft"
        "HandToHand" => (ActorValue.UnarmedDamage, 0.5f),
        _ => null,
    };

    static readonly string[] MwAttributes = ["Strength", "Intelligence", "Willpower", "Agility", "Speed", "Endurance", "Personality", "Luck"];
    static readonly string[] MwSkills =
    [
        "Block", "Armorer", "MediumArmor", "HeavyArmor", "BluntWeapon", "LongBlade", "Axe", "Spear", "Athletics", "Enchant",
        "Destruction", "Alteration", "Illusion", "Conjuration", "Mysticism", "Restoration", "Alchemy", "Unarmored", "Security",
        "Sneak", "Acrobatics", "LightArmor", "ShortBlade", "Marksman", "Mercantile", "Speechcraft", "HandToHand",
    ];

    /// <summary>Morrowind's magic effects by index (OpenMW ESM::MagicEffect).</summary>
    public static readonly string[] MwEffectNames =
    [
        "WaterBreathing", "SwiftSwim", "WaterWalking", "Shield", "FireShield", "LightningShield", "FrostShield", "Burden", "Feather", "Jump",
        "Levitate", "SlowFall", "Lock", "Open", "FireDamage", "ShockDamage", "FrostDamage", "DrainAttribute", "DrainHealth", "DrainMagicka",
        "DrainFatigue", "DrainSkill", "DamageAttribute", "DamageHealth", "DamageMagicka", "DamageFatigue", "DamageSkill", "Poison", "WeaknessToFire", "WeaknessToFrost",
        "WeaknessToShock", "WeaknessToMagicka", "WeaknessToCommonDisease", "WeaknessToBlightDisease", "WeaknessToCorprusDisease", "WeaknessToPoison", "WeaknessToNormalWeapons", "DisintegrateWeapon", "DisintegrateArmor", "Invisibility",
        "Chameleon", "Light", "Sanctuary", "NightEye", "Charm", "Paralyze", "Silence", "Blind", "Sound", "CalmHumanoid",
        "CalmCreature", "FrenzyHumanoid", "FrenzyCreature", "DemoralizeHumanoid", "DemoralizeCreature", "RallyHumanoid", "RallyCreature", "Dispel", "Soultrap", "Telekinesis",
        "Mark", "Recall", "DivineIntervention", "AlmsiviIntervention", "DetectAnimal", "DetectEnchantment", "DetectKey", "SpellAbsorption", "Reflect", "CureCommonDisease",
        "CureBlightDisease", "CureCorprusDisease", "CurePoison", "CureParalyzation", "RestoreAttribute", "RestoreHealth", "RestoreMagicka", "RestoreFatigue", "RestoreSkill", "FortifyAttribute",
        "FortifyHealth", "FortifyMagicka", "FortifyFatigue", "FortifySkill", "FortifyMaximumMagicka", "AbsorbAttribute", "AbsorbHealth", "AbsorbMagicka", "AbsorbFatigue", "AbsorbSkill",
        "ResistFire", "ResistFrost", "ResistShock", "ResistMagicka", "ResistCommonDisease", "ResistBlightDisease", "ResistCorprusDisease", "ResistPoison", "ResistNormalWeapons", "ResistParalysis",
        "RemoveCurse", "TurnUndead", "SummonScamp", "SummonClannfear", "SummonDaedroth", "SummonDremora", "SummonAncestralGhost", "SummonSkeletalMinion", "SummonBonewalker", "SummonGreaterBonewalker",
        "SummonBonelord", "SummonWingedTwilight", "SummonHunger", "SummonGoldenSaint", "SummonFlameAtronach", "SummonFrostAtronach", "SummonStormAtronach", "FortifyAttack", "CommandCreature", "CommandHumanoid",
        "BoundDagger", "BoundLongsword", "BoundMace", "BoundBattleAxe", "BoundSpear", "BoundLongbow", "ExtraSpell", "BoundCuirass", "BoundHelm", "BoundBoots",
        "BoundShield", "BoundGloves", "Corprus", "Vampirism", "SummonCenturionSphere", "SunDamage", "StuntedMagicka", "SummonFabricant", "SummonWolf", "SummonBear",
        "SummonBonewolf", "SummonCreature04", "SummonCreature05",
    ];

    /// <summary>A Morrowind effect (ENAM: index, skill, attribute, range, area, duration, magnitude min/max).</summary>
    public static TesEffect? FromMorrowind(byte[] enam, Func<string, FormKey?> assoc)
    {
        int index = BitConverter.ToInt16(enam, 0);
        if (index < 0 || index >= MwEffectNames.Length) return null;
        string name = MwEffectNames[index];
        sbyte skill = (sbyte)enam[2], attribute = (sbyte)enam[3];
        string? sub = name.EndsWith("Skill") && skill >= 0 && skill < MwSkills.Length ? MwSkills[skill]
            : name.EndsWith("Attribute") && attribute >= 0 && attribute < MwAttributes.Length ? MwAttributes[attribute] : null;
        var range = (TesRange)Math.Clamp(BitConverter.ToInt32(enam, 4), 0, 2);
        int area = BitConverter.ToInt32(enam, 8), duration = BitConverter.ToInt32(enam, 12);
        float magnitude = (BitConverter.ToInt32(enam, 16) + BitConverter.ToInt32(enam, 20)) / 2f;
        return new TesEffect(name, sub, range, area, duration, magnitude, name.StartsWith("Summon") || name.StartsWith("Bound") ? assoc(name) : null);
    }

    /// <summary>Oblivion's effect codes as Morrowind's effect names (Oblivion added the summons Z***, bound items
    /// BW** and BA**, Reanimate and Reflect Damage, and spells its magicka "spell points").</summary>
    public static string? OblivionName(string code) => code switch
    {
        "FIDG" => "FireDamage", "FRDG" => "FrostDamage", "SHDG" => "ShockDamage",
        "DGHE" => "DamageHealth", "DGFA" => "DamageFatigue", "DGSP" or "DGMA" => "DamageMagicka", "DGAT" => "DamageAttribute",
        "DRHE" => "DrainHealth", "DRFA" => "DrainFatigue", "DRSP" or "DRMA" => "DrainMagicka", "DRAT" => "DrainAttribute", "DRSK" => "DrainSkill",
        "ABHE" => "AbsorbHealth", "ABFA" => "AbsorbFatigue", "ABSP" or "ABMA" => "AbsorbMagicka", "ABAT" => "AbsorbAttribute", "ABSK" => "AbsorbSkill",
        "REHE" => "RestoreHealth", "REFA" => "RestoreFatigue", "RESP" or "REMA" => "RestoreMagicka", "REAT" => "RestoreAttribute",
        "FOHE" => "FortifyHealth", "FOFA" => "FortifyFatigue", "FOSP" or "FOMA" => "FortifyMagicka", "FOAT" => "FortifyAttribute",
        "FOSK" => "FortifySkill", "FOMM" => "FortifyMaximumMagicka",
        "RSFI" => "ResistFire", "RSFR" => "ResistFrost", "RSSH" => "ResistShock", "RSMA" => "ResistMagicka", "RSPO" => "ResistPoison",
        "RSDI" => "ResistCommonDisease", "RSNW" => "ResistNormalWeapons", "RSPA" => "ResistParalysis",
        "WKFI" => "WeaknessToFire", "WKFR" => "WeaknessToFrost", "WKSH" => "WeaknessToShock", "WKMA" => "WeaknessToMagicka",
        "WKPO" => "WeaknessToPoison", "WKDI" => "WeaknessToCommonDisease", "WKNW" => "WeaknessToNormalWeapons",
        "CUDI" => "CureCommonDisease", "CUPA" => "CureParalyzation", "CUPO" => "CurePoison",
        "BRDN" => "Burden", "FTHR" => "Feather", "SHLD" => "Shield", "FISH" => "FireShield", "FRSH" => "FrostShield", "LISH" => "LightningShield",
        "WABR" => "WaterBreathing", "WAWA" => "WaterWalking", "OPEN" => "Open", "LOCK" => "Lock",
        "INVI" => "Invisibility", "CHML" => "Chameleon", "LGHT" => "Light", "NEYE" => "NightEye", "CHRM" => "Charm", "PARA" => "Paralyze",
        "SLNC" => "Silence", "CALM" => "CalmHumanoid", "FRNZ" => "FrenzyHumanoid", "DEMO" => "DemoralizeHumanoid", "RALY" => "RallyHumanoid",
        "COCR" => "CommandCreature", "COHU" => "CommandHumanoid",
        "DSPL" => "Dispel", "DTCT" => "DetectAnimal", "RFLC" => "Reflect", "REDG" => "ReflectDamage", "SABS" => "SpellAbsorption",
        "STRP" => "Soultrap", "TELE" => "Telekinesis", "TURN" => "TurnUndead", "DIAR" => "DisintegrateArmor", "DIWE" => "DisintegrateWeapon",
        "STMA" => "StuntedMagicka", "REAN" => "Reanimate",
        _ when code.StartsWith('Z') => "Summon",
        _ when code.StartsWith("BW") => "BoundWeapon",
        "BABO" => "BoundBoots", "BACU" => "BoundCuirass", "BAGA" => "BoundGloves", "BAGR" => "BoundGreaves", "BAHE" => "BoundHelm", "BASH" => "BoundShield",
        _ when code.StartsWith("BA") || code is "MYHL" or "MYTH" => "BoundArmor",   // MYHL/MYTH: Mythic Dawn armour
        _ => null,
    };

    // ---- effects ----

    static readonly ActorValue A = ActorValue.Alteration, C = ActorValue.Conjuration, D = ActorValue.Destruction, I = ActorValue.Illusion, R = ActorValue.Restoration;
    const MagicEffectArchetype.TypeEnum Value = MagicEffectArchetype.TypeEnum.ValueModifier, Peak = MagicEffectArchetype.TypeEnum.PeakValueModifier;

    static ActorValue Pool(string name) => name.EndsWith("Magicka") ? ActorValue.Magicka : name.EndsWith("Fatigue") ? ActorValue.Stamina : ActorValue.Health;
    static string PoolName(ActorValue av) => av == ActorValue.Magicka ? "Magicka" : av == ActorValue.Stamina ? "Stamina" : "Health";

    static (ActorValue Av, string Label)? ResistOf(string what) => what switch
    {
        "Fire" => (ActorValue.ResistFire, "Fire"), "Frost" => (ActorValue.ResistFrost, "Frost"), "Shock" => (ActorValue.ResistShock, "Shock"),
        "Magicka" => (ActorValue.ResistMagic, "Magic"), "Poison" => (ActorValue.PoisonResist, "Poison"),
        "CommonDisease" or "BlightDisease" => (ActorValue.ResistDisease, "Disease"),
        "NormalWeapons" => (ActorValue.DamageResist, "Normal Weapons"),
        _ => null,
    };

    Plan? PlanFor(TesEffect e)
    {
        float m = Math.Max(e.Magnitude, 1);
        var none = ActorValue.None;
        string n = e.Name;
        switch (n)
        {
            case "FireDamage": case "FrostDamage": case "ShockDamage":
            {
                string el = n[..^6];
                var resist = el == "Fire" ? ActorValue.ResistFire : el == "Frost" ? ActorValue.ResistFrost : ActorValue.ResistShock;
                return new($"{el}Damage", $"{el} Damage", Value, ActorValue.Health, D, resist, Role.Harm, m, Element: el, Strike: $"Ench{el}DamageFFContact");
            }
            case "Poison":
                return new("Poison", "Poison", Value, ActorValue.Health, D, ActorValue.PoisonResist, Role.Harm, m, "ParalysisFFAimed");
            case "DamageHealth": case "DamageMagicka": case "DamageFatigue":
            {
                var av = Pool(n);
                return new($"Damage{PoolName(av)}", $"Damage {PoolName(av)}", Value, av, D, ActorValue.ResistMagic, Role.Harm, m,
                    Strike: av == ActorValue.Magicka ? "EnchMagickaDamageFFContact" : av == ActorValue.Stamina ? "EnchStaminaDamageFFContact" : null);
            }
            case "DrainHealth": case "DrainMagicka": case "DrainFatigue":
            {
                var av = Pool(n);
                return new($"Drain{PoolName(av)}", $"Drain {PoolName(av)}", Peak, av, D, ActorValue.ResistMagic, Role.Debuff, m);
            }
            case "DamageAttribute": case "DamageSkill": case "DrainAttribute": case "DrainSkill":
            {
                if (Stat(e.Sub) is not var (av, scale)) return null;
                bool drain = n.StartsWith("Drain");
                return new($"{(drain ? "Drain" : "Damage")}{e.Sub}", $"{(drain ? "Drain" : "Damage")} {e.Sub}", drain ? Peak : Value, av, D, ActorValue.ResistMagic,
                    drain ? Role.Debuff : Role.Harm, m * scale);
            }
            case "AbsorbHealth": case "AbsorbMagicka": case "AbsorbFatigue":
            {
                var av = Pool(n);
                return new($"Absorb{PoolName(av)}", $"Absorb {PoolName(av)}", MagicEffectArchetype.TypeEnum.Absorb, av, ActorValue.Alteration, ActorValue.ResistMagic,
                    Role.Harm, m, Strike: $"EnchAbsorb{PoolName(av)}FFContact");
            }
            case "AbsorbAttribute": case "AbsorbSkill":
                return Stat(e.Sub) is var (aav, ascale)
                    ? new($"Absorb{e.Sub}", $"Absorb {e.Sub}", MagicEffectArchetype.TypeEnum.Absorb, aav, A, ActorValue.ResistMagic, Role.Harm, m * ascale) : null;
            case "RestoreHealth": case "RestoreMagicka": case "RestoreFatigue":
            {
                var av = Pool(n);
                return new($"Restore{PoolName(av)}", $"Restore {PoolName(av)}", Value, av, R, none, Role.Restore, m, "RestoreHealthFFSelf", Keep: false);
            }
            case "RestoreAttribute": case "RestoreSkill":
                return Stat(e.Sub) is var (rav, rscale)
                    ? new($"Restore{e.Sub}", $"Restore {e.Sub}", Value, rav, R, none, Role.Restore, m * rscale, "RestoreHealthFFSelf") : null;
            case "FortifyHealth": case "FortifyMagicka": case "FortifyFatigue":
            {
                var av = Pool(n);
                return new($"Fortify{PoolName(av)}", $"Fortify {PoolName(av)}", Peak, av, R, none, Role.Buff, m, "RestoreHealthFFSelf");
            }
            case "FortifyAttribute": case "FortifySkill":
                return Stat(e.Sub) is var (fav, fscale)
                    ? new($"Fortify{e.Sub}", $"Fortify {e.Sub}", Peak, fav, R, none, Role.Buff, m * fscale, "RestoreHealthFFSelf") : null;
            case "FortifyMaximumMagicka":
                return new("FortifyMaximumMagicka", "Fortify Maximum Magicka", Peak, ActorValue.Magicka, R, none, Role.Buff, m * 5, "RestoreHealthFFSelf");
            case "FortifyAttack":
                return new("FortifyAttack", "Fortify Attack", Peak, ActorValue.AttackDamageMult, R, none, Role.Buff, m / 100f, "RestoreHealthFFSelf");
            case "Shield":
                return new("Shield", "Shield", Peak, ActorValue.DamageResist, A, none, Role.Buff, m * 3, "ArmorFFSelf0");
            case "FireShield": case "FrostShield": case "LightningShield":
            {
                var (av, label) = n.StartsWith("Fire") ? (ActorValue.ResistFire, "Fire") : n.StartsWith("Frost") ? (ActorValue.ResistFrost, "Frost") : (ActorValue.ResistShock, "Lightning");
                return new($"{label}Shield", $"{label} Shield", Peak, av, A, none, Role.Buff, m, "ArmorFFSelf0");
            }
            case "SpellAbsorption":
                return new("SpellAbsorption", "Spell Absorption", Peak, ActorValue.AbsorbChance, mysticism, none, Role.Buff, m, "WaterbreathingFFSelf");
            case "Reflect":
                return new("Reflect", "Reflect", Peak, ActorValue.ResistMagic, mysticism, none, Role.Buff, m, "WaterbreathingFFSelf");
            case "ReflectDamage":
                return new("ReflectDamage", "Reflect Damage", Peak, ActorValue.ReflectDamage, A, none, Role.Buff, m, "WaterbreathingFFSelf");
            case "Sanctuary":
                return new("Sanctuary", "Sanctuary", Peak, ActorValue.DamageResist, I, none, Role.Buff, m * 2, "WaterbreathingFFSelf");
            case "Burden":
                return new("Burden", "Burden", Peak, ActorValue.CarryWeight, A, ActorValue.ResistMagic, Role.Debuff, m);
            case "Feather":
                return new("Feather", "Feather", Peak, ActorValue.CarryWeight, A, none, Role.Buff, m, "WaterbreathingFFSelf");
            case "Jump":
                return new("Jump", "Jump", Peak, ActorValue.JumpingBonus, A, none, Role.Buff, m * 3, "WaterbreathingFFSelf");
            case "Levitate":
                // Skyrim has no flight; a great leap reaches the same ledges (as Daggerfall's Levitate).
                return new("Levitate", "Levitate", Peak, ActorValue.JumpingBonus, A, none, Role.Buff, 300, "WaterbreathingFFSelf");
            case "SlowFall":
                return new("SlowFall", "Slowfall", Peak, ActorValue.JumpingBonus, A, none, Role.Buff, 50, "WaterbreathingFFSelf");
            case "SwiftSwim":
                return new("SwiftSwim", "Swift Swim", Peak, ActorValue.SpeedMult, A, none, Role.Buff, m / 2, "WaterbreathingFFSelf");
            case "WaterBreathing":
                return new("WaterBreathing", "Water Breathing", Peak, ActorValue.WaterBreathing, A, none, Role.Buff, 1, "WaterbreathingFFSelf", Constant: "WaterbreathingConstantSelf");
            case "WaterWalking":
                return new("WaterWalking", "Water Walking", Peak, ActorValue.WaterWalking, A, none, Role.Buff, 1, "WaterbreathingFFSelf");
            case "Invisibility":
                return new("Invisibility", "Invisibility", MagicEffectArchetype.TypeEnum.Invisibility, ActorValue.Invisibility, I, none, Role.Buff, 0, "InvisibillityFFSelf", Keep: true);
            case "Chameleon":
                // Skyrim has no chameleon: near-total chameleon is invisibility, less is stealth.
                return m >= 75
                    ? new("Invisibility", "Chameleon", MagicEffectArchetype.TypeEnum.Invisibility, ActorValue.Invisibility, I, none, Role.Buff, 0, "InvisibillityFFSelf", Keep: true)
                    : new("Chameleon", "Chameleon", Peak, ActorValue.Sneak, I, none, Role.Buff, m, "InvisibillityFFSelf");
            case "Light":
                return new("Light", "Light", MagicEffectArchetype.TypeEnum.Light, none, I, none, Role.Buff, 0, "LightFFSelf", Keep: true);
            case "NightEye":
                return new("NightEye", "Night-Eye", MagicEffectArchetype.TypeEnum.Script, none, I, none, Role.Buff, 0, "NightEyeEffect", Keep: true);
            case "Charm":
                // Morrowind's and Oblivion's charm raises disposition (Disposition.Value).
                return new("Charm", "Charm", Peak, Disposition.Value, I, ActorValue.ResistMagic, Role.Buff, m, "InfluenceAggDownFFAimed");
            case "Paralyze":
                return new("Paralyze", "Paralyze", MagicEffectArchetype.TypeEnum.Paralysis, ActorValue.Paralysis, I, ActorValue.ResistMagic, Role.Special, 0,
                    "ParalysisFFAimed", Strike: "EnchParalysisFFContact", Keep: true);
            case "Silence":
                return new("Silence", "Silence", Peak, ActorValue.MagickaRateMult, I, ActorValue.ResistMagic, Role.Debuff, 100);
            case "StuntedMagicka":
                return new("StuntedMagicka", "Stunted Magicka", Peak, ActorValue.MagickaRateMult, D, none, Role.Debuff, 100);
            case "CalmHumanoid": case "CalmCreature": case "CommandCreature": case "CommandHumanoid":
                return new("Calm", n.StartsWith("Command") ? "Command" : "Calm", MagicEffectArchetype.TypeEnum.Calm, ActorValue.Aggression, I, ActorValue.ResistMagic,
                    Role.Special, Level(m), "InfluenceAggDownFFAimed", Keep: true);
            case "FrenzyHumanoid": case "FrenzyCreature":
                return new("Frenzy", "Frenzy", MagicEffectArchetype.TypeEnum.Frenzy, ActorValue.Aggression, I, ActorValue.ResistMagic, Role.Special, Level(m), "InfluenceAggUpFFAimed", Keep: true);
            case "DemoralizeHumanoid": case "DemoralizeCreature":
                return new("Demoralize", "Demoralize", MagicEffectArchetype.TypeEnum.Demoralize, ActorValue.Confidence, I, ActorValue.ResistMagic, Role.Special, Level(m),
                    "InfluenceConfDownFFAimed", Strike: "EnchInfluenceConfDownFFContactMed", Keep: true);
            case "RallyHumanoid": case "RallyCreature":
                return new("Rally", "Rally", MagicEffectArchetype.TypeEnum.Rally, ActorValue.Confidence, I, none, Role.Special, Level(m), "InfluenceConfUpFFAimed", Keep: true);
            case "TurnUndead":
                return new("TurnUndead", "Turn Undead", MagicEffectArchetype.TypeEnum.TurnUndead, ActorValue.Confidence, R, none, Role.Special, Level(m),
                    "TurnUndeadFFAimed50", Strike: "EnchTurnUndeadFFContact", Keep: true);
            case "Dispel": case "RemoveCurse":
                return new("Dispel", "Dispel", MagicEffectArchetype.TypeEnum.Dispel, none, mysticism, none, Role.Special, m);
            case "Soultrap":
                return new("Soultrap", "Soul Trap", MagicEffectArchetype.TypeEnum.SoulTrap, none, mysticism, none, Role.Special, 0, "SoulTrapFFActor",
                    Strike: "EnchSoulTrapFFContact", Keep: true, KeepDelivery: true);
            case "Telekinesis":
                return new("Telekinesis", "Telekinesis", MagicEffectArchetype.TypeEnum.Telekinesis, none, mysticism, none, Role.Special, m, "TelekinesisEffect", Keep: true, KeepDelivery: true);
            case "DetectAnimal":
                return new("DetectLife", "Detect Life", MagicEffectArchetype.TypeEnum.Script, none, mysticism, none, Role.Buff, m, "DetectLifeFriendExteriorSelfFF", Keep: true);
            case "Reanimate":
                return new("Reanimate", "Reanimate", MagicEffectArchetype.TypeEnum.Reanimate, none, C, none, Role.Special, Level(m), "ReanimateFFAimed50", Keep: true, KeepDelivery: true);
            case "CureCommonDisease": case "CureBlightDisease": case "CureCorprusDisease":
                return new("CureDisease", "Cure Disease", MagicEffectArchetype.TypeEnum.CureDisease, none, R, none, Role.Special, 0, "AlchCureDisease");
            case "CurePoison":
                return new("CurePoison", "Cure Poison", MagicEffectArchetype.TypeEnum.CurePoison, none, R, none, Role.Special, 0, "AlchCurePoison");
            case "CureParalyzation":
                return new("CureParalysis", "Cure Paralysis", MagicEffectArchetype.TypeEnum.CureParalysis, none, R, none, Role.Special, 0, "AlchCurePoison");
            case "DisintegrateArmor":
                return new("DisintegrateArmor", "Disintegrate Armor", Peak, ActorValue.DamageResist, D, ActorValue.ResistMagic, Role.Debuff, m * 3);
            case "DisintegrateWeapon":
                return new("DisintegrateWeapon", "Disintegrate Weapon", MagicEffectArchetype.TypeEnum.Disarm, none, D, ActorValue.ResistMagic, Role.Special, 0);
            case "Lock": case "Open":
                return new(n, n, MagicEffectArchetype.TypeEnum.Script, none, A, none, Role.Special, m, "WaterbreathingFFSelf", Script: "TMTesLockEffect");
            case "Mark": case "Recall":
                return new(n, n, MagicEffectArchetype.TypeEnum.Script, none, mysticism, none, Role.Special, 0, "WaterbreathingFFSelf", Script: "TMTesRecallEffect");
            case "DivineIntervention": case "AlmsiviIntervention":
                return new(n, n == "DivineIntervention" ? "Divine Intervention" : "Almsivi Intervention", MagicEffectArchetype.TypeEnum.Script, none, mysticism, none,
                    Role.Special, 0, "WaterbreathingFFSelf", Script: "TMTesInterventionEffect");
            case "BoundCuirass": case "BoundHelm": case "BoundBoots": case "BoundShield": case "BoundGloves": case "BoundGreaves": case "BoundArmor":
            {
                // Skyrim has no bound armour: Skyrim's Daedric piece, worn while the effect lasts (TMTesBoundArmor).
                // A piece Skyrim hasn't got (greaves) is its protection instead.
                if (BoundPiece(n) is { } piece)
                    return new($"{n}Piece", n switch { "BoundGloves" => "Bound Gauntlets", "BoundHelm" => "Bound Helm", _ => "Bound " + n[5..] },
                        MagicEffectArchetype.TypeEnum.Script, none, C, none, Role.Special, 0, "BoundSwordFFSelf", Script: "TMTesBoundArmor", Assoc: piece, KeepDelivery: true);
                return new("BoundArmor", "Bound Armor", Peak, ActorValue.DamageResist, C, none, Role.Buff, 40, "ArmorFFSelf0");
            }
        }
        if (n.StartsWith("Bound") && e.Assoc is { } weapon)
            return new($"Bound{weapon.ID:X6}", "Bound Weapon", MagicEffectArchetype.TypeEnum.Bound, none, C, none, Role.Special, 0, "BoundSwordFFSelf",
                Keep: true, Assoc: weapon, KeepDelivery: true);
        if (n.StartsWith("Summon") && e.Assoc is { } creature)
            return new($"Summon{creature.ID:X6}", "Summon", MagicEffectArchetype.TypeEnum.SummonCreature, none, C, none, Role.Special, 0, "SummonFlameAtronach",
                Keep: true, Assoc: creature, KeepDelivery: true);
        // A scripted effect (Oblivion's SEFF): a magic effect running the converted script (its properties are filled
        // by whoever converted the script).
        if (n == "ScriptEffect" && e.Sub is { Length: > 0 } script)
            return new($"Script{script}", e.Label is { Length: > 0 } label ? label : "Scripted Effect", MagicEffectArchetype.TypeEnum.Script, none, mysticism, none,
                e.Hostile ? Role.Harm : Role.Special, 0, "WaterbreathingFFSelf", Script: script);
        if (n.StartsWith("Resist") && ResistOf(n[6..]) is var (resistAv, resistLabel))
            return new($"Resist{resistLabel.Replace(" ", "")}", $"Resist {resistLabel}", Peak, resistAv, R, none, Role.Buff, resistAv == ActorValue.DamageResist ? m * 2 : m, "WaterbreathingFFSelf");
        if (n.StartsWith("WeaknessTo") && ResistOf(n[10..]) is var (weakAv, weakLabel))
            return new($"Weakness{weakLabel.Replace(" ", "")}", $"Weakness to {weakLabel}", Peak, weakAv, D, none, Role.Debuff, weakAv == ActorValue.DamageResist ? m * 2 : m);
        return null;
    }

    static readonly ActorValue mysticism = ActorValue.Alteration;

    /// <summary>A magic effect sound of the named kind (Mutagen's enum: Draw/Sheathe, Charge, Ready, Release, CastLoop, OnHit).</summary>
    static MagicEffectSound Sound(string type, FormKey sound)
    {
        var s = new MagicEffectSound { Sound = new FormLink<ISoundDescriptorGetter>(sound) };
        var prop = typeof(MagicEffectSound).GetProperty(nameof(MagicEffectSound.Type))!;
        prop.SetValue(s, Enum.Parse(prop.PropertyType, type));
        return s;
    }

    readonly Dictionary<string, Armor?> boundPieces = new();

    /// <summary>The Skyrim Daedric piece a bound armour effect gives: weightless, worthless, its own record.</summary>
    FormKey? BoundPiece(string effect)
    {
        if (boundPieces.TryGetValue(effect, out var hit)) return hit?.FormKey;
        string? vanillaId = effect switch
        {
            "BoundCuirass" => "ArmorDaedricCuirass", "BoundHelm" => "ArmorDaedricHelmet", "BoundBoots" => "ArmorDaedricBoots",
            "BoundGloves" => "ArmorDaedricGauntlets", "BoundShield" => "ArmorDaedricShield", _ => null,
        };
        Armor? piece = null;
        if (vanillaId is not null && skyrim.Armors.FirstOrDefault(a => a.EditorID == vanillaId) is { } source)
        {
            piece = mod.Armors.DuplicateInAsNewRecord(source);
            piece.EditorID = prefix + effect + "Daedric";
            piece.Name = effect switch { "BoundGloves" => "Bound Gauntlets", "BoundHelm" => "Bound Helm", _ => "Bound " + effect[5..] };
            piece.Value = 0;
            piece.Weight = 0;
        }
        boundPieces[effect] = piece;
        return piece?.FormKey;
    }

    /// <summary>An influence effect's strength (Morrowind's 1-100 magnitude) as the highest level it affects.</summary>
    static float Level(float magnitude) => Math.Clamp(magnitude / 2 + 5, 5, 60);

    static Family FamilyOf(TesCast cast) => cast switch
    {
        TesCast.Ability or TesCast.Disease or TesCast.Constant or TesCast.WhenUsed => Family.Constant,
        TesCast.WhenStrikes => Family.Strike,
        _ => Family.Fired,
    };

    static Delivery DeliveryFor(TesEffect e, Plan p) =>
        p.KeepDelivery ? Delivery.Template
        : e.Range == TesRange.Self ? (e.Area > 0 && p.Role == Role.Harm ? Delivery.SelfArea : Delivery.Self)
        : e.Area > 0 ? Delivery.AimedArea : Delivery.Aimed;

    string TemplateFor(Plan p, Family family, Delivery delivery)
    {
        if (family == Family.Strike) return p.Strike ?? "EnchMagickaDamageFFContact";
        if (family == Family.Constant) return p.Constant ?? (p.Keep && p.Type != MagicEffectArchetype.TypeEnum.Script ? p.Fired : "EnchFortifyHealthConstantSelf");
        if (p.Element is { } el)
            return delivery switch
            {
                Delivery.AimedArea => el == "Shock" ? "ShockDamageFFAimedArea" : $"{el}DamageFFAimedArea",
                Delivery.SelfArea => el == "Shock" ? "ShockDamageFFAimedArea" : $"{el}DamageFFSelfArea100",
                _ => $"{el}DamageFFAimed",
            };
        return p.Fired;
    }

    /// <summary>The Skyrim effect for a plan, used one way and delivered one way (made once and shared).</summary>
    MagicEffect? EffectFor(Plan p, Family family, Delivery delivery, TesEffect e)
    {
        string key = $"{p.Key}|{family}|{delivery}";
        if (effects.TryGetValue(key, out var existing)) return existing;
        string templateId = TemplateFor(p, family, delivery);
        if (!vanilla.TryGetValue(templateId, out var template)) { Skip($"template {templateId}"); return null; }
        var m = template.Duplicate(mod.GetNextFormKey());
        m.EditorID = $"{prefix}Effect{p.Key}{family}{(delivery == Delivery.Template ? "" : delivery.ToString())}";
        m.Name = p.Label;
        m.MagicSkill = p.School;
        m.ResistValue = p.Resist;
        if (family == Family.Constant) { m.CastType = CastType.ConstantEffect; m.TargetType = TargetType.Self; }
        else if (family == Family.Strike) { m.CastType = CastType.FireAndForget; m.TargetType = TargetType.Touch; }
        else
        {
            m.CastType = CastType.FireAndForget;
            if (delivery != Delivery.Template) m.TargetType = delivery is Delivery.Self or Delivery.SelfArea ? TargetType.Self : TargetType.Aimed;
        }
        if (p.Script is { } script)
        {
            m.Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script };
            m.Projectile.Clear();
            var entry = new ScriptEntry { Name = script };
            if (p.Key is "Lock") entry.Properties.Add(new ScriptBoolProperty { Name = "LockIt", Data = true });
            if (script == "TMTesBoundArmor" && p.Assoc is { } piece) entry.Properties.Add(new ScriptObjectProperty { Name = "Piece", Object = new FormLink<ISkyrimMajorRecordGetter>(piece) });
            if (p.Key is "Mark" or "Recall")
            {
                entry.Properties.Add(new ScriptObjectProperty { Name = "Anchor", Object = Anchor().ToLink<ISkyrimMajorRecordGetter>() });
                entry.Properties.Add(new ScriptObjectProperty { Name = "AnchorSet", Object = markSet!.ToLink<ISkyrimMajorRecordGetter>() });
                entry.Properties.Add(new ScriptBoolProperty { Name = "IsMark", Data = p.Key == "Mark" });
            }
            if (p.Key is "DivineIntervention" or "AlmsiviIntervention")
            {
                entry.Properties.Add(new ScriptObjectProperty { Name = "Markers", Object = (p.Key == "DivineIntervention" ? DivineMarkers : AlmsiviMarkers).ToLink<ISkyrimMajorRecordGetter>() });
                entry.Properties.Add(new ScriptObjectProperty { Name = "WorldSpot", Object = WorldSpot.ToLink<ISkyrimMajorRecordGetter>() });
            }
            m.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        }
        else if (p.Assoc is { } a)
        {
            if (p.Type == MagicEffectArchetype.TypeEnum.SummonCreature) m.Archetype = new MagicEffectSummonCreatureArchetype { Association = new FormLink<INpcGetter>(a) };
            else m.Archetype = new MagicEffectBoundArchetype { Association = new FormLink<IWeaponGetter>(a) };
        }
        else if (!p.Keep)
        {
            m.Archetype = p.Type == Peak ? new MagicEffectPeakValueModArchetype { ActorValue = p.Av } : new MagicEffectArchetype { Type = p.Type, ActorValue = p.Av };
            m.SecondActorValue = ActorValue.None;
            m.SecondActorValueWeight = 0;
            m.Conditions.Clear();
            m.VirtualMachineAdapter = null;
            m.Keywords = null;
        }
        var flags = m.Flags & ~(MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental | MagicEffect.Flag.Recover | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoMagnitude);
        flags |= p.Role switch
        {
            Role.Harm => MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental,
            Role.Debuff => MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental | MagicEffect.Flag.Recover,
            Role.Buff => MagicEffect.Flag.Recover,
            _ => template.Flags & (MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental | MagicEffect.Flag.Recover),
        };
        if (p.Magnitude == 0) flags |= MagicEffect.Flag.NoMagnitude;
        m.Flags = flags;
        if (Look?.Invoke(e) is { } look)
        {
            // The game's own visuals and sounds in place of Skyrim's (its hand glow, portal, light, screen effect).
            if (look.CastingArt is { } cast) m.CastingArt.SetTo(cast); else m.CastingArt.Clear();
            if (look.HitArt is { } hit) m.HitEffectArt.SetTo(hit); else m.HitEffectArt.Clear();
            m.CastingLight.Clear();
            m.ImageSpaceModifier.Clear();
            m.ImpactData.Clear();
            m.DualCastArt.Clear();
            m.HitShader.Clear();
            m.EnchantShader.Clear();
            m.Sounds = [.. look.Sounds.Select(s => Sound(s.Type, s.Sound))];
        }
        mod.MagicEffects.Add(m);
        return effects[key] = m;
    }

    void Skip(string what) => Skipped[what] = Skipped.GetValueOrDefault(what) + 1;

    /// <summary>The magic effect one effect becomes on something cast or used this way (a potion's, an ingredient's).</summary>
    public MagicEffect? EffectOf(TesEffect e, TesCast cast)
    {
        if (PlanFor(e) is not { } p) return null;
        var family = FamilyOf(cast);
        return EffectFor(p, family, family == Family.Fired ? DeliveryFor(e, p) : Delivery.Self, e);
    }

    PlacedObject Anchor()
    {
        if (RecallAnchor is null)
        {
            RecallAnchor = new PlacedObject(mod)
            {
                EditorID = prefix + "RecallAnchor",
                Base = new FormLinkNullable<IPlaceableObjectGetter>(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x34)),   // XMarkerHeading
                Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            markSet = new GlobalShort(mod) { EditorID = prefix + "RecallAnchorSet", Data = 0 };
            mod.Globals.Add(markSet);
        }
        return RecallAnchor;
    }

    List<Effect> Effects(IEnumerable<TesEffect> source, Family family, out TargetType target)
    {
        var list = new List<Effect>();
        target = family == Family.Constant ? TargetType.Self : family == Family.Strike ? TargetType.Touch : TargetType.Self;
        bool first = true;
        bool selfSpell = false;
        foreach (var e in source)
        {
            if (PlanFor(e) is not { } p) { Skip(e.Name.EndsWith("Skill") || e.Name.EndsWith("Attribute") ? $"{e.Name} {e.Sub}" : e.Name); continue; }
            var delivery = family == Family.Fired ? DeliveryFor(e, p) : Delivery.Self;
            // Skyrim delivers a spell's effects one way: later effects follow the first (Morrowind and Oblivion let
            // each effect have its own range).
            if (!first && family == Family.Fired && delivery != Delivery.Template)
                delivery = selfSpell ? (delivery == Delivery.SelfArea ? Delivery.SelfArea : Delivery.Self)
                                     : (e.Area > 0 ? Delivery.AimedArea : Delivery.Aimed);
            if (EffectFor(p, family, delivery, e) is not { } mgef) continue;
            if (first && family == Family.Fired) { target = mgef.TargetType; selfSpell = target == TargetType.Self; }
            first = false;
            list.Add(new Effect
            {
                BaseEffect = mgef.ToNullableLink<IMagicEffectGetter>(),
                Data = new EffectData
                {
                    Magnitude = p.Magnitude,
                    Duration = family == Family.Constant ? 0 : Math.Max(0, e.Duration),
                    Area = family == Family.Fired ? Math.Max(0, e.Area) : 0,
                },
            });
        }
        return list;
    }

    // ---- records ----

    /// <summary>A spell, ability, disease or power; null when none of its effects carry over.</summary>
    public Spell? Spell(string edid, string? name, TesCast cast, IEnumerable<TesEffect> source, int cost)
    {
        var family = FamilyOf(cast);
        var list = Effects(source, family, out var target);
        if (list.Count == 0) return null;
        var spell = new Spell(mod)
        {
            EditorID = edid,
            Name = name,
            ObjectBounds = new ObjectBounds(),
            Type = cast switch
            {
                TesCast.Ability or TesCast.Constant or TesCast.WhenUsed => SpellType.Ability,
                TesCast.Disease => SpellType.Disease,
                TesCast.Power => SpellType.Power,
                TesCast.LesserPower => SpellType.LesserPower,
                _ => SpellType.Spell,
            },
            CastType = family == Family.Constant ? CastType.ConstantEffect : CastType.FireAndForget,
            TargetType = target,
            ChargeTime = cast == TesCast.Spell ? 0.5f : 0,
            Description = "",
            Effects = [.. list],
            Flags = SpellDataFlag.ManualCostCalc,
            BaseCost = (uint)Math.Clamp(cost, 0, 2000),
        };
        if (cast == TesCast.Spell) spell.EquipmentType = eitherHand;
        else if (cast is TesCast.Power or TesCast.LesserPower) spell.EquipmentType = voice;
        mod.Spells.Add(spell);
        SpellCount++;
        return spell;
    }

    /// <summary>A scroll (Morrowind's and Oblivion's cast-once enchantments on paper).</summary>
    public Scroll? Scroll(string edid, string? name, IEnumerable<TesEffect> source, string? model, float weight, uint value)
    {
        var list = Effects(source, Family.Fired, out var target);
        if (list.Count == 0) return null;
        var scroll = new Scroll(mod)
        {
            EditorID = edid,
            Name = name,
            ObjectBounds = new ObjectBounds(),
            CastType = CastType.FireAndForget,
            TargetType = target,
            EquipmentType = eitherHand,
            Weight = weight,
            Value = value,
            Effects = [.. list],
            Flags = SpellDataFlag.ManualCostCalc,
            Description = "",
        };
        if (model is not null) scroll.Model = new Model { File = model };
        mod.Scrolls.Add(scroll);
        return scroll;
    }

    /// <summary>
    /// A weapon's or apparel's enchantment: on strike for weapons (charge for the item's charge amount), constant for
    /// worn items, cast for staffs. A cast-when-used enchantment grants, while the item is worn, a lesser power casting its effects.
    /// </summary>
    public ObjectEffect? Enchantment(string edid, string? name, TesCast cast, IEnumerable<TesEffect> source, int cost)
    {
        if (cast == TesCast.WhenUsed)
        {
            if (!powers.TryGetValue(edid, out var power))
            {
                if (Spell(edid + "Power", name, TesCast.LesserPower, source, cost) is not { } made) return null;
                powers[edid] = power = made;
            }
            var used = new ObjectEffect(mod)
            {
                EditorID = edid,
                Name = name,
                ObjectBounds = new ObjectBounds(),
                CastType = CastType.ConstantEffect,
                TargetType = TargetType.Self,
                EnchantType = ObjectEffect.EnchantTypeEnum.Enchantment,
                Flags = ObjectEffect.Flag.NoAutoCalc,
                Effects = [new Effect { BaseEffect = GrantEffect(power).ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
            };
            mod.ObjectEffects.Add(used);
            EnchantmentCount++;
            return used;
        }
        var family = cast == TesCast.WhenStrikes ? Family.Strike : cast == TesCast.Staff ? Family.Fired : Family.Constant;
        var list = Effects(source, family, out var target);
        if (list.Count == 0) return null;
        var ench = new ObjectEffect(mod)
        {
            EditorID = edid,
            Name = name,
            ObjectBounds = new ObjectBounds(),
            CastType = family == Family.Constant ? CastType.ConstantEffect : CastType.FireAndForget,
            TargetType = target,
            EnchantType = cast == TesCast.Staff ? ObjectEffect.EnchantTypeEnum.StaffEnchantment : ObjectEffect.EnchantTypeEnum.Enchantment,
            Flags = ObjectEffect.Flag.NoAutoCalc,
            EnchantmentCost = (uint)Math.Clamp(cost, 0, 5000),
            Effects = [.. list],
        };
        mod.ObjectEffects.Add(ench);
        EnchantmentCount++;
        return ench;
    }

    readonly Dictionary<FormKey, MagicEffect> grants = new();

    /// <summary>The constant effect that lends a worn item's lesser power (TMTesGrantPower); one per power.</summary>
    MagicEffect GrantEffect(Spell power)
    {
        if (grants.TryGetValue(power.FormKey, out var hit)) return hit;
        var m = vanilla["EnchFortifyHealthConstantSelf"].Duplicate(mod.GetNextFormKey());
        m.EditorID = $"{power.EditorID}Grant";
        m.Name = power.Name?.String;
        m.Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script };
        m.Flags = (m.Flags | MagicEffect.Flag.NoMagnitude | MagicEffect.Flag.HideInUI) & ~(MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental);
        m.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMTesGrantPower", Properties = [new ScriptObjectProperty { Name = "Power", Object = power.ToLink<ISkyrimMajorRecordGetter>() }] }],
        };
        mod.MagicEffects.Add(m);
        return grants[power.FormKey] = m;
    }
}
