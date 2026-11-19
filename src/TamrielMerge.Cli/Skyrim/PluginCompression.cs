using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The converted plugins made smaller as the conversion ends, everything in them kept (user, 2026-11-02: "is there a way to
/// compress the esm's so the files are smaller but they hold everything still"; "can it be ultra compressed"; "update the
/// tool to make esms like that"; "as its converting"). Two kinds:
/// <list type="bullet">
/// <item>In the file, as Skyrim reads it (<see cref="CompressRecords"/>): records stored zlib-compressed (flag 0x40000,
/// the data's size first), the types Skyrim's own plugins store so (<see cref="Types"/>, measured 2026-11-02 over
/// Skyrim.esm, Update.esm and the three DLC: NAVM, LAND, CELL and NPC_, nothing else), where that makes them smaller.
/// Oblivion.esm's 617 MB of navmeshes were stored plain (the plugin merger compresses only what it joins): about
/// 358 MB compressed.</item>
/// <item>On the disk (<see cref="CompressOnDisk"/>): Windows' own file compression (NTFS, LZX, as compact /exe:lzx does),
/// which every program reads through unchanged, Skyrim and Mutagen included. Measured on copies 2026-11-02: Daggerfall
/// Towns.esm 8.0 to 1, Daggerfall Foliage.esm 4.9 to 1, a navmesh plugin already zlib-compressed 1.1 to 1. A file written
/// again is stored plain again, so it is done after the last write: the output's plugins once the build is in place,
/// Skyrim's Data's after installing.</item>
/// </list>
/// </summary>
public static class PluginCompression
{
    /// <summary>The record types Skyrim's own plugins store compressed (measured, see the class).</summary>
    public static readonly HashSet<string> Types = ["NAVM", "LAND", "CELL", "NPC_"];

    const int HeaderSize = 24;
    const uint CompressedFlag = 0x00040000;
    /// <summary>Records this small aren't worth it (zlib's own header and the size field eat the gain).</summary>
    const int Smallest = 256;

    /// <summary>What compressing a plugin did.</summary>
    public sealed record Result(long Before, long After, int Records);

    /// <summary>
    /// <paramref name="path"/> rewritten with its records of <see cref="Types"/> compressed where that makes them smaller,
    /// everything else byte for byte as it was; checked against the original record by record (inflated where compressed)
    /// before it takes the original's place. Read and written in passes, a few hundred megabytes held at most.
    /// </summary>
    public static Result CompressRecords(string path)
    {
        string tmp = path + ".zlib.tmp";
        long before = new FileInfo(path).Length;
        int compressed = 0;
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                // The plugin's header record as it is.
                var head = new byte[HeaderSize];
                input.ReadExactly(head);
                var tes4 = new byte[HeaderSize + BitConverter.ToUInt32(head, 4)];
                head.CopyTo(tes4, 0);
                input.ReadExactly(tes4, HeaderSize, tes4.Length - HeaderSize);
                output.Write(tes4);
                var inEnds = new Stack<long>();       // where each open group ends in the input
                var outStarts = new Stack<long>();    // where its header is in the output (its size is set when it ends)
                var batch = new List<Token>();
                long batchBytes = 0;
                void Flush()
                {
                    Parallel.ForEach(batch.Where(t => t.Kind == TokenKind.Record && Eligible(t.Bytes!)), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) },
                        t => { if (Deflate(t.Bytes!) is { } z) { t.Bytes = z; Interlocked.Increment(ref compressed); } });
                    foreach (var t in batch)
                        switch (t.Kind)
                        {
                            case TokenKind.GroupStart:
                                outStarts.Push(output.Position);
                                output.Write(t.Bytes!);
                                break;
                            case TokenKind.GroupEnd:
                                long start = outStarts.Pop(), end = output.Position;
                                output.Position = start + 4;
                                output.Write(BitConverter.GetBytes((uint)(end - start)));
                                output.Position = end;
                                break;
                            default:
                                output.Write(t.Bytes!);
                                break;
                        }
                    batch.Clear();
                    batchBytes = 0;
                }
                var h = new byte[HeaderSize];
                while (input.Position < input.Length)
                {
                    input.ReadExactly(h);
                    uint size = BitConverter.ToUInt32(h, 4);
                    if (h[0] == 'G' && h[1] == 'R' && h[2] == 'U' && h[3] == 'P')
                    {
                        batch.Add(new Token(TokenKind.GroupStart, (byte[])h.Clone()));
                        inEnds.Push(input.Position - HeaderSize + size);
                    }
                    else
                    {
                        var rec = new byte[HeaderSize + size];
                        h.CopyTo(rec, 0);
                        input.ReadExactly(rec, HeaderSize, (int)size);
                        batch.Add(new Token(TokenKind.Record, rec));
                        batchBytes += rec.Length;
                    }
                    while (inEnds.Count > 0 && inEnds.Peek() == input.Position) { inEnds.Pop(); batch.Add(new Token(TokenKind.GroupEnd, null)); }
                    if (batchBytes >= 256L << 20) Flush();
                }
                Flush();
                if (inEnds.Count > 0) throw new InvalidDataException($"{Path.GetFileName(path)}: a group runs past the end of the file");
            }
            Verify(path, tmp);
            File.Move(tmp, path, overwrite: true);
            return new Result(before, new FileInfo(path).Length, compressed);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    enum TokenKind { GroupStart, Record, GroupEnd }
    sealed class Token(TokenKind kind, byte[]? bytes) { public TokenKind Kind = kind; public byte[]? Bytes = bytes; }

    static bool Eligible(byte[] rec) =>
        rec.Length > HeaderSize + Smallest && (BitConverter.ToUInt32(rec, 8) & CompressedFlag) == 0 && Types.Contains(Encoding.ASCII.GetString(rec, 0, 4));

    /// <summary>The record compressed (header, data size, zlib), or null when that isn't smaller.</summary>
    static byte[]? Deflate(byte[] rec)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rec, HeaderSize, rec.Length - HeaderSize);
        if (ms.Length + 4 >= rec.Length - HeaderSize) return null;
        var packed = new byte[HeaderSize + 4 + ms.Length];
        Buffer.BlockCopy(rec, 0, packed, 0, HeaderSize);
        BitConverter.TryWriteBytes(packed.AsSpan(4, 4), (uint)(4 + ms.Length));
        BitConverter.TryWriteBytes(packed.AsSpan(8, 4), BitConverter.ToUInt32(rec, 8) | CompressedFlag);
        BitConverter.TryWriteBytes(packed.AsSpan(HeaderSize, 4), (uint)(rec.Length - HeaderSize));
        ms.Position = 0;
        ms.ReadExactly(packed, HeaderSize + 4, (int)ms.Length);
        return packed;
    }

    /// <summary>
    /// The compressed file against the original, in step: the same groups (header but the size) and the same records in
    /// the same order, each byte for byte, or compressed here with its header the same but the size and flag and its data
    /// inflating to the original's. Throws at the first difference.
    /// </summary>
    static void Verify(string original, string packed)
    {
        using var a = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var b = new FileStream(packed, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var ha = new byte[HeaderSize];
        var hb = new byte[HeaderSize];
        long n = 0;
        // The header record, then everything after it.
        bool first = true;
        while (a.Position < a.Length)
        {
            if (b.Position >= b.Length) throw new InvalidDataException("the compressed plugin ends early");
            a.ReadExactly(ha); b.ReadExactly(hb);
            n++;
            bool group = !first && ha[0] == 'G' && ha[1] == 'R' && ha[2] == 'U' && ha[3] == 'P';
            first = false;
            if (group)
            {
                if (!ha.AsSpan(0, 4).SequenceEqual(hb.AsSpan(0, 4)) || !ha.AsSpan(8).SequenceEqual(hb.AsSpan(8))) throw new InvalidDataException($"group {n} differs");
                continue;
            }
            uint la = BitConverter.ToUInt32(ha, 4), lb = BitConverter.ToUInt32(hb, 4);
            uint fa = BitConverter.ToUInt32(ha, 8), fb = BitConverter.ToUInt32(hb, 8);
            var da = new byte[la];
            a.ReadExactly(da);
            var db = new byte[lb];
            b.ReadExactly(db);
            bool sameRest = ha.AsSpan(0, 4).SequenceEqual(hb.AsSpan(0, 4)) && ha.AsSpan(12).SequenceEqual(hb.AsSpan(12));
            if (!sameRest) throw new InvalidDataException($"record {n} ({Encoding.ASCII.GetString(ha, 0, 4)}) has another header");
            if (fa == fb)
            {
                if (!da.AsSpan().SequenceEqual(db)) throw new InvalidDataException($"record {n} ({Encoding.ASCII.GetString(ha, 0, 4)}) differs");
                continue;
            }
            if ((fa | CompressedFlag) != fb || (fa & CompressedFlag) != 0) throw new InvalidDataException($"record {n} has other flags");
            uint plain = BitConverter.ToUInt32(db, 0);
            if (plain != la) throw new InvalidDataException($"record {n}: compressed size field {plain}, not {la}");
            var inflated = new byte[plain];
            using (var z = new ZLibStream(new MemoryStream(db, 4, db.Length - 4), CompressionMode.Decompress))
                z.ReadExactly(inflated);
            if (!inflated.AsSpan().SequenceEqual(da)) throw new InvalidDataException($"record {n} ({Encoding.ASCII.GetString(ha, 0, 4)}) doesn't inflate to the original");
        }
        if (b.Position != b.Length) throw new InvalidDataException("the compressed plugin has more after the original's end");
    }

    /// <summary>Every plugin of <paramref name="names"/> in <paramref name="dir"/> compressed (<see cref="CompressRecords"/>);
    /// one that can't be is left as it was and said.</summary>
    public static void CompressAll(string dir, IEnumerable<string> names, Action<string> log)
    {
        long before = 0, after = 0;
        int records = 0;
        foreach (var name in names)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var r = CompressRecords(path);
                before += r.Before; after += r.After; records += r.Records;
                if (r.Records > 0) log($"  {name}: {Mb(r.Before)} -> {Mb(r.After)} ({r.Records:N0} records compressed, {clock.Elapsed.TotalSeconds:F0} s)");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log($"  {name}: left as it was ({e.Message})");
            }
        }
        log($"  plugins {Mb(before)} -> {Mb(after)}, {records:N0} records compressed (navmeshes, land, cells and people, as Skyrim's own plugins store them)");
    }

    static string Mb(long bytes) => $"{bytes / 1048576.0:N0} MB";

    /// <summary>
    /// The files compressed on the disk with Windows' LZX file compression (compact /exe:lzx; NTFS only, every program
    /// reads them as before), and what they take on the disk now against their size. Nothing on another file system.
    /// </summary>
    public static void CompressOnDisk(IReadOnlyList<string> files, Action<string> log)
    {
        var present = files.Where(File.Exists).ToList();
        if (present.Count == 0) return;
        string? root = Path.GetPathRoot(Path.GetFullPath(present[0]));
        try
        {
            if (root is null || !new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                log($"  on-disk compression: {root} isn't NTFS, files left as they are");
                return;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException) { log($"  on-disk compression: {root} not looked at ({e.Message})"); return; }
        long size = 0, onDisk = 0;
        int failed = 0, skipped = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // A few at once: compact reads and writes each file whole.
        Parallel.ForEach(present, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) }, file =>
        {
            // Only what shrinks: the land and navmesh plugins' records are compressed already, and LZX made them 1.0 to
            // 1.1 to 1 after reading and writing each whole (2026-11-04: most of the 8.5 minutes compression took).
            if (!Shrinks(file)) { Interlocked.Increment(ref skipped); return; }
            var psi = new System.Diagnostics.ProcessStartInfo("compact.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "/exe:lzx", file }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) Interlocked.Increment(ref failed);
            Interlocked.Add(ref size, new FileInfo(file).Length);
            Interlocked.Add(ref onDisk, SizeOnDisk(file));
        });
        log($"  on-disk compression (LZX): {present.Count - skipped} files, {Mb(size)} in {Mb(onDisk)} on the disk ({(onDisk > 0 ? size / (double)onDisk : 0):F1} to 1, {clock.Elapsed.TotalSeconds:F0} s){(skipped > 0 ? $"; {skipped} that wouldn't shrink left as they are" : "")}{(failed > 0 ? $"; {failed} not compressed" : "")}");
    }

    /// <summary>Whether a file shrinks by 15% or more: eight 1 MB samples through it, deflated (the whole of a smaller one).</summary>
    static bool Shrinks(string file)
    {
        using var fs = File.OpenRead(file);
        long length = fs.Length;
        if (length == 0) return false;
        int samples = length <= 8L << 20 ? 1 : 8, chunk = (int)Math.Min(length, 1 << 20);
        var buffer = new byte[chunk];
        long raw = 0, packed = 0;
        for (int k = 0; k < samples; k++)
        {
            fs.Position = samples == 1 ? 0 : (length - chunk) * k / (samples - 1);
            int n = fs.Read(buffer, 0, chunk);
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) z.Write(buffer, 0, n);
            raw += n; packed += ms.Length;
        }
        return packed <= raw / 1.15;
    }

    /// <summary>What a file takes on the disk (compressed or sparse, less than its size).</summary>
    static long SizeOnDisk(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        return low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0 ? new FileInfo(path).Length : ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
