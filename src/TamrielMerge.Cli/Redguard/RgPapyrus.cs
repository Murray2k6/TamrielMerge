using System.Text;
using System.Text.RegularExpressions;
using static TamrielMerge.Redguard.RgScript;

namespace TamrielMerge.Redguard;

/// <summary>
/// Translates one Redguard object's compiled SOUP script into a Papyrus script extending TMRgSoup (the SOUP engine and
/// function library, Papyrus\TMRgSoup.psc). SOUP code jumps freely (goto, gosub, "end" = resume there next frame) and
/// waits on tasks, so the statement tree is flattened into instructions, cut into blocks where code is jumped to or
/// waits, and each block becomes a function that does its statements and names the block after it (TMRgSoup.Go/
/// EndTo/Task/Gosub/Ret/EndInt); Dispatch(b) picks the block by a binary search. Values are Papyrus Ints: flags are
/// Redguard's flag globals (TMRgSoup.Flag), the object's variables script variables (or the globals the converted
/// dialogue already tests), object fields (pos, angle, hide, dead) TMRgSoup.GetField. Every SOUP function maps to an
/// S_ function of the library with the same name; the few calls that need the build's records (lines, the log, map
/// changes, objects named by script name, items to choose from) are resolved through <see cref="Context"/>.
/// <para>Each statement is counted by what became of it (<see cref="Category"/>): translated to Skyrim's own behaviour,
/// approximated, left without effect because Skyrim has no counterpart (camera moves, sprite flats, animation
/// numbers), or held by Skyrim's dialogue (a talking actor's conversation code).</para>
/// </summary>
public sealed class RgPapyrus
{
    public enum Category { Native, Approximate, NoEffect, Dialogue }

    /// <summary>What a script needs from the build.</summary>
    public interface Context
    {
        /// <summary>A spoken line (RTX tag) by this object (cyrus: the player's): its RGLines index (-1 for none), text
        /// and length in seconds.</summary>
        (int Index, string Text, float Seconds) Line(int tag, bool cyrus);
        /// <summary>Cyrus's log entry's stage on the log quest.</summary>
        int LogStage(int tag);
        /// <summary>Whether this LoadWorld is already a load door (the trigger converted by BuildDoors).</summary>
        bool LoadWorldIsDoor(int? map, int? marker);
    }

    public sealed record Output(string ScriptName, string Source, int Statements, Dictionary<Category, int> Counts,
        Dictionary<string, Dictionary<Category, int>> ByFunction, List<string> Objects, List<(int? Map, int? Marker, int? Facing)> Places,
        List<int> UseItems, HashSet<int> GlobalLocals, int EntryBlock, bool Interrupt)
    {
        /// <summary>The script's menus (outside Skyrim's dialogue: a door that answers, a sign to choose from), by
        /// number: the RTX tags of their items, one message box button each.</summary>
        public List<List<int>> Menus { get; init; } = [];
    }

    // ---- the library's functions (parsed from TMRgSoup.psc) ----

    public sealed record LibFunction(string Name, bool ReturnsFloat, int Arity);

    public static Dictionary<string, LibFunction> LoadLibrary(string soupPsc)
    {
        var lib = new Dictionary<string, LibFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(File.ReadAllText(soupPsc), @"^(Int|Float)\s+Function\s+S_(\w+)\(([^)]*)\)", RegexOptions.Multiline))
        {
            int arity = m.Groups[3].Value.Trim().Length == 0 ? 0 : m.Groups[3].Value.Split(',').Length;
            lib[m.Groups[2].Value] = new LibFunction(m.Groups[2].Value, m.Groups[1].Value == "Float", arity);
        }
        return lib;
    }

    /// <summary>
    /// What each SOUP function became, when not Skyrim's own behaviour: approximations, and functions with no Skyrim
    /// counterpart (left as calls to TMRgSoup.S_None or to library functions that do nothing).
    /// </summary>
    static readonly Dictionary<string, (Category Cat, string Why)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // The camera: Redguard's cutscene camera moves; the player's camera stays (Papyrus has no camera placement).
        ["showObj"] = (Category.NoEffect, "camera"), ["showObjLoc"] = (Category.NoEffect, "camera"), ["showObjPan"] = (Category.NoEffect, "camera"),
        ["showObjPanLoc"] = (Category.NoEffect, "camera"), ["showObjRot"] = (Category.NoEffect, "camera"), ["lookObj"] = (Category.NoEffect, "camera"),
        ["showPlayer"] = (Category.NoEffect, "camera"), ["showPlayerPan"] = (Category.NoEffect, "camera"), ["lookCyrus"] = (Category.NoEffect, "camera"),
        ["showCyrus"] = (Category.NoEffect, "camera"), ["showCyrusLoc"] = (Category.NoEffect, "camera"), ["showCyrusPan"] = (Category.NoEffect, "camera"),
        ["showCyrusPanLoc"] = (Category.NoEffect, "camera"), ["XResetCamera"] = (Category.NoEffect, "camera"), ["updatePlayerViewAttrib"] = (Category.NoEffect, "camera"),
        ["cameraController"] = (Category.NoEffect, "camera"),
        // Redguard's animation numbers and hand items: Skyrim's animations are its own (times kept by the frame waits).
        ["PushAnimation"] = (Category.Approximate, "animation"), ["PushControlAnimation"] = (Category.Approximate, "animation"),
        ["PlayAnimation"] = (Category.Approximate, "animation"), ["WaitAnimFrame"] = (Category.Approximate, "animation time"),
        ["WaitPlayerAnimFrame"] = (Category.Approximate, "animation time"), ["SetAction"] = (Category.NoEffect, "animation"),
        ["ResetAction"] = (Category.NoEffect, "animation"), ["ResetAllAction"] = (Category.NoEffect, "animation"), ["HoldAnimation"] = (Category.NoEffect, "animation"),
        ["ReleaseAnimation"] = (Category.NoEffect, "animation"), ["PauseAnimation"] = (Category.NoEffect, "animation"), ["HandItem"] = (Category.NoEffect, "hand item"),
        ["DisplayHandItem"] = (Category.NoEffect, "hand item"), ["DisplayHandModel"] = (Category.NoEffect, "hand item"), ["DisplayHiltItem"] = (Category.NoEffect, "hand item"),
        ["DisplayHiltModel"] = (Category.NoEffect, "hand item"), ["ShowItem"] = (Category.NoEffect, "item close-up"), ["ShowItemNoRtx"] = (Category.NoEffect, "item close-up"),
        // Sprite flats (MPSF/flat objects aren't converted) and the 2D screen.
        ["Flat"] = (Category.NoEffect, "flat"), ["FlatSetTexture"] = (Category.NoEffect, "flat"), ["FlatOff"] = (Category.NoEffect, "flat"), ["FlatOffset"] = (Category.NoEffect, "flat"),
        ["FlatLikeStatic"] = (Category.NoEffect, "flat"), ["FlatAnimate"] = (Category.NoEffect, "flat"), ["FlatStop"] = (Category.NoEffect, "flat"),
        ["ShowBitmap"] = (Category.NoEffect, "screen picture"), ["UnShowBitmap"] = (Category.NoEffect, "screen picture"), ["ViewGXA"] = (Category.NoEffect, "screen picture"),
        ["AnimateGXA"] = (Category.NoEffect, "screen picture"), ["QueueMovie"] = (Category.NoEffect, "movie"),
        // Light tuning (on/off are Skyrim's).
        ["LightRadius"] = (Category.NoEffect, "light tuning"), ["LightIntensity"] = (Category.NoEffect, "light tuning"), ["LightOffset"] = (Category.NoEffect, "light tuning"),
        ["LightFlicker"] = (Category.NoEffect, "light tuning"), ["FlickerLight"] = (Category.NoEffect, "light tuning"), ["LightFlickerOff"] = (Category.NoEffect, "light tuning"),
        ["LightSize"] = (Category.NoEffect, "light tuning"), ["LightSizeOff"] = (Category.NoEffect, "light tuning"),
        // Redguard's AI internals (Skyrim's AI packages do this).
        ["SetAiType"] = (Category.NoEffect, "AI"), ["SetAiMode"] = (Category.NoEffect, "AI"), ["Guard"] = (Category.NoEffect, "AI"), ["Animal"] = (Category.NoEffect, "AI"),
        ["Wander"] = (Category.NoEffect, "AI"), ["projectileAI"] = (Category.NoEffect, "AI"), ["swordAI"] = (Category.NoEffect, "AI"), ["chargeWeapon"] = (Category.NoEffect, "AI"),
        ["DisableNodeMaps"] = (Category.NoEffect, "AI"), ["EnableNodeMaps"] = (Category.NoEffect, "AI"), ["OpenShell"] = (Category.NoEffect, "AI"),
        // Debugging (developer keys and printouts).
        ["InKey"] = (Category.NoEffect, "debug key"), ["PrintSingleParm"] = (Category.Native, "debug print"), ["PrintParms"] = (Category.NoEffect, "debug print"),
        ["PrintStringParm"] = (Category.NoEffect, "debug print"), ["LogParms"] = (Category.NoEffect, "debug print"),
        // Approximations.
        ["SyncWithGroup"] = (Category.Approximate, "group sync"), ["SyncWithUser"] = (Category.Approximate, "group sync"), ["ResetGroupSync"] = (Category.NoEffect, "group sync"),
        ["ChangeGroup"] = (Category.NoEffect, "group sync"), ["PlayerLineUp"] = (Category.Approximate, "line up"), ["PlayerPickupLineUp"] = (Category.Approximate, "line up"),
        ["PlayerParallelLineUp"] = (Category.Approximate, "line up"), ["PushPlayer"] = (Category.NoEffect, "line up"), ["PopPlayer"] = (Category.NoEffect, "line up"),
        ["MovePlayerAboveMe"] = (Category.NoEffect, "line up"), ["MoveAboveMe"] = (Category.NoEffect, "line up"), ["Offset"] = (Category.NoEffect, "attach offset"),
        ["UnOffset"] = (Category.NoEffect, "attach offset"), ["PlayerOffset"] = (Category.NoEffect, "attach offset"), ["PlayerOffsettask"] = (Category.NoEffect, "attach offset"),
        ["BounceObject"] = (Category.NoEffect, "bounce"), ["SetObjectLocation"] = (Category.NoEffect, "location edit"), ["OffsetLocation"] = (Category.NoEffect, "location edit"),
        ["PlayerHanging"] = (Category.Approximate, "no ledge hanging"), ["PlayerStatus"] = (Category.Approximate, "player status"), ["PlayerStand"] = (Category.Approximate, "standing on"),
        ["PlayerCollide"] = (Category.Approximate, "touching"), ["PlayerCollideStatic"] = (Category.Approximate, "touching"), ["CollidePlayerWeapon"] = (Category.Approximate, "weapon hit"),
        ["shoot"] = (Category.Approximate, "projectile spell"), ["shootPlayer"] = (Category.Approximate, "projectile spell"), ["DragonBreath"] = (Category.Approximate, "breath spell"),
        ["DragonBreathTask"] = (Category.Approximate, "breath spell"), ["GolemSteam"] = (Category.NoEffect, "particles"), ["AddFlatEffect"] = (Category.NoEffect, "particles"),
        ["ScriptParticles"] = (Category.NoEffect, "particles"), ["AddLightEffect"] = (Category.NoEffect, "particles"),
        ["MoveMarker"] = (Category.Approximate, "path to marker"), ["MoveNodeMarker"] = (Category.Approximate, "path to marker"), ["MoveNodeLocation"] = (Category.Approximate, "path"),
        ["WalkForward"] = (Category.Approximate, "walk"), ["WalkBackward"] = (Category.Approximate, "walk"), ["MoveForward"] = (Category.Approximate, "walk"),
        ["FacePlayer"] = (Category.Approximate, "turn"), ["FacePlayerInertia"] = (Category.Approximate, "turn"), ["FaceObject"] = (Category.Approximate, "turn"),
        ["PlayerFaceObj"] = (Category.Approximate, "turn"), ["DropAllItems"] = (Category.NoEffect, "the player keeps their things"), ["PushAllItems"] = (Category.NoEffect, "item stash"),
        ["ItemUsed"] = (Category.NoEffect, "use count"), ["SelectItem"] = (Category.Native, "selection"),
        ["SetAttribute"] = (Category.Approximate, "attributes"), ["setStrength"] = (Category.Approximate, "strength"), ["strength"] = (Category.Approximate, "strength"),
        ["setArmor"] = (Category.Approximate, "armor"), ["armor"] = (Category.Approximate, "armor"), ["InRectangle"] = (Category.Approximate, "unstretched rectangle"),
        ["WaitNonMulti"] = (Category.Native, "wait"), ["GetFramePassed"] = (Category.Approximate, "frames"), ["EndSound"] = (Category.Approximate, "sound"),
        ["EndMySound"] = (Category.Approximate, "sound"), ["StopAllSounds"] = (Category.Approximate, "sound"), ["KillMyTasks"] = (Category.Native, "tasks"),
    };

    /// <summary>SOUP386.DEF [refs] fields TMRgSoup.GetField/SetField know (1-9); others read 0 and are not written.</summary>
    static readonly Dictionary<string, int> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pos.vx"] = 1, ["pos.vy"] = 2, ["pos.vz"] = 3, ["angle.vx"] = 4, ["angle.vy"] = 5, ["angle.vz"] = 6, ["hide"] = 7, ["dead"] = 8, ["torch_timer"] = 9,
    };

    readonly List<string> functionNames, refNames;
    readonly HashSet<string> taskFunctions;
    readonly Dictionary<string, LibFunction> lib;

    public RgPapyrus(List<string> functionNames, HashSet<string> taskFunctions, List<string> refNames, Dictionary<string, LibFunction> lib)
    {
        this.functionNames = functionNames;
        this.taskFunctions = taskFunctions;
        this.refNames = refNames;
        this.lib = lib;
    }

    /// <summary>SOUP386.DEF's task functions (the script waits on them) and [refs] names.</summary>
    public static (HashSet<string> Tasks, List<string> Refs) DefInfo(string soupDef)
    {
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<string>();
        string section = "";
        foreach (var raw in File.ReadLines(soupDef))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { section = line; continue; }
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (section == "[functions]")
            {
                var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("task", StringComparison.OrdinalIgnoreCase)) tasks.Add(parts[1]);
            }
            else if (section == "[refs]") refs.Add(line);
        }
        return (tasks, refs);
    }

    // ---- flattening ----

    abstract record Ins(Stmt Source);
    sealed record Exec(Stmt Source) : Ins(Source);
    sealed record Cond(IfStmt If) : Ins(If) { public int False; }
    sealed record Jump(JumpStmt J) : Ins(J);

    static void Flatten(List<Stmt> list, List<Ins> into)
    {
        foreach (var s in list)
        {
            if (s is IfStmt f)
            {
                var c = new Cond(f);
                into.Add(c);
                Flatten(f.Body, into);
                c.False = into.Count;
            }
            else if (s is JumpStmt j) into.Add(new Jump(j));
            else into.Add(new Exec(s));
        }
    }

    // ---- translation ----

    sealed class State
    {
        public required string Actor;
        public required List<string> Strings;
        public required Context Ctx;
        public required HashSet<int> GlobalLocals;
        public readonly List<string> Objects = [];
        public readonly List<(int? Map, int? Marker, int? Facing)> Places = [];
        public readonly List<int> UseItems = [];
        public readonly Dictionary<Category, int> Counts = [];
        public readonly Dictionary<string, Dictionary<Category, int>> ByFunction = [];
        public readonly HashSet<int> LocalsUsed = [];
        public bool InDialogue;
        public int Temp;
        public readonly List<List<int>> Menus = [];
        public int Menu = -1;
        /// <summary>A statement (counted in Counts) or, with statement false, a function used in a value.</summary>
        public void Count(string what, Category c, bool statement = true)
        {
            if (statement) Counts[c] = Counts.GetValueOrDefault(c) + 1;
            var d = ByFunction.TryGetValue(what, out var x) ? x : ByFunction[what] = [];
            d[c] = d.GetValueOrDefault(c) + 1;
        }
    }

    /// <param name="talks">The object is an actor whose conversations are Skyrim dialogue: code reached only by
    /// activating it (its talk) counts as the dialogue's.</param>
    /// <param name="globalLocals">Variables the converted dialogue already tests as globals (RGVar...), read and set as
    /// those (properties G&lt;n&gt;).</param>
    public Output Translate(string scriptName, string description, List<Stmt> program, int entryAddress, int[] locals, List<string> strings,
        Context ctx, bool talks, HashSet<int> globalLocals)
    {
        var ins = new List<Ins>();
        Flatten(program, ins);
        var st = new State { Actor = scriptName, Strings = strings, Ctx = ctx, GlobalLocals = globalLocals };
        var byAddress = new SortedDictionary<int, int>();
        for (int i = 0; i < ins.Count; i++) byAddress.TryAdd(ins[i].Source.Address, i);
        int IndexOf(int address)
        {
            if (byAddress.TryGetValue(address, out var i)) return i;
            foreach (var (a, k) in byAddress) if (a >= address) return k;
            return -1;
        }

        // Blocks: start at 0, the entry, jump targets, after conditions, jumps and tasks.
        var leaders = new SortedSet<int> { 0 };
        int entry = entryAddress > 0 ? IndexOf(entryAddress) : 0;
        if (entry >= 0) leaders.Add(entry);
        for (int i = 0; i < ins.Count; i++)
        {
            switch (ins[i])
            {
                case Cond c: leaders.Add(i + 1); leaders.Add(c.False); break;
                case Jump j:
                    leaders.Add(i + 1);
                    if (j.J.Kind is "goto" or "end" or "gosub" && IndexOf(j.J.Target) is >= 0 and var t) leaders.Add(t);
                    break;
                case Exec { Source: CallStmt call } when IsWait(call): leaders.Add(i + 1); break;
                case Exec { Source: OtherStmt { What: "return" or "endint" } }: leaders.Add(i + 1); break;
            }
        }
        leaders.RemoveWhere(l => l >= ins.Count);
        var starts = leaders.ToList();

        // A talking actor's conversation: the code reached from Cyrus being lined up to talk (PlayerLineUp, where the
        // converted dialogue starts too) that the rest of the script doesn't reach on its own.
        var conversation = new HashSet<int>();
        if (talks)
        {
            int lineUp = functionNames.IndexOf("PlayerLineUp");
            bool IsLineUp(int i) => ins[i] is Exec { Source: CallStmt c } && c.Function == lineUp;
            HashSet<int> Reach(IEnumerable<int> from, bool throughLineUp)
            {
                var seen = new HashSet<int>();
                var work = new Stack<int>(from);
                while (work.Count > 0)
                {
                    int i = work.Pop();
                    while (i >= 0 && i < ins.Count && seen.Add(i))
                    {
                        if (!throughLineUp && IsLineUp(i)) break;
                        if (ins[i] is Cond c) { work.Push(c.False); i++; continue; }
                        if (ins[i] is Jump j)
                        {
                            int t = IndexOf(j.J.Target);
                            if (j.J.Kind is "gosub") { work.Push(i + 1); i = t; continue; }
                            if (j.J.Kind is "goto" or "end") { i = t; continue; }
                            i++;
                            continue;
                        }
                        if (ins[i] is Exec { Source: OtherStmt { What: "return" or "endint" } }) break;
                        i++;
                    }
                }
                return seen;
            }
            var plain = Reach(entryAddress > 0 ? [entry >= 0 ? entry : 0, 0] : [entry >= 0 ? entry : 0], throughLineUp: false);
            var talk = Reach(Enumerable.Range(0, ins.Count).Where(IsLineUp), throughLineUp: true);
            conversation = talk.Where(i => !plain.Contains(i)).ToHashSet();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"ScriptName {scriptName} extends TMRgSoup");
        sb.AppendLine("{" + description.Replace("{", "(").Replace("}", ")") + "}");
        sb.AppendLine();
        var blocks = new StringBuilder();
        for (int b = 0; b < starts.Count; b++)
        {
            int from = starts[b], to = b + 1 < starts.Count ? starts[b + 1] : ins.Count;
            st.Temp = 0;
            var body = new StringBuilder();
            for (int i = from; i < to; i++)
            {
                st.InDialogue = conversation.Contains(i);
                Instruction(st, ins, i, to, IndexOf, body);
            }
            blocks.AppendLine($"Function B{from}()");
            blocks.Append(body);
            blocks.AppendLine("EndFunction");
            blocks.AppendLine();
        }
        // Variables: the script's own, or the dialogue's globals.
        for (int v = 0; v < Math.Max(locals.Length, st.LocalsUsed.Count == 0 ? 0 : st.LocalsUsed.Max() + 1); v++)
        {
            if (globalLocals.Contains(v)) sb.AppendLine($"GlobalVariable Property G{v} Auto");
            else sb.AppendLine($"Int v{v} = {(v < locals.Length ? locals[v] : 0)}");
        }
        sb.AppendLine();
        sb.AppendLine("Function Dispatch(Int b)");
        Dispatch(sb, starts, 0, starts.Count - 1, "  ");
        sb.AppendLine("EndFunction");
        sb.AppendLine();
        sb.Append(blocks);
        int statements = st.Counts.Values.Sum();
        return new Output(scriptName, sb.ToString(), statements, st.Counts, st.ByFunction, st.Objects, st.Places, st.UseItems,
            globalLocals, entry, entryAddress > 0) { Menus = st.Menus };
    }

    static void Dispatch(StringBuilder sb, List<int> starts, int lo, int hi, string ind)
    {
        if (lo > hi) return;
        if (hi - lo < 4)
        {
            for (int k = lo; k <= hi; k++)
            {
                sb.AppendLine($"{ind}{(k == lo ? "If" : "ElseIf")} b == {starts[k]}");
                sb.AppendLine($"{ind}  B{starts[k]}()");
            }
            sb.AppendLine($"{ind}Else");
            sb.AppendLine($"{ind}  Halt()");
            sb.AppendLine($"{ind}EndIf");
            return;
        }
        int mid = (lo + hi + 1) / 2;
        sb.AppendLine($"{ind}If b < {starts[mid]}");
        Dispatch(sb, starts, lo, mid - 1, ind + "  ");
        sb.AppendLine($"{ind}Else");
        Dispatch(sb, starts, mid, hi, ind + "  ");
        sb.AppendLine($"{ind}EndIf");
    }

    bool IsWait(CallStmt call)
    {
        string name = functionNames.ElementAtOrDefault(call.Function) ?? "";
        return call.Kind == 0 && (taskFunctions.Contains(name) || name is "RTX" or "rtxAnim" or "RTXp" or "RTXpAnim");
    }

    /// <summary>A condition that holds only when the object is activated (an and-chain with ACTIVATE() = 1).</summary>
    bool Activates(IfStmt f) =>
        f.Conditions.All(c => c.Conj is 0 or 1) &&
        f.Conditions.Any(c => c.Lhs is FnValue fv && functionNames.ElementAtOrDefault(fv.Function) is "ACTIVATE" && c.Cmp == 0 && c.Rhs is Num { V: 1 });

    void Instruction(State st, List<Ins> ins, int i, int blockEnd, Func<int, int> indexOf, StringBuilder o)
    {
        bool last = i == blockEnd - 1;
        int next = i + 1 < ins.Count ? i + 1 : -1;
        string Target(int address) => indexOf(address) is var t && t >= 0 ? t.ToString() : "-1";
        switch (ins[i])
        {
            case Cond c:
            {
                st.Count("if", st.InDialogue ? Category.Dialogue : Category.Native);
                var pre = new StringBuilder();
                string cond = Condition(st, c.If, pre);
                o.Append(pre);
                o.AppendLine($"  If {cond}");
                o.AppendLine($"    Go({(next < 0 ? -1 : next)})");
                o.AppendLine("  Else");
                o.AppendLine($"    Go({(c.False < ins.Count ? c.False : -1)})");
                o.AppendLine("  EndIf");
                return;
            }
            case Jump j:
                // "pause a" (Redguard's task pause): holds the main thread's task at address a, for an interrupt to
                // answer at once; here threads take turns anyway, so it goes on.
                st.Count(j.J.Kind, st.InDialogue ? Category.Dialogue : j.J.Kind == "pause" ? Category.Approximate : Category.Native);
                switch (j.J.Kind)
                {
                    case "goto": o.AppendLine($"  Go({Target(j.J.Target)})"); break;
                    case "end": o.AppendLine($"  EndTo({Target(j.J.Target)})"); break;
                    case "pause": o.AppendLine($"  Go({next}) ; pause {j.J.Target}"); break;
                    case "gosub": o.AppendLine($"  Gosub({Target(j.J.Target)}, {next})"); break;
                    default: o.AppendLine($"  Go({next})"); break;
                }
                return;
            case Exec e:
            {
                bool transferred = Statement(st, e.Source, next, o);
                if (last && !transferred) o.AppendLine($"  Go({next})");
                return;
            }
        }
    }

    /// <summary>Writes a statement; true when it set the next block itself (a task, return, endint).</summary>
    bool Statement(State st, Stmt s, int next, StringBuilder o)
    {
        switch (s)
        {
            case AssignStmt a:
            {
                st.Count("assign", st.InDialogue ? Category.Dialogue : Category.Native);
                var pre = new StringBuilder();
                string value = Formula(st, a.Formula, pre);
                o.Append(pre);
                o.AppendLine("  " + Store(st, a.Target, value, pre));
                return false;
            }
            case OtherStmt { What: "return" }:
                st.Count("return", st.InDialogue ? Category.Dialogue : Category.Native);
                o.AppendLine("  Ret()");
                return true;
            case OtherStmt { What: "endint" }:
                st.Count("endint", st.InDialogue ? Category.Dialogue : Category.Native);
                o.AppendLine("  EndInt()");
                return true;
            case OtherStmt other:
                // A stray value or Redguard's script return value (scriptrv): nothing happens.
                st.Count(other.What, st.InDialogue ? Category.Dialogue : Category.NoEffect);
                o.AppendLine($"  ; {other.What}");
                return false;
            case CallStmt call:
                return Call(st, call, next, o);
        }
        return false;
    }

    string Store(State st, Value target, string value, StringBuilder pre)
    {
        switch (target)
        {
            case Flag f: return $"SetFlag({f.Index}, {value})";
            case Local l:
                st.LocalsUsed.Add(l.Index);
                return st.GlobalLocals.Contains(l.Index) ? $"G{l.Index}.SetValueInt({value})" : $"v{l.Index} = {value}";
            case Other o when ParseField(o.What) is { } fr:
                return fr.Field > 0 ? $"SetField({ObjectExpr(st, fr.Obj)}, {fr.Field}, {value})" : $"; {o.What} = {value} (no Skyrim counterpart)";
        }
        return $"; {target} = {value}";
    }

    (string Obj, int Field)? ParseField(string what)
    {
        int at = what.LastIndexOf(".ref", StringComparison.Ordinal);
        if (at < 0 || !int.TryParse(what[(at + 4)..], out int index)) return null;
        string name = index < refNames.Count ? refNames[index] : "";
        return (what[..at], Fields.GetValueOrDefault(name));
    }

    string ObjectExpr(State st, string obj) => obj switch
    {
        "Me" => "Home()",
        "Player" => "Game.GetPlayer()",
        "Camera" => "None",
        _ when obj.StartsWith("string:") && int.TryParse(obj[7..], out var si) => $"Obj({ObjectSlot(st, si)})",
        _ => "None",
    };

    static int ObjectSlot(State st, int stringIndex)
    {
        string name = stringIndex < st.Strings.Count ? st.Strings[stringIndex] : "";
        int i = st.Objects.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { st.Objects.Add(name); i = st.Objects.Count - 1; }
        return i;
    }

    string Formula(State st, List<(Value Value, int Op)> parts, StringBuilder pre)
    {
        if (parts.Count == 0) return "0";
        string acc = Expr(st, parts[0].Value, pre);
        for (int k = 1; k < parts.Count; k++)
            acc = Apply(acc, parts[k - 1].Op, Expr(st, parts[k].Value, pre));
        int lastOp = parts[^1].Op;
        if (lastOp == 10) acc = $"({acc} + 1)";
        else if (lastOp == 11) acc = $"({acc} - 1)";
        return acc;
    }

    static string Apply(string a, int op, string b) => op switch
    {
        1 => $"({a} + {b})",
        2 => $"({a} - {b})",
        3 => $"({a} * {b})",
        4 => $"Div({a}, {b})",
        5 => $"Shl({a}, {b})",
        6 => $"Shr({a}, {b})",
        7 => $"BitAnd({a}, {b})",
        8 => $"BitOr({a}, {b})",
        9 => $"BitXor({a}, {b})",
        _ => a,
    };

    string Condition(State st, IfStmt f, StringBuilder pre)
    {
        string? acc = null;
        int conj = 0;
        foreach (var (lhs, cmp, rhs, c) in f.Conditions)
        {
            string l = Expr(st, lhs, pre), r = Expr(st, rhs, pre);
            string op = cmp switch { 0 => "==", 1 => "!=", 2 => "<", 3 => ">", 4 => "<=", _ => ">=" };
            string term = $"({l} {op} {r})";
            acc = acc is null ? term : conj == 2 ? $"({acc} || {term})" : $"({acc} && {term})";
            conj = c;
        }
        return acc ?? "True";
    }

    string Expr(State st, Value v, StringBuilder pre)
    {
        switch (v)
        {
            case Num n: return n.V.ToString();
            case Flag f: return $"Flag({f.Index})";
            case Local l:
                st.LocalsUsed.Add(l.Index);
                return st.GlobalLocals.Contains(l.Index) ? $"G{l.Index}.GetValueInt()" : $"v{l.Index}";
            case FnValue fv:
            {
                string name = functionNames.ElementAtOrDefault(fv.Function) ?? $"fn{fv.Function}";
                var (call, cat) = FunctionCall(st, name, fv.Args, pre, isValue: true);
                st.Count(name, st.InDialogue ? Category.Dialogue : cat, statement: false);
                string value = lib.TryGetValue(name, out var lf) && lf.ReturnsFloat ? $"({call} as Int)" : call;
                if (fv.Target is { } target && target != "Me")
                {
                    // Another object's value: worked out before the condition, on that object.
                    string t = $"t{st.Temp++}";
                    pre.AppendLine($"  {On(st, target)}");
                    pre.AppendLine($"  Int {t} = {value}");
                    pre.AppendLine("  On(None)");
                    return t;
                }
                return value;
            }
            case Other o when ParseField(o.What) is { } fr:
                return fr.Field > 0 ? $"GetField({ObjectExpr(st, fr.Obj)}, {fr.Field})" : "0";
            default:
                return "0";
        }
    }

    string On(State st, string target) => target switch
    {
        "Player" => "OnPlayer()",
        "Me" => "On(None)",
        _ => $"On({ObjectExpr(st, target)})",
    };

    /// <summary>A call statement; true when it ends the block (it waits, or is the thread's end).</summary>
    bool Call(State st, CallStmt call, int next, StringBuilder o)
    {
        string name = functionNames.ElementAtOrDefault(call.Function) ?? $"fn{call.Function}";
        var pre = new StringBuilder();
        if (call.Target == "Camera")
        {
            // The camera as an object: it has no counterpart.
            st.Count(name, st.InDialogue ? Category.Dialogue : Category.NoEffect);
            o.AppendLine($"  ; Camera.{name}()");
            if (IsWait(call)) { o.AppendLine($"  Task(0.0, {next})"); return true; }
            return false;
        }
        var (expr, cat) = FunctionCall(st, name, call.Args, pre, isValue: false);
        st.Count(name, st.InDialogue ? Category.Dialogue : cat);
        o.Append(pre);
        bool other = call.Target is { } tg && tg != "Me";
        if (other) o.AppendLine($"  {On(st, call.Target!)}");
        bool floatResult = expr.StartsWith("Line(") || (lib.TryGetValue(name, out var lf) && lf.ReturnsFloat && expr.StartsWith($"S_{name}("));
        if (IsWait(call) && floatResult)
        {
            o.AppendLine($"  Task({expr}, {next})");
            if (other) o.AppendLine("  On(None)");
            return true;
        }
        if (call.Kind == 1 && floatResult) o.AppendLine($"  Multi({expr})");
        else o.AppendLine($"  {expr}");
        if (other) o.AppendLine("  On(None)");
        if (IsWait(call)) { o.AppendLine($"  Task(0.0, {next})"); return true; }
        return false;
    }

    static bool IsCamera(string name) => name.StartsWith("show", StringComparison.Ordinal) || name.StartsWith("look", StringComparison.Ordinal)
        || name is "XResetCamera" or "updatePlayerViewAttrib" or "cameraController";

    /// <summary>A SOUP function as a Papyrus call and what it became.</summary>
    (string Call, Category Cat) FunctionCall(State st, string name, Value[] args, StringBuilder pre, bool isValue)
    {
        int? Const(int i) => i < args.Length && args[i] is Num n ? n.V : null;
        string Arg(int i) => i < args.Length ? Expr(st, args[i], pre) : "0";
        var kind = Kinds.TryGetValue(name, out var k) ? k.Cat : Category.Native;
        switch (name)
        {
            case "RTX": case "rtxAnim": case "RTXp": case "RTXpAnim": case "AmbientRtx":
            {
                bool cyrus = name.StartsWith("RTXp");
                if (Const(0) is not int tag) return ("Line(-1, \"\", 0.0, False)", Category.Approximate);
                var (index, text, seconds) = st.InDialogue ? (-1, "", 0f) : st.Ctx.Line(tag, cyrus);
                string sec = seconds.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
                return ($"Line({index}, \"{Escape(text)}\", {sec}, {(cyrus ? "True" : "False")})", Category.Native);
            }
            case "AddLog":
                return Const(0) is int log ? ($"S_AddLog({st.Ctx.LogStage(log)})", Category.Native) : ("S_AddLog(0)", Category.Approximate);
            case "LoadWorld":
            {
                if (st.Ctx.LoadWorldIsDoor(Const(0), Const(1))) return ("S_None() ; LoadWorld: the load door here does this", Category.Native);
                st.Places.Add((Const(0), Const(1), Const(2)));
                return ($"S_LoadWorld({st.Places.Count - 1})", Const(0) is null ? Category.Approximate : Category.Native);
            }
            case "ACTIVATE": case "TorchActivate":
                return ($"S_{name}(0)", Category.Native);
            // A menu outside Skyrim's dialogue: a message box whose buttons are the items added (TMRgSoup.S_menu*).
            case "menuNew":
                if (st.InDialogue) return ("S_None() ; menuNew", Category.Dialogue);
                st.Menus.Add([]);
                st.Menu = st.Menus.Count - 1;
                return ("S_menuNew()", Category.Native);
            case "menuAddItem":
            {
                if (st.InDialogue || st.Menu < 0 || Const(0) is not int tag) { for (int i = 0; i < args.Length; i++) Arg(i); return ("S_None() ; menuAddItem", Category.Approximate); }
                var items = st.Menus[st.Menu];
                int slot = items.IndexOf(tag);
                if (slot < 0 && items.Count < 9) { items.Add(tag); slot = items.Count - 1; }
                if (slot < 0) return ("S_None() ; menuAddItem (more than nine items)", Category.Approximate);
                return ($"S_menuAddItem({slot}, {Arg(2)})", Category.Native);
            }
            case "menuProc":
                if (st.InDialogue || st.Menu < 0) return ("S_None() ; menuProc", st.InDialogue ? Category.Dialogue : Category.Approximate);
                return ($"S_menuProc({st.Menu})", Category.Native);
            case "menuSelection":
                return ("S_menuSelection()", Category.Native);
            case "ActiveItem":
                return ("S_ActiveItem()", Category.Native);
        }
        if (IsCamera(name))
        {
            if (lib.ContainsKey(name)) return ($"S_{name}()", Category.NoEffect);
            return (isValue ? "0" : "S_Camera()", Category.NoEffect);
        }
        if (lib.TryGetValue(name, out var f))
        {
            var list = new List<string>();
            for (int i = 0; i < f.Arity; i++) list.Add(Arg(i));
            // Arguments a call passes beyond the library's (none in the shipped scripts) are evaluated for effect only.
            for (int i = f.Arity; i < args.Length; i++) Arg(i);
            return ($"S_{name}({string.Join(", ", list)})", kind);
        }
        for (int i = 0; i < args.Length; i++) Arg(i);
        // Nothing in Skyrim for it: a visible no-op.
        return (isValue ? "0" : $"S_None() ; {name}", Kinds.ContainsKey(name) ? kind : Category.NoEffect);
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");

    /// <summary>The items a script compares the selected item with (ActiveItem() = n), those compared alone (the one
    /// a script acts on) first.</summary>
    public List<int> UseItemsOf(List<Stmt> program)
    {
        var counts = new Dictionary<int, (int Alone, int First)>();
        int order = 0;
        void Walk(List<Stmt> l)
        {
            foreach (var s in l)
            {
                if (s is not IfStmt f) continue;
                foreach (var c in f.Conditions)
                    if (c.Lhs is FnValue fv && functionNames.ElementAtOrDefault(fv.Function) == "ActiveItem" && c.Cmp == 0 && c.Rhs is Num n && n.V >= 0)
                    {
                        var x = counts.GetValueOrDefault(n.V, (0, order++));
                        counts[n.V] = (x.Item1 + (f.Conditions.Count == 1 ? 1 : 0), x.Item2);
                    }
                Walk(f.Body);
            }
        }
        Walk(program);
        return counts.OrderByDescending(kv => kv.Value.Alone).ThenBy(kv => kv.Value.First).Select(kv => kv.Key).ToList();
    }
}
