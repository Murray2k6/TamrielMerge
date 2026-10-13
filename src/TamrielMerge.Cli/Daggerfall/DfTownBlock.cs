using System.Numerics;
using System.Text;

namespace TamrielMerge.Daggerfall;

/// <summary>A 3D model placed in an RMB block (a building's shell or an attachment, a prop, an interior wall).</summary>
public sealed record DfRmbModel(uint ModelId, byte ObjectType, Vector3 Position, int YRotation);

/// <summary>
/// A flat (billboard) in an RMB block: scenery, a light, a marker, or (FactionId != 0, or archive 182-184/334/357
/// people sprites) a person. Flags bit 0x20 marks a female (Daggerfall Unity StaticNPC).
/// </summary>
public sealed record DfRmbFlat(Vector3 Position, int TextureArchive, int TextureRecord, short FactionId, byte Flags, int RecordOffset);

/// <summary>A door in a building's interior or exterior: the spot the player passes through.</summary>
public sealed record DfRmbDoor(Vector3 Position, short YRotation, short OpenRotation, byte ModelIndex);

/// <summary>One side (exterior or interior) of a building record.</summary>
public sealed record DfRmbSide(IReadOnlyList<DfRmbModel> Models, IReadOnlyList<DfRmbFlat> Flats, IReadOnlyList<DfRmbFlat> People, IReadOnlyList<DfRmbDoor> Doors);

/// <summary>The data Daggerfall keeps about a building (also repeated per location in MAPS.BSA).</summary>
public sealed record DfBuildingData(ushort NameSeed, ushort FactionId, short Sector, ushort LocationId, DfBuildingType Type, byte Quality);

/// <summary>A building record: where it stands in the block, its shell outside and its rooms inside.</summary>
public sealed record DfRmbBuilding(int Index, int XPos, int ZPos, int YRotation, DfBuildingData Data, DfRmbSide Exterior, DfRmbSide Interior);

/// <summary>Building types (Daggerfall Unity DFLocation.BuildingTypes).</summary>
public enum DfBuildingType : byte
{
    Alchemist = 0, HouseForSale = 1, Armorer = 2, Bank = 3, Town4 = 4, Bookseller = 5, ClothingStore = 6, FurnitureStore = 7,
    GemStore = 8, GeneralStore = 9, Library = 10, GuildHall = 11, PawnShop = 12, WeaponSmith = 13, Temple = 14, Tavern = 15,
    Palace = 16, House1 = 17, House2 = 18, House3 = 19, House4 = 20, House5 = 21, House6 = 22, Town23 = 23, Ship = 24,
    Special1 = 0x74, Special2 = 0xDF, Special3 = 0xF9, Special4 = 0xFA, None = 0xFF,
}

/// <summary>
/// An RMB block from BLOCKS.BSA: a 4096-unit square of a town (or a farm, a graveyard, a castle grounds), holding
/// up to 32 building records (each an exterior shell plus its interior), the block's loose props and flats, and a
/// 16 x 16 grid of ground tiles. Format reference: Daggerfall Unity BlocksFile.cs (RMB readers, MIT).
/// Positions are raw Daggerfall units (Y down), as stored.
/// </summary>
public sealed class DfTownBlock
{
    /// <summary>Side of an RMB block in Daggerfall units.</summary>
    public const float Side = 4096f;
    /// <summary>Daggerfall rotation units per degree.</summary>
    public const float RotationDivisor = 2048f / 360f;

    public required string Name { get; init; }
    public required IReadOnlyList<DfRmbBuilding> Buildings { get; init; }
    public required IReadOnlyList<DfRmbModel> MiscModels { get; init; }
    public required IReadOnlyList<DfRmbFlat> MiscFlats { get; init; }
    /// <summary>Ground tiles [x, y]: bits 0-5 texture record, 0x40 rotated, 0x80 flipped.</summary>
    public required byte[,] GroundTiles { get; init; }
    /// <summary>Ground scenery [x, y]: 255 none, else (value / 4 - 1) is the nature flat's record.</summary>
    public required byte[,] GroundScenery { get; init; }
    /// <summary>Daggerfall's 64 x 64 automap of the block (non-zero = building, by building type), row 0 first as stored.</summary>
    public byte[] AutoMap { get; init; } = [];
    /// <summary>Bytes left after the last record (a parse check: should be 0 or a little padding).</summary>
    public int Unread { get; init; }

    public static DfTownBlock Load(DfBsa blocks, string name)
    {
        var d = blocks.Record(name);
        int p = 0;
        int nBuildings = d[p++], nMiscModels = d[p++], nMiscFlats = d[p++];
        var positions = new (int X, int Z, int Rot)[32];
        for (int i = 0; i < 32; i++, p += 20)
            positions[i] = (BitConverter.ToInt32(d, p + 8), BitConverter.ToInt32(d, p + 12), BitConverter.ToInt32(d, p + 16));
        var data = new DfBuildingData[32];
        for (int i = 0; i < 32; i++, p += 26)
            data[i] = new DfBuildingData(BitConverter.ToUInt16(d, p), BitConverter.ToUInt16(d, p + 18), BitConverter.ToInt16(d, p + 20),
                BitConverter.ToUInt16(d, p + 22), (DfBuildingType)d[p + 24], d[p + 25]);
        p += 32 * 4;   // unknown section 2
        var sizes = new int[32];
        for (int i = 0; i < 32; i++, p += 4) sizes[i] = BitConverter.ToInt32(d, p);
        p += 8;        // ground header
        var tiles = new byte[16, 16];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) tiles[x, y] = d[p++];
        var scenery = new byte[16, 16];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) scenery[x, y] = d[p++];
        var automap = d.AsSpan(p, 64 * 64).ToArray();
        p += 64 * 64;
        p += 13 * 33;  // block name + 32 other names

        var buildings = new List<DfRmbBuilding>(nBuildings);
        for (int i = 0; i < nBuildings; i++)
        {
            int q = p;
            var exterior = ReadSide(d, ref q);
            var interior = ReadSide(d, ref q);
            buildings.Add(new DfRmbBuilding(i, positions[i].X, positions[i].Z, positions[i].Rot, data[i], exterior, interior));
            p += sizes[i];   // the stored size steps over padding
        }
        var misc = new List<DfRmbModel>(nMiscModels);
        for (int i = 0; i < nMiscModels; i++) misc.Add(ReadModel(d, ref p));
        var miscFlats = new List<DfRmbFlat>(nMiscFlats);
        for (int i = 0; i < nMiscFlats; i++) miscFlats.Add(ReadFlat(d, ref p));
        return new DfTownBlock { Name = name, Buildings = buildings, MiscModels = misc, MiscFlats = miscFlats, GroundTiles = tiles, GroundScenery = scenery, AutoMap = automap, Unread = d.Length - p };
    }

    static DfRmbSide ReadSide(byte[] d, ref int p)
    {
        int nModels = d[p], nFlats = d[p + 1], nSection3 = d[p + 2], nPeople = d[p + 3], nDoors = d[p + 4];
        p += 5 + 12;
        var models = new List<DfRmbModel>(nModels);
        for (int i = 0; i < nModels; i++) models.Add(ReadModel(d, ref p));
        var flats = new List<DfRmbFlat>(nFlats);
        for (int i = 0; i < nFlats; i++) flats.Add(ReadFlat(d, ref p));
        p += nSection3 * 16;
        var people = new List<DfRmbFlat>(nPeople);
        for (int i = 0; i < nPeople; i++) people.Add(ReadFlat(d, ref p));
        var doors = new List<DfRmbDoor>(nDoors);
        for (int i = 0; i < nDoors; i++, p += 19)
            doors.Add(new DfRmbDoor(new Vector3(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt32(d, p + 8)),
                BitConverter.ToInt16(d, p + 12), BitConverter.ToInt16(d, p + 14), d[p + 16]));
        return new DfRmbSide(models, flats, people, doors);
    }

    static DfRmbModel ReadModel(byte[] d, ref int p)
    {
        // Id1 * 100 + Id2 is the model id; position is the second xyz triple; only a Y rotation.
        uint id = (uint)(BitConverter.ToInt16(d, p) * 100 + d[p + 2]);
        byte type = d[p + 3];
        var pos = new Vector3(BitConverter.ToInt32(d, p + 36), BitConverter.ToInt32(d, p + 40), BitConverter.ToInt32(d, p + 44));
        int rot = BitConverter.ToInt16(d, p + 52);
        p += 66;
        return new DfRmbModel(id, type, pos, rot);
    }

    static DfRmbFlat ReadFlat(byte[] d, ref int p)
    {
        ushort tex = BitConverter.ToUInt16(d, p + 12);
        var flat = new DfRmbFlat(new Vector3(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt32(d, p + 8)),
            tex >> 7, tex & 0x7f, BitConverter.ToInt16(d, p + 14), d[p + 16], p);
        p += 17;
        return flat;
    }

    /// <summary>
    /// A block name the location table gives that BLOCKS.BSA lacks is resolved the way Daggerfall Unity does: a block
    /// with the same 4-letter prefix and the same last six characters.
    /// </summary>
    public static string? Resolve(DfBsa blocks, string name)
    {
        if (blocks.Contains(name)) return name;
        string prefix = name[..4], suffix = name[^6..];
        return blocks.Entries.LastOrDefault(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))?.Name;
    }
}
