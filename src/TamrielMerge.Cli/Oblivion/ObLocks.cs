using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Activator = Mutagen.Bethesda.Skyrim.Activator;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's locks (TMObLock): a locked door or container offers Oblivion's choice of picking by hand (Skyrim's
/// lockpicking) or Auto Attempt, rolled per tumbler with Oblivion.esm's settings. Oblivion's lock levels (0-100, 100
/// needs a key) set the tumblers and the Skyrim lock tier; its key goes with it. Oblivion's lockpick is Skyrim's.
/// </summary>
public sealed partial class ObPluginBuilder
{
    static readonly FormKey ObLockpick = new(ModKey.FromFileName("Oblivion.esm"), 0x00000A);
    static readonly FormKey SkyrimLockpick = new(ModKey.FromFileName("Skyrim.esm"), 0x00000A);
    Message? lockChoice;
    public int LockCount { get; private set; }

    Message LockChoice
    {
        get
        {
            if (lockChoice is not null) return lockChoice;
            lockChoice = new Message(mod)
            {
                EditorID = "OBLockChoice",
                Description = "This lock has %.0f tumblers.",
                Flags = Message.Flag.MessageBox,
                MenuButtons = [],
            };
            foreach (var b in new[] { "Pick the lock", "Auto Attempt", "Leave" }) lockChoice.MenuButtons.Add(new MessageButton { Text = b });
            mod.Messages.Add(lockChoice);
            return lockChoice;
        }
    }

    /// <summary>Oblivion's lock tiers (UESP Oblivion:Open: Very Easy 0-7, Easy 8-20, Average 21-40, Hard 41-80,
    /// Very Hard 81-99, 100 a key) as Skyrim's.</summary>
    static LockLevel SkyrimLock(int level) => level switch
    {
        <= 7 => LockLevel.Novice,
        <= 20 => LockLevel.Apprentice,
        <= 40 => LockLevel.Adept,
        <= 80 => LockLevel.Expert,
        < 100 => LockLevel.Master,
        _ => LockLevel.RequiresKey,
    };

    /// <summary>The references the scripts lock (themselves, by their base's script, or by name): an unlocked Oblivion lock
    /// for those Oblivion left without one, so Lock sets Oblivion's level (TMObState.LockAt).</summary>
    HashSet<FormKey>? lockedByName;
    public int ScriptLocks { get; private set; }

    bool ScriptMayLock(Ob.IPlacedObjectGetter r)
    {
        lockedByName ??= [.. translations.Values.Concat(resultTranslations.Values).Concat(stageTranslations.Values)
            .SelectMany(o => o.LockTargets).Where(t => t.Length > 0).Select(t => scriptWorld?.KeyOf(t)).OfType<FormKey>()];
        if (lockedByName.Contains(r.FormKey)) return true;
        return records.GetValueOrDefault(r.Base.FormKey) is { } rec && ObScriptWorld.ScriptLink(rec) is { } s
            && translations.TryGetValue(s, out var o) && o.LockTargets.Contains("");
    }

    /// <summary>Puts an Oblivion reference's lock on the placed door, container or swinging door (unlocked, for one a
    /// script may lock).</summary>
    void ApplyLock(Ob.IPlacedObjectGetter r, PlacedObject placed)
    {
        var lk = r.Lock;
        if (lk is null && !ScriptMayLock(r)) return;
        var b = placed.Base.FormKey;
        bool swing = swingDoors.Values.Any(s => s?.FormKey == b);
        if (!swing && !mod.Doors.ContainsKey(b) && !mod.Containers.ContainsKey(b)) return;
        int level = lk?.LockLevel ?? 0;
        var key = lk is null || lk.Key.IsNull ? null : Item(lk.Key.FormKey) as Key;
        var entry = new ScriptEntry { Name = "TMObLock" };
        entry.Properties.Add(new ScriptIntProperty { Name = "LockLevel", Data = level });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Choice", Object = LockChoice.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Lockpick", Object = new FormLink<ISkyrimMajorRecordGetter>(SkyrimLockpick) });
        if (key is not null) entry.Properties.Add(new ScriptObjectProperty { Name = "DoorKey", Object = key.ToLink<ISkyrimMajorRecordGetter>() });
        // Its own script's OnActivate takes the place of the default activation (Activate in it does that): the lock only
        // holds the level.
        if (records.GetValueOrDefault(r.Base.FormKey) is { } baseRec && ObScriptWorld.ScriptLink(baseRec) is { } own && translations.TryGetValue(own, out var ownScript) && ownScript.Activates)
            entry.Properties.Add(new ScriptBoolProperty { Name = "ScriptActivates", Data = true });
        if (swing)
        {
            entry.Properties.Add(new ScriptBoolProperty { Name = "Swing", Data = true });
            entry.Properties.Add(new ScriptBoolProperty { Name = "Locked", Data = lk is not null });
        }
        else if (lk is not null) placed.Lock = new LockData { Level = SkyrimLock(level), Key = key is null ? new FormLink<IKeyGetter>() : key.ToLink<IKeyGetter>() };
        placed.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        placed.VirtualMachineAdapter.Scripts.Add(entry);
        if (lk is not null) LockCount++;
        else ScriptLocks++;
    }
}
