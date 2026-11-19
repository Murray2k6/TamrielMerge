using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's item condition and repair (docs/script-gaps.md O68; Skyrim/ItemWear.cs for the records, TMWearOb for the rules
/// and their sources): each weapon's and armour piece's Health as its maximum condition, the repair hammer as a usable tool,
/// and a "Repair" topic for everyone with the Repair AI service. Oblivion.esm leaves most of the repair settings to the
/// engine: the fallbacks below are the defaults in Oblivion.exe, read from its setting table, and hold unless a plugin
/// sets them (the plugins' value wins, as in the game).
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>The repair hammer (Oblivion.esm MISC RepairHammer).</summary>
    static readonly FormKey ObRepairHammer = new(ModKey.FromFileName("Oblivion.esm"), 0x00000C);
    /// <summary>The sounds UIArmorWeaponRepair and UIArmorWeaponRepairBreak.</summary>
    static readonly FormKey ObRepairSound = new(ModKey.FromFileName("Oblivion.esm"), 0x06D9A4), ObRepairBreakSound = new(ModKey.FromFileName("Oblivion.esm"), 0x00545E);

    ItemWear? wear;
    Faction? repairersTag;

    ItemWear Wear => wear ??= new ItemWear(mod, "OB");
    /// <summary>Everyone with the Repair service (the Repair topic's condition).</summary>
    Faction RepairersTag => repairersTag ??= Skyrim.CoinTrade.Tag(mod, "OBRepairers", "Repairers");

    /// <summary>A person with the Repair AI service offers the topic. The flag is read by name, as the Training one is.</summary>
    void MakeRepairer(Npc npc, Ob.INpcGetter n)
    {
        if (n.AIData?.BuySellServices.ToString().Contains("Repair") == true)
            npc.Factions.Add(new RankPlacement { Faction = RepairersTag.ToLink<IFactionGetter>(), Rank = 0 });
    }

    /// <summary>The repair hammer as a usable tool: no quality or uses, it breaks by chance (TMWearOb).</summary>
    IMajorRecord HammerItem(string edid, Ob.IMiscellaneousGetter m)
    {
        float weight = m.Data?.Weight ?? 0;
        var mesh = ItemMesh(m.Model?.File, weight);
        string file = mesh is not null ? SkyrimModelPath(mesh.Path) : StandIns.Misc(m.Name, edid);
        return Wear.Tool(edid, m.Name, weight, (uint)Math.Max(0, m.Data?.Value ?? 0), Bounds(mesh), file, 0, 0);
    }

    /// <summary>A converted item's maximum condition (the record's Health).</summary>
    void WearItem(Ob.IOblivionMajorRecordGetter rec, IMajorRecord? item)
    {
        if (item is Weapon && rec is Ob.IWeaponGetter { Data: { } w }) Wear.Add(item, (int)w.Health);
        else if (item is Armor && rec is Ob.IArmorGetter { Data: { } a }) Wear.Add(item, (int)a.Health);
    }

    /// <summary>"Repair": the service, for anyone in OBRepairers. Silent, as the barter service is here.</summary>
    void RepairTopic()
    {
        Wear.RepairTopic(obDialogueQuest!, RepairersTag, "Repair");
        DialogueTopics++;
    }

    /// <summary>An Oblivion string game setting: the plugins' last value, else the engine's.</summary>
    string GmstText(string name, string fallback) =>
        plugins.SelectMany(p => p.GameSettings).LastOrDefault(g => string.Equals(g.EditorID, name, StringComparison.OrdinalIgnoreCase)) is Ob.IGameSettingStringGetter { Data.Length: > 0 } s ? s.Data! : fallback;

    /// <summary>The quest's script with Oblivion's settings (call when every item exists).</summary>
    void FinishWear()
    {
        ScriptFloatProperty F(string property, string setting, float fallback) => new() { Name = property, Data = Gmst(setting, fallback) };
        ScriptStringProperty S(string property, string setting, string fallback) => new() { Name = property, Data = GmstText(setting, fallback) };
        var properties = new List<ScriptProperty>
        {
            new ScriptObjectProperty { Name = "Mastery", Object = MasteryQuest.ToLink<ISkyrimMajorRecordGetter>() },
            F("DamageToWeaponPercentage", "fDamageToWeaponPercentage", 0.01f),
            F("DamageToArmorPercentage", "fDamageToArmorPercentage", 0.5f),
            F("MaxArmorRating", "fMaxArmorRating", 85),
            F("DamageWeaponMult", "fDamageWeaponMult", 0.5f),
            F("DamageStrengthBase", "fDamageStrengthBase", 0.75f),
            F("DamageStrengthMult", "fDamageStrengthMult", 0.5f),
            F("DamageSkillBase", "fDamageSkillBase", 0.2f),
            F("DamageSkillMult", "fDamageSkillMult", 1.5f),
            F("RepairArmorerBase", "fRepairArmorerBase", 0),
            F("RepairArmorerMult", "fRepairArmorerMult", 3),
            F("RepairStrengthMult", "fRepairStrengthMult", 0.1f),
            F("RepairCostMult", "fRepairCostMult", 0.9f),
            F("RepairSkillBreakBase", "fRepairSkillBreakBase", 30),
            F("RepairSkillBreakMult", "fRepairSkillBreakMult", -0.2f),
            F("RepairBreakApprenticeMult", "fRepairBreakApprenticeMult", 0.5f),
            F("ActorLuckSkillMult", "fActorLuckSkillMult", 0.4f),
            F("ActorLuckSkillBase", "iActorLuckSkillBase", -20),
            // UESP Oblivion:Armorer: 1.5 experience a use, in the experience the progression gives a skill use of 1 (25: an
            // unarmed hit, docs/progression.md), Skyrim's Smithing being on Skyrim's curve.
            new ScriptFloatProperty { Name = "RepairExperience", Data = 1.5f * 25 },
            // The engine's iArmorDamageHelmChance, Cuirass, Greaves, Gauntlets, Boots, Shield (Oblivion.exe; not in Oblivion.esm).
            new ScriptIntListProperty { Name = "PieceChance", Data = [10, 25, 15, 10, 10, 30] },
            S("CanNotInCombat", "sCanNotEquipRepairHammer", "You cannot repair items when enemies are nearby."),
            S("NoRepairMagic", "sNoRepairMagic", "Only a journeyman armorer or higher may repair magic items."),
            S("NoHammer", "sNoHammer", "You need more hammers."),
        };
        float conditionBase = Gmst("fDamageWeaponConditionBase", 0.5f), conditionMult = Gmst("fDamageWeaponConditionMult", 0.5f);
        // Rating x the health's share (UESP Oblivion:Armor); damage x (base + mult x share), (health% + 1) / 2 with these values.
        Wear.Finish("TMWearOb", "Repair all", secondPage: false, share => share, share => conditionBase + conditionMult * share,
            (ObSound(ObRepairSound)?.FormKey, ObSound(ObRepairBreakSound)?.FormKey),
            "This item is broken.", "Nothing you wear or wield needs repair.", "You do not have enough gold.", properties);
    }
}
