using nifly;
using TamrielMerge.Skyrim.Meshes;
using V3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's moving objects (secret walls, gates, levers, drawbridges, Oblivion gate doors, portcullises) as Skyrim
/// animated models. Oblivion keeps a model's animations as Skyrim does: a controller manager on the root with named
/// controller sequences (Forward, Backward, Open, Close, Idle...), each binding a transform interpolator (with its
/// keyframe data) to a node by name, through one multi-target transform controller. So the node tree is kept (shapes
/// in their node's space), and the sequences are carried over block for block: the interpolators and keyframe data
/// as they are, the sequences with their controlled blocks named directly (Skyrim) instead of through a string palette
/// (Oblivion), and the object palette pointing at the new nodes. Only transform animation is kept (visibility,
/// material, texture, morph and particle controllers are dropped from the sequences). Skyrim's PlayGamebryoAnimation
/// plays a sequence by name; a door with Open and Close sequences opens and closes by itself.
///
/// The collision moves with its node: a moving node's bodies are keyframed on the animated-static layer (layer 2,
/// motion keyframed, as vanilla MineSecretDoor01 has them), on the node itself; still parts keep static collision.
/// A model whose only animation is an Idle loop gets it as controllers on its nodes, which run on their own.
/// </summary>
public sealed partial class ObMeshConverter
{
    const uint NoBlock = 0xFFFFFFFF;

    /// <summary>A model's animation, as read from the Oblivion file.</summary>
    sealed class Animation
    {
        public uint Manager = NoBlock;
        /// <summary>The sequences, with the transform links kept from each (in order).</summary>
        public List<(uint Id, string Name, List<Link> Links)> Sequences = [];
        /// <summary>Nodes the animation moves.</summary>
        public HashSet<uint> Moved = [];
        /// <summary>Nodes with transform controllers of their own (they loop by themselves).</summary>
        public Dictionary<uint, uint> NodeControllers = [];
        /// <summary>What the converted model plays: sequence names and lengths.</summary>
        public List<(string Name, float Length)> Played = [];
    }

    sealed record Link(uint Node, string NodeName, uint Interpolator, byte Priority);

    /// <summary>
    /// The model's transform animation: its root's controller manager's sequences (each link to a transform
    /// controller with a transform interpolator, on a node found by name) and nodes with transform controllers of their
    /// own. Null when nothing moves.
    /// </summary>
    static Animation? ReadAnimation(NifFile nif, byte[] file)
    {
        var hdr = nif.GetHeader();
        uint count = hdr.GetNumBlocks();
        var root = nif.GetRootNode();
        uint rootId = hdr.GetBlockID(root);
        var byName = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
            if (hdr.GetBlockById(i) is NiNode n && n.name.get() is { Length: > 0 } name) byName.TryAdd(name, i);
        var a = new Animation();
        if (root.controllerRef.index < count && hdr.GetBlockById(root.controllerRef.index) is NiControllerManager manager)
        {
            a.Manager = root.controllerRef.index;
            byte[] palette = [];
            for (uint k = 0; k < manager.controllerSequenceRefs.GetSize(); k++)
            {
                uint seqId = manager.controllerSequenceRefs.GetBlockRef(k);
                if (seqId >= count || hdr.GetBlockById(seqId) is not NiControllerSequence seq) continue;
                var links = new List<Link>();
                foreach (var link in NiflyRaw.Links(seq))
                {
                    uint pal = NiflyRaw.Ref(link.stringPaletteRef).index;
                    if (palette.Length == 0 && pal < count && hdr.GetBlockById(pal) is NiStringPalette sp) palette = NiflyRaw.Palette(file, sp);
                    string node = link.nodeName.get() is { Length: > 0 } nn ? nn : NiflyRaw.PaletteString(palette, link.nodeNameOffset);
                    string type = link.ctrlType.get() is { Length: > 0 } ct ? ct : NiflyRaw.PaletteString(palette, link.controllerTypeOffset);
                    uint interp = link.interpolatorRef.index;
                    if (type != "NiTransformController" || interp >= count || hdr.GetBlockTypeStringById(interp) != "NiTransformInterpolator") continue;
                    if (!byName.TryGetValue(node, out uint target)) continue;
                    // The root holds still in Oblivion's sequences (a pose without keys); the reference places it.
                    bool keyed = hdr.GetBlockById(interp) is NiTransformInterpolator ti && ti.dataRef.index < count;
                    if (target == rootId && !keyed) continue;
                    links.Add(new Link(target, node, interp, link.priority));
                    if (keyed) a.Moved.Add(target);
                }
                if (links.Count > 0) a.Sequences.Add((seqId, seq.name.get(), links));
            }
        }
        for (uint i = 0; i < count; i++)
        {
            if (hdr.GetBlockById(i) is not NiNode n || n.controllerRef.index >= count) continue;
            if (hdr.GetBlockById(n.controllerRef.index) is NiTransformController tc && tc.interpolatorRef.index < count && hdr.GetBlockTypeStringById(tc.interpolatorRef.index) == "NiTransformInterpolator")
            {
                a.NodeControllers[i] = n.controllerRef.index;
                a.Moved.Add(i);
            }
        }
        return a.Moved.Count > 0 ? a : null;
    }

    /// <summary>Whether a node or one of its parents moves.</summary>
    static bool MovesWith(NifFile nif, Animation a, uint node)
    {
        var hdr = nif.GetHeader();
        for (NiNode? n = hdr.GetBlockById(node) as NiNode; n is not null; n = nif.GetParentNode(n))
            if (a.Moved.Contains(hdr.GetBlockID(n))) return true;
        return false;
    }

    /// <summary>
    /// Writes an animated model: the source's node tree under a BSFadeNode named as the source root (sequences find
    /// their targets by name), each piece on its node, collision on its node (keyframed where it moves), then the
    /// animation.
    /// </summary>
    void WriteAnimated(NifFile src, Animation a, List<Piece> pieces, Dictionary<uint, List<(V3[] Pts, V3 N)>> collision, string outPath, bool moving)
    {
        var sh = src.GetHeader();
        var srcRoot = src.GetRootNode();
        uint srcRootId = sh.GetBlockID(srcRoot);
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var hdr = nif.GetHeader();
        var fade = BlockMeshBuilder.Owned(new BSFadeNode());
        fade.name = new NiStringRef(srcRoot.name.get() is { Length: > 0 } rn ? rn : Path.GetFileNameWithoutExtension(outPath));
        fade.transform = srcRoot.transform;
        hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        uint rootId = hdr.GetBlockID(root);

        // The node tree. Moving nodes get Skyrim's "update transforms" flag (0x80), as vanilla animated nodes have.
        var map = new Dictionary<uint, uint> { [srcRootId] = rootId };
        void Nodes(NiNode from, NiNode to)
        {
            for (uint k = 0; k < from.childRefs.GetSize(); k++)
            {
                uint id = from.childRefs.GetBlockRef(k);
                if (id >= sh.GetNumBlocks() || sh.GetBlockById(id) is not NiNode child || map.ContainsKey(id)) continue;
                string name = child.name.get() ?? "";
                if (IsEditorMarker(name) || (child.flags & 1) != 0) continue;
                var added = nif.AddNode(name.Length > 0 ? name : $"Node{id}", child.transform, to);
                added.flags = a.Moved.Contains(id) ? 0x8008Eu : 0x8000Eu;
                map[id] = hdr.GetBlockID(added);
                Nodes(child, added);
            }
        }
        Nodes(srcRoot, root);
        NiNode NodeFor(uint source) => (NiNode)hdr.GetBlockById(map.TryGetValue(source, out var id) ? id : rootId);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shapes = new Dictionary<uint, uint>();
        foreach (var p in pieces)
        {
            if (AddPiece(nif, p, names) is not { } shape) continue;
            nif.SetParentNode(shape, NodeFor(p.Parent));
            shapes[p.Source] = hdr.GetBlockID(shape);
        }

        bool havok = false;
        foreach (var (source, polys) in collision)
        {
            if (polys.Count == 0) continue;
            bool moves = moving || MovesWith(src, a, source);
            var tex = polys.Select(s => (s.Pts, s.N, new BlockMeshBuilder.Tex("", "", false))).ToList();
            BlockMeshBuilder.AddCollision(nif, NodeFor(source), moves ? tex : Stairs(tex, false), moves, onTarget: moves);
            havok = true;
        }
        // 0x1 animated (the controllers run), 0x2 havok, 0x8 complex: vanilla MineSecretDoor01's 11.
        BlockMeshBuilder.AddBsxFlags(nif, root, havok ? 11 : 1);

        AddAnimation(src, a, nif, map, shapes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
    }

    /// <summary>The source's sequences (or its Idle loop as node controllers) on the converted model.</summary>
    static void AddAnimation(NifFile src, Animation a, NifFile nif, Dictionary<uint, uint> map, Dictionary<uint, uint> shapes)
    {
        var sh = src.GetHeader();
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        uint rootId = hdr.GetBlockID(root);
        using var cache = new niflycpp.BlockCache(sh);
        var copies = new Dictionary<uint, uint>();
        uint Add(NiObject block) => hdr.AddBlock(BlockMeshBuilder.Owned(block));

        // An interpolator and its keyframe data, copied once.
        uint Interpolator(uint id)
        {
            if (copies.TryGetValue(id, out var hit)) return hit;
            var ti = cache.EditableBlockById<NiTransformInterpolator>(id);
            uint data = ti.dataRef.index;
            ti.dataRef.index = data < sh.GetNumBlocks() ? Copy<NiTransformData>(data) : NoBlock;
            return copies[id] = Add(ti);
        }
        uint Copy<T>(uint id) where T : NiObject
        {
            if (copies.TryGetValue(id, out var hit)) return hit;
            return copies[id] = Add(cache.EditableBlockById<T>(id));
        }

        // Nodes with controllers of their own; and a model whose only sequence is an Idle loop gets it the same way
        // (Skyrim plays no sequence by itself).
        var direct = a.NodeControllers.Select(kv => (Node: kv.Key, Source: (NiTransformController?)sh.GetBlockById(kv.Value), Interp: ((NiTransformController)sh.GetBlockById(kv.Value)).interpolatorRef.index)).ToList();
        var sequences = a.Sequences;
        if (sequences.Count == 1 && sequences[0].Name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            && sh.GetBlockById(sequences[0].Id) is NiControllerSequence { cycleType: CycleType.CYCLE_LOOP } idle)
        {
            foreach (var l in sequences[0].Links.Where(l => a.Moved.Contains(l.Node) && !a.NodeControllers.ContainsKey(l.Node)))
                direct.Add((l.Node, null, l.Interpolator));
            a.Played.Add(("Idle", idle.stopTime - idle.startTime));
            sequences = [];
        }
        foreach (var (node, source, interp) in direct)
        {
            if (!map.TryGetValue(node, out var target)) continue;
            var idleSeq = source is null ? (NiControllerSequence)sh.GetBlockById(a.Sequences[0].Id) : null;
            var ctlr = new NiTransformController
            {
                // Active and looping (0x8, cycle 0) with Skyrim's 0x40, as vanilla's looping node controllers.
                flags = (ushort)(source is null ? 0x48 : (source.flags & 0x6) | 0x48),
                frequency = source?.frequency ?? idleSeq!.frequency,
                phase = source?.phase ?? 0,
                startTime = source?.startTime ?? idleSeq!.startTime,
                stopTime = source?.stopTime ?? idleSeq!.stopTime,
            };
            ctlr.targetRef.index = target;
            ctlr.interpolatorRef.index = Interpolator(interp);
            uint ctlrId = Add(ctlr);
            ((NiNode)hdr.GetBlockById(target)).controllerRef.index = ctlrId;
        }
        if (sequences.Count == 0) return;

        // The manager on the root, its multi-target transform controller over the moving nodes (vanilla's flags).
        var manager = new NiControllerManager { flags = 0x4C, frequency = 1, phase = 0, startTime = float.MaxValue, stopTime = float.MinValue, cumulative = false };
        manager.targetRef.index = rootId;
        uint managerId = Add(manager);
        var multi = new NiMultiTargetTransformController { flags = 0x6C, frequency = 1, phase = 0, startTime = float.MaxValue, stopTime = float.MinValue };
        multi.targetRef.index = rootId;
        foreach (var node in sequences.SelectMany(s => s.Links).Select(l => l.Node).Distinct())
            if (map.TryGetValue(node, out var t)) multi.targetRefs.AddBlockRef(t);
        uint multiId = Add(multi);

        var seqIds = new List<uint>();
        foreach (var (id, name, links) in sequences)
        {
            var seq = cache.EditableBlockById<NiControllerSequence>(id);
            var slots = NiflyRaw.Links(seq);
            var kept = links.Where(l => map.ContainsKey(l.Node)).ToList();
            for (int k = 0; k < kept.Count; k++)
            {
                var slot = slots[k];
                slot.interpolatorRef.index = Interpolator(kept[k].Interpolator);
                slot.controllerRef.index = multiId;
                slot.priority = kept[k].Priority;
                slot.nodeName = new NiStringRef(kept[k].NodeName);
                slot.ctrlType = new NiStringRef("NiTransformController");
                slot.propType = new NiStringRef("");
                slot.ctrlID = new NiStringRef("");
                slot.interpID = new NiStringRef("");
                NiflyRaw.Ref(slot.stringPaletteRef).index = NoBlock;
            }
            NiflyRaw.Truncate(seq.controlledBlocks, slots, kept.Count);
            seq.textKeyRef.index = seq.textKeyRef.index < sh.GetNumBlocks() ? TextKeys(seq.textKeyRef.index) : NoBlock;
            seq.managerRef.index = managerId;
            seq.accumRootName = new NiStringRef(root.name.get());
            NiflyRaw.Ref(seq.stringPaletteRef).index = NoBlock;
            seqIds.Add(Add(seq));
            a.Played.Add((name, seq.stopTime - seq.startTime));
        }

        // Text keys: the start and end of the sequence (Oblivion's sound keys name Oblivion's sounds).
        uint TextKeys(uint id)
        {
            if (copies.TryGetValue(id, out var hit)) return hit;
            var tk = cache.EditableBlockById<NiTextKeyExtraData>(id);
            var keys = NiflyRaw.TextKeys(tk);
            var keep = keys.Select(k => (k.time, Value: k.value.get())).Where(k => k.Value is "start" or "end").ToList();
            for (int k = 0; k < keep.Count; k++) { keys[k].time = keep[k].time; keys[k].value = new NiStringRef(keep[k].Value); }
            NiflyRaw.Truncate(tk.textKeys, keys, keep.Count);
            return copies[id] = Add(tk);
        }

        // The object palette: the source's, pointing at the converted nodes and shapes.
        uint paletteId = NoBlock;
        if (sh.GetBlockById(a.Manager) is NiControllerManager srcManager && srcManager.objectPaletteRef.index < sh.GetNumBlocks())
        {
            var palette = cache.EditableBlockById<NiDefaultAVObjectPalette>(srcManager.objectPaletteRef.index);
            var entries = NiflyRaw.PaletteObjects(palette);
            var keep = entries.Select(e => (Name: e.name.get(), Source: e.objectRef.index))
                .Select(e => (e.Name, Target: map.TryGetValue(e.Source, out var n) ? n : shapes.TryGetValue(e.Source, out var s) ? s : NoBlock))
                .Where(e => e.Target != NoBlock).ToList();
            // The root under its converted name.
            keep = keep.Select(e => e.Target == rootId ? (root.name.get(), e.Target) : e).ToList();
            for (int k = 0; k < keep.Count; k++) { entries[k].name = new NiString(keep[k].Name); entries[k].objectRef.index = keep[k].Target; }
            NiflyRaw.Truncate(palette.objects, entries, keep.Count);
            palette.sceneRef.index = rootId;
            paletteId = Add(palette);
        }

        var m = (NiControllerManager)hdr.GetBlockById(managerId);
        foreach (var s in seqIds) m.controllerSequenceRefs.AddBlockRef(s);
        m.objectPaletteRef.index = paletteId;
        m.nextControllerRef.index = multiId;
        root.controllerRef.index = managerId;
    }
}
