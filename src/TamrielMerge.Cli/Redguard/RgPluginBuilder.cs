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

    /// <summary>A scene position in Skyrim, in the scene's stretched world.</summary>
    static Vector3 At(Scene scene, Vector3 position) => scene.Warp.Forward(RgSpace.Point(position));

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
            Climate = tamriel.Climate.AsNullable(),
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

    /// <summary>Skyrim's ocean floor (LCoastOceanFloor01), as Daggerfall's sea bed, for Redguard's water tiles under the sea.</summary>
    static readonly LandscapeTexture OceanFloor = new(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x6B4C6), SkyrimRelease.SkyrimSE);

    LandscapeTexture? LandTexture(string palette, int tile)
    {
        if (WaterTiles.Contains(tile)) return OceanFloor;
        if (landTextures.TryGetValue((palette, tile), out var hit)) return hit;
        landTextures[(palette, tile)] = null;
        if (art.Ensure(palette, 302, tile) is not { } tex) return null;
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
        float GridHeight(int gx, int gz)
        {
            bool allWater = true;
            for (int dz = -1; dz <= 0 && allWater; dz++)
                for (int dx = -1; dx <= 0 && allWater; dx++)
                    allWater &= Water(gx + dx, gz + dz);
            float h = w.HeightAt(gx, gz);
            return allWater && h < 80 ? SeaFloor : h;
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
            return (h00 * (1 - fx) + h10 * fx) * (1 - fz) + (h01 * (1 - fx) + h11 * fx) * fz;
        }
        int TileAt(int gx, int gz) => w.Texture[Math.Clamp(gx, 0, 255), Math.Clamp(gz, 0, 255)] & 0x3F;

        var (areaMin, areaMax) = scene.Warp.Bounds();
        int cx0 = (int)MathF.Floor(areaMin.X / 4096), cx1 = (int)MathF.Ceiling(areaMax.X / 4096), cy0 = (int)MathF.Floor(areaMin.Y / 4096), cy1 = (int)MathF.Ceiling(areaMax.Y / 4096);
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
                // Texture layers per quadrant (17 x 17 vertices); a vertex counts the grid squares it touches.
                for (int q = 0; q < 4; q++)
                {
                    int qx = (q & 1) * 16, qy = (q >> 1) * 16;
                    var weights = new List<Dictionary<int, float>>(17 * 17);
                    var totals = new Dictionary<int, float>();
                    for (int ly = 0; ly < 17; ly++)
                        for (int lx = 0; lx < 17; lx++)
                        {
                            // The four grid squares round the vertex, weighted by how near their middles are.
                            var (gx, gz) = GridAt(cx * 4096 + (qx + lx) * 128, cy * 4096 + (qy + ly) * 128);
                            float tu = gx - 0.5f, tv = gz - 0.5f;
                            int tx0 = (int)MathF.Floor(tu), tz0 = (int)MathF.Floor(tv);
                            float fu = tu - tx0, fv = tv - tz0;
                            var wgt = new Dictionary<int, float>();
                            foreach (var (tx, tz, f) in new[] { (tx0, tz0, (1 - fu) * (1 - fv)), (tx0 + 1, tz0, fu * (1 - fv)), (tx0, tz0 + 1, (1 - fu) * fv), (tx0 + 1, tz0 + 1, fu * fv) })
                            {
                                if (f <= 0) continue;
                                int t = TileAt(tx, tz);
                                wgt[t] = wgt.GetValueOrDefault(t) + f;
                            }
                            foreach (var (t, v) in wgt) totals[t] = totals.GetValueOrDefault(t) + v;
                            weights.Add(wgt);
                        }
                    var order = totals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Where(t => LandTexture(scene.Palette, t) is not null).Take(6).ToList();
                    if (order.Count == 0) continue;
                    var quadrant = (Quadrant)q;
                    land.Layers.Add(new BaseLayer { Header = new LayerHeader { Texture = LandTexture(scene.Palette, order[0])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant } });
                    for (int li = 1; li < order.Count; li++)
                    {
                        var layer = new AlphaLayer { Header = new LayerHeader { Texture = LandTexture(scene.Palette, order[li])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant, LayerNumber = (ushort)(li - 1) }, AlphaLayerData = [] };
                        for (int k = 0; k < weights.Count; k++)
                        {
                            float below = 0;
                            for (int b = 0; b <= li; b++) below += weights[k].GetValueOrDefault(order[b]);
                            float alpha = below > 0 ? weights[k].GetValueOrDefault(order[li]) / below : 0;
                            if (alpha > 0.004f) layer.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)k, Opacity = Math.Min(alpha, 1f) });
                        }
                        if (layer.AlphaLayerData.Count > 0) land.Layers.Add(layer);
                    }
                }
                land.IsCompressed = true;
                cell.Landscape = land;
            }
    }

    // ---- placement ----

    PlacedObject? PlaceStatic(Scene scene, (Static Base, RgMeshConverter.Result Mesh) s, Vector3 position, Matrix4x4 rotation)
    {
        var stat = s.Base;
        var at = At(scene, position);
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
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(ship.Static.FormKey),
            Placement = new Placement { Position = new P3Float(at.X + centre.X, at.Y + centre.Y, at.Z + keel), Rotation = new P3Float(0, 0, heading) },
            Scale = len / Skyrim.Meshes.ShipModel.Length,
        };
        CellFor(scene, at).Temporary.Add(placed);
        shipsByScript[(scene.Name, o.Script)] = (placed, placed.Scale ?? 1);
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
            PlaceStatic(scene, s, o.Position, o.Rotation);
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
                    PlaceStatic(scene, s, o.Position, o.Rotation);
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
            }
        }
        // Redguard's lights reach far (often 1000+ units) at low strength; Skyrim's are bright to their edge, so the
        // radius is kept moderate and colours rounded (each light would otherwise be its own record).
        foreach (var l in data.Lights)
        {
            if (l.Rgb == 0) continue;
            uint Round(uint c) => Math.Min(255, (c + 16) / 32 * 32);
            uint rgb = Round(l.Rgb >> 16 & 255) << 16 | Round(l.Rgb >> 8 & 255) << 8 | Round(l.Rgb & 255);
            PlaceLight(scene, rgb, Math.Clamp(l.Range * 2 / 3, 256, 1024), At(scene, l.Position));
        }
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

    readonly Dictionary<(uint, int), Light> lights = [];

    PlacedObject PlaceLight(Scene scene, uint rgb, int radius, Vector3 at)
    {
        radius = (radius + 31) / 64 * 64;
        if (!lights.TryGetValue((rgb, radius), out var light))
        {
            light = new Light(mod)
            {
                EditorID = $"RGLight{rgb:X6}R{radius}",
                ObjectBounds = new ObjectBounds(),
                Time = -1,
                Radius = (uint)radius,
                Color = System.Drawing.Color.FromArgb(0, (int)(rgb >> 16 & 255), (int)(rgb >> 8 & 255), (int)(rgb & 255)),
                FalloffExponent = 1,
                FOV = 90,
                NearClip = 0.001f,
                FadeValue = 1,
                Flags = 0,
                FlickerPeriod = 0.5f,
                FlickerIntensityAmplitude = 0.2f,
                FlickerMovementAmplitude = 4,
            };
            mod.Lights.Add(light);
            lights[(rgb, radius)] = light;
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

    /// <summary>
    /// Every LoadWorld trigger becomes an automatic load door (vanilla AutoLoadDoorMinUse01, which loads when walked
    /// into, as Redguard's triggers did) at the actor's position, sending the player to the named marker of the other
    /// map. A trigger whose map is a script variable (interiors leading back out) goes to the map that has a trigger
    /// into this one. Doors are paired with a trigger in the destination leading back (the one whose own arrival point
    /// lies nearest this door); a door without one gets a hidden partner far below its arrival point.
    /// </summary>
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
        foreach (var t in triggers)
        {
            if (Destination(t) is not { } to || t.Marker >= to.Data.Markers.Count) { Problems.Add($"{t.From.Name} {t.Actor}: no destination for LoadWorld({t.World?.ToString() ?? "?"}, {t.Marker})"); continue; }
            if (to == t.From && t.World is null) continue;
            var at = At(t.From, t.At);
            var door = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(autoDoor.FormKey),
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
            var back = doors.Where(o => o.Value.To == t.From && o.Key.From == d.To)
                .OrderBy(o => Vector3.Distance(o.Value.Arrival, At(t.From, t.At))).Select(o => o.Value.Door).FirstOrDefault();
            if (back is null)
            {
                back = new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(autoDoor.FormKey),
                    Placement = new Placement { Position = new P3Float(d.Arrival.X, d.Arrival.Y, d.Arrival.Z - 4000), Rotation = new P3Float(0, 0, 0) },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                };
                PersistentCellFor(d.To).Persistent.Add(back);
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
            foreach (var poly in mesh.Solid)
                for (int k = 1; k + 1 < poly.Length; k++)
                    yield return [Vector3.Transform(poly[0] * sc, m) + pos, Vector3.Transform(poly[k] * sc, m) + pos, Vector3.Transform(poly[k + 1] * sc, m) + pos];
        }
    }

    void BuildNavmeshes(Scene scene)
    {
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        if (scene.Interior is { } cell)
        {
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
                var r = navmeshes.BuildInterior(cell, solid, scene.LoadDoors.Cast<IPlacedObjectGetter>(),
                    keepAround: scene.LoadDoors.FirstOrDefault()?.TeleportDestination is null ? null : scene.LoadDoors[0].Placement!.Position);
                Navmeshes++;
                NavmeshTriangles += r.Triangles;
            }
            catch (Exception e) { Problems.Add($"navmesh {cell.EditorID}: {e.Message}"); }
            return;
        }
        var (count, tris) = navmeshes.BuildExteriorCells(scene.World!, scene.Cells,
            (x, y) => scene.Cells.TryGetValue((x, y), out var c) ? Solids(c.Temporary.OfType<PlacedObject>()) : [],
            _ => 0f, scene.LoadDoors.Cast<IPlacedObjectGetter>().ToList(), Problems);
        Navmeshes += count;
        NavmeshTriangles += tris;
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

    public void Build(Action<string>? log = null)
    {
        LoadScenes();
        BuildWarps(log);
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
        // People in their homes first: interiors and the island before the scenes they only visit.
        foreach (var scene in scenes.Values.OrderBy(s => s.Name == "ISLAND" ? 1 : s.Terrain is null ? 0 : 2)) PlaceActors(scene);
        foreach (var scene in scenes.Values) PlaceScriptMarkers(scene);
        log?.Invoke($"placed {Statics} statics, {Props} props, {LightCount} lights, {Effects} effects, {ActorCount} actors; {statics.Count} models");
        // Ropes, and the ledges Redguard's Cyrus could catch (RgEngine), once the places' models are there.
        foreach (var scene in scenes.Values) { PlaceRopes(scene); PlaceLedges(scene); }
        surfaces.Clear();
        log?.Invoke($"ropes: {Ropes} ({RopeSwings} to swing on); ledges: {Ledges} to climb");

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
            PlaceDocksArrival(startScene, at, startFacing * MathF.Tau / 2048);
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
        foreach (var scene in scenes.Values.Where(s => s.World is not null)) AddWorldCells(scene);
        foreach (var scene in scenes.Values) BuildNavmeshes(scene);
        int links = navmeshes?.LinkExteriorEdges() ?? 0;
        log?.Invoke($"navmeshes: {Navmeshes} ({NavmeshTriangles} triangles), {links} exterior edge links");
        foreach (var scene in scenes.Values.Where(s => s.World is not null))
        {
            terrainLod ??= new Skyrim.TerrainLod(mod, modOutDir, skyrimData);
            terrainLod.Build(scene.World!.EditorID!, scene.Cells, _ => 0f, -2048);
        }
        if (terrainLod is not null) Problems.AddRange(terrainLod.Problems);
        Problems.AddRange(art.Problems.Distinct());
    }

    public void Write()
    {
        Skyrim.SwingDoorTwins.AddAll(mod);
        Directory.CreateDirectory(modOutDir);
        TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
        mod.WriteToBinary(Path.Combine(modOutDir, PluginName));
        // What procedural creatures may use from Redguard (read by the creatures step when that option is on).
        WriteCreatureCatalog(Path.Combine(modOutDir, PluginName));
        if (VoiceMap.Count > 0)
            File.WriteAllText(Path.Combine(modOutDir, "Redguard.voicemap.json"), System.Text.Json.JsonSerializer.Serialize(VoiceMap));
    }
}
