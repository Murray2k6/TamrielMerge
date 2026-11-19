using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Replaces Daggerfall's hinged dungeon doors with Skyrim Imperial doors (which bring their own collision and
/// open/close animation), sized to the doorway and turned to face the same way, keeping Daggerfall's lock.
/// </summary>
public static class DoorPlacer
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    // ImpDoorExtSingleSmall01: 134 wide, 228 tall, leaf ~72 units in front of its origin along -Y. Its floor (the
    // bottom of frame and leaf) is 36 below its origin: frame -36..192, leaf -37..172 (measured in
    // ImpWoodDoorSingleSmall01.nif, 2026-10-10). Placed with the origin on the floor it sank 36 x scale, the frame
    // stopping short of the doorway's top and the leaf barely an NPC tall (user: "some doors are too small for npcs
    // too", "the door frames dont match some of the doors").
    internal static readonly FormKey SingleDoor = Sk(0x4DFD9);
    const float SingleHeight = 228;
    internal const float SingleLeafOffset = 72;
    internal const float SingleBottom = -36;
    /// <summary>The single door's opening: the leaf's height (-37..172), the head room its frame leaves.</summary>
    internal const float SingleOpening = 209;
    // ImpDoorDouble01: 292 wide, 309 tall, leaves ~264 units along +Y from its origin; its threshold's bottom is 4
    // above the origin (ImpWoodDoorDouble01.nif).
    internal static readonly FormKey DoubleDoor = Sk(0x439C2);
    const float DoubleWidth = 292;
    internal const float DoubleLeafOffset = 264;
    internal const float DoubleBottom = 4;

    /// <summary>Base doors used for Daggerfall doors, for effects that look for the nearest one (Open, Wizard Lock).</summary>
    public static IEnumerable<FormKey> BaseDoors => [SingleDoor, DoubleDoor];

    /// <summary>Daggerfall Unity's starting-lock table (index = high nibble of the trigger/lock field).</summary>
    static readonly byte[] LockValues = [0x00, 0x02, 0x04, 0x06, 0x08, 0x0A, 0x0C, 0x0E, 0x10, 0x12, 0x14, 0x19, 0x1E, 0x32, 0x80, 0xFF];

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkyrimMod, Dictionary<uint, Door>> made = new();
    // Skyrim's wooden door sounds (DRSWoodImperialSingle01Open and Close).
    static readonly FormKey OpenSound = Sk(0x5B0F3), CloseSound = Sk(0x5B134);

    /// <summary>
    /// The door as Daggerfall's own model, where Daggerfall put it (2026-11-10, user: "some doors arent against walls",
    /// "can we use daggerfalls doors instead of a skyrim one?"; `door-walls` on Privateer's Hold measured the Skyrim door
    /// that stood in for it against the walls beside each doorway). One Door record per door model, its mesh the model
    /// in its own space with its origin on the hinge (BuildMovingModel: its collision goes with it), swung by TMDfDoor.
    /// </summary>
    public static PlacedObject Place(SkyrimMod mod, DfRdbObject o, Vector3 blockOrigin, DfModel model, string modOutDir)
    {
        var doors = made.GetOrCreateValue(mod);
        if (!doors.TryGetValue(o.ModelId, out var baseDoor))
        {
            string rel = Path.Combine("TamrielMerge", "Dungeons", "Doors", $"{o.ModelId}.nif");
            BlockMeshBuilder.BuildMovingModel(model, Path.Combine(modOutDir, "Meshes", rel));
            Vector3 L(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);
            var min = L(model.Min); var max = L(model.Max);
            var entry = new ScriptEntry { Name = "TMDfDoor" };
            entry.Properties.Add(new ScriptObjectProperty { Name = "OpenSound", Object = new FormLink<ISkyrimMajorRecordGetter>(OpenSound) });
            entry.Properties.Add(new ScriptObjectProperty { Name = "CloseSound", Object = new FormLink<ISkyrimMajorRecordGetter>(CloseSound) });
            baseDoor = new Door(mod)
            {
                EditorID = $"DFDoor{o.ModelId}",
                Name = "Door",
                Model = new Model { File = rel },
                ObjectBounds = new ObjectBounds
                {
                    First = new P3Int16((short)MathF.Floor(MathF.Min(min.X, max.X)), (short)MathF.Floor(MathF.Min(min.Y, max.Y)), (short)MathF.Floor(MathF.Min(min.Z, max.Z))),
                    Second = new P3Int16((short)MathF.Ceiling(MathF.Max(min.X, max.X)), (short)MathF.Ceiling(MathF.Max(min.Y, max.Y)), (short)MathF.Ceiling(MathF.Max(min.Z, max.Z))),
                },
                VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] },
            };
            mod.Doors.Add(baseDoor);
            doors[o.ModelId] = baseDoor;
        }
        var (position, angles) = ActionObjectBuilder.Pose(DfDungeon.ObjectTransform(o, blockOrigin));
        var door = new PlacedObject(mod)
        {
            Base = baseDoor.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = position, Rotation = new P3Float(angles.X, angles.Y, angles.Z) },
        };
        if (LockFor(o) is { } level)
        {
            door.Lock = new LockData { Level = level };
            // Daggerfall's own lockpicking (TMDfLock) in place of Skyrim's minigame.
            var entry = new ScriptEntry { Name = "TMDfLock" };
            entry.Properties.Add(new ScriptIntProperty { Name = "LockValue", Data = LockValue(o) });
            door.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        }
        return door;
    }

    /// <summary>Daggerfall lock strength to Skyrim lock level; the two magical values need a key.</summary>
    public static int LockValue(DfRdbObject o) => LockValues[(o.TriggerAndLock >> 4) & 0xF];

    public static LockLevel? LockFor(DfRdbObject o)
    {
        return LockValue(o) switch
        {
            0 => null,
            <= 0x06 => LockLevel.Novice,
            <= 0x0E => LockLevel.Apprentice,
            <= 0x14 => LockLevel.Adept,
            <= 0x1E => LockLevel.Expert,
            <= 0x32 => LockLevel.Master,
            _ => LockLevel.RequiresKey,
        };
    }
}
