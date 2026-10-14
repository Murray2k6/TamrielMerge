using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's quest global variables (Daggerfall Unity PlayerGlobalVars: 64 flags, set by quests and by two dungeon
/// set-global actions), as Skyrim globals named DFQuestGlobal&lt;n&gt;, shared by the dungeon actions and the quest
/// engine. Daggerfall.esm holds all of them at fixed numbers (0xDF0000 + n: a record made in its running order would
/// renumber everything built after it); plugins built on it use its records.
/// </summary>
public static class DfQuestGlobals
{
    public const int Count = 64;
    const uint FixedBase = 0xDF0000;

    public static string EditorId(int n) => $"DFQuestGlobal{n}";

    /// <summary>
    /// The global for index <paramref name="n"/>: <paramref name="mod"/>'s, else <paramref name="master"/>'s, else a new
    /// one in <paramref name="mod"/> (at its fixed number when <paramref name="fixedKey"/>).
    /// </summary>
    public static FormKey For(SkyrimMod mod, int n, ISkyrimModGetter? master = null, bool fixedKey = false)
    {
        string id = EditorId(n);
        if (mod.Globals.FirstOrDefault(g => g.EditorID == id) is { } own) return own.FormKey;
        if (master?.Globals.FirstOrDefault(g => g.EditorID == id) is { } theirs) return theirs.FormKey;
        var global = fixedKey ? new GlobalShort(new FormKey(mod.ModKey, FixedBase + (uint)n), SkyrimRelease.SkyrimSE) : new GlobalShort(mod);
        global.EditorID = id;
        global.Data = 0;
        mod.Globals.Add(global);
        return global.FormKey;
    }

    /// <summary>All of Daggerfall's quest globals, at their fixed numbers (Daggerfall.esm).</summary>
    public static void AddAll(SkyrimMod mod)
    {
        for (int n = 0; n < Count; n++) For(mod, n, fixedKey: true);
    }
}
