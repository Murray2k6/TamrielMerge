using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's towns, each as its own worldspace (a town is far bigger than the patch of the overland map it sits
/// on): the RMB blocks laid out as Daggerfall Unity lays them (<see cref="DfTownLayout"/>), at the dungeons' scale.
/// <list type="bullet">
/// <item>Ground: flat land, each vertex painted from the Daggerfall ground tiles around it, measured by colour into
/// water, dirt, grass, rock and cobbles and drawn with the town style's Skyrim landscape textures, with a margin of
/// open ground around the town.</item>
/// <item>Buildings: one static per block (its buildings' shells and loose models) in Daggerfall's geometry with Skyrim
/// textures in the style of the town's climate (<see cref="TownTextures"/>).</item>
/// <item>Interiors: one interior cell per building with a door, built like a dungeon
/// (<see cref="DungeonCellBuilder.BuildBuildingInterior"/>); each door surface on the street gets a style door linked to
/// the nearest door surface inside.</item>
/// <item>The town is entered by walking onto its spot on the overland map and left by walking off its edge.</item>
/// </list>
/// </summary>
public sealed class TownBuilder(SkyrimMod mod, string arena2, IReadOnlyDictionary<uint, DfModel> models, DfBsa blocksBsa, DfWorldMap map, DungeonCellBuilder cells)
{
    /// <summary>Daggerfall's townsfolk; without it the buildings stand empty.</summary>
    public TownPeople? People { get; init; }
    /// <summary>Everyone placed so far, for conversations.</summary>
    public List<TownPeople.Person> Placed { get; } = [];

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey AutoDoor = Sk(0x0180D8);   // AutoLoadDoorMinUse01: loads when walked into
    static readonly FormKey XMarkerHeading = Sk(0x34);
    const int Persistent = 0x400;
    const int Margin = 2;   // cells of open ground around the town

    internal static FormKey StyleDoor(TownStyle s) => s switch
    {
        TownStyle.Woodland => Sk(0x094193),   // WRDoor02Load
        TownStyle.Mountain => Sk(0x02ABAB),   // NorDoorSmLoad01
        TownStyle.Swamp => Sk(0x04D6ED),      // RiftenDoorLoad01
        _ => Sk(0x04203A),                    // ImpDoorSingleLoad01
    };

    // Ground classes, in this order: water, dirt, grass, rock, cobbles.
    const int Classes = 5, Cobbles = 4;
    static readonly Dictionary<TownStyle, FormKey[]> GroundForms = new()
    {
        [TownStyle.Woodland] = [Sk(0x0799BB), Sk(0x0B424C), Sk(0x013428), Sk(0x06DE8E)],   // LRiverBottom01NoGrass, LDirtPath01, LFieldGrass01, LRocks01NoRocks
        [TownStyle.Desert] = [Sk(0x0799BB), Sk(0x06B3CF), Sk(0x000C1E), Sk(0x06DE8E)],     // .., LCoastBeach01, LScrub01, ..
        [TownStyle.Mountain] = [Sk(0x0799BB), Sk(0x0A2741), Sk(0x024E30), Sk(0x06DE8B)],   // .., LTundra01NoGrass, LTundra01, LTundraRocks01NoRocks
        [TownStyle.Swamp] = [Sk(0x098E6C), Sk(0x057DCF), Sk(0x058606), Sk(0x0AEFA2)],      // LRiverMud01NoGrass, LFallForestDirt01, LFallForestGrass01, LFallForestRocks01NoRocks
    };

    TownTextures? textures;
    TownTextures Textures => textures ??= new TownTextures(arena2, cells.TextureExport);
    readonly Dictionary<string, DfTownBlock> blockCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(int Archive, int Record), float[]> tileMix = new();
    LandscapeTexture? cobbles;
    byte[]? palette;

    public sealed record Built(Worldspace World, PlacedObject Entrance, int Buildings, int Interiors, int Doors, int Unlinked, Dictionary<string, int> Fixtures,
        Location Location, Dictionary<DfBuildingType, List<PlacedObject>> DoorsByType, int Region = -1);

    /// <summary>The cobbled-street landscape texture: LDirtPath01's settings with Windhelm's street stones.</summary>
    LandscapeTexture CobbleTexture(ISkyrimModGetter skyrim)
    {
        if (cobbles is not null) return cobbles;
        var set = new TextureSet(mod)
        {
            EditorID = "DFTownCobblesSet",
            ObjectBounds = new ObjectBounds(),
            Diffuse = @"architecture\windhelm\whstreetstone01.dds",
            NormalOrGloss = @"architecture\windhelm\whstreetstone01_n.dds",
        };
        mod.TextureSets.Add(set);
        var template = skyrim.LandscapeTextures.First(t => t.FormKey == Sk(0x0B424C));
        cobbles = mod.LandscapeTextures.DuplicateInAsNewRecord(template);
        cobbles.EditorID = "DFTownCobbles";
        cobbles.TextureSet = set.ToNullableLink<ITextureSetGetter>();
        cobbles.Grasses.Clear();
        return cobbles;
    }

    /// <summary>Terrain tile archive of a style (Daggerfall Unity ClimateSwaps).</summary>
    int TerrainArchive(TownStyle s)
    {
        int a = s switch { TownStyle.Desert => 2, TownStyle.Mountain => 102, TownStyle.Swamp => 402, _ => 302 };
        return DfTexture.Load(arena2, a) is null ? 302 : a;
    }

    /// <summary>How much of a ground tile is water, dirt, grass, rock and cobbles, measured from its pixels' colours.</summary>
    float[] TileMix(int archive, int record)
    {
        if (tileMix.TryGetValue((archive, record), out var hit)) return hit;
        palette ??= DfTexture.Palette(arena2);
        var mix = new float[Classes];
        var tex = DfTexture.Load(arena2, archive);
        if (tex is null || record >= tex.Count) { mix[1] = 1; return tileMix[(archive, record)] = mix; }
        var img = tex.Image(record);
        foreach (var p in img.Pixels)
        {
            int r = palette[p * 3], g = palette[p * 3 + 1], b = palette[p * 3 + 2];
            int spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            int k = b > r + 20 && b > g + 5 ? 0
                  : g > r + 8 && g > b ? 2
                  : spread < 28 ? ((r + g + b) / 3 > 150 ? Cobbles : 3)
                  : 1;
            mix[k]++;
        }
        float total = mix.Sum();
        if (total > 0) for (int i = 0; i < Classes; i++) mix[i] /= total;
        return tileMix[(archive, record)] = mix;
    }

    DfTownBlock? Block(string name)
    {
        if (blockCache.TryGetValue(name, out var b)) return b;
        string? resolved = DfTownBlock.Resolve(blocksBsa, name);
        if (resolved is null) return null;
        return blockCache[name] = DfTownBlock.Load(blocksBsa, resolved);
    }

    static P3Float ToSkyrim(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);

    static string BuildingName(DfBuildingType t) => t switch
    {
        DfBuildingType.Alchemist => "Alchemist", DfBuildingType.HouseForSale => "House for Sale", DfBuildingType.Armorer => "Armorer",
        DfBuildingType.Bank => "Bank", DfBuildingType.Bookseller => "Bookseller", DfBuildingType.ClothingStore => "Clothing Store",
        DfBuildingType.FurnitureStore => "Furniture Store", DfBuildingType.GemStore => "Gem Store", DfBuildingType.GeneralStore => "General Store",
        DfBuildingType.Library => "Library", DfBuildingType.GuildHall => "Guild Hall", DfBuildingType.PawnShop => "Pawn Shop",
        DfBuildingType.WeaponSmith => "Weaponsmith", DfBuildingType.Temple => "Temple", DfBuildingType.Tavern => "Tavern",
        DfBuildingType.Palace => "Palace", DfBuildingType.Ship => "Ship",
        _ => "House",
    };

    // ---- Ground ----

    static BlockMeshBuilder.Tex Land(string name) => new($@"textures\landscape\{name}.dds", $@"textures\landscape\{name}_n.dds", false);
    static readonly BlockMeshBuilder.Tex CobbleStones = new(@"textures\architecture\windhelm\whstreetstone01.dds", @"textures\architecture\windhelm\whstreetstone01_n.dds", false);

    /// <summary>Ground textures by class (water, dirt, grass, rock, cobbles) for a style.</summary>
    static BlockMeshBuilder.Tex[] GroundTextures(TownStyle s) => s switch
    {
        TownStyle.Desert => [Land("rivermud"), Land("coastbeach01"), Land("fielddirtgrass01"), Land("rocks01"), CobbleStones],
        TownStyle.Mountain => [Land("riverbottom"), Land("tundra02"), Land("tundra01"), Land("tundrarocks01"), CobbleStones],
        TownStyle.Swamp => [Land("rivermud"), Land("fallforestdirt01"), Land("fallforestgrass01"), Land("fallforestrocks01"), CobbleStones],
        _ => [Land("riverbottom"), Land("dirtpath01"), Land("fieldgrass01"), Land("rocks01"), CobbleStones],
    };

    static (Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex) Quad(float x0, float y0, float size, BlockMeshBuilder.Tex tex) =>
        ([new(x0, y0, 0), new(x0 + size, y0, 0), new(x0 + size, y0 + size, 0), new(x0, y0 + size, 0)], Vector3.UnitZ, tex);

    /// <summary>A block's ground: its 16 x 16 Daggerfall tiles, each drawn with the style's texture for the tile's main surface.</summary>
    List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> BlockGround(DfTownBlock tb, TownStyle style) =>
        Textures.GroundPolygons(tb, style);

    // ---- Doors of shared interiors ----

    readonly Dictionary<(TownStyle, bool), FormKey> styleActivators = new();

    /// <summary>
    /// The scripted doors of the style: the street door (TMDfStreetDoor on each reference) and the way out of a shared
    /// interior (TMDfExitDoor), as activators wearing the style door's model.
    /// </summary>
    FormKey DoorActivator(ISkyrimModGetter skyrim, TownStyle style, bool exit)
    {
        if (styleActivators.TryGetValue((style, exit), out var hit)) return hit;
        var door = skyrim.Doors.First(d => d.FormKey == StyleDoor(style));
        var act = new Mutagen.Bethesda.Skyrim.Activator(mod)
        {
            EditorID = $"DF{(exit ? "Exit" : "Street")}Door{style}",
            Name = "Door",
            ObjectBounds = door.ObjectBounds.DeepCopy(),
            Model = door.Model?.DeepCopy(),
            ActivateTextOverride = exit ? "Leave" : "Enter",
        };
        if (exit)
            act.VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMDfExitDoor", Properties = [new ScriptObjectProperty { Name = "Talk", Object = TalkQuest.ToLink<ISkyrimMajorRecordGetter>() }] }],
            };
        mod.Activators.Add(act);
        return styleActivators[(style, exit)] = act.FormKey;
    }

    /// <summary>TMDfExitDoor on an exit reference (its base's script, with the street door it falls back to).</summary>
    VirtualMachineAdapter ExitScript(PlacedObject fallback) => new()
    {
        Scripts =
        [
            new ScriptEntry
            {
                Name = "TMDfExitDoor",
                Properties =
                [
                    new ScriptObjectProperty { Name = "Talk", Object = TalkQuest.ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptObjectProperty { Name = "Fallback", Object = fallback.ToLink<ISkyrimMajorRecordGetter>() },
                ],
            },
        ],
    };

    Quest? talkQuest;
    /// <summary>The conversation quest (filled by <see cref="TownTalk"/>), which the door scripts remember the way back on.</summary>
    public Quest TalkQuest => talkQuest ??= NewTalkQuest();

    GlobalShort? breakIns;
    /// <summary>Shops the player has picked their way into (street doors count them; the Thieves Guild notices).</summary>
    public GlobalShort BreakIns
    {
        get
        {
            if (breakIns is null) { breakIns = new GlobalShort(mod) { EditorID = "DFBreakIns", Data = 0 }; mod.Globals.Add(breakIns); }
            return breakIns;
        }
    }
    Quest NewTalkQuest()
    {
        var q = new Quest(mod) { EditorID = "DFTalk", Name = "Daggerfall Conversations" };
        mod.Quests.Add(q);
        return q;
    }

    /// <summary>A building interior shared by every town with that block, style and region race.</summary>
    sealed record Shared(DungeonCellBuilder.BuiltInterior Built, List<(PlacedObject Exit, PlacedObject Arrival)> Arrivals, List<(PlacedNpc Ref, DfRdbObject Flat)> Occupants);
    readonly Dictionary<string, Shared> sharedInteriors = new();
    int townCount;

    public int SharedInteriors => sharedInteriors.Count;

    DfText? dfText;
    /// <summary>TEXT.RSC (the castles' names).</summary>
    DfText DfTextFile => dfText ??= DfText.Load(arena2);

    public Built Build(DfLocation loc, ISkyrimModGetter skyrim, Worldspace overland, P3Float overlandSpot)
    {
        var style = TownTextures.StyleOf(map.ClimateAt(loc.PixelX, loc.PixelY));
        bool redguard = loc.Region < DfFactions.RegionIsRedguard.Length && DfFactions.RegionIsRedguard[loc.Region];
        string id = LocationPlacer.Sanitize(loc.Name) + loc.Region;
        int townIndex = townCount++;
        // Daggerfall's names for the named buildings (DfBuildingNames), on their street doors for the quests.
        var namedBuildings = DfBuildingNames.Assign(loc, n => Block(n));
        int W = loc.BlockWidth, H = loc.BlockHeight;
        // Town space (Daggerfall units) to Skyrim: scaled, centred on the worldspace origin.
        var offset = new Vector2(-W * DfTownBlock.Side * BlockMeshBuilder.Scale / 2, -H * DfTownBlock.Side * BlockMeshBuilder.Scale / 2);
        P3Float Place(Vector3 df) { var s = ToSkyrim(df); return new P3Float(s.X + offset.X, s.Y + offset.Y, s.Z); }

        int halfX = (int)MathF.Ceiling(W * DfTownBlock.Side * BlockMeshBuilder.Scale / 2 / 4096), halfY = (int)MathF.Ceiling(H * DfTownBlock.Side * BlockMeshBuilder.Scale / 2 / 4096);
        int minX = -halfX - Margin, maxX = halfX + Margin - 1, minY = -halfY - Margin, maxY = halfY + Margin - 1;
        var ws = new Worldspace(mod)
        {
            EditorID = "DFT" + id,
            Name = loc.Name,
            Climate = overland.Climate,
            Water = overland.Water,
            LodWater = overland.LodWater,
            LodWaterHeight = -16000,
            // No terrain: the ground is a mesh per block (a town's land would be too many land records for every town).
            Flags = Worldspace.Flag.NoLandscape,
            LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -16000, DefaultWaterHeight = -16000 },
            ObjectBoundsMin = new P2Float(minX, minY),
            ObjectBoundsMax = new P2Float(maxX + 1, maxY + 1),
            WorldMapOffsetScale = 1,
            DistantLodMultiplier = 1,
            Music = overland.Music,
            MapData = new WorldspaceMap
            {
                NorthwestCellCoords = new P2Int16((short)minX, (short)maxY),
                SoutheastCellCoords = new P2Int16((short)maxX, (short)minY),
                CameraMinHeight = 50000, CameraMaxHeight = 80000, CameraInitialPitch = 50,
            },
            TopCell = new Cell(mod) { Persistent = [], Temporary = [] },
        };
        mod.Worldspaces.Add(ws);
        // The town as a location (its streets), so conversations know which town they're in.
        var townLocation = new Location(mod) { EditorID = "DFLoc" + id, Name = loc.Name };
        mod.Locations.Add(townLocation);
        ws.Location = townLocation.ToNullableLink<ILocationGetter>();
        var doorsByType = new Dictionary<DfBuildingType, List<PlacedObject>>();

        // Cells (no land), grouped as Skyrim groups them.
        var cellsByGrid = new Dictionary<(int, int), Cell>();
        var blocksGroup = new Dictionary<(int, int), WorldspaceBlock>();
        var subGroup = new Dictionary<(int, int), WorldspaceSubBlock>();
        for (int cy = minY; cy <= maxY; cy++)
            for (int cx = minX; cx <= maxX; cx++)
            {
                var cell = new Cell(mod) { Grid = new CellGrid { Point = new P2Int(cx, cy) }, Persistent = [], Temporary = [] };
                cellsByGrid[(cx, cy)] = cell;
                var bk = (FloorDiv(cx, 32), FloorDiv(cy, 32)); var sk = (FloorDiv(cx, 8), FloorDiv(cy, 8));
                if (!blocksGroup.TryGetValue(bk, out var block)) { block = new WorldspaceBlock { BlockNumberX = (short)bk.Item1, BlockNumberY = (short)bk.Item2, GroupType = GroupTypeEnum.ExteriorCellBlock }; blocksGroup[bk] = block; ws.SubCells.Add(block); }
                if (!subGroup.TryGetValue(sk, out var sub)) { sub = new WorldspaceSubBlock { BlockNumberX = (short)sk.Item1, BlockNumberY = (short)sk.Item2, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; subGroup[sk] = sub; block.Items.Add(sub); }
                sub.Items.Add(cell);
            }
        var solids = new Dictionary<(int, int), List<Vector3[]>>();
        void AddSolids(IEnumerable<Vector3[]> polygons, P3Float at)
        {
            foreach (var pts in polygons)
                for (int i = 1; i + 1 < pts.Length; i++)
                {
                    var tri = new[] { pts[0], pts[i], pts[i + 1] }.Select(v => new Vector3(v.X + at.X, v.Y + at.Y, v.Z)).ToArray();
                    var c = (tri[0] + tri[1] + tri[2]) / 3;
                    var key = ((int)MathF.Floor(c.X / 4096), (int)MathF.Floor(c.Y / 4096));
                    if (!solids.TryGetValue(key, out var list)) solids[key] = list = [];
                    list.Add(tri);
                }
        }
        void PlaceStatic(Static s, P3Float at, P3Float centre) =>
            CellAt(cellsByGrid, centre).Temporary.Add(new PlacedObject(mod) { Base = s.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, 0) } });

        // Open ground around the town: one grass tile per margin cell.
        var grass = GroundTextures(style)[2];
        var (marginTile, marginPolys) = cells.GroundStatic($@"{style}\Margin", () => [Quad(0, 0, 4096, grass)]);
        float townMinX = offset.X, townMinY = offset.Y, townMaxX = -offset.X, townMaxY = -offset.Y;
        foreach (var ((cx, cy), _) in cellsByGrid)
        {
            float x0 = cx * 4096, y0 = cy * 4096;
            if (x0 >= townMinX && x0 + 4096 <= townMaxX && y0 >= townMinY && y0 + 4096 <= townMaxY) continue;
            var at = new P3Float(x0, y0, 0);
            PlaceStatic(marginTile, at, new P3Float(x0 + 2048, y0 + 2048, 0));
            AddSolids(marginPolys.Select(p => p.Pts), at);
        }

        // Blocks: buildings, ground, street; building doors into shared interiors.
        var picker = new BlockMeshBuilder.Picker((a, r, n) => Textures.Pick(a, r, n, style, interior: false));
        var interiorPicker = new BlockMeshBuilder.Picker((a, r, n) => Textures.Pick(a, r, n, style, interior: true));
        var doorBase = StyleDoor(style);
        var streetDoorBase = DoorActivator(skyrim, style, exit: false);
        var exitDoorBase = DoorActivator(skyrim, style, exit: true);
        int buildings = 0, interiors = 0, doors = 0, unlinked = 0;
        var fixtures = new Dictionary<string, int>();
        var dungeonEntrances = new List<(DfTownLayout.DoorSurface Surface, Vector3 Origin)>();
        for (int by = 0; by < H; by++)
            for (int bx = 0; bx < W; bx++)
            {
                if (Block(loc.ExteriorBlocks[by * W + bx]) is not { } tb) continue;
                var origin = DfTownLayout.BlockOrigin(bx, by);
                var ext = DfTownLayout.Exterior(tb, DfTownLayout.NatureArchiveOf(map.ClimateAt(loc.PixelX, loc.PixelY)));
                string blockKey = Path.GetFileNameWithoutExtension(tb.Name);
                var blockAt = Place(origin);
                var centre = new P3Float(blockAt.X + DfTownBlock.Side * BlockMeshBuilder.Scale / 2, blockAt.Y + DfTownBlock.Side * BlockMeshBuilder.Scale / 2, 0);
                PlaceStatic(cells.TownStatic(ext, $@"{style}\{blockKey}", picker), blockAt, centre);
                var (ground, groundPolys) = cells.GroundStatic($@"{style}\{blockKey}", () => BlockGround(tb, style));
                PlaceStatic(ground, blockAt, centre);
                AddSolids(groundPolys.Select(p => p.Pts), blockAt);
                AddSolids(BlockMeshBuilder.SolidPolygons(ext, models, picker).Select(p => p.Points), blockAt);
                // Statues face away from the buildings round them (block space, Daggerfall units).
                var extPolys = DungeonCellBuilder.BlockPolygons(ext, models);
                var rooms = new RoomFacing(extPolys);
                var extFloors = extPolys.Where(q => q.Normal.Y > 0.7f).ToList();
                PlaceStreet(ext, loc, bx, by, origin, skyrim, p => Place(p), cellsByGrid, fixtures, rooms);
                // Outdoor props (benches, crates, rocks, statues, the anvil): cut from the block mesh, placed as vanilla objects.
                foreach (var o in ext.Objects.Where(o => o.Type == DfRdbObjectType.Model && PropReplacer.IsReplaced(o.ModelId)))
                    if (models.TryGetValue(o.ModelId, out var propModel)
                        && cells.Props.Replace(o, origin, propModel, DungeonCellBuilder.PropFacing(rooms, o.Position, propModel, extFloors)) is { } prop)
                    {
                        var at = new P3Float(prop.PositionSkyrim.X + offset.X, prop.PositionSkyrim.Y + offset.Y, prop.PositionSkyrim.Z);
                        // Daggerfall's own model with the stand-in's use; the stand-in only if that fails.
                        var r = cells.PropBuilder.Place(o, origin, propModel, prop, new Vector3(offset.X, offset.Y, 0))
                            ?? new PlacedObject(mod) { Base = prop.Base.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, prop.Heading) }, Scale = MathF.Abs(prop.Scale - 1) > 0.01f ? prop.Scale : null };
                        CellAt(cellsByGrid, at).Temporary.Add(r);
                        fixtures["street: " + prop.Kind] = fixtures.GetValueOrDefault("street: " + prop.Kind) + 1;
                    }

                foreach (var s in DfTownLayout.DungeonEntrances(ext, models)) dungeonEntrances.Add((s, origin));
                var streetDoors = DfTownLayout.Doors(ext, models).Where(d => d.Building >= 0).GroupBy(d => d.Building).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var b in tb.Buildings)
                {
                    buildings++;
                    if (!streetDoors.TryGetValue(b.Index, out var outside) || b.Interior.Models.Count == 0) continue;
                    var interiorBlock = DfTownLayout.Interior(tb, b, models);
                    // One interior per layout, style and region race, shared by every town that has it.
                    string key = $@"{style}\{interiorBlock.Name}\{(redguard ? "R" : "B")}";
                    if (!sharedInteriors.TryGetValue(key, out var shared))
                    {
                        var built = cells.BuildBuildingInterior($"DFI{LocationPlacer.Sanitize(key)}", BuildingName(b.Data.Type), interiorBlock, $@"{style}\{interiorBlock.Name}", interiorPicker, doorBase, exitDoorBase);
                        built.Cell.Location = new FormLinkNullable<ILocationGetter>(overland.Location.FormKeyNullable ?? FormKey.Null);
                        interiors++;
                        foreach (var (k, v) in built.Fixtures) fixtures[k] = fixtures.GetValueOrDefault(k) + v;
                        // Where each way in arrives: in front of the exit nearest the street door.
                        var arrivals = new List<(PlacedObject, PlacedObject)>();
                        foreach (var (exit, surface) in built.Exits)
                        {
                            var (_, insideArrival, insideHeading) = cells.SurfaceDoor(surface, Vector3.Zero, doorBase);
                            var marker = new PlacedObject(mod)
                            {
                                Base = XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                                Placement = new Placement { Position = insideArrival, Rotation = new P3Float(0, 0, insideHeading) },
                                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
                            };
                            built.Cell.Persistent.Add(marker);
                            arrivals.Add((exit, marker));
                        }
                        var occupants = new List<(PlacedNpc, DfRdbObject)>();
                        var persons = new List<TownPeople.Person>();
                        if (People is { } people)
                            foreach (var flat in built.People)
                            {
                                int recordOffset = -1 - flat.RecordOffset;
                                if (people.Make(loc, bx, by, b.Index, b.Data.Type, flat, recordOffset) is not { } person) continue;
                                var at = ToSkyrim(flat.Position);
                                var npcRef = new PlacedNpc(mod)
                                {
                                    Base = person.Npc.ToNullableLink<INpcGetter>(),
                                    Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z + 4), Rotation = new P3Float(0, 0, (recordOffset % 360) * MathF.PI / 180) },
                                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
                                };
                                built.Cell.Persistent.Add(npcRef);
                                occupants.Add((npcRef, flat));
                                persons.Add(person);
                                Placed.Add(person);
                                fixtures["npc: " + person.Group] = fixtures.GetValueOrDefault("npc: " + person.Group) + 1;
                            }
                        if (Shops?.Equip(built.Cell, LocationPlacer.Sanitize(key), b.Data.Type, persons, b.Data.FactionId) is not null)
                            fixtures["shop: " + b.Data.Type] = fixtures.GetValueOrDefault("shop: " + b.Data.Type) + 1;
                        sharedInteriors[key] = shared = new Shared(built, arrivals, occupants);
                    }
                    if (shared.Arrivals.Count == 0) { unlinked += outside.Count; continue; }
                    // This town's names for the people inside.
                    var names = People is { } pp ? shared.Occupants.Select(o => pp.NameIn(loc, bx, by, b.Index, b.Data.Type, o.Flat, -1 - o.Flat.RecordOffset)).ToList() : [];
                    // Street doors are in block space; the rooms are in the building's own (unrotated) space.
                    var bOrigin = DfTownLayout.BuildingOrigin(b);
                    var unturn = DfDungeon.UnityRotY(DfTownLayout.BuildingYaw(b) * MathF.PI / 180f);
                    PlacedObject? firstStreet = null;
                    foreach (var s in outside)
                    {
                        var (street, _, _) = cells.SurfaceDoor(s, origin, doorBase, streetDoorBase);
                        street.Placement!.Position = new P3Float(street.Placement.Position.X + offset.X, street.Placement.Position.Y + offset.Y, street.Placement.Position.Z);
                        var local = Vector3.Transform(s.Centre - bOrigin, unturn);
                        var (exit, arrival) = shared.Arrivals.OrderBy(e => Vector2.DistanceSquared(
                            new(e.Exit.Placement!.Position.X, e.Exit.Placement.Position.Y), new(local.X * BlockMeshBuilder.Scale, local.Z * BlockMeshBuilder.Scale))).First();
                        var entry = new ScriptEntry { Name = "TMDfStreetDoor" };
                        entry.Properties.Add(new ScriptObjectProperty { Name = "Talk", Object = TalkQuest.ToLink<ISkyrimMajorRecordGetter>() });
                        entry.Properties.Add(new ScriptObjectProperty { Name = "Arrival", Object = arrival.ToLink<ISkyrimMajorRecordGetter>() });
                        entry.Properties.Add(new ScriptIntProperty { Name = "Town", Data = townIndex });
                        // Daggerfall's hours and lock (Daggerfall Unity GetBuildingLockValue: quality / 2).
                        entry.Properties.Add(new ScriptIntProperty { Name = "BuildingType", Data = (int)b.Data.Type });
                        entry.Properties.Add(new ScriptIntProperty { Name = "LockValue", Data = b.Data.Quality / 2 });
                        entry.Properties.Add(new ScriptObjectProperty { Name = "BreakIns", Object = BreakIns.ToLink<ISkyrimMajorRecordGetter>() });
                        if (People is { } namer && namedBuildings.TryGetValue((bx, by, b.Index), out var nb)
                            && DfBuildingNames.Name(nb.Seed, b.Data.Type, nb.Faction, loc.Name, loc.Region, namer.Factions, namer.Names, DfTextFile) is { } buildingName)
                            entry.Properties.Add(new ScriptStringProperty { Name = "BuildingName", Data = buildingName });
                        if (names.Count > 0)
                        {
                            var occ = new ScriptObjectListProperty { Name = "Occupants" };
                            foreach (var (npcRef, _) in shared.Occupants) occ.Objects.Add(new ScriptObjectProperty { Object = npcRef.ToLink<ISkyrimMajorRecordGetter>() });
                            entry.Properties.Add(occ);
                            entry.Properties.Add(new ScriptStringListProperty { Name = "Names", Data = [.. names] });
                        }
                        street.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
                        CellAt(cellsByGrid, street.Placement.Position).Persistent.Add(street);
                        // The exit's way out when the player didn't come in by a street door (coc, older saves): the first
                        // town's street door nearest it.
                        exit.VirtualMachineAdapter ??= ExitScript(street);
                        firstStreet ??= street;
                        doors++;
                        if (!doorsByType.TryGetValue(b.Data.Type, out var typed)) doorsByType[b.Data.Type] = typed = [];
                        typed.Add(street);
                    }
                    if (firstStreet is not null)
                        foreach (var (exit, _) in shared.Arrivals) exit.VirtualMachineAdapter ??= ExitScript(firstStreet);
                }
            }

        // The town's own dungeon (a castle, a sewer), behind its dungeon entrance: the dungeon's door stands in front
        // of Daggerfall's entrance surface.
        if (Dungeons && loc.DungeonBlocks.Count > 0 && dungeonEntrances.Count > 0)
        {
            var (s, o) = dungeonEntrances[0];
            var front = new Vector3(s.Normal.X, 0, s.Normal.Z);
            front = front.LengthSquared() > 1e-6f ? Vector3.Normalize(front) : Vector3.UnitZ;
            var spot = Place(o + s.Centre + front * 48);
            spot = new P3Float(spot.X, spot.Y, spot.Z - s.Height * BlockMeshBuilder.Scale / 2);
            try
            {
                var dungeon = cells.Build(loc, ws, spot);
                fixtures[$"dungeon {dungeon.Cell.EditorID} ({dungeon.Cell.Temporary.Count} refs)"] = 1;
            }
            catch (Exception e) { fixtures["dungeon failed: " + e.Message] = 1; }
        }

        // In and out: walk onto the town's spot on the overland map to enter at its southern edge; walk off any
        // edge to leave.
        var entrance = new PlacedObject(mod)
        {
            EditorID = "DFTownEntrance" + id,
            Base = AutoDoor.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = overlandSpot, Rotation = new P3Float(0, 0, 0) },
            Scale = 4,
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
        };
        overland.TopCell!.Persistent.Add(entrance);
        float southY = minY * 4096 + 2048, edgeX = (minX + maxX + 1) * 4096 / 2f;
        var arrive = new P3Float(edgeX, southY + 1024, 16);
        var edgeDoors = new List<IPlacedObjectGetter>();
        foreach (var (pos, rot) in new[] { (new P3Float(edgeX, southY, 0), 0f), (new P3Float(edgeX, (maxY + 1) * 4096 - 2048, 0), MathF.PI),
                                          (new P3Float(minX * 4096 + 2048, 0, 0), MathF.PI / 2), (new P3Float((maxX + 1) * 4096 - 2048, 0, 0), -MathF.PI / 2) })
        {
            var exit = new PlacedObject(mod)
            {
                Base = AutoDoor.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = pos, Rotation = new P3Float(0, 0, rot) },
                Scale = 10,
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
                TeleportDestination = new TeleportDestination
                {
                    Door = entrance.ToLink<IPlacedObjectGetter>(),
                    Position = new P3Float(overlandSpot.X, overlandSpot.Y - 900, overlandSpot.Z + 64),
                    Rotation = new P3Float(0, 0, MathF.PI),
                },
            };
            CellAt(cellsByGrid, pos).Persistent.Add(exit);
            edgeDoors.Add(exit);
        }
        entrance.TeleportDestination = new TeleportDestination { Door = ((PlacedObject)edgeDoors[0]).ToLink<IPlacedObjectGetter>(), Position = arrive, Rotation = new P3Float(0, 0, 0) };

        PlaceWanderers(loc, doorsByType.Values.SelectMany(d => d).ToList(), cellsByGrid, fixtures, ws);

        // Streets navmeshed over the ground and the buildings' collision.
        var problems = new List<string>();
        // Cells of the same blocks (in this town or another of the part) share Recast's work (reuse).
        var (navs, navTris) = cells.Navmeshes.BuildExteriorCells(ws, cellsByGrid, (x, y) => solids.TryGetValue((x, y), out var l) ? l : [], _ => null, edgeDoors, problems, reuse: true);
        fixtures[$"street navmeshes ({navTris} triangles)"] = navs;
        foreach (var p in problems.Take(5)) fixtures["navmesh problem: " + p] = 1;
        return new Built(ws, entrance, buildings, interiors, doors, unlinked, fixtures, townLocation, doorsByType, loc.Region);
    }

    /// <summary>Build each town's own dungeon (castles, sewers) behind its entrance.</summary>
    public bool Dungeons { get; init; } = true;

    /// <summary>Puts TMDfDaytime (the street population's day and night) on the conversation quest; call after the
    /// conversations are built (they set the quest's scripts).</summary>
    public void AttachDaytime()
    {
        if (daytime is null) return;
        TalkQuest.VirtualMachineAdapter ??= new QuestAdapter();
        TalkQuest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
        {
            Name = "TMDfDaytime",
            Properties =
            [
                new ScriptObjectProperty { Name = "Marker", Object = daytime.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "GameHour", Object = Sk(0x38).ToLink<ISkyrimMajorRecordGetter>() },
            ],
        });
    }

    PlacedObject? daytime;
    /// <summary>
    /// The street population's enable parent: enabled by day, disabled at night (TMDfDaytime on the conversation
    /// quest), as Daggerfall's mobile townsfolk are out only between 6:00 and 18:00. Null until a town has wanderers.
    /// </summary>
    public PlacedObject? DaytimeMarker => daytime;

    /// <summary>
    /// Daggerfall's street population (Daggerfall Unity PopulationManager: 24 per 16 blocks, 1 to 4 lots; the region's
    /// race; one in 32 a guard), as people of the region's pool standing a few paces out from the town's street doors
    /// and wandering the streets by day.
    /// </summary>
    void PlaceWanderers(DfLocation loc, List<PlacedObject> streetDoors, Dictionary<(int, int), Cell> grid, Dictionary<string, int> counts, Worldspace ws)
    {
        if (People is not { } people || streetDoors.Count == 0) return;
        if (daytime is null)
        {
            daytime = new PlacedObject(mod)
            {
                EditorID = "DFTownDaytimeMarker",
                Base = Sk(0x3B).ToNullableLink<IPlaceableObjectGetter>(),   // XMarker
                Placement = new Placement { Position = new P3Float(0, 0, -15000), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
            };
            ws.TopCell!.Persistent.Add(daytime);
        }
        int count = Math.Clamp(loc.BlockWidth * loc.BlockHeight / 16, 1, 4) * 24;
        var random = new Random(loc.Index * 7919 + loc.Region);
        for (int i = 0; i < count; i++)
        {
            bool guard = i % 32 == 31;
            if (people.Wanderer(loc, random.Next(TownPeople.WandererSlots), guard, out bool created) is not { } person) continue;
            if (created) Placed.Add(person);
            var door = streetDoors[random.Next(streetDoors.Count)].Placement!;
            float a = door.Rotation.Z, out_ = 220 + random.Next(160);
            var at = new P3Float(door.Position.X - MathF.Sin(a) * out_, door.Position.Y - MathF.Cos(a) * out_, 8);
            CellAt(grid, at).Temporary.Add(new PlacedNpc(mod)
            {
                Base = person.Npc.ToNullableLink<INpcGetter>(),
                Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, (float)(random.NextDouble() * MathF.Tau)) },
                EnableParent = new EnableParent { Reference = daytime.ToLink<IPlacedGetter>(), Flags = 0 },
            });
            counts[guard ? "street guard" : "street wanderer"] = counts.GetValueOrDefault(guard ? "street guard" : "street wanderer") + 1;
        }

        // After dark the streets belong to others: the climate's "in location, night" encounters (thieves, assassins,
        // burglars, rogues, and worse) and a thinner night watch, out only while the day's people are not.
        if (Creatures is not { } creatures) return;
        var nightOnly = new EnableParent { Reference = daytime.ToLink<IPlacedGetter>(), Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent };
        int lots = Math.Clamp(loc.BlockWidth * loc.BlockHeight / 16, 1, 4);
        var stalker = creatures.Encounter(TownNights.NightTableOf(map.ClimateAt(loc.PixelX, loc.PixelY)));
        var watch = creatures.Spawn(CityWatch, passive: true);
        for (int i = 0; i < lots * 5; i++)
        {
            bool isWatch = i % 5 >= 3;   // 3 in 5 stalkers, 2 in 5 watchmen
            if ((isWatch ? watch : stalker) is not { } who) continue;
            var door = streetDoors[random.Next(streetDoors.Count)].Placement!;
            float a = door.Rotation.Z, d = 300 + random.Next(400);
            var at = new P3Float(door.Position.X - MathF.Sin(a) * d, door.Position.Y - MathF.Cos(a) * d, 8);
            CellAt(grid, at).Temporary.Add(new PlacedNpc(mod)
            {
                Base = who.ToNullableLink<INpcGetter>(),
                Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, (float)(random.NextDouble() * MathF.Tau)) },
                EnableParent = nightOnly.DeepCopy(),
            });
            counts[isWatch ? "night watch" : "night stalker"] = counts.GetValueOrDefault(isWatch ? "night watch" : "night stalker") + 1;
        }
    }

    /// <summary>Daggerfall's creatures and encounter tables, for the town at night; without it nights are quiet.</summary>
    public CreatureBuilder? Creatures { get; init; }
    const int CityWatch = 146;

    /// <summary>Shops' keepers as merchants; without it nobody trades.</summary>
    public TownShops? Shops { get; init; }

    /// <summary>Daggerfall's clutter sprites as Skyrim objects; without it the streets are bare.</summary>
    public ClutterPlacer? Clutter { get; init; }

    static readonly int[] PeopleArchives = [176, 177, 179, 181, 182, 183, 184];
    const int AnimalsArchive = 201, LightsArchive = 210, EditorArchive = 199;

    /// <summary>Daggerfall's animal sprites as Skyrim animals (pigs as goats, cats as foxes: Skyrim has neither).</summary>
    static readonly Dictionary<int, string> Animals = new()
    {
        [0] = "EncHorseBrown", [1] = "EncHorseBlack", [2] = "EncHorsePalomino", [3] = "EncCow", [4] = "EncCow",
        [5] = "EncGoatDomestic", [6] = "EncGoatDomestic", [7] = "EncFox", [8] = "EncFox", [9] = "EncDog", [10] = "EncDog",
    };

    readonly Dictionary<string, Npc?> tameAnimals = new();
    FormKey? lampPost, streetLight;
    LightFixturePlacer? streetFires;

    /// <summary>A vanilla animal made placid (town animals don't attack, as in Daggerfall).</summary>
    Npc? TameAnimal(ISkyrimModGetter skyrim, string editorId)
    {
        if (tameAnimals.TryGetValue(editorId, out var hit)) return hit;
        var source = skyrim.Npcs.FirstOrDefault(n => n.EditorID == editorId);
        if (source is null) return tameAnimals[editorId] = null;
        var copy = mod.Npcs.DuplicateInAsNewRecord(source);
        copy.EditorID = "DFTown" + editorId;
        if (copy.AIData is { } ai) { ai.Aggression = Aggression.Unaggressive; ai.Confidence = Confidence.Cowardly; }
        return tameAnimals[editorId] = copy;
    }

    /// <summary>
    /// A block's street: the people standing outdoors (as townsfolk), animals, street lamps (Riften's lamp post with a
    /// street light), and the trees, plants, wells, signs and other sprites the clutter table knows, on the ground.
    /// </summary>
    void PlaceStreet(DfDungeonBlock ext, DfLocation loc, int bx, int by, Vector3 origin, ISkyrimModGetter skyrim, Func<Vector3, P3Float> place,
        Dictionary<(int, int), Cell> grid, Dictionary<string, int> counts, RoomFacing rooms)
    {
        void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
        lampPost ??= skyrim.Statics.FirstOrDefault(s => s.EditorID == "RTLampPost01")?.FormKey;
        streetLight ??= skyrim.Lights.FirstOrDefault(s => s.EditorID == "SLightStreetStreet01")?.FormKey;
        foreach (var o in ext.Objects.Where(o => o.Type == DfRdbObjectType.Flat && o.TextureArchive != EditorArchive))
        {
            var ground = place(new Vector3(origin.X + o.Position.X, 0, origin.Z + o.Position.Z));
            uint hash = (uint)HashCode.Combine(o.RecordOffset, o.TextureRecord, (int)o.Position.X, (int)o.Position.Z);
            float heading = hash % 360 * MathF.PI / 180;
            if (o.TextureArchive == AnimalsArchive && Animals.ContainsKey(o.TextureRecord))
            {
                if (Animals.TryGetValue(o.TextureRecord, out var id) && TameAnimal(skyrim, id) is { } animal)
                {
                    CellAt(grid, ground).Temporary.Add(new PlacedNpc(mod)
                    {
                        Base = animal.ToNullableLink<INpcGetter>(),
                        Placement = new Placement { Position = new P3Float(ground.X, ground.Y, 8), Rotation = new P3Float(0, 0, heading) },
                    });
                    Count("animal: " + id);
                }
            }
            else if (o.TextureArchive is LightsArchive or LightFixturePlacer.E3LightsArchive
                && (streetFires ??= new LightFixturePlacer(arena2, [])).Fixtures(o, new Vector3(0, 0, 0)).ToList() is var fire
                && fire.Count > 0 && fire[0].Kind is "campfire" or "brazier" or "standing brazier")
            {
                // A fire in the street (a campfire, a brazier) stays a fire; lamp-like sprites are street lamps.
                foreach (var f in fire)
                    CellAt(grid, ground).Temporary.Add(new PlacedObject(mod)
                    {
                        Base = f.Base.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(ground.X, ground.Y, (f.PositionDf.Y - fire[0].PositionDf.Y) * BlockMeshBuilder.Scale), Rotation = new P3Float(0, 0, heading) },
                    });
                Count("street " + fire[0].Kind);
            }
            else if (o.TextureArchive == LightsArchive)
            {
                if (lampPost is { } post)
                    CellAt(grid, ground).Temporary.Add(new PlacedObject(mod) { Base = post.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = new P3Float(ground.X, ground.Y, 0), Rotation = new P3Float(0, 0, heading) } });
                if (streetLight is { } light)
                    CellAt(grid, ground).Temporary.Add(new PlacedObject(mod) { Base = light.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = new P3Float(ground.X, ground.Y, 200), Rotation = new P3Float(0, 0, 0) } });
                Count("street lamp");
            }
            else if ((PeopleArchives.Contains(o.TextureArchive) || o.FactionOrMobileId != 0) && People is { } people)
            {
                if (people.Make(loc, bx, by, 255, DfBuildingType.None, o, o.RecordOffset) is not { } person) continue;
                CellAt(grid, ground).Persistent.Add(new PlacedNpc(mod)
                {
                    Base = person.Npc.ToNullableLink<INpcGetter>(),
                    Placement = new Placement { Position = new P3Float(ground.X, ground.Y, 8), Rotation = new P3Float(0, 0, heading) },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
                });
                Placed.Add(person);
                Count("npc outdoors");
            }
            else if (Clutter is { } clutter && clutter.Covers(o))
            {
                float h = cells.SpriteHeight(o.TextureArchive, o.TextureRecord) * BlockMeshBuilder.Scale;
                var statueFacing = ClutterPlacer.IsStatue(o) ? DungeonCellBuilder.StatueFacing(rooms, o.Position.X, o.Position.Y - h / BlockMeshBuilder.Scale / 2, o.Position.Z) : null;
                foreach (var p in clutter.Place(o, new Vector3(ground.X, ground.Y, h / 2), h, 0, statueFacing, cells.SpriteWidth(o.TextureArchive, o.TextureRecord) * BlockMeshBuilder.Scale))
                {
                    var r = new PlacedObject(mod)
                    {
                        Base = p.Base.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(p.PositionSkyrim.X, p.PositionSkyrim.Y, p.PositionSkyrim.Z), Rotation = new P3Float(p.Rotation.X, p.Rotation.Y, p.Rotation.Z) },
                    };
                    if (MathF.Abs(p.Scale - 1) > 0.01f) r.Scale = p.Scale;
                    CellAt(grid, ground).Temporary.Add(r);
                    Count("street: " + p.Label);
                }
            }
            else Count($"street sprite {o.TextureArchive}.{o.TextureRecord} not converted");
        }
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    static Cell CellAt(Dictionary<(int, int), Cell> grid, P3Float p) =>
        grid.TryGetValue(((int)MathF.Floor(p.X / 4096), (int)MathF.Floor(p.Y / 4096)), out var c) ? c : grid.Values.First();
}
