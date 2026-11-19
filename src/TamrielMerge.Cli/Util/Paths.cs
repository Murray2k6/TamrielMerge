using System.Text.Json;

namespace TamrielMerge.Util;

/// <summary>
/// Where the tool keeps what it makes on the way (user, 2026-10-26: "move the whole project to d:\ with dirs. even your
/// temp folders in appdata"): the app's settings (TamrielMerge.settings.json in the working folder: "temp" and "build",
/// chosen in the app's Options; user 2026-10-28: "the tool needs to convert on any drive letter that a user has"), or,
/// where the settings name no folder, an optional <c>paths.json</c> beside the program, <c>{"temp": "...", "build":
/// "..."}</c> (2026-10-28: a copied tool folder's old paths.json overrode the folders chosen in the app and sent a
/// build back to the other drive, so the settings win). <c>temp</c> is the folder every temporary file goes in (the process's TMP and TEMP are set to it, so
/// Path.GetTempPath and every program the tool starts use it); <c>build</c> is the folder a conversion is staged in
/// (<see cref="Drives.FastStaging"/>, whichever drive it is on). Without either, or a key, the user's own temp folder
/// and the fastest drive, as before.
/// </summary>
public static class Paths
{
    public const string FileName = "paths.json";

    /// <summary>The staging folder named in paths.json, or null.</summary>
    public static string? Build { get; private set; }
    /// <summary>The temp folder named in paths.json (and set as the process's), or null.</summary>
    public static string? Temp { get; private set; }
    /// <summary>The folder a conversion's result goes to, as the conversion told its child processes
    /// (TAMRIELMERGE_FINAL_OUT), or null: where an earlier conversion's sprite models are looked for.</summary>
    public static string? FinalOut { get; private set; }

    /// <summary>Where the sprite-model maker works (its exported sprites, job list, finished models and checks): in the
    /// temp folder, not under tools (user, 2026-10-29: "the model3d tool keeps its files after conversion is complete").
    /// Removed at the end of a conversion once the models are imported into the output.</summary>
    public static string ModelWork => Path.Combine(Temp ?? Path.Combine(Path.GetTempPath(), "TamrielMerge"), "model3d");

    /// <summary>
    /// A folder for work worth keeping from one conversion to the next (finished videos, the menu's rendered flyby and
    /// scenes, compiled scripts): "TamrielMerge Cache" beside the output's top folder (C:\TamrielMerge Output\convert
    /// gives C:\TamrielMerge Cache), or the settings' "cache". It was the tool's own out folder, which was empty again
    /// by the conversion of 2026-11-07: every video was upscaled again and the flyby rendered again, 495 of that
    /// run's 1,612 minutes (user, 2026-11-10: "fix the tools conversion seed as it takes way too long"). What an
    /// earlier conversion left in the old place is still used from there.
    /// </summary>
    public static string Cache(string sub)
    {
        string old = Path.GetFullPath(Path.Combine("out", sub));
        string? root = Environment.GetEnvironmentVariable("TAMRIELMERGE_CACHE") is { Length: > 0 } chosen ? chosen : null;
        if (root is null && (FinalOut ?? Environment.GetEnvironmentVariable("TAMRIELMERGE_FINAL_OUT")) is { Length: > 0 } final)
        {
            string full = Path.GetFullPath(final).TrimEnd(Path.DirectorySeparatorChar);
            string top = Path.GetDirectoryName(full) is { Length: > 3 } parent ? parent : full;
            root = Path.Combine(Path.GetDirectoryName(top) ?? Path.GetPathRoot(top) ?? top, "TamrielMerge Cache");
        }
        if (root is null) return old;
        string dir = Path.Combine(root, sub);
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(old) && Directory.EnumerateFileSystemEntries(old).Any()) return old;
            Directory.CreateDirectory(dir);
            string note = Path.Combine(root, "What this folder is.txt");
            if (!File.Exists(note))
                File.WriteAllText(note, "TamrielMerge keeps finished work here between conversions: the upscaled videos, the main menu's rendered scenes and the compiled scripts.\r\n"
                    + "Deleting it is safe, but the next conversion makes all of it again, which takes hours.\r\n");
            return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; }
    }

    /// <summary>Tells the child processes of this one (the builds) the folders this one settled on.</summary>
    static void Share()
    {
        if (Temp is not null) Environment.SetEnvironmentVariable("TAMRIELMERGE_TEMP", Temp);
        if (Build is not null) Environment.SetEnvironmentVariable("TAMRIELMERGE_BUILD", Build);
    }

    /// <summary>The conversion's final output folder, for this process's children (<see cref="FinalOut"/>).</summary>
    public static void ShareFinalOut(string finalDir)
    {
        FinalOut = Path.GetFullPath(finalDir);
        Environment.SetEnvironmentVariable("TAMRIELMERGE_FINAL_OUT", FinalOut);
    }

    static bool applied;

    /// <summary>The tool's own folder for temporary files: "updates" in its working folder (user, 2026-11-17: "make a
    /// seperate folder for the tool use called updates for temp"), so the tool's temporary files are never left in the
    /// user's Temp or in a folder elsewhere.</summary>
    public static string ToolTemp => Path.Combine(Workspace(), "updates");

    /// <summary>Without a temp folder chosen (the app's Options or paths.json), every temporary file goes in the tool's
    /// own "updates" folder (<see cref="ToolTemp"/>; until 2026-11-17 a "TamrielMerge Temp" beside the output folder):
    /// the process's TMP and TEMP are set so Path.GetTempPath and every program the tool starts use it.</summary>
    public static void TempBeside(string outDir)
    {
        if (Temp is not null) return;
        string beside = ToolTemp;
        try
        {
            Directory.CreateDirectory(beside);
            Temp = beside;
            Environment.SetEnvironmentVariable("TMP", Temp);
            Environment.SetEnvironmentVariable("TEMP", Temp);
            Share();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"temp folder {beside}: not used ({e.Message}); the user's own temp folder is used");
        }
    }

    /// <summary>The working folder the settings live in: the repository (TamrielMerge.slnx above the program) or the
    /// program's own folder, as the app finds it.</summary>
    public static string Workspace()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TamrielMerge.slnx"))) return dir.FullName;
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    static (string? Temp, string? Build) Read(string file, Action<string> warn)
    {
        string? temp = null, build = null;
        if (!File.Exists(file)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("temp", out var t) && t.GetString() is { Length: > 0 } st) temp = st;
            if (doc.RootElement.TryGetProperty("build", out var b) && b.GetString() is { Length: > 0 } sb) build = sb;
        }
        catch (Exception e) when (e is JsonException or IOException) { warn($"{Path.GetFileName(file)}: not read ({e.Message})"); }
        return (temp, build);
    }

    /// <summary>Reads the settings and paths.json and applies them; once per process.</summary>
    public static void Apply()
    {
        if (applied) return;
        applied = true;
        var settings = Read(Path.Combine(Workspace(), "TamrielMerge.settings.json"), Console.Error.WriteLine);
        var beside = Read(Path.Combine(AppContext.BaseDirectory, FileName), Console.Error.WriteLine);
        // A conversion's child processes (the builds) take the folders it settled on (environment), then the settings.
        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        string? temp = Env("TAMRIELMERGE_TEMP") ?? settings.Temp ?? beside.Temp, build = Env("TAMRIELMERGE_BUILD") ?? settings.Build ?? beside.Build;
        if (Env("TAMRIELMERGE_FINAL_OUT") is { } final) FinalOut = Path.GetFullPath(final);
        try
        {
            if (temp is not null)
            {
                Temp = Path.GetFullPath(temp);
                Directory.CreateDirectory(Temp);
                Environment.SetEnvironmentVariable("TMP", Temp);
                Environment.SetEnvironmentVariable("TEMP", Temp);
            }
            if (build is not null) Build = Path.GetFullPath(build);
            Share();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"temp/build folders: not used ({e.Message}); the usual folders are used");
            Temp = null;
            Build = null;
        }
    }
}
