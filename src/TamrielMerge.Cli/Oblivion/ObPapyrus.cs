using System.Globalization;
using System.Text;
using static TamrielMerge.Oblivion.ObScript;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion scripts (vanilla and OBSE, parsed by <see cref="ObScript"/>) translated into Papyrus:
/// <list type="bullet">
/// <item>an object script becomes OB_&lt;name&gt; extends ObjectReference (Actor for people and creatures), a quest script
/// extends Quest, a magic effect script extends ActiveMagicEffect;</item>
/// <item>blocks become events — OnActivate, OnEquipped, OnDeath, OnHit, OnLoad, OnTrigger, OnContainerChanged, OnPackage*,
/// OnEffectStart/Finish…; GameMode (every frame in Oblivion) becomes OnUpdate on a short timer (quests at their
/// script delay), registered when the object loads or the quest starts;</item>
/// <item>variables become properties (ob_&lt;name&gt;, so other scripts can read quest.var); forms the script names
/// become properties (f_&lt;EditorID&gt;) the converter fills with the converted records; the player is PlayerRef;</item>
/// <item>functions go through a table of Papyrus templates (vanilla Oblivion and OBSE onto Skyrim and SKSE). What has no
/// equivalent yet is kept as a comment and counted, so coverage can be measured per script and per mod.</item>
/// </list>
/// Typing follows Papyrus: references held in variables are Forms and cast where a call needs an ObjectReference or
/// Actor; Bool results used as numbers are cast to Int; Float values stored in Int variables are cast.
/// </summary>
public sealed class ObPapyrus(ObPapyrus.IWorld world, ObFunctions functions)
{
    /// <summary>What a name in a script refers to outside it.</summary>
    public sealed record FormInfo(string EditorID, string PapyrusType, string? ScriptName);

    public interface IWorld
    {
        FormInfo? Find(string editorId);
        /// <summary>The Papyrus script name an Oblivion script EditorID becomes.</summary>
        string ScriptName(string scriptEditorId) => PapyrusName(scriptEditorId);
        /// <summary>A converted script's variables (name → Oblivion type), for quest.variable from other scripts.</summary>
        IReadOnlyDictionary<string, string>? Variables(string papyrusScriptName) => null;
        /// <summary>The quest a quest script runs on (EditorID), when it's one quest's.</summary>
        string? QuestOf(string scriptEditorId) => null;
    }

    /// <summary>OB_&lt;EditorID&gt;, shortened with a hash past Papyrus's 38 characters.</summary>
    public static string PapyrusName(string scriptEditorId)
    {
        string name = "OB_" + Clean(scriptEditorId);
        if (name.Length <= 38) return name;
        uint h = 2166136261;
        foreach (char ch in scriptEditorId.ToLowerInvariant()) h = (h ^ ch) * 16777619;
        return $"{name[..31]}_{h & 0xFFFFFF:X6}";
    }

    /// <summary>
    /// A script property and what fills it: the converted form of the Oblivion record with this EditorID; a quest
    /// variable's global (EditorID is the quest's) when QuestVariable is set; or, when Generated, a record the
    /// converter makes for the scripts (a Message, the fame globals) under this EditorID.
    /// </summary>
    public sealed record Property(string Name, string Type, string EditorID, string? QuestVariable = null, bool Generated = false);
    /// <summary>A message box with buttons: the converter makes a Message record (EditorID) with this text and buttons.</summary>
    public sealed record MessageForm(string EditorID, string Text, List<string> Buttons);
    public sealed record Output(string ScriptName, string Extends, string Kind, string Source, List<Property> Properties, int Statements, int Translated, Dictionary<string, int> Missing, List<MessageForm> Messages);

    // ---- types ----

    enum T { Int, Float, Bool, String, Form, Ref, Actor, Quest, Global, Spell, Void, Unknown }

    static string PapyrusType(T t) => t switch
    {
        T.Int => "Int", T.Float => "Float", T.Bool => "Bool", T.String => "String", T.Ref => "ObjectReference",
        T.Actor => "Actor", T.Quest => "Quest", T.Global => "GlobalVariable", T.Spell => "Spell", _ => "Form",
    };

    static T FromPapyrus(string type) => type switch
    {
        "Int" => T.Int, "Float" => T.Float, "Bool" => T.Bool, "String" => T.String, "ObjectReference" => T.Ref,
        "Actor" => T.Actor, "Quest" => T.Quest, "GlobalVariable" => T.Global, "Spell" => T.Spell, _ => T.Form,
    };

    readonly record struct Code(string Text, T Type);

    public static string Clean(string id)
    {
        var sb = new StringBuilder();
        foreach (char c in id) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    // ---- per-script state ----

    string kind = "Object";       // Object, Quest, MagicEffect, Function
    bool actor;                   // object script on an actor
    readonly Dictionary<string, T> locals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Property> properties = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> missing = new(StringComparer.OrdinalIgnoreCase);
    string blockContext = "";     // the event being written (for akActionRef etc.)
    int statements, translated;
    bool lineOk;
    bool usesSecondsPassed, usesButtons, usesSay, usesActionRef, usesDisposition;

    static bool IsPlayer(Call c, int i) => i < c.Args.Count && c.Args[i] is Name { Target: null } n
        && (n.Id.Equals("player", StringComparison.OrdinalIgnoreCase) || n.Id.Equals("playerref", StringComparison.OrdinalIgnoreCase));
    const string ActionRefMarker = "  ;@actionref@";
    string ActionRef =>blockContext is "onactivate" or "ontrigger" or "ontriggeractor" or "ontriggermob" ? "akActionRef" : "__actionRef";
    readonly List<MessageForm> messages = [];
    string scriptId = "";
    string? ownQuest;

    /// <param name="scriptKind">Object, Quest or MagicEffect (Oblivion's script type).</param>
    /// <param name="onActor">An object script on an NPC or creature.</param>
    /// <param name="questDelay">A quest script's update interval (Oblivion's default 5 seconds).</param>
    public Output Translate(Script script, string scriptEditorId, string scriptKind, bool onActor = false, float questDelay = 5)
    {
        kind = scriptKind; actor = onActor;
        locals.Clear(); properties.Clear(); missing.Clear();
        statements = translated = 0;
        usesSecondsPassed = usesButtons = usesSay = usesActionRef = usesDisposition = false;
        messages.Clear();
        scriptId = scriptEditorId;
        ownQuest = kind == "Quest" ? world.QuestOf(scriptEditorId) : null;
        bool isFunction = script.Blocks.Any(b => b.Type.Equals("Function", StringComparison.OrdinalIgnoreCase));
        if (isFunction) kind = "Function";
        string extends = kind switch { "Quest" => "Quest", "MagicEffect" => "ActiveMagicEffect", "Function" => "Quest", _ => actor ? "Actor" : "ObjectReference" };
        foreach (var v in script.Variables) locals[v.Name] = VariableType(v.Type);

        var body = new StringBuilder();
        // Blocks becoming the same event merge into it, in order, each under its own filter (OnAdd and OnDrop both
        // become OnContainerChanged; two OnActivate blocks, one for the player, one for anyone, share OnActivate).
        var groups = script.Blocks.GroupBy(b => b.Type.ToLowerInvariant()).ToList();
        bool hasUpdate = groups.Any(g => g.Key is "gamemode" or "scripteffectupdate");
        bool hasActivate = groups.Any(g => g.Key == "onactivate");
        foreach (var e in script.Blocks.Where(b => !(kind == "Object" && b.Type.Equals("OnLoad", StringComparison.OrdinalIgnoreCase)))
                     .GroupBy(b => EventHeader(b.Type.ToLowerInvariant(), b) ?? "?" + b.Type.ToLowerInvariant()))
            WriteEvent(body, e.Key, e.ToList());

        // Objects: OnLoad blocks the activation Oblivion's OnActivate replaces and starts GameMode's timer, along with
        // the script's own OnLoad blocks. Quests start their timer when they start.
        var start = new StringBuilder();
        var onLoad = script.Blocks.Where(b => b.Type.Equals("OnLoad", StringComparison.OrdinalIgnoreCase)).ToList();
        if (kind == "Object" && (hasUpdate || hasActivate || onLoad.Count > 0))
        {
            start.AppendLine("Event OnLoad()");
            if (hasActivate) start.AppendLine("  BlockActivation()   ; Oblivion's OnActivate replaces the default activation");
            if (hasUpdate) start.AppendLine("  RegisterForSingleUpdate(0.5)");
            blockContext = "onload";
            foreach (var b in onLoad) WriteStatements(start, b.Body, 1);
            blockContext = "";
            start.AppendLine("EndEvent");
            start.AppendLine();
            if (hasUpdate)
            {
                start.AppendLine("Event OnUnload()");
                start.AppendLine("  UnregisterForUpdate()");
                start.AppendLine("EndEvent");
                start.AppendLine();
            }
        }
        else if (kind == "MagicEffect" && hasUpdate && !groups.Any(g => g.Key == "scripteffectstart"))
        {
            start.AppendLine("Event OnEffectStart(Actor akTarget, Actor akCaster)");
            start.AppendLine("  RegisterForSingleUpdate(0.5)");
            start.AppendLine("EndEvent");
            start.AppendLine();
        }
        else if (kind == "Quest" && hasUpdate)
        {
            start.AppendLine("Event OnInit()");
            start.AppendLine("  RegisterForSingleUpdate(QuestDelay)");
            start.AppendLine("EndEvent");
            start.AppendLine();
        }

        var head = new StringBuilder();
        string name = world.ScriptName(scriptEditorId);
        head.AppendLine($"ScriptName {name} extends {extends}");
        head.AppendLine($"{{Converted from Oblivion's {scriptEditorId} ({scriptKind}) by TamrielMerge.}}");
        head.AppendLine();
        foreach (var (v, t) in locals.Where(l => !(kind == "Quest" && ownQuest is not null && l.Value is T.Int or T.Float)))
            head.AppendLine($"{(t == T.Unknown ? "Form[]" : PapyrusType(t))} Property {Var(v)} Auto Hidden");
        foreach (var p in properties.Values.OrderBy(p => p.Name))
            head.AppendLine($"{p.Type} Property {p.Name} Auto");
        // Every quest script has its delay (SetQuestDelay from any script sets it).
        if (kind == "Quest") head.AppendLine($"Float Property QuestDelay = {F(questDelay)} Auto Hidden");
        if (kind == "Function") head.AppendLine("Float __result");
        if (usesDisposition)
        {
            head.AppendLine();
            head.AppendLine("; Disposition towards the player: Variable09 holds it minus 50, kept within 0-100.");
            head.AppendLine("Function TMModDisposition(Actor akActor, Float afBy)");
            head.AppendLine("  Float now = akActor.GetActorValue(\"Variable09\")");
            head.AppendLine("  Float target = now + afBy");
            head.AppendLine("  If target < -50.0");
            head.AppendLine("    target = -50.0");
            head.AppendLine("  ElseIf target > 50.0");
            head.AppendLine("    target = 50.0");
            head.AppendLine("  EndIf");
            head.AppendLine("  akActor.ModActorValue(\"Variable09\", target - now)");
            head.AppendLine("EndFunction");
        }
        if (usesSay)
        {
            head.AppendLine();
            head.AppendLine("; Oblivion's Say gives the line's length; Skyrim's doesn't, so a typical line's is used.");
            head.AppendLine("Float Function SayTimed(ObjectReference akSpeaker, Topic akTopic)");
            head.AppendLine("  akSpeaker.Say(akTopic)");
            head.AppendLine("  Return 4.0");
            head.AppendLine("EndFunction");
        }
        if (usesButtons)
        {
            head.AppendLine("Int __button = -1");
            head.AppendLine();
            head.AppendLine("Int Function TakeButton()");
            head.AppendLine("  Int b = __button");
            head.AppendLine("  __button = -1");
            head.AppendLine("  Return b");
            head.AppendLine("EndFunction");
        }
        if (usesSecondsPassed)
        {
            head.AppendLine("Float __lastSeconds = -1.0");
            head.AppendLine();
            head.AppendLine("Float Function SecondsPassed()");
            head.AppendLine("  Float now = Utility.GetCurrentRealTime()");
            head.AppendLine("  Float passed = 0.0");
            head.AppendLine("  If __lastSeconds >= 0.0");
            head.AppendLine("    passed = now - __lastSeconds");
            head.AppendLine("  EndIf");
            head.AppendLine("  __lastSeconds = now");
            head.AppendLine("  Return passed");
            head.AppendLine("EndFunction");
        }
        if (usesActionRef) head.AppendLine("ObjectReference __actionRef");
        head.AppendLine();
        var output = head.Append(start).Append(body).ToString()
            .Replace(ActionRefMarker + Environment.NewLine, usesActionRef ? "  __actionRef = akActionRef" + Environment.NewLine : "");
        return new Output(name, extends, kind, output, properties.Values.ToList(), statements, translated, new Dictionary<string, int>(missing), [.. messages]);
    }

    /// <summary>An Oblivion variable's property name (v_, so it can't clash with a script or type name).</summary>
    static string Var(string name) => "v_" + Clean(name);

    static string F(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

    // ---- blocks ----

    string? EventHeader(string type, Block b) => type switch
    {
        "gamemode" => kind == "MagicEffect" ? null : "Event OnUpdate()",
        "scripteffectupdate" => "Event OnUpdate()",
        "onactivate" => "Event OnActivate(ObjectReference akActionRef)",
        "ondeath" => "Event OnDeath(Actor akKiller)",
        "onequip" => "Event OnEquipped(Actor akActor)",
        "onunequip" => "Event OnUnequipped(Actor akActor)",
        "onload" => "Event OnLoad()",
        "onreset" => "Event OnReset()",
        "onadd" or "ondrop" => "Event OnContainerChanged(ObjectReference akNewContainer, ObjectReference akOldContainer)",
        "ontrigger" or "ontriggeractor" or "ontriggermob" => "Event OnTrigger(ObjectReference akActionRef)",
        "onhit" or "onhitwith" => "Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)",
        "onstartcombat" => "Event OnCombatStateChanged(Actor akTarget, Int aeCombatState)",
        "onpackagestart" => "Event OnPackageStart(Package akNewPackage)",
        "onpackagedone" or "onpackageend" => "Event OnPackageEnd(Package akOldPackage)",
        "onpackagechange" => "Event OnPackageChange(Package akOldPackage)",
        "onmagiceffecthit" => "Event OnMagicEffectApply(ObjectReference akCaster, MagicEffect akEffect)",
        "onactorequip" => "Event OnObjectEquipped(Form akBaseObject, ObjectReference akReference)",
        "scripteffectstart" => "Event OnEffectStart(Actor akTarget, Actor akCaster)",
        "scripteffectfinish" => "Event OnEffectFinish(Actor akTarget, Actor akCaster)",
        "function" => FunctionHeader(b),
        _ => null,
    };

    void WriteEvent(StringBuilder sb, string header, List<Block> blocks)
    {
        if (header.StartsWith('?'))
        {
            sb.AppendLine($"; Oblivion block \"{blocks[0].Type}\" has no Skyrim equivalent ({blocks.Sum(b => b.Body.Count)} statements kept as comments)");
            foreach (var b in blocks) CommentOut(sb, b.Body, 0);
            sb.AppendLine();
            Missing("block " + blocks[0].Type);
            return;
        }
        bool function = header.Contains(" Function ");
        sb.AppendLine(header);
        var types = blocks.Select(b => b.Type.ToLowerInvariant()).ToList();
        if (types.Any(t => t is "gamemode" or "scripteffectupdate"))
        {
            // Rescheduled first, so a Return in the block doesn't stop the loop; a quest's only while it runs.
            if (kind == "Quest")
            {
                sb.AppendLine("  If !IsRunning()");
                sb.AppendLine("    Return");
                sb.AppendLine("  EndIf");
                sb.AppendLine("  RegisterForSingleUpdate(QuestDelay)");
            }
            else sb.AppendLine("  RegisterForSingleUpdate(0.5)");
        }
        if (types.Contains("scripteffectstart") && kind == "MagicEffect") sb.AppendLine("  RegisterForSingleUpdate(0.5)");
        if (function)
            foreach (var p in blocks[0].Parameters) sb.AppendLine($"  {Var(p)} = a_{Clean(p)}");
        if (header.Contains("akActionRef")) sb.AppendLine(ActionRefMarker);
        foreach (var b in blocks)
        {
            blockContext = b.Type.ToLowerInvariant();
            // Filters from the block's parameter (OnActivate Player, OnAdd Player, OnTriggerActor …).
            string? filter = BlockFilter(blockContext, b.Parameters);
            int depth = 1;
            if (filter is not null) { sb.AppendLine($"  If {filter}"); depth = 2; }
            WriteStatements(sb, b.Body, depth);
            if (filter is not null) sb.AppendLine("  EndIf");
        }
        if (function) sb.AppendLine("  Return __result");
        sb.AppendLine(function ? "EndFunction" : "EndEvent");
        sb.AppendLine();
        blockContext = "";
    }

    string? BlockFilter(string type, List<string> parameters)
    {
        string Is(string arg, T type) => FormCompare(new Code(arg, type), "==", Expr(new Name(parameters[0]))).Text;
        switch (type)
        {
            case "onactivate" when parameters.Count > 0: return Is("akActionRef", T.Ref);
            case "onadd" when parameters.Count > 0: return Is("akNewContainer", T.Ref);
            case "onadd": return "akNewContainer != None";
            case "ondrop" when parameters.Count > 0: return $"{Is("akOldContainer", T.Ref)} && akNewContainer == None";
            case "ondrop": return "akNewContainer == None";
            case "onequip" when parameters.Count > 0: return Is("akActor", T.Actor);
            case "onunequip" when parameters.Count > 0: return Is("akActor", T.Actor);
            case "ondeath" when parameters.Count > 0: return Is("akKiller", T.Actor);
            case "ontriggeractor": return parameters.Count > 0 ? Is("akActionRef", T.Ref) : "akActionRef as Actor";
            case "ontriggermob": return "akActionRef as Actor";
            case "ontrigger" when parameters.Count > 0: return Is("akActionRef", T.Ref);
            case "onhitwith" when parameters.Count > 0: return Is("akSource", T.Form);
            case "onhit" when parameters.Count > 0: return Is("akAggressor", T.Ref);
            case "onstartcombat": return parameters.Count > 0 ? $"aeCombatState == 1 && {Is("akTarget", T.Actor)}" : "aeCombatState == 1";
            case "onmagiceffecthit" when parameters.Count > 0: return null;   // effect code: matched by name later
            default: return null;
        }
    }

    string FunctionHeader(Block b)
    {
        var args = b.Parameters.Select(p => $"{(locals.TryGetValue(p, out var t) ? PapyrusType(t == T.Unknown ? T.Form : t) : "Float")} a_{Clean(p)}");
        return $"Float Function Run(ObjectReference akSelf{(b.Parameters.Count > 0 ? ", " : "")}{string.Join(", ", args)})";
    }

    // ---- statements ----

    void CommentOut(StringBuilder sb, List<Stmt> body, int depth)
    {
        foreach (var s in body) sb.AppendLine(new string(' ', depth * 2) + "; " + Describe(s));
    }

    static string Describe(Stmt s) => s switch
    {
        Unparsed u => u.Text,
        _ => s.GetType().Name + " (line " + s.Line + ")",
    };

    void WriteStatements(StringBuilder sb, List<Stmt> body, int depth)
    {
        string ind = new(' ', depth * 2);
        // A statement that doesn't translate is kept as a comment (its draft translation) so the script still compiles;
        // a condition that doesn't is False, so its branch never runs.
        string Line(string code) => lineOk ? code.Replace("\n", "\n" + ind) : "; TODO(Oblivion): " + code.Replace("\n", " ");
        string Cond(Expr cond)
        {
            lineOk = true;
            string c = Condition(cond);
            if (lineOk) { translated++; return c; }
            return "False   ; TODO(Oblivion): " + c;
        }
        foreach (var s in body)
        {
            statements++;
            lineOk = true;
            switch (s)
            {
                case Set set:
                    sb.AppendLine(ind + Line(Assignment(set)));
                    break;
                case If i:
                    for (int k = 0; k < i.Branches.Count; k++)
                    {
                        var (cond, branch) = i.Branches[k];
                        string word = k == 0 ? "If" : cond is null ? "Else" : "ElseIf";
                        if (k > 0 && cond is not null) statements++;
                        sb.AppendLine(ind + word + (cond is null ? "" : " " + Cond(cond)));
                        WriteStatements(sb, branch, depth + 1);
                    }
                    sb.AppendLine(ind + "EndIf");
                    continue;   // its conditions are counted by Cond
                case While w:
                    sb.AppendLine(ind + "While " + Cond(w.Condition));
                    WriteStatements(sb, w.Body, depth + 1);
                    sb.AppendLine(ind + "EndWhile");
                    continue;
                case ForEach fe:
                    Missing("foreach (OBSE arrays)");
                    sb.AppendLine(ind + "; foreach over an OBSE array: not converted yet");
                    CommentOut(sb, fe.Body, depth + 1);
                    lineOk = false;
                    break;
                case CallStmt c:
                {
                    var code = CallCode(c.Call, statement: true);
                    sb.AppendLine(ind + Line(code.Text));
                    break;
                }
                case Keyword k:
                    if (k.Word == "return") sb.AppendLine(ind + (kind == "Function" && blockContext == "function" ? "Return __result" : "Return"));
                    else if (k.Word == "endif") { statements--; continue; }
                    else { Missing(k.Word); sb.AppendLine(ind + $"; {k.Word}: Papyrus has none (loop rewritten by hand later)"); lineOk = false; }
                    break;
                case Unparsed u:
                    sb.AppendLine(ind + $"; unreadable in the original: {u.Text}");
                    lineOk = false;
                    break;
            }
            if (lineOk) translated++;
        }
    }

    string Assignment(Set set)
    {
        var value = Expr(set.Value);
        string op = set.Op switch { "+=" => "+", "-=" => "-", "*=" => "*", "/=" => "/", "^=" => "^", _ => "" };
        switch (set.Target)
        {
            case Name n when n.Target is null && locals.TryGetValue(n.Id, out var t):
            {
                if (t == T.Unknown) return Fail("OBSE array variable", "None").Text;
                var slot = LocalSlot(n.Id, t);
                var v = op.Length > 0 ? new Code($"{slot.Read} {op} {Paren(value.Text)}", Arith(t, value.Type)) : value;
                return slot.Write(Convert(v, t));
            }
            case Name n when n.Target is null && world.Find(n.Id) is { PapyrusType: "GlobalVariable" }:
            {
                string g = FormProperty(n.Id, "GlobalVariable");
                var v = op.Length > 0 ? new Code($"{g}.GetValue() {op} {Paren(value.Text)}", T.Float) : value;
                return $"{g}.SetValue({Convert(v, T.Float)})";
            }
            case Name n when n.Target is not null:
            {
                if (Member(n) is not { } slot) return Fail(MemberProblem(n), "None").Text;
                var v = op.Length > 0 ? new Code($"{slot.Read} {op} {Paren(value.Text)}", Arith(slot.Type, value.Type)) : value;
                return slot.Write(Convert(v, slot.Type));
            }
            default:
                Missing("assignment to an OBSE array element");
                lineOk = false;
                return $"; (OBSE array assignment not converted yet) = {value.Text}";
        }
    }

    static T Arith(T a, T b) => a == T.Float || b == T.Float ? T.Float : T.Int;

    string Condition(Expr e)
    {
        var c = Expr(e);
        return c.Type is T.Bool ? c.Text : c.Type is T.Int or T.Float ? $"{Paren(c.Text)} != 0" : c.Text;
    }

    static string Paren(string s) => s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.') ? s : $"({s})";

    /// <summary>A value as the given type: numbers between Int and Float, Bool as Int, forms cast.</summary>
    string Convert(Code c, T to)
    {
        if (c.Type == to) return c.Text;
        // Oblivion keeps references in numbers' clothing: 0 is no reference, and a reference read as a number is a mistake.
        if (IsForm(to) && c.Type is T.Int or T.Float) return c.Text is "0" or "0.0" ? "None" : Fail("number stored in a ref variable", "None").Text;
        if (to is T.Int or T.Float && IsForm(c.Type)) return Fail("ref stored in a number variable", "0").Text;
        if (c.Type == T.Void) return Fail("value of a function whose Skyrim counterpart returns nothing", "0").Text;
        return (to, c.Type) switch
        {
            (T.Int, T.Float) => $"{Paren(c.Text)} as Int",
            (T.Int, T.Bool) => $"{Paren(c.Text)} as Int",
            (T.Float, T.Bool) => $"({Paren(c.Text)} as Int) as Float",
            (T.Float, T.Int) => c.Text,
            (T.Bool, _) => c.Text,
            (T.String, _) => $"{Paren(c.Text)} as String",
            (T.Ref, T.Form) or (T.Actor, T.Form) or (T.Actor, T.Ref) or (T.Quest, T.Form) or (T.Spell, T.Form) or (T.Global, T.Form) => $"{Paren(c.Text)} as {PapyrusType(to)}",
            (T.Form, _) => c.Text,
            _ => c.Text,
        };
    }

    // ---- expressions ----

    Code Expr(Expr e)
    {
        switch (e)
        {
            case Num n:
                return n.Value == Math.Floor(n.Value) && !n.Text.Contains('.') ? new Code(((long)n.Value).ToString(CultureInfo.InvariantCulture), T.Int) : new Code(n.Value.ToString("0.0#####", CultureInfo.InvariantCulture), T.Float);
            case Str s:
                return new Code("\"" + s.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", T.String);
            case Unary u:
            {
                var o = Expr(u.Operand);
                return u.Op switch
                {
                    "-" => new Code("-" + Paren(o.Text), o.Type == T.Bool ? T.Int : o.Type),
                    "!" => new Code("!" + Paren(o.Text), T.Bool),
                    "$" => new Code($"{Paren(o.Text)} as String", T.String),
                    _ => Fail($"OBSE operator {u.Op}", o.Text),
                };
            }
            case Binary b:
            {
                if (b.Op == "::") return Fail("OBSE pair", "None");
                var l = Expr(b.Left); var r = Expr(b.Right);
                switch (b.Op)
                {
                    case "&&" or "||":
                        return new Code($"{Paren(Truth(l))} {b.Op} {Paren(Truth(r))}", T.Bool);
                    case "==" or "!=" or "<" or "<=" or ">" or ">=" when IsForm(l.Type) || IsForm(r.Type):
                        return FormCompare(l, b.Op, r);
                    case "==" or "!=" or "<" or "<=" or ">" or ">=":
                        return new Code($"{Paren(Num(l))} {b.Op} {Paren(Num(r))}", T.Bool);
                    case "%":
                        return new Code($"({Paren(Convert(l, T.Int))} % {Paren(Convert(r, T.Int))})", T.Int);
                    case "^":
                        return new Code($"Math.Pow({Num(l)}, {Num(r)})", T.Float);
                    case "+" when l.Type == T.String || r.Type == T.String:
                        return new Code($"{Paren(l.Text)} + {Paren(r.Text)}", T.String);
                    default:
                        return new Code($"{Paren(Num(l))} {b.Op} {Paren(Num(r))}", b.Op == "/" ? T.Float : Arith(Numeric(l.Type), Numeric(r.Type)));
                }
            }
            case ObScript.Index:
                return Fail("OBSE array element", "None");
            case Call c:
                return CallCode(c, statement: false);
            case Name n:
                return NameCode(n);
        }
        return Fail("expression", "0");
    }

    static T Numeric(T t) => t is T.Float ? T.Float : T.Int;
    static bool IsForm(T t) => t is T.Form or T.Ref or T.Actor or T.Quest or T.Global or T.Spell;

    /// <summary>
    /// Comparing references: ref == 0 / ref != 0 / ref > 0 ask whether it's set (None in Papyrus); two forms of
    /// different kinds compare as Forms.
    /// </summary>
    Code FormCompare(Code l, string op, Code r)
    {
        if (IsForm(r.Type) && !IsForm(l.Type)) { (l, r) = (r, l); op = op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op }; }
        if (!IsForm(r.Type))
        {
            if (r.Text is not ("0" or "0.0")) return Fail("ref compared with a number", "False");
            return op switch
            {
                "==" or "<=" => new Code($"{Paren(l.Text)} == None", T.Bool),
                "!=" or ">" => new Code($"{Paren(l.Text)} != None", T.Bool),
                _ => Fail("ref compared with a number", "False"),
            };
        }
        if (op is not ("==" or "!=")) return Fail("refs compared by order", "False");
        return new Code(l.Type == r.Type && l.Type != T.Form ? $"{Paren(l.Text)} {op} {Paren(r.Text)}" : $"({Paren(l.Text)} as Form) {op} ({Paren(r.Text)} as Form)", T.Bool);
    }
    static string Num(Code c) => c.Type == T.Bool ? $"({Paren(c.Text)} as Int)" : c.Text;
    static string Truth(Code c) => c.Type is T.Int or T.Float ? $"{Paren(c.Text)} != 0" : c.Text;

    Code Fail(string what, string placeholder)
    {
        Missing(what);
        lineOk = false;
        return new Code(placeholder, T.Unknown);
    }

    void Missing(string what) => missing[what] = missing.GetValueOrDefault(what) + 1;

    Code NameCode(Name n)
    {
        if (n.Target is not null)
        {
            // ref.Function written without arguments (ref.GetDisabled) unless the ref's script has a variable of that name.
            if (functions[n.Id] is not null && !MemberIsVariable(n)) return CallCode(new Call(n.Id, n.Target, []), statement: false);
            return Member(n) is { } slot ? new Code(slot.Read, slot.Type) : Fail(MemberProblem(n), "0");
        }
        if (locals.TryGetValue(n.Id, out var lt)) return lt == T.Unknown ? Fail("OBSE array variable", "None") : new Code(LocalSlot(n.Id, lt).Read, lt);
        if (n.Id.Equals("player", StringComparison.OrdinalIgnoreCase) || n.Id.Equals("playerref", StringComparison.OrdinalIgnoreCase))
            return new Code(PlayerRef(), T.Actor);
        if (world.Find(n.Id) is { } form)
        {
            string prop = FormProperty(n.Id, form.PapyrusType);
            return form.PapyrusType == "GlobalVariable" ? new Code($"{prop}.GetValue()", T.Float) : new Code(prop, FromPapyrus(form.PapyrusType));
        }
        // A function called without arguments (GetDisabled, GetSelf, …).
        if (functions[n.Id] is not null) return CallCode(new Call(n.Id, null, []), statement: false);
        if (n.Id.Equals("__unparsed__", StringComparison.Ordinal)) return Fail("unreadable condition", "False");
        return Fail("unknown name", "None");
    }

    /// <summary>Where a variable lives: how to read it, its type, and how to store a value (already of that type).</summary>
    sealed record Slot(string Read, T Type, Func<string, string> Write);

    /// <summary>
    /// A quest script's number variables are Skyrim globals (OBQV&lt;quest&gt;_&lt;variable&gt;), the same ones the
    /// converted dialogue's conditions and result scripts read and set; everything else is a property of the script.
    /// </summary>
    Slot LocalSlot(string id, T t) => kind == "Quest" && ownQuest is not null && t is T.Int or T.Float
        ? QuestGlobal(ownQuest, id, t)
        : new Slot(Var(id), t, v => $"{Var(id)} = {v}");

    Slot QuestGlobal(string questEditorId, string variable, T t)
    {
        string prop = $"q_{Clean(questEditorId)}_{Clean(variable)}";
        properties.TryAdd(prop, new Property(prop, "GlobalVariable", questEditorId, QuestVariable: variable));
        return t == T.Int
            ? new Slot($"{prop}.GetValueInt()", T.Int, v => $"{prop}.SetValueInt({v})")
            : new Slot($"{prop}.GetValue()", T.Float, v => $"{prop}.SetValue({v})");
    }

    /// <summary>quest.var / ref.var: the variable on that quest's (or reference's base's) script.</summary>
    Slot? Member(Name n)
    {
        if (n.Target is not Name owner || owner.Target is not null) return null;
        if (world.Find(owner.Id) is not { } form || form.ScriptName is null) return null;
        if (form.PapyrusType is not ("Quest" or "ObjectReference" or "Actor")) return null;
        var vars = world.Variables(form.ScriptName);
        if (vars is not null && !vars.ContainsKey(n.Id)) return null;
        T t = vars is not null ? VariableType(vars[n.Id]) : T.Float;
        if (t == T.Unknown) return null;
        if (form.PapyrusType == "Quest" && t is T.Int or T.Float) return QuestGlobal(owner.Id, n.Id, t);
        string prop = FormProperty(owner.Id, form.PapyrusType);
        string access = $"({prop} as {form.ScriptName}).{Var(n.Id)}";
        return new Slot(access, t, v => $"{access} = {v}");
    }

    bool MemberIsVariable(Name n) =>
        n.Target is Name owner && world.Find(owner.Id) is { ScriptName: { } s } && world.Variables(s) is { } vars && vars.ContainsKey(n.Id);

    string MemberProblem(Name n) => n.Target is Name owner && owner.Target is null
        ? world.Find(owner.Id) is not { } form ? (locals.ContainsKey(owner.Id) ? "variable of a script held in a ref variable" : "variable of an unknown form")
          : form.ScriptName is null ? "variable of a form without a script"
          : world.Variables(form.ScriptName) is { } v && !v.ContainsKey(n.Id) ? "variable the other script doesn't declare"
          : "variable of a base object's script"
        : "variable of an expression";

    static T VariableType(string oblivionType) => oblivionType.ToLowerInvariant() switch
    {
        "float" => T.Float, "ref" or "reference" => T.Form, "string_var" => T.String, "array_var" => T.Unknown, _ => T.Int,
    };

    string PlayerRef()
    {
        properties.TryAdd("PlayerRef", new Property("PlayerRef", "Actor", "player"));
        return "PlayerRef";
    }

    string FormProperty(string editorId, string type)
    {
        string name = "f_" + Clean(editorId);
        properties.TryAdd(name, new Property(name, type, editorId));
        return name;
    }

    /// <summary>A record the converter makes for the scripts (message boxes, the fame globals, the functions quest).</summary>
    string Generated(string editorId, string type)
    {
        string name = "t_" + Clean(editorId);
        properties.TryAdd(name, new Property(name, type, editorId, Generated: true));
        return name;
    }

    // ---- calls ----

    /// <summary>The object a call acts on: its explicit reference, else the script's own object.</summary>
    Code Subject(Call c)
    {
        if (c.Target is Name t) return NameCode(t);
        if (c.Target is not null) return Expr(c.Target);
        return kind switch
        {
            "Quest" => Fail("call without a reference in a quest script", "None"),
            "MagicEffect" => new Code("GetTargetActor()", T.Actor),
            "Function" => new Code("akSelf", T.Ref),
            _ => new Code("Self", actor ? T.Actor : T.Ref),
        };
    }

    static string AsRef(Code c) => c.Type is T.Ref or T.Actor ? c.Text : $"({Paren(c.Text)} as ObjectReference)";
    // A script's Self can't be cast across to a sibling type directly (an ObjectReference script to Actor): through its parent.
    static string AsActor(Code c) => c.Type == T.Actor ? c.Text : c.Text == "Self" ? "((Self as ObjectReference) as Actor)" : $"({Paren(c.Text)} as Actor)";

    /// <summary>An argument; a quoted EditorID ("MG03Staff") is the form, as Oblivion reads it.</summary>
    Code Arg(Call c, int i) => i >= c.Args.Count ? new Code("None", T.Unknown)
        : c.Args[i] is Str s && !s.Value.Contains(' ') && world.Find(s.Value) is not null ? Expr(new Name(s.Value)) : Expr(c.Args[i]);
    string ArgNum(Call c, int i, string def = "0") => i < c.Args.Count ? Num(Expr(c.Args[i])) : def;
    /// <summary>A bare word argument (axis, actor value, animation group) as written.</summary>
    static string Word(Call c, int i) => i < c.Args.Count && c.Args[i] is Name n ? n.Id : i < c.Args.Count && c.Args[i] is Str s ? s.Value : "";

    Code CallCode(Call c, bool statement)
    {
        string fn = c.Function.ToLowerInvariant();
        var info = functions[c.Function];
        string canonical = (info?.Name ?? c.Function).ToLowerInvariant();
        string self() => AsRef(Subject(c));
        string actorSelf() => AsActor(Subject(c));
        Code R(string text, T type) => new(text, type);
        switch (canonical)
        {
            // quests and globals
            case "getstage": return R($"{Quest(c, 0)}.GetStage()", T.Int);
            case "setstage": return R($"{Quest(c, 0)}.SetStage({ArgNum(c, 1)})", T.Bool);
            case "getstagedone": return R($"{Quest(c, 0)}.IsStageDone({ArgNum(c, 1)})", T.Bool);
            case "startquest": return R($"{Quest(c, 0)}.Start()", T.Bool);
            case "stopquest": return R($"{Quest(c, 0)}.Stop()", T.Void);
            case "completequest": return R($"{Quest(c, 0)}.CompleteQuest()", T.Void);
            case "getquestrunning": return R($"{Quest(c, 0)}.IsRunning()", T.Bool);
            case "getquestcompleted": return R($"{Quest(c, 0)}.IsCompleted()", T.Bool);
            case "setquestobject": return R("; SetQuestObject: quest items are flagged on the converted item", T.Void);
            case "getglobalvalue": return R($"{FormProperty(Word(c, 0), "GlobalVariable")}.GetValue()", T.Float);
            case "setquestdelay":
                if (c.Args.Count > 0 && c.Args[0] is Name qn && world.Find(qn.Id) is { PapyrusType: "Quest", ScriptName: { } qs })
                    return R($"({FormProperty(qn.Id, "Quest")} as {qs}).QuestDelay = {Convert(Arg(c, 1), T.Float)}", T.Void);
                return R($"; SetQuestDelay on a quest without a script", T.Void);
            // activation and enabling
            case "activate":
                if (c.Args.Count == 0) return R(blockContext == "onactivate" ? $"{self()}.Activate(akActionRef, true)" : $"{self()}.Activate({PlayerRef()}, true)", T.Bool);
                return R($"{self()}.Activate({AsRef(Arg(c, 0))})", T.Bool);
            // The last activator or trigger: akActionRef in those events, remembered for the script's other blocks.
            case "isactionref": usesActionRef = true; return FormCompare(new Code(ActionRef, T.Ref), "==", Arg(c, 0));
            case "getactionref": usesActionRef = true; return R(ActionRef, T.Ref);
            case "enable": return R($"{self()}.Enable()", T.Void);
            case "disable": return R($"{self()}.Disable()", T.Void);
            case "getdisabled": return R($"{self()}.IsDisabled()", T.Bool);
            case "getself": return R(self(), T.Ref);
            case "getisreference": return R($"{self()} == {AsRef(Arg(c, 0))}", T.Bool);
            case "getisid": return R($"{self()}.GetBaseObject() == {Arg(c, 0).Text}", T.Bool);
            case "getparentref": return R($"{self()}.GetEnableParent()", T.Ref);
            case "lock": return R($"{self()}.Lock(true)", T.Void);
            case "unlock": return R($"{self()}.Lock(false)", T.Void);
            case "getlocked": return R($"{self()}.IsLocked()", T.Bool);
            case "getlocklevel": return R($"{self()}.GetLockLevel()", T.Int);
            case "setopenstate": return R($"{self()}.SetOpen({ArgNum(c, 0)} != 0)", T.Void);
            case "getopenstate": return R($"{self()}.GetOpenState()", T.Int);
            case "setdestroyed": return R($"{self()}.SetDestroyed({ArgNum(c, 0)} != 0)", T.Void);
            case "getdestroyed": return Fail("GetDestroyed (Skyrim can set it, not read it)", "False");
            // placement
            case "movetomarker" or "moveto":
                return R($"{self()}.MoveTo({AsRef(Arg(c, 0))}, {ArgNum(c, 1, "0.0")}, {ArgNum(c, 2, "0.0")}, {ArgNum(c, 3, "0.0")})", T.Void);
            case "placeatme": return R($"{self()}.PlaceAtMe({Arg(c, 0).Text}, {ArgNum(c, 1, "1")})", T.Ref);
            case "getdistance": return R($"{self()}.GetDistance({AsRef(Arg(c, 0))})", T.Float);
            case "getincell": return R($"{self()}.GetParentCell() == {FormProperty(Word(c, 0), "Cell")}", T.Bool);
            case "getinsamecell": return R($"{self()}.GetParentCell() == {AsRef(Arg(c, 0))}.GetParentCell()", T.Bool);
            case "getpos": return R($"{self()}.GetPosition{Axis(c)}()", T.Float);
            case "getangle": return R($"{self()}.GetAngle{Axis(c)}()", T.Float);
            case "getstartingpos": return R($"{self()}.GetPosition{Axis(c)}()", T.Float);
            case "setpos":
            {
                string a = Axis(c), s = self();
                string v = ArgNum(c, 1);
                return R($"{s}.SetPosition({(a == "X" ? v : s + ".GetPositionX()")}, {(a == "Y" ? v : s + ".GetPositionY()")}, {(a == "Z" ? v : s + ".GetPositionZ()")})", T.Void);
            }
            case "setangle":
            {
                string a = Axis(c), s = self();
                string v = ArgNum(c, 1);
                return R($"{s}.SetAngle({(a == "X" ? v : s + ".GetAngleX()")}, {(a == "Y" ? v : s + ".GetAngleY()")}, {(a == "Z" ? v : s + ".GetAngleZ()")})", T.Void);
            }
            case "rotate": return R($"{self()}.SetAngle({self()}.GetAngleX(), {self()}.GetAngleY(), {self()}.GetAngleZ() + {ArgNum(c, 1)})", T.Void);
            case "positioncell" or "positionworld" or "posworld":
                return Fail("PositionCell/PositionWorld (coordinates in a named cell)", "None");
            // items and inventory
            case "additem": return R($"{self()}.AddItem({Arg(c, 0).Text}, {ArgNum(c, 1, "1")})", T.Void);
            case "removeitem": return R($"{self()}.RemoveItem({Arg(c, 0).Text}, {ArgNum(c, 1, "1")})", T.Void);
            case "getitemcount": return R($"{self()}.GetItemCount({Arg(c, 0).Text})", T.Int);
            case "removeallitems": return R(c.Args.Count > 0 ? $"{self()}.RemoveAllItems({AsRef(Arg(c, 0))})" : $"{self()}.RemoveAllItems()", T.Void);
            case "equipitem": return R($"{actorSelf()}.EquipItem({Arg(c, 0).Text})", T.Void);
            case "unequipitem": return R($"{actorSelf()}.UnequipItem({Arg(c, 0).Text})", T.Void);
            case "drop": return R($"{self()}.DropObject({Arg(c, 0).Text}, {ArgNum(c, 1, "1")})", T.Ref);
            // spells and magic
            case "addspell": return R($"{actorSelf()}.AddSpell({Spell(c, 0)})", T.Bool);
            case "removespell": return R($"{actorSelf()}.RemoveSpell({Spell(c, 0)})", T.Bool);
            case "cast": return R($"{Spell(c, 0)}.Cast({self()}, {AsRef(Arg(c, 1))})", T.Void);
            case "isspelltarget": return R($"{actorSelf()}.HasSpell({Spell(c, 0)})", T.Bool);
            case "hasmagiceffect": return Fail("HasMagicEffect (by effect code)", "False");
            case "dispel": return R($"{actorSelf()}.DispelSpell({Spell(c, 0)})", T.Bool);
            // actors
            case "getdead": return R($"{actorSelf()}.IsDead()", T.Bool);
            case "kill" or "killactor": return R(c.Args.Count > 0 ? $"{actorSelf()}.Kill({AsActor(Arg(c, 0))})" : $"{actorSelf()}.Kill()", T.Void);
            case "resurrect" or "resurrectactor": return R($"{actorSelf()}.Resurrect()", T.Void);
            case "reset3dstate" or "disablelinkedpathpoints" or "enablelinkedpathpoints" or "trapupdate":
                return R($"; {info?.Name ?? c.Function}: nothing to do in Skyrim", T.Void);
            case "setownership":
                if (c.Args.Count == 0) return R($"{self()}.SetActorOwner({PlayerRef()}.GetActorBase())", T.Void);
                return R(c.Args[0] is Name on && world.Find(on.Id)?.PapyrusType == "Faction" ? $"{self()}.SetFactionOwner({FormProperty(on.Id, "Faction")})" : $"{self()}.SetActorOwner({Paren(Arg(c, 0).Text)} as ActorBase)", T.Void);
            case "disableplayercontrols": return R("Game.DisablePlayerControls()", T.Void);
            case "enableplayercontrols": return R("Game.EnablePlayerControls()", T.Void);
            case "getdeadcount": return R($"({Paren(Arg(c, 0).Text)} as ActorBase).GetDeadCount()", T.Int);
            case "getdetectionlevel": return R($"({AsActor(Arg(c, 0))}.IsDetectedBy({actorSelf()}) as Int) * 3", T.Int);
            case "forceweather": return R($"({Paren(Arg(c, 0).Text)} as Weather).ForceActive(true)", T.Void);
            case "setweather": return R($"({Paren(Arg(c, 0).Text)} as Weather).SetActive(true)", T.Void);
            case "releaseweatheroverride": return R("Weather.ReleaseOverride()", T.Void);
            case "getiscurrentweather": return R($"Weather.GetCurrentWeather() == {Arg(c, 0).Text}", T.Bool);
            case "getpcfame": return R($"{Generated("TMOB_PCFame", "GlobalVariable")}.GetValue()", T.Float);
            case "getpcinfamy": return R($"{Generated("TMOB_PCInfamy", "GlobalVariable")}.GetValue()", T.Float);
            case "modpcfame": return R($"{Generated("TMOB_PCFame", "GlobalVariable")}.Mod({Convert(Arg(c, 0), T.Float)})", T.Float);
            case "modpcinfamy": return R($"{Generated("TMOB_PCInfamy", "GlobalVariable")}.Mod({Convert(Arg(c, 0), T.Float)})", T.Float);
            case "setpcfame": return R($"{Generated("TMOB_PCFame", "GlobalVariable")}.SetValue({Convert(Arg(c, 0), T.Float)})", T.Void);
            case "setpcinfamy": return R($"{Generated("TMOB_PCInfamy", "GlobalVariable")}.SetValue({Convert(Arg(c, 0), T.Float)})", T.Void);
            case "triggerhitshader": return R($"Game.TriggerScreenBlood({(c.Args.Count > 0 ? Convert(Arg(c, 0), T.Int) : "1")})", T.Void);
            case "setfactionreaction": return R($"{FormProperty(Word(c, 0), "Faction")}.SetReaction({FormProperty(Word(c, 1), "Faction")}, {Convert(Arg(c, 2), T.Int)})", T.Void);
            case "setactoralpha": return R($"{actorSelf()}.SetAlpha({Convert(Arg(c, 0), T.Float)})", T.Void);
            case "isswimming": return R($"{actorSelf()}.IsSwimming()", T.Bool);
            case "setignorefriendlyhits": return R($"{self()}.IgnoreFriendlyHits({ArgNum(c, 0)} != 0)", T.Void);
            case "getinworldspace": return R($"{self()}.GetWorldSpace() == {FormProperty(Word(c, 0), "WorldSpace")}", T.Bool);
            case "setcombatstyle": return R($"{actorSelf()}.GetActorBase().SetCombatStyle({FormProperty(Word(c, 0), "CombatStyle")})", T.Void);
            case "setcellpublicflag": return R($"{FormProperty(Word(c, 0), "Cell")}.SetPublic({ArgNum(c, 1)} != 0)", T.Void);
            case "setpcexpelled": return R($"{FormProperty(Word(c, 0), "Faction")}.SetPlayerExpelled({ArgNum(c, 1)} != 0)", T.Void);
            case "getpcexpelled": return R($"{FormProperty(Word(c, 0), "Faction")}.IsPlayerExpelled()", T.Bool);
            case "startcombat": return R($"{actorSelf()}.StartCombat({AsActor(Arg(c, 0))})", T.Void);
            case "stopcombat": return R($"{actorSelf()}.StopCombat()", T.Void);
            case "stopcombatalarmonactor": return R($"{actorSelf()}.StopCombatAlarm()", T.Void);
            case "isincombat" or "getcombattarget" when canonical == "isincombat": return R($"{actorSelf()}.IsInCombat()", T.Bool);
            case "getcombattarget": return R($"{actorSelf()}.GetCombatTarget()", T.Actor);
            case "evaluatepackage" or "evp": return R($"{actorSelf()}.EvaluatePackage()", T.Void);
            case "getiscurrentpackage": return R($"{actorSelf()}.GetCurrentPackage() == {Arg(c, 0).Text}", T.Bool);
            case "getinfaction": return R($"{actorSelf()}.IsInFaction({FormProperty(Word(c, 0), "Faction")})", T.Bool);
            case "getfactionrank": return R($"{actorSelf()}.GetFactionRank({FormProperty(Word(c, 0), "Faction")})", T.Int);
            case "setfactionrank": return R($"{actorSelf()}.SetFactionRank({FormProperty(Word(c, 0), "Faction")}, {ArgNum(c, 1)})", T.Void);
            case "modfactionrank": return R($"{actorSelf()}.ModFactionRank({FormProperty(Word(c, 0), "Faction")}, {ArgNum(c, 1)})", T.Void);
            case "setessential": return R($"({Arg(c, 0).Text} as ActorBase).SetEssential({ArgNum(c, 1)} != 0)", T.Void);
            case "setunconscious": return R($"{actorSelf()}.SetUnconscious({ArgNum(c, 0)} != 0)", T.Void);
            case "setrestrained": return R($"{actorSelf()}.SetRestrained({ArgNum(c, 0)} != 0)", T.Void);
            case "setalert": return R($"{actorSelf()}.SetAlert({ArgNum(c, 0)} != 0)", T.Void);
            case "setghost": return R($"{actorSelf()}.SetGhost({ArgNum(c, 0)} != 0)", T.Void);
            case "setforcerun": return R($"; SetForceRun {ArgNum(c, 0)}: the package decides in Skyrim", T.Void);
            case "setforcesneak": return R($"{actorSelf()}.StartSneaking()", T.Void);
            case "getsitting": return R($"{actorSelf()}.GetSitState()", T.Int);
            case "getsleeping": return R($"{actorSelf()}.GetSleepState()", T.Int);
            case "getdetected": return R($"{AsActor(Arg(c, 0))}.IsDetectedBy({actorSelf()})", T.Bool);
            case "getlos" or "getlineofsight": return R($"{actorSelf()}.HasLOS({AsRef(Arg(c, 0))})", T.Bool);
            case "getunconscious": return R($"{actorSelf()}.IsUnconscious()", T.Bool);
            case "getweaponanimtype" or "isweaponout": return R($"{actorSelf()}.IsWeaponDrawn()", T.Bool);
            case "getisrace": return R($"{actorSelf()}.GetRace() == {FormProperty(Word(c, 0), "Race")}", T.Bool);
            case "getissex": return R($"{actorSelf()}.GetActorBase().GetSex() == {(Word(c, 0).StartsWith("F", StringComparison.OrdinalIgnoreCase) ? 1 : 0)}", T.Bool);
            case "getlevel": return R($"{actorSelf()}.GetLevel()", T.Int);
            // Disposition towards the player (Skyrim.Disposition: Variable09 holds it minus 50, kept within 0-100).
            case "moddisposition" when IsPlayer(c, 0):
                usesDisposition = true;
                return R($"TMModDisposition({actorSelf()}, {Convert(Arg(c, 1), T.Float)})", T.Void);
            case "getdisposition" when IsPlayer(c, 0):
                return R($"(50.0 + {actorSelf()}.GetActorValue(\"Variable09\")) as Int", T.Int);
            case "moddisposition" or "getdisposition":
                return Fail("disposition between two NPCs", "0");
            // Oblivion's Say returns how long the line lasts (scripts wait on it); Skyrim's returns nothing, so a value is
            // estimated by SayTimed.
            case "startconversation" when c.Args.Count < 2:
                // Without a topic it's the greeting: with the player, activating the speaker opens the dialogue.
                return c.Args.Count == 1 && c.Args[0] is Name pn && pn.Id.Equals("player", StringComparison.OrdinalIgnoreCase) && statement
                    ? R($"{self()}.Activate({PlayerRef()})", T.Void)
                    : Fail("StartConversation between NPCs", "None");
            case "say" or "sayto" or "startconversation":
            {
                if (Word(c, canonical == "say" ? 0 : 1).Length == 0) return Fail($"{c.Function} without a topic", "None");
                string topic = FormProperty(Word(c, canonical == "say" ? 0 : 1), "Topic");
                if (statement) return R($"{self()}.Say({topic})", T.Void);
                usesSay = true;
                return R($"SayTimed({self()}, {topic})", T.Float);
            }
            case "issneaking": return R($"{actorSelf()}.IsSneaking()", T.Bool);
            case "getequipped": return R($"{actorSelf()}.IsEquipped({Arg(c, 0).Text})", T.Bool);
            case "setscale": return R($"{self()}.SetScale({Convert(Arg(c, 0), T.Float)})", T.Void);
            case "getscale": return R($"{self()}.GetScale()", T.Float);
            case "isininterior": return R($"{self()}.IsInInterior()", T.Bool);
            case "israining": return R("Weather.GetCurrentWeather().GetClassification() == 2", T.Bool);
            case "issnowing": return R("Weather.GetCurrentWeather().GetClassification() == 3", T.Bool);
            case "getpcisrace": return R($"{PlayerRef()}.GetRace() == {FormProperty(Word(c, 0), "Race")}", T.Bool);
            case "getpcissex": return R($"{PlayerRef()}.GetActorBase().GetSex() == {(Word(c, 0).StartsWith("F", StringComparison.OrdinalIgnoreCase) ? 1 : 0)}", T.Bool);
            case "resethealth": return R($"{actorSelf()}.RestoreActorValue(\"Health\", 100000.0)", T.Void);
            case "clearownership": return R($"{self()}.SetActorOwner(None)", T.Void);
            case "look" or "stoplook" when canonical == "look": return R($"{actorSelf()}.SetLookAt({AsRef(Arg(c, 0))})", T.Void);
            case "stoplook": return R($"{actorSelf()}.ClearLookAt()", T.Void);
            case "pushactoraway": return R($"{AsActor(Arg(c, 0))}.PushActorAway({actorSelf()}, {ArgNum(c, 1)})", T.Void);
            // actor values
            case "getactorvalue" or "getav": return R($"{actorSelf()}.GetActorValue(\"{AV(Word(c, 0))}\")", T.Float);
            case "getbaseactorvalue" or "getbaseav": return R($"{actorSelf()}.GetBaseActorValue(\"{AV(Word(c, 0))}\")", T.Float);
            case "setactorvalue" or "setav": return R($"{actorSelf()}.SetActorValue(\"{AV(Word(c, 0))}\", {ArgNum(c, 1)})", T.Void);
            case "modactorvalue" or "modav": return R($"{actorSelf()}.ModActorValue(\"{AV(Word(c, 0))}\", {ArgNum(c, 1)})", T.Void);
            case "forceactorvalue" or "forceav": return R($"{actorSelf()}.ForceActorValue(\"{AV(Word(c, 0))}\", {ArgNum(c, 1)})", T.Void);
            // sound, visuals, animation
            case "playsound" or "playsound3d": return R($"{FormProperty(Word(c, 0), "Sound")}.Play({self()})", T.Int);
            case "playmagicshadervisuals" or "pms": return R($"{FormProperty(Word(c, 0), "EffectShader")}.Play({self()}, {ArgNum(c, 1, "-1.0")})", T.Void);
            case "stopmagicshadervisuals" or "sms": return R($"{FormProperty(Word(c, 0), "EffectShader")}.Stop({self()})", T.Void);
            case "playgroup" or "loopgroup": return R($"; {c.Function} {Word(c, 0)}: Oblivion animation groups have no Skyrim counterpart", T.Void);
            case "pickidle": return R($"; PickIdle: idles are chosen by Skyrim's idle system", T.Void);
            case "playbink": return R($"Game.PlayBink(\"{Word(c, 0)}\")", T.Void);
            case "fadetoblack" or "imod": return R("Game.FadeOutGame(true, true, 0.5, 1.0)", T.Void);
            // messages
            case "message": return R($"Debug.Notification({Format(c)})", T.Void);
            case "messagebox":
                if (c.Args.Count(a => a is Str) > 1) return MessageWithButtons(c);
                return R($"Debug.MessageBox({Format(c)})", T.Void);
            case "getbuttonpressed": usesButtons = true; return R("TakeButton()", T.Int);
            case "printc" or "printtoconsole" or "print": return R($"Debug.Trace({Format(c)})", T.Void);
            case "showmap": return R($"{AsRef(Arg(c, 0))}.AddToMap({ArgNum(c, 1)} != 0)", T.Void);
            case "addtopic": return R($"; AddTopic {Word(c, 0)}: Skyrim topics need no unlocking", T.Void);
            // time and chance
            case "getrandompercent": return R("Utility.RandomInt(0, 99)", T.Int);
            case "rand": return R($"Utility.RandomFloat({ArgNum(c, 0)}, {ArgNum(c, 1)})", T.Float);
            case "getsecondspassed": usesSecondsPassed = true; return R("SecondsPassed()", T.Float);
            case "getcurrenttime": return R("(Game.GetFormFromFile(0x38, \"Skyrim.esm\") as GlobalVariable).GetValue()", T.Float);
            case "getdayofweek": return R("((Utility.GetCurrentGameTime() as Int) % 7)", T.Int);
            case "getgamesetting": return R($"Game.GetGameSettingFloat(\"{Word(c, 0)}\")", T.Float);
            // OBSE (onto SKSE)
            case "iskeypressed" or "iskeypressed2" or "iskeypressed3": return R($"Input.IsKeyPressed({ArgNum(c, 0)})", T.Bool);
            case "iscontrolpressed": return R($"Input.IsKeyPressed(Input.GetMappedKey(\"{ControlName(ArgNum(c, 0))}\"))", T.Bool);
            case "getname": return R(c.Args.Count > 0 ? $"{Arg(c, 0).Text}.GetName()" : $"{self()}.GetDisplayName()", T.String);
            case "setname": return R($"{self()}.GetBaseObject().SetName({Arg(c, 0).Text})", T.Void);
            case "getbaseobject": return R($"{self()}.GetBaseObject()", T.Form);
            case "getparentcell": return R($"{self()}.GetParentCell()", T.Form);
            case "getnumitems": return R($"{self()}.GetNumItems()", T.Int);
            case "getinventoryobject": return R($"{self()}.GetNthForm({ArgNum(c, 0)})", T.Form);
            case "getweight": return R($"{Arg(c, 0).Text}.GetWeight()", T.Float);
            case "getgoldvalue": return R($"{Arg(c, 0).Text}.GetGoldValue()", T.Int);
            case "getequippedobject": return EquippedObject(c);
            case "isplayable": return R($"{Arg(c, 0).Text}.IsPlayable()", T.Bool);
            case "getgameloaded" or "getgamerestarted": return R("False   ; handled by an OnPlayerLoadGame event in Skyrim", T.Bool);
            case "getcrosshairref": return R("Game.GetCurrentCrosshairRef()", T.Ref);
            case "getactivemenumode" or "menumode": return R("Utility.IsInMenuMode()", T.Bool);
            case "con_tfc" or "con_tgm" or "con_tcl" or "con_save" or "con_setcamerafov" or "con_toggleai": return R($"; console {c.Function}: not available to Papyrus", T.Void);
            case "setfunctionvalue":
                if (kind == "Function") { var v = Arg(c, 0); return R($"__result = {Convert(v, T.Float)}", T.Void); }
                return R("; SetFunctionValue outside a function", T.Void);
            case "call":
                return UserCall(c);
            case "messageboxex" or "messageex":
                if (Format(c) is var f && c.Function.Equals("messageex", StringComparison.OrdinalIgnoreCase)) return R($"Debug.Notification({f})", T.Void);
                return Fail("MessageBoxEx with buttons", "None");
            case "sv_construct": return R(Format(c), T.String);
            case "sv_length": return R($"StringUtil.GetLength({Arg(c, 0).Text})", T.Int);
            case "sv_find": return R($"StringUtil.Find({Arg(c, 1).Text}, {Arg(c, 0).Text})", T.Int);
            case "sv_compare" or "stringcompare": return R($"({Arg(c, 0).Text} == {Arg(c, 1).Text}) as Int", T.Int);
            case "sv_destruct": return R("; sv_Destruct: Papyrus strings need no freeing", T.Void);
        }
        if (info is null) return Fail($"{c.Function} (unknown)", "None");
        return Fail($"{info.Name} ({info.Source})", "None");
    }

    string Quest(Call c, int i) => c.Args.Count > i && c.Args[i] is Name n && world.Find(n.Id) is { PapyrusType: "Quest" } ? FormProperty(n.Id, "Quest") : $"({Arg(c, i).Text} as Quest)";
    string Spell(Call c, int i) => c.Args.Count > i && c.Args[i] is Name n && world.Find(n.Id) is { } f ? $"({FormProperty(n.Id, f.PapyrusType)} as Spell)" : $"({Arg(c, i).Text} as Spell)";
    static string Axis(Call c) => Word(c, 0).ToUpperInvariant() is "X" or "Y" or "Z" ? Word(c, 0).ToUpperInvariant() : "Z";

    Code EquippedObject(Call c)
    {
        // OBSE slots: 16 weapon (and 13/14 for others in Oblivion's numbering), otherwise an armor slot.
        string slot = ArgNum(c, 0);
        string a = AsActor(Subject(c));
        return slot switch
        {
            "16" => new Code($"{a}.GetEquippedWeapon()", T.Form),
            "17" => new Code($"{a}.GetEquippedWeapon(true)", T.Form),
            _ => new Code($"{a}.GetWornForm(Armor.GetMaskForSlot({30 + int.Parse(slot, CultureInfo.InvariantCulture) % 32}))", T.Form),
        };
    }

    Code UserCall(Call c)
    {
        if (c.Args.FirstOrDefault() is not Name fnName) return Fail("Call of a function held in a variable", "0");
        string script = world.ScriptName(fnName.Id);
        var args = c.Args.Skip(1).Select(a => Expr(a).Text);
        string self = c.Target is not null ? AsRef(Subject(c)) : kind is "Object" ? "Self" : kind == "Function" ? "akSelf" : "None";
        string fnQuest = Generated("TMObFunctions", "Quest");
        return new Code($"({fnQuest} as {script}).Run({self}{string.Concat(args.Select(a => ", " + a))})", T.Float);
    }

    /// <summary>A message's text with its values: Oblivion's printf-style %.0f / %g / %s (OBSE) filled in.</summary>
    string Format(Call c)
    {
        if (c.Args.Count == 0) return "\"\"";
        if (c.Args[0] is not Str s) return Expr(c.Args[0]).Text;
        var parts = new List<string>();
        var text = new StringBuilder();
        int argIndex = 1;
        for (int i = 0; i < s.Value.Length; i++)
        {
            char ch = s.Value[i];
            if (ch == '%' && i + 1 < s.Value.Length)
            {
                if (s.Value[i + 1] == '%') { text.Append('%'); i++; continue; }
                int j = i + 1;
                while (j < s.Value.Length && (char.IsDigit(s.Value[j]) || s.Value[j] is '.' or '-' or '+')) j++;
                if (j < s.Value.Length && "fgdisrnz".Contains(char.ToLowerInvariant(s.Value[j])))
                {
                    char spec = char.ToLowerInvariant(s.Value[j]);
                    string flags = s.Value[(i + 1)..j];
                    if (text.Length > 0) { parts.Add("\"" + Escape(text.ToString()) + "\""); text.Clear(); }
                    if (argIndex < c.Args.Count)
                    {
                        var v = Expr(c.Args[argIndex++]);
                        parts.Add(IsForm(v.Type) || v.Type == T.String ? v.Text : spec is 'f' && flags.EndsWith(".0") || spec is 'd' or 'i' ? $"({Paren(Num(v))} as Int)" : v.Text);
                    }
                    i = j;
                    continue;
                }
            }
            text.Append(ch);
        }
        if (text.Length > 0 || parts.Count == 0) parts.Add("\"" + Escape(text.ToString()) + "\"");
        return string.Join(" + ", parts);
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// MessageBox "text %.0f" value "Button 1" "Button 2": a Message record with those buttons, shown with the values
    /// (Skyrim formats %.Nf and %g from Show's arguments). Show waits for the choice, which GetButtonPressed then returns
    /// once, as Oblivion's does on the frames after the box closes.
    /// </summary>
    Code MessageWithButtons(Call c)
    {
        if (c.Args.Count == 0 || c.Args[0] is not Str text) return Fail("MessageBox with buttons and computed text", "None");
        var values = c.Args.Skip(1).Where(a => a is not Str).ToList();
        var buttons = c.Args.Skip(1).OfType<Str>().Select(s => s.Value).ToList();
        if (values.Count > 9 || text.Value.Contains("%s", StringComparison.OrdinalIgnoreCase) || text.Value.Contains("%n", StringComparison.OrdinalIgnoreCase))
            return Fail("MessageBox with buttons and names in the text", "None");
        string id = $"TMOB_{Clean(scriptId)}_Msg{messages.Count}";
        messages.Add(new MessageForm(id, text.Value, buttons));
        usesButtons = true;
        var args = values.Select(v => Convert(Expr(v), T.Float));
        return new Code($"__button = {Generated(id, "Message")}.Show({string.Join(", ", args)})", T.Void);
    }

    /// <summary>Oblivion actor values by Skyrim's names (where they differ).</summary>
    static string AV(string av) => av.ToLowerInvariant() switch
    {
        "fatigue" => "Stamina",
        "magicka" => "Magicka",
        "health" => "Health",
        "aggression" => "Aggression",
        "confidence" => "Confidence",
        "responsibility" => "Morality",
        "energy" => "Energy",
        "blade" => "OneHanded",
        "blunt" => "TwoHanded",
        "handtohand" => "UnarmedDamage",
        "armorer" => "Smithing",
        "athletics" => "SpeedMult",
        "acrobatics" => "JumpingBonus",
        "marksman" => "Marksman",
        "mercantile" => "Speechcraft",
        "speechcraft" => "Speechcraft",
        "security" => "Lockpicking",
        "sneak" => "Sneak",
        "heavyarmor" => "HeavyArmor",
        "lightarmor" => "LightArmor",
        "block" => "Block",
        "alchemy" => "Alchemy",
        "alteration" => "Alteration",
        "conjuration" => "Conjuration",
        "destruction" => "Destruction",
        "illusion" => "Illusion",
        "mysticism" => "Alteration",
        "restoration" => "Restoration",
        _ => av,
    };

    /// <summary>Oblivion's control numbers (IsControlPressed) by Skyrim's control names.</summary>
    static string ControlName(string n) => n switch
    {
        "0" => "Forward", "1" => "Back", "2" => "Strafe Left", "3" => "Strafe Right", "4" => "Right Attack/Block", "5" => "Activate",
        "6" => "Left Attack/Block", "7" => "Ready Weapon", "8" => "Sprint", "9" => "Sneak", "10" => "Jump", "11" => "Toggle POV",
        "12" => "Tween Menu", "13" => "Auto-Move", "14" => "Quick Inventory", "15" => "Wait", "16" => "Journal", _ => "Activate",
    };
}
