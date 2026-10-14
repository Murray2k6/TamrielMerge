using System.Numerics;

namespace TamrielMerge.Daggerfall;

public enum DfRdbObjectType : byte { Model = 1, Light = 2, Flat = 3 }

/// <summary>A model's or flat's action (Daggerfall Unity RdbActionResource / RdbFlatResource): movement along/around an
/// axis, damage, a spell, a text..., chained to another object.</summary>
public sealed record DfAction(byte Axis, ushort Duration, ushort Magnitude, byte Flags, int NextObjectIndex);

/// <summary>An object in an RDB dungeon block. Position is block-relative in Daggerfall units with Y up.</summary>
public sealed record DfRdbObject(DfRdbObjectType Type, Vector3 Position, Vector3 Rotation, uint ModelId,
    int TextureArchive, int TextureRecord, ushort FlatFlags, ushort FactionOrMobileId, int LightRadius, bool HasAction)
{
    /// <summary>Three-letter tag from the block's model reference list (DOR door, DDR double door, ...).</summary>
    public string Tag { get; init; } = "";
    /// <summary>Raw trigger/lock field: low nibble trigger flags, high nibble starting lock index. For a flat with an
    /// action, its flags (the trigger).</summary>
    public uint TriggerAndLock { get; init; }
    /// <summary>Sound/index byte: the sound a model plays, or for spell actions the SPELLS.STD record index.</summary>
    public byte SoundIndex { get; init; }
    public DfAction? Action { get; init; }
    /// <summary>A flat's action byte; on enemy markers 99 means passive (attacks only when provoked).</summary>
    public byte FlatAction { get; init; }
    /// <summary>File offset of the object's record, used to resolve action chains.</summary>
    public int RecordOffset { get; init; }
}

/// <summary>An RDB dungeon block from BLOCKS.BSA. Format reference: Daggerfall Unity BlocksFile.cs (RDB readers).</summary>
public sealed class DfDungeonBlock
{
    /// <summary>Side of an RDB block in Daggerfall units.</summary>
    public const float Side = 2048f;
    /// <summary>Daggerfall rotation units per degree (2048 per full turn).</summary>
    public const float RotationDivisor = 2048f / 360f;

    public required string Name { get; init; }
    public required IReadOnlyList<DfRdbObject> Objects { get; init; }

    public static DfDungeonBlock Load(DfBsa blocks, string name)
    {
        var d = blocks.Record(name);
        int width = BitConverter.ToInt32(d, 4), height = BitConverter.ToInt32(d, 8), rootOffset = BitConverter.ToInt32(d, 12);
        // 750 model references: 5-char id + 3-char description tag.
        var modelIds = new uint[750];
        var tags = new string[750];
        for (int i = 0; i < 750; i++)
        {
            uint.TryParse(System.Text.Encoding.ASCII.GetString(d, 20 + i * 8, 5).TrimEnd('\0', ' '), out modelIds[i]);
            tags[i] = System.Text.Encoding.ASCII.GetString(d, 20 + i * 8 + 5, 3).TrimEnd('\0', ' ');
        }
        var objects = new List<DfRdbObject>();
        var actionTargets = new List<(int ObjectIndex, int NextOffset)>();
        for (int r = 0; r < width * height; r++)
        {
            int pos = BitConverter.ToInt32(d, rootOffset + r * 4);
            while (pos > 0)
            {
                int next = BitConverter.ToInt32(d, pos);
                // Daggerfall's Y axis points down; flip so +Y is up.
                var p = new Vector3(BitConverter.ToInt32(d, pos + 8), -BitConverter.ToInt32(d, pos + 12), BitConverter.ToInt32(d, pos + 16));
                var type = (DfRdbObjectType)d[pos + 20];
                int res = BitConverter.ToInt32(d, pos + 21);
                switch (type)
                {
                    case DfRdbObjectType.Model:
                    {
                        var rot = new Vector3(BitConverter.ToInt32(d, res), BitConverter.ToInt32(d, res + 4), BitConverter.ToInt32(d, res + 8)) / RotationDivisor;
                        int modelIndex = BitConverter.ToUInt16(d, res + 12);
                        uint trigger = BitConverter.ToUInt32(d, res + 14);
                        int actionOffset = BitConverter.ToInt32(d, res + 19);
                        DfAction? action = null;
                        if (actionOffset > 0)
                        {
                            byte axis = d[actionOffset];
                            ushort duration = BitConverter.ToUInt16(d, actionOffset + 1), magnitude = BitConverter.ToUInt16(d, actionOffset + 3);
                            int nextObject = BitConverter.ToInt32(d, actionOffset + 5);
                            byte flags = d[actionOffset + 9];
                            // Daggerfall Unity: an object has an action only when its action flags are non-zero.
                            if (flags != 0)
                            {
                                action = new DfAction(axis, duration, magnitude, flags, -1);
                                if (nextObject > 0) actionTargets.Add((objects.Count, nextObject));
                            }
                        }
                        objects.Add(new DfRdbObject(type, p, rot, modelIds[modelIndex], 0, 0, 0, 0, 0, action is not null)
                        { Tag = tags[modelIndex], TriggerAndLock = trigger, SoundIndex = d[res + 18], Action = action, RecordOffset = pos });
                        break;
                    }
                    case DfRdbObjectType.Flat:
                    {
                        ushort tex = BitConverter.ToUInt16(d, res);
                        ushort flags = BitConverter.ToUInt16(d, res + 2);
                        ushort faction = BitConverter.ToUInt16(d, res + 4);
                        // The faction/mobile field's bytes are also the flat's magnitude and sound index (Daggerfall Unity
                        // BlocksFile); a start marker's sound index is its block's water level (DungeonWaterLevel).
                        // A flat's action (Daggerfall Unity AddActionFlatHelper): the action byte, the flags as its trigger,
                        // the magnitude byte as both magnitude and raw axis value, no duration, and its own chain link.
                        // Enemy markers keep theirs as enemy data (99 = passive).
                        byte flatAction = d[res + 10], flatMagnitude = d[res + 4];
                        bool enemy = tex >> 7 == 199 && (tex & 0x7f) is 15 or 16;
                        DfAction? flatAct = null;
                        if (flatAction != 0 && !enemy)
                        {
                            flatAct = new DfAction(flatMagnitude, 0, flatMagnitude, flatAction, -1);
                            int nextFlat = BitConverter.ToInt32(d, res + 6);
                            if (nextFlat > 0) actionTargets.Add((objects.Count, nextFlat));
                        }
                        objects.Add(new DfRdbObject(type, p, Vector3.Zero, 0, tex >> 7, tex & 0x7f, flags, faction, 0, false)
                        { RecordOffset = pos, FlatAction = flatAction, SoundIndex = d[res + 5], Action = flatAct, TriggerAndLock = flatAct is null ? 0u : flags });
                        break;
                    }
                    case DfRdbObjectType.Light:
                        objects.Add(new DfRdbObject(type, p, Vector3.Zero, 0, 0, 0, 0, 0, BitConverter.ToUInt16(d, res + 8), false) { RecordOffset = pos });
                        break;
                }
                pos = next;
            }
        }
        // Resolve action chains: an object's action can trigger the object at another record offset.
        foreach (var (index, nextOffset) in actionTargets)
        {
            int target = objects.FindIndex(o => o.RecordOffset == nextOffset);
            if (target >= 0) objects[index] = objects[index] with { Action = objects[index].Action! with { NextObjectIndex = target } };
        }
        return new DfDungeonBlock { Name = name, Objects = objects };
    }
}
