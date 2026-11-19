using System.IO.Compression;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Joining finished plugins (<see cref="PluginPack"/>): unlike the parts of <see cref="Merge"/> they have different
/// masters and FormIDs, so every FormID is translated. The tree of groups is read without the records' data, as for
/// parts, the FormIDs in record headers and group labels changed there, and later plugins laid over earlier ones (the
/// later copy of a record wins, as it does in the game; a cell's children are joined, as the game joins them; the navmesh
/// map NAVI is joined entry by entry, as the game joins every plugin's). The FormIDs inside a record are found by Mutagen,
/// one record at a time, so a 1.9 GB plugin never sits in memory as objects: its bytes to a record object, the links
/// remapped, the object to bytes. A plugin keeping its name, FormIDs and masters is copied byte for byte.
/// </summary>
public static partial class PluginMerger
{
    /// <summary>A set of 24-bit FormIDs.</summary>
    public sealed class Bits
    {
        readonly ulong[] words = new ulong[1 << 18];
        public void Set(uint id) => words[(id & 0xFFFFFF) >> 6] |= 1UL << (int)(id & 63);
        public bool Get(uint id) => (words[(id & 0xFFFFFF) >> 6] & (1UL << (int)(id & 63))) != 0;
    }

    /// <summary>A plugin as its file holds it, from its record headers alone (no record is read or inflated).</summary>
    public sealed class PluginScan
    {
        public required string Path { get; init; }
        public required ModKey Key { get; init; }
        public required long Size { get; init; }
        public required uint Flags { get; init; }
        public required List<ModKey> Masters { get; init; }
        public string Name => Key.FileName.String;
        /// <summary>Records of each type, in all, and how many of them are new here (an override isn't).</summary>
        public SortedDictionary<string, long> Types { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, long> OwnTypes { get; } = new(StringComparer.Ordinal);
        public long Own { get; set; }
        /// <summary>Bytes of navmesh and land not compressed yet.</summary>
        public long Uncompressed { get; set; }
        public Bits OwnIds { get; } = new();
        public Bits Navmeshes { get; } = new();
        /// <summary>The FormIDs (as this file writes them) of its overrides, NAVI left out: the game joins those.</summary>
        public List<uint> Overrides { get; } = [];
        public List<long> OverrideOffsets { get; } = [];
    }

    /// <summary>A plugin's masters, from its header.</summary>
    public static List<ModKey> MastersOf(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return ReadTes4(s).Subs.Where(x => x.Type == "MAST").Select(x => ModKey.FromFileName(Name(x.Data))).ToList();
    }

    static string Name(byte[] mast) => Encoding.Latin1.GetString(mast).TrimEnd('\0');

    /// <summary>Walks a plugin's record headers.</summary>
    public static PluginScan Scan(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var (tes4, subs) = ReadTes4(s);
        var masters = subs.Where(x => x.Type == "MAST").Select(x => ModKey.FromFileName(Name(x.Data))).ToList();
        var scan = new PluginScan
        {
            Path = path, Key = ModKey.FromFileName(System.IO.Path.GetFileName(path)), Size = s.Length, Flags = BitConverter.ToUInt32(tes4, 8), Masters = masters,
        };
        var h = new byte[HeaderSize];
        var names = new Dictionary<int, string>();
        int navi = Mag("NAVI"), navm = Mag("NAVM"), land = Mag("LAND");
        long p = s.Position, end = s.Length;
        while (p < end)
        {
            s.Position = p;
            s.ReadExactly(h);
            uint size = BitConverter.ToUInt32(h, 4);
            int type = BitConverter.ToInt32(h, 0);
            if (type == Grup) { p += HeaderSize; continue; }
            if (!names.TryGetValue(type, out var name)) names[type] = name = Encoding.ASCII.GetString(h, 0, 4);
            scan.Types[name] = scan.Types.GetValueOrDefault(name) + 1;
            uint id = BitConverter.ToUInt32(h, 12);
            if ((type == navm || type == land) && (BitConverter.ToUInt32(h, 8) & CompressedFlag) == 0) scan.Uncompressed += size;
            if (id >> 24 >= masters.Count)
            {
                scan.Own++;
                scan.OwnTypes[name] = scan.OwnTypes.GetValueOrDefault(name) + 1;
                scan.OwnIds.Set(id);
                if (type == navm) scan.Navmeshes.Set(id);
            }
            else if (type != navi) { scan.Overrides.Add(id); scan.OverrideOffsets.Add(p); }
            p += HeaderSize + size;
        }
        return scan;
    }

    /// <summary>Where the plugin's own records of <paramref name="wanted"/> stand in its file.</summary>
    public static Dictionary<uint, long> OwnOffsets(PluginScan plugin, Bits wanted)
    {
        var found = new Dictionary<uint, long>();
        using var s = new FileStream(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        ReadTes4(s);
        var h = new byte[HeaderSize];
        long p = s.Position, end = s.Length;
        while (p < end)
        {
            s.Position = p;
            s.ReadExactly(h);
            if (BitConverter.ToInt32(h, 0) == Grup) { p += HeaderSize; continue; }
            uint id = BitConverter.ToUInt32(h, 12);
            if (id >> 24 >= plugin.Masters.Count && wanted.Get(id)) found[id & 0xFFFFFF] = p;
            p += HeaderSize + BitConverter.ToUInt32(h, 4);
        }
        return found;
    }

    /// <summary>
    /// Whether two plugins' copies of a cell or worldspace say the same (FormLinks compare by FormKey, so their master lists
    /// don't matter). Each plugin with something in a cell carries a copy of it, and an override of the worldspace, the
    /// same as the rest but for the odd one: an equal copy can't change what wins, and counted as contested those few
    /// records alone stopped every join across the worldspace (measured on the 10-25 output: the worldspace and its persistent
    /// cell, two records of 15 million, kept Daggerfall Dungeons.esm from Daggerfall Land.esm).
    /// </summary>
    public sealed class CopyComparer : IDisposable
    {
        readonly List<PluginScan> plugins;
        readonly Microsoft.Win32.SafeHandles.SafeFileHandle[] handles;
        readonly IReadOnlySeparatedMasterPackage[] packages;
        readonly ThreadLocal<ParsingMeta[]> metas;
        static readonly int CellType = Mag("CELL"), WorldType = Mag("WRLD");

        public CopyComparer(List<PluginScan> plugins)
        {
            this.plugins = plugins;
            handles = plugins.Select(p => File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read)).ToArray();
            packages = plugins.Select(p => PackageOf(p.Key, p.Masters)).ToArray();
            metas = new ThreadLocal<ParsingMeta[]>(() => this.plugins.Select((p, i) => new ParsingMeta(GameConstants.SkyrimSE, p.Key, packages[i])).ToArray());
        }

        byte[] Read(int plugin, long offset)
        {
            var h = new byte[HeaderSize];
            RandomAccess.Read(handles[plugin], h, offset);
            var raw = new byte[HeaderSize + BitConverter.ToUInt32(h, 4)];
            RandomAccess.Read(handles[plugin], raw, offset);
            return (BitConverter.ToUInt32(raw, 8) & CompressedFlag) != 0 ? Inflate(raw) : raw;
        }

        /// <summary>The record at an offset, when it is a cell or a worldspace; else null.</summary>
        public IMajorRecord? Load(int plugin, long offset)
        {
            var bytes = Read(plugin, offset);
            int type = BitConverter.ToInt32(bytes, 0);
            return type == CellType || type == WorldType ? Parse(bytes, metas.Value![plugin], false) : null;
        }

        public void Dispose()
        {
            metas.Dispose();
            foreach (var h in handles) h.Dispose();
        }
    }

    static readonly int Grup = Mag("GRUP");
    static int Mag(string type) => BitConverter.ToInt32(Encoding.ASCII.GetBytes(type));

    /// <summary>Which FormKeys move: a plugin folded into another, its records keeping their IDs or taking new ones.</summary>
    public sealed class FormMap : IReadOnlyDictionary<FormKey, FormKey>
    {
        readonly Dictionary<ModKey, (ModKey To, uint[]? Ids)> moved = [];

        /// <param name="ids">For each 24-bit ID its new one, 0 for the same; null: none change.</param>
        public void Move(ModKey from, ModKey to, uint[]? ids) => moved[from] = (to, ids);
        public bool Moves(ModKey key) => moved.ContainsKey(key);
        public ModKey Target(ModKey key) => moved.TryGetValue(key, out var m) ? m.To : key;
        public uint[]? Ids(ModKey key) => moved.TryGetValue(key, out var m) ? m.Ids : null;
        public FormKey Map(FormKey k) => TryGetValue(k, out var to) ? to : k;

        public bool TryGetValue(FormKey key, out FormKey value)
        {
            if (!key.IsNull && moved.TryGetValue(key.ModKey, out var m))
            {
                uint id = m.Ids is { } ids && ids[key.ID & 0xFFFFFF] is var n && n != 0 ? n : key.ID;
                value = new FormKey(m.To, id);
                return true;
            }
            value = key;
            return false;
        }

        // Mutagen's RemapLinks asks only TryGetValue; the rest is what the interface needs.
        public FormKey this[FormKey key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
        public bool ContainsKey(FormKey key) => !key.IsNull && moved.ContainsKey(key.ModKey);
        public int Count => moved.Count;
        public IEnumerable<FormKey> Keys => throw new NotSupportedException();
        public IEnumerable<FormKey> Values => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<FormKey, FormKey>> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException();
    }

    /// <summary>Every FormID of a set of plugins: a link into a plugin not in the set (Skyrim's own) isn't judged.</summary>
    public sealed class Known
    {
        readonly Dictionary<ModKey, (Bits All, Bits Navmeshes)> sets = [];
        public void Add(ModKey key, Bits all, Bits navmeshes) => sets[key] = (all, navmeshes);
        public bool Exists(FormKey k) => !sets.TryGetValue(k.ModKey, out var s) || s.All.Get(k.ID);
        public bool IsNavmesh(FormKey k) => !sets.TryGetValue(k.ModKey, out var s) || s.Navmeshes.Get(k.ID);
    }

    /// <summary>One plugin to write: <paramref name="Sources"/> (load order) joined under the name <paramref name="Key"/>
    /// with the masters <paramref name="Masters"/>, the header of source <paramref name="HeaderFrom"/>. One source: the
    /// plugin itself, its references to plugins folded elsewhere re-pointed.</summary>
    public sealed record JoinJob(IReadOnlyList<string> Sources, int HeaderFrom, ModKey Key, IReadOnlyList<ModKey> Masters, FormMap Map, string OutPath);

    public sealed class JoinStats
    {
        public long Bytes, Rewritten, Copied, NaviEntries;
        public SortedDictionary<string, long> Dropped { get; } = new(StringComparer.Ordinal);
    }

    public sealed class JoinCheck
    {
        public long Compared, Copied, Different, Collisions, DanglingBefore, DanglingAfter, EdgeLinks, BadEdgeLinks, DoorLinks, BadDoorLinks;
        public List<string> Notes { get; } = [];
        internal void Note(string s) { lock (Notes) if (Notes.Count < 20) Notes.Add(s); }
    }

    /// <summary>One source of a join: what its FormIDs become.</summary>
    sealed class Src
    {
        public required ModKey Key;
        public required List<ModKey> Masters;
        public required byte[] Index;
        public required uint[]?[] Ids;
        public required bool Identity;
        public required IReadOnlySeparatedMasterPackage Package;
        public required byte[] Tes4;
        public required List<Sub> Subs;

        /// <summary>A FormID of this file as the output writes it.</summary>
        public uint Map(uint fid)
        {
            int i = Math.Min((int)(fid >> 24), Masters.Count);
            uint id = fid & 0xFFFFFF;
            if (Ids[i] is { } ids && ids[id] != 0) id = ids[id];
            return (uint)Index[i] << 24 | id;
        }
    }

    static IReadOnlySeparatedMasterPackage PackageOf(ModKey key, IEnumerable<ModKey> masters) =>
        SeparatedMasterPackage.Factory(GameRelease.SkyrimSE, key, MasterStyle.Full,
            new MasterReferenceCollection(key, masters.Select(m => (IMasterReferenceGetter)new MasterReference { Master = m })), null!);

    /// <summary>A join's records as a tree (no data), FormIDs already the output's.</summary>
    sealed class Tree : IDisposable
    {
        public required List<Src> Sources;
        public required List<FileStream> Streams;
        public required List<Node> Top;
        /// <summary>NAVI records in more than one source: every copy, to be joined entry by entry.</summary>
        public required Dictionary<uint, List<(int File, long Offset, long Length)>> Navi;
        public required SortedDictionary<string, long> Dropped;
        public void Dispose() { foreach (var s in Streams) s.Dispose(); }
    }

    static Tree Open(JoinJob job)
    {
        var streams = new List<FileStream>();
        var sources = new List<Src>();
        var navi = new Dictionary<uint, List<(int, long, long)>>();
        var dropped = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var joinedCells = new HashSet<Grp>();
        List<Node>? top = null;
        var index = job.Masters.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        byte OutIndex(ModKey k) => k == job.Key ? (byte)job.Masters.Count
            : index.TryGetValue(k, out int i) ? (byte)i : throw new InvalidOperationException($"{k} is not among the masters of {job.Key}");
        try
        {
            for (int f = 0; f < job.Sources.Count; f++)
            {
                var s = new FileStream(job.Sources[f], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                streams.Add(s);
                var (tes4, subs) = ReadTes4(s);
                var key = ModKey.FromFileName(Path.GetFileName(job.Sources[f]));
                var masters = subs.Where(x => x.Type == "MAST").Select(x => ModKey.FromFileName(Name(x.Data))).ToList();
                var all = masters.Append(key).ToList();
                var src = new Src
                {
                    Key = key, Masters = masters, Tes4 = tes4, Subs = subs, Package = PackageOf(key, masters),
                    Index = all.Select(m => OutIndex(job.Map.Target(m))).ToArray(),
                    Ids = all.Select(m => job.Map.Ids(m)).ToArray(),
                    // Same name, same masters at the same indices, nothing of it moved: its records stand as they are.
                    Identity = key == job.Key && masters.SequenceEqual(job.Masters) && all.All(m => !job.Map.Moves(m)),
                };
                sources.Add(src);
                var nodes = ReadNodes(s, f, s.Position, s.Length);
                if (!src.Identity) Renumber(nodes, src);
                if (src.Ids[^1] is not null) foreach (var g in nodes.OfType<Grp>().Where(g => g.Type == 0 && g.Label == (uint)Mag("CELL"))) Rebucket(g);
                if (top is null) top = nodes;
                else Overlay(top, nodes, 0, streams, navi, dropped, joinedCells);
            }
            foreach (var g in joinedCells) OrderCell(g, streams);
            Tidy(top!);
            return new Tree { Sources = sources, Streams = streams, Top = top!, Navi = navi, Dropped = dropped };
        }
        catch { foreach (var s in streams) s.Dispose(); throw; }
    }

    /// <summary>FormIDs in headers and in the labels of the groups named after a record: a worldspace's children (1), a
    /// cell's (6), a topic's (7), a cell's persistent, temporary and distant references (8, 9, 10).</summary>
    static void Renumber(List<Node> nodes, Src src)
    {
        foreach (var n in nodes)
            if (n is Rec r) r.FormId = src.Map(r.FormId);
            else if (n is Grp g)
            {
                if (g.Type is 1 or 6 or 7 or 8 or 9 or 10)
                {
                    g.Label = src.Map(g.Label);
                    BitConverter.TryWriteBytes(g.Header.AsSpan(8, 4), g.Label);
                }
                Renumber(g.Children, src);
            }
    }

    /// <summary>
    /// Interior cells live in blocks by their own FormID (block: ID % 10, sub-block: ID / 10 % 10, as the Creation Kit and
    /// Mutagen file them); a cell whose ID changed goes to the block its new ID belongs in.
    /// </summary>
    static void Rebucket(Grp cells)
    {
        Grp? blockModel = null, subModel = null;
        var flat = new List<(Rec Cell, Grp? Children)>();
        foreach (var block in cells.Children.OfType<Grp>())
        {
            blockModel ??= block;
            foreach (var sub in block.Children.OfType<Grp>())
            {
                subModel ??= sub;
                for (int i = 0; i < sub.Children.Count; i++)
                    if (sub.Children[i] is Rec r) flat.Add((r, i + 1 < sub.Children.Count && sub.Children[i + 1] is Grp { Type: 6 } g ? g : null));
            }
        }
        if (blockModel is null || subModel is null) return;
        var blocks = new SortedDictionary<uint, (Grp Block, Dictionary<uint, Grp> Subs)>();
        static Grp Copy(Grp model, uint label)
        {
            var h = (byte[])model.Header.Clone();
            BitConverter.TryWriteBytes(h.AsSpan(8, 4), label);
            return new Grp(h, model.Type, label);
        }
        foreach (var (cell, children) in flat)
        {
            uint id = cell.FormId & 0xFFFFFF, b = id % 10, s = id / 10 % 10;
            if (!blocks.TryGetValue(b, out var block)) blocks[b] = block = (Copy(blockModel, b), []);
            if (!block.Subs.TryGetValue(s, out var sub)) { block.Subs[s] = sub = Copy(subModel, s); block.Block.Children.Add(sub); }
            sub.Children.Add(cell);
            if (children is not null) sub.Children.Add(children);
        }
        cells.Children = [.. blocks.Values.Select(b => (Node)b.Block)];
    }

    /// <summary>Lays <paramref name="from"/> over <paramref name="into"/>: the later copy of a record wins where the
    /// earlier stood, groups are joined, a NAVI's copies all kept.</summary>
    static void Overlay(List<Node> into, List<Node> from, uint topLabel, List<FileStream> streams, Dictionary<uint, List<(int, long, long)>> navi, SortedDictionary<string, long> dropped, HashSet<Grp> joinedCells)
    {
        var records = new Dictionary<uint, Rec>();
        var groups = new Dictionary<(int, uint), Grp>();
        foreach (var n in into)
            if (n is Rec r) records[r.FormId] = r;
            else if (n is Grp g) groups.TryAdd((g.Type, g.Label), g);
        var type = new byte[4];
        foreach (var n in from)
        {
            if (n is Rec r)
            {
                if (!records.TryGetValue(r.FormId, out var old)) { records[r.FormId] = r; into.Add(r); continue; }
                var s = streams[old.File];
                s.Position = old.Offset;
                s.ReadExactly(type);
                string name = Encoding.ASCII.GetString(type);
                dropped[name] = dropped.GetValueOrDefault(name) + 1;
                if (topLabel == (uint)Mag("NAVI"))
                {
                    if (!navi.TryGetValue(r.FormId, out var copies)) navi[r.FormId] = copies = [(old.File, old.Offset, old.Length)];
                    copies.Add((r.File, r.Offset, r.Length));
                }
                old.File = r.File; old.Offset = r.Offset; old.Length = r.Length;
            }
            else if (n is Grp g)
            {
                uint top = g.Type == 0 ? g.Label : topLabel;
                if (groups.TryGetValue((g.Type, g.Label), out var same))
                {
                    Overlay(same.Children, g.Children, top, streams, navi, dropped, joinedCells);
                    if (g.Type == 9) joinedCells.Add(same);
                    continue;
                }
                groups[(g.Type, g.Label)] = g;
                int at = IsChildGroup(g.Type) ? into.FindIndex(x => x is Rec rr && rr.FormId == g.Label) : -1;
                if (at >= 0) into.Insert(at + 1, g); else into.Add(g);
            }
        }
    }

    /// <summary>A cell's temporary children in the order they are written, land, then navmeshes, then references: laid over
    /// each other they came mixed, and Mutagen stops reading a cell's navmeshes at its first reference (measured on the
    /// proof: two navmeshes written, one read back).</summary>
    static void OrderCell(Grp cell, List<FileStream> streams)
    {
        int land = Mag("LAND"), navm = Mag("NAVM");
        var type = new byte[4];
        int Rank(Node n)
        {
            if (n is not Rec r) return 3;
            var s = streams[r.File];
            s.Position = r.Offset;
            s.ReadExactly(type);
            int t = BitConverter.ToInt32(type);
            return t == land ? 0 : t == navm ? 1 : 2;
        }
        cell.Children = [.. cell.Children.Select(n => (Node: n, Rank: Rank(n))).OrderBy(x => x.Rank).Select(x => x.Node)];
    }

    /// <summary>Skyrim's order of top-level groups, as the Creation Kit writes them.</summary>
    static readonly string[] TopOrder = ("GMST KYWD LCRT AACT TXST GLOB CLAS FACT HDPT HAIR EYES RACE SOUN ASPC MGEF SCPT LTEX ENCH SPEL SCRL ACTI TACT ARMO BOOK CONT DOOR INGR LIGH MISC APPA STAT "
        + "SCOL MSTT PWAT GRAS TREE CLDC FLOR FURN WEAP AMMO NPC_ LVLN KEYM ALCH IDLM COBJ PROJ HAZD SLGM LVLI WTHR CLMT SPGD RFCT REGN NAVI CELL WRLD DIAL QUST IDLE PACK CSTY LSCR LVSP ANIO "
        + "WATR EFSH EXPL DEBR IMGS IMAD FLST PERK BPTD ADDN AVIF CAMS CPTH VTYP MATT IPCT IPDS ARMA ECZN LCTN MESG RGDL DOBJ LGTM MUSC FSTP FSTS SMBN SMQN SMEN DLBR MUST DLVW WOOP SHOU EQUP "
        + "RELA SCEN ASTP OTFT ARTO MATO MOVT SNDR DUAL SNCT SOPM COLL CLFM REVB").Split(' ');

    /// <summary>The top-level groups in Skyrim's order, and a worldspace's persistent cell before its blocks (a plugin
    /// laid over may have brought it after them).</summary>
    static void Tidy(List<Node> top)
    {
        var rank = TopOrder.Select((t, i) => (t, i)).ToDictionary(x => (uint)Mag(x.t), x => x.i);
        var sorted = top.OrderBy(n => n is Grp g && rank.TryGetValue(g.Label, out int r) ? r : int.MaxValue).ToList();
        top.Clear();
        top.AddRange(sorted);
        foreach (var worlds in top.OfType<Grp>().Where(g => g.Label == (uint)Mag("WRLD")))
            foreach (var children in worlds.Children.OfType<Grp>().Where(g => g.Type == 1))
                children.Children = [.. children.Children.Where(n => n is Rec || n is Grp { Type: 6 }), .. children.Children.Where(n => n is Grp { Type: not 6 })];
    }

    static readonly Dictionary<int, Func<MutagenFrame, TypedParseParams, IMajorRecord>> Readers = MakeReaders();

    /// <summary>Every Skyrim record class's own reader, by record type (133 classes in Mutagen 0.54.4).</summary>
    static Dictionary<int, Func<MutagenFrame, TypedParseParams, IMajorRecord>> MakeReaders()
    {
        var readers = new Dictionary<int, Func<MutagenFrame, TypedParseParams, IMajorRecord>>();
        var assembly = typeof(SkyrimMajorRecord).Assembly;
        foreach (var t in assembly.GetTypes().Where(t => t.IsPublic && t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(SkyrimMajorRecord))))
        {
            var trigger = assembly.GetType(t.FullName + "_Registration")?.GetField("TriggeringRecordType", BindingFlags.Static | BindingFlags.Public);
            var read = t.GetMethod("CreateFromBinary", BindingFlags.Static | BindingFlags.Public, null, [typeof(MutagenFrame), typeof(TypedParseParams)], null);
            if (trigger?.GetValue(null) is RecordType type && read is not null)
                readers[type.TypeInt] = read.CreateDelegate<Func<MutagenFrame, TypedParseParams, IMajorRecord>>();
        }
        return readers;
    }

    /// <summary>A writer's settings. Its FormIDs go through a master package Mutagen sets only inside its own WriteToBinary, so
    /// writing a record alone it is set here by its non-public setter.</summary>
    static WritingBundle BundleFor(ModKey key, IReadOnlyList<ModKey> masters)
    {
        var references = new MasterReferenceCollection(key, masters.Select(m => (IMasterReferenceGetter)new MasterReference { Master = m }));
        var bundle = new WritingBundle(GameConstants.SkyrimSE) { MasterReferences = references };
        typeof(WritingBundle).GetProperty("SeparatedMasterPackage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(bundle, PackageOf(key, masters));
        return bundle;
    }

    /// <summary>What a thread needs: the parsers change their own state record by record, so none is shared.</summary>
    sealed class Worker(Tree tree, ModKey key, IReadOnlyList<ModKey> masters)
    {
        public readonly ParsingMeta[] Meta = tree.Sources.Select(s => new ParsingMeta(GameConstants.SkyrimSE, s.Key, s.Package)).ToArray();
        public readonly ParsingMeta OutMeta = new(GameConstants.SkyrimSE, key, PackageOf(key, masters));
        public readonly WritingBundle Bundle = BundleFor(key, masters);
        public readonly WritingBundle[] SourceBundles = tree.Sources.Select(s => BundleFor(s.Key, s.Masters)).ToArray();
        public readonly MemoryStream Buffer = new();
    }

    static IMajorRecord Parse(byte[] plain, ParsingMeta meta, bool inWorld)
    {
        // A navmesh's parent is read by whether it stands in a worldspace, which the group round it says.
        meta.InWorldspace = inWorld;
        int type = BitConverter.ToInt32(plain, 0);
        if (!Readers.TryGetValue(type, out var read)) throw new InvalidDataException($"no reader for {Encoding.ASCII.GetString(plain, 0, 4)} records");
        return read(new MutagenFrame(new MutagenMemoryReadStream(plain, meta)), default);
    }

    /// <summary>
    /// Record types Mutagen writes back byte for byte, read back to prove it (CheckJoin) on 2.4 million real ones. Other
    /// types can come back changed: 154 of Daggerfall.esm's armours and factions did (values kept as hundredths, 23.45
    /// written back as 23.44, measured 2026-10-30), so those go through <see cref="Rebuilt"/>.
    /// </summary>
    static readonly HashSet<string> WrittenAsRead = ["REFR", "ACHR", "CELL", "LAND", "NAVM", "WRLD", "NAVI"];

    /// <summary>
    /// A record's new bytes from the old: <paramref name="asRead"/> is Mutagen's writing of it before the links moved,
    /// <paramref name="moved"/> after, so where those two differ are the FormIDs, and only those change in
    /// <paramref name="plain"/>: whatever Mutagen would have altered stays as it was.
    /// </summary>
    static byte[] Rebuilt(byte[] plain, byte[] asRead, byte[] moved)
    {
        if (asRead.Length != plain.Length || moved.Length != plain.Length) return moved;
        var result = (byte[])plain.Clone();
        for (int i = 0; i < result.Length; i++) if (asRead[i] != moved[i]) result[i] = moved[i];
        return result;
    }

    static byte[] Emit(IMajorRecord record, Worker w, WritingBundle? bundle = null)
    {
        w.Buffer.SetLength(0);
        using (var writer = new MutagenWriter(w.Buffer, bundle ?? w.Bundle, false)) ((IBinaryItem)record).WriteToBinary(writer);
        var all = w.Buffer.GetBuffer();
        // A cell or worldspace with no children writes only itself; the record's own size marks where it ends.
        int length = HeaderSize + (int)BitConverter.ToUInt32(all, 4);
        return all.AsSpan(0, length).ToArray();
    }

    static byte[] Inflate(byte[] packed)
    {
        uint size = BitConverter.ToUInt32(packed, HeaderSize);
        var plain = new byte[HeaderSize + size];
        Buffer.BlockCopy(packed, 0, plain, 0, HeaderSize);
        using (var z = new ZLibStream(new MemoryStream(packed, HeaderSize + 4, packed.Length - HeaderSize - 4), CompressionMode.Decompress)) z.ReadExactly(plain, HeaderSize, (int)size);
        BitConverter.TryWriteBytes(plain.AsSpan(4, 4), size);
        BitConverter.TryWriteBytes(plain.AsSpan(8, 4), BitConverter.ToUInt32(packed, 8) & ~CompressedFlag);
        return plain;
    }

    /// <summary>A record compressed, or null when that wouldn't make it smaller (the rule of <see cref="Pack"/>).</summary>
    static byte[]? Deflate(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain, HeaderSize, plain.Length - HeaderSize);
        if (ms.Length + 4 >= plain.Length - HeaderSize) return null;
        var packed = new byte[HeaderSize + 4 + ms.Length];
        Buffer.BlockCopy(plain, 0, packed, 0, HeaderSize);
        BitConverter.TryWriteBytes(packed.AsSpan(4, 4), (uint)(4 + ms.Length));
        BitConverter.TryWriteBytes(packed.AsSpan(8, 4), BitConverter.ToUInt32(plain, 8) | CompressedFlag);
        BitConverter.TryWriteBytes(packed.AsSpan(HeaderSize, 4), (uint)(plain.Length - HeaderSize));
        ms.Position = 0;
        ms.ReadExactly(packed, HeaderSize + 4, (int)ms.Length);
        return packed;
    }

    /// <summary>The records of a tree in order, each with whether it stands under a worldspace.</summary>
    static void Flatten(IEnumerable<Node> nodes, bool inWorld, List<(Rec R, bool World)> into)
    {
        foreach (var n in nodes)
            if (n is Rec r) into.Add((r, inWorld));
            else if (n is Grp g) Flatten(g.Children, inWorld || (g.Type == 0 && g.Label == (uint)Mag("WRLD")), into);
    }

    static FormKey KeyOf(uint fid, JoinJob job) => new(fid >> 24 >= job.Masters.Count ? job.Key : job.Masters[(int)(fid >> 24)], fid & 0xFFFFFF);

    /// <summary>Runs <paramref name="work"/> over the items in file order, a batch at a time: at most 4,096 records or
    /// 128 MB held read, on all but two cores.</summary>
    static void Batches<T>(IEnumerable<T> items, Func<T, long> bytes, Action<List<T>> read, Action<T> work)
    {
        var batch = new List<T>();
        long held = 0;
        void Flush()
        {
            if (batch.Count == 0) return;
            read(batch);
            Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, work);
            batch.Clear();
            held = 0;
        }
        foreach (var item in items)
        {
            batch.Add(item);
            held += bytes(item);
            if (batch.Count >= 4096 || held >= 128L << 20) Flush();
        }
        Flush();
    }

    static byte[] ReadAt(FileStream s, long offset, long length)
    {
        var b = new byte[length];
        s.Position = offset;
        s.ReadExactly(b);
        return b;
    }

    /// <summary>A NAVI's copies as one, in the output's FormIDs: every entry, a later copy's for the same navmesh winning.</summary>
    static NavigationMeshInfoMap JoinNavi(Tree tree, List<(int File, long Offset, long Length)> copies, JoinJob job, Worker w)
    {
        NavigationMeshInfoMap? last = null;
        var entries = new Dictionary<FormKey, NavigationMapInfo>();
        foreach (var (file, offset, length) in copies)
        {
            byte[] raw;
            lock (tree.Streams[file]) raw = ReadAt(tree.Streams[file], offset, length);
            var plain = (BitConverter.ToUInt32(raw, 8) & CompressedFlag) != 0 ? Inflate(raw) : raw;
            last = (NavigationMeshInfoMap)Parse(plain, w.Meta[file], false);
            ((IMajorRecord)last).RemapLinks(job.Map);
            foreach (var e in last.MapInfos) entries[e.NavigationMesh.FormKey] = e;
        }
        last!.MapInfos.Clear();
        last.MapInfos.AddRange(entries.Values);
        return last;
    }

    /// <summary>Writes the join; nothing beside it changes.</summary>
    public static JoinStats WriteJoin(JoinJob job)
    {
        using var tree = Open(job);
        var stats = new JoinStats();
        foreach (var (name, n) in tree.Dropped) stats.Dropped[name] = n;
        using var workers = new ThreadLocal<Worker>(() => new Worker(tree, job.Key, job.Masters));
        var all = new List<(Rec R, bool World)>();
        Flatten(tree.Top, false, all);
        var joined = tree.Navi.Keys.ToHashSet();
        var raws = new Dictionary<Rec, byte[]>();
        var h = new byte[HeaderSize];
        var todo = new List<(Rec R, bool World)>();
        foreach (var item in all.Where(x => !joined.Contains(x.R.FormId)).OrderBy(x => x.R.File).ThenBy(x => x.R.Offset))
        {
            // A plugin standing as it is: only its navmesh and land not compressed yet are touched.
            if (tree.Sources[item.R.File].Identity)
            {
                var s = tree.Streams[item.R.File];
                s.Position = item.R.Offset;
                s.ReadExactly(h);
                if ((BitConverter.ToUInt32(h, 8) & CompressedFlag) != 0 || item.R.Length <= HeaderSize + 256 || !Compressed.Contains(Encoding.ASCII.GetString(h, 0, 4))) { stats.Copied++; continue; }
            }
            todo.Add(item);
        }
        long rewritten = 0, copied = 0;
        Batches(todo, x => x.R.Length, batch =>
        {
            raws.Clear();
            foreach (var (r, _) in batch) raws[r] = ReadAt(tree.Streams[r.File], r.Offset, r.Length);
        }, item =>
        {
            var (r, world) = item;
            var src = tree.Sources[r.File];
            var w = workers.Value!;
            var raw = raws[r];
            string type = Encoding.ASCII.GetString(raw, 0, 4);
            if (src.Identity) { r.Packed = Deflate(raw); Interlocked.Increment(ref copied); return; }
            bool packed = (BitConverter.ToUInt32(raw, 8) & CompressedFlag) != 0;
            var before = packed ? Inflate(raw) : raw;
            var record = Parse(before, w.Meta[r.File], world);
            var asRead = WrittenAsRead.Contains(type) ? null : Emit(record, w, w.SourceBundles[r.File]);
            record.RemapLinks(job.Map);
            ((IMajorRecordInternal)record).FormKey = KeyOf(r.FormId, job);
            var plain = Emit(record, w);
            if (asRead is not null) plain = Rebuilt(before, asRead, plain);
            r.Packed = (packed || (Compressed.Contains(type) && plain.Length > HeaderSize + 256) ? Deflate(plain) : null) ?? plain;
            Interlocked.Increment(ref rewritten);
        });
        raws.Clear();
        stats.Rewritten = rewritten;
        stats.Copied += copied;
        var one = new Worker(tree, job.Key, job.Masters);
        foreach (var (id, copies) in tree.Navi)
        {
            var map = JoinNavi(tree, copies, job, one);
            stats.NaviEntries += map.MapInfos.Count;
            ((IMajorRecordInternal)map).FormKey = KeyOf(id, job);
            all.First(x => x.R.FormId == id).R.Packed = Emit(map, one);
        }
        foreach (var n in tree.Top) Measure(n);
        long count = tree.Top.Sum(Count);
        var head = tree.Sources[job.HeaderFrom];
        var onam = new SortedSet<uint>();
        uint next = 0;
        foreach (var src in tree.Sources)
        {
            if (src.Subs.FirstOrDefault(x => x.Type == "HEDR") is { } hedr) next = Math.Max(next, BitConverter.ToUInt32(hedr.Data, 8));
            foreach (var o in src.Subs.Where(x => x.Type == "ONAM"))
                for (int i = 0; i + 4 <= o.Data.Length; i += 4)
                    if (src.Map(BitConverter.ToUInt32(o.Data, i)) is var id && id >> 24 < job.Masters.Count) onam.Add(id);
        }
        foreach (var (r, _) in all) if (r.FormId >> 24 >= job.Masters.Count) next = Math.Max(next, (r.FormId & 0xFFFFFF) + 1);
        var subs = new List<Sub>();
        int mastAt = head.Subs.FindIndex(x => x.Type == "MAST");
        if (mastAt < 0) mastAt = head.Subs.FindIndex(x => x.Type == "HEDR") + 1;
        for (int i = 0; i <= head.Subs.Count; i++)
        {
            if (i == mastAt)
                foreach (var m in job.Masters)
                {
                    subs.Add(new("MAST", [.. Encoding.Latin1.GetBytes(m.FileName.String), 0]));
                    subs.Add(new("DATA", new byte[8]));
                }
            if (i < head.Subs.Count && head.Subs[i].Type is not ("MAST" or "DATA" or "ONAM")) subs.Add(head.Subs[i]);
        }
        using (var output = new FileStream(job.OutPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            WriteTes4(output, head.Tes4, subs, count, next, onam, (BitConverter.ToUInt32(head.Tes4, 8) & 1) != 0);
            var buffer = new byte[1 << 20];
            foreach (var n in tree.Top) Write(output, n, tree.Streams, buffer);
        }
        stats.Bytes = new FileInfo(job.OutPath).Length;
        return stats;
    }

    /// <summary>
    /// A written join read back against its sources, record for record: the same FormIDs in the same places, none twice,
    /// each record (read with Mutagen from the output's bytes) written the same as its source's with the links remapped; every link
    /// into the set reaching a record, as many dangling as before (<paramref name="before"/> judges the sources' links,
    /// <paramref name="after"/> the output's), every navmesh edge link reaching a navmesh.
    /// </summary>
    public static JoinCheck CheckJoin(JoinJob job, Known before, Known after)
    {
        var check = new JoinCheck();
        using var tree = Open(job);
        using var output = new FileStream(job.OutPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (_, outSubs) = ReadTes4(output);
        var outMasters = outSubs.Where(x => x.Type == "MAST").Select(x => ModKey.FromFileName(Name(x.Data))).ToList();
        if (!outMasters.SequenceEqual(job.Masters)) { check.Different++; check.Note($"{job.Key}: masters written are {string.Join(", ", outMasters)}"); }
        var outTop = ReadNodes(output, 0, output.Position, output.Length);
        var a = new List<(Rec R, bool World)>();
        var b = new List<(Rec R, bool World)>();
        Flatten(tree.Top, false, a);
        Flatten(outTop, false, b);
        if (a.Count != b.Count) { check.Different += Math.Abs(a.Count - b.Count); check.Note($"{job.Key}: {b.Count:N0} records written of {a.Count:N0}"); return check; }
        var seen = new Dictionary<byte, Bits>();
        foreach (var (r, _) in b)
        {
            byte mod = (byte)(r.FormId >> 24);
            if (!seen.TryGetValue(mod, out var bits)) seen[mod] = bits = new Bits();
            if (bits.Get(r.FormId)) { check.Collisions++; check.Note($"{job.Key}: FormID {r.FormId:X8} twice"); }
            bits.Set(r.FormId);
        }
        var pairs = Enumerable.Range(0, a.Count).Select(i => (A: a[i].R, B: b[i].R, a[i].World)).OrderBy(p => p.A.File).ThenBy(p => p.A.Offset).ToList();
        foreach (var p in pairs.Where(p => p.A.FormId != p.B.FormId).Take(5)) { check.Different++; check.Note($"{job.Key}: {p.A.FormId:X8} written as {p.B.FormId:X8}"); }
        using var workers = new ThreadLocal<Worker>(() => new Worker(tree, job.Key, job.Masters));
        var raws = new Dictionary<Rec, (byte[] A, byte[] B)>();
        long compared = 0, copied = 0, different = 0, danglingBefore = 0, danglingAfter = 0, edges = 0, badEdges = 0, doors = 0, badDoors = 0;
        Batches(pairs, p => p.A.Length + p.B.Length, batch =>
        {
            raws.Clear();
            foreach (var p in batch) raws[p.A] = (tree.Navi.ContainsKey(p.A.FormId) ? [] : ReadAt(tree.Streams[p.A.File], p.A.Offset, p.A.Length), ReadAt(output, p.B.Offset, p.B.Length));
        }, p =>
        {
            var (rawA, rawB) = raws[p.A];
            var w = workers.Value!;
            var src = tree.Sources[p.A.File];
            var plainB = (BitConverter.ToUInt32(rawB, 8) & CompressedFlag) != 0 ? Inflate(rawB) : rawB;
            if (src.Identity && rawA.Length > 0)
            {
                var plainA = (BitConverter.ToUInt32(rawA, 8) & CompressedFlag) != 0 ? Inflate(rawA) : rawA;
                if (!plainA.AsSpan().SequenceEqual(plainB)) { Interlocked.Increment(ref different); check.Note($"{job.Key}: {p.A.FormId:X8} changed"); }
                Interlocked.Increment(ref copied);
                return;
            }
            IMajorRecord record;
            if (rawA.Length == 0) record = JoinNavi(tree, tree.Navi[p.A.FormId], job, w);
            else
            {
                record = Parse((BitConverter.ToUInt32(rawA, 8) & CompressedFlag) != 0 ? Inflate(rawA) : rawA, w.Meta[p.A.File], p.World);
                foreach (var l in record.EnumerateFormLinks()) if (!l.IsNull && !before.Exists(l.FormKey)) Interlocked.Increment(ref danglingBefore);
                record.RemapLinks(job.Map);
            }
            ((IMajorRecordInternal)record).FormKey = KeyOf(p.B.FormId, job);
            var written = Parse(plainB, w.OutMeta, p.World);
            // Compared as Mutagen writes them: its Equals says no for some records against themselves (101 of Daggerfall.esm's 137 armours).
            if (!Emit(record, w).AsSpan().SequenceEqual(Emit(written, w))) { Interlocked.Increment(ref different); check.Note($"{job.Key}: {written.GetType().Name} {p.B.FormId:X8} differs from its source"); }
            foreach (var l in written.EnumerateFormLinks()) if (!l.IsNull && !after.Exists(l.FormKey)) Interlocked.Increment(ref danglingAfter);
            if (written is INavigationMeshGetter { Data: { } data })
            {
                foreach (var e in data.EdgeLinks) { Interlocked.Increment(ref edges); if (!after.IsNavmesh(e.Mesh.FormKey)) Interlocked.Increment(ref badEdges); }
                foreach (var d in data.DoorTriangles) { Interlocked.Increment(ref doors); if (!d.Door.IsNull && !after.Exists(d.Door.FormKey)) Interlocked.Increment(ref badDoors); }
            }
            Interlocked.Increment(ref compared);
        });
        check.Compared = compared; check.Copied = copied; check.Different += different; check.DanglingBefore = danglingBefore; check.DanglingAfter = danglingAfter;
        check.EdgeLinks = edges; check.BadEdgeLinks = badEdges; check.DoorLinks = doors; check.BadDoorLinks = badDoors;
        return check;
    }
}
