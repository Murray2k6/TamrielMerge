using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using static TamrielMerge.Redguard.RgScript;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's conversations as Skyrim dialogue. A Redguard conversation is script code (SOUP): after PlayerLineUp the
/// actor's opening exchange runs, then a topic menu (menuNew, menuAddItem(text tag, ...) each under its own
/// conditions, menuProc, selection = menuSelection(), then one branch per item that plays the lines — Cyrus's (RTXp,
/// RTXpAnim) and the speaker's (RTX, rtxAnim) — sets flags and variables, writes Cyrus's log (AddLog) and jumps back
/// to the menu, on to another menu, or out. Each branch is followed through the code (gotos, gosubs, ifs; a condition
/// on a flag or variable not known on that path splits it in two, each side keeping its condition), and every path
/// becomes a response:
/// <list type="bullet">
/// <item>menu items are topics named by their text, in one dialogue branch per menu (the actor's main menu top-level,
/// others reached as choices); each path is a response on its item's topic, its prompt Cyrus's first line;</item>
/// <item>a later line of Cyrus's in the path continues as a choice (a child topic prompting that line); the path's end
/// lists the next menu's topics as choices, or says goodbye;</item>
/// <item>conditions are on globals: one per Redguard flag (SOUP386.DEF, with its starting value) and one per actor
/// variable (RAVA); assignments set them (TMMwInfo); every response is the speaker's own (GetIsID);</item>
/// <item>the opening exchange is the speaker's greeting when the speaker talks first, else a topic of the main menu;
/// Cyrus's log is a quest whose stages are the log entries.</item>
/// </list>
/// Lines are the RTX text in sentence case; the speaker's lines get Redguard's own recordings (Redguard.voicemap.json,
/// packed by rg-voices). Cyrus's lines are the player's and stay unvoiced.
/// </summary>
public sealed partial class RgPluginBuilder
{
    RgRtx? rtx;
    List<string> functionNames = [];
    List<(string Name, int Initial, string Comment)> flagDefs = [];
    Quest? dialogueQuest, logQuest;
    readonly Dictionary<string, Global> rgGlobals = new(StringComparer.Ordinal);
    readonly Dictionary<string, ushort> logStages = new(StringComparer.Ordinal);
    public sealed record VoiceLine(uint Info, int Number, string Tag);
    public List<VoiceLine> VoiceMap { get; } = [];
    public int DialogueTopics { get; private set; }
    public int DialogueResponses { get; private set; }
    public int DialogueActors { get; private set; }
    readonly Dictionary<string, int> unhandled = new(StringComparer.Ordinal);
    HashSet<string> properNouns = new(StringComparer.OrdinalIgnoreCase);

    static readonly FormKey PlayerRef = new(ModKey.FromFileName("Skyrim.esm"), 0x14);

    // ---- text ----

    /// <summary>Words written capitalised mid-sentence in Redguard's mixed-case texts (log entries, item names), plus
    /// the people's names: the proper nouns sentence case keeps.</summary>
    void LearnProperNouns()
    {
        var counts = new Dictionary<string, (int Upper, int Lower)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in rtx!.Entries.Values.Where(e => e.Text.Any(char.IsLower)))
        {
            var words = System.Text.RegularExpressions.Regex.Matches(e.Text, @"[A-Za-z][A-Za-z']*");
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i].Value;
                int start = words[i].Index;
                string before = e.Text[..start].TrimEnd();
                bool sentenceStart = before.Length == 0 || ".!?\"".Contains(before[^1]);
                if (sentenceStart) continue;
                var c = counts.GetValueOrDefault(w);
                counts[w] = char.IsUpper(w[0]) ? (c.Upper + 1, c.Lower) : (c.Upper, c.Lower + 1);
            }
        }
        properNouns = counts.Where(kv => kv.Value.Upper > kv.Value.Lower).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in People.Values) foreach (var w in p.Name.Split(' ')) properNouns.Add(w);
        foreach (var w in new[] { "Cyrus", "Stros", "M'Kai", "Iszara", "Hammerfell", "Redguard", "Redguards", "Imperial", "Imperials", "Empire", "Tamriel", "Richton", "League", "Tobias", "Siona", "Brennan", "N'Gasta", "Draggin", "Tale", "Hunding", "Arkay", "Dwemer", "Akavir", "Yokuda", "Yokudan", "Forebear", "Forebears", "Crown", "Crowns", "Daggerfall", "Sentinel", "Clavicus", "Vile", "Cyrodiil", "Tiber", "Septim" })
            properNouns.Add(w);
    }

    /// <summary>Redguard's all-capitals line in sentence case: sentences start capitalised, "I" and proper nouns stay.</summary>
    string Pretty(string text)
    {
        if (text.Any(char.IsLower)) return text.Trim();
        var sb = new StringBuilder(text.Length);
        bool start = true;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z][A-Za-z']*|[^A-Za-z]+"))
        {
            string tok = m.Value;
            if (!char.IsLetter(tok[0]))
            {
                sb.Append(tok);
                if (tok.IndexOfAny(['.', '!', '?']) >= 0) start = true;
                continue;
            }
            string lower = tok.ToLowerInvariant();
            bool proper = properNouns.Contains(tok) || lower is "i" or "i'm" or "i'll" or "i'd" or "i've";
            string word = proper ? string.Join("'", lower.Split('\'').Select((p, i) => p.Length == 0 ? p : i > 0 && p.Length <= 2 && lower is not "m'kai" ? p : char.ToUpperInvariant(p[0]) + p[1..])) : lower;
            if (start && word.Length > 0) word = char.ToUpperInvariant(word[0]) + word[1..];
            sb.Append(word);
            start = false;
        }
        return sb.ToString().Replace(" ,", ",").Trim();
    }

    string? Text(int tag) => rtx!.Entries.TryGetValue(TagString(tag), out var e) ? Pretty(e.Text) : null;
    static string TagString(int v) => Encoding.Latin1.GetString(BitConverter.GetBytes(v));

    // ---- globals and the log ----

    Global GlobalFor(string key, string actor, Dictionary<string, int[]> locals)
    {
        if (rgGlobals.TryGetValue(key, out var g)) return g;
        string edid; float initial;
        if (key.StartsWith('F'))
        {
            int i = int.Parse(key[1..]);
            edid = "RGFlag" + Sanitize(i < flagDefs.Count ? flagDefs[i].Name : key);
            initial = i < flagDefs.Count ? flagDefs[i].Initial : 0;
        }
        else
        {
            var parts = key.Split(':');   // L:<actor>:<index>
            int i = int.Parse(parts[2]);
            edid = $"RGVar{Sanitize(parts[1])}{i}";
            initial = locals.TryGetValue(parts[1], out var l) && i < l.Length ? l[i] : 0;
        }
        g = new GlobalFloat(mod) { EditorID = edid, Data = initial };
        mod.Globals.Add(g);
        return rgGlobals[key] = g;
    }

    ushort LogStage(int tag)
    {
        string t = TagString(tag);
        if (logStages.TryGetValue(t, out var stage)) return stage;
        stage = (ushort)((logStages.Count + 1) * 10);
        logStages[t] = stage;
        logQuest!.Stages.Add(new QuestStage { Index = stage, LogEntries = [new QuestLogEntry { Entry = Text(tag) ?? t }] });
        return stage;
    }

    // ---- following the code ----

    /// <summary>A condition atom: a flag or variable compared with a number (cmp: 0 =, 1 !=, 2 &lt;, 3 &gt;, 4 &lt;=, 5 &gt;=).</summary>
    sealed record Atom(string Key, int Cmp, int Value)
    {
        public Atom Not() => this with { Cmp = Cmp switch { 0 => 1, 1 => 0, 2 => 5, 3 => 4, 4 => 3, _ => 2 } };
        public bool Test(int v) => Cmp switch { 0 => v == Value, 1 => v != Value, 2 => v < Value, 3 => v > Value, 4 => v <= Value, _ => v >= Value };
    }

    abstract record Ev;
    sealed record SayPlayer(int Tag) : Ev;
    sealed record SayNpc(int Tag) : Ev;
    sealed record Log(int Tag) : Ev;
    sealed record Set(string Key, int Value) : Ev;
    sealed record ToMenu(int Address) : Ev;
    sealed record Bye : Ev;

    /// <summary>One way through the code: its conditions (all clauses hold; each clause holds when any atom does) and
    /// what happens.</summary>
    sealed record Route(List<List<Atom>> Conditions, List<Ev> Events);

    sealed class Program
    {
        public required string Actor;
        public required List<Stmt> Root;
        public readonly Dictionary<int, List<(List<Stmt> List, int Index)>> At = [];
        public readonly HashSet<int> MenuAddresses = [];

        public void Index(List<Stmt> list, List<(List<Stmt>, int)> parents, int menuNew)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var here = new List<(List<Stmt>, int)>(parents) { (list, i) };
                At.TryAdd(list[i].Address, here);
                if (list[i] is CallStmt c && c.Function == menuNew) MenuAddresses.Add(list[i].Address);
                if (list[i] is IfStmt f) Index(f.Body, here, menuNew);
            }
        }
    }

    sealed class Cursor
    {
        public List<(List<Stmt> List, int Index)> Frames = [];
        public List<List<(List<Stmt> List, int Index)>> Calls = [];
        public Cursor Clone() => new() { Frames = Frames.ToList(), Calls = Calls.Select(c => c.ToList()).ToList() };

        public Stmt? Current
        {
            get
            {
                while (Frames.Count > 0)
                {
                    var (l, i) = Frames[^1];
                    if (i < l.Count) return l[i];
                    Frames.RemoveAt(Frames.Count - 1);
                    if (Frames.Count > 0) Advance();
                }
                return null;
            }
        }
        public void Advance() { var (l, i) = Frames[^1]; Frames[^1] = (l, i + 1); }
    }

    int Fn(string name) => functionNames.IndexOf(name);

    /// <summary>Follows the code from a cursor: every path to a menu, a goodbye or a dead end (capped).</summary>
    List<Route> Follow(Program prog, Cursor start, Dictionary<string, int> known, List<List<Atom>> conditions, int startMenu = -1)
    {
        var results = new List<Route>();
        var work = new Stack<(Cursor C, Dictionary<string, int> Known, List<List<Atom>> Conds, List<Ev> Events, int Steps)>();
        work.Push((start, known, conditions, [], 0));
        int rtxNpc = Fn("RTX"), rtxAnim = Fn("rtxAnim"), rtxP = Fn("RTXp"), rtxPAnim = Fn("RTXpAnim"), addLog = Fn("AddLog"), menuNew = Fn("menuNew");
        while (work.Count > 0 && results.Count < 24)
        {
            var (c, kn, conds, evs, steps) = work.Pop();
            bool done = false;
            while (!done)
            {
                if (++steps > 3000) { evs.Add(new Bye()); results.Add(new Route(conds, evs)); break; }
                var st = c.Current;
                if (st is null)
                {
                    if (c.Calls.Count > 0) { c.Frames = c.Calls[^1]; c.Calls.RemoveAt(c.Calls.Count - 1); c.Advance(); continue; }
                    evs.Add(new Bye()); results.Add(new Route(conds, evs)); break;
                }
                switch (st)
                {
                    case CallStmt call:
                        if (call.Function == menuNew) { evs.Add(new ToMenu(call.Address)); results.Add(new Route(conds, evs)); done = true; break; }
                        int? tag = call.Args.Length > 0 && call.Args[0] is Num n ? n.V : null;
                        if (tag is int t)
                        {
                            if (call.Function == rtxNpc || call.Function == rtxAnim) evs.Add(new SayNpc(t));
                            else if (call.Function == rtxP || call.Function == rtxPAnim) evs.Add(new SayPlayer(t));
                            else if (call.Function == addLog) evs.Add(new Log(t));
                        }
                        if (!done) c.Advance();
                        break;
                    case AssignStmt a:
                        if (Key(prog, a.Target) is { } key)
                        {
                            if (a.Formula.Count == 1 && a.Formula[0].Value is Num v && a.Formula[0].Op is 0) { kn = new(kn) { [key] = v.V }; evs.Add(new Set(key, v.V)); }
                            else if (a.Formula.Count == 2 && Key(prog, a.Formula[0].Value) == key && a.Formula[1].Value is Num d && kn.TryGetValue(key, out var cur))
                            {
                                int nv = a.Formula[0].Op == 1 ? cur + d.V : a.Formula[0].Op == 2 ? cur - d.V : cur;
                                kn = new(kn) { [key] = nv }; evs.Add(new Set(key, nv));
                            }
                            else kn = kn.Where(kv => kv.Key != key).ToDictionary();
                        }
                        c.Advance();
                        break;
                    case IfStmt f:
                    {
                        var (verdict, atoms, anyOr) = Evaluate(prog, f, kn);
                        if (verdict == true) { c.Frames.Add((f.Body, 0)); break; }
                        if (verdict == false) { c.Advance(); break; }
                        // Unknown: take it with its conditions, and skip it with them negated.
                        var skip = c.Clone(); skip.Advance();
                        var skipConds = conds.ToList();
                        if (atoms.Count > 0)
                        {
                            if (anyOr) foreach (var at in atoms) skipConds.Add([at.Not()]);
                            else skipConds.Add(atoms.Select(at => at.Not()).ToList());
                        }
                        work.Push((skip, kn, skipConds, evs.ToList(), steps));
                        var takeConds = conds.ToList();
                        var takeKnown = new Dictionary<string, int>(kn);
                        if (anyOr) takeConds.Add(atoms);
                        else foreach (var at in atoms) { takeConds.Add([at]); if (at.Cmp == 0) takeKnown[at.Key] = at.Value; }
                        conds = takeConds; kn = takeKnown;
                        c.Frames.Add((f.Body, 0));
                        break;
                    }
                    case JumpStmt j:
                        if (j.Kind is "goto" or "end")
                        {
                            if (prog.MenuAddresses.Contains(j.Target) && j.Target != startMenu) { evs.Add(new ToMenu(j.Target)); results.Add(new Route(conds, evs)); done = true; break; }
                            if (j.Target == startMenu) { evs.Add(new ToMenu(j.Target)); results.Add(new Route(conds, evs)); done = true; break; }
                            if (!prog.At.TryGetValue(j.Target, out var to)) { evs.Add(new Bye()); results.Add(new Route(conds, evs)); done = true; break; }
                            c.Frames = to.ToList();
                        }
                        else if (j.Kind == "gosub" && prog.At.TryGetValue(j.Target, out var sub)) { c.Calls.Add(c.Frames.ToList()); c.Frames = sub.ToList(); }
                        else c.Advance();
                        break;
                    case OtherStmt o when o.What is "return":
                        if (c.Calls.Count > 0) { c.Frames = c.Calls[^1]; c.Calls.RemoveAt(c.Calls.Count - 1); c.Advance(); }
                        else { evs.Add(new Bye()); results.Add(new Route(conds, evs)); done = true; }
                        break;
                    case OtherStmt o when o.What is "endint":
                        evs.Add(new Bye()); results.Add(new Route(conds, evs)); done = true;
                        break;
                    default:
                        c.Advance();
                        break;
                }
            }
        }
        return results;
    }

    static string? Key(Program prog, Value v) => v switch { Flag f => $"F{f.Index}", Local l => $"L:{prog.Actor}:{l.Index}", _ => null };

    /// <summary>An if's conditions: true/false when known (or decided by assumption: in a conversation the player is
    /// close and not fighting), else null with the atoms to condition on.</summary>
    (bool? Verdict, List<Atom> Atoms, bool AnyOr) Evaluate(Program prog, IfStmt f, Dictionary<string, int> known)
    {
        var atoms = new List<Atom>();
        var values = new List<bool?>();
        bool anyOr = f.Conditions.Any(c => c.Conj == 2), anyAnd = f.Conditions.Any(c => c.Conj == 1);
        foreach (var (lhs, cmp, rhs, _) in f.Conditions)
        {
            bool? value = null;
            if (Key(prog, lhs) is { } k && rhs is Num r)
            {
                var atom = new Atom(k, cmp, r.V);
                if (known.TryGetValue(k, out var v)) value = atom.Test(v); else atoms.Add(atom);
            }
            else if (lhs is FnValue fv && rhs is Num rn)
            {
                string name = functionNames.ElementAtOrDefault(fv.Function) ?? "";
                int assumed = name switch { "PlayerDistance" => 0, "menuSelection" => -1, _ => 0 };
                if (name is not ("PlayerDistance" or "IsHoldingWeapon" or "isFighting" or "ACTIVATE")) unhandled[name] = unhandled.GetValueOrDefault(name) + 1;
                value = new Atom("", cmp, rn.V).Test(assumed);
            }
            else value = false;
            values.Add(value);
        }
        if (anyOr && anyAnd) return (atoms.Count == 0 ? values.All(v => v == true) : null, [], false);   // mixed: no conditions kept
        if (anyOr)
        {
            if (values.Any(v => v == true)) return (true, [], true);
            return atoms.Count == 0 ? (false, [], true) : (null, atoms, true);
        }
        if (values.Any(v => v == false)) return (false, [], false);
        return atoms.Count == 0 ? (true, [], false) : (null, atoms, false);
    }

    // ---- building dialogue ----

    /// <summary>Converts every conversation. Call after the actors exist.</summary>
    public void BuildDialogue()
    {
        string def = Path.Combine(rgDir, "soup386", "SOUP386.DEF");
        functionNames = RgScript.FunctionNames(def);
        flagDefs = RgScript.Flags(def);
        rtx = RgRtx.Load(Path.Combine(rgDir, "ENGLISH.RTX"));
        if (rtx is null) { Problems.Add("ENGLISH.RTX not found: no dialogue"); return; }
        LearnProperNouns();
        dialogueQuest = new Quest(mod)
        {
            EditorID = "RGDialogue",
            Name = "Redguard Dialogue",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.AllowRepeatedStages,
            Priority = 40,
            Type = Quest.TypeEnum.Misc,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRef) }],
            NextAliasID = 1,
        };
        mod.Quests.Add(dialogueQuest);
        logQuest = new Quest(mod) { EditorID = "RGCyrusLog", Name = "Cyrus's Log", Priority = 50, Type = Quest.TypeEnum.MainQuest, Stages = [] };
        mod.Quests.Add(logQuest);
        int lineUp = Fn("PlayerLineUp"), menuAdd = Fn("menuAddItem");
        foreach (var scene in scenes.Values)
            foreach (var (actor, root) in scene.Script.Programs)
            {
                if (!scene.Script.Calls.Any(c => c.Actor == actor && c.Function == menuAdd) || !scene.Script.Calls.Any(c => c.Actor == actor && c.Function == lineUp)) continue;
                if (ActorFor(actor) is not { } npc) continue;
                try { Conversation(scene, actor, root, npc); DialogueActors++; }
                catch (Exception e) { Problems.Add($"dialogue {actor}: {e.Message}"); }
            }
        if (logQuest.Stages.Count == 0) mod.Quests.Remove(logQuest);
        foreach (var (fn, n) in unhandled.OrderByDescending(kv => kv.Value).Take(8)) Problems.Add($"dialogue: {n} conditions on {fn} assumed false");
    }

    void Conversation(Scene scene, string actor, List<Stmt> root, Npc speaker)
    {
        var prog = new Program { Actor = actor, Root = root };
        int menuNew = Fn("menuNew");
        prog.Index(root, [], menuNew);
        var entry = prog.At.Keys.Order().FirstOrDefault(a => prog.At[a][^1].List[prog.At[a][^1].Index] is CallStmt c && c.Function == Fn("PlayerLineUp"));
        if (!prog.At.ContainsKey(entry)) return;
        var locals = scene.Script.Locals;
        var openings = Follow(prog, new Cursor { Frames = prog.At[entry].ToList() }, [], []);
        int mainMenu = openings.Select(p => p.Events.LastOrDefault()).OfType<ToMenu>().GroupBy(m => m.Address).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(-1);
        if (mainMenu < 0) return;

        var branches = new Dictionary<int, (DialogBranch Branch, List<DialogTopic> Topics)>();
        var pending = new Queue<int>();
        (DialogBranch Branch, List<DialogTopic> Topics) BranchFor(int menu)
        {
            if (branches.TryGetValue(menu, out var b)) return b;
            var branch = new DialogBranch(mod)
            {
                EditorID = $"RG{Sanitize(actor)}Menu{menu}",
                Quest = dialogueQuest!.ToLink<IQuestGetter>(),
                Category = DialogBranch.CategoryType.Player,
                Flags = menu == mainMenu ? DialogBranch.Flag.TopLevel : 0,
            };
            mod.DialogBranches.Add(branch);
            branches[menu] = b = (branch, []);
            pending.Enqueue(menu);
            return b;
        }
        BranchFor(mainMenu);

        Condition Speaker()
        {
            var d = new GetIsIDConditionData();
            d.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(d, speaker.FormKey);
            return new ConditionFloat { Data = d, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 };
        }
        List<Condition> ConditionsOf(List<List<Atom>> clauses)
        {
            var list = new List<Condition> { Speaker() };
            foreach (var clause in clauses.DistinctBy(c => string.Join("|", c)))
                for (int i = 0; i < clause.Count; i++)
                {
                    var a = clause[i];
                    var g = GlobalFor(a.Key, actor, locals);
                    var d = new GetGlobalValueConditionData();
                    d.Global = new FormLinkOrIndex<IGlobalGetter>(d, g.FormKey);
                    list.Add(new ConditionFloat
                    {
                        Data = d,
                        CompareOperator = a.Cmp switch { 0 => CompareOperator.EqualTo, 1 => CompareOperator.NotEqualTo, 2 => CompareOperator.LessThan, 3 => CompareOperator.GreaterThan, 4 => CompareOperator.LessThanOrEqualTo, _ => CompareOperator.GreaterThanOrEqualTo },
                        ComparisonValue = a.Value,
                        Flags = i < clause.Count - 1 ? Condition.Flag.OR : 0,
                    });
                }
            return list;
        }

        int childNo = 0;
        // A path as responses: the first on `topic`, each later Cyrus line continuing in a child topic.
        void AddPath(DialogTopic topic, Route path, List<Condition> conditions, int currentMenu)
        {
            var segments = new List<(int? Player, List<int> Npc, List<Ev> Effects)>();
            foreach (var ev in path.Events)
            {
                if (ev is SayPlayer sp && (segments.Count == 0 || segments[^1].Npc.Count > 0 || segments[^1].Player is not null)) segments.Add((sp.Tag, [], []));
                else if (ev is SayPlayer sp2 && segments.Count > 0 && segments[^1].Player is null) segments[^1] = (sp2.Tag, segments[^1].Npc, segments[^1].Effects);
                else
                {
                    if (segments.Count == 0) segments.Add((null, [], []));
                    if (ev is SayNpc sn) segments[^1].Npc.Add(sn.Tag);
                    else if (ev is Log or Set) segments[^1].Effects.Add(ev);
                }
            }
            if (segments.Count == 0) segments.Add((null, [], []));
            var end = path.Events.LastOrDefault();
            DialogTopic owner = topic;
            for (int s = 0; s < segments.Count; s++)
            {
                var (player, npcLines, effects) = segments[s];
                var r = new DialogResponses(mod) { Conditions = s == 0 ? [.. conditions] : [Speaker()], Responses = [], Flags = new DialogResponseFlags() };
                if (player is int p && Text(p) is { } prompt) r.Prompt = prompt;
                uint n = 1;
                foreach (var line in npcLines)
                {
                    if (Text(line) is not { } said) continue;
                    r.Responses.Add(new DialogResponse { Text = said, ResponseNumber = (byte)n, Emotion = Emotion.Neutral, EmotionValue = 50 });
                    if (rtx!.Entries.TryGetValue(TagString(line), out var e) && e.AudioLength > 0) VoiceMap.Add(new VoiceLine(r.FormKey.ID, (int)n, TagString(line)));
                    n++;
                }
                if (r.Responses.Count == 0) r.Responses.Add(new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 });
                r.VirtualMachineAdapter = ResponseEffects(effects, actor, locals);
                owner.Responses.Add(r);
                DialogueResponses++;
                if (s < segments.Count - 1)
                {
                    // Cyrus speaks again: the conversation goes on through a one-choice child topic.
                    var child = NewTopic($"RG{Sanitize(actor)}Line{childNo++}", Text(segments[s + 1].Player ?? 0) ?? "...", branches[currentMenu].Branch);
                    r.LinkTo.Add(child.ToLink<IDialogTopicGetter>());
                    owner = child;
                    continue;
                }
                if (end is ToMenu tm && tm.Address != mainMenu && tm.Address != currentMenu)
                {
                    BranchFor(tm.Address);
                    linkLater.Add((r, tm.Address));
                }
                else if (end is Bye) r.Flags.Flags |= DialogResponses.Flag.Goodbye;
            }
        }

        // Openings: the speaker's greeting when they speak first, else a main-menu topic Cyrus begins.
        var hello = NewTopic($"RG{Sanitize(actor)}Hello", null, null, hello: true);
        foreach (var path in openings.Where(p => p.Events.Any(e => e is SayNpc or SayPlayer)).OrderByDescending(p => p.Conditions.Count))
        {
            var first = path.Events.First(e => e is SayNpc or SayPlayer);
            if (first is SayNpc) AddPath(hello, path, ConditionsOf(path.Conditions), mainMenu);
            else
            {
                var t = NewTopic($"RG{Sanitize(actor)}Open{childNo++}", Text(((SayPlayer)first).Tag) ?? "...", branches[mainMenu].Branch);
                branches[mainMenu].Topics.Add(t);
                AddPath(t, path, ConditionsOf(path.Conditions), mainMenu);
            }
        }
        if (hello.Responses.Count == 0) mod.DialogTopics.Remove(hello);

        while (pending.Count > 0)
        {
            int menu = pending.Dequeue();
            foreach (var item in MenuItems(prog, menu))
            {
                var topic = NewTopic($"RG{Sanitize(actor)}{Sanitize(TagString(item.Tag))}", Text(item.Tag) ?? TagString(item.Tag), branches[menu].Branch);
                branches[menu].Topics.Add(topic);
                var known = new Dictionary<string, int> { [item.SelectionKey] = item.Index };
                foreach (var clause in item.Conditions) if (clause.Count == 1 && clause[0].Cmp == 0) known[clause[0].Key] = clause[0].Value;
                var paths = Follow(prog, item.After.Clone(), known, item.Conditions, menu);
                foreach (var path in paths.OrderByDescending(p => p.Conditions.Count))
                    AddPath(topic, path, ConditionsOf(path.Conditions), menu);
                if (topic.Responses.Count == 0) { mod.DialogTopics.Remove(topic); branches[menu].Topics.Remove(topic); }
            }
            branches[menu].Branch.StartingTopic = branches[menu].Topics.FirstOrDefault()?.ToNullableLink<IDialogTopicGetter>() ?? new FormLinkNullable<IDialogTopicGetter>();
        }
        foreach (var (r, menu) in linkLater)
            if (branches.TryGetValue(menu, out var b)) foreach (var t in b.Topics) r.LinkTo.Add(t.ToLink<IDialogTopicGetter>());
        linkLater.Clear();
        foreach (var (_, b) in branches) if (b.Topics.Count == 0) mod.DialogBranches.Remove(b.Branch);
    }

    readonly List<(DialogResponses Response, int Menu)> linkLater = [];

    DialogTopic NewTopic(string edid, string? name, DialogBranch? branch, bool hello = false)
    {
        var topic = new DialogTopic(mod)
        {
            EditorID = edid,
            Quest = dialogueQuest!.ToNullableLink<IQuestGetter>(),
            Priority = 50,
            Category = hello ? DialogTopic.CategoryEnum.Misc : DialogTopic.CategoryEnum.Topic,
            Subtype = hello ? DialogTopic.SubtypeEnum.Hello : DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        if (name is not null) topic.Name = name;
        if (branch is not null) topic.Branch = branch.ToNullableLink<IDialogBranchGetter>();
        mod.DialogTopics.Add(topic);
        DialogueTopics++;
        return topic;
    }

    sealed record MenuItem(int Tag, int Index, List<List<Atom>> Conditions, string SelectionKey, Cursor After);

    /// <summary>A menu's items: each menuAddItem (with the conditions of the if around it) between menuNew and
    /// menuProc, and where the code goes on once the selection is stored.</summary>
    List<MenuItem> MenuItems(Program prog, int menuAddress)
    {
        var items = new List<MenuItem>();
        if (!prog.At.TryGetValue(menuAddress, out var at)) return items;
        var (list, i) = at[^1];
        int menuAdd = Fn("menuAddItem"), menuProc = Fn("menuProc"), menuSel = Fn("menuSelection");
        var found = new List<(int Tag, int Index, List<List<Atom>> Conds)>();
        for (i++; i < list.Count; i++)
        {
            if (list[i] is CallStmt c && c.Function == menuProc) break;
            if (list[i] is CallStmt add && add.Function == menuAdd && add.Args.Length >= 3 && add.Args[0] is Num t && add.Args[2] is Num ix) found.Add((t.V, ix.V, []));
            if (list[i] is IfStmt f && f.Body.OfType<CallStmt>().FirstOrDefault(b => b.Function == menuAdd) is { Args.Length: >= 3 } item && item.Args[0] is Num tag && item.Args[2] is Num idx)
            {
                var (verdict, atoms, anyOr) = Evaluate(prog, f, []);
                var conds = new List<List<Atom>>();
                if (anyOr) conds.Add(atoms); else foreach (var a in atoms) conds.Add([a]);
                if (verdict != false) found.Add((tag.V, idx.V, conds));
            }
        }
        // After menuProc: selection = menuSelection().
        for (i++; i < list.Count; i++)
            if (list[i] is AssignStmt a && a.Formula.Count > 0 && a.Formula[0].Value is FnValue fv && fv.Function == menuSel && Key(prog, a.Target) is { } key)
            {
                var after = new Cursor { Frames = at.Take(at.Count - 1).Append((list, i + 1)).ToList() };
                foreach (var (tag, index, conds) in found) items.Add(new MenuItem(tag, index, conds, key, after));
                break;
            }
        return items;
    }

    /// <summary>What a response does when said: Redguard flags and variables set, Cyrus's log written.</summary>
    DialogResponsesAdapter? ResponseEffects(List<Ev> effects, string actor, Dictionary<string, int[]> locals)
    {
        var sets = effects.OfType<Set>().GroupBy(s => s.Key).Select(g => g.Last()).ToList();
        var logs = effects.OfType<Log>().ToList();
        if (sets.Count == 0 && logs.Count == 0) return null;
        var entry = new ScriptEntry { Name = "TMMwInfo" };
        if (sets.Count > 0)
        {
            var objs = new ScriptObjectListProperty { Name = "Globals" };
            foreach (var s in sets) objs.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(GlobalFor(s.Key, actor, locals).FormKey) });
            entry.Properties.Add(objs);
            entry.Properties.Add(new ScriptFloatListProperty { Name = "GlobalValues", Data = [.. sets.Select(s => (float)s.Value)] });
        }
        if (logs.Count > 0)
        {
            var quests = new ScriptObjectListProperty { Name = "StageQuests" };
            foreach (var _ in logs) quests.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(logQuest!.FormKey) });
            entry.Properties.Add(quests);
            entry.Properties.Add(new ScriptIntListProperty { Name = "Stages", Data = [.. logs.Select(l => (int)LogStage(l.Tag))] });
        }
        return new DialogResponsesAdapter
        {
            Scripts = [entry],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = "TMMwInfo",
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMMwInfo", FragmentName = "Fragment_End" },
            },
        };
    }
}
