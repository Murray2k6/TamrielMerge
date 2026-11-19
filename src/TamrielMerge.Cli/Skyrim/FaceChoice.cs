using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Which of Skyrim's faces a converted person wears. A person of another game has that game's own look in its data:
/// an age and a skin shade; Skyrim's faces (VanillaActors.Faces: one per vanilla person of the race and sex) have
/// theirs. The face is picked to match: first by age (young, middle-aged, old), then by where the person's skin lies
/// among their race's in their own game, darkest to lightest, against where the face's lies among the race's faces
/// here; among the few nearest, by the person's name, so two alike in the data still differ. Until 2026-11-14 the
/// face was by name alone, and the old Emperor wore a middle-aged innkeeper's (user: "plus age of the npc matters",
/// "we need skin colors too").
/// </summary>
public sealed class FaceChoice(ISkyrimModGetter skyrim)
{
    readonly Dictionary<FormKey, string> names = skyrim.VoiceTypes.Select(v => (v.FormKey, v.EditorID))
        .Concat(skyrim.TextureSets.Select(t => (t.FormKey, t.EditorID)))
        .Concat(skyrim.Colors.Select(c => (c.FormKey, c.EditorID)))
        .Where(x => x.EditorID is not null).GroupBy(x => x.FormKey).ToDictionary(g => g.Key, g => g.First().EditorID!);
    readonly Dictionary<FormKey, int> ages = new();

    /// <summary>
    /// A vanilla face's age: 2 old, 1 middle-aged, 0 young. Skyrim's people have no age in their records; what shows
    /// it is the voice (the "Old" voices), the skin's texture set (the aged ones: ...Old, ...Age40, ...Age50, ...Rough)
    /// and grey or white hair.
    /// </summary>
    public int AgeClass(INpcGetter n)
    {
        if (ages.TryGetValue(n.FormKey, out int hit)) return hit;
        string voice = names.GetValueOrDefault(n.Voice.FormKey, ""), skin = names.GetValueOrDefault(n.HeadTexture.FormKey, ""), hair = names.GetValueOrDefault(n.HairColor.FormKey, "");
        static bool Has(string s, string words) => System.Text.RegularExpressions.Regex.IsMatch(s, words, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int age = Has(voice, "Old") || Has(skin, "Old|Age50|Age60") || Has(hair, "Grey|Gray|White|Silver") ? 2
            : Has(skin, "Age40|Rough|Aged") ? 1 : 0;
        return ages[n.FormKey] = age;
    }

    /// <summary>How light a face's skin is (0-255): its skin tone (the record's texture lighting).</summary>
    public static float Light(INpcGetter n) => n.TextureLighting is { } c ? 0.299f * c.R + 0.587f * c.G + 0.114f * c.B : 128;

    /// <summary>How many faces were picked with an age to match, with a shade to match, and how many of the aged ones
    /// found no face of their age among their race's.</summary>
    public int ByAge { get; private set; }
    public int ByShade { get; private set; }
    public int AgeUnmatched { get; private set; }

    /// <param name="ageClass">The person's age (0 young, 1 middle-aged, 2 old), when the game's data tells it.</param>
    /// <param name="shadeRank">Where the person's skin lies among their race's in their game (0 darkest, 1 lightest).</param>
    public INpcGetter Pick(IReadOnlyList<INpcGetter> pool, int? ageClass, float? shadeRank, string id)
    {
        var candidates = pool.ToList();
        if (ageClass is { } want)
        {
            ByAge++;
            int nearest = candidates.Min(n => Math.Abs(AgeClass(n) - want));
            if (nearest > 0) AgeUnmatched++;
            candidates = candidates.Where(n => Math.Abs(AgeClass(n) - want) == nearest).ToList();
        }
        if (shadeRank is { } rank && candidates.Count > 1)
        {
            ByShade++;
            var byLight = candidates.OrderBy(Light).ThenBy(n => n.FormKey.ID).ToList();
            int centre = (int)MathF.Round(Math.Clamp(rank, 0, 1) * (byLight.Count - 1));
            // The three nearest in shade (fewer at the ends), the one among them by name.
            int from = Math.Max(0, Math.Min(centre - 1, byLight.Count - 3)), count = Math.Min(3, byLight.Count - from);
            candidates = byLight.GetRange(from, count);
        }
        return candidates[VanillaActors.StableHash(id) % candidates.Count];
    }
}
