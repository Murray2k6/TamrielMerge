using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Activator = Mutagen.Bethesda.Skyrim.Activator;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's scripts in the converted plugin: every script translated into Papyrus (<see cref="ObPapyrus"/>),
/// compiled against Skyrim's and SKSE's sources, and attached to the records that carried it — quests, activators,
/// containers, doors, lights, items, NPCs and creatures — with its properties filled with the converted forms. The
/// references scripts name are placed persistent so properties can point at them.
/// <list type="bullet">
/// <item><see cref="PrepareScripts"/> runs before the world is built (it decides which references are persistent);</item>
/// <item><see cref="AttachScripts"/> after it and the dialogue (quests, globals and topics exist by then).</item>
/// </list>
/// Without the Creation Kit's compiler the sources are still written, but nothing is attached.
/// </summary>
public sealed partial class ObPluginBuilder
{
    ObScriptWorld? scriptWorld;
    readonly Dictionary<FormKey, ObPapyrus.Output> translations = new();
    /// <summary>Oblivion references the scripts name: placed persistent, and remembered here once placed.</summary>
    readonly HashSet<FormKey> scriptRefs = new();
    readonly Dictionary<FormKey, IMajorRecord> placedBySource = new();
    public int ScriptStatements { get; private set; }
    public int ScriptStatementsTranslated { get; private set; }
    public int ScriptsAttached { get; private set; }
    public int PropertiesFilled { get; private set; }
    public Dictionary<string, int> ScriptMissing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ScriptCount => translations.Count;

    /// <summary>Translates every script of the active plugins and marks the references they name.</summary>
    public void PrepareScripts()
    {
        scriptWorld = new ObScriptWorld(plugins);
        var functions = ObFunctions.Load(data.DataDir);
        foreach (var (key, sc) in scriptWorld.Scripts)
        {
            if (sc.Fields.SourceCode is not { } src || sc.EditorID is not { } id) continue;
            string kind = scriptWorld.QuestScripts.Contains(key) ? "Quest" : sc.Fields.MetadataSummary.Type.ToString();
            try
            {
                var output = new ObPapyrus(scriptWorld, functions).Translate(ObScript.Parse(src), id, kind, scriptWorld.OnActor(key));
                translations[key] = output;
                ScriptStatements += output.Statements;
                ScriptStatementsTranslated += output.Translated;
                foreach (var (m, n) in output.Missing) ScriptMissing[m] = ScriptMissing.GetValueOrDefault(m) + n;
                foreach (var p in output.Properties)
                    if (p.PapyrusTypeIsReference() && !p.Generated && p.QuestVariable is null && scriptWorld.KeyOf(p.EditorID) is { } refKey)
                        scriptRefs.Add(refKey);
            }
            catch (Exception e) { Problems.Add($"script {id}: {e.Message}"); }
        }
    }

    /// <summary>A reference a script names: persistent (in the cell's persistent group), and remembered.</summary>
    bool PlaceForScripts(FormKey source, IPlaced placed, Cell cell, Cell persistentCell)
    {
        if (!scriptRefs.Contains(source)) return false;
        placed.MajorRecordFlagsRaw |= 0x400;
        persistentCell.Persistent.Add(placed);
        placedBySource[source] = placed;
        return true;
    }

    /// <summary>Writes, compiles and attaches the scripts; returns how many compiled.</summary>
    public int AttachScripts()
    {
        if (scriptWorld is null) return 0;
        string sourceDir = Path.Combine(modOutDir, "Source", "Scripts");
        string scriptsDir = Path.Combine(modOutDir, "Scripts");
        Directory.CreateDirectory(sourceDir);
        foreach (var o in translations.Values) File.WriteAllText(Path.Combine(sourceDir, o.ScriptName + ".psc"), o.Source);
        var (compiled, log) = CompilePapyrus(SkyrimData!, sourceDir, scriptsDir);
        if (compiled is null) { Problems.Add("scripts: " + log); return 0; }
        foreach (var line in log.Split('\n').Where(l => l.Contains("): ")).Take(50)) Problems.Add("script compile: " + line.Trim());

        // Records the scripts make for themselves: message boxes, the fame globals, the functions quest.
        var generated = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in translations.Values)
            foreach (var m in o.Messages)
            {
                var msg = new Message(mod) { EditorID = m.EditorID, Description = m.Text, Flags = Message.Flag.MessageBox, MenuButtons = [] };
                foreach (var b in m.Buttons) msg.MenuButtons.Add(new MessageButton { Text = b });
                mod.Messages.Add(msg);
                generated[m.EditorID] = msg.FormKey;
            }
        FormKey GeneratedGlobal(string edid)
        {
            if (generated.TryGetValue(edid, out var k)) return k;
            var g = new GlobalFloat(mod) { EditorID = edid, Data = 0 };
            mod.Globals.Add(g);
            return generated[edid] = g.FormKey;
        }
        Quest? functionsQuest = null;
        // OBSE user functions: scripts on one quest that's always running.
        foreach (var (key, o) in translations)
            if (o.Kind == "Function" && compiled.Contains(o.ScriptName))
            {
                functionsQuest ??= NewFunctionsQuest();
                Attach(functionsQuest, o);
            }
        if (functionsQuest is not null) generated["TMObFunctions"] = functionsQuest.FormKey;

        // Carriers: quests, then base objects (containers, activators, doors, lights, items, actors).
        foreach (var q in plugins.SelectMany(p => p.Quests).GroupBy(q => q.FormKey).Select(g => g.Last()))
            if (q.Script.FormKeyNullable is { } s && translations.TryGetValue(s, out var o) && obQuests.TryGetValue(q.FormKey, out var quest))
                Attach(quest, o);
        foreach (var (key, rec) in records)
        {
            if (rec is Ob.IQuestGetter || ObScriptWorld.ScriptLink(rec) is not { } s || !translations.TryGetValue(s, out var o)) continue;
            foreach (var carrier in new[] { bases.GetValueOrDefault(key), items.GetValueOrDefault(key), actors.GetValueOrDefault(key), swingDoors.GetValueOrDefault(key) }.Distinct())
                if (carrier is not null) Attach(carrier, o);
        }
        return compiled.Count;

        void Attach(IMajorRecord carrier, ObPapyrus.Output o)
        {
            if (!compiled.Contains(o.ScriptName)) return;
            var entry = new ScriptEntry { Name = o.ScriptName };
            foreach (var p in o.Properties)
                if (Fill(p) is { } target)
                {
                    entry.Properties.Add(new ScriptObjectProperty { Name = p.Name, Object = target.ToLink<ISkyrimMajorRecordGetter>() });
                    PropertiesFilled++;
                }
            if (carrier is Quest quest)
            {
                quest.VirtualMachineAdapter ??= new QuestAdapter();
                quest.VirtualMachineAdapter.Scripts.Add(entry);
            }
            else if (carrier.GetType().GetProperty("VirtualMachineAdapter") is { CanWrite: true } vmProp && vmProp.PropertyType == typeof(VirtualMachineAdapter))
            {
                // Activators, containers, doors, lights, items and actors all hold a plain VirtualMachineAdapter.
                if (vmProp.GetValue(carrier) is not VirtualMachineAdapter vm) vmProp.SetValue(carrier, vm = new VirtualMachineAdapter());
                vm.Scripts.Add(entry);
            }
            else { Problems.Add($"script {o.ScriptName}: {carrier.EditorID} can't carry scripts"); return; }
            ScriptsAttached++;
        }

        FormKey? Fill(ObPapyrus.Property p)
        {
            if (p.Generated)
                return p.EditorID.StartsWith("TMOB_PC", StringComparison.Ordinal) ? GeneratedGlobal(p.EditorID) : generated.TryGetValue(p.EditorID, out var gk) ? gk : null;
            if (p.EditorID == "player") return PlayerRefKey;
            if (p.QuestVariable is { } variable)
                return QuestByEditorId(p.EditorID) is { } qk ? QuestVariable(qk, variable).FormKey : null;
            if (scriptWorld.KeyOf(p.EditorID) is not { } key) return null;
            if (placedBySource.TryGetValue(key, out var placed)) return placed.FormKey;
            if (interiorsBySource.TryGetValue(key, out var cell)) return cell.FormKey;
            if (!records.TryGetValue(key, out var rec)) return null;
            IMajorRecord? converted = rec switch
            {
                Ob.IQuestGetter => obQuests.GetValueOrDefault(key),
                Ob.IGlobalGetter => ObGlobal(key),
                Ob.IFactionGetter => ObFaction(key),
                Ob.IClassGetter => ObClass(key),
                Ob.IDialogTopicGetter => obTopics.GetValueOrDefault(key),
                Ob.INpcGetter or Ob.ICreatureGetter or Ob.ILeveledCreatureGetter => Actor(key),
                _ when IsItem(rec) => Item(key),
                _ => bases.GetValueOrDefault(key),
            };
            return converted?.FormKey;
        }
    }

    Quest NewFunctionsQuest()
    {
        var quest = new Quest(mod)
        {
            EditorID = "TMObFunctions",
            Name = "Oblivion script functions",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
            Priority = 0,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(quest);
        return quest;
    }

    /// <summary>
    /// Compiles every .psc in sourceDir into outDir with the Creation Kit's Papyrus compiler, importing Skyrim's
    /// sources and SKSE's (Data\Scripts\Source, which SKSE installs). Returns the scripts that compiled (null without
    /// the compiler) and the compiler's output.
    /// </summary>
    public static (HashSet<string>? Compiled, string Log) CompilePapyrus(string skyrimData, string sourceDir, string outDir)
    {
        string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
        string compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        string vanilla = Path.Combine(skyrimData, "Source", "Scripts"), skse = Path.Combine(skyrimData, "Scripts", "Source");
        if (!File.Exists(compiler) || !File.Exists(Path.Combine(vanilla, "ObjectReference.psc")))
            return (null, "no Papyrus compiler or Skyrim script sources (install the Creation Kit)");
        Directory.CreateDirectory(outDir);
        var psi = new ProcessStartInfo(compiler)
        {
            ArgumentList = { Path.GetFullPath(sourceDir), "-all", "-q", "-f=TESV_Papyrus_Flags.flg", $"-i={Path.GetFullPath(sourceDir)};{skse};{vanilla}", $"-o={Path.GetFullPath(outDir)}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string log = Run(psi);
        bool Fresh(string n) => File.Exists(Path.Combine(outDir, n + ".pex")) && File.GetLastWriteTimeUtc(Path.Combine(outDir, n + ".pex")) >= File.GetLastWriteTimeUtc(Path.Combine(sourceDir, n + ".psc"));
        var names = Directory.GetFiles(sourceDir, "*.psc").Select(f => Path.GetFileNameWithoutExtension(f)!).ToList();
        // The compiler works in batches and can drop a whole batch when memory runs short: those are compiled again
        // one at a time (real errors fail again and stay in the log).
        foreach (var n in names.Where(n => !Fresh(n)))
        {
            var one = new ProcessStartInfo(compiler)
            {
                ArgumentList = { n + ".psc", "-q", "-f=TESV_Papyrus_Flags.flg", $"-i={Path.GetFullPath(sourceDir)};{skse};{vanilla}", $"-o={Path.GetFullPath(outDir)}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            log += Run(one);
        }
        return (names.Where(Fresh).ToHashSet(StringComparer.OrdinalIgnoreCase), log);

        static string Run(ProcessStartInfo info)
        {
            using var p = Process.Start(info)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + err.Result;
            p.WaitForExit();
            return output;
        }
    }
}

static class ObScriptPropertyKinds
{
    /// <summary>Properties that point at placed references (which must be persistent to be filled).</summary>
    public static bool PapyrusTypeIsReference(this ObPapyrus.Property p) => p.Type is "ObjectReference" or "Actor";
}
