using System.Globalization;
using System.Text;
using static TamrielMerge.Morrowind.MwScript;

namespace TamrielMerge.Morrowind;

/// <summary>Statements, expressions, variables and properties of a translated script.</summary>
public sealed partial class MwPapyrus
{
    // ---- statements ----

    void WriteStatements(StringBuilder sb, List<Stmt> body, int depth)
    {
        string ind = new(' ', depth * 2);
        // A statement that doesn't translate is kept as a comment (its draft translation) so the script still compiles;
        // a condition that doesn't is False, so its branch never runs.
        string Line(string code) => lineOk ? code.Replace("\n", "\n" + ind) : $"; TODO(Morrowind) [{lastFailure}]: " + code.Replace("\n", " ");
        string Cond(Expr cond)
        {
            lineOk = true;
            string c = Condition(cond);
            if (lineOk) { translated++; return c; }
            return $"False   ; TODO(Morrowind) [{lastFailure}]: " + c.Replace("\n", " ");
        }
        foreach (var s in body)
        {
            if (s is Return { Line: < 0 }) continue;   // a stray endif, which Morrowind ignores
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
                case CallStmt c:
                {
                    var code = CallCode(c.Call, statement: true);
                    if (code.Text.Length > 0) sb.AppendLine(ind + Line(code.Text));
                    break;
                }
                case Return:
                    sb.AppendLine(ind + "Return");
                    break;
                case NoOp n:
                    sb.AppendLine(ind + $"; Morrowind's compiler ignores this line (a condition without its if): {n.Text}");
                    break;
                case Unparsed u:
                    Missing("unreadable statement");
                    sb.AppendLine(ind + $"; unreadable in the original ({u.Reason}): {u.Text}");
                    lineOk = false;
                    break;
            }
            if (lineOk) translated++;
        }
    }

    string Assignment(Set set)
    {
        var value = Expr(set.Value);
        if (Slot(set.Target) is not { } slot) return Fail(SlotProblem(set.Target), "None").Text + " = " + value.Text;
        return slot.Write(Convert(value, slot.Type));
    }

    static T Arith(T a, T b) => a == T.Float || b == T.Float ? T.Float : T.Int;

    string Condition(Expr e)
    {
        var c = Expr(e);
        return c.Type switch
        {
            T.Bool => c.Text,
            T.Int or T.Float => $"{Paren(c.Text)} != 0",
            T.Void => Fail("a value of a function that returns none", "False").Text,
            _ => c.Text,
        };
    }

    static string Paren(string s) => s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.') || IsCallText(s) ? s : $"({s})";

    /// <summary>A plain call or member chain (a.b(c).d()), which needs no parentheses around it.</summary>
    static bool IsCallText(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '"') { i = s.IndexOf('"', i + 1); if (i < 0) return false; continue; }
            if (ch == '(') depth++;
            else if (ch == ')') { depth--; if (depth < 0) return false; }
            else if (depth == 0 && !(char.IsLetterOrDigit(ch) || ch is '_' or '.')) return false;
        }
        return depth == 0 && s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.EndsWith(')');
    }

    /// <summary>A value as the given type: numbers between Int and Float, Bool as a number.</summary>
    string Convert(Code c, T to)
    {
        if (c.Type == to) return c.Text;
        if (c.Type == T.Unknown) return c.Text;
        if (c.Type == T.Void) return Fail("a value of a function that returns none", to is T.Int ? "0" : to is T.Float ? "0.0" : "None").Text;
        return (to, c.Type) switch
        {
            (T.Int, T.Float) => $"{Paren(c.Text)} as Int",
            (T.Int, T.Bool) => $"{Paren(c.Text)} as Int",
            (T.Float, T.Bool) => $"({Paren(c.Text)} as Int) as Float",
            (T.Float, T.Int) => c.Text,
            (T.Bool, T.Int or T.Float) => $"{Paren(c.Text)} != 0",
            (T.String, _) => $"{Paren(c.Text)} as String",
            (T.Int or T.Float, T.Form or T.Ref or T.Actor) => Fail("a reference used as a number", "0").Text,
            (T.Actor, T.Ref or T.Form) => $"{Paren(c.Text)} as Actor",
            (T.Ref, T.Form) => $"{Paren(c.Text)} as ObjectReference",
            _ => c.Text,
        };
    }

    // ---- expressions ----

    Code Expr(Expr e)
    {
        switch (e)
        {
            case Num n:
                return n.Value == Math.Floor(n.Value) && !n.Text.Contains('.') && Math.Abs(n.Value) < int.MaxValue
                    ? new Code(((long)n.Value).ToString(CultureInfo.InvariantCulture), T.Int)
                    : new Code(n.Value.ToString("0.0#####", CultureInfo.InvariantCulture), T.Float);
            case Str s:
                // A quoted name used as a value: a local, global or ID written in quotes.
                return NameCode(new Name(s.Value));
            case Unary u:
            {
                var o = Expr(u.Operand);
                return new Code("-" + Paren(Num(o)), o.Type == T.Float ? T.Float : T.Int);
            }
            case Binary b:
            {
                var l = Expr(b.Left); var r = Expr(b.Right);
                switch (b.Op)
                {
                    case "&&" or "||":
                        return new Code($"{Paren(Truth(l))} {b.Op} {Paren(Truth(r))}", T.Bool);
                    case "==" or "!=" or "<" or "<=" or ">" or ">=":
                        return Compare(l, b.Op, r);
                    default:
                        if (IsForm(l.Type) || IsForm(r.Type)) return Fail("arithmetic on a reference", "0");
                        return new Code($"{Paren(Num(l))} {b.Op} {Paren(Num(r))}", b.Op == "/" ? T.Float : Arith(Numeric(l.Type), Numeric(r.Type)));
                }
            }
            case Call c:
                return CallCode(c, statement: false);
            case Name n:
                return NameCode(n);
        }
        return Fail("expression", "0");
    }

    /// <summary>A comparison; a Bool against 0 or 1 reads as itself or its negation.</summary>
    Code Compare(Code l, string op, Code r)
    {
        if (IsForm(l.Type) || IsForm(r.Type)) return Fail("a reference compared", "False");
        if (l.Type == T.Bool && r.Text is "0" or "1" && op is "==" or "!=" or ">" or ">=" or "<" or "<=")
        {
            bool truth = (op, r.Text) switch
            {
                ("==", "1") or ("!=", "0") or (">", "0") or (">=", "1") => true,
                ("==", "0") or ("!=", "1") or ("<", "1") or ("<=", "0") => false,
                _ => true,
            };
            if (op is ">=" && r.Text == "0" || op is "<=" && r.Text == "1") return new Code("True", T.Bool);
            if (op is "<" && r.Text == "0" || op is ">" && r.Text == "1") return new Code("False", T.Bool);
            return new Code(truth ? l.Text : "!" + Paren(l.Text), T.Bool);
        }
        return new Code($"{Paren(Num(l))} {op} {Paren(Num(r))}", T.Bool);
    }

    static T Numeric(T t) => t is T.Float ? T.Float : T.Int;
    static bool IsForm(T t) => t is T.Form or T.Ref or T.Actor;
    static string Num(Code c) => c.Type == T.Bool ? $"({Paren(c.Text)} as Int)" : c.Text;
    static string Truth(Code c) => c.Type is T.Int or T.Float ? $"{Paren(c.Text)} != 0" : c.Text;

    Code Fail(string what, string placeholder)
    {
        Missing(what);
        lineOk = false;
        return new Code(placeholder, T.Unknown);
    }

    void Missing(string what) { missing[what] = missing.GetValueOrDefault(what) + 1; lastFailure = what; }
    string lastFailure = "";

    Code NameCode(Name n)
    {
        if (n.Id.StartsWith("__unparsed__", StringComparison.Ordinal)) return Fail("unreadable condition", "False");
        if (n.Owner is null && n.Id.Equals("player", StringComparison.OrdinalIgnoreCase)) return new Code(PlayerRef(), T.Actor);
        if (Slot(n) is { } slot) return new Code(slot.Read, slot.Type);
        // A function written without arguments where the parser took it for a name.
        if (n.Owner is null && MwFunctions.Find(n.Id) is not null) return CallCode(new Call(n.Id, null, []), statement: false);
        return Fail(SlotProblem(n), "0");
    }

    /// <summary>Where a variable lives: how to read it, its type, and how to store a value (already of that type).</summary>
    sealed record VarSlot(string Read, T Type, Func<string, string> Write);

    /// <summary>
    /// A variable: the script's own local; a global (Skyrim's own for the time of day and the calendar); another
    /// object's local (ID.variable, through the script its base carries, or a global script's); in a result script, a
    /// local of the speaker's script.
    /// </summary>
    VarSlot? Slot(Name n)
    {
        if (n.Owner is null)
        {
            if (kind != Kind.Result && locals.TryGetValue(n.Id, out var lt))
                return new VarSlot(Var(n.Id), lt, v => $"{Var(n.Id)} = {v}");
            if (world.IsGlobalVariable(n.Id))
            {
                string g = GlobalProperty(n.Id);
                return new VarSlot($"{g}.GetValue()", T.Float, v => $"{g}.SetValue({v})");
            }
            if (kind == Kind.Result) return SpeakerLocal(n.Id);
            // A name the script uses without declaring it (its declaration lost before the game shipped; the compiled
            // script had it): a local of its own.
            if (world.TypeOf(n.Id) is null && !world.IsScript(n.Id) && !n.Id.Equals("player", StringComparison.OrdinalIgnoreCase) && MwFunctions.Find(n.Id) is null)
            {
                locals[n.Id] = T.Float;
                return new VarSlot(Var(n.Id), T.Float, v => $"{Var(n.Id)} = {v}");
            }
            return null;
        }
        // owner.variable
        string owner = n.Owner;
        if (world.IsScript(owner) && world.IsGlobalScript(owner) && world.Variables(owner) is { } gv && gv.TryGetValue(n.Id, out var gt))
        {
            string q = ScriptQuest(owner);
            string access = $"({q} as {world.GlobalScriptName(owner)}).{Var(n.Id)}";
            return new VarSlot(access, LocalType(gt), v => $"{access} = {v}");
        }
        if (world.ScriptOf(owner) is not { } script || world.Variables(script) is not { } vars || !vars.TryGetValue(n.Id, out var type)) return null;
        string r = RefProperty(owner);
        string member = $"({r} as {world.ObjectScriptName(script)}).{Var(n.Id)}";
        return new VarSlot(member, LocalType(type), v => $"{member} = {v}");
    }

    static T LocalType(string mwType) => mwType.Equals("float", StringComparison.OrdinalIgnoreCase) ? T.Float : T.Int;

    string SlotProblem(Name n)
    {
        if (n.Owner is null) return kind == Kind.Result ? "local the speaker's script doesn't declare" : "unknown name";
        if (world.TypeOf(n.Owner) is null) return "variable of an unknown ID";
        if (world.ScriptOf(n.Owner) is null) return "variable of an object without a script";
        return "variable the object's script doesn't declare";
    }

    // ---- a result script's speaker locals ----

    readonly Dictionary<string, (T Type, List<string> Scripts)> speakerLocals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A name a result script uses without declaring it: a local of the speaker's script. With one speaker, its script;
    /// otherwise every script on people that declares it, tried in turn.
    /// </summary>
    VarSlot? SpeakerLocal(string variable)
    {
        List<string> scripts;
        if (ctx.SpeakerScript is { } own)
        {
            if (world.Variables(own) is not { } v || !v.ContainsKey(variable)) return null;
            scripts = [own];
        }
        else scripts = world.ScriptsDeclaring(variable).ToList();
        if (scripts.Count == 0) return null;
        var types = scripts.Select(s => LocalType(world.Variables(s)![variable])).Distinct().ToList();
        T t = types.Count == 1 ? types[0] : T.Float;
        if (scripts.Count == 1)
        {
            string access = $"(akSpeakerRef as {world.ObjectScriptName(scripts[0])}).{Var(variable)}";
            return new VarSlot(access, t, v => $"{access} = {v}");
        }
        speakerLocals[variable] = (t, scripts);
        string fn = "MwSpeaker_" + Clean(variable);
        return new VarSlot($"Get{fn}(akSpeakerRef)", t, v => $"Set{fn}(akSpeakerRef, {v})");
    }

    void WriteSpeakerLocals(StringBuilder sb)
    {
        foreach (var (variable, (t, scripts)) in speakerLocals)
        {
            string pt = PapyrusType(t), fn = "MwSpeaker_" + Clean(variable);
            sb.AppendLine($"; The speaker's {variable}: whichever of its scripts declares it.");
            sb.AppendLine($"{pt} Function Get{fn}(ObjectReference akSpeaker)");
            foreach (var s in scripts)
            {
                string cls = world.ObjectScriptName(s);
                sb.AppendLine($"  {cls} s{Clean(cls)} = akSpeaker as {cls}");
                sb.AppendLine($"  If s{Clean(cls)}");
                sb.AppendLine($"    Return s{Clean(cls)}.{Var(variable)}{(LocalType(world.Variables(s)![variable]) != t ? " as " + pt : "")}");
                sb.AppendLine("  EndIf");
            }
            sb.AppendLine($"  Return 0");
            sb.AppendLine("EndFunction");
            sb.AppendLine();
            sb.AppendLine($"Function Set{fn}(ObjectReference akSpeaker, {pt} value)");
            foreach (var s in scripts)
            {
                string cls = world.ObjectScriptName(s);
                sb.AppendLine($"  {cls} s{Clean(cls)} = akSpeaker as {cls}");
                sb.AppendLine($"  If s{Clean(cls)}");
                sb.AppendLine($"    s{Clean(cls)}.{Var(variable)} = value{(LocalType(world.Variables(s)![variable]) != t ? " as " + PapyrusType(LocalType(world.Variables(s)![variable])) : "")}");
                sb.AppendLine("  EndIf");
            }
            sb.AppendLine("EndFunction");
            sb.AppendLine();
        }
    }

    // ---- properties ----

    /// <summary>A property filled by the converter ("kind:id"), named prefix + the cleaned ID (unique per fill).</summary>
    string Prop(string fill, string type, string name)
    {
        if (propertyByFill.TryGetValue(fill, out var existing)) return existing;
        string unique = name;
        for (int n = 2; properties.ContainsKey(unique); n++) unique = name + "_" + n;
        properties[unique] = new Property(unique, type, fill);
        propertyByFill[fill] = unique;
        return unique;
    }

    string PlayerRef() => Prop("player", "Actor", "PlayerRef");

    /// <summary>A record the converter makes for the scripts (message boxes, Say lines, anchors, mirror factions...).</summary>
    string Generated(string fill, string type, string name) => Prop(fill, type, name);

    string GlobalProperty(string id) => id.ToLowerInvariant() switch
    {
        // Morrowind's clock and calendar are Skyrim's own.
        "gamehour" => Prop("vanilla:GameHour", "GlobalVariable", "GameHour"),
        "day" => Prop("vanilla:GameDay", "GlobalVariable", "GameDay"),
        "month" => Prop("vanilla:GameMonth", "GlobalVariable", "GameMonth"),
        "year" => Prop("vanilla:GameYear", "GlobalVariable", "GameYear"),
        "dayspassed" => Prop("vanilla:GameDaysPassed", "GlobalVariable", "GameDaysPassed"),
        "timescale" => Prop("vanilla:TimeScale", "GlobalVariable", "TimeScale"),
        _ => Prop("global:" + id, "GlobalVariable", "g_" + Clean(id)),
    };

    /// <summary>The reference of an object ID (persistent in the plugin): an Actor for people and creatures.</summary>
    Code RefCode(string id)
    {
        if (id.Equals("player", StringComparison.OrdinalIgnoreCase) || id.Equals("playerref", StringComparison.OrdinalIgnoreCase)) return new Code(PlayerRef(), T.Actor);
        bool actor = world.TypeOf(id) is "NPC_" or "CREA";
        return new Code(RefProperty(id), actor ? T.Actor : T.Ref);
    }

    string RefProperty(string id)
    {
        bool actor = world.TypeOf(id) is "NPC_" or "CREA";
        return Prop("ref:" + id, actor ? "Actor" : "ObjectReference", "r_" + Clean(id));
    }

    string BaseProperty(string id) => Prop("base:" + id, "Form", "f_" + Clean(id));
    string ScriptQuest(string scriptId) => Prop("script:" + scriptId, "Quest", "s_" + Clean(scriptId));
}
