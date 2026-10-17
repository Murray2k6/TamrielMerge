using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Replaces Daggerfall's dungeon furniture and props with Skyrim objects (which bring their own collision and,
/// for furniture, sitting/sleeping). The model-to-category table comes from inspecting rendered thumbnails of every
/// prop model used in Daggerfall's dungeon blocks (prop-sheets). Furniture keeps Skyrim's own size so it stays
/// usable; crates, rocks and statues are scaled to the Daggerfall footprint. Objects are turned to match: along the
/// long axis, or for chairs, pews, thrones and beds so their backs/heads face the same way.
/// </summary>
public sealed class PropReplacer
{
    enum Fit { SkyrimSize, Footprint, Length, Height }
    enum Turn { LongAxis, Back, None, Room }
    sealed record Kind(string Name, FormKey Base, Fit Fit, Turn Turn);

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly Kind Chair = new("chair", Sk(0x2EC1C), Fit.SkyrimSize, Turn.Back);          // CommonChair01
    static readonly Kind Throne = new("throne", Sk(0x6B691), Fit.SkyrimSize, Turn.Back);        // NobleChair01
    static readonly Kind Bench = new("bench", Sk(0x2E6CF), Fit.SkyrimSize, Turn.LongAxis);      // CommonBench01
    static readonly Kind Pew = new("pew", Sk(0x6B57B), Fit.SkyrimSize, Turn.Back);              // NobleBench01
    static readonly Kind Stool = new("stool", Sk(0x74EC6), Fit.SkyrimSize, Turn.None);          // WoodenBarStool
    static readonly Kind Table = new("table", Sk(0x24CB9), Fit.SkyrimSize, Turn.LongAxis);      // CommonTable01
    static readonly Kind LongTable = new("long table", Sk(0xE2604), Fit.SkyrimSize, Turn.LongAxis); // CommonTable02
    static readonly Kind LowTable = new("low table", Sk(0xE25DB), Fit.SkyrimSize, Turn.LongAxis);   // CommonTableLow01
    static readonly Kind Bed = new("bed", Sk(0x30091), Fit.SkyrimSize, Turn.Back);              // CommonBed01
    static readonly Kind Bookcase = new("bookcase", Sk(0x2E6CD), Fit.SkyrimSize, Turn.Back);    // CommonShelf01
    static readonly Kind Wardrobe = new("wardrobe", Sk(0x24CA7), Fit.SkyrimSize, Turn.Back);    // CommonWardrobe01
    static readonly Kind Counter = new("counter", Sk(0x101E97), Fit.SkyrimSize, Turn.Back);     // CommonCounter01
    static readonly Kind Crate = new("crate", Sk(0x7A603), Fit.Footprint, Turn.LongAxis);       // CrateSmall01
    static readonly Kind BigCrate = new("big crate", Sk(0xDAE80), Fit.Footprint, Turn.LongAxis); // CommonCrate01
    static readonly Kind Chest = new("chest", Sk(0x3AC21), Fit.SkyrimSize, Turn.LongAxis);      // TreasBanditChest (leveled loot)
    static readonly Kind Coffin = new("coffin", Sk(0xEB093), Fit.SkyrimSize, Turn.LongAxis);    // CommonCoffin01 (container)
    static readonly Kind OpenCoffin = new("open coffin", Sk(0xEB094), Fit.SkyrimSize, Turn.LongAxis); // CommonCoffinOpen01
    static readonly Kind Rack = new("torture rack", Sk(0xAA045), Fit.SkyrimSize, Turn.LongAxis); // TortureRack
    // Daggerfall's statues are slim (about three times as tall as their base is wide), like StatueDibella. Mara and
    // Talos (broad, on wide plinths) fitted to that height spread to ~550 units and blocked doorways, and they faced
    // north whatever way the room did (user, 2026-10-10: "statues obstruct doors in the throne room", "some statues
    // are rotated"). Dibella's front is local -Y; Turn.Room faces it into the room.
    static readonly Kind Statue = new("statue", Sk(0x8F965), Fit.Height, Turn.Room);            // StatueDibella
    static readonly Kind BigStatue = new("big statue", Sk(0x8F965), Fit.Height, Turn.Room);     // StatueDibella
    static readonly Kind Rocks = new("rocks", Sk(0xF2D2F), Fit.Footprint, Turn.LongAxis);       // RockPileM02CaveDirt01
    static readonly Kind Anvil = new("anvil", Sk(0x1BCC2), Fit.SkyrimSize, Turn.LongAxis);      // BlacksmithAnvilStatic
    static readonly Kind Lectern = new("lectern", Sk(0xF4B4B), Fit.SkyrimSize, Turn.Back);      // NobleLectern01
    static readonly Kind FallenLog = new("fallen log", Sk(0x13250), Fit.Footprint, Turn.LongAxis); // TreePineForestLog01 (branching fallen pine)

    static readonly Dictionary<uint, Kind> Table_ = Build(
        (Chair, [41101, 41100, 41102, 41103, 41129]),
        (Throne, [41104, 41122, 41123]),
        (Bench, [41105, 41106, 41130]),
        (Pew, [41126]),
        (Stool, [41113, 41114]),
        (Table, [41108, 41111]),
        (LongTable, [41109, 41110]),
        (LowTable, [41112]),
        (Bed, [41000, 41001]),
        (Bookcase, [41015, 41031, 41019, 41030, 41011, 41048, 41012, 41006, 41049, 41044, 41026, 41018, 41013, 41029, 41047, 41025, 41005, 41046, 41014, 41017, 41027, 41045, 41043, 41010, 41041, 41040]),
        (Wardrobe, [41028, 41032, 41804, 41124]),
        (Counter, [41002, 41004, 41003, 41801, 41800]),
        (Crate, [41822, 41828, 41818, 41823, 41827, 41824, 41826, 41829, 41815, 41816, 41817, 41825, 41830, 41831]),
        (BigCrate, [41832, 41833, 41834, 74134]),
        (Chest, [41811, 41812]),
        (Coffin, [41317, 41315, 41319]),
        (OpenCoffin, [41318, 41320, 41321, 41322, 41323, 41325, 41326, 41327]),
        (Rack, [41303, 41300, 41301, 41302]),
        (Statue, [62323, 62327]),
        (BigStatue, [62324, 62328]),
        (Rocks, [41704, 41712, 41710, 41705, 41707, 74044, 74135, 74045, 74136]),
        (Anvil, [41118]),
        (Lectern, [41020]),
        (FallenLog, [41735]));   // forked tree branch, e.g. by the campfire where a new game begins

    static Dictionary<uint, Kind> Build(params (Kind Kind, uint[] Ids)[] groups) =>
        groups.SelectMany(g => g.Ids.Select(id => (id, g.Kind))).ToDictionary(x => x.id, x => x.Kind);

    /// <summary>True when a Daggerfall model is replaced by a Skyrim object (and so left out of the block mesh).</summary>
    /// <summary>Whether this model's stand-in is turned to face into the room (statues).</summary>
    public static bool TurnsToRoom(DfModel model) => Table_.TryGetValue(model.Id, out var k) && k.Turn == Turn.Room;

    public static bool IsReplaced(uint modelId) => Table_.ContainsKey(modelId) || (Ship is not null && ShipModels.Contains(modelId));

    /// <summary>The Skyrim object a Daggerfall furniture or prop model stands for (its function: sit, sleep, open), and
    /// what kind it is; null for models not in the table (ships included).</summary>
    public static (FormKey Base, string Kind)? StandInFor(uint modelId) => Table_.TryGetValue(modelId, out var k) ? (k.Base, k.Name) : null;

    /// <summary>A copy of a Skyrim furniture model's marker block (where and which way an actor sits or lies;
    /// BSFurnitureMarkerNode), to put into another mesh built in the same space; null when it has none.</summary>
    public nifly.NiObject? FurnitureMarkerBlock(FormKey key)
    {
        var record = skyrim.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key);
        string? path = (record as IModeledGetter)?.Model?.File.DataRelativePath.Path;
        if (path is null) return null;
        path = path.Replace('/', '\\');
        if (!path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) path = "meshes\\" + path;
        foreach (var archive in meshArchives)
        {
            var file = archive.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, file.GetBytes());
            try
            {
                using var nif = new nifly.NifFile();
                if (nif.Load(tmp) != 0) return null;
                var hdr = nif.GetHeader();
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                    if (hdr.GetBlockById(i) is nifly.BSFurnitureMarker marker)
                        return marker.Clone();
            }
            finally { File.Delete(tmp); }
            break;
        }
        return null;
    }

    /// <summary>Daggerfall's ships (the large and the small one the player can buy), replaced by the tool's ship
    /// (Skyrim/Meshes/ShipModel; user, 2026-10-09: "redguards and daggerfalls ship need replaced with this").</summary>
    static readonly uint[] ShipModels = [909, 910];

    /// <summary>The ship's static, once the plugin being built has made it (<see cref="SetShip"/>).</summary>
    public static FormKey? Ship { get; private set; }

    public void SetShip(FormKey key, Vector3 min, Vector3 max) { Ship = key; AddShape(key, min, max); }

    /// <summary>Local height an object rests at, where that isn't its mesh's lowest point.</summary>
    static readonly Dictionary<FormKey, float> RestHeight = new()
    {
        [Sk(0x13250)] = -69,   // TreePineForestLog01: underside of the trunk (its branch tips reach -104)
    };

    readonly Dictionary<FormKey, (Vector3 Min, Vector3 Max, Vector2 Back)> skyrimShapes = new();

    /// <summary>Bounds of an object this conversion made itself (a model generated from a sprite), which isn't in Skyrim's plugins.</summary>
    public void AddShape(FormKey key, Vector3 min, Vector3 max) => skyrimShapes[key] = (min, max, Vector2.Zero);
    readonly IArchiveReader[] meshArchives;
    readonly ISkyrimModGetter skyrim;
    public ISkyrimModGetter Skyrim => skyrim;

    public PropReplacer(ISkyrimModGetter skyrim, string skyrimData)
    {
        this.skyrim = skyrim;
        meshArchives = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa").Select(p => Archive.CreateReader(GameRelease.SkyrimSE, p)).ToArray();
    }

    public sealed record Placement(FormKey Base, Vector3 PositionSkyrim, float Heading, float Scale, string Kind);

    /// <param name="room">For objects turned to the room (statues): the way into it from here (Skyrim horizontal), see
    /// <see cref="RoomFacing"/>.</param>
    public Placement? Replace(DfRdbObject o, Vector3 blockOrigin, DfModel model, Vector2? room = null)
    {
        if (!Table_.TryGetValue(o.ModelId, out var kind))
        {
            if (Ship is not { } ship || !ShipModels.Contains(o.ModelId)) return null;
            kind = new Kind("ship", ship, Fit.Length, Turn.LongAxis);
        }
        var (smin, smax, sback) = SkyrimShape(kind.Base);
        var m = DfDungeon.ObjectTransform(o, blockOrigin);

        // Daggerfall footprint in Skyrim units, and its long axis and "back" direction in Skyrim space.
        Vector3 Sky(Vector3 df) => new(df.X * BlockMeshBuilder.Scale, df.Z * BlockMeshBuilder.Scale, df.Y * BlockMeshBuilder.Scale);
        var corners = new[] { new Vector3(model.Min.X, 0, model.Min.Z), new Vector3(model.Max.X, 0, model.Min.Z), new Vector3(model.Min.X, 0, model.Max.Z) }
            .Select(c => Sky(Vector3.Transform(c, m))).ToArray();
        var axisA = corners[1] - corners[0]; var axisB = corners[2] - corners[0];
        var dfLong = axisA.Length() >= axisB.Length() ? axisA : axisB;
        float dfLongLen = MathF.Max(axisA.Length(), axisB.Length()), dfShortLen = MathF.Min(axisA.Length(), axisB.Length());
        var baseCentre = Sky(Vector3.Transform(new Vector3((model.Min.X + model.Max.X) / 2, model.Min.Y, (model.Min.Z + model.Max.Z) / 2), m));

        float heading = 0;
        if (kind.Turn == Turn.LongAxis)
        {
            // Skyrim object's long axis is X or Y in its own frame; turn it onto the Daggerfall long axis.
            bool skyLongIsX = smax.X - smin.X >= smax.Y - smin.Y;
            float dfAngle = MathF.Atan2(dfLong.X, dfLong.Y); // clockwise from north
            heading = dfAngle - (skyLongIsX ? MathF.PI / 2 : 0);
        }
        else if (kind.Turn == Turn.Back && DaggerfallBack(model, m) is { } dfBack && sback != Vector2.Zero)
        {
            float dfAngle = MathF.Atan2(dfBack.X, dfBack.Y), skyAngle = MathF.Atan2(sback.X, sback.Y);
            heading = dfAngle - skyAngle;
        }
        else if (kind.Turn == Turn.Room && room is { } toward)
            heading = RoomFacing.Heading(toward, frontPlusY: false);

        float scale = 1;
        if (kind.Fit == Fit.Length) scale = dfLongLen / Math.Max(1, MathF.Max(smax.X - smin.X, smax.Y - smin.Y));
        else if (kind.Fit == Fit.Height)
        {
            // As tall as Daggerfall's, but no broader than its base (a quarter over at most).
            float dfHeight = (model.Max.Y - model.Min.Y) * BlockMeshBuilder.Scale;
            float skyLong = MathF.Max(smax.X - smin.X, smax.Y - smin.Y);
            scale = Math.Clamp(MathF.Min(dfHeight / Math.Max(smax.Z - smin.Z, 1), dfLongLen * 1.25f / Math.Max(skyLong, 1)), 0.3f, 3f);
        }
        else if (kind.Fit == Fit.Footprint)
        {
            float skyLong = MathF.Max(smax.X - smin.X, smax.Y - smin.Y), skyShort = MathF.Min(smax.X - smin.X, smax.Y - smin.Y);
            float dfHeight = (model.Max.Y - model.Min.Y) * BlockMeshBuilder.Scale, skyHeight = smax.Z - smin.Z;
            scale = Math.Clamp(new[] { dfLongLen / Math.Max(skyLong, 1), dfShortLen / Math.Max(skyShort, 1), dfHeight / Math.Max(skyHeight, 1) }.Max(), 0.3f, 3f);
        }

        // Skyrim origins aren't centred: place so the object's footprint centre lands on Daggerfall's.
        var localCentre = new Vector2((smin.X + smax.X) / 2, (smin.Y + smax.Y) / 2) * scale;
        float c = MathF.Cos(heading), s = MathF.Sin(heading);
        var worldOffset = new Vector2(localCentre.X * c + localCentre.Y * s, -localCentre.X * s + localCentre.Y * c);
        var position = new Vector3(baseCentre.X - worldOffset.X, baseCentre.Y - worldOffset.Y, baseCentre.Z - smin.Z * scale);
        return new Placement(kind.Base, position, heading, scale, kind.Name);
    }

    /// <summary>
    /// Direction (Skyrim horizontal) from the footprint centre towards the model's tall part: a chair or throne's
    /// backrest, a bed's headboard, a bookcase's back. Null when the model has no clearly taller side.
    /// </summary>
    static Vector2? DaggerfallBack(DfModel model, Matrix4x4 m)
    {
        float top = model.Max.Y, bottom = model.Min.Y, threshold = bottom + (top - bottom) * 0.7f;
        var centre = new Vector3((model.Min.X + model.Max.X) / 2, 0, (model.Min.Z + model.Max.Z) / 2);
        var high = model.Polygons.SelectMany(p => p.Points).Where(p => p.Y >= threshold).ToList();
        if (high.Count == 0) return null;
        var avg = high.Aggregate(Vector3.Zero, (a, b) => a + b) / high.Count;
        var d = Vector3.TransformNormal(new Vector3(avg.X - centre.X, 0, avg.Z - centre.Z), m);
        var v = new Vector2(d.X, d.Z);
        return v.Length() < 1 ? null : Vector2.Normalize(v);
    }

    /// <summary>How far a Skyrim object's mesh reaches below its origin (0 when it doesn't): what an object stood on a
    /// floor by its origin is raised by, so it stands on the floor rather than in it.</summary>
    public float DepthBelowOrigin(FormKey key)
    {
        lock (skyrimShapes) return MathF.Max(0, -SkyrimShape(key).Min.Z);
    }

    /// <summary>Bounds and backrest direction of a Skyrim object's mesh (read from the game's archives).</summary>
    public (Vector3 Min, Vector3 Max, Vector2 Back) SkyrimShape(FormKey key)
    {
        if (skyrimShapes.TryGetValue(key, out var shape)) return shape;
        // Not Skyrim's and not registered (AddShape): no shape, rather than failing the dungeon (2026-10-17: Daggerfall's
        // own items on dungeon floors failed 18% of the dungeons when their clutter was settled).
        if (skyrim.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key) is { } record) return SkyrimShape(record);
        lock (skyrimShapes) return skyrimShapes[key] = (Vector3.Zero, Vector3.Zero, Vector2.Zero);
    }

    /// <summary><see cref="SkyrimShape(FormKey)"/> for a record already in hand (any plugin's).</summary>
    public (Vector3 Min, Vector3 Max, Vector2 Back) SkyrimShape(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter record)
    {
        var key = record.FormKey;
        if (skyrimShapes.TryGetValue(key, out var shape)) return shape;
        var bounds = (record as IObjectBoundedGetter)?.ObjectBounds;
        var min = bounds is null ? Vector3.Zero : new Vector3(bounds.First.X, bounds.First.Y, bounds.First.Z);
        var max = bounds is null ? Vector3.Zero : new Vector3(bounds.Second.X, bounds.Second.Y, bounds.Second.Z);
        var back = Vector2.Zero;
        var modelPath = (record as IModeledGetter)?.Model?.File.DataRelativePath.Path;
        if (modelPath is not null && ReadMesh(modelPath) is { } verts && verts.Count > 0)
        {
            var (obMin, obMax) = (min, max);
            min = verts.Aggregate(Vector3.Min); max = verts.Aggregate(Vector3.Max);
            // Some meshes (e.g. the fallen pine) have parts whose transforms the reader gets wrong, giving bounds far
            // beyond the record's; trust the record's bounds then.
            var meshSize = max - min; var obSize = obMax - obMin;
            if (obSize.X > 1 && obSize.Y > 1 && obSize.Z > 1 && (meshSize.X > obSize.X * 1.5f || meshSize.Y > obSize.Y * 1.5f || meshSize.Z > obSize.Z * 1.5f))
                (min, max) = (obMin, obMax);
            // Or the right size in the wrong place (a transform the reader missed: HumanSkullStatic sits 17-32 below
            // its origin by the record, the mesh read put it at 0-15, and caged skulls hung under their cages).
            var shift = Vector3.Abs((min + max) / 2 - (obMin + obMax) / 2);
            if (obSize.X > 1 && obSize.Y > 1 && obSize.Z > 1 && (shift.X > MathF.Max(8, obSize.X / 4) || shift.Y > MathF.Max(8, obSize.Y / 4) || shift.Z > MathF.Max(8, obSize.Z / 4)))
                (min, max) = (obMin, obMax);
            float threshold = min.Z + (max.Z - min.Z) * 0.7f;
            var high = verts.Where(v => v.Z >= threshold).ToList();
            var centre = (min + max) / 2;
            var avg = high.Aggregate(Vector3.Zero, (a, b) => a + b) / Math.Max(high.Count, 1);
            var d = new Vector2(avg.X - centre.X, avg.Y - centre.Y);
            if (d.Length() >= 1) back = Vector2.Normalize(d);
        }
        // Objects whose lowest points (branch tips, roots) belong under the ground: rest them on their body instead.
        if (RestHeight.TryGetValue(key, out var rest)) min = min with { Z = rest };
        return skyrimShapes[key] = (min, max, back);
    }

    List<Vector3>? ReadMesh(string dataRelativePath)
    {
        string path = dataRelativePath.Replace('/', '\\');
        foreach (var archive in meshArchives)
        {
            var file = archive.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, file.GetBytes());
            try
            {
                using var nif = new nifly.NifFile();
                if (nif.Load(tmp) != 0) return null;
                var verts = new List<Vector3>();
                foreach (var shapeObj in nif.GetShapes())
                {
                    // The shape's own transform, then each parent node's up to the root. (Looking transforms up by
                    // name fails for unnamed shapes, leaving rotated parts such as a log's branches untransformed.)
                    var chain = new List<nifly.MatTransform> { shapeObj.transform };
                    for (var parent = nif.GetParentNode(shapeObj); parent is not null; parent = nif.GetParentNode(parent))
                        chain.Add(parent.transform);
                    foreach (var v in nif.GetVertsForShape(shapeObj))
                    {
                        var g = v;
                        foreach (var t in chain) g = t.ApplyTransform(g);
                        verts.Add(new Vector3(g.x, g.y, g.z));
                    }
                }
                return verts;
            }
            finally { File.Delete(tmp); }
        }
        return null;
    }
}
