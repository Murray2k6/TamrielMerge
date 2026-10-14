using System.Text;
using System.Text.RegularExpressions;

namespace TamrielMerge.Daggerfall;

/// <summary>Dev views of Daggerfall's quests: df-quest-dump (one quest as readable commands) and df-quest-check (how much
/// of every quest the converter supports).</summary>
public static class DfQuestDump
{
    /// <summary>Symbol names from Daggerfall Unity's text quests (a folder of *.txt; dev only, to compare), by hash.</summary>
    public static Dictionary<uint, string> NamesFrom(string? folder, string quest)
    {
        var d = new Dictionary<uint, string>();
        if (folder is null) return d;
        string f = Path.Combine(folder, quest + ".txt");
        if (!File.Exists(f)) return d;
        foreach (Match m in Regex.Matches(File.ReadAllText(f), @"_[A-Za-z0-9.]+_"))
        {
            string n = m.Value;
            d.TryAdd(DfQuest.Hash(n), n);
        }
        return d;
    }

    /// <summary>A quest as readable commands, close to Daggerfall Unity's text quests: its resources, then each state's
    /// task (the trigger lines that set it, then the actions that run when it is set), the start-up first.</summary>
    public static string Readable(DfQuest q, Dictionary<uint, string> names)
    {
        string N(uint h) => q.SymbolNames.TryGetValue(h, out var n) ? "_" + n + "_" : names.TryGetValue(h, out var m) ? m : $"_h{h:X}_";
        string Sym<T>(IEnumerable<T> list, Func<T, int> index, Func<T, uint> hash, int i, string kind) =>
            list.FirstOrDefault(x => index(x) == i) is { } x ? N(hash(x)) : $"_{kind}{i}_";
        // States without a name of their own (no hash, or one shared with other states, as global links have) by index.
        var shared = q.States.GroupBy(s => s.Hash).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        string State(int i) => q.States.FirstOrDefault(s => s.Index == i) is { } s && s.Hash != 0 && !shared.Contains(s.Hash) ? N(s.Hash) : $"_S.{i:00}_";
        string Arg(QArgView a) => a.Role switch
        {
            's' => (a.Arg.Not ? "not " : "") + State((int)a.Arg.Value),
            'i' => Sym(q.Items, x => x.Index, x => x.Hash, (int)a.Arg.Value, "item"),
            'n' => Sym(q.People, x => x.Index, x => x.Hash, (int)a.Arg.Value, "npc"),
            'p' => Sym(q.Places, x => x.Index, x => x.Hash, (int)a.Arg.Value, "place"),
            'c' => Sym(q.Clocks, x => x.Index, x => x.Hash, (int)a.Arg.Value, "clock"),
            'f' => Sym(q.Foes, x => x.Index, x => x.Hash, (int)a.Arg.Value, "foe"),
            _ => a.Arg.Int.ToString(),
        };
        var sb = new StringBuilder();
        sb.AppendLine($"-- {q.Name} ({DfQuest.GroupOf(q.Name)}): {q.Ops.Count} operations, {q.States.Count} states, {q.Messages.Count} messages");
        foreach (var i in q.Items)
            sb.AppendLine($"Item {N(i.Hash)} " + (i.Reward is 2 or 3 ? (i.Category == 0xFFFF ? "gold" : $"gold range {i.CategoryIndex} to {i.Category}")
                : $"{(i.Reward == 1 ? "random " : "")}group {i.Category} index {(i.CategoryIndex == 0xFFFF ? "any" : i.CategoryIndex)}") + Texts(i.Text1, i.Text2));
        foreach (var p in q.People)
            sb.AppendLine($"Person {N(p.Hash)} {PersonKind(p)}{(p.Face is > 0 and < 255 ? $" face {p.Face}" : "")}{(p.Gender switch { 0 => " local", 1 => " remote", _ => "" })}" + Texts(p.Text1, p.Text2));
        foreach (var p in q.Places)
            sb.AppendLine($"Place {N(p.Hash)} " + (p.Scope == 0 ? $"permanent site 0x{p.P1:X4} 0x{(ushort)p.P2:X4}" : $"{(p.Scope == 1 ? "local" : "remote")} {(p.P1 == 1 ? "dungeon" : "building")} type {p.P2} select {p.P3}") + Texts(p.Text1, p.Text2));
        foreach (var c in q.Clocks)
            sb.AppendLine($"Clock {N(c.Hash)} kind {c.Kind} flags {c.Flags} {c.Min}" + (c.Max > 0 ? $"-{c.Max}" : "") + " minutes" + (c.Kind >= 2 ? $" travel to {c.Link1 & 0xFF}/{c.Link2 & 0xFF}" : ""));
        foreach (var f in q.Foes)
            sb.AppendLine($"Foe {N(f.Hash)} is {(f.Count > 1 ? f.Count + " " : "")}{(DfCreatures.Mobiles.TryGetValue(f.Type, out var mob) ? mob.Name : "mobile " + f.Type)}");
        foreach (var s in q.States.Where(s => s.IsGlobal))
            sb.AppendLine($"Variable {State(s.Index)} global {s.GlobalIndex}");
        sb.AppendLine();
        var ops = q.Ops.Select(o => (Op: o, Info: DfQuestOps.Of(o.Code))).ToList();
        string Line((DfQuest.QOp Op, DfQuestOps.OpInfo Info) x)
        {
            var args = View(x.Op, x.Info);
            string t = x.Info.Dfu;
            // {k+}: operands k.. joined; {k+ and}/{k+ or}: joined by the word.
            t = Regex.Replace(t, @"\{(\d)\+( (and|or))?\}", m =>
            {
                int k = int.Parse(m.Groups[1].Value);
                return string.Join(m.Groups[3].Success ? $" {m.Groups[3].Value} " : " ", args.Where(a => a.Index >= k && !a.Arg.IsNone).Select(Arg));
            });
            t = Regex.Replace(t, @"\{(\d)\}", m =>
            {
                int k = int.Parse(m.Groups[1].Value);
                var a = args.FirstOrDefault(a => a.Index == k);
                return a is null ? "-" : a.Arg.IsNone && a.Role != '#' ? (a.Role == 'n' ? "pc" : "-") : Arg(a);
            });
            if (x.Op.Message >= 0) t += $" saying {x.Op.Message}";
            return "\t" + t + (x.Info.Supported ? "" : "    -- not supported: " + x.Info.Gap);
        }
        sb.AppendLine("--\tQuest start-up:");
        foreach (var x in ops.Where(x => x.Info.Kind == DfQuestOps.OpKind.Action && x.Op.Args[0].IsNone)) sb.AppendLine(Line(x));
        foreach (var s in q.States)
        {
            var mine = ops.Where(x => !x.Op.Args[0].IsNone && x.Op.Args[0].Value == s.Index).ToList();
            bool clock = s.Hash != 0 && q.Clocks.Any(c => c.Hash == s.Hash);
            string tag = (clock ? " -- clock" : "") + (s.IsGlobal ? $" -- global {s.GlobalIndex}" : "");
            if (mine.Count == 0) { sb.AppendLine($"\nvariable {State(s.Index)}{tag}"); continue; }
            sb.AppendLine($"\n{State(s.Index)} task:{tag}");
            foreach (var x in mine.Where(x => x.Info.Kind != DfQuestOps.OpKind.Action)) sb.AppendLine(Line(x));
            foreach (var x in mine.Where(x => x.Info.Kind == DfQuestOps.OpKind.Action)) sb.AppendLine(Line(x));
        }
        return sb.ToString();
    }

    static string Texts(int a, int b) => (a > 0 ? $" anyInfo {a}" : "") + (b > 0 ? $" used/rumors {b}" : "");

    public static string PersonKind(DfQuest.QPerson p) => (short)p.Kind switch
    {
        21 => "group Questor",
        >= 0 and <= 20 => $"group {(DfBuildingType)p.Kind}",
        -8 => $"named faction {p.Faction} atHome",
        -1 => $"faction {p.Faction}",
        -2 => $"factiontype {(DfFactionType)p.Faction}",
        var k => $"group {-k}.{p.Faction}",
    };

    public sealed record QArgView(int Index, char Role, DfQuest.QArg Arg);

    /// <summary>An operation's valid operands with their roles.</summary>
    public static List<QArgView> View(DfQuest.QOp op, DfQuestOps.OpInfo info)
    {
        var list = new List<QArgView>();
        for (int a = 0; a < Math.Min(op.ArgCount, 5); a++)
        {
            char role = a < info.Args.Length ? info.Args[a] : '-';
            if (role != '-') list.Add(new QArgView(a, role, op.Args[a]));
        }
        return list;
    }

    /// <summary>Coverage: each quest's operations, how many the converter supports, and what is missing.</summary>
    public static string Check(IReadOnlyList<DfQuest> quests)
    {
        var sb = new StringBuilder();
        int ops = 0, supported = 0, full = 0;
        var missing = new SortedDictionary<int, (int Uses, SortedSet<string> Quests)>();
        foreach (var q in quests)
        {
            int n = q.Ops.Count, ok = q.Ops.Count(o => DfQuestOps.Of(o.Code).Supported);
            ops += n; supported += ok;
            if (ok == n) full++;
            foreach (var o in q.Ops.Where(o => !DfQuestOps.Of(o.Code).Supported))
            {
                var m = missing.TryGetValue(o.Code, out var hit) ? hit : (0, new SortedSet<string>());
                m.Item2.Add(q.Name);
                missing[o.Code] = (m.Item1 + 1, m.Item2);
            }
            sb.AppendLine($"{q.Name,-9} {DfQuest.GroupOf(q.Name),-15} {ok,3}/{n,-3} {(ok == n ? "full" : string.Join(", ", q.Ops.Where(o => !DfQuestOps.Of(o.Code).Supported).Select(o => DfQuestOps.Of(o.Code).Name).Distinct()))}");
        }
        sb.AppendLine();
        sb.AppendLine($"{quests.Count} quests, {full} with every operation supported; {supported}/{ops} operations ({100.0 * supported / Math.Max(ops, 1):F2}%); {quests.SelectMany(q => q.Ops.Select(o => o.Code)).Distinct().Count()} distinct opcodes");
        foreach (var (code, m) in missing)
            sb.AppendLine($"  not supported: 0x{code:X2} {DfQuestOps.Of(code).Name}: {m.Uses} uses in {string.Join(", ", m.Quests)} -- {DfQuestOps.Of(code).Gap}");
        return sb.ToString();
    }

    public static string Raw(DfQuest q, Dictionary<uint, string> names)
    {
        string N(uint h) => q.SymbolNames.TryGetValue(h, out var n) ? "_" + n + "_" : names.TryGetValue(h, out var m) ? m : $"#{h:X}";
        var sb = new StringBuilder();
        sb.AppendLine($"{q.Name}: {q.Items.Count} items, {q.People.Count} people, {q.Places.Count} places, {q.Clocks.Count} clocks, {q.Foes.Count} foes, {q.Ops.Count} ops, {q.States.Count} states, {q.Messages.Count} messages");
        foreach (var i in q.Items) sb.AppendLine($"Item {i.Index} {N(i.Hash)} reward {i.Reward} cat {i.Category} idx {i.CategoryIndex} text {i.Text1}/{i.Text2}");
        foreach (var p in q.People) sb.AppendLine($"Person {p.Index} {N(p.Hash)} gender {p.Gender} face {p.Face} kind {p.Kind} ({(short)p.Kind}) faction {p.Faction} text {p.Text1}/{p.Text2}");
        foreach (var p in q.Places) sb.AppendLine($"Place {p.Index} {N(p.Hash)} flags {p.Flags} scope {p.Scope} p1 {p.P1:X} p2 {p.P2} ({(ushort)p.P2:X}) p3 {p.P3} unk {p.Unknown} text {p.Text1}/{p.Text2}");
        foreach (var c in q.Clocks) sb.AppendLine($"Clock {c.Index} {N(c.Hash)} flags {c.Flags} kind {c.Kind} min {c.Min} max {c.Max} links {c.Link1}/{c.Link2}");
        foreach (var f in q.Foes) sb.AppendLine($"Foe {f.Index} {N(f.Hash)} type {f.Type} count {f.Count}");
        foreach (var s in q.States) sb.AppendLine($"State {s.Index} {N(s.Hash)}{(s.IsGlobal ? $" global {s.GlobalIndex}" : "")}");
        foreach (var o in q.Ops)
        {
            sb.Append($"Op {o.Position,3} 0x{o.Code:X2} f{o.Flags:X} n{o.ArgCount}");
            for (int a = 0; a < Math.Min(o.ArgCount, 5); a++)
            {
                var g = o.Args[a];
                string v = g.Section switch
                {
                    -1 => $"#{g.Int}",
                    9 when !g.IsNone => StateName(q, (int)g.Value, N),
                    _ when g.IsNone => $"s{g.Section}:none",
                    0 => "item:" + (q.Items.FirstOrDefault(i => i.Index == g.Value) is { } it ? N(it.Hash) : g.Value.ToString()),
                    3 => "npc:" + (q.People.FirstOrDefault(i => i.Index == g.Value) is { } pp ? N(pp.Hash) : g.Value.ToString()),
                    4 => "place:" + (q.Places.FirstOrDefault(i => i.Index == g.Value) is { } pl ? N(pl.Hash) : g.Value.ToString()),
                    6 => "clock:" + (q.Clocks.FirstOrDefault(i => i.Index == g.Value) is { } cl ? N(cl.Hash) : g.Value.ToString()),
                    7 => "foe:" + (q.Foes.FirstOrDefault(i => i.Index == g.Value) is { } fo ? N(fo.Hash) : g.Value.ToString()),
                    _ => $"s{g.Section}:{g.Value}",
                };
                sb.Append(' ').Append(g.Not ? "!" : "").Append(v);
            }
            if (o.Message >= 0) sb.Append($" msg {o.Message}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string StateName(DfQuest q, int index, Func<uint, string> n)
    {
        var s = q.States.FirstOrDefault(x => x.Index == index);
        if (s is null) return $"S{index}?";
        return s.Hash == 0 ? $"_S.{index:00}_" : n(s.Hash);
    }
}
