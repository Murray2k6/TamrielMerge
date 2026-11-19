using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The reference handles of a whole load order, not only the converted plugins' (user, 2026-11-01: "daggerfall has a
/// reference bug and hits the skyrim limit"; "it also depends on mods a user has"): Skyrim's masters, then every plugin
/// switched on, a plugin at a time, nothing of it kept. And what a plugin's persistent references are (<see cref="Why"/>),
/// to find the ones that needn't be.
/// </summary>
public static class HandleAudit
{
    static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>Handles at load past which the load order is warned of: room for what the game makes as it runs.</summary>
    public const long LoadOrderWarn = 850_000;

    static List<string> On(string list) => File.Exists(list)
        ? File.ReadAllLines(list).Where(l => l.StartsWith('*')).Select(l => l[1..].Trim()).Where(l => l.Length > 0).ToList() : [];

    /// <summary>The plugins switched on, Skyrim's masters first: Mod Organizer 2's selected profile when it has more on than
    /// Skyrim's own list (the user's mods live there), else Plugins.txt. Mod Organizer's also lists the mods' folders
    /// switched on, where those plugins are.</summary>
    public static (List<string> Plugins, List<string> Folders, string Source) EnabledPlugins()
    {
        string own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "Plugins.txt");
        var on = On(own);
        var folders = new List<string>();
        string source = "Plugins.txt";
        foreach (var profile in Setup.PluginList.Mo2ProfileFolders())
        {
            var theirs = On(Path.Combine(profile, "plugins.txt"));
            if (theirs.Count <= on.Count) continue;
            on = theirs;
            source = "Mod Organizer 2 profile " + Path.GetFileName(profile);
            string mods = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(profile)!)!, "mods");
            string modlist = Path.Combine(profile, "modlist.txt");
            folders = File.Exists(modlist) ? File.ReadAllLines(modlist).Where(l => l.StartsWith('+')).Select(l => Path.Combine(mods, l[1..].Trim())).Where(Directory.Exists).ToList() : [];
        }
        return ([.. Masters, .. on.Where(p => !Masters.Contains(p, StringComparer.OrdinalIgnoreCase))], folders, source);
    }

    /// <summary>The whole load order counted, as found by <see cref="EnabledPlugins"/>.</summary>
    public static long ReportLoadOrder(string data, Action<string> log)
    {
        var (plugins, folders, source) = EnabledPlugins();
        log($"Reference handles of the whole load order ({source}):");
        return Report(data, plugins, log, folders);
    }

    /// <summary>Counts the load order in <paramref name="data"/>, biggest first, and says how much of the limit is left.</summary>
    public static long Report(string data, IReadOnlyList<string> plugins, Action<string> log, IReadOnlyList<string>? folders = null, int top = 20)
    {
        var all = new List<HandleCount.Of>();
        var missing = new List<string>();
        foreach (var p in plugins)
        {
            string path = (folders ?? []).Select(f => Path.Combine(f, p)).FirstOrDefault(File.Exists) ?? Path.Combine(data, p);
            if (!File.Exists(path)) { missing.Add(p); continue; }
            try { all.Add(HandleCount.Count(path)); }
            catch (Exception e) { log($"  {p}: not read ({e.GetType().Name}: {e.Message.Split('\n')[0]})"); }
        }
        long total = all.Sum(c => c.Handles);
        foreach (var c in all.OrderByDescending(c => c.Handles).Take(top).Where(c => c.Handles > 0))
            log($"  {c.Handles,10:N0}  {c.Plugin}{(c.Master ? "" : $" (not a master: its {c.WorldRefs:N0} worldspace references count)")}");
        if (missing.Count > 0) log($"  {missing.Count} plugins switched on but not in Data: {string.Join(", ", missing.Take(6))}");
        log($"Load order: {all.Count} plugins, {total:N0} of {HandleCount.Limit:N0} reference handles at load ({100.0 * total / HandleCount.Limit:F1}%), {HandleCount.Limit - total:N0} left");
        // References in loaded cells and the game's own at run time take handles beyond these.
        if (total > LoadOrderWarn)
            log($"WARNING: this load order takes {total:N0} of Skyrim's {HandleCount.Limit:N0} reference handles before the game has placed anything: the plugins above take the most, so turn some of them off or the game may fail or crash");
        return total;
    }

    /// <summary>What <paramref name="path"/>'s persistent references are: by kind, interior or overland, with the count of
    /// each. A reference is told by what it does; those with nothing to say for themselves are the ones to look at.</summary>
    public static void Why(string path, Action<string> log, int top = 24)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var kinds = new Dictionary<string, long>();
        void Tally(IPlacedGetter r, string where)
        {
            string kind = r switch
            {
                IPlacedNpcGetter n => n.EnableParent is not null ? "person, enable-parented" : n.LinkedReferences.Count > 0 ? "person, linked" : "person",
                IPlacedObjectGetter o when o.TeleportDestination is not null => "load door",
                IPlacedObjectGetter o when o.MapMarker is not null => "map marker",
                IPlacedObjectGetter o when o.VirtualMachineAdapter is not null => "object with a script",
                IPlacedObjectGetter o when o.LinkedReferences.Count > 0 => "object, linked",
                IPlacedObjectGetter o when o.EnableParent is not null => "object, enable-parented",
                IPlacedObjectGetter o => "object: " + (o.EditorID is { Length: > 0 } e ? new string(e.TakeWhile(c => !char.IsDigit(c)).ToArray()) : Short(o.Base.FormKey)),
                _ => r.GetType().Name,
            };
            string key = $"{where}: {kind}";
            kinds[key] = kinds.GetValueOrDefault(key) + 1;
        }
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            foreach (var r in cell.Persistent) Tally(r, "interior");
        foreach (var w in mod.Worldspaces)
        {
            foreach (var cell in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                foreach (var r in cell.Persistent) Tally(r, "overland");
            if (w.TopCell is { } t)
                foreach (var r in t.Persistent) Tally(r, "overland");
        }
        log($"{Path.GetFileName(path)}: {kinds.Values.Sum():N0} persistent references");
        foreach (var (k, n) in kinds.OrderByDescending(kv => kv.Value).Take(top)) log($"  {n,9:N0}  {k}");
    }

    static string Short(Mutagen.Bethesda.Plugins.FormKey k) => $"base {k.ID:X6}";

    /// <summary>
    /// How many of <paramref name="paths"/>' persistent references nothing in those plugins points at: no script property,
    /// form list, quest, package, scene, dialogue, link or enable parent, and no script number that could be one's FormID
    /// (counted as a pointer whatever it means, so the figure errs on the side of keeping). Only counts.
    /// </summary>
    public static void Unneeded(IReadOnlyList<string> paths, Action<string> log)
    {
        var keys = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        var numbers = new HashSet<uint>();
        void Scripts(IHaveVirtualMachineAdapterGetter? o)
        {
            if (o?.VirtualMachineAdapter is not { } vm) return;
            foreach (var s in vm.Scripts)
                foreach (var p in s.Properties)
                    switch (p)
                    {
                        case IScriptIntPropertyGetter i: numbers.Add((uint)i.Data & 0xFFFFFF); break;
                        case IScriptIntListPropertyGetter l: foreach (int v in l.Data) numbers.Add((uint)v & 0xFFFFFF); break;
                    }
        }
        foreach (var path in paths)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            void Links(IEnumerable<Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter> records)
            {
                foreach (var r in records)
                {
                    foreach (var l in r.EnumerateFormLinks()) if (!l.IsNull) keys.Add(l.FormKey);
                    Scripts(r as IHaveVirtualMachineAdapterGetter);
                }
            }
            Links(mod.Quests); Links(mod.FormLists); Links(mod.Packages); Links(mod.Scenes); Links(mod.Messages);
            Links(mod.Activators); Links(mod.Npcs); Links(mod.Containers); Links(mod.Doors); Links(mod.MagicEffects); Links(mod.Perks);
            foreach (var t in mod.DialogTopics) foreach (var i in t.Responses) { foreach (var l in i.EnumerateFormLinks()) if (!l.IsNull) keys.Add(l.FormKey); }
            void Cell(ICellGetter c)
            {
                foreach (var r in c.Persistent.Concat(c.Temporary))
                {
                    if (r is IPlacedObjectGetter { VirtualMachineAdapter: not null } or IPlacedNpcGetter or IPlacedObjectGetter { EnableParent: not null } or IPlacedObjectGetter { LinkedReferences.Count: > 0 } or IPlacedObjectGetter { TeleportDestination: not null })
                    {
                        foreach (var l in r.EnumerateFormLinks()) if (!l.IsNull) keys.Add(l.FormKey);
                        Scripts(r as IHaveVirtualMachineAdapterGetter);
                    }
                }
            }
            foreach (var c in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells)) Cell(c);
            foreach (var w in mod.Worldspaces)
            {
                foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)) Cell(c);
                if (w.TopCell is { } t) Cell(t);
            }
            log($"read {Path.GetFileName(path)}: {keys.Count:N0} forms pointed at, {numbers.Count:N0} script numbers");
        }
        var byKind = new Dictionary<string, (long Pointed, long Free)>();
        void Tally(IPlacedGetter r, string where)
        {
            bool pointed = keys.Contains(r.FormKey) || numbers.Contains(r.FormKey.ID);
            string kind = r switch
            {
                IPlacedNpcGetter => "person",
                IPlacedObjectGetter o when o.TeleportDestination is not null => "load door",
                IPlacedObjectGetter o when o.MapMarker is not null => "map marker",
                IPlacedObjectGetter o when o.VirtualMachineAdapter is not null => "object with a script",
                IPlacedObjectGetter o when o.Base.FormKey.ID == 0x34 => "XMarkerHeading",
                _ => "other",
            };
            string key = $"{where}: {kind}";
            var (p, f) = byKind.GetValueOrDefault(key);
            byKind[key] = pointed ? (p + 1, f) : (p, f + 1);
        }
        foreach (var path in paths)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var c in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                foreach (var r in c.Persistent) Tally(r, "interior");
            foreach (var w in mod.Worldspaces)
            {
                foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                    foreach (var r in c.Persistent) Tally(r, "overland");
                if (w.TopCell is { } t) foreach (var r in t.Persistent) Tally(r, "overland");
            }
        }
        foreach (var (k, v) in byKind.OrderByDescending(kv => kv.Value.Free)) log($"  {k,-36} pointed at {v.Pointed,8:N0}   nothing points at {v.Free,8:N0}");
        log($"persistent: {byKind.Values.Sum(v => v.Pointed + v.Free):N0}, nothing points at {byKind.Values.Sum(v => v.Free):N0}");
    }
}
