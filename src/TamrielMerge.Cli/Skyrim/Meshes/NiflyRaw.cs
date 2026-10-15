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

    /// <summary>A block reference wrapped where the wrapper exposes it only as an opaque pointer type.</summary>
    public static NiRef Ref(object opaqueRef) => Wrap<NiRef>(Ptr(opaqueRef));
}
