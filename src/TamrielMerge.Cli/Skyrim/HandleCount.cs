using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// What a plugin takes of Skyrim's reference handles at load (a user's report, 2026-10-27: Daggerfall Towns.esm with
/// 3,462,154 persistent references crashed the data load, and TamrielMerge - Creatures.esp, a plain ESP of 537,318
/// references, filled the table so other mods' first reference got a null handle). The engine keeps at most 2^20
/// (1,048,576) handles: every persistent reference takes one at load, and in a plugin without the master flag every
/// reference in its worldspaces does. The converter counts them for each plugin it writes and warns when the total nears
/// the limit (<see cref="Warn"/>), leaving room for the user's other mods.
/// </summary>
public static class HandleCount
{
    public const long Limit = 1L << 20;
    /// <summary>Handles the converted plugins may take together before a warning: room for Skyrim's own (about 110,000)
    /// and the user's other mods.</summary>
    public const long Warn = 700_000;

    public sealed record Of(string Plugin, bool Master, long Persistent, long WorldRefs, long Handles);

    /// <summary>The handles <paramref name="path"/> takes: its persistent references (anywhere), plus, when it hasn't
    /// the master flag, every other reference in its worldspaces.</summary>
    public static Of Count(string path)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        bool master = mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master);
        long persistent = 0, worldRefs = 0;
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            persistent += cell.Persistent.Count;
        foreach (var w in mod.Worldspaces)
        {
            var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
            if (w.TopCell is { } top) cells.Add(top);
            foreach (var cell in cells)
            {
                persistent += cell.Persistent.Count;
                worldRefs += cell.Temporary.Count;
            }
        }
        return new Of(System.IO.Path.GetFileName(path), master, persistent, worldRefs, persistent + (master ? 0 : worldRefs));
    }

    /// <summary>Counts <paramref name="plugins"/> (in <paramref name="dir"/>), logging each and a warning past <see cref="Warn"/>
    /// in all; returns the total.</summary>
    public static long Report(string dir, IEnumerable<string> plugins, Action<string> log)
    {
        long total = 0;
        foreach (var p in plugins)
        {
            string path = System.IO.Path.Combine(dir, p);
            if (!File.Exists(path)) continue;
            var c = Count(path);
            total += c.Handles;
            if (c.Handles > 0) log($"  {c.Plugin}: {c.Handles:N0} reference handles ({c.Persistent:N0} persistent{(c.Master ? "" : $", {c.WorldRefs:N0} in worldspaces of a plugin without the master flag")})");
        }
        log(total > Warn
            ? $"WARNING: the converted plugins take {total:N0} of Skyrim's {Limit:N0} reference handles at load, over the {Warn:N0} that leaves room for the game's own and other mods: the game may crash loading"
            : $"Reference handles: {total:N0} of Skyrim's {Limit:N0} taken by the converted plugins at load");
        return total;
    }
}
