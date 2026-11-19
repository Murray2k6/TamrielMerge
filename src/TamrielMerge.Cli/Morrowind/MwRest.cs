using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's rest rules (TMMwRest, on the quest MWRest; OpenMW apps/openmw/mwworld/worldimp.cpp canRest,
/// mwgui/waitdialog.cpp, mwmechanics/actors.cpp restoreDynamicStats):
/// <list type="bullet">
/// <item>the player may rest (sleep without a bed) anywhere but in a cell flagged "illegal to sleep here" (CELL DATA
/// flag 4, ESM::Cell::NoSleep: 741 of Morrowind's 1,340 interiors), where only waiting is allowed (sRestIllegal), and
/// never in the air or under water (sNotifyMessage1) or with enemies near (sNotifyMessage2; Skyrim refuses that itself).
/// Skyrim's wait is Morrowind's rest where resting is allowed, its wait where it isn't; a bed is a bed in both;</item>
/// <item>a rest restores Morrowind's amounts an hour: health 0.1 x Endurance, magicka fRestMagicMult x Intelligence
/// (none under Stunted Magicka: the Atronach sign), and fatigue - resting or waiting - 3,600 x (fFatigueReturnBase +
/// fFatigueReturnMult x (1 - encumbrance)) x fEndFatigueMult x Endurance, each as the share of Morrowind's own maximum
/// it is, applied to Skyrim's;</item>
/// <item>sleeping outside in a region with a sleep list (REGN BNAM, a levelled creature list) can be interrupted: a
/// roll of 0..hours-1 under fSleepRandMod x hours, at fSleepRestMod x hours (whole hours) before the end; then
/// sSleepInterrupt and 1 + rand(iNumberCreatures) of the list's creatures placed 220 units in front of the player
/// (OpenMW World::spawnRandomCreature).</item>
/// </list>
/// The region of an outside position is read from a grid the converter writes (one letter per Morrowind cell per row),
/// since Papyrus can't read a cell's region.
/// </summary>
public sealed partial class MwPluginBuilder
{
    Quest? restQuest;
    bool restFinished;

    /// <summary>Interiors where only waiting is allowed, and regions with a sleep list.</summary>
    public int NoSleepInteriors { get; private set; }
    public int SleepRegions { get; private set; }

    /// <summary>The quest holding TMMwRest (filled when the plugin is written, once every cell exists).</summary>
    Quest RestQuest()
    {
        if (restQuest is not null) return restQuest;
        restQuest = new Quest(mod)
        {
            EditorID = "MWRest",
            Name = "Morrowind rest",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            VirtualMachineAdapter = new QuestAdapter { Scripts = [new ScriptEntry { Name = "TMMwRest" }] },
        };
        mod.Quests.Add(restQuest);
        return restQuest;
    }

    /// <summary>After the cells exist: the no-sleep interiors, each region's sleep list and the grid of regions.</summary>
    void FinishRest()
    {
        if (restFinished) return;
        restFinished = true;
        var quest = RestQuest();
        var entry = quest.VirtualMachineAdapter!.Scripts[0];
        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };

        // Interiors flagged "illegal to sleep here" (CELL DATA flag 4; OpenMW ESM::Cell::NoSleep -> Rest_OnlyWaiting).
        var noSleep = new FormList(mod) { EditorID = "MWNoSleepCells" };
        foreach (var (name, cell) in interiorsByName)
            if (world.Interiors.TryGetValue(name, out var source) && (source.Flags & 4) != 0)
                noSleep.Items.Add(cell.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(noSleep);
        NoSleepInteriors = noSleep.Items.Count;

        // Each region's sleep list (REGN BNAM, ESM::Region::mSleepList): the levelled creature list as the actor that
        // places it (MwActors.LeveledCreature), indexed by its letter in the grid.
        var lists = new FormList(mod) { EditorID = "MWRegionSleepLists" };
        var letter = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in world.Objects.Values.Where(r => r.Type == "REGN").OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (rec.Str("BNAM") is not { Length: > 0 } listId) continue;
            if (Actor(listId) is not Npc placer) { Problems.Add($"rest: region {rec.Id}'s sleep list {listId} isn't a levelled creature list of this build"); continue; }
            // 'A'.. per list; past 'z' the grid's letters would leave the printable range.
            if (lists.Items.Count > 'z' - 'A') { Problems.Add($"rest: more than {'z' - 'A' + 1} regions with sleep lists; {rec.Id}'s dropped"); continue; }
            letter[rec.Id] = (char)('A' + lists.Items.Count);
            lists.Items.Add(placer.ToLink<ISkyrimMajorRecordGetter>());
        }
        mod.FormLists.Add(lists);
        SleepRegions = lists.Items.Count;

        // The grid: a string per row of Morrowind cells from the southernmost, a letter per cell from the westernmost
        // ('.' for no region or one without a sleep list).
        var rows = new List<string>();
        int minX = 0, minY = 0, width = 0;
        if (world.Exteriors.Count > 0)
        {
            minX = world.Exteriors.Keys.Min(k => k.X);
            minY = world.Exteriors.Keys.Min(k => k.Y);
            int maxX = world.Exteriors.Keys.Max(k => k.X), maxY = world.Exteriors.Keys.Max(k => k.Y);
            width = maxX - minX + 1;
            for (int y = minY; y <= maxY; y++)
            {
                var row = new string('.', width).ToCharArray();
                for (int x = minX; x <= maxX; x++)
                    if (world.Exteriors.TryGetValue((x, y), out var cell) && cell.Region is { } region && letter.TryGetValue(region, out var c))
                        row[x - minX] = c;
                rows.Add(new string(row));
            }
        }
        entry.Properties.Add(Obj("NoSleepCells", noSleep.FormKey));
        entry.Properties.Add(Obj("RegionSleepLists", lists.FormKey));
        entry.Properties.Add(new ScriptStringListProperty { Name = "RegionRows", Data = [.. rows] });
        entry.Properties.Add(new ScriptIntProperty { Name = "GridMinX", Data = minX });
        entry.Properties.Add(new ScriptIntProperty { Name = "GridMinY", Data = minY });
        entry.Properties.Add(new ScriptIntProperty { Name = "GridWidth", Data = width });
        entry.Properties.Add(Obj("PlayerRef", PlayerRefKey));
        entry.Properties.Add(Obj("GameHour", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x38)));
        entry.Properties.Add(Obj("GameDaysPassed", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x39)));
        // Morrowind's own values (GMST), as OpenMW reads them.
        entry.Properties.Add(new ScriptFloatProperty { Name = "SleepRandMod", Data = SettingFloat("fSleepRandMod", 0.25f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "SleepRestMod", Data = SettingFloat("fSleepRestMod", 0.3f) });
        entry.Properties.Add(new ScriptIntProperty { Name = "NumberCreatures", Data = SettingInt("iNumberCreatures", 1) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "RestMagicMult", Data = SettingFloat("fRestMagicMult", 0.15f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "FatigueReturnBase", Data = SettingFloat("fFatigueReturnBase", 2.5f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "FatigueReturnMult", Data = SettingFloat("fFatigueReturnMult", 0.02f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "EndFatigueMult", Data = SettingFloat("fEndFatigueMult", 0.04f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "LevelUpHealthEndMult", Data = SettingFloat("fLevelUpHealthEndMult", 0.1f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "PCbaseMagickaMult", Data = SettingFloat("fPCbaseMagickaMult", 1) });
        entry.Properties.Add(new ScriptStringProperty { Name = "RestIllegal", Data = SettingText("sRestIllegal", "Resting here is illegal. You'll need to find a bed.") });
        entry.Properties.Add(new ScriptStringProperty { Name = "OnlyOnGround", Data = SettingText("sNotifyMessage1", "You can only rest on solid ground.") });
        entry.Properties.Add(new ScriptStringProperty { Name = "SleepInterrupt", Data = SettingText("sSleepInterrupt", "Your rest has been interrupted.") });
        // Stunted Magicka: Morrowind's Atronach sign is Skyrim's Atronach Stone (the signs are the standing stones).
        VanillaNpc("EncBandit");   // opens the masters
        if (skyrimEsm!.Spells.FirstOrDefault(sp => sp.EditorID is { } e && e.Equals("doomAtronachAbility", StringComparison.OrdinalIgnoreCase)) is { } atronach)
            entry.Properties.Add(Obj("StuntedSign", atronach.FormKey));
        else Problems.Add("rest: Skyrim's doomAtronachAbility not found; Stunted Magicka won't stop a rest's magicka");
        // The player's kept attributes (Endurance, Intelligence, Strength, Willpower, Agility) when the scripts are
        // converted; without them TMMwRest reckons with Morrowind's usual starting value, 40.
        if (scriptWorld is not null) entry.Properties.Add(Obj("MwState", StateQuest().FormKey));
    }
}
