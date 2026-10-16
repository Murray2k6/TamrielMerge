using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's worn armour and clothing look: each item's body parts (ARMO/CLOT INDX + BNAM male / CNAM female BODY
/// records) converted from Morrowind's skeleton to Skyrim's (<see cref="MwWornSource"/>, <see cref="WornMesh"/>),
/// fitted over Skyrim's body at both weights, written as skinned armour NIFs with first-person versions (the arms and
/// hands), and given Skyrim armor addons with the biped slots Skyrim draws them in.
/// <para>Slots (Skyrim's naked body is one piece where Morrowind's is many, so most parts layer over it): helmets take
/// the hair (and the head when they cover Morrowind's head part); cuirasses take the body, and carry Skyrim's own body
/// beneath them so arms and legs still show; right gauntlets and gloves take the hands and carry both bare hands (the
/// left is then its own glove or bare); boots and shoes the feet; right bracers the forearms; left gauntlets, gloves and
/// bracers, pauldrons, greaves, shirts, pants, skirts, robes and belts unused slots of their own (46-60), so each is
/// worn with the rest as in Morrowind. Rings and amulets have no worn model in Morrowind and show nothing.</para>
/// </summary>
public sealed partial class MwPluginBuilder
{
    SkyrimCharacterAssets? characterAssets;
    MwWornSource? wornSource;
    /// <summary>Items whose worn look is Morrowind's own, and those that fell back to their Skyrim template's.</summary>
    public int WornConverted { get; private set; }
    public int WornFallbacks { get; private set; }

    /// <summary>How a kind of worn item is drawn: its slots, the dismember partition, what it fits over (Skyrim's naked
    /// "body", "hands" or "feet", null for nothing), how far outside it sits, the bare skin it carries, and the slots
    /// whose worn items it draws over and hides (<see cref="Hides"/>: its addon claims them too, at a higher priority,
    /// while the item itself keeps only its own slot, so the hidden items stay worn and keep their armour).</summary>
    sealed record WornLook(BipedObjectFlag Slots, int Partition, string? Fit, float Margin, string? Skin, BipedObjectFlag Hides = 0);

    /// <summary>The addon priority of things worn over others (robes, skirts): above every template's (0-1).</summary>
    const byte OverPriority = 50;

    static readonly BipedObjectFlag Head = BipedObjectFlag.Head, Hair = BipedObjectFlag.Hair;

    /// <summary>The look of a Morrowind armour type (AODT type: 0 helmet .. 10 right bracer).</summary>
    static WornLook? ArmorLook(int type, bool coversHead) => type switch
    {
        0 => new(coversHead ? Head | Hair : Hair, coversHead ? 30 : 31, null, 0, null),
        1 => new(BipedObjectFlag.Body, 32, "body", 1.2f, "body"),
        2 => new(Slot(57), 57, "body", 1.6f, null),
        3 => new(Slot(58), 58, "body", 1.6f, null),
        4 => new(Slot(53), 53, "body", 1.2f, null),
        5 => new(BipedObjectFlag.Feet, 37, "feet", 0.8f, null),
        6 => new(Slot(60), 60, "hands", 0.6f, null),
        7 => new(BipedObjectFlag.Hands, 33, "hands", 0.6f, "hands"),
        8 => new(BipedObjectFlag.Shield, 39, null, 0, null),
        9 => new(Slot(60), 60, "body", 1.0f, null),
        10 => new(BipedObjectFlag.Forearms, 34, "body", 1.0f, null),
        _ => null,
    };

    /// <summary>The look of a Morrowind clothing type (CTDT type: 0 pants .. 9 amulet); rings and amulets have none.</summary>
    static WornLook? ClothingLook(int type) => type switch
    {
        0 => new(Slot(52), 52, "body", 0.4f, null),
        1 => new(BipedObjectFlag.Feet, 37, "feet", 0.5f, null),
        2 => new(Slot(46), 46, "body", 0.4f, null),
        3 => new(Slot(48), 48, "body", 0.9f, null),
        // Robes are worn over everything on the body and legs, as Morrowind draws them (2026-10-16, user: "fix the older
        // gaps" - a cuirass or greaves showed through the robe): the cuirass, greaves, shirt, pants and skirt hidden under
        // it; the robe carries the bare body beneath, as a cuirass does, since it takes the body slot from the skin.
        4 => new(Slot(47), 47, "body", 1.8f, "body", BipedObjectFlag.Body | Slot(53) | Slot(46) | Slot(52) | Slot(49)),
        5 => new(BipedObjectFlag.Hands, 33, "hands", 0.4f, "hands"),
        6 => new(Slot(60), 60, "hands", 0.4f, null),
        // Skirts over the greaves and pants.
        7 => new(Slot(49), 49, "body", 1.4f, null, Slot(53) | Slot(52)),
        _ => null,
    };

    /// <summary>An item's body parts: (part index, male BODY id, female BODY id), in record order.</summary>
    static List<(int Index, string? Male, string? Female)> WornParts(Tes3Record rec)
    {
        var parts = new List<(int, string?, string?)>();
        foreach (var f in rec.Fields)
        {
            if (f.Type == "INDX") parts.Add((f.Data[0], null, null));
            else if (parts.Count > 0 && f.Type is "BNAM" or "CNAM")
            {
                var (i, m, fe) = parts[^1];
                string id = f.Text.TrimEnd('\0');
                parts[^1] = f.Type == "BNAM" ? (i, id.Length > 0 ? id : null, fe) : (i, m, id.Length > 0 ? id : null);
            }
        }
        return parts;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<(bool Female, bool FirstPerson), string>?> wornMeshes = new(StringComparer.OrdinalIgnoreCase);
    readonly object wornSourceLock = new();

    /// <summary>
    /// The worn meshes of an item (male and female, both weights, first person where it has arms or hands), written
    /// under meshes\tamrielmerge\mw\worn\&lt;id&gt;\; their data-relative paths by (female, first person), or null when
    /// they can't be made (the reason added to <paramref name="problems"/>). Safe to run in parallel.
    /// </summary>
    Dictionary<(bool Female, bool FirstPerson), string>? WornMeshes(Tes3Record rec, WornLook look, List<string> problems)
    {
        var parts = WornParts(rec);
        if (parts.Count == 0) return null;
        string folder = $@"{MwMeshConverter.OutFolder}\worn\{Sanitize(rec.Id).ToLowerInvariant()}";
        var paths = new Dictionary<(bool Female, bool FirstPerson), string>();
        try
        {
            foreach (bool female in new[] { false, true })
            {
                var skyrimSkeleton = characterAssets!.Skeleton(female);
                var shapes = new List<WornShape>();
                foreach (var (index, male, fem) in parts)
                {
                    string? bodyId = female ? fem ?? male : male;
                    if (bodyId is null || !world.Objects.TryGetValue(bodyId, out var body) || body.Type != "BODY" || body.Str("MODL") is not { Length: > 0 } model) continue;
                    List<WornShape> source; BindSkeleton skeleton;
                    // Reading converts textures (shared state): one part at a time.
                    lock (wornSourceLock) (source, skeleton) = wornSource!.Part(model, index, female, problems);
                    shapes.AddRange(WornMesh.Retarget(source, skeleton, skyrimSkeleton, problems));
                }
                if (shapes.Count == 0) throw new InvalidDataException("no part has a model");
                bool any1st = false;
                foreach (int weight in new[] { 0, 1 })
                {
                    var fitted = shapes.Select(s => s.With(s.V.ToArray(), s.N.ToArray())).ToList();
                    // Fitted until nothing sinks in (a push can bring a vertex near another part of the body: armpits,
                    // between the legs), at most four rounds.
                    if (look.Fit is { } over)
                        for (int round = 0; round < 4 && WornMesh.FitOutside(fitted, characterAssets.Surface(over, female, weight), look.Margin) > 0; round++) { }
                    if (look.Skin is { } skin) fitted.AddRange(characterAssets.Naked(skin, female, weight));
                    string name = $@"{folder}\{(female ? "f" : "m")}_{weight}.nif";
                    SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, name), fitted, skyrimSkeleton, look.Partition));
                    var first = WornMesh.FirstPerson(fitted);
                    if (first.Count > 0)
                    {
                        string firstName = $@"{folder}\1st_{(female ? "f" : "m")}_{weight}.nif";
                        SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, firstName), first, skyrimSkeleton, look.Partition));
                        any1st = true;
                    }
                }
                paths[(female, false)] = $@"{folder}\{(female ? "f" : "m")}_1.nif";
                if (any1st) paths[(female, true)] = $@"{folder}\1st_{(female ? "f" : "m")}_1.nif";
            }
            return paths;
        }
        catch (Exception e)
        {
            problems.Add($"worn {rec.Id}: {e.Message} (template look kept)");
            return null;
        }
    }

    /// <summary>The look an item gets (null: none drawn, or a canon Skyrim remake that keeps Skyrim's).</summary>
    WornLook? LookOf(Tes3Record rec)
    {
        if (rec.Type == "ARMO" && rec.Get("AODT") is { } d)
        {
            int type = d.Int(0);
            return CanonArmor(rec.Id, type) is null ? ArmorLook(type, WornParts(rec).Any(p => p.Index == 0)) : null;
        }
        return rec.Type == "CLOT" && rec.Get("CTDT") is { } c ? ClothingLook(c.Int(0)) : null;
    }

    /// <summary>
    /// Converts every armour and clothing item's worn meshes at once, in parallel (geometry on all cores; NIF writing
    /// and Morrowind's texture conversion one at a time), so building the items later only makes their records.
    /// Returns (converted, failed).
    /// </summary>
    public (int Converted, int Failed) PrepareWorn()
    {
        characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
        wornSource ??= new MwWornSource(data, textures);
        foreach (bool female in new[] { false, true })
        {
            characterAssets.Skeleton(female);
            foreach (var part in new[] { "body", "hands", "feet" })
                foreach (int w in new[] { 0, 1 }) characterAssets.Surface(part, female, w);
        }
        var work = world.Objects.Values.Where(o => o.Type is "ARMO" or "CLOT")
            .Select(o => (Rec: o, Look: LookOf(o))).Where(x => x.Look is not null && WornParts(x.Rec).Count > 0).ToList();
        int converted = 0, failed = 0;
        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, x =>
        {
            var local = new List<string>();
            var paths = WornMeshes(x.Rec, x.Look!, local);
            wornMeshes[x.Rec.Id] = paths;
            if (paths is null) Interlocked.Increment(ref failed); else Interlocked.Increment(ref converted);
            foreach (var p in local) problems.Add(p);
        });
        Problems.AddRange(problems);
        return (converted, failed);
    }

    /// <summary>
    /// Morrowind's worn look for an item as Skyrim armor addons (one per addon of the vanilla template, keeping its
    /// races, sounds and priorities), or null when it can't be made (logged; the item then keeps its template's look).
    /// </summary>
    List<ArmorAddon>? WornAddons(Tes3Record rec, string edid, WornLook look, IArmorGetter template)
    {
        characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
        wornSource ??= new MwWornSource(data, textures);
        if (!wornMeshes.TryGetValue(rec.Id, out var paths)) wornMeshes[rec.Id] = paths = WornMeshes(rec, look, Problems);
        if (paths is null) return null;
        string Model(bool female, bool firstPerson) => SkyrimModelPath(paths[(female, firstPerson)]);
        // One addon per template addon (races: human, Argonian, Khajiit variants), all wearing Morrowind's model.
        var templates = template.Armature.Select(l => SkyrimEsm.ArmorAddons.FirstOrDefault(a => a.FormKey == l.FormKey)).OfType<IArmorAddonGetter>().ToList();
        if (templates.Count == 0) templates.Add(SkyrimEsm.ArmorAddons.First(a => a.EditorID == "IronCuirassAA"));
        var addons = new List<ArmorAddon>();
        for (int i = 0; i < templates.Count; i++)
        {
            var aa = mod.ArmorAddons.DuplicateInAsNewRecord(templates[i]);
            aa.EditorID = edid + "AA" + (i == 0 ? "" : i.ToString());
            // The addon claims the slots it hides too: of addons sharing a slot, Skyrim draws the higher priority's.
            aa.BodyTemplate = new BodyTemplate { FirstPersonFlags = look.Slots | look.Hides, ArmorType = ArmorType.Clothing, Flags = templates[i].BodyTemplate?.Flags ?? 0 };
            if (look.Hides != 0) aa.Priority = new GenderedItem<byte>(OverPriority, OverPriority);
            aa.WorldModel = new GenderedItem<Model?>(new Model { File = Model(false, false) }, new Model { File = Model(true, false) });
            aa.FirstPersonModel = paths.ContainsKey((false, true)) || paths.ContainsKey((true, true))
                ? new GenderedItem<Model?>(paths.ContainsKey((false, true)) ? new Model { File = Model(false, true) } : null, paths.ContainsKey((true, true)) ? new Model { File = Model(true, true) } : null)
                : null;
            aa.WeightSliderEnabled = new GenderedItem<bool>(true, true);
            aa.SkinTexture = null;
            aa.TextureSwapList = null;
            addons.Add(aa);
        }
        return addons;
    }

    /// <summary>Gives an item (already its template's copy) Morrowind's worn look, or keeps the template's when that
    /// fails. <paramref name="look"/> null: the item has no worn model in Morrowind (rings, amulets) and draws none.</summary>
    void ApplyWornLook(Armor a, Tes3Record rec, WornLook? look, IArmorGetter template, BipedObjectFlag? fallbackSlot)
    {
        a.BodyTemplate ??= new BodyTemplate();
        if (look is null)
        {
            a.Armature.Clear();
            return;
        }
        if (WornParts(rec).Count == 0)
        {
            // No body parts: Morrowind draws nothing for it when worn (belts, some rings of armour); it keeps its slot.
            a.Armature.Clear();
            a.BodyTemplate.FirstPersonFlags = look.Slots;
            return;
        }
        if (WornAddons(rec, a.EditorID!, look, template) is { } addons)
        {
            a.Armature.Clear();
            foreach (var aa in addons) a.Armature.Add(aa.ToLink<IArmorAddonGetter>());
            a.BodyTemplate.FirstPersonFlags = look.Slots;
            WornConverted++;
            return;
        }
        // Fallback (logged by WornAddons): the template's own look, or for a part Skyrim has no slot for, that unused
        // slot drawing nothing.
        WornFallbacks++;
        if (fallbackSlot is { } s)
        {
            a.BodyTemplate.FirstPersonFlags = s;
            a.Armature.Clear();
        }
    }

    /// <summary>Dev aid (mw-worn): converts the worn look of the given Morrowind items into the output folder, with
    /// their Skyrim records built as the plugin builds them. Returns lines describing each.</summary>
    public IEnumerable<string> ConvertWorn(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            int before = Problems.Count;
            var item = Item(id);
            yield return item is Armor a
                ? $"{id}: {a.EditorID} slots {a.BodyTemplate?.FirstPersonFlags}, {a.Armature.Count} addons: {string.Join(", ", a.Armature.Select(l => mod.ArmorAddons.FirstOrDefault(x => x.FormKey == l.FormKey)?.WorldModel?.Male?.File.DataRelativePath.Path ?? l.FormKey.ToString()))}"
                : $"{id}: {item?.GetType().Name ?? "not an item"}";
            foreach (var p in Problems.Skip(before)) yield return "   problem: " + p;
        }
    }
}
