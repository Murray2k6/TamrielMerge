using nifly;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// A door that swings, as Skyrim's own: Morrowind's and Oblivion's plain doors have no animation (those games turn a
/// door about its origin, the hinge), so their models are given one: everything under a node "Door", and an Open and
/// a Close sequence on the root turning that node a quarter turn about its up axis and back, laid out as vanilla's
/// doors are (ImpJailDoor02: a controller manager on the root, a multi-target transform controller, two clamped
/// sequences each with one transform link and its start and end text keys, the object palette; BSX animated, havok,
/// complex). The door is then a Skyrim door record: the game opens and closes it, says "Open" and "Close", locks it,
/// and its people open it on their way, which an activator swung by a script (TMSwingDoor, how these doors were
/// converted until 2026-11-14) never was to them.
/// <para>The node stands raised by <c>lift</c>, so the model's origin is at the door's foot (people only open a door
/// that stands on their floor: ObDoorFoot); the hinge stays where it was across and along.</para>
/// </summary>
public static class DoorSwing
{
    const uint NoBlock = uint.MaxValue;
    static readonly object NifLock = new();

    /// <summary>The seconds a door takes to swing.</summary>
    public const float Seconds = 0.8f;

    /// <param name="from">A converted model with keyframed collision (a door's "moving" form).</param>
    /// <param name="degrees">The turn when open, counter-clockwise from above (Bethesda's headings go clockwise: -90
    /// is the quarter turn clockwise TMSwingDoor made).</param>
    public static void Write(string from, string to, float lift, float degrees = -90f)
    {
        lock (NifLock)
        {
            using var nif = new NifFile();
            if (nif.Load(from) != 0) throw new InvalidDataException($"{from} can't be read back");
            var hdr = nif.GetHeader();
            var root = nif.GetRootNode();
            uint rootId = hdr.GetBlockID(root);
            if (string.IsNullOrEmpty(root.name.get())) root.name = new NiStringRef(Path.GetFileNameWithoutExtension(to));
            uint Add(NiObject block) => hdr.AddBlock(BlockMeshBuilder.Owned(block));

            var children = new List<uint>();
            for (uint i = 0; i < root.childRefs.GetSize(); i++)
                if (root.childRefs.GetBlockRef(i) is var id && id < hdr.GetNumBlocks()) children.Add(id);
            // The door: every part under one node (0x80: its transform is kept up to date, as vanilla's moving nodes).
            var door = nif.AddNode("Door", new MatTransform { translation = new Vector3(0, 0, lift), scale = 1 }, root);
            door.flags = 0x8008E;
            uint doorId = hdr.GetBlockID(door);
            foreach (uint id in children) nif.SetParentNode(hdr.GetBlockById(id), door);
            if (root.collisionRef.index < hdr.GetNumBlocks() && hdr.GetBlockById(root.collisionRef.index) is NiCollisionObject col)
            {
                door.collisionRef.index = root.collisionRef.index;
                col.targetRef.index = doorId;
                root.collisionRef.index = NoBlock;
            }

            // BSX: animated, havok, complex (vanilla MineSecretDoor01's 11).
            bool flagged = false;
            for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                if (hdr.GetBlockById(i) is BSXFlags bsx) { bsx.integerData |= 11; flagged = true; }
            if (!flagged) BlockMeshBuilder.AddBsxFlags(nif, root, 11);

            var manager = new NiControllerManager { flags = 0x4C, frequency = 1, phase = 0, startTime = float.MaxValue, stopTime = float.MinValue, cumulative = false };
            manager.targetRef.index = rootId;
            uint managerId = Add(manager);
            var multi = new NiMultiTargetTransformController { flags = 0x6C, frequency = 1, phase = 0, startTime = float.MaxValue, stopTime = float.MinValue };
            multi.targetRef.index = rootId;
            multi.targetRefs.AddBlockRef(doorId);
            uint multiId = Add(multi);

            float open = degrees * MathF.PI / 180f;
            uint Turn(float fromAngle, float toAngle)
            {
                // The node's whole transform while the sequence holds it: its place (raised), and the turn as Euler
                // keys about the up axis (the wrapper can't add quaternion keys: MwVfx).
                var data = new NiTransformData { rotationType = NiKeyType.XYZ_ROTATION_KEY };
                foreach (var g in new[] { data.xRotations, data.yRotations, data.zRotations }) g.SetInterpolationType(NiKeyType.LINEAR_KEY);
                NifKeys.Add(data.xRotations, 0, 0f); NifKeys.Add(data.xRotations, Seconds, 0f);
                NifKeys.Add(data.yRotations, 0, 0f); NifKeys.Add(data.yRotations, Seconds, 0f);
                NifKeys.Add(data.zRotations, 0, fromAngle); NifKeys.Add(data.zRotations, Seconds, toAngle);
                var interpolator = new NiTransformInterpolator { translation = new Vector3(0, 0, lift), rotation = new Quaternion { w = 1, x = 0, y = 0, z = 0 }, scale = 1 };
                interpolator.dataRef.index = Add(data);
                return Add(interpolator);
            }
            uint Sequence(string name, float fromAngle, float toAngle)
            {
                var seq = NiflyRaw.NewSequence(1);
                var slot = NiflyRaw.Links(seq)[0];
                slot.interpolatorRef.index = Turn(fromAngle, toAngle);
                slot.controllerRef.index = multiId;
                slot.priority = 0;
                slot.nodeName = new NiStringRef("Door");
                slot.ctrlType = new NiStringRef("NiTransformController");
                slot.propType = new NiStringRef("");
                slot.ctrlID = new NiStringRef("");
                slot.interpID = new NiStringRef("");
                NiflyRaw.Ref(slot.stringPaletteRef).index = NoBlock;
                seq.name = new NiStringRef(name);
                seq.cycleType = CycleType.CYCLE_CLAMP;
                seq.frequency = 1;
                seq.startTime = 0;
                seq.stopTime = Seconds;
                seq.weight = 1;
                seq.managerRef.index = managerId;
                seq.accumRootName = new NiStringRef(root.name.get());
                seq.textKeyRef.index = Add(NiflyRaw.NewTextKeys([(0f, "start"), (Seconds, "end")]));
                NiflyRaw.Ref(seq.stringPaletteRef).index = NoBlock;
                return Add(seq);
            }
            uint openId = Sequence("Open", 0, open), closeId = Sequence("Close", open, 0);

            var palette = NiflyRaw.NewPalette(2);
            var entries = NiflyRaw.PaletteObjects(palette);
            entries[0].name = new NiString(root.name.get()); entries[0].objectRef.index = rootId;
            entries[1].name = new NiString("Door"); entries[1].objectRef.index = doorId;
            palette.sceneRef.index = rootId;
            uint paletteId = Add(palette);

            var m = (NiControllerManager)hdr.GetBlockById(managerId);
            m.controllerSequenceRefs.AddBlockRef(openId);
            m.controllerSequenceRefs.AddBlockRef(closeId);
            m.objectPaletteRef.index = paletteId;
            m.nextControllerRef.index = multiId;
            root.controllerRef.index = managerId;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
            if (nif.Save(to) != 0) throw new IOException($"{to} can't be written");
        }
    }
}
