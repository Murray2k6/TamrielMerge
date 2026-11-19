using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Each installed plugin's start list, Data\SEQ\&lt;plugin&gt;.seq: the FormIDs of its quests that start with the game
/// (the file the Creation Kit writes for a plugin; Skyrim's own add-ons ship theirs). Without it Skyrim starts those
/// quests on a new game with their dialogue left out: seen in game 2026-11-09, where Morrowind's dialogue quest ran
/// (sqv: Running) with a topic known, yet nobody said a greeting and no topic menu ever opened; with Morrowind.seq in
/// place Fargoth said "I don't know you. What do you want?", the menu offered Latest rumors and Background, and Latest
/// rumors taught Solstheim. A FormID in the file is as the plugin's own records hold it: the plugin's master count in
/// the top byte.
/// </summary>
public static class SeqFiles
{
    public const string Folder = "SEQ";

    /// <summary>The path under Data of <paramref name="plugin"/>'s start list.</summary>
    public static string NameOf(string plugin) => Path.Combine(Folder, Path.GetFileNameWithoutExtension(plugin) + ".seq");

    /// <summary>The start list's bytes for the plugin at <paramref name="path"/>, or none when no quest of its own
    /// starts with the game.</summary>
    public static byte[]? Build(string path)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        uint index = (uint)mod.ModHeader.MasterReferences.Count << 24;
        var ids = mod.Quests.Where(q => q.FormKey.ModKey == mod.ModKey && q.Flags.HasFlag(Quest.Flag.StartGameEnabled))
            .Select(q => index | q.FormKey.ID).Order().ToList();
        if (ids.Count == 0) return null;
        var bytes = new byte[ids.Count * 4];
        for (int i = 0; i < ids.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), ids[i]);
        return bytes;
    }

    /// <summary>Writes the start lists of <paramref name="plugins"/> (names in <paramref name="data"/>) and removes the
    /// list of one that has no such quest any more. Returns the lists written (paths under Data).</summary>
    public static List<string> Write(string data, IEnumerable<string> plugins, Action<string> log)
    {
        var written = new List<string>();
        int quests = 0;
        foreach (var plugin in plugins.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(data, plugin);
            if (!File.Exists(path)) continue;
            string rel = NameOf(plugin), target = Path.Combine(data, rel);
            var bytes = Build(path);
            if (bytes is null) { if (File.Exists(target)) File.Delete(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(target, bytes);
            written.Add(rel);
            quests += bytes.Length / 4;
        }
        log($"  start lists (SEQ): {written.Count} plugins, {quests:N0} quests that start with the game ({string.Join(", ", written.Select(Path.GetFileName))})");
        return written;
    }
}
