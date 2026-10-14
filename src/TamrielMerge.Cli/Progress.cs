namespace TamrielMerge;

/// <summary>
/// Progress for the app's bar: lines "PROGRESS 0.4210 text" on standard output (the app reads them and shows the text
/// and a bar; on a console they read as they are). A step can run inside part of another's range (<see cref="Within"/>):
/// `convert` gives each of its steps a share, and a step's own reports (parts built, lines spoken) fill that share.
/// </summary>
static class Progress
{
    static double offset, span = 1;
    static string? statusFile;
    static DateTime statusStarted;

    /// <summary>Reports <paramref name="fraction"/> (0..1) of the current range done, with what is happening.</summary>
    public static void Report(double fraction, string text)
    {
        if (!double.IsFinite(fraction)) fraction = 0;   // nothing to count (0 of 0) is no progress, not NaN
        string line = FormattableString.Invariant($"PROGRESS {offset + span * Math.Clamp(fraction, 0, 1):F4} {text}");
        // A part built beside others (convert's parts) leaves the bar to its parent.
        if (Environment.GetEnvironmentVariable("TAMRIELMERGE_PROGRESS") != "off") Console.WriteLine(line);
        if (statusFile is null) return;
        // The status file: this process, when it started, and its latest report (written whole, then renamed).
        try
        {
            File.WriteAllText(statusFile + ".tmp", $"{Environment.ProcessId}\n{statusStarted:O}\n{line}\n");
            File.Move(statusFile + ".tmp", statusFile, true);
        }
        catch (IOException) { }
    }

    /// <summary>Reports also go to <paramref name="file"/> until the result is disposed (which deletes it), so the app shows
    /// a long run's progress even when it didn't start it or was closed and opened again (user, 2026-10-10: "the tool
    /// needs a progress timer and progress left bar").</summary>
    public static IDisposable ToStatusFile(string file)
    {
        statusFile = file;
        statusStarted = DateTime.Now;
        return new Cleanup(file);
    }

    sealed class Cleanup(string file) : IDisposable
    {
        public void Dispose() { statusFile = null; try { File.Delete(file); } catch (IOException) { } }
    }

    /// <summary>The reports until the result is disposed fill [start, start + length] of the current range.</summary>
    public static IDisposable Within(double start, double length)
    {
        var restore = new Restore(offset, span);
        offset += span * start;
        span *= length;
        return restore;
    }

    sealed class Restore(double o, double s) : IDisposable
    {
        public void Dispose() { offset = o; span = s; }
    }
}
