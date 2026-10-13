using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;
using Activator = Mutagen.Bethesda.Skyrim.Activator;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall action objects (levers, switches, lifts, platforms, secret walls, trapdoors, portcullises...) as
/// scripted Skyrim activators running TMDfAction, plus trigger boxes for walk-on/walk-into triggers. Movement
/// follows Daggerfall Unity's RDBLayout/DaggerfallAction: rotations turn about the object's own axes (2048 units
/// per turn), translations move along world axes, durations are in 1/20 s, and each trigger toggles between the
/// start and end pose. Poses are converted to Skyrim's absolute positions and angles here.
/// </summary>
public sealed class ActionObjectBuilder(SkyrimMod mod, IReadOnlyDictionary<uint, DfModel> models, string modOutDir, SpellBuilder? spells = null)
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey FallbackTrapSpell = Sk(0x7E598);   // TrapLightningBolt02, for spell indexes SPELLS.STD lacks
    static readonly FormKey XMarkerHeading = Sk(0x34);

    readonly Dictionary<(uint Model, bool Named), Activator> activators = new();
    Activator? triggerBase;

    /// <summary>TEXT.RSC, for the messages of text actions (11), riddles (12) and door texts (99).</summary>
    public DfText? Text { get; init; }
    /// <summary>Riddle messages made so far (shared by the dungeons of one plugin), by TEXT.RSC record.</summary>
    public Dictionary<int, Message> Riddles { get; init; } = new();

    // Daggerfall Unity DaggerfallAction: text indexes and the answers its riddles accept.
    const int ShowTextBase = 8600, RiddleBase = 5400, DoorTextBase = 7700;
    /// <summary>Skyrim has no typed answer, so each riddle offers its answer among others (the first is right).</summary>
    static readonly Dictionary<int, string[]> RiddleChoices = new()
    {
        [5404] = ["A bow", "A spear", "A mace", "A staff"],                       // the master of the blade's other weapon
        [5406] = ["One", "Two", "Six", "None"],                                   // "how many defy me?"
        [5423] = ["The Benefactor", "The Guardian", "The Keeper", "No one"],      // "speak only my name"
        [5424] = ["Shut up!", "Open!", "Let me pass.", "Please."],                // "command me"
        [5464] = ["Yes, I agree.", "No."],                                        // the court's terms
    };

    /// <summary>Daggerfall's text with its player macros filled in for a message box.</summary>
    static string Say(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace("%pcn", "traveller").Replace("%pcf", "traveller"), @"%\w+", "").Trim();

    string? TextOf(int id) => Text?[id] is { Count: > 0 } lines && !lines[0].Contains("do not translate") ? Say(lines[0]) : null;

    /// <summary>A riddle as a message: its question, its answers in a stable shuffled order; returns the right button.</summary>
    (Message Message, int Answer)? Riddle(int id)
    {
        if (!RiddleChoices.TryGetValue(id, out var choices) || TextOf(id) is not { } question) return null;
        // Shuffle by the record so the right answer isn't always first.
        int shift = id % choices.Length;
        var order = choices.Select((c, i) => (c, i)).OrderBy(x => (x.i + shift) % choices.Length).ToList();
        int answer = order.FindIndex(x => x.i == 0);
        if (!Riddles.TryGetValue(id, out var message))
        {
            message = new Message(mod) { EditorID = $"DFRiddle{id}", Description = question, Flags = Message.Flag.MessageBox, MenuButtons = [] };
            foreach (var (c, _) in order) message.MenuButtons.Add(new MessageButton { Text = c });
            mod.Messages.Add(message);
            Riddles[id] = message;
        }
        return (message, answer);
    }

    public sealed record Placed(PlacedObject Ref, DfRdbObject Source);

    /// <summary>The Skyrim reference standing for a block object, by its index in the block (for chain links).</summary>
    public Dictionary<int, PlacedObject> ByIndex { get; } = new();

    public static bool IsActionObject(DfRdbObject o) => o.Type == DfRdbObjectType.Model && o.Action is not null && !BlockMeshBuilder.IsDoor(o);

    /// <summary>Places an action object as a scripted activator (and its trigger box, if it has a collision trigger).</summary>
    public PlacedObject Place(Cell cell, DfRdbObject o, int index, Vector3 blockOrigin)
    {
        var model = models[o.ModelId];
        int trigger = TriggerOf(o);
        bool direct = trigger is 2 or 6 or 8 or 9;
        var m = DfDungeon.ObjectTransform(o, blockOrigin);
        var (start, startAngles) = Pose(m);
        var r = new PlacedObject(mod)
        {
            Base = Activator(o, direct).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = start, Rotation = new P3Float(startAngles.X, startAngles.Y, startAngles.Z) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400, // persistent: chained objects reference it
        };
        r.VirtualMachineAdapter = Script(o, m);
        cell.Persistent.Add(r);
        ByIndex[index] = r;
        if (trigger is 1 or 3 or 8 or 9) AddTriggerBox(cell, r, model, m, walkOn: trigger == 1);
        return r;
    }

    /// <summary>A door that carries an action (teleport, lock/unlock, open/close...) gets the same script.</summary>
    public void AttachToDoor(PlacedObject door, DfRdbObject o, int index, Vector3 blockOrigin)
    {
        door.VirtualMachineAdapter = Script(o, DfDungeon.ObjectTransform(o, blockOrigin), forDoor: true);
        door.SkyrimMajorRecordFlags |= (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
        ByIndex[index] = door;
    }

    public void Register(int index, PlacedObject r) => ByIndex[index] = r;

    /// <summary>
    /// Fill in each script's NextObject now every object in the block is placed. Teleport destinations that are
    /// editor flats (no Skyrim object) get an XMarkerHeading where the flat stood.
    /// </summary>
    public int LinkChains(Cell cell, DfDungeonBlock block, Vector3 blockOrigin)
    {
        int links = 0;
        foreach (var (index, r) in ByIndex.ToList())
        {
            var source = block.Objects[index];
            if (source.Action is not { NextObjectIndex: >= 0 } action || r.VirtualMachineAdapter is null) continue;
            if (!ByIndex.TryGetValue(action.NextObjectIndex, out var target))
            {
                var t = block.Objects[action.NextObjectIndex];
                var p = ToSkyrim(blockOrigin + t.Position);
                target = new PlacedObject(mod)
                {
                    Base = XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = new P3Float(p.X, p.Y, p.Z), Rotation = new P3Float(0, 0, 0) },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                };
                cell.Persistent.Add(target);
                ByIndex[action.NextObjectIndex] = target;
            }
            var script = r.VirtualMachineAdapter.Scripts.First(s => s.Name == "TMDfAction");
            script.Properties.Add(new ScriptObjectProperty { Name = "NextObject", Object = target.ToLink<ISkyrimMajorRecordGetter>() });
            links++;
        }
        ByIndex.Clear();
        return links;
    }

    /// <summary>Daggerfall Unity uses the trigger/lock field as a trigger only when it is a known trigger value.</summary>
    static int TriggerOf(DfRdbObject o) => o.TriggerAndLock is 1 or 2 or 3 or 5 or 6 or 8 or 9 ? (int)o.TriggerAndLock : 0;

    VirtualMachineAdapter Script(DfRdbObject o, Matrix4x4 m, bool forDoor = false)
    {
        var a = o.Action!;
        int type = a.Flags is >= 1 and <= 8 ? 1 : a.Flags; // all translation/rotation variants are "move"
        var props = new List<ScriptProperty>
        {
            new ScriptIntProperty { Name = "ActionType", Data = type },
            new ScriptIntProperty { Name = "TriggerType", Data = TriggerOf(o) },
        };
        if (type == 1 && !forDoor && EndPose(o, m) is { } end)
        {
            var (pos, angles, seconds) = end;
            var list = new ScriptFloatListProperty { Name = "EndPose" };
            list.Data.AddRange([pos.X, pos.Y, pos.Z, angles.X, angles.Y, angles.Z]);
            props.Add(list);
            props.Add(new ScriptFloatProperty { Name = "Duration", Data = Math.Max(seconds, 0.1f) });
        }
        if (a.Flags is >= 21 and <= 28)
            props.Add(new ScriptFloatProperty { Name = "Magnitude", Data = Math.Max(1, (int)a.Axis) });
        if (a.Flags == 9)
        {
            // The sound/index byte names the SPELLS.STD spell (Daggerfall Unity CastSpell).
            bool found = spells?.Traps.ContainsKey(o.SoundIndex) == true;
            var trap = found ? spells!.Traps[o.SoundIndex] : default;
            props.Add(new ScriptObjectProperty { Name = "TrapSpell", Object = (found ? trap.Spell.FormKey : FallbackTrapSpell).ToLink<ISkyrimMajorRecordGetter>() });
            if (found && trap.OnPlayer) props.Add(new ScriptBoolProperty { Name = "TrapOnPlayer", Data = true });
        }
        // Messages: a pop-up (11), a riddle whose right answer passes the trigger on (12), a door's notice (99; Daggerfall
        // Unity's patches: 7701-7704 read as 7705, 7700 says nothing).
        if (a.Flags == 11 && TextOf(ShowTextBase + o.SoundIndex) is { } shown)
            props.Add(new ScriptStringProperty { Name = "Text", Data = shown });
        if (a.Flags == 12 && Riddle(RiddleBase + o.SoundIndex) is { } riddle)
        {
            props.Add(new ScriptObjectProperty { Name = "Riddle", Object = riddle.Message.ToLink<ISkyrimMajorRecordGetter>() });
            props.Add(new ScriptIntProperty { Name = "RiddleAnswer", Data = riddle.Answer });
        }
        if (a.Flags == 99 && o.SoundIndex > 0 && TextOf(DoorTextBase + (o.SoundIndex is >= 1 and <= 4 ? 5 : o.SoundIndex)) is { } notice)
            props.Add(new ScriptStringProperty { Name = "Text", Data = notice });
        var entry = new ScriptEntry { Name = "TMDfAction" };
        entry.Properties.AddRange(props);
        return new VirtualMachineAdapter { Scripts = [entry] };
    }

    /// <summary>End pose (Skyrim position, angles in degrees, seconds) of a move action, following Daggerfall Unity.</summary>
    static (Vector3 Position, Vector3 AnglesDegrees, float Seconds)? EndPose(DfRdbObject o, Matrix4x4 m)
    {
        var a = o.Action!;
        float seconds = a.Duration / 20f;
        var rotation = Vector3.Zero;   // degrees about the object's own (Unity) axes
        var translation = Vector3.Zero; // Daggerfall units along world (Unity) axes
        switch (a.Flags)
        {
            case 8:
            {
                int axis = a.Axis; float magnitude = a.Magnitude;
                if (axis == 13 && o.Tag == "TRP") { axis = 1; magnitude = 400; } // DFU special case for trapdoors
                float deg = magnitude / DfDungeonBlock.RotationDivisor;
                rotation = axis switch { 1 => new(-deg, 0, 0), 2 => new(deg, 0, 0), 3 => new(0, -deg, 0), 4 => new(0, deg, 0), 5 => new(0, 0, -deg), 6 => new(0, 0, deg), _ => Vector3.Zero };
                if (o.Tag == "LID") rotation = new(0, 0, -90);
                if (o.Tag == "WHE") rotation = Vector3.Zero; // a full turn ends where it began
                break;
            }
            case 1:
                translation = Translate(a.Axis, a.Magnitude);
                break;
            case >= 2 and <= 7:
                // Directional variants: fixed 2.5 s, magnitude = raw axis value x 8 (DFU RDBLayout.AddAction).
                seconds = 50 / 20f;
                translation = Translate(a.Flags switch { 2 => 2, 3 => 1, 4 => 4, 5 => 3, 6 => 6, _ => 5 }, a.Axis * 8);
                break;
            default:
                return null;
        }
        const float rad = MathF.PI / 180;
        var local = DfDungeon.UnityRotZ(rotation.Z * rad) * DfDungeon.UnityRotX(rotation.X * rad) * DfDungeon.UnityRotY(rotation.Y * rad);
        var end = local * m;
        end.Translation = m.Translation + translation;
        var (position, angles) = Pose(end);
        return (new Vector3(position.X, position.Y, position.Z), angles * (180 / MathF.PI), seconds);
    }

    /// <summary>Daggerfall Unity GetTranslationActionVector (note the mirrored X and Z signs).</summary>
    static Vector3 Translate(int axis, float magnitude) => axis switch
    {
        1 => new(magnitude, 0, 0), 2 => new(-magnitude, 0, 0),
        3 => new(0, -magnitude, 0), 4 => new(0, magnitude, 0),
        5 => new(0, 0, magnitude), 6 => new(0, 0, -magnitude),
        _ => Vector3.Zero,
    };

    /// <summary>Skyrim position and Euler angles (radians) for a Daggerfall object transform (Unity frame, row vectors).</summary>
    internal static (P3Float Position, Vector3 Angles) Pose(Matrix4x4 m)
    {
        var t = m.Translation;
        var position = new P3Float(t.X * BlockMeshBuilder.Scale, t.Z * BlockMeshBuilder.Scale, t.Y * BlockMeshBuilder.Scale);
        return (position, SkyrimAngles(m));
    }

    /// <summary>
    /// Euler angles for a Skyrim reference whose local-to-world rotation equals the Daggerfall transform's, after
    /// swapping Unity's Y-up axes for Skyrim's Z-up. Skyrim angles turn clockwise and apply X, then Y, then Z about
    /// world axes: R = Rz(-z) Ry(-y) Rx(-x) in counter-clockwise column-vector form.
    /// </summary>
    public static Vector3 SkyrimAngles(Matrix4x4 m)
    {
        // Row-vector Unity rotation -> column-vector Skyrim rotation: swap Y/Z rows and columns, then transpose.
        float[,] u = { { m.M11, m.M12, m.M13 }, { m.M21, m.M22, m.M23 }, { m.M31, m.M32, m.M33 } };
        int[] map = [0, 2, 1];
        var r = new float[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = u[map[j], map[i]];
        float b = MathF.Asin(Math.Clamp(-r[2, 0], -1, 1));
        float a = MathF.Atan2(r[2, 1], r[2, 2]);
        float c = MathF.Atan2(r[1, 0], r[0, 0]);
        return new Vector3(-a, -b, -c);
    }

    static Vector3 ToSkyrim(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);

    Activator Activator(DfRdbObject o, bool named)
    {
        if (activators.TryGetValue((o.ModelId, named), out var acti)) return acti;
        string rel = Path.Combine("TamrielMerge", "Dungeons", "Actions", $"{o.ModelId}.nif");
        string full = Path.Combine(modOutDir, "Meshes", rel);
        // Rebuilt every time (a cached file would keep old mesh-writing bugs).
        BlockMeshBuilder.BuildMovingModel(models[o.ModelId], full);
        var model = models[o.ModelId];
        var min = ToSkyrimLocal(model.Min); var max = ToSkyrimLocal(model.Max);
        acti = new Activator(mod)
        {
            EditorID = $"DFAct{o.ModelId}{(named ? "Use" : "")}",
            Model = new Model { File = rel },
            ObjectBounds = new ObjectBounds
            {
                First = new P3Int16((short)MathF.Min(min.X, max.X), (short)MathF.Min(min.Y, max.Y), (short)MathF.Min(min.Z, max.Z)),
                Second = new P3Int16((short)MathF.Max(min.X, max.X), (short)MathF.Max(min.Y, max.Y), (short)MathF.Max(min.Z, max.Z)),
            },
        };
        // Only directly-used objects get a name: Skyrim shows an activate prompt for named activators.
        if (named) acti.Name = o.Tag switch { "L0Z" or "L03" or "LDB" or "LDW" => "Lever", "C0Z" => "Switch", "CRN" => "Crank", _ => "Mechanism" };
        mod.Activators.Add(acti);
        return activators[(o.ModelId, named)] = acti;
    }

    static Vector3 ToSkyrimLocal(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);

    /// <summary>Invisible box around (walk-into) or on top of (walk-on) the object, running TMDfTrigger.</summary>
    void AddTriggerBox(Cell cell, PlacedObject target, DfModel model, Matrix4x4 m, bool walkOn)
    {
        triggerBase ??= AddTriggerBase();
        var min = model.Min; var max = model.Max;
        var centreLocal = (min + max) / 2;
        var half = (max - min) / 2 * BlockMeshBuilder.Scale + new Vector3(24);
        if (walkOn) { centreLocal.Y = max.Y; half.Y = 48; }
        var centre = Vector3.Transform(centreLocal, m);
        var (_, angles) = Pose(m);
        var box = new PlacedObject(mod)
        {
            Base = triggerBase.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(centre.X * BlockMeshBuilder.Scale, centre.Z * BlockMeshBuilder.Scale, centre.Y * BlockMeshBuilder.Scale + (walkOn ? 32 : 0)), Rotation = new P3Float(angles.X, angles.Y, angles.Z) },
            // Vanilla trigger boxes: box primitive, unknown 0.15, no collision layer set.
            Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(half.X, half.Z, half.Y), Color = System.Drawing.Color.FromArgb(0, 204, 76, 51), Unknown = 0.15f },
        };
        var entry = new ScriptEntry { Name = "TMDfTrigger" };
        entry.Properties.Add(new ScriptObjectProperty { Name = "Target", Object = target.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptBoolProperty { Name = "WalkOn", Data = walkOn });
        box.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        cell.Temporary.Add(box);
    }

    Activator AddTriggerBase()
    {
        var acti = new Activator(mod) { EditorID = "DFActionTrigger" };
        mod.Activators.Add(acti);
        return acti;
    }
}
