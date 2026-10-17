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

    public static PlacedObject Place(SkyrimMod mod, DfRdbObject o, Vector3 blockOrigin, DfModel model)
    {
        // The panel is the model's bounding box: its thinnest horizontal axis is the way the door faces.
        var min = model.Min; var max = model.Max;
        float ex = max.X - min.X, ez = max.Z - min.Z, height = max.Y - min.Y;
        var localNormal = ex < ez ? Vector3.UnitX : Vector3.UnitZ;
        float width = MathF.Max(ex, ez);
        var baseCentre = new Vector3((min.X + max.X) / 2, min.Y, (min.Z + max.Z) / 2);
        var m = DfDungeon.ObjectTransform(o, blockOrigin);
        var centre = Vector3.Transform(baseCentre, m);
        var n = Vector3.TransformNormal(localNormal, m);
        var facing = Vector3.Normalize(new Vector3(n.X, n.Z, 0)); // Skyrim horizontal
        float heading = MathF.Atan2(facing.X, facing.Y);

        bool isDouble = o.Tag == "DDR";
        float scale = isDouble
            ? Math.Clamp(width * BlockMeshBuilder.Scale / DoubleWidth, 0.6f, 2f)
            : Math.Clamp(height * BlockMeshBuilder.Scale / SingleHeight, 0.6f, 2f);
        // Skyrim's door leaf sits off its origin along local Y (single -72, double +264); shift the origin so the
        // leaf lands in the doorway. Local +Y points along the facing after the heading turn.
        float offset = (isDouble ? DoubleLeafOffset : -SingleLeafOffset) * scale;
        var sky = new Vector3(centre.X, centre.Z, centre.Y) * BlockMeshBuilder.Scale;
        var position = sky - facing * offset;
        // The door's own floor on Daggerfall's (centre is the bottom of the Daggerfall door).
        position.Z -= (isDouble ? DoubleBottom : SingleBottom) * scale;

        var door = new PlacedObject(mod)
        {
            Base = (isDouble ? DoubleDoor : SingleDoor).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(position.X, position.Y, position.Z), Rotation = new P3Float(0, 0, heading) },
        };
        if (Math.Abs(scale - 1) > 0.01f) door.Scale = scale;
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
