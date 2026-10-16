using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using System.Numerics;
using Activator = Mutagen.Bethesda.Skyrim.Activator;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Builds the Morrowind plugin for Skyrim SE from the original game data (<see cref="MwWorld"/>): Morrowind's own
/// models and textures converted by <see cref="MwMeshConverter"/>, cells rebuilt reference by reference at the same
/// positions (Morrowind and Skyrim share units and rotation conventions).
/// </summary>
public sealed partial class MwPluginBuilder
{
    public const string PluginName = "Morrowind.esm";
    static readonly FormKey LightingTemplate = new(ModKey.FromFileName("Skyrim.esm"), 0x6909B);   // ImperialFortCloseTemplate

    readonly MwWorld world;
    readonly MwMeshConverter meshes;
    readonly Tes3Data data;
    readonly MwTextures textures;
    readonly string modOutDir;
    readonly SkyrimMod mod;
    readonly Dictionary<string, IMajorRecord?> bases = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MwMeshConverter.Result?> converted = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = [];

    public SkyrimMod Mod => mod;
    /// <summary>Textures models asked for that the game doesn't have (those surfaces render as Skyrim's purple).</summary>
    public IEnumerable<string> MissingTextures => meshes.Textures.Missing.Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The story's start: where Morrowind begins a new game. Created first so it is always FormID 0x800 in
    /// Morrowind.esm, which the story order (TMStoryOrder) looks up with Game.GetFormFromFile; placed when its cell is
    /// built.</summary>
    public PlacedObject StartMarker { get; }
    /// <summary>Each built interior's collision triangles in world space, by cell name (for checks like headroom).</summary>
    public Dictionary<string, List<Vector3[]>> CellSolids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public const uint StartMarkerId = 0x800;
    static readonly FormKey XMarkerHeading = new(ModKey.FromFileName("Skyrim.esm"), 0x34);
    // Skyrim's wooden door sounds: descriptors for load doors, sound markers for the swinging-door script.
    static readonly FormKey DoorOpenSound = new(ModKey.FromFileName("Skyrim.esm"), 0x0BFBE8), DoorCloseSound = new(ModKey.FromFileName("Skyrim.esm"), 0x0BFBE9);
    static readonly FormKey SwingOpenSound = new(ModKey.FromFileName("Skyrim.esm"), 0x05B0F3), SwingCloseSound = new(ModKey.FromFileName("Skyrim.esm"), 0x05B134);
    /// <summary>Morrowind's opening position, from its CharGen script's "Player->PositionCell x, y, z, degrees, cell".</summary>
    public (string Cell, Vector3 Position, float Degrees)? Start { get; }

    public MwPluginBuilder(MwWorld world, Tes3Data data, string modOutDir)
    {
        this.world = world;
        this.modOutDir = modOutDir;
        this.data = data;
        textures = new MwTextures(data, modOutDir);
        meshes = new MwMeshConverter(data, textures);
        mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        StartMarker = new PlacedObject(mod)
        {
            EditorID = "MWStartMarker",
            Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,   // persistent
        };
        if (StartMarker.FormKey.ID != StartMarkerId) throw new InvalidOperationException($"start marker got {StartMarker.FormKey}, expected {StartMarkerId:X}");
        Start = FindStart(world);
    }

    /// <summary>The player's opening position: the first "Player->PositionCell" of the CharGen script.</summary>
    static (string Cell, Vector3 Position, float Degrees)? FindStart(MwWorld world)
    {
        if (!world.Objects.TryGetValue("CharGen", out var script) || script.Str("SCTX") is not { } text) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"player\s*->\s*PositionCell[\s,]+(-?[\d.]+)[\s,]+(-?[\d.]+)[\s,]+(-?[\d.]+)[\s,]+(-?[\d.]+)[\s,]+""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        float F(int g) => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (m.Groups[5].Value, new Vector3(F(1), F(2), F(3)), F(4));
    }

    static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    MwMeshConverter.Result? Mesh(string model)
    {
        if (converted.TryGetValue(model, out var hit)) return hit;
        try { hit = meshes.Convert(model, modOutDir); }
        catch (Exception e) { Problems.Add($"model {model}: {e.Message}"); hit = null; }
        return converted[model] = hit;
    }

    public int EffectCount { get; private set; }

    /// <summary>
    /// Vanilla effects standing in for a reference's particle systems (<see cref="Skyrim.EffectStandIns"/>), at its
    /// emitters: for lights, activators and statics. A fire's several flame emitters (flames, inner flames) give one
    /// fire; separate candles each keep their flame. Effects stand upright whatever the reference's rotation.
    /// </summary>
    IEnumerable<PlacedObject> Effects(Tes3Ref r)
    {
        if (!world.Objects.TryGetValue(r.BaseId, out var rec) || rec.Type is not ("LIGH" or "ACTI" or "STAT")) yield break;
        if (rec.Str("MODL") is not { Length: > 0 } model || Mesh(model) is not { Emitters.Count: > 0 } mesh) yield break;
        foreach (var (at, effect, scale) in Skyrim.EffectStandIns.Pick(mesh.Emitters, r.Scale))
            {
                var w = ToWorld(at, r);
                EffectCount++;
                yield return new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(effect),
                    Placement = new Placement { Position = new P3Float(w.X, w.Y, w.Z), Rotation = new P3Float(0, 0, 0) },
                    Scale = scale,
                };
            }
    }

    static ObjectBounds Bounds(MwMeshConverter.Result? r) => r is null ? new ObjectBounds() : new ObjectBounds
    {
        First = new P3Int16((short)Math.Clamp(r.Min.X, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Min.Y, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Min.Z, short.MinValue, short.MaxValue)),
        Second = new P3Int16((short)Math.Clamp(r.Max.X, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Max.Y, short.MinValue, short.MaxValue), (short)Math.Clamp(r.Max.Z, short.MinValue, short.MaxValue)),
    };

    /// <summary>The Skyrim base object for a Morrowind object ID, created on first use; null when it has no model
    /// or isn't placeable yet (NPCs, creatures, leveled lists come later).</summary>
    IMajorRecord? Base(string id)
    {
        if (bases.TryGetValue(id, out var hit)) return hit;
        bases[id] = null;
        if (!world.Objects.TryGetValue(id, out var rec)) { Problems.Add($"unknown object {id}"); return null; }
        string? model = rec.Str("MODL") is { Length: > 0 } m ? m : null;
        if (rec.Type is "NPC_" or "CREA" or "LEVC" or "LEVI" or "BODY") return null;
        // Editor markers (prison, travel, divine/temple intervention, north and arrow markers): Morrowind doesn't draw
        // models whose file name starts with "marker".
        if (model is not null && Path.GetFileName(model).StartsWith("marker", StringComparison.OrdinalIgnoreCase)) return null;
        string edid = "MW" + Sanitize(id);
        // Items (with physics) and containers (with their contents) have their own records.
        if (IsItemType(rec.Type)) return bases[id] = Item(id);
        if (rec.Type == "CONT") return bases[id] = ContainerItem(rec, edid);
        var mesh = model is null ? null : Mesh(model);
        IMajorRecord result;
        if (rec.Type == "LIGH")
        {
            var d = rec.Get("LHDT")!.Value;
            int radius = d.Int(12);
            uint flags = BitConverter.ToUInt32(d.Data, 20);
            var light = new Light(mod)
            {
                EditorID = edid,
                ObjectBounds = Bounds(mesh),
                Time = d.Int(8),
                Radius = (uint)Math.Max(1, radius),
                Color = System.Drawing.Color.FromArgb(0, d.Data[16], d.Data[17], d.Data[18]),
                FalloffExponent = 1,
                FOV = 90,
                NearClip = 0.001f,
                FadeValue = 1,
                Weight = d.Float(0),
                Value = (uint)Math.Max(0, d.Int(4)),
                Flags = ((flags & 0x2) != 0 ? Light.Flag.CanBeCarried : 0)
                      | ((flags & 0x4) != 0 ? Light.Flag.Negative : 0)
                      | ((flags & 0x48) != 0 ? Light.Flag.Flicker : 0)
                      | ((flags & 0x40) != 0 ? Light.Flag.FlickerSlow : 0)
                      | ((flags & 0x80) != 0 ? Light.Flag.Pulse : 0)
                      | ((flags & 0x100) != 0 ? Light.Flag.PulseSlow : 0)
                      | ((flags & 0x20) != 0 ? Light.Flag.OffByDefault : 0),
                FlickerPeriod = 0.5f,
                FlickerIntensityAmplitude = 0.2f,
                FlickerMovementAmplitude = 4,
            };
            if (rec.Str("FNAM") is { Length: > 0 } name) light.Name = name;
            if (mesh is not null) light.Model = new Model { File = SkyrimModelPath(mesh.Path) };
            mod.Lights.Add(light);
            result = light;
        }
        else
        {
            if (mesh is null) return null;
            if (rec.Type == "DOOR")
            {
                var door = new Door(mod)
                {
                    EditorID = edid,
                    ObjectBounds = Bounds(mesh),
                    Model = new Model { File = SkyrimModelPath(mesh.Path) },
                    OpenSound = new FormLinkNullable<ISoundDescriptorGetter>(DoorOpenSound),
                    CloseSound = new FormLinkNullable<ISoundDescriptorGetter>(DoorCloseSound),
                };
                if (rec.Str("FNAM") is { Length: > 0 } doorName) door.Name = doorName;
                mod.Doors.Add(door);
                return bases[id] = door;
            }
            // A scripted activator can carry its script (statics can't).
            if (ScriptedActivator(rec, edid, mesh) is { } scripted) return bases[id] = scripted;
            var stat = new Static(mod) { EditorID = edid, ObjectBounds = Bounds(mesh), Model = new Model { File = SkyrimModelPath(mesh.Path) } };
            mod.Statics.Add(stat);
            result = stat;
        }
        return bases[id] = result;
    }

    readonly Dictionary<string, Activator?> swingDoors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A Morrowind door that doesn't teleport, as Skyrim can open it: an activator running TMSwingDoor, which swings it
    /// 90 degrees about its hinge (the model's origin) as Morrowind does, on a variant of the model whose collision is
    /// keyframed so it follows the door.
    /// </summary>
    Activator? SwingDoor(string id)
    {
        if (swingDoors.TryGetValue(id, out var hit)) return hit;
        swingDoors[id] = null;
        if (!world.Objects.TryGetValue(id, out var rec) || rec.Str("MODL") is not { Length: > 0 } model) return null;
        MwMeshConverter.Result? mesh;
        try { mesh = meshes.Convert(model, modOutDir, moving: true); }
        catch (Exception e) { Problems.Add($"model {model}: {e.Message}"); return null; }
        var door = new Activator(mod)
        {
            EditorID = "MW" + Sanitize(id) + "Swing",
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
        if (rec.Str("FNAM") is { Length: > 0 } name) door.Name = name;
        mod.Activators.Add(door);
        return swingDoors[id] = door;
    }

    /// <summary>The base to place for a reference: a swinging door for a door that doesn't teleport.</summary>
    IMajorRecord? PlaceableFor(Tes3Ref r) =>
        world.Objects.TryGetValue(r.BaseId, out var actorRec) && actorRec.Type is "NPC_" or "CREA" or "LEVC"
            ? Actor(r.BaseId)
            : r.DoorDestination is null && world.Objects.TryGetValue(r.BaseId, out var rec) && rec.Type == "DOOR"
            && !(rec.Str("MODL") is { } m && Path.GetFileName(m).StartsWith("marker", StringComparison.OrdinalIgnoreCase))
            ? SwingDoor(r.BaseId)
            : Base(r.BaseId);

    /// <summary>Model paths in records are relative to the Meshes folder.</summary>
    static string SkyrimModelPath(string dataRelative) => dataRelative.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? dataRelative[7..] : dataRelative;

    /// <summary>Morrowind's interior ambient/sun/fog (AMBI) as Skyrim cell lighting. Fog: OpenMW's reading of the
    /// density — fog ends at the view distance and starts (1 − density) of the way there.</summary>
    static CellLighting Lighting(Tes3Cell c)
    {
        System.Drawing.Color Col(Vector4? v) => v is { } x ? System.Drawing.Color.FromArgb(0, (int)(x.X * 255), (int)(x.Y * 255), (int)(x.Z * 255)) : System.Drawing.Color.Black;
        var ambient = Col(c.Ambient);
        const float view = 7168;
        return new CellLighting
        {
            AmbientColor = ambient,
            DirectionalColor = Col(c.Sunlight),
            DirectionalFade = 1,
            DirectionalRotationXY = 45,
            DirectionalRotationZ = 45,
            FogNearColor = Col(c.Fog),
            FogFarColor = Col(c.Fog),
            FogNear = view * (1 - Math.Clamp(c.FogDensity, 0, 1)),
            FogFar = view,
            FogPower = 1,
            FogMax = c.FogDensity > 0 ? 1 : 0,
            FogClipDistance = 0,
            LightFadeBegin = 6000,
            LightFadeEnd = 8000,
            AmbientColors = new AmbientColors
            {
                DirectionalXPlus = ambient, DirectionalXMinus = ambient, DirectionalYPlus = ambient,
                DirectionalYMinus = ambient, DirectionalZPlus = ambient, DirectionalZMinus = ambient,
                Specular = System.Drawing.Color.Black, Scale = 1,
            },
            Inherits = 0,
        };
    }

    /// <summary>Builds an interior cell with every placeable reference. Returns the cell.</summary>
    public Cell BuildInterior(Tes3Cell source)
    {
        var cell = new Cell(mod)
        {
            EditorID = "MW" + Sanitize(source.Name),
            Name = source.Name,
            Flags = Cell.Flag.IsInteriorCell | (source.HasWater ? Cell.Flag.HasWater : 0),
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = Lighting(source),
            Persistent = [],
            Temporary = [],
        };
        if (source.HasWater && source.WaterHeight is { } wh) cell.WaterHeight = wh;
        var solid = new List<Vector3[]>();
        var visible = new List<(string Model, Vector3[] Tri)>();
        foreach (var r in source.Refs)
        {
            foreach (var fx in Effects(r)) cell.Temporary.Add(fx);
            if (InterventionMarker(r, cell, source.Name) || PrisonMarker(r, cell, source.Name)) continue;
            if (PlaceableFor(r) is not { } b) continue;
            if (world.Model(r.BaseId) is { Length: > 0 } model && converted.GetValueOrDefault(model) is { } mesh)
            {
                solid.AddRange(mesh.Solid.Select(t => t.Select(v => ToWorld(v, r)).ToArray()));
                visible.AddRange(mesh.Visible.Select(t => (model, t.Select(v => ToWorld(v, r)).ToArray())));
            }
            if (b is Npc actor)
            {
                var placedActor = new PlacedNpc(mod)
                {
                    Base = new FormLinkNullable<INpcGetter>(actor.FormKey),
                    Placement = new Placement { Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z), Rotation = new P3Float(r.Rotation.X, r.Rotation.Y, r.Rotation.Z) },
                    Scale = r.Scale == 1 ? null : r.Scale,
                };
                if (!PlaceForScripts(r, placedActor, cell)) cell.Temporary.Add(placedActor);
                NoteActor(placedActor, r, source.Name);
                continue;
            }
            var placed = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(b.FormKey),
                Placement = new Placement { Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z), Rotation = new P3Float(r.Rotation.X, r.Rotation.Y, r.Rotation.Z) },
                Scale = r.Scale == 1 ? null : r.Scale,
                ItemCount = b is IItemGetter && StackOf(r.BaseId, r.Count) is > 1 and var n ? n : null,
            };
            ApplyLock(r, placed, b);
            if (b is Door)
            {
                // Load doors are persistent, as Skyrim requires.
                placed.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
                cell.Persistent.Add(placed);
                doors.Add((r, placed, source.Name));
                NoteDoorForScripts(r, placed);
            }
            else if (!PlaceForScripts(r, placed, cell)) cell.Temporary.Add(placed);
        }
        AddAnchor(source.Name, cell);
        CellSolids[source.Name] = solid;
        BuildNavmesh(cell, solid);
        if (Start is { } start && string.Equals(start.Cell, source.Name, StringComparison.OrdinalIgnoreCase))
        {
            var spot = ClearSpot(start.Position, solid);
            if (spot is null)
            {
                var under = solid.Where(t => InTriangle2D(start.Position, t)).Select(t =>
                {
                    var n = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
                    return MathF.Abs(n.Z) < 1e-6f ? float.NaN : t[0].Z - ((start.Position.X - t[0].X) * n.X + (start.Position.Y - t[0].Y) * n.Y) / n.Z;
                }).Order().Select(z => z.ToString("F0"));
                var seen = visible.Where(v => InTriangle2D(start.Position, v.Tri)).Select(v =>
                {
                    var t = v.Tri; var n = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
                    return (Z: MathF.Abs(n.Z) < 1e-6f ? float.NaN : t[0].Z - ((start.Position.X - t[0].X) * n.X + (start.Position.Y - t[0].Y) * n.Y) / n.Z, v.Model);
                }).OrderBy(x => x.Z).Select(x => $"{x.Z:F0} {x.Model}");
                Problems.Add($"start: no clear floor near {start.Position} ({solid.Count} collision triangles in the cell; surfaces under it at z {string.Join(", ", under)}; visible: {string.Join("; ", seen)}); keeping Morrowind's position");
            }
            else if (Vector2.Distance(new Vector2(spot.Value.X, spot.Value.Y), new Vector2(start.Position.X, start.Position.Y)) > 1)
                Problems.Add($"start moved {Vector2.Distance(new Vector2(spot.Value.X, spot.Value.Y), new Vector2(start.Position.X, start.Position.Y)):F0} units sideways to {spot}: Skyrim's player is wider than Morrowind's");
            var at = spot ?? start.Position;
            StartMarker.Placement = new Placement
            {
                Position = new P3Float(at.X, at.Y, at.Z),
                Rotation = new P3Float(0, 0, start.Degrees * MathF.PI / 180),
            };
            cell.Persistent.Add(StartMarker);
        }
        AddInteriorCell(cell);
        interiorsByName[source.Name] = cell;
        return cell;
    }

    readonly Dictionary<string, Cell> interiorsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A model-space point placed by a reference: scaled, rotated (Bethesda rotations are clockwise,
    /// applied X, Y, then Z) and moved.</summary>
    static Vector3 ToWorld(Vector3 v, Tes3Ref r) =>
        Vector3.Transform(v * r.Scale, Matrix4x4.CreateRotationX(-r.Rotation.X) * Matrix4x4.CreateRotationY(-r.Rotation.Y) * Matrix4x4.CreateRotationZ(-r.Rotation.Z)) + r.Position;

    /// <summary>
    /// Where Skyrim's player can stand near a Morrowind position: on the floor it would land on (the highest
    /// upward-facing collision triangle under the spot, from 10 above down to 250 below — Morrowind places the player
    /// in the air and lets them drop, e.g. 130 units onto the prison ship's hold) with no collision inside a standing
    /// body above it (radius 30, from knee height 30 to 130). Morrowind's own spot when it qualifies, else the nearest
    /// such spot on a spiral out to 200 units; null when there is none. Skyrim's player is wider and taller than
    /// Morrowind's, so it can overlap a ledge or crate Morrowind's player passes.
    /// </summary>
    static Vector3? ClearSpot(Vector3 at, List<Vector3[]> solid)
    {
        const float radius = 30, knee = 30, height = 130, above = 10, below = 250;
        // Triangles whose bounds reach the search area (a large deck triangle can have every corner far away).
        const float area = 200 + 2 * radius;
        var near = solid.Where(t =>
            t.Max(v => v.X) > at.X - area && t.Min(v => v.X) < at.X + area &&
            t.Max(v => v.Y) > at.Y - area && t.Min(v => v.Y) < at.Y + area &&
            t.Max(v => v.Z) > at.Z - below - 10 && t.Min(v => v.Z) < at.Z + height + 10).ToList();
        float? FloorAt(Vector3 p)
        {
            float? best = null;
            foreach (var t in near)
            {
                var n = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
                if (n.LengthSquared() < 1e-6f || Vector3.Normalize(n).Z < 0.7f && Vector3.Normalize(n).Z > -0.7f) continue;
                if (!InTriangle2D(p, t)) continue;
                float z = t[0].Z - ((p.X - t[0].X) * n.X + (p.Y - t[0].Y) * n.Y) / n.Z;
                if (z <= p.Z + above && z >= p.Z - below && (best is null || z > best.Value)) best = z;
            }
            return best;
        }
        bool Blocked(Vector3 feet) => near.Any(t => Samples(t).Any(q =>
            q.Z > feet.Z + knee && q.Z < feet.Z + height && Vector2.Distance(new Vector2(q.X, q.Y), new Vector2(feet.X, feet.Y)) < radius));
        Vector3? Try(Vector3 p) => FloorAt(p) is { } z && !Blocked(p with { Z = z }) ? p with { Z = z } : null;
        if (Try(at) is { } here) return here;
        for (float d = 8; d <= 200; d += 8)
            for (int k = 0; k < 32; k++)
                if (Try(at + new Vector3(d * MathF.Cos(k * MathF.PI / 16), d * MathF.Sin(k * MathF.PI / 16), 0)) is { } p) return p;
        return null;
    }

    Skyrim.Navmesh.NavmeshBuilder? navmeshes;
    public int Navmeshes { get; private set; }
    public int NavmeshTriangles { get; private set; }

    /// <summary>
    /// The interior's navmesh (Recast over its static collision, as for Daggerfall's dungeons), so NPCs and creatures
    /// can walk; load doors are linked, and the areas around actors are kept even when nothing else connects to them.
    /// Items, swinging doors and actors aren't part of the collision, so it runs through doorways.
    /// </summary>
    void BuildNavmesh(Cell cell, List<Vector3[]> solid)
    {
        if (solid.Count == 0) return;
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        var polygons = solid.Select(t => (t, Vector3.Normalize(Vector3.Cross(t[1] - t[0], t[2] - t[0])))).Where(p => !float.IsNaN(p.Item2.X))
            .Select(p => (p.t, p.Item2.Z < 0 ? -p.Item2 : p.Item2)).ToList();   // collision winding is either way; walkable faces point up
        // Every persistent placed object here except the start marker is a load door (linked to its partner later).
        var loadDoors = cell.Persistent.OfType<PlacedObject>().Where(d => d != StartMarker && !scriptPersistent.Contains(d)).Cast<IPlacedObjectGetter>().ToList();
        var actorsHere = cell.Temporary.Concat(cell.Persistent).OfType<PlacedNpc>().Select(n => n.Placement!.Position).ToList();
        try
        {
            var r = navmeshes.BuildInterior(cell, polygons, loadDoors, keepAround: StartMarker.Placement is { } sp && cell.Persistent.Contains(StartMarker) ? sp.Position : null, keepAlso: actorsHere);
            Navmeshes++;
            NavmeshTriangles += r.Triangles;
        }
        catch (Exception e) { Problems.Add($"navmesh {cell.EditorID}: {e.Message}"); }
    }

    /// <summary>Floor and ceiling heights at a point: the highest surface at or below <paramref name="z"/> + 10 and
    /// the lowest above it (null when there is none).</summary>
    public static (float? Floor, float? Ceiling) Clearance(List<Vector3[]> solid, Vector3 p)
    {
        float? floor = null, ceiling = null;
        foreach (var t in solid)
        {
            if (!InTriangle2D(p, t)) continue;
            var n = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
            if (MathF.Abs(n.Z) < 1e-6f) continue;
            float z = t[0].Z - ((p.X - t[0].X) * n.X + (p.Y - t[0].Y) * n.Y) / n.Z;
            if (z <= p.Z + 10) { if (floor is null || z > floor) floor = z; }
            else if (ceiling is null || z < ceiling) ceiling = z;
        }
        return (floor, ceiling);
    }

    static bool InTriangle2D(Vector3 p, Vector3[] t)
    {
        static float Side(Vector3 a, Vector3 b, Vector3 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        float d1 = Side(t[0], t[1], p), d2 = Side(t[1], t[2], p), d3 = Side(t[2], t[0], p);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    /// <summary>Points over a triangle (a barycentric grid about 8 units apart), to test it against a body.</summary>
    static IEnumerable<Vector3> Samples(Vector3[] t)
    {
        float longest = MathF.Max(Vector3.Distance(t[0], t[1]), MathF.Max(Vector3.Distance(t[1], t[2]), Vector3.Distance(t[2], t[0])));
        int n = Math.Clamp((int)(longest / 8), 1, 64);
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n - i; j++)
                yield return t[0] + (t[1] - t[0]) * (i / (float)n) + (t[2] - t[0]) * (j / (float)n);
    }

    readonly List<(Tes3Ref Ref, PlacedObject Placed, string Cell)> doors = [];
    /// <summary>The key for the exterior in door bookkeeping (a door's DNAM is absent when it leads outside).</summary>
    const string Exterior = "";

    /// <summary>
    /// Builds Morrowind's exterior (Vvardenfell and Solstheim) as one worldspace: each Morrowind cell's terrain as
    /// 2x2 Skyrim cells (<see cref="MwTerrain"/>) and every exterior reference in the Skyrim cell its position falls
    /// in. Doors are persistent and live in the worldspace's persistent cell, as Skyrim keeps them. Climate and water
    /// are Skyrim's for now. Returns the worldspace.
    /// </summary>
    public Worldspace BuildExteriors(string skyrimData)
    {
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var tamriel = skyrim.Worldspaces.First(w => w.EditorID == "Tamriel");
        var terrain = new MwTerrain(mod, meshes.Textures);
        var cells = new Dictionary<(int X, int Y), Cell>();
        Cell CellAt((int X, int Y) g) => cells.TryGetValue(g, out var c) ? c : cells[g] = new Cell(mod)
        {
            Grid = new CellGrid { Point = new P2Int(g.X, g.Y) },
            Flags = Cell.Flag.HasWater,
            Temporary = [],
            Persistent = [],
        };
        foreach (var ((gx, gy), (land, ltex)) in world.Lands)
            foreach (var (grid, landscape, min) in terrain.Build(gx, gy, land, ltex))
            {
                var cell = CellAt(grid);
                cell.Landscape = landscape;
                if (min >= 0) cell.Flags &= ~Cell.Flag.HasWater;
            }
        Problems.AddRange(terrain.Problems);

        var top = new Cell(mod) { Flags = tamriel.TopCell?.Flags ?? 0, SkyrimMajorRecordFlags = tamriel.TopCell?.SkyrimMajorRecordFlags ?? 0, Persistent = [], Temporary = [] };
        // Settlements (Morrowind's named exterior cells: Balmora, Seyda Neen, Vivec...): one marker at the middle of
        // each, for the dangers of the night in town (TMNightDanger, which finds MWTownCentres by name).
        var centres = new FormList(mod) { EditorID = Skyrim.TownNights.TownCentresId("MW") };
        foreach (var town in world.Exteriors.Values.Where(c => c.Name.Length > 0).GroupBy(c => c.Name))
        {
            var z = town.SelectMany(c => c.Refs).Select(r => r.Position.Z).DefaultIfEmpty(0).Average();
            var marker = new PlacedObject(mod)
            {
                EditorID = "MWTownCentre" + Sanitize(town.Key),
                Base = Skyrim.TownNights.XMarker.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float((float)town.Average(c => (c.GridX + 0.5) * 8192), (float)town.Average(c => (c.GridY + 0.5) * 8192), z), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            top.Persistent.Add(marker);
            centres.Items.Add(marker.ToLink<ISkyrimMajorRecordGetter>());
            // A map marker for fast travel: on the map once found (Skyrim discovers markers as you come near).
            var mapMarker = new PlacedObject(mod)
            {
                EditorID = "MWMapMarker" + Sanitize(town.Key),
                Base = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x10).ToNullableLink<IPlaceableObjectGetter>(),   // MapMarker
                Placement = new Placement { Position = marker.Placement.Position, Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                MapMarker = new MapMarker { Name = town.Key, Type = town.Count() >= 4 ? MapMarker.MarkerType.City : MapMarker.MarkerType.Settlement },
            };
            top.Persistent.Add(mapMarker);
            townMapMarkers[town.Key] = mapMarker;   // ShowMap's places
        }
        AddAnchor("", top);
        if (centres.Items.Count > 0) mod.FormLists.Add(centres);
        // Each reference's collision by the Skyrim cell it is placed in, for the exterior navmeshes.
        var solids = new Dictionary<(int X, int Y), List<(Tes3Ref Ref, MwMeshConverter.Result Mesh)>>();
        foreach (var source in world.Exteriors.Values)
            foreach (var r in source.Refs)
            {
                foreach (var fx in Effects(r))
                    CellAt(((int)MathF.Floor(r.Position.X / MwTerrain.SkyCellUnits), (int)MathF.Floor(r.Position.Y / MwTerrain.SkyCellUnits))).Temporary.Add(fx);
                if (InterventionMarker(r, top, Exterior) || PrisonMarker(r, top, Exterior)) continue;
                if (PlaceableFor(r) is not { } b) continue;
                if (b is not Npc && world.Model(r.BaseId) is { Length: > 0 } model && converted.GetValueOrDefault(model) is { Solid.Count: > 0 } mesh)
                {
                    var key = ((int)MathF.Floor(r.Position.X / MwTerrain.SkyCellUnits), (int)MathF.Floor(r.Position.Y / MwTerrain.SkyCellUnits));
                    if (!solids.TryGetValue(key, out var list)) solids[key] = list = [];
                    list.Add((r, mesh));
                }
                if (b is Npc actor)
                {
                    var placedActor = new PlacedNpc(mod)
                    {
                        Base = new FormLinkNullable<INpcGetter>(actor.FormKey),
                        Placement = new Placement { Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z), Rotation = new P3Float(r.Rotation.X, r.Rotation.Y, r.Rotation.Z) },
                        Scale = r.Scale == 1 ? null : r.Scale,
                    };
                    if (!PlaceForScripts(r, placedActor, top))
                        CellAt(((int)MathF.Floor(r.Position.X / MwTerrain.SkyCellUnits), (int)MathF.Floor(r.Position.Y / MwTerrain.SkyCellUnits))).Temporary.Add(placedActor);
                    NoteActor(placedActor, r, null);
                    continue;
                }
                var placed = new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(b.FormKey),
                    Placement = new Placement { Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z), Rotation = new P3Float(r.Rotation.X, r.Rotation.Y, r.Rotation.Z) },
                    Scale = r.Scale == 1 ? null : r.Scale,
                    ItemCount = b is IItemGetter && StackOf(r.BaseId, r.Count) is > 1 and var n ? n : null,
                };
                ApplyLock(r, placed, b);
                if (b is Door)
                {
                    placed.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
                    top.Persistent.Add(placed);
                    doors.Add((r, placed, Exterior));
                    NoteDoorForScripts(r, placed);
                }
                else if (!PlaceForScripts(r, placed, top))
                    CellAt(((int)MathF.Floor(r.Position.X / MwTerrain.SkyCellUnits), (int)MathF.Floor(r.Position.Y / MwTerrain.SkyCellUnits))).Temporary.Add(placed);
            }

        int minX = cells.Keys.Min(k => k.X), maxX = cells.Keys.Max(k => k.X), minY = cells.Keys.Min(k => k.Y), maxY = cells.Keys.Max(k => k.Y);
        var ws = new Worldspace(mod)
        {
            EditorID = "MWVvardenfell",
            Name = "Vvardenfell",
            Climate = tamriel.Climate.AsNullable(),
            Water = tamriel.Water.AsNullable(),
            LodWater = tamriel.LodWater.AsNullable(),
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
        var blocks = new Dictionary<(int, int), WorldspaceBlock>();
        var subBlocks = new Dictionary<(int, int), WorldspaceSubBlock>();
        static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
        foreach (var ((x, y), cell) in cells.OrderBy(c => c.Key.Y).ThenBy(c => c.Key.X))
        {
            var bk = (FloorDiv(x, 32), FloorDiv(y, 32));
            var sk = (FloorDiv(x, 8), FloorDiv(y, 8));
            if (!blocks.TryGetValue(bk, out var block))
            {
                block = new WorldspaceBlock { BlockNumberX = (short)bk.Item1, BlockNumberY = (short)bk.Item2, GroupType = GroupTypeEnum.ExteriorCellBlock };
                blocks[bk] = block;
                ws.SubCells.Add(block);
            }
            if (!subBlocks.TryGetValue(sk, out var sub))
            {
                sub = new WorldspaceSubBlock { BlockNumberX = (short)sk.Item1, BlockNumberY = (short)sk.Item2, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
                subBlocks[sk] = sub;
                block.Items.Add(sub);
            }
            sub.Items.Add(cell);
        }
        mod.Worldspaces.Add(ws);
        exteriorCells = cells;   // GetPCCell's exterior cells
        AddRegions(skyrim, ws, cells);

        // Navmeshes outdoors: Morrowind's sea level is 0.
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        var (navCount, navTriangles) = navmeshes.BuildExteriorCells(ws, cells,
            (x, y) => solids.TryGetValue((x, y), out var list) ? list.SelectMany(s => s.Mesh.Solid.Select(t => t.Select(v => ToWorld(v, s.Ref)).ToArray())) : [],
            c => c.Flags.HasFlag(Cell.Flag.HasWater) ? 0f : null,
            top.Persistent.OfType<PlacedObject>().Where(p => !scriptPersistent.Contains(p)).Cast<IPlacedObjectGetter>().ToList(), Problems);
        ExteriorNavmeshes += navCount;
        NavmeshTriangles += navTriangles;
        ExteriorEdgeLinks = navmeshes.LinkExteriorEdges();

        // Distant terrain.
        var lod = new Skyrim.TerrainLod(mod, modOutDir, skyrimData);
        lod.Build(ws.EditorID!, cells, c => c.Flags.HasFlag(Cell.Flag.HasWater) ? 0f : null, -2048);
        TerrainLodChunks = lod.Chunks;
        Problems.AddRange(lod.Problems);
        return ws;
    }

    public int TerrainLodChunks { get; private set; }

    public int ExteriorNavmeshes { get; private set; }
    public int ExteriorEdgeLinks { get; private set; }

    /// <summary>Morrowind weather (REGN WEAT chances, in order) -> Skyrim weather; ash and blight storms are Solstheim's
    /// ash storm from Dragonborn.</summary>
    static readonly FormKey[] RegionWeathers =
    [
        new(ModKey.FromFileName("Skyrim.esm"), 0x00081A),      // clear: SkyrimClear
        new(ModKey.FromFileName("Skyrim.esm"), 0x012F89),      // cloudy: SkyrimCloudy
        new(ModKey.FromFileName("Skyrim.esm"), 0x0C821E),      // foggy: SkyrimFog
        new(ModKey.FromFileName("Skyrim.esm"), 0x012F89),      // overcast: SkyrimCloudy
        new(ModKey.FromFileName("Skyrim.esm"), 0x0C821F),      // rain: SkyrimOvercastRain
        new(ModKey.FromFileName("Skyrim.esm"), 0x0C8220),      // thunder: SkyrimStormRain
        new(ModKey.FromFileName("Dragonborn.esm"), 0x032336),  // ash storm: DLC02VolcanicAshStorm01
        new(ModKey.FromFileName("Dragonborn.esm"), 0x032336),  // blight storm
        new(ModKey.FromFileName("Skyrim.esm"), 0x04D7FB),      // snow: SkyrimOvercastSnow
        new(ModKey.FromFileName("Skyrim.esm"), 0x0C8221),      // blizzard: SkyrimStormSnow
    ];

    /// <summary>
    /// Each Morrowind region (REGN) as a Skyrim region over the cells that name it (RGNN), with Morrowind's weather
    /// chances; each cell also lists its region, as the Creation Kit does. Based on a vanilla region record so its
    /// header is valid; its areas, objects, grass, sounds and map data are replaced or cleared.
    /// </summary>
    void AddRegions(ISkyrimModGetter skyrim, Worldspace ws, Dictionary<(int X, int Y), Cell> cells)
    {
        var template = skyrim.Regions.First(r => r.Weather is { Weathers.Count: > 0 });
        var byRegion = world.Exteriors.Values.Where(c => c.Region is not null).GroupBy(c => c.Region!, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byRegion)
        {
            if (!world.Objects.TryGetValue(group.Key, out var rec) || rec.Type != "REGN") continue;
            var region = mod.Regions.DuplicateInAsNewRecord(template);
            region.EditorID = "MWRegion" + Sanitize(group.Key);
            region.Worldspace = ws.ToNullableLink<IWorldspaceGetter>();
            region.RegionAreas.Clear();
            region.Objects = null;
            region.Grasses = null;
            region.Sounds = null;
            region.Land = null;
            region.Map = null;
            if (rec.Get("CNAM") is { } color) region.MapColor = System.Drawing.Color.FromArgb(color.Data[0], color.Data[1], color.Data[2]);
            var chances = rec.Get("WEAT")?.Data ?? [];
            region.Weather!.Weathers = [];
            for (int i = 0; i < Math.Min(chances.Length, RegionWeathers.Length); i++)
            {
                if (chances[i] == 0) continue;
                var existing = region.Weather.Weathers.FirstOrDefault(w => w.Weather.FormKey == RegionWeathers[i]);
                if (existing is not null) existing.Chance += chances[i];
                else region.Weather.Weathers.Add(new WeatherType { Weather = new FormLink<IWeatherGetter>(RegionWeathers[i]), Chance = chances[i] });
            }
            // One area per run of neighbouring cells in a row (Morrowind cells, 8192 units).
            foreach (var row in group.GroupBy(c => c.GridY))
            {
                var xs = row.Select(c => c.GridX).Order().ToList();
                for (int i = 0; i < xs.Count;)
                {
                    int j = i;
                    while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
                    float x0 = xs[i] * MwTerrain.MwCellUnits, x1 = (xs[j] + 1) * MwTerrain.MwCellUnits;
                    float y0 = row.Key * MwTerrain.MwCellUnits, y1 = (row.Key + 1) * MwTerrain.MwCellUnits;
                    region.RegionAreas.Add(new RegionArea { EdgeFallOff = 0, RegionPointListData = [new P2Float(x0, y0), new P2Float(x0, y1), new P2Float(x1, y1), new P2Float(x1, y0)] });
                    i = j + 1;
                }
            }
            foreach (var c in group)
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                        if (cells.TryGetValue((c.GridX * 2 + i, c.GridY * 2 + j), out var cell))
                        {
                            cell.Regions ??= [];
                            cell.Regions.Add(region.ToLink<IRegionGetter>());
                        }
        }
    }

    /// <summary>
    /// Makes Morrowind's teleporting doors Skyrim load doors. Morrowind stores only where a door takes you (DODT:
    /// position and rotation, DNAM: the interior cell, absent for the outside); Skyrim also needs the door on the other
    /// side. That is the door in the destination cell that leads back to this one, nearest the arrival point (else the
    /// nearest door there). The player arrives at Morrowind's own spot. Doors to cells not built yet (the exterior,
    /// for now) stay closed. Returns (linked, unlinked).
    /// </summary>
    public (int Linked, int Unlinked) LinkDoors()
    {
        var byCell = doors.GroupBy(d => d.Cell, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int linked = 0, unlinked = 0;
        foreach (var (r, placed, cell) in doors.ToList())
        {
            if (r.DoorDestination is not { } dest) continue;
            string destCell = r.DoorDestinationCell ?? Exterior;
            if (!byCell.TryGetValue(destCell, out var there))
            {
                // A one-way door (Mortrag Glacier's maze gate into Huntsman's Hall, left by script in Morrowind): Skyrim
                // load doors need a door on the other side, so put a copy of this one just behind the arrival point,
                // leading back.
                if (!interiorsByName.TryGetValue(destCell, out var destination)) { unlinked++; Problems.Add($"door {r.BaseId} in {cell} at {r.Position} leads to \"{destCell}\", which isn't built"); continue; }
                float yaw = dest.Rotation.Z;
                var back = new PlacedObject(mod)
                {
                    Base = placed.Base,
                    Placement = new Placement
                    {
                        Position = new P3Float(dest.Position.X - MathF.Sin(yaw) * 96, dest.Position.Y - MathF.Cos(yaw) * 96, dest.Position.Z),
                        Rotation = new P3Float(0, 0, yaw),
                    },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                    TeleportDestination = new TeleportDestination
                    {
                        Door = placed.ToLink<IPlacedObjectGetter>(),
                        Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z),
                        Rotation = new P3Float(r.Rotation.X, r.Rotation.Y, r.Rotation.Z),
                    },
                };
                destination.Persistent.Add(back);
                there = [(r, back, destCell)];
                byCell[destCell] = there;
                Problems.Add($"door {r.BaseId} in {cell}: one-way into \"{destCell}\", given a return door");
            }
            var partner = there
                .OrderBy(d => string.Equals(d.Ref.DoorDestinationCell ?? Exterior, cell, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => Vector3.Distance(d.Ref.Position, dest.Position))
                .First();
            placed.TeleportDestination = new TeleportDestination
            {
                Door = partner.Placed.ToLink<IPlacedObjectGetter>(),
                Position = new P3Float(dest.Position.X, dest.Position.Y, dest.Position.Z),
                Rotation = new P3Float(dest.Rotation.X, dest.Rotation.Y, dest.Rotation.Z),
            };
            linked++;
        }
        return (linked, unlinked);
    }

    void AddInteriorCell(Cell cell)
    {
        int blockNo = (int)(cell.FormKey.ID % 10), subNo = (int)(cell.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
        if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNo);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    public int SwingDoorTwinCount { get; private set; }

    public void Write()
    {
        // Every spell exists (for spell merchants and the player's start), not only those someone carries.
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "SPEL").ToList()) MwSpell(rec.Id);
        FinishMagic();
        FinishLaw();
        FinishLocks();
        SwingDoorTwinCount = Skyrim.SwingDoorTwins.AddAll(mod);
        // Morrowind's recorded barks: after every other record, so those keep their FormIDs.
        BuildBarks();
        Directory.CreateDirectory(modOutDir);
        // Masters in load order (Mutagen's default is alphabetical, which would list Dragonborn before Skyrim.esm).
        var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
        TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
        mod.WriteToBinary(Path.Combine(modOutDir, PluginName), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
        });
        // What procedural creatures may use from Morrowind (read by the creatures step when that option is on).
        WriteCreatureCatalog(Path.Combine(modOutDir, PluginName));
    }
}
