using System.Numerics;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using nifly;
using Noggog;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Render;

/// <summary>
/// A converted place as a scene for the offline renderer (tools/scene3d/render_scene.py), so pictures and flythroughs
/// of what the converter builds (the main menu's logo and background) are made without running the game. From a
/// worldspace in the converted plugins: every placed object with a model (its mesh's shapes, transformed, with their
/// textures and alpha), the land (heights and the textures painted on it) and the water. Meshes and textures come from
/// the build outputs, then Skyrim's Data folder, then Skyrim's archives; they are written as plain arrays the renderer
/// reads with numpy, textures as the DDS files themselves.
/// </summary>
public sealed class SceneExporter
{
    readonly string[] looseDirs;
    readonly IArchiveReader[] archives;
    readonly Dictionary<string, IArchiveFile> archiveFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly string outDir;
    readonly Dictionary<string, int> meshIds = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> meshNames = [];
    readonly HashSet<string> textures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each land texture record's grass (LTEX -> GRAS).</summary>
    readonly Dictionary<FormKey, List<IGrassGetter>> landGrasses = new();
    public List<string> Missing { get; } = [];
    /// <summary>The model being written, for a texture it names that is missing.</summary>
    string? meshNow;
    /// <summary>Shapes whose vertex colours are dark but switched off in their shader (so drawn without them).</summary>
    public int UnusedDarkColours { get; private set; }

    public SceneExporter(string skyrimData, IEnumerable<string> looseDirs, string outDir)
    {
        this.looseDirs = [.. looseDirs, skyrimData];
        this.outDir = outDir;
        var names = new[] { "Skyrim - ", "Update", "Dawnguard", "HearthFires", "Dragonborn" };
        archives = Directory.GetFiles(skyrimData, "*.bsa").Where(p => names.Any(n => Path.GetFileName(p).StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            .Select(p => Archive.CreateReader(GameRelease.SkyrimSE, p)).ToArray();
        foreach (var a in archives)
            foreach (var f in a.Files) archiveFiles.TryAdd(f.Path.Replace('/', '\\'), f);
        Directory.CreateDirectory(Path.Combine(outDir, "meshes"));
        Directory.CreateDirectory(Path.Combine(outDir, "textures"));
    }

    /// <summary>A Data-relative file's bytes: the first loose copy, else from the archives.</summary>
    byte[]? Find(string dataRelative)
    {
        string rel = dataRelative.Replace('/', '\\').TrimStart('\\');
        foreach (var dir in looseDirs)
        {
            string p = Path.Combine(dir, rel);
            if (File.Exists(p)) return File.ReadAllBytes(p);
        }
        return archiveFiles.TryGetValue(rel, out var f) ? f.GetBytes() : null;
    }

    public sealed record Instance(int Mesh, float[] Matrix);

    /// <summary>
    /// Exports the worldspace <paramref name="worldspace"/> as it stands with <paramref name="plugins"/> loaded over
    /// Skyrim's (cells and references from each, later ones winning), within <paramref name="radius"/> units of
    /// <paramref name="centre"/> (everything when the radius is 0), into scene.json. The land reaches
    /// <paramref name="landRadius"/> when that is larger: terrain to the horizon costs little beside its objects
    /// (the menu's Whiterun stood on an island of land otherwise, the ground beyond it sunk away).
    /// </summary>
    /// <summary>
    /// Where a location (its record's EditorID, "DFLocDaggerfall17") stands on its overland: the worldspace whose cells
    /// carry it, and the middle of those cells on its ground (the lowest of its blocks' ground statics, "DFGround..."), so
    /// a shot can be given in the location's own frame, centred on it at ground level, as Daggerfall's towns were when
    /// they were worldspaces of their own.
    /// </summary>
    static (string World, Vector3 Frame)? LocationFrame(IEnumerable<ISkyrimModDisposableGetter> mods, string location)
    {
        var key = mods.SelectMany(m => m.Locations).FirstOrDefault(l => string.Equals(l.EditorID, location, StringComparison.OrdinalIgnoreCase))?.FormKey;
        if (key is null) return null;
        var grounds = mods.SelectMany(m => m.Statics).Where(s => s.EditorID?.StartsWith("DFGround", StringComparison.Ordinal) == true).Select(s => s.FormKey).ToHashSet();
        string? world = null;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        float ground = float.MaxValue;
        foreach (var m in mods)
            foreach (var w in m.Worldspaces)
                foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                {
                    if (c.Location.FormKeyNullable != key || c.Grid is not { } g) continue;
                    world ??= w.EditorID;
                    if (w.EditorID != world) continue;
                    x0 = Math.Min(x0, g.Point.X); x1 = Math.Max(x1, g.Point.X); y0 = Math.Min(y0, g.Point.Y); y1 = Math.Max(y1, g.Point.Y);
                    foreach (var r in c.Temporary.OfType<IPlacedObjectGetter>())
                        if (grounds.Contains(r.Base.FormKey) && r.Placement is { } p) ground = MathF.Min(ground, p.Position.Z);
                }
        if (world is null) return null;
        return (world, new Vector3((x0 + x1 + 1) * 2048f, (y0 + y1 + 1) * 2048f, ground == float.MaxValue ? 0 : ground));
    }

    /// <summary>A model the distant view keeps: a tree (not stumps, driftwood, logs or branch piles), a mountain, or a
    /// big rock - Skyrim's mountains, Oblivion's great-forest rocks of size 200 and up (their names give the size, as
    /// its distant view keeps the big ones), Morrowind's terrain rocks; and its trees (flora_tree_*, the parasols).</summary>
    static bool FarObject(string model)
    {
        var low = model.Replace('/', '\\').ToLowerInvariant();
        var file = Path.GetFileNameWithoutExtension(low);
        if (low.Contains("\\landscape\\mountains\\") || file.StartsWith("terrain_rock")) return true;
        if (System.Text.RegularExpressions.Regex.Match(file, @"^rock(greatforest|mountain|cliff)\D*(\d+)") is { Success: true } rock)
            return int.Parse(rock.Groups[2].Value) >= 200 || rock.Groups[1].Value != "greatforest";
        // Daggerfall's trees and rocks, made from its sprites (its overland's foliage, DfFoliage).
        if (low.Contains("\\tamrielmerge\\df\\models\\") && FarSprites.Contains(file)) return true;
        if (file.Contains("stump") || file.Contains("driftwood") || file.Contains("branchpile") || file.Contains("log")) return false;
        return (file.Contains("tree") && (low.Contains("\\trees\\") || low.Contains("\\tamrielmerge\\")))
            || file.Contains("emp_parasol");
    }

    static readonly HashSet<string> FarSprites = SpriteModels.Targets.Where(t => t.Kind is SpriteModels.Kind.Tree or SpriteModels.Kind.Prop)
        .Select(t => t.Name.ToLowerInvariant()).ToHashSet();

    public void Export(string skyrimData, IReadOnlyList<string> plugins, string worldspace, Vector2 centre, float radius, float landRadius = 0)
    {
        float landReach = radius <= 0 ? 0 : Math.Max(radius, landRadius);
        var mods = new List<ISkyrimModDisposableGetter>();
        foreach (var m in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" })
            mods.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE));
        // A plugin already loaded as a base (Skyrim.esm itself, for the menu's Whiterun shot) isn't loaded twice.
        foreach (var p in plugins)
            if (!mods.Any(m => m.ModKey.FileName.String.Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)))
                mods.Add(SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE));
        try
        {
            // Models of everything placeable, the last plugin's record winning.
            var models = new Dictionary<FormKey, string>();
            foreach (var mod in mods)
                foreach (var r in mod.EnumerateMajorRecords())
                    if (r is IModeledGetter { Model: { } model } && r is IPlaceableObjectGetter && model.File.DataRelativePath.Path is { Length: > 0 } path)
                        models[r.FormKey] = path;

            var refs = new Dictionary<FormKey, (string Model, P3Float Pos, P3Float Rot, float Scale)>();
            var npcRefs = new Dictionary<FormKey, (FormKey Npc, P3Float Pos, P3Float Rot, float Scale)>();
            var lands = new Dictionary<(int, int), ILandscapeGetter>();
            var landTextures = new Dictionary<FormKey, string>();
            var waters = new List<float>();
            float defaultWater = 0;
            // "Tamriel+WhiterunWorld": a city worldspace over its parent (they share coordinates). The city's own cells
            // replace the parent's land there, and the parent's LOD stand-ins for the city (*LOD.nif) are left out:
            // the menu's Whiterun showed Dragonsreach and the houses as their distant-view models otherwise.
            // "Overland+Town@dx,dy,dz": a town worldspace of its own placed into its overland (user, 2026-10-09: the flybys
            // "need to be the games worldspace not a test area", "ground sinks/floats"): its objects moved by the offset
            // (gate onto its overland door), its own land (none for Daggerfall's towns) left out, and the overland's land
            // cells under it taken away (holes, listed in scene.json: the town's own ground covers them).
            // "~+DFLocTown@location": the overland a location stands on (Daggerfall's towns are on its tiles), found
            // from the cells carrying its location record, so a conversion with other terrain settings (another tile, other
            // positions) needs no new numbers: '~' is that worldspace, and centre is given in the location's own frame
            // (LocationFrame: its middle, on its ground). The frame goes into scene.json ("offset") for the camera.
            var tokens = worldspace.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? frameWorld = null;
            Vector3? frame = null;
            foreach (var place in tokens.Where(t => t.EndsWith("@location", StringComparison.OrdinalIgnoreCase)).Select(t => t[..t.IndexOf('@')]))
            {
                var found = LocationFrame(mods, place) ?? throw new InvalidOperationException($"no overland cells carry the location {place}");
                frame ??= found.Frame;
                frameWorld ??= found.World;
                Console.WriteLine($"  {place}: on {found.World}, its middle at {found.Frame.X:F0}, {found.Frame.Y:F0}, {found.Frame.Z:F0}");
            }
            if (frame is { } f) centre += new Vector2(f.X, f.Y);
            var worldSpecs = tokens
                .Where(w => !w.EndsWith("@location", StringComparison.OrdinalIgnoreCase))
                .Select(w =>
                {
                    var at = w.Split('@');
                    string name = at[0] == "~" ? frameWorld ?? throw new InvalidOperationException("'~' needs a location placed @location") : at[0];
                    var o = at.Length > 1 ? at[1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : [0f, 0f, 0f];
                    return (Name: name, Offset: new Vector3(o[0], o[1], o[2]));
                }).ToList();
            var worlds = worldSpecs.Select(w => w.Name).ToArray();
            var offsetRefs = new List<Vector2>();
            var movedRefs = new HashSet<FormKey>();
            var cityLand = new Dictionary<(int, int), ILandscapeGetter>();
            // A town split over worldspaces on its overland's coordinates (Oblivion's Imperial City: a worldspace a
            // district, each with its own land and its own copies of the walls, the tower and the shore round it; user,
            // 2026-10-09: "the games have interiors and exteriors so the worldspace needs to be correct"): each land cell
            // from the world with the most objects in it (the overland on a tie), and an object the worlds share once.
            var worldLand = new Dictionary<(string, int, int), ILandscapeGetter>();
            var worldObjects = new Dictionary<(string, int, int), int>();
            var placedOnce = new HashSet<(string, int, int, int)>();
            foreach (var mod in mods)
            {
                foreach (var ws in mod.Worldspaces.Where(w => w.EditorID is { } id && worlds.Contains(id)))
                {
                    bool primary = ws.EditorID == worlds[0];
                    var offset = worldSpecs.First(w => w.Name == ws.EditorID).Offset;
                    bool moved = offset != Vector3.Zero;
                    P3Float Moved(P3Float q) => moved ? new P3Float(q.X + offset.X, q.Y + offset.Y, q.Z + offset.Z) : q;
                    if (primary && ws.LandDefaults is { } d) defaultWater = d.DefaultWaterHeight;
                    var cells = (ws.TopCell is { } top ? new[] { top } : []).Concat(ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items));
                    // A worldspace that uses its parent's land (WhiterunWorld, the Imperial City's districts: UseLandData)
                    // is drawn on the parent's in the game; its own land records are unused leftovers at other heights
                    // (user, 2026-10-11: "did you fix the roads in the flyby": Whiterun's roads floated over them, their
                    // edges showing as black trenches, and the Imperial City's hillside had a black seam between them and
                    // Tamriel's).
                    bool usesParentLand = ws.Parent is { } wsParent && !wsParent.Worldspace.IsNull
                        && wsParent.Flags.HasFlag((Enum)Enum.Parse(wsParent.Flags.GetType(), "UseLandData"));
                    foreach (var cell in cells)
                    {
                        if (cell.Grid is { } g && cell.Landscape is { } land && !moved && !usesParentLand)
                        {
                            float cx = g.Point.X * 4096 + 2048, cy = g.Point.Y * 4096 + 2048;
                            if (landReach <= 0 || Vector2.Distance(new Vector2(cx, cy), centre) < landReach + 3000)
                            {
                                if (primary) lands[(g.Point.X, g.Point.Y)] = land;
                                else worldLand[(ws.EditorID!, g.Point.X, g.Point.Y)] = land;
                            }
                        }
                        if (primary && cell.Grid is { } wg && lands.ContainsKey((wg.Point.X, wg.Point.Y)) && cell.Flags.HasFlag(Cell.Flag.HasWater))
                            waters.Add(cell.WaterHeight is { } wh && wh is > -30000 and < 30000 ? wh : defaultWater);
                        foreach (var r in cell.Persistent.Concat(cell.Temporary))
                        {
                            // People out by day: not initially disabled, not those enabled opposite to a parent (the
                            // night's stalkers and watch).
                            if (r is IPlacedNpcGetter pn && pn.Placement is { } np && (pn.MajorRecordFlagsRaw & (0x800 | 0x20)) == 0
                                && !(pn.EnableParent is { } ep && ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent))
                                && (radius <= 0 || Vector2.Distance(new Vector2(Moved(np.Position).X, Moved(np.Position).Y), centre) <= radius))
                                npcRefs[pn.FormKey] = (pn.Base.FormKey, Moved(np.Position), np.Rotation, pn.Scale ?? 1);
                            if (r is not IPlacedObjectGetter o || o.Placement is not { } pl) continue;
                            var pos = Moved(pl.Position);
                            float dist = Vector2.Distance(new Vector2(pos.X, pos.Y), centre);
                            if (radius > 0 && dist > landReach) continue;
                            if ((o.MajorRecordFlagsRaw & (0x800 | 0x20)) != 0) continue; // initially disabled, deleted
                            if (!models.TryGetValue(o.Base.FormKey, out var model)) continue;
                            // Past the objects' radius, out to the land's: its trees and mountains, as the game's distant
                            // view (LOD) shows them (user, 2026-10-11: "the trees arent there wither" - the hills round
                            // Whiterun stood bare past 22000 units).
                            if (radius > 0 && dist > radius && !FarObject(model)) continue;
                            if (model.Contains("marker", StringComparison.OrdinalIgnoreCase) && !model.Contains("mapmarker", StringComparison.OrdinalIgnoreCase)) continue;
                            if (primary && worlds.Length > 1 && model.EndsWith("LOD.nif", StringComparison.OrdinalIgnoreCase)) continue;
                            if (worlds.Length > 1 && !placedOnce.Add((model.ToLowerInvariant(), (int)MathF.Round(pos.X), (int)MathF.Round(pos.Y), (int)MathF.Round(pos.Z)))) continue;
                            var objCell = (ws.EditorID!, (int)MathF.Floor(pos.X / 4096), (int)MathF.Floor(pos.Y / 4096));
                            worldObjects[objCell] = worldObjects.GetValueOrDefault(objCell) + 1;
                            refs[o.FormKey] = (model, pos, pl.Rotation, o.Scale ?? 1);
                            if (moved) { offsetRefs.Add(new Vector2(pos.X, pos.Y)); movedRefs.Add(o.FormKey); }
                        }
                    }
                }
            }
            // Only land that is painted: a district's land outside its walls is never seen from inside it and has no
            // textures (the game's default dirt; user, 2026-10-09: the dark ground below the Imperial City's walls
            // "does not look right"), so there the overland's own land stays.
            // All four of its quarters (a district's edge cells are painted only on the side facing in).
            static bool Painted(ILandscapeGetter l) => (l.Layers ?? []).Where(x => x is not IAlphaLayerGetter && x.Header is { } h && !h.Texture.IsNull)
                .Select(x => x.Header!.Quadrant).Distinct().Count() == 4;
            foreach (var cellLands in worldLand.GroupBy(w => (w.Key.Item2, w.Key.Item3)))
            {
                int own = worldObjects.GetValueOrDefault((worlds[0], cellLands.Key.Item1, cellLands.Key.Item2));
                var painted = cellLands.Where(w => Painted(w.Value)).ToList();
                if (painted.Count == 0) { if (!lands.ContainsKey(cellLands.Key)) cityLand[cellLands.Key] = cellLands.First().Value; continue; }
                var best = painted.MaxBy(w => worldObjects.GetValueOrDefault(w.Key));
                if (!lands.ContainsKey(cellLands.Key) || worldObjects.GetValueOrDefault(best.Key) > own) cityLand[cellLands.Key] = best.Value;
            }
            foreach (var (k, land) in cityLand) lands[k] = land;
            // The placed town's own area: the box round its cells holding two or more objects (its blocks and walls). A
            // Daggerfall town worldspace also has its own stand-in country round it, one ground tile a cell: left out,
            // the overland's real land there instead. The overland's cells in that box lose their land (holes; the town's
            // own ground is there).
            static (int, int) CellOf(float x, float y) => ((int)MathF.Floor(x / 4096), (int)MathF.Floor(y / 4096));
            var townCells = offsetRefs.GroupBy(q => CellOf(q.X, q.Y)).Where(g => g.Count() >= 2).Select(g => g.Key).ToList();
            var holes = new HashSet<(int, int)>();
            if (townCells.Count > 0)
            {
                int x0 = townCells.Min(c => c.Item1), x1 = townCells.Max(c => c.Item1), y0 = townCells.Min(c => c.Item2), y1 = townCells.Max(c => c.Item2);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gy = y0; gy <= y1; gy++) holes.Add((gx, gy));
                foreach (var k in refs.Where(r => movedRefs.Contains(r.Key) && !holes.Contains(CellOf(r.Value.Pos.X, r.Value.Pos.Y))).Select(r => r.Key).ToList())
                    refs.Remove(k);
            }
            foreach (var h in holes) lands.Remove(h);
            // and the overland's own objects there (its stand-ins for the town, trees through its streets).
            foreach (var k in refs.Where(r => !movedRefs.Contains(r.Key)
                         && holes.Contains(CellOf(r.Value.Pos.X, r.Value.Pos.Y))).Select(r => r.Key).ToList())
                refs.Remove(k);
            // The land's textures: LTEX -> TXST diffuse.
            var txst = new Dictionary<FormKey, string>();
            foreach (var mod in mods)
                foreach (var t in mod.TextureSets)
                    if (t.Diffuse is { } diff) txst[t.FormKey] = diff.DataRelativePath.Path;
            foreach (var mod in mods)
                foreach (var lt in mod.LandscapeTextures)
                    if (txst.TryGetValue(lt.TextureSet.FormKey, out var diff)) landTextures[lt.FormKey] = diff;
            // The grass each land texture grows (LTEX -> GRAS, later plugins winning), as the game grows it round the
            // player (user, 2026-10-11: "do the grass" - the land between Whiterun's pieces showed bare and dark).
            var grassRecords = new Dictionary<FormKey, IGrassGetter>();
            foreach (var mod in mods)
                foreach (var g in mod.Grasses) grassRecords[g.FormKey] = g;
            landGrasses.Clear();
            foreach (var mod in mods)
                foreach (var lt in mod.LandscapeTextures)
                    landGrasses[lt.FormKey] = lt.Grasses.Select(l => grassRecords.GetValueOrDefault(l.FormKey)).OfType<IGrassGetter>().ToList();

            // The water most of the place's cells have.
            float water = waters.Count > 0 ? waters.GroupBy(w => MathF.Round(w)).OrderByDescending(g => g.Count()).First().Key : defaultWater;
            var instances = new List<Instance>();
            foreach (var (_, (model, pos, rot, scale)) in refs)
            {
                int id = MeshId(model);
                if (id < 0) continue;
                instances.Add(new Instance(id, Matrix(pos, rot, scale)));
            }
            var terrain = ExportLand(lands, landTextures);
            // Past the land: the world's own distant view (user, 2026-10-25: "why does the flyby have the test cells of
            // oblivion and whiterun instead of the actual worldspace", "i need it to show wholy"): its LOD land and
            // objects out to LodReach, as the game draws them past its loaded cells.
            var lod = radius > 0 ? DistantView(worldSpecs[0].Name, centre, [.. lands.Keys, .. holes]) : [];
            var people = new List<object>();
            if (npcRefs.Count > 0)
            {
                var cache = mods.ToImmutableLinkCache();
                foreach (var (_, (npc, pos, rot, scale)) in npcRefs)
                    if (Person(cache, npc) is { Parts.Count: > 0 } person)
                    {
                        var ids = person.Parts.Select(m => MeshId(m, person.Skeleton)).Where(i => i >= 0).Distinct().ToList();
                        var walk = Enumerable.Range(0, Skeleton.WalkSteps)
                            .Select(step => person.Parts.Select(m => MeshId(m, person.Skeleton, step)).Where(i => i >= 0).Distinct().ToList()).ToList();
                        if (ids.Count > 0) people.Add(new { parts = ids, walk, m = Matrix(pos, new P3Float(0, 0, rot.Z), scale * person.Height) });
                    }
            }
            var scene = new
            {
                worldspace,
                holes = holes.Select(h => new[] { h.Item1, h.Item2 }).ToList(),
                offset = frame is { } fr ? new[] { fr.X, fr.Y, fr.Z }
                    : worldSpecs.Select(w => w.Offset).Where(o => o != Vector3.Zero).Select(o => new[] { o.X, o.Y, o.Z }).FirstOrDefault(),
                centre = new[] { centre.X, centre.Y },
                radius,
                water,
                meshes = meshNames,
                instances = instances.Select(i => new { mesh = i.Mesh, m = i.Matrix }).ToList(),
                terrain,
                lod,
                people,
            };
            File.WriteAllText(Path.Combine(outDir, "scene.json"), JsonSerializer.Serialize(scene));
            // The game's own lighting for the place (its main world's climate and weather, every time of day), for the
            // flyby's game shading (blend_scene: shaded as the game does, not physically).
            var primaryWorld = mods.SelectMany(m => m.Worldspaces).LastOrDefault(w => w.EditorID == worlds[0]);
            if (primaryWorld is not null && GameLighting.Read(mods.ToImmutableLinkCache(), primaryWorld) is { } lighting)
            {
                // The game's cloud mesh, each layer drawn on its own shape (user, 2026-10-12: "the games have a skydome mesh").
                int cloudMesh = MeshId(@"meshes\sky\clouds.nif");
                if (cloudMesh >= 0) lighting["cloud_mesh"] = cloudMesh;
                File.WriteAllText(Path.Combine(outDir, "lighting.json"), JsonSerializer.Serialize(lighting, new JsonSerializerOptions { WriteIndented = true }));
                // Its cloud layers' textures, for the flyby's sky.
                if (lighting.GetValueOrDefault("clouds") is List<object> clouds)
                    foreach (var c in clouds.OfType<Dictionary<string, object?>>())
                        if (c.GetValueOrDefault("texture") is string ct) Texture(NormaliseTexture(ct));
            }
            Console.WriteLine($"{worldspace}: {people.Count} people of {npcRefs.Count} placed; {instances.Count} objects, {meshIds.Count} meshes, {textures.Count} textures, {lands.Count} land cells, water {water}; missing {Missing.Count}; {UnusedDarkColours} shapes' unused dark vertex colours left out");
        }
        finally { foreach (var m in mods) m.Dispose(); }
    }

    /// <summary>How far the distant view reaches from the scene's centre (units): past it the haze has the land.</summary>
    const float LodReach = 600000;
    /// <summary>A chunk this many of its own widths from the centre or nearer is drawn as its four finer ones.</summary>
    const float LodSplit = 2.5f;

    /// <summary>
    /// The world's distant view round <paramref name="centre"/> as the game draws it past its loaded cells: its LOD land
    /// chunks (meshes\terrain\&lt;world&gt;\&lt;world&gt;.&lt;level&gt;.&lt;x&gt;.&lt;y&gt;.btr, from the converted output or Skyrim's
    /// archives) and their objects (objects\*.bto), finer near the centre and coarser away from it (a chunk splits
    /// within LodSplit of its widths), none over the scene's own land or a placed town (<paramref name="covered"/>:
    /// a chunk with such a cell splits down to the finest level, whose shapes blend_scene cuts there). Each chunk's
    /// shapes hold its local units scaled by the level, the engine placing it at its south-west cell, so each is an
    /// instance there: {mesh, m, kind ("land" or "objects"), level}.
    /// </summary>
    List<object> DistantView(string world, Vector2 centre, HashSet<(int, int)> covered)
    {
        var land = new Dictionary<(int Level, int X, int Y), string>();
        var objects = new Dictionary<(int Level, int X, int Y), string>();
        void Add(string rel)
        {
            var parts = Path.GetFileName(rel).Split('.');
            if (parts.Length < 5 || !parts[0].Equals(world, StringComparison.OrdinalIgnoreCase)) return;
            if (!int.TryParse(parts[^4], out int level) || !int.TryParse(parts[^3], out int x) || !int.TryParse(parts[^2], out int y)) return;
            string ext = parts[^1].ToLowerInvariant();
            if (ext == "btr") land.TryAdd((level, x, y), rel);
            else if (ext == "bto") objects.TryAdd((level, x, y), rel);
        }
        // Loose copies first (the conversion's own), then the archives (Skyrim's own Tamriel).
        foreach (var dir in looseDirs)
        {
            string d = Path.Combine(dir, "meshes", "terrain", world);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.bt?", SearchOption.AllDirectories))
                Add(Path.GetRelativePath(dir, f));
        }
        string prefix = $"meshes\\terrain\\{world}\\";
        foreach (var rel in archiveFiles.Keys)
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Add(rel);
        var result = new List<object>();
        if (land.Count == 0) { Console.WriteLine($"  distant view: {world} has no LOD land"); return result; }

        float Near(int level, int x, int y)
        {
            float x0 = x * 4096f, y0 = y * 4096f, x1 = (x + level) * 4096f, y1 = (y + level) * 4096f;
            float dx = Math.Max(Math.Max(x0 - centre.X, 0), centre.X - x1), dy = Math.Max(Math.Max(y0 - centre.Y, 0), centre.Y - y1);
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        int Covered(int level, int x, int y)
        {
            int n = 0;
            for (int i = 0; i < level; i++)
                for (int j = 0; j < level; j++)
                    if (covered.Contains((x + i, y + j))) n++;
            return n;
        }
        int min = land.Keys.Min(k => k.Level), max = land.Keys.Max(k => k.Level);
        var chosen = new List<(int Level, int X, int Y)>();
        void Visit(int level, int x, int y)
        {
            float d = Near(level, x, y);
            if (d > LodReach) return;
            int cov = Covered(level, x, y);
            if (level > min && (d < LodSplit * level * 4096 || cov > 0))
            {
                int h = level / 2;
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++) Visit(h, x + i * h, y + j * h);
                return;
            }
            if (cov < level * level && land.ContainsKey((level, x, y))) chosen.Add((level, x, y));
        }
        foreach (var root in land.Keys.Where(k => k.Level == max).ToList()) Visit(root.Level, root.X, root.Y);

        float[] At(int x, int y) => Matrix(new P3Float(x * 4096f, y * 4096f, 0), new P3Float(0, 0, 0), 1);
        int landShown = 0, objectsShown = 0;
        foreach (var (level, x, y) in chosen)
        {
            int id = MeshId(land[(level, x, y)]);
            if (id >= 0) { result.Add(new { mesh = id, m = At(x, y), kind = "land", level }); landShown++; }
            // Its objects: the chunk's own, or its four finer ones where the coarsest level has none (Skyrim's and
            // the conversion's objects stop at 16).
            var keys = objects.ContainsKey((level, x, y)) ? [(level, x, y)]
                : level > min ? new[] { (0, 0), (1, 0), (0, 1), (1, 1) }.Select(o => (level / 2, x + o.Item1 * level / 2, y + o.Item2 * level / 2)).Where(objects.ContainsKey).ToArray()
                : [];
            foreach (var k in keys)
            {
                int oid = MeshId(objects[k]);
                if (oid >= 0) { result.Add(new { mesh = oid, m = At(k.Item2, k.Item3), kind = "objects", level = k.Item1 }); objectsShown++; }
            }
        }
        var byLevel = chosen.GroupBy(c => c.Level).OrderBy(g => g.Key).Select(g => $"{g.Count()} at {g.Key}");
        Console.WriteLine($"  distant view of {world}: {landShown} land chunks ({string.Join(", ", byLevel)}), {objectsShown} object chunks, out to {LodReach:F0} units; {land.Count} land and {objects.Count} object chunks there");
        return result;
    }

    /// <summary>One mesh (Data-relative) as a scene of its own, at the origin, to look at a model alone.</summary>
    public void ExportMesh(string model)
    {
        int id = MeshId(model);
        var scene = new
        {
            worldspace = "",
            centre = new[] { 0f, 0f },
            radius = 0f,
            water = -100000f,
            meshes = meshNames,
            instances = id < 0 ? [] : new[] { new { mesh = id, m = Matrix(new P3Float(0, 0, 0), new P3Float(0, 0, 0), 1) } },
            terrain = new { cells = 0, textures = new List<string>() },
        };
        File.WriteAllText(Path.Combine(outDir, "scene.json"), JsonSerializer.Serialize(scene));
        Console.WriteLine($"{model}: {(id < 0 ? "not found" : "exported")}, {textures.Count} textures; missing {string.Join(", ", Missing)}");
    }

    /// <summary>
    /// A reference's placement as a row-major 4x4 matrix (column vectors), its angles as Skyrim applies them
    /// (<see cref="SkyrimRotation"/>).
    /// </summary>
    static float[] Matrix(P3Float pos, P3Float rot, float scale)
    {
        var m = SkyrimRotation.Matrix(rot);
        m *= Matrix4x4.CreateScale(scale);
        m.Translation = new Vector3(pos.X, pos.Y, pos.Z);
        // Transpose to column-vector, row-major for the renderer.
        return [m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43, m.M14, m.M24, m.M34, m.M44];
    }

    int MeshId(string model, string? skeleton = null, int step = -1)
    {
        string rel = model.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) || model.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? model : "meshes\\" + model;
        string key = skeleton is null ? rel : rel + "|" + skeleton + "|" + step;
        if (meshIds.TryGetValue(key, out int id)) return id;
        id = meshNames.Count;
        string file = Path.Combine(outDir, "meshes", id + ".bin");
        var bytes = Find(rel);
        meshNow = rel;
        bool written = bytes is not null && WriteMesh(bytes, file, skeleton is null ? null : SkeletonFor(skeleton, step));
        meshNow = null;
        if (!written) { Missing.Add(rel); meshIds[key] = -1; if (File.Exists(file)) File.Delete(file); return -1; }
        meshIds[key] = id;
        meshNames.Add(rel);
        return id;
    }

    /// <summary>
    /// A mesh's visible shapes in the mesh's own space: per shape its diffuse and normal map, alpha (test threshold,
    /// blend), two-sidedness, then positions, normals, UVs, colours and triangles.
    /// </summary>
    bool WriteMesh(byte[] bytes, string path, Skeleton? skeleton = null)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_scene_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var nif = new NifFile();
            if (nif.Load(tmp) != 0) return false;
            var hdr = nif.GetHeader();
            using var w = new BinaryWriter(File.Create(path));
            int count = 0;
            using var body = new MemoryStream();
            using var bw = new BinaryWriter(body);
            foreach (var shape in nif.GetShapes())
            {
                if ((shape.flags & 1) != 0) continue;
                string type = hdr.GetBlockTypeStringById(hdr.GetBlockID(shape));
                if (type.Contains("Particle")) continue;
                var chain = new List<MatTransform> { shape.transform };
                bool hidden = false;
                for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent))
                {
                    chain.Add(parent.transform);
                    string name = parent.name.get() ?? "";
                    hidden |= (parent.flags & 1) != 0 || name.Contains("EditorMarker", StringComparison.OrdinalIgnoreCase);
                }
                if (hidden) continue;
                using var tris = new vectorTriangle();
                shape.GetTriangles(tris);
                var verts = nif.GetVertsForShape(shape)?.ToList() ?? [];
                if (tris.Count == 0 || verts.Count == 0) continue;
                var shader = nif.GetShader(shape);
                // Effect shaders (glows, mist, water planes) aren't drawn.
                if (shader is not null && hdr.GetBlockTypeStringById(hdr.GetBlockID(shader)) == "BSEffectShaderProperty") continue;
                var normals = nif.GetNormalsForShape(shape)?.ToList() ?? [];
                var uvs = nif.GetUvsForShape(shape)?.ToList() ?? [];
                var colors = nif.GetColorsForShape(shape)?.ToList() ?? [];
                // The game uses a shape's vertex colours only when its shader says so (SLSF2_Vertex_Colors, 0x20 of the
                // second flags); else they're ignored, whatever they hold (user, 2026-10-11: black bands along
                // Whiterun's walls and street edges: parts whose unused colours are black, 0, came out black).
                if (colors.Count > 0 && shader is not null && hdr.GetBlockTypeStringById(hdr.GetBlockID(shader)) == "BSLightingShaderProperty"
                    && !shader.HasVertexColors())
                {
                    if (colors.Average(c => (c.r + c.g + c.b) / 3) < 0.5f) UnusedDarkColours++;
                    colors = [];
                }
                string diffuse = nif.GetTexturePathByIndex(shape, 0) ?? "";
                string normal = nif.GetTexturePathByIndex(shape, 1) ?? "";
                foreach (var t in new[] { diffuse, normal }) if (t.Length > 0) Texture(t);
                int alphaMode = 0; float threshold = 128;
                if (nif.GetAlphaProperty(shape) is { } alpha)
                {
                    if ((alpha.flags & 0x200) != 0) { alphaMode = 1; threshold = alpha.threshold; }
                    else if ((alpha.flags & 1) != 0) alphaMode = 2;
                }
                bool twoSided = shader is not null && shader.IsDoubleSided();
                // A skinned shape (a body, clothes, a head part) is placed through its bones on the race's skeleton,
                // posed with the arms at the sides.
                Func<nifly.Vector3, int, nifly.Vector3>? skinned = null;
                Func<nifly.Vector3, int, nifly.Vector3>? skinnedDir = null;
                if (skeleton is not null && shape.IsSkinned() && OnSkeleton(nif, shape, verts.Count, skeleton) is { } placed)
                    (skinned, skinnedDir) = placed;

                WriteString(bw, NormaliseTexture(diffuse));
                WriteString(bw, NormaliseTexture(normal));
                bw.Write(alphaMode); bw.Write(threshold); bw.Write(twoSided ? 1 : 0);
                bw.Write(verts.Count); bw.Write((int)tris.Count);
                for (int i = 0; i < verts.Count; i++)
                {
                    var g = verts[i];
                    if (skinned is not null) g = skinned(g, i);
                    else foreach (var t in chain) g = t.ApplyTransform(g);
                    bw.Write(g.x); bw.Write(g.y); bw.Write(g.z);
                }
                for (int i = 0; i < verts.Count; i++)
                {
                    Vector3 n = Vector3.UnitZ;
                    if (normals.Count == verts.Count)
                    {
                        var g = normals[i];
                        if (skinnedDir is not null) g = skinnedDir(g, i);
                        else foreach (var t in chain) g = t.ApplyTransformToDir(g);
                        n = new Vector3(g.x, g.y, g.z);
                        n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
                    }
                    bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
                }
                for (int i = 0; i < verts.Count; i++)
                {
                    if (uvs.Count == verts.Count) { bw.Write(uvs[i].u); bw.Write(uvs[i].v); }
                    else { bw.Write(0f); bw.Write(0f); }
                }
                for (int i = 0; i < verts.Count; i++)
                {
                    if (colors.Count == verts.Count) { bw.Write(colors[i].r); bw.Write(colors[i].g); bw.Write(colors[i].b); bw.Write(colors[i].a); }
                    else { bw.Write(1f); bw.Write(1f); bw.Write(1f); bw.Write(1f); }
                }
                foreach (var t in tris) { bw.Write((int)t.p1); bw.Write((int)t.p2); bw.Write((int)t.p3); }
                count++;
            }
            if (count == 0) return false;
            w.Write(count);
            bw.Flush();
            w.Write(body.ToArray());
            return true;
        }
        catch { return false; }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// A race's skeleton in its bind pose (every bone's place in the actor's space), with the arms brought down to the
    /// sides: each arm bone turned about its shoulder so the arm hangs ~77 degrees below level.
    /// </summary>
    sealed class Skeleton
    {
        public readonly Dictionary<string, Matrix4x4> Bones = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Parent = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Steps of the walk cycle the people are exported at (and standing, -1).</summary>
        public const int WalkSteps = 8;

        /// <summary>
        /// This skeleton at step <paramref name="step"/> of a walk (of <see cref="WalkSteps"/>): the thighs swing about
        /// the hips, the knee of the leg coming forward bends, and the arms swing against the legs. A turn at a joint
        /// carries everything below it.
        /// </summary>
        public Skeleton Walking(int step)
        {
            var w = new Skeleton();
            foreach (var (k, v) in Bones) w.Bones[k] = v;
            foreach (var (k, v) in Parent) w.Parent[k] = v;
            float phase = step / (float)WalkSteps * 2 * MathF.PI;
            foreach (var (side, sign) in new[] { ("L", 1f), ("R", -1f) })
            {
                float swing = MathF.Sin(phase) * sign;                          // +1: this leg forward
                w.Turn($"NPC {side} Thigh", 0.42f * swing);
                w.Turn($"NPC {side} Calf", -0.75f * MathF.Max(0, MathF.Cos(phase) * sign) - 0.12f);
                w.Turn($"NPC {side} UpperArm", -0.32f * swing);
                w.Turn($"NPC {side} Forearm", -0.18f);
            }
            return w;
        }

        /// <summary>Turns a bone (the first whose name starts so) and all below it about the side-to-side axis at its joint.</summary>
        void Turn(string prefix, float angle)
        {
            var bone = Bones.Keys.FirstOrDefault(b => b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !b.Contains("Twist"));
            if (bone is null) return;
            var pivot = Bones[bone].Translation;
            var rot = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateRotationX(angle) * Matrix4x4.CreateTranslation(pivot);
            foreach (var b in Bones.Keys.ToList())
                if (b.Equals(bone, StringComparison.OrdinalIgnoreCase) || IsBelow(b, bone)) Bones[b] = Bones[b] * rot;
        }

        bool IsBelow(string bone, string ancestor)
        {
            for (int depth = 0; Parent.TryGetValue(bone, out var p) && depth < 64; depth++, bone = p)
                if (p.Equals(ancestor, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static Skeleton? Load(byte[] bytes)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_skel_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) return null;
                var sk = new Skeleton();
                var hdr = nif.GetHeader();
                for (uint id = 0; id < hdr.GetNumBlocks(); id++)
                {
                    if (hdr.GetBlockById(id) is not NiNode node) continue;
                    string name = node.name.get() ?? "";
                    if (name.Length == 0) continue;
                    var m = ToMatrix(node.transform);
                    for (var parent = nif.GetParentNode(node); parent is not null; parent = nif.GetParentNode(parent)) m *= ToMatrix(parent.transform);
                    sk.Bones[name] = m;
                    if (nif.GetParentNode(node) is { } up && up.name.get() is { Length: > 0 } upName) sk.Parent[name] = upName;
                }
                sk.ArmsDown();
                return sk;
            }
            catch { return null; }
            finally { File.Delete(tmp); }
        }

        void ArmsDown()
        {
            foreach (var side in new[] { "L", "R" })
            {
                var upper = Bones.Keys.FirstOrDefault(b => b.StartsWith($"NPC {side} UpperArm", StringComparison.OrdinalIgnoreCase) && !b.Contains("Twist"));
                var hand = Bones.Keys.FirstOrDefault(b => b.StartsWith($"NPC {side} Hand", StringComparison.OrdinalIgnoreCase));
                if (upper is null || hand is null) continue;
                var shoulder = Bones[upper].Translation;
                var tip = Bones[hand].Translation;
                float dx = tip.X - shoulder.X;
                if (MathF.Abs(dx) < 1) continue;
                float current = MathF.Atan2(shoulder.Z - tip.Z, MathF.Abs(dx));
                float turn = (1.35f - current) * MathF.Sign(dx);
                // Row-vector maths: a turn about Y through the shoulder, swinging the arm's far end down.
                var rot = Matrix4x4.CreateTranslation(-shoulder) * Matrix4x4.CreateRotationY(turn) * Matrix4x4.CreateTranslation(shoulder);
                foreach (var b in Bones.Keys.ToList())
                    if (b.StartsWith($"NPC {side} ", StringComparison.OrdinalIgnoreCase)
                        && (b.Contains("UpperArm") || b.Contains("Forearm") || b.Contains("Hand") || b.Contains("Finger") || b.Contains("Thumb") || b.Contains("Weapon") || b.Contains("Shield")))
                        Bones[b] = Bones[b] * rot;
            }
        }
    }

    /// <summary>A nifly transform as a row-vector matrix (v * M), built from where it takes the origin and the axes.</summary>
    static Matrix4x4 ToMatrix(MatTransform t)
    {
        var o = t.ApplyTransform(new nifly.Vector3(0, 0, 0));
        var x = t.ApplyTransform(new nifly.Vector3(1, 0, 0));
        var y = t.ApplyTransform(new nifly.Vector3(0, 1, 0));
        var z = t.ApplyTransform(new nifly.Vector3(0, 0, 1));
        return new Matrix4x4(x.x - o.x, x.y - o.y, x.z - o.z, 0, y.x - o.x, y.y - o.y, y.z - o.z, 0, z.x - o.x, z.y - o.y, z.z - o.z, 0, o.x, o.y, o.z, 1);
    }

    readonly Dictionary<string, Skeleton?> skeletons = new(StringComparer.OrdinalIgnoreCase);

    Skeleton? SkeletonFor(string path, int step = -1)
    {
        string key = path + "|" + step;
        if (skeletons.TryGetValue(key, out var s)) return s;
        if (step >= 0) return skeletons[key] = SkeletonFor(path)?.Walking(step);
        string rel = path.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? path : "meshes\\" + path;
        return skeletons[key] = Find(rel) is { } bytes ? Skeleton.Load(bytes) : null;
    }

    /// <summary>
    /// Where a skinned shape's vertices sit on <paramref name="skeleton"/>: each vertex through its (up to four) bones,
    /// skin space to bone space to the posed skeleton's actor space, by weight; the same for normals.
    /// </summary>
    static (Func<nifly.Vector3, int, nifly.Vector3> Pos, Func<nifly.Vector3, int, nifly.Vector3> Dir)? OnSkeleton(NifFile nif, NiShape shape, int count, Skeleton skeleton)
    {
        var names = new vectorstring();
        nif.GetShapeBoneList(shape, names);
        var bones = names.ToList();
        var hdr = nif.GetHeader();
        using var cache = new niflycpp.BlockCache(hdr);
        var tri = cache.EditableBlockById<BSTriShape>(hdr.GetBlockID(shape));
        if (tri is null || tri.vertData.Count != count || bones.Count == 0) return null;
        var boneMats = new Matrix4x4[bones.Count];
        var known = new bool[bones.Count];
        for (int b = 0; b < bones.Count; b++)
        {
            var t = new MatTransform();
            nif.GetShapeTransformSkinToBone(shape, (uint)b, t);
            if (skeleton.Bones.TryGetValue(bones[b], out var world)) { boneMats[b] = ToMatrix(t) * world; known[b] = true; }
        }
        if (!known.Any(k => k)) return null;
        var weights = new (byte Bone, float W)[count][];
        for (int v = 0; v < count; v++)
        {
            var vd = tri.vertData[v];
            var w = vd.weights.ToArray(); var wb = vd.weightBones.ToArray();
            var list = new List<(byte, float)>();
            for (int k = 0; k < 4; k++) if (w[k] > 0 && wb[k] < bones.Count && known[wb[k]]) list.Add((wb[k], w[k]));
            if (list.Count == 0) { int any = Array.IndexOf(known, true); list.Add(((byte)any, 1)); }
            float sum = list.Sum(x => x.Item2);
            weights[v] = list.Select(x => (x.Item1, x.Item2 / sum)).ToArray();
        }
        nifly.Vector3 Pos(nifly.Vector3 v, int i)
        {
            var p = new Vector3(v.x, v.y, v.z);
            var r = Vector3.Zero;
            foreach (var (b, w) in weights[i]) r += Vector3.Transform(p, boneMats[b]) * w;
            return new nifly.Vector3(r.X, r.Y, r.Z);
        }
        nifly.Vector3 Dir(nifly.Vector3 v, int i)
        {
            var d = new Vector3(v.x, v.y, v.z);
            var r = Vector3.Zero;
            foreach (var (b, w) in weights[i]) r += Vector3.TransformNormal(d, boneMats[b]) * w;
            return new nifly.Vector3(r.X, r.Y, r.Z);
        }
        return (Pos, Dir);
    }

    sealed record PersonParts(List<string> Parts, float Height, string Skeleton);

    /// <summary>
    /// A person as the game dresses them: the race's body (where the outfit leaves it bare), the outfit's clothes and
    /// armour, and the head parts (the NPC's, else the race's defaults), with the race's and NPC's height. Traits and
    /// inventory come from the NPC's template when it uses one (a leveled list's first entry).
    /// </summary>
    static PersonParts? Person(ILinkCache cache, FormKey npcKey)
    {
        INpcGetter? Resolve(FormKey key, int depth = 0)
        {
            if (depth > 8) return null;
            if (cache.TryResolve<INpcGetter>(key, out var n)) return n;
            if (cache.TryResolve<ILeveledNpcGetter>(key, out var l) && l.Entries?.FirstOrDefault()?.Data?.Reference.FormKey is { } first) return Resolve(first, depth + 1);
            return null;
        }
        INpcGetter From(INpcGetter npc, NpcConfiguration.TemplateFlag flag, int depth = 0)
        {
            if (depth > 8 || !npc.Configuration.TemplateFlags.HasFlag(flag) || npc.Template.IsNull) return npc;
            return Resolve(npc.Template.FormKey) is { } t ? From(t, flag, depth + 1) : npc;
        }
        var baseNpc = Resolve(npcKey);
        if (baseNpc is null) return null;
        var traits = From(baseNpc, NpcConfiguration.TemplateFlag.Traits);
        var gear = From(baseNpc, NpcConfiguration.TemplateFlag.Inventory);
        if (!cache.TryResolve<IRaceGetter>(traits.Race.FormKey, out var race)) return null;
        bool female = traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        var parts = new List<string>();
        string? Model(IArmorAddonGetter a)
        {
            var mine = female ? a.WorldModel?.Female : a.WorldModel?.Male;
            var other = female ? a.WorldModel?.Male : a.WorldModel?.Female;
            return mine?.File.DataRelativePath.Path is { Length: > 0 } m ? m : other?.File.DataRelativePath.Path is { Length: > 0 } o ? o : null;
        }
        List<IArmorAddonGetter> Addons(IArmorGetter armor)
        {
            var all = new List<IArmorAddonGetter>();
            foreach (var l in armor.Armature) if (cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var a)) all.Add(a);
            var mine = all.Where(a => a.Race.FormKey == race.FormKey || a.AdditionalRaces.Any(r => r.FormKey == race.FormKey)).ToList();
            return mine.Count > 0 ? mine : all.Take(1).ToList();
        }
        // The outfit, and which body slots it covers.
        ulong covered = 0;
        if (!gear.DefaultOutfit.IsNull && cache.TryResolve<IOutfitGetter>(gear.DefaultOutfit.FormKey, out var outfit))
            foreach (var item in outfit.Items ?? [])
            {
                IArmorGetter? armor = cache.TryResolve<IArmorGetter>(item.FormKey, out var a) ? a : null;
                if (armor is null && cache.TryResolve<ILeveledItemGetter>(item.FormKey, out var li))
                    foreach (var e in li.Entries ?? [])
                        if (e.Data?.Reference.FormKey is { } fk && cache.TryResolve<IArmorGetter>(fk, out var la)) { armor = la; break; }
                if (armor is null) continue;
                covered |= (ulong)(armor.BodyTemplate?.FirstPersonFlags ?? 0);
                foreach (var addon in Addons(armor)) if (Model(addon) is { } m) parts.Add(m);
            }
        // The body (skin) where the outfit doesn't cover it.
        var skinKey = !traits.WornArmor.IsNull ? traits.WornArmor.FormKey : race.Skin.FormKey;
        if (cache.TryResolve<IArmorGetter>(skinKey, out var skin))
            foreach (var addon in Addons(skin))
                if (((ulong)(addon.BodyTemplate?.FirstPersonFlags ?? 0) & covered) == 0 && Model(addon) is { } m) parts.Add(m);
        // Head parts: the NPC's, then the race's default for any kind it lacks; with their extra parts.
        var heads = new Dictionary<HeadPart.TypeEnum, IHeadPartGetter>();
        void AddHead(FormKey k, bool overwrite)
        {
            if (!cache.TryResolve<IHeadPartGetter>(k, out var hp) || hp.Type is not { } type) return;
            if (overwrite || !heads.ContainsKey(type)) heads[type] = hp;
        }
        foreach (var hp in traits.HeadParts) AddHead(hp.FormKey, true);
        var headData = female ? race.HeadData?.Female : race.HeadData?.Male;
        foreach (var r in headData?.HeadParts ?? []) AddHead(r.Head.FormKey, false);
        void AddPart(IHeadPartGetter hp, int depth)
        {
            if (hp.Model?.File.DataRelativePath.Path is { Length: > 0 } m) parts.Add(m);
            if (depth < 3) foreach (var e in hp.ExtraParts) if (cache.TryResolve<IHeadPartGetter>(e.FormKey, out var x)) AddPart(x, depth + 1);
        }
        foreach (var hp in heads.Values) AddPart(hp, 0);
        float height = (female ? race.Height.Female : race.Height.Male) * (traits.Height > 0 ? traits.Height : 1);
        var skeleton = (female ? race.SkeletalModel?.Female : race.SkeletalModel?.Male)?.File.DataRelativePath.Path
            ?? @"Actors\Character\Character Assets\skeleton.nif";
        return new PersonParts(parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), height > 0 ? height : 1, skeleton);
    }

    static void WriteString(BinaryWriter w, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        w.Write(b.Length); w.Write(b);
    }

    static string NormaliseTexture(string t)
    {
        if (t.Length == 0) return "";
        t = t.Replace('/', '\\').TrimStart('\\');
        return t.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? t.ToLowerInvariant() : ("textures\\" + t).ToLowerInvariant();
    }

    /// <summary>Copies a texture (DDS) into the scene's textures folder, once.</summary>
    void Texture(string t)
    {
        string rel = NormaliseTexture(t);
        if (!textures.Add(rel)) return;
        var bytes = Find(rel);
        // Which model names it (a model's own path is named when it is missing).
        if (bytes is null) { Missing.Add(meshNow is null ? rel : $"{rel} (named by {meshNow})"); return; }
        string dest = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, bytes);
    }

    /// <summary>
    /// The land: per cell its 33x33 heights, and the textures painted on it as per-vertex weights (the base texture of
    /// each quadrant and its alpha layers), for the renderer to bake.
    /// </summary>
    object ExportLand(Dictionary<(int, int), ILandscapeGetter> lands, Dictionary<FormKey, string> landTextures)
    {
        var texIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var grasses = new List<List<object>>();
        int TexId(FormKey k)
        {
            string t = landTextures.TryGetValue(k, out var d) ? NormaliseTexture(d) : "textures\\landscape\\dirt01.dds";
            if (!texIds.TryGetValue(t, out int id)) { id = texIds.Count; texIds[t] = id; Texture(t); grasses.Add([]); }
            // A texture's grass from the first of its LTEX records that has any (two may share a diffuse).
            if (grasses[id].Count == 0 && landGrasses.TryGetValue(k, out var gs))
                foreach (var g in gs)
                {
                    if (g.Model?.File.DataRelativePath.Path is not { Length: > 0 } model) continue;
                    int mesh = MeshId(model);
                    if (mesh < 0) continue;
                    grasses[id].Add(new
                    {
                        mesh, density = (int)g.Density, min_slope = (int)g.MinSlope, max_slope = (int)g.MaxSlope,
                        water = (int)g.UnitsFromWater, water_type = g.UnitsFromWaterType.ToString(),
                        position_range = g.PositionRange, height_range = g.HeightRange, color_range = g.ColorRange,
                        fit_to_slope = g.Flags.HasFlag(Grass.Flag.FitToSlope), uniform_scale = g.Flags.HasFlag(Grass.Flag.UniformScaling),
                    });
                }
            return id;
        }
        string landPath = Path.Combine(outDir, "land.bin");
        using var w = new BinaryWriter(File.Create(landPath));
        int written = 0;
        w.Write(lands.Count);
        foreach (var ((gx, gy), land) in lands)
        {
            if (land.VertexHeightMap is not { } vhgt) { w.Write(gx); w.Write(gy); w.Write(0); continue; }
            var h = new float[33 * 33];
            float row = vhgt.Offset;
            for (int y = 0; y < 33; y++)
            {
                row += vhgt.HeightMap[0, y];
                float v = row;
                h[y * 33] = v * 8;
                for (int x = 1; x < 33; x++) { v += vhgt.HeightMap[x, y]; h[y * 33 + x] = v * 8; }
            }
            // Weights per vertex per texture, painted quadrant by quadrant (base, then alpha layers in order).
            var weights = new Dictionary<int, float[]>();
            float[] W(int id) => weights.TryGetValue(id, out var a) ? a : weights[id] = new float[33 * 33];
            var layers = land.Layers ?? [];
            for (int q = 0; q < 4; q++)
            {
                int qx = (q & 1) * 16, qy = (q >> 1) * 16;
                var inQuad = layers.Where(l => (int)l.Header!.Quadrant == q).ToList();
                int baseId = inQuad.FirstOrDefault(l => l is not IAlphaLayerGetter) is { } b ? TexId(b.Header!.Texture.FormKey) : TexId(FormKey.Null);
                for (int ly = 0; ly < 17; ly++)
                    for (int lx = 0; lx < 17; lx++) W(baseId)[(qy + ly) * 33 + qx + lx] = 1;
                foreach (var layer in inQuad.OfType<IAlphaLayerGetter>().OrderBy(l => l.Header!.LayerNumber))
                {
                    int id = TexId(layer.Header!.Texture.FormKey);
                    foreach (var p in layer.AlphaLayerData ?? [])
                    {
                        int i = (qy + p.Position / 17) * 33 + qx + p.Position % 17;
                        float a = Math.Clamp(p.Opacity, 0, 1);
                        foreach (var (k, arr) in weights) if (k != id) arr[i] *= 1 - a;
                        W(id)[i] = W(id)[i] * (1 - a) + a;
                    }
                }
            }
            w.Write(gx); w.Write(gy); w.Write(1);
            foreach (var v in h) w.Write(v);
            w.Write(weights.Count);
            foreach (var (id, arr) in weights) { w.Write(id); foreach (var v in arr) w.Write(v); }
            written++;
        }
        return new { cells = written, textures = texIds.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList(), grasses };
    }
}
