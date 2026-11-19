using Mutagen.Bethesda.Plugins;

namespace TamrielMerge.Skyrim;

/// <summary>Skyrim's own plugins in their load order: the master order converted plugins are written with (Mutagen's
/// default is alphabetical, which lists Dragonborn.esm before Skyrim.esm).</summary>
public static class VanillaMasters
{
    public static readonly ModKey[] Order =
        [.. new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f))];
}
