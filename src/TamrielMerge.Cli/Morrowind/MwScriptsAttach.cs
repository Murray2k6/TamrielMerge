using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NAudio.Wave;
using Noggog;
using Activator = Mutagen.Bethesda.Skyrim.Activator;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's scripts on the converted records:
/// <list type="bullet">
/// <item><see cref="PrepareScripts"/>, before the cells are built, translates every script (<see cref="MwScriptSet"/>):
/// the references they name by ID are placed persistent (the first reference of that ID, as Morrowind's "ID->" finds),
/// scripted activators become Activators (unscripted ones stay statics), and the cells PositionCell names get a marker
/// at their origin.</item>
/// <item>The dialogue gives each response with a result script its fragment (<see cref="ResultAdapter"/>).</item>
/// <item><see cref="AttachScripts"/>, after the dialogue, compiles the scripts and attaches them: object scripts to
/// the activators, containers, doors, lights, items, people and creatures carrying them; global scripts to a quest each
/// (start scripts start with the game and wait for the player to reach Morrowind); result scripts to their responses
/// (a response whose script didn't compile keeps TMMwInfo's fixed set). Properties are filled with the converted
/// records and the records the scripts need made: message boxes, Say lines, the AI quest, mirror factions…</item>
/// </list>
/// </summary>
public sealed partial class MwPluginBuilder
{
    MwScriptWorld? scriptWorld;
    MwScriptSet? scriptSet;
    readonly HashSet<string> scriptRefIds = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> anchorsWanted = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IPlaced> scriptPlaced = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<IPlaced> scriptPersistent = [];
    readonly Dictionary<string, PlacedObject> anchors = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> scriptedActivators = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(DialogResponses Response, string Script, DialogResponsesAdapter? Fallback)> resultResponses = [];
    readonly Dictionary<string, PlacedObject> townMapMarkers = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<(int X, int Y), Cell>? exteriorCells;

    public int ScriptCount => scriptSet is null ? 0 : scriptSet.Objects.Count + scriptSet.Globals.Count + scriptSet.Results.Count;
    public int ScriptStatements => scriptSet?.Statements ?? 0;
    public int ScriptStatementsTranslated => scriptSet?.TranslatedStatements ?? 0;
    public int ScriptsAttached { get; private set; }
    public int ResultScriptsAttached { get; private set; }
    public int PropertiesFilled { get; private set; }
    public int PropertiesUnfilled { get; private set; }
    public IReadOnlyDictionary<string, int> ScriptMissing => scriptSet?.Missing ?? [];

    /// <summary>Translates every script; call before the cells are built (it decides what is placed persistent).</summary>
    public void PrepareScripts()
    {
        dialogueData ??= MwDialogue.Load(world.Plugins);
        scriptWorld = new MwScriptWorld(world, dialogueData, data);
        scriptSet = MwScriptSet.Translate(scriptWorld);
        foreach (var o in scriptSet.All)
            foreach (var p in o.Properties)
            {
                var (kind, id) = SplitFill(p.Fill);
                if (kind == "ref") scriptRefIds.Add(id);
                if (kind == "anchor") anchorsWanted.Add(id);
            }
        foreach (var t in scriptSet.Objects)
            if (scriptWorld.Carriers.TryGetValue(t.ScriptId, out var carriers))
                foreach (var c in carriers.Where(c => c.Type == "ACTI")) scriptedActivators.Add(c.Id);
    }

    static (string Kind, string Id) SplitFill(string fill)
    {
        int colon = fill.IndexOf(':');
        return colon < 0 ? (fill, "") : (fill[..colon], fill[(colon + 1)..]);
    }

    /// <summary>A scripted activator as an Activator (unscripted ones are statics), or null for anything else.</summary>
    Activator? ScriptedActivator(Tes3Record rec, string edid, MwMeshConverter.Result? mesh)
    {
        if (rec.Type != "ACTI" || !scriptedActivators.Contains(rec.Id) || mesh is null) return null;
        var act = new Activator(mod) { EditorID = edid, ObjectBounds = Bounds(mesh), Model = new Model { File = SkyrimModelPath(mesh.Path) } };
        if (rec.Str("FNAM") is { Length: > 0 } name) act.Name = name;
        mod.Activators.Add(act);
        return act;
    }

    /// <summary>
    /// The first reference of an ID the scripts name (by "ID->" or "ID.variable") is placed persistent, in the cell's
    /// persistent group (the worldspace's for the exterior), so their properties can point at it. True when it was.
    /// </summary>
    bool PlaceForScripts(Tes3Ref r, IPlaced placed, Cell persistentCell)
    {
        if (!scriptRefIds.Contains(r.BaseId) || scriptPlaced.ContainsKey(r.BaseId)) return false;
        placed.MajorRecordFlagsRaw |= 0x400;
        persistentCell.Persistent.Add(placed);
        scriptPlaced[r.BaseId] = placed;
        scriptPersistent.Add(placed);
        return true;
    }

    /// <summary>A door the scripts name is remembered too (doors are persistent already).</summary>
    void NoteDoorForScripts(Tes3Ref r, IPlaced placed)
    {
        if (scriptRefIds.Contains(r.BaseId)) scriptPlaced.TryAdd(r.BaseId, placed);
    }

    /// <summary>A marker at the cell's origin ("" for the exterior) when the scripts position things there.</summary>
    void AddAnchor(string cellName, Cell persistentCell)
    {
        if (!anchorsWanted.Contains(cellName) || anchors.ContainsKey(cellName)) return;
        var marker = new PlacedObject(mod)
        {
            EditorID = "MWScriptAnchor" + (cellName.Length == 0 ? "Exterior" : Sanitize(cellName)),
            Base = new FormLinkNullable<IPlaceableObjectGetter>(Sk(0x3B)),   // XMarker
            Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        persistentCell.Persistent.Add(marker);
        anchors[cellName] = marker;
        scriptPersistent.Add(marker);
    }

    /// <summary>
    /// A response's result script as its fragment (MWR_…, which extends TMMwInfo so persuasion's properties still
    /// apply), remembered so it can fall back to TMMwInfo's fixed set if it doesn't compile.
    /// </summary>
    DialogResponsesAdapter? ResultAdapter(MwDialogue.Info info, DialogResponses r, DialogResponsesAdapter? fallback)
    {
        if (scriptSet is null || !scriptSet.InfoScripts.TryGetValue(info.Id, out var name)) return fallback;
        resultResponses.Add((r, name, fallback));
        return new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = name }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = name,
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = name, FragmentName = "Fragment_End" },
            },
        };
    }

    bool ResultSaysGoodbye(MwDialogue.Info info) => scriptSet?.Goodbyes.Contains(info.Id) == true;

    /// <summary>The faction mirroring a local the dialogue tests, when the scripts are converted (null otherwise).</summary>
    FormKey? LocalFaction(string variable) =>
        scriptSet is null || scriptWorld is null || !scriptWorld.ScriptsDeclaring(variable).Any() ? null : Fill(new MwPapyrus.Property("", "Faction", "local:" + variable));

    // ---- attaching ----

    readonly Dictionary<string, FormKey?> generated = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Quest> globalScriptQuests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes, compiles and attaches the scripts; returns how many compiled.</summary>
    /// <param name="precompiled">A folder of these scripts compiled already (mw-script-translate's Scripts): used instead
    /// of compiling (a test aid).</param>
    public int AttachScripts(Action<string>? progress = null, string? precompiled = null)
    {
        if (scriptSet is null || scriptWorld is null) return 0;
        string sourceDir = Path.Combine(modOutDir, "Source", "MWScripts");
        if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
        WriteScriptSources(scriptSet.All, sourceDir);
        // A source the same as the last build's (kept in Source\Scripts) takes that copy's time, so its compiled script
        // from then counts as fresh and isn't compiled again (all 4,930 took 17 minutes).
        string kept = Path.Combine(modOutDir, "Source", "Scripts");
        foreach (var f in Directory.GetFiles(sourceDir, "*.psc"))
            if (Path.Combine(kept, Path.GetFileName(f)) is var old && File.Exists(old) && File.ReadAllBytes(old).AsSpan().SequenceEqual(File.ReadAllBytes(f)))
                File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(old));
        var (compiled, log) = precompiled is null ? CompilePapyrus(SkyrimData!, sourceDir, Path.Combine(modOutDir, "Scripts"), progress)
            : (scriptSet.All.Select(o => o.ScriptName).Where(n => File.Exists(Path.Combine(precompiled, n + ".pex"))).ToHashSet(StringComparer.OrdinalIgnoreCase), "");
        if (compiled is null) { Problems.Add("scripts: " + log); FallBackResults(new(StringComparer.OrdinalIgnoreCase)); return 0; }
        foreach (var line in log.Split('\n').Where(l => l.Contains("): ")).Take(50)) Problems.Add("script compile: " + line.Trim());
        // The sources beside the other scripts' (Data\Source\Scripts), for anyone reading them.
        string sources = Path.Combine(modOutDir, "Source", "Scripts");
        Directory.CreateDirectory(sources);
        foreach (var f in Directory.GetFiles(sourceDir, "*.psc")) File.Copy(f, Path.Combine(sources, Path.GetFileName(f)), true);
        Directory.Delete(sourceDir, true);

        // Global scripts: a quest each.
        foreach (var t in scriptSet.Globals)
        {
            if (!compiled.Contains(t.Output.ScriptName)) continue;
            bool start = scriptWorld.StartScripts.Contains(t.ScriptId);
            var quest = new Quest(mod)
            {
                EditorID = "MWScript" + Sanitize(t.ScriptId),
                Name = "Morrowind script " + t.ScriptId,
                Flags = start ? Quest.Flag.StartGameEnabled : 0,
                Priority = 0,
                Type = Quest.TypeEnum.None,
            };
            mod.Quests.Add(quest);
            globalScriptQuests[t.ScriptId] = quest;
        }
        foreach (var t in scriptSet.Globals)
            if (globalScriptQuests.TryGetValue(t.ScriptId, out var quest))
            {
                quest.VirtualMachineAdapter ??= new QuestAdapter();
                quest.VirtualMachineAdapter.Scripts.Add(Entry(t.Output));
                ScriptsAttached++;
            }

        // Object scripts: on every converted record that carries them.
        foreach (var t in scriptSet.Objects)
        {
            if (!compiled.Contains(t.Output.ScriptName) || !scriptWorld.Carriers.TryGetValue(t.ScriptId, out var carriers)) continue;
            foreach (var rec in carriers)
                foreach (var carrier in ConvertedCarriers(rec))
                    Attach(carrier, t.Output, rec);
        }

        // Result scripts: their responses (or, not compiled, TMMwInfo's fixed set).
        FallBackResults(compiled);

        // Journal indexes the scripts set that no entry has: a stage without a log entry, so SetStage takes them.
        foreach (var o in scriptSet.All)
            foreach (var (journal, index) in o.JournalIndexes)
                if (journals.TryGetValue(journal, out var q) && index >= 0 && !q.Stages.Any(s => s.Index == index))
                    q.Stages.Add(new QuestStage { Index = (ushort)Math.Min(index, ushort.MaxValue), LogEntries = [] });
        foreach (var q in journals.Values) q.Stages.Sort((a, b) => a.Index.CompareTo(b.Index));

        CopyBinks();
        // Morrowind's engine sets CharGenState to 1 on a new game, which starts Main's character generation (the
        // prison ship, the census office): here it starts so, and Main runs it when the player reaches Morrowind.
        if (globalScriptQuests.ContainsKey("Main") && MwGlobal("CharGenState") is GlobalFloat chargen && chargen.Data == 0) chargen.Data = 1;
        return compiled.Count;
    }

    void FallBackResults(HashSet<string> compiled)
    {
        foreach (var (response, name, fallback) in resultResponses)
        {
            if (compiled.Contains(name) && scriptSet!.Results.TryGetValue(name, out var output))
            {
                var entry = response.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == name);
                if (entry is null) continue;
                foreach (var p in Properties(output)) entry.Properties.Add(p);
                ResultScriptsAttached++;
                continue;
            }
            // Persuasion's properties (on the result script's entry) go with the fallback.
            var persuasion = response.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == name)?.Properties.ToList() ?? [];
            response.VirtualMachineAdapter = fallback;
            if (persuasion.Count > 0)
            {
                var entry = InfoEntry(response);
                foreach (var p in persuasion) { entry.Properties.RemoveAll(x => x.Name == p.Name); entry.Properties.Add(p); }
            }
        }
    }

    /// <summary>The converted records a Morrowind record carrying a script became (made already: nothing new is made).</summary>
    IEnumerable<IMajorRecord> ConvertedCarriers(Tes3Record rec)
    {
        if (rec.Type is "NPC_" or "CREA") { if (actors.GetValueOrDefault(rec.Id) is { } a) yield return a; yield break; }
        if (IsItemType(rec.Type) && items.GetValueOrDefault(rec.Id) is { } item && item.FormKey.ModKey == mod.ModKey) { yield return item; yield break; }
        if (bases.GetValueOrDefault(rec.Id) is { } b && (b is not IItemGetter || b.FormKey.ModKey == mod.ModKey)) yield return b;
        if (rec.Type == "DOOR" && swingDoors.GetValueOrDefault(rec.Id) is { } swing) yield return swing;
    }

    void Attach(IMajorRecord carrier, MwPapyrus.Output o, Tes3Record? rec = null)
    {
        if (carrier.GetType().GetProperty("VirtualMachineAdapter") is { CanWrite: true } vmProp && vmProp.PropertyType == typeof(VirtualMachineAdapter))
        {
            if (vmProp.GetValue(carrier) is not VirtualMachineAdapter vm) vmProp.SetValue(carrier, vm = new VirtualMachineAdapter());
            if (vm.Scripts.Any(s => s.Name == o.ScriptName)) return;
            vm.Scripts.Add(Entry(o, rec));
            // A creature copied from a templated vanilla actor would take its template's scripts instead of its own.
            if (carrier is Npc npc) npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Script;
            ScriptsAttached++;
        }
        else Problems.Add($"script {o.ScriptName}: {carrier.EditorID} ({carrier.GetType().Name}) can't carry scripts");
    }

    ScriptEntry Entry(MwPapyrus.Output o, Tes3Record? carrier = null)
    {
        var entry = new ScriptEntry { Name = o.ScriptName };
        foreach (var p in Properties(o, carrier)) entry.Properties.Add(p);
        return entry;
    }

    /// <param name="carrier">The Morrowind record the script is attached for (its own values: MwBaseStats).</param>
    IEnumerable<ScriptProperty> Properties(MwPapyrus.Output o, Tes3Record? carrier = null)
    {
        foreach (var p in o.Properties)
        {
            // The carrier's Morrowind attributes and skills (GetStrength... on itself), per record.
            if (p.Fill == "basestats")
            {
                if (carrier is not null && scriptWorld?.Stats.Of(carrier.Id) is { } stats)
                {
                    PropertiesFilled++;
                    yield return new ScriptIntListProperty { Name = p.Name, Data = [.. stats] };
                }
                else PropertiesUnfilled++;
                continue;
            }
            if (Fill(p) is { } key)
            {
                PropertiesFilled++;
                yield return new ScriptObjectProperty { Name = p.Name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
            }
            else PropertiesUnfilled++;
        }
    }

    /// <summary>What fills a script property (see <see cref="MwPapyrus.Property"/>), or null when it doesn't exist.</summary>
    FormKey? Fill(MwPapyrus.Property p)
    {
        if (generated.TryGetValue(p.Fill, out var hit)) return hit;
        var (kind, id) = SplitFill(p.Fill);
        FormKey? key = null;
        try { key = MakeFill(kind, id); }
        catch (Exception e) { Problems.Add($"script property {p.Fill}: {e.Message}"); }
        return generated[p.Fill] = key;
    }

    FormKey? MakeFill(string kind, string id)
    {
        switch (kind)
        {
            case "player": return PlayerRefKey;
            case "ref": return scriptPlaced.GetValueOrDefault(id)?.FormKey;
            case "base": return BaseForm(id);
            case "global": return MwGlobal(id)?.FormKey;
            case "vanilla": return VanillaGlobal(id);
            case "journal": return journals.GetValueOrDefault(id)?.FormKey;
            case "script": return globalScriptQuests.GetValueOrDefault(id)?.FormKey;
            case "anchor": return anchors.GetValueOrDefault(id)?.FormKey;
            case "faction": return MwFaction(id)?.FormKey;
            case "spell": return MwSpell(id)?.FormKey;
            case "sound": return SoundMarkerFor(id, loop: false);
            case "loopsound": return SoundMarkerFor(id, loop: true);
            case "voice": return VoiceSound(id);
            case "race":
                VanillaNpc("EncBandit");
                return MwRaces.TryGetValue(id, out var race) ? skyrimEsm!.Races.FirstOrDefault(r => r.EditorID == race)?.FormKey : null;
            case "weather":
            {
                var parts = id.Split(':');
                return parts.Length == 2 && int.TryParse(parts[1], out int type) && type >= 0 && type < RegionWeathers.Length ? RegionWeathers[type] : null;
            }
            case "message": return ScriptMessage(id);
            case "say": return SayTopic(id);
            case "local": return Hidden(new Faction(mod) { EditorID = "MWLocal" + Sanitize(id), Name = "Morrowind local " + id, Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [new Rank { Number = 0 }] });
            case "facrep":
            {
                var g = new GlobalFloat(mod) { EditorID = "MWReputation" + Sanitize(id), Data = 0 };
                mod.Globals.Add(g);
                return g.FormKey;
            }
            case "gen" when id == "MWReputation":
            {
                var g = new GlobalFloat(mod) { EditorID = "MWReputation", Data = 0 };
                mod.Globals.Add(g);
                return g.FormKey;
            }
            case "gen" when id == "MWCrimeFaction":
                return Hidden(new Faction(mod) { EditorID = "MWCrimeFaction", Name = "Morrowind", Flags = Faction.FactionFlag.HiddenFromPC | Faction.FactionFlag.TrackCrime, Ranks = [new Rank { Number = 0 }] });
            case "ai": return AiQuest().FormKey;
            case "state": return StateQuest().FormKey;
            case "jindex": return journals.ContainsKey(id) ? JournalIndexGlobal(id).FormKey : null;
            case "music": return StreamedMusic(id);
            case "diseases": return SpellList(id == "blight" ? 2 : 3).FormKey;
            case "effects": return EffectList(id)?.FormKey;
            case "soulgem": return SoulGemFor(id);
            case "cells": return CellList(id)?.FormKey;
            case "mapmarkers": return MapMarkerList(id)?.FormKey;
        }
        Problems.Add($"script property kind {kind} unknown");
        return null;
    }

    FormKey Hidden(Faction f)
    {
        mod.Factions.Add(f);
        return f.FormKey;
    }

    /// <summary>A base object by Morrowind ID: the item (coins: Skyrim's gold), the person or creature, the spell, or the
    /// placeable object (made now if nothing placed it).</summary>
    FormKey? BaseForm(string id)
    {
        if (!world.Objects.TryGetValue(id, out var rec)) return null;
        if (IsItemType(rec.Type)) return Item(id)?.FormKey;
        if (rec.Type is "NPC_" or "CREA" or "LEVC") return Actor(id)?.FormKey;
        if (rec.Type == "SPEL") return MwSpell(id)?.FormKey;
        if (rec.Type == "FACT") return MwFaction(id)?.FormKey;
        return Base(id)?.FormKey;
    }

    static FormKey? VanillaGlobal(string editorId) => editorId switch
    {
        "GameHour" => Sk(0x38), "GameDay" => Sk(0x37), "GameMonth" => Sk(0x36), "GameYear" => Sk(0x35), "GameDaysPassed" => Sk(0x39), "TimeScale" => Sk(0x3A),
        _ => null,
    };

    readonly Dictionary<string, FormKey?> soundMarkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A Morrowind sound (its descriptor, <see cref="Sound"/>) as the SoundMarker Papyrus plays; looping ones
    /// on a copy of Skyrim's looping test descriptor.</summary>
    FormKey? SoundMarkerFor(string soundId, bool loop)
    {
        string key = (loop ? "loop:" : "") + soundId;
        if (soundMarkers.TryGetValue(key, out var hit)) return hit;
        if (Sound(soundId) is not { } descriptor) return soundMarkers[key] = null;
        if (loop)
        {
            var looping = mod.SoundDescriptors.DuplicateInAsNewRecord(skyrimEsm!.SoundDescriptors.First(d => d.EditorID == "tstLoopMonoLPSD"));
            looping.EditorID = descriptor.EditorID + "Loop";
            looping.SoundFiles.Clear();
            looping.SoundFiles.AddRange(descriptor.SoundFiles);
            descriptor = looping;
        }
        var marker = new SoundMarker(mod) { EditorID = descriptor.EditorID + "Marker", SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
        mod.SoundMarkers.Add(marker);
        return soundMarkers[key] = marker.FormKey;
    }

    /// <summary>A Say line heard as a sound (said by the player or a creature): Morrowind's recording (MP3) as a WAV, in
    /// Skyrim's voice category.</summary>
    FormKey? VoiceSound(string file)
    {
        string key = "voice:" + file;
        if (soundMarkers.TryGetValue(key, out var hit)) return hit;
        soundMarkers[key] = null;
        string path = file.Replace('/', '\\');
        byte[]? bytes = data.Read(@"sound\" + path) ?? data.Read(@"sound\" + Path.ChangeExtension(path, ".mp3"));
        if (bytes is null) { Problems.Add($"script Say: {file} not found"); return null; }
        string rel = Path.Combine("fx", "TamrielMerge", "MW", Path.ChangeExtension(path, ".wav"));
        string target = Path.Combine(modOutDir, "Sound", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = Path.Combine(Path.GetTempPath(), $"tm_mw_say_{Guid.NewGuid():N}{(bytes.Length > 3 && bytes[0] == 'R' && bytes[1] == 'I' ? ".wav" : ".mp3")}");
        try
        {
            File.WriteAllBytes(temp, bytes);
            using (var reader = new MediaFoundationReader(temp))
            using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 1)) { ResamplerQuality = 60 })
                WaveFileWriter.CreateWaveFile(target, resampler);
        }
        catch (Exception e) { Problems.Add($"script Say: {file}: {e.Message}"); return null; }
        finally { File.Delete(temp); }
        // A copy of a vanilla voice descriptor (a made-up category link crashes the game at startup).
        var descriptor = mod.SoundDescriptors.DuplicateInAsNewRecord(skyrimEsm!.SoundDescriptors.First(d => d.EditorID == "VOCShoutFXThrowVoice"));
        descriptor.EditorID = "MWSay" + Sanitize(Path.GetFileNameWithoutExtension(file));
        descriptor.SoundFiles.Clear();
        descriptor.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(rel));
        descriptor.Category = new FormLinkNullable<ISoundCategoryGetter>(skyrimEsm.SoundCategories.First(c => c.EditorID == "AudioCategoryVOCGeneral").FormKey);
        var marker = new SoundMarker(mod) { EditorID = descriptor.EditorID + "Sound", SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
        mod.SoundMarkers.Add(marker);
        return soundMarkers[key] = marker.FormKey;
    }

    Dictionary<string, MwPapyrus.MessageForm>? messageForms;
    Dictionary<string, MwPapyrus.SayLine>? sayLines;

    /// <summary>A message box with buttons as a Message record.</summary>
    FormKey? ScriptMessage(string editorId)
    {
        messageForms ??= scriptSet!.All.SelectMany(o => o.Messages).GroupBy(m => m.EditorID).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (!messageForms.TryGetValue(editorId, out var m)) return null;
        var msg = new Message(mod) { EditorID = editorId, Description = m.Text, Flags = Message.Flag.MessageBox, MenuButtons = [] };
        foreach (var b in m.Buttons) msg.MenuButtons.Add(new MessageButton { Text = b });
        mod.Messages.Add(msg);
        return msg.FormKey;
    }

    Quest? sayQuest;

    /// <summary>
    /// A Say line as a topic of the MWScriptSay quest: one response per speaker (said only by them), carrying Morrowind's
    /// recording for the voice planner to pack in the speaker's voice, and the subtitle.
    /// </summary>
    FormKey? SayTopic(string editorId)
    {
        sayLines ??= scriptSet!.All.SelectMany(o => o.Says).GroupBy(s => s.EditorID).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (!sayLines.TryGetValue(editorId, out var line)) return null;
        if (sayQuest is null)
        {
            sayQuest = new Quest(mod)
            {
                EditorID = "MWScriptSay",
                Name = "Morrowind script lines",
                Flags = Quest.Flag.StartGameEnabled,
                Priority = 50,
                Type = Quest.TypeEnum.Misc,
            };
            mod.Quests.Add(sayQuest);
        }
        var topic = new DialogTopic(mod)
        {
            EditorID = editorId,
            Quest = sayQuest.ToNullableLink<IQuestGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        string file = line.File.Replace('/', '\\').ToLowerInvariant();
        if (file.EndsWith(".wav")) file = Path.ChangeExtension(file, ".mp3");
        foreach (var speaker in line.Speakers)
        {
            if (Actor(speaker) is not Npc npc) continue;
            var said = new GetIsIDConditionData();
            said.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(said, npc.FormKey);
            topic.Responses.Add(new DialogResponses(mod)
            {
                Conditions = [new ConditionFloat { Data = said, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
                Flags = new DialogResponseFlags(),
                Responses = [new DialogResponse { Text = line.Text, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50, ScriptNotes = RecordingNote + file }],
            });
        }
        if (topic.Responses.Count == 0) return null;
        mod.DialogTopics.Add(topic);
        return topic.FormKey;
    }

    Quest? aiQuest;

    /// <summary>
    /// The MWScriptAI quest (TMMwAI): aliases with Skyrim's packages that AI functions put actors in — following the
    /// player (DefaultFollowPlayer), holding position where they are (DefaultHoldPositionCurrentLoc128), and wandering
    /// about it (DefaultSandboxCurrentLocation256 and 1024).
    /// </summary>
    Quest AiQuest()
    {
        if (aiQuest is not null) return aiQuest;
        aiQuest = new Quest(mod)
        {
            EditorID = "MWScriptAI",
            Name = "Morrowind script AI",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 60,
            Type = Quest.TypeEnum.None,
            Aliases = [],
        };
        mod.Quests.Add(aiQuest);
        var entry = new ScriptEntry { Name = "TMMwAI" };
        uint next = 0;
        foreach (var (property, package, count) in new[] { ("FollowSlots", 0x04B037u, 16), ("HoldSlots", 0x0296AEu, 24), ("SandboxSlots", 0x0956B8u, 16), ("WideSandboxSlots", 0x0BFB6Bu, 8) })
        {
            var list = new ScriptObjectListProperty { Name = property };
            for (int i = 0; i < count; i++)
            {
                var alias = new QuestAlias
                {
                    ID = next,
                    Name = $"{property}{i:00}",
                    Flags = QuestAlias.Flag.Optional | QuestAlias.Flag.AllowReserved | QuestAlias.Flag.AllowReuseInQuest,
                };
                alias.PackageData.Add(new FormLink<IPackageGetter>(Sk(package)));
                aiQuest.Aliases.Add(alias);
                list.Objects.Add(new ScriptObjectProperty { Object = aiQuest.ToLink<ISkyrimMajorRecordGetter>(), Alias = (short)next });
                next++;
            }
            entry.Properties.Add(list);
        }
        aiQuest.NextAliasID = next;
        entry.Properties.Add(new ScriptObjectProperty { Name = "XMarker", Object = new FormLink<ISkyrimMajorRecordGetter>(Sk(0x3B)) });
        aiQuest.VirtualMachineAdapter = new QuestAdapter { Scripts = [entry] };
        return aiQuest;
    }

    Quest? stateQuest;

    /// <summary>
    /// The MWScriptState quest (TMMwState), which keeps Morrowind's attributes: with Morrowind's race values (by
    /// Skyrim's race of each, and its vampire form) and birthsigns (by Skyrim's standing stones, which are Morrowind's
    /// signs) for reckoning the player's.
    /// </summary>
    Quest StateQuest()
    {
        if (stateQuest is not null) return stateQuest;
        VanillaNpc("EncBandit");   // opens the masters
        stateQuest = new Quest(mod)
        {
            EditorID = "MWScriptState",
            Name = "Morrowind script state",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(stateQuest);
        var entry = new ScriptEntry { Name = "TMMwState" };
        var races = new ScriptObjectListProperty { Name = "Races" };
        var rows = new List<int>();
        var male = new List<int>();
        var female = new List<int>();
        int row = 0;
        foreach (var (mwRace, skyRace) in MwRaces)
        {
            if (scriptWorld!.Stats.Race(mwRace) is not { } values) continue;
            bool any = false;
            foreach (var r in skyrimEsm!.Races.Where(r => r.EditorID == skyRace || r.EditorID == skyRace + "Vampire"))
            {
                races.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(r.FormKey) });
                rows.Add(row);
                any = true;
            }
            if (!any) continue;
            male.AddRange(values.Male);
            female.AddRange(values.Female);
            row++;
        }
        entry.Properties.Add(races);
        entry.Properties.Add(new ScriptIntListProperty { Name = "RaceRows", Data = [.. rows] });
        entry.Properties.Add(new ScriptIntListProperty { Name = "RaceMale", Data = [.. male] });
        entry.Properties.Add(new ScriptIntListProperty { Name = "RaceFemale", Data = [.. female] });
        var signs = new ScriptObjectListProperty { Name = "Signs" };
        var signStats = new List<int>();
        foreach (var (name, values) in scriptWorld!.Stats.Birthsigns())
        {
            // "The Lady" is Skyrim's doomLadyAbility, the Lady Stone's.
            string word = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (skyrimEsm!.Spells.FirstOrDefault(sp => sp.EditorID is { } e && e.Equals("doom" + word + "Ability", StringComparison.OrdinalIgnoreCase)) is not { } stone) continue;
            signs.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(stone.FormKey) });
            signStats.AddRange(values);
        }
        entry.Properties.Add(signs);
        entry.Properties.Add(new ScriptIntListProperty { Name = "SignStats", Data = [.. signStats] });
        stateQuest.VirtualMachineAdapter = new QuestAdapter { Scripts = [entry] };
        return stateQuest;
    }

    /// <summary>
    /// StreamMusic's track (music\…, Morrowind's MP3) as Skyrim music: decoded and encoded as xWMA (the Creation Kit's
    /// xWMAEncode; WAV without it) into Data\Music\TamrielMerge\MW, a music track, and a music type of its own that plays
    /// it once, at once, over the place's music.
    /// </summary>
    FormKey? StreamedMusic(string path)
    {
        if (data.Read(path) is not { } bytes) { Problems.Add($"script StreamMusic: {path} not found"); return null; }
        string name = Sanitize(Path.GetFileNameWithoutExtension(path));
        string dir = Path.Combine(modOutDir, "Music", "TamrielMerge", "MW");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, name + ".wav"), xwm = Path.Combine(dir, name + ".xwm");
        string temp = Path.Combine(Path.GetTempPath(), $"tm_mw_music_{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            File.WriteAllBytes(temp, bytes);
            using (var reader = new MediaFoundationReader(temp))
            using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 2)) { ResamplerQuality = 60 })
                WaveFileWriter.CreateWaveFile(wav, resampler);
            string game = Path.GetDirectoryName(Path.GetFullPath(SkyrimData!.TrimEnd('\\', '/')))!;
            string encoder = Path.Combine(game, "Tools", "Audio", "xWMAEncode.exe");
            if (File.Exists(encoder))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(encoder) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-b", "192000", wav, xwm }) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0 && File.Exists(xwm)) File.Delete(wav);
                else Problems.Add($"script StreamMusic: xWMAEncode failed on {path}; kept as WAV");
            }
        }
        catch (Exception e) { Problems.Add($"script StreamMusic: {path}: {e.Message}"); return null; }
        finally { File.Delete(temp); }
        // Vanilla tracks name .wav and ship .xwm; the engine takes either.
        var track = new MusicTrack(mod) { EditorID = "MWMusic" + name, Type = MusicTrack.TypeEnum.SingleTrack, TrackFilename = $@"Data\Music\TamrielMerge\MW\{name}.wav" };
        mod.MusicTracks.Add(track);
        var type = new MusicType(mod)
        {
            EditorID = "MWStreamMusic" + name,
            Flags = MusicType.Flag.PlaysOneSelection | MusicType.Flag.AbruptTransition,
            Data = new MusicTypeData { Priority = 10, DuckingDecibel = 0 },
            FadeDuration = 1,
            Tracks = [track.ToLink<IMusicTrackGetter>()],
        };
        mod.MusicTypes.Add(type);
        return type.FormKey;
    }

    readonly Dictionary<int, FormList> spellLists = [];

    /// <summary>Morrowind's diseases of a kind (SPDT type 2 blight, 3 common disease), converted.</summary>
    FormList SpellList(int type)
    {
        if (spellLists.TryGetValue(type, out var hit)) return hit;
        var list = new FormList(mod) { EditorID = type == 2 ? "MWBlightDiseases" : "MWCommonDiseases" };
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "SPEL" && o.Get("SPDT") is { } d && d.Int(0) == type).ToList())
            if (MwSpell(rec.Id) is { } s) list.Items.Add(s.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        return spellLists[type] = list;
    }

    /// <summary>The converted magic effects of a Morrowind effect (sEffectPoison: MWEffectPoison…), for GetEffect.</summary>
    FormList? EffectList(string effect)
    {
        string name = effect.StartsWith("sEffect", StringComparison.OrdinalIgnoreCase) ? effect[7..] : effect;
        // Every spell converted, so every effect exists to be listed (not FinishMagic: it places the Mark/Recall anchor
        // and the Intervention spots, and called once per effect it put the same anchor in the start cell again each
        // time: "Two records with the same FormKey", 2026-10-15).
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "SPEL").ToList()) MwSpell(rec.Id);
        var list = new FormList(mod) { EditorID = "MWEffects" + Sanitize(name) };
        foreach (var m in mod.MagicEffects.Where(m => m.EditorID?.StartsWith("MWEffect" + name, StringComparison.OrdinalIgnoreCase) == true))
            list.Items.Add(m.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        return list;
    }

    /// <summary>A Morrowind soul gem (misc_soulgem_petty…) as Skyrim's filled gem of that size.</summary>
    static FormKey? SoulGemFor(string gem)
    {
        string g = gem.ToLowerInvariant();
        return g.Contains("azura") ? Sk(0x063B27) : g.Contains("grand") ? Sk(0x02E4FF) : g.Contains("greater") ? Sk(0x02E4FB)
            : g.Contains("common") ? Sk(0x02E4F3) : g.Contains("lesser") ? Sk(0x02E4E5) : Sk(0x02E4E3);
    }

    /// <summary>The cells GetPCCell's name means: interiors starting with it, and the exterior cells of that name.</summary>
    FormList? CellList(string name)
    {
        var list = new FormList(mod) { EditorID = "MWCells" + Sanitize(name) };
        foreach (var cell in interiorsByName.Where(kv => kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value))
            list.Items.Add(cell.ToLink<ISkyrimMajorRecordGetter>());
        if (exteriorCells is not null)
            foreach (var mw in world.Exteriors.Values.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                        if (exteriorCells.TryGetValue((mw.GridX * 2 + i, mw.GridY * 2 + j), out var cell))
                            list.Items.Add(cell.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        return list;
    }

    /// <summary>ShowMap's place: the map markers of towns of that name.</summary>
    FormList? MapMarkerList(string name)
    {
        var list = new FormList(mod) { EditorID = "MWMapMarkers" + Sanitize(name) };
        foreach (var (town, marker) in townMapMarkers)
            if (town.Equals(name, StringComparison.OrdinalIgnoreCase) || town.StartsWith(name + ",", StringComparison.OrdinalIgnoreCase))
                list.Items.Add(marker.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        return list;
    }

    /// <summary>PlayBink's videos: Morrowind's own Bink files beside Skyrim's (Data\Video\TamrielMerge_MW_…).</summary>
    void CopyBinks()
    {
        foreach (var file in scriptSet!.All.SelectMany(o => o.Binks).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string source = Path.Combine(data.DataFiles, "Video", Path.GetFileName(file.Replace('/', '\\')));
            if (!File.Exists(source)) { Problems.Add($"script PlayBink: {file} not found"); continue; }
            string target = Path.Combine(modOutDir, "Video", MwPapyrus.BinkName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
    }
}
