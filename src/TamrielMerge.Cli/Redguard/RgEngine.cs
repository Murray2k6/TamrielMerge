using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Redguard;

/// <summary>
/// What Redguard's engine did that Skyrim's doesn't, built for the converted scripts and the player:
/// <list type="bullet">
/// <item>ledges (RGLedgeMarker): Redguard's Cyrus caught any ledge he jumped at; the places too high to jump onto
/// in Skyrim (a walkable edge with walkable ground 72-240 below, found in each place's collision) are marked, and
/// TMRgMove carries the player up when they jump at one;</item>
/// <item>ropes (MPRP, never placed before): hanging links of Redguard's own rope or chain model; a rope with somewhere to
/// land is an activator (TMRgRope) that swings the player across, its landings found in the collision;</item>
/// <item>screen pictures (ViewGXA's .GXA images: the island map's pieces, the mages' mirror) as notes showing the
/// image (RGPictures), the animation groups' idles (RGIdles, RGPlayerIdles), and Gerrick's parrot (Redguard's own
/// model on its perch, its talk on an unseen actor).</item>
/// </list>
/// </summary>
public sealed partial class RgPluginBuilder
{
    // ---- the shared quest of the movement helpers (TMRgMove) ----

    Quest? movementQuest;
    Static? ledgeBase;
    public int Ledges { get; private set; }
    public int Ropes { get; private set; }
    public int RopeSwings { get; private set; }

    Quest MovementQuest()
    {
        if (movementQuest is not null) return movementQuest;
        movementQuest = new Quest(mod)
        {
            EditorID = "RGMovement",
            Name = "Redguard Movement",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
            Priority = 10,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(movementQuest);
        return movementQuest;
    }

    Static LedgeBase()
    {
        if (ledgeBase is not null) return ledgeBase;
        var xmarker = SkyrimEsm.Statics.First(s => s.FormKey == Sk(0x3B));
        ledgeBase = mod.Statics.DuplicateInAsNewRecord(xmarker);
        ledgeBase.EditorID = "RGLedgeMarker";
        return ledgeBase;
    }

    /// <summary>Fills in the movement quest's script once the shared quest exists (AttachScripts).</summary>
    void FinishMovement()
    {
        var q = MovementQuest();
        var entry = new ScriptEntry { Name = "TMRgMove" };
        entry.Properties.Add(ObjProp("RG", scriptsQuest!.FormKey));
        entry.Properties.Add(ObjProp("LedgeMarker", LedgeBase().FormKey));
        q.VirtualMachineAdapter = new QuestAdapter { Scripts = [entry] };
    }

    // ---- surfaces (for ledges and rope landings) ----

    sealed class Surfaces
    {
        public readonly List<Vector3[]> Tris = [];
        readonly Dictionary<(int, int), List<int>> grid = [];
        const float G = 128;

        public void Add(Vector3[] t)
        {
            var n = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
            if (n.LengthSquared() < 1e-6f) return;
            n = Vector3.Normalize(n);
            if (MathF.Abs(n.Z) < 0.7f) return;   // walkable: under about 45 degrees
            int i = Tris.Count;
            Tris.Add(t);
            float x0 = MathF.Min(t[0].X, MathF.Min(t[1].X, t[2].X)), x1 = MathF.Max(t[0].X, MathF.Max(t[1].X, t[2].X));
            float y0 = MathF.Min(t[0].Y, MathF.Min(t[1].Y, t[2].Y)), y1 = MathF.Max(t[0].Y, MathF.Max(t[1].Y, t[2].Y));
            for (int gx = (int)MathF.Floor(x0 / G); gx <= (int)MathF.Floor(x1 / G); gx++)
                for (int gy = (int)MathF.Floor(y0 / G); gy <= (int)MathF.Floor(y1 / G); gy++)
                    (grid.TryGetValue((gx, gy), out var l) ? l : grid[(gx, gy)] = []).Add(i);
        }

        /// <summary>The highest walkable surface at (x, y) between bottom and top.</summary>
        public float? Below(float x, float y, float top, float bottom)
        {
            if (!grid.TryGetValue(((int)MathF.Floor(x / G), (int)MathF.Floor(y / G)), out var l)) return null;
            float? best = null;
            foreach (int i in l)
            {
                var t = Tris[i];
                if (HeightIn(t, x, y) is float z && z <= top && z >= bottom && (best is null || z > best)) best = z;
            }
            return best;
        }

        static float? HeightIn(Vector3[] t, float x, float y)
        {
            float d = (t[1].Y - t[2].Y) * (t[0].X - t[2].X) + (t[2].X - t[1].X) * (t[0].Y - t[2].Y);
            if (MathF.Abs(d) < 1e-6f) return null;
            float a = ((t[1].Y - t[2].Y) * (x - t[2].X) + (t[2].X - t[1].X) * (y - t[2].Y)) / d;
            float b = ((t[2].Y - t[0].Y) * (x - t[2].X) + (t[0].X - t[2].X) * (y - t[2].Y)) / d;
            float c = 1 - a - b;
            if (a < -0.001f || b < -0.001f || c < -0.001f) return null;
            return a * t[0].Z + b * t[1].Z + c * t[2].Z;
        }
    }

    readonly Dictionary<string, Surfaces> surfaces = [];

    /// <summary>A place's walkable surfaces: its placed models' collision, and its land.</summary>
    Surfaces SurfacesOf(Scene scene)
    {
        if (surfaces.TryGetValue(scene.Name, out var s)) return s;
        s = new Surfaces();
        if (scene.Interior is { } cell)
            foreach (var t in Solids(cell.Temporary.OfType<PlacedObject>())) s.Add(t);
        else
            foreach (var ((cx, cy), cell2) in scene.Cells)
            {
                foreach (var t in Solids(cell2.Temporary.OfType<PlacedObject>())) s.Add(t);
                if (cell2.Landscape is { } land)
                {
                    var tris = new List<Vector3[]>();
                    Skyrim.Navmesh.NavmeshBuilder.AddTerrain(tris, land, cx, cy, null);
                    foreach (var t in tris) s.Add(t);
                }
            }
        return surfaces[scene.Name] = s;
    }

    // ---- ledges ----

    const float LedgeMin = 72, LedgeMax = 240;

    /// <summary>
    /// The ledges of a place: along each walkable triangle's edges (every 48 units), where the walkable surface stops
    /// (nothing walkable within 40 of its height just beyond) and walkable ground lies 72-240 below a little further
    /// out, a marker at the top, facing onto it from below. One per 48-unit cell.
    /// </summary>
    void PlaceLedges(Scene scene)
    {
        if (scene.Name == OpeningScene) return;
        var s = SurfacesOf(scene);
        var seen = new HashSet<(int, int, int)>();
        int placed = 0;
        foreach (var t in s.Tris)
        {
            var c = (t[0] + t[1] + t[2]) / 3;
            for (int e = 0; e < 3; e++)
            {
                var a = t[e]; var b = t[(e + 1) % 3];
                var along = new Vector2(b.X - a.X, b.Y - a.Y);
                float len = along.Length();
                if (len < 12 || MathF.Abs(a.Z - b.Z) > len * 0.5f) continue;
                along /= len;
                var n = new Vector2(along.Y, -along.X);
                if (Vector2.Dot(n, new Vector2((a.X + b.X) / 2 - c.X, (a.Y + b.Y) / 2 - c.Y)) < 0) n = -n;
                int steps = Math.Max(1, (int)(len / 48));
                for (int k = 0; k < steps; k++)
                {
                    float f = (k + 0.5f) / steps;
                    var p = Vector3.Lerp(a, b, f);
                    // The surface goes no further: nothing walkable near its height just beyond the edge.
                    if (s.Below(p.X + n.X * 20, p.Y + n.Y * 20, p.Z + 40, p.Z - 40) is not null) continue;
                    // Ground below, out from the edge, too far down to jump up from.
                    if (s.Below(p.X + n.X * 48, p.Y + n.Y * 48, p.Z - LedgeMin, p.Z - LedgeMax) is not float low) continue;
                    if (s.Below(p.X + n.X * 48, p.Y + n.Y * 48, p.Z + 40, p.Z - LedgeMin + 1) is not null) continue;
                    var top = new Vector3(p.X - n.X * 14, p.Y - n.Y * 14, p.Z);
                    if (!seen.Add(((int)MathF.Floor(top.X / 48), (int)MathF.Floor(top.Y / 48), (int)MathF.Floor(top.Z / 48)))) continue;
                    CellFor(scene, top).Temporary.Add(new PlacedObject(mod)
                    {
                        Base = new FormLinkNullable<IPlaceableObjectGetter>(LedgeBase().FormKey),
                        Placement = new Placement { Position = new P3Float(top.X, top.Y, top.Z), Rotation = new P3Float(0, 0, MathF.Atan2(-n.X, -n.Y)) },
                    });
                    placed++;
                }
            }
        }
        Ledges += placed;
    }

    // ---- ropes ----

    readonly Dictionary<(string Palette, string Link, int Links), (RgMeshConverter.Result Mesh, float Length)?> ropeMeshes = [];

    /// <summary>A hanging rope or chain: its link model repeated downwards from the top (no collision).</summary>
    (RgMeshConverter.Result Mesh, float Length)? RopeMesh(Scene scene, RgScene.Rope rope)
    {
        var key = (scene.Palette, rope.Link.ToUpperInvariant(), rope.Links);
        if (ropeMeshes.TryGetValue(key, out var hit)) return hit;
        ropeMeshes[key] = null;
        if (rope.Links <= 0 || Model(scene, rope.Link) is not { } link || link.Vertices.Count == 0) return null;
        // Redguard's y is down: links stack along +y, each its own height apart.
        float y0 = link.Vertices.Min(v => v.Y), y1 = link.Vertices.Max(v => v.Y), h = MathF.Max(4, y1 - y0);
        var model = new RgModel();
        for (int i = 0; i < Math.Min(rope.Links, 200); i++)
        {
            int start = model.Vertices.Count;
            var offset = new Vector3(0, i * h - y0, 0);
            model.Vertices.AddRange(link.Vertices.Select(v => v + offset));
            foreach (var f in link.Faces)
                model.Faces.Add(f with { Vertices = f.Vertices.Select(x => x + start).ToArray(), Uv = (Vector2[])f.Uv.Clone() });
        }
        try
        {
            var mesh = meshes.Convert(model, $"rope_{Sanitize(rope.Link).ToLowerInvariant()}_{rope.Links}", scene.Palette, modOutDir, collide: false);
            return ropeMeshes[key] = mesh is null || mesh.Path.Length == 0 ? null : (mesh, Math.Min(rope.Links, 200) * h);
        }
        catch (Exception e) { Problems.Add($"rope {rope.Link}: {e.Message}"); return null; }
    }

    Mutagen.Bethesda.Skyrim.Activator? ropeActivator;
    readonly Dictionary<string, Static> ropeStatics = [];

    /// <summary>
    /// Redguard's ropes and chains, hanging from their tops. A rope (not a chain carrying something) with ground to
    /// land on is an activator that swings the player: the landings are the two walkable places, on opposite sides
    /// and within its reach (out to 1.1 times its length, from its bottom up to just under its top), whose line
    /// crosses the deepest drop under the rope; a rope with one such place takes the player between it and the ground
    /// below the rope (a rope to climb).
    /// </summary>
    void PlaceRopes(Scene scene)
    {
        if (scene.Name == OpeningScene) return;
        foreach (var rope in scene.Data.Ropes)
        {
            if (RopeMesh(scene, rope) is not var (mesh, length)) continue;
            var top = At(scene, rope.Top);
            short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            var bounds = new ObjectBounds { First = new P3Int16(C(mesh.Min.X), C(mesh.Min.Y), C(mesh.Min.Z)), Second = new P3Int16(C(mesh.Max.X), C(mesh.Max.Y), C(mesh.Max.Z)) };
            float[]? landings = rope.Hang.Length == 0 && rope.Link.StartsWith("ROPE", StringComparison.OrdinalIgnoreCase) ? RopeLandings(scene, top, length) : null;
            var placed = new PlacedObject(mod) { Placement = new Placement { Position = new P3Float(top.X, top.Y, top.Z), Rotation = new P3Float(0, 0, 0) } };
            if (landings is not null)
            {
                var act = new Mutagen.Bethesda.Skyrim.Activator(mod)
                {
                    EditorID = $"RGRope{Ropes}",
                    Name = "Rope",
                    ActivateTextOverride = "Swing on",
                    ObjectBounds = bounds,
                    Model = new Model { File = mesh.Path[7..] },
                };
                mod.Activators.Add(act);
                placed.Base = new FormLinkNullable<IPlaceableObjectGetter>(act.FormKey);
                placed.VirtualMachineAdapter = new VirtualMachineAdapter
                {
                    Scripts =
                    [
                        new ScriptEntry
                        {
                            Name = "TMRgRope",
                            Properties = [ObjProp("Mover", MovementQuest().FormKey), new ScriptFloatListProperty { Name = "Landings", Data = [.. landings] }, new ScriptFloatProperty { Name = "RopeLength", Data = length }],
                        },
                    ],
                };
                RopeSwings++;
            }
            else
            {
                if (!ropeStatics.TryGetValue(mesh.Path, out var stat))
                {
                    stat = new Static(mod) { EditorID = $"RGRopeStatic{ropeStatics.Count}", ObjectBounds = bounds, Model = new Model { File = mesh.Path[7..] } };
                    mod.Statics.Add(stat);
                    ropeStatics[mesh.Path] = stat;
                }
                placed.Base = new FormLinkNullable<IPlaceableObjectGetter>(stat.FormKey);
            }
            CellFor(scene, top).Temporary.Add(placed);
            Ropes++;
        }
    }

    /// <summary>The two landings of a rope (x y z each), or null when there is nowhere to swing to.</summary>
    float[]? RopeLandings(Scene scene, Vector3 top, float length)
    {
        var s = SurfacesOf(scene);
        float bottom = top.Z - length;
        // Under the rope: the floor (or nothing: a pit).
        float under = s.Below(top.X, top.Y, top.Z - 40, bottom - 2000) ?? bottom - 2000;
        // Around it, every 15 degrees: the nearest walkable place from 64 out to 1.1 lengths, high enough to matter.
        var best = new (Vector3 P, float Score)?[24];
        for (int d = 0; d < 24; d++)
        {
            float ang = d * MathF.PI / 12;
            var dir = new Vector2(MathF.Sin(ang), MathF.Cos(ang));
            for (float r = 64; r <= length * 1.1f; r += 24)
            {
                float x = top.X + dir.X * r, y = top.Y + dir.Y * r;
                if (s.Below(x, y, top.Z - 60, bottom - 120) is float z && z > under + 48)
                {
                    best[d] = (new Vector3(x + dir.X * 32, y + dir.Y * 32, (s.Below(x + dir.X * 32, y + dir.Y * 32, z + 40, z - 40) ?? z)), z - under);
                    break;
                }
            }
        }
        // The pair on opposite sides (within 45 degrees of it) with the deepest drop between.
        (Vector3 A, Vector3 B, float Score)? pair = null;
        for (int i = 0; i < 24; i++)
            for (int j = i + 1; j < 24; j++)
            {
                int apart = Math.Abs(i - j);
                if (best[i] is not { } a || best[j] is not { } b || Math.Min(apart, 24 - apart) < 9) continue;
                float score = MathF.Min(a.Score, b.Score);
                if (pair is null || score > pair.Value.Score) pair = (a.P, b.P, score);
            }
        if (pair is { } p) return [p.A.X, p.A.Y, p.A.Z, p.B.X, p.B.Y, p.B.Z];
        // One place to reach: between it and the floor under the rope.
        var one = best.Where(b => b is not null).Select(b => b!.Value).OrderByDescending(b => b.P.Z).FirstOrDefault();
        if (one.Score > 0 && under > bottom - 400)
            return [one.P.X, one.P.Y, one.P.Z, top.X, top.Y - 1, under];
        return null;
    }

    // ---- screen pictures ----

    readonly List<string> pictureFiles = [];

    /// <summary>The picture number of a ViewGXA image (its file under Redguard's folder), -1 when it isn't there.</summary>
    int PictureIndex(string file)
    {
        string rel = file.Replace('/', '\\').Trim();
        if (!File.Exists(Path.Combine(rgDir, rel))) return -1;
        int i = pictureFiles.FindIndex(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) return i;
        pictureFiles.Add(rel);
        return pictureFiles.Count - 1;
    }

    /// <summary>
    /// The pictures as notes (RGPictures): each .GXA's first image (GXlib's BMHD/BPAL/BBMP: a 6-bit VGA palette, then
    /// per frame an 18-byte header with the size at 2 and 4 and the 8-bit pixels) as a texture under
    /// textures\tamrielmerge\rg\pictures, shown full page.
    /// </summary>
    FormList BuildPictures()
    {
        var list = new FormList(mod) { EditorID = "RGPictures" };
        mod.FormLists.Add(list);
        var template = SkyrimEsm.Books.First(b => b.FormKey == Sk(0x0F0422));   // dunTolvaldsCaveNote01: a single page
        foreach (var rel in pictureFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(rel).ToLowerInvariant();
            try
            {
                var (px, w, h) = ReadGxa(File.ReadAllBytes(Path.Combine(rgDir, rel)));
                string dds = Path.Combine(modOutDir, "textures", "tamrielmerge", "rg", "pictures", stem + ".dds");
                Directory.CreateDirectory(Path.GetDirectoryName(dds)!);
                Morrowind.MwTextures.WriteBcDds(dds, px, w, h, false);
                var book = mod.Books.DuplicateInAsNewRecord(template);
                book.EditorID = "RGPicture" + Sanitize(stem);
                book.Name = stem.StartsWith("stmap") ? "Map of Stros M'Kai" : stem.StartsWith("mirror") ? "The Mirror's Vision" : stem;
                book.BookText = $"<p align='center'><img src='img://Textures/TamrielMerge/rg/pictures/{stem}.png' width='440' height='{440 * h / Math.Max(1, w)}'></p>";
                book.Flags |= Book.Flag.CantBeTaken;
                book.Teaches = new BookTeachesNothing();
                book.VirtualMachineAdapter = null;
                book.Value = 0;
                list.Items.Add(book.ToLink<ISkyrimMajorRecordGetter>());
            }
            catch (Exception e)
            {
                Problems.Add($"picture {rel}: {e.Message}");
                list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(Sk(0x3B)));
            }
        }
        return list;
    }

    public static (uint[] Px, int W, int H) ReadGxa(byte[] b)
    {
        uint[] palette = new uint[256];
        int p = 0;
        while (p + 8 <= b.Length)
        {
            string tag = System.Text.Encoding.ASCII.GetString(b, p, 4);
            int len = b[p + 4] << 24 | b[p + 5] << 16 | b[p + 6] << 8 | b[p + 7];
            int o = p + 8;
            if (tag == "BPAL")
                for (int i = 0; i < 256 && o + i * 3 + 2 < b.Length; i++)
                {
                    uint V(int k) => (uint)Math.Min(255, b[o + i * 3 + k] * 255 / 63);
                    palette[i] = 0xFF000000u | V(0) << 16 | V(1) << 8 | V(2);
                }
            else if (tag == "BBMP")
            {
                int w = BitConverter.ToUInt16(b, o + 2), h = BitConverter.ToUInt16(b, o + 4);
                if (w <= 0 || h <= 0 || o + 18 + w * h > b.Length) throw new InvalidDataException("bad BBMP");
                var px = new uint[w * h];
                for (int i = 0; i < w * h; i++) px[i] = palette[b[o + 18 + i]];
                return (px, w, h);
            }
            if (tag == "END ") break;
            p = o + len;
        }
        throw new InvalidDataException("no BBMP");
    }

    // ---- animation groups as idles ----

    FormList IdleList(string edid, Dictionary<int, string> idles)
    {
        var list = new FormList(mod) { EditorID = edid };
        mod.FormLists.Add(list);
        int count = Math.Max(170, idles.Keys.Max() + 1);
        for (int i = 0; i < count; i++)
        {
            var idle = idles.TryGetValue(i, out var name) ? SkyrimEsm.IdleAnimations.FirstOrDefault(x => x.EditorID == name) : null;
            if (idles.ContainsKey(i) && idle is null) Problems.Add($"idle {name} not in Skyrim.esm");
            list.Items.Add(idle is null ? new FormLink<ISkyrimMajorRecordGetter>(Sk(0x3B)) : new FormLink<ISkyrimMajorRecordGetter>(idle.FormKey));
        }
        return list;
    }

    // ---- Gerrick's parrot ----

    /// <summary>
    /// The parrot as Redguard showed it: its own model (PRRTA001.3DC, first frame) on its perch, and the talk on the
    /// actor standing there, whose chicken body is left unseen (a skin with nothing to wear).
    /// </summary>
    void PlaceParrotModel(Scene scene, RgScene.Placed o, Npc npc)
    {
        if (StaticFor(scene, "PRRTA001") is not { } s || s.Base is null) { Problems.Add("parrot: PRRTA001 not converted"); return; }
        PlaceStatic(scene, s, o.Position, o.Rotation);
        var skin = new Armor(mod)
        {
            EditorID = "RGParrotSkin",
            ObjectBounds = new ObjectBounds(),
            BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body },
            Armature = [],
        };
        mod.Armors.Add(skin);
        npc.WornArmor.SetTo(skin);
        npc.Packages.Clear();
    }
}
