using System.Text.RegularExpressions;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's movies (MENU.INI's movies page: movie_name[n], the Smacker files on the CD, and movie#_text[k] = flag,
/// red, green, blue, first frame, last frame, line). QueueMovie(n) in the scripts names them by n: 1 SCENE3 (the
/// journal), 2 SCENE7 (escaping N'Gasta's isle), 3 SCENE10 (A'tor's soul in the sword), 4 WIN (the ending, at
/// WonGame), 5-10 the dwarven ruins' machines (C_, A_ and B_RUINS). The intro (0) is the story's opening video. The
/// videos step converts them to Video\TamrielMerge_RedguardMovie{n}.bik with their lines drawn on the frames.
/// </summary>
public static class RgMovies
{
    public sealed record Line(int Start, int End, string Text);
    public sealed record Movie(int Number, string File, List<Line> Lines);

    public static List<Movie> Load(string rgDir)
    {
        string ini = Path.Combine(rgDir, "MENU.INI");
        if (!File.Exists(ini)) return [];
        var names = new SortedDictionary<int, string>();
        var lines = new Dictionary<int, List<Line>>();
        foreach (var raw in File.ReadLines(ini))
        {
            string line = raw.Trim();
            if (line.StartsWith(';')) continue;
            if (Regex.Match(line, @"^movie_name\[(\d+)\]\s*=\s*(\S+)", RegexOptions.IgnoreCase) is { Success: true } m)
                names.TryAdd(int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim());
            else if (Regex.Match(line, @"^movie(\d+)_text\[\d+\]\s*=\s*(.*)$", RegexOptions.IgnoreCase) is { Success: true } t)
            {
                var parts = t.Groups[2].Value.Split(',', 7);
                if (parts.Length == 7 && int.TryParse(parts[4], out int a) && int.TryParse(parts[5], out int b))
                {
                    int n = int.Parse(t.Groups[1].Value);
                    (lines.TryGetValue(n, out var l) ? l : lines[n] = []).Add(new Line(a, b, parts[6].Trim()));
                }
            }
        }
        return names.Select(kv => new Movie(kv.Key, kv.Value, lines.GetValueOrDefault(kv.Key) ?? [])).ToList();
    }

    /// <summary>A movie's lines as remaster.py's --subtitles JSON.</summary>
    public static string SubtitlesJson(Movie m) =>
        System.Text.Json.JsonSerializer.Serialize(m.Lines.Select(l => new { start = l.Start, end = l.End, text = l.Text }));
}
