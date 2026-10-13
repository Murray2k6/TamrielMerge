namespace TamrielMerge.Daggerfall;

/// <summary>Faction types in FACTION.TXT.</summary>
public enum DfFactionType { Daedra = 0, God = 1, Group = 2, Subgroup = 3, Individual = 4, Official = 5, VampireClan = 6, Province = 7, WitchesCoven = 8, Temple = 9, KnightlyGuard = 10, MagicUser = 11, Generic = 12, Thieves = 13, Courts = 14, People = 15 }

/// <summary>Social groups (a faction's sgroup): who an NPC counts as in conversation.</summary>
public enum DfSocialGroup { None = -1, Commoners = 0, Merchants = 1, Scholars = 2, Nobility = 3, Underworld = 4, SGroup5 = 5, SupernaturalBeings = 6, GuildMembers = 7 }

/// <summary>
/// One faction from FACTION.TXT. Race uses the file's own numbering (Daggerfall Unity FactionFile.FactionRaces):
/// 0 Nord, 1 Khajiit, 2 Redguard, 3 Breton, 4 Argonian, 5 Wood Elf, 6 High Elf, 7 Dark Elf.
/// </summary>
public sealed record DfFaction(int Id, int Parent, DfFactionType Type, string Name, int Rep, int Region, int Race, DfSocialGroup SGroup, int GGroup,
    int Face, int Flat, IReadOnlyList<int> Allies, IReadOnlyList<int> Enemies);

/// <summary>
/// FACTION.TXT: "#id" opens a faction, "key: value" lines fill it, and indentation (tabs) nests it under the faction
/// above it. Comments start with ';'.
/// </summary>
public sealed class DfFactions
{
    public IReadOnlyDictionary<int, DfFaction> ById { get; }

    DfFactions(Dictionary<int, DfFaction> byId) => ById = byId;

    public DfFaction? this[int id] => ById.GetValueOrDefault(id);

    public static DfFactions Load(string arena2)
    {
        var result = new Dictionary<int, DfFaction>();
        var stack = new List<(int Depth, int Id)>();
        Dictionary<string, string>? fields = null;
        var allies = new List<int>(); var enemies = new List<int>();
        int id = -1, depth = 0;
        void Flush()
        {
            if (fields is null || id < 0) return;
            int I(string k, int d = -1) => fields.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
            while (stack.Count > 0 && stack[^1].Depth >= depth) stack.RemoveAt(stack.Count - 1);
            int parent = stack.Count > 0 ? stack[^1].Id : 0;
            result[id] = new DfFaction(id, parent, (DfFactionType)I("type", 2), fields.GetValueOrDefault("name", ""), I("rep", 0), I("region"), I("race"),
                (DfSocialGroup)I("sgroup"), I("ggroup"), I("face"), I("flat"), [.. allies], [.. enemies]);
            stack.Add((depth, id));
        }
        foreach (var raw in File.ReadLines(Path.Combine(arena2, "FACTION.TXT"), System.Text.Encoding.Latin1))
        {
            int comment = raw.IndexOf(';');
            string line = comment >= 0 ? raw[..comment] : raw;
            if (line.Trim().Length == 0) continue;
            string t = line.Trim();
            if (t.StartsWith('#'))
            {
                Flush();
                depth = line.TakeWhile(c => c == '\t').Count();
                id = int.Parse(t[1..].Trim());
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                allies.Clear(); enemies.Clear();
                continue;
            }
            int colon = t.IndexOf(':');
            if (colon < 0 || fields is null) continue;
            string key = t[..colon].Trim(), value = t[(colon + 1)..].Trim();
            if (key.Equals("ally", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var a)) allies.Add(a);
            else if (key.Equals("enemy", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var e)) enemies.Add(e);
            else fields[key] = value;
        }
        Flush();
        return new DfFactions(result);
    }

    /// <summary>The "People of &lt;region&gt;" faction, or the region's court.</summary>
    public DfFaction? RegionFaction(int region, DfFactionType type) => ById.Values.FirstOrDefault(f => f.Region == region && f.Type == type);

    /// <summary>
    /// The faction an NPC acts for when talking (Daggerfall Unity TalkManager.GetStaticNPCFactionData): climb from its
    /// own faction while the faction is a mere subgroup, up to a group, province, temple, people, court or individual.
    /// </summary>
    public DfFaction? Effective(int id)
    {
        var f = this[id];
        while (f is not null && f.Type is not (DfFactionType.Group or DfFactionType.Province or DfFactionType.Temple or DfFactionType.People
                   or DfFactionType.Courts or DfFactionType.Individual) && this[f.Parent] is { } up)
            f = up;
        return f;
    }

    /// <summary>Region race (Daggerfall Unity MapsFile.regionRaces, from FALL.EXE): Breton or Redguard townsfolk.</summary>
    public static readonly bool[] RegionIsRedguard = [.. new byte[]
    {
        1, 1, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1,
        0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0,
        0, 1,
    }.Select(b => b == 1)];
}
