using System.Reflection;
using System.Runtime.InteropServices;
using nifly;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Animation keys for nifly's key groups. The .NET wrapper doesn't expose nifly's NiAnimationKey&lt;T&gt;, only an
/// opaque pointer that AddKey copies from, so a key is laid out in native memory as nifly declares it (Keys.hpp:
/// NiKeyType type; float time; T value, forward, backward; TBC tension, bias, continuity) and passed by pointer.
/// </summary>
public static class NifKeys
{
    public const uint Linear = 1, Const = 5;

    static T Pointer<T>(IntPtr p) =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.NonPublic | BindingFlags.Instance, null, [p, false], null)!;

    /// <summary>Writes a key: type, time, value then (tangents, TBC) left zero; size is the struct's full size.</summary>
    static IntPtr Key(uint type, float time, int size, Action<IntPtr> value)
    {
        var p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
        Marshal.WriteInt32(p, 0, (int)type);
        Marshal.WriteInt32(p, 4, BitConverter.SingleToInt32Bits(time));
        value(p + 8);
        return p;
    }

    static void WriteFloats(IntPtr at, params float[] values)
    {
        for (int i = 0; i < values.Length; i++) Marshal.WriteInt32(at, 4 * i, BitConverter.SingleToInt32Bits(values[i]));
    }

    public static void Add(NiAnimationKeyGroupfloat group, float time, float value, uint type = Linear)
    {
        var p = Key(type, time, 8 + 3 * 4 + 12, at => WriteFloats(at, value));
        try { group.AddKey(Pointer<SWIGTYPE_p_nifly__NiAnimationKeyT_float_t>(p)); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>A bool key (value, forward, backward are bytes; TBC aligned to 4: 24 bytes).</summary>
    public static void Add(NiAnimationKeyGroupuchar group, float time, bool value, uint type = Const)
    {
        var p = Key(type, time, 24, at => Marshal.WriteByte(at, value ? (byte)1 : (byte)0));
        try { group.AddKey(Pointer<SWIGTYPE_p_nifly__NiAnimationKeyT_unsigned_char_t>(p)); }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static void Add(NiAnimationKeyGroupVector3 group, float time, System.Numerics.Vector3 value, uint type = Linear)
    {
        var p = Key(type, time, 8 + 3 * 12 + 12, at => WriteFloats(at, value.X, value.Y, value.Z));
        try { group.AddKey(Pointer<SWIGTYPE_p_nifly__NiAnimationKeyT_nifly__Vector3_t>(p)); }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static void Add(NiAnimationKeyGroupColor4 group, float time, System.Numerics.Vector4 value, uint type = Linear)
    {
        var p = Key(type, time, 8 + 3 * 16 + 12, at => WriteFloats(at, value.X, value.Y, value.Z, value.W));
        try { group.AddKey(Pointer<SWIGTYPE_p_nifly__NiAnimationKeyT_nifly__Color4_t>(p)); }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Reads a float key back (time, value), for checking what was written.</summary>
    public static (float Time, float Value) Read(NiAnimationKeyGroupfloat group, int index)
    {
        var key = group.GetKey(index);
        var handle = (HandleRef)typeof(SWIGTYPE_p_nifly__NiAnimationKeyT_float_t).GetMethod("getCPtr", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [key])!;
        return (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(handle.Handle, 4)), BitConverter.Int32BitsToSingle(Marshal.ReadInt32(handle.Handle, 8)));
    }
}
