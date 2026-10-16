using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NAudio.Wave;
using Noggog;
using static TamrielMerge.Redguard.RgScript;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's object scripts in Redguard.esm: every scripted object's SOUP program translated to Papyrus
/// (<see cref="RgPapyrus"/>, on the library Papyrus\TMRgSoup.psc), compiled with the Creation Kit's compiler and attached
/// to what stands for the object in Skyrim:
/// <list type="bullet">
/// <item>scripted props (levers, doors, blocks, blades, pickups) are activators instead of statics — named by the
/// activation label their script tests (ACTIVATE("$ctl") = "Pull lever"), with collision that moves with them when a
/// script moves them (RgMeshConverter's moving variant, keyframed);</item>
/// <item>people and creatures carry their own script on their reference; a talking actor's conversations stay Skyrim
/// dialogue (activating it talks, its script's talk code is the dialogue's);</item>
/// <item>everything else (sound emitters, effect and light objects, cutscene controllers, scene doubles of people who
/// live elsewhere) carries its script on a marker where it stood; a light object's script switches its light;</item>
/// <item>an item's use script (ITEM.INI use_script) is an activator's script that the item's use effect places beside
/// the player (TMRgUseItem).</item>
/// </list>
/// The scripts share the quest RGScripts (TMRgState): Redguard's flags as globals (the dialogue's RGFlag* globals, all
/// 369 of them, by index in RGFlags), the items (RGItems), the sound effects (RGSounds, MAIN.SFX as sound markers) and
/// the lines the scripts speak (RGLines: an actor's own voiced topic, else the recording played where it is said, with
/// the line shown). Same programs in several scenes (the item and effect scripts every scene carries) are one script.
/// START.RGM's scripts are the opening, which TMRgOpening plays: they are translated (and counted) but not attached.
/// </summary>
public sealed partial class RgPluginBuilder
{
    RgPapyrus? papyrus;
    Dictionary<string, RgPapyrus.LibFunction> soupLibrary = [];
    List<string> refNames = [];
    HashSet<string> taskFunctions = [];
    /// <summary>Each scene's translated programs, by actor (script) name.</summary>
    readonly Dictionary<(string Scene, string Actor), RgPapyrus.Output> sceneScripts = [];
    readonly Dictionary<string, RgPapyrus.Output> scriptsByKey = [];
    /// <summary>Objects other scripts name (they must be persistent to be pointed at) and objects scripts move.</summary>
    readonly HashSet<(string Scene, string Actor)> namedObjects = [], movedObjects = [];
    /// <summary>What carries each scene object's script, by MPOB index.</summary>
    readonly Dictionary<(string Scene, int Index), (IPlaced Ref, ObjectReferenceKind Kind)> carriers = [];
    public enum ObjectReferenceKind { Prop, Actor, Marker, Light }
    /// <summary>The light placed for each light object (type 4), by MPOB index.</summary>
    readonly Dictionary<(string Scene, int Index), PlacedObject> objectLights = [];
    public int ScriptStatements { get; private set; }
    public Dictionary<RgPapyrus.Category, int> ScriptCounts { get; } = [];
    public Dictionary<string, Dictionary<RgPapyrus.Category, int>> ScriptFunctions { get; } = [];
    public int ScriptsWritten { get; private set; }
    public int ScriptsCompiled { get; private set; }
    public int ScriptsAttached { get; private set; }
    public List<string> CompileFailures { get; } = [];
    public bool CompilerFound { get; private set; }
    /// <summary>Per scene, statements (counted as the audit counts: every scene's every program).</summary>
    public Dictionary<string, (int Statements, Dictionary<RgPapyrus.Category, int> Counts)> ScriptsByScene { get; } = [];

    static readonly HashSet<string> MovingFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "RotateByAxis", "RotateToAxis", "MoveByAxis", "MoveAlongAxis", "MoveObjectAxis", "MoveToLocation", "WanderToLocation",
        "ResetRotation", "ResetPosition", "ReloadRotation", "MoveForward", "MoveBackward", "WalkForward", "WalkBackward", "MoveMarker",
        "MoveNodeMarker", "MoveNodeLocation", "sRotate", "sRotateByAxis", "sRotateToAxis", "sMove", "sMoveByAxis", "sMoveAlongAxis",
    };

    /// <summary>Scripts acting on Cyrus rather than on their own object (as Redguard's player scripts did).</summary>
    static readonly HashSet<string> PlayerScripts = new(StringComparer.OrdinalIgnoreCase) { "COMMON" };

    static bool Trivial(List<Stmt> program)
    {
        bool any = false;
        void Walk(List<Stmt> l) { foreach (var s in l) { if (s is CallStmt or AssignStmt) any = true; if (s is IfStmt f) Walk(f.Body); } }
        Walk(program);
        return !any;
    }

    bool TalksIn(Scene scene, string actor) =>
        scene.Script.Calls.Any(c => c.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase) && c.Function == Fn("menuAddItem"))
        && scene.Script.Calls.Any(c => c.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase) && c.Function == Fn("PlayerLineUp"))
        && ActorFor(actor) is not null;

    /// <summary>The text, function names and flags the scripts and dialogue read (once).</summary>
    void LoadText()
    {
        string def = Path.Combine(rgDir, "soup386", "SOUP386.DEF");
        functionNames = RgScript.FunctionNames(def);
        flagDefs = RgScript.Flags(def);
        (taskFunctions, refNames) = RgPapyrus.DefInfo(def);
        rtx ??= RgRtx.Load(Path.Combine(rgDir, "ENGLISH.RTX"));
        if (rtx is not null) LearnProperNouns();
        DialogueQuest();
    }

    // ---- translation (before the world is placed: it decides what is an activator and what is persistent) ----

    sealed class ScriptContext(RgPluginBuilder b, Scene scene, string actor, Npc? speaker) : RgPapyrus.Context
    {
        public (int Index, string Text, float Seconds) Line(int tag, bool cyrus) => b.ScriptLine(tag, cyrus ? null : speaker);
        public int LogStage(int tag) => b.LogStage(tag);
        // A person sending Cyrus on by a map they chose (VILEKID after the riddles) isn't a door where they stand.
        public bool LoadWorldIsDoor(int? map, int? marker, bool setByScript) =>
            !(setByScript && People.ContainsKey(actor)) && marker is not null && scene.Data.Objects.Any(o => o.Script.Equals(actor, StringComparison.OrdinalIgnoreCase));
        public Vector3 Place(Vector3 redguard) => At(scene, redguard);
        public RgPapyrus.Category HandItem(int item) => HandCategory(b.ItemDefs, item);
        public int Picture(string file) => b.PictureIndex(file);
        public bool ActsOnPlayer => PlayerScripts.Contains(actor) || b.ItemDefs.Any(d => d.UseScript.Equals(actor, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Use scripts of items one drinks (Skyrim's own potion use shows the drinking).</summary>
    static readonly HashSet<string> Drinks = new(StringComparer.OrdinalIgnoreCase) { "IHEALTH", "IIRONSK", "ISTRENTH", "IBLOOD", "IMILK", "IECTO", "ISAP", "IBOTWAT", "IBOTALOE" };

    /// <summary>What HandItem(n) is in Skyrim: an empty hand, a sword (type 1) or the torch (type 3) are Skyrim's
    /// equip calls, a drink Skyrim's own potion use; other items have nothing to show them held.</summary>
    public static RgPapyrus.Category HandCategory(List<ItemDef> defs, int item)
    {
        if (item <= 0) return RgPapyrus.Category.Native;
        var d = defs.FirstOrDefault(x => x.Id == item);
        if (d is null) return RgPapyrus.Category.Approximate;
        return d.Type is 1 or 3 || Drinks.Contains(d.UseScript) ? RgPapyrus.Category.Native : RgPapyrus.Category.Approximate;
    }

    void TranslateScripts(Action<string>? log)
    {
        string soup = Path.Combine(AppContext.BaseDirectory, "Papyrus", "TMRgSoup.psc");
        if (!File.Exists(soup)) { Problems.Add($"scripts: {soup} missing"); return; }
        soupLibrary = RgPapyrus.LoadLibrary(soup);
        papyrus = new RgPapyrus(functionNames, taskFunctions, refNames, soupLibrary);
        foreach (var scene in scenes.Values.OrderBy(s => s.Name == "ISLAND" ? 0 : 1).ThenBy(s => s.Name))
        {
            int sceneStatements = 0;
            var sceneCounts = new Dictionary<RgPapyrus.Category, int>();
            foreach (var (actor, program) in scene.Script.Programs)
            {
                bool talks = TalksIn(scene, actor);
                bool hasObject = scene.Data.Objects.Any(o => o.Script.Equals(actor, StringComparison.OrdinalIgnoreCase));
                var speaker = hasObject && scene.Data.Objects.Any(o => o.Script.Equals(actor, StringComparison.OrdinalIgnoreCase) && o.Model.Length == 0) ? ActorFor(actor) : null;
                var globals = talks ? Enumerable.Range(0, scene.Script.Locals.GetValueOrDefault(actor)?.Length ?? 0).ToHashSet() : [];
                string hash = scene.Script.Hash.GetValueOrDefault(actor, $"{scene.Name}:{actor}");
                string key = $"{hash}|{talks}|{speaker?.FormKey}|{actor}|{(talks ? "" : hasObject ? "o" : "")}";
                if (!scriptsByKey.TryGetValue(key, out var output))
                {
                    string name = "TMRg" + Sanitize(scene.Name) + "_" + Sanitize(actor);
                    try
                    {
                        output = papyrus.Translate(name, $"Redguard {scene.Name} {actor}: its SOUP script, converted (RgPapyrus).", program,
                            scene.Script.Entry.GetValueOrDefault(actor), scene.Script.Locals.GetValueOrDefault(actor) ?? [],
                            scene.Script.Strings.GetValueOrDefault(actor) ?? [], new ScriptContext(this, scene, actor, speaker), talks, globals);
                        output.UseItems.AddRange(papyrus.UseItemsOf(program));
                    }
                    catch (Exception e) { Problems.Add($"script {scene.Name} {actor}: {e.Message}"); continue; }
                    scriptsByKey[key] = output;
                }
                sceneScripts[(scene.Name, actor)] = output;
                sceneStatements += output.Statements;
                foreach (var (c, n) in output.Counts) { sceneCounts[c] = sceneCounts.GetValueOrDefault(c) + n; ScriptCounts[c] = ScriptCounts.GetValueOrDefault(c) + n; }
                foreach (var (f, d) in output.ByFunction)
                {
                    var into = ScriptFunctions.TryGetValue(f, out var x) ? x : ScriptFunctions[f] = [];
                    foreach (var (c, n) in d) into[c] = into.GetValueOrDefault(c) + n;
                }
                foreach (var named in output.Objects) namedObjects.Add((scene.Name, named));
                // What gets moved: this object, or the objects it names, by a moving call.
                var strings = scene.Script.Strings.GetValueOrDefault(actor) ?? [];
                void Walk(List<Stmt> l)
                {
                    foreach (var s in l)
                    {
                        if (s is CallStmt c && MovingFunctions.Contains(functionNames.ElementAtOrDefault(c.Function) ?? ""))
                        {
                            if (c.Target is null or "Me") movedObjects.Add((scene.Name, actor));
                            else if (c.Target.StartsWith("string:") && int.TryParse(c.Target[7..], out var si) && si < strings.Count) movedObjects.Add((scene.Name, strings[si]));
                        }
                        if (s is IfStmt f) Walk(f.Body);
                    }
                }
                Walk(program);
            }
            ScriptStatements += sceneStatements;
            ScriptsByScene[scene.Name] = (sceneStatements, sceneCounts);
        }
        log?.Invoke($"scripts: {sceneScripts.Count} programs in {scenes.Count} scenes as {scriptsByKey.Count} Papyrus scripts, {ScriptStatements} statements");
    }

    /// <summary>The scene's translated script for an object, when it does anything.</summary>
    RgPapyrus.Output? ScriptOf(Scene scene, string actor) =>
        scene.Name != OpeningScene && actor.Length > 0 && sceneScripts.TryGetValue((scene.Name, actor), out var o)
        && scene.Script.Programs.TryGetValue(actor, out var p) && !Trivial(p) ? o : null;

    // ---- lines ----

    FormList? linesList;
    readonly Dictionary<(int Tag, FormKey? Speaker), (int Index, string Text, float Seconds)> lines = [];

    (int Index, string Text, float Seconds) ScriptLine(int tag, Npc? speaker)
    {
        if (lines.TryGetValue((tag, speaker?.FormKey), out var hit)) return hit;
        string t = TagString(tag);
        if (rtx is null || !rtx.Entries.TryGetValue(t, out var e)) return lines[(tag, speaker?.FormKey)] = (-1, "", 0);
        string text = Pretty(e.Text);
        float seconds = e.AudioLength > 0 && e.SampleRate > 0 ? e.AudioLength / (float)(e.SampleRate * Math.Max(1, e.Bits / 8)) : MathF.Max(1.5f, text.Length / 14f);
        linesList ??= new FormList(mod) { EditorID = "RGLines" };
        if (speaker is not null && dialogueQuest is not null)
        {
            // The actor's own line: a topic it says, voiced from the RTX by rg-voices.
            var said = new GetIsIDConditionData();
            said.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(said, speaker.FormKey);
            var r = new DialogResponses(mod)
            {
                Conditions = [new ConditionFloat { Data = said, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
                Responses = [new DialogResponse { Text = text, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
                Flags = new DialogResponseFlags(),
            };
            string edid = $"RGLine{Sanitize(t)}{Sanitize(speaker.EditorID ?? "")}";
            if (!topicIds.Add(edid)) { int n = 2; while (!topicIds.Add($"{edid}{n}")) n++; edid = $"{edid}{n}"; }
            var topic = new DialogTopic(mod)
            {
                EditorID = edid,
                Quest = dialogueQuest.ToNullableLink<IQuestGetter>(),
                Priority = 50,
                Category = DialogTopic.CategoryEnum.Topic,
                Subtype = DialogTopic.SubtypeEnum.Custom,
                Responses = [r],
            };
            mod.DialogTopics.Add(topic);
            if (e.AudioLength > 0) VoiceMap.Add(new VoiceLine(r.FormKey.ID, 1, t));
            linesList.Items.Add(topic.ToLink<ISkyrimMajorRecordGetter>());
            return lines[(tag, speaker.FormKey)] = (linesList.Items.Count - 1, text, seconds + 0.3f);
        }
        // Said by Cyrus or by something that isn't a person: the recording where it is said, the text shown.
        if (e.AudioLength <= 0) return lines[(tag, null)] = (-1, text, seconds);
        string rel = Path.Combine("fx", "tamrielmerge", "rg", "lines", Sanitize(t).ToLowerInvariant() + $"_{tag & 0xFFFF:x4}.wav");
        string wav = Path.Combine(modOutDir, "sound", rel);
        if (!File.Exists(wav))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            string raw = Path.Combine(Path.GetTempPath(), $"tm_rg_line_{Guid.NewGuid():N}.wav");
            try
            {
                if (!rtx.WriteWav(e, raw)) return lines[(tag, null)] = (-1, text, seconds);
                Resample(raw, wav);
            }
            catch (Exception ex) { Problems.Add($"line {t}: {ex.Message}"); return lines[(tag, null)] = (-1, text, seconds); }
            finally { File.Delete(raw); }
        }
        var marker = SoundFor($"RGLine{Sanitize(t)}{tag & 0xFFFF:X4}", rel, "AudioCategoryVOCGeneral", loop: false);
        linesList.Items.Add(marker.ToLink<ISkyrimMajorRecordGetter>());
        return lines[(tag, null)] = (linesList.Items.Count - 1, text, seconds + 0.3f);
    }

    static void Resample(string from, string to)
    {
        using var reader = new WaveFileReader(from);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 1)) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(to, resampler);
    }

    /// <summary>A sound marker playing a wav under sound\ (a copy of a vanilla descriptor: a made-up one crashed the
    /// game), looping or once, in a sound category.</summary>
    SoundMarker SoundFor(string edid, string relWav, string category, bool loop)
    {
        var template = SkyrimEsm.SoundDescriptors.First(d => d.EditorID == (loop ? "OBJWindmillSolitudeLPSD" : "VOCShoutFXThrowVoice"));
        var descriptor = mod.SoundDescriptors.DuplicateInAsNewRecord(template);
        descriptor.EditorID = edid + "SD";
        descriptor.SoundFiles.Clear();
        descriptor.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(relWav));
        descriptor.Category = new FormLinkNullable<ISoundCategoryGetter>(SkyrimEsm.SoundCategories.First(c => c.EditorID == category).FormKey);
        var marker = new SoundMarker(mod) { EditorID = edid, SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
        mod.SoundMarkers.Add(marker);
        return marker;
    }

    // ---- placing what carries the scripts ----

    readonly Dictionary<(string Palette, string Model, bool Moving, string Label), Mutagen.Bethesda.Skyrim.Activator?> scriptedActivators = [];

    /// <summary>The activation label a script tests (its first ACTIVATE("$xyz")), as (verb, name).</summary>
    (string Verb, string Name)? LabelOf(Scene scene, string actor)
    {
        int activate = Fn("ACTIVATE");
        var call = scene.Script.Calls.FirstOrDefault(c => c.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase) && c.Function == activate && c.Args.Length > 0 && c.Args[0] is int);
        if (call is null || rtx is null || !rtx.Entries.TryGetValue(TagString(call.Args[0]!.Value), out var e)) return null;
        string label = TitleCase(e.Text.Trim());
        foreach (var verb in new[] { "Talk to", "Look at", "Pick up", "Put in", "Put on", "Climb up", "Climb down", "Go to", "Look in", "Look through" })
            if (label.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) return (verb, label[(verb.Length + 1)..]);
        int space = label.IndexOf(' ');
        return space > 0 ? (label[..space], label[(space + 1)..]) : ("Use", label);
    }

    /// <summary>A scripted prop: an activator with the prop's model (its moving variant when scripts move it).</summary>
    PlacedObject? PlaceScriptedProp(Scene scene, int index, RgScene.Placed o)
    {
        string name = o.Model.Split('.')[0].Trim().ToUpperInvariant();
        bool moving = movedObjects.Contains((scene.Name, o.Script));
        var label = LabelOf(scene, o.Script);
        var key = (scene.Palette, name, moving, label is { } l ? l.Verb + "|" + l.Name : "");
        if (!scriptedActivators.TryGetValue(key, out var act))
        {
            scriptedActivators[key] = null;
            if (Model(scene, name) is not { } model) return null;
            RgMeshConverter.Result? mesh;
            try { mesh = moving ? meshes.Convert(model, name, scene.Palette, modOutDir, moving: true) : StaticFor(scene, o.Model)?.Mesh; }
            catch (Exception e) { Problems.Add($"model {name}: {e.Message}"); return null; }
            if (mesh is null || mesh.Path.Length == 0) return null;
            short C(float v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            act = new Mutagen.Bethesda.Skyrim.Activator(mod)
            {
                EditorID = $"RGAct{Sanitize(scene.Palette)}{Sanitize(name)}{(moving ? "M" : "")}{scriptedActivators.Count}",
                ObjectBounds = new ObjectBounds { First = new P3Int16(C(mesh.Min.X), C(mesh.Min.Y), C(mesh.Min.Z)), Second = new P3Int16(C(mesh.Max.X), C(mesh.Max.Y), C(mesh.Max.Z)) },
                Model = new Model { File = mesh.Path[7..] },
            };
            if (label is { } lb) { act.Name = lb.Name; act.ActivateTextOverride = lb.Verb; }
            mod.Activators.Add(act);
            scriptedActivators[key] = act;
            meshOfBase[act.FormKey] = mesh;
        }
        if (act is null) return null;
        var at = At(scene, o.Position);
        var euler = RgSpace.Euler(o.Rotation);
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(act.FormKey),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(euler.X, euler.Y, euler.Z) },
        };
        if (namedObjects.Contains((scene.Name, o.Script)))
        {
            placed.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
            PersistentCellFor(scene).Persistent.Add(placed);
        }
        else CellFor(scene, at).Temporary.Add(placed);
        meshOf[placed.FormKey] = meshOfBase[act.FormKey];
        carriers[(scene.Name, index)] = (placed, ObjectReferenceKind.Prop);
        return placed;
    }

    readonly Dictionary<FormKey, RgMeshConverter.Result> meshOfBase = [];

    /// <summary>Markers for the scripts with nothing else to carry them (sound, effect and light objects, cutscene
    /// controllers, scene doubles), once the props and actors are placed.</summary>
    void PlaceScriptMarkers(Scene scene)
    {
        if (scene.Name == OpeningScene) return;
        for (int i = 0; i < scene.Data.Objects.Count; i++)
        {
            var o = scene.Data.Objects[i];
            if (carriers.ContainsKey((scene.Name, i)) || ScriptOf(scene, o.Script) is null) continue;
            var at = At(scene, o.Position);
            var euler = RgSpace.Euler(o.Rotation);
            var marker = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(Sk(0x3B)),   // XMarker
                Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z), Rotation = new P3Float(euler.X, euler.Y, euler.Z) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            PersistentCellFor(scene).Persistent.Add(marker);
            carriers[(scene.Name, i)] = (marker, objectLights.ContainsKey((scene.Name, i)) ? ObjectReferenceKind.Light : ObjectReferenceKind.Marker);
        }
    }

    // ---- attaching ----

    Quest? scriptsQuest;
    FormList? flagsList, soundsList;
    readonly Dictionary<string, FormList> sceneMarkers = [];
    readonly Dictionary<int, SoundMarker?> sfxMarkers = [];

    /// <summary>Writes, compiles and attaches the scripts. Run after the dialogue, the opening and the doors.</summary>
    void AttachScripts(Action<string>? log)
    {
        if (papyrus is null) return;
        // Every flag as its global (the dialogue's, by the same names), in index order.
        flagsList = new FormList(mod) { EditorID = "RGFlags" };
        mod.FormLists.Add(flagsList);
        for (int i = 0; i < flagDefs.Count; i++) flagsList.Items.Add(GlobalFor($"F{i}", "", []).ToLink<ISkyrimMajorRecordGetter>());
        // Sound effects: those scripts play.
        soundsList = new FormList(mod) { EditorID = "RGSounds" };
        mod.FormLists.Add(soundsList);
        BuildSounds();
        linesList ??= new FormList(mod) { EditorID = "RGLines" };
        mod.FormLists.Add(linesList);
        if (!mod.Quests.Contains(logQuest!) && logQuest!.Stages.Count > 0) mod.Quests.Add(logQuest);

        scriptsQuest = new Quest(mod)
        {
            EditorID = "RGScripts",
            Name = "Redguard Scripts",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
            Priority = 10,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(scriptsQuest);
        var state = new ScriptEntry { Name = "TMRgState" };
        state.Properties.Add(ObjProp("Flags", flagsList.FormKey));
        state.Properties.Add(ObjProp("Items", itemList?.FormKey));
        state.Properties.Add(ObjProp("Sounds", soundsList.FormKey));
        state.Properties.Add(ObjProp("Lines", linesList.FormKey));
        if (mod.Quests.Contains(logQuest!)) state.Properties.Add(ObjProp("LogQuest", logQuest!.FormKey));
        state.Properties.Add(ObjProp("Fireball", Sk(0x07E56D)));   // TrapFirebolt01
        state.Properties.Add(ObjProp("Breath", Sk(0x03F9EB)));     // VoiceFireBreath1
        state.Properties.Add(ObjProp("Zap", Sk(0x07E56B)));        // TrapSparks01
        var shows = new ScriptObjectListProperty { Name = "MenuShow" };
        for (int i = 0; i < 10; i++)
        {
            var g = new GlobalShort(mod) { EditorID = $"RGMenuShow{i}", Data = 0 };
            mod.Globals.Add(g);
            menuShow.Add(g);
            shows.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(g.FormKey) });
        }
        state.Properties.Add(shows);
        // What stands in for Redguard's engine (RgEngine): idles for its animation groups, its screen pictures, the
        // movement helpers, and whether the camera returns to first person after a shot of Cyrus (0: it stays).
        state.Properties.Add(ObjProp("Idles", IdleList("RGIdles", RgPapyrus.PeopleIdles).FormKey));
        state.Properties.Add(ObjProp("PlayerIdles", IdleList("RGPlayerIdles", RgPapyrus.CyrusIdles).FormKey));
        state.Properties.Add(ObjProp("IdleStop", Sk(0x0E4242)));   // IdleStop
        state.Properties.Add(ObjProp("Pictures", BuildPictures().FormKey));
        state.Properties.Add(ObjProp("Mover", MovementQuest().FormKey));
        var firstPerson = new GlobalShort(mod) { EditorID = "RGCameraFirstPerson", Data = 0 };
        mod.Globals.Add(firstPerson);
        state.Properties.Add(ObjProp("FirstPersonAfterShots", firstPerson.FormKey));
        scriptsQuest.VirtualMachineAdapter = new QuestAdapter { Scripts = [state] };
        FinishMovement();
        SyncGroups();

        // Sources and compiling.
        string sourceDir = Path.Combine(modOutDir, "Source", "Scripts"), scriptsDir = Path.Combine(modOutDir, "Scripts");
        Directory.CreateDirectory(sourceDir);
        foreach (var o in scriptsByKey.Values.DistinctBy(o => o.ScriptName))
        {
            string path = Path.Combine(sourceDir, o.ScriptName + ".psc");
            if (!File.Exists(path) || File.ReadAllText(path) != o.Source) File.WriteAllText(path, o.Source);
            ScriptsWritten++;
        }
        var compiled = CompileRedguardScripts(sourceDir, scriptsDir, scriptsByKey.Values.Select(o => o.ScriptName).ToHashSet(StringComparer.OrdinalIgnoreCase), log);
        ScriptsCompiled = compiled.Count;
        if (!CompilerFound) Problems.Add("scripts: no Creation Kit Papyrus compiler (or Skyrim's script sources): Redguard's scripts are written but not attached");

        // Use items: the shells their effects place.
        foreach (var (id, mgef) in useEffects)
        {
            var def = ItemDefs.FirstOrDefault(d => d.Id == id);
            if (def is null) continue;
            var scene = scenes.Values.OrderBy(s => s.Name == "ISLAND" ? 0 : 1).FirstOrDefault(s => sceneScripts.ContainsKey((s.Name, def.UseScript)));
            if (scene is null || sceneScripts[(scene.Name, def.UseScript)] is not { } o || !compiled.Contains(o.ScriptName)) { Problems.Add($"item {id}: use script {def.UseScript} not available"); continue; }
            var shell = new Mutagen.Bethesda.Skyrim.Activator(mod) { EditorID = $"RGShell{Sanitize(def.UseScript)}{id}", ObjectBounds = new ObjectBounds() };
            shell.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [Entry(o, scene, def.UseScript, shell: true)] };
            mod.Activators.Add(shell);
            var item = itemRecords[id];
            mgef.VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMRgUseItem", Properties = [ObjProp("Shell", shell.FormKey), ObjProp("Item", item)] }],
            };
            ScriptsAttached++;
        }

        // The world's objects.
        foreach (var ((sceneName, index), (carrier, kind)) in carriers)
        {
            var scene = scenes[sceneName];
            var obj = scene.Data.Objects[index];
            if (ScriptOf(scene, obj.Script) is not { } o || !compiled.Contains(o.ScriptName)) continue;
            // A script that only does what Skyrim has no counterpart for (a torch's flicker, a sprite's frames) and waits
            // would only cost time: translated, not attached.
            if (Idle(o)) { ScriptsIdle++; continue; }
            var entry = Entry(o, scene, obj.Script, shell: false);
            if (kind == ObjectReferenceKind.Actor && TalksIn(scene, obj.Script)) entry.Properties.Add(new ScriptBoolProperty { Name = "Talks", Data = true });
            if (objectLights.TryGetValue((sceneName, index), out var light)) entry.Properties.Add(ObjProp("MyLight", light.FormKey));
            // Locations: the start, then RALC's offsets.
            var offsets = scene.Script.LocationOffsets.GetValueOrDefault(obj.Script) ?? [];
            if (offsets.Count > 0)
            {
                var locs = new List<float>();
                foreach (var p in new[] { obj.Position }.Concat(offsets.Select(d => obj.Position + d)))
                {
                    var s = At(scene, p);
                    locs.AddRange([s.X, s.Y, s.Z]);
                }
                entry.Properties.Add(new ScriptFloatListProperty { Name = "Locations", Data = [.. locs] });
            }
            switch (carrier)
            {
                case PlacedObject po: (po.VirtualMachineAdapter ??= new VirtualMachineAdapter()).Scripts.Add(entry); break;
                case PlacedNpc pn: (pn.VirtualMachineAdapter ??= new VirtualMachineAdapter()).Scripts.Add(entry); break;
            }
            ScriptsAttached++;
        }
        log?.Invoke($"scripts: {ScriptsWritten} written, {ScriptsCompiled} compiled, {ScriptsAttached} attached ({ScriptsIdle} with nothing for Skyrim to do left off); {linesList.Items.Count} lines, {sfxMarkers.Count(kv => kv.Value is not null)} sound effects");
    }

    public int ScriptsIdle { get; private set; }

    /// <summary>The scripts the converted ones run on (Papyrus folder; compiled into the output with them).</summary>
    static readonly string[] Library = ["TMRgSoup", "TMRgState", "TMRgUseItem", "TMRgMove", "TMRgRope", "TMRgLaw"];

    static readonly HashSet<string> ControlOnly = new(StringComparer.OrdinalIgnoreCase) { "if", "goto", "end", "gosub", "return", "endint", "pause", "Wait", "WaitOnTasks", "WaitNonMulti" };

    static bool Idle(RgPapyrus.Output o) =>
        !o.ByFunction.Any(kv => !ControlOnly.Contains(kv.Key) && kv.Value.Keys.Any(c => c is RgPapyrus.Category.Native or RgPapyrus.Category.Approximate));

    static ScriptObjectProperty ObjProp(string name, FormKey? key) => new() { Name = name, Object = key is { } k ? new FormLink<ISkyrimMajorRecordGetter>(k) : new FormLink<ISkyrimMajorRecordGetter>() };

    /// <summary>A script's entry with the properties of its object in its scene.</summary>
    ScriptEntry Entry(RgPapyrus.Output o, Scene scene, string actor, bool shell)
    {
        var entry = new ScriptEntry { Name = o.ScriptName };
        entry.Properties.Add(ObjProp("RG", scriptsQuest!.FormKey));
        entry.Properties.Add(new ScriptIntProperty { Name = "EntryPoint", Data = o.EntryBlock });
        if (o.Interrupt) entry.Properties.Add(new ScriptBoolProperty { Name = "Interrupt", Data = true });
        if (shell) entry.Properties.Add(new ScriptBoolProperty { Name = "Shell", Data = true });
        if (shell || PlayerScripts.Contains(actor)) entry.Properties.Add(new ScriptBoolProperty { Name = "ActsOnPlayer", Data = true });
        // Variables the dialogue tests: its globals.
        foreach (var v in o.GlobalLocals) entry.Properties.Add(ObjProp($"G{v}", GlobalFor($"L:{actor}:{v}", actor, scene.Script.Locals).FormKey));
        if (o.Objects.Count > 0)
        {
            var list = new ScriptObjectListProperty { Name = "Objects" };
            foreach (var name in o.Objects)
            {
                var target = carriers.Where(kv => kv.Key.Scene == scene.Name && scene.Data.Objects[kv.Key.Index].Script.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Value.Ref).OrderByDescending(r => (r.MajorRecordFlagsRaw & 0x400) != 0).FirstOrDefault();
                list.Objects.Add(new ScriptObjectProperty { Object = target is null ? new FormLink<ISkyrimMajorRecordGetter>() : new FormLink<ISkyrimMajorRecordGetter>(target.FormKey) });
            }
            entry.Properties.Add(list);
        }
        if (o.Places.Count > 0)
        {
            var list = new ScriptObjectListProperty { Name = "Places" };
            foreach (var (map, marker, facing) in o.Places)
                list.Objects.Add(new ScriptObjectProperty { Object = PlaceMarker(map, marker, facing) is { } pm ? new FormLink<ISkyrimMajorRecordGetter>(pm.FormKey) : new FormLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(list);
        }
        if (UsesMarkers(o)) entry.Properties.Add(ObjProp("Markers", MarkersOf(scene).FormKey));
        if (o.Menus.Count > 0)
        {
            var list = new ScriptObjectListProperty { Name = "Menus" };
            foreach (var items in o.Menus) list.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(MenuMessage(items).FormKey) });
            entry.Properties.Add(list);
        }
        if (o.UseItems.Count > 0)
        {
            entry.Properties.Add(new ScriptIntListProperty { Name = "UseItems", Data = [.. o.UseItems] });
            if (o.UseItems.Count > 1) entry.Properties.Add(ObjProp("UseMenu", UseMenu(o.UseItems).FormKey));
        }
        // Starting attributes the engine reads (panic group, camera target height), its sync group, hanging from it.
        if (scene.Script.Attributes.TryGetValue(actor, out var attrs))
        {
            var init = new List<int>();
            foreach (int a in new[] { 44, 47, 48, 49 }) if (attrs[a] != 0) init.AddRange([a, attrs[a]]);
            if (init.Count > 0) entry.Properties.Add(new ScriptIntListProperty { Name = "AttrInit", Data = [.. init] });
            if (syncGroups.TryGetValue((scene.Name, attrs[29]), out var g) && o.ByFunction.ContainsKey("SyncWithGroup"))
            {
                entry.Properties.Add(new ScriptIntProperty { Name = "SyncGroup", Data = g.Id });
                entry.Properties.Add(new ScriptIntProperty { Name = "SyncMembers", Data = g.Members });
            }
        }
        if (o.ByFunction.ContainsKey("PlayerHanging")) entry.Properties.Add(new ScriptBoolProperty { Name = "Hangable", Data = true });
        return entry;
    }

    /// <summary>Redguard's synchronisation groups (attr_group of objects whose scripts SyncWithGroup), per scene, as
    /// barrier numbers on RGScripts with how many objects take part.</summary>
    readonly Dictionary<(string Scene, int Group), (int Id, int Members)> syncGroups = [];

    void SyncGroups()
    {
        int sync = Fn("SyncWithGroup");
        foreach (var scene in scenes.Values.OrderBy(s => s.Name))
        {
            var members = new Dictionary<int, int>();
            foreach (var ((sceneName, index), _) in carriers)
            {
                if (sceneName != scene.Name) continue;
                string actor = scene.Data.Objects[index].Script;
                if (!scene.Script.Attributes.TryGetValue(actor, out var a) || a[29] == 0) continue;
                if (!scene.Script.Calls.Any(c => c.Function == sync && c.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase))) continue;
                members[a[29]] = members.GetValueOrDefault(a[29]) + 1;
            }
            foreach (var (g, n) in members.OrderBy(kv => kv.Key))
                if (syncGroups.Count < 128) syncGroups[(scene.Name, g)] = (syncGroups.Count, n);
        }
    }

    static bool UsesMarkers(RgPapyrus.Output o) => o.ByFunction.Keys.Any(f => f is "MoveMarker" or "MoveNodeMarker" or "AtNodeMarker");

    /// <summary>The scene's markers (MPMK) as persistent markers, in a list by index.</summary>
    FormList MarkersOf(Scene scene)
    {
        if (sceneMarkers.TryGetValue(scene.Name, out var list)) return list;
        list = new FormList(mod) { EditorID = $"RGMarkers{Sanitize(scene.Name)}" };
        mod.FormLists.Add(list);
        foreach (var m in scene.Data.Markers)
        {
            var at = At(scene, m);
            var r = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
                Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z + 8), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            PersistentCellFor(scene).Persistent.Add(r);
            list.Items.Add(r.ToLink<ISkyrimMajorRecordGetter>());
        }
        return sceneMarkers[scene.Name] = list;
    }

    readonly Dictionary<(int, int), PlacedObject?> placeMarkers = [];

    /// <summary>Where a script's LoadWorld(map, marker, facing) leads: a heading marker at the map's marker.</summary>
    PlacedObject? PlaceMarker(int? map, int? marker, int? facing)
    {
        if (map is not int m || marker is not int k) return null;
        if (placeMarkers.TryGetValue((m, k), out var hit)) return hit;
        placeMarkers[(m, k)] = null;
        if (!ini.Maps.TryGetValue(m, out var mi) || !scenes.TryGetValue(mi.Scene, out var to) || k >= to.Data.Markers.Count) return null;
        var at = At(to, to.Data.Markers[k]);
        var r = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z + 8), Rotation = new P3Float(0, 0, (facing ?? 0) * MathF.Tau / 2048) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        PersistentCellFor(to).Persistent.Add(r);
        return placeMarkers[(m, k)] = r;
    }

    readonly List<Global> menuShow = [];
    readonly Dictionary<string, Message> menuMessages = [];

    /// <summary>A script's menu as a message box: a button per item (its RTX text), shown when menuAddItem added it.</summary>
    Message MenuMessage(List<int> items)
    {
        string key = string.Join(",", items);
        if (menuMessages.TryGetValue(key, out var hit)) return hit;
        var msg = new Message(mod) { EditorID = $"RGMenu{menuMessages.Count}", Description = " ", Flags = Message.Flag.MessageBox, MenuButtons = [] };
        for (int i = 0; i < items.Count; i++)
        {
            var button = new MessageButton { Text = Text(items[i]) ?? TagString(items[i]) };
            var d = new GetGlobalValueConditionData();
            d.Global = new FormLinkOrIndex<IGlobalGetter>(d, menuShow[i].FormKey);
            button.Conditions.Add(new ConditionFloat { Data = d, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 });
            msg.MenuButtons.Add(button);
        }
        mod.Messages.Add(msg);
        return menuMessages[key] = msg;
    }

    readonly Dictionary<string, Message> useMenus = [];

    /// <summary>"Use which item?": a button per item (shown when the player has it) and Nothing.</summary>
    Message UseMenu(List<int> items)
    {
        string key = string.Join(",", items);
        if (useMenus.TryGetValue(key, out var hit)) return hit;
        var msg = new Message(mod) { EditorID = $"RGUseMenu{useMenus.Count}", Description = "Use which item?", Flags = Message.Flag.MessageBox, MenuButtons = [] };
        foreach (var n in items.Take(9))
        {
            var button = new MessageButton { Text = ItemDefs.FirstOrDefault(d => d.Id == n) is { } d ? ItemName(d) : $"Item {n}" };
            if (ItemRecord(n) is { } item && n != 45)
            {
                var have = new GetItemCountConditionData();
                have.ItemOrList = new FormLinkOrIndex<IItemOrListGetter>(have, item);
                have.RunOnType = Condition.RunOnType.Reference;
                have.Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerRef);
                button.Conditions.Add(new ConditionFloat { Data = have, CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0 });
            }
            msg.MenuButtons.Add(button);
        }
        msg.MenuButtons.Add(new MessageButton { Text = "Nothing" });
        mod.Messages.Add(msg);
        return useMenus[key] = msg;
    }

    /// <summary>MAIN.SFX's effects the scripts play, as sound markers in RGSounds by index (None where unused).</summary>
    void BuildSounds()
    {
        var used = new HashSet<int>();
        int sound = Fn("Sound"), flat = Fn("FlatSound"), ambient = Fn("AmbientSound");
        foreach (var scene in scenes.Values)
            foreach (var c in scene.Script.Calls)
                if ((c.Function == sound || c.Function == flat || c.Function == ambient) && c.Args.Length > 0 && c.Args[0] is int n && n >= 0) used.Add(n);
        var ambientOnly = scenes.Values.SelectMany(s => s.Script.Calls).Where(c => c.Function == ambient && c.Args.Length > 0 && c.Args[0] is int).Select(c => c.Args[0]!.Value).ToHashSet();
        var sfx = RgSfx.Load(Path.Combine(rgDir, "sound", "MAIN.SFX"));
        if (sfx is null) { Problems.Add("sound\\MAIN.SFX not found: no sound effects"); return; }
        for (int i = 0; i < sfx.Effects.Count; i++)
        {
            SoundMarker? marker = null;
            if (used.Contains(i))
            {
                string rel = Path.Combine("fx", "tamrielmerge", "rg", "sfx", $"sfx{i:D3}.wav");
                string wav = Path.Combine(modOutDir, "sound", rel);
                try
                {
                    if (!File.Exists(wav))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
                        string raw = Path.Combine(Path.GetTempPath(), $"tm_rg_sfx_{Guid.NewGuid():N}.wav");
                        try { if (sfx.WriteWav(i, raw)) Resample(raw, wav); }
                        finally { File.Delete(raw); }
                    }
                    if (File.Exists(wav)) marker = SoundFor($"RGSfx{i:D3}", rel, "AudioCategorySFX", loop: ambientOnly.Contains(i) && sfx.Effects[i].Type == 3);
                }
                catch (Exception e) { Problems.Add($"sound effect {i}: {e.Message}"); }
            }
            sfxMarkers[i] = marker;
            soundsList!.Items.Add(marker is null ? new FormLink<ISkyrimMajorRecordGetter>(Sk(0x3B)) : marker.ToLink<ISkyrimMajorRecordGetter>());
        }
    }

    /// <summary>
    /// Compiles the Redguard scripts that changed (sources newer than their .pex), importing the tool's Papyrus folder
    /// (TMRgSoup and the scripts it uses), SKSE's sources and Skyrim's; the library itself goes into the output too.
    /// Returns the scripts that compiled.
    /// </summary>
    HashSet<string> CompileRedguardScripts(string sourceDir, string outDir, HashSet<string> names, Action<string>? log)
    {
        string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
        string compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        string vanillaSrc = Path.Combine(skyrimData, "Source", "Scripts"), skseSrc = Path.Combine(skyrimData, "Scripts", "Source");
        string shipped = Path.Combine(AppContext.BaseDirectory, "Papyrus");
        CompilerFound = File.Exists(compiler) && File.Exists(Path.Combine(vanillaSrc, "ObjectReference.psc"));
        Directory.CreateDirectory(outDir);
        if (!CompilerFound)
        {
            // The library's precompiled copies still ship, for scripts compiled elsewhere.
            foreach (var lib in Library)
                if (File.Exists(Path.Combine(shipped, "Compiled", lib + ".pex"))) File.Copy(Path.Combine(shipped, "Compiled", lib + ".pex"), Path.Combine(outDir, lib + ".pex"), true);
            return names.Where(n => File.Exists(Path.Combine(outDir, n + ".pex"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        string imports = $"{Path.GetFullPath(sourceDir)};{shipped};{skseSrc};{vanillaSrc}";
        string Run(string target, bool all, string cwd)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(compiler) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(target);
            if (all) psi.ArgumentList.Add("-all");
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-f=TESV_Papyrus_Flags.flg");
            psi.ArgumentList.Add($"-i={imports}");
            psi.ArgumentList.Add($"-o={Path.GetFullPath(outDir)}");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + err.Result;
            p.WaitForExit();
            return output;
        }
        // The library.
        foreach (var lib in Library)
        {
            string src = Path.Combine(shipped, lib + ".psc"), pex = Path.Combine(outDir, lib + ".pex");
            if (!File.Exists(pex) || File.GetLastWriteTimeUtc(pex) < File.GetLastWriteTimeUtc(src)) Run(lib + ".psc", false, shipped);
        }
        // A changed library (its functions' signatures) makes every script stale.
        string stamp = Path.Combine(outDir, "TMRgLibrary.hash");
        string libHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Concat(new[] { "TMRgSoup", "TMRgState", "TMRgMove" }.Select(l => File.ReadAllText(Path.Combine(shipped, l + ".psc")))))));
        bool libraryChanged = !File.Exists(stamp) || File.ReadAllText(stamp) != libHash;
        bool Fresh(string n) => !libraryChanged && File.Exists(Path.Combine(outDir, n + ".pex")) && File.GetLastWriteTimeUtc(Path.Combine(outDir, n + ".pex")) >= File.GetLastWriteTimeUtc(Path.Combine(sourceDir, n + ".psc"));
        var stale = names.Where(n => !Fresh(n)).ToList();
        if (stale.Count > 0)
        {
            // The changed ones in batches, each in a folder of its own, up to four compiled at once, as Morrowind's and
            // Oblivion's are (2026-10-15: all 904 in one run were one compiler on one core, 261 s); the source folder
            // stays an import, so the scripts still see each other. What a batch drops (the compiler can when memory
            // runs short) once more one at a time.
            foreach (var n in stale) File.Delete(Path.Combine(outDir, n + ".pex"));
            int parallel = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            // Batches of at most 400, and as many as can run at once when there are enough scripts (100 or more each).
            int batches = Math.Max((stale.Count + 399) / 400, Math.Min(parallel, (stale.Count + 99) / 100));
            int size = (stale.Count + batches - 1) / batches;
            var chunks = stale.Chunk(size).Select((names, i) => (Index: i, Names: names)).ToList();
            var outputs = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = parallel }, c =>
            {
                string batch = Path.Combine(Path.GetTempPath(), $"TamrielMergeRgScripts{Environment.ProcessId}_{c.Index}");
                if (Directory.Exists(batch)) Directory.Delete(batch, true);
                Directory.CreateDirectory(batch);
                foreach (var n in c.Names) File.Copy(Path.Combine(sourceDir, n + ".psc"), Path.Combine(batch, n + ".psc"));
                outputs[c.Index] = Run(batch, true, batch);
                try { Directory.Delete(batch, true); } catch (IOException) { }
            });
            string output = string.Concat(outputs.OrderBy(kv => kv.Key).Select(kv => kv.Value));
            libraryChanged = false;
            File.WriteAllText(stamp, libHash);
            foreach (var n in stale.Where(n => !Fresh(n))) output += Run(n + ".psc", false, sourceDir);
            log?.Invoke($"scripts: compiled {stale.Count} changed scripts in {sw.Elapsed.TotalSeconds:F0}s ({chunks.Count} batches, up to {parallel} at once)");
            foreach (var line in output.Split('\n').Where(l => l.Contains("): ")).Take(200)) CompileFailures.Add(line.Trim());
        }
        var ok = names.Where(Fresh).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.Where(n => !ok.Contains(n)).Take(20)) Problems.Add($"script {n} didn't compile");
        return ok;
    }

    // ---- for rg-script-coverage (translating without building) ----

    /// <summary>A script talks (holds conversations: a menu and Cyrus lined up) and is a person or kind the build
    /// makes an actor of.</summary>
    public static bool IsTalker(RgScript script, string actor, List<string> functionNames)
    {
        int menuAdd = functionNames.IndexOf("menuAddItem"), lineUp = functionNames.IndexOf("PlayerLineUp");
        return script.Calls.Any(c => c.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase) && c.Function == menuAdd)
            && script.Calls.Any(c => c.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase) && c.Function == lineUp)
            && (People.ContainsKey(actor) || Kinds.Any(k => actor.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Lines, log stages, doors and places as placeholders: what a statement becomes doesn't depend on them
    /// (items and pictures do: from the install, as the build reads them).</summary>
    public sealed class CoverageContext(string rgDir) : RgPapyrus.Context
    {
        readonly List<ItemDef> defs = LoadItemDefs(rgDir);
        public string Actor { get; set; } = "";
        public (int Index, string Text, float Seconds) Line(int tag, bool cyrus) => (0, "", 1f);
        public int LogStage(int tag) => 10;
        public bool LoadWorldIsDoor(int? map, int? marker, bool setByScript) => !(setByScript && People.ContainsKey(Actor)) && marker is not null;
        public Vector3 Place(Vector3 redguard) => RgSpace.Point(redguard);
        public RgPapyrus.Category HandItem(int item) => HandCategory(defs, item);
        public int Picture(string file) => File.Exists(Path.Combine(rgDir, file.Replace('/', '\\'))) ? 0 : -1;
        public bool ActsOnPlayer => PlayerScripts.Contains(Actor) || defs.Any(d => d.UseScript.Equals(Actor, StringComparison.OrdinalIgnoreCase));
    }
}
