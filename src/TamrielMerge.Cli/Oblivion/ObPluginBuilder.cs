using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Builds the Oblivion plugin for Skyrim SE from the original game (<see cref="ObData"/>): Oblivion's own models and
/// textures (<see cref="ObMeshConverter"/>) and its cells rebuilt reference by reference. Oblivion and Skyrim share
/// units, cell size and rotation conventions, so positions carry over unchanged. Records are looked up across every
/// active plugin, a later plugin's version winning.
/// </summary>
public sealed partial class ObPluginBuilder : IDisposable
{
    public const string PluginName = "Oblivion.esm";
    static readonly FormKey LightingTemplate = new(ModKey.FromFileName("Skyrim.esm"), 0x6909B);   // ImperialFortCloseTemplate

    readonly ObData data;
    readonly ObMeshConverter meshes;
    readonly string modOutDir;
    readonly SkyrimMod mod;
    readonly List<Ob.IOblivionModDisposableGetter> plugins = [];
    readonly Dictionary<FormKey, Ob.IOblivionMajorRecordGetter> records = new();
    readonly Dictionary<FormKey, IMajorRecord?> bases = new();
    readonly Dictionary<string, ObMeshConverter.Result?> converted = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The converted model behind each static and door base (for navmesh collision).</summary>
    readonly Dictionary<FormKey, ObMeshConverter.Result> meshByBase = new();
    /// <summary>Particle emitters of converted bases (by Skyrim base), and of Oblivion effect models that couldn't be
    /// converted at all (by Oblivion base), for vanilla stand-in effects.</summary>
    readonly Dictionary<FormKey, List<Skyrim.EffectStandIns.Emitter>> emittersByBase = new(), effectOnly = new();
    public int EffectCount { get; private set; }

    /// <summary>Places vanilla effects at a reference's emitters (upright, whatever the reference's rotation).</summary>
    void PlaceEffects(List<Skyrim.EffectStandIns.Emitter> emitters, Placement placement, float scale, Cell cell)
    {
        var m = System.Numerics.Matrix4x4.CreateRotationX(-placement.Rotation.X) * System.Numerics.Matrix4x4.CreateRotationY(-placement.Rotation.Y) * System.Numerics.Matrix4x4.CreateRotationZ(-placement.Rotation.Z);
        var pos = new System.Numerics.Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z);
        foreach (var (at, effect, fxScale) in Skyrim.EffectStandIns.Pick(emitters, scale))
        {
            var w = System.Numerics.Vector3.Transform(at * scale, m) + pos;
            cell.Temporary.Add(new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(effect),
                Placement = new Placement { Position = new P3Float(w.X, w.Y, w.Z), Rotation = new P3Float(0, 0, 0) },
                Scale = fxScale,
            });
            EffectCount++;
        }
    }
    public List<string> Problems { get; } = [];
    public SkyrimMod Mod => mod;
    public IReadOnlyList<Ob.IOblivionModDisposableGetter> Plugins => plugins;

    /// <summary>The story's start: where Oblivion begins a new game (CGPlayerStartMarker, in the player's prison
    /// cell). Created first so it is always FormID 0x800 in Oblivion.esm, which the story order (TMStoryOrder) looks
    /// up with Game.GetFormFromFile; placed when its cell is built.</summary>
    public PlacedObject StartMarker { get; }
    public const uint StartMarkerId = 0x800;
    const string OblivionStartRef = "CGPlayerStartMarker";
    static readonly FormKey XMarkerHeading = new(ModKey.FromFileName("Skyrim.esm"), 0x34);

    public ObPluginBuilder(ObData data, string modOutDir)
    {
        this.data = data;
        this.modOutDir = modOutDir;
        meshes = new ObMeshConverter(data, new ObTextures(data, modOutDir));
        mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        StartMarker = new PlacedObject(mod)
        {
            EditorID = "OBStartMarker",
            Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,   // persistent
        };
        if (StartMarker.FormKey.ID != StartMarkerId) throw new InvalidOperationException($"start marker got {StartMarker.FormKey}, expected {StartMarkerId:X}");
        foreach (var p in data.Plugins)
        {
            var plugin = Ob.OblivionMod.CreateFromBinaryOverlay(Path.Combine(data.DataDir, p), Ob.OblivionRelease.Oblivion);
            plugins.Add(plugin);
            foreach (var r in plugin.EnumerateMajorRecords())
                if (r is Ob.IOblivionMajorRecordGetter o and not Ob.ICellGetter and not Ob.IPlacedGetter) records[r.FormKey] = o;
        }
    }

    public void Dispose() { foreach (var p in plugins) p.Dispose(); }

    /// <summary>An interior cell by EditorID, as the last plugin that defines it has it.</summary>
    public Ob.ICellGetter? FindInterior(string editorId) =>
        Enumerable.Reverse(plugins)
            .SelectMany(p => p.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            .FirstOrDefault(c => string.Equals(c.EditorID, editorId, StringComparison.OrdinalIgnoreCase));

    static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    ObMeshConverter.Result? Mesh(string model)
    {
        if (converted.TryGetValue(model, out var hit)) return hit;
        try { hit = meshes.Convert(model, modOutDir); }
        catch (Exception e) { Problems.Add($"model {model}: {e.Message}"); hit = null; }
        return converted[model] = hit;
    }

    static ObjectBounds Bounds(ObMeshConverter.Result? r)
    {
        short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        return r is null ? new ObjectBounds() : new ObjectBounds { First = new P3Int16(C(r.Min.X), C(r.Min.Y), C(r.Min.Z)), Second = new P3Int16(C(r.Max.X), C(r.Max.Y), C(r.Max.Z)) };
    }

    static string SkyrimModelPath(string dataRelative) => dataRelative.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? dataRelative[7..] : dataRelative;

    /// <summary>Editor markers (XMarker, heading markers, door and travel markers) aren't placed as objects.</summary>
    static bool IsMarker(string model) => Path.GetFileName(model).StartsWith("Marker", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Skyrim base object for an Oblivion base, created on first use; null for what isn't converted yet
    /// (actors, markers) or has no model.</summary>
    IMajorRecord? Base(FormKey key)
    {
        if (bases.TryGetValue(key, out var hit)) return hit;
        bases[key] = null;
        if (!records.TryGetValue(key, out var rec)) { Problems.Add($"unknown base {key}"); return null; }
        string edid = "OB" + Sanitize(rec.EditorID ?? key.ID.ToString("X6"));
        // Items (with physics) and containers (with their contents) have their own records.
        if (IsItem(rec)) return bases[key] = Item(key);
        if (rec is Ob.IContainerGetter container) return bases[key] = ContainerFor(container, edid);
        if (rec is Ob.ILightGetter l)
        {
            string? model = l.Model?.File;
            var mesh = model is { Length: > 0 } && !IsMarker(model) ? Mesh(model) : null;
            var d = l.Data!;
            Light.Flag flags = 0;
            foreach (var name in d.Flags.ToString().Split(", "))
                if (Enum.TryParse<Light.Flag>(name, out var f)) flags |= f;
            var light = new Light(mod)
            {
                EditorID = edid,
                ObjectBounds = Bounds(mesh),
                Time = d.Time,
                Radius = d.Radius,
                Color = System.Drawing.Color.FromArgb(0, d.Color.R, d.Color.G, d.Color.B),
                FalloffExponent = d.FalloffExponent > 0 ? d.FalloffExponent : 1,
                FOV = d.FOV > 0 ? d.FOV : 90,
                NearClip = 0.001f,
                FadeValue = l.Fade ?? 1,
                Weight = d.Weight,
                Value = d.Value,
                Flags = flags,
                FlickerPeriod = 0.5f,
                FlickerIntensityAmplitude = 0.2f,
                FlickerMovementAmplitude = 4,
            };
            if (l.Name is { Length: > 0 } n) light.Name = n;
            if (mesh is not null) light.Model = new Model { File = SkyrimModelPath(mesh.Path) };
            if (mesh is { Emitters.Count: > 0 }) emittersByBase[light.FormKey] = mesh.Emitters;
            mod.Lights.Add(light);
            return bases[key] = light;
        }
        if (rec is not Ob.IModeledGetter { Model.File: { Length: > 0 } file } || IsMarker(file) || rec is Ob.INpcGetter or Ob.ICreatureGetter)
            return null;
        var converted = Mesh(file);
        if (converted is null)
        {
            // An effect model nifly can't read: a stand-in by its name (FXFireBrazier, FXSteam..., FXMist...).
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("FX", StringComparison.OrdinalIgnoreCase) && Skyrim.EffectStandIns.KindOf(fileName) is var kind and not Skyrim.EffectStandIns.Kind.None)
                effectOnly[key] = [new Skyrim.EffectStandIns.Emitter(System.Numerics.Vector3.Zero, Skyrim.EffectStandIns.TypicalSize(kind), 0, fileName)];
            return null;
        }
        // A scripted activator stays an activator (it carries its converted script); the rest are statics.
        if (rec is Ob.IActivatorGetter act && !act.Script.IsNull && translations.Count > 0)
        {
            var activator = new Mutagen.Bethesda.Skyrim.Activator(mod) { EditorID = edid, ObjectBounds = Bounds(converted), Model = new Model { File = SkyrimModelPath(converted.Path) } };
            if (act.Name is { Length: > 0 } an) activator.Name = an;
            mod.Activators.Add(activator);
            meshByBase[activator.FormKey] = converted;
            if (converted.Emitters.Count > 0) emittersByBase[activator.FormKey] = converted.Emitters;
            return bases[key] = activator;
        }
        var stat = new Static(mod) { EditorID = edid, ObjectBounds = Bounds(converted), Model = new Model { File = SkyrimModelPath(converted.Path) } };
        mod.Statics.Add(stat);
        meshByBase[stat.FormKey] = converted;
        if (converted.Emitters.Count > 0) emittersByBase[stat.FormKey] = converted.Emitters;
        return bases[key] = stat;
    }

    /// <summary>Oblivion's interior lighting (ambient, directional, fog) as Skyrim cell lighting.</summary>
    static CellLighting Lighting(Ob.ICellLightingGetter? l)
    {
        System.Drawing.Color C(System.Drawing.Color c) => System.Drawing.Color.FromArgb(0, c.R, c.G, c.B);
        var ambient = l is null ? System.Drawing.Color.Black : C(l.AmbientColor);
        return new CellLighting
        {
            AmbientColor = ambient,
            DirectionalColor = l is null ? System.Drawing.Color.Black : C(l.DirectionalColor),
            DirectionalFade = l?.DirectionalFade ?? 1,
            DirectionalRotationXY = l?.DirectionalRotationXY ?? 0,
            DirectionalRotationZ = l?.DirectionalRotationZ ?? 0,
            FogNearColor = l is null ? System.Drawing.Color.Black : C(l.FogColor),
            FogFarColor = l is null ? System.Drawing.Color.Black : C(l.FogColor),
            FogNear = l?.FogNear ?? 0,
            FogFar = l is { FogFar: > 0 } ? l.FogFar : 100000,
            FogPower = 1,
            FogMax = 1,
            FogClipDistance = l?.FogClipDistance ?? 0,
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

    /// <summary>One interior as this plugin has it (quick tests; the full build merges plugins).</summary>
    public Cell BuildInterior(Ob.ICellGetter source)
    {
        var cell = NewInterior(source);
        foreach (var r in source.Persistent.Concat(source.Temporary).Concat(source.VisibleWhenDistant))
        {
            if (r is Ob.IPlacedObjectGetter o) Place(o, cell, cell);
            else PlaceActor(r, cell, cell);
        }
        BuildNavmesh(cell);
        AddInteriorCell(cell);
        return cell;
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
        FinishMagic();
        SwingDoorTwinCount = Skyrim.SwingDoorTwins.AddAll(mod);
        Directory.CreateDirectory(modOutDir);
        TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
        mod.WriteToBinary(Path.Combine(modOutDir, PluginName));
        // What procedural creatures may use from Oblivion (read by the creatures step when that option is on).
        WriteCreatureCatalog(Path.Combine(modOutDir, PluginName));
        if (VoiceMap.Count > 0)
            File.WriteAllText(Path.Combine(modOutDir, "Oblivion.voicemap.json"), System.Text.Json.JsonSerializer.Serialize(
                VoiceMap.Select(v => new { v.Info, v.SourcePlugin, v.OriginPlugin, v.OriginId, Lines = v.Lines.Select(l => new { l.Number, l.Text }) })));
    }
}
