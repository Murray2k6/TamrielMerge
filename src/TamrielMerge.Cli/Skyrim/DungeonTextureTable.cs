using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's dungeon texture swaps (Daggerfall Unity, MIT: DungeonTextureTables.RandomTextureTableClassic and
/// ApplyTextureTable, in its default mode, RandomDungeonTextures=0, the classic algorithm). Dungeon blocks are made with
/// texture archives 119, 120, 122, 123, 124 and 168; each dungeon wears others in their place, rolled from its location's
/// ID in its climate's family (19, 119, 319 or 419 and up), the sewer slot a sewer archive of the climate; archive 74
/// takes the climate's step. Until 2026-11-03 every dungeon wore the blocks' own (user: "fix the wall textures in some of
/// the dungeons").
/// </summary>
public static class DungeonTextureTable
{
    /// <summary>The archives the blocks are made with, the slots a table fills.</summary>
    public static readonly int[] Default = [119, 120, 122, 123, 124, 168];

    // Daggerfall Unity TravelTimeCalculator.climateIndices, by climate - Ocean.
    static readonly byte[] ClimateIndices = [0, 0, 0, 1, 2, 3, 4, 5, 5, 5];
    static readonly byte[] ArchiveIndices = [0, 0, 1, 4, 4, 0, 3, 3, 3, 0];
    // Classic's values (index 4 = 119 too).
    static readonly short[] ClimateArchives = [19, 119, 319, 419, 119];
    const int Sewer = 68;   // DFLocation.ClimateTextureSet.Interior_Sewer

    /// <summary>A dungeon's table, from its location's ID (the dungeon record's LocationId, the seed) and the climate of
    /// its map pixel.</summary>
    public static int[] Classic(int locationId, DfClimate climate)
    {
        int c = (int)climate;
        if (c is < (int)DfClimate.Ocean or > (int)DfClimate.HauntedWoodlands) c = (int)DfClimate.Woodlands;
        if (c == (int)DfClimate.Ocean) c = (int)DfClimate.Swamp;
        int family = ArchiveIndices[ClimateIndices[c - (int)DfClimate.Ocean]];
        var random = new DfRandom((uint)locationId);
        var table = new int[Default.Length];
        // Classic skips this loop in the rainforest (family 1), leaving the last dungeon's table: Daggerfall Unity takes
        // that for a bug and fills it, as here.
        for (int i = 0; i < 5; i++)
        {
            int offset = (int)random.Rand() % 5;
            if (offset == 2) offset = 4;   // no archive x21 in these families
            table[i] = ClimateArchives[family] + offset;
        }
        table[5] = Sewer + 100 * ArchiveIndices[c - (int)DfClimate.Desert];
        return table;
    }

    /// <summary>The climate's texture step (Daggerfall Unity MapsFile.GetWorldClimateSettings' ClimateType: desert 0,
    /// mountain 100, temperate 300, swamp 400).</summary>
    public static int ClimateBase(DfClimate climate) => climate switch
    {
        DfClimate.Desert or DfClimate.Desert2 or DfClimate.Subtropical => 0,
        DfClimate.Mountain => 100,
        DfClimate.Ocean or DfClimate.Rainforest or DfClimate.Swamp => 400,
        _ => 300,
    };

    /// <summary>The archive a block's texture archive becomes in a dungeon with this table and climate step.</summary>
    public static int Apply(int archive, int[] table, int climateBase) => archive switch
    {
        74 => 74 + climateBase,
        119 => table[0],
        120 => table[1],
        122 => table[2],
        123 => table[3],
        124 => table[4],
        168 => table[5],
        _ => archive,
    };
}
