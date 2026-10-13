using System.Runtime.InteropServices;

namespace TamrielMerge.Gui;

/// <summary>
/// Pausing a running conversion: the converter and every process it started (builds, Blender, the voice generator's
/// workers), plus any extra processes named, are suspended and later resumed where they were (NtSuspendProcess /
/// NtResumeProcess). Nothing is lost. A held process's memory is then trimmed out to the page file (user, 2026-10-09:
/// "memory shouldnt be held if pausing for something else"), so the RAM is free for other programs while paused; it
/// pages back in as the work carries on.
/// </summary>
static class ProcessTree
{
    /// <summary>The process and all its descendants, by a snapshot of every process's parent.</summary>
    public static List<int> WithDescendants(int root)
    {
        var parents = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snap != IntPtr.Zero && snap != new IntPtr(-1))
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snap, ref e))
                do parents[(int)e.ProcessId] = (int)e.ParentProcessId; while (Process32Next(snap, ref e));
            CloseHandle(snap);
        }
        var result = new List<int> { root };
        for (int i = 0; i < result.Count; i++)
            foreach (var (pid, parent) in parents)
                if (parent == result[i] && pid != root && !result.Contains(pid)) result.Add(pid);
        return result;
    }

    public static int Suspend(IEnumerable<int> ids) => Each(ids, NtSuspendProcess);
    public static int Resume(IEnumerable<int> ids) => Each(ids, NtResumeProcess);

    /// <summary>Trims each process's working set (its RAM) out to the page file; returns the bytes freed.</summary>
    public static long Trim(IEnumerable<int> ids)
    {
        long freed = 0;
        foreach (int id in ids.Distinct())
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(id);
                long before = p.WorkingSet64;
                IntPtr h = OpenProcess(0x0400 | 0x0100, false, (uint)id);   // PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA
                if (h == IntPtr.Zero) continue;
                EmptyWorkingSet(h);
                CloseHandle(h);
                p.Refresh();
                freed += Math.Max(0, before - p.WorkingSet64);
            }
            catch { }
        }
        return freed;
    }

    static int Each(IEnumerable<int> ids, Func<IntPtr, int> act)
    {
        int n = 0;
        foreach (int id in ids.Distinct())
        {
            IntPtr h = OpenProcess(0x0800, false, (uint)id);   // PROCESS_SUSPEND_RESUME
            if (h == IntPtr.Zero) continue;
            if (act(h) == 0) n++;
            CloseHandle(h);
        }
        return n;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr process);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
}
