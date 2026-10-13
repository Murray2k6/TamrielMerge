using System.Text;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's scripting language (with OBSE's additions), parsed from a script's source text (SCTX, which plugins
/// keep) into a tree the Papyrus translator works from:
/// <list type="bullet">
/// <item>header: "scn Name"; variables: short / long / int / float / ref / string_var / array_var name;</item>
/// <item>blocks: Begin Type [parameters] … End (GameMode, OnActivate, OnAdd, ScriptEffectStart, OBSE's Function{args},
/// …);</item>
/// <item>statements: set x to expr; OBSE let x := expr (and +=, -=, *=, /=); if / elseif / else / endif; OBSE while /
/// foreach x &lt;- expr / loop, break, continue; return; and calls: [ref.]Function args… (Call is OBSE's call of a
/// user function);</item>
/// <item>expressions: || &amp;&amp; == != &lt; &lt;= &gt; &gt;= + - * / % ^ unary - !, parentheses, numbers, strings,
/// quest.var / ref.var member access, OBSE array[index] and $ (to string), and function calls whose arguments follow
/// without parentheses (taken until an operator or the end of the statement).</item>
/// </list>
/// Statements are one per line; ';' starts a comment.
/// </summary>
public static class ObScript
{
    // ---- tree ----

    public abstract record Expr;
    public sealed record Num(double Value, string Text) : Expr;
    public sealed record Str(string Value) : Expr;
    /// <summary>A name: a variable, a form's EditorID, or (Target set) a quest's / reference's variable.</summary>
    public sealed record Name(string Id, Expr? Target = null) : Expr;
    public sealed record Call(string Function, Expr? Target, List<Expr> Args) : Expr;
    public sealed record Unary(string Op, Expr Operand) : Expr;
    public sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
    public sealed record Index(Expr Array, Expr Key) : Expr;

    public abstract record Stmt(int Line);
    public sealed record Set(int Line, Expr Target, string Op, Expr Value) : Stmt(Line);
    public sealed record If(int Line, List<(Expr? Condition, List<Stmt> Body)> Branches) : Stmt(Line);
    public sealed record While(int Line, Expr Condition, List<Stmt> Body) : Stmt(Line);
    public sealed record ForEach(int Line, Expr Item, Expr Collection, List<Stmt> Body) : Stmt(Line);
    public sealed record CallStmt(int Line, Call Call) : Stmt(Line);
    public sealed record Keyword(int Line, string Word) : Stmt(Line);   // return, break, continue
    public sealed record Unparsed(int Line, string Text, string Reason) : Stmt(Line);

    public sealed record Variable(string Type, string Name);
    public sealed record Block(string Type, List<string> Parameters, List<Stmt> Body, int Line);
    public sealed record Script(string Name, List<Variable> Variables, List<Block> Blocks)
    {
        public IEnumerable<Stmt> AllStatements() => Blocks.SelectMany(b => Walk(b.Body));
        static IEnumerable<Stmt> Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                yield return s;
                var inner = s switch
                {
                    If i => i.Branches.SelectMany(b => b.Body),
                    While w => w.Body,
                    ForEach f => f.Body,
                    _ => [],
                };
                foreach (var x in Walk(inner)) yield return x;
            }
        }
    }

    // ---- lexer ----

    enum T { Ident, Number, String, Op, End }
    readonly record struct Token(T Kind, string Text);

    static readonly string[] Ops = [":=", "+=", "-=", "*=", "/=", "^=", "==", "!=", "<=", ">=", "&&", "||", "<-", "->", "::", "<", ">", "+", "-", "*", "/", "%", "^", "(", ")", "[", "]", "{", "}", ".", ",", "!", "$", "#", "=", "|", "&", "?", ":"];

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
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '.')) j++;
                tokens.Add(new(T.Number, line[i..j]));
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                // An unquoted file path argument (Meshes\magiceffects\null.nif) is one string.
                if (j < line.Length && line[j] == '\\')
                {
                    while (j < line.Length && !char.IsWhiteSpace(line[j]) && line[j] != ',' && line[j] != ';') j++;
                    tokens.Add(new(T.String, line[i..j]));
                    i = j;
                    continue;
                }
                tokens.Add(new(T.Ident, line[i..j]));
                i = j;
                continue;
            }
            string? op = Ops.FirstOrDefault(o => string.CompareOrdinal(line, i, o, 0, o.Length) == 0);
            if (op is null) { tokens.Add(new(T.Op, c.ToString())); i++; continue; }
            tokens.Add(new(T.Op, op));
            i += op.Length;
        }
        tokens.Add(new(T.End, ""));
        return tokens;
    }

    // ---- parser ----

    static readonly HashSet<string> VarTypes = new(StringComparer.OrdinalIgnoreCase) { "short", "long", "int", "float", "ref", "reference", "string_var", "array_var" };

    public static Script Parse(string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string name = "";
        var variables = new List<Variable>();
        var blocks = new List<Block>();
        var conditionProblems = new List<Unparsed>();
        int at = 0;

        // Statement lists end at a closing word (endif, elseif, else, loop, end), returned to the caller.
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
                if (closers.Contains(head)) { closer = head; closerTokens = toks; closerLine = lineNo; return list; }
                if (VarTypes.Contains(head) && toks.Count >= 3 && toks[1].Kind == T.Ident) { variables.Add(new(head, toks[1].Text)); continue; }
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
                        return new Set(lineNo, new ExprParser(toks[1..to].Append(new(T.End, "")).ToList()).ParseAll(), ":=", new ExprParser(toks[(to + 1)..]).ParseAll());
                    }
                    case "let":
                    {
                        int op = toks.FindIndex(t => t.Kind == T.Op && t.Text is ":=" or "+=" or "-=" or "*=" or "/=" or "^=");
                        if (op < 0) return new Unparsed(lineNo, Text(toks), "let without :=");
                        return new Set(lineNo, new ExprParser(toks[1..op].Append(new(T.End, "")).ToList()).ParseAll(), toks[op].Text, new ExprParser(toks[(op + 1)..]).ParseAll());
                    }
                    case "if":
                    {
                        var branches = new List<(Expr?, List<Stmt>)>();
                        // A condition that can't be read keeps the if's structure (its body still parses); the condition
                        // is marked unread.
                        Expr Condition(List<Token> t, int ln)
                        {
                            try { return new ExprParser(t).ParseAll(); }
                            catch (FormatException ex) { conditionProblems.Add(new Unparsed(ln, Text(t), ex.Message)); return new Name("__unparsed__"); }
                        }
                        Expr? cond = Condition(toks[1..], lineNo);
                        while (true)
                        {
                            var body = Body(out var closer, out var ct, out int cl, "elseif", "else", "endif", "end");
                            branches.Add((cond, body));
                            if (closer == "elseif") { cond = Condition(ct[1..], cl); continue; }
                            if (closer == "else") { cond = null; branches.Add((null, Body(out closer, out _, out _, "endif", "end"))); }
                            if (closer == "end") at--;   // a missing endif: let the block end
                            break;
                        }
                        return new If(lineNo, branches);
                    }
                    case "while":
                    {
                        var cond = new ExprParser(toks[1..]).ParseAll();
                        var body = Body(out var closer, out _, out _, "loop", "end");
                        if (closer == "end") at--;
                        return new While(lineNo, cond, body);
                    }
                    case "foreach":
                    {
                        int arrow = toks.FindIndex(t => t.Text == "<-");
                        if (arrow < 0) return new Unparsed(lineNo, Text(toks), "foreach without <-");
                        var item = new ExprParser(toks[1..arrow].Append(new(T.End, "")).ToList()).ParseAll();
                        var coll = new ExprParser(toks[(arrow + 1)..]).ParseAll();
                        var body = Body(out var closer, out _, out _, "loop", "end");
                        if (closer == "end") at--;
                        return new ForEach(lineNo, item, coll, body);
                    }
                    case "return": case "break": case "continue":
                        return new Keyword(lineNo, head);
                    case "endif":
                        return new Keyword(lineNo, "endif");   // an extra endif: Oblivion's compiler ignores it, so do we
                    case "elseif": case "else": case "loop":
                        return new Unparsed(lineNo, Text(toks), $"'{head}' without its if/while");
                }
                // A call statement: [ref.]Function args…
                var e = new ExprParser(toks, statement: true).ParseAll();
                return e is Call c ? new CallStmt(lineNo, c)
                    : e is Name n ? new CallStmt(lineNo, new Call(n.Id, n.Target, []))
                    : new Unparsed(lineNo, Text(toks), "not a statement");
            }
            catch (FormatException ex) { return new Unparsed(lineNo, Text(toks), ex.Message); }
        }

        while (at < lines.Length)
        {
            int lineNo = at + 1;
            var toks = Lex(lines[at++]);
            if (toks[0].Kind != T.Ident) continue;
            string head = toks[0].Text.ToLowerInvariant();
            if (head is "scn" or "scriptname" && toks.Count > 1) { name = toks[1].Text; continue; }
            if (VarTypes.Contains(head) && toks.Count >= 3 && toks[1].Kind == T.Ident) { variables.Add(new(head, toks[1].Text)); continue; }
            if (head == "begin" && toks.Count > 1)
            {
                string type = toks[1].Text;
                var parameters = new List<string>();
                for (int k = 2; k < toks.Count - 1; k++)
                    if (toks[k].Kind is T.Ident or T.Number) parameters.Add(toks[k].Text);
                var body = Body(out _, out _, out _, "end");
                blocks.Add(new Block(type, parameters, body, lineNo));
            }
        }
        // Unreadable conditions are listed in the block they belong to (first block if unsure), so they're counted.
        if (conditionProblems.Count > 0 && blocks.Count > 0)
            foreach (var u in conditionProblems)
                (blocks.LastOrDefault(b => b.Line <= u.Line) ?? blocks[0]).Body.Add(u);
        return new Script(name, variables, blocks);
    }

    static string Text(List<Token> toks) => string.Join(" ", toks.Where(t => t.Kind != T.End).Select(t => t.Kind == T.String ? $"\"{t.Text}\"" : t.Text));

    /// <summary>Expressions by precedence climbing. A function call's arguments are the primaries that follow it,
    /// up to an operator, a comma, a closing bracket or the end.</summary>
    /// <param name="statement">A call statement: there "- 25" after a function's arguments is a negative argument (as
    /// in ModDisposition Player -25), not a subtraction.</param>
    sealed class ExprParser(List<Token> toks, bool statement = false)
    {
        int p;
        Token Peek => toks[Math.Min(p, toks.Count - 1)];

        public Expr ParseAll()
        {
            var e = ParseBinary(0);
            if (Peek.Kind != T.End) throw new FormatException($"unexpected '{Peek.Text}'");
            return e;
        }

        static readonly (string[] Ops, int Level)[] Levels =
        [
            (["::"], 1), (["||"], 2), (["&&"], 3), (["==", "!=", "<", "<=", ">", ">="], 4), (["+", "-"], 5), (["*", "/", "%"], 6), (["^"], 7),
        ];

        static int LevelOf(Token t) => t.Kind == T.Op ? Levels.FirstOrDefault(l => l.Ops.Contains(t.Text)).Level : 0;

        Expr ParseBinary(int minLevel)
        {
            var left = ParseUnary();
            while (LevelOf(Peek) is int level and > 0 && level > minLevel)
            {
                string op = toks[p++].Text;
                var right = ParseBinary(level);
                left = new Binary(op, left, right);
            }
            return left;
        }

        Expr ParseUnary()
        {
            // OBSE: eval (expr) evaluates an OBSE expression; * dereferences (an array element or pair value), & takes a
            // string's value.
            if (Peek is { Kind: T.Ident } && Peek.Text.Equals("eval", StringComparison.OrdinalIgnoreCase)) { p++; return ParseUnary(); }
            if (Peek is { Kind: T.Op, Text: "-" or "!" or "$" or "#" or "*" or "&" })
            {
                string op = toks[p++].Text;
                return new Unary(op, ParseUnary());
            }
            return ParsePostfix(ParsePrimary(allowCall: true));
        }

        Expr ParsePostfix(Expr e)
        {
            while (Peek is { Kind: T.Op, Text: "[" })
            {
                p++;
                var key = ParseBinary(0);
                Expect("]");
                e = new Index(e, key);
            }
            return e;
        }

        void Expect(string op)
        {
            if (Peek.Kind == T.Op && Peek.Text == op) { p++; return; }
            throw new FormatException($"expected '{op}'");
        }

        bool StartsPrimary(Token t) => t.Kind is T.Ident or T.Number or T.String || t.Kind == T.Op && t.Text is "(" or "$" or "#";

        Expr ParsePrimary(bool allowCall)
        {
            var t = Peek;
            switch (t.Kind)
            {
                case T.Number:
                    p++;
                    return new Num(double.TryParse(t.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0, t.Text);
                case T.String when toks[p + 1] is { Kind: T.Op, Text: "." } && toks[p + 2].Kind == T.Ident:
                    // A quoted EditorID ("1MarkFxEffectWorldRef".SetPos …): IDs starting with a digit are written so.
                    toks[p] = new Token(T.Ident, t.Text);
                    return ParsePrimary(allowCall);
                case T.String: p++; return new Str(t.Text);
                case T.Op when t.Text == "(":
                {
                    p++;
                    var e = ParseBinary(0);
                    Expect(")");
                    return e;
                }
                case T.Op when t.Text is "$" or "#":
                    p++;
                    return new Unary(t.Text, ParsePrimary(allowCall: false));
                case T.Ident:
                {
                    p++;
                    Expr? target = null;
                    string id = t.Text;
                    // ref.Function / quest.variable (one level, as Oblivion allows)
                    if (Peek is { Kind: T.Op, Text: "." } && toks[p + 1].Kind == T.Ident)
                    {
                        p++;
                        target = new Name(id);
                        id = toks[p++].Text;
                    }
                    // OBSE user-function call: Call Func args…
                    if (allowCall && id.Equals("call", StringComparison.OrdinalIgnoreCase) && Peek.Kind == T.Ident)
                    {
                        var args = Args();
                        return new Call("Call", target, args);
                    }
                    // Anything followed by a primary takes it (and what follows) as arguments: a function call.
                    if (allowCall && StartsPrimary(Peek) && !(Peek.Kind == T.Op && Peek.Text == "("))
                        return new Call(id, target, Args());
                    if (allowCall && Peek is { Kind: T.Op, Text: "," } && StartsPrimary(toks[p + 1]))
                        return new Call(id, target, Args());
                    if (allowCall && statement && Peek is { Kind: T.Op, Text: "-" } && toks[p + 1].Kind == T.Number)
                        return new Call(id, target, Args());
                    if (allowCall && Peek is { Kind: T.Op, Text: "(" } && IsKnownFunctionWithParens(id))
                        return new Call(id, target, Args());
                    return new Name(id, target);
                }
            }
            throw new FormatException($"unexpected '{t.Text}'");
        }

        // Arguments: primaries (with postfix indexing) until something that isn't one; commas are optional separators.
        List<Expr> Args()
        {
            var args = new List<Expr>();
            while (true)
            {
                if (Peek is { Kind: T.Op, Text: "," }) { p++; continue; }
                if (statement && Peek is { Kind: T.Op, Text: "-" } && toks[p + 1].Kind == T.Number)
                {
                    p++;
                    var n = (Num)ParsePrimary(allowCall: false);
                    args.Add(new Num(-n.Value, "-" + n.Text));
                    continue;
                }
                if (!StartsPrimary(Peek)) break;
                var arg = ParsePostfix(ParsePrimary(allowCall: false));
                // OBSE pairs as arguments: key :: value
                if (Peek is { Kind: T.Op, Text: "::" }) { p++; arg = new Binary("::", arg, ParsePostfix(ParsePrimary(allowCall: false))); }
                args.Add(arg);
            }
            return args;
        }

        // A name directly followed by "(" is a call only for a few functions OBSE scripts write that way; otherwise the
        // parenthesis starts an argument expression (handled as an argument) — so this stays conservative.
        static bool IsKnownFunctionWithParens(string id) => false;
    }
}
