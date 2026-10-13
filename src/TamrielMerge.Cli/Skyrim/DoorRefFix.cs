using System.Numerics;
using System.Text;
using Mutagen.Bethesda.Plugins;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Brings the door references of Daggerfall plugins built before 2026-10-10 in line with the converter (user: "some
/// doors are too small for npcs too", "the door frames dont match some of the doors"), so they needn't be rebuilt:
/// dungeon doors (<see cref="DoorPlacer"/>) stood their model's origin, not its floor, on Daggerfall's floor (the single
/// door sunk 36 x scale, the double raised 4 x scale), and town doors (DungeonCellBuilder.SurfaceDoor) were turned as
/// if every style's door faced along local -Y. Only each reference's DATA (position, rotation) changes, in place, the
/// rest byte for byte as <see cref="PluginMerger"/> handles parts; the header gets <see cref="Marker"/> in its
/// description so a plugin is never fixed twice. Plugins built since carry the marker from the start.
/// </summary>
public static class DoorRefFix
{
    public const string Marker = "TamrielMerge doors 2026-10-10";
    const int HeaderSize = 24;
    const uint Compressed = 0x00040000;

    sealed record Plugin(string Self, List<string> Masters, bool Fixed);

    /// <summary>Fixes each of <paramref name="paths"/>; <paramref name="others"/> are plugins whose door activators they
    /// may use (the main Daggerfall.esm for its parts). Returns a line per plugin.</summary>
    public static IEnumerable<string> Fix(IReadOnlyList<string> paths, IReadOnlyList<string> others, PropReplacer props)
    {
        // Every street and exit door activator (DF{Street,Exit}Door{Style}) by (plugin, local FormID).
        var styleOf = new Dictionary<(string, uint), TownStyle>();
        foreach (var p in paths.Concat(others).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var plugin = Open(p);
            var bytes = File.ReadAllBytes(p);
            foreach (var (offset, type, formId) in Records(bytes))
            {
                if (type != "ACTI" || (BitConverter.ToUInt32(bytes, (int)offset + 8) & Compressed) != 0) continue;
                int size = BitConverter.ToInt32(bytes, (int)offset + 4);
                var edid = Subrecords(bytes, (int)offset + HeaderSize, size).Where(s => s.Type == "EDID")
                    .Select(s => Encoding.ASCII.GetString(bytes, s.At, s.Length).TrimEnd('\0')).FirstOrDefault();
                foreach (var style in Enum.GetValues<TownStyle>())
                    if (edid == $"DFStreetDoor{style}" || edid == $"DFExitDoor{style}") styleOf[Key(plugin, formId)] = style;
            }
        }
        string skyrim = ModKey.FromFileName("Skyrim.esm").FileName;
        foreach (var path in paths)
        {
            var plugin = Open(path);
            if (plugin.Fixed) { yield return $"{Path.GetFileName(path)}: already fixed"; continue; }
            var bytes = File.ReadAllBytes(path);
            int single = 0, dbl = 0, town = 0;
            foreach (var (offset, type, _) in Records(bytes))
            {
                if (type != "REFR" || (BitConverter.ToUInt32(bytes, (int)offset + 8) & Compressed) != 0) continue;
                int size = BitConverter.ToInt32(bytes, (int)offset + 4);
                uint? baseId = null; float scale = 1; int dataAt = -1;
                foreach (var (st, at, len) in Subrecords(bytes, (int)offset + HeaderSize, size))
                {
                    if (st == "NAME") baseId = BitConverter.ToUInt32(bytes, at);
                    else if (st == "XSCL") scale = BitConverter.ToSingle(bytes, at);
                    else if (st == "DATA" && len == 24) dataAt = at;
                }
                if (baseId is null || dataAt < 0) continue;
                var (file, local) = Key(plugin, baseId.Value);
                var pos = new Vector3(BitConverter.ToSingle(bytes, dataAt), BitConverter.ToSingle(bytes, dataAt + 4), BitConverter.ToSingle(bytes, dataAt + 8));
                float heading = BitConverter.ToSingle(bytes, dataAt + 20);
                if (string.Equals(file, skyrim, StringComparison.OrdinalIgnoreCase) && local is 0x4DFD9 or 0x439C2)
                {
                    pos.Z -= (local == 0x4DFD9 ? DoorPlacer.SingleBottom : DoorPlacer.DoubleBottom) * scale;
                    if (local == 0x4DFD9) single++; else dbl++;
                }
                else if (styleOf.TryGetValue((file, local), out var style) && DungeonCellBuilder.FrontTurn(TownBuilder.StyleDoor(style)) is var turn && turn != 0)
                {
                    // SurfaceDoor centres the door's bounds on the wall: position = wall - R(h) (centre x scale).
                    var (min, max, _) = props.SkyrimShape(TownBuilder.StyleDoor(style));
                    var c = new Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2) * scale;
                    float newHeading = heading - turn;
                    var shift = Turn(c, heading) - Turn(c, newHeading);
                    pos += new Vector3(shift, 0);
                    heading = newHeading;
                    town++;
                }
                else continue;
                BitConverter.TryWriteBytes(bytes.AsSpan(dataAt, 4), pos.X);
                BitConverter.TryWriteBytes(bytes.AsSpan(dataAt + 4, 4), pos.Y);
                BitConverter.TryWriteBytes(bytes.AsSpan(dataAt + 8, 4), pos.Z);
                BitConverter.TryWriteBytes(bytes.AsSpan(dataAt + 20, 4), heading);
            }
            // The marker: added to (or as) the header's description.
            int tes4Size = BitConverter.ToInt32(bytes, 4);
            var subs = Subrecords(bytes, HeaderSize, tes4Size).Select(s => (s.Type, Data: bytes.AsSpan(s.At, s.Length).ToArray())).ToList();
            if (subs.Any(s => s.Data.Length > ushort.MaxValue)) throw new InvalidDataException($"{path}: header field too large to rewrite");
            int snam = subs.FindIndex(s => s.Type == "SNAM");
            string description = snam >= 0 ? Encoding.UTF8.GetString(subs[snam].Data).TrimEnd('\0') + "; " + Marker : Marker;
            var desc = ("SNAM", Encoding.UTF8.GetBytes(description + "\0"));
            if (snam >= 0) subs[snam] = desc;
            else subs.Insert(subs.FindLastIndex(s => s.Type is "HEDR" or "CNAM") + 1, desc);
            using var header = new MemoryStream();
            foreach (var (st, d) in subs)
            {
                header.Write(Encoding.ASCII.GetBytes(st));
                header.Write(BitConverter.GetBytes((ushort)d.Length));
                header.Write(d);
            }
            var tes4 = bytes.AsSpan(0, HeaderSize).ToArray();
            BitConverter.TryWriteBytes(tes4.AsSpan(4, 4), (int)header.Length);
            string tmp = path + ".tmp";
            using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                o.Write(tes4);
                header.Position = 0;
                header.CopyTo(o);
                o.Write(bytes, HeaderSize + tes4Size, bytes.Length - HeaderSize - tes4Size);
            }
            File.Move(tmp, path, overwrite: true);
            yield return $"{Path.GetFileName(path)}: {single} single and {dbl} double dungeon doors seated on the floor, {town} town doors turned to face out";
        }
    }

    /// <summary>Local (x, y) turned by heading h as Skyrim does: (x cos h + y sin h, -x sin h + y cos h).</summary>
    static Vector2 Turn(Vector2 v, float h) => new(v.X * MathF.Cos(h) + v.Y * MathF.Sin(h), -v.X * MathF.Sin(h) + v.Y * MathF.Cos(h));

    static (string, uint) Key(Plugin p, uint formId)
    {
        int index = (int)(formId >> 24);
        return (index < p.Masters.Count ? p.Masters[index] : p.Self, formId & 0xFFFFFF);
    }

    static Plugin Open(string path)
    {
        using var s = File.OpenRead(path);
        var h = new byte[HeaderSize];
        s.ReadExactly(h);
        if (Encoding.ASCII.GetString(h, 0, 4) != "TES4") throw new InvalidDataException($"{path}: not a Skyrim plugin");
        var data = new byte[BitConverter.ToInt32(h, 4)];
        s.ReadExactly(data);
        var subs = Subrecords(data, 0, data.Length).ToList();
        bool done = subs.Any(x => x.Type == "SNAM" && Encoding.UTF8.GetString(data, x.At, x.Length).Contains(Marker));
        var masters = subs.Where(x => x.Type == "MAST").Select(x => Encoding.ASCII.GetString(data, x.At, x.Length).TrimEnd('\0')).ToList();
        return new Plugin(Path.GetFileName(path), masters, done);
    }

    /// <summary>A record's fields (type, where its data starts, its length) in data[start .. start + size].</summary>
    internal static IEnumerable<(string Type, int At, int Length)> Subrecords(byte[] data, int start, int size)
    {
        for (int p = start, end = start + size; p + 6 <= end;)
        {
            string type = Encoding.ASCII.GetString(data, p, 4);
            int len = BitConverter.ToUInt16(data, p + 4);
            if (type == "XXXX")
            {
                int big = BitConverter.ToInt32(data, p + 6);
                p += 10;
                yield return (Encoding.ASCII.GetString(data, p, 4), p + 6, big);
                p += 6 + big;
                continue;
            }
            yield return (type, p + 6, len);
            p += 6 + len;
        }
    }

    /// <summary>Every record after the header: offset, type and FormID (groups are stepped into).</summary>
    static IEnumerable<(long Offset, string Type, uint FormId)> Records(byte[] bytes)
    {
        long p = HeaderSize + BitConverter.ToInt32(bytes, 4);
        while (p + HeaderSize <= bytes.Length)
        {
            string type = Encoding.ASCII.GetString(bytes, (int)p, 4);
            if (type == "GRUP") { p += HeaderSize; continue; }
            yield return (p, type, BitConverter.ToUInt32(bytes, (int)p + 12));
            p += HeaderSize + BitConverter.ToUInt32(bytes, (int)p + 4);
        }
    }
}
