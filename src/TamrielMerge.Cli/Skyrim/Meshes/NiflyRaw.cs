using System.Reflection;
using System.Runtime.InteropServices;
using nifly;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// The elements of nifly's sync vectors, which the .NET wrapper keeps opaque (a sequence's controlled blocks, an
/// object palette's objects, text keys). Each element is found through what the wrapper does expose: the vector's
/// string references (or block pointers) point into its elements, so an element's address is that pointer less the
/// member's offset, measured once on a standalone element. The element is then wrapped as its .NET class, reading and
/// writing the block in place. Vectors are MSVC std::vectors (first, last, end at the start of the sync vector); a
/// vector is shortened by moving its end back (the dropped elements' strings are leaked, a few bytes).
/// </summary>
public static class NiflyRaw
{
    public static IntPtr Ptr(object proxy)
    {
        var m = proxy.GetType().GetMethod("getCPtr", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        return ((HandleRef)m.Invoke(null, [proxy])!).Handle;
    }

    /// <summary>A .NET wrapper over native memory nifly owns (not freed by .NET).</summary>
    public static T Wrap<T>(IntPtr p) =>
        (T)System.Activator.CreateInstance(typeof(T), BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public, null, [p, false], null)!;

    static readonly Dictionary<Type, (long Offset, int PerElement)> offsets = [];

    /// <summary>Where an element's first string reference sits in it, and how many it has.</summary>
    static (long Offset, int PerElement) StringRefLayout<T>(Func<T> make, Action<T, vectorNiStringRefPtr> refs) where T : class
    {
        lock (offsets)
        {
            if (offsets.TryGetValue(typeof(T), out var hit)) return hit;
            var probe = make();
            using var list = new vectorNiStringRefPtr();
            refs(probe, list);
            return offsets[typeof(T)] = ((long)Ptr(list[0]) - (long)Ptr(probe), list.Count);
        }
    }

    static List<T> ByStringRefs<T>(Action<vectorNiStringRefPtr> vectorRefs, Func<T> make, Action<T, vectorNiStringRefPtr> elementRefs) where T : class
    {
        var (offset, per) = StringRefLayout(make, elementRefs);
        using var refs = new vectorNiStringRefPtr();
        vectorRefs(refs);
        var result = new List<T>();
        for (int i = 0; i + per <= refs.Count; i += per) result.Add(Wrap<T>((IntPtr)((long)Ptr(refs[i]) - offset)));
        return result;
    }

    /// <summary>A sequence's controlled blocks (ControllerLink), in order.</summary>
    public static List<ControllerLink> Links(NiSequence seq)
    {
        var vec = seq.controlledBlocks;
        return ByStringRefs(r => vec.GetStringRefs(r), () => new ControllerLink(), (l, r) => l.GetStringRefs(r));
    }

    /// <summary>A text key block's keys, in order.</summary>
    public static List<NiTextKey> TextKeys(NiTextKeyExtraData data)
    {
        var vec = data.textKeys;
        return ByStringRefs(r => vec.GetStringRefs(r), () => new NiTextKey(), (k, r) => k.GetStringRefs(r));
    }

    static long? avObjectRefOffset;

    /// <summary>An object palette's entries (name, object), in order (found by their object pointers).</summary>
    public static List<AVObject> PaletteObjects(NiDefaultAVObjectPalette palette)
    {
        if (avObjectRefOffset is null)
        {
            var probe = new AVObject();
            avObjectRefOffset = (long)Ptr(probe.objectRef) - (long)Ptr(probe);
        }
        using var set = new setNiRef();
        palette.objects.GetPtrs(set);
        return set.Select(r => (long)Ptr(r)).Order().Select(p => Wrap<AVObject>((IntPtr)(p - avObjectRefOffset.Value))).ToList();
    }

    /// <summary>The elements of a sync vector of plain structs of <paramref name="size"/> bytes (no pointers or strings
    /// to find them by), e.g. furniture positions (24).</summary>
    public static List<T> Elements<T>(object syncVector, int size) where T : class
    {
        IntPtr vec = Ptr(syncVector);
        long first = Marshal.ReadInt64(vec, 0), last = Marshal.ReadInt64(vec, 8);
        if (last < first || (last - first) % size != 0) throw new InvalidOperationException("unexpected vector layout");
        return Enumerable.Range(0, (int)((last - first) / size)).Select(i => Wrap<T>((IntPtr)(first + (long)i * size))).ToList();
    }

    /// <summary>Shortens a sync vector whose elements are <paramref name="elements"/> to its first
    /// <paramref name="count"/>.</summary>
    public static void Truncate<T>(object syncVector, List<T> elements, int count) where T : class
    {
        if (count >= elements.Count) return;
        IntPtr vec = Ptr(syncVector);
        long first = Marshal.ReadInt64(vec, 0);
        if (elements.Count == 0 || first != (long)Ptr(elements[0])) throw new InvalidOperationException("unexpected vector layout");
        long stride = elements.Count > 1 ? (long)Ptr(elements[1]) - first : Marshal.ReadInt64(vec, 8) - first;
        if (Marshal.ReadInt64(vec, 8) != first + stride * elements.Count) throw new InvalidOperationException("unexpected vector layout");
        Marshal.WriteInt64(vec, 8, first + stride * count);
    }

    /// <summary>The bytes of an MSVC std::string (16-byte small buffer or pointer, size, capacity).</summary>
    public static byte[] StdString(IntPtr p)
    {
        long size = Marshal.ReadInt64(p, 16), cap = Marshal.ReadInt64(p, 24);
        IntPtr data = cap >= 16 ? Marshal.ReadIntPtr(p) : p;
        var b = new byte[size];
        Marshal.Copy(data, b, 0, (int)size);
        return b;
    }

    /// <summary>
    /// A string palette's bytes (Gamebryo 10.2-20.0 sequences name their targets by offsets into it). nifly keeps only
    /// the palette's first string, so the block is found in the file by its length and that first string.
    /// </summary>
    public static byte[] Palette(byte[] file, NiStringPalette palette)
    {
        var first = StdString(Ptr(palette.palette));
        var pat = new byte[4 + first.Length + 1];
        BitConverter.GetBytes(palette.length).CopyTo(pat, 0);
        first.CopyTo(pat, 4);
        int at = file.AsSpan().IndexOf(pat);
        return at < 0 || at + 4 + palette.length > file.Length ? first : file.AsSpan(at + 4, (int)palette.length).ToArray();
    }

    public static string PaletteString(byte[] palette, uint offset)
    {
        if (offset >= palette.Length) return "";
        int end = Array.IndexOf(palette, (byte)0, (int)offset);
        return System.Text.Encoding.ASCII.GetString(palette, (int)offset, (end < 0 ? palette.Length : end) - (int)offset);
    }

    const int TemplateLinks = 128;
    static NifFile? template;
    static uint templateSequence, templatePalette;

    /// <summary>
    /// A new controller sequence with <paramref name="links"/> controlled blocks to fill (through <see cref="Links"/>):
    /// the wrapper can't add a sequence's controlled blocks, so a sequence with 128 blank ones is written once (nifly
    /// saves an empty one, and the blank blocks - no interpolator, controller or names - are put into the file's bytes,
    /// its block size and count with them), loaded, and copied here and cut to size.
    /// </summary>
    public static NiControllerSequence NewSequence(int links)
    {
        if (links > TemplateLinks) throw new ArgumentOutOfRangeException(nameof(links), $"at most {TemplateLinks} controlled blocks");
        lock (offsets)
        {
            if (template is null)
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"tm_seqtemplate_{Environment.ProcessId}.nif");
                using (var nif = new NifFile())
                {
                    nif.Create(NiVersion.getSSE());
                    // A manager on the root holding the sequence and the palette (nifly saves what the root reaches).
                    var hdr0 = nif.GetHeader();
                    var seq = new NiControllerSequence();
                    GC.SuppressFinalize(seq);
                    seq.name = new NiStringRef("Template");
                    uint seqId = hdr0.AddBlock(seq);
                    var palette = new NiDefaultAVObjectPalette();
                    GC.SuppressFinalize(palette);
                    uint paletteId = hdr0.AddBlock(palette);
                    var manager = new NiControllerManager();
                    GC.SuppressFinalize(manager);
                    manager.controllerSequenceRefs.AddBlockRef(seqId);
                    manager.objectPaletteRef.index = paletteId;
                    nif.GetRootNode().controllerRef.index = hdr0.AddBlock(manager);
                    if (nif.Save(tmp) != 0) throw new IOException("nifly couldn't save the sequence template");
                }
                // A controlled block: interpolator and controller refs, priority, five string indices (29 bytes), after
                // the sequence's name, count and growth; a palette entry: an empty name and no object (8 bytes), after
                // the palette's scene.
                var link = new byte[29];
                Array.Fill(link, (byte)0xFF);
                link[8] = 0;
                var bytes = WithBlank(File.ReadAllBytes(tmp), "NiControllerSequence", 4, 12, link, TemplateLinks);
                bytes = WithBlank(bytes, "NiDefaultAVObjectPalette", 4, 8, [0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF], TemplateLinks);
                File.WriteAllBytes(tmp, bytes);
                template = new NifFile();
                if (template.Load(tmp) != 0) throw new IOException("nifly couldn't read the sequence template");
                File.Delete(tmp);
                var hdr = template.GetHeader();
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    if (hdr.GetBlockTypeStringById(i) == "NiControllerSequence") templateSequence = i;
                    if (hdr.GetBlockTypeStringById(i) == "NiDefaultAVObjectPalette") templatePalette = i;
                }
            }
            // A new copy each time (EditableBlockById hands out the same one for a block).
            var copy = niflycpp.BlockCache.SafeClone<NiControllerSequence>(template.GetHeader().GetBlockById(templateSequence));
            var slots = Links(copy);
            if (slots.Count != TemplateLinks) throw new InvalidOperationException($"the sequence template has {slots.Count} controlled blocks");
            Truncate(copy.controlledBlocks, slots, links);
            return copy;
        }
    }

    /// <summary>A new object palette with <paramref name="entries"/> entries to fill (<see cref="PaletteObjects"/>),
    /// copied from the template as <see cref="NewSequence"/>'s sequences are.</summary>
    public static NiDefaultAVObjectPalette NewPalette(int entries)
    {
        NewSequence(0);   // the template, made once
        lock (offsets)
        {
            var copy = niflycpp.BlockCache.SafeClone<NiDefaultAVObjectPalette>(template!.GetHeader().GetBlockById(templatePalette));
            var slots = PaletteObjects(copy);
            if (slots.Count != TemplateLinks) throw new InvalidOperationException($"the palette template has {slots.Count} entries");
            Truncate(copy.objects, slots, entries);
            return copy;
        }
    }

    /// <summary>
    /// A Skyrim SE NIF (20.2.0.7, Bethesda stream 100) with <paramref name="count"/> blank elements put into the vector
    /// of the first block of that type, which nifly wrote empty: its count, <paramref name="countAt"/> bytes into the
    /// block, goes up, the elements go in at <paramref name="insertAt"/>, and the block's size with them.
    /// </summary>
    static byte[] WithBlank(byte[] file, string blockType, int countAt, int insertAt, byte[] element, int count)
    {
        int p = Array.IndexOf(file, (byte)10) + 1;
        p += 4 + 1 + 4;                                  // version, endianness, user version
        int blocks = BitConverter.ToInt32(file, p); p += 4;
        uint stream = BitConverter.ToUInt32(file, p); p += 4;
        p += 1 + file[p];                                // author
        if (stream > 130) p += 4;
        p += 1 + file[p];                                // process script
        p += 1 + file[p];                                // export script
        if (stream >= 103) p += 1 + file[p];             // max file path
        int typeCount = BitConverter.ToUInt16(file, p); p += 2;
        var types = new List<string>();
        for (int k = 0; k < typeCount; k++) { int l = BitConverter.ToInt32(file, p); p += 4; types.Add(System.Text.Encoding.ASCII.GetString(file, p, l)); p += l; }
        int indexAt = p; p += 2 * blocks;
        int sizesAt = p; p += 4 * blocks;
        int strings = BitConverter.ToInt32(file, p); p += 8;
        for (int k = 0; k < strings; k++) p += 4 + BitConverter.ToInt32(file, p);
        int groups = BitConverter.ToInt32(file, p); p += 4 + 4 * groups;
        int at = p, target = -1;
        for (int i = 0; i < blocks; i++)
        {
            if (types[BitConverter.ToUInt16(file, indexAt + 2 * i) & 0x7FFF] == blockType) { target = i; break; }
            at += BitConverter.ToInt32(file, sizesAt + 4 * i);
        }
        if (target < 0) throw new InvalidDataException($"no {blockType} to add elements to");
        var output = new List<byte>(file.Length + element.Length * count);
        output.AddRange(file.AsSpan(0, at + countAt).ToArray());
        output.AddRange(BitConverter.GetBytes(BitConverter.ToInt32(file, at + countAt) + count));
        output.AddRange(file.AsSpan(at + countAt + 4, insertAt - countAt - 4).ToArray());
        for (int k = 0; k < count; k++) output.AddRange(element);
        output.AddRange(file.AsSpan(at + insertAt).ToArray());
        var bytes = output.ToArray();
        BitConverter.GetBytes(BitConverter.ToInt32(file, sizesAt + 4 * target) + element.Length * count).CopyTo(bytes, sizesAt + 4 * target);
        return bytes;
    }

    /// <summary>A block reference wrapped where the wrapper exposes it only as an opaque pointer type.</summary>
    public static NiRef Ref(object opaqueRef) => Wrap<NiRef>(Ptr(opaqueRef));
}
