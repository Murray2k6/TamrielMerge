using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Which voice files a converted plugin's dialogue needs. Skyrim plays a response line from
/// sound\voice\&lt;plugin&gt;\&lt;voice type&gt;\&lt;quest&gt;_&lt;topic&gt;_&lt;info id&gt;_&lt;n&gt;.fuz (the quest's EditorID cut to 10
/// characters, the topic's to 25 minus that, the response's FormID with the plugin's index as 00, the line number),
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
    public sealed record Job(string VoiceType, string Text, string File, int Speakers, string? SpeakAs = null)
    {
        /// <summary>The voice (and effect) the line is spoken in: its own voice type's, or the stand-in; for a line with
        /// a recording of its own ("recording:" and its file under the game's Sound folder), that.</summary>
        public string Speech => SpeakAs ?? VoiceType;

        /// <summary>The recording the line is spoken with (Morrowind's barks), or null for a generated line.</summary>
        public string? Recording => SpeakAs is { } s && s.StartsWith(RecordingPrefix, StringComparison.Ordinal) ? s[RecordingPrefix.Length..] : null;
    }

    public const string RecordingPrefix = "recording:";

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
        foreach (var topic in plugin.DialogTopics)
        {
            string questPart = Cut(quests.GetValueOrDefault(topic.Quest.FormKey)?.EditorID ?? "", 10);
            string topicPart = Cut(topic.EditorID ?? "", 25 - questPart.Length);
            foreach (var info in topic.Responses)
            {
                IEnumerable<(FormKey Key, FormKey Race, bool Female, HashSet<FormKey> Factions, FormKey Class, string Voice, string Clone)> who = people;
                foreach (var c in info.Conditions.OfType<IConditionFloatGetter>())
                {
                    if (c.Flags.HasFlag(Condition.Flag.OR) || c.Data.RunOnType != Condition.RunOnType.Subject) continue;
                    bool yes = c.CompareOperator == CompareOperator.EqualTo && c.ComparisonValue == 1;
                    bool no = c.CompareOperator == CompareOperator.EqualTo && c.ComparisonValue == 0;
                    if (!yes && !no) continue;
                    switch (c.Data)
                    {
                        case IGetIsIDConditionDataGetter d: { var k = d.Object.Link.FormKey; who = who.Where(p => (p.Key == k) == yes); break; }
                        case IGetIsRaceConditionDataGetter d: { var k = d.Race.Link.FormKey; who = who.Where(p => (p.Race == k) == yes); break; }
                        case IGetInFactionConditionDataGetter d: { var k = d.Faction.Link.FormKey; who = who.Where(p => p.Factions.Contains(k) == yes); break; }
                        case IGetIsClassConditionDataGetter d: { var k = d.Class.Link.FormKey; who = who.Where(p => (p.Class == k) == yes); break; }
                        case IGetIsSexConditionDataGetter d: { bool female = d.MaleFemaleGender == MaleFemaleGender.Female; who = who.Where(p => (p.Female == female) == yes); break; }
                    }
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
                    string name = $"{questPart}_{topicPart}_{info.FormKey.ID:x8}_{line.ResponseNumber}.fuz".ToLowerInvariant();
                    foreach (var (v, clone) in voices)
                    {
                        jobs.Add(new Job(v, text, Path.Combine("sound", "voice", plugin.ModKey.FileName.String.ToLowerInvariant(), v.ToLowerInvariant(), name),
                            speakers.Count, recording is not null ? RecordingPrefix + recording : clone.Length > 0 ? clone : null));
                        perVoiceType[v] = perVoiceType.GetValueOrDefault(v) + 1;
                    }
                }
            }
        }
        return jobs;
    }

    static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    public static void WriteJobs(List<Job> jobs, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = false }));
}
