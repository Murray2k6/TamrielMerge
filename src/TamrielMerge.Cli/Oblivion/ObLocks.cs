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

    /// <summary>Puts an Oblivion reference's lock on the placed door, container or swinging door.</summary>
    void ApplyLock(Ob.IPlacedObjectGetter r, PlacedObject placed)
    {
        if (r.Lock is not { } lk) return;
        var b = placed.Base.FormKey;
        bool swing = swingDoors.Values.Any(s => s?.FormKey == b);
        if (!swing && !mod.Doors.ContainsKey(b) && !mod.Containers.ContainsKey(b)) return;
        int level = lk.LockLevel;
        var key = lk.Key.IsNull ? null : Item(lk.Key.FormKey) as Key;
        var entry = new ScriptEntry { Name = "TMObLock" };
        entry.Properties.Add(new ScriptIntProperty { Name = "LockLevel", Data = level });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Choice", Object = LockChoice.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Lockpick", Object = new FormLink<ISkyrimMajorRecordGetter>(SkyrimLockpick) });
        if (key is not null) entry.Properties.Add(new ScriptObjectProperty { Name = "DoorKey", Object = key.ToLink<ISkyrimMajorRecordGetter>() });
        if (swing)
        {
            entry.Properties.Add(new ScriptBoolProperty { Name = "Swing", Data = true });
            entry.Properties.Add(new ScriptBoolProperty { Name = "Locked", Data = true });
        }
        else placed.Lock = new LockData { Level = SkyrimLock(level), Key = key is null ? new FormLink<IKeyGetter>() : key.ToLink<IKeyGetter>() };
        placed.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        placed.VirtualMachineAdapter.Scripts.Add(entry);
        LockCount++;
    }
}
