namespace TamrielMerge.Redguard;

/// <summary>
/// The actors of a Redguard scene (.RGM) and their compiled SOUP scripts, as statement trees. RAHD has one 165-byte
/// record per actor after an 8-byte prefix (script name at 4, string count at 65, string table index at 73, script
/// length at 77, entry point at 85; scripts are stored one after another in RASC from the first actor's data offset at
/// 81); RAST/RASB hold the scripts' strings. The bytecode is a tree of values, each starting with a type byte (layout as
/// Dillonn241's redguard-mod-manager ScriptReader reads it):
/// <list type="bullet">
/// <item>0/1/2 task, multitask, function: u16 id (SOUP386.DEF [functions] order, 0 = none), u8 argument count and the
/// arguments; 3 if: (value, u8 comparison, value, u8 conjunction: 1 and, 2 or) until the conjunction is 0, then the
/// block's end (u32) and the block; 4/5 goto/end, 17 gosub, 27 task pause: u32 address; 18 return, 19 end of
/// interrupt; 21 string: u32 index;</item>
/// <item>6 flag (u16) and 10 local variable (u8), followed by a formula (as a statement: an assignment), an operator
/// (in a comparison) or padding (u16 / 3 bytes, as an argument); 7/22 number: u32 (4-character RTX tags too); 15/16
/// object.reference ++/--, 20 object.reference (+ formula or operator), 25/26 object.task; 23: u8; 30: u8 and a
/// 4-byte block.</item>
/// </list>
/// </summary>
public sealed class RgScript
{
    public sealed record Call(string Actor, int Function, int?[] Args);

    public abstract record Value;
    /// <summary>A number (or 4-character tag).</summary>
    public sealed record Num(int V) : Value;
    /// <summary>A global flag (SOUP386.DEF [flags] order).</summary>
    public sealed record Flag(int Index) : Value;
    public sealed record Local(int Index) : Value;
    public sealed record FnValue(int Function, Value[] Args) : Value;
    public sealed record Other(string What) : Value;

    public abstract record Stmt(int Address);
    public sealed record CallStmt(int Address, int Function, Value[] Args) : Stmt(Address);
    /// <summary>if (lhs cmp rhs [and/or ...]) { body }; cmp: 0 =, 1 !=, 2 &lt;, 3 &gt;, 4 &lt;=, 5 &gt;=.</summary>
    public sealed record IfStmt(int Address, List<(Value Lhs, int Cmp, Value Rhs, int Conj)> Conditions, List<Stmt> Body) : Stmt(Address);
    /// <summary>target = values combined with operators (1 +, 2 -, 3 /, 4 *, ...; 10 ++, 11 --).</summary>
    public sealed record AssignStmt(int Address, Value Target, List<(Value Value, int Op)> Formula) : Stmt(Address);
    public sealed record JumpStmt(int Address, string Kind, int Target) : Stmt(Address);
    public sealed record OtherStmt(int Address, string What) : Stmt(Address);

    public List<string> Actors { get; } = [];
    public List<Call> Calls { get; } = [];
    /// <summary>Each actor's statements (addresses relative to the actor's script) and strings.</summary>
    public Dictionary<string, List<Stmt>> Programs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each actor's local variables' starting values (RAVA, from RAHD's count at 117 and byte offset at 125).</summary>
    public Dictionary<string, int[]> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SOUP function names by id (id 0 is the null function).</summary>
    public static List<string> FunctionNames(string soupDef)
    {
        var names = new List<string> { "NullFunction" };
        bool inFunctions = false;
        foreach (var raw in File.ReadLines(soupDef))
        {
            string line = raw.Trim();
            if (line == "[functions]") { inFunctions = true; continue; }
            if (line == "[refs]") break;
            if (!inFunctions || line.Length == 0 || line.StartsWith(';')) continue;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) names.Add(parts[1]);
        }
        return names;
    }

    /// <summary>SOUP global flag names by index, with their comments (SOUP386.DEF [flags]: type, name, initial value).</summary>
    public static List<(string Name, string Comment)> FlagNames(string soupDef) => Flags(soupDef).Select(f => (f.Name, f.Comment)).ToList();

    /// <summary>SOUP global flags by index: name, initial value (TRUE/FALSE or a number) and comment.</summary>
    public static List<(string Name, int Initial, string Comment)> Flags(string soupDef)
    {
        var flags = new List<(string, int, string)>();
        bool inFlags = false;
        foreach (var raw in File.ReadLines(soupDef))
        {
            string line = raw.Trim();
            if (line == "[flags]") { inFlags = true; continue; }
            if (!inFlags || line.Length == 0 || line.StartsWith(';')) continue;
            int semi = line.IndexOf(';');
            var parts = (semi >= 0 ? line[..semi] : line).Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            int initial = parts.Length < 3 ? 0 : parts[2].Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : int.TryParse(parts[2], out var v) ? v : 0;
            if (parts.Length >= 2) flags.Add((parts[1], initial, semi >= 0 ? line[(semi + 1)..].Trim() : ""));
        }
        return flags;
    }

    public static RgScript Load(byte[] rgm, RgScene scene)
    {
        var s = new RgScript();
        if (!scene.Sections.TryGetValue("RAHD", out var rahd) || !scene.Sections.TryGetValue("RASC", out var rasc)) return s;
        scene.Sections.TryGetValue("RAST", out var rast);
        scene.Sections.TryGetValue("RASB", out var rasb);
        scene.Sections.TryGetValue("RAVA", out var rava);
        int count = BitConverter.ToInt32(rgm, rahd.Offset);
        int pos = -1;
        for (int i = 0; i < count; i++)
        {
            int r = rahd.Offset + 8 + i * 165;
            if (r + 165 > rahd.Offset + rahd.Length) break;
            string name = System.Text.Encoding.ASCII.GetString(rgm, r + 4, 9).Split('\0')[0].Trim();
            int length = BitConverter.ToInt32(rgm, r + 77);
            if (pos < 0) pos = BitConverter.ToInt32(rgm, r + 81);
            s.Actors.Add(name);
            var strings = new List<string>();
            int numStrings = BitConverter.ToInt32(rgm, r + 65), stringIndex = BitConverter.ToInt32(rgm, r + 73);
            for (int k = 0; k < numStrings && rasb.Length > 0 && stringIndex + k * 4 + 4 <= rasb.Length; k++)
            {
                int so = BitConverter.ToInt32(rgm, rasb.Offset + stringIndex + k * 4);
                if (so < 0 || so >= rast.Length) { strings.Add(""); continue; }
                int e = Array.IndexOf(rgm, (byte)0, rast.Offset + so);
                strings.Add(System.Text.Encoding.Latin1.GetString(rgm, rast.Offset + so, Math.Max(0, (e < 0 ? rast.Offset + rast.Length : e) - rast.Offset - so)));
            }
            s.Strings.TryAdd(name, strings);
            int numVars = BitConverter.ToInt32(rgm, r + 117), varOffset = BitConverter.ToInt32(rgm, r + 125);
            var locals = new int[Math.Clamp(numVars, 0, 1024)];
            for (int k = 0; k < locals.Length && rava.Length > 0 && varOffset + k * 4 + 4 <= rava.Length; k++)
                locals[k] = BitConverter.ToInt32(rgm, rava.Offset + varOffset + k * 4);
            s.Locals.TryAdd(name, locals);
            if (length > 0 && pos + length <= rasc.Length)
            {
                var walker = new Walker(rgm, rasc.Offset + pos, rasc.Offset + pos + length, name, s.Calls);
                try { walker.Run(); }
                catch (IndexOutOfRangeException) { }
                catch (ArgumentOutOfRangeException) { }
                s.Programs.TryAdd(name, walker.Program);
            }
            pos += Math.Max(0, length);
        }
        return s;
    }

    sealed class Walker(byte[] b, int start, int end, string actor, List<Call> calls)
    {
        enum Mode { Main, Lhs, Rhs, Parameter, Reference, Formula }
        int p = start;
        public List<Stmt> Program { get; } = [];

        byte U8() => b[p++];
        int U16() { int v = BitConverter.ToUInt16(b, p); p += 2; return v; }
        int I32() { int v = BitConverter.ToInt32(b, p); p += 4; return v; }

        public void Run() => Block(end, Program);

        void Block(int last, List<Stmt> into)
        {
            last = Math.Min(last, end);
            while (p < last) into.Add(Statement());
        }

        Stmt Statement()
        {
            int at = p - start;
            if (p >= end) throw new IndexOutOfRangeException();
            int type = U8();
            switch (type)
            {
                case 0: case 1: case 2: { var (fn, args) = TaskCall(); return new CallStmt(at, fn, args); }
                case 3: return If(at);
                case 4: return new JumpStmt(at, "goto", I32());
                case 5: return new JumpStmt(at, "end", I32());
                case 17: return new JumpStmt(at, "gosub", I32());
                case 27: return new JumpStmt(at, "pause", I32());
                case 18: return new OtherStmt(at, "return");
                case 19: return new OtherStmt(at, "endint");
                case 6: { var f = new Flag(U16()); return new AssignStmt(at, f, Formula()); }
                case 10: { var l = new Local(U8()); return new AssignStmt(at, l, Formula()); }
                case 20: { var o = ObjectRef(); return new AssignStmt(at, o, Formula()); }
                case 15: case 16: { var o = ObjectRef(); return new AssignStmt(at, o, [(new Num(1), type == 15 ? 10 : 11)]); }
                case 25: case 26:
                {
                    ObjectName();
                    var v = Value(Mode.Reference);
                    return v is FnValue f ? new CallStmt(at, f.Function, f.Args) : new OtherStmt(at, "object task");
                }
                case 30: { U8(); var body = new List<Stmt>(); Block(p + 4, body); return new OtherStmt(at, "scriptrv"); }
                case 7: case 22: I32(); return new OtherStmt(at, "number");
                case 21: I32(); return new OtherStmt(at, "string");
                case 23: U8(); return new OtherStmt(at, "anchor");
                default: return new OtherStmt(at, $"op{type}");
            }
        }

        IfStmt If(int at)
        {
            var conds = new List<(Value, int, Value, int)>();
            int conjunction;
            do
            {
                var lhs = Value(Mode.Lhs);
                int cmp = U8();
                var rhs = Value(Mode.Rhs);
                conjunction = U8();
                conds.Add((lhs, cmp, rhs, conjunction));
            } while (conjunction != 0);
            int blockEnd = start + I32();
            var body = new List<Stmt>();
            Block(blockEnd, body);
            return new IfStmt(at, conds, body);
        }

        Value Value(Mode mode)
        {
            if (p >= end) throw new IndexOutOfRangeException();
            int type = U8();
            switch (type)
            {
                case 0: case 1: case 2: { var (fn, args) = TaskCall(); return new FnValue(fn, args); }
                case 6:
                {
                    var f = new Flag(U16());
                    if (mode is Mode.Lhs or Mode.Rhs) Operator(); else if (mode == Mode.Parameter) U16();
                    return f;
                }
                case 7: case 22: return new Num(I32());
                case 10:
                {
                    var l = new Local(U8());
                    if (mode is Mode.Lhs or Mode.Rhs) Operator(); else if (mode == Mode.Parameter) p += 3;
                    return l;
                }
                case 15: case 16: return ObjectRef();
                case 20:
                {
                    var o = ObjectRef();
                    if (mode == Mode.Lhs) Operator();
                    return o;
                }
                case 21: return new Other($"string{I32()}");
                case 23: return new Other($"anchor{U8()}");
                case 25: case 26:
                    ObjectName();
                    if (mode == Mode.Main) return Value(Mode.Reference);
                    { var (fn, args) = TaskCall(); return new FnValue(fn, args); }
                default:
                    if (type is 4 or 5 or 17 or 27) I32();
                    return new Other($"type{type}");
            }
        }

        (int Function, Value[] Args) TaskCall()
        {
            int id = U16();
            int n = id == 0 ? 0 : U8();
            var args = new Value[n];
            for (int i = 0; i < n; i++) args[i] = Value(Mode.Parameter);
            calls.Add(new Call(actor, id, args.Select(a => a is Num num ? (int?)num.V : null).ToArray()));
            return (id, args);
        }

        Other ObjectRef()
        {
            string o = ObjectName();
            int reference = U16() & 0xFF;
            return new Other($"{o}.ref{reference}");
        }

        string ObjectName()
        {
            int kind = U8();
            if (kind is 0 or 1 or 2) { U8(); return kind switch { 0 => "Me", 1 => "Player", _ => "Camera" }; }
            if (kind is 4 or 10) return $"obj{kind}:{U8()}";
            return $"obj{kind}";
        }

        List<(Value, int)> Formula()
        {
            var parts = new List<(Value, int)>();
            while (true)
            {
                var v = Value(Mode.Formula);
                int op = U8();
                if (op is 10 or 11) { U8(); parts.Add((v, op)); return parts; }
                parts.Add((v, op));
                if (op is <= 0 or >= 10) return parts;
            }
        }

        void Operator()
        {
            int op = U8();
            if (op is 10 or 11) U8();
        }
    }
}
