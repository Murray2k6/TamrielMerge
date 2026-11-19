using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// The people Morrowind's quests need, kept alive (user, 2026-11-12: "some npcs need there essintial flag if they dont
/// need to die in the game when they are needed in the games"). Morrowind flags few of its people essential (56 of 3,000
/// in the conversion of 2026-11-08) and lets the rest die, quests and all; in Skyrim a stray fight, a dragon or a guard
/// kills a quest giver the player has not met yet. A person is needed when a response only they can give writes the
/// journal (their own line moves a quest on). Such a person is:
/// <list type="bullet">
/// <item>essential, when nothing in the game asks whether they are dead: no dialogue condition on their death, no script
/// counting it (GetDeadCount) and no OnDeath in their own script;</item>
/// <item>protected (only the player can kill them), when the game does ask: a quest may send the player to kill them
/// later, so their death stays the player's to deal.</item>
/// </list>
/// Morrowind's own essential flags stand as they are.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>People made essential and protected by <see cref="MarkNeeded"/>, and those Morrowind itself flags.</summary>
    public int NeededEssential { get; private set; }
    public int NeededProtected { get; private set; }

    static readonly Regex JournalCall = new(@"(?im)^\s*Journal\b", RegexOptions.Compiled);
    static readonly Regex DeadCount = new(@"(?i)GetDeadCount\s*,?\s*(?:""([^""]+)""|([A-Za-z0-9_'\-]+))", RegexOptions.Compiled);
    static readonly Regex OnDeath = new(@"(?i)\bOnDeath\b", RegexOptions.Compiled);

    void MarkNeeded()
    {
        if (dialogueData is null) return;
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var askedDead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var topic in dialogueData.Topics.Values)
            foreach (var info in topic.Infos)
            {
                if (info.Actor is { Length: > 0 } actor && info.Result is { Length: > 0 } result && JournalCall.IsMatch(result)) needed.Add(actor);
                foreach (var c in info.Conditions) if (c.Kind == '6') askedDead.Add(c.Name);
                if (info.Result is { } r) foreach (Match m in DeadCount.Matches(r)) askedDead.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }
        foreach (var s in world.Objects.Values.Where(o => o.Type == "SCPT"))
            foreach (Match m in DeadCount.Matches(s.Str("SCTX") ?? "")) askedDead.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        foreach (var o in world.Objects.Values.Where(o => o.Type is "NPC_" or "CREA"))
            if (o.Str("SCRI") is { Length: > 0 } sc && world.Objects.TryGetValue(sc, out var script) && OnDeath.IsMatch(script.Str("SCTX") ?? "")) askedDead.Add(o.Id);
        int already = 0;
        foreach (string id in needed)
        {
            if (Actor(id) is not Npc npc) continue;
            if (npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential)) { already++; continue; }
            if (askedDead.Contains(id)) { npc.Configuration.Flags |= NpcConfiguration.Flag.Protected; NeededProtected++; }
            else { npc.Configuration.Flags |= NpcConfiguration.Flag.Essential; NeededEssential++; }
        }
        Console.WriteLine($"  people the quests need: {needed.Count} whose own lines write the journal; {NeededEssential} made essential, {NeededProtected} protected (their death is asked about somewhere: only the player can kill them), {already} essential in Morrowind already; {askedDead.Count} ids whose death the game asks about");
    }
}
