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
/// start and end pose. Poses are converted to Skyrim's absolute positions and angles here, both of them, so the
/// script does no set-up work of its own (45,000 persistent references).
/// Flats with actions (Daggerfall Unity AddActionFlatHelper: a torch that opens a wall, a hidden spell trap, a
/// teleport pad...) are invisible activators at the flat: clickable ones get a primitive the crosshair picks
/// (vanilla's click triggers: collision layer L_NONCOLLIDABLE), and walk-on/walk-into ones a trigger box.
/// </summary>
public sealed class ActionObjectBuilder(SkyrimMod mod, IReadOnlyDictionary<uint, DfModel> models, string modOutDir, SpellBuilder? spells = null)
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey FallbackTrapSpell = Sk(0x7E598);   // TrapLightningBolt02, for spell indexes SPELLS.STD lacks
    static readonly FormKey XMarkerHeading = Sk(0x34);

    readonly Dictionary<(uint Model, bool Named), Activator> activators = new();
    Activator? triggerBase, flatBase, flatUseBase;

    /// <summary>Daggerfall's quest global variables by index, for set-global actions (31); default: found or made in this plugin.</summary>
    public Func<int, FormKey>? QuestGlobal { get; init; }

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

    /// <summary>
    /// A door whose action slides it (a secret door or portcullis that lifts, Daggerfall Unity's translation actions):
    /// Skyrim's hinged doors can't do that, so it is placed as a moving activator wearing its Daggerfall model. Doors
    /// that turn (rotation actions, a few degrees about the hinge on top of the swing) stay Skyrim doors, which swing.
    /// </summary>
    public static bool IsMovingDoor(DfRdbObject o) => o.Type == DfRdbObjectType.Model && BlockMeshBuilder.IsDoor(o)
        && o.Action is { } a && a.Flags switch
        {
            1 => Translate(a.Axis, a.Magnitude) != Vector3.Zero,
            >= 2 and <= 7 => a.Axis != 0,
            _ => false,
        };

    /// <summary>A flat (billboard) with an action; enemy markers carry none (their action byte is enemy data).</summary>
    public static bool IsActionFlat(DfRdbObject o) => o.Type == DfRdbObjectType.Flat && o.Action is not null;

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
        r.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [Script(o, m)] };
        cell.Persistent.Add(r);
        ByIndex[index] = r;
        // Walk-on triggers (1, and 9: "activated by collision / walking on", Daggerfall Unity DFBlock) get a box over
        // the top; walk-into ones (3, 8) a box around.
        if (trigger is 1 or 3 or 8 or 9) AddTriggerBox(cell, r, model.Min, model.Max, m, walkOn: trigger is 1 or 9);
        return r;
    }

    /// <summary>
    /// A door that carries an action (teleport, lock/unlock, open/close...) gets the same script beside its own (its
    /// lock, TMDfLock). A door an action can lock gets TMDfLock too, so the lock it is given is Daggerfall's.
    /// </summary>
    public void AttachToDoor(PlacedObject door, DfRdbObject o, int index, Vector3 blockOrigin)
    {
        door.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        door.VirtualMachineAdapter.Scripts.Add(Script(o, DfDungeon.ObjectTransform(o, blockOrigin), forDoor: true));
        if (o.Action!.Flags is 16 or 20 && !door.VirtualMachineAdapter.Scripts.Any(s => s.Name == "TMDfLock"))
        {
            var lockEntry = new ScriptEntry { Name = "TMDfLock" };
            lockEntry.Properties.Add(new ScriptIntProperty { Name = "LockValue", Data = DoorPlacer.LockValue(o) });
            door.VirtualMachineAdapter.Scripts.Add(lockEntry);
        }
        door.SkyrimMajorRecordFlags |= (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
        ByIndex[index] = door;
    }

    /// <summary>
    /// A flat with an action, as an invisible activator where the flat stands (and its trigger box), so the player
    /// and chains can trigger it. A flat whose action does nothing here (a flat's move, poison, the unknown types) and
    /// that passes nothing on is left out; so is a talking flat (29) that is a person already: Skyrim's own
    /// conversation is its dialogue. Returns the activator, or null.
    /// </summary>
    public PlacedObject? PlaceFlat(Cell cell, DfRdbObject o, int index, Vector3 blockOrigin, IPlacedNpcGetter? speaker = null)
    {
        var a = o.Action!;
        bool chained = a.NextObjectIndex >= 0;
        var m = Matrix4x4.CreateTranslation(blockOrigin + o.Position);
        if (a.Flags == 29 && speaker is not null && !chained) return null;
        var entry = Script(o, m, speaker: speaker);
        bool acts = a.Flags switch
        {
            9 or 14 or (>= 16 and <= 25) or 28 or 31 => true,
            11 or 12 or 32 or 29 => entry.Properties.Any(p => p.Name is "Text" or "Riddle" or "Speaker"),
            _ => false,
        };
        if (!acts && !chained) return null;
        int trigger = TriggerOf(o);
        bool direct = trigger is 2 or 6 or 8 or 9;
        var p = ToSkyrim(blockOrigin + o.Position);
        var r = new PlacedObject(mod)
        {
            Base = (direct ? flatUseBase ??= AddFlatBase(true) : flatBase ??= AddFlatBase(false)).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(p.X, p.Y, p.Z), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] },
        };
        if (direct)
        {
            // Vanilla's click triggers (PlayerBookShelfClickTrigger, MannequinActivateTrig...): a box primitive on
            // L_NONCOLLIDABLE, which the crosshair picks and nothing bumps into. About a flat's size.
            r.Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(FlatHalf, FlatHalf, FlatHalf * 1.5f), Color = System.Drawing.Color.FromArgb(0, 204, 76, 51), Unknown = 0.15f };
            r.CollisionLayer = 15;
        }
        cell.Persistent.Add(r);
        ByIndex[index] = r;
        if (trigger is 1 or 3 or 8 or 9)
        {
            var half = new Vector3(FlatHalf, FlatHalf * 1.5f, FlatHalf) / BlockMeshBuilder.Scale; // Daggerfall units, Y up
            AddTriggerBox(cell, r, -half, half, m, walkOn: trigger is 1 or 9);
        }
        return r;
    }

    /// <summary>Half the side of a flat's activation box (Skyrim units): about a Daggerfall flat's size.</summary>
    const float FlatHalf = 32;

    Activator AddFlatBase(bool named)
    {
        var acti = new Activator(mod) { EditorID = named ? "DFActionFlatUse" : "DFActionFlat" };
        if (named) acti.Name = "Something odd";
        mod.Activators.Add(acti);
        return acti;
    }

    public void Register(int index, PlacedObject r) => ByIndex[index] = r;

    /// <summary>
    /// Fill in each script's NextObject now every object in the block is placed. Chains into flats with actions reach
    /// their activators (PlaceFlat); teleport destinations that are editor flats or lights (no Skyrim object) get an
    /// XMarkerHeading where they stood.
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
                // A teleport turns the player the way its destination faces (Daggerfall Unity Teleport): a model's
                // heading; flats and lights have none (Unity's identity: north).
                float heading = t.Type == DfRdbObjectType.Model ? Pose(DfDungeon.ObjectTransform(t, blockOrigin)).Angles.Z : 0;
                target = new PlacedObject(mod)
                {
                    Base = XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = new P3Float(p.X, p.Y, p.Z), Rotation = new P3Float(0, 0, heading) },
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

    ScriptEntry Script(DfRdbObject o, Matrix4x4 m, bool forDoor = false, IPlacedNpcGetter? speaker = null)
    {
        var a = o.Action!;
        bool flat = o.Type == DfRdbObjectType.Flat;
        int type = a.Flags is >= 1 and <= 8 ? 1 : a.Flags; // all translation/rotation variants are "move"
        // A move that goes nowhere (an axis Daggerfall Unity doesn't know, 10, 11 or 21, or no magnitude) only passes
        // the trigger on, as there: 0 (2026-10-16: 247 such were checked as moving parts that end where they start).
        var end = type == 1 && !forDoor && !flat ? EndPose(o, m) : null;
        var spin = type == 1 && !forDoor && !flat ? SpinPoses(o, m) : null;
        if (type == 1 && !forDoor && !flat && spin is null && (end is not { } e || Still(e, m))) type = 0;
        var props = new List<ScriptProperty>
        {
            new ScriptIntProperty { Name = "ActionType", Data = type },
            new ScriptIntProperty { Name = "TriggerType", Data = TriggerOf(o) },
        };
        // A wheel turns once round on each trigger (Daggerfall Unity turns it and back to where it began).
        if (spin is not null)
        {
            var spinList = new ScriptFloatListProperty { Name = "SpinPoses" };
            spinList.Data.AddRange(spin);
            props.Add(spinList);
            props.Add(new ScriptFloatProperty { Name = "Duration", Data = Math.Max(a.Duration / 20f, 0.4f) });
        }
        // Flats don't move here (Daggerfall Unity moves the billboard in no time); hinged doors swing as Skyrim doors.
        else if (type == 1 && end is { } moved)
        {
            var (pos, angles, seconds) = moved;
            var (startPos, startAngles) = Pose(m);
            startAngles *= 180 / MathF.PI;
            var first = new ScriptFloatListProperty { Name = "StartPose" };
            first.Data.AddRange([startPos.X, startPos.Y, startPos.Z, startAngles.X, startAngles.Y, startAngles.Z]);
            props.Add(first);
            var list = new ScriptFloatListProperty { Name = "EndPose" };
            list.Data.AddRange([pos.X, pos.Y, pos.Z, angles.X, angles.Y, angles.Z]);
            props.Add(list);
            props.Add(new ScriptFloatProperty { Name = "Duration", Data = Math.Max(seconds, 0.1f) });
        }
        // Damage and drain (Daggerfall Unity DrainHealth21/DrainHealth/DrainMagicka): type 21 rolls between the
        // magnitude and the index; 22-25 take the raw axis value (a flat's magnitude) per level; 28 drains at least 1.
        if (a.Flags == 21)
        {
            props.Add(new ScriptFloatProperty { Name = "Magnitude", Data = a.Magnitude });
            props.Add(new ScriptIntProperty { Name = "Index", Data = o.SoundIndex });
        }
        if (a.Flags is >= 22 and <= 25)
            props.Add(new ScriptFloatProperty { Name = "Magnitude", Data = a.Axis });
        if (a.Flags == 28)
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
        // Unity's patches: 7701-7704 read as 7705, 7700 says nothing). Type 32 (unknown, on four objects) shows its
        // index's text, should TEXT.RSC have one.
        if (a.Flags == 11 && TextOf(ShowTextBase + o.SoundIndex) is { } shown)
            props.Add(new ScriptStringProperty { Name = "Text", Data = shown });
        if (a.Flags == 12 && Riddle(RiddleBase + o.SoundIndex) is { } riddle)
        {
            props.Add(new ScriptObjectProperty { Name = "Riddle", Object = riddle.Message.ToLink<ISkyrimMajorRecordGetter>() });
            props.Add(new ScriptIntProperty { Name = "RiddleAnswer", Data = riddle.Answer });
        }
        if (a.Flags == 99 && o.SoundIndex > 0 && TextOf(DoorTextBase + (o.SoundIndex is >= 1 and <= 4 ? 5 : o.SoundIndex)) is { } notice)
            props.Add(new ScriptStringProperty { Name = "Text", Data = notice });
        if (a.Flags == 32 && o.SoundIndex > 0 && (TextOf(ShowTextBase + o.SoundIndex) ?? TextOf(DoorTextBase + o.SoundIndex)) is { } said)
            props.Add(new ScriptStringProperty { Name = "Text", Data = said });
        // Dialogue (29): the person it stands for talks.
        if (a.Flags == 29 && speaker is not null)
            props.Add(new ScriptObjectProperty { Name = "Speaker", Object = speaker.ToLink<ISkyrimMajorRecordGetter>() });
        // Set global (31): Daggerfall's quest global variable, numbered by the raw axis value (Daggerfall Unity SetGlobalVar).
        if (a.Flags == 31)
            props.Add(new ScriptObjectProperty { Name = "QuestGlobal", Object = (QuestGlobal?.Invoke(a.Axis) ?? DfQuestGlobals.For(mod, a.Axis)).ToLink<ISkyrimMajorRecordGetter>() });
        // A door's starting lock, which closing it (20) puts back (Daggerfall Unity CloseDoor).
        if (forDoor && a.Flags == 20 && DoorPlacer.LockValue(o) is > 0 and var startLock)
            props.Add(new ScriptIntProperty { Name = "DoorLock", Data = startLock });
        var entry = new ScriptEntry { Name = "TMDfAction" };
        entry.Properties.AddRange(props);
        return entry;
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

    /// <summary>An end pose no different from the start (under a unit, half a degree).</summary>
    static bool Still((Vector3 Position, Vector3 AnglesDegrees, float Seconds) end, Matrix4x4 m)
    {
        var (start, startAngles) = Pose(m);
        startAngles *= 180 / MathF.PI;
        static float Wrap(float d) { d %= 360; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }
        float moved = Vector3.Distance(end.Position, new Vector3(start.X, start.Y, start.Z));
        float turned = MathF.Max(MathF.Abs(Wrap(end.AnglesDegrees.X - startAngles.X)), MathF.Max(MathF.Abs(Wrap(end.AnglesDegrees.Y - startAngles.Y)), MathF.Abs(Wrap(end.AnglesDegrees.Z - startAngles.Z))));
        return moved < 1 && turned < 0.5f;
    }

    /// <summary>
    /// A wheel's turn (tag WHE, a rotate action): the poses a quarter turn apart through whole turns (Daggerfall's
    /// magnitude rounded to whole turns, at least one) about its action axis, the last exactly its start pose, as
    /// Skyrim positions and angles in degrees (six floats each). Null for anything else.
    /// </summary>
    static List<float>? SpinPoses(DfRdbObject o, Matrix4x4 m)
    {
        var a = o.Action!;
        if (o.Tag != "WHE" || a.Flags != 8) return null;
        float deg = a.Magnitude / DfDungeonBlock.RotationDivisor;
        var unit = a.Axis switch { 1 => new Vector3(-1, 0, 0), 2 => new(1, 0, 0), 3 => new(0, -1, 0), 4 => new(0, 1, 0), 5 => new(0, 0, -1), 6 => new(0, 0, 1), _ => Vector3.Zero };
        if (unit == Vector3.Zero || MathF.Abs(deg) < 1) return null;
        float total = 360 * MathF.Max(1, MathF.Round(MathF.Abs(deg) / 360));
        int steps = (int)(total / 90);
        const float rad = MathF.PI / 180;
        var poses = new List<float>();
        for (int k = 1; k <= steps; k++)
        {
            var r = unit * (total * k / steps);
            var turned = DfDungeon.UnityRotZ(r.Z * rad) * DfDungeon.UnityRotX(r.X * rad) * DfDungeon.UnityRotY(r.Y * rad) * m;
            turned.Translation = m.Translation;
            var (p, angles) = Pose(k == steps ? m : turned);
            angles *= 180 / MathF.PI;
            poses.AddRange([p.X, p.Y, p.Z, angles.X, angles.Y, angles.Z]);
        }
        return poses;
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
    void AddTriggerBox(Cell cell, PlacedObject target, Vector3 min, Vector3 max, Matrix4x4 m, bool walkOn)
    {
        triggerBase ??= AddTriggerBase();
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
