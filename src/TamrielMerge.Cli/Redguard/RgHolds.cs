using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Redguard;

/// <summary>
/// A hold under every ship's deck (user, 2026-11-09: "the ships dont have holds when they need too", and of the opening:
/// "after the captain comes out of the hold"). The tool's ship is one solid model with nothing inside, so each ship gets a
/// hatch on its main deck (a door, at the clear place nearest the middle of the deck) and a hold of its own below (an
/// interior: planked floor, sides and deck beams overhead, cargo of Skyrim's crates and barrels, a lantern, and a ladder
/// back up to the hatch), with a navmesh so the crew can follow. Brennan stands on his ship's hatch when the opening
/// brings him out (RgOpening).
/// </summary>
public sealed partial class RgPluginBuilder
{
    const float HoldLength = 832, HoldWidth = 384, HoldHeight = 176, HatchSize = 112;
    static readonly BlockMeshBuilder.Tex HoldTex = new(@"textures\architecture\riften\riftencanalplanks01.dds", @"textures\architecture\riften\riftencanalplanks01_n.dds", true);

    /// <summary>Each ship's hatch (by the ship's reference).</summary>
    readonly Dictionary<FormKey, PlacedObject> hatchOf = [];
    readonly List<(Cell Cell, List<(Vector3[] Points, Vector3 Normal)> Solid, PlacedObject Door)> holds = [];
    public int Holds => hatchOf.Count;
    Door? hatchDoor, ladderDoor;
    Static? holdRoom;
    List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>? holdRoomPolys;

    static (Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex) HoldQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        // Wound counter-clockwise seen from the side it faces.
        return Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0 ? (new[] { a, b, c, d }, n, HoldTex) : (new[] { d, c, b, a }, n, HoldTex);
    }

    /// <summary>A box's six faces, facing out.</summary>
    static IEnumerable<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)> HoldBox(Vector3 lo, Vector3 hi)
    {
        Vector3 P(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
        yield return HoldQuad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), Vector3.UnitZ);
        yield return HoldQuad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0), -Vector3.UnitZ);
        yield return HoldQuad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1), -Vector3.UnitX);
        yield return HoldQuad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1), Vector3.UnitX);
        yield return HoldQuad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), -Vector3.UnitY);
        yield return HoldQuad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1), Vector3.UnitY);
    }

    static ObjectBounds BoundsOf(Vector3 min, Vector3 max) => new()
    {
        First = new P3Int16((short)MathF.Floor(min.X), (short)MathF.Floor(min.Y), (short)MathF.Floor(min.Z)),
        Second = new P3Int16((short)MathF.Ceiling(max.X), (short)MathF.Ceiling(max.Y), (short)MathF.Ceiling(max.Z)),
    };

    /// <summary>The hatch, the ladder and the hold's room: one model of each, shared by every ship.</summary>
    void MakeHoldModels()
    {
        if (hatchDoor is not null) return;
        float h = HatchSize / 2;
        // The hatch: a low coaming with planks across it.
        var hatch = HoldBox(new Vector3(-h, -h, 0), new Vector3(h, h, 6)).ToList();
        var hm = BlockMeshBuilder.BuildPolygons(hatch, Path.Combine(modOutDir, "meshes", @"tamrielmerge\rg\holdhatch.nif"));
        hatchDoor = new Door(mod) { EditorID = "RGHoldHatch", Name = "Hold", Model = new Model { File = @"tamrielmerge\rg\holdhatch.nif" }, ObjectBounds = BoundsOf(hm.Min, hm.Max) };
        mod.Doors.Add(hatchDoor);

        // The ladder: two rails and a rung every 32, leaning on the hold's end.
        var ladder = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>();
        ladder.AddRange(HoldBox(new Vector3(-28, -4, 0), new Vector3(-20, 4, HoldHeight)));
        ladder.AddRange(HoldBox(new Vector3(20, -4, 0), new Vector3(28, 4, HoldHeight)));
        for (float z = 24; z < HoldHeight - 8; z += 32) ladder.AddRange(HoldBox(new Vector3(-20, -3, z), new Vector3(20, 3, z + 6)));
        var lm = BlockMeshBuilder.BuildPolygons(ladder, Path.Combine(modOutDir, "meshes", @"tamrielmerge\rg\holdladder.nif"));
        ladderDoor = new Door(mod) { EditorID = "RGHoldLadder", Name = "Deck", Model = new Model { File = @"tamrielmerge\rg\holdladder.nif" }, ObjectBounds = BoundsOf(lm.Min, lm.Max) };
        mod.Doors.Add(ladderDoor);

        // The room: floor, sides, ends, the deck overhead, and a deck beam across every 128.
        float x = HoldWidth / 2, y = HoldLength / 2;
        var room = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>
        {
            HoldQuad(new(-x, -y, 0), new(x, -y, 0), new(x, y, 0), new(-x, y, 0), Vector3.UnitZ),
            HoldQuad(new(-x, -y, HoldHeight), new(x, -y, HoldHeight), new(x, y, HoldHeight), new(-x, y, HoldHeight), -Vector3.UnitZ),
            HoldQuad(new(-x, -y, 0), new(-x, y, 0), new(-x, y, HoldHeight), new(-x, -y, HoldHeight), Vector3.UnitX),
            HoldQuad(new(x, -y, 0), new(x, y, 0), new(x, y, HoldHeight), new(x, -y, HoldHeight), -Vector3.UnitX),
            HoldQuad(new(-x, -y, 0), new(x, -y, 0), new(x, -y, HoldHeight), new(-x, -y, HoldHeight), Vector3.UnitY),
            HoldQuad(new(-x, y, 0), new(x, y, 0), new(x, y, HoldHeight), new(-x, y, HoldHeight), -Vector3.UnitY),
        };
        for (float by = -y + 64; by < y; by += 128) room.AddRange(HoldBox(new Vector3(-x, by - 8, HoldHeight - 16), new Vector3(x, by + 8, HoldHeight)));
        // The ship's ribs up each side.
        for (float by = -y + 128; by < y; by += 128)
            foreach (int s in new[] { -1, 1 })
                room.AddRange(HoldBox(new Vector3(s > 0 ? x - 10 : -x, by - 6, 0), new Vector3(s > 0 ? x : -x + 10, by + 6, HoldHeight - 16)));
        var rm = BlockMeshBuilder.BuildPolygons(room, Path.Combine(modOutDir, "meshes", @"tamrielmerge\rg\hold.nif"));
        holdRoom = new Static(mod) { EditorID = "RGHoldRoom", Model = new Model { File = @"tamrielmerge\rg\hold.nif" }, ObjectBounds = BoundsOf(rm.Min, rm.Max) };
        mod.Statics.Add(holdRoom);
        holdRoomPolys = room;
    }

    /// <summary>A hatch on each of the scene's ships and the hold under it.</summary>
    void BuildHolds(Scene scene, Action<string>? log)
    {
        if (ship is null) return;
        foreach (var (_, r) in placedShips.Where(s => s.Scene == scene.Name))
        {
            float scale = r.Scale ?? 1, heading = r.Placement!.Rotation.Z;
            var c = new Vector3(r.Placement.Position.X, r.Placement.Position.Y, r.Placement.Position.Z);
            var fwd = new Vector3(MathF.Sin(heading), MathF.Cos(heading), 0);
            float deckZ = c.Z + ship.Deck * scale;
            var solid = ShipSolid(ship, r.Placement.Position, r.Placement.Rotation, scale);
            // On the main deck's centreline, the clear place nearest its middle: where a body can stand, at the deck's own
            // height (not the forecastle's or the poop's), with the hatch's width of clear deck (its four corners too).
            Vector3? spot = null;
            float reach = ShipModel.Length * scale * 0.4f;
            // With clear deck all round first; on a ship too small for that (the hideout's smallest, at scale 0.374, had
            // no such place and got no hold), the hatch's own footprint clear.
            foreach (bool wide in new[] { true, false })
            for (float d = 0; d <= reach && spot is null; d += 32)
                foreach (float along in d == 0 ? new[] { 0f } : new[] { -d, d })
                {
                    var q = c + fwd * along;
                    var from = new Vector3(q.X, q.Y, deckZ + 10);
                    if (Morrowind.MwPluginBuilder.ClearSpot(from, solid, above: 20) is not { } landed) continue;
                    if (MathF.Abs(landed.Z - deckZ) > 24 || Vector2.Distance(new Vector2(landed.X, landed.Y), new Vector2(q.X, q.Y)) > 8) continue;
                    bool room = true;
                    // ...and as much again round it (2026-11-10, in game: the first hatch lay against the foot of a mast).
                    foreach (var (ox, oy) in new[] { (-2, -2), (0, -2), (2, -2), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0), (-1, -1), (1, -1), (1, 1), (-1, 1) }.Where(o => wide || Math.Abs(o.Item1) + Math.Abs(o.Item2) == 2 && Math.Abs(o.Item1) == 1))
                    {
                        var corner = new Vector3(landed.X + ox * HatchSize / 2, landed.Y + oy * HatchSize / 2, deckZ + 10);
                        if (Morrowind.MwPluginBuilder.ClearSpot(corner, solid, above: 20) is not { } cl || MathF.Abs(cl.Z - landed.Z) > 8
                            || Vector2.Distance(new Vector2(cl.X, cl.Y), new Vector2(corner.X, corner.Y)) > 8) { room = false; break; }
                    }
                    if (!room) continue;
                    spot = landed;
                    break;
                }
            MakeHoldModels();
            BuildCabin(scene, r, solid, c, fwd, deckZ, scale, heading, log);
            if (spot is not { } at) { Problems.Add($"{scene.Name}: ship at ({c.X:F0}, {c.Y:F0}): no clear deck for a hatch, no hold"); continue; }

            var hatch = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(hatchDoor!.FormKey),
                Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(0, 0, heading) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            PersistentCellFor(scene).Persistent.Add(hatch);
            scene.LoadDoors.Add(hatch);
            hatchOf[r.FormKey] = hatch;

            // The hold: an interior of its own, lit by its lantern.
            int n = hatchOf.Count;
            var amb = System.Drawing.Color.FromArgb(0, 52, 44, 34);
            var cell = new Cell(mod)
            {
                EditorID = $"RGShipHold{n:00}",
                Name = "Ship's Hold",
                Flags = Cell.Flag.IsInteriorCell | Cell.Flag.PublicArea,
                LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
                Lighting = new CellLighting
                {
                    AmbientColor = amb, DirectionalColor = System.Drawing.Color.FromArgb(0, 40, 34, 26), DirectionalFade = 1,
                    FogNearColor = System.Drawing.Color.Black, FogFarColor = System.Drawing.Color.Black, FogFar = 100000, FogPower = 1, FogMax = 1,
                    LightFadeBegin = 6000, LightFadeEnd = 8000,
                    AmbientColors = new AmbientColors
                    {
                        DirectionalXPlus = amb, DirectionalXMinus = amb, DirectionalYPlus = amb, DirectionalYMinus = amb, DirectionalZPlus = amb, DirectionalZMinus = amb,
                        Specular = System.Drawing.Color.Black, Scale = 1,
                    },
                    Inherits = 0,
                },
                Persistent = [],
                Temporary = [],
            };
            int blockNo = (int)(cell.FormKey.ID % 10), subNo = (int)(cell.FormKey.ID / 10 % 10);
            var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
            if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
            var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNo);
            if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
            sub.Cells.Add(cell);

            PlacedObject Put(FormKey baseKey, float x, float y, float z, float yaw = 0, bool persistent = false)
            {
                var p = new PlacedObject(mod)
                {
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(baseKey),
                    Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, yaw) },
                };
                if (persistent) { p.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400; cell.Persistent.Add(p); }
                else cell.Temporary.Add(p);
                return p;
            }
            Put(holdRoom!.FormKey, 0, 0, 0);
            float endY = HoldLength / 2;
            // The ladder at the fore end, leading up to the hatch.
            var ladder = Put(ladderDoor!.FormKey, 0, endY - 8, 0, persistent: true);
            hatch.TeleportDestination = new TeleportDestination { Door = ladder.ToLink<IPlacedObjectGetter>(), Position = new P3Float(0, endY - 96, 8), Rotation = new P3Float(0, 0, MathF.PI) };
            ladder.TeleportDestination = new TeleportDestination { Door = hatch.ToLink<IPlacedObjectGetter>(), Position = new P3Float(at.X, at.Y, at.Z + 14), Rotation = new P3Float(0, 0, heading) };

            // Cargo down both sides and across the aft end, a gangway left clear down the middle; what stands there is in
            // the navmesh's way too.
            var solidIn = holdRoomPolys!.Select(p => (p.Pts, p.N)).ToList();
            void Cargo(FormKey baseKey, float x, float y, float yaw, float half, float height)
            {
                Put(baseKey, x, y, 0, yaw);
                solidIn.AddRange(HoldBox(new Vector3(x - half, y - half, 0), new Vector3(x + half, y + half, height)).Select(p => (p.Pts, p.N)));
            }
            var crate = vanilla.Get<IStaticGetter>("CrateSmall01EECo")?.FormKey;
            var longCrate = vanilla.Get<IStaticGetter>("CrateSmallLong01EECo")?.FormKey;
            var barrel = vanilla.Get<IContainerGetter>("MeadBarrel02")?.FormKey;
            float side = HoldWidth / 2 - 56;
            int item = 0;
            for (float y = -endY + 64; y <= endY - 192; y += 104, item++)
                foreach (int s in new[] { -1, 1 })
                {
                    var what = (((item + (s > 0 ? 1 : 0)) % 3) switch { 0 => crate, 1 => barrel, _ => longCrate }) ?? crate ?? barrel;
                    if (what is { } k) Cargo(k, s * side, y, (item * 37 + s * 11) % 360 * MathF.PI / 180, 40, 72);
                }
            if (vanilla.Get<IStaticGetter>("CandleLanternwithCandle01") is { } lantern) Put(lantern.FormKey, 0, 0, HoldHeight - 16 - 40);
            if (vanilla.Get<ILightGetter>("SolitudeInnCandleDefault") is { } light)
            {
                Put(light.FormKey, 0, 0, HoldHeight - 64);
                Put(light.FormKey, 0, endY - 160, HoldHeight - 64);
                Put(light.FormKey, 0, -endY + 160, HoldHeight - 64);
            }
            holds.Add((cell, solidIn, ladder));
            log?.Invoke($"hold {n}: hatch at ({at.X:F0}, {at.Y:F0}, {at.Z:F0}) on the ship at ({c.X:F0}, {c.Y:F0}), deck {deckZ:F0}");
        }
    }

    /// <summary>The holds' navmeshes begun with the scenes' interiors.</summary>
    void BeginHoldNavmeshes(List<(Cell Cell, Skyrim.Navmesh.NavmeshBuilder.PendingInterior Pending)> interiors)
    {
        foreach (var (cell, solid, door) in holds)
            try
            {
                interiors.Add((cell, navmeshes!.BeginInterior(cell, solid, [door], keepAround: door.Placement!.Position, keepAlso: null, underwater: null,
                    background: true, snapActors: true, bridgeGaps: true)));
            }
            catch (Exception e) { Problems.Add($"navmesh {cell.EditorID}: {e.Message}"); }
    }

    // ---- the captain's quarters ----

    const float CabinLength = 448, CabinWidth = 352;
    /// <summary>Each ship's cabin door (by the ship's reference) and the place on deck before it.</summary>
    readonly Dictionary<FormKey, (PlacedObject Door, Vector3 Front)> cabinOf = [];
    public int Cabins => cabinOf.Count;
    Door? cabinDoor, cabinDoorOut;
    Static? cabinRoom;
    List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>? cabinRoomPolys;

    void MakeCabinModels()
    {
        if (cabinDoor is not null) return;
        // A plank door in its frame, upright, thin along its own Y.
        var door = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>();
        door.AddRange(HoldBox(new Vector3(-36, -3, 0), new Vector3(36, 3, 150)));
        door.AddRange(HoldBox(new Vector3(-46, -6, 0), new Vector3(-36, 6, 160)));
        door.AddRange(HoldBox(new Vector3(36, -6, 0), new Vector3(46, 6, 160)));
        door.AddRange(HoldBox(new Vector3(-46, -6, 150), new Vector3(46, 6, 160)));
        var dm = BlockMeshBuilder.BuildPolygons(door, Path.Combine(modOutDir, "meshes", @"tamrielmerge\rg\cabindoor.nif"));
        cabinDoor = new Door(mod) { EditorID = "RGCabinDoor", Name = "Captain's Quarters", Model = new Model { File = @"tamrielmerge\rg\cabindoor.nif" }, ObjectBounds = BoundsOf(dm.Min, dm.Max) };
        cabinDoorOut = new Door(mod) { EditorID = "RGCabinDoorOut", Name = "Deck", Model = new Model { File = @"tamrielmerge\rg\cabindoor.nif" }, ObjectBounds = BoundsOf(dm.Min, dm.Max) };
        mod.Doors.Add(cabinDoor);
        mod.Doors.Add(cabinDoorOut);

        float x = CabinWidth / 2, y = CabinLength / 2;
        var room = new List<(Vector3[] Pts, Vector3 N, BlockMeshBuilder.Tex Tex)>
        {
            HoldQuad(new(-x, -y, 0), new(x, -y, 0), new(x, y, 0), new(-x, y, 0), Vector3.UnitZ),
            HoldQuad(new(-x, -y, HoldHeight), new(x, -y, HoldHeight), new(x, y, HoldHeight), new(-x, y, HoldHeight), -Vector3.UnitZ),
            HoldQuad(new(-x, -y, 0), new(-x, y, 0), new(-x, y, HoldHeight), new(-x, -y, HoldHeight), Vector3.UnitX),
            HoldQuad(new(x, -y, 0), new(x, y, 0), new(x, y, HoldHeight), new(x, -y, HoldHeight), -Vector3.UnitX),
            HoldQuad(new(-x, -y, 0), new(x, -y, 0), new(x, -y, HoldHeight), new(-x, -y, HoldHeight), Vector3.UnitY),
            HoldQuad(new(-x, y, 0), new(x, y, 0), new(x, y, HoldHeight), new(-x, y, HoldHeight), -Vector3.UnitY),
        };
        for (float by = -y + 64; by < y; by += 128) room.AddRange(HoldBox(new Vector3(-x, by - 8, HoldHeight - 16), new Vector3(x, by + 8, HoldHeight)));
        var rm = BlockMeshBuilder.BuildPolygons(room, Path.Combine(modOutDir, "meshes", @"tamrielmerge\rg\cabin.nif"));
        cabinRoom = new Static(mod) { EditorID = "RGCabinRoom", Model = new Model { File = @"tamrielmerge\rg\cabin.nif" }, ObjectBounds = BoundsOf(rm.Min, rm.Max) };
        mod.Statics.Add(cabinRoom);
        cabinRoomPolys = room;
    }

    Cell NewShipCell(string editorId, string name)
    {
        var amb = System.Drawing.Color.FromArgb(0, 60, 50, 38);
        var cell = new Cell(mod)
        {
            EditorID = editorId,
            Name = name,
            Flags = Cell.Flag.IsInteriorCell | Cell.Flag.PublicArea,
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Lighting = new CellLighting
            {
                AmbientColor = amb, DirectionalColor = System.Drawing.Color.FromArgb(0, 44, 38, 28), DirectionalFade = 1,
                FogNearColor = System.Drawing.Color.Black, FogFarColor = System.Drawing.Color.Black, FogFar = 100000, FogPower = 1, FogMax = 1,
                LightFadeBegin = 6000, LightFadeEnd = 8000,
                AmbientColors = new AmbientColors
                {
                    DirectionalXPlus = amb, DirectionalXMinus = amb, DirectionalYPlus = amb, DirectionalYMinus = amb, DirectionalZPlus = amb, DirectionalZMinus = amb,
                    Specular = System.Drawing.Color.Black, Scale = 1,
                },
                Inherits = 0,
            },
            Persistent = [],
            Temporary = [],
        };
        int blockNo = (int)(cell.FormKey.ID % 10), subNo = (int)(cell.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
        if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(sb => sb.BlockNumber == subNo);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
        return cell;
    }

    /// <summary>
    /// The captain's quarters (user, 2026-11-10: "the captain suppose to come out of catains quarters of the ship too and
    /// theres not a quarter"): a door on the bulkhead at the aft end of the main deck, where the ship's own cabin would
    /// be (the first wall met going aft along the middle of the deck, a little over it), and the cabin behind it as an
    /// interior: bed, table, chair, chest, lantern. Brennan comes out of his ship's in the opening (RgOpening).
    /// </summary>
    void BuildCabin(Scene scene, PlacedObject r, List<Vector3[]> solid, Vector3 c, Vector3 fwd, float deckZ, float scale, float heading, Action<string>? log)
    {
        // Aft from the deck's middle at chest height: the nearest wall across the ship.
        var from = new Vector3(c.X, c.Y, deckZ + 70);
        var dir = -fwd;
        float best = float.MaxValue;
        foreach (var tri in solid)
            for (int k = 1; k + 1 < tri.Length; k++)
            {
                var a0 = tri[0]; var e1 = tri[k] - a0; var e2 = tri[k + 1] - a0;
                var nrm = Vector3.Cross(e1, e2);
                if (nrm.LengthSquared() < 1e-6f || MathF.Abs(Vector3.Dot(Vector3.Normalize(nrm), fwd)) < 0.5f) continue;
                var h = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-6f) continue;
                var sv = from - a0; float u = Vector3.Dot(sv, h) / det;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(sv, e1); float v = Vector3.Dot(dir, q) / det;
                if (v < 0 || u + v > 1) continue;
                float t = Vector3.Dot(e2, q) / det;
                if (t > 96 && t < best) best = t;
            }
        if (best > ShipModel.Length * scale * 0.6f) { Problems.Add($"{scene.Name}: ship at ({c.X:F0}, {c.Y:F0}): no bulkhead aft of its main deck, no captain's quarters"); return; }
        var wall = from + dir * best;
        // The ship's own quarters (ShipModel.Quarters: the wall built across its stern): the door at that wall's middle.
        // The first wall met along the deck's middle had been the main mast (the door stood on it 471 aft of the middle
        // of Brennan's ship, 2026-11-12; user: "the ship does not have a captain's quarter").
        if (ship is not null)
        {
            float aft = -ShipModel.QuartersWallY * scale, across = ShipModel.QuartersDoorX * scale;
            // The model's +x is to the right of its +y (the bow) seen from above.
            var right = new Vector3(fwd.Y, -fwd.X, 0);
            wall = new Vector3(c.X + dir.X * aft + right.X * across, c.Y + dir.Y * aft + right.Y * across, deckZ + 70);
            best = aft;
        }
        // Room to stand before it, on the main deck.
        if (Morrowind.MwPluginBuilder.ClearSpot(new Vector3(wall.X + fwd.X * 80, wall.Y + fwd.Y * 80, deckZ + 10), solid, above: 20) is not { } front
            || MathF.Abs(front.Z - deckZ) > 24)
        { Problems.Add($"{scene.Name}: ship at ({c.X:F0}, {c.Y:F0}): no room to stand before its aft bulkhead, no captain's quarters"); return; }
        MakeCabinModels();
        var door = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(cabinDoor!.FormKey),
            Placement = new Placement { Position = new P3Float(wall.X + fwd.X * 8, wall.Y + fwd.Y * 8, deckZ), Rotation = new P3Float(0, 0, heading) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        // No taller than the wall it stands in on a small ship (the door is 160 high, the wall 257 at the model's own size).
        if (ship is not null && (ShipModel.WheelDeckZ - ship.Deck) * scale < 170) door.Scale = MathF.Max(0.4f, (ShipModel.WheelDeckZ - ship.Deck) * scale / 170f);
        PersistentCellFor(scene).Persistent.Add(door);
        scene.LoadDoors.Add(door);
        cabinOf[r.FormKey] = (door, front);

        int n = cabinOf.Count;
        var cell = NewShipCell($"RGShipCabin{n:00}", "Captain's Quarters");
        PlacedObject Put(FormKey baseKey, float x, float y, float z, float yaw = 0, bool persistent = false)
        {
            var p = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(baseKey),
                Placement = new Placement { Position = new P3Float(x, y, z), Rotation = new P3Float(0, 0, yaw) },
            };
            if (persistent) { p.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400; cell.Persistent.Add(p); }
            else cell.Temporary.Add(p);
            return p;
        }
        Put(cabinRoom!.FormKey, 0, 0, 0);
        float endY = CabinLength / 2;
        // The way out at the fore end (the cabin lies aft of its door).
        var outDoor = Put(cabinDoorOut!.FormKey, 0, endY - 8, 0, persistent: true);
        door.TeleportDestination = new TeleportDestination { Door = outDoor.ToLink<IPlacedObjectGetter>(), Position = new P3Float(0, endY - 96, 8), Rotation = new P3Float(0, 0, MathF.PI) };
        outDoor.TeleportDestination = new TeleportDestination { Door = door.ToLink<IPlacedObjectGetter>(), Position = new P3Float(front.X, front.Y, front.Z + 8), Rotation = new P3Float(0, 0, heading) };
        var solidIn = cabinRoomPolys!.Select(p => (p.Pts, p.N)).ToList();
        void Furnish(FormKey? baseKey, float x, float y, float yaw, float halfX, float halfY, float height)
        {
            if (baseKey is not { } k) return;
            Put(k, x, y, 0, yaw);
            solidIn.AddRange(HoldBox(new Vector3(x - halfX, y - halfY, 0), new Vector3(x + halfX, y + halfY, height)).Select(p => (p.Pts, p.N)));
        }
        float side = CabinWidth / 2;
        Furnish((vanilla.Get<IFurnitureGetter>("CommonBed01") ?? vanilla.Get<IFurnitureGetter>("CommonBedDouble01"))?.FormKey, -side + 60, -endY + 110, 0, 56, 104, 48);
        Furnish(vanilla.Get<IStaticGetter>("CommonTable02")?.FormKey, side - 80, -endY + 120, MathF.PI / 2, 60, 90, 56);
        Furnish(vanilla.Get<IFurnitureGetter>("CommonChair01")?.FormKey, side - 150, -endY + 120, MathF.PI / 2, 24, 24, 56);
        Furnish(vanilla.Get<IContainerGetter>("TreasUpperChestEMPTY")?.FormKey, -side + 44, 40, MathF.PI / 2, 30, 46, 44);
        if (vanilla.Get<IStaticGetter>("CandleLanternwithCandle01") is { } lantern) Put(lantern.FormKey, 0, 0, HoldHeight - 16 - 40);
        if (vanilla.Get<ILightGetter>("SolitudeInnCandleDefault") is { } light)
        {
            Put(light.FormKey, 0, 0, HoldHeight - 64);
            Put(light.FormKey, 0, -endY + 120, HoldHeight - 64);
        }
        holds.Add((cell, solidIn, outDoor));
        log?.Invoke($"cabin {n}: door at ({wall.X:F0}, {wall.Y:F0}, {deckZ:F0}), {best:F0} aft of the middle of the ship at ({c.X:F0}, {c.Y:F0})");
    }
}
