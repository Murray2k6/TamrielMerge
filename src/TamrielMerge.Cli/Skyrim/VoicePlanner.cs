using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Which voice files a converted plugin's dialogue needs. Skyrim plays a response line from
/// sound\voice\&lt;plugin&gt;\&lt;voice type&gt;\&lt;quest&gt;_&lt;topic&gt;_&lt;info id&gt;_&lt;n&gt;.fuz (the quest's and topic's EditorIDs,
/// <see cref="Prefix"/>; the response's FormID with the plugin's index as 00; the line number),
/// for the voice type of whoever says it. A response can be said by every converted person its speaker, race, sex,
/// faction and class conditions allow; each person's voice type is that of the vanilla NPC whose look they wear
/// (template traits). Creatures don't speak converted lines.
/// </summary>
public static class VoicePlanner
{
    /// <summary>A voice file to make; <see cref="Speakers"/> is how many converted people can say the line (lines for
    /// one named speaker are worth more than generic ones, so generation goes in that order).</summary>
    /// <param name="SpeakAs">The voice the line is spoken in when not <see cref="VoiceType"/>'s own (its folder stays the
    /// speaker's voice type, which the game plays from): "voice type[+effect]".</param>
    /// <param name="Was">The file's path under the naming used until 2026-11-09 (the quest's name always cut to 10
    /// characters), when that differs: a voice file made then is this line's (<see cref="Renamed"/>).</param>
    public sealed record Job(string VoiceType, string Text, string File, int Speakers, string? SpeakAs = null, string? Was = null)
    {
        /// <summary>The voice (and effect) the line is spoken in: its own voice type's, or the stand-in; for a line with
        /// a recording of its own ("recording:" and its file under the game's Sound folder), that.</summary>
        public string Speech => SpeakAs ?? VoiceType;

        /// <summary>The recording the line is spoken with (Morrowind's barks), or null for a generated line.</summary>
        public string? Recording => SpeakAs is { } s && s.StartsWith(RecordingPrefix, StringComparison.Ordinal) ? s[RecordingPrefix.Length..] : null;
    }

    public const string RecordingPrefix = "recording:";

    /// <summary>
    /// The quest and topic part of a voice file's name: both EditorIDs whole when they fit in 25 characters together,
    /// else the quest's cut to 10 and the topic's to what is left. Measured 2026-11-09 on the 44,271 names of Skyrim -
    /// Voices_en0.bsa: "voicepowers_shout03" (210 files) keeps its 11-character quest, "dialoguewh_dialoguewhiteru" is
    /// cut to 10 and 15, "mg02_mg02tolfdirstage60psi" to 4 and 21, "dialogueriftenkeepscene11_" (no topic name) keeps
    /// 25. In game: Jiub's line under "mwscriptsa_mwsaydb25aadc" (the quest MWScriptSay cut to 10) was begun and never
    /// heard, the three under "mwscriptsay_..." were held for their recordings' lengths, subtitled and heard.
    /// </summary>
    public static string Prefix(string quest, string topic)
    {
        if (quest.Length + topic.Length <= 25) return $"{quest}_{topic}";
        string q = Cut(quest, 10);
        return $"{q}_{Cut(topic, 25 - q.Length)}";
    }

    /// <summary>Voice files made under the old naming (<see cref="Job.Was"/>) moved to their names under
    /// <paramref name="dir"/> (the folder the jobs' paths are under), so lines spoken already aren't spoken again.
    /// Returns how many were moved.</summary>
    public static int Renamed(IEnumerable<Job> jobs, string dir)
    {
        int moved = 0;
        foreach (var j in jobs)
        {
            if (j.Was is null) continue;
            string old = Path.Combine(dir, j.Was), now = Path.Combine(dir, j.File);
            if (!System.IO.File.Exists(old)) continue;
            if (System.IO.File.Exists(now)) { System.IO.File.Delete(old); continue; }
            System.IO.File.Move(old, now);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Who speaks in a stand-in voice (user, 2026-10-10: "they need a voice"; "dagoth is a ashen and vivec and almexia are
    /// gods from morrowind"; "dwarven spheres and atronachs are creatures"): people whose voice type is a creature's or
    /// absent get a human voice cloned into their own voice type's folder. Morrowind's living gods speak as Dark Elves with
    /// a divine hall about them (+god), the Sixth House's ashen (Dagoth Ur, his kin, the ash vampires and sleepers) as
    /// Dark Elves pitched down and roughened (+ash), the talking dead as old voices, Yagrum Bagarn (a Dwemer on a sphere's
    /// legs) as an old scholar. Creatures (dwarven automatons, atronachs, beasts) stay silent: null.
    /// </summary>
    static string? StandIn(string name, string race, string voice, bool female, bool speaks)
    {
        string elf = female ? "FemaleDarkElf" : "MaleDarkElf";
        if (name.Contains("Vivec", StringComparison.OrdinalIgnoreCase) || name.Contains("Almalexia", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Sotha Sil", StringComparison.OrdinalIgnoreCase))
            return elf + "+god";
        if (name.StartsWith("Dagoth", StringComparison.OrdinalIgnoreCase) || race.Contains("AshSpawn", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ash Vampire", StringComparison.OrdinalIgnoreCase) || name.Contains("Sleeper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ash Ghoul", StringComparison.OrdinalIgnoreCase))
            return elf + "+ash";
        // Yagrum Bagarn, the last living Dwemer (user, 2026-10-10: "yagrum needs a voice hes dwemer"): a person on a
        // dwarven sphere's legs, so his look's voice type is the sphere's; he speaks as an old scholar.
        if (name.Contains("Yagrum", StringComparison.OrdinalIgnoreCase)) return "MaleOldKindly";
        if (speaks) return "";
        if (new[] { "Skeleton", "Draugr", "Ghost", "DragonPriest", "Lich" }.Any(k => race.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return female ? "FemaleOldGrumpy" : "MaleOldGrumpy";
        return null;
    }

    public static List<Job> Plan(ISkyrimModGetter plugin, string skyrimData, out Dictionary<string, int> perVoiceType)
    {
        // Skyrim and its DLC: converted people wear their looks too (Dragonborn's ash spawn for the Sixth House).
        var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "Dragonborn.esm" }
            .Select(m => Path.Combine(skyrimData, m)).Where(File.Exists)
            .Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        using var skyrim = masters[0];
        var vanillaNpcs = new Dictionary<FormKey, INpcGetter>();
        var voiceNames = new Dictionary<FormKey, string>();
        var raceNames = new Dictionary<FormKey, string>();
        var playableRaces = new HashSet<FormKey>();
        var leveled = new Dictionary<FormKey, ILeveledNpcGetter>();
        foreach (var m in masters)
        {
            foreach (var n in m.Npcs) vanillaNpcs[n.FormKey] = n;
            foreach (var v in m.VoiceTypes) voiceNames[v.FormKey] = v.EditorID ?? v.FormKey.ToString();
            foreach (var r in m.Races) { raceNames[r.FormKey] = r.EditorID ?? ""; if (r.Flags.HasFlag(Race.Flag.Playable)) playableRaces.Add(r.FormKey); }
            foreach (var l in m.LeveledNpcs) leveled[l.FormKey] = l;
        }
        foreach (var m in plugin.Races) raceNames[m.FormKey] = m.EditorID ?? "";

        // Converted people: template (look) NPC from Skyrim.esm with traits.
        var people = new List<(FormKey Key, FormKey Race, bool Female, HashSet<FormKey> Factions, FormKey Class, string Voice, string Clone)>();
        // Every look an NPC can have: the template's (traits from a template), through levelled lists too (user,
        // 2026-10-09: Redguard's guards and pirates, generic people made from Skyrim's encounter records, had no voice
        // files, so their lines flashed by without subtitles); without a template, its own voice.
        IEnumerable<INpcGetter> Looks(FormKey key, int depth)
        {
            if (depth > 6) yield break;
            if (vanillaNpcs.TryGetValue(key, out var n))
            {
                if (!n.Template.IsNull && n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
                    foreach (var l in Looks(n.Template.FormKey, depth + 1)) yield return l;
                else yield return n;
            }
            else if (leveled.TryGetValue(key, out var list))
                foreach (var e in list.Entries ?? [])
                    if (e.Data is { } d)
                        foreach (var l in Looks(d.Reference.FormKey, depth + 1)) yield return l;
        }
        // Speakers: playable races, and humanoids with a spoken voice (Skyrim's Dremora speak); others by StandIn.
        bool Speaks(FormKey race, string voice) => playableRaces.Contains(race) || voice.StartsWith("Male") || voice.StartsWith("Female")
            || voice.Equals("CrDremoraVoice", StringComparison.OrdinalIgnoreCase);
        void Add(INpcGetter npc, HashSet<FormKey> factions, FormKey race, bool female, string voice)
        {
            string name = npc.Name?.String ?? npc.EditorID ?? "";
            if (StandIn(name, raceNames.GetValueOrDefault(race) ?? "", voice, female, Speaks(race, voice)) is { } clone)
                people.Add((npc.FormKey, race, female, factions, npc.Class.FormKey, voice, clone));
        }
        foreach (var npc in plugin.Npcs)
        {
            var factions = npc.Factions.Select(f => f.Faction.FormKey).ToHashSet();
            if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
            {
                foreach (var look in Looks(npc.Template.FormKey, 0).DistinctBy(l => (l.Voice.FormKey, l.Race.FormKey)))
                    if (!look.Voice.IsNull && voiceNames.TryGetValue(look.Voice.FormKey, out var voice))
                        Add(npc, factions, look.Race.FormKey, look.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female), voice);
            }
            else if (!npc.Voice.IsNull && voiceNames.TryGetValue(npc.Voice.FormKey, out var own))
                Add(npc, factions, npc.Race.FormKey, npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female), own);
        }
        foreach (var m in masters.Skip(1)) m.Dispose();

        var quests = plugin.Quests.ToDictionary(q => q.FormKey);
        var jobs = new List<Job>();
        perVoiceType = new Dictionary<string, int>();
        // Skyrim reads a condition flagged OR together with the one after it (A OR B, then AND the rest). The plan has
        // long skipped the flagged one and taken the next as required: a line Oblivion gives to "Baurus OR Glenroy" was
        // voiced for Glenroy alone, and on 2026-11-15 every Oblivion greeting ("in a conversation" OR "holding the
        // greeting mark", a faction only the game puts people in) lost its voice files, 54,000 of them, the Emperor's
        // among them. Oblivion's plugin is planned by whole OR groups; the other games' plans are left as they were
        // (their voice runs are under way: user, 2026-11-15, "be careful it does not change the other games").
        bool orGroups = plugin.ModKey.FileName.String.Equals("Oblivion.esm", StringComparison.OrdinalIgnoreCase);
        foreach (var topic in plugin.DialogTopics)
        {
            string questName = quests.GetValueOrDefault(topic.Quest.FormKey)?.EditorID ?? "";
            string prefix = Prefix(questName, topic.EditorID ?? "");
            string questPart = Cut(questName, 10), was = $"{questPart}_{Cut(topic.EditorID ?? "", 25 - questPart.Length)}";
            foreach (var info in topic.Responses)
            {
                IEnumerable<(FormKey Key, FormKey Race, bool Female, HashSet<FormKey> Factions, FormKey Class, string Voice, string Clone)> who = people;
                var conditions = info.Conditions.OfType<IConditionFloatGetter>().ToList();
                if (!orGroups)
                    foreach (var c in conditions)
                    {
                        if (c.Flags.HasFlag(Condition.Flag.OR)) continue;
                        if (Speaker(c) is { } test) who = who.Where(test);
                    }
                else
                    for (int i = 0; i < conditions.Count;)
                    {
                        // One group: this condition and the ones it is ORed with.
                        int end = i;
                        while (end < conditions.Count - 1 && conditions[end].Flags.HasFlag(Condition.Flag.OR)) end++;
                        var tests = conditions.GetRange(i, end - i + 1).Select(Speaker).ToList();
                        i = end + 1;
                        // A group the plan can't read whole (one of its conditions isn't about who speaks: "in a
                        // conversation", a quest's stage) may hold for anybody: it narrows nothing.
                        if (tests.Any(t => t is null)) continue;
                        who = tests.Count == 1 ? who.Where(tests[0]!) : who.Where(p => tests.Any(t => t!(p)));
                    }
                var speakers = who.ToList();
                var voices = speakers.Select(p => (p.Voice, p.Clone)).Distinct().ToList();
                foreach (var line in info.Responses)
                {
                    string text = (line.Text?.String ?? "").Trim();
                    // A line with a recording of its own (Morrowind's barks: script notes "mwvoice:<file>") is packed from it.
                    string? recording = line.ScriptNotes is { } notes && notes.StartsWith(Morrowind.MwPluginBuilder.RecordingNote, StringComparison.Ordinal)
                        ? notes[Morrowind.MwPluginBuilder.RecordingNote.Length..] : null;
                    if (text.Length == 0 && recording is null) continue;
                    string tail = $"_{info.FormKey.ID:x8}_{line.ResponseNumber}.fuz";
                    string name = (prefix + tail).ToLowerInvariant();
                    string? oldName = was == prefix ? null : (was + tail).ToLowerInvariant();
                    foreach (var (v, clone) in voices)
                    {
                        string folder = Path.Combine("sound", "voice", plugin.ModKey.FileName.String.ToLowerInvariant(), v.ToLowerInvariant());
                        jobs.Add(new Job(v, text, Path.Combine(folder, name),
                            speakers.Count, recording is not null ? RecordingPrefix + recording : clone.Length > 0 ? clone : null,
                            oldName is null ? null : Path.Combine(folder, oldName)));
                        perVoiceType[v] = perVoiceType.GetValueOrDefault(v) + 1;
                    }
                }
            }
        }
        return jobs;
    }

    /// <summary>A condition about who speaks (their ID, race, faction, class or sex, tested on the speaker for 1 or 0)
    /// as a test of a person; null for any other.</summary>
    static Func<(FormKey Key, FormKey Race, bool Female, HashSet<FormKey> Factions, FormKey Class, string Voice, string Clone), bool>? Speaker(IConditionFloatGetter c)
    {
        if (c.Data.RunOnType != Condition.RunOnType.Subject) return null;
        bool yes = c.CompareOperator == CompareOperator.EqualTo && c.ComparisonValue == 1;
        bool no = c.CompareOperator == CompareOperator.EqualTo && c.ComparisonValue == 0;
        if (!yes && !no) return null;
        switch (c.Data)
        {
            case IGetIsIDConditionDataGetter d: { var k = d.Object.Link.FormKey; return p => (p.Key == k) == yes; }
            case IGetIsRaceConditionDataGetter d: { var k = d.Race.Link.FormKey; return p => (p.Race == k) == yes; }
            case IGetInFactionConditionDataGetter d: { var k = d.Faction.Link.FormKey; return p => p.Factions.Contains(k) == yes; }
            case IGetIsClassConditionDataGetter d: { var k = d.Class.Link.FormKey; return p => (p.Class == k) == yes; }
            case IGetIsSexConditionDataGetter d: { bool female = d.MaleFemaleGender == MaleFemaleGender.Female; return p => (p.Female == female) == yes; }
            default: return null;
        }
    }

    static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    public static void WriteJobs(List<Job> jobs, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = false }));
}
