using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// The people Oblivion's quests need, kept alive (user, 2026-11-12: "some npcs need there essintial flag if they dont
/// need to die in the game when they are needed in the games"). Oblivion flags its own essential people (278 in the
/// conversion of 2026-11-08) and lets every other quest giver die; in Skyrim's world a dragon, a vampire raid or a
/// stray fight kills one the player has not met yet. The rule is Morrowind's (MwNeeded): a person is needed when a
/// response only they give (a GetIsID condition on them) moves a quest (its result script sets a stage, starts, stops
/// or completes a quest). Such a person is:
/// <list type="bullet">
/// <item>essential, when nothing in the game asks whether they are dead: no GetDeadCount or GetDead on them in a
/// condition or a script, no OnDeath block in their own script;</item>
/// <item>protected (only the player can kill them), when the game does ask: a quest may want them dead later, and
/// their death stays the player's to deal.</item>
/// </list>
/// Oblivion's own essential flags stand as they are.
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>People made essential and protected by <see cref="MarkNeeded"/>.</summary>
    public int NeededEssential { get; private set; }
    public int NeededProtected { get; private set; }

    static readonly Regex MovesQuest = new(@"(?im)^\s*(?:SetStage|StartQuest|StopQuest|CompleteQuest|SetQuestObject)\b", RegexOptions.Compiled);
    static readonly Regex DeadCountOf = new(@"(?i)\bGetDeadCount\s*,?\s*""?([A-Za-z0-9_]+)", RegexOptions.Compiled);
    static readonly Regex DeadOf = new(@"(?i)\b([A-Za-z0-9_]+)\.GetDead\b", RegexOptions.Compiled);
    static readonly Regex OnDeathBlock = new(@"(?im)^\s*Begin\s+OnDeath\b", RegexOptions.Compiled);

    void MarkNeeded()
    {
        // Each response's winning version, with the plugin it is read from (its form IDs are that plugin's).
        var winning = new Dictionary<FormKey, (Ob.IDialogItemGetter Item, Ob.IOblivionModGetter Source)>();
        foreach (var p in plugins)
            foreach (var t in p.DialogTopics)
                foreach (var i in t.Items) winning[i.FormKey] = (i, p);

        // A name in a script: an NPC's own, or a placed one's (its base).
        var byName = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, rec) in records)
        {
            if (rec.EditorID is not { Length: > 0 } id) continue;
            if (rec is Ob.INpcGetter) byName[id] = key;
            else if (rec is Ob.IPlacedNpcGetter placed) byName[id] = placed.Base.FormKey;
        }

        var needed = new HashSet<FormKey>();
        var askedDead = new HashSet<FormKey>();
        void Asked(string source)
        {
            foreach (Match m in DeadCountOf.Matches(source)) if (byName.TryGetValue(m.Groups[1].Value, out var k)) askedDead.Add(k);
            foreach (Match m in DeadOf.Matches(source)) if (byName.TryGetValue(m.Groups[1].Value, out var k)) askedDead.Add(k);
        }
        void AskedBy(IEnumerable<Ob.IConditionGetter> conditions, Ob.IOblivionModGetter source)
        {
            foreach (var c in conditions)
                if (c.Function.ToString() == "GetDeadCount") askedDead.Add(Resolve(source, (uint)c.FirstParameter));
        }

        foreach (var (item, source) in winning.Values)
        {
            AskedBy(item.Conditions, source);
            string? result = item.Script?.SourceCode;
            if (result is { Length: > 0 }) Asked(result);
            if (result is null || !MovesQuest.IsMatch(result)) continue;
            foreach (var c in item.Conditions)
                if (c.Function.ToString() == "GetIsID" && c.ComparisonValue == 1 && c.CompareOperator.ToString() == "EqualTo")
                    needed.Add(Resolve(source, (uint)c.FirstParameter));
        }
        foreach (var p in plugins)
        {
            foreach (var sc in p.Scripts) if (sc.Fields?.SourceCode is { } src) Asked(src);
            foreach (var q in p.Quests)
            {
                AskedBy(q.Conditions, p);
                foreach (var stage in q.Stages)
                    foreach (var entry in stage.LogEntries)
                    {
                        AskedBy(entry.Conditions, p);
                        if (entry.ResultScript?.SourceCode is { } src) Asked(src);
                    }
            }
        }
        // Their own script watches their death.
        foreach (var (key, rec) in records)
            if (rec is Ob.INpcGetter n && !n.Script.IsNull && records.GetValueOrDefault(n.Script.FormKey) is Ob.IScriptGetter own
                && OnDeathBlock.IsMatch(own.Fields?.SourceCode ?? "")) askedDead.Add(key);

        int already = 0, made = 0;
        foreach (var key in needed)
        {
            if (Actor(key) is not Npc npc) continue;
            made++;
            if (npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential)) { already++; continue; }
            if (askedDead.Contains(key)) { npc.Configuration.Flags |= NpcConfiguration.Flag.Protected; NeededProtected++; }
            else { npc.Configuration.Flags |= NpcConfiguration.Flag.Essential; NeededEssential++; }
        }
        Console.WriteLine($"  people the quests need: {made} whose own lines move a quest; {NeededEssential} made essential, {NeededProtected} protected (their death is asked about somewhere: only the player can kill them), {already} essential in Oblivion already; {askedDead.Count} people whose death the game asks about");
    }
}
