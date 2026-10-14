using System.Runtime.InteropServices;

namespace TamrielMerge.Util;

/// <summary>The machine's free memory, for how much work to run at once.</summary>
public static class Machine
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    /// <summary>Physical memory free now, in GB.</summary>
    public static double FreeRamGb()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? s.AvailPhys / (double)(1L << 30) : 8;
    }
}
