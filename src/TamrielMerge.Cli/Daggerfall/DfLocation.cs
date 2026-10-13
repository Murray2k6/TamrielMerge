using System.Text;

namespace TamrielMerge.Daggerfall;

/// <summary>Location types from MAPTABLE (Daggerfall Unity DFRegion.LocationTypes).</summary>
public enum DfLocationType
{
    TownCity = 0, TownHamlet = 1, TownVillage = 2, HomeFarms = 3, DungeonLabyrinth = 4, ReligionTemple = 5,
    Tavern = 6, DungeonKeep = 7, HomeWealthy = 8, ReligionCult = 9, DungeonRuin = 10, HomePoor = 11,
    Graveyard = 12, Coven = 13, HomeYourShips = 14,
}

public enum DfDungeonType : byte
{
    Crypt, OrcStronghold, HumanStronghold, Prison, DesecratedTemple, Mine, NaturalCave, Coven, VampireHaunt,
    Laboratory, HarpyNest, RuinedCastle, SpiderNest, GiantStronghold, DragonsDen, BarbarianStronghold,
    VolcanicCaves, ScorpionNest, Cemetery, NoDungeon = 255,
}

public sealed record DfBuilding(ushort NameSeed, ushort FactionId, short Sector, byte BuildingType, byte Quality);

public sealed record DfDungeonBlockPlacement(sbyte X, sbyte Z, string BlockName, bool IsStart);

/// <summary>A Daggerfall location from MAPS.BSA, with its exterior RMB block grid and optional RDB dungeon.</summary>
public sealed class DfLocation
{
    public required int Region { get; init; }
    public required string RegionName { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int MapId { get; init; }
    public required int LocationId { get; init; }
    public required DfLocationType Type { get; init; }
    public required DfDungeonType DungeonType { get; init; }
    public required int PixelX { get; init; }
    public required int PixelY { get; init; }
    public required int BlockWidth { get; init; }
    public required int BlockHeight { get; init; }
    /// <summary>RMB block names, row-major, BlockWidth x BlockHeight.</summary>
    public required string[] ExteriorBlocks { get; init; }
    public required IReadOnlyList<DfBuilding> Buildings { get; init; }
    public required IReadOnlyList<DfDungeonBlockPlacement> DungeonBlocks { get; init; }
    public bool IsPort { get; init; }

    public override string ToString() => $"{Name} ({Type}, {RegionName}) @({PixelX},{PixelY}) {BlockWidth}x{BlockHeight} blocks, {DungeonBlocks.Count} dungeon blocks";
}

/// <summary>
/// Reads every location in MAPS.BSA. Each of the 62 regions has four records in order: MAPPITEM (exterior
/// data), MAPDITEM (dungeons), MAPTABLE (type and coordinates) and MAPNAMES.
/// Format reference: Daggerfall Unity MapsFile.cs.
/// </summary>
public static class DfLocations
{
    public static readonly string[] RegionNames =
    [
        "Alik'r Desert", "Dragontail Mountains", "Glenpoint Foothills", "Daggerfall Bluffs",
        "Yeorth Burrowland", "Dwynnen", "Ravennian Forest", "Devilrock",
        "Malekna Forest", "Isle of Balfiera", "Bantha", "Dak'fron",
        "Islands in the Western Iliac Bay", "Tamarilyn Point", "Lainlyn Cliffs", "Bjoulsae River",
        "Wrothgarian Mountains", "Daggerfall", "Glenpoint", "Betony", "Sentinel", "Anticlere", "Lainlyn", "Wayrest",
        "Gen Tem High Rock village", "Gen Rai Hammerfell village", "Orsinium Area", "Skeffington Wood",
        "Hammerfell bay coast", "Hammerfell sea coast", "High Rock bay coast", "High Rock sea coast",
        "Northmoor", "Menevia", "Alcaire", "Koegria", "Bhoriane", "Kambria", "Phrygias", "Urvaius",
        "Ykalon", "Daenia", "Shalgora", "Abibon-Gora", "Kairou", "Pothago", "Myrkwasa", "Ayasofya",
        "Tigonus", "Kozanset", "Satakalaam", "Totambu", "Mournoth", "Ephesus", "Santaki", "Antiphyllos",
        "Bergama", "Gavaudon", "Tulune", "Glenumbra Moors", "Ilessan Hills", "Cybiades",
    ];

    static readonly string[] RmbPrefixes =
    [
        "TVRN", "GENR", "RESI", "WEAP", "ARMR", "ALCH", "BANK", "BOOK",
        "CLOT", "FURN", "GEMS", "LIBR", "PAWN", "TEMP", "TEMP", "PALA",
        "FARM", "DUNG", "CAST", "MANR", "SHRI", "RUIN", "SHCK", "GRVE",
        "FILL", "KRAV", "KDRA", "KOWL", "KMOO", "KCAN", "KFLA", "KHOR",
        "KROS", "KWHE", "KSCA", "KHAW", "MAGE", "THIE", "DARK", "FIGH",
        "CUST", "WALL", "MARK", "SHIP", "WITC",
    ];
    static readonly string[] Letter2 = ["A", "L", "M", "S"];
    static readonly string[] RdbLetters = ["N", "W", "L", "S", "B", "M"];

    public static List<DfLocation> LoadAll(string arena2)
    {
        var bsa = new DfBsa(Path.Combine(arena2, "MAPS.BSA"));
        var all = new List<DfLocation>();
        for (int region = 0; region < RegionNames.Length; region++)
        {
            var pitem = bsa.Record(region * 4).ToArray();
            var ditem = bsa.Record(region * 4 + 1).ToArray();
            var table = bsa.Record(region * 4 + 2).ToArray();
            var names = bsa.Record(region * 4 + 3).ToArray();
            if (names.Length == 0 || table.Length == 0 || pitem.Length == 0) continue;
            var dungeons = ReadDungeons(ditem);

            int count = (int)BitConverter.ToUInt32(names, 0);
            for (int i = 0; i < count; i++)
            {
                string name = CString(names, 4 + i * 32, 32);
                int t = i * 17;
                int mapId = BitConverter.ToInt32(table, t);
                uint bits = BitConverter.ToUInt32(table, t + 4);
                int longitude = (int)(bits & 0x1FFFFFF) >> 8;
                var type = (DfLocationType)((4 * bits) >> 27);
                int latitude = (BitConverter.ToInt32(table, t + 8) & 0xFFFFFF) >> 8;
                var dungeonType = (DfDungeonType)table[t + 12];

                using var r = new BinaryReader(new MemoryStream(pitem));
                r.BaseStream.Position = i * 4;
                r.BaseStream.Position = count * 4 + r.ReadUInt32();
                int locationId = ReadRecordElementHeader(r);
                int buildingCount = r.ReadUInt16();
                r.ReadBytes(5);
                var buildings = new List<DfBuilding>(buildingCount);
                for (int b = 0; b < buildingCount; b++)
                {
                    ushort seed = r.ReadUInt16(); r.ReadUInt32(); r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); r.ReadUInt32();
                    ushort faction = r.ReadUInt16(); short sector = r.ReadInt16(); r.ReadUInt16();
                    byte btype = r.ReadByte(), quality = r.ReadByte();
                    buildings.Add(new DfBuilding(seed, faction, sector, btype, quality));
                }
                r.ReadBytes(32); // AnotherName
                r.ReadInt32(); r.ReadUInt32();
                int w = r.ReadByte(), h = r.ReadByte();
                r.ReadBytes(4);
                byte letter1 = r.ReadByte(), port = r.ReadByte();
                r.ReadByte();
                byte[] bIndex = r.ReadBytes(64), bNumber = r.ReadBytes(64), bChar = r.ReadBytes(64);
                var blocks = new string[w * h];
                for (int b = 0; b < blocks.Length; b++)
                    blocks[b] = RmbName(bIndex[b], bNumber[b], bChar[b], letter1);

                all.Add(new DfLocation
                {
                    Region = region,
                    RegionName = RegionNames[region],
                    Index = i,
                    Name = name,
                    MapId = mapId,
                    LocationId = locationId,
                    Type = type,
                    DungeonType = dungeonType,
                    PixelX = longitude / 128,
                    PixelY = 499 - latitude / 128,
                    BlockWidth = w,
                    BlockHeight = h,
                    ExteriorBlocks = blocks,
                    Buildings = buildings,
                    DungeonBlocks = dungeons.TryGetValue(locationId, out var dg) ? dg : [],
                    IsPort = (port & 1) != 0,
                });
            }
        }
        return all;
    }

    /// <summary>Skips the door list and record header, returning the LocationId.</summary>
    static int ReadRecordElementHeader(BinaryReader r)
    {
        uint doors = r.ReadUInt32();
        r.BaseStream.Position += doors * 6;
        r.BaseStream.Position += 4 + 2 + 1 + 4 + 4 + 4 + 2 + 2 + 4 + 4 + 2; // through AlwaysOne2
        int locationId = r.ReadUInt16();
        r.BaseStream.Position += 4 + 2 + 4 + 26 + 32 + 9;
        return locationId;
    }

    static Dictionary<int, List<DfDungeonBlockPlacement>> ReadDungeons(byte[] d)
    {
        var result = new Dictionary<int, List<DfDungeonBlockPlacement>>();
        if (d.Length == 0) return result;
        int count = (int)BitConverter.ToUInt32(d, 0);
        for (int i = 0; i < count; i++)
        {
            uint offset = BitConverter.ToUInt32(d, 4 + i * 8);
            int exteriorId = BitConverter.ToUInt16(d, 4 + i * 8 + 6);
            using var r = new BinaryReader(new MemoryStream(d));
            r.BaseStream.Position = 4 + count * 8 + offset;
            int locationId = ReadRecordElementHeader(r);
            r.ReadUInt16(); r.ReadUInt32(); r.ReadUInt32();
            int blockCount = r.ReadUInt16();
            r.ReadBytes(5);
            var blocks = new List<DfDungeonBlockPlacement>(blockCount);
            for (int b = 0; b < blockCount; b++)
            {
                sbyte x = r.ReadSByte(), z = r.ReadSByte();
                ushort bits = r.ReadUInt16();
                int number = bits & 0x3ff, index = bits >> 11;
                blocks.Add(new DfDungeonBlockPlacement(x, z, $"{RdbLetters[index]}{number:0000000}.RDB", (bits & 0x400) != 0));
            }
            // Orsinium: classic moves one overlapping border block (from Daggerfall Unity).
            if (locationId == 50015 && blocks.Count > 13) blocks[13] = blocks[13] with { Z = -2 };
            result[exteriorId] = blocks;
        }
        return result;
    }

    static string RmbName(byte blockIndex, byte blockNumber, byte blockCharacter, byte letter1Ascii)
    {
        string letter1 = (blockCharacter & 0x10) != 0 ? ((char)letter1Ascii).ToString() : "A";
        string letter2 = Letter2[(byte)(2 * blockCharacter) >> 6];
        string numbers = blockIndex is 13 or 14
            ? (char)((blockCharacter & 0xF) + 65) + blockNumber.ToString()
            : blockNumber.ToString("00");
        return RmbPrefixes[blockIndex] + letter1 + letter2 + numbers + ".RMB";
    }

    static string CString(byte[] d, int offset, int max)
    {
        int end = Array.IndexOf(d, (byte)0, offset, max);
        return Encoding.Latin1.GetString(d, offset, (end < 0 ? offset + max : end) - offset);
    }
}
