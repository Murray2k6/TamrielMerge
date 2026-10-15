namespace TamrielMerge.Morrowind;

/// <summary>
/// What Morrowind's scripts refer to, from the loaded game (<see cref="MwWorld"/>) and its dialogue: every script parsed,
/// the records carrying each (SCRI), which scripts run as global scripts (start scripts — Tribunal's and Bloodmoon's
/// SSCR records and Morrowind's Main — and whatever StartScript, StopScript or ScriptRunning names anywhere, dialogue
/// results included), the object IDs placed in cells, the journals, and the locals the dialogue's conditions test.
/// </summary>
public sealed class MwScriptWorld : MwPapyrus.IWorld
{
    readonly MwWorld world;
    readonly Tes3Data? data;

    /// <summary>Every script by ID, parsed.</summary>
    public Dictionary<string, MwScript.Script> Scripts { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The records carrying each script.</summary>
    public Dictionary<string, List<Tes3Record>> Carriers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StartScripts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GlobalScripts { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The plugin each script comes from (the last to define it).</summary>
    public Dictionary<string, string> ScriptPlugin { get; } = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> journals = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> dialogueLocals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<string, string>> variables = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> declaring = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> cellNames = [];
    /// <summary>Globals (GLOB) by ID: their own namespace (Tribunal has globals named like scripts).</summary>
    public Dictionary<string, Tes3Record> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> factionsByName = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> regionNames = new(StringComparer.OrdinalIgnoreCase);
    public MwDialogue? Dialogue { get; }
    /// <summary>Morrowind's attributes of the game's people and creatures (GetStrength...).</summary>
    public MwStats Stats { get; }

    public MwScriptWorld(MwWorld world, MwDialogue? dialogue, Tes3Data? data = null)
    {
        this.world = world;
        this.data = data;
        Dialogue = dialogue;
        // Scripts and globals by their own namespaces (the world's objects are one, where a later record of another type
        // with the same ID wins).
        var scriptRecords = new Dictionary<string, Tes3Record>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records)
            {
                if (r.Type == "SCPT" && r.Id.Length > 0)
                {
                    if (r.Deleted) { scriptRecords.Remove(r.Id); continue; }
                    scriptRecords[r.Id] = r;
                    ScriptPlugin[r.Id] = plugin.FileName;
                }
                if (r.Type == "GLOB" && r.Id.Length > 0) { if (r.Deleted) Globals.Remove(r.Id); else Globals[r.Id] = r; }
                if (r.Type == "SSCR" && r.Str("NAME") is { Length: > 0 } start) StartScripts.Add(start);
            }
        // Morrowind runs Main from the start of every game.
        if (scriptRecords.ContainsKey("Main")) StartScripts.Add("Main");
        StartScripts.RemoveWhere(s => !scriptRecords.ContainsKey(s));
        foreach (var rec in scriptRecords.Values)
        {
            var script = MwScript.Parse(rec.Str("SCTX") ?? "");
            Scripts[rec.Id] = script;
            variables[rec.Id] = script.Variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);
        }
        foreach (var rec in world.Objects.Values)
        {
            if (rec.Type == "FACT" && rec.Str("FNAM") is { Length: > 0 } fname) factionsByName.TryAdd(fname, rec.Id);
            if (rec.Type == "REGN" && rec.Str("FNAM") is { Length: > 0 } rname) regionNames.Add(rname);
            if (rec.Str("SCRI") is { Length: > 0 } s)
            {
                if (!Carriers.TryGetValue(s, out var list)) Carriers[s] = list = [];
                list.Add(rec);
            }
        }
        foreach (var cell in world.Interiors.Values) { cellNames.Add(cell.Name); foreach (var r in cell.Refs) placed.Add(r.BaseId); }
        foreach (var cell in world.Exteriors.Values) { if (cell.Name.Length > 0) cellNames.Add(cell.Name); foreach (var r in cell.Refs) placed.Add(r.BaseId); }
        GlobalScripts.UnionWith(StartScripts);
        foreach (var script in Scripts.Values) NoteGlobals(script);
        if (dialogue is not null)
        {
            foreach (var topic in dialogue.Topics.Values)
            {
                if (topic.Type == MwDialogue.TopicType.Journal) journals.Add(topic.Name);
                foreach (var info in topic.Infos)
                {
                    foreach (var c in info.Conditions)
                        if (c.Kind is '3' or 'C') dialogueLocals.Add(c.Name);
                    if (info.Result is { Length: > 0 } result && result.Contains("script", StringComparison.OrdinalIgnoreCase))
                        NoteGlobals(MwScript.ParseResult(result));
                }
            }
        }
        // Morrowind's dialogue offers Companion Share to whoever's companion local is 1 (TamrielMerge's topic tests it).
        if (variables.Values.Any(v => v.ContainsKey("companion"))) dialogueLocals.Add("companion");
        Stats = new MwStats(world);
        // Scripts on people declaring each local (a result script's speaker locals).
        foreach (var (id, vars) in variables)
            if (Carriers.TryGetValue(id, out var carriers) && carriers.Any(c => c.Type is "NPC_" or "CREA"))
                foreach (var v in vars.Keys)
                {
                    if (!declaring.TryGetValue(v, out var list)) declaring[v] = list = [];
                    list.Add(id);
                }
    }

    void NoteGlobals(MwScript.Script script)
    {
        foreach (var c in script.AllCalls())
            if (c.Function.Equals("StartScript", StringComparison.OrdinalIgnoreCase) || c.Function.Equals("StopScript", StringComparison.OrdinalIgnoreCase)
                || c.Function.Equals("ScriptRunning", StringComparison.OrdinalIgnoreCase))
                if (c.Args.FirstOrDefault() switch { MwScript.Name n => n.Id, MwScript.Str s => s.Value, _ => null } is { } target
                    && Scripts.ContainsKey(target))
                    GlobalScripts.Add(Scripts.Keys.First(k => k.Equals(target, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- IWorld ----

    public string? TypeOf(string id) => world.Objects.TryGetValue(id, out var r) ? r.Type : null;
    public bool IsPlaced(string id) => placed.Contains(id);
    public string? ScriptOf(string objectId) => world.Objects.TryGetValue(objectId, out var r) && r.Str("SCRI") is { Length: > 0 } s ? s : null;
    public IReadOnlyDictionary<string, string>? Variables(string scriptId) => variables.GetValueOrDefault(scriptId);
    public bool IsJournal(string id) => journals.Contains(id);
    public bool IsScript(string id) => Scripts.ContainsKey(id);
    public bool IsGlobalVariable(string id) => Globals.ContainsKey(id);
    public string? FactionId(string idOrName)
    {
        if (world.Objects.TryGetValue(idOrName, out var r) && r.Type == "FACT") return r.Id;
        if (factionsByName.TryGetValue(idOrName, out var byName)) return byName;
        // "House Telvanni" for Telvanni: a faction whose ID is the name's last word.
        string last = idOrName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return last.Length > 0 && world.Objects.TryGetValue(last, out var l) && l.Type == "FACT" ? l.Id : null;
    }
    public string? InteriorNamed(string name)
    {
        if (name.Length == 0) return null;
        if (world.Interiors.TryGetValue(name, out var exact)) return exact.Name;
        // Exterior names win over a prefix of an interior's ("Balmora" is the town, not "Balmora, Guild of Mages").
        if (IsExteriorName(name)) return null;
        return world.Interiors.Keys.Where(c => c.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
    public bool IsExteriorName(string name)
    {
        if (name.Length == 0) return false;
        if (regionNames.Contains(name) || world.Exteriors.Values.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return true;
        // "Solstheim, Brodir Grove": a region and a place in it.
        int comma = name.IndexOf(',');
        return comma > 0 && (regionNames.Contains(name[..comma].Trim()) || regionNames.Contains(name[(comma + 1)..].Trim())
            || world.Exteriors.Values.Any(c => c.Name.Equals(name[..comma].Trim(), StringComparison.OrdinalIgnoreCase)));
    }
    /// <summary>The cells a GetPCCell name matches: interiors whose name starts with it, and exterior cells of that name.</summary>
    public IEnumerable<string> InteriorsMatching(string name) => world.Interiors.Keys.Where(c => c.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    public int CoinsIn(string id) => MwPluginBuilder.CoinsIn(id);
    public string ObjectScriptName(string scriptId) => MwPapyrus.PapyrusName("MW_", scriptId);
    public string GlobalScriptName(string scriptId) => MwPapyrus.PapyrusName("MWG_", scriptId);
    public bool IsGlobalScript(string scriptId) => GlobalScripts.Contains(scriptId);
    public bool IsDialogueLocal(string variable) => dialogueLocals.Contains(variable);
    public IEnumerable<string> ScriptsDeclaring(string variable) => declaring.GetValueOrDefault(variable) ?? [];
    public IReadOnlySet<string> DialogueLocals => dialogueLocals;

    readonly Dictionary<string, float> soundSeconds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A sound's length from its WAV header (data size over bytes per second); 2 seconds when unknown.</summary>
    public float SoundSeconds(string soundId)
    {
        if (soundSeconds.TryGetValue(soundId, out var s)) return s;
        float seconds = 2;
        if (data is not null && world.Objects.TryGetValue(soundId, out var rec) && rec.Str("FNAM") is { Length: > 0 } file && data.Read(@"sound\" + file) is { Length: > 44 } bytes
            && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
        {
            int byteRate = 0, size = 0;
            for (int p = 12; p + 8 <= bytes.Length;)
            {
                string chunk = System.Text.Encoding.ASCII.GetString(bytes, p, 4);
                int len = BitConverter.ToInt32(bytes, p + 4);
                if (chunk == "fmt " && p + 16 <= bytes.Length) byteRate = BitConverter.ToInt32(bytes, p + 16);
                if (chunk == "data") { size = len; break; }
                p += 8 + len + (len & 1);
            }
            if (byteRate > 0 && size > 0) seconds = Math.Clamp((float)size / byteRate, 0.1f, 600f);
        }
        return soundSeconds[soundId] = seconds;
    }

    public int[]? BaseStats(string id) => Stats.Of(id);

    readonly Dictionary<string, float> voiceSeconds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A Say recording's length (Morrowind's voices are MP3s, whatever extension the script names); 3 seconds
    /// when the file isn't there.</summary>
    public float VoiceSeconds(string file)
    {
        if (voiceSeconds.TryGetValue(file, out var s)) return s;
        string path = @"sound\" + file.Replace('/', '\\').TrimStart('\\');
        byte[]? bytes = data?.Read(path) ?? data?.Read(Path.ChangeExtension(path, ".mp3")) ?? data?.Read(Path.ChangeExtension(path, ".wav"));
        float seconds = bytes is null ? 3f : AudioSeconds(bytes) ?? 3f;
        return voiceSeconds[file] = seconds;
    }

    /// <summary>A WAV's or MP3's length in seconds: the WAV's data over its byte rate; the MP3's frames' samples over
    /// their rate (each frame header read in turn, after an ID3 tag).</summary>
    public static float? AudioSeconds(byte[] b)
    {
        if (b.Length > 44 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F')
        {
            int byteRate = 0;
            for (int p = 12; p + 8 <= b.Length;)
            {
                string chunk = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                if (chunk == "fmt " && p + 16 <= b.Length) byteRate = BitConverter.ToInt32(b, p + 16);
                if (chunk == "data") return byteRate > 0 ? (float)len / byteRate : null;
                if (len < 0) break;
                p += 8 + len + (len & 1);
            }
            return null;
        }
        int pos = 0;
        if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            pos = 10 + (b[6] << 21 | b[7] << 14 | b[8] << 7 | b[9]);
        int[] v1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        int[] v2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
        int[][] rates = [[11025, 12000, 8000], [0, 0, 0], [22050, 24000, 16000], [44100, 48000, 32000]];
        double total = 0;
        int frames = 0;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int version = (b[pos + 1] >> 3) & 3, layer = (b[pos + 1] >> 1) & 3;
            int bitrateIndex = b[pos + 2] >> 4, rateIndex = (b[pos + 2] >> 2) & 3, padding = (b[pos + 2] >> 1) & 1;
            if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) { pos++; continue; }   // Layer III only
            int rate = rates[version][rateIndex];
            int bitrate = (version == 3 ? v1 : v2)[bitrateIndex] * 1000;
            int samples = version == 3 ? 1152 : 576;
            int length = samples / 8 * bitrate / rate + padding;
            if (length <= 4) { pos++; continue; }
            total += (double)samples / rate;
            frames++;
            pos += length;
        }
        return frames > 0 ? (float)total : null;
    }

    public string? MusicFile(string file)
    {
        if (file.Length == 0 || data is null) return null;
        string path = @"music\" + file.Replace('/', '\\').TrimStart('\\');
        return data.Exists(path) ? path : null;
    }

    public string? Setting(string name) => world.Settings.TryGetValue(name, out var r) ? r.Str("STRV") : null;

    /// <summary>Whether a creature carries the script.</summary>
    public bool OnCreature(string scriptId) => Carriers.TryGetValue(scriptId, out var c) && c.Any(r => r.Type == "CREA");

    /// <summary>Whether only people and creatures carry the script.</summary>
    public bool OnActor(string scriptId) => Carriers.TryGetValue(scriptId, out var c) && c.Count > 0 && c.All(r => r.Type is "NPC_" or "CREA");
    /// <summary>Whether an item carries the script (the player can carry it).</summary>
    public bool OnItem(string scriptId) => Carriers.TryGetValue(scriptId, out var c) && c.Any(r => MwPluginBuilder.IsItemType(r.Type) || r.Type == "LIGH");
    /// <summary>The NPCs carrying the script (for its Say lines).</summary>
    public List<string> Speakers(string scriptId) => Carriers.TryGetValue(scriptId, out var c) ? c.Where(r => r.Type == "NPC_").Select(r => r.Id).Take(8).ToList() : [];

    /// <summary>A result script's context: the speaker's script and faction when the response has one speaker.</summary>
    public MwPapyrus.Context ResultContext(MwDialogue.Info info)
    {
        string? script = info.Actor is { } a ? ScriptOf(a) : null;
        string? faction = info.Faction ?? (info.Actor is { } b && world.Objects.TryGetValue(b, out var npc) ? npc.Str("ANAM") is { Length: > 0 } f ? f : null : null);
        return new MwPapyrus.Context(OnActor: true, Speakers: info.Actor is { } s && TypeOf(s) == "NPC_" ? [s] : [], SpeakerScript: script, SpeakerFaction: faction,
            SpeakerId: info.Actor is { } id && TypeOf(id) is "NPC_" or "CREA" ? id : null);
    }
}
