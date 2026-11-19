using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's vampirism (docs/script-gaps.md O63) and sigil stones (O62).
/// <list type="bullet">
/// <item>Vampirism is Oblivion's own records and script: Porphyric Hemophilia (VampDisease, Drain Fatigue 5), the
/// Vampirism25/50/75/100 abilities (VAMP 25..100, Sun Damage 0/1/4/8, Weakness to Fire 20..50, Resist Disease and
/// Paralysis 100, Resist Normal Weapons 5..20), the Att/Skills abilities (+5..+20 Strength, Willpower, Speed and seven
/// skills) and the four powers, all converted as spells (ObMagic), and VampireScript on the Vampire quest (translated),
/// which does the rest: the disease's three days and a sleep, +25% a day without feeding at the next sleep or wait,
/// back to 25% on feeding, the dreams, the Vampire Cure quest. TesMagic has no plan for VAMP and SUDG (Skyrim has no
/// such effects), so they are the converter's own: <see cref="VampirismEffect"/> (TMObVampirism: the stage into
/// TMObState for GetAV Vampirism, the vampire look, the Feed choice) and <see cref="SunDamageEffect"/> (TMObSunDamage).
/// The Feed choice is Skyrim's own VampireFeedBeds perk with the converter's fragment (<see cref="VampireFeedPerk"/>),
/// so Skyrim's PlayerVampireQuest (started only by its own disease's script, VampireDiseaseEffectScript) never runs.
/// Oblivion's vampire people (races Vampire and VampireRace, mortal stand-ins in ObContent) carry Skyrim's Vampire
/// keyword, which GetVampire, GetIsRace Vampire and TMObState.IsVampire test.</item>
/// <item>A sigil stone (SGST: an offensive touch effect for a weapon, a defensive self effect for apparel, its uses)
/// is a potion carrying <see cref="SigilStoneItem"/>'s effect (TMObSigilStone): the worn piece the player picks in a
/// message box is enchanted in place with the stone's enchantment (TesMagic.Enchantment of each effect) through SKSE's
/// WornObject.CreateEnchantment, and the stone is consumed (UESP Oblivion:Sigil Stones).</item>
/// </list>
/// </summary>
public sealed partial class ObPluginBuilder
{
    MagicEffect? vampirismEffect, sunDamageEffect;
    Perk? vampireFeedPerk;
    bool vampireFeedPerkMade;
    FormList? oblivionPlanes;
    Message? sigilMenu;
    readonly GlobalFloat?[] sigilSlots = new GlobalFloat?[9];

    /// <summary>Skyrim's Vampire keyword (on its vampire races; HasKeyword in conditions, Actor.HasKeyword in scripts).</summary>
    static readonly FormKey VampireKeyword = Sk(0x0A82BB);

    /// <summary>Oblivion's vampire races (Vampire, VampireRace: people with the vampire face; ObContent.RaceStandIns
    /// gives them the Imperial look).</summary>
    static bool IsVampireRace(string? editorId) => string.Equals(editorId, "Vampire", StringComparison.OrdinalIgnoreCase)
        || string.Equals(editorId, "VampireRace", StringComparison.OrdinalIgnoreCase);

    /// <summary>An Oblivion magic effect by its four-letter code.</summary>
    Ob.IMagicEffectGetter? ObEffect(string code)
    {
        magicEffectsByCode ??= records.Values.OfType<Ob.IMagicEffectGetter>().Where(m => m.EditorID is not null)
            .GroupBy(m => m.EditorID!).ToDictionary(g => g.Key, g => g.Last());
        return magicEffectsByCode.GetValueOrDefault(code);
    }

    // ---- vampirism ----

    /// <summary>A spell's VAMP and SUDG effects (which <see cref="TesMagic"/> plans nothing for and ObMagic.EffectsOf
    /// leaves out) as the converter's own effects on the made spell, at Oblivion's magnitudes (25..100, 1/4/8).</summary>
    void AddVampirism(Spell? spell, IEnumerable<Ob.IEffectGetter> source)
    {
        if (spell is null) return;
        foreach (var e in source)
        {
            if (e.Data is not { } d) continue;
            string code = d.MagicEffect.EDID.Type;
            if (code is not ("VAMP" or "SUDG")) continue;
            var mgef = code == "VAMP" ? VampirismEffect() : SunDamageEffect();
            spell.Effects.Add(new Effect { BaseEffect = mgef.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = d.Magnitude } });
        }
    }

    /// <summary>Oblivion's Vampirism effect (VAMP, 0018A3): a constant script effect running TMObVampirism (made once).
    /// Named "Vampirism" so HasMagicEffect VAMP finds it (ObScriptRecords.EffectList: OBEffect + the effect's name).</summary>
    MagicEffect VampirismEffect()
    {
        if (vampirismEffect is not null) return vampirismEffect;
        var m = new MagicEffect(mod)
        {
            EditorID = "OBEffectVampirism",
            Name = "Vampirism",
            Description = "You are a vampire.",
            Flags = MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.Painless,
            Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script),
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
        };
        var entry = new ScriptEntry { Name = "TMObVampirism" };
        if (VampireFeedPerk() is { } perk) entry.Properties.Add(new ScriptObjectProperty { Name = "FeedPerk", Object = perk.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Effect", Object = m.ToLink<ISkyrimMajorRecordGetter>() });
        m.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        mod.MagicEffects.Add(m);
        return vampirismEffect = m;
    }

    /// <summary>Oblivion's Sun Damage effect (SUDG, 0018A0): a constant script effect running TMObSunDamage, its
    /// magnitude the health a second in daylight (made once).</summary>
    MagicEffect SunDamageEffect()
    {
        if (sunDamageEffect is not null) return sunDamageEffect;
        var m = new MagicEffect(mod)
        {
            EditorID = "OBEffectSunDamage",
            Name = "Sun Damage",
            Description = "The sun burns you.",
            Flags = MagicEffect.Flag.Detrimental | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.Painless,
            Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script),
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
        };
        var entry = new ScriptEntry { Name = "TMObSunDamage" };
        entry.Properties.Add(new ScriptObjectProperty { Name = "OblivionPlanes", Object = OblivionPlanes().ToLink<ISkyrimMajorRecordGetter>() });
        m.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        mod.MagicEffects.Add(m);
        return sunDamageEffect = m;
    }

    /// <summary>The worldspaces of Oblivion's planes (no sun there; UESP Oblivion:Vampirism), filled by
    /// <see cref="FinishVampirism"/> once every worldspace is made.</summary>
    FormList OblivionPlanes()
    {
        if (oblivionPlanes is not null) return oblivionPlanes;
        oblivionPlanes = new FormList(mod) { EditorID = "OBOblivionPlanes" };
        mod.FormLists.Add(oblivionPlanes);
        return oblivionPlanes;
    }

    /// <summary>Every converted worldspace of Oblivion's planes (Oblivion.esm's Oblivion* worldspaces: the random
    /// planes, Kvatch's, Bruma's, the Deadlands) into the planes list.</summary>
    void FinishVampirism()
    {
        if (oblivionPlanes is null) return;
        oblivionPlanes.Items.Clear();
        foreach (var ws in mod.Worldspaces)
            if (ws.EditorID is { } id && id.StartsWith("OB", StringComparison.Ordinal) && id[2..].StartsWith("Oblivion", StringComparison.OrdinalIgnoreCase))
                oblivionPlanes.Items.Add(ws.ToLink<ISkyrimMajorRecordGetter>());
    }

    /// <summary>
    /// The Feed choice on a sleeper for an Oblivion vampire (made once): Skyrim's own VampireFeedBeds perk (0CF02C: an
    /// Add Activate Choice on a sleeping, uneaten adult NPC while the owner isn't feeding) with the converter's fragment
    /// script TMObVampireFeed in place of Skyrim's PRKF_VampireFeedBeds (which fed Skyrim's PlayerVampireQuest). Its
    /// "owner has the Vampire keyword" condition goes: TMObVampirism gives the perk with the first stage and takes it
    /// with the cure, whether or not the player's race has a vampire look. Null without the vanilla perk.
    /// </summary>
    Perk? VampireFeedPerk()
    {
        if (vampireFeedPerkMade) return vampireFeedPerk;
        vampireFeedPerkMade = true;
        if (Vanilla.Get<IPerkGetter>("VampireFeed") is not { } source) { Problems.Add("vampirism: Skyrim's VampireFeed perk not found"); return null; }
        var perk = mod.Perks.DuplicateInAsNewRecord(source);
        perk.EditorID = "OBVampireFeed";
        foreach (var effect in perk.Effects)
            foreach (var tab in effect.Conditions)
                tab.Conditions.RemoveAll(c => c.Data is HasKeywordConditionData k && k.Keyword.Link.FormKey == VampireKeyword);
        perk.VirtualMachineAdapter = new PerkAdapter
        {
            Version = 5,
            ObjectFormat = 2,
            Scripts = [new ScriptEntry { Name = "TMObVampireFeed" }],
            ScriptFragments = new PerkScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = "TMObVampireFeed",
                // As Skyrim's own perk fragment is bound (index 0, 0, 1, script, Fragment_0).
                Fragments = [new IndexedScriptFragment { FragmentIndex = 0, Unknown = 0, Unknown2 = 1, ScriptName = "TMObVampireFeed", FragmentName = "Fragment_0" }],
            },
        };
        return vampireFeedPerk = perk;
    }

    // ---- sigil stones ----

    /// <summary>The worn pieces a sigil stone offers, in the order TMObSigilStone reads the choice (then Cancel).</summary>
    static readonly string[] SigilChoices = ["Right hand", "Left hand", "Head", "Body", "Hands", "Feet", "Shield", "Amulet", "Ring"];

    /// <summary>TMOB_SigilSlot0..8: whether each choice's button shows (the script sets them before the message).</summary>
    GlobalFloat SigilSlot(int i) => sigilSlots[i] ??= ScriptGlobal($"TMOB_SigilSlot{i}");

    /// <summary>The sigil stone's message box (made once): a button per worn piece, shown when its slot global is 1, and Cancel.</summary>
    Message SigilMenu()
    {
        if (sigilMenu is not null) return sigilMenu;
        var m = new Message(mod) { EditorID = "TMObSigilStoneMenu", Description = "Which worn piece takes the sigil stone?", Flags = Message.Flag.MessageBox, MenuButtons = [] };
        for (int i = 0; i < SigilChoices.Length; i++)
        {
            var d = new GetGlobalValueConditionData();
            d.Global = new FormLinkOrIndex<IGlobalGetter>(d, SigilSlot(i).FormKey);
            m.MenuButtons.Add(new MessageButton
            {
                Text = SigilChoices[i],
                Conditions = [new ConditionFloat { Data = d, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
            });
        }
        m.MenuButtons.Add(new MessageButton { Text = "Cancel" });
        mod.Messages.Add(m);
        return sigilMenu = m;
    }

    /// <summary>An effect's magicka cost by Oblivion's formula (UESP Oblivion:Spell Making): base cost / 10 x
    /// magnitude^1.28 x duration x area x 0.15, each factor at least 1, x1.5 for a targeted effect.</summary>
    float ObEffectCost(Ob.IEffectGetter e)
    {
        if (e.Data is not { } d || ObEffect(d.MagicEffect.EDID.Type)?.Data is not { } md) return 0;
        float b = md.BaseCost / 10f;
        float m = MathF.Pow(Math.Max(d.Magnitude, 1), 1.28f);
        float dur = Math.Max(d.Duration, 1);
        float a = Math.Max(d.Area * 0.15f, 1);
        return b * m * dur * a * (d.Type.ToString() == "Target" ? 1.5f : 1);
    }

    /// <summary>
    /// A sigil stone as a potion that runs TMObSigilStone when used: its touch effect as an on-strike enchantment for a
    /// weapon (its charge the stone's uses times the strike's cost), its self effect as a constant enchantment for
    /// apparel (UESP Oblivion:Sigil Stones: the offensive effect "will automatically be used if you choose to enchant a
    /// weapon", the defensive one for "armor, clothing, or other apparel"). Oblivion's name, model, value and weight.
    /// </summary>
    IMajorRecord SigilStoneItem(string edid, Ob.ISigilStoneGetter s)
    {
        float weight = s.Data?.Weight ?? 1;
        var mesh = ItemMesh(s.Model?.File, weight);
        var strikes = s.Effects.Where(e => e.Data?.Type.ToString() == "Touch").ToList();
        var wears = s.Effects.Where(e => e.Data is not null && e.Data.Type.ToString() != "Touch").ToList();
        int cost = Math.Max(1, (int)Math.Round(strikes.Sum(ObEffectCost)));
        var weapon = strikes.Count > 0 ? Magic.Enchantment(edid + "Weapon", s.Name, TesCast.WhenStrikes, EffectsOf(strikes), cost) : null;
        var apparel = wears.Count > 0 ? Magic.Enchantment(edid + "Apparel", s.Name, TesCast.Constant, EffectsOf(wears), 0) : null;
        float charge = (s.Data?.Uses ?? 0) * cost;
        var mgef = new MagicEffect(mod)
        {
            EditorID = edid + "Use",
            Name = s.Name,
            Flags = MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoArea | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.Painless,
            Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script),
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
        };
        var entry = new ScriptEntry { Name = "TMObSigilStone" };
        if (weapon is not null) entry.Properties.Add(new ScriptObjectProperty { Name = "WeaponEnchantment", Object = weapon.ToLink<ISkyrimMajorRecordGetter>() });
        if (apparel is not null) entry.Properties.Add(new ScriptObjectProperty { Name = "ApparelEnchantment", Object = apparel.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptFloatProperty { Name = "WeaponCharge", Data = charge });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Menu", Object = SigilMenu().ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectListProperty
        {
            Name = "SlotShown",
            Objects = [.. Enumerable.Range(0, SigilChoices.Length).Select(i => new ScriptObjectProperty { Object = SigilSlot(i).ToLink<ISkyrimMajorRecordGetter>() })],
        });
        mgef.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        mod.MagicEffects.Add(mgef);
        var potion = new Ingestible(mod)
        {
            EditorID = edid,
            Name = s.Name,
            Weight = weight,
            Value = s.Data?.Value ?? 0,
            ObjectBounds = Bounds(mesh),
            ConsumeSound = new FormLink<ISoundDescriptorGetter>(Sk(0x0B6435)),
            Flags = Ingestible.Flag.NoAutoCalc | Ingestible.Flag.Medicine,
            // The effect lasts while the message box is up; the script dispels it when done.
            Effects = [new Effect { BaseEffect = mgef.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = 0, Duration = 30, Area = 0 } }],
        };
        potion.Model = new Model { File = mesh is not null ? SkyrimModelPath(mesh.Path) : StandIns.Misc(s.Name, edid) };
        entry.Properties.Add(new ScriptObjectProperty { Name = "Stone", Object = potion.ToLink<ISkyrimMajorRecordGetter>() });
        mod.Ingestibles.Add(potion);
        SigilStoneCount++;
        return potion;
    }

    public int SigilStoneCount { get; private set; }
}
