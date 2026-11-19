using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's races that have no Skyrim race of their own (RaceStandIns: Dremora, Golden Saints, Dark Seducers,
/// Sheogorath, the vampire races wear a mortal Skyrim race's look) keep their Oblivion race as a keyword on each
/// converted actor (OBRaceDremora, OBRaceGoldenSaint, OBRaceDarkSeducer, OBRaceSheogorath, OBRaceVampire), so
/// GetIsRace on them (ObDialogue's conditions, ObPapyrus's scripts) asks the keyword, not the stand-in race, which
/// Skyrim's own people would satisfy too. Both vampire races share OBRaceVampire (vampirism marks the same).
/// </summary>
public sealed partial class ObPluginBuilder
{
    readonly Dictionary<string, Keyword> raceKeywords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The keyword's EditorID for an Oblivion stand-in race: OBRace + the race (Vampire for both vampire races).</summary>
    internal static string RaceKeywordId(string obRace) =>
        "OBRace" + (obRace.Equals("VampireRace", StringComparison.OrdinalIgnoreCase) ? "Vampire" : obRace);

    /// <summary>The keyword of an Oblivion stand-in race, made once (by the race's EditorID or the keyword's).</summary>
    Keyword RaceKeyword(string obRace)
    {
        string id = obRace.StartsWith("OBRace", StringComparison.OrdinalIgnoreCase) ? obRace : RaceKeywordId(obRace);
        lock (raceKeywords)
        {
            if (raceKeywords.TryGetValue(id, out var k)) return k;
            k = mod.Keywords.FirstOrDefault(x => string.Equals(x.EditorID, id, StringComparison.OrdinalIgnoreCase)) ?? new Keyword(mod) { EditorID = id };
            if (!mod.Keywords.ContainsKey(k.FormKey)) mod.Keywords.Add(k);
            return raceKeywords[id] = k;
        }
    }
}
