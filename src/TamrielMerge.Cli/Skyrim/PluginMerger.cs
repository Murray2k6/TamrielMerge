using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// One plugin from parts built separately (one process each, so a part's memory is given back before the next): every
/// part is the same plugin (same ModKey and masters list, <see cref="PreparePart"/>) with its own range of FormIDs,
/// so the parts' records go into one file as they are, byte for byte, without loading them. Groups are merged by type
/// and label (a cell's children, a worldspace's blocks); a record in more than one part (the overland tiles'
/// overrides, their persistent cells) is kept once and its children merged. Keeps the converted Daggerfall to a few
/// full plugins rather than one per batch.
/// </summary>
public static class PluginMerger
{
    /// <summary>The masters every part lists, in this order (Skyrim's and its DLCs, then <paramref name="extra"/>).</summary>
    public static ModKey[] Masters(params string[] extra) =>
        new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Concat(extra).Select(f => ModKey.FromFileName(f)).ToArray();

    /// <summary>First FormID of part <paramref name="part"/> (1-based) of <paramref name="parts"/>: the 24-bit space split evenly.</summary>
    public static uint PartStart(int part, int parts) => 0x800 + (uint)((part - 1) * Stride(parts));
    public static uint Stride(int parts) => (0xFFFFFFu - 0x800) / (uint)Math.Max(parts, 1);

    /// <summary>Makes <paramref name="mod"/> part <paramref name="part"/> of <paramref name="parts"/>: its FormIDs start in its range.</summary>
    public static void PreparePart(SkyrimMod mod, int part, int parts) => mod.ModHeader.Stats.NextFormID = PartStart(part, parts);

    /// <summary>Writes a part with exactly <paramref name="masters"/> as its masters (so every part's FormIDs mean the same).</summary>
    public static void WritePart(SkyrimMod mod, string path, ModKey[] masters, int part, int parts)
    {
        uint end = PartStart(part, parts) + Stride(parts);
        if (mod.ModHeader.Stats.NextFormID >= end)
            throw new InvalidOperationException($"part {part} of {parts} ran out of FormIDs ({mod.ModHeader.Stats.NextFormID:X6} >= {end:X6}): use more parts");
        mod.ModHeader.MasterReferences.Clear();
        foreach (var m in masters) mod.ModHeader.MasterReferences.Add(new MasterReference { Master = m });
        TopicSubtypes.Fill(mod);
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            MastersListContent = MastersListContentOption.NoCheck,
            ModKey = ModKeyOption.NoCheck,
            MastersListOrdering = new MastersListOrderingByLoadOrder(masters),
        });
    }

    abstract class Node { }
    sealed class Rec(int file, long offset, long length, uint formId) : Node
    {
        public int File = file; public long Offset = offset; public long Length = length; public uint FormId = formId;
        /// <summary>The record as written when it is compressed here (header and zlib data), else null.</summary>
        public byte[]? Packed;
    }

    /// <summary>
    /// Record types written compressed, as Skyrim's own plugins store them (zlib, flag 0x40000, the data's size first):
    /// navmeshes and land. The joined Daggerfall Towns.esm reached 2,065 MB on 2026-10-13, 1,176 MB of it navmeshes,
    /// past the 2 GB that Mutagen (and Skyrim's plugin reading, safely) can take.
    /// </summary>
    static readonly HashSet<string> Compressed = ["NAVM", "LAND"];
    const uint CompressedFlag = 0x00040000;
    sealed class Grp(byte[] header, int type, uint label) : Node
    {
        public byte[] Header = header; public int Type = type; public uint Label = label;
        public List<Node> Children = [];
        public long Size;
    }

    const int HeaderSize = 24;

    /// <summary>Merges <paramref name="parts"/> (in order) into <paramref name="outPath"/>; returns the records written.</summary>
    public static long Merge(IReadOnlyList<string> parts, string outPath, bool master = true)
    {
        if (parts.Count == 0) throw new ArgumentException("no parts");
        var streams = parts.Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)).ToList();
        try
        {
            List<Node>? top = null;
            byte[]? tes4 = null;
            List<byte[]>? mastersOf0 = null;
            uint nextId = 0;
            var onam = new SortedSet<uint>();
            for (int f = 0; f < streams.Count; f++)
            {
                var s = streams[f];
                var (header, subs) = ReadTes4(s);
                var masters = subs.Where(x => x.Type == "MAST").Select(x => x.Data).ToList();
                if (mastersOf0 is null) { mastersOf0 = masters; tes4 = header; }
                else if (masters.Count != mastersOf0.Count || masters.Zip(mastersOf0).Any(p => !p.First.AsSpan().SequenceEqual(p.Second)))
                    throw new InvalidDataException($"{Path.GetFileName(parts[f])} has different masters from {Path.GetFileName(parts[0])}");
                if (subs.FirstOrDefault(x => x.Type == "HEDR") is { } hedr) nextId = Math.Max(nextId, BitConverter.ToUInt32(hedr.Data, 8));
                foreach (var o in subs.Where(x => x.Type == "ONAM"))
                    for (int i = 0; i + 4 <= o.Data.Length; i += 4) onam.Add(BitConverter.ToUInt32(o.Data, i));
                var nodes = ReadNodes(s, f, s.Position, s.Length);
                if (top is null) top = nodes; else MergeInto(top, nodes);
            }
            Pack(top!, streams);
            foreach (var n in top!) Measure(n);
            long count = top.Sum(Count);

            var firstSubs = ReadTes4(Reset(streams[0])).Subs;
            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            WriteTes4(output, tes4!, firstSubs, count, nextId, onam, master);
            var buffer = new byte[1 << 20];
            foreach (var n in top) Write(output, n, streams, buffer);
            return count;
        }
        finally { foreach (var s in streams) s.Dispose(); }
    }

    /// <summary>
    /// A copy of a plugin without its top-level groups of the given record types (e.g. DIAL, QUST), to find which kind
    /// of record the game fails on. References to the dropped records are left dangling.
    /// </summary>
    public static long Strip(string plugin, string outPath, IReadOnlySet<string> dropTypes)
    {
        using var s = new FileStream(plugin, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (tes4, subs) = ReadTes4(s);
        var nodes = ReadNodes(s, 0, s.Position, s.Length)
            .Where(n => n is not Grp { Type: 0 } g || !dropTypes.Contains(Encoding.ASCII.GetString(BitConverter.GetBytes(g.Label)))).ToList();
        foreach (var n in nodes) Measure(n);
        long count = nodes.Sum(Count);
        uint nextId = subs.FirstOrDefault(x => x.Type == "HEDR") is { } h ? BitConverter.ToUInt32(h.Data, 8) : 0;
        using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteTes4(output, tes4, subs, count, nextId, new SortedSet<uint>(), (BitConverter.ToUInt32(tes4, 8) & 1) != 0);
        var buffer = new byte[1 << 20];
        foreach (var n in nodes) Write(output, n, [s], buffer);
        return count;
    }

    static FileStream Reset(FileStream s) { s.Position = 0; return s; }

    sealed record Sub(string Type, byte[] Data);

    static (byte[] Header, List<Sub> Subs) ReadTes4(FileStream s)
    {
        s.Position = 0;
        var header = new byte[HeaderSize];
        s.ReadExactly(header);
        if (Encoding.ASCII.GetString(header, 0, 4) != "TES4") throw new InvalidDataException("not a Skyrim plugin");
        int size = BitConverter.ToInt32(header, 4);
        var data = new byte[size];
        s.ReadExactly(data);
        var subs = new List<Sub>();
        for (int p = 0; p < size;)
        {
            string type = Encoding.ASCII.GetString(data, p, 4);
            int len = BitConverter.ToUInt16(data, p + 4);
            if (type == "XXXX") { int big = BitConverter.ToInt32(data, p + 6); p += 10; type = Encoding.ASCII.GetString(data, p, 4); subs.Add(new(type, data[(p + 6)..(p + 6 + big)])); p += 6 + big; continue; }
            subs.Add(new(type, data[(p + 6)..(p + 6 + len)]));
            p += 6 + len;
        }
        return (header, subs);
    }

    static List<Node> ReadNodes(FileStream s, int file, long start, long end)
    {
        var nodes = new List<Node>();
        var h = new byte[HeaderSize];
        long p = start;
        while (p < end)
        {
            s.Position = p;
            s.ReadExactly(h);
            uint size = BitConverter.ToUInt32(h, 4);
            if (h[0] == 'G' && h[1] == 'R' && h[2] == 'U' && h[3] == 'P')
            {
                var g = new Grp((byte[])h.Clone(), BitConverter.ToInt32(h, 12), BitConverter.ToUInt32(h, 8));
                g.Children = ReadNodes(s, file, p + HeaderSize, p + size);
                nodes.Add(g);
                p += size;
            }
            else
            {
                nodes.Add(new Rec(file, p, HeaderSize + size, BitConverter.ToUInt32(h, 12)));
                p += HeaderSize + size;
            }
        }
        return nodes;
    }

    /// <summary>Groups that belong right after a record: a worldspace's (1), a cell's (6) and a topic's (7) children.</summary>
    static bool IsChildGroup(int type) => type is 1 or 6 or 7;

    static void MergeInto(List<Node> into, List<Node> from)
    {
        var records = new HashSet<uint>();
        var groups = new Dictionary<(int, uint), Grp>();
        foreach (var n in into)
            if (n is Rec r) records.Add(r.FormId);
            else if (n is Grp g) groups.TryAdd((g.Type, g.Label), g);
        foreach (var n in from)
        {
            if (n is Rec r)
            {
                if (records.Add(r.FormId)) into.Add(r);
            }
            else if (n is Grp g)
            {
                if (groups.TryGetValue((g.Type, g.Label), out var same)) { MergeInto(same.Children, g.Children); continue; }
                groups[(g.Type, g.Label)] = g;
                // A record's children group goes right after the record (which may have come from an earlier part).
                int at = IsChildGroup(g.Type) ? into.FindIndex(x => x is Rec rr && rr.FormId == g.Label) : -1;
                if (at >= 0) into.Insert(at + 1, g); else into.Add(g);
            }
        }
    }

    /// <summary>Compresses the records of <see cref="Compressed"/> types not compressed yet, where that makes them smaller
    /// (read in file order, compressed on all but two cores).</summary>
    static void Pack(List<Node> top, List<FileStream> streams)
    {
        var recs = new List<Rec>();
        void Collect(Node n) { if (n is Rec r) recs.Add(r); else foreach (var c in ((Grp)n).Children) Collect(c); }
        foreach (var n in top) Collect(n);
        var h = new byte[HeaderSize];
        const int Batch = 4096;
        var batch = new List<(Rec R, byte[] Bytes)>(Batch);
        void Flush()
        {
            Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, item =>
            {
                var b = item.Bytes;
                using var ms = new MemoryStream();
                using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    z.Write(b, HeaderSize, b.Length - HeaderSize);
                if (ms.Length + 4 >= b.Length - HeaderSize) return;
                var packed = new byte[HeaderSize + 4 + ms.Length];
                Buffer.BlockCopy(b, 0, packed, 0, HeaderSize);
                BitConverter.TryWriteBytes(packed.AsSpan(4, 4), (uint)(4 + ms.Length));
                BitConverter.TryWriteBytes(packed.AsSpan(8, 4), BitConverter.ToUInt32(b, 8) | CompressedFlag);
                BitConverter.TryWriteBytes(packed.AsSpan(HeaderSize, 4), (uint)(b.Length - HeaderSize));
                ms.Position = 0;
                ms.ReadExactly(packed, HeaderSize + 4, (int)ms.Length);
                item.R.Packed = packed;
            });
            batch.Clear();
        }
        foreach (var r in recs.OrderBy(r => r.File).ThenBy(r => r.Offset))
        {
            if (r.Length <= HeaderSize + 256) continue;
            var s = streams[r.File];
            s.Position = r.Offset;
            s.ReadExactly(h);
            if (!Compressed.Contains(Encoding.ASCII.GetString(h, 0, 4)) || (BitConverter.ToUInt32(h, 8) & CompressedFlag) != 0) continue;
            var bytes = new byte[r.Length];
            Buffer.BlockCopy(h, 0, bytes, 0, HeaderSize);
            s.ReadExactly(bytes, HeaderSize, (int)r.Length - HeaderSize);
            batch.Add((r, bytes));
            if (batch.Count == Batch) Flush();
        }
        Flush();
    }

    static long Measure(Node n)
    {
        if (n is Rec r) return r.Packed?.Length ?? r.Length;
        var g = (Grp)n;
        g.Size = HeaderSize + g.Children.Sum(Measure);
        if (g.Size > uint.MaxValue) throw new InvalidDataException("group over 4 GB");
        return g.Size;
    }

    static long Count(Node n) => n is Grp g ? 1 + g.Children.Sum(Count) : 1;

    static void Write(FileStream output, Node n, List<FileStream> streams, byte[] buffer)
    {
        if (n is Rec r)
        {
            if (r.Packed is { } packed) { output.Write(packed); return; }
            var s = streams[r.File];
            s.Position = r.Offset;
            for (long left = r.Length; left > 0;)
            {
                int chunk = (int)Math.Min(left, buffer.Length);
                s.ReadExactly(buffer, 0, chunk);
                output.Write(buffer, 0, chunk);
                left -= chunk;
            }
            return;
        }
        var g = (Grp)n;
        BitConverter.TryWriteBytes(g.Header.AsSpan(4, 4), (uint)g.Size);
        output.Write(g.Header);
        foreach (var c in g.Children) Write(output, c, streams, buffer);
    }

    static void WriteTes4(FileStream output, byte[] header, List<Sub> subs, long count, uint nextId, SortedSet<uint> onam, bool master)
    {
        using var data = new MemoryStream();
        void Put(string type, byte[] d)
        {
            if (d.Length > ushort.MaxValue)
            {
                data.Write(Encoding.ASCII.GetBytes("XXXX")); data.Write(BitConverter.GetBytes((ushort)4)); data.Write(BitConverter.GetBytes(d.Length));
                data.Write(Encoding.ASCII.GetBytes(type)); data.Write(BitConverter.GetBytes((ushort)0));
            }
            else { data.Write(Encoding.ASCII.GetBytes(type)); data.Write(BitConverter.GetBytes((ushort)d.Length)); }
            data.Write(d);
        }
        // The overridden-forms list (every part's) goes after the masters (or the header when there are none).
        int onamAfter = subs.FindLastIndex(x => x.Type == "DATA");
        if (onamAfter < 0) onamAfter = subs.FindIndex(x => x.Type == "HEDR");
        for (int i = 0; i < subs.Count; i++)
        {
            var sub = subs[i];
            if (sub.Type == "HEDR")
            {
                var h = (byte[])sub.Data.Clone();
                BitConverter.TryWriteBytes(h.AsSpan(4, 4), (uint)count);
                BitConverter.TryWriteBytes(h.AsSpan(8, 4), nextId);
                Put("HEDR", h);
            }
            else if (sub.Type != "ONAM") Put(sub.Type, sub.Data);
            if (i == onamAfter && onam.Count > 0) Put("ONAM", onam.SelectMany(BitConverter.GetBytes).ToArray());
        }
        var h2 = (byte[])header.Clone();
        BitConverter.TryWriteBytes(h2.AsSpan(4, 4), (uint)data.Length);
        uint flags = BitConverter.ToUInt32(h2, 8);
        flags = master ? flags | 1u : flags & ~1u;
        BitConverter.TryWriteBytes(h2.AsSpan(8, 4), flags);
        output.Write(h2);
        data.Position = 0;
        data.CopyTo(output);
    }
}
