using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's scripts in the converted plugin (see <see cref="MwPapyrus"/> for the translation): compiled with the
/// Creation Kit's Papyrus compiler against Skyrim's, SKSE's and TamrielMerge's own script sources (TMMwScript, TMMwAI…),
/// and attached to the records that carried them, with their properties filled with the converted forms.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>
    /// Compiles every .psc in sourceDir into outDir, importing TamrielMerge's shipped scripts (the Papyrus folder beside
    /// the program), SKSE's sources (Data\Scripts\Source) and Skyrim's (Data\Source\Scripts). Returns the scripts that
    /// compiled (null without the compiler) and the compiler's output. Thousands of scripts go in batches of a few
    /// hundred (the compiler can drop a whole batch when memory runs short); what a batch left out without an error of
    /// its own is compiled again alone.
    /// </summary>
    public static (HashSet<string>? Compiled, string Log) CompilePapyrus(string skyrimData, string sourceDir, string outDir, Action<string>? progress = null)
    {
        string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
        string compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        string vanilla = Path.Combine(skyrimData, "Source", "Scripts"), skse = Path.Combine(skyrimData, "Scripts", "Source");
        string shipped = Path.Combine(AppContext.BaseDirectory, "Papyrus");
        if (!File.Exists(compiler) || !File.Exists(Path.Combine(vanilla, "ObjectReference.psc")))
            return (null, "no Papyrus compiler or Skyrim script sources (install the Creation Kit)");
        sourceDir = Path.GetFullPath(sourceDir);
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        string imports = $"-i={sourceDir};{shipped};{skse};{vanilla}";
        var names = Directory.GetFiles(sourceDir, "*.psc").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList();
        bool Fresh(string n) => File.Exists(Path.Combine(outDir, n + ".pex")) && File.GetLastWriteTimeUtc(Path.Combine(outDir, n + ".pex")) >= File.GetLastWriteTimeUtc(Path.Combine(sourceDir, n + ".psc"));
        var log = new System.Text.StringBuilder();
        string chunkDir = sourceDir.TrimEnd('\\', '/') + "_batch";
        const int batch = 400;
        for (int start = 0; start < names.Count; start += batch)
        {
            var chunk = names.Skip(start).Take(batch).Where(n => !Fresh(n)).ToList();
            if (chunk.Count == 0) continue;
            if (Directory.Exists(chunkDir)) Directory.Delete(chunkDir, true);
            Directory.CreateDirectory(chunkDir);
            foreach (var n in chunk) File.Copy(Path.Combine(sourceDir, n + ".psc"), Path.Combine(chunkDir, n + ".psc"));
            log.Append(Run(new ProcessStartInfo(compiler)
            {
                ArgumentList = { chunkDir, "-all", "-q", "-f=TESV_Papyrus_Flags.flg", imports, $"-o={outDir}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }));
            progress?.Invoke($"{Math.Min(start + batch, names.Count)} of {names.Count} scripts compiled");
        }
        if (Directory.Exists(chunkDir)) Directory.Delete(chunkDir, true);
        // Scripts that failed with errors of their own stay failed; the rest (a dropped batch) are compiled alone.
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(log.ToString(), @"No output generated for ([^,\s]+)\.psc")) failed.Add(m.Groups[1].Value);
        foreach (var n in names.Where(n => !Fresh(n) && !failed.Contains(n)))
            log.Append(Run(new ProcessStartInfo(compiler)
            {
                ArgumentList = { n + ".psc", "-q", "-f=TESV_Papyrus_Flags.flg", imports, $"-o={outDir}" },
                WorkingDirectory = sourceDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }));
        return (names.Where(Fresh).ToHashSet(StringComparer.OrdinalIgnoreCase), log.ToString());

        static string Run(ProcessStartInfo info)
        {
            using var p = Process.Start(info)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + err.Result;
            p.WaitForExit();
            return output;
        }
    }

    /// <summary>Writes translated scripts' sources (Windows-1252, as Morrowind's text and Papyrus's strings are).</summary>
    public static void WriteScriptSources(IEnumerable<MwPapyrus.Output> outputs, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var o in outputs) File.WriteAllText(Path.Combine(dir, o.ScriptName + ".psc"), o.Source, Tes3Plugin.Encoding);
    }
}
