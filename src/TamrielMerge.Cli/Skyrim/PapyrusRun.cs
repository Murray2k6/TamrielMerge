using System.Diagnostics;

namespace TamrielMerge.Skyrim;

/// <summary>Runs the Creation Kit's Papyrus compiler.</summary>
public static class PapyrusRun
{
    /// <summary>
    /// Runs the compiler and returns its output, with null for the exit code when it stopped answering and was stopped.
    /// Both of its streams are read at once, and it is given up after <paramref name="timeout"/> with the assemblers it
    /// started (2026-10-15: a 76-script -all compile sat for 24 minutes with ten PapyrusAssembler processes doing nothing,
    /// holding the output pipe open, so the build never went on).
    /// </summary>
    public static (int? ExitCode, string Output) Run(ProcessStartInfo info, TimeSpan timeout)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        int? code = null;
        if (p.WaitForExit(timeout)) code = p.ExitCode;
        else try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        // An assembler left behind (started after the tree was read) can still hold the pipe: the output read so far is enough.
        Task.WaitAll([output, error], TimeSpan.FromSeconds(30));
        string text = (output.IsCompletedSuccessfully ? output.Result : "") + (error.IsCompletedSuccessfully ? error.Result : "");
        return (code, code is null ? text + $"\n(the compiler stopped answering after {timeout.TotalMinutes:0} minutes and was stopped)\n" : text);
    }

    /// <summary>The batch compile's time limit: generous for a few hundred scripts, short of a hang.</summary>
    public static readonly TimeSpan Batch = TimeSpan.FromMinutes(10);

    /// <summary>One script's time limit.</summary>
    public static readonly TimeSpan One = TimeSpan.FromMinutes(2);
}
