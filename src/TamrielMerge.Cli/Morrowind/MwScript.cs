namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's scripting language (MWScript), parsed from a script's source text (SCPT's SCTX, or a dialogue response's
/// result, BNAM) into a tree the Papyrus translator (<see cref="MwPapyrus"/>) works from:
/// <list type="bullet">
/// <item>a script is one block, "Begin Name … End [Name]", run every frame; a result script has no Begin/End;</item>
/// <item>variables: short / long / float name, declared anywhere;</item>
/// <item>statements: set target to expr; if / elseif / else / endif; while / endwhile; return; and calls:
/// [ref->]Function args… where ref is an ID (quoted when it has spaces), and arguments are separated by spaces or
/// commas;</item>
/// <item>expressions: comparisons (== != &lt; &lt;= &gt; &gt;=, and = as ==), + - * /, unary -, parentheses, numbers,
/// strings, names, ref.variable (another object's local) and calls whose arguments follow without parentheses (as many
/// as the function takes, see <see cref="MwFunctions"/>; unknown functions take primaries up to an operator).</item>
/// </list>
/// Statements are one per line; ';' starts a comment. The original compiler is lenient (stray words after a statement,
/// "else if", "end if"), and so is this parser.
/// </summary>
public static class MwScript
{
    // ---- tree ----

    public abstract record Expr;
    public sealed record Num(double Value, string Text) : Expr;
    public sealed record Str(string Value) : Expr;
    /// <summary>A name: a local, a global, an ID, or (Owner set) another object's local, owner.variable.</summary>
    public sealed record Name(string Id, string? Owner = null) : Expr;
    /// <summary>A call: [Target->]Function args. Target is the reference's ID as written (player, "Some ID").</summary>
    public sealed record Call(string Function, string? Target, List<Expr> Args) : Expr;
    public sealed record Unary(string Op, Expr Operand) : Expr;
    public sealed record Binary(string Op, Expr Left, Expr Right) : Expr;

    public abstract record Stmt(int Line);
    public sealed record Set(int Line, Name Target, Expr Value) : Stmt(Line);
    public sealed record If(int Line, List<(Expr? Condition, List<Stmt> Body)> Branches) : Stmt(Line);
    public sealed record While(int Line, Expr Condition, List<Stmt> Body) : Stmt(Line);
    public sealed record CallStmt(int Line, Call Call) : Stmt(Line);
    public sealed record Return(int Line) : Stmt(Line);
    public sealed record Unparsed(int Line, string Text, string Reason) : Stmt(Line);
    /// <summary>A line Morrowind's compiler accepts and does nothing with (a condition without its "if").</summary>
    public sealed record NoOp(int Line, string Text) : Stmt(Line);

    public sealed record Variable(string Type, string Name);
    public sealed record Script(string Name, List<Variable> Variables, List<Stmt> Body)
    {
        public IEnumerable<Stmt> AllStatements() => Walk(Body);
        public static IEnumerable<Stmt> Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                yield return s;
                var inner = s switch
                {
                    If i => i.Branches.SelectMany(b => b.Body),
                    While w => w.Body,
                    _ => [],
                };
                foreach (var x in Walk(inner)) yield return x;
            }
        }

        /// <summary>Every call in the script, in statements and in expressions.</summary>
        public IEnumerable<Call> AllCalls()
        {
            foreach (var s in AllStatements())
            {
                IEnumerable<Expr> roots = s switch
                {
                    Set set => [set.Value],
                    If i => i.Branches.Where(b => b.Condition is not null).Select(b => b.Condition!),
                    While w => [w.Condition],
                    CallStmt c => [c.Call],
                    _ => [],
                };
                foreach (var r in roots)
                    foreach (var c in Calls(r)) yield return c;
            }
        }

        public static IEnumerable<Call> Calls(Expr e)
        {
            switch (e)
            {
                case Call c:
                    yield return c;
                    foreach (var a in c.Args) foreach (var x in Calls(a)) yield return x;
                    break;
                case Unary u: foreach (var x in Calls(u.Operand)) yield return x; break;
                case Binary b:
                    foreach (var x in Calls(b.Left)) yield return x;
                    foreach (var x in Calls(b.Right)) yield return x;
                    break;
            }
        }

        /// <summary>Every name read in the script (bare names and owner.variable), in statements and expressions.</summary>
        public IEnumerable<Name> AllNames()
        {
            foreach (var s in AllStatements())
            {
                IEnumerable<Expr> roots = s switch
                {
                    Set set => [set.Target, set.Value],
                    If i => i.Branches.Where(b => b.Condition is not null).Select(b => b.Condition!),
                    While w => [w.Condition],
                    CallStmt c => [c.Call],
                    _ => [],
                };
                foreach (var r in roots)
                    foreach (var n in Names(r)) yield return n;
            }
        }

        static IEnumerable<Name> Names(Expr e)
        {
            switch (e)
            {
                case Name n: yield return n; break;
                case Call c: foreach (var a in c.Args) foreach (var x in Names(a)) yield return x; break;
                case Unary u: foreach (var x in Names(u.Operand)) yield return x; break;
                case Binary b:
                    foreach (var x in Names(b.Left)) yield return x;
                    foreach (var x in Names(b.Right)) yield return x;
                    break;
            }
        }
    }

    // ---- lexer ----

    enum T { Ident, Number, String, Op, End }
    readonly record struct Token(T Kind, string Text);

    static readonly string[] Ops = ["->", "<==", ">==", "==", "!=", "<=", ">=", "<", ">", "=", "+", "-", "*", "/", "(", ")", ".", ","];

    static List<Token> Lex(string line)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == ';') break;
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '"')
            {
                int j = line.IndexOf('"', i + 1);
                if (j < 0) j = line.Length;
                tokens.Add(new(T.String, line[(i + 1)..Math.Min(j, line.Length)]));
                i = j + 1;
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1]))
            {
                int j = i;
                while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.')) j++;
                // An ID starting with digits (1st_thing) is a name.
                if (j < line.Length && (char.IsLetter(line[j]) || line[j] == '_'))
                {
                    j = IdEnd(line, i);
                    tokens.Add(new(T.Ident, line[i..j]));
                }
                else tokens.Add(new(T.Number, line[i..j].TrimEnd('.')));
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c > 127)
            {
                int j = IdEnd(line, i);
                tokens.Add(new(T.Ident, line[i..j]));
                i = j;
                continue;
            }
            string? op = Ops.FirstOrDefault(o => string.CompareOrdinal(line, i, o, 0, o.Length) == 0);
            // "- >" written with a space is still the arrow.
            if (op == "-" && line.AsSpan(i + 1).TrimStart().StartsWith(">") && !line.AsSpan(i + 1).TrimStart().StartsWith(">="))
            {
                tokens.Add(new(T.Op, "->"));
                i = line.IndexOf('>', i) + 1;
                continue;
            }
            if (op is null) { i++; continue; }   // stray characters (a lone ':' or '[') are ignored, as Morrowind does
            // "<==" and ">==" (typos the original compiler accepted) are <= and >=.
            tokens.Add(new(T.Op, op switch { "<==" => "<=", ">==" => ">=", _ => op }));
            i += op.Length;
        }
        tokens.Add(new(T.End, ""));
        return tokens;
    }

    static bool IsIdChar(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '\'' || ch > 127;

    /// <summary>The end of a name starting at i: letters, digits, '_', and '-' joining two words without spaces
    /// (Ingred_Hackle-Lo_Leaf_01, an ID; "x - 1" and "x-1" stay a subtraction).</summary>
    static int IdEnd(string line, int i)
    {
        int j = i;
        while (j < line.Length)
        {
            if (IsIdChar(line[j])) { j++; continue; }
            if (line[j] == '-' && j + 1 < line.Length && (char.IsLetter(line[j + 1]) || line[j + 1] == '_') && j > i && line[j - 1] != ' ') { j++; continue; }
            break;
        }
        return j;
    }

    // ---- parser ----

    static readonly HashSet<string> VarTypes = new(StringComparer.OrdinalIgnoreCase) { "short", "long", "float" };

    /// <summary>A script: "Begin Name" … "End". Lines before Begin and after End are ignored.</summary>
    public static Script Parse(string source) => ParseLines(source, script: true);

    /// <summary>A dialogue result script: statements without Begin/End.</summary>
    public static Script ParseResult(string source) => ParseLines(source, script: false);

    static Script ParseLines(string source, bool script)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string name = "";
        var variables = new List<Variable>();
        int at = 0;
        // Locals first: a local may share a function's name (short move), and is the local wherever it is named.
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            var t = Lex(l);
            if (t.Count >= 3 && t[0].Kind == T.Ident && VarTypes.Contains(t[0].Text) && t[1].Kind == T.Ident) localNames.Add(t[1].Text);
        }
        if (script)
        {
            // Up to Begin.
            while (at < lines.Length)
            {
                var toks = Lex(lines[at++]);
                if (toks[0].Kind == T.Ident && toks[0].Text.Equals("begin", StringComparison.OrdinalIgnoreCase))
                {
                    name = toks.Count > 1 ? toks[1].Text : "";
                    break;
                }
            }
        }

        // Statement lists end at a closing word (endif, elseif, else, endwhile, end), returned to the caller.
        List<Stmt> Body(out string closer, out List<Token> closerTokens, out int closerLine, params string[] closers)
        {
            var list = new List<Stmt>();
            closer = ""; closerTokens = []; closerLine = at;
            while (at < lines.Length)
            {
                int lineNo = at + 1;
                var toks = Lex(lines[at++]);
                if (toks[0].Kind == T.End) continue;
                string head = toks[0].Kind == T.Ident ? toks[0].Text.ToLowerInvariant() : "";
                // "else if" / "end if" / "end while" written as two words
                if (head == "else" && toks.Count > 2 && toks[1] is { Kind: T.Ident } t1 && t1.Text.Equals("if", StringComparison.OrdinalIgnoreCase))
                {
                    head = "elseif";
                    toks.RemoveAt(1);
                }
                else if (head == "end" && toks.Count > 2 && toks[1] is { Kind: T.Ident } t2 && (t2.Text.Equals("if", StringComparison.OrdinalIgnoreCase) || t2.Text.Equals("while", StringComparison.OrdinalIgnoreCase)))
                {
                    head = "end" + t2.Text.ToLowerInvariant();
                    toks.RemoveAt(1);
                }
                if (closers.Contains(head)) { closer = head; closerTokens = toks; closerLine = lineNo; return list; }
                if (VarTypes.Contains(head) && toks.Count >= 3 && toks[1].Kind == T.Ident)
                {
                    if (!variables.Any(v => v.Name.Equals(toks[1].Text, StringComparison.OrdinalIgnoreCase))) variables.Add(new(head, toks[1].Text));
                    continue;
                }
                list.Add(Statement(toks, lineNo));
            }
            return list;
        }

        Stmt Statement(List<Token> toks, int lineNo)
        {
            string head = toks[0].Kind == T.Ident ? toks[0].Text.ToLowerInvariant() : "";
            try
            {
                switch (head)
                {
                    case "set":
                    {
                        int to = toks.FindIndex(t => t.Kind == T.Ident && t.Text.Equals("to", StringComparison.OrdinalIgnoreCase));
                        if (to < 0) return new Unparsed(lineNo, Text(toks), "set without 'to'");
                        // The target: a name, or owner.name (the owner quoted when it has spaces).
                        var tt = toks[1..to];
                        Name? setTarget = tt.Count switch
                        {
                            1 when tt[0].Kind is T.Ident or T.String => new Name(tt[0].Text),
                            3 when tt[0].Kind is T.Ident or T.String && tt[1] is { Kind: T.Op, Text: "." or "->" } && tt[2].Kind == T.Ident => new Name(tt[2].Text, tt[0].Text),
                            _ => null,
                        };
                        if (setTarget is null) return new Unparsed(lineNo, Text(toks), "set of something that isn't a variable");
                        return new Set(lineNo, setTarget, new ExprParser(toks[(to + 1)..], localNames).ParseAll(lenient: true));
                    }
                    case "if":
                    {
                        return IfChain(Condition(toks[1..], lineNo), lineNo);
                    }
                    case "while":
                    {
                        var cond = Condition(toks[1..], lineNo);
                        var body = Body(out var closer, out _, out _, "endwhile", "end");
                        if (closer == "end") at--;
                        return new While(lineNo, cond, body);
                    }
                    case "return":
                        return new Return(lineNo);
                    case "begin":
                        return new NoOp(lineNo, Text(toks));   // a second Begin line, left in an unused script
                    case "endif" or "endwhile":
                        return new Return(-lineNo);   // an extra endif: Morrowind's compiler ignores it (marked by a negative line)
                    // An elseif left over after an extra endif closed its chain: Morrowind's scripts mean a new if.
                    case "elseif":
                        return IfChain(Condition(toks[1..], lineNo), lineNo);
                    // An else without its if: Morrowind's compiler jumps over it, so its body never runs.
                    case "else":
                        return IfChain(new Num(0, "0"), lineNo, elseBody: true);
                }
                // A condition without its "if" (a line in parentheses): compiled to nothing.
                if (toks[0] is { Kind: T.Op, Text: "(" }) return new NoOp(lineNo, Text(toks));
                var e = new ExprParser(toks, localNames, statement: true).ParseAll(lenient: true);
                return e is Call c ? new CallStmt(lineNo, c)
                    : e is Name { Owner: null } n ? new CallStmt(lineNo, new Call(n.Id, null, []))
                    : new Unparsed(lineNo, Text(toks), "not a statement");
            }
            catch (FormatException ex) { return new Unparsed(lineNo, Text(toks), ex.Message); }
        }

        // if … elseif … else … endif, from just after the if's line.
        If IfChain(Expr cond, int lineNo, bool elseBody = false)
        {
            var branches = new List<(Expr?, List<Stmt>)>();
            while (true)
            {
                var body = Body(out var closer, out var ct, out int cl, elseBody ? ["endif", "end"] : ["elseif", "else", "endif", "end"]);
                branches.Add((cond, body));
                if (closer == "elseif") { cond = Condition(ct[1..], cl); continue; }
                if (closer == "else") { branches.Add((null, Body(out closer, out _, out _, "endif", "end"))); }
                if (closer == "end") at--;   // a missing endif: let the script end
                break;
            }
            return new If(lineNo, branches);
        }

        // A condition that can't be read keeps the if's structure (its body still parses); it is marked unread.
        Expr Condition(List<Token> t, int ln)
        {
            try { return new ExprParser(t, localNames).ParseAll(lenient: true); }
            catch (FormatException ex) { return new Name("__unparsed__:" + ln + ":" + ex.Message + ": " + Text(t)); }
        }

        var all = Body(out _, out _, out _, script ? ["end"] : []);
        return new Script(name, variables, all);
    }

    static string Text(List<Token> toks) => string.Join(" ", toks.Where(t => t.Kind != T.End).Select(t => t.Kind == T.String ? $"\"{t.Text}\"" : t.Text));

    /// <summary>
    /// Expressions by precedence climbing. A function's arguments are the primaries that follow it, as many as it
    /// takes (<see cref="MwFunctions"/>), separated by spaces or commas.
    /// </summary>
    /// <param name="statement">A call statement: there "- 25" after a function is a negative argument (as in
    /// ModDisposition -25), not a subtraction.</param>
    sealed class ExprParser(List<Token> toks, HashSet<string>? locals = null, bool statement = false)
    {
        int p;
        Token Peek => toks[Math.Min(p, toks.Count - 1)];
        Token PeekAt(int k) => toks[Math.Min(p + k, toks.Count - 1)];

        /// <param name="lenient">Words left after a complete expression are ignored, as Morrowind's compiler ignores
        /// them (a comma or a stray word after a statement).</param>
        public Expr ParseAll(bool lenient = false)
        {
            if (Peek.Kind == T.End) throw new FormatException("nothing to read");
            var e = ParseBinary(0);
            // A trailing ')' left over from unbalanced parentheses is common in the original scripts.
            while (Peek is { Kind: T.Op, Text: ")" or "," }) p++;
            if (Peek.Kind != T.End && !lenient) throw new FormatException($"unexpected '{Peek.Text}'");
            return e;
        }

        static readonly (string[] Ops, int Level)[] Levels =
        [
            (["||"], 1), (["&&"], 2), (["==", "!=", "<", "<=", ">", ">=", "="], 3), (["+", "-"], 4), (["*", "/"], 5),
        ];

        static int LevelOf(Token t) => t.Kind == T.Op ? Levels.FirstOrDefault(l => l.Ops.Contains(t.Text)).Level : 0;

        Expr ParseBinary(int minLevel)
        {
            var left = ParseUnary();
            while (true)
            {
                if (Peek is { Kind: T.Op, Text: "," } && LevelOf(PeekAt(1)) is > 0) p++;   // "GetDistance, Player, < 100"
                if (LevelOf(Peek) is not (int level and > 0) || level <= minLevel) break;
                string op = toks[p++].Text;
                if (op == "=") op = "==";
                var right = ParseBinary(level);
                left = new Binary(op, left, right);
            }
            return left;
        }

        Expr ParseUnary()
        {
            if (Peek is { Kind: T.Op, Text: "-" })
            {
                p++;
                var operand = ParseUnary();
                return operand is Num n ? new Num(-n.Value, "-" + n.Text) : new Unary("-", operand);
            }
            if (Peek is { Kind: T.Op, Text: "+" }) { p++; return ParseUnary(); }
            return ParsePrimary(allowCall: true);
        }

        void Expect(string op)
        {
            if (Peek.Kind == T.Op && Peek.Text == op) { p++; return; }
            if (Peek.Kind == T.End && op == ")") return;   // a missing ')' at the end of the line
            throw new FormatException($"expected '{op}'");
        }

        static bool StartsPrimary(Token t) => t.Kind is T.Ident or T.Number or T.String || t.Kind == T.Op && t.Text is "(";

        Expr ParsePrimary(bool allowCall)
        {
            var t = Peek;
            switch (t.Kind)
            {
                case T.Number:
                    p++;
                    return new Num(double.TryParse(t.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0, t.Text);
                case T.String when PeekAt(1) is { Kind: T.Op, Text: "->" or "." }:
                case T.Ident:
                {
                    p++;
                    string id = t.Text;
                    // ref->( expression ): the reference applies to the calls inside ("x"->( OnDeath == 1 )).
                    if (Peek is { Kind: T.Op, Text: "->" } && PeekAt(1) is { Kind: T.Op, Text: "(" })
                    {
                        p += 2;
                        var inner = ParseBinary(0);
                        Expect(")");
                        return Retarget(inner, id);
                    }
                    // ref->Function args
                    if (Peek is { Kind: T.Op, Text: "->" } && PeekAt(1).Kind is T.Ident or T.String)
                    {
                        p++;
                        string fn = toks[p++].Text;
                        return new Call(fn, id, allowCall ? Args(fn) : []);
                    }
                    // owner.variable
                    if (Peek is { Kind: T.Op, Text: "." } && PeekAt(1).Kind == T.Ident)
                    {
                        p++;
                        return new Name(toks[p++].Text, id);
                    }
                    // owner. with the variable missing (a slip in the original): the value of an unset variable, 0.
                    if (Peek is { Kind: T.Op, Text: "." } && t.Kind == T.String)
                    {
                        p++;
                        return new Num(0, "0");
                    }
                    if (t.Kind == T.String) return new Str(id);
                    if (!allowCall || locals is not null && locals.Contains(id)) return new Name(id);
                    var info = MwFunctions.Find(id);
                    // A function without arguments followed by a quoted ID: the reference it was meant for
                    // (GetDisabled "Cimber Valerius").
                    if (info is { MaxArgs: 0 } && Peek.Kind == T.String && (PeekAt(1).Kind == T.End || LevelOf(PeekAt(1)) > 0 || PeekAt(1) is { Kind: T.Op, Text: ")" }))
                        return new Call(id, toks[p++].Text, []);
                    if (info is not null) return info.MaxArgs == 0 ? new Call(id, null, []) : new Call(id, null, Args(id));
                    // An unknown word followed by arguments is a call (a variable is never followed by a value); else a name.
                    if (StartsPrimary(Peek) || Peek is { Kind: T.Op, Text: "," } && StartsPrimary(PeekAt(1)) || statement && p == 1 && Peek.Kind != T.End)
                        return new Call(id, null, Args(id));
                    return new Name(id);
                }
                case T.String: p++; return new Str(t.Text);
                case T.Op when t.Text == "(":
                {
                    p++;
                    var e = ParseBinary(0);
                    Expect(")");
                    return e;
                }
            }
            throw new FormatException($"unexpected '{t.Text}'");
        }

        /// <summary>
        /// A function's arguments: up to as many as it takes (unknown functions: primaries until an operator); commas
        /// are optional separators. An argument is a number, a string, a name or a parenthesised expression; a minus
        /// before a number is its sign where an argument is expected.
        /// </summary>
        List<Expr> Args(string function)
        {
            var info = MwFunctions.Find(function);
            int max = info?.MaxArgs ?? int.MaxValue;
            int min = info?.MinArgs ?? 0;
            var args = new List<Expr>();
            while (args.Count < max)
            {
                if (Peek is { Kind: T.Op, Text: "," }) { p++; continue; }
                // A negative number argument: always where one is still needed, else in a statement (no subtraction there).
                if (Peek is { Kind: T.Op, Text: "-" } && PeekAt(1).Kind == T.Number && (args.Count < min || statement || PrevWasComma()))
                {
                    p++;
                    var n = (Num)ParsePrimary(allowCall: false);
                    args.Add(new Num(-n.Value, "-" + n.Text));
                    continue;
                }
                if (!StartsPrimary(Peek)) break;
                // An argument that is itself a call needs parentheses in Morrowind; a bare function name is just a name
                // (an ID or a variable), except a value function used as an argument in an expression.
                args.Add(ParsePrimary(allowCall: false));
            }
            return args;
        }

        bool PrevWasComma() => p > 0 && toks[p - 1] is { Kind: T.Op, Text: "," };

        static Expr Retarget(Expr e, string target) => e switch
        {
            Call c when c.Target is null => c with { Target = target },
            Unary u => u with { Operand = Retarget(u.Operand, target) },
            Binary b => b with { Left = Retarget(b.Left, target), Right = Retarget(b.Right, target) },
            _ => e,
        };
    }
}
