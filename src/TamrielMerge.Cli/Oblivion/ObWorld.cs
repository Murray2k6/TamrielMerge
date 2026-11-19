using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Activator = Mutagen.Bethesda.Skyrim.Activator;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's whole world: every interior and every worldspace (Cyrodiil, the Shivering Isles, the city worlds with
/// their parent, the planes of Oblivion), cell by cell. Oblivion cells, units and landscape encoding (heights in
/// 8-unit byte steps, normals, vertex colours, a base and alpha texture layers per quadrant) are Skyrim's, so the
/// land is copied as it is with its textures. Load doors already name their partner door, as in Skyrim. Trees are
/// SpeedTree in Oblivion and become Skyrim trees of the same kind, scaled to Oblivion's height.
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>Skyrim's Data folder (vanilla trees, climate and sounds); set before building the world.</summary>
    public string? SkyrimData { get; set; }
    ISkyrimModDisposableGetter? skyrimEsm;
    ISkyrimModDisposableGetter SkyrimEsm => skyrimEsm ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(SkyrimData ?? throw new InvalidOperationException("SkyrimData not set"), "Skyrim.esm"), SkyrimRelease.SkyrimSE);

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    /// <summary>A cell as all active plugins together have it: the last plugin's header and every reference, a later
    /// plugin's version of a reference replacing the earlier.</summary>
    sealed class MergedCell
    {
        public required Ob.ICellGetter Header;
        public Ob.ILandscapeGetter? Land;
        public Ob.IWorldspaceGetter? World;
        public bool IsPersistentCell;
        public Dictionary<FormKey, Ob.IPlacedGetter> Refs { get; } = new();
    }

    List<MergedCell>? merged;
    readonly Dictionary<FormKey, PlacedObject> placedDoors = new();
    readonly Dictionary<FormKey, Ob.IPlacedObjectGetter> sourceDoors = new();
    readonly Dictionary<FormKey, Worldspace> worldspaces = new();

    List<MergedCell> Merge()
    {
        if (merged is not null) return merged;
        var cells = new Dictionary<FormKey, MergedCell>();
        void Take(Ob.ICellGetter c, Ob.IWorldspaceGetter? ws, bool persistentCell)
        {
            if (!cells.TryGetValue(c.FormKey, out var m)) cells[c.FormKey] = m = new MergedCell { Header = c, World = ws, IsPersistentCell = persistentCell };
            else m.Header = c;
            if (c.Landscape is { } land) m.Land = land;
            foreach (var r in c.Persistent.Concat(c.Temporary).Concat(c.VisibleWhenDistant)) m.Refs[r.FormKey] = r;
        }
        foreach (var p in plugins)
        {
            foreach (var c in p.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells)) Take(c, null, false);
            foreach (var ws in p.Worldspaces)
            {
                if (ws.TopCell is { } top) Take(top, ws, true);
                foreach (var c in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)) Take(c, ws, false);
            }
        }
        return merged = cells.Values.ToList();
    }

    // ---- bases for doors and trees ----

    static readonly FormKey DoorOpenSound = Sk(0x0BFBE8), DoorCloseSound = Sk(0x0BFBE9), SwingOpenSound = Sk(0x05B0F3), SwingCloseSound = Sk(0x05B134);
    readonly Dictionary<FormKey, Activator?> swingDoors = new();
    /// <summary>Door bases whose model was raised to stand on its foot, by how far the foot was from the old origin.</summary>
    readonly Dictionary<FormKey, float> doorFoot = new();
    public int SwingingDoorsPlaced { get; private set; }
    /// <summary>Inner doors given Oblivion's rule for people and doors (TMObDoor).</summary>
    public int InnerDoorsWatched { get; private set; }
    /// <summary>The inner doors placed (Skyrim doors: gates, cell doors, rooms' doors), whose doorways their interior's
    /// navmesh keeps to the frame (<see cref="Doorways"/>).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<FormKey, byte> innerDoors = new();

    /// <param name="solid">Whether the navmesh goes around it (a load door; not a door that opens by itself).</param>
    Door? LoadDoor(Ob.IDoorGetter d, string edid, bool solid = true)
    {
        if (d.Model?.File is not { Length: > 0 } file || IsMarker(file)) return null;
        var mesh = Mesh(file);
        if (mesh is null) return null;
        // Its origin at its foot, where Skyrim's people look for a door to open (ObDoorFoot).
        (mesh, float foot) = DoorAtFoot(mesh);
        var door = new Door(mod)
        {
            EditorID = edid,
            ObjectBounds = Bounds(mesh),
            Model = new Model { File = SkyrimModelPath(mesh.Path) },
            OpenSound = new FormLinkNullable<ISoundDescriptorGetter>(DoorOpenSound),
            CloseSound = new FormLinkNullable<ISoundDescriptorGetter>(DoorCloseSound),
        };
        if (d.Name is { Length: > 0 } n) door.Name = n;
        mod.Doors.Add(door);
        if (foot != 0) doorFoot[door.FormKey] = foot;
        // A door that opens by itself stands open as often as closed: the navmesh goes through it, as through swing
        // doors.
        if (solid) meshByBase[door.FormKey] = mesh;
        return door;
    }

    /// <summary>Whether the door's model has its own Open animation (then Skyrim's door plays it).</summary>
    bool OpensByItself(Ob.IDoorGetter d) => d.Model?.File is { Length: > 0 } file && !IsMarker(file) && Mesh(file) is { } mesh && mesh.Plays("Open");

    /// <summary>A door that doesn't teleport: an activator running TMSwingDoor (90 degrees about the model's origin),
    /// on a variant of the model with keyframed collision.</summary>
    Activator? SwingDoor(FormKey key)
    {
        if (swingDoors.TryGetValue(key, out var hit)) return hit;
        swingDoors[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IDoorGetter d || d.Model?.File is not { Length: > 0 } file || IsMarker(file)) return null;
        ObMeshConverter.Result? mesh;
        try { mesh = meshes.Convert(file, modOutDir, moving: true); }
        catch (Exception e) { Problems.Add($"model {file}: {e.Message}"); return null; }
        var door = new Activator(mod)
        {
            EditorID = "OB" + Sanitize(d.EditorID ?? key.ID.ToString("X6")) + "Swing",
            ObjectBounds = Bounds(mesh),
            Model = new Model { File = SkyrimModelPath(mesh.Path) },
            ActivateTextOverride = "Open",
            VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts =
                [
                    new ScriptEntry
                    {
                        Name = "TMSwingDoor",
                        Properties =
                        [
                            new ScriptObjectProperty { Name = "OpenSound", Object = new FormLink<ISkyrimMajorRecordGetter>(SwingOpenSound) },
                            new ScriptObjectProperty { Name = "CloseSound", Object = new FormLink<ISkyrimMajorRecordGetter>(SwingCloseSound) },
                        ],
                    },
                ],
            },
        };
        if (d.Name is { Length: > 0 } n) door.Name = n;
        mod.Activators.Add(door);
        return swingDoors[key] = door;
    }

    /// <summary>Oblivion tree name fragment -> Skyrim trees of that kind (one is picked per tree type).</summary>
    static readonly (string Match, string[] Trees)[] TreeKinds =
    [
        ("mania", ["TreeReachTree01Magic"]), ("dementia", ["TreeTundraDriftWoodTree01"]), ("dtree", ["TreeTundraDriftWoodTree01"]),
        ("mugopine", ["TreePineShrub01", "TreePineShrub02"]),
        ("shrub", ["TreeFallForestShrub01", "TreeFallForestShrub02", "TreeReachShrub01", "TreeReachShrub02", "TreeVineMaple01", "TreeYellowShrub01"]),
        ("snow", ["TreePineForestSnow01", "TreePineForestSnow02", "TreePineForestSnow03", "TreePineForestSnow05"]),
        ("pine", ["TreePineForest01", "TreePineForest02", "TreePineForest03", "TreePineForest05"]),
        ("hemlock", ["TreePineForest01", "TreePineForest02", "TreePineForest04"]),
        ("deodar", ["TreePineForest02", "TreePineForest04"]), ("redwood", ["TreePineForest04", "TreePineForest02"]),
        ("juniper", ["TreeFloraJuniper01"]),
        ("fa", ["TreeAspen01", "TreeAspen02", "TreeAspen03", "TreeAspen05"]),
        ("", ["TreeReachTree01", "TreeReachTree02", "TreeAspen02"]),
    ];

    readonly Dictionary<FormKey, (FormKey Tree, float Scale)?> trees = new();

    (FormKey Tree, float Scale)? TreeFor(Ob.ITreeGetter t)
    {
        if (trees.TryGetValue(t.FormKey, out var hit)) return hit;
        string name = (t.EditorID ?? "").ToLowerInvariant() + " " + (t.Model?.File ?? "").ToLowerInvariant();
        var kind = TreeKinds.First(k => k.Match.Length == 0 || name.Contains(k.Match) && (k.Match != "fa" || name.Contains("fa.") || name.EndsWith("fa") || name.Contains("fa ")));
        string pick = kind.Trees[Math.Abs((t.EditorID ?? "").GetHashCode(StringComparison.OrdinalIgnoreCase)) % kind.Trees.Length];
        var vanilla = SkyrimEsm.Trees.FirstOrDefault(v => v.EditorID == pick);
        if (vanilla is null) { Problems.Add($"tree {t.EditorID}: vanilla {pick} not found"); return trees[t.FormKey] = null; }
        // Scale the Skyrim tree to Oblivion's billboard height.
        float obHeight = t.BillboardDimensions?.Height ?? 0;
        float skyHeight = vanilla.ObjectBounds is { } ob ? ob.Second.Z - ob.First.Z : 0;
        float scale = obHeight > 0 && skyHeight > 0 ? Math.Clamp(obHeight / skyHeight, 0.25f, 2.5f) : 1;
        return trees[t.FormKey] = (vanilla.FormKey, scale);
    }

    // ---- placement ----

    /// <summary>Places one Oblivion reference into a Skyrim cell (null for what isn't converted yet, e.g. actors).</summary>
    PlacedObject? Place(Ob.IPlacedObjectGetter r, Cell cell, Cell persistentCell)
    {
        if (r.Location is null || r.Base.IsNull) return null;
        var placement = new Placement { Position = r.Location.Position, Rotation = r.Location.Rotation };
        if (r.EditorID == OblivionStartRef)
        {
            StartMarker.Placement = placement;
            persistentCell.Persistent.Add(StartMarker);
            // Oblivion's scripts name it too (CharacterGen's stage 5 moves the player to it; unregistered, that
            // property was left empty: "cannot be moved to a None object's location", 2026-11-12).
            placedBySource[r.FormKey] = StartMarker;
            return StartMarker;
        }
        var rec = records.GetValueOrDefault(r.Base.FormKey);
        if (rec is Ob.ILeveledCreatureGetter or Ob.ICreatureGetter or Ob.INpcGetter)
        {
            PlaceActorAt(r.FormKey, r.Base.FormKey, r.Location, r.Scale, cell, persistentCell);
            return null;
        }
        // Oblivion's prisons: Skyrim prison markers, for the lands' jails (ObCrime).
        if (rec?.EditorID == "PrisonMarker") return PlacePrisonMarker(r, cell, persistentCell);
        // Oblivion's map markers (every city, settlement, ruin, cave, camp...): Skyrim's, for the map and fast travel.
        if (rec?.EditorID == "MapMarker") return PlaceMapMarker(r, persistentCell);
        float scale = r.Scale ?? 1;
        FormKey baseKey;
        bool isDoor = false, innerDoor = false;
        switch (rec)
        {
            case Ob.ITreeGetter tree:
                if (TreeFor(tree) is not { } t) return null;
                baseKey = t.Tree; scale *= t.Scale;
                break;
            case Ob.IDoorGetter door when r.TeleportDestination is null && !OpensByItself(door):
                // A Skyrim door with a swing of its own (people open it); the script-swung activator only where its
                // model couldn't be given one.
                if (SwingingDoor(door.FormKey) is { } swinging)
                {
                    baseKey = swinging.FormKey;
                    if (doorFoot.TryGetValue(baseKey, out float swingFoot)) { placement = AtFoot(placement, swingFoot, scale); DoorsFooted++; }
                    SwingingDoorsPlaced++;
                    innerDoor = true;
                    break;
                }
                if (SwingDoor(door.FormKey) is not { } swing) return null;
                baseKey = swing.FormKey;
                break;
            case Ob.IDoorGetter door:
                // A load door, or a door whose model opens and closes by its own animation (gates, portcullises,
                // cell doors, secret doors: a Skyrim door plays their Open and Close, as vanilla MineSecretDoor01).
                if (!bases.TryGetValue(door.FormKey, out var known)) bases[door.FormKey] = known = LoadDoor(door, "OB" + Sanitize(door.EditorID ?? door.FormKey.ID.ToString("X6")), solid: r.TeleportDestination is not null);
                if (known is null) return null;
                baseKey = known.FormKey; isDoor = r.TeleportDestination is not null; innerDoor = !isDoor;
                if (doorFoot.TryGetValue(baseKey, out float foot)) { placement = AtFoot(placement, foot, scale); DoorsFooted++; }
                break;
            default:
                if (Base(r.Base.FormKey) is not { } b)
                {
                    if (effectOnly.TryGetValue(r.Base.FormKey, out var only)) PlaceEffects(only, placement, scale, cell);
                    // Markers scripts move things to (MoveTo, PlaceAtMe) are kept, as Skyrim heading markers.
                    if (!scriptRefs.Contains(r.FormKey)) return null;
                    baseKey = XMarkerHeading;
                    break;
                }
                baseKey = b.FormKey;
                break;
        }
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(baseKey),
            Placement = placement,
            Scale = MathF.Abs(scale - 1) > 0.001f ? scale : null,
            ItemCount = r.Count is > 1 && records.GetValueOrDefault(r.Base.FormKey) is { } br && IsItem(br) ? r.Count : null,
        };
        ApplyLock(r, placed, innerDoor);
        if (innerDoor)
        {
            // Oblivion's rule for people and doors (TMObDoor): Skyrim's people don't open these by themselves.
            placed.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            placed.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "TMObDoor" });
            InnerDoorsWatched++;
            innerDoors[placed.FormKey] = 0;
        }
        if (OwnerOf(r.Owner.FormKey, r.FactionRank, !r.GlobalVariable.IsNull, cell: false) is { } owner)
        {
            placed.Owner.SetTo(owner.Owner);
            placed.FactionRank = owner.Rank;
        }
        if (PathLinked().Contains(r.FormKey)) placedPathLinked[r.FormKey] = placed;
        if (isDoor)
        {
            placed.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
            persistentCell.Persistent.Add(placed);
            placedDoors[r.FormKey] = placed;
            sourceDoors[r.FormKey] = r;
            placedOf[r.FormKey] = placed;
            if (scriptRefs.Contains(r.FormKey)) placedBySource[r.FormKey] = placed;
        }
        else if (!PlaceForScripts(r.FormKey, placed, cell, persistentCell)) cell.Temporary.Add(placed);
        if (emittersByBase.TryGetValue(baseKey, out var emitters)) PlaceEffects(emitters, placement, scale, cell);
        // Its model's trigger shapes as trigger boxes, when its script listens for what enters them.
        if (!isDoor && meshByBase.TryGetValue(baseKey, out var withTriggers) && withTriggers.Triggers.Count > 0)
        {
            TriggerShapesSeen++;
            if (ListensForTriggers(r.Base.FormKey)) PlaceTriggers(placed, withTriggers.Triggers, scale, cell, persistentCell);
            else if (TriggerUnheard.Count < 8) TriggerUnheard.Add(records.GetValueOrDefault(r.Base.FormKey)?.EditorID ?? r.Base.FormKey.ToString());
        }
        return placed;
    }

    /// <summary>Trigger boxes placed for Oblivion's trigger shapes (<see cref="PlaceTriggers"/>), the references whose
    /// model has one, and some whose script listens for none.</summary>
    public int TriggersPlaced { get; private set; }
    /// <summary>Oblivion's map markers placed as Skyrim's (fast travel; the ones Oblivion shows from the start keep that).</summary>
    public int MapMarkersPlaced { get; private set; }

    /// <summary>
    /// A map marker reference (base MapMarker, XMRK: name, kind, visible from the start / can be travelled to) as
    /// Skyrim's (base MapMarker 0x10, MapMarker data), persistent in the worldspace's cell as Morrowind's towns'
    /// are (MwPluginBuilder). Oblivion's kinds map onto Skyrim's icons by name where Skyrim has the kind (Camp,
    /// Cave, City, Mine, Landmark, Settlement); its Ayleid and fort ruins are Skyrim's Nordic ruins and forts,
    /// taverns settlements, Daedric shrines shrines, Oblivion gates and plain doors landmarks.
    /// </summary>
    PlacedObject? PlaceMapMarker(Ob.IPlacedObjectGetter r, Cell persistentCell)
    {
        if (r.MapMarker is not { } mm || r.Location is null) return null;
        string kind = $"{mm.Types}";
        string flags = $"{mm.Flags}";
        var type = kind switch
        {
            "ElvenRuin" => MapMarker.MarkerType.NordicRuins,
            "FortRuin" => MapMarker.MarkerType.Fort,
            "Tavern" => MapMarker.MarkerType.Settlement,
            "DaedricShrine" => MapMarker.MarkerType.Shrine,
            "OblivionGate" or "Door" or "None" => MapMarker.MarkerType.Landmark,
            _ => Enum.TryParse<MapMarker.MarkerType>(kind, out var same) ? same : MapMarker.MarkerType.Landmark,
        };
        var placed = new PlacedObject(mod)
        {
            EditorID = "OBMapMarker" + Sanitize(r.EditorID ?? r.FormKey.ID.ToString("X6")),
            Base = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x10).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = r.Location.Position, Rotation = r.Location.Rotation },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            MapMarker = new MapMarker
            {
                Name = mm.Name ?? "",
                Type = type,
                Flags = (flags.Contains("Visible") ? MapMarker.Flag.Visible : 0) | (flags.Contains("CanTravelTo") ? MapMarker.Flag.CanTravelTo : 0),
            },
        };
        persistentCell.Persistent.Add(placed);
        if (scriptRefs.Contains(r.FormKey)) placedBySource[r.FormKey] = placed;
        MapMarkersPlaced++;
        return placed;
    }
    public int TriggerShapesSeen { get; private set; }
    public List<string> TriggerUnheard { get; } = [];

    readonly Dictionary<FormKey, bool> listensForTriggers = new();

    /// <summary>Whether an Oblivion base object's script has an OnTrigger, OnTriggerActor or OnTriggerMob block.</summary>
    bool ListensForTriggers(FormKey obBase)
    {
        if (listensForTriggers.TryGetValue(obBase, out bool known)) return known;
        bool listens = records.GetValueOrDefault(obBase) is { } rec && ObScriptWorld.ScriptLink(rec) is { } script
            && records.GetValueOrDefault(script) is Ob.IScriptGetter s && s.Fields?.SourceCode is { } source
            && System.Text.RegularExpressions.Regex.IsMatch(source, @"(?im)^\s*begin\s+ontrigger");
        return listensForTriggers[obBase] = listens;
    }

    Activator? triggerBase;

    /// <summary>
    /// A reference's trigger shapes (its model's phantoms, ObMeshConverter.Result.Triggers) as Skyrim trigger boxes, as
    /// vanilla's are: a reference of a model-less activator with a box primitive (unknown 0.15, no collision layer:
    /// 2,899 of vanilla's scripted ones, vanilla-primitives) at the shape's middle, turned with the reference, its script
    /// TMObTrigger telling the reference what enters (2026-10-20, ob-hazards: 5,890 placed Oblivion objects whose
    /// scripts hurt, most from OnTrigger: flames burning whoever touches them, pressure plates, tripwires, kill boxes, fire
    /// columns, gas and spore traps; the shapes were left out, so none of them fired). The reference, named by the boxes'
    /// script, is made persistent.
    /// </summary>
    void PlaceTriggers(PlacedObject owner, List<(System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)> boxes, float scale, Cell cell, Cell persistentCell)
    {
        if (cell.Temporary.Remove(owner))
        {
            owner.SkyrimMajorRecordFlags |= (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
            persistentCell.Persistent.Add(owner);
        }
        if (triggerBase is null) { triggerBase = new Activator(mod) { EditorID = "TMObTriggerBox" }; mod.Activators.Add(triggerBase); }
        var pl = owner.Placement!;
        var m = TamrielMerge.Skyrim.SkyrimRotation.Matrix(pl.Rotation);
        var pos = new System.Numerics.Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
        foreach (var (lo, hi) in boxes)
        {
            var centre = System.Numerics.Vector3.Transform((lo + hi) / 2 * scale, m) + pos;
            // The whole box: the record's bounds here are its full size (the file holds half of them: measured
            // 2026-11-15 on HearthFires.esm, file 44 x 160 x 24 against 88 x 320 x 48 here). Given half sizes until
            // then, every trigger was half as big each way as Oblivion's: the prison cell's "stand by the window"
            // zone, 534 x 53 x 380 along the back wall, came out 267 x 27 x 190 and only its far corner was in the
            // cell (user: "it does not matter where on the wall you stand, the marker is wrong in the prison").
            var size = (hi - lo) * scale;
            var box = new PlacedObject(mod)
            {
                Base = triggerBase.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(centre.X, centre.Y, centre.Z), Rotation = pl.Rotation },
                Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(size.X, size.Y, size.Z), Color = System.Drawing.Color.FromArgb(0, 204, 76, 51), Unknown = 0.15f },
                VirtualMachineAdapter = new VirtualMachineAdapter
                {
                    Scripts = [new ScriptEntry { Name = "TMObTrigger", Properties = [new ScriptObjectProperty { Name = "Owner", Object = owner.ToLink<ISkyrimMajorRecordGetter>() }] }],
                },
            };
            cell.Temporary.Add(box);
            TriggersPlaced++;
        }
    }

    /// <summary>Every interior cell of the active plugins.</summary>
    // ---- linked path points ----

    Keyword? pathCutKeyword;
    HashSet<FormKey>? pathLinked;
    readonly Dictionary<FormKey, PlacedObject> placedPathLinked = new();

    /// <summary>Path points switched off by a script (PlacePathCuts), and the references they hang on.</summary>
    public int PathCutsPlaced { get; private set; }
    public int PathCutOwners { get; private set; }

    /// <summary>The keyword linking a reference to its navmesh-cutting boxes (TMObState.LinkedPathPoints follows it).</summary>
    Keyword PathCutKeyword()
    {
        if (pathCutKeyword is null) { pathCutKeyword = new Keyword(mod) { EditorID = "OBPathCut" }; mod.Keywords.Add(pathCutKeyword); }
        return pathCutKeyword;
    }

    /// <summary>The references some cell's path grid links points to (PGRL), over every cell of the plugins.</summary>
    HashSet<FormKey> PathLinked()
    {
        if (pathLinked is not null) return pathLinked;
        pathLinked = [];
        foreach (var m in Merge())
            foreach (var map in m.Header.PathGrid?.PointToReferenceMappings ?? [])
                pathLinked.Add(map.Reference.FormKey);
        return pathLinked;
    }

    /// <summary>
    /// Oblivion's linked path points (a path grid's PGRL: the points a gate, a bridge or a rock fall stands on, which
    /// its script switches off with DisableLinkedPathPoints so nobody walks through it, and on again). Skyrim has no path
    /// grid and its own function of that name only returns; what it has for the purpose is the navmesh-cutting box (a
    /// CollisionMarker primitive on the collision layer L_NAVCUT, 49: 439 in Skyrim.esm, vanilla-primitives), which
    /// cuts the navmesh while it is enabled. Each linked point is one, off to begin with (Oblivion's points are on),
    /// chained to its reference by OBPathCut links; TMObState.LinkedPathPoints enables them to switch the points off.
    /// </summary>
    void PlacePathCuts(Ob.ICellGetter? source, Cell persistentCell)
    {
        if (source?.PathGrid is not { PointToReferenceMappings.Count: > 0 } grid) return;
        // (A grid that maps points to references has its points; none, and every mapping is out of range.)
        var points = grid.PointToPointConnections ?? [];
        foreach (var map in grid.PointToReferenceMappings)
        {
            if (!placedPathLinked.TryGetValue(map.Reference.FormKey, out var owner)) continue;
            PlacedObject last = owner;
            int made = 0;
            foreach (int i in map.Points.Select(p => (int)p).Distinct())
            {
                if (i < 0 || i >= points.Count) continue;
                var at = points[i].Point;
                var box = new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(Sk(0x21)),   // CollisionMarker
                    // The box stands on the point: half a person's height each way from 64 above it.
                    Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z + 64), Rotation = new P3Float(0, 0, 0) },
                    Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(112, 112, 160), Color = System.Drawing.Color.FromArgb(0, 204, 76, 51), Unknown = 0.15f },
                    CollisionLayer = 49,
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)(0x400 | 0x800),   // persistent, initially disabled
                };
                persistentCell.Persistent.Add(box);
                last.LinkedReferences.Add(new LinkedReferences { KeywordOrReference = PathCutKeyword().ToLink<IKeywordLinkedReferenceGetter>(), Reference = box.ToLink<IPlacedGetter>() });
                last = box;
                made++;
            }
            if (made > 0) { PathCutsPlaced += made; PathCutOwners++; }
        }
    }

    public int BuildAllInteriors()
    {
        int n = 0;
        // Each interior's navmesh made on a worker while the next are built, finished in the order begun (as many in hand as
        // two per core).
        var inHand = new Queue<NavJob>();
        int most = Math.Max(2, Environment.ProcessorCount * 2);
        foreach (var m in Merge().Where(m => m.World is null))
        {
            var cell = NewInterior(m.Header);
            foreach (var r in m.Refs.Values)
            {
                if (r is Ob.IPlacedObjectGetter o) Place(o, cell, cell);
                else PlaceActor(r, cell, cell);
            }
            PlacePathCuts(m.Header, cell);
            if (BeginNavmesh(cell, m.Header, background: true) is { } job) inHand.Enqueue(job);
            while (inHand.Count > most) FinishNavmesh(inHand.Dequeue());
            AddInteriorCell(cell);
            interiorsBySource[m.Header.FormKey] = cell;
            n++;
        }
        while (inHand.Count > 0) FinishNavmesh(inHand.Dequeue());
        return n;
    }

    Cell NewInterior(Ob.ICellGetter source)
    {
        var cell = new Cell(mod)
        {
            EditorID = "OB" + Sanitize(source.EditorID ?? source.FormKey.ID.ToString("X6")),
            Name = source.Name,
            // "Public place": an owned inn, shop or hall anyone may enter. Without it Skyrim takes the player for a
            // trespasser there (seen in game 2026-11-09: The Feed Bag's innkeeper at noon said only "it looks like you've
            // wandered into a restricted area"; its converted cell had the interior flag alone and its owner).
            Flags = Cell.Flag.IsInteriorCell | (source.Flags is { } f && f.HasFlag(Ob.Cell.Flag.HasWater) ? Cell.Flag.HasWater : 0)
                // Every interior, not only Oblivion's "public place" ones: in Oblivion an owned place is trespassed only by
                // getting in through a locked door, so an open shop or house by day is no trespass, flagged public or not
                // (Jensine's shop isn't: DATA 0x01; in game she said only the trespass line). Skyrim counts every owned
                // interior that isn't public as trespass at once. Not carried: trespass after picking a lock.
                | Cell.Flag.PublicArea,
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = Lighting(source.Lighting),
            WaterHeight = source.WaterHeight,
            Persistent = [],
            Temporary = [],
        };
        // A water that hurts (lava) as lava that hurts (HurtingWater).
        if (!source.Water.IsNull && HurtingWater(source.Water.FormKey) is { } lava) cell.Water.SetTo(lava);
        // Its owner (a house, a shop, a guild hall): what is in it is theirs, as in Oblivion.
        if (OwnerOf(source.Owner.FormKey, source.FactionRank, !source.GlobalVariable.IsNull, cell: true) is { } owner)
        {
            cell.Owner.SetTo(owner.Owner);
            if (owner.Rank is { } rank) cell.FactionRank = rank;
        }
        return cell;
    }

    /// <summary>References and interior cells given their owner (2026-11-05, user: "theres no ownership by npcs etc for
    /// objects that can be picked up", "for any game"): Oblivion's person or faction (with the rank from which its members
    /// may take freely), as Skyrim's ownership. One whose ownership has a global (Oblivion's rented beds and rooms, free
    /// while it is set) is left unowned, as Skyrim's ownership has no global to free it by.</summary>
    public int OwnedRefs { get; private set; }
    public int OwnedCells { get; private set; }
    public int OwnershipByGlobalLeft { get; private set; }

    (FormKey Owner, int? Rank)? OwnerOf(FormKey? obOwner, int? rank, bool byGlobal, bool cell)
    {
        if (obOwner is not { IsNull: false } key) return null;
        if (byGlobal) { OwnershipByGlobalLeft++; return null; }
        FormKey? to = records.GetValueOrDefault(key) switch
        {
            Ob.INpcGetter => (Actor(key) as INpcGetter)?.FormKey,
            Ob.IFactionGetter => ObFaction(key)?.FormKey,
            _ => null,
        };
        if (to is null) return null;
        if (cell) OwnedCells++; else OwnedRefs++;
        return (to.Value, records.GetValueOrDefault(key) is Ob.IFactionGetter ? rank : null);
    }

    // ---- water ----

    Dictionary<FormKey, Ob.IWaterGetter>? obWaterRecords;
    readonly Dictionary<FormKey, Water?> hurtingWaters = new();

    /// <summary>Oblivion's water type by its FormKey (the last plugin's).</summary>
    Ob.IWaterGetter? ObWater(FormKey key)
    {
        obWaterRecords ??= plugins.SelectMany(p => p.Waters).GroupBy(w => w.FormKey).ToDictionary(g => g.Key, g => g.Last());
        return obWaterRecords.GetValueOrDefault(key);
    }

    /// <summary>
    /// Oblivion's water types that hurt (CausesDamage), as Skyrim lava: Skyrim's own LavaWater (Skyrim.esm, its surface
    /// and its underwater look from Update.esm), flagged CausesDamage as Apocrypha's water is, with Oblivion's damage a
    /// second (2026-10-20, ob-waters: OblivionLavaTest01, 50 a second, the water of the 14 Oblivion planes and 31
    /// interiors; OblivionCitadelLavaPlane, 5000, 15 interiors; CamoranLava02, 1). Null for a water that doesn't hurt.
    /// Whether Skyrim applies a water's damage can't be seen without the game; the navmesh keeps off it either way.
    /// </summary>
    Water? HurtingWater(FormKey key)
    {
        if (hurtingWaters.TryGetValue(key, out var made)) return made;
        Water? lava = null;
        if (ObWater(key) is { } ob && ob.Flags?.HasFlag(Ob.Water.Flag.CausesDamage) == true && Vanilla.Get<IWaterGetter>("LavaWater") is { } skyrimLava)
        {
            lava = mod.Waters.DuplicateInAsNewRecord(skyrimLava);
            lava.EditorID = "OB" + Sanitize(ob.EditorID ?? key.ID.ToString("X6"));
            lava.Flags |= Water.Flag.CausesDamage;
            lava.DamagePerSecond = (ushort)Math.Clamp((int)(ob.Data?.Damage ?? 0), 0, ushort.MaxValue);
        }
        return hurtingWaters[key] = lava;
    }

    // ---- landscape ----

    readonly Dictionary<FormKey, LandscapeTexture?> landTextures = new();

    LandscapeTexture? LandTexture(FormKey obTexture)
    {
        if (landTextures.TryGetValue(obTexture, out var hit)) return hit;
        landTextures[obTexture] = null;
        if (records.GetValueOrDefault(obTexture) is not Ob.ILandTextureGetter lt || lt.Icon is not { Length: > 0 } icon) return null;
        string file = icon.StartsWith("landscape", StringComparison.OrdinalIgnoreCase) ? icon : @"landscape\" + icon;
        var diffuse = meshes.Textures.EnsureLand(file);
        if (diffuse is null) return null;
        var set = new TextureSet(mod)
        {
            EditorID = "OBLandSet" + Sanitize(lt.EditorID ?? obTexture.ID.ToString("X6")),
            ObjectBounds = new ObjectBounds(),
            Diffuse = diffuse[9..],
            NormalOrGloss = meshes.Textures.Normal(file)[9..],
        };
        mod.TextureSets.Add(set);
        var skyLt = new LandscapeTexture(mod)
        {
            EditorID = "OBLand" + Sanitize(lt.EditorID ?? obTexture.ID.ToString("X6")),
            TextureSet = set.ToNullableLink<ITextureSetGetter>(),
            MaterialType = new FormLinkNullable<IMaterialTypeGetter>(),
            HavokFriction = 30,
            HavokRestitution = 30,
            TextureSpecularExponent = (byte)(lt.TextureSpecularExponent ?? 30),
        };
        // Its grass (user, 2026-10-11: "check other games for the same things" - the converted Imperial City's land grew
        // none: its land textures came without Oblivion's grass).
        foreach (var g in lt.PotentialGrass ?? [])
            if (ConvertGrass(g.FormKey) is { } grass) skyLt.Grasses.Add(grass.ToLink<IGrassGetter>());
        mod.LandscapeTextures.Add(skyLt);
        return landTextures[obTexture] = skyLt;
    }

    readonly Dictionary<FormKey, Grass?> grasses = new();

    /// <summary>An Oblivion grass (GRAS) as Skyrim's: its model converted, density, slope limits, distance from water,
    /// position, height and colour ranges, wave period and flags (the same bits) as they are.</summary>
    Grass? ConvertGrass(FormKey obGrass)
    {
        if (grasses.TryGetValue(obGrass, out var hit)) return hit;
        grasses[obGrass] = null;
        if (records.GetValueOrDefault(obGrass) is not Ob.IGrassGetter g || g.Model?.File is not { Length: > 0 } file || g.Data is not { } d) return null;
        var mesh = Mesh(file);
        if (mesh is null) return null;
        var grass = new Grass(mod)
        {
            EditorID = "OBGrass" + Sanitize(g.EditorID ?? obGrass.ID.ToString("X6")),
            ObjectBounds = Bounds(mesh),
            Model = new Model { File = SkyrimModelPath(mesh.Path) },
            Density = d.Density,
            MinSlope = d.MinSlope,
            MaxSlope = d.MaxSlope,
            UnitsFromWater = d.UnitFromWaterAmount,
            UnitsFromWaterType = (Grass.UnitsFromWaterTypeEnum)(int)d.UnitFromWaterMode,
            PositionRange = d.PositionRange,
            HeightRange = d.HeightRange,
            ColorRange = d.ColorRange,
            WavePeriod = d.WavePeriod,
            Flags = (Grass.Flag)(int)d.Flags,
        };
        mod.Grasses.Add(grass);
        return grasses[obGrass] = grass;
    }

    /// <summary>An Oblivion LAND as a Skyrim one: the raw height, normal and colour grids (same layout) and the texture
    /// layers (alpha entries: vertex index, opacity).</summary>
    Landscape? ConvertLand(Ob.ILandscapeGetter land, int cx, int cy)
    {
        if (land.VertexHeightMap is not { } vhgt || vhgt.Length < 4 + 33 * 33) return null;
        var bytes = vhgt.ToArray();
        var heights = new Array2d<sbyte>(new sbyte[33, 33]);
        for (int y = 0; y < 33; y++)
            for (int x = 0; x < 33; x++)
                heights[x, y] = (sbyte)bytes[4 + y * 33 + x];
        var l = new Landscape(mod)
        {
            Flags = (Landscape.Flag)(0x1D | (land.VertexColors is not null ? 0x2 : 0)),
            VertexHeightMap = new LandscapeVertexHeightMap { Offset = BitConverter.ToSingle(bytes, 0), HeightMap = heights },
            VertexNormals = new Array2d<P3UInt8>(new P3UInt8[33, 33]),
        };
        if (land.VertexNormals is { } vn && vn.Length >= 33 * 33 * 3)
        {
            var n = vn.ToArray();
            for (int y = 0; y < 33; y++)
                for (int x = 0; x < 33; x++)
                    l.VertexNormals[x, y] = new P3UInt8(n[(y * 33 + x) * 3], n[(y * 33 + x) * 3 + 1], n[(y * 33 + x) * 3 + 2]);
        }
        if (land.VertexColors is { } vc && vc.Length >= 33 * 33 * 3)
        {
            var c = vc.ToArray();
            l.VertexColors = new Array2d<P3UInt8>(new P3UInt8[33, 33]);
            for (int y = 0; y < 33; y++)
                for (int x = 0; x < 33; x++)
                    l.VertexColors[x, y] = new P3UInt8(c[(y * 33 + x) * 3], c[(y * 33 + x) * 3 + 1], c[(y * 33 + x) * 3 + 2]);
        }
        AddLayers(l, land, cx, cy);
        l.IsCompressed = true;
        return l;
    }

    /// <summary>A land quadrant's grounds as Oblivion has them: its base (null key: none, the default ground) and its
    /// alpha layers in their order, each with its strength at the quadrant's 17x17 vertices. A texture laid twice is
    /// one ground (its strengths added); a texture without a file is left out.</summary>
    sealed class LandQuad
    {
        public FormKey Base = FormKey.Null;
        public List<(FormKey Texture, float[] Opacity)> Alphas = [];
        /// <summary>Each ground's share of each vertex: the alpha layers' strengths, the base the rest.</summary>
        public Dictionary<FormKey, float[]> Shares()
        {
            var shares = new Dictionary<FormKey, float[]>();
            var rest = new float[289];
            Array.Fill(rest, 1f);
            foreach (var (t, o) in Alphas)
            {
                if (t == Base) continue;   // the base's own texture laid over itself
                shares[t] = o;
                for (int i = 0; i < 289; i++) rest[i] -= o[i];
            }
            for (int i = 0; i < 289; i++) rest[i] = Math.Max(0, rest[i]);
            shares[Base] = rest;
            return shares;
        }
    }

    LandQuad?[] LandQuads(Ob.ILandscapeGetter land)
    {
        var quads = new LandQuad?[4];
        foreach (var layer in land.Layers)
        {
            // (Every layer of Oblivion's land has its header: one without names no quadrant, as one out of range.)
            if (layer.Header is not { } header) continue;
            int q = (int)header.Quadrant;
            if (q < 0 || q > 3) continue;
            var key = header.Texture.FormKey;
            bool laid = LandTexture(key) is not null;
            var quad = quads[q] ??= new LandQuad();
            if (layer is Ob.IAlphaLayerGetter alpha)
            {
                if (!laid || alpha.AlphaLayerData is not { } data) continue;
                int found = quad.Alphas.FindIndex(a => a.Texture == key);
                var o = found >= 0 ? quad.Alphas[found].Opacity : new float[289];
                if (found < 0) quad.Alphas.Add((key, o));
                var d = data.ToArray();
                for (int i = 0; i + 8 <= d.Length; i += 8)
                    if (BitConverter.ToUInt16(d, i) is var pos && pos < 289) o[pos] = Math.Min(1f, o[pos] + BitConverter.ToSingle(d, i + 4));
            }
            else if (laid) quad.Base = key;
        }
        return quads;
    }

    /// <summary>The grounds each world quadrant of the worldspace being built lays (six at most), with how much of the
    /// quadrant each covers; by world quadrant (two a cell each way).</summary>
    readonly Dictionary<(int, int), Dictionary<FormKey, float>> landKept = [];
    /// <summary>The grounds each world quadrant holds past its six, which it cannot lay.</summary>
    readonly Dictionary<(int, int), HashSet<FormKey>> landDropped = [];
    public int LandCrowdedQuadrants { get; private set; }
    public int LandVerticesRestood { get; private set; }
    public int LandNothingShared { get; private set; }

    /// <summary>Before a worldspace's lands are converted: which six grounds each of its quadrants keeps.</summary>
    void PrepareLands(IEnumerable<(Ob.ILandscapeGetter Land, int X, int Y)> lands)
    {
        landKept.Clear();
        landDropped.Clear();
        foreach (var (land, x, y) in lands)
        {
            var quads = LandQuads(land);
            for (int q = 0; q < 4; q++)
            {
                if (quads[q] is not { } quad) continue;
                var totals = quad.Shares().ToDictionary(kv => kv.Key, kv => kv.Value.Sum()).Where(kv => kv.Value > 0.004f || kv.Key == quad.Base)
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.ToString(), StringComparer.Ordinal).ToList();
                if (totals.Count > 6) LandCrowdedQuadrants++;
                landKept[(x * 2 + (q & 1), y * 2 + (q >> 1))] = totals.Take(6).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (totals.Count > 6) landDropped[(x * 2 + (q & 1), y * 2 + (q >> 1))] = [.. totals.Skip(6).Select(kv => kv.Key)];
            }
        }
    }

    /// <summary>
    /// A land's texture layers. Skyrim lays six textures a quadrant where Oblivion lays nine, and 28,003 of Cyrodiil's
    /// 82,628 quadrants hold more than six (measured on the converted plugin, 2026-11-12): the layers past the sixth
    /// were lost, and what one quadrant lost its neighbour might keep, a straight seam along their edge. A quadrant
    /// keeps its six commonest grounds; a vertex holding a ground that a quadrant it lies on does not lay (its own,
    /// or on an edge a neighbour's) gives that ground's share to its others, or, having no other, takes the commonest
    /// ground all those quadrants lay; so both sides of an edge draw the same ground. Every other vertex keeps
    /// Oblivion's own strengths.
    /// </summary>
    void AddLayers(Landscape l, Ob.ILandscapeGetter land, int cx, int cy)
    {
        var quads = LandQuads(land);
        for (int q = 0; q < 4; q++)
        {
            if (quads[q] is not { } quad) continue;
            int qx = cx * 2 + (q & 1), qy = cy * 2 + (q >> 1);
            if (!landKept.TryGetValue((qx, qy), out var kept)) continue;
            var shares = quad.Shares();
            // The base: none where Oblivion has none (the default ground cannot be a layer); else Oblivion's own if
            // kept, else the commonest kept.
            FormKey baseKey = kept.ContainsKey(FormKey.Null) && shares.ContainsKey(FormKey.Null) ? FormKey.Null
                : kept.ContainsKey(quad.Base) ? quad.Base : kept.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.ToString(), StringComparer.Ordinal).First().Key;
            var order = quad.Alphas.Select(a => a.Texture).Where(t => t != baseKey && kept.ContainsKey(t)).ToList();
            if (quad.Base != baseKey && kept.ContainsKey(quad.Base) && !quad.Base.IsNull) order.Insert(0, quad.Base);
            var written = order.ToDictionary(t => t, _ => new float[289]);
            var here = new Dictionary<FormKey, float>();
            for (int ly = 0; ly < 17; ly++)
                for (int lx = 0; lx < 17; lx++)
                {
                    int i = ly * 17 + lx;
                    // A ground is lost to this vertex when this quadrant does not keep it, or a quadrant the vertex
                    // also lies on holds it past its six. (Not when a neighbour simply never laid it: Oblivion's own
                    // lands differ along many edges, and those are left as they are; counting them, the first build
                    // changed 389,341 vertices, 131,806 of them with no ground in common.)
                    var sharing = new List<Dictionary<FormKey, float>> { kept };
                    var lost = new List<HashSet<FormKey>>();
                    for (int oy = ly == 0 ? -1 : 0; oy <= (ly == 16 ? 1 : 0); oy++)
                        for (int ox = lx == 0 ? -1 : 0; ox <= (lx == 16 ? 1 : 0); ox++)
                            if ((ox != 0 || oy != 0) && landDropped.TryGetValue((qx + ox, qy + oy), out var gone))
                            { lost.Add(gone); if (landKept.TryGetValue((qx + ox, qy + oy), out var theirs)) sharing.Add(theirs); }
                    bool Allowed(FormKey t) { if (!kept.ContainsKey(t)) return false; foreach (var g in lost) if (g.Contains(t)) return false; return true; }
                    here.Clear();
                    float sum = 0, held = 0;
                    bool whole = baseKey == quad.Base;
                    foreach (var (t, o) in shares)
                    {
                        if (o[i] <= 0.004f) continue;
                        sum += o[i];
                        if (Allowed(t)) { here[t] = o[i]; held += o[i]; } else whole = false;
                    }
                    if (whole)
                    {
                        // Nothing of this vertex is lost: Oblivion's own strengths.
                        foreach (var (t, o) in quad.Alphas) if (written.TryGetValue(t, out var w)) w[i] = o[i];
                        continue;
                    }
                    if (held > 0) { foreach (var (t, v) in here) if (written.TryGetValue(t, out var w)) w[i] = v / held; }
                    else
                    {
                        var common = kept.Keys.Where(Allowed).ToList();
                        if (common.Count == 0) { common = [.. kept.Keys]; LandNothingShared++; }
                        var stand = common.OrderByDescending(t => sharing.Sum(s => s.GetValueOrDefault(t))).ThenBy(t => t.ToString(), StringComparer.Ordinal).First();
                        if (written.TryGetValue(stand, out var w)) w[i] = 1;
                    }
                    LandVerticesRestood++;
                }
            if (!baseKey.IsNull) l.Layers.Add(new BaseLayer { Header = new LayerHeader { Texture = LandTexture(baseKey)!.ToLink<ILandscapeTextureGetter>(), Quadrant = (Quadrant)q } });
            ushort number = 0;
            foreach (var t in order)
            {
                var al = new AlphaLayer { Header = new LayerHeader { Texture = LandTexture(t)!.ToLink<ILandscapeTextureGetter>(), Quadrant = (Quadrant)q, LayerNumber = number }, AlphaLayerData = [] };
                var w = written[t];
                for (int i = 0; i < 289; i++)
                    if (w[i] > 0.004f) al.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)i, Opacity = Math.Min(w[i], 1f) });
                if (al.AlphaLayerData.Count > 0) { number++; l.Layers.Add(al); }
            }
        }
    }

    // ---- worldspaces ----

    /// <summary>Every worldspace, parents first, with its land and references (with <paramref name="only"/>, those whose
    /// EditorID it matches; with <paramref name="navBox"/>, x0,y0,x1,y1 in Skyrim cells, navmeshes only for the cells
    /// in it, the rest there as their neighbours: test builds).</summary>
    public (int Worldspaces, int Cells) BuildWorldspaces(System.Text.RegularExpressions.Regex? only = null, (int X0, int Y0, int X1, int Y1)? navBox = null)
    {
        var tamriel = SkyrimEsm.Worldspaces.First(w => w.EditorID == "Tamriel");
        var byWorld = Merge().Where(m => m.World is not null).GroupBy(m => m.World!.FormKey).ToDictionary(g => g.Key, g => g.ToList());
        var sources = plugins.SelectMany(p => p.Worldspaces).GroupBy(w => w.FormKey).ToDictionary(g => g.Key, g => g.Last());
        // TM_OB_WATER=1 (dev aid): each worldspace's cells, those Oblivion marks as having water and at what heights.
        if (Environment.GetEnvironmentVariable("TM_OB_WATER") == "1")
            foreach (var (wkey, wsrc) in sources)
            {
                var headers = (byWorld.GetValueOrDefault(wkey) ?? []).Where(m => !m.IsPersistentCell && m.Header.Grid is not null).ToList();
                var wetCells = headers.Where(m => m.Header.Flags is { } f && f.HasFlag(Ob.Cell.Flag.HasWater)).ToList();
                var heights = wetCells.GroupBy(m => m.Header.WaterHeight?.ToString("F0") ?? "none").OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}");
                Console.WriteLine($"  water {wsrc.EditorID}: {headers.Count} cells, {wetCells.Count} with water in Oblivion (heights {string.Join(", ", heights)}); worldspace water {wsrc.Water.FormKey}, its flags {wsrc.Flags}");
            }
        int cellCount = 0;
        // Every worldspace's navmeshes made together once all are built (NavmeshBuilder.GenerateExteriorCells), then
        // written in the order built.
        var navJobs = new List<Skyrim.Navmesh.NavmeshBuilder.ExteriorJob>();
        void Build(FormKey key)
        {
            if (worldspaces.ContainsKey(key) || !sources.TryGetValue(key, out var src)) return;
            if (src.Parent.FormKey is { IsNull: false } parentKey) Build(parentKey);
            var cells = new Dictionary<(int X, int Y), Cell>();
            var cellRegions = new List<(Cell Cell, IEnumerable<FormKey> Regions)>();
            // By the cell itself: a record's hash is of its contents, which change as its land and references are added,
            // and looked up afterwards most cells weren't found (2026-10-20: Tamriel's navmesh had 872,250 triangles
            // wholly under water and none flagged Water).
            var water = new Dictionary<Cell, float>(ReferenceEqualityComparer.Instance);
            var top = new Cell(mod) { Flags = tamriel.TopCell?.Flags ?? 0, SkyrimMajorRecordFlags = tamriel.TopCell?.SkyrimMajorRecordFlags ?? 0, Persistent = [], Temporary = [] };
            Cell CellAt((int X, int Y) g) => cells.TryGetValue(g, out var c) ? c : cells[g] = new Cell(mod) { Grid = new CellGrid { Point = new P2Int(g.X, g.Y) }, Flags = Cell.Flag.HasWater, Persistent = [], Temporary = [] };
            PrepareLands((byWorld.GetValueOrDefault(key) ?? []).Where(m => !m.IsPersistentCell && m.Header.Grid is not null && m.Land is not null).Select(m => (m.Land!, m.Header.Grid!.Value.X, m.Header.Grid!.Value.Y)));
            foreach (var m in byWorld.GetValueOrDefault(key) ?? [])
            {
                Cell? cell = null;
                if (!m.IsPersistentCell && m.Header.Grid is { } grid)
                {
                    cell = CellAt((grid.X, grid.Y));
                    if (m.Land is { } land && ConvertLand(land, grid.X, grid.Y) is { } converted) cell.Landscape = converted;
                    if (m.Header.WaterHeight is { } wh) cell.WaterHeight = wh;
                    if (m.Header.Flags is { } hf && hf.HasFlag(Ob.Cell.Flag.HasWater)) water[cell] = m.Header.WaterHeight ?? 0;
                    if (m.Header.Regions is { Count: > 0 } rs) cellRegions.Add((cell, rs.Select(r => r.FormKey)));
                }
                foreach (var r in m.Refs.Values)
                {
                    var at = r switch { Ob.IPlacedObjectGetter po => po.Location, Ob.IPlacedNpcGetter n => n.Location, Ob.IPlacedCreatureGetter c => c.Location, _ => null };
                    var target = cell ?? (at is { } loc ? CellAt(((int)MathF.Floor(loc.Position.X / 4096), (int)MathF.Floor(loc.Position.Y / 4096))) : null);
                    if (target is null) continue;
                    if (r is Ob.IPlacedObjectGetter o) Place(o, target, top);
                    else PlaceActor(r, target, top);
                }
                PlacePathCuts(m.Header, top);
            }
            if (cells.Count == 0) { cells[(0, 0)] = CellAt((0, 0)); }
            // Its water: lava where Oblivion's hurts (HurtingWater), else Skyrim's.
            var lavaHere = src.Water.IsNull ? null : HurtingWater(src.Water.FormKey);
            int minX = cells.Keys.Min(k => k.X), maxX = cells.Keys.Max(k => k.X), minY = cells.Keys.Min(k => k.Y), maxY = cells.Keys.Max(k => k.Y);
            var ws = new Worldspace(mod)
            {
                EditorID = "OB" + Sanitize(src.EditorID ?? key.ID.ToString("X6")),
                // Oblivion's Tamriel has no name; Skyrim's map shows the world's.
                Name = string.IsNullOrEmpty(src.Name) && src.EditorID == "Tamriel" ? "Cyrodiil" : src.Name,
                Climate = tamriel.Climate.AsNullable(),
                Water = lavaHere is null ? tamriel.Water.AsNullable() : lavaHere.ToNullableLink<IWaterGetter>(),
                LodWater = lavaHere is null ? tamriel.LodWater.AsNullable() : lavaHere.ToNullableLink<IWaterGetter>(),
                LodWaterHeight = 0,
                LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -2048, DefaultWaterHeight = 0 },
                ObjectBoundsMin = new P2Float(minX, minY),
                ObjectBoundsMax = new P2Float(maxX + 1, maxY + 1),
                WorldMapOffsetScale = 1,
                DistantLodMultiplier = 1,
                MapData = new WorldspaceMap
                {
                    NorthwestCellCoords = new P2Int16((short)minX, (short)maxY),
                    SoutheastCellCoords = new P2Int16((short)maxX, (short)minY),
                    CameraMinHeight = tamriel.MapData?.CameraMinHeight ?? 50000,
                    CameraMaxHeight = tamriel.MapData?.CameraMaxHeight ?? 80000,
                    CameraInitialPitch = tamriel.MapData?.CameraInitialPitch ?? 50,
                },
                TopCell = top,
            };
            if (src.Parent.FormKey is { IsNull: false } pk && worldspaces.TryGetValue(pk, out var parent))
                ws.Parent = new WorldspaceParent { Worldspace = parent.ToLink<IWorldspaceGetter>(), Flags = WorldspaceParent.Flag.UseLandData | WorldspaceParent.Flag.UseLodData | WorldspaceParent.Flag.UseMapData | WorldspaceParent.Flag.UseWaterData | WorldspaceParent.Flag.UseClimateData | WorldspaceParent.Flag.UseSkyCell };
            var blocks = new Dictionary<(int, int), WorldspaceBlock>();
            var subBlocks = new Dictionary<(int, int), WorldspaceSubBlock>();
            static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
            foreach (var ((x, y), cell) in cells.OrderBy(c => c.Key.Y).ThenBy(c => c.Key.X))
            {
                var bk = (FloorDiv(x, 32), FloorDiv(y, 32));
                var sk = (FloorDiv(x, 8), FloorDiv(y, 8));
                if (!blocks.TryGetValue(bk, out var block)) { blocks[bk] = block = new WorldspaceBlock { BlockNumberX = (short)bk.Item1, BlockNumberY = (short)bk.Item2, GroupType = GroupTypeEnum.ExteriorCellBlock }; ws.SubCells.Add(block); }
                if (!subBlocks.TryGetValue(sk, out var sub)) { subBlocks[sk] = sub = new WorldspaceSubBlock { BlockNumberX = (short)sk.Item1, BlockNumberY = (short)sk.Item2, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; block.Items.Add(sub); }
                sub.Items.Add(cell);
            }
            mod.Worldspaces.Add(ws);
            worldspaces[key] = ws;
            AddRegions(key, ws, cellRegions);
            navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod) { FloorFit = 8 };
            var loadDoorSet = placedDoors.Values.ToHashSet();
            // A city (a child worldspace: Anvil, Bravil, the Imperial City's districts...): a marker in the middle of
            // its doors, for the dangers of the night in town (TMNightDanger finds OBTownCentres by name).
            if (ws.Parent is not null && top.Persistent.OfType<PlacedObject>().Where(loadDoorSet.Contains).ToList() is { Count: > 0 } cityDoors)
            {
                var centre = new PlacedObject(mod)
                {
                    EditorID = "OBTownCentre" + Sanitize(src.EditorID ?? key.ID.ToString("X6")),
                    Base = Skyrim.TownNights.XMarker.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement
                    {
                        Position = new P3Float(cityDoors.Average(d => d.Placement!.Position.X), cityDoors.Average(d => d.Placement!.Position.Y), cityDoors.Average(d => d.Placement!.Position.Z)),
                        Rotation = new P3Float(0, 0, 0),
                    },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                };
                top.Persistent.Add(centre);
                if (townCentres is null) { townCentres = new FormList(mod) { EditorID = Skyrim.TownNights.TownCentresId("OB") }; mod.FormLists.Add(townCentres); }
                townCentres.Items.Add(centre.ToLink<ISkyrimMajorRecordGetter>());
            }
            // TM_OB_WINDING=1 (dev aid): how the models placed outdoors here are wound, by model and by reference.
            if (Environment.GetEnvironmentVariable("TM_OB_WINDING") == "1")
                foreach (var g in cells.Values.SelectMany(c => c.Temporary.OfType<PlacedObject>()).Where(p => meshByBase.ContainsKey(p.Base.FormKey)).GroupBy(p => p.Base.FormKey))
                {
                    string kind = Skyrim.Navmesh.NavmeshBuilder.WindingKind(meshByBase[g.Key].Solid);
                    var c = windingKinds.GetValueOrDefault(kind);
                    windingKinds[kind] = (c.Models + 1, c.Refs + g.Count());
                    if (kind is not ("closed, outward" or "open, top facing up")) windingOdd.Add((meshByBase[g.Key].Path, g.Count(), $"{kind}: {Skyrim.Navmesh.NavmeshBuilder.Winding(meshByBase[g.Key].Solid)}"));
                }
            // Faces as wound where a model's are wound one way, outward; turned up only where not (measured: TM_OB_WINDING).
            navJobs.Add(navmeshes.BeginExteriorCells(ws, cells,
                (x, y) => cells.TryGetValue((x, y), out var c) ? Solids(c.Temporary.OfType<PlacedObject>(), turnUntrusted: true) : [],
                // Lava is swum as water is (user, 2026-10-20: "lava is swimmable though and its hazard in case you fall in"):
                // its bed navmeshed and flagged Water, so whoever falls in can swim out; the lava itself hurts (HurtingWater).
                c => water.TryGetValue(c, out var w) ? w : null,
                top.Persistent.OfType<PlacedObject>().Where(loadDoorSet.Contains).Cast<IPlacedObjectGetter>().ToList(), Problems, asWound: true,
                only: navBox is { } nb ? (x, y) => x >= nb.X0 && y >= nb.Y0 && x <= nb.X1 && y <= nb.Y1 : null));
            // Distant terrain, for worldspaces that don't use their parent's.
            if (ws.Parent is null)
            {
                terrainLod ??= new Skyrim.TerrainLod(mod, modOutDir, SkyrimData!);
                terrainLod.Build(ws.EditorID!, cells, c => water.TryGetValue(c, out var w) ? w : null, -2048);
            }
            cellCount += cells.Count;
        }
        foreach (var key in sources.Keys)
            if (only is null || only.IsMatch(sources[key].EditorID ?? "")) Build(key);
        Skyrim.Navmesh.NavmeshBuilder.GenerateExteriorCells(navJobs);
        foreach (var job in navJobs)
        {
            var (navCount, navTriangles) = job.Finish();
            ExteriorNavmeshes += navCount;
            NavmeshTriangles += navTriangles;
        }
        if (windingKinds.Count > 0)
        {
            foreach (var (kind, c) in windingKinds.OrderByDescending(k => k.Value.Refs)) Console.WriteLine($"  exterior winding: {kind}: {c.Models} models (counted a worldspace each), {c.Refs} references");
            foreach (var o in windingOdd.OrderByDescending(o => o.Refs).Take(15)) Console.WriteLine($"    {o.Path} x{o.Refs}: {o.Note}");
        }
        ExteriorEdgeLinks = navmeshes?.LinkExteriorEdges() ?? 0;
        if (terrainLod is not null) Problems.AddRange(terrainLod.Problems);
        return (worldspaces.Count, cellCount);
    }

    /// <summary>Interiors whose water is lava (HurtingWater), swum over as water is.</summary>
    public int HazardCells { get; private set; }

    /// <summary>TM_OB_WINDING's counts: by kind, the models and references; the models not wound one way outward.</summary>
    readonly Dictionary<string, (int Models, int Refs)> windingKinds = new();
    readonly List<(string Path, int Refs, string Note)> windingOdd = new();

    FormList? townCentres;
    readonly Dictionary<FormKey, Region> regions = new();
    Dictionary<FormKey, Ob.IWeatherGetter>? obWeathers;
    public int RegionCount => regions.Count;

    /// <summary>
    /// The worldspace's Oblivion regions as Skyrim regions: the same areas (Oblivion's region points are in world
    /// units, as Skyrim's are) and weather chances, each Oblivion weather as its own converted record (ObWeather:
    /// Oblivion's colours, fog, clouds, sounds), the nearest Skyrim one by name only where Oblivion has no data; the
    /// Oblivion planes' storms are Helgen's dragon-attack sky. Each cell lists its regions, as in Oblivion. Based on a
    /// vanilla region record so its header is valid.
    /// </summary>
    void AddRegions(FormKey world, Worldspace ws, List<(Cell Cell, IEnumerable<FormKey> Regions)> cellRegions)
    {
        obWeathers ??= plugins.SelectMany(p => p.Weathers).GroupBy(w => w.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var template = SkyrimEsm.Regions.First(r => r.Weather is { Weathers.Count: > 0 });
        foreach (var src in plugins.SelectMany(p => p.Regions).GroupBy(r => r.FormKey).Select(g => g.Last()).Where(r => r.Worldspace.FormKey == world))
        {
            var region = mod.Regions.DuplicateInAsNewRecord(template);
            region.EditorID = "OB" + Sanitize(src.EditorID ?? src.FormKey.ID.ToString("X6"));
            region.Worldspace = ws.ToNullableLink<IWorldspaceGetter>();
            region.RegionAreas.Clear();
            region.Objects = null;
            region.Grasses = null;
            region.Sounds = null;
            region.Land = null;
            region.Map = null;
            if (src.MapColor is { } color) region.MapColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
            region.Weather!.Weathers = [];
            foreach (var w in src.Weather?.Weathers ?? [])
            {
                // Oblivion's own weather record (ObWeather); the vanilla look-alike by name only where it has no data.
                if (w.Chance <= 0 || (ObWeather(w.Weather.FormKey)?.FormKey ?? WeatherFor(w.Weather.FormKey)) is not { } sky) continue;
                var existing = region.Weather.Weathers.FirstOrDefault(x => x.Weather.FormKey == sky);
                if (existing is not null) existing.Chance += w.Chance;
                else region.Weather.Weathers.Add(new WeatherType { Weather = new FormLink<IWeatherGetter>(sky), Chance = w.Chance });
            }
            if (region.Weather.Weathers.Count == 0) region.Weather = null;
            foreach (var area in src.Areas)
            {
                // (An area without points has fewer than three: left out, as one too small.)
                var points = (area.RegionPoints ?? []).Select(p => new P2Float(p.X, p.Y)).ToList();
                if (points.Count >= 3) region.RegionAreas.Add(new RegionArea { EdgeFallOff = area.EdgeFallOff ?? 0, RegionPointListData = [.. points] });
            }
            regions[src.FormKey] = region;
        }
        foreach (var (cell, keys) in cellRegions)
            foreach (var k in keys)
                if (regions.TryGetValue(k, out var region))
                {
                    cell.Regions ??= [];
                    if (!cell.Regions.Any(r => r.FormKey == region.FormKey)) cell.Regions.Add(region.ToLink<IRegionGetter>());
                }
    }

    static readonly ModKey SkyrimKey = ModKey.FromFileName("Skyrim.esm"), DragonbornKey = ModKey.FromFileName("Dragonborn.esm");

    /// <summary>The fallback: a vanilla weather chosen by the Oblivion weather's name, for one with no record data.</summary>
    FormKey? WeatherFor(FormKey obWeather)
    {
        string id = obWeathers!.GetValueOrDefault(obWeather)?.EditorID ?? "";
        bool has(string s) => id.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (has("Oblivion") || has("Sigil") || has("Gloom") || has("Summoning")) return Vanilla.Get<IWeatherGetter>("HelgenAttackWeather")?.FormKey ?? new FormKey(SkyrimKey, 0x0C8220);
        if (has("Ash")) return new FormKey(DragonbornKey, 0x032336);              // DLC02VolcanicAshStorm01
        if (has("Thunder") || has("Storm")) return new FormKey(SkyrimKey, 0x0C8220);  // SkyrimStormRain
        if (has("Rain")) return new FormKey(SkyrimKey, 0x0C821F);                 // SkyrimOvercastRain
        if (has("Snow")) return new FormKey(SkyrimKey, 0x04D7FB);                 // SkyrimOvercastSnow
        if (has("Fog")) return new FormKey(SkyrimKey, 0x0C821E);                  // SkyrimFog
        if (has("Cloudy") || has("Overcast")) return new FormKey(SkyrimKey, 0x012F89);  // SkyrimCloudy
        return new FormKey(SkyrimKey, 0x00081A);                                  // SkyrimClear
    }

    Skyrim.Navmesh.NavmeshBuilder? navmeshes;
    public int Navmeshes { get; private set; }
    public int NavmeshTriangles { get; private set; }

    /// <summary>
    /// An interior's navmesh (Recast over the collision of its placed statics and load doors), so NPCs and creatures
    /// can walk; load doors are linked and the areas around actors kept. Items, swinging doors and actors aren't part
    /// of it, so it runs through doorways.
    /// </summary>
    Skyrim.TerrainLod? terrainLod;
    public int TerrainLodChunks => terrainLod?.Chunks ?? 0;

    public int ExteriorNavmeshes { get; private set; }
    public int ExteriorEdgeLinks { get; private set; }

    /// <summary>The collision of placed objects as world-space triangles (scaled, rotated X, Y then Z clockwise, moved).
    /// With <paramref name="turnUntrusted"/>, the faces of a model whose winding can't be trusted
    /// (<see cref="Skyrim.Navmesh.NavmeshBuilder.TrustedWinding"/>) are each turned up, the rest left as wound.</summary>
    IEnumerable<System.Numerics.Vector3[]> Solids(IEnumerable<PlacedObject> placed, bool turnUntrusted = false)
    {
        foreach (var p in placed)
        {
            if (p.Placement is not { } pl || !meshByBase.TryGetValue(p.Base.FormKey, out var mesh)) continue;
            // What gives way isn't in the way: a wall, gate or bridge a script moves aside (an activator whose model
            // has a move from one state to another: "Forward", "Open") is walked through once moved, as a door is, and
            // Oblivion's path grid runs through it. With its collision in the navmesh's input the navmesh ended at it
            // for good: the Emperor's party had no way through the prison's secret wall after Captain Renault opened it
            // (seen in game 2026-11-14: the opening held at stage 19, the party turned back to the cell gate).
            if (GivesWay(p.Base.FormKey, mesh)) { Interlocked.Increment(ref SolidsGivingWay); continue; }
            float sc = p.Scale ?? 1;
            var m = TamrielMerge.Skyrim.SkyrimRotation.Matrix(pl.Rotation);
            var pos = new System.Numerics.Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
            bool turn = turnUntrusted && !Trusted(p.Base.FormKey, mesh);
            foreach (var t in mesh.Solid)
            {
                var w = t.Select(v => System.Numerics.Vector3.Transform(v * sc, m) + pos).ToArray();
                yield return turn && System.Numerics.Vector3.Cross(w[1] - w[0], w[2] - w[0]).Z < 0 ? [w[0], w[2], w[1]] : w;
            }
        }
    }

    /// <summary>Placed things left out of the navmesh's input because they give way (<see cref="GivesWay"/>).</summary>
    public int SolidsGivingWay;

    /// <summary>Whether a placed thing is moved aside by a script: an activator (or a moving static) whose model has
    /// a sequence that ends (not a loop: a wheel turning stays where it is).</summary>
    bool GivesWay(FormKey baseKey, ObMeshConverter.Result mesh) =>
        (mod.Activators.ContainsKey(baseKey) || mod.MoveableStatics.ContainsKey(baseKey)) && mesh.Sequences.Any(q => !q.Loop);

    /// <summary>Each base object's collision: whether its winding can be trusted (worked out once).</summary>
    readonly Dictionary<FormKey, bool> trustedWinding = new();
    bool Trusted(FormKey key, ObMeshConverter.Result mesh)
    {
        lock (trustedWinding)
            return trustedWinding.TryGetValue(key, out bool known) ? known : trustedWinding[key] = Skyrim.Navmesh.NavmeshBuilder.TrustedWinding(mesh.Solid);
    }

    /// <summary>Interior actors and load doors checked against their navmesh, and those within 64 units of it.</summary>
    public int ActorsChecked, ActorsOnNavmesh, DoorsChecked, DoorsOnNavmesh;

    /// <summary>Interiors given an invisible floor (<see cref="InvisibleFloor"/>), with how.</summary>
    public List<string> FlooredInteriors { get; } = [];

    /// <summary>The floor for an interior Oblivion left with nothing to walk on (<see cref="Skyrim.Navmesh.InvisibleFloors"/>): from
    /// its pathgrid, else from what stands in it (TestGrantWarehouseUpper's 167 statics are all candles, books and plates).</summary>
    List<(System.Numerics.Vector3[] Points, System.Numerics.Vector3 Normal)> InvisibleFloor(Cell cell, Ob.ICellGetter? source, out string how, bool wide = false)
    {
        // The cell's pathgrid, if it has one.
        var grid = source?.PathGrid?.PointToPointConnections?.ToList() ?? [];
        var points = grid.Select(g => new System.Numerics.Vector3(g.Point.X, g.Point.Y, g.Point.Z)).ToList();
        var links = new List<(int, int)>();
        for (int i = 0; i < grid.Count; i++)
            foreach (var j in grid[i].Connections.Select(c => (int)c).Where(j => j > i)) links.Add((i, j));
        // What stands in it and everything else, by its bottom: actors at their feet, objects at their model's bottom.
        var stands = new List<System.Numerics.Vector3>();
        var rests = new List<System.Numerics.Vector3>();
        foreach (var n in cell.Temporary.Concat(cell.Persistent).OfType<PlacedNpc>())
            if (n.Placement is { } np) stands.Add(new(np.Position.X, np.Position.Y, np.Position.Z));
        foreach (var o in cell.Temporary.Concat(cell.Persistent).OfType<PlacedObject>())
        {
            if (o.Placement is not { } op || !meshByBase.TryGetValue(o.Base.FormKey, out var mesh)) continue;
            float s = o.Scale ?? 1;
            var bottom = new System.Numerics.Vector3(op.Position.X, op.Position.Y, op.Position.Z + mesh.Min.Z * s);
            // Its kind by the plugin's groups (each a lookup; searching every record for each object was most of a
            // floored interior's time).
            var bk = o.Base.FormKey;
            bool stand = mod.Doors.ContainsKey(bk) || mod.Furniture.ContainsKey(bk) || mod.Containers.ContainsKey(bk) || (mesh.Max.Z - mesh.Min.Z) * s >= 40;
            (stand ? stands : rests).Add(bottom);
        }
        return Skyrim.Navmesh.InvisibleFloors.Plan(points, links, stands, rests, out how, wide);
    }

    void PlaceInvisibleFloor(Cell cell, List<(System.Numerics.Vector3[] Points, System.Numerics.Vector3 Normal)> floor) =>
        Skyrim.Navmesh.InvisibleFloors.Place(mod, modOutDir, "ob", "OB", cell, floor);

    void BuildNavmesh(Cell cell, Ob.ICellGetter? source = null)
    {
        if (BeginNavmesh(cell, source, background: false) is { } job) FinishNavmesh(job);
    }

    /// <summary>An interior's navmesh begun (<see cref="BeginNavmesh"/>): what its finish needs.</summary>
    sealed class NavJob
    {
        public required Cell Cell;
        public Ob.ICellGetter? Source;
        public required List<(System.Numerics.Vector3[] Points, System.Numerics.Vector3 Normal)> Solid, TurnedUp;
        public int SolidOwn;
        public bool Floored;
        public required List<IPlacedObjectGetter> LoadDoors;
        public required List<P3Float> Actors;
        public Func<P3Float, bool>? Underwater;
        public Skyrim.Navmesh.NavmeshBuilder.PendingInterior? Pending;
    }

    /// <summary>
    /// An interior's navmesh begun from its collision (its own invisible floor first when it has none): with
    /// <paramref name="background"/> made on a worker thread while the next interiors are built (BuildAllInteriors), the
    /// cell left alone until <see cref="FinishNavmesh"/>. Null when there is nothing to make one on.
    /// </summary>
    NavJob? BeginNavmesh(Cell cell, Ob.ICellGetter? source, bool background)
    {
        var solid = new List<(System.Numerics.Vector3[] Points, System.Numerics.Vector3 Normal)>();
        var turnedUp = new List<(System.Numerics.Vector3[] Points, System.Numerics.Vector3 Normal)>();
        foreach (var w in Solids(cell.Temporary.OfType<PlacedObject>().Concat(cell.Persistent.OfType<PlacedObject>())))
        {
            var n = System.Numerics.Vector3.Cross(w[1] - w[0], w[2] - w[0]);
            if (n.LengthSquared() < 1e-6f) continue;
            n = System.Numerics.Vector3.Normalize(n);
            // Each face as wound: a ceiling or an underside is an obstacle, not a floor. Oblivion's collision is wound
            // consistently (2026-10-18: Weatherleah's 35 models, 5,870 edges shared oppositely, none the same way, every
            // closed model's volume positive; the Shivering Isles' Root pieces once their packed triangles face their
            // stored normals, 30,980 to 65). Turned up, every underside under enough headroom was a floor: Fain's tower
            // stacked 68 of them in one column, past Recast's 62, and got no navmesh. Every face turned up only where
            // that puts more actors on the navmesh (NavmeshBuilder.BuildInterior).
            solid.Add((w, n));
            turnedUp.Add((w, n.Z < 0 ? -n : n));
        }
        // TM_OB_PROBE=<EditorID>,x,y: every collision triangle over that point, its height there, which way it faces as
        // wound, and the object it is of.
        if (Environment.GetEnvironmentVariable("TM_OB_PROBE")?.Split(',') is [var pc, var px, var py] && pc == cell.EditorID && float.TryParse(px, out var qx) && float.TryParse(py, out var qy))
            foreach (var placed in cell.Temporary.OfType<PlacedObject>().Concat(cell.Persistent.OfType<PlacedObject>()))
                foreach (var w in Solids([placed]))
                {
                    float d = (w[1].Y - w[2].Y) * (w[0].X - w[2].X) + (w[2].X - w[1].X) * (w[0].Y - w[2].Y);
                    if (MathF.Abs(d) < 1e-6f) continue;
                    float a = ((w[1].Y - w[2].Y) * (qx - w[2].X) + (w[2].X - w[1].X) * (qy - w[2].Y)) / d, b = ((w[2].Y - w[0].Y) * (qx - w[2].X) + (w[0].X - w[2].X) * (qy - w[2].Y)) / d;
                    if (a < 0 || b < 0 || a + b > 1) continue;
                    var nz = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(w[1] - w[0], w[2] - w[0])).Z;
                    var rec = mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == placed.Base.FormKey);
                    Console.WriteLine($"  probe {cell.EditorID} {qx},{qy}: height {a * w[0].Z + b * w[1].Z + (1 - a - b) * w[2].Z:F0}, normal z {nz:F2}, {rec?.EditorID} {(rec as IModeledGetter)?.Model?.File}");
                }
        // TM_OB_SOLIDS=<EditorID>[,<EditorID>...]: what each placed object gives that cell's navmesh (triangles, those facing up).
        if (Environment.GetEnvironmentVariable("TM_OB_SOLIDS") is { Length: > 0 } traced && traced.Split(',').Contains(cell.EditorID))
            foreach (var g in cell.Temporary.OfType<PlacedObject>().Concat(cell.Persistent.OfType<PlacedObject>()).GroupBy(p => p.Base.FormKey))
            {
                var tris = Solids(g).ToList();
                int up = tris.Count(w => System.Numerics.Vector3.Cross(w[1] - w[0], w[2] - w[0]) is var c && c.LengthSquared() > 1e-6f && System.Numerics.Vector3.Normalize(c).Z > 0.64f);
                var rec = mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == g.Key);
                string name = rec is null ? g.Key.ToString() : $"{rec.GetType().Name} {rec.EditorID} {(rec as IModeledGetter)?.Model?.File}";
                Console.WriteLine($"  solids {cell.EditorID}: {g.Count()} x {name}: {(meshByBase.TryGetValue(g.Key, out var mb) ? $"{tris.Count} triangles, {up} facing up; winding: {Skyrim.Navmesh.NavmeshBuilder.Winding(mb.Solid)}" : "no collision mesh")}");
            }
        bool floored = false;
        var solidOwn = solid.Count;
        if (solid.Count == 0)
        {
            // No collision at all in it (Oblivion's TestCastleInterior: a ladder and a trapdoor in the air): its own floor.
            var floor0 = InvisibleFloor(cell, source, out string how0);
            if (floor0.Count == 0) return null;
            PlaceInvisibleFloor(cell, floor0);
            solid.AddRange(floor0);
            turnedUp.AddRange(floor0);
            FlooredInteriors.Add($"{cell.EditorID} ({how0})");
            floored = true;
        }
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod) { FloorFit = 8 };
        var job = new NavJob
        {
            Cell = cell, Source = source, Solid = solid, TurnedUp = turnedUp, SolidOwn = solidOwn, Floored = floored,
            LoadDoors = cell.Persistent.OfType<PlacedObject>().Where(d => d != StartMarker && placedDoors.ContainsValue(d)).Cast<IPlacedObjectGetter>().ToList(),
            Actors = cell.Temporary.OfType<PlacedNpc>().Select(n => n.Placement!.Position).ToList(),
        };
        // The cell's water, lava (HurtingWater) as any other: swum over, the ground wholly under it flagged Water, so
        // whoever falls into lava can swim out (the lava hurts).
        float? level = cell.Flags.HasFlag(Cell.Flag.HasWater) ? cell.WaterHeight ?? 0 : null;
        if (level is not null && !cell.Water.IsNull && hurtingWaters.Values.Any(w => w is not null && w.FormKey == cell.Water.FormKey)) HazardCells++;
        job.Underwater = level is { } wl ? p => p.Z < wl : null;
        job.Pending = navmeshes.BeginInterior(cell, solid, turnedUp, job.LoadDoors, cell.Persistent.Contains(StartMarker) ? StartMarker.Placement!.Position : null,
            job.Actors, null, job.Underwater, background, Doorways(cell));
        return job;
    }

    /// <summary>
    /// The doorways of an interior's inner doors (<see cref="Skyrim.Navmesh.NavmeshBuilder.Doorway"/>): each door's
    /// bounds where it stands, across its width as they are (and a little: the frame stops the floor anyway) and a step
    /// through it on either side (the agent's radius and a little), from its foot down to a stair's climb below and up
    /// to a knee above. Skyrim's people go through an inner door only when the navmesh under it is as wide as it.
    /// </summary>
    List<Skyrim.Navmesh.NavmeshBuilder.Doorway> Doorways(Cell cell)
    {
        const float Across = 8, Through = 32, Below = 64, Above = 48;
        var list = new List<Skyrim.Navmesh.NavmeshBuilder.Doorway>();
        foreach (var p in cell.Persistent.Concat(cell.Temporary).OfType<PlacedObject>())
        {
            if (!innerDoors.ContainsKey(p.FormKey) || p.Placement is not { } pl || !mod.Doors.TryGetValue(p.Base.FormKey, out var door) || door.ObjectBounds is not { } b) continue;
            float sc = p.Scale ?? 1;
            // The door's width runs along its bounds' longer side; through it, the shorter.
            bool wideOnX = b.Second.X - b.First.X >= b.Second.Y - b.First.Y;
            float x0 = b.First.X - (wideOnX ? Across : Through), x1 = b.Second.X + (wideOnX ? Across : Through);
            float y0 = b.First.Y - (wideOnX ? Through : Across), y1 = b.Second.Y + (wideOnX ? Through : Across);
            var m = TamrielMerge.Skyrim.SkyrimRotation.Matrix(pl.Rotation);
            var pos = new System.Numerics.Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
            var corners = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) }
                .Select(c => System.Numerics.Vector3.Transform(new System.Numerics.Vector3(c.Item1, c.Item2, 0) * sc, m) + pos)
                .Select(w => new System.Numerics.Vector2(w.X, w.Y)).ToArray();
            list.Add(new(corners, pl.Position.Z - Below, pl.Position.Z + Above));
        }
        return list;
    }

    /// <summary>An interior's navmesh finished (in the order begun): a floor of its own when it had nothing to walk on, and
    /// who stands on it counted.</summary>
    void FinishNavmesh(NavJob job)
    {
        var (cell, source, solid, turnedUp, solidOwn, floored, loadDoors, actors, underwater) =
            (job.Cell, job.Source, job.Solid, job.TurnedUp, job.SolidOwn, job.Floored, job.LoadDoors, job.Actors, job.Underwater);
        try
        {
            Skyrim.Navmesh.NavmeshBuilder.Result r;
            try
            {
                r = job.Pending!.Finish();
                // A slow one said, with where its time went (2026-10-22: one interior held the build to a core for 31 minutes).
                if (job.Pending.Seconds > 20) Console.WriteLine($"  navmesh {cell.EditorID}: {job.Pending.Seconds:F0} s ({job.Pending.Timing})");
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("no floor to walk on"))
            {
                // Nothing to walk on in it (Oblivion's test cells and leftovers): a floor of its own (a wide one under its
                // pathgrid when the one along it kept nothing), then the navmesh on it.
                Skyrim.Navmesh.NavmeshBuilder.Result? made = null;
                foreach (bool wide in floored ? new[] { true } : [false, true])
                {
                    var floor = InvisibleFloor(cell, source, out string how, wide);
                    if (floor.Count == 0) continue;
                    PlaceInvisibleFloor(cell, floor);
                    solid.RemoveRange(solidOwn, solid.Count - solidOwn);
                    turnedUp.RemoveRange(solidOwn, turnedUp.Count - solidOwn);
                    solid.AddRange(floor);
                    turnedUp.AddRange(floor);
                    FlooredInteriors.RemoveAll(f => f.StartsWith(cell.EditorID + " ("));
                    FlooredInteriors.Add($"{cell.EditorID} ({how})");
                    try
                    {
                        // (Made by BeginNavmesh before this navmesh was begun.)
                        made = navmeshes!.BuildInterior(cell, solid, turnedUp, loadDoors,
                            cell.Persistent.Contains(StartMarker) ? StartMarker.Placement!.Position : null, actors, underwater: underwater);
                        break;
                    }
                    catch (InvalidOperationException again) when (again.Message.StartsWith("no floor to walk on")) { }
                }
                // Still nothing to walk on: the first failure, as it was thrown.
                if (made is null) throw;
                r = made;
            }
            Navmeshes++;
            NavmeshTriangles += r.Triangles;
            // Who stands on it: the cell's actors and load doors within 64 units of a navmesh triangle.
            if (r.Navmesh.Data is { } nd)
                foreach (var (p, door) in actors.Select(a => (a, false)).Concat(loadDoors.Select(d => (d.Placement!.Position, true))))
                {
                    float far = Skyrim.Navmesh.NavmeshBuilder.Distance(nd, p);
                    bool on = far <= 64;
                    if (door) { DoorsChecked++; if (on) DoorsOnNavmesh++; }
                    else { ActorsChecked++; if (on) ActorsOnNavmesh++; }
                }
        }
        catch (Exception e)
        {
            // Where in the converter it failed (the innermost of its own frames).
            string? at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("TamrielMerge."));
            Problems.Add($"navmesh {cell.EditorID}: {e.Message}{(at is null ? "" : $" ({at})")}");
        }
    }

    /// <summary>Load doors: Oblivion names each door's partner, as Skyrim does. Returns (linked, unlinked).</summary>
    public (int Linked, int Unlinked) LinkDoors()
    {
        int linked = 0, unlinked = 0;
        foreach (var (key, placed) in placedDoors)
        {
            var dest = sourceDoors[key].TeleportDestination!;
            if (!placedDoors.TryGetValue(dest.Destination.FormKey, out var partner)) { unlinked++; continue; }
            placed.TeleportDestination = new TeleportDestination { Door = partner.ToLink<IPlacedObjectGetter>(), Position = dest.Position, Rotation = dest.Rotation };
            linked++;
        }
        // Doors linked here and any other without a door triangle get theirs now. (Doors that lead nowhere get none, as
        // in Skyrim.esm: none of its 1,577 inner doors is among its navmeshes' door triangles or the navmesh map's
        // linked doors; the navmesh just runs on under them. Measured with nav-inner-doors, 2026-11-15.)
        DoorTrianglesAdded = navmeshes?.AddMissingDoorTriangles(mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells)) ?? 0;
        return (linked, unlinked);
    }

    /// <summary>Load doors given their door triangle after the navmeshes (<see cref="LinkDoors"/>).</summary>
    public int DoorTrianglesAdded { get; private set; }
}
