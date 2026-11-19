using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Script strings too long for a save. Skyrim stores every script variable in the save game, its strings in a table it
/// reads back into 4,096-byte blocks: a string that doesn't fit what is left starts a new block, but one of 4,096
/// characters or more overruns that block and its own bookkeeping, and loading the save crashes (measured 2026-11-05:
/// SkyrimSE.exe+14AA7ED, "mov byte ptr [rbx+rax], 0" after the read, the block's cursor overwritten with ",0,0,0,0";
/// Morrowind's FactionRanks, 4,207 characters, and a Daggerfall region's town names, 4,348, were in the save that
/// crashed). Every string property, and every element of a string list, has to stay under <see cref="Limit"/>.
/// </summary>
public static class ScriptStrings
{
    /// <summary>The longest a stored script string may be (a 4,096-byte block holds 4,095 characters and the end).</summary>
    public const int SaveBlock = 4096;

    /// <summary>What the converter keeps them under, with room to spare.</summary>
    public const int Limit = 3000;

    public sealed record Long(string Record, string Script, string Property, int Index, int Length);

    /// <summary>Every script string property (and string list element) in the plugin at least <paramref name="min"/>
    /// characters long: on records, quest aliases and dialogue responses.</summary>
    public static List<Long> Find(ISkyrimModGetter mod, int min = SaveBlock)
    {
        var found = new List<Long>();
        void Scripts(string where, IEnumerable<IScriptEntryGetter> scripts)
        {
            foreach (var s in scripts)
                foreach (var p in s.Properties)
                    switch (p)
                    {
                        case IScriptStringPropertyGetter one when one.Data.Length >= min:
                            found.Add(new(where, s.Name, p.Name, -1, one.Data.Length));
                            break;
                        case IScriptStringListPropertyGetter list:
                            for (int i = 0; i < list.Data.Count; i++)
                                if (list.Data[i].Length >= min) found.Add(new(where, s.Name, p.Name, i, list.Data[i].Length));
                            break;
                    }
        }
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec is not IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } vm }) continue;
            string where = $"{rec.GetType().Name.Replace("BinaryOverlay", "")} {rec.FormKey.ID:X6} {rec.EditorID}";
            Scripts(where, vm.Scripts);
            if (vm is IQuestAdapterGetter q)
                foreach (var a in q.Aliases) Scripts(where + " alias " + a.Property.Alias, a.Scripts);
        }
        return found;
    }

    /// <summary>Groups of names (a region's towns) as pieces under <see cref="Limit"/>, "|" between the names in a piece,
    /// each group in one piece or more (never a name split), and each group's first piece (TMStrings.Group reads one
    /// back): (Pieces, First).</summary>
    public static (List<string> Pieces, List<int> First) Grouped(IEnumerable<IEnumerable<string>> groups, int limit = Limit)
    {
        var pieces = new List<string>();
        var first = new List<int>();
        foreach (var group in groups)
        {
            first.Add(pieces.Count);
            var piece = new System.Text.StringBuilder();
            foreach (var name in group)
            {
                if (piece.Length > 0 && piece.Length + 1 + name.Length > limit) { pieces.Add(piece.ToString()); piece.Clear(); }
                if (piece.Length > 0) piece.Append('|');
                piece.Append(name);
            }
            pieces.Add(piece.ToString());
        }
        return (pieces, first);
    }

    /// <summary>One group's names back from <see cref="Grouped"/>'s pieces (also a list of one piece per group, with no
    /// first indexes: a plugin made before the pieces).</summary>
    public static List<string> Group(IReadOnlyList<string> pieces, IReadOnlyList<int> first, int g)
    {
        if (first.Count == 0) return g < pieces.Count ? [.. pieces[g].Split('|', StringSplitOptions.RemoveEmptyEntries)] : [];
        if (g < 0 || g >= first.Count) return [];
        int end = g + 1 < first.Count ? first[g + 1] : pieces.Count;
        return [.. Enumerable.Range(first[g], end - first[g]).SelectMany(i => pieces[i].Split('|', StringSplitOptions.RemoveEmptyEntries))];
    }

    /// <summary>A text cut into pieces under <see cref="Limit"/>, each ending after a <paramref name="separator"/> where
    /// one is near (a piece is never empty; joined again they are the text).</summary>
    public static List<string> Chunks(string text, char separator, int limit = Limit)
    {
        var pieces = new List<string>();
        int at = 0;
        while (text.Length - at > limit)
        {
            int cut = text.LastIndexOf(separator, at + limit - 1, limit);
            int end = cut > at ? cut + 1 : at + limit;
            pieces.Add(text[at..end]);
            at = end;
        }
        if (at < text.Length || pieces.Count == 0) pieces.Add(text[at..]);
        return pieces;
    }
}
