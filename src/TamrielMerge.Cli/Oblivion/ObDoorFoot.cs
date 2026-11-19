using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using Noggog;
using TamrielMerge.Skyrim.Meshes;
using Ob = Mutagen.Bethesda.Oblivion;
using V3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Doors with their origin at their foot, as Skyrim's own doors have it (measured: vanilla's inner doors stand a
/// median 2 units under their navmesh; Oblivion's converted stood a median 26 over it, the 95th percentile 240). A
/// door's model is written again with everything in it raised until its foot (the lowest point of its collision; of
/// its shapes, without) is at the origin, and the door is placed that much lower: it looks and stands as before.
/// <para>What this does NOT do: make Skyrim's people open the door. That was the reason it was written (2026-11-14
/// afternoon, after Skyrim's jail door put in the prisoner's cell gate's place was tried by the Emperor on the floor
/// and not 68 units up), and the runs that followed showed otherwise: the converted gate with its origin on the
/// floor was still never tried. What keeps Skyrim's people from a door is Skyrim's lock on it with no key in their
/// hand (TMObLock.Own, TMObDoor). Also here: plain doors given a swing of their own (DoorSwing).</para>
/// </summary>
public sealed partial class ObPluginBuilder
{
    readonly Dictionary<string, (ObMeshConverter.Result Mesh, float Foot)> doorsAtFoot = new(StringComparer.OrdinalIgnoreCase);
    static readonly object DoorFootLock = new();
    /// <summary>Door models written again with their origin at their foot, and the doors placed with one.</summary>
    public int DoorModelsFooted { get; private set; }
    public int DoorsFooted { get; private set; }

    /// <summary>How far under (negative) or over a model's origin its foot is.</summary>
    static float FootOf(ObMeshConverter.Result mesh) =>
        mesh.Solid.Count > 0 ? mesh.Solid.Min(t => t.Min(p => p.Z)) : mesh.Min.Z;

    /// <summary>The door's model with its origin at its foot, and how far the foot was from the origin (a door placed
    /// with it goes that far along its own up axis: <see cref="AtFoot"/>). The model itself when its foot is within 4
    /// units of its origin already.</summary>
    (ObMeshConverter.Result Mesh, float Foot) DoorAtFoot(ObMeshConverter.Result mesh)
    {
        if (doorsAtFoot.TryGetValue(mesh.Path, out var hit)) return hit;
        float foot = FootOf(mesh);
        if (MathF.Abs(foot) <= 4) return doorsAtFoot[mesh.Path] = (mesh, 0);
        string rel = Path.ChangeExtension(mesh.Path, null) + "_foot.nif";
        var up = new V3(0, 0, -foot);
        try
        {
            lock (DoorFootLock)
            {
                using var nif = new NifFile();
                if (nif.Load(Path.Combine(modOutDir, mesh.Path)) != 0) throw new InvalidDataException("can't be read back");
                var hdr = nif.GetHeader();
                var root = nif.GetRootNode();
                uint rootId = hdr.GetBlockID(root);
                var children = new List<uint>();
                for (uint i = 0; i < root.childRefs.GetSize(); i++)
                    if (root.childRefs.GetBlockRef(i) is var id && id < hdr.GetNumBlocks()) children.Add(id);
                // Everything under one node standing that much higher: the parts keep their own places, animations
                // (which set the parts' own transforms by name) and collision under it.
                var lift = nif.AddNode("DoorFoot", new MatTransform { translation = new nifly.Vector3(0, 0, -foot), scale = 1 }, root);
                uint liftId = hdr.GetBlockID(lift);
                foreach (uint id in children) nif.SetParentNode(hdr.GetBlockById(id), lift);
                // Collision on the root itself goes with it.
                if (root.collisionRef.index < hdr.GetNumBlocks() && hdr.GetBlockById(root.collisionRef.index) is NiCollisionObject col)
                {
                    lift.collisionRef.index = root.collisionRef.index;
                    col.targetRef.index = liftId;
                    root.collisionRef.index = uint.MaxValue;
                }
                string outPath = Path.Combine(modOutDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                if (nif.Save(outPath) != 0) throw new InvalidDataException("can't be written");
            }
        }
        catch (Exception e)
        {
            Problems.Add($"door model {mesh.Path}: origin not moved to its foot ({e.Message}); people may not open it");
            return doorsAtFoot[mesh.Path] = (mesh, 0);
        }
        DoorModelsFooted++;
        var raised = mesh with
        {
            Path = rel,
            Min = mesh.Min + up,
            Max = mesh.Max + up,
            Solid = [.. mesh.Solid.Select(t => t.Select(p => p + up).ToArray())],
            Emitters = [.. mesh.Emitters.Select(e => e with { Position = e.Position + up })],
            Triggers = [.. mesh.Triggers.Select(t => (t.Min + up, t.Max + up))],
        };
        return doorsAtFoot[mesh.Path] = (raised, foot);
    }

    readonly Dictionary<FormKey, Door?> swingingDoors = new();
    /// <summary>Plain doors made Skyrim doors with a swing of their own (DoorSwing), and those that couldn't be.</summary>
    public int SwingingDoorModels { get; private set; }
    public int SwingingDoorsFailed { get; private set; }

    /// <summary>
    /// A door that leads nowhere and has no animation of its own (a house's inner door, a cupboard-room's), as a
    /// Skyrim door: its model given an Open and a Close that turn it about its hinge (DoorSwing), its origin at its
    /// foot. Skyrim then opens and closes it, locks it, and its people use it. Null when the model can't be made
    /// (logged; the caller falls back to the script-swung activator).
    /// </summary>
    Door? SwingingDoor(FormKey key)
    {
        if (swingingDoors.TryGetValue(key, out var hit)) return hit;
        swingingDoors[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IDoorGetter d || d.Model?.File is not { Length: > 0 } file || IsMarker(file)) return null;
        ObMeshConverter.Result mesh;
        string rel;
        float foot;
        try
        {
            mesh = meshes.Convert(file, modOutDir, moving: true);
            foot = FootOf(mesh);
            if (MathF.Abs(foot) <= 4) foot = 0;
            rel = Path.ChangeExtension(mesh.Path, null) + "_door.nif";
            DoorSwing.Write(Path.Combine(modOutDir, mesh.Path), Path.Combine(modOutDir, rel), -foot);
        }
        catch (Exception e)
        {
            SwingingDoorsFailed++;
            Problems.Add($"door model {file}: no swing of its own ({e.Message}); swung by script, which people can't open");
            return null;
        }
        SwingingDoorModels++;
        var up = new V3(0, 0, -foot);
        var raised = mesh with { Path = rel, Min = mesh.Min + up, Max = mesh.Max + up };
        var door = new Door(mod)
        {
            EditorID = "OB" + Sanitize(d.EditorID ?? key.ID.ToString("X6")) + "Swing",
            ObjectBounds = Bounds(raised),
            Model = new Model { File = SkyrimModelPath(rel) },
            OpenSound = new FormLinkNullable<ISoundDescriptorGetter>(SwingOpenSound),
            CloseSound = new FormLinkNullable<ISoundDescriptorGetter>(SwingCloseSound),
        };
        if (d.Name is { Length: > 0 } n) door.Name = n;
        mod.Doors.Add(door);
        if (foot != 0) doorFoot[door.FormKey] = foot;
        return swingingDoors[key] = door;
    }

    /// <summary>Where a door placed with its foot-origin model stands: its place moved along its own up axis by its
    /// foot's distance from the old origin (times its scale).</summary>
    static Placement AtFoot(Placement placement, float foot, float scale)
    {
        if (foot == 0) return placement;
        var by = V3.Transform(new V3(0, 0, foot * scale), TamrielMerge.Skyrim.SkyrimRotation.Matrix(placement.Rotation));
        return new Placement { Position = new P3Float(placement.Position.X + by.X, placement.Position.Y + by.Y, placement.Position.Z + by.Z), Rotation = placement.Rotation };
    }
}
