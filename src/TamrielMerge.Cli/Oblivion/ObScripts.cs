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
    /// <summary>Statements translated as Oblivion does them (ObPapyrus.Output.Faithful).</summary>
    public int ScriptStatementsFaithful { get; private set; }
    /// <summary>What the rest fall short in, with how many statements (ObPapyrus.Gap).</summary>
    public Dictionary<string, int> ScriptGaps { get; } = new(StringComparer.OrdinalIgnoreCase);

    void CountScript(ObPapyrus.Output output)
    {
        ScriptStatements += output.Statements;
        ScriptStatementsTranslated += output.Translated;
        ScriptStatementsFaithful += output.Faithful;
        foreach (var (m, n) in output.Missing) ScriptMissing[m] = ScriptMissing.GetValueOrDefault(m) + n;
        foreach (var (g, n) in output.Gaps) ScriptGaps[g] = ScriptGaps.GetValueOrDefault(g) + n;
    }
    public int ScriptsAttached { get; private set; }
    public int PropertiesFilled { get; private set; }
    /// <summary>Properties no converted record fills (the script reads None there: Papyrus's log warns on each use).</summary>
    public List<string> PropertiesUnfilled { get; } = [];
    public Dictionary<string, int> ScriptMissing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ScriptCount => translations.Count;
    public int TrapModelCount { get; private set; }
    /// <summary>Oblivion people and creatures carrying their Oblivion attributes and skills (TMObActor).</summary>
    public int ActorsWithStats { get; private set; }

    /// <summary>Translates every script of the active plugins and marks the references they name.</summary>
    public void PrepareScripts()
    {
        scriptWorld = new ObScriptWorld(plugins);
        var functions = ObFunctions.Load(data.DataDir);
        scriptWorld.Functions = functions;
        foreach (var (key, sc) in scriptWorld.Scripts)
        {
            if (sc.Fields.SourceCode is not { } src || sc.EditorID is not { } id) continue;
            string kind = scriptWorld.QuestScripts.Contains(key) ? "Quest" : sc.Fields.MetadataSummary.Type.ToString();
            try
            {
                var output = new ObPapyrus(scriptWorld, functions).Translate(ObScript.Parse(src), id, kind, scriptWorld.OnActor(key));
                translations[key] = output;
                CountScript(output);
                foreach (var p in output.Properties)
                {
                    if (p.PapyrusTypeIsReference() && !p.Generated && p.QuestVariable is null && scriptWorld.KeyOf(p.EditorID) is { } refKey)
                        scriptRefs.Add(refKey);
                    // The references a package the scripts use names (where it goes, whom it follows) are persistent too.
                    if (p.Type == "Package" && scriptWorld.KeyOf(p.EditorID) is { } pk && records.GetValueOrDefault(pk) is Ob.IAIPackageGetter package)
                        foreach (var r in PackageRefs(package)) scriptRefs.Add(r);
                }
                foreach (var (pkg, uses) in output.ScriptPackages)
                    if (scriptWorld.KeyOf(pkg) is { } pk2) scriptPackageUses[pk2] = scriptPackageUses.GetValueOrDefault(pk2) + uses;
            }
            catch (Exception e) { Problems.Add($"script {id}: {e.Message}"); }
        }
        // The references the dialogue's and packages' conditions name are placed persistent, as the scripts' are.
        foreach (var key in ConditionRefs()) scriptRefs.Add(key);
        // Traps (their scripts call TrapUpdate): their models' collision on the trap layer, made after this.
        foreach (var (key, rec) in records)
            if (ObScriptWorld.ScriptLink(rec) is { } s && translations.TryGetValue(s, out var o) && o.Trap
                && rec.GetType().GetProperty("Model")?.GetValue(rec) is Ob.IModelGetter { File: { Length: > 0 } file })
                meshes.TrapModels.Add(ObData.Normalize(file.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? file : @"meshes\" + file));
        TrapModelCount = meshes.TrapModels.Count;
        PrepareResultScripts(functions);
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

    /// <summary>What the Papyrus compiler said of the translated scripts (scripts-compile.txt).</summary>
    public int CompileErrors { get; private set; }
    public int CompileWarnings { get; private set; }

    /// <summary>Writes, compiles and attaches the scripts; returns how many compiled.</summary>
    public int AttachScripts()
    {
        if (scriptWorld is null) return 0;
        // A folder of their own: the output folder may be Skyrim's Data, whose Source\Scripts holds the vanilla sources,
        // which compiling the whole folder (-all) would rebuild into Data\Scripts.
        string sourceDir = Path.Combine(modOutDir, "Source", "Scripts", "TamrielMerge", "Oblivion");
        string scriptsDir = Path.Combine(modOutDir, "Scripts");
        Directory.CreateDirectory(sourceDir);
        // Unchanged sources keep their dates, so only what changed (and what names it) is compiled again.
        foreach (var o in translations.Values) Skyrim.PapyrusRun.WriteIfChanged(Path.Combine(sourceDir, o.ScriptName + ".psc"), o.Source);
        foreach (var (name, source) in ResultScriptSources()) Skyrim.PapyrusRun.WriteIfChanged(Path.Combine(sourceDir, name + ".psc"), source);
        var (compiled, log) = CompilePapyrus(SkyrimData!, sourceDir, scriptsDir);
        if (compiled is null) { Problems.Add("scripts: " + log); return 0; }
        // Every error and warning the compiler and the assembler gave (scripts-compile.txt), counted (PapyrusRun.Tally);
        // the errors as problems too.
        var (errors, warnings, said) = Skyrim.PapyrusRun.Tally(log);
        File.WriteAllLines(Path.Combine(modOutDir, "scripts-compile.txt"), said);
        CompileWarnings = warnings;
        CompileErrors = errors;
        foreach (var line in said.Where(l => !l.Contains("warning", StringComparison.OrdinalIgnoreCase))) Problems.Add("script compile: " + line);

        // Records the scripts make for themselves: message boxes, the fame globals, the functions quest.
        var generated = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in translations.Values.Concat(resultTranslations.Values).Concat(stageTranslations.Values))
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
            return generated[edid] = ScriptGlobal(edid).FormKey;
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
        // What Oblivion's engine kept for its scripts (crimes, animations, script packages...), and every Oblivion
        // person and creature telling it of assaults and murders.
        generated["TMObState"] = StateQuest(Fill).FormKey;
        // ... with its Oblivion attributes and skills (ObStats), which the scripts read and change.
        var withActor = new HashSet<Npc>();
        foreach (var (source, made) in actors)
        {
            if (made is not Npc npc || !withActor.Add(npc)) continue;
            var entry = new ScriptEntry
            {
                Name = "TMObActor",
                Properties = [new ScriptObjectProperty { Name = "ObState", Object = new FormLink<ISkyrimMajorRecordGetter>(generated["TMObState"]) }],
            };
            if (records.GetValueOrDefault(source) is Ob.IOblivionMajorRecordGetter rec && ObStats(rec) is { } stats)
            {
                entry.Properties.Add(new ScriptIntListProperty { Name = "StatBase", Data = [.. stats.Base] });
                if (stats.Rate.Any(r => r != 0)) entry.Properties.Add(new ScriptFloatListProperty { Name = "StatRate", Data = [.. stats.Rate] });
                ActorsWithStats++;
            }
            // A person (not a creature) talks with the others (TMObConversations on OBDialogue).
            if (records.GetValueOrDefault(source) is Ob.INpcGetter && obDialogueQuest is not null && obDialogueQuest.VirtualMachineAdapter?.Scripts.Any(s => s.Name == "TMObConversations") == true)
            {
                entry.Properties.Add(new ScriptObjectProperty { Name = "Conversations", Object = obDialogueQuest.ToLink<ISkyrimMajorRecordGetter>() });
                ConversationPeople++;
            }
            npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            npc.VirtualMachineAdapter.Scripts.Add(entry);
        }

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
        // Scripted magic effects (SEFF): the magic effects made for them carry their scripts (TesMagic names the
        // script; its properties are filled here).
        var byName = translations.Values.GroupBy(o => o.ScriptName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // A copy: filling a script's properties can make more magic effects (2026-10-15: "Collection was modified").
        foreach (var m in mod.MagicEffects.ToList())
        {
            if (m.VirtualMachineAdapter is not { } vm) continue;
            foreach (var se in vm.Scripts.Where(x => x.Properties.Count == 0 && byName.ContainsKey(x.Name)).ToList())
            {
                vm.Scripts.Remove(se);
                Attach(m, byName[se.Name]);
            }
        }
        AttachResultScripts(compiled, Entry);
        return compiled.Count;

        // The script with its properties filled (those that name nothing converted stay empty and are counted).
        ScriptEntry Entry(ObPapyrus.Output o)
        {
            var entry = new ScriptEntry { Name = o.ScriptName };
            foreach (var p in o.Properties)
                if (Fill(p) is { } target)
                {
                    entry.Properties.Add(new ScriptObjectProperty { Name = p.Name, Object = target.ToLink<ISkyrimMajorRecordGetter>() });
                    PropertiesFilled++;
                }
                else PropertiesUnfilled.Add($"{o.ScriptName}.{p.Name} ({p.Type} {p.EditorID})");
            return entry;
        }

        void Attach(IMajorRecord carrier, ObPapyrus.Output o)
        {
            if (!compiled.Contains(o.ScriptName)) return;
            var entry = Entry(o);
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
            if (p.EditorID.StartsWith('@') && p.EditorID.Contains(':') && !p.EditorID.StartsWith("@ref:", StringComparison.Ordinal)) return Special(p);
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
                Ob.ISpellGetter => ObSpell(key),
                Ob.IEnchantmentGetter => ObEnchantment(key),
                Ob.IAIPackageGetter => ObPackage(key),
                Ob.ISoundGetter => ObSound(key),
                Ob.IWorldspaceGetter => worldspaces.GetValueOrDefault(key),
                _ when IsItem(rec) => Item(key),
                _ => bases.GetValueOrDefault(key) ?? (rec is Ob.IOblivionMajorRecordGetter && p.Type is "Form" or "Activator" or "Static" or "Furniture" ? Base(key) : null),
            };
            if (converted is not null) return converted.FormKey;
            // Look-alikes from Skyrim for what isn't converted: races, effect shaders, weathers, combat styles.
            return rec switch
            {
                Ob.IRaceGetter r => Vanilla.Get<IRaceGetter>(RaceStandIns.GetValueOrDefault(r.EditorID ?? "", r.EditorID ?? "") + "Race")?.FormKey,
                Ob.IEffectShaderGetter e => ShaderStandIn(e.EditorID ?? ""),
                Ob.IWeatherGetter w => WeatherStandIn(w.EditorID ?? ""),
                Ob.ICombatStyleGetter cs => CombatStyleStandIn(cs.EditorID ?? ""),
                _ => null,
            };
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
        // The converter's own helper scripts (TMOb…), which the translated scripts call.
        string helpers = Path.Combine(AppContext.BaseDirectory, "Papyrus");
        if (!File.Exists(compiler) || !File.Exists(Path.Combine(vanilla, "ObjectReference.psc")))
            return (null, "no Papyrus compiler or Skyrim script sources (install the Creation Kit)");
        Directory.CreateDirectory(outDir);
        var names = Directory.GetFiles(sourceDir, "*.psc").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList();
        // What changed since the last build into this folder, and what names it (Skyrim.PapyrusRun.Stale); a script
        // compiled again counts once its .pex is this run's.
        var stale = Skyrim.PapyrusRun.Stale(sourceDir, outDir, helpers, names);
        var started = DateTime.UtcNow.AddSeconds(-2);
        bool Fresh(string n) => File.Exists(Path.Combine(outDir, n + ".pex")) && (!stale.Contains(n) || File.GetLastWriteTimeUtc(Path.Combine(outDir, n + ".pex")) >= started);
        // In batches of 400, up to four at once, each in a folder of its own (the whole folder in one run was one
        // compiler on one core); the folder stays an import, so the scripts still see each other.
        string imports = $"-i={Path.GetFullPath(sourceDir)};{helpers};{skse};{vanilla}";
        const int batch = 400;
        var chunks = Enumerable.Range(0, (names.Count + batch - 1) / batch)
            .Select(i => (Index: i, Names: names.Skip(i * batch).Take(batch).Where(n => !Fresh(n)).ToList())).Where(c => c.Names.Count > 0).ToList();
        var logs = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) }, c =>
        {
            string chunkDir = Path.GetFullPath(sourceDir).TrimEnd('\\', '/') + "_batch" + c.Index;
            if (Directory.Exists(chunkDir)) Directory.Delete(chunkDir, true);
            Directory.CreateDirectory(chunkDir);
            foreach (var n in c.Names) File.Copy(Path.Combine(sourceDir, n + ".psc"), Path.Combine(chunkDir, n + ".psc"));
            logs.Add(Run(new ProcessStartInfo(compiler)
            {
                ArgumentList = { chunkDir, "-all", "-f=TESV_Papyrus_Flags.flg", imports, $"-o={Path.GetFullPath(outDir)}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }));
            Directory.Delete(chunkDir, true);
        });
        string log = string.Concat(logs);
        // The compiler works in batches and can drop a whole batch when memory runs short: those are compiled again
        // one at a time (real errors fail again and stay in the log).
        foreach (var n in names.Where(n => !Fresh(n)))
        {
            var one = new ProcessStartInfo(compiler)
            {
                ArgumentList = { n + ".psc", "-f=TESV_Papyrus_Flags.flg", $"-i={Path.GetFullPath(sourceDir)};{helpers};{skse};{vanilla}", $"-o={Path.GetFullPath(outDir)}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            log += Run(one);
        }
        Skyrim.PapyrusRun.KeepHelperSignatures(outDir, helpers);
        return (names.Where(Fresh).ToHashSet(StringComparer.OrdinalIgnoreCase), log);

        static string Run(ProcessStartInfo info) => Skyrim.PapyrusRun.Run(info, info.ArgumentList.Contains("-all") ? Skyrim.PapyrusRun.Batch : Skyrim.PapyrusRun.One).Output;
    }
}

static class ObScriptPropertyKinds
{
    /// <summary>Properties that point at placed references (which must be persistent to be filled).</summary>
    public static bool PapyrusTypeIsReference(this ObPapyrus.Property p) => p.Type is "ObjectReference" or "Actor";
}
