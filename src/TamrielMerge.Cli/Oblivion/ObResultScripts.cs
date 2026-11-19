using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's result scripts: a dialogue response's (run as the response ends, on its speaker) and a quest stage log
/// entry's (run as the stage is set, when that entry is the one whose conditions hold), translated whole by
/// <see cref="ObPapyrus"/> as the other scripts are (2026-10-22: the responses kept a regex's handful of lines,
/// SetStage, StartQuest and the like, every other line dropped; the stages' scripts were dropped altogether).
/// <list type="bullet">
/// <item>A response's: OB_R_... extends TMObLine, its Result the script: run when the line is done (TMObLine's end
/// fragment), or as it begins when a script's Say says it, as Oblivion does (TMObState.LineBegins).</item>
/// <item>A stage entry's: OB_S_... extends Quest, its OB_Run the script, attached to the quest; the quest's fragment
/// script OB_QF_... (Skyrim runs a stage's fragments from one script) calls it from Fragment_n, the fragment of that
/// stage and log entry. Skyrim, as Oblivion, takes the first log entry of a stage whose conditions hold.</item>
/// </list>
/// Without a compiler the responses keep the regex's subset (<see cref="Result"/>).
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>Each dialogue response with a result script (the Oblivion INFO): its translation.</summary>
    readonly Dictionary<FormKey, ObPapyrus.Output> resultTranslations = new();
    /// <summary>Each quest stage log entry with a result script (quest, stage, entry): its translation.</summary>
    readonly Dictionary<(FormKey Quest, int Stage, int Entry), ObPapyrus.Output> stageTranslations = new();
    /// <summary>The converted response of each Oblivion response.</summary>
    readonly Dictionary<FormKey, DialogResponses> responsesBySource = new();
    public int ResultScripts => resultTranslations.Count;
    public int StageScripts => stageTranslations.Count;

    /// <summary>The winning version of every dialogue response (as BuildDialogue merges them).</summary>
    IEnumerable<Ob.IDialogItemGetter> WinningResponses() =>
        plugins.SelectMany(p => p.DialogTopics.SelectMany(t => t.Items)).GroupBy(i => i.FormKey).Select(g => g.Last());

    IEnumerable<Ob.IQuestGetter> WinningQuests() => plugins.SelectMany(p => p.Quests).GroupBy(q => q.FormKey).Select(g => g.Last());

    /// <summary>Translates the result scripts (with the other scripts, before the world: the references they name are
    /// made persistent).</summary>
    void PrepareResultScripts(ObFunctions functions)
    {
        foreach (var item in WinningResponses())
        {
            if (item.Script?.SourceCode is not { } src || src.Trim().Length == 0) continue;
            string? quest = item.Quest.IsNull ? null : records.GetValueOrDefault(item.Quest.FormKey)?.EditorID;
            string id = $"R_{item.FormKey.ID:X6}_{ObPapyrus.Clean(item.FormKey.ModKey.Name)}";
            TranslateResult(src, id, "Result", quest, o => resultTranslations[item.FormKey] = o);
        }
        foreach (var q in WinningQuests())
            foreach (var stage in q.Stages)
                for (int e = 0; e < stage.LogEntries.Count; e++)
                {
                    if (stage.LogEntries[e].ResultScript?.SourceCode is not { } src || src.Trim().Length == 0) continue;
                    string id = $"S_{q.EditorID ?? q.FormKey.ID.ToString("X6")}_{stage.Stage}_{e}";
                    int stageIndex = stage.Stage, entry = e;
                    TranslateResult(src, id, "Stage", q.EditorID, o => stageTranslations[(q.FormKey, stageIndex, entry)] = o);
                }

        void TranslateResult(string source, string id, string kind, string? quest, Action<ObPapyrus.Output> keep)
        {
            try
            {
                var output = new ObPapyrus(scriptWorld!, functions).Translate(ObPapyrus.ParseFragment(source), id, kind, fragmentQuest: quest);
                keep(output);
                CountScript(output);
                foreach (var p in output.Properties)
                {
                    if (p.PapyrusTypeIsReference() && !p.Generated && p.QuestVariable is null && scriptWorld!.KeyOf(p.EditorID) is { } refKey)
                        scriptRefs.Add(refKey);
                    if (p.Type == "Package" && scriptWorld!.KeyOf(p.EditorID) is { } pk && records.GetValueOrDefault(pk) is Ob.IAIPackageGetter package)
                        foreach (var r in PackageRefs(package)) scriptRefs.Add(r);
                }
                foreach (var (pkg, uses) in output.ScriptPackages)
                    if (scriptWorld!.KeyOf(pkg) is { } pk2) scriptPackageUses[pk2] = scriptPackageUses.GetValueOrDefault(pk2) + uses;
            }
            catch (Exception e) { Problems.Add($"result script {id}: {e.Message}"); }
        }
    }

    /// <summary>The quest's fragment script's name (one per quest with stage result scripts).</summary>
    static string StageFragmentScript(string questEditorId) => ObPapyrus.PapyrusName("QF_" + questEditorId);

    /// <summary>Every result script's source, and each quest's fragment script calling its stages' scripts.</summary>
    IEnumerable<(string Name, string Source)> ResultScriptSources()
    {
        foreach (var o in resultTranslations.Values) yield return (o.ScriptName, o.Source);
        foreach (var o in stageTranslations.Values) yield return (o.ScriptName, o.Source);
        foreach (var g in stageTranslations.GroupBy(kv => kv.Key.Quest))
        {
            string questId = records.GetValueOrDefault(g.Key)?.EditorID ?? g.Key.ID.ToString("X6");
            string name = StageFragmentScript(questId);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ScriptName {name} extends Quest Hidden");
            sb.AppendLine($"{{The stage fragments of Oblivion's {questId} quest: each runs its log entry's result script (TamrielMerge).}}");
            sb.AppendLine();
            foreach (var (key, o) in g.OrderBy(kv => kv.Key.Stage).ThenBy(kv => kv.Key.Entry))
            {
                sb.AppendLine($"Function {StageFragmentName(key.Stage, key.Entry)}()");
                sb.AppendLine($"  ((Self as Quest) as {o.ScriptName}).OB_Run()");
                sb.AppendLine("EndFunction");
                sb.AppendLine();
            }
            yield return (name, sb.ToString());
        }
    }

    static string StageFragmentName(int stage, int entry) => $"Fragment_{stage}_{entry}";

    /// <summary>
    /// Attaches the compiled result scripts: each response's as its fragment (in place of the regex's subset), each
    /// stage entry's on its quest with the quest's fragment script calling it. <paramref name="attach"/> adds a script
    /// with its properties filled to a record (as for the other scripts).
    /// </summary>
    void AttachResultScripts(HashSet<string> compiled, Func<ObPapyrus.Output, ScriptEntry?> entryFor)
    {
        // The topic of each response, for why one wasn't attached (ResultsUnattached).
        var topicOf = plugins.SelectMany(p => p.DialogTopics.SelectMany(t => t.Items.Select(i => (i.FormKey, t))))
            .GroupBy(x => x.FormKey).ToDictionary(g => g.Key, g => g.Last().t);
        foreach (var (source, o) in resultTranslations)
        {
            if (!compiled.Contains(o.ScriptName) || !responsesBySource.TryGetValue(source, out var r))
            {
                var t = topicOf.GetValueOrDefault(source);
                string why = !compiled.Contains(o.ScriptName) ? "not compiled"
                    : responsesNeverHolding.Contains(source) ? $"response left out: a condition never holds ({t?.DialogType})"
                    : $"response not made ({t?.DialogType}{(t?.DialogType is Ob.DialogType.Topic or Ob.DialogType.Conversation or Ob.DialogType.Persuasion ? "" : " " + t?.EditorID)})";
                ResultsUnattached[why] = ResultsUnattached.GetValueOrDefault(why) + 1;
                continue;
            }
            if (entryFor(o) is not { } entry) continue;
            // The script extends TMObLine: what the line was given stays with it (the topics the response gives the
            // player, its part in a conversation: without ConvTopic a conversation never heard of the line being said
            // and stopped at it).
            if (r.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == LineScript) is { } was)
                foreach (var p in was.Properties)
                    if (!entry.Properties.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) entry.Properties.Add(p);
            r.VirtualMachineAdapter = LineAdapter(entry, o.ScriptName);
            ResultScriptsAttached++;
        }
        foreach (var g in stageTranslations.GroupBy(kv => kv.Key.Quest))
        {
            if (!obQuests.TryGetValue(g.Key, out var quest)) continue;
            string questId = records.GetValueOrDefault(g.Key)?.EditorID ?? g.Key.ID.ToString("X6");
            string fragments = StageFragmentScript(questId);
            if (!compiled.Contains(fragments)) continue;
            quest.VirtualMachineAdapter ??= new QuestAdapter();
            var vm = quest.VirtualMachineAdapter;
            foreach (var (key, o) in g.OrderBy(kv => kv.Key.Stage).ThenBy(kv => kv.Key.Entry))
            {
                if (!compiled.Contains(o.ScriptName) || entryFor(o) is not { } entry) continue;
                vm.Scripts.Add(entry);
                vm.Fragments.Add(new QuestScriptFragment
                {
                    Stage = (ushort)key.Stage,
                    StageIndex = key.Entry,
                    ScriptName = fragments,
                    FragmentName = StageFragmentName(key.Stage, key.Entry),
                });
                StageScriptsAttached++;
            }
            vm.FileName = fragments;
            if (!vm.Scripts.Any(s => s.Name == fragments)) vm.Scripts.Add(new ScriptEntry { Name = fragments });
        }
    }

    public int ResultScriptsAttached { get; private set; }
    /// <summary>The dialogue result scripts not attached, by why (not compiled; its response not made, by topic type and,
    /// for the engine's topics, name).</summary>
    public Dictionary<string, int> ResultsUnattached { get; } = new();
    public int StageScriptsAttached { get; private set; }
}
