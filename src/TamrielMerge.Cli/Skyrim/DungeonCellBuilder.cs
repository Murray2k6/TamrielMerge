using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Turns a Daggerfall location dungeon into a Skyrim interior cell: one static per unique RDB block (shared
/// across every dungeon that uses it), placed on the block grid, plus lights, start marker and an exit load door
/// linked to a door at the location in the overland worldspace.
/// </summary>
public sealed partial class DungeonCellBuilder(SkyrimMod mod, string arena2, IReadOnlyDictionary<uint, DfModel> models, DfBsa blocksBsa, string modOutDir, Navmesh.NavmeshBuilder navmeshes, PropReplacer props)
{
    DfPropBuilder? propBuilder;
    /// <summary>Daggerfall's furniture and props as their own objects with the Skyrim stand-ins' use.</summary>
    internal DfPropBuilder PropBuilder => propBuilder ??= new DfPropBuilder(mod, props.Skyrim, modOutDir, props, FixedPropKeys);

    /// <summary>Daggerfall.esm: prop bases at fixed numbers, so none renumbers what is built after it.</summary>
    public bool FixedPropKeys { get; init; }
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey LightingTemplate = Sk(0x6909B); // ImperialFortCloseTemplate
    Light? dungeonLight;

    /// <summary>
    /// Daggerfall Unity's dungeon light (prefab "DaggerfallLight [Dungeon]"): a white point light at intensity 0.8,
    /// gently flickering (its Animate option), unshadowed here for Skyrim's per-scene shadow budget.
    /// </summary>
    Light DungeonLight()
    {
        if (dungeonLight is not null) return dungeonLight;
        dungeonLight = new Light(mod)
        {
            EditorID = "DFDungeonLight",
            ObjectBounds = new ObjectBounds(),
            Time = -1,
            Radius = 512,
            Color = System.Drawing.Color.FromArgb(0, 255, 255, 255),
            Flags = Light.Flag.Dynamic | Light.Flag.Flicker | Light.Flag.PortalStrict,
            FalloffExponent = 1,
            FOV = 90,
            NearClip = 0.001f,
            FlickerPeriod = 0.25f,
            FlickerIntensityAmplitude = 0.12f,
            FlickerMovementAmplitude = 0,
            FadeValue = 0.8f,
        };
        mod.Lights.Add(dungeonLight);
        return dungeonLight;
    }

    /// <summary>
    /// Daggerfall Unity's dungeon atmosphere (PlayerAmbientLight.DungeonAmbientLight): a dim grey ambient of 0.12, no
    /// directional light and no fog, so the dungeon is lit only by its own lights.
    /// </summary>
    static CellLighting DungeonLighting()
    {
        var ambient = System.Drawing.Color.FromArgb(0, 31, 31, 31);
        var black = System.Drawing.Color.FromArgb(0, 0, 0, 0);
        return new CellLighting
        {
            AmbientColor = ambient,
            DirectionalColor = black,
            DirectionalFade = 0,
            FogNearColor = black,
            FogFarColor = black,
            FogNear = 0,
            FogFar = 100000,
            FogPower = 1,
            FogMax = 0,
            FogClipDistance = 0,
            LightFadeBegin = 6000,
            LightFadeEnd = 8000,
            AmbientColors = new AmbientColors
            {
                DirectionalXPlus = ambient, DirectionalXMinus = ambient, DirectionalYPlus = ambient,
                DirectionalYMinus = ambient, DirectionalZPlus = ambient, DirectionalZMinus = ambient,
                Specular = black, Scale = 1,
            },
            Inherits = 0,
        };
    }
    static readonly FormKey ExitDoor = Sk(0x4E33D);         // ImpDoorExtSingleSmallLoad01
    static readonly FormKey XMarkerHeading = Sk(0x34);
    const int PersistentFlag = 0x400;
    const int EditorFlatArchive = 199, StartMarkerRecord = 10, EnterMarkerRecord = 8;

    /// <summary>Converted Daggerfall spells, for spell traps. Without it traps fall back to a vanilla lightning trap.</summary>
    public SpellBuilder? Spells { get; init; }
    /// <summary>Daggerfall's clutter sprites as Skyrim objects. Without it the sprites are left out.</summary>
    public ClutterPlacer? Clutter { get; init; }
    /// <summary>Daggerfall's monsters. Without it the enemy markers are left empty.</summary>
    public CreatureBuilder? Creatures { get; init; }
    /// <summary>Daggerfall's quest global variables by index (set-global actions); without it they are found or made in this plugin.</summary>
    public Func<int, FormKey>? QuestGlobal { get; init; }

    const int RandomEnemyRecord = 15, FixedEnemyRecord = 16, NoEnemy = 99, RandomTreasureRecord = 19;
    /// <summary>Daggerfall's quest markers (Daggerfall Unity QuestMarker): 199.11 where quest people and foes are put,
    /// 199.18 where quest items are.</summary>
    const int QuestSpawnRecord = 11, QuestItemRecord = 18;

    /// <summary>Each built dungeon's and building interior's quest markers (persistent XMarkers, item markers flagged),
    /// for the quest machine's sites (QuestSites).</summary>
    public Dictionary<Cell, List<(PlacedObject Marker, bool Item)>> QuestMarkers { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>Every dungeon built, for the quest machine's sites (QuestSites).</summary>
    public List<(DfLocation Location, Built Built)> BuiltDungeons { get; } = [];

    /// <summary>A quest marker flat as a persistent XMarker in the cell, remembered in <see cref="QuestMarkers"/>.</summary>
    void AddQuestMarker(Cell cell, P3Float at, bool item)
    {
        var marker = new PlacedObject(mod)
        {
            Base = XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
        };
        cell.Persistent.Add(marker);
        if (!QuestMarkers.TryGetValue(cell, out var list)) QuestMarkers[cell] = list = [];
        list.Add((marker, item));
    }

    DfText? text;
    readonly Dictionary<int, Message> riddles = new();

    /// <summary>Daggerfall's furniture and props as vanilla objects (also for town streets).</summary>
    public PropReplacer Props => props;

    /// <summary>Daggerfall's people, for those standing in dungeons (castle courts); without it they are left out.</summary>
    public TownPeople? People { get; init; }
    /// <summary>The people placed in dungeons, for conversations and guilds.</summary>
    public List<TownPeople.Person> PlacedPeople { get; } = [];
    static readonly int[] PeopleArchives = [176, 177, 179, 181, 182, 183, 184, 334, 357];
    /// <summary>96, corpses: fallen adventurers, as dead thieves, warriors, mages and rogues (enemy classes' lists).</summary>
    const int CorpsesArchive = 96, StartsDeadFlag = 0x200;
    static readonly int[] CorpseClasses = [138, 144, 128, 136];
    /// <summary>Building index used for people in dungeons when naming them (town buildings use their record index, the
    /// street 255).</summary>
    const int DungeonBuildingIndex = 254;

    /// <summary>A castle block (Daggerfall Unity: the start marker's magnitude, the low byte of its faction field).</summary>
    static bool IsCastleBlock(DfDungeonBlock block) =>
        block.Objects.FirstOrDefault(o => o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && o.TextureRecord == StartMarkerRecord)
            is { } marker && (marker.FactionOrMobileId & 0xFF) != 0;
    static readonly FormKey TreasureKnapsack = Sk(0x0B7879);   // TreasKnapsack

    readonly Dictionary<(int Archive, int Record), float> spriteHeights = new();
    readonly Dictionary<(int Archive, int Record), float> spriteWidths = new();

    readonly Dictionary<string, Static> blockStatics = new();
    readonly DfTextureExport textureExport = new(arena2, modOutDir);
    readonly Dictionary<string, DfDungeonBlock> blockCache = new();

    /// <summary>Ordinary creature placements (for procedural creatures): the reference, its cell, its habitat.</summary>
    public List<(PlacedNpc Ref, Cell Cell, string Habitat)> CreatureSpots { get; } = [];

    /// <summary>The habitat of a dungeon type whose encounters are monsters; null for people's strongholds and prisons.</summary>
    static string? MonsterDungeonHabitat(DfDungeonType type) => type switch
    {
        DfDungeonType.Crypt or DfDungeonType.Cemetery or DfDungeonType.VampireHaunt or DfDungeonType.RuinedCastle => "Undead",
        DfDungeonType.DesecratedTemple or DfDungeonType.Coven or DfDungeonType.Laboratory => "Daedra",
        DfDungeonType.Mine or DfDungeonType.NaturalCave or DfDungeonType.SpiderNest or DfDungeonType.ScorpionNest => "Cave",
        DfDungeonType.HarpyNest or DfDungeonType.GiantStronghold or DfDungeonType.DragonsDen => "Beast",
        DfDungeonType.VolcanicCaves => "Ash",
        _ => null,
    };

    /// <summary>A dungeon built; its navmesh is finished when <see cref="Navmesh"/> is first read (see <see cref="BackgroundNavmesh"/>).</summary>
    public sealed record Built(Cell Cell, PlacedObject StartMarker, PlacedObject InteriorDoor, PlacedObject ExteriorDoor, Navmesh.NavmeshBuilder.PendingInterior NavmeshWork, IReadOnlyDictionary<string, int> Fixtures)
    {
        public Navmesh.NavmeshBuilder.Result Navmesh => NavmeshWork.Finish();
    }

    /// <summary>
    /// Each dungeon's navmesh made on a worker thread while the next dungeons are laid out (the dungeon batches: the
    /// navmesh is most of a dungeon's time). The caller reads each <see cref="Built.Navmesh"/> in build order, which
    /// finishes it; without this the navmesh is made before <see cref="Build"/> returns.
    /// </summary>
    public bool BackgroundNavmesh { get; init; }

    /// <summary>The overland's ground height at a world position, for where the player is set down outside (in front of
    /// the door, 160 units on: on a slope the ground there is higher than the door's, and the player arrived under it).</summary>
    public Func<float, float, float>? GroundAt { get; init; }

    /// <summary>Static for an RDB block, building its NIF on first use.</summary>
    /// <summary>Block statics an already built plugin has (by EditorID), used instead of new copies (dungeon batches).</summary>
    public IReadOnlyDictionary<string, FormKey>? MasterStatics { get; init; }

    Static BlockStatic(DfDungeonBlock block)
    {
        if (blockStatics.TryGetValue(block.Name, out var stat)) return stat;
        string edid = "DFDunBlk" + Path.GetFileNameWithoutExtension(block.Name);
        if (MasterStatics?.TryGetValue(edid, out var masterKey) == true)
            return blockStatics[block.Name] = new Static(masterKey, SkyrimRelease.SkyrimSE) { EditorID = edid };
        string rel = Path.Combine("TamrielMerge", "Dungeons", Path.ChangeExtension(block.Name, ".nif"));
        BlockMeshBuilder.Export = textureExport;
        var r = BlockMeshBuilder.Build(block, models, Path.Combine(modOutDir, "Meshes", rel));
        stat = new Static(mod)
        {
            EditorID = "DFDunBlk" + Path.GetFileNameWithoutExtension(block.Name),
            Model = new Model { File = rel },
            ObjectBounds = new ObjectBounds
            {
                First = new P3Int16((short)r.Min.X, (short)r.Min.Y, (short)r.Min.Z),
                Second = new P3Int16((short)r.Max.X, (short)r.Max.Y, (short)r.Max.Z),
            },
        };
        mod.Statics.Add(stat);
        blockStatics[block.Name] = stat;
        return stat;
    }

    public Built Build(DfLocation location, Worldspace overland, P3Float exteriorPosition)
    {
        var dungeon = DfDungeon.Load(location, blocksBsa, blockCache);
        string id = LocationPlacer.Sanitize(location.Name);
        var cell = new Cell(mod)
        {
            EditorID = "DF" + id,
            Name = location.Name,
            Flags = Cell.Flag.IsInteriorCell,
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = DungeonLighting(),
            Persistent = [],
            Temporary = [],
        };

        var fixtures = new LightFixturePlacer(arena2, dungeon.Polygons(models));
        var fixtureCounts = new Dictionary<string, int>();
        var placedProps = new List<PlacedObject>();
        var actions = new ActionObjectBuilder(mod, models, modOutDir, Spells) { Text = text ??= DfText.Load(arena2), Riddles = riddles, QuestGlobal = QuestGlobal };
        var floorPolys = dungeon.Polygons(models).Where(q => q.Normal.Y > 0.7f).ToList();
        var ceilingPolys = dungeon.Polygons(models).Where(q => q.Normal.Y < -0.7f).ToList();
        var clutterObstacles = new List<PlacedObject>();
        var rooms = new RoomFacing(dungeon.Polygons(models));
        var spawnPoints = new List<P3Float>();
        var campfires = new List<P3Float>();   // creatures need the navmesh where they stand, however small the room
        PlacedObject? start = null, enter = null;
        Vector3? exitPos = null; Vector3 exitFacing = new(0, 0, -1);
        var waterBlocks = new List<(Vector3 Origin, float Level)>();
        foreach (var (block, placement) in dungeon.Blocks)
        {
            var origin = DfDungeon.BlockOrigin(placement);
            cell.Temporary.Add(new PlacedObject(mod)
            {
                Base = BlockStatic(block).ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = ToSkyrim(origin), Rotation = new P3Float(0, 0, 0) },
            });
            float? water = WaterLevel(block);
            bool castle = IsCastleBlock(block);
            if (water is { } level)
            {
                waterBlocks.Add((origin, level));
                foreach (var plane in WaterPlanes(origin, level)) cell.Temporary.Add(plane);
                fixtureCounts["water block"] = fixtureCounts.GetValueOrDefault("water block") + 1;
            }

            for (int index = 0; index < block.Objects.Count; index++)
            {
                var o = block.Objects[index];
                var pos = ToSkyrim(origin + o.Position);
                PlacedNpc? personRef = null;   // a person standing for a flat, which a dialogue action (29) talks through
                if (o.Type == DfRdbObjectType.Light)
                {
                    cell.Temporary.Add(new PlacedObject(mod)
                    {
                        Base = DungeonLight().ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = pos, Rotation = new P3Float(0, 0, 0) },
                        // Daggerfall Unity: range = radius x 3.
                        Radius = o.LightRadius * 3 * BlockMeshBuilder.Scale,
                    });
                }
                else if (LightFixturePlacer.IsLight(o))
                {
                    foreach (var f in fixtures.Fixtures(o, origin + o.Position))
                    {
                        cell.Temporary.Add(new PlacedObject(mod)
                        {
                            Base = f.Base.ToNullableLink<IPlaceableObjectGetter>(),
                            Placement = new Placement { Position = ToSkyrim(f.PositionDf), Rotation = new P3Float(0, 0, f.HeadingRadians) },
                        });
                        fixtureCounts[f.Kind] = fixtureCounts.GetValueOrDefault(f.Kind) + 1;
                        if (f.Kind == "campfire") campfires.Add(ToSkyrim(f.PositionDf));
                        if (Environment.GetEnvironmentVariable("TM_DEBUG_LIGHTS") == "1")
                            Console.WriteLine($"    210.{o.TextureRecord} -> {f.Kind} {fixtures.Describe(origin + o.Position)}");
                    }
                }
                else if (o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && o.TextureRecord is QuestSpawnRecord or QuestItemRecord)
                {
                    var c = origin + o.Position;
                    float floor = SurfaceY(floorPolys, c.X, c.Z, c.Y + 16, above: false) ?? c.Y;
                    AddQuestMarker(cell, new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 8), o.TextureRecord == QuestItemRecord);
                }
                else if (o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && o.TextureRecord is RandomEnemyRecord or FixedEnemyRecord
                    && Creatures is { } creatures)
                {
                    // Random markers draw from the dungeon type's encounter list; fixed ones name their creature
                    // (Daggerfall Unity AddFixedRDBEnemy: low byte of the faction/mobile field, 99 = none).
                    bool passive = o.FlatAction == NoEnemy;
                    int mobile = o.FactionOrMobileId & 0xFF;
                    // Below the block's water: Daggerfall's underwater table, and water creatures may live there.
                    bool underwater = water is { } wl && o.Position.Y < wl;
                    FormKey? spawn = o.TextureRecord == RandomEnemyRecord ? creatures.RandomEncounter(location.DungeonType, underwater)
                        : mobile == NoEnemy ? null : creatures.Spawn(mobile, passive, underwater);
                    if (spawn is { } s)
                    {
                        var c = origin + o.Position;
                        float floor = SurfaceY(floorPolys, c.X, c.Z, c.Y + 16, above: false) ?? c.Y;
                        uint hash = (uint)HashCode.Combine(o.RecordOffset, block.Name);
                        var at = new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 4);
                        var placedCreature = new PlacedNpc(mod)
                        {
                            Base = s.ToNullableLink<INpcGetter>(),
                            Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, hash % 360 * MathF.PI / 180) },
                        };
                        cell.Temporary.Add(placedCreature);
                        // Ordinary creatures for procedural creatures: fixed monsters (not bosses or people), and random
                        // encounters where the dungeon type's table is monsters.
                        string? habitat = o.TextureRecord == RandomEnemyRecord ? MonsterDungeonHabitat(location.DungeonType)
                            : !passive && CreatureBuilder.IsOrdinary(mobile) ? CreatureCatalog.HabitatsOf(DfCreatures.Mobiles[mobile].Name)[0] : null;
                        if (habitat is not null) CreatureSpots.Add((placedCreature, cell, habitat));
                        spawnPoints.Add(at);
                        string kind = o.TextureRecord == RandomEnemyRecord ? "random encounter" : DfCreatures.Mobiles.TryGetValue(mobile, out var m) ? m.Name : $"mobile {mobile}";
                        fixtureCounts["creature: " + kind] = fixtureCounts.GetValueOrDefault("creature: " + kind) + 1;
                    }
                }
                else if (o.Type == DfRdbObjectType.Flat && o.TextureArchive == CorpsesArchive && Creatures is { } corpses
                    && corpses.Spawn(CorpseClasses[o.TextureRecord % CorpseClasses.Length]) is { } fallen)
                {
                    // A dead adventurer (Daggerfall's bloody corpse sprites): a Skyrim body that starts dead, searchable.
                    var c = origin + o.Position;
                    float floor = SurfaceY(floorPolys, c.X, c.Z, c.Y + 16, above: false) ?? c.Y;
                    cell.Temporary.Add(new PlacedNpc(mod)
                    {
                        Base = fallen.ToNullableLink<INpcGetter>(),
                        Placement = new Placement { Position = new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 4), Rotation = new P3Float(0, 0, (uint)HashCode.Combine(o.RecordOffset, block.Name) % 360 * MathF.PI / 180) },
                        SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)StartsDeadFlag,
                    });
                    fixtureCounts["corpse"] = fixtureCounts.GetValueOrDefault("corpse") + 1;
                }
                else if (o.Type == DfRdbObjectType.Flat && PeopleArchives.Contains(o.TextureArchive) && People is { } people
                    && people.Make(location, placement.X, placement.Z, DungeonBuildingIndex, castle ? DfBuildingType.Palace : DfBuildingType.None, o, o.RecordOffset) is { } person)
                {
                    // A person in a dungeon: a castle's court and household (King Gothryd in Castle Daggerfall), or
                    // whoever else Daggerfall stands there, as in town (TownPeople); in a castle block they are its court.
                    var c = origin + o.Position;
                    float floor = SurfaceY(floorPolys, c.X, c.Z, c.Y + 16, above: false) ?? c.Y;
                    var at = new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 4);
                    personRef = new PlacedNpc(mod)
                    {
                        Base = person.Npc.ToNullableLink<INpcGetter>(),
                        Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, (uint)HashCode.Combine(o.RecordOffset, block.Name) % 360 * MathF.PI / 180) },
                        SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
                    };
                    cell.Persistent.Add(personRef);
                    PlacedPeople.Add(person);
                    spawnPoints.Add(at);
                    fixtureCounts["person: " + person.Name] = fixtureCounts.GetValueOrDefault("person: " + person.Name) + 1;
                }
                else if (Clutter is { } clutter && clutter.Covers(o))
                {
                    foreach (var p in PlaceSprite(clutter, o, origin + o.Position, floorPolys, ceilingPolys, rooms))
                    {
                        var r = new PlacedObject(mod)
                        {
                            Base = p.Base.ToNullableLink<IPlaceableObjectGetter>(),
                            Placement = new Placement { Position = new P3Float(p.PositionSkyrim.X, p.PositionSkyrim.Y, p.PositionSkyrim.Z), Rotation = new P3Float(p.Rotation.X, p.Rotation.Y, p.Rotation.Z) },
                        };
                        if (MathF.Abs(p.Scale - 1) > 0.01f) r.Scale = p.Scale;
                        cell.Temporary.Add(r);
                        if (!p.IsItem && p.Mount == ClutterPlacer.Mount.Floor) clutterObstacles.Add(r);
                        fixtureCounts["clutter: " + p.Label] = fixtureCounts.GetValueOrDefault("clutter: " + p.Label) + 1;
                    }
                }
                else if (o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && o.TextureRecord == RandomTreasureRecord)
                {
                    // Daggerfall's random treasure (a loot pile of random items): a dropped knapsack of vanilla loot.
                    var c = origin + o.Position;
                    float floor = SurfaceY(floorPolys, c.X, c.Z, c.Y + 16, above: false) ?? c.Y;
                    var loot = new PlacedObject(mod)
                    {
                        Base = TreasureKnapsack.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 2), Rotation = new P3Float(0, 0, (uint)HashCode.Combine(o.RecordOffset, block.Name) % 360 * MathF.PI / 180) },
                    };
                    cell.Temporary.Add(loot);
                    clutterObstacles.Add(loot);
                    fixtureCounts["random treasure"] = fixtureCounts.GetValueOrDefault("random treasure") + 1;
                }
                else if (o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && placement.IsStart)
                {
                    var floor = FloorBelow(dungeon, origin + o.Position);
                    var marker = new PlacedObject(mod)
                    {
                        Base = XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 8), Rotation = new P3Float(0, 0, 0) },
                        SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
                    };
                    if (o.TextureRecord == StartMarkerRecord && start is null) { marker.EditorID = "DFStart" + id; start = marker; cell.Persistent.Add(marker); }
                    else if (o.TextureRecord == EnterMarkerRecord && enter is null) { marker.EditorID = "DFEnter" + id; enter = marker; cell.Persistent.Add(marker); }
                }
                else if (o.Type == DfRdbObjectType.Model && !o.HasAction && PropReplacer.IsReplaced(o.ModelId) && models.TryGetValue(o.ModelId, out var propModel)
                    && props.Replace(o, origin, propModel, PropFacing(rooms, origin + o.Position, propModel, floorPolys)) is { } prop)
                {
                    // Daggerfall's own model with the stand-in's use (DfPropBuilder); the stand-in itself only if that fails.
                    var r = PropBuilder.Place(o, origin, propModel, prop) ?? new PlacedObject(mod)
                    {
                        Base = prop.Base.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(prop.PositionSkyrim.X, prop.PositionSkyrim.Y, prop.PositionSkyrim.Z), Rotation = new P3Float(0, 0, prop.Heading) },
                        Scale = MathF.Abs(prop.Scale - 1) > 0.01f ? prop.Scale : null,
                    };
                    cell.Temporary.Add(r);
                    placedProps.Add(r);
                    fixtureCounts[prop.Kind] = fixtureCounts.GetValueOrDefault(prop.Kind) + 1;
                }
                else if (ActionObjectBuilder.IsMovingDoor(o) && models.ContainsKey(o.ModelId))
                {
                    // A door that slides (secret doors, portcullises): its own model, moving as action objects do.
                    actions.Place(cell, o, index, origin);
                    fixtureCounts["sliding door"] = fixtureCounts.GetValueOrDefault("sliding door") + 1;
                }
                else if (o.Type == DfRdbObjectType.Model && BlockMeshBuilder.IsDoor(o) && models.TryGetValue(o.ModelId, out var doorModel))
                {
                    var door = DoorPlacer.Place(mod, o, origin, doorModel);
                    if (o.Action is not null)
                    {
                        actions.AttachToDoor(door, o, index, origin);
                        cell.Persistent.Add(door);
                        fixtureCounts["door with action"] = fixtureCounts.GetValueOrDefault("door with action") + 1;
                    }
                    else
                    {
                        cell.Temporary.Add(door);
                    }
                    fixtureCounts[door.Lock is null ? "door" : $"door ({door.Lock.Level})"] = fixtureCounts.GetValueOrDefault(door.Lock is null ? "door" : $"door ({door.Lock.Level})") + 1;
                }
                else if (ActionObjectBuilder.IsActionObject(o) && models.ContainsKey(o.ModelId))
                {
                    actions.Place(cell, o, index, origin);
                    fixtureCounts[$"moving object (action {o.Action!.Flags})"] = fixtureCounts.GetValueOrDefault($"moving object (action {o.Action!.Flags})") + 1;
                }
                else if (o.Type == DfRdbObjectType.Model && o.ModelId == BlockMeshBuilder.ExitDoorModel && placement.IsStart && exitPos is null)
                {
                    // The exit model is one textured panel; its normal is the direction the door faces.
                    var m = DfDungeon.ObjectTransform(o, origin);
                    var panel = models[o.ModelId].Polygons[0];
                    var centre = panel.Points.Aggregate(Vector3.Zero, (a, b) => a + b) / panel.Points.Length;
                    exitPos = Vector3.Transform(new Vector3(centre.X, 0, centre.Z), m);
                    exitFacing = Vector3.Normalize(Vector3.TransformNormal(panel.Normal, m));
                }
                // A flat with an action (whatever shows it: a light, clutter, a person, nothing for editor flats) also
                // gets its action, as an invisible activator the player or a chain triggers.
                if (ActionObjectBuilder.IsActionFlat(o) && actions.PlaceFlat(cell, o, index, origin, personRef) is not null)
                    fixtureCounts[$"action flat (action {o.Action!.Flags})"] = fixtureCounts.GetValueOrDefault($"action flat (action {o.Action!.Flags})") + 1;
            }
            int links = actions.LinkChains(cell, block, origin);
            if (links > 0) fixtureCounts["action chain links"] = fixtureCounts.GetValueOrDefault("action chain links") + links;
        }
        start ??= enter ?? throw new InvalidDataException($"{location.Name} has no start or enter marker");
        enter ??= start;
        // Where a new game begins: Daggerfall Unity prefers the enter marker (StartDungeonInterior, preferEnterMarker),
        // which in Privateer's Hold is by the campfire. Face the nearest campfire, if one is close.
        var newGameMarker = enter;
        var ap = newGameMarker.Placement!.Position;
        if (campfires.OrderBy(c => (c.X - ap.X) * (c.X - ap.X) + (c.Y - ap.Y) * (c.Y - ap.Y)).FirstOrDefault() is var fire
            && campfires.Count > 0 && MathF.Abs(fire.Z - ap.Z) < 200 && MathF.Sqrt((fire.X - ap.X) * (fire.X - ap.X) + (fire.Y - ap.Y) * (fire.Y - ap.Y)) < 800)
            newGameMarker.Placement.Rotation = new P3Float(0, 0, MathF.Atan2(fire.X - ap.X, fire.Y - ap.Y));   // clockwise from north

        // Exit: a load door where Daggerfall's exit door model stood (or at the enter marker), paired with a door
        // standing at the location in the overland worldspace.
        // Skyrim's door faces -Y in its own frame with the leaf ~65 units in front of its origin, so set the
        // origin behind the wall and turn it (Skyrim headings run clockwise) to face Daggerfall's panel normal.
        var facing = new Vector3(exitFacing.X, exitFacing.Z, 0);
        facing = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : new Vector3(0, -1, 0);
        float heading = MathF.Atan2(-facing.X, -facing.Y);
        var wall = exitPos is { } e ? ToSkyrim(e) : enter.Placement!.Position;
        var doorPos = new P3Float(wall.X - facing.X * 65, wall.Y - facing.Y * 65, wall.Z);
        var arrival = new P3Float(wall.X + facing.X * 120, wall.Y + facing.Y * 120, wall.Z + 8);
        var interiorDoor = new PlacedObject(mod)
        {
            EditorID = "DFExitDoor" + id,
            Base = ExitDoor.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = doorPos, Rotation = new P3Float(0, 0, heading) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
        };
        var exteriorDoor = new PlacedObject(mod)
        {
            EditorID = "DFEntranceDoor" + id,
            Base = ExitDoor.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = exteriorPosition, Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
        };
        // Arrive a little in front of each door (doors face -Y in their own frame).
        interiorDoor.TeleportDestination = new TeleportDestination
        {
            Door = exteriorDoor.ToLink<IPlacedObjectGetter>(),
            Position = new P3Float(exteriorPosition.X, exteriorPosition.Y - 160,
                MathF.Max(exteriorPosition.Z, GroundAt?.Invoke(exteriorPosition.X, exteriorPosition.Y - 160) ?? exteriorPosition.Z) + 16),
            Rotation = new P3Float(0, 0, MathF.PI),
        };
        exteriorDoor.TeleportDestination = new TeleportDestination
        {
            Door = interiorDoor.ToLink<IPlacedObjectGetter>(),
            Position = exitPos is null ? enter.Placement!.Position : arrival,
            Rotation = new P3Float(0, 0, heading + MathF.PI),
        };
        cell.Persistent.Add(interiorDoor);
        overland.TopCell!.Persistent.Add(exteriorDoor);

        // Navmesh over every placed block's solid geometry, linked to the exit door.
        var walkable = new List<(Vector3[] Points, Vector3 Normal)>();
        foreach (var (block, placement) in dungeon.Blocks)
        {
            var offset = ToSkyrim(DfDungeon.BlockOrigin(placement));
            var shift = new Vector3(offset.X, offset.Y, offset.Z);
            walkable.AddRange(BlockMeshBuilder.SolidPolygons(block, models).Select(p => (p.Points.Select(v => v + shift).ToArray(), p.Normal)));
            // Its platforms, lifts, drawbridges and raised floors where they rest.
            walkable.AddRange(BlockMeshBuilder.ActionSolids(block, models).Select(p => (p.Points.Select(v => v + shift).ToArray(), p.Normal)));
        }
        int settled = SettleProps(placedProps, walkable);
        if (settled > 0) fixtureCounts["props settled onto supports"] = settled;
        // Props are obstacles for pathing: add each one's box so the navmesh goes around it.
        foreach (var r in placedProps) walkable.AddRange(PropBox(r));
        // Standing clutter big enough to walk around (columns, carts, statues) is an obstacle too.
        foreach (var r in clutterObstacles)
        {
            var (min, max) = PropBounds(r);
            if (MathF.Max(max.X - min.X, max.Y - min.Y) >= 40 && max.Z - min.Z >= 40) walkable.AddRange(PropBox(r));
        }
        var nav = navmeshes.BeginInterior(cell, walkable, [interiorDoor], newGameMarker.Placement!.Position, spawnPoints.Append(start.Placement!.Position),
            p => Underwater(waterBlocks, p), BackgroundNavmesh);
        if (!BackgroundNavmesh) nav.Finish();

        AddInteriorCell(cell);
        var result = new Built(cell, newGameMarker, interiorDoor, exteriorDoor, nav, fixtureCounts);
        BuiltDungeons.Add((location, result));
        return result;
    }

    (Vector3 Min, Vector3 Max) PropBounds(PlacedObject r)
    {
        var (min, max, _) = props.SkyrimShape(r.Base.FormKey);
        float sc = r.Scale ?? 1;
        return (min * sc, max * sc);
    }

    /// <summary>The prop's world-space corners: local bounds turned by its heading (clockwise) and moved to its position.</summary>
    Vector3[] PropCorners(PlacedObject r)
    {
        var (min, max) = PropBounds(r);
        float h = r.Placement!.Rotation.Z, c = MathF.Cos(h), s = MathF.Sin(h);
        var p = r.Placement.Position;
        return [.. from x in new[] { min.X, max.X } from y in new[] { min.Y, max.Y } from z in new[] { min.Z, max.Z }
                   select new Vector3(p.X + x * c + y * s, p.Y - x * s + y * c, p.Z + z)];
    }

    /// <summary>
    /// Rest props on what is under them, lowest first: the dungeon floor or an already-settled prop. Daggerfall
    /// stacks crates, and the Skyrim crates differ slightly in height, so stacks would otherwise float or sink.
    /// Supports more than 400 units away are ignored rather than dropping a prop through floors.
    /// </summary>
    int SettleProps(List<PlacedObject> placed, List<(Vector3[] Points, Vector3 Normal)> walkable)
    {
        var floors = walkable.Where(p => p.Normal.Z > 0.7f).ToList();
        var done = new List<PlacedObject>();
        int moved = 0;
        foreach (var r in placed.OrderBy(r => r.Placement!.Position.Z + PropBounds(r).Min.Z))
        {
            var p = r.Placement!.Position;
            float bottom = p.Z + PropBounds(r).Min.Z;
            float? support = null;
            foreach (var f in floors)
                for (int i = 1; i + 1 < f.Points.Length; i++)
                    if (HeightInTriangleXY(f.Points[0], f.Points[i], f.Points[i + 1], p.X, p.Y) is { } z && z <= bottom + 48 && (support is null || z > support)) support = z;
            foreach (var o in done)
            {
                var corners = PropCorners(o);
                float top = corners.Max(v => v.Z);
                if (top <= bottom + 48 && p.X >= corners.Min(v => v.X) && p.X <= corners.Max(v => v.X) && p.Y >= corners.Min(v => v.Y) && p.Y <= corners.Max(v => v.Y)
                    && (support is null || top > support)) support = top;
            }
            if (support is { } sz && MathF.Abs(sz - bottom) > 2 && MathF.Abs(sz - bottom) < 400)
            {
                r.Placement.Position = new P3Float(p.X, p.Y, p.Z + (sz - bottom));
                moved++;
            }
            done.Add(r);
        }
        return moved;
    }

    /// <summary>The prop's box as obstacle polygons (sides and top), wound counter-clockwise around outward normals.</summary>
    List<(Vector3[] Points, Vector3 Normal)> PropBox(PlacedObject r)
    {
        var c = PropCorners(r); // index = x*4 + y*2 + z over (min, max)
        int[][] faces = [[0, 2, 6, 4], [1, 5, 7, 3], [0, 4, 5, 1], [2, 3, 7, 6], [1, 3, 7, 5]];
        var centre = c.Aggregate(Vector3.Zero, (a, b) => a + b) / c.Length;
        var result = new List<(Vector3[] Points, Vector3 Normal)>();
        foreach (var f in faces)
        {
            var pts = f.Select(i => c[i]).ToArray();
            var n = Vector3.Normalize(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]));
            var faceCentre = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / 4;
            if (Vector3.Dot(n, faceCentre - centre) < 0) { Array.Reverse(pts); n = -n; }
            result.Add((pts, n));
        }
        return result;
    }

    static float? HeightInTriangleXY(Vector3 a, Vector3 b, Vector3 c, float x, float y)
    {
        float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(den) < 1e-6f) return null;
        float w0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den;
        float w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den;
        float w2 = 1 - w0 - w1;
        return w0 < 0 || w1 < 0 || w2 < 0 ? null : w0 * a.Z + w1 * b.Z + w2 * c.Z;
    }

    /// <summary>In-world height of a sprite, in Daggerfall units.</summary>
    internal float SpriteHeight(int archive, int record)
    {
        if (spriteHeights.TryGetValue((archive, record), out var h)) return h;
        var tex = DfTexture.Load(arena2, archive);
        h = tex is not null && record < tex.Count ? tex.Image(record).WorldSize.Height : 32;
        spriteWidths[(archive, record)] = tex is not null && record < tex.Count ? tex.Image(record).WorldSize.Width : 32;
        return spriteHeights[(archive, record)] = h;
    }

    /// <summary>A sprite's in-world width (Daggerfall units).</summary>
    internal float SpriteWidth(int archive, int record)
    {
        SpriteHeight(archive, record);
        return spriteWidths[(archive, record)];
    }

    /// <summary>
    /// The objects standing for a sprite at <paramref name="c"/> (its centre, Daggerfall units, in the cell's space):
    /// stood on the floor under its base or hung from the ceiling over its top; statues turned into the room.
    /// </summary>
    internal IEnumerable<ClutterPlacer.Placement> PlaceSprite(ClutterPlacer clutter, DfRdbObject o, Vector3 c,
        List<DfPlacedPolygon> floorPolys, List<DfPlacedPolygon> ceilingPolys, RoomFacing rooms)
    {
        float h = SpriteHeight(o.TextureArchive, o.TextureRecord);
        bool hangs = clutter.MountFor(o) != ClutterPlacer.Mount.Floor;
        float anchor = hangs ? SurfaceY(ceilingPolys, c.X, c.Z, c.Y + h / 2 - 24, above: true) ?? c.Y + h / 2
                             : SurfaceY(floorPolys, c.X, c.Z, c.Y - h / 2 + 24, above: false) ?? c.Y - h / 2;
        var facing = ClutterPlacer.IsStatue(o) && !hangs ? StatueFacing(rooms, c.X, anchor, c.Z) : null;
        var pos = ToSkyrim(c);
        return clutter.Place(o, new Vector3(pos.X, pos.Y, pos.Z), h * BlockMeshBuilder.Scale, anchor * BlockMeshBuilder.Scale,
            facing, SpriteWidth(o.TextureArchive, o.TextureRecord) * BlockMeshBuilder.Scale);
    }

    /// <summary>For a replaced model turned to the room (statues): the way into the room from where it stands.</summary>
    internal static Vector2? PropFacing(RoomFacing rooms, Vector3 at, DfModel model, List<DfPlacedPolygon> floorPolys) =>
        !PropReplacer.TurnsToRoom(model) ? null
            : StatueFacing(rooms, at.X, SurfaceY(floorPolys, at.X, at.Z, at.Y + 16, above: false) ?? at.Y, at.Z);

    /// <summary>The way into the room from a statue standing at (x, z) on the floor at height y (Daggerfall units).</summary>
    internal static Vector2? StatueFacing(RoomFacing rooms, float x, float floorY, float z) => rooms.Toward(new Vector3(x, floorY + 48, z));

    /// <summary>
    /// Height (Daggerfall units) of the nearest floor at or below <paramref name="fromY"/>, or of the nearest ceiling
    /// at or above it, under/over (x, z) within 512 units.
    /// </summary>
    internal static float? SurfaceY(List<DfPlacedPolygon> polys, float x, float z, float fromY, bool above)
    {
        float? best = null;
        foreach (var poly in polys)
        {
            var pts = poly.Points;
            for (int i = 1; i + 1 < pts.Length; i++)
                if (HeightInTriangle(pts[0], pts[i], pts[i + 1], x, z) is { } y)
                {
                    if (above ? y >= fromY && y - fromY < 512 && (best is null || y < best) : y <= fromY && fromY - y < 512 && (best is null || y > best)) best = y;
                }
        }
        return best;
    }

    static P3Float ToSkyrim(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);

    // ---- Water ----

    /// <summary>Vanilla Water2048DefaultIntWaistStill: a 2048-unit square of still interior water (its surface at z 0).</summary>
    static readonly FormKey WaterPlane = Sk(0x0CC7B5);
    const float WaterPlaneSide = 2048;
    /// <summary>Navmesh this far (Skyrim units) under the surface is swum over (the Water flag), not walked.</summary>
    const float SwimDepth = 80;

    /// <summary>A dungeon block's water level (Daggerfall units, +Y up), or null when it is dry: the sound index of its
    /// start-marker flat (199.10) x 8 (Daggerfall Unity DaggerfallBillboard, RDBLayout.AddWater).</summary>
    static float? WaterLevel(DfDungeonBlock block) =>
        block.Objects.FirstOrDefault(o => o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && o.TextureRecord == StartMarkerRecord)
            is { SoundIndex: > 0 } marker ? 8f * marker.SoundIndex : null;

    /// <summary>Water planes covering a block (Daggerfall's water fills the whole block square at its level).</summary>
    IEnumerable<PlacedObject> WaterPlanes(Vector3 origin, float level)
    {
        float side = DfDungeonBlock.Side * BlockMeshBuilder.Scale;
        int n = (int)MathF.Ceiling(side / WaterPlaneSide);
        float step = side / n, scale = step / WaterPlaneSide;
        var corner = ToSkyrim(origin);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var plane = new PlacedObject(mod)
                {
                    Base = WaterPlane.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = new P3Float(corner.X + (i + 0.5f) * step, corner.Y + (j + 0.5f) * step, level * BlockMeshBuilder.Scale), Rotation = new P3Float(0, 0, 0) },
                };
                if (MathF.Abs(scale - 1) > 0.01f) plane.Scale = scale;
                yield return plane;
            }
    }

    /// <summary>Whether a point (Skyrim space) lies deep under one of the dungeon's water blocks.</summary>
    static bool Underwater(List<(Vector3 Origin, float Level)> waterBlocks, P3Float p)
    {
        float x = p.X / BlockMeshBuilder.Scale, z = p.Y / BlockMeshBuilder.Scale;
        foreach (var (o, level) in waterBlocks)
            if (x >= o.X && x < o.X + DfDungeonBlock.Side && z >= o.Z && z < o.Z + DfDungeonBlock.Side)
                return p.Z < level * BlockMeshBuilder.Scale - SwimDepth;
        return false;
    }

    /// <summary>Height (Daggerfall units) of the highest upward-facing polygon under a point, within 256 units.</summary>
    float FloorBelow(DfDungeon dungeon, Vector3 p)
    {
        float best = float.MinValue;
        foreach (var poly in dungeon.Polygons(models).Where(q => q.Normal.Y > 0.7f))
        {
            var pts = poly.Points;
            for (int i = 1; i + 1 < pts.Length; i++)
                if (HeightInTriangle(pts[0], pts[i], pts[i + 1], p.X, p.Z) is { } y && y <= p.Y + 64 && y > best) best = y;
        }
        return best == float.MinValue ? p.Y : best;
    }

    static float? HeightInTriangle(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-6f) return null;
        float w0 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
        float w1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
        float w2 = 1 - w0 - w1;
        return w0 < 0 || w1 < 0 || w2 < 0 ? null : w0 * a.Y + w1 * b.Y + w2 * c.Y;
    }

    /// <summary>Interior cells live in block (ID % 10) / sub-block ((ID / 10) % 10) groups.</summary>
    void AddInteriorCell(Cell cell)
    {
        int blockNo = (int)(cell.FormKey.ID % 10), subNo = (int)(cell.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
        if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNo);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
}
