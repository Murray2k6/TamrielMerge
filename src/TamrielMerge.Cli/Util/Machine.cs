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

    /// <summary>
    /// Memory that can still be committed now (the commit limit, RAM and page file together, less what is committed), in
    /// GB: what running out of gives "Not enough memory" errors (2026-10-16: 35.9 of 38.9 GB committed with 3.6 GB of RAM
    /// still free, the user's other programs failing to start).
    /// </summary>
    public static double FreeCommitGb()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? s.AvailPageFile / (double)(1L << 30) : 8;
    }

    /// <summary>For a build's log: this process's memory now (private, and the managed heap) and its peak so far.</summary>
    public static string MemoryNote()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return $"[memory {self.PrivateMemorySize64 / (double)(1L << 30):F1} GB, managed {GC.GetTotalMemory(false) / (double)(1L << 30):F1} GB, peak {self.PeakWorkingSet64 / (double)(1L << 30):F1} GB]";
    }
}
