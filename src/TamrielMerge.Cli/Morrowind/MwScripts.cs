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
        const int batch = 400;
        // Several batches at once, each in a folder of its own (2026-10-14: one at a time took 17 minutes for 4,930
        // scripts; two side by side took no longer than one).
        var chunks = Enumerable.Range(0, (names.Count + batch - 1) / batch)
            .Select(i => (Index: i, Names: names.Skip(i * batch).Take(batch).Where(n => !Fresh(n)).ToList())).Where(c => c.Names.Count > 0).ToList();
        int compiled = 0;
        Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 4) }, c =>
        {
            string chunkDir = sourceDir.TrimEnd('\\', '/') + "_batch" + c.Index;
            if (Directory.Exists(chunkDir)) Directory.Delete(chunkDir, true);
            Directory.CreateDirectory(chunkDir);
            foreach (var n in c.Names) File.Copy(Path.Combine(sourceDir, n + ".psc"), Path.Combine(chunkDir, n + ".psc"));
            string output = Run(new ProcessStartInfo(compiler)
            {
                ArgumentList = { chunkDir, "-all", "-q", "-f=TESV_Papyrus_Flags.flg", imports, $"-o={outDir}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Directory.Delete(chunkDir, true);
            lock (log)
            {
                log.Append(output);
                compiled += c.Names.Count;
                progress?.Invoke($"{compiled} of {chunks.Sum(x => x.Names.Count)} scripts compiled");
            }
        });
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

        static string Run(ProcessStartInfo info) => Skyrim.PapyrusRun.Run(info, info.ArgumentList.Contains("-all") ? Skyrim.PapyrusRun.Batch : Skyrim.PapyrusRun.One).Output;
    }

    /// <summary>Writes translated scripts' sources (Windows-1252, as Morrowind's text and Papyrus's strings are).</summary>
    public static void WriteScriptSources(IEnumerable<MwPapyrus.Output> outputs, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var o in outputs) File.WriteAllText(Path.Combine(dir, o.ScriptName + ".psc"), o.Source, Tes3Plugin.Encoding);
    }
}
