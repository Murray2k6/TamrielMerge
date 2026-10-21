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
    /// <summary>
    /// Placed collision faces as the navmesh should see them: as Skyrim collides, each face is a slab 12 units thick
    /// behind it (BlockMeshBuilder.ConvexSlab), so the face as wound and the slab's back face, wound the other way, 12
    /// behind it. A face wound inside out is then a ceiling under a floor 12 over it, as in the game (2026-10-20:
    /// Hawkmoth Towers' spiral stairs, whose shell is wound inside out, had no navmesh on any flight; their pathgrid
    /// points stood over faces facing down).
    /// </summary>
    static IEnumerable<Vector3[]> AsSlabs(IEnumerable<Vector3[]> faces)
    {
        foreach (var t in faces)
        {
            yield return t;
            var c = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
            if (c.LengthSquared() < 1e-8f) continue;
            var back = Vector3.Normalize(c) * -12f;
            yield return [t[0] + back, t[2] + back, t[1] + back];
        }
    }

    /// <summary>Interiors given an invisible floor (<see cref="GiveFloor"/>), with how.</summary>
    public List<string> FlooredInteriors { get; } = [];

    /// <summary>
    /// A floor for an interior Morrowind left with nothing to walk on (<see cref="Skyrim.Navmesh.InvisibleFloors"/>): from
    /// its pathgrid (PGRD, the last plugin's), else from what stands in it; placed in the cell and its triangles added to
    /// <paramref name="into"/>. False when there is nothing to put one under.
    /// </summary>
    bool GiveFloor(Cell cell, Tes3Cell source, List<Vector3[]> into)
    {
        var points = new List<Vector3>();
        var links = new List<(int, int)>();
        foreach (var plugin in world.Plugins)
            foreach (var rec in plugin.Records.Where(r => r.Type == "PGRD" && string.Equals(r.Str("NAME"), source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (rec.Get("PGRP") is not { } pgrp) continue;
                points.Clear(); links.Clear();
                var counts = new List<int>();
                for (int k = 0; k + 16 <= pgrp.Data.Length; k += 16)
                {
                    points.Add(new(BitConverter.ToInt32(pgrp.Data, k), BitConverter.ToInt32(pgrp.Data, k + 4), BitConverter.ToInt32(pgrp.Data, k + 8)));
                    counts.Add(pgrp.Data[k + 13]);
                }
                if (rec.Get("PGRC") is { } pgrc)
                {
                    int at = 0;
                    for (int i = 0; i < points.Count; i++)
                        for (int c = 0; c < counts[i] && at + 4 <= pgrc.Data.Length; c++, at += 4)
                            if (BitConverter.ToInt32(pgrc.Data, at) is var j && j > i && j < points.Count) links.Add((i, j));
                }
            }
        var stands = new List<Vector3>();
        var rests = new List<Vector3>();
        foreach (var r in source.Refs)
        {
            string type = world.Objects.TryGetValue(r.BaseId, out var b) ? b.Type : "";
            if (type is "NPC_" or "CREA" or "LEVC") { stands.Add(r.Position); continue; }
            if (world.Model(r.BaseId) is not { Length: > 0 } model || converted.GetValueOrDefault(model) is not { } mesh) continue;
            var bottom = r.Position with { Z = r.Position.Z + mesh.Min.Z * r.Scale };
            bool stand = type is "DOOR" or "CONT" || (mesh.Max.Z - mesh.Min.Z) * r.Scale >= 40;
            (stand ? stands : rests).Add(bottom);
        }
        var floor = Skyrim.Navmesh.InvisibleFloors.Plan(points, links, stands, rests, out string how);
        if (floor.Count == 0) return false;
        Skyrim.Navmesh.InvisibleFloors.Place(mod, modOutDir, "mw", "MW", cell, floor);
        into.AddRange(floor.Select(f => f.Points));
        FlooredInteriors.Add($"{cell.EditorID} ({how})");
        return true;
    }

    /// <summary>Interiors turned as one piece by <see cref="TipBack"/>: the point they turned about and the turn (row vectors).</summary>
    readonly Dictionary<string, (Vector3 Pivot, Matrix4x4 Turn)> tipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Interiors <see cref="TipBack"/> turned, with their hull's slope before and after.</summary>
    public List<string> TippedInteriors { get; } = [];

    /// <summary>
    /// A wreck too steep to walk in Skyrim tipped back (user, 2026-10-20: "Tip back to ~35"): when one piece holds most of
    /// an interior's floor and stands tilted more than 40 degrees (Morrowind's shipwrecks: Remote's hull 66, Unmarked's 51,
    /// Lonely's, Lost's and Neglected's 45-46, none with a pathgrid point; Skyrim's player walks slopes to 45), the whole
    /// interior is turned about that piece so its floor slopes 35, the way it fell: every object with it, actors moved and
    /// kept upright. Doors leading in arrive where the turn puts their arrival point (LinkDoors).
    /// </summary>
    void TipBack(Tes3Cell source)
    {
        const float target = 35, over = 40;
        // Each piece's floor: its faces up in the model (within 25 degrees), by its scale.
        (Tes3Ref Ref, float Area)? hull = null;
        float total = 0;
        foreach (var r in source.Refs)
        {
            if (world.Model(r.BaseId) is not { Length: > 0 } model || Mesh(model) is not { } mesh) continue;
            float area = mesh.Solid.Sum(t => { var n = Vector3.Cross(t[1] - t[0], t[2] - t[0]); float a = n.Length() / 2; return a > 0 && n.Z / (2 * a) > 0.9f ? a : 0; }) * r.Scale * r.Scale;
            total += area;
            if (hull is null || area > hull.Value.Area) hull = (r, area);
        }
        if (hull is not { } h || h.Area < 200_000 || h.Area < total / 2) return;
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, Skyrim.SkyrimRotation.Matrix(h.Ref.Rotation)));
        float tilt = MathF.Acos(Math.Clamp(up.Z, -1, 1)) * 180 / MathF.PI;
        if (tilt <= over || tilt >= 90) return;
        // Turned about the axis across its fall, toward upright, by what is over the target.
        var axis = Vector3.Normalize(Vector3.Cross(up, Vector3.UnitZ));
        var turn = Matrix4x4.CreateFromAxisAngle(axis, (tilt - target) * MathF.PI / 180);
        float after = MathF.Acos(Math.Clamp(Vector3.TransformNormal(up, turn).Z, -1, 1)) * 180 / MathF.PI;
        var pivot = h.Ref.Position;
        foreach (var r in source.Refs)
        {
            r.Position = Vector3.Transform(r.Position - pivot, turn) + pivot;
            bool actor = world.Objects.TryGetValue(r.BaseId, out var b) && b.Type is "NPC_" or "CREA" or "LEVC";
            if (!actor) r.Rotation = Skyrim.SkyrimRotation.Angles(Skyrim.SkyrimRotation.Matrix(r.Rotation) * turn);
        }
        tipped[source.Name] = (pivot, turn);
        TippedInteriors.Add($"{source.Name} ({h.Ref.BaseId} {tilt:F0} -> {after:F0} deg)");
    }

    public Cell BuildInterior(Tes3Cell source)
    {
        TipBack(source);
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
        if (source.HasWater) cell.Water = MwWater().ToNullableLink<IWaterGetter>();
        // An interior that behaves like an exterior (Mournhold's districts): the sky, and its region's weather.
        if (source.QuasiExterior && source.Region is { } skyRegionName && SkyRegion(skyRegionName) is { } skyRegion)
        {
            cell.Flags |= Cell.Flag.ShowSky;
            cell.SkyAndWeatherFromRegion.SetTo(skyRegion);
            SkyInteriors++;
        }
        var solid = new List<Vector3[]>();
        var hazard = new List<Vector3[]>();
        var visible = new List<(string Model, Vector3[] Tri)>();
        foreach (var r in source.Refs)
        {
            foreach (var fx in Effects(r)) cell.Temporary.Add(fx);
            if (InterventionMarker(r, cell, source.Name) || PrisonMarker(r, cell, source.Name)) continue;
            if (PlaceableFor(r) is not { } b) continue;
            if (world.Model(r.BaseId) is { Length: > 0 } model && converted.GetValueOrDefault(model) is { } mesh)
            {
                solid.AddRange(mesh.Solid.Select(t => t.Select(v => ToWorld(v, r)).ToArray()));
                if (Hurting.Contains(r.BaseId)) hazard.AddRange(mesh.Solid.Select(t => t.Select(v => ToWorld(v, r)).ToArray()));
                if (Environment.GetEnvironmentVariable("TM_MW_PROBE")?.Split(',') is [var pc, var px, var py] && pc == "MW" + Sanitize(source.Name)
                    && float.TryParse(px, out var qx) && float.TryParse(py, out var qy))
                    foreach (var w in mesh.Solid.Select(t => t.Select(v => ToWorld(v, r)).ToArray()))
                    {
                        float d = (w[1].Y - w[2].Y) * (w[0].X - w[2].X) + (w[2].X - w[1].X) * (w[0].Y - w[2].Y);
                        if (MathF.Abs(d) < 1e-6f) continue;
                        float a = ((w[1].Y - w[2].Y) * (qx - w[2].X) + (w[2].X - w[1].X) * (qy - w[2].Y)) / d, bb = ((w[2].Y - w[0].Y) * (qx - w[2].X) + (w[0].X - w[2].X) * (qy - w[2].Y)) / d;
                        if (a < 0 || bb < 0 || a + bb > 1) continue;
                        Console.WriteLine($"  probe object {r.BaseId} {model} (collision node: {mesh.FromCollisionNode}): height {a * w[0].Z + bb * w[1].Z + (1 - a - bb) * w[2].Z:F0}, normal z {Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0])).Z:F2}");
                    }
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
        BuildNavmesh(cell, solid, hazard, source);
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

    /// <summary>A model-space point placed by a reference: scaled, rotated (<see cref="Skyrim.SkyrimRotation"/>, which
    /// Morrowind's angles share) and moved.</summary>
    static Vector3 ToWorld(Vector3 v, Tes3Ref r) => Vector3.Transform(v * r.Scale, Skyrim.SkyrimRotation.Matrix(r.Rotation)) + r.Position;

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

    /// <summary>Interior actors checked against their navmesh, and those within 64 units of it.</summary>
    public int ActorsChecked { get; private set; }
    public int ActorsOnNavmesh { get; private set; }
    public int Navmeshes { get; private set; }
    public int NavmeshTriangles { get; private set; }

    /// <summary>
    /// The interior's navmesh (Recast over its static collision, as for Daggerfall's dungeons), so NPCs and creatures
    /// can walk; load doors are linked, and the areas around actors are kept even when nothing else connects to them.
    /// Items, swinging doors and actors aren't part of the collision, so it runs through doorways.
    /// </summary>
    void BuildNavmesh(Cell cell, List<Vector3[]> solid, List<Vector3[]> hazard, Tes3Cell? source = null)
    {
        // No collision at all in it: a floor of its own (InvisibleFloors).
        if (solid.Count == 0 && (source is null || !GiveFloor(cell, source, solid))) return;
        if (Environment.GetEnvironmentVariable("TM_MW_WINDING") == "1") Console.WriteLine($"  winding {cell.EditorID}: {Skyrim.Navmesh.NavmeshBuilder.Winding(solid)}");
        // TM_MW_PROBE=<EditorID>,x,y: every collision triangle over that point, its height there and which way it faces.
        if (Environment.GetEnvironmentVariable("TM_MW_PROBE")?.Split(',') is [var pc, var px, var py] && pc == cell.EditorID && float.TryParse(px, out var qx) && float.TryParse(py, out var qy))
            foreach (var w in solid)
            {
                float d = (w[1].Y - w[2].Y) * (w[0].X - w[2].X) + (w[2].X - w[1].X) * (w[0].Y - w[2].Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float a = ((w[1].Y - w[2].Y) * (qx - w[2].X) + (w[2].X - w[1].X) * (qy - w[2].Y)) / d, b = ((w[2].Y - w[0].Y) * (qx - w[2].X) + (w[0].X - w[2].X) * (qy - w[2].Y)) / d;
                if (a < 0 || b < 0 || a + b > 1) continue;
                Console.WriteLine($"  probe {cell.EditorID} {qx},{qy}: height {a * w[0].Z + b * w[1].Z + (1 - a - b) * w[2].Z:F0}, normal z {Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0])).Z:F2}");
            }
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        // Each face as wound (a ceiling or an underside is an obstacle, not a floor; Morrowind's collision is wound
        // consistently, 2026-10-19, all 1,335 interiors: 10,784,047 edges shared oppositely, 4,339 the same way), or
        // every face turned up where that puts more actors on the navmesh (NavmeshBuilder.BuildInterior).
        var raw = solid.Select(t => (t, Vector3.Normalize(Vector3.Cross(t[1] - t[0], t[2] - t[0])))).Where(p => !float.IsNaN(p.Item2.X)).ToList();
        var polygons = AsSlabs(solid).Select(t => (t, Vector3.Normalize(Vector3.Cross(t[1] - t[0], t[2] - t[0])))).Where(p => !float.IsNaN(p.Item2.X)).ToList();
        var turnedUp = raw.Select(p => (p.t, p.Item2.Z < 0 ? -p.Item2 : p.Item2)).ToList();
        // Every persistent placed object here except the start marker is a load door (linked to its partner later).
        var loadDoors = cell.Persistent.OfType<PlacedObject>().Where(d => d != StartMarker && !scriptPersistent.Contains(d)).Cast<IPlacedObjectGetter>().ToList();
        var actorsHere = cell.Temporary.Concat(cell.Persistent).OfType<PlacedNpc>().Select(n => n.Placement!.Position).ToList();
        try
        {
            // What hurts to stand on (Hurting: the lava pools) is no floor.
            var hazards = hazard.Count > 0 ? new Skyrim.Navmesh.NavmeshBuilder.Hazards(new Skyrim.Navmesh.WoundInput(hazard), null) : null;
            if (hazards is not null) HazardCells++;
            Skyrim.Navmesh.NavmeshBuilder.Result r;
            try { r = navmeshes.BuildInterior(cell, polygons, turnedUp, loadDoors, StartMarker.Placement is { } sp && cell.Persistent.Contains(StartMarker) ? sp.Position : null, actorsHere, hazards); }
            catch (InvalidOperationException e) when (e.Message.StartsWith("no floor to walk on") && source is not null)
            {
                // Nothing to walk on in it (Bloodmoon's "Solstheim" cell: Molag Bal's statue and one actor): a floor of its
                // own, then the navmesh on it.
                var added = new List<Vector3[]>();
                if (!GiveFloor(cell, source, added)) throw;
                foreach (var t in added) { var nn = Vector3.Normalize(Vector3.Cross(t[1] - t[0], t[2] - t[0])); polygons.Add((t, nn)); turnedUp.Add((t, nn)); }
                r = navmeshes.BuildInterior(cell, polygons, turnedUp, loadDoors, StartMarker.Placement is { } sp2 && cell.Persistent.Contains(StartMarker) ? sp2.Position : null, actorsHere, hazards);
            }
            Navmeshes++;
            NavmeshTriangles += r.Triangles;
            // Who stands on it: the cell's actors within 64 units of a navmesh triangle.
            if (r.Navmesh.Data is { } nd)
                foreach (var a in actorsHere)
                {
                    float far = Skyrim.Navmesh.NavmeshBuilder.Distance(nd, a);
                    ActorsChecked++;
                    if (far <= 64) ActorsOnNavmesh++;
                }
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
    /// in. Doors are persistent and live in the worldspace's persistent cell, as Skyrim keeps them. Weather, climate
    /// and water are Morrowind's own (<see cref="MwWeathers"/>, <see cref="MwClimate"/>, <see cref="MwWater"/>): each
    /// region its climate and weather chances, the sea Morrowind's water at its sea level 0. Returns the worldspace.
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
        // The worldspace's climate (where no region's weather applies): Morrowind's weathers by how much of the land
        // each region's chances cover.
        var mixed = new int[WeatherKinds.Length];
        int regionCells = 0;
        foreach (var c in world.Exteriors.Values)
            if (c.Region is { } rn && world.Objects.TryGetValue(rn, out var rr) && rr.Type == "REGN")
            {
                var ch = Chances(rr);
                for (int i = 0; i < Math.Min(ch.Length, mixed.Length); i++) mixed[i] += ch[i];
                regionCells++;
            }
        var wsClimate = MwClimate("MWClimateVvardenfell", regionCells > 0 ? mixed.Select(m => (int)Math.Round(m / (double)regionCells)).ToArray() : [100]);
        var water = MwWater();
        var ws = new Worldspace(mod)
        {
            EditorID = "MWVvardenfell",
            Name = "Vvardenfell",
            Climate = wsClimate.ToNullableLink<IClimateGetter>(),
            Water = water.ToNullableLink<IWaterGetter>(),
            LodWater = water.ToNullableLink<IWaterGetter>(),
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

        // What scripts take away for good is no obstacle to the navmesh: the state it is in for nearly all of play.
        var gone = DisabledForGood();
        int goneRefs = 0;
        var goneIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in solids.Values)
            goneRefs += list.RemoveAll(s =>
            {
                if (!gone.Contains(s.Ref.BaseId)) return false;
                goneIds[s.Ref.BaseId] = goneIds.GetValueOrDefault(s.Ref.BaseId) + 1;
                return true;
            });
        ExteriorGoneForGood = goneRefs;
        ExteriorGoneIds = string.Join(", ", goneIds.OrderByDescending(g => g.Value).Select(g => $"{g.Key} ({g.Value})"));

        // TM_MW_WINDING=1 (dev aid): how the placed models' collision is wound, by model and by reference: every shared
        // edge run oppositely (closed, wound outward or inside out; open, its top facing up or down) or some the same way.
        if (Environment.GetEnvironmentVariable("TM_MW_WINDING") == "1")
        {
            var byModel = solids.Values.SelectMany(l => l).GroupBy(s => s.Mesh.Path).ToList();
            var classes = new Dictionary<string, (int Models, int Refs, long Tris)>();
            var mixedList = new List<(string Path, int Refs, string Note)>();
            foreach (var g in byModel)
            {
                var w = Skyrim.Navmesh.NavmeshBuilder.WindingOf(g.First().Mesh.Solid);
                string kind = w.Inconsistent > 0 ? "mixed" : w.Open == 0 ? (w.Volume >= 0 ? "closed, outward" : "closed, inside out") : (w.TopUp >= 0 ? "open, top facing up" : "open, top facing down");
                var c = classes.GetValueOrDefault(kind);
                classes[kind] = (c.Models + 1, c.Refs + g.Count(), c.Tris + (long)w.Count * g.Count());
                if (kind != "closed, outward" && kind != "open, top facing up") mixedList.Add((g.Key, g.Count(), $"{kind}: {Skyrim.Navmesh.NavmeshBuilder.Winding(g.First().Mesh.Solid)}"));
            }
            foreach (var (kind, c) in classes.OrderByDescending(k => k.Value.Refs))
                Console.WriteLine($"  exterior winding: {kind}: {c.Models} models, {c.Refs} references, {c.Tris:N0} placed triangles");
            foreach (var m in mixedList.OrderByDescending(m => m.Refs).Take(15)) Console.WriteLine($"    {m.Path} x{m.Refs}: {m.Note}");
        }

        // TM_MW_PROBE=exterior,x,y[;x,y...] (dev aid): every placed object's collision triangle over those points, its
        // height there, which way it faces, its corners and the object it is of.
        if (Environment.GetEnvironmentVariable("TM_MW_PROBE") is { } exteriorProbe && exteriorProbe.StartsWith("exterior,"))
            foreach (var point in exteriorProbe["exterior,".Length..].Split(';'))
            {
                if (point.Split(',') is not [var epx, var epy] || !float.TryParse(epx, out var eqx) || !float.TryParse(epy, out var eqy)) continue;
                int pcx = (int)MathF.Floor(eqx / 4096), pcy = (int)MathF.Floor(eqy / 4096);
                for (int ndy = -1; ndy <= 1; ndy++)
                    for (int ndx = -1; ndx <= 1; ndx++)
                        if (solids.TryGetValue((pcx + ndx, pcy + ndy), out var probed))
                            foreach (var s in probed)
                                foreach (var w in s.Mesh.Solid.Select(t => t.Select(v => ToWorld(v, s.Ref)).ToArray()))
                                {
                                    float d = (w[1].Y - w[2].Y) * (w[0].X - w[2].X) + (w[2].X - w[1].X) * (w[0].Y - w[2].Y);
                                    if (MathF.Abs(d) < 1e-6f) continue;
                                    float a = ((w[1].Y - w[2].Y) * (eqx - w[2].X) + (w[2].X - w[1].X) * (eqy - w[2].Y)) / d, bb = ((w[2].Y - w[0].Y) * (eqx - w[2].X) + (w[0].X - w[2].X) * (eqy - w[2].Y)) / d;
                                    if (a < 0 || bb < 0 || a + bb > 1) continue;
                                    Console.WriteLine($"  probe {eqx},{eqy}: object {s.Ref.BaseId} {world.Model(s.Ref.BaseId)} at {s.Ref.Position} (collision node: {s.Mesh.FromCollisionNode}): height {a * w[0].Z + bb * w[1].Z + (1 - a - bb) * w[2].Z:F0}, " +
                                        $"normal z {Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0])).Z:F2}, corners {string.Join(" ", w.Select(v => $"({v.X:F0},{v.Y:F0},{v.Z:F0})"))}");
                                }
            }

        // Navmeshes outdoors: Morrowind's sea level is 0.
        navmeshes ??= new Skyrim.Navmesh.NavmeshBuilder(mod);
        // Faces as wound where a model's are wound one way, outward (2026-10-19, TM_MW_WINDING over the 1,350 models placed
        // outdoors: 1,338 of them, 99,644 of 99,846 references, every shared edge run oppositely, closed ones wound outward
        // and open ones' tops facing up); turned up, each face, only where a model's winding is mixed or its open shell's
        // top faces down. Every face turned up, the navmesh stood on undersides: 0.17% of it, the inside of Vivec's
        // cantons (ex_vivec_c_02 over 29356,-77877: a face down at 2669 under its roof at 3008, the navmesh at 2682).
        var trusted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Trusted(MwMeshConverter.Result mesh)
        {
            lock (trusted)
            {
                if (trusted.TryGetValue(mesh.Path, out bool known)) return known;
                var w = Skyrim.Navmesh.NavmeshBuilder.WindingOf(mesh.Solid);
                return trusted[mesh.Path] = w.Inconsistent == 0 && (w.Open == 0 ? w.Volume >= 0 : w.TopUp >= 0);
            }
        }
        Vector3[] Placed(Vector3[] t, Tes3Ref r, bool asWound)
        {
            var w = t.Select(v => ToWorld(v, r)).ToArray();
            return asWound || Vector3.Cross(w[1] - w[0], w[2] - w[0]).Z >= 0 ? w : [w[0], w[2], w[1]];
        }
        var (navCount, navTriangles) = navmeshes.BuildExteriorCells(ws, cells,
            // (Not as slabs, AsSlabs, as interiors are: outdoors that left 74% fewer spans marked shut inside something and
            // made 26% more walkable, 2026-10-20 test block, unmeasured where; the winding here is right for 99.8% of references.)
            (x, y) => solids.TryGetValue((x, y), out var list) ? list.SelectMany(s => { bool asWound = Trusted(s.Mesh); return s.Mesh.Solid.Select(t => Placed(t, s.Ref, asWound)); }) : [],
            c => c.Flags.HasFlag(Cell.Flag.HasWater) ? 0f : null,
            top.Persistent.OfType<PlacedObject>().Where(p => !scriptPersistent.Contains(p)).Cast<IPlacedObjectGetter>().ToList(), Problems, asWound: true,
            // What hurts to stand on (Hurting) is no floor.
            hazardsOf: (x, y) => solids.TryGetValue((x, y), out var list) ? list.Where(s => Hurting.Contains(s.Ref.BaseId)).SelectMany(s => s.Mesh.Solid.Select(t => t.Select(v => ToWorld(v, s.Ref)).ToArray())) : []);
        ExteriorWoundTrusted = trusted.Count(t => t.Value);
        ExteriorWoundTurned = trusted.Count(t => !t.Value);
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

    /// <summary>Interiors whose navmesh kept off something that hurts (<see cref="Hurting"/>).</summary>
    public int HazardCells { get; private set; }

    /// <summary>
    /// The objects that hurt whoever stands on or touches them, by ID: those whose script calls HurtStandingActor or
    /// HurtCollidingActor with more than nothing (2026-10-20: Morrowind's lava pools, In_lava_256/512/1024/256A/oval,
    /// whose script "lava" hurts a standing actor 20 a second; placed 315 times indoors, 348 outdoors). The navmesh keeps
    /// off them (NavmeshBuilder.Hazards); their scripts still hurt (TMMwScript.HurtTouching).
    /// </summary>
    HashSet<string> Hurting => hurting ??= FindHurting();
    HashSet<string>? hurting;
    HashSet<string> FindHurting()
    {
        var hurt = new System.Text.RegularExpressions.Regex(@"(?im)^\s*[^;\r\n]*\bHurt(Standing|Colliding)Actor\b[\s,]*(-?[\d.]+)");
        var scripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in world.Objects.Values.Where(o => o.Type == "SCPT"))
            foreach (System.Text.RegularExpressions.Match m in hurt.Matches(s.Str("SCTX") ?? ""))
                if (float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0) scripts.Add(s.Id);
        return world.Objects.Values.Where(o => o.Str("SCRI") is { Length: > 0 } sc && scripts.Contains(sc)).Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Exterior references left out of the navmeshes as taken away for good (<see cref="DisabledForGood"/>).</summary>
    public int ExteriorGoneForGood { get; private set; }

    /// <summary>Those references' objects, each with its count, for the build's log.</summary>
    public string ExteriorGoneIds { get; private set; } = "";

    /// <summary>Models placed outdoors whose collision went into the navmeshes as wound, and those turned up.</summary>
    public int ExteriorWoundTrusted { get; private set; }
    public int ExteriorWoundTurned { get; private set; }

    /// <summary>
    /// The objects the character's making takes away and nothing brings back, by ID: a CharGen script's
    /// <c>"id"-&gt;Disable</c>, and the objects whose own CharGen script disables them (a bare <c>Disable</c>), less those
    /// any script or dialogue result enables. Their references are there only until then (2026-10-19: the CharGen script disables
    /// "CharGen Boat", the prison ship at Seyda Neen, when the character is made; in the navmesh it stood over the dock
    /// for good, and Siege at Firemoth's longboat inside its hull had none).
    /// </summary>
    HashSet<string> DisabledForGood()
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targeted = new System.Text.RegularExpressions.Regex(@"(?:""([^""]+)""|([A-Za-z0-9_]+))\s*->\s*(disable|enable)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var bare = new System.Text.RegularExpressions.Regex(@"^\s*(disable|enable)\s*(;.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        // Disables counted only from the character's making, the CharGen scripts every game runs first; what other scripts
        // and dialogue take away is there for much of play (2026-10-19, counted from every script: the Ghostfence,
        // ex_gg_fence_*, standing until the main quest ends; Raven Rock's colony trees and rocks, cleared as it is built;
        // Solstheim's snow doors), so it stays an obstacle. Enables are counted from everything.
        static bool AtStart(string script) => script.StartsWith("CharGen", StringComparison.OrdinalIgnoreCase);
        void Scan(string text, bool start)
        {
            foreach (System.Text.RegularExpressions.Match m in targeted.Matches(text))
            {
                bool disable = m.Groups[3].Value.Equals("disable", StringComparison.OrdinalIgnoreCase);
                if (disable && !start) continue;
                (disable ? disabled : enabled).Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }
        }
        // Scripts that disable or enable the object running them.
        var selfDisabling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selfEnabling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in world.Objects.Values.Where(o => o.Type == "SCPT"))
        {
            string text = s.Str("SCTX") ?? "";
            Scan(text, AtStart(s.Id));
            foreach (System.Text.RegularExpressions.Match m in bare.Matches(text))
            {
                bool disable = m.Groups[1].Value.Equals("disable", StringComparison.OrdinalIgnoreCase);
                if (disable && !AtStart(s.Id)) continue;
                (disable ? selfDisabling : selfEnabling).Add(s.Id);
            }
        }
        // Not kept in dialogueData: its being there means the dialogue is built (BuildBarks).
        var dialogue = dialogueData ?? MwDialogue.Load(world.Plugins);
        foreach (var info in dialogue.Topics.Values.SelectMany(t => t.Infos))
            if (info.Result is { } result) Scan(result, false);
        foreach (var o in world.Objects.Values)
            if (o.Str("SCRI") is { Length: > 0 } script)
            {
                if (selfDisabling.Contains(script)) disabled.Add(o.Id);
                if (selfEnabling.Contains(script)) enabled.Add(o.Id);
            }
        disabled.ExceptWith(enabled);
        return disabled;
    }

    public int ExteriorNavmeshes { get; private set; }
    public int ExteriorEdgeLinks { get; private set; }

    /// <summary>
    /// Each Morrowind region (REGN) as a Skyrim region over the cells that name it (RGNN), with Morrowind's own
    /// weathers (<see cref="MwWeathers"/>) at its chances, and a climate of its own (MWClimate&lt;region&gt;) holding the
    /// same; each cell also lists its region, as the Creation Kit does. Based on a vanilla region record so its header
    /// is valid; its areas, objects, grass, sounds and map data are replaced or cleared.
    /// </summary>
    void AddRegions(ISkyrimModGetter skyrim, Worldspace ws, Dictionary<(int X, int Y), Cell> cells)
    {
        var template = skyrim.Regions.First(r => r.Weather is { Weathers.Count: > 0 });
        var weathers = MwWeathers();
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
            var chances = Chances(rec);
            region.Weather!.Weathers = [];
            for (int i = 0; i < Math.Min(chances.Length, weathers.Length); i++)
                if (chances[i] > 0) region.Weather.Weathers.Add(new WeatherType { Weather = weathers[i].ToLink<IWeatherGetter>(), Chance = chances[i] });
            MwClimate("MWClimate" + Sanitize(group.Key), chances);
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

    readonly Dictionary<string, Region?> skyRegions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The region an interior that behaves like an exterior takes its sky and weather from (Skyrim's interiors with
    /// Show Sky take them from a region: 268 of vanilla's 286, vanilla-interior-sky, Apocrypha among them): Morrowind's
    /// region (REGN) of that name, with Morrowind's own weathers at its chances, in no worldspace (2026-10-20, mw-quasi:
    /// Mournhold's five districts, Godsreach, the Great Bazaar, Plaza Brindisi Dorom, the Royal Palace's courtyard and the
    /// Temple Courtyard, name "Mournhold Region", which no exterior cell names: clear 25, cloudy 35, foggy 5, overcast
    /// 20, rain 10, thunder 5). Null when there is no such region.
    /// </summary>
    Region? SkyRegion(string name)
    {
        if (skyRegions.TryGetValue(name, out var known)) return known;
        Region? region = null;
        if (world.Objects.TryGetValue(name, out var rec) && rec.Type == "REGN" && SkyrimEsm.Regions.FirstOrDefault(r => r.Weather is { Weathers.Count: > 0 }) is { } template)
        {
            var weathers = MwWeathers();
            region = mod.Regions.DuplicateInAsNewRecord(template);
            region.EditorID = "MWRegion" + Sanitize(name);
            region.Worldspace.Clear();
            region.RegionAreas.Clear();
            region.Objects = null;
            region.Grasses = null;
            region.Sounds = null;
            region.Land = null;
            region.Map = null;
            if (rec.Get("CNAM") is { } color) region.MapColor = System.Drawing.Color.FromArgb(color.Data[0], color.Data[1], color.Data[2]);
            var chances = Chances(rec);
            region.Weather!.Weathers = [];
            for (int i = 0; i < Math.Min(chances.Length, weathers.Length); i++)
                if (chances[i] > 0) region.Weather.Weathers.Add(new WeatherType { Weather = weathers[i].ToLink<IWeatherGetter>(), Chance = chances[i] });
        }
        return skyRegions[name] = region;
    }

    /// <summary>Interiors that behave like exteriors, given the sky (<see cref="SkyRegion"/>).</summary>
    public int SkyInteriors { get; private set; }

    /// <summary>
    /// Makes Morrowind's teleporting doors Skyrim load doors. Morrowind stores only where a door takes you (DODT:
    /// position and rotation, DNAM: the interior cell, absent for the outside); Skyrim also needs the door on the other
    /// side. That is the door in the destination cell that leads back to this one, nearest the arrival point (else the
    /// nearest door there). The player arrives at Morrowind's own spot. Doors to cells that weren't built (a test build's
    /// --cells or --no-exterior) stay closed. Returns (linked, unlinked).
    /// </summary>
    public (int Linked, int Unlinked) LinkDoors()
    {
        var byCell = doors.GroupBy(d => d.Cell, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int linked = 0, unlinked = 0;
        foreach (var (r, placed, cell) in doors.ToList())
        {
            if (r.DoorDestination is not { } dest) continue;
            string destCell = r.DoorDestinationCell ?? Exterior;
            // Into an interior TipBack turned: the arrival point turned with it (the arrival still upright).
            if (tipped.TryGetValue(destCell, out var tip))
                dest = (Vector3.Transform(dest.Position - tip.Pivot, tip.Turn) + tip.Pivot, dest.Rotation);
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
        // Doors placed here (the backs of one-way doors) and any other without one get their door triangle now.
        DoorTrianglesAdded = navmeshes?.AddMissingDoorTriangles(mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells)) ?? 0;
        return (linked, unlinked);
    }

    /// <summary>Load doors given their door triangle after the navmeshes (<see cref="LinkDoors"/>).</summary>
    public int DoorTrianglesAdded { get; private set; }

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
        // Morrowind's levelling for the Mundus stones (its birthsigns' spell lists go into the plugin).
        var levelling = Levelling();
        BuildTrainers();
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
        // What the progression build offers on the Mundus stones from Morrowind.
        levelling.Save(Path.Combine(modOutDir, PluginName));
        LevellingSummary = $"{levelling.Classes.Count} playable classes, {levelling.Signs.Count} birthsigns, {levelling.Races.Count} races; skill rates major x{levelling.MajorRate:0.##}, "
            + $"minor x{levelling.MinorRate:0.##}, other x{levelling.MiscRate:0.##}, specialization x{levelling.SpecialRate:0.##}; level-up multipliers {string.Join(",", levelling.LevelUp)}; "
            + $"{trainerNpcs.Count} trainers teaching {TrainerSkills} skills; {SkillBooks} skill books";
    }

    /// <summary>What <see cref="Write"/> exported for the Mundus stones.</summary>
    public string LevellingSummary { get; private set; } = "";
}
