using System.Globalization;
using System.Text;
using static TamrielMerge.Morrowind.MwScript;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's scripts (parsed by <see cref="MwScript"/>) translated into Papyrus.
/// <list type="bullet">
/// <item>A Morrowind script is one block run every frame while its object is in an active cell (or, as a global script,
/// while it runs). An object script becomes MW_&lt;name&gt; extends ObjectReference (Actor when only people and creatures
/// carry it) whose body is the function MwTick, run by OnUpdate on a short timer while the object's 3D is loaded or the
/// player carries it (0.5 s; 0.05 s for scripts that move or turn things). A script whose every statement waits on an
/// event (OnActivate, OnDeath...) doesn't poll at all: the event runs it.</item>
/// <item>Events set flags the script's own functions read once, as Morrowind's do: OnActivate (the default activation is
/// blocked while the script has OnActivate, and Activate performs it), OnDeath, OnMurder, OnKnockout, OnPCAdd, OnPCDrop,
/// OnPCEquip (the function and the special local of that name, with PCSkipEquip), OnPCHitMe; each runs MwTick at once.
/// GetPCSleep is true from Skyrim's sleep start to its end (the script runs at the start).</item>
/// <item>A global script (StartScript, start scripts) becomes MWG_&lt;name&gt; extends Quest on a quest of its own that
/// polls while it runs: StartScript starts it, StopScript stops it, ScriptRunning is IsRunning. Its implicit reference is
/// MwSelf (set by a targeted StartScript). Start scripts only run while the player is in Morrowind's lands.</item>
/// <item>A dialogue response's result script becomes a TopicInfo fragment, MWR_&lt;hash&gt;, run as the line ends, its
/// implicit reference the speaker; the speaker's own locals are reached through its script.</item>
/// <item>Locals become properties (v_&lt;name&gt;) so other scripts can read and set them ("ID.variable"); locals the
/// dialogue tests are mirrored as the rank of a hidden faction (MWLocal&lt;name&gt;) the dialogue conditions read.</item>
/// <item>Names the script uses become properties the converter fills: references by ID (r_, persistent in the plugin),
/// base objects (f_), globals (g_), journal quests (q_), global scripts' quests (s_), cells, factions, spells, sounds,
/// and records it makes for the scripts (message boxes with buttons, Say lines, anchors for PositionCell).</item>
/// <item>Functions map to Papyrus and SKSE; shared helpers live in TMMwScript (global functions) and TMMwAI (AI
/// packages: travel, follow, escort, wander, activate). What Skyrim has no counterpart for is translated to the
/// nearest behaviour, documented beside it; anything else is kept as a comment and counted.</item>
/// </list>
/// </summary>
public sealed partial class MwPapyrus(MwPapyrus.IWorld world)
{
    public enum Kind { Object, Global, Result }

    /// <summary>What the translator needs to know about the game's data.</summary>
    public interface IWorld
    {
        /// <summary>The record type of an object ID (NPC_, CREA, MISC, GLOB, SCPT, SOUN, FACT, SPEL, REGN...), or null.</summary>
        string? TypeOf(string id);
        /// <summary>Whether a reference of this object ID is placed somewhere.</summary>
        bool IsPlaced(string id);
        /// <summary>The script an object ID carries, or null.</summary>
        string? ScriptOf(string objectId);
        /// <summary>A script's locals (name → short/long/float), or null for an unknown script.</summary>
        IReadOnlyDictionary<string, string>? Variables(string scriptId);
        bool IsJournal(string id);
        /// <summary>A script's ID (scripts and globals have their own namespaces: a global may share a script's name).</summary>
        bool IsScript(string id);
        bool IsGlobalVariable(string id);
        /// <summary>A faction by its ID, or by its name as scripts sometimes write it ("House Telvanni").</summary>
        string? FactionId(string idOrName);
        /// <summary>The interior cell of that name (or the first whose name starts with it), or null.</summary>
        string? InteriorNamed(string name);
        /// <summary>Whether the name is an exterior cell's (a town's, a region's): positions there are the world's.</summary>
        bool IsExteriorName(string name);
        /// <summary>How many coins an item ID is (gold_005 is 5); 0 for anything else.</summary>
        int CoinsIn(string id);
        /// <summary>A sound's length in seconds (for GetSoundPlaying), estimated when unknown.</summary>
        float SoundSeconds(string soundId);
        string ObjectScriptName(string scriptId);
        string GlobalScriptName(string scriptId);
        /// <summary>Scripts that run as global scripts (StartScript, start scripts).</summary>
        bool IsGlobalScript(string scriptId);
        /// <summary>Locals the dialogue tests (mirrored as faction ranks).</summary>
        bool IsDialogueLocal(string variable);
        /// <summary>Scripts on people declaring a local of this name (a result script's speaker locals).</summary>
        IEnumerable<string> ScriptsDeclaring(string variable);
    }

    /// <param name="OnActor">Only people and creatures carry the (object) script.</param>
    /// <param name="IsItem">Items carry it (the player can carry them).</param>
    /// <param name="Speakers">NPC IDs whose voice a Say line needs (the carriers; for a result, the speaker).</param>
    /// <param name="SpeakerScript">A result script's speaker's script, when the response has one speaker.</param>
    /// <param name="Start">A start script (runs from the beginning, while the player is in Morrowind's lands).</param>
    /// <param name="SpeakerFaction">A result script's speaker's faction (for faction functions without one).</param>
    public sealed record Context(bool OnActor = false, bool IsItem = false, List<string>? Speakers = null, string? SpeakerScript = null, bool Start = false, string? SpeakerFaction = null);

    /// <summary>
    /// A script property and what fills it: <see cref="Fill"/> is "kind:id" - player, ref:ID (a placed reference),
    /// base:ID (a base object), global:ID, journal:ID, script:ID (a global script's quest), cells:name (a FormList of the
    /// cells of that name), anchor:cell (a marker in that cell, "anchor:" for the exterior), faction:ID, spell:ID,
    /// sound:ID, loopsound:ID, voice:file (a sound playing a voice file), region:ID, race:ID, class:ID, message:EditorID,
    /// say:EditorID, local:variable (the faction mirroring a dialogue local), facrep:faction, gen:EditorID (a global the
    /// scripts keep), vanilla:EditorID (a Skyrim record), ai (the AI quest), diseases:blight / diseases:common.
    /// </summary>
    public sealed record Property(string Name, string Type, string Fill);
    public sealed record MessageForm(string EditorID, string Text, List<string> Buttons);
    /// <summary>A Say line: a topic (EditorID) with one response per speaker NPC (none: a sound and a subtitle).</summary>
    public sealed record SayLine(string EditorID, string File, string Text, List<string> Speakers);
    public sealed record Output(string ScriptName, string Extends, Kind Kind, string Source, List<Property> Properties, int Statements, int Translated,
        Dictionary<string, int> Missing, List<MessageForm> Messages, List<SayLine> Says, List<(string Journal, int Index)> JournalIndexes,
        List<string> Binks, bool Polls, bool Goodbye, bool UsesActivate);

    // ---- types ----

    enum T { Int, Float, Bool, String, Form, Ref, Actor, Void, Unknown }

    static string PapyrusType(T t) => t switch
    {
        T.Int => "Int", T.Float => "Float", T.Bool => "Bool", T.String => "String", T.Ref => "ObjectReference", T.Actor => "Actor", _ => "Form",
    };

    readonly record struct Code(string Text, T Type);

    public static string Clean(string id)
    {
        var sb = new StringBuilder();
        foreach (char c in id) sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    /// <summary>A Papyrus script name: prefix + the Morrowind name, shortened with a hash past 38 characters.</summary>
    public static string PapyrusName(string prefix, string id)
    {
        string name = prefix + Clean(id);
        if (name.Length <= 38) return name;
        return $"{name[..31]}_{Hash(id) & 0xFFFFFF:X6}";
    }

    public static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char ch in s.ToLowerInvariant()) h = (h ^ ch) * 16777619;
        return h;
    }

    // ---- per-script state ----

    Kind kind;
    Context ctx = new();
    string scriptId = "";
    readonly Dictionary<string, T> locals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Property> properties = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> propertyByFill = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> missing = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> uses = new(StringComparer.Ordinal);
    readonly List<MessageForm> messages = [];
    readonly List<SayLine> says = [];
    readonly List<(string, int)> journalIndexes = [];
    readonly List<string> binks = [];
    readonly HashSet<string> loopSounds = new(StringComparer.OrdinalIgnoreCase);
    int statements, translated;
    bool lineOk, goodbye;
    /// <summary>An object script checking OnActivate (which holds back the object's activation, as in Morrowind).</summary>
    bool checksActivate;

    /// <summary>Morrowind's special locals: set by the game (OnPCEquip...), or read by it (PCSkipEquip, companion...).</summary>
    static readonly HashSet<string> SpecialLocals = new(StringComparer.OrdinalIgnoreCase) { "OnPCEquip", "PCSkipEquip", "OnPCDrop", "OnPCAdd", "OnPCSoulGemUse", "OnPCRepair" };

    public Output Translate(Script script, string id, Kind scriptKind, Context? context = null)
    {
        kind = scriptKind; ctx = context ?? new Context(); scriptId = id;
        locals.Clear(); properties.Clear(); propertyByFill.Clear(); missing.Clear(); uses.Clear();
        messages.Clear(); says.Clear(); journalIndexes.Clear(); binks.Clear(); loopSounds.Clear(); speakerLocals.Clear();
        ScriptReadsButtons = script.AllCalls().Any(c => c.Function.Equals("GetButtonPressed", StringComparison.OrdinalIgnoreCase));
        checksActivate = kind == Kind.Object && script.AllCalls().Any(c => c.Target is null && c.Function.Equals("OnActivate", StringComparison.OrdinalIgnoreCase));
        statements = translated = 0;
        goodbye = false;
        foreach (var v in script.Variables) locals[v.Name] = v.Type.Equals("float", StringComparison.OrdinalIgnoreCase) ? T.Float : T.Int;

        string name = kind switch
        {
            Kind.Object => world.ObjectScriptName(id),
            Kind.Global => world.GlobalScriptName(id),
            _ => PapyrusName("MWR_", id),
        };
        // A result script extends TMMwInfo, whose properties (persuasion's disposition, gold and roll) it keeps.
        string extends = kind switch { Kind.Global => "Quest", Kind.Result => "TMMwInfo", _ => ctx.OnActor ? "Actor" : "ObjectReference" };

        var body = new StringBuilder();
        WriteStatements(body, script.Body, 1);

        bool polls = kind == Kind.Global || kind == Kind.Object && NeedsPolling(script.Body);
        float interval = MovesThings(script) ? 0.05f : 0.5f;

        // Properties the events need, before the events are written and the properties listed.
        if (kind == Kind.Object)
        {
            foreach (var v in locals.Keys.Where(v => world.IsDialogueLocal(v) && ctx.OnActor).ToList()) Generated("local:" + v, "Faction", "l_" + Clean(v));
            if (ctx.IsItem || uses.Overlaps(["onpcadd", "onpcdrop", "onpcequip", "onpchitme", "onmurder"]) || SpecialLocals.Any(locals.ContainsKey)) PlayerRef();
        }
        if (uses.Contains("cellchanged")) PlayerRef();
        var events = new StringBuilder();
        WriteEvents(events, polls, interval);
        var helpers = new StringBuilder();
        WriteHelpers(helpers);

        var head = new StringBuilder();
        head.AppendLine($"ScriptName {name} extends {extends}{(kind == Kind.Result ? " Hidden" : "")}");
        head.AppendLine(kind == Kind.Result
            ? $"{{Converted from a Morrowind dialogue result by TamrielMerge: {Summary(script)}}}"
            : $"{{Converted from Morrowind's {id} ({(kind == Kind.Global ? "global script" : "object script")}) by TamrielMerge.}}");
        head.AppendLine();
        if (kind != Kind.Result)
            foreach (var (v, t) in locals)
            {
                if (world.IsDialogueLocal(v) && kind == Kind.Object && ctx.OnActor)
                {
                    // Mirrored into a faction rank the dialogue's conditions read.
                    string f = Generated("local:" + v, "Faction", "l_" + Clean(v));
                    string pt = PapyrusType(t);
                    head.AppendLine($"{pt} __{Var(v)}");
                    head.AppendLine($"{pt} Property {Var(v)} Hidden");
                    head.AppendLine($"  {pt} Function Get()");
                    head.AppendLine($"    Return __{Var(v)}");
                    head.AppendLine("  EndFunction");
                    head.AppendLine($"  Function Set({pt} value)");
                    // The faction rank changes only when the whole number does.
                    string cast = t == T.Float ? " as Int" : "";
                    head.AppendLine($"    Bool changed = (value{cast}) != (__{Var(v)}{cast})");
                    head.AppendLine($"    __{Var(v)} = value");
                    head.AppendLine("    If changed");
                    head.AppendLine($"      TMMwScript.MirrorLocal(Self, {f}, value{cast})");
                    head.AppendLine("    EndIf");
                    head.AppendLine("  EndFunction");
                    head.AppendLine("EndProperty");
                    uses.Add("mirror");
                }
                else head.AppendLine($"{PapyrusType(t)} Property {Var(v)} Auto Hidden");
            }
        foreach (var p in properties.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            head.AppendLine($"{p.Type} Property {p.Name} Auto");
        if (kind == Kind.Global) head.AppendLine("ObjectReference Property MwSelf Auto Hidden");
        head.AppendLine();

        var tick = new StringBuilder();
        if (kind == Kind.Result)
        {
            tick.AppendLine("Function Fragment_End(ObjectReference akSpeakerRef)");
            tick.AppendLine("  Parent.Fragment_End(akSpeakerRef)");
            tick.Append(body);
            tick.AppendLine("EndFunction");
            tick.AppendLine();
            WriteSpeakerLocals(tick);
        }
        else
        {
            tick.AppendLine("Function MwTick()");
            if (uses.Contains("dt"))
            {
                tick.AppendLine("  Float now = Utility.GetCurrentRealTime()");
                tick.AppendLine("  __dt = 0.0");
                tick.AppendLine("  If __last >= 0.0");
                tick.AppendLine($"    __dt = now - __last");
                tick.AppendLine("  EndIf");
                // Time in menus, loading and sleep isn't play time; Morrowind's frames are short.
                tick.AppendLine($"  If __dt > {F(Math.Max(1f, interval * 4))}");
                tick.AppendLine($"    __dt = {F(interval)}");
                tick.AppendLine("  EndIf");
                tick.AppendLine("  __last = now");
            }
            if (uses.Contains("cellchanged"))
            {
                tick.AppendLine("  Cell here = PlayerRef.GetParentCell()");
                tick.AppendLine("  __cellChanged = __cellSeen && here != __lastCell");
                tick.AppendLine("  __lastCell = here");
                tick.AppendLine("  __cellSeen = True");
            }
            tick.Append(body);
            tick.AppendLine("EndFunction");
        }

        var output = head.Append(helpers).Append(events).Append(tick).ToString();
        return new Output(name, extends, kind, output, properties.Values.ToList(), statements, translated, new Dictionary<string, int>(missing),
            [.. messages], [.. says], [.. journalIndexes], [.. binks], polls, goodbye, uses.Contains("onactivate"));
    }

    static string Summary(Script s)
    {
        var first = string.Join("; ", s.Body.Take(3).Select(st => st switch
        {
            CallStmt c => (c.Call.Target is { } t ? t + "->" : "") + c.Call.Function,
            Set set => "set " + set.Target.Id,
            If => "if",
            _ => st.GetType().Name,
        }));
        return first.Replace("{", "(").Replace("}", ")");
    }

    /// <summary>A local's property name (v_, so it can't clash with a function or type name).</summary>
    static string Var(string name) => "v_" + Clean(name);

    static string F(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

    // ---- polling ----

    static readonly HashSet<string> EventFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnActivate", "OnDeath", "OnMurder", "OnKnockout", "OnPCAdd", "OnPCDrop", "OnPCEquip", "OnPCHitMe", "OnPCSoulGemUse", "OnRepair", "OnPCRepair", "GetPCSleep",
    };

    /// <summary>
    /// Whether the script must run on a timer: unless every statement only acts on an event (if OnActivate..., possibly
    /// after "if MenuMode return"), it does something every frame Morrowind runs it.
    /// </summary>
    bool NeedsPolling(List<Stmt> body)
    {
        foreach (var s in body)
        {
            if (s is Return { Line: < 0 }) continue;
            if (s is If i && i.Branches[0].Condition is { } c && ReadsEventOnly(c) && i.Branches.Skip(1).All(b => b.Condition is { } bc && ReadsEventOnly(bc)))
                continue;
            if (s is If m && i_IsMenuModeReturn(m)) continue;
            return true;
        }
        return false;

        static bool i_IsMenuModeReturn(If m) => m.Branches.Count == 1 && m.Branches[0].Condition is { } c && Script.Calls(c).Any(x => x.Function.Equals("MenuMode", StringComparison.OrdinalIgnoreCase))
            && m.Branches[0].Body.All(b => b is Return);
    }

    bool ReadsEventOnly(Expr c)
    {
        var calls = Script.Calls(c).ToList();
        bool anEvent = calls.Any(x => EventFunctions.Contains(x.Function))
            || NamesIn(c).Any(n => n.Owner is null && SpecialLocals.Contains(n.Id) && locals.ContainsKey(n.Id));
        return anEvent;
    }

    static IEnumerable<Name> NamesIn(Expr e) => e switch
    {
        Name n => [n],
        Unary u => NamesIn(u.Operand),
        Binary b => NamesIn(b.Left).Concat(NamesIn(b.Right)),
        Call c => c.Args.SelectMany(NamesIn),
        _ => [],
    };

    static readonly HashSet<string> MovingFunctions = new(StringComparer.OrdinalIgnoreCase) { "Move", "MoveWorld", "Rotate", "RotateWorld", "SetPos", "SetAngle" };

    static bool MovesThings(Script s) => s.AllCalls().Any(c => MovingFunctions.Contains(c.Function));
}
