using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;
using TamrielMerge.Skyrim.Meshes;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's hair, hair colours, ages and skin on its people (user, 2026-11-14: "the hairstyles from the games need
/// to be used and the colors", "make sure the hair fits and does not clip", "plus age of the npc matters", "we need
/// skin colors too", "the hair is its own model").
/// <list type="bullet">
/// <item>Each person has a hairstyle (a HAIR record: its own model and texture) and a hair colour. The style's model
/// is brought from Oblivion's head onto Skyrim's (the two skulls box to box: FaceGeomHair.OntoHead), fitted outside
/// Skyrim's head so none of it sinks in, tinted with the person's colour (the hair shader's tint; colours in steps of
/// 16 a channel, so people of one style and nearly one colour share a model), and worn as its own model: an item in
/// the hair slots (31 and 41), which hides the Skyrim face's own hair as a helmet does. It can't be taken (not
/// playable). Under a helmet or hood of their own they wear none: the helmet hides hair as in Oblivion.</item>
/// <item>The face itself is one of Skyrim's of the race and sex (a converted person needs a pre-made face), picked by
/// the person's age and skin shade (ObFaces, FaceChoice).</item>
/// </list>
/// Not here: a hair cut into a face file (a plain shape among a face file's crashed the game, 2026-11-14).
/// </summary>
public sealed partial class ObPluginBuilder
{
    ObFaces? obFaces;
    FaceChoice? faceChoice;
    readonly Dictionary<string, Armor?> hairWigs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(FormKey Race, bool Female), float[]> shadeSpread = new();
    public int HairModels { get; private set; }
    public int HairWorn { get; private set; }
    public int HairUnderHeadwear { get; private set; }
    public int HairFailed { get; private set; }
    public int FacesByAge => faceChoice?.ByAge ?? 0;
    public int FacesByShade => faceChoice?.ByShade ?? 0;
    public int FacesAgeUnmatched => faceChoice?.AgeUnmatched ?? 0;

    /// <summary>Skyrim's head model for an Oblivion race: its beast and orc heads have their own.</summary>
    static string HeadKind(string obRace) =>
        obRace.Contains("Khajiit", StringComparison.OrdinalIgnoreCase) ? "khajiit"
        : obRace.Contains("Argonian", StringComparison.OrdinalIgnoreCase) ? "argonian"
        : obRace.Contains("Orc", StringComparison.OrdinalIgnoreCase) ? "orc" : "";

    /// <summary>Oblivion's head model of a race (its face data's head part), the human one without.</summary>
    string ObHeadModel(FormKey race)
    {
        const string human = @"characters\imperial\headhuman.nif";
        if (records.GetValueOrDefault(race) is not Ob.IRaceGetter r) return human;
        foreach (var part in r.FaceData)
            if (part.Model?.File is { Length: > 0 } file && Path.GetFileName(file).StartsWith("head", StringComparison.OrdinalIgnoreCase)) return file;
        return human;
    }

    /// <summary>The face a person wears: one of the pool's by their age and skin shade (FaceChoice).</summary>
    INpcGetter FaceFor(Ob.INpcGetter n, IReadOnlyList<INpcGetter> pool, bool female)
    {
        obFaces ??= new ObFaces(data);
        faceChoice ??= new FaceChoice(Vanilla.Skyrim);
        int? ageClass = obFaces.Age(n) is { } age ? (age >= 55 ? 2 : age >= 36 ? 1 : 0) : null;
        float? rank = null;
        if (obFaces.Shade(n) is { } shade)
        {
            var key = (n.Race.FormKey, female);
            if (!shadeSpread.TryGetValue(key, out var spread))
                shadeSpread[key] = spread = records.Values.OfType<Ob.INpcGetter>()
                    .Where(x => x.Race.FormKey == key.Item1 && (x.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.Female) == true) == female)
                    .Select(x => obFaces.Shade(x)).OfType<float>().Order().ToArray();
            if (spread.Length > 1)
            {
                int at = Array.BinarySearch(spread, shade);
                if (at < 0) at = ~at;
                rank = Math.Clamp(at / (float)(spread.Length - 1), 0, 1);
            }
        }
        return faceChoice.Pick(pool, ageClass, rank, n.EditorID ?? n.FormKey.ToString());
    }

    /// <summary>The person's hair as an item to wear, or null (no hairstyle, or its model can't be made: logged).</summary>
    Armor? HairWig(Ob.INpcGetter n, bool female, string obRace)
    {
        if (n.Hair.FormKeyNullable is not { } hairKey || records.GetValueOrDefault(hairKey) is not Ob.IHairGetter hair
            || hair.Model?.File is not { Length: > 0 } model) return null;
        var colour = n.HairColor ?? System.Drawing.Color.FromArgb(96, 80, 64);
        // In steps of 16 a channel (the middle of each step).
        static int Step(int c) => Math.Min(255, c / 16 * 16 + 8);
        int r = Step(colour.R), g = Step(colour.G), b = Step(colour.B);
        string kind = HeadKind(obRace);
        string stem = Sanitize(hair.EditorID ?? hairKey.ID.ToString("X6"));
        string id = $"{stem}{(female ? "F" : "M")}{kind}{r:X2}{g:X2}{b:X2}";
        if (hairWigs.TryGetValue(id, out var done)) return done;
        hairWigs[id] = null;
        try
        {
            characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
            wornSource ??= new ObWornSource(data, meshes.Textures);
            var problems = new List<string>();
            var skeleton = characterAssets.Skeleton(female);
            List<WornShape> source, headSource; BindSkeleton obSkeleton;
            lock (wornSourceLock)
            {
                (source, obSkeleton) = wornSource.Read(model, problems, keepSkin: true, rides: "Bip01 Head");
                (headSource, _) = wornSource.Read(ObHeadModel(n.Race.FormKey), problems, keepSkin: true, rides: "Bip01 Head");
            }
            if (source.Count == 0) throw new InvalidDataException("no shapes");
            if (headSource.Count == 0) throw new InvalidDataException("Oblivion's head for it has no shapes");
            var shapes = WornMesh.Retarget(source, obSkeleton, skeleton, problems);
            var obHead = WornMesh.Retarget(headSource, obSkeleton, skeleton, problems);
            FaceGeomHair.OntoHead(shapes, obHead, FaceGeomHair.HeadOf(characterAssets.Head(female, kind)));
            var surface = characterAssets.HeadSurface(female, kind);
            for (int round = 0; round < 4 && WornMesh.FitOutside(shapes, surface, 0.3f) > 0; round++) { }
            // The style's own texture (the HAIR record's: grey, for the colour to tint), not the model's stand-in.
            string? texture = hair.Icon is { Length: > 0 } icon ? (icon.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? icon : @"textures\" + icon) : null;
            string? diffuse = texture is not null ? meshes.Textures.Ensure(texture) : null;
            string? normal = diffuse is not null ? meshes.Textures.Normal(texture!) : null;
            var tint = new Vector3(r / 255f, g / 255f, b / 255f);
            var worn = new List<WornShape>();
            foreach (var s in shapes)
            {
                var w = s.With();
                w.ShaderType = 6;
                // Vanilla hair's shader flags (HairShort in a face file: 0x8E440303 / 0x002080A1) without its vertex
                // colours, which these models don't have.
                w.Flags1 = 0x8E440303;
                w.Flags2 = 0x00208081;
                w.Glossiness = 10;
                w.SpecularStrength = 0.9f;
                w.Alpha = 1;
                w.AlphaFlags = 4845;
                w.AlphaThreshold = 111;
                w.HairTint = tint;
                w.Emissive = Vector3.Zero;
                w.EmissiveMultiple = 0;
                if (diffuse is not null) w.Textures = [diffuse, normal ?? meshes.Textures.Flat()];
                worn.Add(w);
            }
            string rel = $@"{ObMeshConverter.OutFolder}\hair\{id.ToLowerInvariant()}.nif";
            SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, rel), worn, skeleton, 131));
            HairModels++;

            var slots = BipedObjectFlag.Hair | BipedObjectFlag.LongHair;
            var aa = mod.ArmorAddons.DuplicateInAsNewRecord(Vanilla.Skyrim.ArmorAddons.First(x => x.EditorID == "IronHelmetAA"));
            aa.EditorID = $"OBHair{id}AA";
            aa.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, ArmorType = ArmorType.Clothing };
            aa.WorldModel = new GenderedItem<Model?>(new Model { File = SkyrimModelPath(rel) }, new Model { File = SkyrimModelPath(rel) });
            aa.FirstPersonModel = null;
            aa.WeightSliderEnabled = new GenderedItem<bool>(false, false);
            aa.SkinTexture = null;
            aa.TextureSwapList = null;

            var wig = mod.Armors.DuplicateInAsNewRecord(Vanilla.Get<IArmorGetter>("ArmorIronHelmet")!);
            wig.EditorID = $"OBHair{id}";
            wig.Name = hair.Name is { Length: > 0 } styleName ? styleName : "Hair";
            wig.VirtualMachineAdapter = null;
            wig.ObjectEffect.Clear();
            wig.TemplateArmor.Clear();
            wig.Keywords = null;
            wig.Description = null;
            wig.Value = 0;
            wig.Weight = 0;
            wig.ArmorRating = 0;
            wig.WorldModel = null;
            wig.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, ArmorType = ArmorType.Clothing, Flags = BodyTemplate.Flag.NonPlayable };
            wig.MajorFlags |= Armor.MajorFlag.NonPlayable;
            wig.Armature.Clear();
            wig.Armature.Add(aa.ToLink<IArmorAddonGetter>());
            foreach (var p in problems.Distinct()) Problems.Add($"hair {model}: {p}");
            return hairWigs[id] = wig;
        }
        catch (Exception e)
        {
            HairFailed++;
            Problems.Add($"hair {model} ({hair.EditorID}): {e.Message}");
            return null;
        }
    }

    /// <summary>Puts the person's own hair on them, unless they wear something over the head already.</summary>
    void WearHair(Npc npc, Ob.INpcGetter n, bool female, string obRace)
    {
        if (HairWig(n, female, obRace) is not { } wig) return;
        var head = BipedObjectFlag.Head | BipedObjectFlag.Hair | BipedObjectFlag.LongHair;
        bool covered = npc.Items?.Any(i => mod.Armors.TryGetValue(i.Item.Item.FormKey, out var a) && ((a.BodyTemplate?.FirstPersonFlags ?? 0) & head) != 0) == true;
        if (covered) { HairUnderHeadwear++; return; }
        npc.Items ??= [];
        npc.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(wig.FormKey), Count = 1 } });
        HairWorn++;
    }
}
