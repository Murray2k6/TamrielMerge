using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's 17 diseases as Skyrim diseases (Daggerfall Unity DiseaseEffect, MIT; its table is FALL.EXE 1.07.213's,
/// from offset 0x1C0053). Each is a Skyrim disease spell (so Skyrim's cures, potions, shrines and Cure Disease, cure it)
/// whose effect runs TMDfDisease: from the day after it is caught, each day a roll of its damage (minimum to maximum)
/// times each affected stat's factor comes off the attributes (given back when it ends), health, stamina (Daggerfall's
/// fatigue) and magicka, with "You feel somewhat bad."; most never end until cured, Caliron's Curse, Blood Rot and Wizard
/// Fever run 3-18 days; one caught at level 1 doesn't take. Named and described as Daggerfall has them (TEXT.RSC 100 +
/// the disease: "You have contracted ...").
/// </summary>
public static class DfDiseases
{
    /// <summary>Daggerfall Unity's Diseases, in its order (0-16).</summary>
    public static readonly string[] Names =
    [
        "Witches' Pox", "Plague", "Yellow Fever", "Stomach Rot", "Consumption", "Brain Fever", "Swamp Rot", "Caliron's Curse",
        "Cholera", "Leprosy", "Wound Rot", "Red Death", "Blood Rot", "Typhoid Fever", "Dementia", "Chrondiasis", "Wizard Fever",
    ];

    /// <summary>Daggerfall Unity DiseaseEffect.GetClassicDiseaseData: STR INT WIL AGI END PER SPD LUC HEA FAT SPL factors,
    /// minimum and maximum damage, days of symptoms (minimum, maximum; 255 never ends).</summary>
    static readonly int[][] Data =
    [
        [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 2, 10, 0xFF, 0xFF], // Witches' Pox
        [1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 30, 0xFF, 0xFF], // Plague
        [0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0, 5, 10, 0xFF, 0xFF], // Yellow Fever
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 5, 0xFF, 0xFF],  // Stomach Rot
        [1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 2, 10, 0xFF, 0xFF], // Consumption
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 5, 0xFF, 0xFF],  // Brain Fever
        [1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 2, 10, 0xFF, 0xFF], // Swamp Rot
        [1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 5, 10, 3, 18],      // Caliron's Curse
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 5, 30, 0xFF, 0xFF], // Cholera
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 5, 30, 0xFF, 0xFF], // Leprosy
        [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 2, 4, 0xFF, 0xFF],  // Wound Rot
        [0, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 2, 10, 0xFF, 0xFF], // Red Death
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 5, 10, 3, 18],      // Blood Rot
        [0, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 2, 10, 0xFF, 0xFF], // Typhoid Fever
        [0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 2, 10, 0xFF, 0xFF], // Dementia
        [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 5, 10, 0xFF, 0xFF], // Chrondiasis
        [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 3, 18],       // Wizard Fever
    ];

    /// <summary>Monsters' disease lists (Daggerfall Unity OnMonsterHit): B for rats and giant bats, C for zombies and mummies.</summary>
    public static readonly int[] ListB = [1, 3, 5];
    public static readonly int[] ListC = [1, 2, 3, 4, 5, 6, 8, 9, 11, 13, 14];

    /// <summary>The form list of the 17 diseases in Daggerfall's order (DFDiseases, made once), which quests and monsters
    /// name a disease by its place in.</summary>
    public static FormList List(SkyrimMod mod, DfText? text)
    {
        if (mod.FormLists.FirstOrDefault(f => f.EditorID == "DFDiseases") is { } made) return made;
        var list = new FormList(mod) { EditorID = "DFDiseases" };
        foreach (var s in Build(mod, text)) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(s.FormKey));
        mod.FormLists.Add(list);
        return list;
    }

    /// <summary>
    /// Daggerfall's lycanthropy (DFLycanthropy, made once): the werewolf's and the wereboar's infection (disease spells
    /// running TMDfLycanthropyInfection: the dream the next day, the change after three) and the werewolf's and wereboar's
    /// curse (abilities running TMDfLycanthropy on Skyrim's beast form), in that order.
    /// </summary>
    public static FormList Lycanthropy(SkyrimMod mod, ISkyrimModGetter skyrim, FormList diseases)
    {
        if (mod.FormLists.FirstOrDefault(f => f.EditorID == "DFLycanthropy") is { } made) return made;
        FormKey? Find<T>(IEnumerable<T> records, string editorId) where T : ISkyrimMajorRecordGetter => records.FirstOrDefault(r => r.EditorID == editorId)?.FormKey;
        var beastForm = Find(skyrim.Spells, "WerewolfChange");
        var immunity = Find(skyrim.Spells, "WerewolfImmunity");
        var beastRace = Find(skyrim.Races, "WerewolfBeastRace");
        var ring = Find(skyrim.Armors, "DA05HircinesRing");
        var vampireQuest = Find(skyrim.Quests, "PlayerVampireQuest");
        var housekeeping = skyrim.Quests.FirstOrDefault(q => q.VirtualMachineAdapter?.Scripts.Any(s => s.Name == "CompanionsHousekeepingScript") == true)?.FormKey;
        string[] kinds = ["Werewolf", "Wereboar"];
        var infections = new Spell[2];
        var curses = new Spell[2];
        var infectionScripts = new ScriptEntry[2];
        var curseScripts = new ScriptEntry[2];
        for (int k = 0; k < 2; k++)
        {
            infectionScripts[k] = new ScriptEntry { Name = "TMDfLycanthropyInfection", Properties = [new ScriptIntProperty { Name = "Kind", Data = k }] };
            infections[k] = Make(mod, $"DFLycanthropyInfection{kinds[k]}", "Lycanthropy", "", SpellType.Disease, infectionScripts[k], hostile: true);
            curseScripts[k] = new ScriptEntry { Name = "TMDfLycanthropy", Properties = [new ScriptIntProperty { Name = "Kind", Data = k }] };
            curses[k] = Make(mod, $"DFLycanthropy{kinds[k]}", kinds[k], $"You are a {kinds[k].ToLowerInvariant()}.", SpellType.Ability, curseScripts[k], hostile: false);
        }
        ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = key.ToLink<ISkyrimMajorRecordGetter>() };
        for (int k = 0; k < 2; k++)
        {
            var inf = infectionScripts[k].Properties;
            inf.Add(Obj("Infection", infections[k].FormKey));
            inf.Add(Obj("Other", infections[1 - k].FormKey));
            inf.Add(Obj("Curse", curses[k].FormKey));
            inf.Add(Obj("OtherCurse", curses[1 - k].FormKey));
            if (beastForm is { } bf) inf.Add(Obj("BeastForm", bf));
            if (vampireQuest is { } vq) inf.Add(Obj("VampireQuest", vq));
            var curse = curseScripts[k].Properties;
            curse.Add(Obj("Curse", curses[k].FormKey));
            if (beastForm is { } bf2) curse.Add(Obj("BeastForm", bf2));
            if (immunity is { } im) curse.Add(Obj("Immunity", im));
            if (housekeeping is { } hk) curse.Add(Obj("Housekeeping", hk));
            if (beastRace is { } br) curse.Add(Obj("BeastRace", br));
            if (ring is { } rg) curse.Add(Obj("HircinesRing", rg));
            curse.Add(Obj("Diseases", diseases.FormKey));
            // Skyrim's calendar globals (GameYear, GameMonth, GameDay), for Daggerfall's lunar phases.
            curse.Add(Obj("GameYear", new FormKey(skyrim.ModKey, 0x35)));
            curse.Add(Obj("GameMonth", new FormKey(skyrim.ModKey, 0x36)));
            curse.Add(Obj("GameDay", new FormKey(skyrim.ModKey, 0x37)));
        }
        var list = new FormList(mod) { EditorID = "DFLycanthropy" };
        foreach (var s in infections.Concat(curses)) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(s.FormKey));
        mod.FormLists.Add(list);
        return list;
    }

    static Spell Make(SkyrimMod mod, string id, string name, string description, SpellType type, ScriptEntry script, bool hostile)
    {
        var effect = new MagicEffect(mod)
        {
            EditorID = id + "Effect",
            Name = name,
            Description = description,
            Flags = (hostile ? MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental : 0) | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.NoHitEvent
                | (hostile ? MagicEffect.Flag.HideInUI : 0),
            Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script),
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
            VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [script] },
        };
        mod.MagicEffects.Add(effect);
        var spell = new Spell(mod)
        {
            EditorID = id, Name = name, Description = description, ObjectBounds = new ObjectBounds(),
            Type = type, CastType = CastType.ConstantEffect, TargetType = TargetType.Self, Flags = SpellDataFlag.ManualCostCalc,
            Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
        };
        mod.Spells.Add(spell);
        return spell;
    }

    /// <summary>The 17 disease spells in <paramref name="mod"/>, in Daggerfall's order (made once).</summary>
    public static List<Spell> Build(SkyrimMod mod, DfText? text)
    {
        var made = new List<Spell>();
        for (int i = 0; i < Names.Length; i++)
        {
            string id = "DFDisease" + new string(Names[i].Where(char.IsLetter).ToArray());
            if (mod.Spells.FirstOrDefault(s => s.EditorID == id) is { } existing) { made.Add(existing); continue; }
            var d = Data[i];
            // TEXT.RSC 100 + the disease: what Daggerfall says of having it.
            string description = text?[100 + i] is { Count: > 0 } lines ? string.Join(" ", lines).Replace("%pcn", "").Trim() : $"You have contracted {Names[i]}.";
            var entry = new ScriptEntry { Name = "TMDfDisease" };
            var factors = new ScriptIntListProperty { Name = "Factors" };
            factors.Data.AddRange(d.Take(11));
            entry.Properties.Add(factors);
            entry.Properties.Add(new ScriptIntProperty { Name = "MinDamage", Data = d[11] });
            entry.Properties.Add(new ScriptIntProperty { Name = "MaxDamage", Data = d[12] });
            entry.Properties.Add(new ScriptIntProperty { Name = "DaysMin", Data = d[13] });
            entry.Properties.Add(new ScriptIntProperty { Name = "DaysMax", Data = d[14] });
            var effect = new MagicEffect(mod)
            {
                EditorID = id + "Effect",
                Name = Names[i],
                Description = description,
                Flags = MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea | MagicEffect.Flag.NoHitEvent,
                Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script),
                CastType = CastType.ConstantEffect,
                TargetType = TargetType.Self,
                VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] },
            };
            mod.MagicEffects.Add(effect);
            var spell = new Spell(mod)
            {
                EditorID = id,
                Name = Names[i],
                Description = description,
                ObjectBounds = new ObjectBounds(),
                Type = SpellType.Disease,
                CastType = CastType.ConstantEffect,
                TargetType = TargetType.Self,
                Flags = SpellDataFlag.ManualCostCalc,
                Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
            };
            mod.Spells.Add(spell);
            // The effect ends its own disease (its days over, or caught at level 1).
            entry.Properties.Add(new ScriptObjectProperty { Name = "Disease", Object = spell.ToLink<ISkyrimMajorRecordGetter>() });
            made.Add(spell);
        }
        return made;
    }
}
