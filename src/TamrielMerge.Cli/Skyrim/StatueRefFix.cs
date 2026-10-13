using System.Numerics;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Brings the statues of Daggerfall plugins built before 2026-10-10 in line with the converter (user: "statues obstruct
/// doors in the throne room", "some statues are rotated"), so they needn't be rebuilt: in each dungeon cell ("DF" +
/// location) and building interior ("DFI" + style, RMB block, building, R/B), the statues are placed again as the
/// converter now places them (<see cref="PropReplacer"/>'s slim StatueDibella fitted to height, <see cref="ClutterPlacer"/>
/// within the sprite's width, both facing into the room) and each existing statue reference nearest a new placement
/// takes its base, position, rotation and scale. Only those references change; the plugin is rewritten as
/// <see cref="PluginMerger"/> handles parts (records byte for byte, group sizes recounted), and its header gets
/// <see cref="Marker"/> so it is fixed once. Statues in town streets keep theirs until the towns are rebuilt.
/// </summary>
public static class StatueRefFix
{
    public const string Marker = "TamrielMerge statues 2026-10-10";
    const int HeaderSize = 24;
    const uint Compressed = 0x00040000;

    /// <summary>Skyrim.esm statues the converter has used for Daggerfall's statues.</summary>
    static readonly HashSet<uint> StatueBases = [0xE3FC7, 0x91712, 0x8F965, 0xD068D, 0xAA93D, 0x39DF9, 0xAA93F];

    sealed record Old(long Offset, Vector3 Position);
    sealed record New(FormKey Base, Vector3 Position, Vector3 Rotation, float Scale);

    public static IEnumerable<string> Fix(IReadOnlyList<string> paths, string arena2, string skyrimData, Action<string>? detail = null)
    {
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var props = new PropReplacer(skyrim, skyrimData);
        var models = DfModel.LoadAll(arena2);
        var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
        var scratch = Path.Combine(Path.GetTempPath(), "TamrielMerge-statues");
        var builder = new DungeonCellBuilder(new SkyrimMod(ModKey.FromFileName("StatueFix.esp"), SkyrimRelease.SkyrimSE), arena2, models, bsa, scratch, new Navmesh.NavmeshBuilder(new SkyrimMod(ModKey.FromFileName("StatueFixNav.esp"), SkyrimRelease.SkyrimSE)), props);
        var clutter = new ClutterPlacer(skyrim, props, []);
        var locations = DfLocations.LoadAll(arena2).Where(l => l.DungeonBlocks.Count > 0).ToLookup(l => LocationPlacer.Sanitize(l.Name));
        var cache = new Dictionary<string, List<New>>();
        var skyrimKey = ModKey.FromFileName("Skyrim.esm");

        foreach (var path in paths)
        {
            var bytes = File.ReadAllBytes(path);
            int tes4Size = BitConverter.ToInt32(bytes, 4);
            var tes4 = DoorRefFix.Subrecords(bytes, HeaderSize, tes4Size).ToList();
            if (tes4.Any(s => s.Type == "SNAM" && Encoding.UTF8.GetString(bytes, s.At, s.Length).Contains(Marker))) { yield return $"{Path.GetFileName(path)}: already fixed"; continue; }
            var masters = tes4.Where(s => s.Type == "MAST").Select(s => Encoding.ASCII.GetString(bytes, s.At, s.Length).TrimEnd('\0')).ToList();
            if (masters.Count == 0 || !masters[0].Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) { yield return $"{Path.GetFileName(path)}: Skyrim.esm isn't its first master, left as it is"; continue; }

            // Interior cells by FormID (EditorID), and each one's statue references.
            var cellNames = new Dictionary<uint, string>();
            var statues = new Dictionary<uint, List<Old>>();
            Walk(bytes, HeaderSize + tes4Size, bytes.Length, 0, (offset, type, formId, cell) =>
            {
                if ((BitConverter.ToUInt32(bytes, (int)offset + 8) & Compressed) != 0) return;
                int size = BitConverter.ToInt32(bytes, (int)offset + 4);
                var subs = DoorRefFix.Subrecords(bytes, (int)offset + HeaderSize, size).ToList();
                if (type == "CELL")
                {
                    if (subs.FirstOrDefault(s => s.Type == "EDID") is { Type: "EDID" } e) cellNames[formId] = Encoding.ASCII.GetString(bytes, e.At, e.Length).TrimEnd('\0');
                }
                else if (type == "REFR" && cell != 0 && subs.FirstOrDefault(s => s.Type == "NAME") is { Type: "NAME" } n && subs.FirstOrDefault(s => s.Type == "DATA" && s.Length == 24) is { Type: "DATA" } d)
                {
                    uint baseId = BitConverter.ToUInt32(bytes, n.At);
                    if (baseId >> 24 != 0 || !StatueBases.Contains(baseId & 0xFFFFFF)) return;
                    var pos = new Vector3(BitConverter.ToSingle(bytes, d.At), BitConverter.ToSingle(bytes, d.At + 4), BitConverter.ToSingle(bytes, d.At + 8));
                    if (!statues.TryGetValue(cell, out var list)) statues[cell] = list = [];
                    list.Add(new Old(offset, pos));
                }
            });

            var replaced = new Dictionary<long, byte[]>();
            int cells = 0, moved = 0, unknown = 0;
            foreach (var (cell, olds) in statues)
            {
                if (!cellNames.TryGetValue(cell, out var name)) { unknown++; detail?.Invoke($"  cell {cell:X8} (no name, outdoors): {olds.Count} statues"); continue; }
                // The placements this cell's statues should have (candidates: every source the name could be).
                var options = new List<List<New>>();
                if (name.StartsWith("DFI"))
                {
                    if (Interior(name) is { } key)
                        options.Add(cache.TryGetValue(name, out var c) ? c : cache[name] = InteriorStatues(key.Rmb, key.Index));
                }
                else if (name.StartsWith("DF"))
                    foreach (var loc in locations[name[2..]])
                    {
                        string k = $"{loc.Name}@{loc.RegionName}";
                        options.Add(cache.TryGetValue(k, out var c) ? c : cache[k] = DungeonStatues(loc));
                    }
                var best = options.Select(o => (Placements: o, Pairs: Pair(olds, o))).OrderByDescending(o => o.Pairs.Count).ThenBy(o => o.Pairs.Sum(p => p.Distance)).FirstOrDefault();
                if (best.Placements is null || best.Pairs.Count == 0)
                {
                    unknown++;
                    detail?.Invoke($"  {name}: {olds.Count} statues, {options.Count} sources ({string.Join(", ", options.Select(o => o.Count))} statues placed), none paired; first at {olds[0].Position}");
                    continue;
                }
                if (best.Pairs.Count < olds.Count) detail?.Invoke($"  {name}: {best.Pairs.Count} of {olds.Count} statues paired");
                cells++;
                foreach (var (old, n, _) in best.Pairs)
                {
                    if (n.Base.ModKey != skyrimKey) continue;
                    replaced[old.Offset] = Rewritten(bytes, old.Offset, n);
                    moved++;
                }
            }
            // The new file: records as they were except the statues, groups resized, the marker in the header.
            using var output = new MemoryStream(bytes.Length + 4096);
            output.Write(WithMarker(bytes, tes4Size));
            output.Write(Copy(bytes, HeaderSize + tes4Size, bytes.Length, replaced));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, output.ToArray());
            File.Move(tmp, path, overwrite: true);
            yield return $"{Path.GetFileName(path)}: {moved} statues placed again in {cells} cells{(unknown > 0 ? $" ({unknown} cells with statues not traced to Daggerfall, left as they were)" : "")}";
        }

        // A dungeon's statues, in its cell's space.
        List<New> DungeonStatues(DfLocation loc)
        {
            var result = new List<New>();
            var dungeon = DfDungeon.Load(loc, bsa, []);
            var polys = dungeon.Polygons(models).ToList();
            var floors = polys.Where(q => q.Normal.Y > 0.7f).ToList();
            var ceilings = polys.Where(q => q.Normal.Y < -0.7f).ToList();
            var rooms = new RoomFacing(polys);
            foreach (var (block, placement) in dungeon.Blocks)
            {
                var origin = DfDungeon.BlockOrigin(placement);
                foreach (var o in block.Objects)
                    result.AddRange(Statues(o, origin, !o.HasAction, floors, ceilings, rooms));
            }
            return result;
        }

        // A building's interior statues, in the interior's (its block's) space.
        List<New> InteriorStatues(string rmb, int index)
        {
            var tb = DfTownBlock.Load(bsa, rmb + ".RMB");
            if (tb.Buildings.FirstOrDefault(b => b.Index == index) is not { } building) return [];
            var block = DfTownLayout.Interior(tb, building, models);
            var polys = DungeonCellBuilder.BlockPolygons(block, models);
            var floors = polys.Where(q => q.Normal.Y > 0.7f).ToList();
            var ceilings = polys.Where(q => q.Normal.Y < -0.7f).ToList();
            var rooms = new RoomFacing(polys);
            return block.Objects.SelectMany(o => Statues(o, Vector3.Zero, true, floors, ceilings, rooms)).ToList();
        }

        IEnumerable<New> Statues(DfRdbObject o, Vector3 origin, bool replaceable, List<DfPlacedPolygon> floors, List<DfPlacedPolygon> ceilings, RoomFacing rooms)
        {
            if (o.Type == DfRdbObjectType.Model && replaceable && PropReplacer.IsReplaced(o.ModelId) && models.TryGetValue(o.ModelId, out var model) && PropReplacer.TurnsToRoom(model)
                && props.Replace(o, origin, model, DungeonCellBuilder.PropFacing(rooms, origin + o.Position, model, floors)) is { } prop)
                yield return new New(prop.Base, prop.PositionSkyrim, new Vector3(0, 0, prop.Heading), prop.Scale);
            else if (ClutterPlacer.IsStatue(o) && o.RecordOffset >= 0 && clutter.Covers(o))
                foreach (var p in builder.PlaceSprite(clutter, o, origin + o.Position, floors, ceilings, rooms))
                    yield return new New(p.Base, p.PositionSkyrim, p.Rotation, p.Scale);
        }
    }

    /// <summary>"DFI" + style + RMB block + building (two digits) + R/B, as TownBuilder names shared interiors.</summary>
    static (string Rmb, int Index)? Interior(string name)
    {
        string rest = name[3..];
        foreach (var style in Enum.GetNames<TownStyle>())
            if (rest.StartsWith(style))
            {
                rest = rest[style.Length..];
                if (rest.Length < 4 || rest[^1] is not ('R' or 'B') || !int.TryParse(rest[^3..^1], out int index)) return null;
                return (rest[..^3], index);
            }
        return null;
    }

    /// <summary>Each new placement with the nearest unused old statue (within 200 units across, 600 up or down).</summary>
    static List<(Old Old, New New, float Distance)> Pair(List<Old> olds, List<New> news)
    {
        var used = new HashSet<Old>();
        var pairs = new List<(Old, New, float)>();
        foreach (var n in news)
        {
            Old? best = null; float bestD = 200;
            foreach (var o in olds)
            {
                if (used.Contains(o) || MathF.Abs(o.Position.Z - n.Position.Z) > 600) continue;
                float d = Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), new Vector2(n.Position.X, n.Position.Y));
                if (d < bestD) { bestD = d; best = o; }
            }
            if (best is null) continue;
            used.Add(best);
            pairs.Add((best, n, bestD));
        }
        return pairs;
    }

    /// <summary>The reference at <paramref name="offset"/> with the new base, position, rotation and scale.</summary>
    static byte[] Rewritten(byte[] bytes, long offset, New n)
    {
        int size = BitConverter.ToInt32(bytes, (int)offset + 4);
        using var data = new MemoryStream();
        void Put(string type, byte[] d) { data.Write(Encoding.ASCII.GetBytes(type)); data.Write(BitConverter.GetBytes((ushort)d.Length)); data.Write(d); }
        byte[] Floats(params float[] f) => f.SelectMany(BitConverter.GetBytes).ToArray();
        bool scaled = MathF.Abs(n.Scale - 1) > 0.01f;
        foreach (var (type, at, len) in DoorRefFix.Subrecords(bytes, (int)offset + HeaderSize, size))
        {
            if (type == "XSCL") continue;   // written before DATA when wanted
            if (type == "NAME") Put("NAME", BitConverter.GetBytes(n.Base.ID & 0xFFFFFF));   // Skyrim.esm is master 0
            else if (type == "DATA" && len == 24)
            {
                if (scaled) Put("XSCL", Floats(n.Scale));
                Put("DATA", Floats(n.Position.X, n.Position.Y, n.Position.Z, n.Rotation.X, n.Rotation.Y, n.Rotation.Z));
            }
            else Put(type, bytes.AsSpan(at, len).ToArray());
        }
        var header = bytes.AsSpan((int)offset, HeaderSize).ToArray();
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), (int)data.Length);
        return [.. header, .. data.ToArray()];
    }

    /// <summary>bytes[start .. end) with the replaced records swapped in and every group's size recounted.</summary>
    static byte[] Copy(byte[] bytes, long start, long end, Dictionary<long, byte[]> replaced)
    {
        using var output = new MemoryStream();
        for (long p = start; p < end;)
        {
            uint size = BitConverter.ToUInt32(bytes, (int)p + 4);
            if (Encoding.ASCII.GetString(bytes, (int)p, 4) == "GRUP")
            {
                var inner = Copy(bytes, p + HeaderSize, p + size, replaced);
                var header = bytes.AsSpan((int)p, HeaderSize).ToArray();
                BitConverter.TryWriteBytes(header.AsSpan(4, 4), (uint)(inner.Length + HeaderSize));
                output.Write(header);
                output.Write(inner);
                p += size;
            }
            else
            {
                if (replaced.TryGetValue(p, out var r)) output.Write(r);
                else output.Write(bytes, (int)p, HeaderSize + (int)size);
                p += HeaderSize + size;
            }
        }
        return output.ToArray();
    }

    /// <summary>Every record in bytes[start .. end) with the interior cell it belongs to (0 outside a cell's children).</summary>
    static void Walk(byte[] bytes, long start, long end, uint cell, Action<long, string, uint, uint> visit)
    {
        for (long p = start; p < end;)
        {
            string type = Encoding.ASCII.GetString(bytes, (int)p, 4);
            uint size = BitConverter.ToUInt32(bytes, (int)p + 4);
            if (type == "GRUP")
            {
                int groupType = BitConverter.ToInt32(bytes, (int)p + 12);
                uint label = BitConverter.ToUInt32(bytes, (int)p + 8);
                // A cell's children (6) and its persistent (8) / temporary (9) references carry the cell's FormID.
                Walk(bytes, p + HeaderSize, p + size, groupType is 6 or 8 or 9 ? label : groupType is 0 or 1 or 2 or 3 or 4 or 5 ? 0 : cell, visit);
                p += size;
            }
            else
            {
                visit(p, type, BitConverter.ToUInt32(bytes, (int)p + 12), cell);
                p += HeaderSize + size;
            }
        }
    }

    /// <summary>The header with <see cref="Marker"/> added to its description.</summary>
    static byte[] WithMarker(byte[] bytes, int tes4Size)
    {
        var subs = DoorRefFix.Subrecords(bytes, HeaderSize, tes4Size).Select(s => (s.Type, Data: bytes.AsSpan(s.At, s.Length).ToArray())).ToList();
        int snam = subs.FindIndex(s => s.Type == "SNAM");
        string description = snam >= 0 ? Encoding.UTF8.GetString(subs[snam].Data).TrimEnd('\0') + "; " + Marker : Marker;
        var desc = ("SNAM", Encoding.UTF8.GetBytes(description + "\0"));
        if (snam >= 0) subs[snam] = desc;
        else subs.Insert(subs.FindLastIndex(s => s.Type is "HEDR" or "CNAM") + 1, desc);
        using var header = new MemoryStream();
        foreach (var (st, d) in subs)
        {
            if (d.Length > ushort.MaxValue) throw new InvalidDataException("header field too large to rewrite");
            header.Write(Encoding.ASCII.GetBytes(st));
            header.Write(BitConverter.GetBytes((ushort)d.Length));
            header.Write(d);
        }
        var tes4 = bytes.AsSpan(0, HeaderSize).ToArray();
        BitConverter.TryWriteBytes(tes4.AsSpan(4, 4), (int)header.Length);
        return [.. tes4, .. header.ToArray()];
    }
}
