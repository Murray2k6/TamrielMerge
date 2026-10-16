using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Town building interiors, built like dungeons (<see cref="DfTownLayout.Interior"/> lays a building's rooms out as an
/// RDB-style block): walls and floors as one static with the town's Skyrim textures, Daggerfall's furniture as Skyrim
/// furniture, its clutter sprites as Skyrim clutter, its light sprites as Skyrim fixtures each with a warm light, the
/// doors between rooms as hinged Skyrim doors, and the way out (Daggerfall's door surfaces) as load doors.
/// </summary>
public sealed partial class DungeonCellBuilder
{
    Light? houseLight;

    /// <summary>A candle or lamp's light indoors: warm, flickering, as Daggerfall Unity lights its interiors.</summary>
    Light HouseLight()
    {
        if (houseLight is not null) return houseLight;
        houseLight = new Light(mod)
        {
            EditorID = "DFHouseLight",
            ObjectBounds = new ObjectBounds(),
            Time = -1,
            Radius = 600,
            Color = System.Drawing.Color.FromArgb(0, 255, 214, 160),
            Flags = Light.Flag.Dynamic | Light.Flag.Flicker | Light.Flag.PortalStrict,
            FalloffExponent = 1,
            FOV = 90,
            NearClip = 0.001f,
            FlickerPeriod = 0.3f,
            FlickerIntensityAmplitude = 0.1f,
            FlickerMovementAmplitude = 0,
            FadeValue = 0.9f,
        };
        mod.Lights.Add(houseLight);
        return houseLight;
    }

    /// <summary>Daggerfall Unity's building interiors: a grey ambient of 0.18, otherwise lit by their own lights.</summary>
    static CellLighting BuildingLighting()
    {
        var l = DungeonLighting();
        var ambient = System.Drawing.Color.FromArgb(0, 46, 46, 46);
        l.AmbientColor = ambient;
        l.AmbientColors!.DirectionalXPlus = ambient; l.AmbientColors.DirectionalXMinus = ambient; l.AmbientColors.DirectionalYPlus = ambient;
        l.AmbientColors.DirectionalYMinus = ambient; l.AmbientColors.DirectionalZPlus = ambient; l.AmbientColors.DirectionalZMinus = ambient;
        return l;
    }

    public sealed record BuiltInterior(Cell Cell, List<(PlacedObject Door, DfTownLayout.DoorSurface Surface)> Exits, Navmesh.NavmeshBuilder.Result? Navmesh, Dictionary<string, int> Fixtures, List<DfRdbObject> People);

    readonly Dictionary<string, (Static Static, BlockMeshBuilder.Result Mesh)> townStatics = new();
    static readonly System.Text.Json.JsonSerializerOptions SharedJson = new() { IncludeFields = true };

    /// <summary>A town block's or building's static (one NIF per layout and style), built on first use.</summary>
    internal Static TownStatic(DfDungeonBlock block, string key, BlockMeshBuilder.Picker picker)
    {
        if (townStatics.TryGetValue(key, out var hit)) return hit.Static;
        string rel = Path.Combine("TamrielMerge", "Towns", key + ".nif");
        string outPath = Path.Combine(modOutDir, "Meshes", rel);
        // Another part of the same conversion made this mesh already (the shared interiors and town blocks are in many
        // parts; making them was a quarter of a part's time, 2026-10-14): its copy, and its size from beside it.
        string? shared = Environment.GetEnvironmentVariable("TAMRIELMERGE_TOWN_MESHES") is { Length: > 0 } cache ? Path.Combine(cache, rel) : null;
        BlockMeshBuilder.Result? r = null;
        if (shared is not null && File.Exists(shared + ".json") && File.Exists(shared))
            try
            {
                r = System.Text.Json.JsonSerializer.Deserialize<BlockMeshBuilder.Result>(File.ReadAllText(shared + ".json"), SharedJson);
                if (r is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.Copy(shared, outPath, overwrite: true);
                    r = r with { Path = outPath };
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { r = null; }
        if (r is null)
        {
            BlockMeshBuilder.Export = textureExport;
            r = BlockMeshBuilder.Build(block, models, outPath, picker);
            if (shared is not null)
                try
                {
                    // The mesh first, then its size: a part finding the size finds the whole mesh.
                    Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
                    string tmp = shared + ".tmp" + Environment.ProcessId;
                    File.Copy(outPath, tmp, overwrite: true);
                    File.Move(tmp, shared, overwrite: true);
                    File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(r, SharedJson));
                    File.Move(tmp, shared + ".json", overwrite: true);
                }
                catch (IOException) { }
        }
        var stat = new Static(mod)
        {
            EditorID = "DFTown" + LocationPlacer.Sanitize(key),
            Model = new Model { File = rel },
            ObjectBounds = new ObjectBounds
            {
                First = new P3Int16((short)Math.Clamp(r.Min.X, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Min.Y, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Min.Z, short.MinValue, short.MaxValue)),
                Second = new P3Int16((short)Math.Clamp(r.Max.X, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Max.Y, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Max.Z, short.MinValue, short.MaxValue)),
            },
        };
        mod.Statics.Add(stat);
        townStatics[key] = (stat, r);
        return stat;
    }

    /// <summary>A ground mesh (built once per key from its polygons, Skyrim units), as a static.</summary>
    internal (Static Static, List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> Polygons) GroundStatic(string key, Func<List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>> polygons)
    {
        if (groundStatics.TryGetValue(key, out var hit)) return hit;
        var polys = polygons();
        string rel = Path.Combine("TamrielMerge", "Towns", "Ground", key + ".nif");
        var r = BlockMeshBuilder.BuildPolygons(polys, Path.Combine(modOutDir, "Meshes", rel));
        var stat = new Static(mod)
        {
            EditorID = "DFGround" + LocationPlacer.Sanitize(key),
            Model = new Model { File = rel },
            ObjectBounds = new ObjectBounds { First = new P3Int16((short)r.Min.X, (short)r.Min.Y, (short)Math.Min(r.Min.Z, -8)), Second = new P3Int16((short)r.Max.X, (short)r.Max.Y, (short)Math.Max(r.Max.Z, 8)) },
        };
        mod.Statics.Add(stat);
        return groundStatics[key] = (stat, polys);
    }
    readonly Dictionary<string, (Static, List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>)> groundStatics = new();

    internal DfTextureExport TextureExport => textureExport;
    internal Navmesh.NavmeshBuilder Navmeshes => navmeshes;

    /// <summary>A town block's model polygons (doors left out) in the block's own space, Daggerfall units.</summary>
    internal static List<DfPlacedPolygon> BlockPolygons(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models)
    {
        var polys = new List<DfPlacedPolygon>();
        foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model && !BlockMeshBuilder.IsDoor(o)))
        {
            if (!models.TryGetValue(o.ModelId, out var model)) continue;
            var m = DfDungeon.ObjectTransform(o, Vector3.Zero);
            foreach (var p in model.Polygons)
                if (p.Points.Length >= 3)
                    polys.Add(new DfPlacedPolygon(p.Points.Select(v => Vector3.Transform(v, m)).ToArray(), Vector3.Normalize(Vector3.TransformNormal(p.Normal, m)), p.TextureArchive, p.TextureRecord, o.ModelId));
        }
        return polys;
    }

    /// <summary>
    /// A Skyrim door standing in a Daggerfall door surface (outside a building or inside it): scaled to the surface's
    /// height, its front turned to face the way the surface does, centred on it and set into the wall plane.
    /// <paramref name="origin"/> is where the surface's frame starts (Daggerfall units). Returns the door and the point in
    /// front of it where someone arriving through it should stand (Skyrim units) with the heading to face away.
    /// </summary>
    internal (PlacedObject Door, P3Float Arrival, float ArrivalHeading) SurfaceDoor(DfTownLayout.DoorSurface s, Vector3 origin, FormKey doorBase, FormKey? placeAs = null)
    {
        var (min, max, _) = props.SkyrimShape(doorBase);
        float height = MathF.Max(max.Z - min.Z, 1);
        float scale = Math.Clamp(s.Height * BlockMeshBuilder.Scale / height, 0.6f, 1.6f);
        var facing = new Vector2(s.Normal.X, s.Normal.Z);
        facing = facing.LengthSquared() > 1e-6f ? Vector2.Normalize(facing) : new Vector2(0, -1);
        float facingHeading = MathF.Atan2(-facing.X, -facing.Y);   // turns local -Y to face out
        float heading = facingHeading - FrontTurn(doorBase);
        var wall = ToSkyrim(origin + s.Centre);
        float bottom = wall.Z - s.Height * BlockMeshBuilder.Scale / 2;
        // Local (x, y) turns into world (x cos h + y sin h, -x sin h + y cos h); put the door's middle on the wall point.
        var local = new Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2) * scale;
        float c = MathF.Cos(heading), sn = MathF.Sin(heading);
        var offset = new Vector2(local.X * c + local.Y * sn, -local.X * sn + local.Y * c);
        var door = new PlacedObject(mod)
        {
            Base = (placeAs ?? doorBase).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(wall.X - offset.X, wall.Y - offset.Y, bottom - min.Z * scale), Rotation = new P3Float(0, 0, heading) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
        };
        if (MathF.Abs(scale - 1) > 0.01f) door.Scale = scale;
        var arrival = new P3Float(wall.X + facing.X * 110, wall.Y + facing.Y * 110, bottom + 8);
        return (door, arrival, facingHeading + MathF.PI);
    }

    /// <summary>
    /// How far a door model's front is turned from local -Y (the way <see cref="SurfaceDoor"/> faces out), learned
    /// from Skyrim's own placements (dev command door-front: the arrival point outside each load door, in the door's
    /// frame): ImpDoorSingleLoad01 -Y; RiftenDoorLoad01 +Y; NorDoorSmLoad01 -X; WRDoor02Load, never placed as a load
    /// door, -X like the Nordic one (both are thin in X with the extra depth behind). Before this the Woodland and
    /// Mountain doors stood across their door frames and Riften's faced in (user, 2026-10-10: "the door frames dont
    /// match some of the doors").
    /// </summary>
    internal static float FrontTurn(FormKey door) => door.ModKey != ModKey.FromFileName("Skyrim.esm") ? 0 : door.ID switch
    {
        0x04D6ED => MathF.PI,
        0x094193 or 0x02ABAB => MathF.PI / 2,
        _ => 0,
    };

    /// <summary>Builds one building's interior as an interior cell; the exits still need linking to the street.</summary>
    internal BuiltInterior BuildBuildingInterior(string editorId, string name, DfDungeonBlock block, string meshKey, BlockMeshBuilder.Picker picker, FormKey exitDoor, FormKey? exitPlaceAs = null)
    {
        var cell = new Cell(mod)
        {
            EditorID = editorId,
            Name = name,
            Flags = Cell.Flag.IsInteriorCell,
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = BuildingLighting(),
            Persistent = [],
            Temporary = [],
        };
        cell.Temporary.Add(new PlacedObject(mod)
        {
            Base = TownStatic(block, meshKey, picker).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
        });

        // The block's polygons in its own space, for fixtures and for finding floors and ceilings.
        var polys = BlockPolygons(block, models);
        var floorPolys = polys.Where(q => q.Normal.Y > 0.7f).ToList();
        var ceilingPolys = polys.Where(q => q.Normal.Y < -0.7f).ToList();
        var fixtures = new LightFixturePlacer(arena2, polys);
        var counts = new Dictionary<string, int>();
        var placedProps = new List<PlacedObject>();
        var surfaceCandles = new List<PlacedObject>();
        var obstacles = new List<PlacedObject>();
        var people = new List<DfRdbObject>();
        var rooms = new RoomFacing(polys);
        void Count(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;

        foreach (var o in block.Objects)
        {
            var pos = ToSkyrim(o.Position);
            if (o.Type == DfRdbObjectType.Flat && o.RecordOffset < 0) { people.Add(o); Count("person"); continue; }
            if (o.Type == DfRdbObjectType.Flat && o.TextureArchive == EditorFlatArchive && o.TextureRecord is QuestSpawnRecord or QuestItemRecord)
            {
                float floor = SurfaceY(floorPolys, o.Position.X, o.Position.Z, o.Position.Y + 16, above: false) ?? o.Position.Y;
                AddQuestMarker(cell, new P3Float(pos.X, pos.Y, floor * BlockMeshBuilder.Scale + 8), o.TextureRecord == QuestItemRecord);
                Count("quest marker");
                continue;
            }
            if (LightFixturePlacer.IsLight(o))
            {
                float? lift = null;
                foreach (var f in fixtures.Fixtures(o, o.Position))
                {
                    lift ??= FixtureLift(f);
                    var fp = ToSkyrim(f.PositionDf);
                    var fixture = new PlacedObject(mod)
                    {
                        Base = f.Base.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(fp.X, fp.Y, fp.Z + lift.Value), Rotation = new P3Float(0, 0, f.HeadingRadians) },
                    };
                    cell.Temporary.Add(fixture);
                    if (f.Kind == LightFixturePlacer.SurfaceCandle) surfaceCandles.Add(fixture);
                    Count(f.Kind);
                }
                // Each lamp or candle lights the room, a little below the flame.
                cell.Temporary.Add(new PlacedObject(mod)
                {
                    Base = HouseLight().ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = new P3Float(pos.X, pos.Y, pos.Z - 16), Rotation = new P3Float(0, 0, 0) },
                });
            }
            else if (Clutter is { } clutter && clutter.Covers(o))
            {
                foreach (var p in PlaceSprite(clutter, o, o.Position, floorPolys, ceilingPolys, rooms))
                {
                    var r = new PlacedObject(mod)
                    {
                        Base = p.Base.ToNullableLink<IPlaceableObjectGetter>(),
                        Placement = new Placement { Position = new P3Float(p.PositionSkyrim.X, p.PositionSkyrim.Y, p.PositionSkyrim.Z), Rotation = new P3Float(p.Rotation.X, p.Rotation.Y, p.Rotation.Z) },
                    };
                    if (MathF.Abs(p.Scale - 1) > 0.01f) r.Scale = p.Scale;
                    cell.Temporary.Add(r);
                    if (!p.IsItem && p.Mount == ClutterPlacer.Mount.Floor) obstacles.Add(r);
                    Count("clutter: " + p.Label);
                }
            }
            else if (o.Type == DfRdbObjectType.Model && PropReplacer.IsReplaced(o.ModelId) && models.TryGetValue(o.ModelId, out var propModel)
                && props.Replace(o, Vector3.Zero, propModel, PropFacing(rooms, o.Position, propModel, floorPolys)) is { } prop)
            {
                var r = PropBuilder.Place(o, Vector3.Zero, propModel, prop) ?? new PlacedObject(mod)
                {
                    Base = prop.Base.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = new P3Float(prop.PositionSkyrim.X, prop.PositionSkyrim.Y, prop.PositionSkyrim.Z), Rotation = new P3Float(0, 0, prop.Heading) },
                    Scale = MathF.Abs(prop.Scale - 1) > 0.01f ? prop.Scale : null,
                };
                cell.Temporary.Add(r);
                placedProps.Add(r);
                Count(prop.Kind);
            }
            else if (o.Type == DfRdbObjectType.Model && BlockMeshBuilder.IsDoor(o) && models.TryGetValue(o.ModelId, out var doorModel))
            {
                cell.Temporary.Add(DoorPlacer.Place(mod, o, Vector3.Zero, doorModel));
                Count("door");
            }
            else if (o.Type == DfRdbObjectType.Model && !models.ContainsKey(o.ModelId)) Count($"missing model {o.ModelId}");
        }

        // The way out: a load door in each of Daggerfall's door surfaces.
        var exits = new List<(PlacedObject, DfTownLayout.DoorSurface)>();
        P3Float? keep = null;
        foreach (var s in DfTownLayout.Doors(block, models))
        {
            var (door, arrival, _) = SurfaceDoor(s, Vector3.Zero, exitDoor, exitPlaceAs);
            cell.Persistent.Add(door);
            exits.Add((door, s));
            keep ??= arrival;
        }

        var walkable = BlockMeshBuilder.SolidPolygons(block, models, picker);
        walkable.AddRange(BlockMeshBuilder.ActionSolids(block, models));   // platforms, lifts, raised floors where they rest
        int settled = SettleProps(placedProps, walkable);
        if (settled > 0) counts["props settled"] = settled;
        // Candles that stood on Daggerfall's tables and shelves, onto the Skyrim ones in their place.
        int candles = SettleProps(surfaceCandles, walkable, placedProps);
        if (candles > 0) counts["candles settled onto tables"] = candles;
        foreach (var r in placedProps) walkable.AddRange(PropBox(r));
        foreach (var r in obstacles)
        {
            var (bmin, bmax) = PropBounds(r);
            if (MathF.Max(bmax.X - bmin.X, bmax.Y - bmin.Y) >= 40 && bmax.Z - bmin.Z >= 40) walkable.AddRange(PropBox(r));
        }
        Navmesh.NavmeshBuilder.Result? nav = null;
        // Scripted exits (shared interiors) aren't load doors: only real load doors get navmesh door links.
        var loadDoors = exits.Select(e => e.Item1).Where(d => exitPlaceAs is null);
        if (BackgroundInteriors)
            pendingInteriors.Add((navmeshes.BeginInterior(cell, walkable, loadDoors, keep, people.Select(p => ToSkyrim(p.Position)).ToList(), null, background: true), counts));
        else
            try { nav = navmeshes.BuildInterior(cell, walkable, loadDoors, keep, people.Select(p => ToSkyrim(p.Position))); }
            catch (Exception e) { counts["navmesh failed: " + e.Message] = 1; }
        AddInteriorCell(cell);
        return new BuiltInterior(cell, exits, nav, counts, people);
    }
}
