using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TamrielMerge.Util;

/// <summary>
/// The user's drives, looked at for where a build writes fastest (user, 2026-10-13: "with a users dir drives must be
/// looked at automatically"). A hard disk slows a build that writes many small files to a crawl (its head seeks between
/// them while the processors wait), so a build whose output is on one is staged on a solid-state drive and moved there
/// once at the end. The drives are asked whether they seek (Windows' own test, as its defragmenter uses), so nothing is
/// assumed about which letter is which.
/// </summary>
public static class Drives
{
    /// <summary>True for a spinning disk, false for a solid-state drive, null when Windows doesn't say.</summary>
    public static bool? Seeks(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        if (root.Length < 2 || root[1] != ':') return null;
        using var h = CreateFile($@"\\.\{root[..2]}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var query = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 };   // StorageDeviceSeekPenaltyProperty, standard
        var result = new SeekPenalty();
        if (!DeviceIoControl(h, 0x2D1400, ref query, Marshal.SizeOf<StoragePropertyQuery>(), out result, Marshal.SizeOf<SeekPenalty>(), out _, IntPtr.Zero))
            return null;
        return result.IncursSeekPenalty != 0;
    }

    /// <summary>
    /// Where to build output meant for <paramref name="outDir"/>: the build folder the user chose (the app's Options or
    /// paths.json: <see cref="Paths.Build"/>), whatever drive it is on, else <paramref name="outDir"/> itself (null: "build
    /// in place"). Until 2026-10-28 a hard-disk output was staged on a solid-state drive found by itself (the user's temp
    /// folder or a drive's root); the user: "the tool needs to convert on any drive letter that a user has", "it only
    /// made the folder and went back to d" - everything now goes where the output is pointed unless a build folder says
    /// otherwise. <paramref name="needBytes"/> and <paramref name="name"/> are kept for the callers.
    /// </summary>
    public static string? FastStaging(string outDir, long needBytes, string name)
    {
        _ = needBytes; _ = name;
        if (Paths.Build is { } chosen)
            return string.Equals(Path.GetFullPath(chosen).TrimEnd('\\'), Path.GetFullPath(outDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : chosen;
        return null;
    }

    /// <summary>
    /// The room a build leaves free on the drive holding <paramref name="path"/> (user, 2026-10-13: "leave enough room
    /// for windows"): on Windows' own drive 32 GB, for its updates, page file, hibernation and temporary files; 10 GB on
    /// any other.
    /// </summary>
    public static long Reserve(string path) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)
            ? 32L << 30 : 10L << 30;

    /// <summary>
    /// A staging folder an earlier, stopped build left (named <paramref name="name"/>, in the user's temp folder or at a
    /// solid-state drive's root, with something in it), or null: a resumed build carries on there whatever room the
    /// estimate wants (2026-10-13: a resume found too little room for a whole new build and rebuilt the towns on the
    /// hard disk instead).
    /// </summary>
    public static string? ExistingStaging(string name)
    {
        _ = name;
        // Only the build folder the user chose: a stopped build is wherever the output or that folder is (since
        // 2026-10-28 nothing is staged elsewhere by itself).
        return Paths.Build is { } chosen && Directory.Exists(chosen) && Directory.EnumerateFileSystemEntries(chosen).Any() ? chosen : null;
    }

    /// <summary>Free bytes on the drive holding <paramref name="path"/>.</summary>
    public static long FreeBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    /// <summary>
    /// Moves everything under <paramref name="from"/> into <paramref name="to"/> (replacing what is there), one file at a
    /// time so a hard disk writes each in one go, then removes the emptied folders. Returns the number of files.
    /// </summary>
    public static int MoveInto(string from, string to, Action<double>? progress = null)
    {
        if (!Directory.Exists(from)) return 0;
        // On one volume a folder that isn't in the output yet moves whole, a rename (530,880 files moved one by one
        // took 7.9 minutes on 2026-11-05).
        int whole = 0;
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(to);
            void MoveFolders(string src, string dst)
            {
                foreach (var dir in Directory.GetDirectories(src))
                {
                    string target = Path.Combine(dst, Path.GetFileName(dir));
                    if (!Directory.Exists(target) && !File.Exists(target))
                    {
                        int n = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
                        try { Directory.Move(dir, target); whole += n; continue; }
                        catch (IOException) { }
                    }
                    MoveFolders(dir, target);
                }
            }
            MoveFolders(from, to);
        }
        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, files[i]));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(files[i], target, overwrite: true);
            if (i % 256 == 0) progress?.Invoke(i / (double)files.Length);
        }
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
        return files.Length + whole;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StoragePropertyQuery { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

    [StructLayout(LayoutKind.Sequential)]
    struct SeekPenalty { public uint Version; public uint Size; public byte IncursSeekPenalty; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize, out SeekPenalty output, int outputSize, out int returned, IntPtr overlapped);
}
