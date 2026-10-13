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
    /// Where to build output meant for <paramref name="outDir"/>: <paramref name="outDir"/> itself unless it is on a hard
    /// disk and a solid-state drive has <paramref name="needBytes"/> free beyond its <see cref="Reserve"/>; then a folder on that drive:
    /// in the user's own temp folder when it is there, else at the drive's root. Null for "build in place".
    /// </summary>
    public static string? FastStaging(string outDir, long needBytes, string name)
    {
        string outRoot = Path.GetPathRoot(Path.GetFullPath(outDir)) ?? "";
        if (Seeks(outRoot) != true) return null;
        string userTemp = Path.GetFullPath(Path.GetTempPath());
        string userRoot = Path.GetPathRoot(userTemp) ?? "";
        var best = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name, outRoot, StringComparison.OrdinalIgnoreCase))
            .Where(d => Seeks(d.Name) == false && d.AvailableFreeSpace >= needBytes + Reserve(d.Name))
            // The user's own drive first (their temp folder), then the one with the most room.
            .OrderByDescending(d => string.Equals(d.Name, userRoot, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();
        if (best is null) return null;
        return string.Equals(best.Name, userRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(userTemp, name)
            : Path.Combine(best.Name, name);
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
        var places = new List<string> { Path.Combine(Path.GetFullPath(Path.GetTempPath()), name) };
        places.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && Seeks(d.Name) == false).Select(d => Path.Combine(d.Name, name)));
        return places.FirstOrDefault(p => Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any());
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
        return files.Length;
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
