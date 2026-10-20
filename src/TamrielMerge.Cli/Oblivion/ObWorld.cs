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

    /// <param name="solid">Whether the navmesh goes around it (a load door; not a door that opens by itself).</param>
    Door? LoadDoor(Ob.IDoorGetter d, string edid, bool solid = true)
    {
        if (d.Model?.File is not { Length: > 0 } file || IsMarker(file)) return null;
        var mesh = Mesh(file);
        if (mesh is null) return null;
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
        float scale = r.Scale ?? 1;
        FormKey baseKey;
        bool isDoor = false;
        switch (rec)
        {
            case Ob.ITreeGetter tree:
                if (TreeFor(tree) is not { } t) return null;
                baseKey = t.Tree; scale *= t.Scale;
                break;
            case Ob.IDoorGetter door when r.TeleportDestination is null && !OpensByItself(door):
                if (SwingDoor(door.FormKey) is not { } swing) return null;
                baseKey = swing.FormKey;
                break;
            case Ob.IDoorGetter door:
                // A load door, or a door whose model opens and closes by its own animation (gates, portcullises,
                // cell doors, secret doors: a Skyrim door plays their Open and Close, as vanilla MineSecretDoor01).
                if (!bases.TryGetValue(door.FormKey, out var known)) bases[door.FormKey] = known = LoadDoor(door, "OB" + Sanitize(door.EditorID ?? door.FormKey.ID.ToString("X6")), solid: r.TeleportDestination is not null);
                if (known is null) return null;
                baseKey = known.FormKey; isDoor = r.TeleportDestination is not null;
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
        ApplyLock(r, placed);
        if (isDoor)
        {
            placed.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
            persistentCell.Persistent.Add(placed);
            placedDoors[r.FormKey] = placed;
            sourceDoors[r.FormKey] = r;
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
            var half = (hi - lo) / 2 * scale;
            var box = new PlacedObject(mod)
            {
                Base = triggerBase.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(centre.X, centre.Y, centre.Z), Rotation = pl.Rotation },
                Primitive = new PlacedPrimitive { Type = PlacedPrimitive.TypeEnum.Box, Bounds = new P3Float(half.X, half.Y, half.Z), Color = System.Drawing.Color.FromArgb(0, 204, 76, 51), Unknown = 0.15f },
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
    public int BuildAllInteriors()
    {
        int n = 0;
        foreach (var m in Merge().Where(m => m.World is null))
        {
            var cell = NewInterior(m.Header);
            foreach (var r in m.Refs.Values)
            {
                if (r is Ob.IPlacedObjectGetter o) Place(o, cell, cell);
                else PlaceActor(r, cell, cell);
            }
            BuildNavmesh(cell);
            AddInteriorCell(cell);
            interiorsBySource[m.Header.FormKey] = cell;
            n++;
        }
        return n;
    }

    Cell NewInterior(Ob.ICellGetter source)
    {
        var cell = new Cell(mod)
        {
            EditorID = "OB" + Sanitize(source.EditorID ?? source.FormKey.ID.ToString("X6")),
            Name = source.Name,
            Flags = Cell.Flag.IsInteriorCell | (source.Flags is { } f && f.HasFlag(Ob.Cell.Flag.HasWater) ? Cell.Flag.HasWater : 0),
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = Lighting(source.Lighting),
            WaterHeight = source.WaterHeight,
            Persistent = [],
            Temporary = [],
        };
        // A water that hurts (lava) as lava that hurts (HurtingWater).
        if (!source.Water.IsNull && HurtingWater(source.Water.FormKey) is { } lava) cell.Water.SetTo(lava);
        return cell;
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
        var diffuse = meshes.Textures.Ensure(file);
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
    Landscape? ConvertLand(Ob.ILandscapeGetter land)
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
        foreach (var layer in land.Layers)
        {
            if (LandTexture(layer.Header.Texture.FormKey) is not { } tex) continue;
            var header = new LayerHeader { Texture = tex.ToLink<ILandscapeTextureGetter>(), Quadrant = (Quadrant)(int)layer.Header.Quadrant, LayerNumber = layer.Header.LayerNumber };
            if (layer is Ob.IAlphaLayerGetter alpha)
            {
                var al = new AlphaLayer { Header = header, AlphaLayerData = [] };
                if (alpha.AlphaLayerData is { } data)
                {
                    var d = data.ToArray();
                    for (int i = 0; i + 8 <= d.Length; i += 8)
                        al.AlphaLayerData.Add(new AlphaLayerData { Position = BitConverter.ToUInt16(d, i), Opacity = BitConverter.ToSingle(d, i + 4) });
                }
                l.Layers.Add(al);
            }
            else l.Layers.Add(new BaseLayer { Header = header });
        }
        l.IsCompressed = true;
        return l;
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
            foreach (var m in byWorld.GetValueOrDefault(key) ?? [])
            {
                Cell? cell = null;
                if (!m.IsPersistentCell && m.Header.Grid is { } grid)
                {
                    cell = CellAt((grid.X, grid.Y));
                    if (m.Land is { } land && ConvertLand(land) is { } converted) cell.Landscape = converted;
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
            navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
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
            var (navCount, navTriangles) = navmeshes.BuildExteriorCells(ws, cells,
                (x, y) => cells.TryGetValue((x, y), out var c) ? Solids(c.Temporary.OfType<PlacedObject>(), turnUntrusted: true) : [],
                // Lava is swum as water is (user, 2026-10-20: "lava is swimmable though and its hazard in case you fall in"):
                // its bed navmeshed and flagged Water, so whoever falls in can swim out; the lava itself hurts (HurtingWater).
                c => water.TryGetValue(c, out var w) ? w : null,
                top.Persistent.OfType<PlacedObject>().Where(loadDoorSet.Contains).Cast<IPlacedObjectGetter>().ToList(), Problems, asWound: true,
                only: navBox is { } nb ? (x, y) => x >= nb.X0 && y >= nb.Y0 && x <= nb.X1 && y <= nb.Y1 : null);
            ExteriorNavmeshes += navCount;
            NavmeshTriangles += navTriangles;
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
    /// units, as Skyrim's are) and weather chances, each Oblivion weather as the nearest Skyrim one by name; the
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
                if (w.Chance <= 0 || WeatherFor(w.Weather.FormKey) is not { } sky) continue;
                var existing = region.Weather.Weathers.FirstOrDefault(x => x.Weather.FormKey == sky);
                if (existing is not null) existing.Chance += w.Chance;
                else region.Weather.Weathers.Add(new WeatherType { Weather = new FormLink<IWeatherGetter>(sky), Chance = w.Chance });
            }
            if (region.Weather.Weathers.Count == 0) region.Weather = null;
            foreach (var area in src.Areas)
            {
                var points = area.RegionPoints.Select(p => new P2Float(p.X, p.Y)).ToList();
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

    /// <summary>Each base object's collision: whether its winding can be trusted (worked out once).</summary>
    readonly Dictionary<FormKey, bool> trustedWinding = new();
    bool Trusted(FormKey key, ObMeshConverter.Result mesh)
    {
        lock (trustedWinding)
            return trustedWinding.TryGetValue(key, out bool known) ? known : trustedWinding[key] = Skyrim.Navmesh.NavmeshBuilder.TrustedWinding(mesh.Solid);
    }

    /// <summary>Interior actors and load doors checked against their navmesh, and those within 64 units of it.</summary>
    public int ActorsChecked, ActorsOnNavmesh, DoorsChecked, DoorsOnNavmesh;

    void BuildNavmesh(Cell cell)
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
        if (solid.Count == 0) return;
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        var loadDoors = cell.Persistent.OfType<PlacedObject>().Where(d => d != StartMarker && placedDoors.ContainsValue(d)).Cast<IPlacedObjectGetter>().ToList();
        try
        {
            var actors = cell.Temporary.OfType<PlacedNpc>().Select(n => n.Placement!.Position).ToList();
            // The cell's water, lava (HurtingWater) as any other: swum over, the ground wholly under it flagged Water, so
            // whoever falls into lava can swim out (the lava hurts).
            float? level = cell.Flags.HasFlag(Cell.Flag.HasWater) ? cell.WaterHeight ?? 0 : null;
            if (level is not null && !cell.Water.IsNull && hurtingWaters.Values.Any(w => w is not null && w.FormKey == cell.Water.FormKey)) HazardCells++;
            Func<P3Float, bool>? underwater = level is { } wl ? p => p.Z < wl : null;
            var r = navmeshes.BuildInterior(cell, solid, turnedUp, loadDoors,
                cell.Persistent.Contains(StartMarker) ? StartMarker.Placement!.Position : null, actors, underwater: underwater);
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
        // Doors linked here and any other without a door triangle get theirs now.
        DoorTrianglesAdded = navmeshes?.AddMissingDoorTriangles(mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells)) ?? 0;
        return (linked, unlinked);
    }

    /// <summary>Load doors given their door triangle after the navmeshes (<see cref="LinkDoors"/>).</summary>
    public int DoorTrianglesAdded { get; private set; }
}
