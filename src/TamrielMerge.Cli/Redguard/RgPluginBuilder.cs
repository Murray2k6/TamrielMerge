using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Redguard;

/// <summary>
/// Builds Redguard.esm: Stros M'Kai and the rest of Redguard's maps from the game's own files, with its own models and
/// textures (<see cref="RgMeshConverter"/>, <see cref="RgArt"/>). Each scene WORLD.INI lists (maps\*.RGM) becomes an
/// interior cell, or, when it has terrain (a .WLD), a worldspace whose land is the WLD heightmap and terrain tiles.
/// Scene statics (MPSO) and props (MPOB with a model) are placed as statics, lights (MPSL) as lights, fires as
/// vanilla effects. Redguard moves between maps by script (LoadWorld(map, marker, facing), found in each actor's
/// compiled SOUP script by <see cref="RgScript"/>); each such trigger becomes an automatic load door where the actor
/// stands, leading to the marker it names, paired with the trigger in the other map that leads back.
/// </summary>
public sealed partial class RgPluginBuilder
{
    public const string PluginName = "Redguard.esm";
    public const uint StartMarkerId = 0x800;
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey XMarkerHeading = Sk(0x34), LightingTemplate = Sk(0x6909B);

    readonly string rgDir, artDir, modOutDir, skyrimData;
    readonly SkyrimMod mod;
    readonly RgArt art;
    readonly RgMeshConverter meshes;
    readonly RgWorldIni ini;
    readonly Skyrim.VanillaActors vanilla;
    ISkyrimModDisposableGetter? skyrimEsm;
    ISkyrimModDisposableGetter SkyrimEsm => skyrimEsm ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);

    public PlacedObject StartMarker { get; }
    public List<string> Problems { get; } = [];
    public SkyrimMod Mod => mod;
    public int Statics { get; private set; }
    public int Props { get; private set; }
    public int LightCount { get; private set; }
    public int Effects { get; private set; }
    public int DoorsLinked { get; private set; }
    public int DoorsOneWay { get; private set; }
    public int Navmeshes { get; private set; }
    public int NavmeshTriangles { get; private set; }

    /// <summary>Display names of Redguard's maps.</summary>
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ISLAND"] = "Stros M'Kai", ["EXTPALAC"] = "Stros M'Kai Palace Grounds", ["NECRISLE"] = "Isle of N'Gasta", ["HIDEINT"] = "The Hideout", ["START"] = "Iliac Bay",
        ["CATACOMB"] = "Catacombs", ["PALACE"] = "Palace of Stros M'Kai", ["CAVERNS"] = "Ogre's Tooth Caverns", ["OBSERVE"] = "Observatory",
        ["NECRTOWR"] = "N'Gasta's Tower", ["DRINT"] = "Dwarven Ruins", ["JAILINT"] = "Palace Jail", ["TEMPLE"] = "Temple",
        ["MGUILD"] = "Mages Guild", ["VILE"] = "Vile's Lair", ["TAVERN"] = "The Draggin Tale", ["SILVER1"] = "Silversmith's Shop",
        ["SILVER2"] = "Silversmith's House", ["BELLTOWR"] = "Bell Tower", ["HARBTOWR"] = "Harbor Tower", ["GERRICKS"] = "Gerrick's Store",
        ["CARTOGR"] = "Cartographer's", ["SMDEN"] = "Smugglers' Den", ["ROLLOS"] = "Rollo's House", ["JFFERS"] = "J'ffer's Books",
        ["BRENNANS"] = "Brennan's House",
    };

    sealed class Scene
    {
        public required string Name;
        public required RgScene Data;
        public required RgScript Script;
        public required RgRob? Rob;
        public required string Palette;
        public required List<int> MapIds;
        public RgWorld? Terrain;
        public string? TerrainFile;
        /// <summary>The world made bigger (RgWarp): every position placed in it goes through this.</summary>
        public RgWarp Warp = RgWarp.None;
        /// <summary>An indoor scene's move to Skyrim's origin (<see cref="CentreInteriors"/>): added to every position
        /// placed in it, after the conversion.</summary>
        public Vector3 Offset;
        /// <summary>Built as an interior cell (no terrain of its own).</summary>
        public bool Indoors;
        public Cell? Interior;
        public Worldspace? World;
        public Dictionary<(int X, int Y), Cell> Cells = [];
        public Cell? Top;
        public List<PlacedObject> LoadDoors = [];
    }

    readonly Dictionary<string, Scene> scenes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<RgRob> allRobs = [];

    public RgPluginBuilder(string rgDir, string modOutDir, string skyrimData)
    {
        this.rgDir = rgDir;
        this.modOutDir = modOutDir;
        this.skyrimData = skyrimData;
        artDir = Path.Combine(rgDir, "3dart");
        mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        StartMarker = new PlacedObject(mod)
        {
            EditorID = "RGStartMarker",
            Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        if (StartMarker.FormKey.ID != StartMarkerId) throw new InvalidOperationException($"start marker got {StartMarker.FormKey}, expected {StartMarkerId:X}");
        art = new RgArt(artDir, modOutDir);
        meshes = new RgMeshConverter(art);
        ini = RgWorldIni.Load(Path.Combine(rgDir, "WORLD.INI"));
        vanilla = new Skyrim.VanillaActors(skyrimData);
    }

    // ---- loading ----

    void LoadScenes()
    {
        var functions = RgScript.FunctionNames(Path.Combine(rgDir, "soup386", "SOUP386.DEF"));
        loadWorld = functions.IndexOf("LoadWorld");
        foreach (var f in Directory.GetFiles(artDir, "*.ROB"))
            if (RgRob.Load(File.ReadAllBytes(f)) is { } r) { allRobs.Add(r); robNames[r] = Path.GetFileNameWithoutExtension(f).ToUpperInvariant(); }
        foreach (var group in ini.Maps.Values.GroupBy(m => m.Scene, StringComparer.OrdinalIgnoreCase))
        {
            string name = group.Key.ToUpperInvariant();
            string rgm = Path.Combine(rgDir, "maps", name + ".RGM");
            if (!File.Exists(rgm)) { Problems.Add($"scene {name}: no {name}.RGM"); continue; }
            var bytes = File.ReadAllBytes(rgm);
            var data = RgScene.Load(bytes)!;
            var first = group.OrderBy(m => m.Id).First();
            var scene = new Scene
            {
                Name = name, Data = data, Script = RgScript.Load(bytes, data),
                Rob = allRobs.FirstOrDefault(r => robNames[r] == name),
                Palette = first.Palette, MapIds = group.Select(m => m.Id).ToList(),
            };
            if (name != OpeningScene && first.World is { } wld && File.Exists(Path.Combine(rgDir, "maps", wld + ".WLD")))
            {
                scene.Terrain = RgWorld.Load(File.ReadAllBytes(Path.Combine(rgDir, "maps", wld + ".WLD")));
                scene.TerrainFile = wld.ToUpperInvariant();
            }
            scenes[name] = scene;
        }
    }

    /// <summary>Scenery that moves with the ground rather than holding it (RgWarp): palms, plants, rocks, fish.</summary>
    static readonly string[] Scenery = ["PALMTR", "PLANT", "ROCK", "WATROCK", "DOLPHIN", "DR_FLWR"];

    /// <summary>
    /// Each terrain's stretch (RgWarp), shared by the scenes on the same .WLD (Stros M'Kai and its palace grounds), from
    /// the footprints of what stands in them.
    /// </summary>
    void BuildWarps(Action<string>? log)
    {
        foreach (var group in scenes.Values.Where(sc => sc.Terrain is not null).GroupBy(sc => sc.TerrainFile!))
        {
            var rigid = new List<(Vector2 Min, Vector2 Max)>();
            foreach (var scene in group)
                foreach (var o in scene.Data.Statics.Concat(scene.Data.Objects.Where((o, i) => scene.Data.ObjectTypes[i] == 1 && o.Model.Length > 0)))
                {
                    string name = o.Model.Split('.')[0].Trim().ToUpperInvariant();
                    if (Scenery.Any(name.StartsWith) || Model(scene, name) is not { } model || model.Vertices.Count == 0) continue;
                    var at = RgSpace.Point(o.Position);
                    var pts = model.Vertices.Select(v => RgSpace.Dir(Vector3.Transform(v, o.Rotation)) + at).ToList();
                    var mn = pts.Aggregate(Vector3.Min); var mx = pts.Aggregate(Vector3.Max);
                    if (MathF.Max(mx.X - mn.X, mx.Y - mn.Y) < 200 && mx.Z - mn.Z < 300) continue;
                    rigid.Add((new Vector2(mn.X, mn.Y), new Vector2(mx.X, mx.Y)));
                }
            var w = group.First().Terrain!;
            var land = new List<Vector2>();
            for (int gz = 8; gz < RgWorld.Size - 8; gz++)
                for (int gx = 8; gx < RgWorld.Size - 8; gx++)
                    if (w.HeightAt(gx, gz) > 0 && !WaterTiles.Contains(w.Texture[gx, gz] & 0x3F))
                        land.Add(new Vector2(gx * RgWorld.Cell - RgSpace.Half, RgSpace.Half - gz * RgWorld.Cell));
            var warp = RgWarp.Build(rigid, land, RgWarp.Stretch, line => log?.Invoke($"{group.Key}: {line}"), group.Key);
            foreach (var scene in group) scene.Warp = warp;
        }
    }

    /// <summary>A scene position in Skyrim, in the scene's stretched world; indoors, about the origin
    /// (<see cref="CentreInteriors"/>) and never past <see cref="InteriorReach"/> on x or y.</summary>
    static Vector3 At(Scene scene, Vector3 position)
    {
        var p = scene.Warp.Forward(RgSpace.Point(position)) + scene.Offset;
        if (scene.Indoors && (MathF.Abs(p.X) > InteriorReach || MathF.Abs(p.Y) > InteriorReach))
        {
            Interlocked.Increment(ref interiorClamped);
            p = new Vector3(Math.Clamp(p.X, -InteriorReach, InteriorReach), Math.Clamp(p.Y, -InteriorReach, InteriorReach), p.Z);
        }
        return p;
    }

    /// <summary>How far from the origin, on x or y, anything is placed indoors (see <see cref="CentreInteriors"/>).</summary>
    public const float InteriorReach = 28000;
    static int interiorClamped;
    /// <summary>Indoor scenes moved to the origin, and positions held at <see cref="InteriorReach"/>.</summary>
    public int InteriorsCentred { get; private set; }
    public static int InteriorPlacesHeld => interiorClamped;

    /// <summary>
    /// Every indoor scene moved so the middle of what stands in it is at Skyrim's origin. Redguard's small buildings
    /// keep the island's coordinates indoors (the Draggin Tale's room at 2048, 2298 of 65536), which the conversion
    /// puts about 30,700 units from the origin on both axes, and Skyrim, loading an interior, moves every reference
    /// with a model that stands 30,000 units or more from the origin on x or y to where the player arrives (measured in
    /// the game, 2026-11-12, TMNative.DumpCell: all 124 statics, activators and doors of RGTAVERN at one point, three of
    /// its five people fallen out of the world; with the same plugin's cell moved, every reference kept its place at
    /// 29,937 and none at 30,121, on either axis alone). The move is a whole number of 256 units; the scene's scripts
    /// get it as OffX and OffY (TMRgSoup) for the positions they work out from Redguard's numbers as they run. A place
    /// still past <see cref="InteriorReach"/> after the move (objects Redguard parks at its map's corner) is held there.
    /// </summary>
    void CentreInteriors(Action<string>? log)
    {
        foreach (var scene in scenes.Values.Where(sc => sc.Terrain is null && sc.Name != OpeningScene))
        {
            scene.Indoors = true;
            var pts = scene.Data.Statics.Select(o => o.Position).Concat(scene.Data.Objects.Select(o => o.Position)).Select(RgSpace.Point).ToList();
            if (pts.Count == 0) continue;
            // The middle of the body of the scene: what is within 20,000 of the median (parked objects left out).
            float Median(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
            float mx = Median(pts.Select(p => p.X)), my = Median(pts.Select(p => p.Y));
            var body = pts.Where(p => MathF.Abs(p.X - mx) < 20000 && MathF.Abs(p.Y - my) < 20000).ToList();
            float cx = (body.Min(p => p.X) + body.Max(p => p.X)) / 2, cy = (body.Min(p => p.Y) + body.Max(p => p.Y)) / 2;
            scene.Offset = new Vector3(-MathF.Round(cx / 256) * 256, -MathF.Round(cy / 256) * 256, 0);
            if (scene.Offset != Vector3.Zero) InteriorsCentred++;
            log?.Invoke($"{scene.Name}: indoors, its middle {cx:F0}, {cy:F0} moved to the origin by {scene.Offset.X:F0}, {scene.Offset.Y:F0} ({pts.Count - body.Count} parked far off)");
        }
    }

    int loadWorld;
    readonly Dictionary<RgRob, string> robNames = [];
    readonly Dictionary<(string, string), RgModel?> models = [];

    /// <summary>A model by name for a scene: its own archive first, then loose .3D/.3DC files, then any archive.</summary>
    RgModel? Model(Scene scene, string name)
    {
        name = name.Split('.')[0].Trim().ToUpperInvariant();
        if (name.Length == 0) return null;
        var key = (scene.Name, name);
        if (models.TryGetValue(key, out var hit)) return hit;
        RgModel? FromRob(RgRob? rob) =>
            rob?.Segments.FirstOrDefault(s => s.Type != 512 && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } seg ? RgModel.Load(rob.Data, seg.DataOffset) : null;
        RgModel? Loose()
        {
            foreach (var ext in new[] { ".3D", ".3DC" })
                if (File.Exists(Path.Combine(artDir, name + ext))) return RgModel.Load(File.ReadAllBytes(Path.Combine(artDir, name + ext)));
            return null;
        }
        var model = FromRob(scene.Rob) ?? Loose() ?? FromRob(allRobs.FirstOrDefault(r => robNames[r] == "ISLAND")) ?? allRobs.Select(FromRob).FirstOrDefault(m => m is not null);
        return models[key] = model;
    }

    readonly Dictionary<(string Palette, string Model), (Static Base, RgMeshConverter.Result Mesh)?> statics = [];

    (Static Base, RgMeshConverter.Result Mesh)? StaticFor(Scene scene, string modelName)
    {
        string name = modelName.Split('.')[0].Trim().ToUpperInvariant();
        var key = (scene.Palette, name);
        if (statics.TryGetValue(key, out var hit)) return hit;
        statics[key] = null;
        if (Model(scene, name) is not { } model) { Problems.Add($"{scene.Name}: model {name} not found"); return null; }
        RgMeshConverter.Result? mesh;
        try { mesh = meshes.Convert(model, name, scene.Palette, modOutDir); }
        catch (Exception e) { Problems.Add($"model {name}: {e.Message}"); return null; }
        if (mesh is null) return null;
        short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        if (mesh.Path.Length == 0) return statics[key] = (null!, mesh);   // all water: only the water is placed
        var stat = new Static(mod)
        {
            EditorID = $"RG{Sanitize(scene.Palette)}{Sanitize(name)}",
            ObjectBounds = new ObjectBounds { First = new P3Int16(C(mesh.Min.X), C(mesh.Min.Y), C(mesh.Min.Z)), Second = new P3Int16(C(mesh.Max.X), C(mesh.Max.Y), C(mesh.Max.Z)) },
            Model = new Model { File = mesh.Path[7..] },
        };
        mod.Statics.Add(stat);
        return statics[key] = (stat, mesh);
    }

    static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    // ---- cells ----

    Cell CellFor(Scene scene, Vector3 skyrimPos)
    {
        if (scene.Interior is { } interior) return interior;
        var g = ((int)MathF.Floor(skyrimPos.X / 4096), (int)MathF.Floor(skyrimPos.Y / 4096));
        if (!scene.Cells.TryGetValue(g, out var cell))
            scene.Cells[g] = cell = new Cell(mod) { Grid = new CellGrid { Point = new P2Int(g.Item1, g.Item2) }, Flags = Cell.Flag.HasWater, Persistent = [], Temporary = [] };
        return cell;
    }

    Cell PersistentCellFor(Scene scene) => scene.Interior ?? scene.Top!;

    static System.Drawing.Color Rgb(string? s, int fallback)
    {
        var parts = (s ?? "").Split(',').Select(p => int.TryParse(p.Trim(), out var v) ? Math.Clamp(v, 0, 255) : fallback).ToArray();
        return parts.Length >= 3 ? System.Drawing.Color.FromArgb(0, parts[0], parts[1], parts[2]) : System.Drawing.Color.FromArgb(0, fallback, fallback, fallback);
    }

    void CreateInterior(Scene scene)
    {
        var settings = ini.Maps[scene.MapIds.Min()].Settings;
        var ambient = Rgb(settings.GetValueOrDefault("ambientrgb"), 48);
        // Redguard's interiors are dim and warm-lit; keep a little more ambient so the converted art stays readable.
        ambient = System.Drawing.Color.FromArgb(0, Math.Min(255, ambient.R + 16), Math.Min(255, ambient.G + 14), Math.Min(255, ambient.B + 10));
        var directional = Rgb(settings.GetValueOrDefault("sunrgb"), 64);
        scene.Interior = new Cell(mod)
        {
            EditorID = "RG" + scene.Name,
            Name = Names.GetValueOrDefault(scene.Name, scene.Name),
            Flags = Cell.Flag.IsInteriorCell,
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = new CellLighting
            {
                AmbientColor = ambient,
                DirectionalColor = directional,
                DirectionalFade = 1,
                FogNearColor = System.Drawing.Color.Black,
                FogFarColor = System.Drawing.Color.Black,
                FogFar = 100000,
                FogPower = 1,
                FogMax = 1,
                LightFadeBegin = 6000,
                LightFadeEnd = 8000,
                AmbientColors = new AmbientColors
                {
                    DirectionalXPlus = ambient, DirectionalXMinus = ambient, DirectionalYPlus = ambient,
                    DirectionalYMinus = ambient, DirectionalZPlus = ambient, DirectionalZMinus = ambient,
                    Specular = System.Drawing.Color.Black, Scale = 1,
                },
                Inherits = 0,
            },
            Persistent = [],
            Temporary = [],
        };
        int blockNo = (int)(scene.Interior.FormKey.ID % 10), subNo = (int)(scene.Interior.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
        if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNo);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(scene.Interior);
    }

    void CreateWorldspace(Scene scene)
    {
        var tamriel = SkyrimEsm.Worldspaces.First(w => w.EditorID == "Tamriel");
        scene.Top = new Cell(mod) { Flags = tamriel.TopCell?.Flags ?? 0, SkyrimMajorRecordFlags = tamriel.TopCell?.SkyrimMajorRecordFlags ?? 0, Persistent = [], Temporary = [] };
        scene.World = new Worldspace(mod)
        {
            EditorID = "RG" + scene.Name,
            Name = Names.GetValueOrDefault(scene.Name, scene.Name),
            Climate = new FormLinkNullable<IClimateGetter>(Skyrim.AtmosphereBuilder.DimmedClimate(mod, SkyrimEsm, tamriel.Climate.FormKey, "RG")),
            Water = tamriel.Water.AsNullable(),
            LodWater = tamriel.LodWater.AsNullable(),
            LodWaterHeight = 0,
            LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -2048, DefaultWaterHeight = 0 },
            ObjectBoundsMin = new P2Float(-8, -8),
            ObjectBoundsMax = new P2Float(8, 8),
            WorldMapOffsetScale = 1,
            DistantLodMultiplier = 1,
            MapData = new WorldspaceMap
            {
                NorthwestCellCoords = new P2Int16(-8, 7),
                SoutheastCellCoords = new P2Int16(7, -8),
                CameraMinHeight = 20000,
                CameraMaxHeight = 50000,
                CameraInitialPitch = 50,
            },
            TopCell = scene.Top,
        };
        mod.Worldspaces.Add(scene.World);
        if (scene.Terrain is { } terrain) BuildTerrain(scene, terrain);
    }

    // ---- terrain ----

    readonly Dictionary<(string, int), LandscapeTexture?> landTextures = [];
    static readonly HashSet<int> WaterTiles = [0, 5, 30, 31];
    const float SeaFloor = -420;

    /// <summary>
    /// The grid points of a world's water squares that something big stands on. Redguard's water is flat ground drawn
    /// as water, and what stands on it stands on ground: the observatory's gold foot (OB_EXT02, foot at z 11), the
    /// docks and their posts, N'Gasta's bridges. With every water square sunk to the sea floor they hung at the
    /// waterline 420 over it (rg-floating: 446 models "over the land a -> a + 420"; the user from the docks, 2026-11-12:
    /// "the beacon is floating on the island", the observatory 18,400 away). Under such a model's footprint the land
    /// keeps Redguard's own height, a shoal just under the water.
    /// </summary>
    HashSet<(int X, int Z)> StoodOnWater(Scene scene, Func<Vector3, float>? level = null)
    {
        var stood = new HashSet<(int, int)>();
        foreach (var o in scene.Data.Statics.Concat(scene.Data.Objects.Where((o, i) => scene.Data.ObjectTypes[i] == 1 && o.Model.Length > 0)))
        {
            string name = o.Model.Split('.')[0].Trim().ToUpperInvariant();
            if (name.StartsWith("BOAT") || Model(scene, name) is not { } model || model.Vertices.Count == 0) continue;
            var origin = RgSpace.Point(o.Position);
            var local = model.Vertices.Select(v => RgSpace.Dir(Vector3.Transform(v, o.Rotation))).ToList();
            var mn = local.Aggregate(Vector3.Min); var mx = local.Aggregate(Vector3.Max);
            bool rigid = !Scenery.Any(name.StartsWith) && (MathF.Max(mx.X - mn.X, mx.Y - mn.Y) >= 200 || mx.Z - mn.Z >= 300);
            // Its foot at the water: no higher than a step over it, no deeper than a man.
            // (With a level: its foot against that level where it stands, for water up on the land.)
            float foot = origin.Z + mn.Z - (level?.Invoke(origin) ?? 0);
            if (!rigid || foot > 96 || foot < -160) continue;
            float gx0 = (origin.X + mn.X - 96 + RgSpace.Half) / RgWorld.Cell, gx1 = (origin.X + mx.X + 96 + RgSpace.Half) / RgWorld.Cell;
            float gz0 = (RgSpace.Half - (origin.Y + mx.Y + 96)) / RgWorld.Cell, gz1 = (RgSpace.Half - (origin.Y + mn.Y - 96)) / RgWorld.Cell;
            for (int gz = (int)MathF.Floor(gz0); gz <= (int)MathF.Ceiling(gz1); gz++)
                for (int gx = (int)MathF.Floor(gx0); gx <= (int)MathF.Ceiling(gx1); gx++)
                    stood.Add((gx, gz));
        }
        return stood;
    }

    /// <summary>A water square's height as land: the sea floor, or a shoal under what stands on it.</summary>
    static float WaterLand(float h, bool stood) => stood ? MathF.Min(h, -24) : SeaFloor;

    /// <summary>Skyrim's ocean floor (LCoastOceanFloor01), as Daggerfall's sea bed, for Redguard's water tiles under the sea.</summary>
    static readonly LandscapeTexture OceanFloor = new(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x6B4C6), SkyrimRelease.SkyrimSE);

    LandscapeTexture? LandTexture(string palette, int tile)
    {
        if (WaterTiles.Contains(tile)) return OceanFloor;
        if (landTextures.TryGetValue((palette, tile), out var hit)) return hit;
        landTextures[(palette, tile)] = null;
        if (art.Ensure(palette, 302, tile, land: true) is not { } tex) return null;
        var set = new TextureSet(mod)
        {
            EditorID = $"RGLandSet{Sanitize(palette)}{tile}",
            ObjectBounds = new ObjectBounds(),
            Diffuse = tex.Diffuse[9..],
            NormalOrGloss = tex.Normal[9..],
        };
        mod.TextureSets.Add(set);
        var lt = new LandscapeTexture(mod)
        {
            EditorID = $"RGLand{Sanitize(palette)}{tile}",
            TextureSet = set.ToNullableLink<ITextureSetGetter>(),
            MaterialType = new FormLinkNullable<IMaterialTypeGetter>(),
            HavokFriction = 30,
            HavokRestitution = 30,
            TextureSpecularExponent = 30,
        };
        mod.LandscapeTextures.Add(lt);
        return landTextures[(palette, tile)] = lt;
    }

    /// <summary>
    /// The WLD heightmap as Skyrim land: 16 x 16 cells (Redguard's 256 x 256 grid of 256-unit squares at Skyrim's 128-unit
    /// spacing, heights interpolated), sea floor lowered under Redguard's water tiles (it drew a water plane there at
    /// height 0), texture layers per quadrant weighted by the tiles around each vertex (MwTerrain's scheme).
    /// </summary>
    void BuildTerrain(Scene scene, RgWorld w)
    {
        // Height at a Redguard grid point, with water squares sunk to a sea floor.
        bool Water(int gx, int gz) => WaterTiles.Contains(w.Texture[Math.Clamp(gx, 0, 255), Math.Clamp(gz, 0, 255)] & 0x3F);
        var stood = StoodOnWater(scene);
        int shoals = 0;
        // Water up on the land. Redguard's water is a ground picture, and it lies wherever the land is: the streams and
        // pools of the island are water squares 96 to 848 over the sea. Only the sea's squares were sunk and only the
        // sea has water here, so those lay dry as bare sea-floor ground (user, 2026-11-12: "there was not water where it
        // was suppose to be"; test build rg28 measured: 5,300 land vertices of sea-floor ground 80 or more over the sea).
        // A square of water whose four corners are all 80 or more over the sea is inland water: its bed is cut under
        // it (Bed), and water is laid over it at the level of its lowest corner (LayInlandWater).
        var inland = new bool[RgWorld.Size, RgWorld.Size];
        int inlandSquares = 0;
        for (int sz = 1; sz < RgWorld.Size - 1; sz++)
            for (int sx = 1; sx < RgWorld.Size - 1; sx++)
                if (Water(sx, sz) && MathF.Min(MathF.Min(w.HeightAt(sx, sz), w.HeightAt(sx + 1, sz)), MathF.Min(w.HeightAt(sx, sz + 1), w.HeightAt(sx + 1, sz + 1))) >= 80)
                { inland[sx, sz] = true; inlandSquares++; }
        // How far the bed lies under Redguard's ground at a point of the grid: nothing along a bank (a square that
        // isn't water), the whole depth from 0.4 of a square off it.
        float Bed(float gx, float gz)
        {
            int sx = (int)MathF.Floor(gx), sz = (int)MathF.Floor(gz);
            if (sx < 1 || sz < 1 || sx > RgWorld.Size - 2 || sz > RgWorld.Size - 2 || !inland[sx, sz]) return 0;
            float near = 1;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dz == 0) || Water(sx + dx, sz + dz)) continue;
                    float ox = dx < 0 ? gx - sx : dx > 0 ? sx + 1 - gx : 0, oz = dz < 0 ? gz - sz : dz > 0 ? sz + 1 - gz : 0;
                    near = MathF.Min(near, MathF.Sqrt(ox * ox + oz * oz));
                }
            float t = Math.Clamp(near / 0.4f, 0, 1);
            return InlandBed * t * t * (3 - 2 * t);
        }
        float GridHeight(int gx, int gz)
        {
            bool allWater = true;
            for (int dz = -1; dz <= 0 && allWater; dz++)
                for (int dx = -1; dx <= 0 && allWater; dx++)
                    allWater &= Water(gx + dx, gz + dz);
            float h = w.HeightAt(gx, gz);
            // The map's rim and all beyond it is open sea whatever height the rim holds: ISLAND's first column is 720
            // on water squares, and carried out to the world's edge it stood as a shelf of sea floor 720 over the water
            // all down the west side (test build rg28: cells -25, y: 561 sea-floor vertices each at 720, 35 cells).
            bool rim = gx <= 0 || gz <= 0 || gx >= RgWorld.Size - 1 || gz >= RgWorld.Size - 1;
            if (allWater && rim) return SeaFloor;
            // And a height standing alone in the sea, none of its four water squares water up on the land, is no land
            // either (rg30: a ridge of sea floor 680 high one grid point wide, cells -25, -16 and -25, -17).
            if (allWater && h >= 80 && !(inland[gx - 1, gz - 1] || inland[gx, gz - 1] || inland[gx - 1, gz] || inland[gx, gz])) return SeaFloor;
            if (!(allWater && h < 80)) return h;
            if (stood.Contains((gx, gz))) shoals++;
            return WaterLand(h, stood.Contains((gx, gz)));
        }
        // Skyrim world (x, y), stretched, to the grid: unstretched x = gx * 256 - 32768, y = 32768 - gz * 256.
        (float Gx, float Gz) GridAt(float x, float y)
        {
            var p = scene.Warp.Inverse(new Vector2(x, y));
            return ((p.X + RgSpace.Half) / RgWorld.Cell, (RgSpace.Half - p.Y) / RgWorld.Cell);
        }
        float HeightAt(float x, float y)
        {
            var (gx, gz) = GridAt(x, y);
            int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
            float fx = gx - x0, fz = gz - z0;
            float h00 = GridHeight(x0, z0), h10 = GridHeight(x0 + 1, z0), h01 = GridHeight(x0, z0 + 1), h11 = GridHeight(x0 + 1, z0 + 1);
            return (h00 * (1 - fx) + h10 * fx) * (1 - fz) + (h01 * (1 - fx) + h11 * fx) * fz - Bed(gx, gz);
        }
        // Each square's ground as a mix of plain grounds (RgTileMix): the pictures where two grounds meet are not laid
        // as textures of their own.
        RgTileMix? mixer = null;
        string colFile = Path.Combine(artDir, scene.Palette + ".COL"), bankFile = Path.Combine(artDir, "TEXTURE.302");
        // (With the water pictures as four grounds this crashed the game: test build rg27 died twice with the player at
        // 19500, -16000, 1200, access violation at SkyrimSE.exe+757322, the cell there never attached. A quadrant then
        // had the sea floor as its base and as two or three layers over itself; with one ground a texture (below) the
        // same place loads and stays up, build rg28, 2026-11-12.)
        if (File.Exists(colFile) && File.Exists(bankFile) && RgTextureBank.Load(File.ReadAllBytes(bankFile)) is { } bank)
        {
            mixer = new RgTileMix(w, bank, RgPalette.Load(File.ReadAllBytes(colFile)), t => WaterTiles.Contains(t) || LandTexture(scene.Palette, t) is not null);
            Console.WriteLine($"  {scene.Name} ground: {mixer.Plain} plain pictures, {mixer.Mixed} where two meet (laid as mixes of the plain ones); pictures {mixer.Way.Name}, rows running {(mixer.Way.RowsNorth ? "north" : "south")}: {mixer.Seams.Agree} grid points where the four squares agree, {mixer.Seams.Differ} where they don't");
        }

        var (areaMin, areaMax) = scene.Warp.Bounds();
        int cx0 = (int)MathF.Floor(areaMin.X / 4096), cx1 = (int)MathF.Ceiling(areaMax.X / 4096), cy0 = (int)MathF.Floor(areaMin.Y / 4096), cy1 = (int)MathF.Ceiling(areaMax.Y / 4096);

        // The ground at every land vertex of the world, as (ground, share) lists, made once so that the cells and
        // quadrants sharing a vertex lay the same ground there.
        int vw = (cx1 - cx0) * 32 + 1, vh = (cy1 - cy0) * 32 + 1;
        var ground = new (int T, float W)[vw * vh][];
        var pure = new Dictionary<int, (int T, float W)[]>();
        (int T, float W)[] Pure(int t) => pure.TryGetValue(t, out var hit) ? hit : pure[t] = [(t, 1f)];
        for (int iy = 0; iy < vh; iy++)
            for (int ix = 0; ix < vw; ix++)
            {
                // The four grid squares round the vertex, weighted by how near their middles are.
                var (gx, gz) = GridAt((cx0 * 32 + ix) * 128, (cy0 * 32 + iy) * 128);
                float tu = gx - 0.5f, tv = gz - 0.5f;
                int tx0 = (int)MathF.Floor(tu), tz0 = (int)MathF.Floor(tv);
                float fu = tu - tx0, fv = tv - tz0;
                var wgt = mixer?.At(gx, gz) ?? new Dictionary<int, float>();
                if (mixer is null)
                    foreach (var (tx, tz, f) in new[] { (tx0, tz0, (1 - fu) * (1 - fv)), (tx0 + 1, tz0, fu * (1 - fv)), (tx0, tz0 + 1, (1 - fu) * fv), (tx0 + 1, tz0 + 1, fu * fv) })
                    {
                        if (f <= 0) continue;
                        int t = w.Texture[Math.Clamp(tx, 0, 255), Math.Clamp(tz, 0, 255)] & 0x3F;
                        wgt[t] = wgt.GetValueOrDefault(t) + f;
                    }
                // One ground a texture: Redguard's four water pictures are all the sea floor here, and as four
                // grounds they made it the base and two or three layers over itself in one quadrant (cell 4, -4
                // of the island: base and layers 1 and 3 of quadrant 0, base and both layers of quadrant 1).
                var folded = new Dictionary<int, float>();
                foreach (var (t, v) in wgt)
                {
                    int key = WaterTiles.Contains(t) ? WaterTiles.Min() : t;
                    if (v <= 0 || LandTexture(scene.Palette, key) is null) continue;
                    folded[key] = folded.GetValueOrDefault(key) + v;
                }
                float sum = folded.Values.Sum();
                ground[iy * vw + ix] = folded.Count == 0 ? [] : folded.Count == 1 ? Pure(folded.Keys.First()) : folded.Select(kv => (kv.Key, kv.Value / sum)).ToArray();
            }

        // A quadrant holds six textures. Where more grounds met in one, the least of them were dropped from that
        // quadrant alone, and the quadrant or cell beside it, which kept them, showed another ground along the shared
        // line (user, 2026-11-12: "i saw the landscape seams that need fixed"; test build rg28 measured: 6,999 vertices
        // on cell borders and 8,385 on quadrant borders inside cells with different ground on the two sides, up to a
        // whole swap, 1,030 quadrants at six textures). So the least ground of a crowded quadrant is taken out of its
        // vertices themselves: a vertex with other grounds shares it among them, a vertex of that ground alone takes
        // the ground nearest it in colour that the quadrant keeps. The vertices on its edges change for the neighbours
        // too, so both sides stay alike; repeated until no quadrant holds more than six.
        const int MaxGrounds = 6;
        int qw = (cx1 - cx0) * 2, qh = (cy1 - cy0) * 2, merged = 0, crowded = 0;
        HashSet<int> GroundsOf(int qx, int qy)
        {
            var set = new HashSet<int>();
            for (int ly = 0; ly < 17; ly++)
                for (int lx = 0; lx < 17; lx++)
                    foreach (var (t, _) in ground[(qy * 16 + ly) * vw + qx * 16 + lx]) set.Add(t);
            return set;
        }
        // How much of the whole world each ground covers: after 30 rounds a quadrant still crowded gives up the ground
        // rarest in the world (not its own least) and its lone vertices take a kept one, which is then always a commoner
        // ground, so no two quadrants can pass grounds back and forth and the rounds end (test build rg30: 17 quadrants
        // of the island still did after 200 rounds by their own least).
        var common = new Dictionary<int, float>();
        foreach (var g in ground) foreach (var (t, v) in g) common[t] = common.GetValueOrDefault(t) + v;
        for (int pass = 0; pass < 2000; pass++)
        {
            bool any = false, byWorld = pass >= 30;
            for (int qy = 0; qy < qh; qy++)
                for (int qx = 0; qx < qw; qx++)
                {
                    var totals = new Dictionary<int, float>();
                    for (int ly = 0; ly < 17; ly++)
                        for (int lx = 0; lx < 17; lx++)
                            foreach (var (t, v) in ground[(qy * 16 + ly) * vw + qx * 16 + lx]) totals[t] = totals.GetValueOrDefault(t) + v;
                    if (totals.Count > MaxGrounds && pass == 0) crowded++;
                    while (totals.Count > MaxGrounds)
                    {
                        int least = byWorld ? totals.Keys.MinBy(t => common.GetValueOrDefault(t)) : totals.MinBy(kv => kv.Value).Key;
                        totals.Remove(least);
                        // Land stands in for land and sea floor for sea floor while the quadrant keeps one of its kind (by
                        // colour alone, test build rg31 turned 588 land vertices by the docks into sea floor).
                        var kind = totals.Keys.Where(t => WaterTiles.Contains(t) == WaterTiles.Contains(least)).ToList();
                        if (kind.Count == 0) kind = totals.Keys.ToList();
                        int stand = mixer?.Nearest(least, kind) ?? kind.MaxBy(t => totals[t]);
                        for (int ly = 0; ly < 17; ly++)
                            for (int lx = 0; lx < 17; lx++)
                            {
                                int at = (qy * 16 + ly) * vw + qx * 16 + lx;
                                var g = ground[at];
                                if (!g.Any(e => e.T == least)) continue;
                                var rest = g.Where(e => e.T != least).ToArray();
                                if (rest.Length == 0)
                                {
                                    // On the quadrant's edge the vertex is its neighbours' too: a ground they hold as
                                    // well, where there is one, so that none of them gains a ground by it (with the
                                    // nearest ground alone, test build rg29 left 23 quadrants of the island passing
                                    // grounds back and forth, still over six after 200 rounds).
                                    int put = stand;
                                    if (!byWorld && (lx == 0 || lx == 16 || ly == 0 || ly == 16))
                                    {
                                        var shared = new HashSet<int>(totals.Keys);
                                        for (int oy = ly == 0 ? -1 : 0; oy <= (ly == 16 ? 1 : 0); oy++)
                                            for (int ox = lx == 0 ? -1 : 0; ox <= (lx == 16 ? 1 : 0); ox++)
                                                if ((ox != 0 || oy != 0) && qx + ox >= 0 && qy + oy >= 0 && qx + ox < qw && qy + oy < qh)
                                                {
                                                    // The neighbour's grounds away from this vertex's own old ground.
                                                    var theirs = GroundsOf(qx + ox, qy + oy);
                                                    shared.IntersectWith(theirs);
                                                }
                                        shared.IntersectWith(kind);
                                        if (shared.Count > 0) put = mixer?.Nearest(least, shared) ?? shared.MaxBy(t => totals[t]);
                                    }
                                    ground[at] = Pure(put); totals[put] += 1; continue;
                                }
                                float left = rest.Sum(e => e.W);
                                ground[at] = rest.Length == 1 ? Pure(rest[0].T) : rest.Select(e => (e.T, e.W / left)).ToArray();
                                foreach (var e in rest) totals[e.T] += e.W / left - e.W;
                            }
                        merged++;
                        any = true;
                    }
                }
            if (!any) break;
        }
        int still = 0;
        for (int qy = 0; qy < qh; qy++)
            for (int qx = 0; qx < qw; qx++)
            {
                var seen = new HashSet<int>();
                for (int ly = 0; ly < 17; ly++)
                    for (int lx = 0; lx < 17; lx++)
                        foreach (var (t, _) in ground[(qy * 16 + ly) * vw + qx * 16 + lx]) seen.Add(t);
                if (seen.Count > MaxGrounds) still++;
            }
        Console.WriteLine($"  {scene.Name} land: {crowded} of {qw * qh} quadrants held more than {MaxGrounds} grounds; {merged} least grounds shared out among their neighbours, {still} quadrants still over");
        if (still > 0) Problems.Add($"{scene.Name}: {still} land quadrants still hold more than {MaxGrounds} grounds (seams there)");

        for (int cy = cy0; cy < cy1; cy++)
            for (int cx = cx0; cx < cx1; cx++)
            {
                var cell = CellFor(scene, new Vector3(cx * 4096 + 1, cy * 4096 + 1, 0));
                var h = new float[33, 33];
                for (int y = 0; y < 33; y++)
                    for (int x = 0; x < 33; x++)
                        h[x, y] = HeightAt(cx * 4096 + x * 128, cy * 4096 + y * 128);
                var deltas = new Array2d<sbyte>(new sbyte[33, 33]);
                int origin = (int)MathF.Round(h[0, 0] / 8), rowStart = origin;
                for (int y = 0; y < 33; y++)
                {
                    int t0 = (int)MathF.Round(h[0, y] / 8);
                    int d0 = y == 0 ? 0 : Math.Clamp(t0 - rowStart, -128, 127);
                    rowStart += d0;
                    deltas[0, y] = (sbyte)d0;
                    int current = rowStart;
                    for (int x = 1; x < 33; x++)
                    {
                        int d = Math.Clamp((int)MathF.Round(h[x, y] / 8) - current, -128, 127);
                        current += d;
                        deltas[x, y] = (sbyte)d;
                    }
                }
                var land = new Landscape(mod)
                {
                    Flags = (Landscape.Flag)0x1D,
                    VertexHeightMap = new LandscapeVertexHeightMap { Offset = origin, HeightMap = deltas },
                    VertexNormals = new Array2d<P3UInt8>(new P3UInt8[33, 33]),
                };
                for (int y = 0; y < 33; y++)
                    for (int x = 0; x < 33; x++)
                    {
                        float wx = cx * 4096 + x * 128, wy = cy * 4096 + y * 128;
                        float dx = (HeightAt(wx + 128, wy) - HeightAt(wx - 128, wy)) / 256f, dy = (HeightAt(wx, wy + 128) - HeightAt(wx, wy - 128)) / 256f;
                        float len = MathF.Sqrt(dx * dx + dy * dy + 1);
                        land.VertexNormals[x, y] = new P3UInt8((byte)(sbyte)Math.Round(-dx / len * 127), (byte)(sbyte)Math.Round(-dy / len * 127), (byte)(sbyte)Math.Round(1 / len * 127));
                    }
                // Texture layers per quadrant (17 x 17 vertices), from the vertices' grounds.
                for (int q = 0; q < 4; q++)
                {
                    int vx = (cx - cx0) * 32 + (q & 1) * 16, vy = (cy - cy0) * 32 + (q >> 1) * 16;
                    var weights = new List<(int T, float W)[]>(17 * 17);
                    var totals = new Dictionary<int, float>();
                    for (int ly = 0; ly < 17; ly++)
                        for (int lx = 0; lx < 17; lx++)
                        {
                            var g = ground[(vy + ly) * vw + vx + lx];
                            foreach (var (t, v) in g) totals[t] = totals.GetValueOrDefault(t) + v;
                            weights.Add(g);
                        }
                    var order = totals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(MaxGrounds).ToList();
                    if (order.Count == 0) continue;
                    var quadrant = (Quadrant)q;
                    land.Layers.Add(new BaseLayer { Header = new LayerHeader { Texture = LandTexture(scene.Palette, order[0])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant } });
                    ushort number = 0;
                    for (int li = 1; li < order.Count; li++)
                    {
                        var layer = new AlphaLayer { Header = new LayerHeader { Texture = LandTexture(scene.Palette, order[li])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant, LayerNumber = number }, AlphaLayerData = [] };
                        for (int k = 0; k < weights.Count; k++)
                        {
                            // A layer's strength is its ground's share of the vertex, as it is, and the base takes what
                            // the layers leave. (Written as layers laid one over another, each its share of itself and
                            // all under it, the strengths of a vertex of four even grounds came to 1.08 and of six to
                            // 1.45, and the game, which adds the layers, drew such land too bright, by another amount in
                            // each quadrant: straight bright and dark edges along cell borders with the same grounds on
                            // both sides, seen from above the corner of cells 1,4 2,4 1,5 2,5 in test build rg32.)
                            float alpha = 0;
                            foreach (var (t, v) in weights[k]) if (t == order[li]) alpha = v;
                            if (alpha > 0.004f) layer.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)k, Opacity = Math.Min(alpha, 1f) });
                        }
                        if (layer.AlphaLayerData.Count == 0) continue;
                        land.Layers.Add(layer);
                        number++;
                    }
                }
                land.IsCompressed = true;
                cell.Landscape = land;
            }
        if (inlandSquares > 0) LayInlandWater(scene, w, inland, inlandSquares);
    }

    /// <summary>How far an inland water square's bed is cut under Redguard's ground, and its water under that ground.</summary>
    const float InlandBed = 56, InlandSurface = 10;
    /// <summary>The drop from one water level to the next from which the square between is running water (at 30, the
    /// stream's smaller steps showed as bare straight edges between flat pieces of water: test build rg33).</summary>
    const float RunStep = 8;

    /// <summary>
    /// Skyrim water over Redguard's inland water squares (BuildTerrain). Squares that touch and share a level (the
    /// lowest of a square's four corners) are one water surface (Skyrim/Meshes/WaterSurface, as the water of placed
    /// models), a little under Redguard's ground so that it meets the banks. A square that slopes down from higher
    /// water beside it is where the stream runs down to the next level: it gets vanilla's falling-water sheet as well,
    /// laid along the slope from the upper water to the lower.
    /// </summary>
    void LayInlandWater(Scene scene, RgWorld w, bool[,] inland, int squares)
    {
        float Level(int sx, int sz) => MathF.Min(MathF.Min(w.HeightAt(sx, sz), w.HeightAt(sx + 1, sz)), MathF.Min(w.HeightAt(sx, sz + 1), w.HeightAt(sx + 1, sz + 1)));
        Vector2 Corner(int gx, int gz) => scene.Warp.Forward(new Vector2(gx * RgWorld.Cell - RgSpace.Half, RgSpace.Half - gz * RgWorld.Cell));
        short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        var steps = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        int surfaces = 0, runs = 0, runSquares = 0;

        // Level water: squares joined edge to edge at one level.
        var done = new bool[RgWorld.Size, RgWorld.Size];
        for (int z0 = 1; z0 < RgWorld.Size - 1; z0++)
            for (int x0 = 1; x0 < RgWorld.Size - 1; x0++)
            {
                if (!inland[x0, z0] || done[x0, z0]) continue;
                float level = Level(x0, z0);
                var group = new List<(int X, int Z)>();
                var queue = new Queue<(int X, int Z)>();
                queue.Enqueue((x0, z0)); done[x0, z0] = true;
                while (queue.Count > 0)
                {
                    var (sx, sz) = queue.Dequeue();
                    group.Add((sx, sz));
                    foreach (var (dx, dz) in steps)
                    {
                        int nx = sx + dx, nz = sz + dz;
                        if (!inland[nx, nz] || done[nx, nz] || Level(nx, nz) != level) continue;
                        done[nx, nz] = true; queue.Enqueue((nx, nz));
                    }
                }
                // Counter-clockwise from above: south-west, south-east, north-east, north-west (z runs south).
                var world = group.Select(g => new[] { Corner(g.X, g.Z + 1), Corner(g.X + 1, g.Z + 1), Corner(g.X + 1, g.Z), Corner(g.X, g.Z) }).ToList();
                var lo = world.SelectMany(q => q).Aggregate(Vector2.Min); var hi = world.SelectMany(q => q).Aggregate(Vector2.Max);
                var centre = (lo + hi) / 2;
                float z = level - InlandSurface;
                string rel = $@"{RgMeshConverter.OutFolder}\water\{scene.Name.ToLowerInvariant()}_{waterSurfaces}.nif";
                Skyrim.Meshes.WaterSurface.Write(skyrimData, Path.Combine(modOutDir, rel), world.Select(q => q.Select(v => v - centre).ToArray()).ToList(), InlandBed + 16);
                var water = new Mutagen.Bethesda.Skyrim.Activator(mod)
                {
                    EditorID = $"RGWater{scene.Name}{waterSurfaces}",
                    ObjectBounds = new ObjectBounds { First = new P3Int16(C(lo.X - centre.X), C(lo.Y - centre.Y), -8), Second = new P3Int16(C(hi.X - centre.X), C(hi.Y - centre.Y), 8) },
                    Model = new Model { File = rel[7..] },
                    WaterType = scene.World!.Water.FormKey.ToNullableLink<IWaterGetter>(),
                };
                mod.Activators.Add(water);
                CellFor(scene, new Vector3(centre, z)).Temporary.Add(new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(water.FormKey),
                    Placement = new Placement { Position = new P3Float(centre.X, centre.Y, z), Rotation = new P3Float(0, 0, 0) },
                });
                waterSurfaces++;
                surfaces++;
            }

        // Running water: sloping squares with higher water beside them, joined edge to edge.
        bool Runs(int sx, int sz)
        {
            if (!inland[sx, sz]) return false;
            float level = Level(sx, sz);
            float top = MathF.Max(MathF.Max(w.HeightAt(sx, sz), w.HeightAt(sx + 1, sz)), MathF.Max(w.HeightAt(sx, sz + 1), w.HeightAt(sx + 1, sz + 1)));
            if (top - level < RunStep) return false;
            foreach (var (dx, dz) in steps)
                if (inland[sx + dx, sz + dz] && Level(sx + dx, sz + dz) >= level + RunStep) return true;
            return false;
        }
        var ran = new bool[RgWorld.Size, RgWorld.Size];
        for (int z0 = 1; z0 < RgWorld.Size - 1; z0++)
            for (int x0 = 1; x0 < RgWorld.Size - 1; x0++)
            {
                if (ran[x0, z0] || !Runs(x0, z0)) continue;
                var group = new List<(int X, int Z)>();
                var queue = new Queue<(int X, int Z)>();
                queue.Enqueue((x0, z0)); ran[x0, z0] = true;
                while (queue.Count > 0)
                {
                    var (sx, sz) = queue.Dequeue();
                    group.Add((sx, sz));
                    foreach (var (dx, dz) in steps)
                    {
                        int nx = sx + dx, nz = sz + dz;
                        if (ran[nx, nz] || !Runs(nx, nz)) continue;
                        ran[nx, nz] = true; queue.Enqueue((nx, nz));
                    }
                }
                Vector3 Point(int gx, int gz) => new(Corner(gx, gz), w.HeightAt(gx, gz) - InlandSurface);
                var world = group.Select(g => new[] { Point(g.X, g.Z + 1), Point(g.X + 1, g.Z + 1), Point(g.X + 1, g.Z), Point(g.X, g.Z) }).ToList();
                var lo = world.SelectMany(q => q).Aggregate(Vector3.Min); var hi = world.SelectMany(q => q).Aggregate(Vector3.Max);
                var centre = (lo + hi) / 2;
                // Two triangles a square (its four corners are seldom in one plane).
                var tris = world.SelectMany(q => new[] { new[] { q[0] - centre, q[1] - centre, q[2] - centre }, new[] { q[0] - centre, q[2] - centre, q[3] - centre } }).ToList();
                string rel = $@"{RgMeshConverter.OutFolder}\water\{scene.Name.ToLowerInvariant()}_run{fallStatics.Count}.nif";
                Skyrim.Meshes.WaterSurface.WriteFalls(skyrimData, Path.Combine(modOutDir, rel), tris);
                var fall = new Static(mod)
                {
                    EditorID = $"RGWaterRun{scene.Name}{fallStatics.Count}",
                    ObjectBounds = new ObjectBounds { First = new P3Int16(C(lo.X - centre.X), C(lo.Y - centre.Y), C(lo.Z - centre.Z)), Second = new P3Int16(C(hi.X - centre.X), C(hi.Y - centre.Y), C(hi.Z - centre.Z)) },
                    Model = new Model { File = rel[7..] },
                };
                mod.Statics.Add(fall);
                fallStatics[$"run_{scene.Name}_{fallStatics.Count}"] = fall;
                CellFor(scene, centre).Temporary.Add(new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(fall.FormKey),
                    Placement = new Placement { Position = new P3Float(centre.X, centre.Y, centre.Z), Rotation = new P3Float(0, 0, 0) },
                });
                runs++;
                runSquares += group.Count;
            }
        // What stands in that water (its foot within a step of Redguard's ground there) now stands that much over it.
        var standing = StoodOnWater(scene, p => LandAt(w, new Vector2(p.X, p.Y)));
        int stoodOn = 0;
        for (int sz = 1; sz < RgWorld.Size - 1; sz++)
            for (int sx = 1; sx < RgWorld.Size - 1; sx++)
                if (inland[sx, sz] && standing.Contains((sx, sz)) && standing.Contains((sx + 1, sz)) && standing.Contains((sx, sz + 1)) && standing.Contains((sx + 1, sz + 1))) stoodOn++;
        Console.WriteLine($"  {scene.Name} inland water: {squares} squares of water 80 or more over the sea; {surfaces} water surfaces laid over them, {runs} runs of falling water over {runSquares} sloping squares; {stoodOn} of the squares under big models' footprints");
    }

    // ---- placement ----

    /// <summary>Scenery set back on the stretched land (for the log).</summary>
    public int SceneryLowered { get; private set; }
    public int SceneryRaised { get; private set; }

    /// <summary>Redguard's land (its own heights, bilinear over its grid) at an unstretched Skyrim position.</summary>
    static float LandAt(RgWorld w, Vector2 unstretched)
    {
        float gx = (unstretched.X + RgSpace.Half) / RgWorld.Cell, gz = (RgSpace.Half - unstretched.Y) / RgWorld.Cell;
        int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
        float fx = gx - x0, fz = gz - z0;
        float H(int x, int z) => w.HeightAt(Math.Clamp(x, 0, RgWorld.Size - 1), Math.Clamp(z, 0, RgWorld.Size - 1));
        return (H(x0, z0) * (1 - fx) + H(x0 + 1, z0) * fx) * (1 - fz) + (H(x0, z0 + 1) * (1 - fx) + H(x0 + 1, z0 + 1) * fx) * fz;
    }

    /// <summary>
    /// How far to move a piece of scenery up or down so it stands on the stretched land as it stood on Redguard's
    /// (user, 2026-11-10: things floating on Stros M'Kai's slopes). Scenery moves with the ground under its origin and
    /// keeps its height, but a clump of palms is one model up to 560 units across, and the stretch makes the slope
    /// under it gentler: the trunks away from the origin were left up to 148 units in the air (rg-floating: 192 palm
    /// and plant models over 8 higher than in Redguard, none of anything else). Lowered until no foot is higher over
    /// the land than it was; a piece left deeper in the ground at every foot is raised by the least of them.
    /// </summary>
    float SettleScenery(Scene scene, string modelName, Vector3 position, Matrix4x4 rotation)
    {
        string name = modelName.Split('.')[0].Trim().ToUpperInvariant();
        if (scene.Terrain is not { } w || scene.Warp.IsNone || !Scenery.Any(name.StartsWith) || Model(scene, name) is not { } model || model.Vertices.Count == 0) return 0;
        var origin = RgSpace.Point(position);
        var moved = scene.Warp.Forward(origin);
        var local = model.Vertices.Select(v => RgSpace.Dir(Vector3.Transform(v, rotation))).ToList();
        float lowest = local.Min(v => v.Z), top = local.Max(v => v.Z);
        float most = float.MinValue, least = float.MaxValue;
        foreach (var v in local.Where(v => v.Z <= lowest + MathF.Max(8, (top - lowest) * 0.1f)))
        {
            var was = origin + v; var isNow = moved + v;
            float rise = (isNow.Z - LandAt(w, scene.Warp.Inverse(new Vector2(isNow.X, isNow.Y)))) - (was.Z - LandAt(w, new Vector2(was.X, was.Y)));
            most = MathF.Max(most, rise); least = MathF.Min(least, rise);
        }
        if (most > 2) { SceneryLowered++; return -most; }
        if (most < -2) { SceneryRaised++; return -most; }
        return 0;
    }

    PlacedObject? PlaceStatic(Scene scene, (Static Base, RgMeshConverter.Result Mesh) s, Vector3 position, Matrix4x4 rotation, string? model = null)
    {
        var stat = s.Base;
        var at = At(scene, position);
        if (model is not null) at.Z += SettleScenery(scene, model, position, rotation);
        var euler = RgSpace.Euler(rotation);
        PlaceWater(scene, s.Mesh, at, euler);
        if (stat is null) return null;
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(stat.FormKey),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(euler.X, euler.Y, euler.Z) },
        };
        CellFor(scene, at).Temporary.Add(placed);
        meshOf[placed.FormKey] = s.Mesh;
        return placed;
    }

    // ---- ships: the tool's ship in place of Redguard's ----

    Skyrim.Meshes.ShipModel.Made? ship;
    bool shipTried;
    public int Ships { get; private set; }

    /// <summary>
    /// Redguard's ships (the BOAT models in Stros M'Kai's harbour) replaced by the tool's ship (Skyrim/Meshes/ShipModel;
    /// user, 2026-10-09: "redguards and daggerfalls ship need replaced with this"): as long as the boat, along its long
    /// axis with the bow at its narrower end, the keel where the boat's hull bottom was.
    /// </summary>
    bool PlaceShip(Scene scene, RgScene.Placed o)
    {
        string name = o.Model.Split('.')[0].Trim().ToUpperInvariant();
        if (scene.Interior is not null || !name.StartsWith("BOAT") || Model(scene, name) is not { } model || model.Vertices.Count == 0) return false;   // outdoors only: indoors they are models on display
        if (!shipTried) { shipTried = true; ship = Skyrim.Meshes.ShipModel.Build(mod, modOutDir, "RGShip", Problems); }
        if (ship is null) return false;
        var at = At(scene, o.Position);
        var pts = model.Vertices.Select(v => RgSpace.Dir(Vector3.Transform(v, o.Rotation))).ToList();
        // Long axis: the horizontal principal direction.
        var mid = pts.Aggregate(Vector3.Zero, (a, p) => a + p) / pts.Count;
        float sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts) { float dx = p.X - mid.X, dy = p.Y - mid.Y; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        float angle = 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
        var axis = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var across = new Vector2(-axis.Y, axis.X);
        var along = pts.Select(p => Vector2.Dot(new Vector2(p.X, p.Y), axis)).ToList();
        float a0 = along.Min(), a1 = along.Max(), len = a1 - a0;
        float Width(Func<float, bool> end)
        {
            var w = pts.Where((p, i) => end(along[i])).Select(p => Vector2.Dot(new Vector2(p.X, p.Y), across)).ToList();
            return w.Count == 0 ? 0 : w.Max() - w.Min();
        }
        var acrossVals = pts.Select(p => Vector2.Dot(new Vector2(p.X, p.Y), across)).ToList();
        var centre = axis * ((a0 + a1) / 2) + across * ((acrossVals.Min() + acrossVals.Max()) / 2);   // footprint middle
        if (Width(a => a > a1 - len * 0.12f) > Width(a => a < a0 + len * 0.12f)) axis = -axis;   // bow: the narrower end
        float heading = MathF.Atan2(axis.X, axis.Y);   // the ship's +Y (bow) onto the axis
        float keel = pts.Min(p => p.Z);
        // As long as the boat, and no wider: the tool's hull is broader for its length than Redguard's boats, and one
        // scaled by length alone lay through both piers of the harbour slip its boat sat in (2026-11-06: 281 and 261 units
        // in). Its hull's width below the deck, at scale 1.
        var shipHull = ship.Solid.SelectMany(t => t).Where(v => v.Z > 0 && v.Z <= ship.Deck + 16).ToList();
        float hullWidth = shipHull.Count == 0 ? 0 : shipHull.Max(v => v.X) - shipHull.Min(v => v.X);
        float boatWidth = acrossVals.Max() - acrossVals.Min();
        float scale = len / Skyrim.Meshes.ShipModel.Length;
        if (hullWidth > 0 && boatWidth > 0) scale = MathF.Min(scale, boatWidth / hullWidth);
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(ship.Static.FormKey),
            // In the water to its waterline (ShipModel.Draught): Redguard's boats are modelled from the water up, and the
            // ship put with its keel where their bottoms are stood on the sea, its whole hull out of it (2026-11-10, measured
            // on a build: all ten ships' keels at 0 to 5 over a sea at 0; user: "boats dont touch the water").
            Placement = new Placement { Position = new P3Float(at.X + centre.X, at.Y + centre.Y, at.Z + keel - Skyrim.Meshes.ShipModel.Draught(ship.Deck) * scale), Rotation = new P3Float(0, 0, heading) },
            Scale = scale,
        };
        CellFor(scene, at).Temporary.Add(placed);
        shipsByScript[(scene.Name, o.Script)] = (placed, placed.Scale ?? 1);
        placedShips.Add((scene.Name, placed));
        meshOf[placed.FormKey] = new RgMeshConverter.Result("", ship.Min, ship.Max, ship.Solid, 0, [], []);
        Ships++;
        return true;
    }

    // ---- water: Skyrim's in place of Redguard's ----

    readonly Dictionary<string, Static> fallStatics = [];
    int waterSurfaces;
    public int WaterSurfaces => waterSurfaces;

    /// <summary>
    /// Skyrim water where a placed model had Redguard's (RgMeshConverter.WaterImages): each level of its flat water
    /// becomes a water surface of that outline (Skyrim/Meshes/WaterSurface, an activator of the interior still water
    /// type indoors, of the sea's water type outdoors, as Daggerfall's), its falling water vanilla's waterfall sheet.
    /// </summary>
    void PlaceWater(Scene scene, RgMeshConverter.Result mesh, Vector3 at, Vector3 euler)
    {
        if (mesh.Water.Count == 0 && mesh.Falls.Count == 0) return;
        var m = RgSpace.SkyrimMatrix(euler);
        // Falling water: one sheet per model, placed as the model is.
        if (mesh.Falls.Count > 0)
        {
            string name = Path.GetFileNameWithoutExtension(mesh.Path.Length > 0 ? mesh.Path : $"water{fallStatics.Count}");
            string key = $"{scene.Palette}_{name}_{mesh.Falls.Count}_{mesh.Falls[0][0]}";
            if (!fallStatics.TryGetValue(key, out var fall))
            {
                string rel = $@"{RgMeshConverter.OutFolder}\water\{scene.Palette.ToLowerInvariant()}_{name}_falls{fallStatics.Count}.nif";
                Skyrim.Meshes.WaterSurface.WriteFalls(skyrimData, Path.Combine(modOutDir, rel), mesh.Falls);
                var lo = mesh.Falls.SelectMany(p => p).Aggregate(Vector3.Min); var hi = mesh.Falls.SelectMany(p => p).Aggregate(Vector3.Max);
                short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                fall = new Static(mod)
                {
                    EditorID = $"RGWaterfall{fallStatics.Count}",
                    ObjectBounds = new ObjectBounds { First = new P3Int16(C(lo.X), C(lo.Y), C(lo.Z)), Second = new P3Int16(C(hi.X), C(hi.Y), C(hi.Z)) },
                    Model = new Model { File = rel[7..] },
                };
                mod.Statics.Add(fall);
                fallStatics[key] = fall;
            }
            CellFor(scene, at).Temporary.Add(new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(fall.FormKey),
                Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(euler.X, euler.Y, euler.Z) },
            });
        }
        if (mesh.Water.Count == 0) return;
        // Flat water in the world, by level (within 16 units).
        var world = mesh.Water.Select(p => p.Select(v => Vector3.Transform(v, m) + at).ToArray()).ToList();
        var bottom = (mesh.Solid.Count > 0 ? mesh.Solid.SelectMany(p => p) : mesh.Water.SelectMany(p => p)).Select(v => (Vector3.Transform(v, m) + at).Z).Min();
        foreach (var level in world.GroupBy(p => (int)MathF.Round(p.Average(v => v.Z) / 16)))
        {
            var polys = level.ToList();
            float z = polys.Average(p => p.Average(v => v.Z));
            var lo = polys.SelectMany(p => p).Aggregate(Vector3.Min); var hi = polys.SelectMany(p => p).Aggregate(Vector3.Max);
            var centre = new Vector2((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2);
            var local = polys.Select(p => p.Select(v => new Vector2(v.X, v.Y) - centre).ToArray()).ToList();
            string rel = $@"{RgMeshConverter.OutFolder}\water\{scene.Name.ToLowerInvariant()}_{waterSurfaces}.nif";
            Skyrim.Meshes.WaterSurface.Write(skyrimData, Path.Combine(modOutDir, rel), local, MathF.Max(128, z - bottom + 16));
            short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            var water = new Mutagen.Bethesda.Skyrim.Activator(mod)
            {
                EditorID = $"RGWater{scene.Name}{waterSurfaces}",
                ObjectBounds = new ObjectBounds { First = new P3Int16(C(lo.X - centre.X), C(lo.Y - centre.Y), -8), Second = new P3Int16(C(hi.X - centre.X), C(hi.Y - centre.Y), 8) },
                Model = new Model { File = rel[7..] },
                WaterType = (scene.Interior is not null ? Skyrim.Meshes.WaterSurface.InteriorWaterType : scene.World!.Water.FormKey).ToNullableLink<IWaterGetter>(),
            };
            mod.Activators.Add(water);
            CellFor(scene, new Vector3(centre, z)).Temporary.Add(new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(water.FormKey),
                Placement = new Placement { Position = new P3Float(centre.X, centre.Y, z), Rotation = new P3Float(0, 0, 0) },
            });
            waterSurfaces++;
        }
    }

    readonly Dictionary<FormKey, RgMeshConverter.Result> meshOf = [];

    void PlaceAll(Scene scene)
    {
        var data = scene.Data;
        foreach (var o in data.Statics)
        {
            if (PlaceShip(scene, o)) continue;
            if (StaticFor(scene, o.Model) is not { } s) continue;
            PlaceStatic(scene, s, o.Position, o.Rotation, o.Model);
            Statics++;
        }
        for (int i = 0; i < data.Objects.Count; i++)
        {
            var o = data.Objects[i];
            int type = data.ObjectTypes[i];
            if (scene.Name == OpeningScene && o.Script is "SCENEST" or "BOAT1") continue;   // the opening's hidden controllers
            var at = At(scene, o.Position);
            switch (type)
            {
                case 1 when o.Model.Length > 0:
                    // A scripted prop (lever, door, pickup...): an activator carrying its script.
                    if (ScriptOf(scene, o.Script) is not null && !o.Model.StartsWith("BOAT", StringComparison.OrdinalIgnoreCase)) { if (PlaceScriptedProp(scene, i, o) is not null) { Props++; break; } }
                    if (Pickup(o.Script) is { } pickup) { PlaceItem(scene, pickup.Item, pickup.Count, at); break; }
                    if (PlaceShip(scene, o)) break;
                    if (StaticFor(scene, o.Model) is not { } s) break;
                    PlaceStatic(scene, s, o.Position, o.Rotation, o.Model);
                    Props++;
                    break;
                case 3:
                    // Fires and bubbling lava (LH_FIRE, LAVABUB1): vanilla effects.
                    var kind = o.Script.Contains("FIRE", StringComparison.OrdinalIgnoreCase) ? Skyrim.EffectStandIns.Kind.Flame : Skyrim.EffectStandIns.Kind.Steam;
                    if (Skyrim.EffectStandIns.For(kind, kind == Skyrim.EffectStandIns.Kind.Flame ? 60 : 30) is { } fx) PlaceEffect(scene, fx.Effect, fx.Scale, at);
                    break;
                case 4:
                    // Torches and lamps (NTTORCH, NTLIGHT): a flame and a warm light.
                    if (o.Script.Contains("TORCH", StringComparison.OrdinalIgnoreCase) && Skyrim.EffectStandIns.For(Skyrim.EffectStandIns.Kind.Flame, 16) is { } flame)
                        PlaceEffect(scene, flame.Effect, flame.Scale, at);
                    objectLights[(scene.Name, i)] = PlaceLight(scene, 0xFFB060, 400, at);
                    break;
                case 6 when i < data.ObjectLights.Count:
                    // A local light (formats/RGM.html: MPOB kind 6), its own radius and signed colour.
                    var ol = data.ObjectLights[i];
                    if (PlaceSigned(scene, ol.R, ol.G, ol.B, ol.Radius, at) is { } placed) objectLights[(scene.Name, i)] = placed;
                    ObjectLightsPlaced++;
                    break;
            }
        }
        foreach (var l in data.Lights) PlaceSigned(scene, l.R, l.G, l.B, l.Range, At(scene, l.Position));
    }

    /// <summary>Type-6 objects placed as their lights.</summary>
    public int ObjectLightsPlaced { get; private set; }
    /// <summary>Redguard lights (or their negative channels) that only darken, not placed.</summary>
    public int DarkeningLights { get; private set; }

    /// <summary>
    /// A Redguard light of signed strengths (a negative channel subtracts light: formats/RGM.html; added to the
    /// vertex's light, then clamped): its positive channels as a light. The negative ones darken, which Skyrim's lights
    /// can't be shown to do (none of Skyrim.esm's 435 lights has the Negative flag, sk-output-models 2026-10-23), so
    /// they are left out (before, their magnitude made them white lights where Redguard darkens). Redguard's lights
    /// reach far (often 1000+ units) at low strength; Skyrim's are bright to their edge, so the radius is kept
    /// moderate and colours rounded (each light would otherwise be its own record).
    /// </summary>
    PlacedObject? PlaceSigned(Scene scene, int r, int g, int b, int range, Vector3 at)
    {
        static uint Round(int c) => (uint)Math.Min(255, (c + 16) / 32 * 32);
        uint add = Round(Math.Max(0, r)) << 16 | Round(Math.Max(0, g)) << 8 | Round(Math.Max(0, b));
        if (r < 0 || g < 0 || b < 0) DarkeningLights++;
        return add != 0 ? PlaceLight(scene, add, Math.Clamp(range * 2 / 3, 256, 1024), at) : null;
    }

    (FormKey Item, int Count)? Pickup(string script)
    {
        string s = script.ToUpperInvariant();
        if (s.Contains("GOLD") || s.StartsWith("XMONEY")) return (Sk(0x00000F), s.StartsWith("RND") ? 5 : 10);   // Gold001
        if (s.StartsWith("XHEALTH") && vanilla.Get<IIngestibleGetter>("RestoreHealth01") is { } potion) return (potion.FormKey, 1);
        return null;
    }

    void PlaceItem(Scene scene, FormKey item, int count, Vector3 at) =>
        CellFor(scene, at).Temporary.Add(new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(item),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z + 8), Rotation = new P3Float(0, 0, 0) },
            ItemCount = count > 1 ? count : null,
        });

    void PlaceEffect(Scene scene, FormKey effect, float scale, Vector3 at)
    {
        CellFor(scene, at).Temporary.Add(new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(effect),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(0, 0, 0) },
            Scale = MathF.Abs(scale - 1) > 0.01f ? scale : null,
        });
        Effects++;
    }

    readonly Dictionary<(uint, int, bool, float, bool), Light> lights = [];

    /// <param name="fade">Skyrim's brightness (FadeValue; 1 is a torch's).</param>
    /// <param name="flicker">Whether the light flickers (a script's LightFlicker).</param>
    PlacedObject PlaceLight(Scene scene, uint rgb, int radius, Vector3 at, bool negative = false, float fade = 1, bool flicker = false)
    {
        radius = (radius + 31) / 64 * 64;
        fade = MathF.Round(fade * 4) / 4;
        if (!lights.TryGetValue((rgb, radius, negative, fade, flicker), out var light))
        {
            light = new Light(mod)
            {
                EditorID = $"RGLight{(negative ? "Dark" : "")}{rgb:X6}R{radius}{(fade != 1 ? $"F{(int)(fade * 100)}" : "")}{(flicker ? "Flicker" : "")}",
                ObjectBounds = new ObjectBounds(),
                Time = -1,
                Radius = (uint)radius,
                Color = System.Drawing.Color.FromArgb(0, (int)(rgb >> 16 & 255), (int)(rgb >> 8 & 255), (int)(rgb & 255)),
                FalloffExponent = 1,
                FOV = 90,
                NearClip = 0.001f,
                FadeValue = fade,
                Flags = (negative ? Light.Flag.Negative : 0) | (flicker ? Light.Flag.Flicker : 0),
                FlickerPeriod = 0.5f,
                FlickerIntensityAmplitude = 0.2f,
                FlickerMovementAmplitude = 4,
            };
            mod.Lights.Add(light);
            lights[(rgb, radius, negative, fade, flicker)] = light;
        }
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(light.FormKey),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(0, 0, 0) },
        };
        CellFor(scene, at).Temporary.Add(placed);
        LightCount++;
        return placed;
    }

    // ---- doors ----

    sealed record Trigger(Scene From, string Actor, Vector3 At, int? World, int Marker, int Facing);

    Door? oneWayBack;

    /// <summary>The far end of a one-way map change: a door with no model, hidden, never automatic.</summary>
    Door OneWayBackDoor()
    {
        var d = new Door(mod) { EditorID = "RGOneWayBack", Flags = Door.Flag.Hidden | Door.Flag.MinimalUse, ObjectBounds = new ObjectBounds() };
        mod.Doors.Add(d);
        return d;
    }

    /// <summary>
    /// Every LoadWorld trigger becomes an automatic load door (vanilla AutoLoadDoorMinUse01, which loads when walked
    /// into, as Redguard's triggers did) at the actor's position, sending the player to the named marker of the other
    /// map. A trigger whose map is a script variable (interiors leading back out) goes to the map that has a trigger
    /// into this one. Doors are paired with a trigger in the destination leading back (the one whose own arrival point
    /// lies nearest this door); a door without one gets a hidden partner far below its arrival point.
    /// </summary>
    readonly Dictionary<(string Name, bool Facing), Door> loadDoorBases = [];

    /// <summary>Doors the player opens (LoadDoorBase), against those walked into.</summary>
    public int DoorsOpened { get; private set; }

    /// <summary>
    /// A way between maps that Redguard's script opens on the use key (its ACTIVATE label, "Enter Draggin Tale") is a door
    /// the player opens, by that name, not one that takes them on walking into it (user, 2026-11-11: "we need to use
    /// redguards doors"; rg-script --dump: EXTTAV tests ACTIVATE("$exr") before its LoadWorld). What is seen is Redguard's
    /// own door, part of its scene; the door record here is only what the crosshair finds, with nothing drawn.
    /// <para>Seen in game 2026-11-12 (the Draggin Tale's door from inside): a box of collision at the script object's
    /// place stood behind Redguard's door, whose own collision stopped the crosshair first: no prompt. What the
    /// crosshair finds is now on the non-collidable layer (15: found by the crosshair, walked through; a copy of the
    /// door record placed in the open showed "Open the Draggin Tale" and the player walked through it) and reaches out
    /// in front of the door, as plates one behind another every 12 units (the crosshair's ray, from an eye that the
    /// player's own width keeps 18 or more from the door, always meets one before the door): facing, plates as wide as
    /// Redguard's doors (LH_DOOR 125 x 186, rg-sizes) from 24 to 132 in front of the script object's place, turned to
    /// where the player arrives through the door back; else boxes one inside another, 40 to 136 from it all round.</para>
    /// </summary>
    Door LoadDoorBase(string name, bool facing)
    {
        if (loadDoorBases.TryGetValue((name, facing), out var hit)) return hit;
        string rel = facing ? @"tamrielmerge\rg\loaddoorway.nif" : @"tamrielmerge\rg\loaddoor.nif";
        string path = Path.Combine(modOutDir, "meshes", rel);
        var box = new List<(Vector3[] Pts, Vector3 N, TamrielMerge.Skyrim.Meshes.BlockMeshBuilder.Tex Tex)>();
        Vector3 lo, hi;
        if (facing)
        {
            // Plates across the way, their faces to +y (the reference is turned so +y is into the room).
            for (float y = 24; y <= 132; y += 12)
                box.Add(HoldQuad(new(-70, y, -95), new(70, y, -95), new(70, y, 95), new(-70, y, 95), Vector3.UnitY));
            lo = new Vector3(-70, 0, -95); hi = new Vector3(70, 132, 95);
        }
        else
        {
            for (float r = 40; r <= 136; r += 12) box.AddRange(HoldBox(new Vector3(-r, -r, -95), new Vector3(r, r, 95)));
            lo = new Vector3(-136, -136, -95); hi = new Vector3(136, 136, 95);
        }
        if (!File.Exists(path))
        {
            using var nif = new nifly.NifFile();
            nif.Create(nifly.NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var fade = TamrielMerge.Skyrim.Meshes.BlockMeshBuilder.Owned(new nifly.BSFadeNode());
            fade.name = new nifly.NiStringRef("loaddoor");
            hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
            var root = nif.GetRootNode();
            TamrielMerge.Skyrim.Meshes.BlockMeshBuilder.AddCollision(nif, root, box.Select(f => (f.Pts, f.N, new TamrielMerge.Skyrim.Meshes.BlockMeshBuilder.Tex("", "", false))).ToList(), false, layer: 15);
            TamrielMerge.Skyrim.Meshes.BlockMeshBuilder.AddBsxFlags(nif, root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (nif.Save(path) != 0) throw new IOException($"nifly failed to save {path}");
        }
        var door = new Door(mod)
        {
            EditorID = "RGDoor" + Sanitize(name) + (facing ? "Way" : ""),
            Name = name,
            Model = new Model { File = rel },
            ObjectBounds = BoundsOf(lo, hi),
        };
        mod.Doors.Add(door);
        return loadDoorBases[(name, facing)] = door;
    }

    /// <summary>Opened doors turned to face where the player arrives through the door back (LoadDoorBase).</summary>
    public int DoorsFacing { get; private set; }

    void BuildDoors()
    {
        var autoDoor = vanilla.Get<IDoorGetter>("AutoLoadDoorMinUse01") ?? throw new InvalidOperationException("AutoLoadDoorMinUse01 not found");
        var triggers = new List<Trigger>();
        foreach (var scene in scenes.Values)
            foreach (var call in scene.Script.Calls.Where(c => c.Function == loadWorld && c.Args.Length >= 3))
                foreach (var o in scene.Data.Objects.Where(o => o.Script.Equals(call.Actor, StringComparison.OrdinalIgnoreCase)).Take(1))
                    // A person's own map change (VILEKID after the riddles: its map a flag it sets) is its script's
                    // (S_LoadWorld), not a door where it stands.
                    if (call.Args[1] is int marker && !(call.Args[0] is null && People.ContainsKey(call.Actor)))
                        triggers.Add(new Trigger(scene, call.Actor, o.Position, call.Args[0], marker, call.Args[2] ?? 0));
        triggers = triggers.DistinctBy(t => (t.From.Name, t.Actor, t.World, t.Marker)).ToList();

        Scene? SceneOfMap(int id) => ini.Maps.TryGetValue(id, out var m) ? scenes.GetValueOrDefault(m.Scene) : null;
        Scene? Destination(Trigger t)
        {
            if (t.World is int id) return SceneOfMap(id);
            // A variable: the map that has a trigger into this one and a marker of that number.
            return triggers.Where(o => o.World is int w && SceneOfMap(w) == t.From && o.From != t.From && t.Marker < o.From.Data.Markers.Count)
                .Select(o => o.From).GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        }

        var doors = new Dictionary<Trigger, (PlacedObject Door, Scene To, Vector3 Arrival, float Facing)>();
        var openedAs = new Dictionary<Trigger, string>();
        foreach (var t in triggers)
        {
            if (Destination(t) is not { } to || t.Marker >= to.Data.Markers.Count) { Problems.Add($"{t.From.Name} {t.Actor}: no destination for LoadWorld({t.World?.ToString() ?? "?"}, {t.Marker})"); continue; }
            if (to == t.From && t.World is null) continue;
            var at = At(t.From, t.At);
            // Opened on the use key where Redguard's script asks for it, walked into where it doesn't (the ways between
            // outdoor maps).
            var opened = LabelOf(t.From, t.Actor);
            if (opened is not null) { DoorsOpened++; openedAs[t] = opened.Value.Name; }
            var door = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(opened is { } lb ? LoadDoorBase(lb.Name, false).FormKey : autoDoor.FormKey),
                Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            PersistentCellFor(t.From).Persistent.Add(door);
            t.From.LoadDoors.Add(door);
            doors[t] = (door, to, At(to, to.Data.Markers[t.Marker]), t.Facing * MathF.Tau / 2048);
        }
        foreach (var (t, d) in doors)
        {
            // The partner: a door in the destination leading back here, arriving nearest this one.
            var backWay = doors.Where(o => o.Value.To == t.From && o.Key.From == d.To)
                .OrderBy(o => Vector3.Distance(o.Value.Arrival, At(t.From, t.At))).ToList();
            var back = backWay.Select(o => o.Value.Door).FirstOrDefault();
            // An opened door faces where the player stands on arriving through the door back: the side it is opened from.
            if (backWay.Count > 0 && openedAs.TryGetValue(t, out var label))
            {
                var here = At(t.From, t.At); var arrive = backWay[0].Value.Arrival;
                float dx = arrive.X - here.X, dy = arrive.Y - here.Y;
                if (dx * dx + dy * dy > 24 * 24)
                {
                    d.Door.Base.SetTo(LoadDoorBase(label, true).FormKey);
                    d.Door.Placement!.Rotation = new P3Float(0, 0, MathF.Atan2(dx, dy));
                    DoorsFacing++;
                }
            }
            if (back is null)
            {
                // A one-way way in (Redguard's LoadWorld without a way back): Skyrim's load door still needs a partner. It
                // stands 64 under the arrival, in the floor where no one reaches it (an auto load door, it would take the
                // player back on arriving), over the navmesh it gets its door triangle from, as every load door has one, and
                // locked for good (a key no one has), so neither the player nor anyone following can go back through it
                // (2026-10-21: 4000 under the arrival, three doors had no door triangle).
                // Not an automatic door: locked or not, the automatic one took whoever walked over it. In a new game Cyrus,
                // free at last on the island's docks, walked ten steps and stood in START's sea: the island's arrival
                // from START has this door under it (seen in game 2026-11-12, twice at 22340, 10960; user: "after the
                // opening you appeared on land that didnt look good instead of the ship"). A door of ours with no model:
                // nothing to walk into and nothing to use.
                oneWayBack ??= OneWayBackDoor();
                back = new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(oneWayBack.FormKey),
                    Placement = new Placement { Position = new P3Float(d.Arrival.X, d.Arrival.Y, d.Arrival.Z - 64), Rotation = new P3Float(0, 0, 0) },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                    Lock = new LockData { Level = LockLevel.RequiresKey },
                };
                PersistentCellFor(d.To).Persistent.Add(back);
                d.To.LoadDoors.Add(back);   // given its door triangle with the others (BuildNavmeshes)
                back.TeleportDestination = new TeleportDestination { Door = d.Door.ToLink<IPlacedObjectGetter>(), Position = d.Door.Placement!.Position, Rotation = new P3Float(0, 0, 0) };
                DoorsOneWay++;
            }
            else DoorsLinked++;
            d.Door.TeleportDestination = new TeleportDestination
            {
                Door = back.ToLink<IPlacedObjectGetter>(),
                Position = new P3Float(d.Arrival.X, d.Arrival.Y, d.Arrival.Z + 8),
                Rotation = new P3Float(0, 0, d.Facing),
            };
        }
    }

    // ---- navmesh ----

    Skyrim.Navmesh.NavmeshBuilder? navmeshes;
    Skyrim.TerrainLod? terrainLod;
    public int TerrainLodChunks => terrainLod?.Chunks ?? 0;

    IEnumerable<Vector3[]> Solids(IEnumerable<PlacedObject> placed)
    {
        foreach (var p in placed)
        {
            if (p.Placement is not { } pl || !meshOf.TryGetValue(p.FormKey, out var mesh)) continue;
            var m = RgSpace.SkyrimMatrix(new Vector3(pl.Rotation.X, pl.Rotation.Y, pl.Rotation.Z));
            var pos = new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
            float sc = p.Scale ?? 1;
            // What the navmesh is made from (RgMeshConverter.Result.Walk: stairs as ramps, a moving prop's level faces).
            foreach (var poly in mesh.Walk ?? mesh.Solid)
                for (int k = 1; k + 1 < poly.Length; k++)
                    yield return [Vector3.Transform(poly[0] * sc, m) + pos, Vector3.Transform(poly[k] * sc, m) + pos, Vector3.Transform(poly[k + 1] * sc, m) + pos];
        }
    }

    /// <summary>The scene's navmeshes begun: an interior's made in the background (<paramref name="interiors"/>), a
    /// worldspace's cells queued (<paramref name="exteriors"/>, made together by NavmeshBuilder.GenerateExteriorCells);
    /// <see cref="BuildAllNavmeshes"/> finishes them in the scenes' order.</summary>
    void BeginNavmeshes(Scene scene, List<(Cell Cell, Skyrim.Navmesh.NavmeshBuilder.PendingInterior Pending)> interiors, List<Skyrim.Navmesh.NavmeshBuilder.ExteriorJob> exteriors)
    {
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        if (scene.Interior is { } cell)
        {
            // Every face turned up. Redguard's maps are wound consistently (2026-10-19, its 21 interiors: 13 of some
            // 257,000 shared edges run the same way), but a floor can face down: the Necromancer's Tower's load door
            // stands 120 over a face wound down at 1192 with the next face, also down, at 1620. As wound, six of the 21
            // lost the navmesh by a door (the tower's from 112 units to 221), and its interiors have no actors to choose
            // between the two by (as Morrowind's and Oblivion's do).
            var solid = new List<(Vector3[] Points, Vector3 Normal)>();
            foreach (var w in Solids(cell.Temporary.OfType<PlacedObject>()))
            {
                var n = Vector3.Cross(w[1] - w[0], w[2] - w[0]);
                if (n.LengthSquared() < 1e-6f) continue;
                n = Vector3.Normalize(n);
                solid.Add((w, n.Z < 0 ? -n : n));
            }
            if (solid.Count == 0) return;
            try
            {
                interiors.Add((cell, navmeshes.BeginInterior(cell, solid, scene.LoadDoors.Cast<IPlacedObjectGetter>(),
                    keepAround: scene.LoadDoors.FirstOrDefault()?.TeleportDestination is null ? null : scene.LoadDoors[0].Placement!.Position,
                    keepAlso: null, underwater: null, background: true,
                    // Actors off it moved onto it (2026-10-21: 29 of Redguard's 162 interior actors stood more than 64 off).
                    snapActors: true,
                    // Pieces joined across the grooves and sills Recast leaves gaps at (2026-11-06: 117 of 162 interior
                    // people on pieces apart from every load door).
                    bridgeGaps: true)));
            }
            catch (Exception e) { Problems.Add($"navmesh {cell.EditorID}: {e.Message}"); }
            return;
        }
        // The world's persistent models (a ship a script sails: Vandar's in the opening, 2026-11-06, left the opening world
        // with no navmesh) by the cell they stand in, with each cell's own.
        var persistentIn = (scene.Top?.Persistent.OfType<PlacedObject>() ?? []).Where(p => p.Placement is not null)
            .GroupBy(p => ((int)MathF.Floor(p.Placement!.Position.X / 4096), (int)MathF.Floor(p.Placement.Position.Y / 4096)))
            .ToDictionary(g => g.Key, g => g.ToList());
        exteriors.Add(navmeshes.BeginExteriorCells(scene.World!, scene.Cells,
            (x, y) => scene.Cells.TryGetValue((x, y), out var c)
                ? Solids(c.Temporary.OfType<PlacedObject>().Concat(persistentIn.GetValueOrDefault((x, y)) ?? []))
                : [],
            _ => 0f, scene.LoadDoors.Cast<IPlacedObjectGetter>().ToList(), Problems));
    }

    /// <summary>Every scene's navmeshes on every core: the interiors in the background while the worldspaces' cells are
    /// made, then all written in the scenes' order (2026-10-22: one interior at a time, then each worldspace waiting
    /// on its slowest cell).</summary>
    void BuildAllNavmeshes()
    {
        var interiors = new List<(Cell Cell, Skyrim.Navmesh.NavmeshBuilder.PendingInterior Pending)>();
        var exteriors = new List<Skyrim.Navmesh.NavmeshBuilder.ExteriorJob>();
        foreach (var scene in scenes.Values) BeginNavmeshes(scene, interiors, exteriors);
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        BeginHoldNavmeshes(interiors);
        Skyrim.Navmesh.NavmeshBuilder.GenerateExteriorCells(exteriors);
        foreach (var (cell, pending) in interiors)
            try
            {
                var r = pending.Finish();
                Navmeshes++;
                NavmeshTriangles += r.Triangles;
            }
            catch (Exception e) { Problems.Add($"navmesh {cell.EditorID}: {e.Message}"); }
        foreach (var job in exteriors)
        {
            var (count, tris) = job.Finish();
            Navmeshes += count;
            NavmeshTriangles += tris;
        }
    }

    /// <summary>
    /// A cell wherever the world's placed models reach (their bounds, placed) and its persistent people stand, so each gets
    /// a navmesh: a cell was made only where some object's origin is, and the opening world, sea with no land, had none
    /// under the part of Vandar's boat whose deck reached the next cell (2026-11-06: four people aboard on no navmesh).
    /// </summary>
    void CoverWorldCells(Scene scene)
    {
        foreach (var p in scene.Top?.Persistent.OfType<PlacedNpc>() ?? [])
            if (p.Placement is { } pl) CellFor(scene, new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z));
        foreach (var p in scene.Cells.Values.ToList().SelectMany(c => c.Temporary.OfType<PlacedObject>()).Concat(scene.Top?.Persistent.OfType<PlacedObject>() ?? []).ToList())
            {
                if (p.Placement is not { } pl || !meshOf.TryGetValue(p.FormKey, out var mesh) || (mesh.Walk ?? mesh.Solid).Count == 0) continue;
                var m = RgSpace.SkyrimMatrix(new Vector3(pl.Rotation.X, pl.Rotation.Y, pl.Rotation.Z));
                var pos = new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
                float sc = p.Scale ?? 1;
                var corners = Enumerable.Range(0, 8).Select(k => Vector3.Transform(new Vector3((k & 1) != 0 ? mesh.Max.X : mesh.Min.X, (k & 2) != 0 ? mesh.Max.Y : mesh.Min.Y, (k & 4) != 0 ? mesh.Max.Z : mesh.Min.Z) * sc, m) + pos).ToList();
                int x0 = (int)MathF.Floor(corners.Min(c => c.X) / 4096), x1 = (int)MathF.Floor(corners.Max(c => c.X) / 4096);
                int y0 = (int)MathF.Floor(corners.Min(c => c.Y) / 4096), y1 = (int)MathF.Floor(corners.Max(c => c.Y) / 4096);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        if (!scene.Cells.ContainsKey((x, y))) CellFor(scene, new Vector3(x * 4096 + 1, y * 4096 + 1, 0));
            }
    }

    void AddWorldCells(Scene scene)
    {
        var ws = scene.World!;
        var blocks = new Dictionary<(int, int), WorldspaceBlock>();
        var subBlocks = new Dictionary<(int, int), WorldspaceSubBlock>();
        static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
        foreach (var ((x, y), cell) in scene.Cells.OrderBy(c => c.Key.Y).ThenBy(c => c.Key.X))
        {
            var bk = (FloorDiv(x, 32), FloorDiv(y, 32));
            var sk = (FloorDiv(x, 8), FloorDiv(y, 8));
            if (!blocks.TryGetValue(bk, out var block)) { blocks[bk] = block = new WorldspaceBlock { BlockNumberX = (short)bk.Item1, BlockNumberY = (short)bk.Item2, GroupType = GroupTypeEnum.ExteriorCellBlock }; ws.SubCells.Add(block); }
            if (!subBlocks.TryGetValue(sk, out var sub)) { subBlocks[sk] = sub = new WorldspaceSubBlock { BlockNumberX = (short)sk.Item1, BlockNumberY = (short)sk.Item2, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; block.Items.Add(sub); }
            sub.Items.Add(cell);
        }
        int minX = scene.Cells.Keys.Min(k => k.X), maxX = scene.Cells.Keys.Max(k => k.X), minY = scene.Cells.Keys.Min(k => k.Y), maxY = scene.Cells.Keys.Max(k => k.Y);
        ws.ObjectBoundsMin = new P2Float(minX, minY);
        ws.ObjectBoundsMax = new P2Float(maxX + 1, maxY + 1);
        ws.MapData!.NorthwestCellCoords = new P2Int16((short)minX, (short)maxY);
        ws.MapData.SoutheastCellCoords = new P2Int16((short)maxX, (short)minY);
    }

    // ---- the whole game ----

    /// <summary>
    /// Dev aid (rg-floating; user, 2026-11-10: "in redguard theres a floating brazier and a floating wall on a hill
    /// slope"): every placed model of the scenes with land whose foot stands higher over the stretched land than it
    /// did over Redguard's own, by how much. The foot is the lowest tenth of its points; "before" is the gap under them
    /// in Redguard's own place, "now" the gap under them where the stretch (RgWarp) puts the model.
    /// </summary>
    /// <summary>Small models in a big one's footprint that the stretch moved apart from it by over a unit, and how many
    /// such pairs were looked at (FloatReport).</summary>
    public List<(string Scene, string Small, string Big, float Distance)> Apart { get; } = [];
    public int Together { get; private set; }

    public List<(string Scene, string Model, bool Rigid, Vector3 At, float Before, float Now, float Size)> FloatReport()
    {
        LoadScenes();
        BuildWarps(null);
        var found = new List<(string, string, bool, Vector3, float, float, float)>();
        foreach (var scene in scenes.Values.Where(sc => sc.Terrain is not null))
        {
            var w = scene.Terrain!;
            bool Water(int gx, int gz) => WaterTiles.Contains(w.Texture[Math.Clamp(gx, 0, 255), Math.Clamp(gz, 0, 255)] & 0x3F);
            var stood = StoodOnWater(scene);
            float Ground(Vector2 unstretched, bool asBuilt = false)
            {
                float gx = (unstretched.X + RgSpace.Half) / RgWorld.Cell, gz = (RgSpace.Half - unstretched.Y) / RgWorld.Cell;
                int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
                float fx = gx - x0, fz = gz - z0;
                // As built: the land BuildTerrain makes, its water squares sunk to the sea floor.
                float H(int x, int z)
                {
                    float h = w.HeightAt(Math.Clamp(x, 0, RgWorld.Size - 1), Math.Clamp(z, 0, RgWorld.Size - 1));
                    return asBuilt && h < 80 && Water(x - 1, z - 1) && Water(x, z - 1) && Water(x - 1, z) && Water(x, z) ? WaterLand(h, stood.Contains((x, z))) : h;
                }
                return (H(x0, z0) * (1 - fx) + H(x0 + 1, z0) * fx) * (1 - fz) + (H(x0, z0 + 1) * (1 - fx) + H(x0 + 1, z0 + 1) * fx) * fz;
            }
            // Every placed model's points and up-facing triangles, where Redguard has them and where the stretch puts
            // them: what a foot stands on is the land or another model's top, whichever is highest under it.
            var placed = new List<(string Name, Vector3 Origin, Vector3 Moved, List<Vector3> Local, bool Rigid, float Size)>();
            var wasGrid = new Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C, int Owner)>>();
            var nowGrid = new Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C, int Owner)>>();
            const float bucket = 256;
            void AddTri(Dictionary<(int, int), List<(Vector3, Vector3, Vector3, int)>> grid, Vector3 a, Vector3 b, Vector3 c, int owner)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.Length() < 1e-3f || MathF.Abs(n.Z) / n.Length() < 0.3f) return;
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / bucket), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / bucket);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / bucket), y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / bucket);
                if (x1 - x0 > 64 || y1 - y0 > 64) return;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = [];
                        list.Add((a, b, c, owner));
                    }
            }
            foreach (var o in scene.Data.Statics.Concat(scene.Data.Objects.Where((o, i) => scene.Data.ObjectTypes[i] == 1 && o.Model.Length > 0)))
            {
                string name = o.Model.Split('.')[0].Trim().ToUpperInvariant();
                if (name.StartsWith("BOAT") || Model(scene, name) is not { } model || model.Vertices.Count == 0) continue;
                var origin = RgSpace.Point(o.Position);
                var moved = scene.Warp.Forward(origin);
                moved.Z += SettleScenery(scene, o.Model, o.Position, o.Rotation);
                var local = model.Vertices.Select(v => RgSpace.Dir(Vector3.Transform(v, o.Rotation))).ToList();
                var mn = local.Aggregate(Vector3.Min); var mx = local.Aggregate(Vector3.Max);
                bool rigid = !Scenery.Any(name.StartsWith) && (MathF.Max(mx.X - mn.X, mx.Y - mn.Y) >= 200 || mx.Z - mn.Z >= 300);
                int owner = placed.Count;
                placed.Add((name, origin, moved, local, rigid, MathF.Max(mx.X - mn.X, mx.Y - mn.Y)));
                foreach (var f in model.Faces)
                    for (int t = 1; t + 1 < f.Vertices.Length; t++)
                    {
                        Vector3 a = local[f.Vertices[0]], b = local[f.Vertices[t]], c = local[f.Vertices[t + 1]];
                        AddTri(wasGrid, origin + a, origin + b, origin + c, owner);
                        AddTri(nowGrid, moved + a, moved + b, moved + c, owner);
                    }
            }
            // The gap under a point: down to the highest of the land and the other models' tops under it (a top up to
            // 12 over the point counts: a foot set a little into what it stands on).
            float Gap(Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C, int Owner)>> grid, Vector3 p, int owner, float land)
            {
                float best = land;
                if (grid.TryGetValue(((int)MathF.Floor(p.X / bucket), (int)MathF.Floor(p.Y / bucket)), out var list))
                    foreach (var (a, b, c, own) in list)
                    {
                        if (own == owner) continue;
                        float det = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                        if (MathF.Abs(det) < 1e-6f) continue;
                        float u = ((p.X - a.X) * (c.Y - a.Y) - (p.Y - a.Y) * (c.X - a.X)) / det, v = ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / det;
                        if (u < -0.02f || v < -0.02f || u + v > 1.02f) continue;
                        float z = a.Z + (b.Z - a.Z) * u + (c.Z - a.Z) * v;
                        if (z <= p.Z + 12 && z > best) best = z;
                    }
                return p.Z - best;
            }
            // Small things set in or against big ones (a door in its wall, a lantern on its house; user, 2026-11-10: doors
            // not against walls, "check redguard"): how far the stretch moves each apart from the big model whose
            // footprint it stands in. Both should move as one.
            foreach (var small in placed.Where(q => !q.Rigid && !Scenery.Any(q.Name.StartsWith)))
            {
                var smallMove = small.Moved - small.Origin;
                foreach (var big in placed.Where(q => q.Rigid))
                {
                    var lo = big.Local.Aggregate(Vector3.Min) + big.Origin; var hi = big.Local.Aggregate(Vector3.Max) + big.Origin;
                    if (small.Origin.X < lo.X - 32 || small.Origin.X > hi.X + 32 || small.Origin.Y < lo.Y - 32 || small.Origin.Y > hi.Y + 32) continue;
                    var apart = smallMove - (big.Moved - big.Origin);
                    float d = new Vector2(apart.X, apart.Y).Length();
                    Together++;
                    if (d > 1) Apart.Add((scene.Name, small.Name, big.Name, d));
                }
            }
            for (int k = 0; k < placed.Count; k++)
            {
                var (name, origin, moved, local, rigid, size) = placed[k];
                float lowest = local.Min(v => v.Z), top = local.Max(v => v.Z);
                var foot = local.Where(v => v.Z <= lowest + MathF.Max(8, (top - lowest) * 0.1f)).ToList();
                if (foot.Count > 24) foot = foot.Where((_, i) => i % (foot.Count / 24 + 1) == 0).ToList();
                // The foot point that lost most of what was under it.
                float before = 0, now = 0, rise = float.MinValue;
                foreach (var v in foot)
                {
                    var was = origin + v; var isNow = moved + v;
                    float b = Gap(wasGrid, was, k, Ground(new Vector2(was.X, was.Y)));
                    float n = Gap(nowGrid, isNow, k, Ground(scene.Warp.Inverse(new Vector2(isNow.X, isNow.Y)), asBuilt: true));
                    if (n - b > rise) { rise = n - b; before = b; now = n; }
                }
                found.Add((scene.Name, name, rigid, new Vector3(moved.X, moved.Y, moved.Z + lowest), before, now, size));
            }
        }
        return found;
    }

    public void Build(Action<string>? log = null)
    {
        LoadScenes();
        BuildWarps(log);
        CentreInteriors(log);
        log?.Invoke($"{scenes.Count} scenes: {string.Join(", ", scenes.Values.Select(s => s.Name + (s.Terrain is null ? "" : " (terrain)")))}");
        LoadText();
        BuildItems();
        log?.Invoke($"items: {ItemCount} ({useEffects.Count} usable)");
        TranslateScripts(log);
        foreach (var scene in scenes.Values)
        {
            if (scene.Terrain is not null || scene.Name == OpeningScene) CreateWorldspace(scene); else CreateInterior(scene);
            PlaceAll(scene);
        }
        // The ships moored at their docks with gangplanks to their decks (RgMooring), before anyone stands on them.
        foreach (var scene in scenes.Values.Where(s => s.World is not null)) MoorShips(scene, log is null ? null : m => log($"{scene.Name} {m}"));
        log?.Invoke($"ships: {ShipsMoored} moored, {Gangplanks} gangplanks");
        // A hatch on each ship's deck and its hold below (RgHolds), where the ships now lie.
        foreach (var scene in scenes.Values.Where(s => s.World is not null)) BuildHolds(scene, log is null ? null : m => log($"{scene.Name} {m}"));
        log?.Invoke($"holds: {Holds} of {placedShips.Count} ships; captain's quarters: {Cabins}");
        // People in their homes first: interiors and the island before the scenes they only visit.
        foreach (var scene in scenes.Values.OrderBy(s => s.Name == "ISLAND" ? 1 : s.Terrain is null ? 0 : 2)) PlaceActors(scene);
        foreach (var scene in scenes.Values) PlaceScriptMarkers(scene);
        log?.Invoke($"scenery set on the stretched land: {SceneryLowered} lowered, {SceneryRaised} raised");
        log?.Invoke($"placed {Statics} statics, {Props} props, {LightCount} lights, {Effects} effects, {ActorCount} actors, {AnimalsPlaced} animals as their own models; {statics.Count} models");
        // Ropes, and the ledges Redguard's Cyrus could catch (RgEngine), once the places' models are there.
        foreach (var scene in scenes.Values) { PlaceRopes(scene); PlaceLedges(scene); }
        surfaces.Clear();
        log?.Invoke($"ropes: {Ropes} ({RopeSwings} to swing on); ledges: {Ledges} to climb");
        log?.Invoke($"interiors: {InteriorsCentred} built about the origin (Skyrim moves what stands 30,000 units out indoors)");

        // The start: where the opening cutscene (START.RGM) sends Cyrus — Stros M'Kai's docks.
        var start = ini.Maps.TryGetValue(ini.StartWorld, out var startMap) ? startMap : null;
        int startMap2 = 1, startMarker = 1, startFacing = 256;
        string startRgm = Path.Combine(rgDir, "maps", (start?.Scene ?? "START") + ".RGM");
        if (File.Exists(startRgm))
        {
            var bytes = File.ReadAllBytes(startRgm);
            var call = RgScript.Load(bytes, RgScene.Load(bytes)!).Calls.FirstOrDefault(c => c.Function == loadWorld && c.Args.Length >= 3 && c.Args[0] is int && c.Args[1] is int);
            if (call is not null) { startMap2 = call.Args[0]!.Value; startMarker = call.Args[1]!.Value; startFacing = call.Args[2] ?? 0; }
        }
        if (ini.Maps.TryGetValue(startMap2, out var sm) && scenes.TryGetValue(sm.Scene, out var startScene) && startMarker < startScene.Data.Markers.Count)
        {
            var at = At(startScene, startScene.Data.Markers[startMarker]);
            PlaceDocksArrival(startScene, at, startFacing * MathF.Tau / 2048, log);
            log?.Invoke($"docks arrival: {startScene.Name} marker {startMarker} at {at}");
        }
        else Problems.Add("start marker not placed");

        BuildDialogue();
        log?.Invoke($"dialogue: {DialogueActors} speakers, {DialogueTopics} topics, {DialogueResponses} responses, {VoiceMap.Count} voiced lines, {rgGlobals.Count} globals, {logStages.Count} log entries");
        // The story starts with the opening at sea; without it, at the docks.
        if (!BuildOpening(log) && docksArrival is not null)
        {
            StartMarker.Placement = docksArrival.Placement!.DeepCopy();
            var docksCell = scenes.Values.First(s => s.Top?.Persistent.Contains(docksArrival) == true || s.Interior?.Persistent.Contains(docksArrival) == true);
            PersistentCellFor(docksCell).Persistent.Add(StartMarker);
        }
        BuildDoors();
        log?.Invoke($"doors: {DoorsLinked} linked, {DoorsOneWay} one-way");
        AttachScripts(log);
        BuildLaw();
        log?.Invoke($"law: no bounty; {GuardCount} guards mind drawn weapons (RGLaw)");
        foreach (var scene in scenes.Values.Where(s => s.World is not null)) { CoverWorldCells(scene); AddWorldCells(scene); }
        BuildAllNavmeshes();
        int links = navmeshes?.LinkExteriorEdges() ?? 0;
        log?.Invoke($"navmeshes: {Navmeshes} ({NavmeshTriangles} triangles), {links} exterior edge links");
        foreach (var scene in scenes.Values.Where(s => s.World is not null))
        {
            terrainLod ??= new Skyrim.TerrainLod(mod, modOutDir, skyrimData);
            // An island in the sea: the distant view's whole square sea where there are no cells (TerrainLod seaLevel).
            terrainLod.Build(scene.World!.EditorID!, scene.Cells, _ => 0f, -2048, seaLevel: 0f);
        }
        if (terrainLod is not null) Problems.AddRange(terrainLod.Problems);
        Problems.AddRange(art.Problems.Distinct());
    }

    public void Write()
    {
        Skyrim.SwingDoorTwins.AddAll(mod);
        Directory.CreateDirectory(modOutDir);
        TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
        // References held by scripts, quests and lists persistent, in their worldspace's persistent cell (HeldReferences).
        TamrielMerge.Skyrim.HeldReferences.Prepare(mod, Console.WriteLine);
        TamrielMerge.Skyrim.HavokSettle.Apply(mod, modOutDir, Console.WriteLine);
        TamrielMerge.Skyrim.InteriorBounds.Apply(mod, Console.WriteLine);
        Console.WriteLine($"  doors: {DoorsOpened} opened on the use key by Redguard's own label ({loadDoorBases.Keys.Select(k => k.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()} names; {DoorsFacing} turned to the side they are opened from), the rest walked into");
        Console.WriteLine($"  textures on faces: {RgModel.FacesUnwrapped} faces' corners brought back by whole textures (stripes), {RgModel.FacesByFourth} mapped by their fourth corner (first three in a line)");
        Console.WriteLine($"  actors: {ActorsRaised} set on top of what their legs stood inside; {vanilla.NotableFacesLeftOut} well-known Skyrim faces (jarls, their courts, the titled) left out of the looks");
        // Masters in load order, as every other converted plugin's (Mutagen's default is alphabetical: Dragonborn.esm before
        // Skyrim.esm). Written alphabetically, the plugin's navmesh map override was Skyrim's map through master 1, and in the
        // game its entries were missing: entering Oblivion's prison crashed reading a navmesh entry the game made itself, with
        // no parent cell (2026-11-05, SkyrimSE.exe+4E02B8).
        mod.WriteToBinary(Path.Combine(modOutDir, PluginName), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(Skyrim.VanillaMasters.Order),
        });
        // What procedural creatures may use from Redguard (read by the creatures step when that option is on).
        WriteCreatureCatalog(Path.Combine(modOutDir, PluginName));
        if (VoiceMap.Count > 0)
            File.WriteAllText(Path.Combine(modOutDir, "Redguard.voicemap.json"), System.Text.Json.JsonSerializer.Serialize(VoiceMap));
    }
}
