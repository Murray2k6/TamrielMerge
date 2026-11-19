using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's vampirism, corprus and lycanthropy: what its engine does about them beyond its scripts. The scripts
/// carry the cycles themselves and are translated with the rest (docs/morrowind-scripts.md): VampireCheck counts the
/// three days of a clan's Porphyric Hemophilia ("vampire blood aundae/berne/quarra") and starts Vampire_&lt;clan&gt;_PC,
/// which gives the abilities ("vampire attributes", "vampire skills", "vampire immunities", "vampire sun damage",
/// "vampire touch", the clan's "specials") and sets PCVampire and VampClan; Vampire_Cure_PC takes them back;
/// vampdreamScript dreams; Bloodmoon's WereCheckScript counts Sanies Lupinus' three days and sets PCWerewolf,
/// WereChangeScript changes the player at 21:00 and back at 6:00 (BecomeWerewolf / UndoWerewolf: Skyrim's beast form)
/// and werehungerScript takes 23% of the health an hour until an NPC is killed (GetWerewolfKills). The engine's part:
/// <list type="bullet">
/// <item>the magic effects Vampirism (133), Sun Damage (135) and Corprus (132) as scripted effects (TesMagic:
/// TMMwVampirism, TMMwSunDamage, TMMwCorprus), with Morrowind's values from its records, settings and Morrowind.ini
/// (<see cref="AfflictionProperties"/>, <see cref="FinishAfflictions"/>);</item>
/// <item>the corprus disease as an ability no cure of Skyrim's takes, its attribute effects landing a day at a time
/// (<see cref="IsCorprusDisease"/>; OpenMW activespells.cpp / spelleffects.cpp);</item>
/// <item>the vampire look: a person whose head is a vampire body part (BYDT's vampire flag, OpenMW npcanimation.cpp
/// getVampireHead) is built as Skyrim's vampire form of its race (<see cref="HasVampireHead"/>, MwActors); the player
/// is swapped to it while the Vampirism effect runs (TMMwAfflictions);</item>
/// <item>the player alias on the quest MWAfflictions (TMMwAfflictions): the werewolf change's kills, stats and
/// witnesses (OpenMW NpcStats::setWerewolf, Player::setWerewolfStats, MechanicsManager::setWerewolf), the corprus
/// cure's undoing, the vampire race swap.</item>
/// </list>
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>Morrowind's effect indices (OpenMW ESM::MagicEffect): Drain Attribute 17, Fortify Attribute 79, Corprus 132.</summary>
    const int DrainAttributeEffect = 17, FortifyAttributeEffect = 79;

    /// <summary>A disease or blight (SPDT type 2 or 3) carrying the Corprus effect: Morrowind's "corprus".</summary>
    static bool IsCorprusDisease(Tes3Record rec) => rec.Type == "SPEL" && SpellType(rec) is 2 or 3 && HasEffect(rec, CorprusEffect);

    /// <summary>A person whose head (BNAM) is one of Morrowind's vampire body parts (BYDT byte 1, the vampire flag).</summary>
    bool HasVampireHead(Tes3Record npc) =>
        npc.Str("BNAM") is { Length: > 0 } head && world.Objects.TryGetValue(head, out var part) && part.Type == "BODY"
        && part.Get("BYDT") is { Data.Length: >= 2 } bydt && bydt.Data[1] != 0;

    /// <summary>Skyrim's race of a Morrowind race, and its vampire form (NordRace, NordRaceVampire), where both exist.</summary>
    (ScriptObjectListProperty Base, ScriptObjectListProperty Vampire) VampireRaceLists()
    {
        VanillaNpc("EncBandit");   // opens the masters
        var bases = new ScriptObjectListProperty { Name = "BaseRaces" };
        var vampires = new ScriptObjectListProperty { Name = "VampireRaces" };
        foreach (var skyRace in MwRaces.Values.Distinct())
        {
            var plain = skyrimEsm!.Races.FirstOrDefault(r => r.EditorID == skyRace);
            var vampire = skyrimEsm.Races.FirstOrDefault(r => r.EditorID == skyRace + "Vampire");
            if (plain is null || vampire is null) continue;
            bases.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(plain.FormKey) });
            vampires.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(vampire.FormKey) });
        }
        return (bases, vampires);
    }

    /// <summary>
    /// The properties of the scripted effects' scripts as TesMagic makes each effect (its ScriptProperties hook):
    /// TMMwVampirism needs nothing; TMMwSunDamage gets the sun's hours and the weathers' glare from Morrowind.ini and
    /// fMagicSunBlockedMult; TMMwCorprus is filled when the plugin is finished (its spell doesn't exist yet while its
    /// own effect is being made: <see cref="FinishAfflictions"/>).
    /// </summary>
    void AfflictionProperties(string script, ScriptEntry entry)
    {
        if (script != "TMMwSunDamage") return;
        entry.Properties.Add(new ScriptObjectProperty { Name = "GameHour", Object = new FormLink<ISkyrimMajorRecordGetter>(Sk(0x38)) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "SunriseTime", Data = IniFloat("Weather", "Sunrise Time", 6) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "SunsetTime", Data = IniFloat("Weather", "Sunset Time", 18) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "SunriseDuration", Data = Math.Max(0.01f, IniFloat("Weather", "Sunrise Duration", 2)) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "SunsetDuration", Data = Math.Max(0.01f, IniFloat("Weather", "Sunset Duration", 2)) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "BlockedMult", Data = SettingFloat("fMagicSunBlockedMult", 0.5f) });
        var weathers = new ScriptObjectListProperty { Name = "Weathers" };
        var glare = new ScriptFloatListProperty { Name = "GlareView" };
        var made = MwWeathers();
        for (int i = 0; i < made.Length && i < WeatherKinds.Length; i++)
        {
            weathers.Objects.Add(new ScriptObjectProperty { Object = made[i].ToLink<ISkyrimMajorRecordGetter>() });
            glare.Data.Add(IniFloat(WeatherKinds[i].Section, "Glare View", 1));
        }
        entry.Properties.Add(weathers);
        entry.Properties.Add(glare);
    }

    GlobalFloat? corprusWorsenings;

    /// <summary>MWCorprusWorsenings: how many times the player's corprus has landed (TMMwCorprus counts, the cure clears).</summary>
    GlobalFloat CorprusWorseningsGlobal()
    {
        if (corprusWorsenings is not null) return corprusWorsenings;
        corprusWorsenings = new GlobalFloat(mod) { EditorID = "MWCorprusWorsenings", Data = 0 };
        mod.Globals.Add(corprusWorsenings);
        return corprusWorsenings;
    }

    /// <summary>The corprus record's Drain Attribute and Fortify Attribute effects: the attribute and the magnitude (the
    /// mean of its minimum and maximum, as every converted effect's is).</summary>
    (List<int> DrainAttributes, List<float> DrainMagnitudes, List<int> FortifyAttributes, List<float> FortifyMagnitudes) CorprusEffects()
    {
        var drainAttributes = new List<int>(); var drainMagnitudes = new List<float>();
        var fortifyAttributes = new List<int>(); var fortifyMagnitudes = new List<float>();
        foreach (var rec in world.Objects.Values.Where(IsCorprusDisease).OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
            foreach (var e in rec.All("ENAM"))
            {
                if (e.Data.Length < 24) continue;
                int effect = BitConverter.ToInt16(e.Data, 0);
                sbyte attribute = (sbyte)e.Data[3];
                if (effect is not (DrainAttributeEffect or FortifyAttributeEffect) || attribute < 0 || attribute > 7) continue;
                float magnitude = (BitConverter.ToInt32(e.Data, 16) + BitConverter.ToInt32(e.Data, 20)) / 2f;
                if (effect == DrainAttributeEffect) { drainAttributes.Add(attribute); drainMagnitudes.Add(magnitude); }
                else { fortifyAttributes.Add(attribute); fortifyMagnitudes.Add(magnitude); }
            }
        return (drainAttributes, drainMagnitudes, fortifyAttributes, fortifyMagnitudes);
    }

    bool afflictionsFinished;

    /// <summary>
    /// After every spell is converted: TMMwCorprus's properties on the Corprus effects (the corprus spell, its drains
    /// and fortifies, sMagicCorprusWorsens, the worsening count), and the quest MWAfflictions with the player alias
    /// (TMMwAfflictions) for the werewolf change, the corprus cure and the vampire look.
    /// </summary>
    void FinishAfflictions()
    {
        if (afflictionsFinished) return;
        afflictionsFinished = true;
        var corprusSpell = world.Objects.Values.Where(IsCorprusDisease).OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(r => MwSpell(r.Id)).OfType<Spell>().FirstOrDefault();
        var (drainAttributes, drainMagnitudes, fortifyAttributes, fortifyMagnitudes) = CorprusEffects();
        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        Quest? stateFor = scriptWorld is not null ? StateQuest() : null;
        var corprusEffects = mod.MagicEffects.Where(m => m.VirtualMachineAdapter?.Scripts.Any(s => s.Name == "TMMwCorprus") == true).ToList();
        foreach (var entry in corprusEffects.Select(m => m.VirtualMachineAdapter!.Scripts.First(s => s.Name == "TMMwCorprus")))
        {
            if (corprusSpell is not null) entry.Properties.Add(Obj("Disease", corprusSpell.FormKey));
            entry.Properties.Add(new ScriptIntListProperty { Name = "DrainAttributes", Data = [.. drainAttributes] });
            entry.Properties.Add(new ScriptFloatListProperty { Name = "DrainMagnitudes", Data = [.. drainMagnitudes] });
            entry.Properties.Add(new ScriptIntListProperty { Name = "FortifyAttributes", Data = [.. fortifyAttributes] });
            entry.Properties.Add(new ScriptFloatListProperty { Name = "FortifyMagnitudes", Data = [.. fortifyMagnitudes] });
            entry.Properties.Add(Obj("Worsenings", CorprusWorseningsGlobal().FormKey));
            entry.Properties.Add(new ScriptStringProperty { Name = "WorsensMessage", Data = world.Settings.TryGetValue("sMagicCorprusWorsens", out var s) ? s.Str("STRV") ?? "" : "" });
            if (stateFor is not null) entry.Properties.Add(Obj("MwState", stateFor.FormKey));
        }
        if (corprusSpell is null) Problems.Add("afflictions: no corprus disease spell (Morrowind.esm's \"corprus\" not loaded or not converted)");

        // The player alias: TMMwAfflictions on MWAfflictions.
        var vampirism = mod.MagicEffects.FirstOrDefault(m => m.VirtualMachineAdapter?.Scripts.Any(s => s.Name == "TMMwVampirism") == true);
        var (bases, vampires) = VampireRaceLists();
        var quest = new Quest(mod)
        {
            EditorID = "MWAfflictions",
            Name = "Morrowind afflictions",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
        };
        quest.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRefKey) });
        quest.NextAliasID = 1;
        var alias = new ScriptEntry { Name = "TMMwAfflictions" };
        alias.Properties.Add(Obj("WerewolfKills", WerewolfKillsGlobal().FormKey));
        alias.Properties.Add(Obj("Crime", MwCrime.FormKey));
        if (stateFor is not null) alias.Properties.Add(Obj("MwState", stateFor.FormKey));
        if (MwGlobal("PCKnownWerewolf") is { } known) alias.Properties.Add(Obj("KnownWerewolf", known.FormKey));
        alias.Properties.Add(new ScriptStringProperty { Name = "AlarmMessage", Data = world.Settings.TryGetValue("sWerewolfAlarmMessage", out var am) ? am.Str("STRV") ?? "" : "" });
        alias.Properties.Add(new ScriptFloatProperty { Name = "AlarmRadius", Data = SettingFloat("fAlarmRadius", 2000) });
        alias.Properties.Add(new ScriptIntProperty { Name = "WerewolfBounty", Data = SettingInt("iWereWolfBounty", 1000) });
        alias.Properties.Add(Obj("BeastRace", Sk(0x0CDD84)));   // WerewolfBeastRace
        // Player::setWerewolfStats: the attributes' werewolf values, Morrowind's fWereWolf* settings (TMMwState's order).
        string[] werewolfSettings = ["fWereWolfStrength", "fWereWolfIntellegence", "fWereWolfWillPower", "fWereWolfAgility", "fWereWolfSpeed", "fWereWolfEndurance", "fWereWolfPersonality", "fWereWolfLuck"];
        float[] werewolfDefaults = [150, 0, 0, 150, 90, 150, 0, 25];
        alias.Properties.Add(new ScriptFloatListProperty { Name = "WerewolfAttributes", Data = [.. werewolfSettings.Select((name, i) => SettingFloat(name, werewolfDefaults[i]))] });
        if (vampirism is not null) alias.Properties.Add(Obj("VampirismEffect", vampirism.FormKey));
        alias.Properties.Add(bases);
        alias.Properties.Add(vampires);
        if (corprusSpell is not null) alias.Properties.Add(Obj("CorprusDisease", corprusSpell.FormKey));
        alias.Properties.Add(Obj("CorprusWorsenings", CorprusWorseningsGlobal().FormKey));
        alias.Properties.Add(new ScriptIntListProperty { Name = "CorprusDrainAttributes", Data = [.. drainAttributes] });
        alias.Properties.Add(new ScriptFloatListProperty { Name = "CorprusDrainMagnitudes", Data = [.. drainMagnitudes] });
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [alias],
                },
            ],
        };
        mod.Quests.Add(quest);
    }
}
