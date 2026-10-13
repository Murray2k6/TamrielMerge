using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall sprites Skyrim has no model for (user 2026-10-07: "make the seagulls, palm trees etc that are missing",
/// AI-generated from the sprites): each becomes a 3D model made from its own sprite by TripoSR
/// (tools/model3d/sprite2mesh.py, on the GPU), then a NIF and a static here. <see cref="Targets"/> lists them;
/// <c>sprite-models --export</c> writes the sprites and the job list, <c>--import</c> turns the results into NIFs.
/// </summary>
public static class SpriteModels
{
    /// <summary>What a sprite is, for its placement: standing, hanging, or a tree (no collision above the trunk).</summary>
    public enum Kind { Prop, Plant, Tree, Hanging }

    /// <summary>How a model moves: not at all, or a creature idling on a perch (the part above the perch is cut free
    /// and given a looping idle in the NIF: a breathing bob, looking about, a dip of the head).</summary>
    public enum Motion { None, Perched }

    /// <param name="Whiten">How much to lift the sprite's pale, unsaturated colours (Daggerfall's palette is dim and its
    /// lighting lifts it; a seagull is white): 1 leaves them, 2 doubles them. Saturated colours are left alone.</param>
    public sealed record Target(int Archive, int Record, string Name, Kind Kind, Motion Motion = Motion.None, float Whiten = 1);

    static IEnumerable<Target> Many(int archive, Kind kind, string name, params int[] records) => records.Select(r => new Target(archive, r, $"{name}{archive}_{r}", kind));

    /// <summary>The sprites to model, by eye from the contact sheets (out/flats).</summary>
    public static readonly IReadOnlyList<Target> Targets =
    [
        new(201, 11, "Seagull201_11", Kind.Prop, Motion.Perched, Whiten: 1.7f),
        // Rainforest (500), subtropical (501), swamp (502): palms, bulb plants, coral, glowing orbs, coiled stalks.
        .. Many(500, Kind.Tree, "Palm", 12, 13, 14, 15, 16, 21, 27),
        .. Many(500, Kind.Plant, "Jungle", 5, 11, 23, 24, 26, 28, 29),
        .. Many(501, Kind.Tree, "Palm", 1, 11, 12, 13, 16, 27, 28, 30, 31),
        .. Many(501, Kind.Plant, "Jungle", 2, 14, 18, 21, 29),
        .. Many(501, Kind.Prop, "Toadstool", 22),
        .. Many(502, Kind.Plant, "Swamp", 1, 2, 14, 21, 22, 23, 26, 31),
        // Desert (503): cacti, yucca, agave, succulents.
        .. Many(503, Kind.Plant, "Cactus", 1, 13, 14, 15, 16, 17, 25, 26, 27),
        // Giant mushrooms (504, 505, 508, 509).
        .. Many(504, Kind.Tree, "Mushroom", 23),
        .. Many(505, Kind.Tree, "Mushroom", 23),
        .. Many(508, Kind.Tree, "Mushroom", 22, 23),
        .. Many(509, Kind.Tree, "Mushroom", 22, 23),
        // Statues of monsters Skyrim has none of (rat, spiders).
        .. Many(98, Kind.Prop, "Statue", 10, 11, 13),
        // Misc (211): rags, tripods, scarecrow, rocking horse, noose, scales, pipes, iron maidens, pillory, crosses,
        // model houses, banners, bell, necklace, goblet, sceptre, portraits, scarab, chest, idol, painting.
        .. Many(211, Kind.Prop, "Misc", 0, 3, 10, 12, 13, 14, 20, 21, 23, 24, 25, 26, 27, 28, 30, 32, 37, 38, 39, 41, 42, 47, 48, 49, 50, 51, 52, 53, 54, 55, 57),
        .. Many(211, Kind.Hanging, "Misc", 22, 43, 44, 45, 46),
        // Scholarly things (208): globe, magnifier, scales, telescope, mirror, hourglass.
        .. Many(208, Kind.Prop, "Study", 0, 1, 3, 4, 5, 6),
        // Tools (214) the clutter table left out.
        .. Many(214, Kind.Prop, "Tool", 4, 5, 8, 9, 11, 14, 15),
        // Every other piece of scenery the clutter table stood a Skyrim static, tree or movable static in for (user,
        // 2026-10-12: clutter - "Models from DF sprites"): statues, furnishings, columns, remains, outdoor things,
        // stalactites, crops and every climate's nature (500-511). Generated from `clutter-table`; items, containers,
        // potions and lights keep their Skyrim objects (they are used, not only seen). The nature archives' grass (records
        // 7 and 8) are crossed sprites instead, as Skyrim's grass (Terrain/DfGrass.AddBlockGrass).
        new(97, 0, "Scenery97_0", Kind.Prop),   // statue
        new(97, 1, "Scenery97_1", Kind.Prop),   // statue
        new(97, 2, "Scenery97_2", Kind.Prop),   // statue
        new(97, 3, "Scenery97_3", Kind.Prop),   // statue
        new(97, 4, "Scenery97_4", Kind.Prop),   // statue
        new(97, 5, "Scenery97_5", Kind.Prop),   // statue
        new(97, 6, "Scenery97_6", Kind.Prop),   // statue
        new(97, 7, "Scenery97_7", Kind.Prop),   // statue
        new(97, 8, "Scenery97_8", Kind.Prop),   // statue
        new(97, 9, "Scenery97_9", Kind.Prop),   // statue
        new(97, 10, "Scenery97_10", Kind.Prop),   // statue
        new(97, 11, "Scenery97_11", Kind.Prop),   // statue
        new(97, 12, "Scenery97_12", Kind.Prop),   // statue
        new(97, 13, "Scenery97_13", Kind.Prop),   // statue
        new(97, 14, "Scenery97_14", Kind.Prop),   // statue
        new(97, 15, "Scenery97_15", Kind.Prop),   // statue
        new(97, 16, "Scenery97_16", Kind.Prop),   // statue
        new(97, 17, "Scenery97_17", Kind.Prop),   // statue
        new(97, 18, "Scenery97_18", Kind.Prop),   // statue
        new(97, 19, "Scenery97_19", Kind.Prop),   // statue
        new(97, 20, "Scenery97_20", Kind.Prop),   // statue
        new(97, 21, "Scenery97_21", Kind.Prop),   // statue
        new(98, 0, "Scenery98_0", Kind.Prop),   // monster statue
        new(98, 1, "Scenery98_1", Kind.Prop),   // monster statue
        new(98, 2, "Scenery98_2", Kind.Prop),   // monster statue
        new(98, 3, "Scenery98_3", Kind.Prop),   // monster statue
        new(98, 4, "Scenery98_4", Kind.Prop),   // monster statue
        new(98, 5, "Scenery98_5", Kind.Prop),   // monster statue
        new(98, 6, "Scenery98_6", Kind.Prop),   // monster statue
        new(98, 7, "Scenery98_7", Kind.Prop),   // monster statue
        new(98, 8, "Scenery98_8", Kind.Prop),   // monster statue
        new(98, 9, "Scenery98_9", Kind.Prop),   // monster statue
        new(98, 12, "Scenery98_12", Kind.Prop),   // monster statue
        new(98, 14, "Scenery98_14", Kind.Prop),   // monster statue
        new(100, 2, "Scenery100_2", Kind.Prop),   // arm bones
        new(100, 3, "Scenery100_3", Kind.Prop),   // leg bone
        new(100, 4, "Scenery100_4", Kind.Prop),   // cage
        new(100, 5, "Scenery100_5", Kind.Prop),   // chain
        new(100, 6, "Scenery100_6", Kind.Prop),   // chain
        new(100, 7, "Scenery100_7", Kind.Prop),   // chain
        new(100, 8, "Scenery100_8", Kind.Prop),   // chain
        new(100, 9, "Scenery100_9", Kind.Prop),   // horned skull
        new(100, 10, "Scenery100_10", Kind.Prop),   // remains
        new(100, 11, "Scenery100_11", Kind.Prop),   // impaled remains
        new(100, 12, "Scenery100_12", Kind.Prop),   // impaled remains
        new(100, 13, "Scenery100_13", Kind.Prop),   // post
        new(100, 14, "Scenery100_14", Kind.Prop),   // post
        new(100, 15, "Scenery100_15", Kind.Prop),   // post
        new(100, 16, "Scenery100_16", Kind.Prop),   // post
        new(100, 17, "Scenery100_17", Kind.Prop),   // bones
        new(100, 18, "Scenery100_18", Kind.Prop),   // ribcage
        new(100, 19, "Scenery100_19", Kind.Hanging),   // hanging roots
        new(100, 20, "Scenery100_20", Kind.Hanging),   // hanging roots
        new(100, 21, "Scenery100_21", Kind.Hanging),   // hanging roots
        new(100, 22, "Scenery100_22", Kind.Hanging),   // hanging roots
        new(100, 23, "Scenery100_23", Kind.Prop),   // skull
        new(100, 24, "Scenery100_24", Kind.Prop),   // impaled remains
        new(100, 28, "Scenery100_28", Kind.Prop),   // impaled remains
        new(100, 29, "Scenery100_29", Kind.Prop),   // impaled remains
        new(200, 7, "Scenery200_7", Kind.Hanging),   // hanging lantern
        new(200, 8, "Scenery200_8", Kind.Hanging),   // hanging lantern
        new(200, 9, "Scenery200_9", Kind.Hanging),   // hanging lantern
        new(200, 10, "Scenery200_10", Kind.Prop),   // lantern
        new(200, 14, "Scenery200_14", Kind.Prop),   // chair
        new(200, 15, "Scenery200_15", Kind.Prop),   // bench
        new(200, 16, "Scenery200_16", Kind.Prop),   // bench
        new(200, 20, "Scenery200_20", Kind.Prop),   // chair
        new(202, 0, "Scenery202_0", Kind.Prop),   // statue
        new(202, 1, "Scenery202_1", Kind.Prop),   // statue
        new(202, 2, "Scenery202_2", Kind.Prop),   // statue
        new(202, 3, "Scenery202_3", Kind.Prop),   // statue
        new(202, 4, "Scenery202_4", Kind.Prop),   // statue
        new(202, 5, "Scenery202_5", Kind.Prop),   // statue
        new(202, 6, "Scenery202_6", Kind.Prop),   // statue
        new(203, 0, "Scenery203_0", Kind.Prop),   // column
        new(203, 1, "Scenery203_1", Kind.Prop),   // column
        new(203, 2, "Scenery203_2", Kind.Prop),   // broken column
        new(203, 3, "Scenery203_3", Kind.Prop),   // broken column
        new(203, 4, "Scenery203_4", Kind.Prop),   // rubble
        new(203, 5, "Scenery203_5", Kind.Prop),   // column
        new(203, 6, "Scenery203_6", Kind.Prop),   // column
        new(205, 0, "Scenery205_0", Kind.Prop),   // barrel
        new(205, 24, "Scenery205_24", Kind.Prop),   // crates
        new(205, 25, "Scenery205_25", Kind.Prop),   // crates
        new(205, 26, "Scenery205_26", Kind.Prop),   // crates
        new(205, 27, "Scenery205_27", Kind.Prop),   // cart
        new(205, 28, "Scenery205_28", Kind.Prop),   // cart
        new(205, 37, "Scenery205_37", Kind.Plant),   // potted plant
        new(205, 38, "Scenery205_38", Kind.Plant),   // potted plant
        new(205, 39, "Scenery205_39", Kind.Plant),   // potted plant
        new(205, 40, "Scenery205_40", Kind.Plant),   // potted plant
        new(206, 0, "Scenery206_0", Kind.Prop),   // skull
        new(206, 1, "Scenery206_1", Kind.Prop),   // skull
        new(206, 2, "Scenery206_2", Kind.Prop),   // impaled remains
        new(206, 7, "Scenery206_7", Kind.Prop),   // impaled remains
        new(206, 8, "Scenery206_8", Kind.Prop),   // bones
        new(206, 9, "Scenery206_9", Kind.Prop),   // impaled remains
        new(206, 10, "Scenery206_10", Kind.Prop),   // bones
        new(206, 11, "Scenery206_11", Kind.Prop),   // impaled remains
        new(206, 12, "Scenery206_12", Kind.Prop),   // impaled remains
        new(206, 13, "Scenery206_13", Kind.Prop),   // impaled remains
        new(206, 14, "Scenery206_14", Kind.Prop),   // remains
        new(206, 15, "Scenery206_15", Kind.Prop),   // impaled remains
        new(206, 16, "Scenery206_16", Kind.Prop),   // impaled remains
        new(206, 17, "Scenery206_17", Kind.Prop),   // impaled remains
        new(206, 18, "Scenery206_18", Kind.Prop),   // impaled remains
        new(206, 20, "Scenery206_20", Kind.Prop),   // gravestone
        new(206, 21, "Scenery206_21", Kind.Prop),   // gravestone
        new(206, 22, "Scenery206_22", Kind.Prop),   // bones
        new(206, 23, "Scenery206_23", Kind.Prop),   // bones
        new(206, 24, "Scenery206_24", Kind.Prop),   // bones
        new(206, 25, "Scenery206_25", Kind.Prop),   // bones
        new(206, 26, "Scenery206_26", Kind.Prop),   // cage
        new(206, 27, "Scenery206_27", Kind.Prop),   // pike
        new(206, 29, "Scenery206_29", Kind.Prop),   // ribcage
        new(206, 30, "Scenery206_30", Kind.Prop),   // arm bones
        new(206, 31, "Scenery206_31", Kind.Prop),   // leg bone
        new(206, 32, "Scenery206_32", Kind.Prop),   // horned skull
        new(206, 35, "Scenery206_35", Kind.Prop),   // remains
        new(206, 36, "Scenery206_36", Kind.Hanging),   // hanging remains
        new(211, 4, "Scenery211_4", Kind.Prop),   // chain
        new(211, 5, "Scenery211_5", Kind.Prop),   // chain
        new(211, 6, "Scenery211_6", Kind.Prop),   // chain
        new(211, 7, "Scenery211_7", Kind.Prop),   // chain
        new(211, 15, "Scenery211_15", Kind.Prop),   // wheel
        new(211, 16, "Scenery211_16", Kind.Prop),   // wheel
        new(211, 17, "Scenery211_17", Kind.Prop),   // wheel
        new(211, 18, "Scenery211_18", Kind.Prop),   // stocks
        new(211, 19, "Scenery211_19", Kind.Prop),   // stocks
        new(211, 29, "Scenery211_29", Kind.Prop),   // pike
        new(211, 33, "Scenery211_33", Kind.Hanging),   // hanging remains
        new(211, 34, "Scenery211_34", Kind.Prop),   // cage
        new(211, 35, "Scenery211_35", Kind.Prop),   // anvil
        new(211, 36, "Scenery211_36", Kind.Prop),   // hook
        new(212, 0, "Scenery212_0", Kind.Prop),   // well
        new(212, 1, "Scenery212_1", Kind.Prop),   // haypile
        new(212, 2, "Scenery212_2", Kind.Prop),   // fountain
        new(212, 3, "Scenery212_3", Kind.Prop),   // fountain
        new(212, 4, "Scenery212_4", Kind.Prop),   // signpost
        new(212, 5, "Scenery212_5", Kind.Prop),   // signpost
        new(212, 6, "Scenery212_6", Kind.Prop),   // signpost
        new(212, 7, "Scenery212_7", Kind.Prop),   // rubble
        new(212, 8, "Scenery212_8", Kind.Prop),   // water pump
        new(212, 9, "Scenery212_9", Kind.Prop),   // water pump
        new(212, 10, "Scenery212_10", Kind.Prop),   // post
        new(212, 11, "Scenery212_11", Kind.Prop),   // woodpile
        new(212, 12, "Scenery212_12", Kind.Prop),   // woodpile
        new(212, 13, "Scenery212_13", Kind.Prop),   // post
        new(212, 14, "Scenery212_14", Kind.Prop),   // post
        new(212, 15, "Scenery212_15", Kind.Prop),   // scaffold
        new(212, 16, "Scenery212_16", Kind.Prop),   // scaffold
        new(212, 17, "Scenery212_17", Kind.Prop),   // standing stone
        new(212, 18, "Scenery212_18", Kind.Prop),   // standing stone
        new(213, 2, "Scenery213_2", Kind.Plant),   // potted plant
        new(213, 3, "Scenery213_3", Kind.Plant),   // potted plant
        new(213, 4, "Scenery213_4", Kind.Plant),   // potted plant
        new(213, 5, "Scenery213_5", Kind.Plant),   // potted plant
        new(213, 7, "Scenery213_7", Kind.Hanging),   // hanging roots
        new(213, 8, "Scenery213_8", Kind.Hanging),   // hanging roots
        new(213, 9, "Scenery213_9", Kind.Hanging),   // hanging roots
        new(213, 10, "Scenery213_10", Kind.Hanging),   // hanging roots
        new(213, 15, "Scenery213_15", Kind.Plant),   // potted plant
        new(213, 16, "Scenery213_16", Kind.Plant),   // potted plant
        new(213, 17, "Scenery213_17", Kind.Plant),   // potted plant
        new(214, 7, "Scenery214_7", Kind.Prop),   // hoe
        new(300, 0, "Scenery300_0", Kind.Hanging),   // stalactite
        new(300, 1, "Scenery300_1", Kind.Hanging),   // stalactite
        new(300, 2, "Scenery300_2", Kind.Hanging),   // stalactite
        new(300, 3, "Scenery300_3", Kind.Hanging),   // stalactite
        new(300, 4, "Scenery300_4", Kind.Hanging),   // stalactite
        new(300, 5, "Scenery300_5", Kind.Hanging),   // stalactite
        new(300, 6, "Scenery300_6", Kind.Hanging),   // stalagmite
        new(300, 7, "Scenery300_7", Kind.Hanging),   // stalagmite
        new(300, 8, "Scenery300_8", Kind.Hanging),   // stalagmite
        new(300, 9, "Scenery300_9", Kind.Hanging),   // stalagmite
        new(300, 10, "Scenery300_10", Kind.Hanging),   // stalagmite
        new(300, 11, "Scenery300_11", Kind.Hanging),   // stalagmite
        new(300, 12, "Scenery300_12", Kind.Hanging),   // stalagmite
        new(300, 13, "Scenery300_13", Kind.Hanging),   // stalagmite
        new(300, 14, "Scenery300_14", Kind.Hanging),   // stalagmite
        new(300, 15, "Scenery300_15", Kind.Hanging),   // stalagmite
        new(301, 0, "Scenery301_0", Kind.Prop),   // sheaf
        new(301, 1, "Scenery301_1", Kind.Prop),   // sheaf
        new(301, 2, "Scenery301_2", Kind.Plant),   // wheat
        new(301, 3, "Scenery301_3", Kind.Plant),   // sunflowers
        new(301, 4, "Scenery301_4", Kind.Plant),   // sunflowers
        new(301, 5, "Scenery301_5", Kind.Plant),   // berry bush
        new(301, 6, "Scenery301_6", Kind.Prop),   // sheaf
        new(301, 7, "Scenery301_7", Kind.Plant),   // wheat
        new(301, 8, "Scenery301_8", Kind.Plant),   // wheat
        new(301, 9, "Scenery301_9", Kind.Plant),   // wheat
        new(301, 10, "Scenery301_10", Kind.Plant),   // vegetables
        new(301, 11, "Scenery301_11", Kind.Plant),   // berry bush
        new(301, 12, "Scenery301_12", Kind.Plant),   // vegetables
        new(301, 13, "Scenery301_13", Kind.Plant),   // flowers
        new(301, 14, "Scenery301_14", Kind.Plant),   // flowers
        new(301, 15, "Scenery301_15", Kind.Plant),   // flowers
        new(301, 16, "Scenery301_16", Kind.Plant),   // flowers
        new(301, 17, "Scenery301_17", Kind.Plant),   // flowers
        new(301, 18, "Scenery301_18", Kind.Plant),   // cabbage
        new(301, 19, "Scenery301_19", Kind.Plant),   // wheat
        new(301, 20, "Scenery301_20", Kind.Plant),   // berry bush
        new(301, 21, "Scenery301_21", Kind.Plant),   // wheat
        new(301, 22, "Scenery301_22", Kind.Plant),   // grass
        new(301, 23, "Scenery301_23", Kind.Plant),   // grass
        new(500, 1, "Scenery500_1", Kind.Plant),   // bush
        new(500, 2, "Scenery500_2", Kind.Plant),   // flowers
        new(500, 3, "Scenery500_3", Kind.Tree),   // small tree
        new(500, 4, "Scenery500_4", Kind.Prop),   // tall rock
        new(500, 6, "Scenery500_6", Kind.Plant),   // flowers
        new(500, 9, "Scenery500_9", Kind.Plant),   // fern
        new(500, 10, "Scenery500_10", Kind.Plant),   // fern
        new(500, 17, "Scenery500_17", Kind.Prop),   // tall rock
        new(500, 18, "Scenery500_18", Kind.Tree),   // tree
        new(500, 19, "Scenery500_19", Kind.Prop),   // rocks
        new(500, 20, "Scenery500_20", Kind.Plant),   // flowers
        new(500, 22, "Scenery500_22", Kind.Plant),   // flowers
        new(500, 25, "Scenery500_25", Kind.Prop),   // rocks
        new(500, 30, "Scenery500_30", Kind.Tree),   // tree
        new(500, 31, "Scenery500_31", Kind.Prop),   // rocks
        new(501, 3, "Scenery501_3", Kind.Prop),   // rocks
        new(501, 4, "Scenery501_4", Kind.Prop),   // rocks
        new(501, 5, "Scenery501_5", Kind.Prop),   // rocks
        new(501, 6, "Scenery501_6", Kind.Prop),   // tall rock
        new(501, 9, "Scenery501_9", Kind.Plant),   // fern
        new(501, 10, "Scenery501_10", Kind.Prop),   // rocks
        new(501, 15, "Scenery501_15", Kind.Tree),   // small tree
        new(501, 17, "Scenery501_17", Kind.Tree),   // small tree
        new(501, 19, "Scenery501_19", Kind.Tree),   // gnarled tree
        new(501, 20, "Scenery501_20", Kind.Tree),   // small tree
        new(501, 23, "Scenery501_23", Kind.Prop),   // tall rock
        new(501, 24, "Scenery501_24", Kind.Tree),   // gnarled tree
        new(501, 25, "Scenery501_25", Kind.Plant),   // flowers
        new(501, 26, "Scenery501_26", Kind.Plant),   // flowers
        new(502, 3, "Scenery502_3", Kind.Prop),   // tall rock
        new(502, 4, "Scenery502_4", Kind.Plant),   // dead plant
        new(502, 5, "Scenery502_5", Kind.Prop),   // stacked stones
        new(502, 6, "Scenery502_6", Kind.Prop),   // stacked stones
        new(502, 9, "Scenery502_9", Kind.Plant),   // dead plant
        new(502, 10, "Scenery502_10", Kind.Prop),   // stacked stones
        new(502, 11, "Scenery502_11", Kind.Prop),   // rocks
        new(502, 12, "Scenery502_12", Kind.Tree),   // twisted tree
        new(502, 13, "Scenery502_13", Kind.Tree),   // twisted tree
        new(502, 15, "Scenery502_15", Kind.Tree),   // gnarled tree
        new(502, 16, "Scenery502_16", Kind.Tree),   // twisted tree
        new(502, 17, "Scenery502_17", Kind.Tree),   // twisted tree
        new(502, 18, "Scenery502_18", Kind.Tree),   // small tree
        new(502, 19, "Scenery502_19", Kind.Tree),   // gnarled tree
        new(502, 20, "Scenery502_20", Kind.Plant),   // fern
        new(502, 24, "Scenery502_24", Kind.Tree),   // gnarled tree
        new(502, 25, "Scenery502_25", Kind.Tree),   // gnarled tree
        new(502, 27, "Scenery502_27", Kind.Plant),   // jungle plant
        new(502, 28, "Scenery502_28", Kind.Plant),   // fern
        new(502, 29, "Scenery502_29", Kind.Plant),   // nightshade
        new(502, 30, "Scenery502_30", Kind.Plant),   // dead plant
        new(503, 2, "Scenery503_2", Kind.Prop),   // rocks
        new(503, 3, "Scenery503_3", Kind.Prop),   // rocks
        new(503, 4, "Scenery503_4", Kind.Prop),   // tall rock
        new(503, 5, "Scenery503_5", Kind.Tree),   // small tree
        new(503, 6, "Scenery503_6", Kind.Plant),   // dead plant
        new(503, 9, "Scenery503_9", Kind.Plant),   // bush
        new(503, 10, "Scenery503_10", Kind.Plant),   // dead plant
        new(503, 11, "Scenery503_11", Kind.Tree),   // gnarled tree
        new(503, 12, "Scenery503_12", Kind.Tree),   // gnarled tree
        new(503, 18, "Scenery503_18", Kind.Prop),   // tall rock
        new(503, 19, "Scenery503_19", Kind.Prop),   // tall rock
        new(503, 20, "Scenery503_20", Kind.Prop),   // rocks
        new(503, 21, "Scenery503_21", Kind.Prop),   // rocks
        new(503, 22, "Scenery503_22", Kind.Prop),   // rocks
        new(503, 23, "Scenery503_23", Kind.Plant),   // dead plant
        new(503, 24, "Scenery503_24", Kind.Plant),   // shrub
        new(503, 28, "Scenery503_28", Kind.Tree),   // gnarled tree
        new(503, 29, "Scenery503_29", Kind.Plant),   // shrub
        new(503, 30, "Scenery503_30", Kind.Tree),   // gnarled tree
        new(503, 31, "Scenery503_31", Kind.Plant),   // bush
        new(504, 1, "Scenery504_1", Kind.Plant),   // bush
        new(504, 2, "Scenery504_2", Kind.Plant),   // flowering shrub
        new(504, 3, "Scenery504_3", Kind.Prop),   // rocks
        new(504, 4, "Scenery504_4", Kind.Prop),   // rocks
        new(504, 5, "Scenery504_5", Kind.Prop),   // rocks
        new(504, 6, "Scenery504_6", Kind.Prop),   // rocks
        new(504, 9, "Scenery504_9", Kind.Plant),   // dead plant
        new(504, 10, "Scenery504_10", Kind.Tree),   // small tree
        new(504, 11, "Scenery504_11", Kind.Tree),   // small tree
        new(504, 12, "Scenery504_12", Kind.Tree),   // tree
        new(504, 13, "Scenery504_13", Kind.Tree),   // pine
        new(504, 14, "Scenery504_14", Kind.Tree),   // tree
        new(504, 15, "Scenery504_15", Kind.Tree),   // tree
        new(504, 16, "Scenery504_16", Kind.Tree),   // tree
        new(504, 17, "Scenery504_17", Kind.Tree),   // autumn tree
        new(504, 18, "Scenery504_18", Kind.Tree),   // tree
        new(504, 19, "Scenery504_19", Kind.Prop),   // stump
        new(504, 20, "Scenery504_20", Kind.Prop),   // stump
        new(504, 21, "Scenery504_21", Kind.Plant),   // flowers
        new(504, 22, "Scenery504_22", Kind.Plant),   // flowers
        new(504, 24, "Scenery504_24", Kind.Tree),   // dead tree
        new(504, 25, "Scenery504_25", Kind.Tree),   // pine
        new(504, 26, "Scenery504_26", Kind.Plant),   // fern
        new(504, 27, "Scenery504_27", Kind.Plant),   // big bush
        new(504, 28, "Scenery504_28", Kind.Plant),   // big bush
        new(504, 29, "Scenery504_29", Kind.Plant),   // fern
        new(504, 30, "Scenery504_30", Kind.Tree),   // dead tree
        new(504, 31, "Scenery504_31", Kind.Prop),   // fallen log
        new(505, 1, "Scenery505_1", Kind.Plant),   // snowy bush
        new(505, 2, "Scenery505_2", Kind.Plant),   // flowers
        new(505, 3, "Scenery505_3", Kind.Prop),   // snowy rocks
        new(505, 4, "Scenery505_4", Kind.Prop),   // snowy rocks
        new(505, 5, "Scenery505_5", Kind.Prop),   // snowy rocks
        new(505, 6, "Scenery505_6", Kind.Prop),   // snowy rocks
        new(505, 9, "Scenery505_9", Kind.Plant),   // snowy shrub
        new(505, 10, "Scenery505_10", Kind.Tree),   // snowy tree
        new(505, 11, "Scenery505_11", Kind.Tree),   // snowy tree
        new(505, 12, "Scenery505_12", Kind.Tree),   // snowy tree
        new(505, 13, "Scenery505_13", Kind.Tree),   // snowy pine
        new(505, 14, "Scenery505_14", Kind.Tree),   // snowy tree
        new(505, 15, "Scenery505_15", Kind.Tree),   // dead tree
        new(505, 16, "Scenery505_16", Kind.Tree),   // snowy tree
        new(505, 17, "Scenery505_17", Kind.Tree),   // snowy tree
        new(505, 18, "Scenery505_18", Kind.Tree),   // snowy tree
        new(505, 19, "Scenery505_19", Kind.Prop),   // hollow trunk
        new(505, 20, "Scenery505_20", Kind.Prop),   // hollow trunk
        new(505, 21, "Scenery505_21", Kind.Plant),   // flowers
        new(505, 22, "Scenery505_22", Kind.Plant),   // snowy shrub
        new(505, 24, "Scenery505_24", Kind.Tree),   // dead tree
        new(505, 25, "Scenery505_25", Kind.Tree),   // snowy pine
        new(505, 26, "Scenery505_26", Kind.Plant),   // snowy shrub
        new(505, 27, "Scenery505_27", Kind.Plant),   // snowy bush
        new(505, 28, "Scenery505_28", Kind.Plant),   // snowy bush
        new(505, 29, "Scenery505_29", Kind.Plant),   // snowy shrub
        new(505, 30, "Scenery505_30", Kind.Tree),   // dead tree
        new(505, 31, "Scenery505_31", Kind.Prop),   // fallen log
        new(506, 1, "Scenery506_1", Kind.Prop),   // tall rock
        new(506, 2, "Scenery506_2", Kind.Plant),   // flowers
        new(506, 3, "Scenery506_3", Kind.Prop),   // tall rock
        new(506, 4, "Scenery506_4", Kind.Prop),   // tall rock
        new(506, 5, "Scenery506_5", Kind.Tree),   // pine
        new(506, 6, "Scenery506_6", Kind.Prop),   // tall rock
        new(506, 9, "Scenery506_9", Kind.Plant),   // bush
        new(506, 10, "Scenery506_10", Kind.Tree),   // small tree
        new(506, 11, "Scenery506_11", Kind.Tree),   // pine
        new(506, 12, "Scenery506_12", Kind.Tree),   // pine
        new(506, 13, "Scenery506_13", Kind.Tree),   // pine
        new(506, 14, "Scenery506_14", Kind.Tree),   // tree
        new(506, 15, "Scenery506_15", Kind.Tree),   // tree
        new(506, 16, "Scenery506_16", Kind.Tree),   // tree
        new(506, 17, "Scenery506_17", Kind.Prop),   // tall rock
        new(506, 18, "Scenery506_18", Kind.Prop),   // tall rock
        new(506, 19, "Scenery506_19", Kind.Prop),   // hollow trunk
        new(506, 20, "Scenery506_20", Kind.Prop),   // hollow trunk
        new(506, 21, "Scenery506_21", Kind.Plant),   // flowers
        new(506, 22, "Scenery506_22", Kind.Plant),   // flowers
        new(506, 23, "Scenery506_23", Kind.Plant),   // jungle plant
        new(506, 24, "Scenery506_24", Kind.Tree),   // dead tree
        new(506, 25, "Scenery506_25", Kind.Tree),   // pine
        new(506, 26, "Scenery506_26", Kind.Plant),   // jungle plant
        new(506, 27, "Scenery506_27", Kind.Plant),   // big bush
        new(506, 28, "Scenery506_28", Kind.Prop),   // tall rock
        new(506, 29, "Scenery506_29", Kind.Plant),   // fern
        new(506, 30, "Scenery506_30", Kind.Tree),   // pine
        new(506, 31, "Scenery506_31", Kind.Plant),   // fern
        new(507, 1, "Scenery507_1", Kind.Prop),   // snowy rock
        new(507, 2, "Scenery507_2", Kind.Plant),   // snowy shrub
        new(507, 3, "Scenery507_3", Kind.Prop),   // snowy rock
        new(507, 4, "Scenery507_4", Kind.Prop),   // snowy rock
        new(507, 5, "Scenery507_5", Kind.Tree),   // snowy pine
        new(507, 6, "Scenery507_6", Kind.Prop),   // snowy rock
        new(507, 9, "Scenery507_9", Kind.Plant),   // snowy shrub
        new(507, 10, "Scenery507_10", Kind.Tree),   // snowy tree
        new(507, 11, "Scenery507_11", Kind.Tree),   // snowy pine
        new(507, 12, "Scenery507_12", Kind.Tree),   // snowy pine
        new(507, 13, "Scenery507_13", Kind.Tree),   // snowy pine
        new(507, 14, "Scenery507_14", Kind.Tree),   // snowy tree
        new(507, 15, "Scenery507_15", Kind.Tree),   // dead tree
        new(507, 16, "Scenery507_16", Kind.Tree),   // snowy tree
        new(507, 17, "Scenery507_17", Kind.Prop),   // snowy rock
        new(507, 18, "Scenery507_18", Kind.Prop),   // snowy rock
        new(507, 19, "Scenery507_19", Kind.Prop),   // hollow trunk
        new(507, 20, "Scenery507_20", Kind.Prop),   // hollow trunk
        new(507, 21, "Scenery507_21", Kind.Plant),   // flowers
        new(507, 22, "Scenery507_22", Kind.Plant),   // snowy shrub
        new(507, 23, "Scenery507_23", Kind.Plant),   // snowy bush
        new(507, 24, "Scenery507_24", Kind.Tree),   // dead tree
        new(507, 25, "Scenery507_25", Kind.Tree),   // snowy pine
        new(507, 26, "Scenery507_26", Kind.Plant),   // snowy shrub
        new(507, 27, "Scenery507_27", Kind.Plant),   // snowy bush
        new(507, 28, "Scenery507_28", Kind.Prop),   // snowy rock
        new(507, 29, "Scenery507_29", Kind.Plant),   // snowy shrub
        new(507, 30, "Scenery507_30", Kind.Plant),   // snowy shrub
        new(507, 31, "Scenery507_31", Kind.Plant),   // snowy shrub
        new(508, 1, "Scenery508_1", Kind.Prop),   // tall rock
        new(508, 2, "Scenery508_2", Kind.Plant),   // bush
        new(508, 3, "Scenery508_3", Kind.Prop),   // tall rock
        new(508, 4, "Scenery508_4", Kind.Prop),   // tall rock
        new(508, 5, "Scenery508_5", Kind.Prop),   // tall rock
        new(508, 6, "Scenery508_6", Kind.Prop),   // tall rock
        new(508, 9, "Scenery508_9", Kind.Plant),   // dead plant
        new(508, 10, "Scenery508_10", Kind.Tree),   // small tree
        new(508, 12, "Scenery508_12", Kind.Prop),   // rocks
        new(508, 13, "Scenery508_13", Kind.Tree),   // autumn tree
        new(508, 14, "Scenery508_14", Kind.Plant),   // fern
        new(508, 15, "Scenery508_15", Kind.Tree),   // tree
        new(508, 16, "Scenery508_16", Kind.Tree),   // gnarled tree
        new(508, 17, "Scenery508_17", Kind.Plant),   // dead plant
        new(508, 18, "Scenery508_18", Kind.Tree),   // gnarled tree
        new(508, 19, "Scenery508_19", Kind.Prop),   // hollow trunk
        new(508, 20, "Scenery508_20", Kind.Prop),   // hollow trunk
        new(508, 21, "Scenery508_21", Kind.Plant),   // flowers
        new(508, 24, "Scenery508_24", Kind.Tree),   // dead tree
        new(508, 25, "Scenery508_25", Kind.Tree),   // gnarled tree
        new(508, 26, "Scenery508_26", Kind.Plant),   // fern
        new(508, 27, "Scenery508_27", Kind.Plant),   // big bush
        new(508, 28, "Scenery508_28", Kind.Plant),   // big bush
        new(508, 29, "Scenery508_29", Kind.Plant),   // fern
        new(508, 30, "Scenery508_30", Kind.Tree),   // dead tree
        new(508, 31, "Scenery508_31", Kind.Prop),   // fallen log
        new(509, 1, "Scenery509_1", Kind.Prop),   // snowy rock
        new(509, 2, "Scenery509_2", Kind.Plant),   // snowy shrub
        new(509, 3, "Scenery509_3", Kind.Prop),   // snowy rock
        new(509, 4, "Scenery509_4", Kind.Prop),   // snowy rock
        new(509, 5, "Scenery509_5", Kind.Prop),   // snowy rock
        new(509, 6, "Scenery509_6", Kind.Prop),   // snowy rock
        new(509, 9, "Scenery509_9", Kind.Plant),   // snowy shrub
        new(509, 10, "Scenery509_10", Kind.Tree),   // snowy tree
        new(509, 12, "Scenery509_12", Kind.Prop),   // snowy rocks
        new(509, 13, "Scenery509_13", Kind.Tree),   // dead tree
        new(509, 14, "Scenery509_14", Kind.Plant),   // snowy shrub
        new(509, 15, "Scenery509_15", Kind.Tree),   // dead tree
        new(509, 16, "Scenery509_16", Kind.Tree),   // dead tree
        new(509, 17, "Scenery509_17", Kind.Plant),   // snowy shrub
        new(509, 18, "Scenery509_18", Kind.Tree),   // gnarled tree
        new(509, 19, "Scenery509_19", Kind.Prop),   // hollow trunk
        new(509, 20, "Scenery509_20", Kind.Prop),   // hollow trunk
        new(509, 21, "Scenery509_21", Kind.Plant),   // flowers
        new(509, 24, "Scenery509_24", Kind.Tree),   // dead tree
        new(509, 25, "Scenery509_25", Kind.Tree),   // gnarled tree
        new(509, 26, "Scenery509_26", Kind.Plant),   // snowy shrub
        new(509, 27, "Scenery509_27", Kind.Plant),   // snowy bush
        new(509, 28, "Scenery509_28", Kind.Plant),   // snowy bush
        new(509, 29, "Scenery509_29", Kind.Plant),   // snowy shrub
        new(509, 30, "Scenery509_30", Kind.Tree),   // dead tree
        new(509, 31, "Scenery509_31", Kind.Prop),   // fallen log
        new(510, 1, "Scenery510_1", Kind.Prop),   // tall rock
        new(510, 2, "Scenery510_2", Kind.Plant),   // flowers
        new(510, 3, "Scenery510_3", Kind.Prop),   // tall rock
        new(510, 4, "Scenery510_4", Kind.Prop),   // tall rock
        new(510, 5, "Scenery510_5", Kind.Tree),   // pine
        new(510, 6, "Scenery510_6", Kind.Prop),   // tall rock
        new(510, 9, "Scenery510_9", Kind.Plant),   // bush
        new(510, 10, "Scenery510_10", Kind.Tree),   // small tree
        new(510, 11, "Scenery510_11", Kind.Tree),   // pine
        new(510, 12, "Scenery510_12", Kind.Tree),   // pine
        new(510, 13, "Scenery510_13", Kind.Tree),   // pine
        new(510, 14, "Scenery510_14", Kind.Prop),   // tall rock
        new(510, 15, "Scenery510_15", Kind.Tree),   // tree
        new(510, 16, "Scenery510_16", Kind.Tree),   // gnarled tree
        new(510, 17, "Scenery510_17", Kind.Prop),   // tall rock
        new(510, 18, "Scenery510_18", Kind.Prop),   // tall rock
        new(510, 19, "Scenery510_19", Kind.Prop),   // hollow trunk
        new(510, 20, "Scenery510_20", Kind.Prop),   // hollow trunk
        new(510, 21, "Scenery510_21", Kind.Tree),   // small tree
        new(510, 22, "Scenery510_22", Kind.Plant),   // flowers
        new(510, 23, "Scenery510_23", Kind.Plant),   // jungle plant
        new(510, 24, "Scenery510_24", Kind.Tree),   // dead tree
        new(510, 25, "Scenery510_25", Kind.Tree),   // pine
        new(510, 26, "Scenery510_26", Kind.Plant),   // jungle plant
        new(510, 27, "Scenery510_27", Kind.Prop),   // tall rock
        new(510, 28, "Scenery510_28", Kind.Prop),   // tall rock
        new(510, 29, "Scenery510_29", Kind.Plant),   // fern
        new(510, 30, "Scenery510_30", Kind.Tree),   // pine
        new(510, 31, "Scenery510_31", Kind.Plant),   // dead plant
        new(511, 1, "Scenery511_1", Kind.Prop),   // snowy rock
        new(511, 2, "Scenery511_2", Kind.Plant),   // snowy shrub
        new(511, 3, "Scenery511_3", Kind.Prop),   // snowy rock
        new(511, 4, "Scenery511_4", Kind.Prop),   // snowy rock
        new(511, 5, "Scenery511_5", Kind.Tree),   // snowy pine
        new(511, 6, "Scenery511_6", Kind.Prop),   // snowy rock
        new(511, 9, "Scenery511_9", Kind.Plant),   // snowy shrub
        new(511, 10, "Scenery511_10", Kind.Tree),   // snowy tree
        new(511, 11, "Scenery511_11", Kind.Tree),   // snowy pine
        new(511, 12, "Scenery511_12", Kind.Prop),   // stacked stones
        new(511, 13, "Scenery511_13", Kind.Tree),   // snowy pine
        new(511, 14, "Scenery511_14", Kind.Prop),   // snowy rock
        new(511, 15, "Scenery511_15", Kind.Tree),   // dead tree
        new(511, 16, "Scenery511_16", Kind.Tree),   // gnarled tree
        new(511, 17, "Scenery511_17", Kind.Prop),   // snowy rock
        new(511, 18, "Scenery511_18", Kind.Prop),   // snowy rock
        new(511, 19, "Scenery511_19", Kind.Prop),   // hollow trunk
        new(511, 20, "Scenery511_20", Kind.Prop),   // hollow trunk
        new(511, 21, "Scenery511_21", Kind.Tree),   // snowy tree
        new(511, 22, "Scenery511_22", Kind.Plant),   // snowy shrub
        new(511, 23, "Scenery511_23", Kind.Plant),   // snowy bush
        new(511, 24, "Scenery511_24", Kind.Tree),   // dead tree
        new(511, 25, "Scenery511_25", Kind.Tree),   // snowy pine
        new(511, 26, "Scenery511_26", Kind.Plant),   // snowy shrub
        new(511, 27, "Scenery511_27", Kind.Prop),   // snowy rock
        new(511, 28, "Scenery511_28", Kind.Prop),   // snowy rock
        new(511, 29, "Scenery511_29", Kind.Plant),   // snowy shrub
        new(511, 30, "Scenery511_30", Kind.Plant),   // snowy shrub
        new(511, 31, "Scenery511_31", Kind.Plant),   // snowy shrub
    ];

    /// <summary>Writes each target sprite as a PNG and the job list for sprite2mesh.py. Returns how many.</summary>
    public static int Export(string arena2, string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "sprites"));
        var palette = DfTexture.Palette(arena2);
        var lines = new List<string>();
        foreach (var t in Targets)
        {
            string png = Path.GetFullPath(Path.Combine(dir, "sprites", t.Name + ".png"));
            // Then the face budget (trees 8000, the rest 5000) and the whitening of pale colours.
            if (DfTexture.SaveSprite(arena2, t.Archive, t.Record, png, palette))
                lines.Add($"{t.Name}\t{png}\t{(t.Kind == Kind.Tree ? 8000 : 5000)}\t{t.Whiten.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{t.Kind}");
        }
        File.WriteAllLines(Path.Combine(dir, "sprites.txt"), lines);
        return lines.Count;
    }
}
