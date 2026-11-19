using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's item condition and repair (Skyrim/ItemWear.cs for the records, TMWearMw for the rules and their sources):
/// each weapon's and armour piece's health (WPDT, AODT) as its maximum condition, the repair hammers and prongs (REPA) as
/// usable tools with their quality and uses (RIDT), and the "Repair" topic (sRepair) for every person with the repair service
/// (AIDT 0x20000). Morrowind.esm's 21 creatures with that service flag aren't given the topic, as merchant creatures aren't
/// given a shop (<see cref="IsVendor"/> takes NPC_ records only).
/// </summary>
public sealed partial class MwPluginBuilder
{
    ItemWear? wear;
    Faction? repairersTag;
    readonly List<(Npc Npc, Tes3Record Record)> repairerNpcs = [];

    ItemWear Wear => wear ??= new ItemWear(mod, "MW");
    /// <summary>Everyone with the repair service (the Repair topic's condition).</summary>
    Faction RepairersTag => repairersTag ??= Skyrim.CoinTrade.Tag(mod, "MWRepairers", "Repairers");

    /// <summary>Marks a person with the repair service as its actor is made.</summary>
    void MakeRepairer(Npc npc, Tes3Record rec)
    {
        if (rec.Type != "NPC_" || (Services(rec) & ServiceRepair) == 0) return;
        npc.Factions.Add(new RankPlacement { Faction = RepairersTag.ToLink<IFactionGetter>(), Rank = 0 });
        repairerNpcs.Add((npc, rec));
    }

    /// <summary>A repair item as a usable tool. RIDT: weight, value, uses, quality (OpenMW ESM::Repair::Data).</summary>
    IMajorRecord RepairTool(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("RIDT")!.Value;
        float weight = d.Float(0);
        var mesh = ItemMesh(model, weight);
        string file = mesh is not null ? SkyrimModelPath(mesh.Path) : (vendorStandIns ??= new Skyrim.ModelStandIns(SkyrimEsm)).Misc(name, edid);
        return Wear.Tool(edid, name, weight, (uint)Math.Max(0, d.Int(4)), Bounds(mesh), file, d.Float(12), d.Int(8));
    }

    /// <summary>A converted item's maximum condition. WPDT: weight, value, type (short), health (short); AODT: type, weight,
    /// value, health. A thrown weapon is a misc item here and has none.</summary>
    void WearItem(Tes3Record rec, IMajorRecord? item)
    {
        if (item is Weapon && rec.Get("WPDT") is { Data.Length: >= 12 } w) Wear.Add(item, BitConverter.ToUInt16(w.Data, 10));
        else if (item is Armor && rec.Get("AODT") is { Data.Length: >= 16 } a) Wear.Add(item, a.Int(12));
    }

    /// <summary>"Repair" (sRepair): the service, for anyone in MWRepairers. Silent, as Barter is.</summary>
    void RepairTopic()
    {
        Wear.RepairTopic(dialogueQuest!, RepairersTag, SettingText("sRepair", "Repair"));
        DialogueTopics++;
    }

    /// <summary>Armorer's use value for a repair (SKDT's first, ESM::Skill::Armorer_Repair), in the experience the progression
    /// gives a Morrowind use of 1 (25: an unarmed hit, docs/progression.md), Skyrim's Smithing being on Skyrim's curve.</summary>
    float ArmorerExperience()
    {
        float use = 0.4f;
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type == "SKIL"))
                if (r.Get("INDX") is { Data.Length: >= 4 } i && i.Int() == 1 && r.Get("SKDT") is { Data.Length: >= 12 } d) use = d.Float(8);
        return use * 25;
    }

    /// <summary>The quest's script with Morrowind's settings, and each repairer's stats (call when every item exists).</summary>
    void FinishWear()
    {
        ScriptFloatProperty F(string property, string setting, float fallback) => new() { Name = property, Data = Setting(setting, fallback) };
        ScriptStringProperty S(string property, string setting, string fallback) => new() { Name = property, Data = SettingText(setting, fallback) };
        var properties = new List<ScriptProperty>
        {
            F("WeaponDamageMult", "fWeaponDamageMult", 0.1f),
            F("CombatArmorMinMult", "fCombatArmorMinMult", 0.25f),
            F("RepairAmountMult", "fRepairAmountMult", 3),
            F("RepairMult", "fRepairMult", 1),
            F("FatigueBase", "fFatigueBase", 1.25f),
            F("FatigueMult", "fFatigueMult", 0.5f),
            F("DamageStrengthBase", "fDamageStrengthBase", 0.5f),
            F("DamageStrengthMult", "fDamageStrengthMult", 0.1f),
            F("UnarmoredBase1", "fUnarmoredBase1", 0.1f),
            F("UnarmoredBase2", "fUnarmoredBase2", 0.065f),
            F("BaseArmorSkill", "iBaseArmorSkill", 30),
            new ScriptFloatProperty { Name = "ArmorerUse", Data = ArmorerExperience() },
            S("RepairSuccess", "sRepairSuccess", "Your repair succeeded!"),
            S("RepairFailed", "sRepairFailed", "Your repair failed!"),
            S("UsedUp", "sNotifyMessage51", "The %s has been used up."),
            new ScriptObjectProperty { Name = "MediumArmor", Object = MediumArmorList().ToLink<ISkyrimMajorRecordGetter>() },
        };
        // The player's Morrowind stats live on TMMwState when the scripts are converted.
        if (scriptWorld is not null) properties.Add(new ScriptObjectProperty { Name = "MwState", Object = StateQuest().ToLink<ISkyrimMajorRecordGetter>() });
        // Morrowind's item condition scales armour rating and weapon damage alike (OpenMW getArmorRating, adjustWeaponDamage).
        Wear.Finish("TMWearMw", "More", secondPage: true, share => share, share => share,
            (SoundMarkerFor("Repair", loop: false), SoundMarkerFor("Repair Fail", loop: false)),
            SettingText("sInventoryMessage1", "This object is broken and cannot be equipped until fixed."),
            "Nothing you wear or wield needs repair.", "You do not have enough gold.", properties);
        var stats = new MwStats(world);
        foreach (var (npc, rec) in repairerNpcs)
        {
            var own = stats.Of(rec.Id);
            npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
            {
                Name = "TMMwRepairer",
                Properties =
                [
                    new ScriptIntProperty { Name = "Mercantile", Data = own?[8 + 24] ?? 0 },
                    new ScriptIntProperty { Name = "Luck", Data = own?[7] ?? 0 },
                    new ScriptIntProperty { Name = "Personality", Data = own?[6] ?? 0 },
                ],
            });
        }
    }
}
