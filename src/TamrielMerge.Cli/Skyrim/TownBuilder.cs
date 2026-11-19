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
/// Daggerfall's locations with exterior blocks (towns, and the farms, temples, taverns and dungeons of the wilds) on the
/// overland itself, as in Daggerfall (user, 2026-10-25: "towns go on the overland like in daggerfall"; docs/
/// overland-locations.md): each at its <see cref="Terrain.LocationLayout"/> footprint, the RMB blocks laid out as
/// Daggerfall Unity lays them (<see cref="DfTownLayout"/>), at the dungeons' scale.
/// <list type="bullet">
/// <item>Ground: each block's own ground mesh (its Daggerfall ground tiles drawn with the style's Skyrim textures) on the
/// land, which the land plugins level to the footprint's ground (it carries no grass there, TerrainBuilder.Settled).</item>
/// <item>Buildings: one static per block (its buildings' shells and loose models) in Daggerfall's geometry with Skyrim
/// textures in the style of the location's climate (<see cref="TownTextures"/>).</item>
/// <item>Interiors: one interior cell per building layout, shared by every location with it; each door surface on the
/// street gets a style door into it.</item>
/// <item>Its navmesh is the overland's, made after every plugin is built from what stands on the land (OverlandNavmesh).</item>
/// </list>
/// </summary>
public sealed class TownBuilder(SkyrimMod mod, string arena2, IReadOnlyDictionary<uint, DfModel> models, DfBsa blocksBsa, DfWorldMap map, DungeonCellBuilder cells)
{
    /// <summary>Daggerfall's townsfolk; without it the buildings stand empty.</summary>
    public TownPeople? People { get; init; }
    /// <summary>Everyone placed so far, for conversations.</summary>
    public List<TownPeople.Person> Placed { get; } = [];

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey XMarkerHeading = Sk(0x34);
    const int Persistent = 0x400;

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

    public sealed record Built(Worldspace World, int Buildings, int Interiors, int Doors, int Unlinked, Dictionary<string, int> Fixtures,
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

    internal DfTownBlock? Block(string name)
    {
        if (blockCache.TryGetValue(name, out var b)) return b;
        string? resolved = DfTownBlock.Resolve(blocksBsa, name);
        if (resolved is null) return null;
        return blockCache[name] = DfTownBlock.Load(blocksBsa, resolved);
    }

    static P3Float ToSkyrim(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);

    /// <summary>
    /// A street door's name for a building Daggerfall gives no name of its own (Daggerfall Unity's Internal_Strings): a
    /// house "The %s Residence" by its people's family name (the first person's last name), "Residence" with no one in
    /// it; a house for sale "House for sale"; anything else its kind.
    /// </summary>
    static string DoorLabel(DfBuildingType t, List<string> people)
    {
        if (t == DfBuildingType.HouseForSale) return "House for sale";
        if ((int)t is >= 17 and <= 22)
            return people.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim().Split(' ') is { Length: > 1 } parts ? $"The {parts[^1]} Residence" : "Residence";
        return BuildingName(t);
    }

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
            // No model: Daggerfall's door on the building's wall is the door, and each reference is a box over it
            // (DungeonCellBuilder.SurfaceDoor).
            ObjectBounds = new ObjectBounds(),
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
                    new ScriptObjectProperty { Name = "Way", Object = Way },
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
    /// <summary>Buildings whose things are someone's: houses and shops (and the bank's vault); not a house for sale, which
    /// may be the player's.</summary>
    static bool IsPrivate(DfBuildingType t) => t is DfBuildingType.Alchemist or DfBuildingType.Armorer or DfBuildingType.Bookseller
        or DfBuildingType.ClothingStore or DfBuildingType.FurnitureStore or DfBuildingType.GemStore or DfBuildingType.GeneralStore
        or DfBuildingType.PawnShop or DfBuildingType.WeaponSmith or DfBuildingType.Bank
        or DfBuildingType.House1 or DfBuildingType.House2 or DfBuildingType.House3 or DfBuildingType.House4 or DfBuildingType.House5 or DfBuildingType.House6;

    Quest NewTalkQuest()
    {
        var q = new Quest(mod) { EditorID = "DFTalk", Name = "Daggerfall Conversations" };
        mod.Quests.Add(q);
        return q;
    }

    /// <summary>
    /// A building interior shared by every town with that block, style and region race. Only its anchor is persistent
    /// (the one reference a street door moves the player to; Skyrim keeps every persistent reference in memory from the
    /// start, and 3.4 million of them in Daggerfall Towns.esm ended the game while its data loaded, 2026-10-23): each
    /// exit's arrival is a place in the cell the street door sets the player at after the move. Its people are
    /// temporary too, named for the town entered when they load (TMDfOccupant).
    /// </summary>
    /// <param name="Owned">Built here (false: another part of the plugin builds it, and this part's street doors point at
    /// its anchor by the FormID every part gives it).</param>
    /// <param name="People">The interior's person flats in its block's order: an occupant's Index is its place here, and
    /// each street door names them in this order.</param>
    sealed record Shared(bool Owned, FormKey? Anchor, List<(PlacedObject Exit, P3Float At, float Heading)> Arrivals, List<DfRdbObject> People);
    readonly Dictionary<string, Shared> sharedInteriors = new();
    int townCount;

    public int SharedInteriors => sharedInteriors.Count;
    /// <summary>Of <see cref="SharedInteriors"/>, how many this part built (the rest are other parts').</summary>
    public int SharedInteriorsBuilt => sharedInteriors.Values.Count(s => s.Owned);

    /// <summary>
    /// For a plugin built in parts: every shared interior of every part's towns (<see cref="InteriorKeys"/>, in the towns'
    /// order) with its place in that order and whether this part builds it, the part of the first town to have it. Each is
    /// then made once in the joined plugin, at FormIDs every part gives it (<see cref="FixedKeysOf"/>): 2026-10-24, each part
    /// built its own copies, 161,790 interior cells of 15,039 interiors, and Skyrim, which lists the interior cells in an
    /// array of at most 65,535 (NiTArray's 16-bit size and capacity, SKSE NiTypes.h; TESDataHandler +D08), overran it while
    /// loading the data: the heap broke (TamrielMergeCrashLog, SkyrimSE.exe+CED82B) on a Daggerfall Towns.esm interior.
    /// Unset: every interior is built here.
    /// </summary>
    public IReadOnlyDictionary<string, (int Index, bool Own)>? SharedOwners { get; set; }

    /// <summary>FormIDs per shared interior: its cell, anchor, first quest spawn marker and first quest item marker.</summary>
    const int FixedPerInterior = 4;

    (DungeonCellBuilder.FixedKeys Keys, FormKey Anchor) FixedKeysOf(int index) =>
        (new DungeonCellBuilder.FixedKeys(FixedKey(index, 0), FixedKey(index, 2), FixedKey(index, 3)), FixedKey(index, 1));
    FormKey FixedKey(int index, int slot) => new(mod.ModKey, PluginMerger.SharedId(index, slot, FixedPerInterior));

    /// <summary>The quest markers (first spawn, first item; the item the spawn when there is none) of the shared interiors
    /// other parts build, by their anchor: QuestSites names them as it names this part's own.</summary>
    public Dictionary<FormKey, (FormKey Spawn, FormKey Item)> OtherPartsMarkers { get; } = new();

    readonly Dictionary<(string Block, string Nature), HashSet<int>> buildingsWithDoors = new();

    /// <summary>
    /// The shared interiors a town's buildings go into, as <see cref="Build"/> keys them (style, interior layout, region
    /// race), in the order it meets them: every building with a street door and an inside.
    /// </summary>
    public IEnumerable<string> InteriorKeys(DfLocation loc)
    {
        var climate = map.ClimateAt(loc.PixelX, loc.PixelY);
        var style = TownTextures.StyleOf(climate);
        bool redguard = loc.Region < DfFactions.RegionIsRedguard.Length && DfFactions.RegionIsRedguard[loc.Region];
        string nature = DfTownLayout.NatureArchiveOf(climate).ToString();
        foreach (var name in loc.ExteriorBlocks)
        {
            if (Block(name) is not { } tb) continue;
            if (!buildingsWithDoors.TryGetValue((tb.Name, nature), out var withDoors))
            {
                var ext = DfTownLayout.Exterior(tb, DfTownLayout.NatureArchiveOf(climate));
                buildingsWithDoors[(tb.Name, nature)] = withDoors = DfTownLayout.Doors(ext, models).Where(d => d.Building >= 0).Select(d => d.Building).ToHashSet();
            }
            foreach (var b in tb.Buildings)
                if (withDoors.Contains(b.Index) && b.Interior.Models.Count > 0)
                    yield return $@"{style}\{Path.GetFileNameWithoutExtension(tb.Name)}_{b.Index:00}\{(redguard ? "R" : "B")}";
        }
    }

    /// <summary>The quest whose LastDoor and occupant names every shared interior reads (TMDfTalk): the plugin's own
    /// conversation quest, or, for a part of a plugin built in parts whose interiors may come from any part, the
    /// master's (Daggerfall.esm's DFTalk). Unset: <see cref="TalkQuest"/>.</summary>
    public FormKey? WayQuest { get; set; }
    FormLink<ISkyrimMajorRecordGetter> Way => new(WayQuest ?? TalkQuest.FormKey);

    /// <summary>Added to shared interiors' EditorIDs, so another plugin's interiors of the same layout (Daggerfall.esm's)
    /// keep theirs: Skyrim indexes interior cells by EditorID ("T" in Daggerfall Towns.esm; earlier builds made a copy per
    /// part, "P3" for part 3's).</summary>
    public string InteriorSuffix { get; set; } = "";

    DfText? dfText;
    /// <summary>TEXT.RSC (the castles' names).</summary>
    DfText DfTextFile => dfText ??= DfText.Load(arena2);

    /// <summary>
    /// Where a location is built on the overland (docs/overland-locations.md): the overland worldspace it stands in
    /// (<paramref name="World"/>: Daggerfall.esm's, in global coordinates, or a part's copy of the tile), its cells as
    /// the builder makes or overrides them (<paramref name="CellAt"/>, by grid in that worldspace's coordinates), and the
    /// footprint's south-west corner (the same coordinates, at the ground level the land is made under it) and size in
    /// cells.
    /// </summary>
    public sealed record OverlandSite(Worldspace World, Func<int, int, Cell> CellAt, P3Float Corner, int CellsW, int CellsH);

    public Built Build(DfLocation loc, ISkyrimModGetter skyrim, OverlandSite at)
    {
        var style = TownTextures.StyleOf(map.ClimateAt(loc.PixelX, loc.PixelY));
        bool redguard = loc.Region < DfFactions.RegionIsRedguard.Length && DfFactions.RegionIsRedguard[loc.Region];
        string id = LocationPlacer.Sanitize(loc.Name) + loc.Region;
        int townIndex = townCount++;
        // Daggerfall's names for the named buildings (DfBuildingNames), on their street doors for the quests.
        var namedBuildings = DfBuildingNames.Assign(loc, n => Block(n));
        int W = loc.BlockWidth, H = loc.BlockHeight;
        // Town space (Daggerfall units) to Skyrim: scaled, its south-west corner on the footprint's (block (0, 0) there,
        // rows running north), on the ground the land was levelled to.
        var offset = new Vector2(at.Corner.X, at.Corner.Y);
        float groundZ = at.Corner.Z;
        P3Float Place(Vector3 df) { var s = ToSkyrim(df); return new P3Float(s.X + offset.X, s.Y + offset.Y, s.Z + groundZ); }

        var ws = at.World;
        // The town as a location (its streets), so conversations know which town they're in: every cell of its ground
        // carries it.
        var townLocation = new Location(mod) { EditorID = "DFLoc" + id, Name = loc.Name };
        mod.Locations.Add(townLocation);
        var doorsByType = new Dictionary<DfBuildingType, List<PlacedObject>>();

        // Its cells: the overland's, as this part overrides them.
        int cx0 = (int)MathF.Floor(at.Corner.X / 4096), cy0 = (int)MathF.Floor(at.Corner.Y / 4096);
        var cellsByGrid = new Dictionary<(int, int), Cell>();
        for (int cy = cy0; cy < cy0 + at.CellsH; cy++)
            for (int cx = cx0; cx < cx0 + at.CellsW; cx++)
            {
                var cell = at.CellAt(cx, cy);
                cell.Location = townLocation.ToNullableLink<ILocationGetter>();
                cellsByGrid[(cx, cy)] = cell;
            }
        void PlaceStatic(Static s, P3Float p, P3Float centre) =>
            CellAt(cellsByGrid, centre).Temporary.Add(new PlacedObject(mod) { Base = s.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = p, Rotation = new P3Float(0, 0, 0) } });

        // Blocks: buildings, ground, street; building doors into shared interiors.
        var picker = new BlockMeshBuilder.Picker((a, r, n) => Textures.Pick(a, r, n, style, interior: false));
        var interiorPicker = new BlockMeshBuilder.Picker((a, r, n) => Textures.Pick(a, r, n, style, interior: true));
        var doorBase = StyleDoor(style);
        var streetDoorBase = DoorActivator(skyrim, style, exit: false);
        var exitDoorBase = DoorActivator(skyrim, style, exit: true);
        int buildings = 0, interiors = 0, doors = 0, unlinked = 0;
        var fixtures = new Dictionary<string, int>();
        for (int by = 0; by < H; by++)
            for (int bx = 0; bx < W; bx++)
            {
                if (Block(loc.ExteriorBlocks[by * W + bx]) is not { } tb) continue;
                var origin = DfTownLayout.BlockOrigin(bx, by);
                // (A graveyard without a way into its dungeon gets Daggerfall's crypt here: its own static, "..._Crypt".)
                var ext = Outside(loc, bx, by) ?? DfTownLayout.Exterior(tb, DfTownLayout.NatureArchiveOf(map.ClimateAt(loc.PixelX, loc.PixelY)));
                string blockKey = Path.GetFileNameWithoutExtension(tb.Name);
                var blockAt = Place(origin);
                var centre = new P3Float(blockAt.X + DfTownBlock.Side * BlockMeshBuilder.Scale / 2, blockAt.Y + DfTownBlock.Side * BlockMeshBuilder.Scale / 2, 0);
                PlaceStatic(cells.TownStatic(ext, $@"{style}\{ext.Name}", picker), blockAt, centre);
                var (ground, _) = cells.GroundStatic($@"{style}\{blockKey}", () => BlockGround(tb, style));
                PlaceStatic(ground, blockAt, centre);
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
                        var propAt = new P3Float(prop.PositionSkyrim.X + offset.X, prop.PositionSkyrim.Y + offset.Y, prop.PositionSkyrim.Z + groundZ);
                        // Daggerfall's own model with the stand-in's use; the stand-in only if that fails.
                        var r = cells.PropBuilder.Place(o, origin, propModel, prop, new Vector3(offset.X, offset.Y, groundZ))
                            ?? new PlacedObject(mod) { Base = prop.Base.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = propAt, Rotation = new P3Float(0, 0, prop.Heading) }, Scale = MathF.Abs(prop.Scale - 1) > 0.01f ? prop.Scale : null };
                        CellAt(cellsByGrid, propAt).Temporary.Add(r);
                        fixtures["street: " + prop.Kind] = fixtures.GetValueOrDefault("street: " + prop.Kind) + 1;
                    }

                var streetDoors = DfTownLayout.Doors(ext, models).Where(d => d.Building >= 0).GroupBy(d => d.Building).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var b in tb.Buildings)
                {
                    buildings++;
                    if (!streetDoors.TryGetValue(b.Index, out var outside) || b.Interior.Models.Count == 0) continue;
                    var interiorBlock = DfTownLayout.Interior(tb, b, models);
                    // One interior per layout, style and region race, shared by every town that has it.
                    string key = $@"{style}\{interiorBlock.Name}\{(redguard ? "R" : "B")}";
                    (int Index, bool Own) owner = default;
                    bool fixedIds = SharedOwners?.TryGetValue(key, out owner) == true;
                    if (!sharedInteriors.TryGetValue(key, out var shared) && fixedIds && !owner.Own)
                    {
                        // Another part's: where its exits stand and where each way in arrives, worked out as that part does
                        // (BuildBuildingInterior's exits, the arrivals below), and its people's order, without making it.
                        var (fixedKeys, fixedAnchor) = FixedKeysOf(owner.Index);
                        var others = new List<(PlacedObject, P3Float, float)>();
                        foreach (var surface in DfTownLayout.Doors(interiorBlock, models))
                        {
                            var (exitDoor, _, _) = cells.SurfaceDoor(surface, Vector3.Zero, doorBase, exitDoorBase, persistent: false);
                            var (_, insideArrival, insideHeading) = cells.SurfaceDoor(surface, Vector3.Zero, doorBase, persistent: false);
                            others.Add((exitDoor, insideArrival, insideHeading));
                        }
                        var (otherPeople, hasSpawn, hasItem) = DungeonCellBuilder.InteriorContents(interiorBlock);
                        var spawn = hasSpawn ? fixedKeys.FirstSpawn : FormKey.Null;
                        OtherPartsMarkers[fixedAnchor] = (spawn, hasItem ? fixedKeys.FirstItem : spawn);
                        sharedInteriors[key] = shared = new Shared(false, others.Count > 0 ? fixedAnchor : null, others, otherPeople);
                    }
                    if (shared is null)
                    {
                        var fixedSet = fixedIds ? FixedKeysOf(owner.Index) : default;
                        var built = cells.BuildBuildingInterior($"DFI{LocationPlacer.Sanitize(key)}{InteriorSuffix}", BuildingName(b.Data.Type), interiorBlock, $@"{style}\{interiorBlock.Name}", interiorPicker, doorBase, exitDoorBase,
                            fixedIds ? fixedSet.Keys : null);
                        built.Cell.Location = new FormLinkNullable<ILocationGetter>(ws.Location.FormKeyNullable ?? FormKey.Null);
                        // Houses and shops are private property (Daggerfall Unity: a house's things, a shop's wares): owned by
                        // the region's law, so taking them is theft (TMDfLaw counts it in the region the player is in).
                        if (IsPrivate(b.Data.Type)) built.Cell.Owner.SetTo(DfLaw.Region(mod, loc.Region));
                        interiors++;
                        foreach (var (k, v) in built.Fixtures) fixtures[k] = fixtures.GetValueOrDefault(k) + v;
                        // Where each way in arrives: in front of the exit nearest the street door. One persistent anchor
                        // (at the first exit's arrival) to move to; each exit's arrival a place the door sets.
                        var arrivals = new List<(PlacedObject, P3Float, float)>();
                        PlacedObject? anchor = null;
                        foreach (var (exit, surface) in built.Exits)
                        {
                            var (_, insideArrival, insideHeading) = cells.SurfaceDoor(surface, Vector3.Zero, doorBase, persistent: false);
                            anchor ??= new PlacedObject(fixedIds ? fixedSet.Anchor : mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
                            {
                                Base = XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                                Placement = new Placement { Position = insideArrival, Rotation = new P3Float(0, 0, insideHeading) },
                                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)Persistent,
                            };
                            arrivals.Add((exit, insideArrival, insideHeading));
                        }
                        if (anchor is not null) built.Cell.Persistent.Add(anchor);
                        var persons = new List<TownPeople.Person>();
                        if (People is { } people)
                            for (int index = 0; index < built.People.Count; index++)
                            {
                                var flat = built.People[index];
                                int recordOffset = -1 - flat.RecordOffset;
                                if (people.Make(loc, bx, by, b.Index, b.Data.Type, flat, recordOffset) is not { } person) continue;
                                var standAt = ToSkyrim(flat.Position);
                                // Temporary: named for the town the player came in from when it loads (TMDfOccupant reads
                                // the names the street door left on the way quest).
                                var npcRef = new PlacedNpc(mod)
                                {
                                    Base = person.Npc.ToNullableLink<INpcGetter>(),
                                    Placement = new Placement { Position = new P3Float(standAt.X, standAt.Y, standAt.Z + 4), Rotation = new P3Float(0, 0, (recordOffset % 360) * MathF.PI / 180) },
                                    VirtualMachineAdapter = new VirtualMachineAdapter
                                    {
                                        Scripts =
                                        [
                                            new ScriptEntry
                                            {
                                                Name = "TMDfOccupant",
                                                Properties =
                                                [
                                                    new ScriptObjectProperty { Name = "Way", Object = Way },
                                                    // Its place among the interior's person flats, the order every part's
                                                    // street doors name them in.
                                                    new ScriptIntProperty { Name = "Index", Data = index },
                                                ],
                                            },
                                        ],
                                    },
                                };
                                built.Cell.Temporary.Add(npcRef);
                                persons.Add(person);
                                Placed.Add(person);
                                fixtures["npc: " + person.Group] = fixtures.GetValueOrDefault("npc: " + person.Group) + 1;
                            }
                        if (Shops?.Equip(built.Cell, LocationPlacer.Sanitize(key), b.Data.Type, persons, b.Data.FactionId) is not null)
                            fixtures["shop: " + b.Data.Type] = fixtures.GetValueOrDefault("shop: " + b.Data.Type) + 1;
                        sharedInteriors[key] = shared = new Shared(true, anchor?.FormKey, arrivals, built.People);
                    }
                    if (shared.Arrivals.Count == 0 || shared.Anchor is not { } anchorKey) { unlinked += outside.Count; continue; }
                    // This town's names for the people inside, one per person flat (by their Index).
                    var names = People is { } pp ? shared.People.Select(f => pp.NameIn(loc, bx, by, b.Index, b.Data.Type, f, -1 - f.RecordOffset)).ToList() : [];
                    // Street doors are in block space; the rooms are in the building's own (unrotated) space.
                    var bOrigin = DfTownLayout.BuildingOrigin(b);
                    var unturn = DfDungeon.UnityRotY(DfTownLayout.BuildingYaw(b) * MathF.PI / 180f);
                    PlacedObject? firstStreet = null;
                    // Persistent only where something points at the door: the first doors of each type (QuestSites takes
                    // them) and the doors of the first town with this interior (its exits fall back to them). Every
                    // persistent reference takes one of the game's 1,048,576 handles from the start, whatever else the
                    // user has installed. "Where is" holds positions (TownTalk), not doors: its lists kept 84,104 doors
                    // persistent for their x and y (2026-11-01). The door the player came in by is held by TMDfTalk's
                    // LastDoor, as a house's always was.
                    // (The first town of the part that builds it: another part's interior has its exits there.)
                    bool fallbackTown = shared.Owned && shared.Arrivals.Any(a => a.Exit.VirtualMachineAdapter is null);
                    bool house = (int)b.Data.Type is >= 17 and <= 22;
                    foreach (var s in outside)
                    {
                        int sameType = doorsByType.TryGetValue(b.Data.Type, out var listed) ? listed.Count : 0;
                        var local = Vector3.Transform(s.Centre - bOrigin, unturn);
                        var (exit, arrivalAt, arrivalHeading) = shared.Arrivals.OrderBy(e => Vector2.DistanceSquared(
                            new(e.Exit.Placement!.Position.X, e.Exit.Placement.Position.Y), new(local.X * BlockMeshBuilder.Scale, local.Z * BlockMeshBuilder.Scale))).First();
                        // Of the fallback town's doors, only those an exit will name (2026-11-01: every door of it was kept,
                        // 15,543 with nothing pointing at them): the first this exit reaches, and the building's first
                        // for the exits no door reaches.
                        bool exitsHere = fallbackTown && (exit.VirtualMachineAdapter is null || firstStreet is null);
                        bool keep = exitsHere || sameType < (house ? QuestSites.PerHouseType : QuestSites.PerType);
                        var (street, _, _) = cells.SurfaceDoor(s, origin, doorBase, streetDoorBase, persistent: keep);
                        street.Placement!.Position = new P3Float(street.Placement.Position.X + offset.X, street.Placement.Position.Y + offset.Y, street.Placement.Position.Z + groundZ);
                        var entry = new ScriptEntry { Name = "TMDfStreetDoor" };
                        entry.Properties.Add(new ScriptObjectProperty { Name = "Talk", Object = TalkQuest.ToLink<ISkyrimMajorRecordGetter>() });
                        entry.Properties.Add(new ScriptObjectProperty { Name = "Way", Object = Way });
                        entry.Properties.Add(new ScriptObjectProperty { Name = "Arrival", Object = new FormLink<ISkyrimMajorRecordGetter>(anchorKey) });
                        entry.Properties.Add(new ScriptFloatListProperty { Name = "ArrivalAt", Data = [arrivalAt.X, arrivalAt.Y, arrivalAt.Z, arrivalHeading * 180f / MathF.PI] });
                        entry.Properties.Add(new ScriptIntProperty { Name = "Town", Data = townIndex });
                        // Daggerfall's hours and lock (Daggerfall Unity GetBuildingLockValue: quality / 2).
                        entry.Properties.Add(new ScriptIntProperty { Name = "BuildingType", Data = (int)b.Data.Type });
                        entry.Properties.Add(new ScriptIntProperty { Name = "LockValue", Data = b.Data.Quality / 2 });
                        entry.Properties.Add(new ScriptObjectProperty { Name = "BreakIns", Object = BreakIns.ToLink<ISkyrimMajorRecordGetter>() });
                        entry.Properties.Add(new ScriptObjectProperty { Name = "CrimeReport", Object = DfLaw.CrimeReport(mod).ToLink<ISkyrimMajorRecordGetter>() });
                        string? buildingName = People is { } namer && namedBuildings.TryGetValue((bx, by, b.Index), out var nb)
                            ? DfBuildingNames.Name(nb.Seed, b.Data.Type, nb.Faction, loc.Name, loc.Region, namer.Factions, namer.Names, DfTextFile) : null;
                        if (buildingName is not null) entry.Properties.Add(new ScriptStringProperty { Name = "BuildingName", Data = buildingName });
                        // What the door says it is (TMDfStreetDoor shows it): the building's name, else as Daggerfall Unity
                        // calls the rest (2026-11-04, user: "doors do not have names", "doors dont have residents names").
                        entry.Properties.Add(new ScriptStringProperty { Name = "DoorName", Data = buildingName ?? DoorLabel(b.Data.Type, names) });
                        // The people inside are named from these when they load (TMDfOccupant, by their Index).
                        if (names.Count > 0) entry.Properties.Add(new ScriptStringListProperty { Name = "Names", Data = [.. names] });
                        street.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
                        // A persistent door in the worldspace's persistent cell, where Skyrim has it from the start: in an
                        // overland cell's persistent group it never loaded, and the quest sites' BDoor and the exits'
                        // Fallback held nothing (2026-11-02: all 43,323 BDoor elements bound as None).
                        if (keep) ws.TopCell!.Persistent.Add(street);
                        else CellAt(cellsByGrid, street.Placement.Position).Temporary.Add(street);
                        // The exit's way out when the player didn't come in by a street door (coc, older saves): the first
                        // town's street door nearest it.
                        if (shared.Owned) exit.VirtualMachineAdapter ??= ExitScript(street);
                        firstStreet ??= street;
                        doors++;
                        if (!doorsByType.TryGetValue(b.Data.Type, out var typed)) doorsByType[b.Data.Type] = typed = [];
                        typed.Add(street);
                    }
                    if (firstStreet is not null && shared.Owned)
                        foreach (var (exit, _, _) in shared.Arrivals) exit.VirtualMachineAdapter ??= ExitScript(firstStreet);
                }
            }

        // A town's own dungeon (a castle, a sewer), behind its dungeon entrance: the dungeon's door set into Daggerfall's
        // entrance surface. (A dungeon of the wilds - a keep, a ruin, a labyrinth - has its exterior built here too, and
        // its inside made by the dungeons' parts, its door in the same surface: DungeonSpot.)
        if (Dungeons && IsTown(loc) && loc.DungeonBlocks.Count > 0 && EntranceSurface(loc) is { } way)
        {
            var spot = cells.SurfaceEntrance(way.Surface, way.Origin, new Vector3(offset.X, offset.Y, groundZ));
            try
            {
                var dungeon = cells.Build(loc, ws, spot);
                fixtures[$"dungeon {dungeon.Cell.EditorID} ({dungeon.Cell.Temporary.Count} refs)"] = 1;
            }
            catch (Exception e) { fixtures["dungeon failed: " + e.Message] = 1; }
        }

        PlaceWanderers(loc, doorsByType.Values.SelectMany(d => d).ToList(), cellsByGrid, fixtures, ws, groundZ);
        return new Built(ws, buildings, interiors, doors, unlinked, fixtures, townLocation, doorsByType, loc.Region);
    }

    /// <summary>Build each town's own dungeon (castles, sewers) behind its entrance.</summary>
    public bool Dungeons { get; init; } = true;

    /// <summary>A town, village or hamlet (the dungeons' parts make the insides of every other location's dungeon).</summary>
    public static bool IsTown(DfLocation loc) => loc.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage;

    /// <summary>Daggerfall's editor flats (TEXTURE.199): record 10 marks where a traveller arrives in a city.</summary>
    const int EditorFlatArchive = 199, StartMarkerRecord = 10;

    /// <summary>
    /// Where fast travel sets the traveller down at a location built at <paramref name="corner"/> (its map marker),
    /// as Daggerfall Unity's StreamingWorld.PositionPlayerToLocation does: outside one side of the location, a tenth of a
    /// block's side past its edge ("Sometimes buildings are right up to edge of block"), and in a city (TownCity) on the
    /// start marker (an RMB misc flat, 199.10) nearest that point. Daggerfall Unity picks the side at random each time;
    /// a Skyrim map marker stands in one place, so it is the south side, the traveller facing north into the location.
    /// <paramref name="groundAt"/> gives the land's height outside it. With the side's point when the city has no start
    /// marker.
    /// </summary>
    public static P3Float ArrivalSpot(DfLocation loc, P3Float corner, Func<string, DfTownBlock?> block, Func<float, float, float> groundAt)
    {
        float side = DfTownBlock.Side * BlockMeshBuilder.Scale;
        float x = corner.X + loc.BlockWidth * side / 2, y = corner.Y - side * 0.1f;
        if (loc.Type == DfLocationType.TownCity)
        {
            P3Float? best = null;
            float bestD = float.MaxValue;
            for (int by = 0; by < loc.BlockHeight; by++)
                for (int bx = 0; bx < loc.BlockWidth; bx++)
                {
                    if (block(loc.ExteriorBlocks[by * loc.BlockWidth + bx]) is not { } tb) continue;
                    foreach (var o in DfTownLayout.Exterior(tb).Objects)
                    {
                        if (o.Type != DfRdbObjectType.Flat || o.TextureArchive != EditorFlatArchive || o.TextureRecord != StartMarkerRecord) continue;
                        var s = ToSkyrim(DfTownLayout.BlockOrigin(bx, by) + o.Position);
                        var p = new P3Float(s.X + corner.X, s.Y + corner.Y, corner.Z);
                        float d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                        if (d < bestD) { bestD = d; best = p; }
                    }
                }
            if (best is { } marker) return marker;
        }
        return new P3Float(x, y, groundAt(x, y));
    }

    /// <summary>Whether Daggerfall puts people in a location's streets (Daggerfall Unity StreamingWorld.UpdateLocation
    /// gives these location types a PopulationManager): towns, farms, wealthy homes, temples and taverns.</summary>
    public static bool HasWanderers(DfLocation loc) => IsTown(loc) || loc.Type is DfLocationType.HomeFarms or DfLocationType.HomeWealthy
        or DfLocationType.ReligionTemple or DfLocationType.Tavern;

    /// <summary>
    /// Where a location's dungeon door stands when its exterior is built at <paramref name="corner"/> (the footprint's
    /// south-west corner, at its ground level, in whatever coordinates the caller places in): set into the first of its
    /// blocks' dungeon entrance surfaces, as <see cref="Build"/> finds them; null when its blocks have none.
    /// </summary>
    public DungeonCellBuilder.Entrance? DungeonSpot(DfLocation loc, P3Float corner) =>
        EntranceSurface(loc) is { } e ? cells.SurfaceEntrance(e.Surface, e.Origin, new Vector3(corner.X, corner.Y, corner.Z)) : null;

    /// <summary>A location's dungeon entrance surface and its block's frame: the first of its blocks' entrances (with the
    /// crypt <see cref="Outside"/> adds where they have none).</summary>
    (DfTownLayout.DoorSurface Surface, Vector3 Origin)? EntranceSurface(DfLocation loc)
    {
        for (int by = 0; by < loc.BlockHeight; by++)
            for (int bx = 0; bx < loc.BlockWidth; bx++)
                if (Outside(loc, bx, by) is { } ext)
                    foreach (var s in DfTownLayout.DungeonEntrances(ext, models))
                        return (s, DfTownLayout.BlockOrigin(bx, by));
        return null;
    }

    /// <summary>
    /// A location's block (<paramref name="bx"/>, <paramref name="by"/>) outside, as built: its blocks' exteriors, and
    /// for a location with a dungeon none of whose blocks has an entrance (Daggerfall's graveyards on GRVEAS10 and
    /// GRVEAS36), its middle block with Daggerfall's graveyard crypt in it (DfTownLayout.WithCrypt), the dungeon's way in.
    /// </summary>
    DfDungeonBlock? Outside(DfLocation loc, int bx, int by)
    {
        if (Block(loc.ExteriorBlocks[by * loc.BlockWidth + bx]) is not { } tb) return null;
        int nature = DfTownLayout.NatureArchiveOf(map.ClimateAt(loc.PixelX, loc.PixelY));
        var ext = DfTownLayout.Exterior(tb, nature);
        if (loc.DungeonBlocks.Count == 0 || bx != loc.BlockWidth / 2 || by != loc.BlockHeight / 2 || !NeedsCrypt(loc, nature)) return ext;
        string key = $"{tb.Name}|{nature}";
        if (!crypted.TryGetValue(key, out var withCrypt))
        {
            var crypt = Block(DfTownLayout.CryptBlock) is { } cb ? DfTownLayout.Exterior(cb, nature).Objects.FirstOrDefault(o => o.Type == DfRdbObjectType.Model && o.ModelId == DfTownLayout.CryptModel) : null;
            crypted[key] = withCrypt = crypt is null ? null : DfTownLayout.WithCrypt(ext, crypt, models);
        }
        return withCrypt ?? ext;
    }
    readonly Dictionary<string, DfDungeonBlock?> crypted = new();

    /// <summary>Whether none of a location's blocks has a dungeon entrance.</summary>
    bool NeedsCrypt(DfLocation loc, int nature)
    {
        foreach (var name in loc.ExteriorBlocks)
            if (Block(name) is { } tb && DfTownLayout.DungeonEntrances(DfTownLayout.Exterior(tb, nature), models).Any()) return false;
        return true;
    }

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
    /// Daggerfall's street population (Daggerfall Unity PopulationManager: 24 about the player; here 24 for every 2 x 2
    /// blocks of town; the region's race; one in 32 a guard), as people of the region's pool, each named by this town
    /// (TMDfPasserby), standing a few paces out from the town's street doors and wandering the streets by day.
    /// </summary>
    void PlaceWanderers(DfLocation loc, List<PlacedObject> streetDoors, Dictionary<(int, int), Cell> grid, Dictionary<string, int> counts, Worldspace ws, float groundZ)
    {
        if (People is not { } people || streetDoors.Count == 0 || !HasWanderers(loc)) return;
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
        // Daggerfall Unity keeps up to 24 for every 16 blocks (at most 96, PopulationManager: populationIndexPer16Blocks)
        // about the player wherever they are in town, spawning them round them; placed once for the whole town, that
        // many spread over a city left about four in sight (user, 2026-10-25: "theres no pcs besides about 4"). So 24 for
        // every 2 x 2 blocks, the ground in view round a player.
        int lots = Math.Max(1, (loc.BlockWidth * loc.BlockHeight + 3) / 4);
        // At least as many round the player, in the 5 x 5 cells Skyrim keeps loaded, as Daggerfall Unity keeps there
        // (24 for every 16 blocks, 96 at most): spread over a big city, 24 a lot left about 37 round the player in
        // Daggerfall's own 8 x 8 blocks where Daggerfall Unity keeps 96 (user, 2026-11-02: "daggerfall towns arent
        // populated"). Smaller towns keep 24 a lot, which is already more.
        int blocks = loc.BlockWidth * loc.BlockHeight;
        int nearPlayer = Math.Clamp(blocks / 16, 1, 4) * 24;
        float townCells = blocks * Terrain.LocationLayout.CellsPerBlock * Terrain.LocationLayout.CellsPerBlock;
        int count = Math.Max(lots * 24, (int)MathF.Ceiling(nearPlayer * MathF.Max(1f, townCells / 25f)));
        var random = new Random(loc.Index * 7919 + loc.Region);
        for (int i = 0; i < count; i++)
        {
            bool guard = i % 32 == 31;
            int slot = random.Next(TownPeople.WandererSlots);
            if (people.Wanderer(loc, slot, guard, out bool created) is not { } person) continue;
            if (created) Placed.Add(person);
            var door = streetDoors[random.Next(streetDoors.Count)].Placement!;
            float a = door.Rotation.Z, out_ = 220 + random.Next(160);
            var at = new P3Float(door.Position.X - MathF.Sin(a) * out_, door.Position.Y - MathF.Cos(a) * out_, groundZ + 8);
            CellAt(grid, at).Temporary.Add(new PlacedNpc(mod)
            {
                Base = person.Npc.ToNullableLink<INpcGetter>(),
                Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, (float)(random.NextDouble() * MathF.Tau)) },
                EnableParent = new EnableParent { Reference = daytime.ToLink<IPlacedGetter>(), Flags = 0 },
                // This town's name for them (the region's few wanderer records are placed in all its towns).
                VirtualMachineAdapter = new VirtualMachineAdapter
                {
                    Scripts = [new ScriptEntry { Name = "TMDfPasserby", Properties = [new ScriptStringProperty { Name = "TownName", Data = people.WandererName(loc, slot, guard, i) }] }],
                },
            });
            counts[guard ? "street guard" : "street wanderer"] = counts.GetValueOrDefault(guard ? "street guard" : "street wanderer") + 1;
        }

        // After dark the streets belong to others: the climate's "in location, night" encounters (thieves, assassins,
        // burglars, rogues, and worse) and a thinner night watch, out only while the day's people are not.
        if (Creatures is not { } creatures) return;
        var nightOnly = new EnableParent { Reference = daytime.ToLink<IPlacedGetter>(), Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent };
        var stalker = creatures.Encounter(TownNights.NightTableOf(map.ClimateAt(loc.PixelX, loc.PixelY)));
        var watch = creatures.Spawn(CityWatch, passive: true);
        for (int i = 0; i < lots * 5; i++)
        {
            bool isWatch = i % 5 >= 3;   // 3 in 5 stalkers, 2 in 5 watchmen
            if ((isWatch ? watch : stalker) is not { } who) continue;
            var door = streetDoors[random.Next(streetDoors.Count)].Placement!;
            float a = door.Rotation.Z, d = 300 + random.Next(400);
            var at = new P3Float(door.Position.X - MathF.Sin(a) * d, door.Position.Y - MathF.Cos(a) * d, groundZ + 8);
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
                        Placement = new Placement { Position = new P3Float(ground.X, ground.Y, ground.Z + 8), Rotation = new P3Float(0, 0, heading) },
                    });
                    Count("animal: " + id);
                }
            }
            else if (o.TextureArchive is LightsArchive or LightFixturePlacer.E3LightsArchive
                && (streetFires ??= new LightFixturePlacer(arena2, [])).Fixtures(o, new Vector3(0, 0, 0)).ToList() is var fire
                && fire.Count > 0 && fire[0].Kind is "campfire" or "brazier" or "standing brazier")
            {
                // A fire in the street (a campfire, a brazier) stays a fire; lamp-like sprites are street lamps. Raised
                // by how far the first one's mesh reaches below its origin (a standing brazier's legs: 33).
                float lift = cells.Props.DepthBelowOrigin(fire[0].Base);
                foreach (var f in fire)
                    CellAt(grid, ground).Temporary.Add(new PlacedObject(mod)
                    {
                        Base = f.Base.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(ground.X, ground.Y, ground.Z + (f.PositionDf.Y - fire[0].PositionDf.Y) * BlockMeshBuilder.Scale + lift), Rotation = new P3Float(0, 0, heading) },
                    });
                Count("street " + fire[0].Kind);
            }
            else if (o.TextureArchive == LightsArchive)
            {
                if (lampPost is { } post)
                    CellAt(grid, ground).Temporary.Add(new PlacedObject(mod) { Base = post.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = new P3Float(ground.X, ground.Y, ground.Z), Rotation = new P3Float(0, 0, heading) } });
                if (streetLight is { } light)
                    CellAt(grid, ground).Temporary.Add(new PlacedObject(mod) { Base = light.ToNullableLink<IPlaceableObjectGetter>(), Placement = new Placement { Position = new P3Float(ground.X, ground.Y, ground.Z + 200), Rotation = new P3Float(0, 0, 0) } });
                Count("street lamp");
            }
            else if ((PeopleArchives.Contains(o.TextureArchive) || o.FactionOrMobileId != 0) && People is { } people)
            {
                if (people.Make(loc, bx, by, 255, DfBuildingType.None, o, o.RecordOffset) is not { } person) continue;
                // Temporary: with every location's people on the one overland, persistent ones are all held in memory from
                // the start (3.4 million persistent references ended the game while it loaded, 2026-10-23).
                CellAt(grid, ground).Temporary.Add(new PlacedNpc(mod)
                {
                    Base = person.Npc.ToNullableLink<INpcGetter>(),
                    Placement = new Placement { Position = new P3Float(ground.X, ground.Y, ground.Z + 8), Rotation = new P3Float(0, 0, heading) },
                });
                Placed.Add(person);
                Count("npc outdoors");
            }
            else if (Clutter is { } clutter && clutter.Covers(o))
            {
                float h = cells.SpriteHeight(o.TextureArchive, o.TextureRecord) * BlockMeshBuilder.Scale;
                var statueFacing = ClutterPlacer.IsStatue(o) ? DungeonCellBuilder.StatueFacing(rooms, o.Position.X, o.Position.Y - h / BlockMeshBuilder.Scale / 2, o.Position.Z) : null;
                foreach (var p in clutter.Place(o, new Vector3(ground.X, ground.Y, ground.Z + h / 2), h, ground.Z, statueFacing, cells.SpriteWidth(o.TextureArchive, o.TextureRecord) * BlockMeshBuilder.Scale))
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
