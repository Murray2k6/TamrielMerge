using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The references a plugin's scripts and quests hold, kept where Skyrim can give them out, checked just before each
/// converted plugin is written (2026-11-02, a user's Papyrus log: 86,646 errors "Element of property BDoor on script
/// TMDfQuestSites ... cannot be bound because &lt;nullptr form&gt;"; every one of Daggerfall's 43,323 quest-site street doors
/// was flagged persistent but stood in an ordinary overland cell's persistent group, where Skyrim never loads it at the
/// start, while the same quests' dungeon doors and map markers in the worldspace's persistent cell bound fine; the same
/// held the 266 Redguard torches TMRgISLAND_TORCH names, all temporary, and a Daggerfall rider marker made temporary).
/// <list type="bullet">
/// <item>Every reference something holds is persistent: script properties (on any record, the references' own too), and
/// whatever quests, packages, scenes, form lists and dialogue name. A temporary reference held so is made persistent.</item>
/// <item>Every persistent reference of a worldspace stands in the worldspace's persistent cell, as the Creation Kit puts
/// them: one in an overland cell's persistent group is moved there (Skyrim places the persistent cell's references at
/// their own positions).</item>
/// </list>
/// </summary>
public static class HeldReferences
{
    const int PersistentFlag = 0x400;

    /// <summary>What <see cref="Prepare"/> changed, for the build's log.</summary>
    public sealed record Result(int MadePersistent, int MovedToWorldCell, int NoWorldCell);

    public static Result Prepare(SkyrimMod mod)
    {
        var held = new HashSet<FormKey>();
        void Add(IFormLinkContainerGetter? links)
        {
            if (links is null) return;
            foreach (var l in links.EnumerateFormLinks())
                if (!l.IsNull) held.Add(l.FormKey);
        }
        foreach (var r in mod.EnumerateMajorRecords())
        {
            if (r is IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } vm }) Add(vm);
            if (r is IQuestGetter or IPackageGetter or ISceneGetter or IFormListGetter or IDialogResponsesGetter) Add(r);
        }
        int made = 0, moved = 0, noCell = 0;
        void Persist(IPlaced p)
        {
            if (p is SkyrimMajorRecord m && ((int)m.SkyrimMajorRecordFlags & PersistentFlag) == 0)
            {
                m.SkyrimMajorRecordFlags |= (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag;
                made++;
            }
        }
        // Interiors: what is held into the cell's persistent group.
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            foreach (var p in cell.Temporary.Where(p => held.Contains(p.FormKey)).ToList())
            {
                cell.Temporary.Remove(p);
                Persist(p);
                cell.Persistent.Add(p);
            }
        // Worldspaces: what is held, and every persistent reference, into the worldspace's persistent cell.
        foreach (var ws in mod.Worldspaces)
            foreach (var cell in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
            {
                var toWorld = cell.Persistent.Concat(cell.Temporary.Where(p => held.Contains(p.FormKey))).ToList();
                if (toWorld.Count == 0) continue;
                if (ws.TopCell is not { } top) { noCell += toWorld.Count; continue; }
                foreach (var p in toWorld)
                {
                    if (!cell.Persistent.Remove(p)) cell.Temporary.Remove(p);
                    Persist(p);
                    top.Persistent.Add(p);
                    moved++;
                }
            }
        return new Result(made, moved, noCell);
    }

    /// <summary>
    /// Script list properties with nothing in them left out (they are None in game either way): Skyrim logs each as "cannot
    /// be initialized because the value is the incorrect type" on every load (2026-11-02: 6,553 such lines in a user's
    /// Papyrus log, TMDfQuestSites, tmdfquest and TMMwVendor's empty lists). Returns how many.
    /// </summary>
    public static int DropEmptyLists(SkyrimMod mod)
    {
        int dropped = 0;
        static bool Empty(ScriptProperty p) => p switch
        {
            ScriptObjectListProperty l => l.Objects.Count == 0,
            ScriptIntListProperty l => l.Data.Count == 0,
            ScriptFloatListProperty l => l.Data.Count == 0,
            ScriptBoolListProperty l => l.Data.Count == 0,
            ScriptStringListProperty l => l.Data.Count == 0,
            _ => false,
        };
        void Clean(IEnumerable<ScriptEntry> scripts)
        {
            foreach (var script in scripts) dropped += script.Properties.RemoveAll(Empty);
        }
        foreach (var r in mod.EnumerateMajorRecords())
        {
            if (r is not IHaveVirtualMachineAdapter { VirtualMachineAdapter: { } vm }) continue;
            Clean(vm.Scripts);
            if (vm is QuestAdapter quest) foreach (var alias in quest.Aliases) Clean(alias.Scripts);
        }
        return dropped;
    }

    /// <summary><see cref="Prepare"/> and <see cref="DropEmptyLists"/>, said in the log when they changed anything.</summary>
    public static void Prepare(SkyrimMod mod, Action<string> log)
    {
        int empty = DropEmptyLists(mod);
        if (empty > 0) log($"  empty script lists ({mod.ModKey.FileName}): {empty:N0} left out");
        var r = Prepare(mod);
        if (r.MadePersistent + r.MovedToWorldCell + r.NoWorldCell == 0) return;
        log($"  held references ({mod.ModKey.FileName}): {r.MadePersistent:N0} made persistent (a script, quest, package or list holds them), " +
            $"{r.MovedToWorldCell:N0} moved into their worldspace's persistent cell{(r.NoWorldCell > 0 ? $", {r.NoWorldCell:N0} left (their worldspace has no persistent cell here)" : "")}");
    }
}
