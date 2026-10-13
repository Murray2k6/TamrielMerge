using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Activator = Mutagen.Bethesda.Skyrim.Activator;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Swinging doors (TMSwingDoor activators) say "Open" when shut and "Close" when open. An activator's prompt is fixed
/// in its base record and Papyrus can't change it, so each placed door gets a twin: a "Close" copy standing at the
/// open angle, initially disabled. Opening swings the door, then swaps the twin in; activating the twin swaps the
/// door back and swings it shut (TMSwingDoor.psc). Both are persistent, as references a script points at must be,
/// and each names the other in its own script properties (a reference's properties override its base's).
/// </summary>
public static class SwingDoorTwins
{
    const string Script = "TMSwingDoor";

    /// <summary>Adds twins for every placed swinging door in the mod's interiors and worldspaces. Returns how many.</summary>
    public static int AddAll(SkyrimMod mod)
    {
        var bases = mod.Activators.Where(a => a.VirtualMachineAdapter?.Scripts.Any(s => s.Name == Script) == true).ToDictionary(a => a.FormKey);
        if (bases.Count == 0) return 0;
        var twinBases = new Dictionary<FormKey, Activator>();
        int count = 0;
        void Process(Cell cell, Cell persistent)
        {
            var doors = cell.Temporary.OfType<PlacedObject>().Concat(cell.Persistent.OfType<PlacedObject>())
                .Where(p => bases.ContainsKey(p.Base.FormKey) && p.VirtualMachineAdapter?.Scripts.Any(s => s.Name == Script) != true).ToList();
            foreach (var door in doors)
            {
                var doorBase = bases[door.Base.FormKey];
                if (!twinBases.TryGetValue(doorBase.FormKey, out var twinBase))
                {
                    twinBase = mod.Activators.DuplicateInAsNewRecord(doorBase);
                    twinBase.EditorID = doorBase.EditorID + "Open";
                    twinBase.ActivateTextOverride = "Close";
                    twinBases[doorBase.FormKey] = twinBase;
                }
                if (cell.Temporary.Remove(door)) persistent.Persistent.Add(door);
                door.SkyrimMajorRecordFlags |= (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
                var rot = door.Placement!.Rotation;
                var twin = new PlacedObject(mod)
                {
                    Base = twinBase.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = door.Placement.Position, Rotation = new P3Float(rot.X, rot.Y, rot.Z + MathF.PI / 2) },
                    Scale = door.Scale,
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)(0x400 | 0x800),   // persistent, initially disabled
                };
                persistent.Persistent.Add(twin);
                // The door keeps its other scripts (a lock: TMMwLock); the twin only swings.
                var own = References(doorBase, twin.FormKey, openTwin: false);
                if (door.VirtualMachineAdapter is { } vmad) vmad.Scripts.AddRange(own.Scripts);
                else door.VirtualMachineAdapter = own;
                twin.VirtualMachineAdapter = References(doorBase, door.FormKey, openTwin: true);
                count++;
            }
        }
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells)) Process(cell, cell);
        foreach (var ws in mod.Worldspaces)
            if (ws.TopCell is { } top)
                foreach (var cell in ws.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items)) Process(cell, top);
        return count;
    }

    /// <summary>The base's TMSwingDoor properties plus this reference's partner (and, on a twin, IsOpenTwin).</summary>
    static VirtualMachineAdapter References(Activator doorBase, FormKey partner, bool openTwin)
    {
        var entry = new ScriptEntry { Name = Script };
        foreach (var p in doorBase.VirtualMachineAdapter!.Scripts.First(s => s.Name == Script).Properties)
            entry.Properties.Add(p.DeepCopy());
        entry.Properties.Add(new ScriptObjectProperty { Name = "Partner", Object = new FormLink<ISkyrimMajorRecordGetter>(partner) });
        if (openTwin) entry.Properties.Add(new ScriptBoolProperty { Name = "IsOpenTwin", Data = true });
        return new VirtualMachineAdapter { Scripts = [entry] };
    }
}
