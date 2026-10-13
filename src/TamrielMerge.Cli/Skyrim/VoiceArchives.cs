using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The generated voices as a mod of their own (user, 2026-10-10: "the voices need to be a mod because incase a user
/// wanted to disable them to be vanilla"; "you can make multiple bsa"): the packed voice files in archives of under
/// <see cref="Limit"/> bytes, uncompressed, two to a plugin (Skyrim loads "&lt;plugin&gt;.bsa" and "&lt;plugin&gt; - Textures.bsa"
/// for each), with empty light plugins "TamrielMerge Voices.esp", "TamrielMerge Voices 2.esp"... Turning those plugins off
/// turns the generated voices off: the lines are shown, not spoken, as in the original games. The games' own recordings
/// (Morrowind's barks, Redguard's and Oblivion's voice acting) stay with each game's conversion.
/// </summary>
public static class VoiceArchives
{
    public const string Name = "TamrielMerge Voices";
    const long Limit = 1_900_000_000;

    /// <summary>The voice files under <paramref name="packedDir"/> as archives and plugins in <paramref name="outDir"/>
    /// (those of an earlier build replaced); returns the plugins' file names.</summary>
    public static List<string> Build(string packedDir, string outDir)
    {
        foreach (var old in Directory.EnumerateFiles(outDir, Name + "*").Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)).ToList())
            File.Delete(old);
        if (!Directory.Exists(packedDir)) return [];
        var files = Directory.EnumerateFiles(packedDir, "*.fuz", SearchOption.AllDirectories)
            .Select(f => (InArchive: Path.GetRelativePath(packedDir, f), Source: f, Size: new FileInfo(f).Length))
            .OrderBy(f => f.InArchive, StringComparer.OrdinalIgnoreCase).ToList();
        var archives = new List<List<(string InArchive, string Source)>>();
        long size = Limit;
        foreach (var f in files)
        {
            if (size + f.Size > Limit) { archives.Add([]); size = 0; }
            archives[^1].Add((f.InArchive, f.Source));
            size += f.Size;
        }
        var plugins = new List<string>();
        for (int k = 0; k < archives.Count; k++)
        {
            string plugin = k / 2 == 0 ? Name : $"{Name} {k / 2 + 1}";
            BsaWriter.Write(Path.Combine(outDir, plugin + (k % 2 == 0 ? ".bsa" : " - Textures.bsa")), archives[k]);
            if (k % 2 == 0)
            {
                var mod = new SkyrimMod(ModKey.FromFileName(plugin + ".esp"), SkyrimRelease.SkyrimSE);
                mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;   // light: no load-order slot
                mod.WriteToBinary(Path.Combine(outDir, plugin + ".esp"));
                plugins.Add(plugin + ".esp");
            }
            Console.WriteLine($"Voices: archive {k + 1} of {archives.Count}: {archives[k].Count:N0} voice files");
        }
        return plugins;
    }
}
