using System.Text.RegularExpressions;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Quest objective lines short enough for Skyrim's HUD, which shows an objective on one line and cuts the rest off
/// (2026-11-04, user: "when i quest pops up the prompt gets cutoff where it shows the quest": Daggerfall's quest log
/// messages were whole objectives, 308 of 310 over 80 characters, median 218; Morrowind's journal entries shown again
/// 2,525 of 3,383). The whole text stays in the journal (a stage's log entry, which the journal wraps); the objective
/// is its first sentence, cut at a word if it is still too long.
/// </summary>
public static partial class QuestText
{
    /// <summary>The visible characters an objective line holds (Skyrim's HUD at 1080p shows about 75 of its font).</summary>
    public const int HeadlineLength = 70;

    /// <summary>What a text-replacement tag is counted as (a name or a place: <c>&lt;Alias=T1&gt;</c> shows a name).</summary>
    const int TagLength = 14;

    [GeneratedRegex(@"<Alias=[^>]*>|<Global=[^>]*>")]
    private static partial Regex Tag();

    /// <summary>A leading date tag ("&lt;Alias=T6&gt;: "): Daggerfall's log entries begin with the day they were written.</summary>
    [GeneratedRegex(@"^\s*<Alias=[^>]*>\s*:\s*")]
    private static partial Regex LeadingDate();

    /// <summary>How long a text shows, its tags counted as names.</summary>
    public static int Visible(string text) => Tag().Replace(text, new string('x', TagLength)).Length;

    /// <summary>The objective line for a quest text: the whole text when it fits, else its first sentence (without a
    /// leading date), cut at a word with "..." when that is still too long. Tags are never cut through.</summary>
    public static string Headline(string text, int max = HeadlineLength)
    {
        string t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (Visible(t) <= max) return t;
        t = LeadingDate().Replace(t, "");
        // The first sentence: up to the first ". ", "! " or "? " outside a tag.
        int end = -1;
        for (int i = 0, depth = 0; i < t.Length - 1; i++)
        {
            if (t[i] == '<') depth++;
            else if (t[i] == '>') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && t[i] is '.' or '!' or '?' && t[i + 1] == ' ') { end = i + 1; break; }
        }
        if (end > 0) t = t[..end];
        if (Visible(t) <= max) return t;
        // Cut at the last word boundary that fits, keeping whole tags.
        var words = t.Split(' ');
        string kept = "";
        foreach (var w in words)
        {
            string next = kept.Length == 0 ? w : kept + " " + w;
            if (Visible(next) + 3 > max) break;
            kept = next;
        }
        if (kept.Length == 0) kept = words[0];
        return kept.TrimEnd(',', ';', ':', ' ') + "...";
    }
}
