using System.Text.RegularExpressions;

namespace TamrielMerge.Morrowind;

/// <summary>
/// All of the loaded game's scripts translated (<see cref="MwPapyrus"/>): each object script (MW_), each script that
/// runs as a global script (MWG_; a script both carried and started gets both), and each dialogue response's result
/// script (MWR_, one per distinct translation: responses with the same result share it). Used by the plugin builder and
/// by the mw-script-translate command.
/// </summary>
public sealed class MwScriptSet
{
    public sealed record Translated(string ScriptId, MwPapyrus.Output Output);

    public List<Translated> Objects { get; } = [];
    public List<Translated> Globals { get; } = [];
    /// <summary>Result scripts by Papyrus name.</summary>
    public Dictionary<string, MwPapyrus.Output> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each response (INFO ID) with a result that does something: its result script's name.</summary>
    public Dictionary<string, string> InfoScripts { get; } = [];
    /// <summary>Responses whose result says Goodbye.</summary>
    public HashSet<string> Goodbyes { get; } = [];
    public int ResultInfos { get; private set; }
    public int Statements { get; private set; }
    public int TranslatedStatements { get; private set; }
    public Dictionary<string, int> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Failed { get; } = [];
    /// <summary>Per group (a plugin's scripts, or "dialogue results"): scripts, statements, translated.</summary>
    public Dictionary<string, (int Scripts, int Statements, int Translated)> PerGroup { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<MwPapyrus.Output> All => Objects.Select(o => o.Output).Concat(Globals.Select(g => g.Output)).Concat(Results.Values);

    public static MwScriptSet Translate(MwScriptWorld world, Regex? only = null)
    {
        var set = new MwScriptSet();
        foreach (var (id, script) in world.Scripts)
        {
            if (only is not null && !only.IsMatch(id)) continue;
            string group = world.ScriptPlugin.GetValueOrDefault(id) ?? "?";
            // Carried by records (or by nothing at all, and not started either: still translated, for completeness).
            bool global = world.IsGlobalScript(id);
            bool carried = world.Carriers.ContainsKey(id);
            if (carried || !global)
                set.Add(world, group, id, () => new MwPapyrus(world).Translate(script, id, MwPapyrus.Kind.Object,
                    new MwPapyrus.Context(OnActor: world.OnActor(id), IsItem: world.OnItem(id), Speakers: world.Speakers(id))), set.Objects);
            if (global)
                set.Add(world, group, id, () => new MwPapyrus(world).Translate(script, id, MwPapyrus.Kind.Global,
                    new MwPapyrus.Context(Start: world.StartScripts.Contains(id))), set.Globals);
        }
        if (world.Dialogue is { } dialogue && (only is null || only.IsMatch("results")))
        {
            var translator = new MwPapyrus(world);
            foreach (var topic in dialogue.Topics.Values)
                foreach (var info in topic.Infos)
                {
                    if (info.Result is not { Length: > 0 } text || text.Trim().Length == 0) continue;
                    set.ResultInfos++;
                    MwPapyrus.Output output;
                    try { output = translator.Translate(MwScript.ParseResult(text), "result", MwPapyrus.Kind.Result, world.ResultContext(info)); }
                    catch (Exception e) { set.Failed.Add($"result {info.Id}: {e.Message}"); continue; }
                    if (output.Goodbye) set.Goodbyes.Add(info.Id);
                    if (IsEmpty(output.Source)) { set.Count("dialogue results", output, newScript: false); continue; }
                    // Responses with the same translation share one script, named by its text.
                    string body = output.Source[(output.Source.IndexOf('\n') + 1)..];
                    string name = $"MWR_{MwPapyrus.Hash(body):X8}";
                    set.InfoScripts[info.Id] = name;
                    if (set.Results.ContainsKey(name)) continue;
                    var named = output with { ScriptName = name, Source = $"ScriptName {name} extends TopicInfo Hidden\n" + body };
                    set.Results[name] = named;
                    set.Count("dialogue results", named, newScript: true);
                }
        }
        return set;
    }

    void Add(MwScriptWorld world, string group, string id, Func<MwPapyrus.Output> translate, List<Translated> into)
    {
        try
        {
            var output = translate();
            into.Add(new Translated(id, output));
            Count(group, output, newScript: true);
        }
        catch (Exception e) { Failed.Add($"{id}: {e.Message}"); }
    }

    void Count(string group, MwPapyrus.Output o, bool newScript)
    {
        Statements += o.Statements;
        TranslatedStatements += o.Translated;
        foreach (var (m, n) in o.Missing) Missing[m] = Missing.GetValueOrDefault(m) + n;
        var t = PerGroup.GetValueOrDefault(group);
        PerGroup[group] = (t.Scripts + (newScript ? 1 : 0), t.Statements + o.Statements, t.Translated + o.Translated);
    }

    /// <summary>A result script that does nothing Skyrim needs a script for (Goodbye, Choice, AddTopic…): only comments.</summary>
    static bool IsEmpty(string source)
    {
        int start = source.IndexOf("Function Fragment_End", StringComparison.Ordinal);
        int end = source.IndexOf("EndFunction", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) return false;
        return source[(source.IndexOf('\n', start) + 1)..end].Split('\n').Select(l => l.Trim()).All(l => l.Length == 0 || l.StartsWith(';'));
    }

    /// <summary>The coverage report: per group, the share of statements translated, and what is missing most.</summary>
    public List<string> Report(int top = int.MaxValue)
    {
        var report = new List<string>
        {
            $"{Objects.Count} object scripts, {Globals.Count} global scripts, {Results.Count} result scripts (for {InfoScripts.Count} of {ResultInfos} responses with a result)",
            $"{TranslatedStatements}/{Statements} statements translated ({100.0 * TranslatedStatements / Math.Max(1, Statements):F2}%)",
        };
        foreach (var (group, (s, st, tr)) in PerGroup.OrderBy(kv => kv.Key))
            report.Add($"  {group}: {s} scripts, {tr}/{st} statements translated ({100.0 * tr / Math.Max(1, st):F2}%)");
        report.Add($"{Failed.Count} scripts failed to translate");
        report.AddRange(Failed.Select(f => "  " + f));
        report.Add("Missing most:");
        report.AddRange(Missing.OrderByDescending(kv => kv.Value).Take(top).Select(kv => $"  {kv.Value,6} {kv.Key}"));
        report.Add("Scripts with the most untranslated statements:");
        report.AddRange(All.Where(o => o.Translated < o.Statements).OrderByDescending(o => o.Statements - o.Translated).Take(Math.Min(top, 60))
            .Select(o => $"  {o.Statements - o.Translated,5} of {o.Statements,5}  {o.ScriptName}"));
        return report;
    }
}
