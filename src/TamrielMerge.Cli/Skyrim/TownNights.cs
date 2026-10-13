using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Nights in town (docs/progression.md, "Nights in town"), what each game's plugin provides for them:
/// <list type="bullet">
/// <item>its guards, tagged "{prefix}Guards", some of whom doze on night watch (TMNightDanger, TMDozeEffect);</item>
/// <item>its settlements: Daggerfall places night stalkers from its climate's "in location, night" encounter table in
/// every town (TownBuilder), Morrowind and Oblivion list their town centres ("{prefix}TownCentres", XMarkers) so that
/// TMNightDanger can bring Skyrim's thieves and assassins out after dark near the player.</item>
/// </list>
/// The progression plugin finds these by EditorID in each converted game's plugin.
/// </summary>
public static class TownNights
{
    public static readonly FormKey XMarker = new(ModKey.FromFileName("Skyrim.esm"), 0x3B);

    public static string GuardsId(string prefix) => prefix + "Guards";
    public static string TownCentresId(string prefix) => prefix + "TownCentres";

    static readonly Dictionary<(ModKey, string), Faction> guards = new();

    /// <summary>A game's guard tag (made once per plugin).</summary>
    public static Faction Guards(SkyrimMod mod, string prefix)
    {
        lock (guards)
        {
            if (!guards.TryGetValue((mod.ModKey, prefix), out var f))
                guards[(mod.ModKey, prefix)] = f = CoinTrade.Tag(mod, GuardsId(prefix), "Guards");
            return f;
        }
    }

    /// <summary>Daggerfall's "in location, night" encounter table for a climate (Daggerfall Unity RandomEncounters:
    /// 20 desert, 23 mountain, 26 rainforest, 29 subtropical, 32 swamp and woodlands, 35 haunted woodlands).</summary>
    public static int NightTableOf(DfClimate climate) => climate switch
    {
        DfClimate.Desert or DfClimate.Desert2 => 20,
        DfClimate.Mountain => 23,
        DfClimate.Rainforest => 26,
        DfClimate.Subtropical => 29,
        DfClimate.HauntedWoodlands => 35,
        _ => 32,
    };
}
