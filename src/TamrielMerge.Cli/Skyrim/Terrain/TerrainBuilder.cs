using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>Turns a <see cref="HeightField"/> into a Skyrim exterior worldspace and its cells' LAND records.</summary>
public static class TerrainBuilder
{
    /// <summary>Cells lying entirely below this get no LAND record; the worldspace default land height covers them.</summary>
    public const float SkipBelow = -1500;
    public const float DefaultLandHeight = -2048;
    const int CellUnits = HeightField.QuadsPerCell * HeightField.UnitsPerQuad;
    const int QuadVerts = 17;

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    /// <summary>
    /// Daggerfall's ground (user, 2026-10-12: "now for the daggerfall textures for the game itself and worldspace"; "the
    /// landscape needs worked on" - Daggerfall's ground patterns): each climate's terrain tile set (desert 2, mountain
    /// 102, temperate 302, swamp 402) with its dirt, grass and stone (records 1, 2, 3; Daggerfall goes by the number, so
    /// the swamp set's reddish mud is its grass), laid as Daggerfall Unity lays them (<see cref="GroundNoise"/>). The
    /// subtropical climate shares the desert set but not its sparse grass; per entry, the climate's nature archive for
    /// its grass and how thick that grows there (DfGrass): Daggerfall Unity's chances of a nature flat, 0.9 on grass,
    /// 0.2 on dirt and 0.05 on stone, the desert's a quarter of that.
    /// </summary>
    internal static readonly (string Name, int Archive, int Record, int Nature, float Grass)[] Ground =
    [
        ("DesertDirt", 2, 1, 503, 0.25f * DirtGrass), ("DesertGrass", 2, 2, 503, 0.25f), ("DesertStone", 2, 3, 503, 0.25f * StoneGrass),
        ("SubtropicalDirt", 2, 1, 501, DirtGrass), ("SubtropicalGrass", 2, 2, 501, 1), ("SubtropicalStone", 2, 3, 501, StoneGrass),
        ("MountainDirt", 102, 1, 510, DirtGrass), ("MountainGrass", 102, 2, 510, 1), ("MountainStone", 102, 3, 510, StoneGrass),
        ("TemperateDirt", 302, 1, 504, DirtGrass), ("TemperateGrass", 302, 2, 504, 1), ("TemperateStone", 302, 3, 504, StoneGrass),
        ("SwampDirt", 402, 1, 502, DirtGrass), ("SwampGrass", 402, 2, 502, 1), ("SwampStone", 402, 3, 502, StoneGrass),
    ];
    const float DirtGrass = 0.2f / 0.9f, StoneGrass = 0.05f / 0.9f;
    enum Group { Desert, Subtropical, Mountain, Temperate, Swamp }
    const int Dirt = 0, Grassy = 1, Stone = 2, Kinds = 3, Groups = 5;
    static readonly int TexCount = Ground.Length;

    static Group GroupOf(DfClimate c) => c switch
    {
        DfClimate.Desert or DfClimate.Desert2 => Group.Desert,
        DfClimate.Subtropical => Group.Subtropical,
        DfClimate.Mountain or DfClimate.MountainWoods => Group.Mountain,
        DfClimate.Swamp or DfClimate.Rainforest => Group.Swamp,
        _ => Group.Temperate,   // woodlands, haunted woodlands, the sea
    };

    /// <summary>Skyrim's land textures standing for each, when Daggerfall's aren't made (and whose footstep material
    /// and friction Daggerfall's take): LDirtPath01, the climate's grass, LRocks01NoRocks.</summary>
    static readonly FormKey[] SkyrimForms = Ground.Select(g => g.Record switch
    {
        1 => Sk(0x0B424C),
        3 => Sk(0x06DE8E),
        _ => g.Archive switch { 2 => Sk(0x01342A), 102 => Sk(0x0137CC), 402 => Sk(0x058606), _ => Sk(0x013428) },
    }).ToArray();

    /// <summary>The land textures cells take: Daggerfall's (<see cref="AddDaggerfallTextures"/>) once made, else Skyrim's.</summary>
    static FormKey[]? daggerfallForms;
    static FormKey Form(int tex) => (daggerfallForms ?? SkyrimForms)[tex];

    /// <summary>
    /// The Daggerfall land textures (<see cref="Ground"/>) as texture sets and landscape textures in
    /// <paramref name="mod"/>, their pictures upscaled into <paramref name="outDir"/> (DfTextureExport), each with its
    /// climate's Daggerfall grass (<see cref="DfGrass"/>); cells built afterwards take them. Returns them.
    /// </summary>
    public static FormKey[] AddDaggerfallTextures(SkyrimMod mod, ISkyrimModGetter skyrim, string arena2, string outDir)
    {
        var export = new Meshes.DfTextureExport(arena2, outDir);
        var grass = new DfGrass(mod, skyrim, arena2, outDir);
        var forms = new FormKey[TexCount];
        var made = new Dictionary<(int, int), TextureSet>();
        for (int i = 0; i < TexCount; i++)
        {
            var (name, archive, record, _, _) = Ground[i];
            if (!made.TryGetValue((archive, record), out var set))
            {
                string path = export.Ensure(archive, record).Path;
                set = new TextureSet(mod)
                {
                    EditorID = $"DFLandSet{archive}_{record}",
                    ObjectBounds = new ObjectBounds(),
                    Diffuse = path[9..],                                   // without "textures\"
                    NormalOrGloss = Meshes.DfTextureExport.FlatNormal[9..],
                };
                mod.TextureSets.Add(set);
                made[(archive, record)] = set;
            }
            var skyrimTex = skyrim.LandscapeTextures.FirstOrDefault(t => t.FormKey == SkyrimForms[i]);
            var lt = new LandscapeTexture(mod)
            {
                EditorID = "DFLand" + name,
                TextureSet = set.ToNullableLink<ITextureSetGetter>(),
                MaterialType = skyrimTex?.MaterialType.FormKey is { } mt && !mt.IsNull ? new FormLinkNullable<IMaterialTypeGetter>(mt) : new FormLinkNullable<IMaterialTypeGetter>(),
                HavokFriction = skyrimTex?.HavokFriction ?? 30,
                HavokRestitution = skyrimTex?.HavokRestitution ?? 30,
                TextureSpecularExponent = 30,
            };
            foreach (var g in grass.For(i)) lt.Grasses.Add(g.ToLink<IGrassGetter>());
            mod.LandscapeTextures.Add(lt);
            forms[i] = lt.FormKey;
            TerrainLod.KnownDiffuse[lt.FormKey] = @"textures\" + set.Diffuse!.DataRelativePath.Path;
        }
        return daggerfallForms = forms;
    }

    /// <summary>Builds before 2026-10-12 painted these Skyrim land textures (one per climate, sand, rock, snow, sea
    /// floor): the Daggerfall ground each stands for, for df-land-textures moving such a build over.</summary>
    static readonly (FormKey Old, string New)[] Legacy =
    [
        (Sk(0x13428), "TemperateGrass"), (Sk(0x1342A), "SubtropicalGrass"), (Sk(0x137CC), "MountainGrass"), (Sk(0x1CC47), "SwampDirt"),
        (Sk(0x21711), "DesertDirt"), (Sk(0x2C6C6), "MountainDirt"), (Sk(0x57DC7), "TemperateGrass"), (Sk(0x132E9), "SwampStone"),
        (Sk(0x6B3CF), "DesertDirt"), (Sk(0x6DE8E), "MountainStone"), (Sk(0x0089B), "MountainStone"), (Sk(0x6B4C6), "TemperateDirt"),
    ];

    /// <summary>Where an old build's Skyrim land texture stands in <see cref="Ground"/> (-1 if it isn't one of ours).</summary>
    public static int SkyrimIndex(FormKey skyrimLtex) =>
        Legacy.FirstOrDefault(l => l.Old == skyrimLtex) is { New: { } n } ? DaggerfallIndex("DFLand" + n) : -1;

    /// <summary>Where a Daggerfall land texture (EditorID "DFLand" + its name) stands in <see cref="Ground"/>, or -1.</summary>
    public static int DaggerfallIndex(string? editorId) => Array.FindIndex(Ground, g => "DFLand" + g.Name == editorId);

    /// <summary>A Daggerfall tile's size in the world: Daggerfall Unity's 819.2 m map pixel is 128 tiles, 6.4 m each;
    /// the ground's patches keep that size whatever scale the map is built at.</summary>
    const double TileUnits = 6.4 * 70;

    /// <summary>
    /// Daggerfall Unity's ground weight (DefaultTerrainTexturing.NoiseWeight): Perlin noise (0..1) at 0.05 a tile,
    /// three octaves, amplitude 0.9 falling by 0.4. Its tiles are dirt below 0.5, stone above 0.95 and grass between.
    /// </summary>
    static float GroundNoise(double tileX, double tileY)
    {
        double f = 0.05, a = 0.9, sum = 0;
        for (int i = 0; i < 3; i++, f *= 2, a *= 0.4)
            sum += (Noise.Perlin(tileX * f + 41.7, tileY * f + 70.28) * 0.5 + 0.5) * a;
        return (float)Math.Clamp(sum, -1, 1);
    }

    /// <summary>
    /// The overland worldspace for a grid of cells (no cells yet: the land is built per tile, <see cref="OverlandTiles"/>).
    /// </summary>
    public static Worldspace NewWorldspace(SkyrimMod mod, ISkyrimModGetter skyrim, HeightField f, string editorId, string name)
    {
        var tamriel = skyrim.Worldspaces.First(w => w.EditorID == "Tamriel");
        var ws = new Worldspace(mod)
        {
            EditorID = editorId,
            Name = name,
            Climate = tamriel.Climate.AsNullable(),
            Water = tamriel.Water.AsNullable(),
            LodWater = tamriel.LodWater.AsNullable(),
            LodWaterHeight = 0,
            LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = DefaultLandHeight, DefaultWaterHeight = 0 },
            // NAM0/NAM9 are in cell coordinates (vanilla Tamriel: -57,-43 .. 62,51), not world units.
            ObjectBoundsMin = new P2Float(f.MinCellX, f.MinCellY),
            ObjectBoundsMax = new P2Float(f.MinCellX + f.CellsX, f.MinCellY + f.CellsY),
            WorldMapOffsetScale = 1,
            DistantLodMultiplier = 1,
            MapData = new WorldspaceMap
            {
                NorthwestCellCoords = new P2Int16((short)f.MinCellX, (short)(f.MinCellY + f.CellsY - 1)),
                SoutheastCellCoords = new P2Int16((short)(f.MinCellX + f.CellsX - 1), (short)f.MinCellY),
                CameraMinHeight = tamriel.MapData?.CameraMinHeight ?? 50000,
                CameraMaxHeight = tamriel.MapData?.CameraMaxHeight ?? 80000,
                CameraInitialPitch = tamriel.MapData?.CameraInitialPitch ?? 50,
            },
            TopCell = new Cell(mod)
            {
                Flags = tamriel.TopCell?.Flags ?? 0,
                SkyrimMajorRecordFlags = tamriel.TopCell?.SkyrimMajorRecordFlags ?? 0,
            },
        };
        mod.Worldspaces.Add(ws);
        return ws;
    }

    /// <summary>Files a cell under its worldspace's block and sub-block groups, as Skyrim groups them.</summary>
    public static void AddCell(Worldspace ws, Cell cell)
    {
        int gx = cell.Grid!.Point.X, gy = cell.Grid.Point.Y;
        short bx = (short)FloorDiv(gx, 32), by = (short)FloorDiv(gy, 32), sx = (short)FloorDiv(gx, 8), sy = (short)FloorDiv(gy, 8);
        var block = ws.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null)
        {
            block = new WorldspaceBlock { BlockNumberX = bx, BlockNumberY = by, GroupType = GroupTypeEnum.ExteriorCellBlock };
            ws.SubCells.Add(block);
        }
        var sub = block.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
        if (sub is null)
        {
            sub = new WorldspaceSubBlock { BlockNumberX = sx, BlockNumberY = sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            block.Items.Add(sub);
        }
        sub.Items.Add(cell);
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>Whether <see cref="BuildCell"/> gives field cell (<paramref name="cx"/>, <paramref name="cy"/>) land
    /// (any of its heights at or above <see cref="SkipBelow"/>).</summary>
    public static bool HasLand(HeightField f, int cx, int cy)
    {
        const int n = HeightField.QuadsPerCell + 1;
        int vx0 = cx * HeightField.QuadsPerCell, vy0 = cy * HeightField.QuadsPerCell;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (f.At(vx0 + x, vy0 + y) >= SkipBelow) return true;
        return false;
    }

    /// <summary>
    /// The cell at field cell (<paramref name="cx"/>, <paramref name="cy"/>) with its land, at grid
    /// (<paramref name="gx"/>, <paramref name="gy"/>) of the worldspace it goes in; null when it lies all under the sea.
    /// </summary>
    public static Cell? BuildCell(SkyrimMod mod, HeightField f, DfWorldMap map, int cx, int cy, float peak, int gx, int gy)
    {
        const int n = HeightField.QuadsPerCell + 1;
        int vx0 = cx * HeightField.QuadsPerCell, vy0 = cy * HeightField.QuadsPerCell;
        float max = float.MinValue, min = float.MaxValue;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float h = f.At(vx0 + x, vy0 + y);
                max = Math.Max(max, h); min = Math.Min(min, h);
            }
        if (max < SkipBelow) return null;

        var land = new Landscape(mod)
        {
            Flags = (Landscape.Flag)0x1D, // has normals+heights, has layers, unknown 0x8 and 0x10 as in vanilla (minus vertex colours)
            VertexHeightMap = EncodeHeights(f, vx0, vy0),
            VertexNormals = new Array2d<P3UInt8>(new P3UInt8[n, n]),
        };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var (nx, ny, nz) = Normal(f, vx0 + x, vy0 + y);
                land.VertexNormals[x, y] = new P3UInt8((byte)(sbyte)Math.Round(nx * 127), (byte)(sbyte)Math.Round(ny * 127), (byte)(sbyte)Math.Round(nz * 127));
            }
        land.Layers.AddRange(BuildLayers(f, map, vx0, vy0, peak));
        land.IsCompressed = true;

        return new Cell(mod)
        {
            Grid = new CellGrid { Point = new P2Int(gx, gy) },
            Flags = min < 0 ? Cell.Flag.HasWater : 0,
            Landscape = land,
            Timestamp = 0,
        };
    }

    /// <summary>
    /// VHGT stores heights in 8-unit steps as signed byte deltas: the first vertex of each row relative to the
    /// first vertex of the row below, every other vertex relative to its western neighbour.
    /// </summary>
    static LandscapeVertexHeightMap EncodeHeights(HeightField f, int vx0, int vy0)
    {
        const int n = HeightField.QuadsPerCell + 1;
        var deltas = new Array2d<sbyte>(new sbyte[n, n]);
        int origin = (int)Math.Round(f.At(vx0, vy0) / 8);
        int rowStart = origin;
        for (int y = 0; y < n; y++)
        {
            int target0 = (int)Math.Round(f.At(vx0, vy0 + y) / 8);
            int d0 = y == 0 ? 0 : Math.Clamp(target0 - rowStart, -128, 127);
            rowStart += d0;
            deltas[0, y] = (sbyte)d0;
            int current = rowStart;
            for (int x = 1; x < n; x++)
            {
                int target = (int)Math.Round(f.At(vx0 + x, vy0 + y) / 8);
                int d = Math.Clamp(target - current, -128, 127);
                current += d;
                deltas[x, y] = (sbyte)d;
            }
        }
        return new LandscapeVertexHeightMap { Offset = origin, HeightMap = deltas };
    }

    static (double x, double y, double z) Normal(HeightField f, int vx, int vy)
    {
        double dx = (f.At(vx + 1, vy) - f.At(vx - 1, vy)) / (2.0 * HeightField.UnitsPerQuad);
        double dy = (f.At(vx, vy + 1) - f.At(vx, vy - 1)) / (2.0 * HeightField.UnitsPerQuad);
        double len = Math.Sqrt(dx * dx + dy * dy + 1);
        return (-dx / len, -dy / len, 1 / len);
    }

    static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Texture weights at a global vertex: the climates' ground (blended between map pixels) split into dirt, grass and
    /// stone by <see cref="GroundNoise"/> (a narrow blend at each threshold, where Daggerfall has its transition tiles);
    /// then, as Daggerfall Unity, its dirt on the beach and under the sea, and its stone on slopes too steep for the
    /// rest. Peaks keep their ground: Daggerfall's snow is winter's, and this world has no seasons.
    /// </summary>
    internal static void Weights(HeightField f, DfWorldMap map, int vx, int vy, float peak, Span<float> w)
    {
        w.Clear();
        var (u, v) = f.PixelCoord(vx, vy);
        double px = u - 0.5, py = v - 0.5;
        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        float fx = (float)(px - x0), fy = (float)(py - y0);
        float n = GroundNoise(vx * (double)HeightField.UnitsPerQuad / TileUnits, vy * (double)HeightField.UnitsPerQuad / TileUnits);
        float stone = Smooth(0.93f, 0.97f, n), dirt = 1 - Smooth(0.47f, 0.53f, n), grass = Math.Max(0, 1 - dirt - stone);
        void Add(Span<float> w, DfClimate c, float b)
        {
            int g = (int)GroupOf(c) * Kinds;
            w[g + Dirt] += b * dirt; w[g + Grassy] += b * grass; w[g + Stone] += b * stone;
        }
        Add(w, map.ClimateAt(x0, y0), (1 - fx) * (1 - fy));
        Add(w, map.ClimateAt(x0 + 1, y0), fx * (1 - fy));
        Add(w, map.ClimateAt(x0, y0 + 1), (1 - fx) * fy);
        Add(w, map.ClimateAt(x0 + 1, y0 + 1), fx * fy);

        float h = f.At(vx, vy);
        float nz = (float)Normal(f, vx, vy).z;
        Shift(w, Dirt, 1 - Smooth(60, 260, h));      // beach and sea floor
        Shift(w, Stone, Smooth(0.86f, 0.72f, nz));    // ~31 to ~44 degrees
    }

    /// <summary>Moves <paramref name="a"/> of each climate's weight to its <paramref name="kind"/> of ground.</summary>
    static void Shift(Span<float> w, int kind, float a)
    {
        if (a <= 0) return;
        for (int g = 0; g < Groups * Kinds; g += Kinds)
        {
            float total = w[g] + w[g + 1] + w[g + 2];
            for (int k = 0; k < Kinds; k++) w[g + k] *= 1 - a;
            w[g + kind] += total * a;
        }
    }

    /// <summary>
    /// One base layer plus alpha layers per quadrant. Skyrim paints alpha layers in order over the base, so
    /// a layer's opacity is its weight divided by the total weight of itself and everything beneath it.
    /// </summary>
    static IEnumerable<BaseLayer> BuildLayers(HeightField f, DfWorldMap map, int vx0, int vy0, float peak)
    {
        const int maxAlphaLayers = 5;
        var w = new float[QuadVerts * QuadVerts * TexCount];
        var totals = new float[TexCount];
        for (int q = 0; q < 4; q++)
        {
            int qx = (q & 1) * 16, qy = (q >> 1) * 16;
            Array.Clear(totals);
            for (int ly = 0; ly < QuadVerts; ly++)
                for (int lx = 0; lx < QuadVerts; lx++)
                {
                    var span = w.AsSpan((ly * QuadVerts + lx) * TexCount, TexCount);
                    Weights(f, map, vx0 + qx + lx, vy0 + qy + ly, peak, span);
                    for (int t = 0; t < TexCount; t++) totals[t] += span[t];
                }

            var order = Enumerable.Range(0, TexCount).Where(t => totals[t] > 0.05f).OrderByDescending(t => totals[t]).Take(maxAlphaLayers + 1).ToArray();
            var quadrant = (Quadrant)q;
            yield return new BaseLayer { Header = new LayerHeader { Texture = Form(order[0]).ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant } };

            for (int li = 1; li < order.Length; li++)
            {
                var layer = new AlphaLayer { Header = new LayerHeader { Texture = Form(order[li]).ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant, LayerNumber = (ushort)(li - 1) }, AlphaLayerData = [] };
                for (int i = 0; i < QuadVerts * QuadVerts; i++)
                {
                    var span = w.AsSpan(i * TexCount, TexCount);
                    float below = 0;
                    for (int k = 0; k <= li; k++) below += span[order[k]];
                    float alpha = below > 0 ? span[order[li]] / below : 0;
                    if (alpha > 0.004f)
                        layer.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)i, Opacity = Math.Min(alpha, 1f) });
                }
                if (layer.AlphaLayerData.Count > 0) yield return layer;
            }
        }
    }
}
