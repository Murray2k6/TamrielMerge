using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's alchemy apparatus (docs/game-systems.md "Alchemy apparatus", docs/script-gaps.md): the four APPA kinds
/// (AADT type 0 mortar and pestle, 1 alembic, 2 calcinator, 3 retort; quality, weight, value) become usable items on
/// Oblivion's pattern (ObMasteryPerks.ApparatusItem): an ingestible whose one effect runs TMMwApparatus, which gives the
/// item back and asks the quest MWAlchemy (TMMwAlchemy) to place Skyrim's alchemy workbench at the player and open it.
/// <para>
/// In Morrowind using any apparatus opens the alchemy window, unless in combat (OpenMW ActionAlchemy, sInventoryMessage3),
/// the best of each kind carried filling its slot (Alchemy::setAlchemist), and no potion can be made without a mortar
/// (sNotifyMessage45). So every kind opens the bench when a mortar is carried.
/// </para>
/// <para>
/// The strength is Morrowind's (OpenMW Alchemy::updateEffects, getAlchemyFactor, applyTools; TMMwAlchemy has the formula):
/// mortar quality x (Alchemy + Intelligence / 10 + Luck / 10) x fPotionStrengthMult, over fPotionT1MagMult for the
/// magnitude and fPotionT1DurMult for the duration, over the effect's base cost, then the alembic and calcinator on a
/// harmful effect, the retort and calcinator on any other. Skyrim's bench already applies the Alchemy skill, so what is
/// added is the ratio of that to a Journeyman's mortar (quality 1) alone, as a hidden perk's Mod Alchemy Effectiveness,
/// which Skyrim's own Benefactor and Poisoner use with a condition on each effect (entry 0x42 with
/// EPAlchemyEffectHasKeyword, measured in Skyrim.esm): one entry for effects with MagicAlchHarmful, one for the others. A
/// Fortify Alchemy ability, as on Oblivion's side, can't do this: it can't tell a harmful effect from another, and an
/// Apprentice's mortar (quality 0.5) needs less than one.
/// </para>
/// </summary>
public sealed partial class MwPluginBuilder
{
    readonly List<(Ingestible Item, int Kind, float Quality)> apparatus = [];
    Quest? alchemyQuest;
    GlobalFloat? brewActive;

    /// <summary>The quest holding TMMwAlchemy (its properties filled by <see cref="FinishApparatus"/>).</summary>
    Quest AlchemyQuest()
    {
        if (alchemyQuest is not null) return alchemyQuest;
        alchemyQuest = new Quest(mod)
        {
            EditorID = "MWAlchemy",
            Name = "Morrowind alchemy",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            VirtualMachineAdapter = new QuestAdapter { Scripts = [new ScriptEntry { Name = "TMMwAlchemy" }] },
        };
        mod.Quests.Add(alchemyQuest);
        return alchemyQuest;
    }

    /// <summary>A Morrowind apparatus as a usable item: AADT type, quality, weight, value.</summary>
    IMajorRecord ApparatusItem(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("AADT")!.Value;
        int kind = d.Int(0);
        float quality = d.Float(4), weight = d.Float(8);
        var mesh = ItemMesh(model, weight);
        var use = new MagicEffect(mod)
        {
            EditorID = edid + "Use",
            Name = name,
            Flags = MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoDuration | MagicEffect.Flag.Painless,
            Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script },
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            MenuDisplayObject = new FormLinkNullable<IStaticGetter>(),
            Description = "",
        };
        mod.MagicEffects.Add(use);
        var item = new Ingestible(mod)
        {
            EditorID = edid,
            Name = name,
            Weight = weight,
            Value = (uint)Math.Max(0, d.Int(12)),
            ObjectBounds = Bounds(mesh),
            ConsumeSound = new FormLink<ISoundDescriptorGetter>(Sk(0x0B6435)),   // ITMPotionUse, as Morrowind's potions
            Flags = Ingestible.Flag.NoAutoCalc | Ingestible.Flag.Medicine,
            Effects = [new Effect { BaseEffect = use.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = 0, Area = 0, Duration = 0 } }],
        };
        if (mesh is not null) item.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Ingestibles.Add(item);
        use.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts =
            [
                new ScriptEntry
                {
                    Name = "TMMwApparatus",
                    Properties =
                    [
                        new ScriptObjectProperty { Name = "Item", Object = item.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "Alchemy", Object = AlchemyQuest().ToLink<ISkyrimMajorRecordGetter>() },
                    ],
                },
            ],
        };
        apparatus.Add((item, kind, quality));
        return item;
    }

    /// <summary>1 while a Morrowind apparatus is in use (the perk's condition).</summary>
    GlobalFloat BrewActive()
    {
        if (brewActive is not null) return brewActive;
        brewActive = new GlobalFloat(mod) { EditorID = "MWBrewActive", Data = 0 };
        mod.Globals.Add(brewActive);
        return brewActive;
    }

    /// <summary>The hidden perk whose Mod Alchemy Effectiveness entries multiply a brew while MWBrewActive is 1: entry 0 the
    /// effects with MagicAlchHarmful, entry 1 the others (TMMwAlchemy sets both). One tab, as Skyrim's Benefactor and
    /// Poisoner have (Skyrim.esm: entry 0x42, function Multiply Value, one tab, the conditions on tab 0).</summary>
    Perk BrewPerk()
    {
        var perk = new Perk(mod) { EditorID = "MWBrewPerk", Name = "Morrowind apparatus", Playable = false, Hidden = true, NumRanks = 1 };
        foreach (bool harmful in new[] { true, false })
        {
            var active = new GetGlobalValueConditionData(); active.Global = new FormLinkOrIndex<IGlobalGetter>(active, BrewActive().FormKey);
            var effect = new EPAlchemyEffectHasKeywordConditionData(); effect.Keyword = new FormLinkOrIndex<IKeywordGetter>(effect, Sk(0x042509));   // MagicAlchHarmful
            var tab = new PerkCondition { RunOnTabIndex = 0 };
            tab.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = active });
            tab.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = harmful ? 1 : 0, Data = effect });
            var entry = new PerkEntryPointModifyValue
            {
                EntryPoint = APerkEntryPointEffect.EntryType.ModAlchemyEffectiveness,
                PerkConditionTabCount = 1,
                Modification = PerkEntryPointModifyValue.ModificationType.Multiply,
                Value = 1,
            };
            entry.Conditions.Add(tab);
            perk.Effects.Add(entry);
        }
        mod.Perks.Add(perk);
        return perk;
    }

    /// <summary>The magic effects' base costs (MGEF: INDX, MEDT byte 4), the last plugin's winning.</summary>
    Dictionary<int, float> MwEffectBaseCosts()
    {
        var costs = new Dictionary<int, float>();
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type == "MGEF"))
                if (r.Get("INDX") is { Data.Length: >= 4 } i && r.Get("MEDT") is { Data.Length: >= 8 } d) costs[i.Int()] = d.Float(4);
        return costs;
    }

    static float Median(List<float> values)
    {
        values.Sort();
        return values.Count == 0 ? 0 : values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    /// <summary>Makes every apparatus (shops, cells and scripts have asked for some) and fills TMMwAlchemy: the workbench,
    /// the perk, each kind's list best first, Morrowind's settings, and the median base cost of the ingredients' effects
    /// (IRDT): a potion's tools add to its magnitude and duration in points, so their share depends on the effect, which
    /// Skyrim's perk can't see; the median effect stands for all.</summary>
    void FinishApparatus()
    {
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "APPA").ToList()) Item(rec.Id);
        if (apparatus.Count == 0) return;
        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        var entry = AlchemyQuest().VirtualMachineAdapter!.Scripts[0];
        void Kind(string list, string qualities, int kind)
        {
            var sorted = apparatus.Where(x => x.Kind == kind).OrderByDescending(x => x.Quality).ToList();
            var formList = new FormList(mod) { EditorID = "MWApparatus" + list };
            foreach (var x in sorted) formList.Items.Add(x.Item.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(formList);
            entry.Properties.Add(Obj(list, formList.FormKey));
            entry.Properties.Add(new ScriptFloatListProperty { Name = qualities, Data = [.. sorted.Select(x => x.Quality)] });
        }
        Kind("Mortars", "MortarQualities", 0);
        Kind("Alembics", "AlembicQualities", 1);
        Kind("Calcinators", "CalcinatorQualities", 2);
        Kind("Retorts", "RetortQualities", 3);
        var baseCosts = MwEffectBaseCosts();
        var ingredientCosts = new List<float>();
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "INGR"))
            if (rec.Get("IRDT") is { Data.Length: >= 24 } d)
                for (int i = 0; i < 4; i++)
                    if (baseCosts.TryGetValue(d.Int(8 + i * 4), out float cost) && cost > 0) ingredientCosts.Add(cost);
        entry.Properties.Add(Obj("PlayerRef", PlayerRefKey));
        entry.Properties.Add(Obj("Lab", Sk(0x0BAD0C)));   // Skyrim's alchemy workbench, as TMObMastery places it
        entry.Properties.Add(Obj("BrewPerk", BrewPerk().FormKey));
        entry.Properties.Add(Obj("Active", BrewActive().FormKey));
        if (scriptWorld is not null) entry.Properties.Add(Obj("MwState", StateQuest().FormKey));
        entry.Properties.Add(new ScriptFloatProperty { Name = "StrengthMult", Data = SettingFloat("fPotionStrengthMult", 0.5f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "T1MagMult", Data = SettingFloat("fPotionT1MagMult", 1.5f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "T1DurMult", Data = SettingFloat("fPotionT1DurMult", 0.5f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "MedianBaseCost", Data = ingredientCosts.Count == 0 ? 1 : Median(ingredientCosts) });
        entry.Properties.Add(new ScriptStringProperty { Name = "NeedMortar", Data = SettingText("sNotifyMessage45", "You need a Mortar and Pestle to use") });
        entry.Properties.Add(new ScriptStringProperty { Name = "NoBattle", Data = SettingText("sInventoryMessage3", "You cannot make potions during battle.") });
    }
}
