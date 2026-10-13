using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's own furniture and props with Skyrim's use (user, 2026-10-12: objects - "DF models, Skyrim use"): each
/// model PropReplacer's table names (chairs, thrones, benches, beds, bookcases, wardrobes, counters, crates, chests,
/// coffins, statues...) becomes a mesh of its own (Daggerfall's polygons and upscaled textures, static collision,
/// Meshes\TamrielMerge\Props\{model}.nif) and a copy of the Skyrim record it stood for - a container still opens and
/// keeps its loot, furniture still seats or beds. Furniture's mesh is built in its Skyrim stand-in's space, Daggerfall's
/// model where the stand-in would have stood, so the stand-in's sit and sleep markers (copied from its model) fit, and
/// it is placed as the stand-in was; everything else is placed exactly as Daggerfall places it.
/// </summary>
/// <param name="fixedKeys">In Daggerfall.esm, each base at 0xE00000 + its model number: a record numbered in the
/// running order would renumber everything built after it, which the plugins built on it and saves refer to.</param>
public sealed class DfPropBuilder(SkyrimMod mod, ISkyrimModGetter skyrim, string modOutDir, PropReplacer props, bool fixedKeys = false)
{
    readonly Dictionary<uint, (FormKey Base, bool Furniture)> bases = new();

    /// <summary>The beds made (copies of Skyrim's bed: tavern rooms are made of them, TownShops).</summary>
    public static readonly HashSet<FormKey> Beds = new();

    /// <summary>The reference for a table model, or null when the model isn't in the table. <paramref name="sky"/> is
    /// where its Skyrim stand-in would have stood (PropReplacer.Replace).</summary>
    public PlacedObject? Place(DfRdbObject o, Vector3 blockOrigin, DfModel model, PropReplacer.Placement? sky, Vector3 shift = default)
    {
        if (PropReplacer.StandInFor(o.ModelId) is not { } standIn) return null;
        var m = DfDungeon.ObjectTransform(o, blockOrigin);
        if (Base(o.ModelId, model, standIn.Base, m, sky) is not { } made) return null;
        if (made.Furniture && sky is not null)
            return new PlacedObject(mod)
            {
                Base = made.Base.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(sky.PositionSkyrim.X + shift.X, sky.PositionSkyrim.Y + shift.Y, sky.PositionSkyrim.Z + shift.Z), Rotation = new P3Float(0, 0, sky.Heading) },
            };
        var (position, angles) = ActionObjectBuilder.Pose(m);
        return new PlacedObject(mod)
        {
            Base = made.Base.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(position.X + shift.X, position.Y + shift.Y, position.Z + shift.Z), Rotation = new P3Float(angles.X, angles.Y, angles.Z) },
        };
    }

    (FormKey Base, bool Furniture)? Base(uint id, DfModel model, FormKey skyrimBase, Matrix4x4 m, PropReplacer.Placement? sky)
    {
        if (bases.TryGetValue(id, out var hit)) return hit;
        var record = skyrim.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == skyrimBase);
        if (record is null) return null;
        string rel = Path.Combine("TamrielMerge", "Props", $"{id}.nif");
        string full = Path.Combine(modOutDir, "Meshes", rel);
        bool furniture = record is IFurnitureGetter && sky is not null;
        BlockMeshBuilder.Result built;
        if (furniture)
        {
            // Daggerfall's model where it stands, then into the stand-in's own space (its position, turned back by its
            // heading: Skyrim turns clockwise).
            var (pos, angles) = ActionObjectBuilder.Pose(m);
            float c = MathF.Cos(sky!.Heading), s = MathF.Sin(sky.Heading);
            Vector3 World(Vector3 local) => Turn(local, angles) + new Vector3(pos.X, pos.Y, pos.Z);
            Vector3 Back(Vector3 w) => new(w.X * c - w.Y * s, w.X * s + w.Y * c, w.Z);
            var origin = new Vector3(sky.PositionSkyrim.X, sky.PositionSkyrim.Y, sky.PositionSkyrim.Z);
            built = BlockMeshBuilder.BuildPropModel(model, full, v => Back(World(v) - origin), n => Back(Turn(n, angles)));
            AddMarkers(full, skyrimBase);
        }
        else
            built = BlockMeshBuilder.BuildPropModel(model, full);
        var bounds = new ObjectBounds
        {
            First = new P3Int16(C(built.Min.X), C(built.Min.Y), C(built.Min.Z)),
            Second = new P3Int16(C(built.Max.X), C(built.Max.Y), C(built.Max.Z)),
        };
        var key = fixedKeys ? new FormKey(mod.ModKey, 0xE00000 + id) : mod.GetNextFormKey();
        IMajorRecord copy;
        switch (record)
        {
            case IFurnitureGetter f:
                var furn = (Furniture)f.Duplicate(key);
                furn.Model = new Model { File = rel }; furn.ObjectBounds = bounds; mod.Furniture.Add(furn); copy = furn;
                break;
            case IContainerGetter ct:
                var cont = (Container)ct.Duplicate(key);
                cont.Model = new Model { File = rel }; cont.ObjectBounds = bounds; mod.Containers.Add(cont); copy = cont;
                break;
            case IActivatorGetter a:
                var act = (Mutagen.Bethesda.Skyrim.Activator)a.Duplicate(key);
                act.Model = new Model { File = rel }; act.ObjectBounds = bounds; mod.Activators.Add(act); copy = act;
                break;
            default:
                var stat = new Static(key, SkyrimRelease.SkyrimSE) { Model = new Model { File = rel }, ObjectBounds = bounds };
                mod.Statics.Add(stat); copy = stat;
                break;
        }
        copy.EditorID = $"DFProp{id}";
        if (skyrimBase == TownShops.Bed) Beds.Add(copy.FormKey);
        props.AddShape(copy.FormKey, built.Min, built.Max);   // its bounds, for resting stacks (SettleProps)
        return bases[id] = (copy.FormKey, furniture);
    }

    /// <summary>A vector turned by Skyrim Euler angles (radians; clockwise, X then Y then Z about world axes).</summary>
    static Vector3 Turn(Vector3 v, Vector3 a)
    {
        // Column-vector R = Rz(-z) Ry(-y) Rx(-x); System.Numerics' rotations are counter-clockwise for row vectors,
        // so the same matrices applied in that order give it.
        var r = Matrix4x4.CreateRotationX(-a.X) * Matrix4x4.CreateRotationY(-a.Y) * Matrix4x4.CreateRotationZ(-a.Z);
        return Vector3.TransformNormal(v, r);
    }

    static short C(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);

    /// <summary>The Skyrim furniture's marker block, copied as it is into the prop's mesh (built in the same space).</summary>
    void AddMarkers(string nifPath, FormKey skyrimBase)
    {
        if (props.FurnitureMarkerBlock(skyrimBase) is not nifly.NiExtraData markers) return;
        using var nif = new nifly.NifFile();
        if (nif.Load(nifPath) != 0) return;
        nif.AssignExtraData(nif.GetRootNode(), BlockMeshBuilder.Owned(markers));
        nif.Save(nifPath);
    }
}
