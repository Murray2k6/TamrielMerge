using System.Numerics;

namespace TamrielMerge.Daggerfall;

/// <summary>
/// Lays out RMB town blocks the way Daggerfall Unity does (RMBLayout, DaggerfallInterior; MIT), as RDB-style objects in
/// the frame the dungeon pipeline uses (Daggerfall units, Y up, Z north, rotations in degrees), so town exteriors and
/// building interiors go through the same mesh, prop, clutter, light and door builders as dungeons:
/// <list type="bullet">
/// <item>a building record stands at (X, 0, 4096 - Z) turned by its Y rotation; its models at (x, -y, z) inside it;</item>
/// <item>loose block models at (x, -y - 4, z + 4096); exterior flats at (x + X, -y - 6, z + 4096 - Z);</item>
/// <item>nature scenery on the 16 x 16 ground grid, tile (x, 15 - y) at (x * 256, -2, y * 256 + 256);</item>
/// <item>inside, models at (x, -y, z) — props (object type 3) at (x, y, z) lifted by their lowest vertex — people and
/// flats at (x, -y, z), and the doors between rooms (door records) as hinged doors (models 9000-9004, tag DOR).</item>
/// </list>
/// Town space puts block (bx, by) of the location's grid at (bx * 4096, 0, by * 4096).
/// </summary>
public static class DfTownLayout
{
    const float Div = 2048f / 360f;
    const int PropModelType = 3;
    /// <summary>Daggerfall's building door surfaces (texture archive family 74, Daggerfall Unity MeshReader).</summary>
    public const int BuildingDoorArchive = 74;
    /// <summary>The nature flats archive, before Daggerfall Unity's climate swap (temperate woodland).</summary>
    public const int NatureArchive = 504;

    /// <summary>A climate's nature flats (summer; each has a _Snow set one archive later used in winter), as Daggerfall
    /// Unity MapsFile.GetWorldClimateSettings: rainforest 500, subtropical 501, swamp 502, desert 503, temperate
    /// woodland 504, woodland hills 506, haunted woodlands 508, mountains 510.</summary>
    public static int NatureArchiveOf(DfClimate climate) => climate switch
    {
        DfClimate.Desert or DfClimate.Desert2 => 503,
        DfClimate.Mountain => 510,
        DfClimate.Rainforest => 500,
        DfClimate.Swamp => 502,
        DfClimate.Subtropical => 501,
        DfClimate.MountainWoods => 506,
        DfClimate.HauntedWoodlands => 508,
        _ => 504,
    };

    public static Vector3 BlockOrigin(int bx, int by) => new(bx * DfTownBlock.Side, 0, by * DfTownBlock.Side);

    static Vector3 RotateY(Vector3 v, float degrees) => Vector3.Transform(v, DfDungeon.UnityRotY(-degrees * MathF.PI / 180f));

    public static Vector3 BuildingOrigin(DfRmbBuilding b) => new(b.XPos, 0, DfTownBlock.Side - b.ZPos);
    public static float BuildingYaw(DfRmbBuilding b) => b.YRotation / Div;

    static DfRdbObject Model(uint id, Vector3 position, float yawDegrees, int recordOffset = 0) =>
        new(DfRdbObjectType.Model, position, new Vector3(0, yawDegrees, 0), id, 0, 0, 0, 0, 0, false) { RecordOffset = recordOffset };

    static DfRdbObject Flat(DfRmbFlat f, Vector3 position) =>
        new(DfRdbObjectType.Flat, position, Vector3.Zero, 0, f.TextureArchive, f.TextureRecord, f.Flags, (ushort)f.FactionId, 0, false) { RecordOffset = f.RecordOffset };

    /// <summary>
    /// A block's outside: building shells and their attachments, loose models, exterior flats (scenery, lights,
    /// people standing outdoors) and the ground scenery, in block space.
    /// </summary>
    public static DfDungeonBlock Exterior(DfTownBlock block, int natureArchive = NatureArchive)
    {
        var objects = new List<DfRdbObject>();
        foreach (var b in block.Buildings)
        {
            var origin = BuildingOrigin(b);
            float yaw = BuildingYaw(b);
            foreach (var m in b.Exterior.Models)
                objects.Add(Model(m.ModelId, origin + RotateY(new Vector3(m.Position.X, -m.Position.Y, m.Position.Z), yaw), yaw + m.YRotation / Div, b.Index));
            foreach (var f in b.Exterior.Flats)
                objects.Add(Flat(f, new Vector3(f.Position.X + b.XPos, -f.Position.Y - 6, f.Position.Z + DfTownBlock.Side - b.ZPos)));
            // People standing outside the building (placed like its outside flats; the street turns them into townsfolk).
            foreach (var f in b.Exterior.People)
                objects.Add(Flat(f, new Vector3(f.Position.X + b.XPos, -f.Position.Y - 6, f.Position.Z + DfTownBlock.Side - b.ZPos)));
        }
        foreach (var m in block.MiscModels)
            objects.Add(Model(m.ModelId, new Vector3(m.Position.X, -m.Position.Y - 4, m.Position.Z + DfTownBlock.Side), m.YRotation / Div, -1));
        foreach (var f in block.MiscFlats)
            objects.Add(Flat(f, new Vector3(f.Position.X, -f.Position.Y - 6, f.Position.Z + DfTownBlock.Side)));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                byte s = block.GroundScenery[x, 15 - y];
                int record = s == 255 ? -1 : s / 4 - 1;
                if (record < 1) continue;
                objects.Add(new DfRdbObject(DfRdbObjectType.Flat, new Vector3(x * 256, -2, y * 256 + 256), Vector3.Zero, 0, natureArchive, record, 0, 0, 0, false));
            }
        return new DfDungeonBlock { Name = Path.GetFileNameWithoutExtension(block.Name), Objects = objects };
    }

    /// <summary>
    /// A building's inside in its own space: walls, floors and props, flats (clutter and lights), the people, and the
    /// hinged doors between rooms.
    /// </summary>
    public static DfDungeonBlock Interior(DfTownBlock block, DfRmbBuilding b, IReadOnlyDictionary<uint, DfModel> models)
    {
        var objects = new List<DfRdbObject>();
        foreach (var m in b.Interior.Models)
        {
            // Daggerfall Unity: ARMRGL01's second building has a model blocking its stairs.
            if (m.ModelId == 31000 && block.Name.StartsWith("ARMRGL01", StringComparison.OrdinalIgnoreCase) && b.Index == 1) continue;
            Vector3 pos;
            if (m.ObjectType == PropModelType && models.TryGetValue(m.ModelId, out var model))
                pos = new Vector3(m.Position.X, m.Position.Y - model.Min.Y, m.Position.Z);
            else
                pos = new Vector3(m.Position.X, -m.Position.Y, m.Position.Z);
            objects.Add(Model(m.ModelId, pos, m.YRotation / Div));
        }
        foreach (var f in b.Interior.Flats)
            objects.Add(Flat(f, new Vector3(f.Position.X, -f.Position.Y, f.Position.Z)));
        foreach (var f in b.Interior.People)
            objects.Add(Flat(f, new Vector3(f.Position.X, -f.Position.Y, f.Position.Z)) with { RecordOffset = -1 - f.RecordOffset });
        foreach (var d in b.Interior.Doors)
            objects.Add(Model((uint)(9000 + d.ModelIndex % 5), new Vector3(d.Position.X, -d.Position.Y, d.Position.Z), d.YRotation / Div) with { Tag = "DOR" });
        return new DfDungeonBlock { Name = $"{Path.GetFileNameWithoutExtension(block.Name)}_{b.Index:00}", Objects = objects };
    }

    /// <summary>A door surface on a placed model: its centre, outward normal, width and height (Daggerfall units, Y up).</summary>
    public sealed record DoorSurface(Vector3 Centre, Vector3 Normal, float Width, float Height, int Building);

    /// <summary>
    /// Dungeon entrances on a block's models (Daggerfall Unity MeshReader: texture archive family 56, and archive 331's
    /// records above 0 for ruins), in block space — a castle's or a town dungeon's way in.
    /// </summary>
    public static IEnumerable<DoorSurface> DungeonEntrances(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models) =>
        Surfaces(block, models, p => p.TextureArchive % 100 == 56 || p.TextureArchive == 331 && p.TextureRecord > 0);

    /// <summary>The building door surfaces (texture family 74) on a block's models, in block space.</summary>
    public static IEnumerable<DoorSurface> Doors(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models) =>
        Surfaces(block, models, p => p.TextureArchive % 100 == BuildingDoorArchive);

    static IEnumerable<DoorSurface> Surfaces(DfDungeonBlock block, IReadOnlyDictionary<uint, DfModel> models, Func<DfPolygon, bool> isDoor)
    {
        foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model))
        {
            if (!models.TryGetValue(o.ModelId, out var model)) continue;
            var m = DfDungeon.ObjectTransform(o, Vector3.Zero);
            foreach (var p in model.Polygons)
            {
                if (p.Points.Length < 3 || !isDoor(p)) continue;
                var pts = p.Points.Select(v => Vector3.Transform(v, m)).ToArray();
                var n = Vector3.Normalize(Vector3.TransformNormal(p.Normal, m));
                var centre = pts.Aggregate(Vector3.Zero, (a, c) => a + c) / pts.Length;
                float height = pts.Max(v => v.Y) - pts.Min(v => v.Y);
                var across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
                float width = pts.Max(v => Vector3.Dot(v, across)) - pts.Min(v => Vector3.Dot(v, across));
                yield return new DoorSurface(centre, n, width, height, o.RecordOffset);
            }
        }
    }
}
