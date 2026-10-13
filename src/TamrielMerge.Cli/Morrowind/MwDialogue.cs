namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's dialogue: topics (DIAL; DATA byte: 0 topic, 1 voice, 2 greeting, 3 persuasion, 4 journal), each
/// followed in its plugin by its responses (INFO). A response's place in its topic is a linked list (PNAM previous,
/// NNAM next) that later plugins edit: an INFO with a known INAM replaces that response, a new one goes after its
/// PNAM, and a deleted one is removed. Responses are checked in order; the first whose conditions all hold is said.
/// </summary>
public sealed class MwDialogue
{
    public enum TopicType : byte { Topic = 0, Voice = 1, Greeting = 2, Persuasion = 3, Journal = 4 }

    public sealed record Topic(string Name, TopicType Type, List<Info> Infos);

    /// <summary>One response with its filters (empty = any) and its function/variable conditions (SCVR).</summary>
    public sealed record Info(string Id, string Text, string? Actor, string? Race, string? Class, string? Faction, int Rank,
        string? Cell, string? PcFaction, int PcRank, int Sex, int Disposition, int JournalIndex,
        List<Condition> Conditions, string? Result, string? Sound, bool QuestName, bool QuestFinished, bool QuestRestart);

    /// <summary>
    /// A SCVR condition: its kind (1 function, 2 global, 3 local, 4 journal, 5 item, 6 dead, 7 not ID, 8 not
    /// faction, 9 not class, A not race, B not cell, C not local), the function code (two digits, for kind 1), the
    /// comparison (0 =, 1 !=, 2 &gt;, 3 &gt;=, 4 &lt;, 5 &lt;=), the name it tests and the value.
    /// </summary>
    public sealed record Condition(char Kind, string Function, int Compare, string Name, float Value);

    public Dictionary<string, Topic> Topics { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static MwDialogue Load(IEnumerable<Tes3Plugin> plugins)
    {
        var d = new MwDialogue();
        foreach (var plugin in plugins)
        {
            Topic? current = null;
            foreach (var rec in plugin.Records)
            {
                if (rec.Type == "DIAL")
                {
                    string name = rec.Id;
                    var type = (TopicType)(rec.Get("DATA")?.Data[0] ?? 0);
                    if (!d.Topics.TryGetValue(name, out current)) d.Topics[name] = current = new Topic(name, type, []);
                    if (rec.Deleted) { d.Topics.Remove(name); current = null; }
                    continue;
                }
                if (rec.Type != "INFO" || current is null) continue;
                string id = rec.Str("INAM") ?? "";
                int existing = current.Infos.FindIndex(i => i.Id == id);
                if (rec.Deleted) { if (existing >= 0) current.Infos.RemoveAt(existing); continue; }
                var info = Read(rec, id);
                if (existing >= 0) { current.Infos[existing] = info; continue; }
                string prev = rec.Str("PNAM") ?? "";
                int after = prev.Length > 0 ? current.Infos.FindIndex(i => i.Id == prev) : -1;
                current.Infos.Insert(after >= 0 ? after + 1 : prev.Length == 0 ? 0 : current.Infos.Count, info);
            }
        }
        return d;
    }

    static Info Read(Tes3Record rec, string id)
    {
        // DATA: dialogue type (int), disposition or journal index (int), speaker rank (sbyte), sex (sbyte), PC rank (sbyte), unused.
        var data = rec.Get("DATA")?.Data ?? new byte[12];
        int disposition = data.Length >= 8 ? BitConverter.ToInt32(data, 4) : 0;
        int rank = data.Length >= 9 ? (sbyte)data[8] : -1, sex = data.Length >= 10 ? (sbyte)data[9] : -1, pcRank = data.Length >= 11 ? (sbyte)data[10] : -1;
        var conditions = new List<Condition>();
        Tes3Field? pending = null;
        foreach (var f in rec.Fields)
        {
            if (f.Type == "SCVR") { if (pending is { } p) conditions.Add(Cond(p, 0)); pending = f; continue; }
            if (pending is { } q && f.Type is "INTV" or "FLTV")
            {
                conditions.Add(Cond(q, f.Type == "INTV" ? f.Int() : f.Float()));
                pending = null;
            }
        }
        if (pending is { } last) conditions.Add(Cond(last, 0));
        string? S(string t) => rec.Str(t) is { Length: > 0 } v ? v : null;
        return new Info(id, rec.Str("NAME") ?? "", S("ONAM"), S("RNAM"), S("CNAM"), S("FNAM"), rank, S("ANAM"), S("DNAM"), pcRank, sex,
            disposition, disposition, conditions, S("BNAM"), S("SNAM"), rec.Has("QSTN"), rec.Has("QSTF"), rec.Has("QSTR"));
    }

    static Condition Cond(Tes3Field scvr, float value)
    {
        // SCVR: index char, type char, two-char function code, comparison char ('0'..'5'), then the name.
        string s = Tes3Plugin.Encoding.GetString(scvr.Data).TrimEnd('\0');
        if (s.Length < 5) return new Condition('?', "", 0, s, value);
        return new Condition(s[1], s.Substring(2, 2), s[4] - '0', s[5..], value);
    }
}
