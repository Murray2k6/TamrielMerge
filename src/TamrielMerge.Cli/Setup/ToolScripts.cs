using System.Diagnostics;

namespace TamrielMerge.Setup;

/// <summary>The Python tools shipped with the converter (copied beside it under tools\, else the repository's
/// tools\ when run from source) and running them with the installed tools' environment.</summary>
public static class ToolScripts
{
    /// <summary>A shipped script or data file, e.g. Path("menu", "intro.py").</summary>
    public static string Path(params string[] parts)
    {
        string shipped = System.IO.Path.Combine([AppContext.BaseDirectory, "tools", .. parts]);
        return File.Exists(shipped) ? shipped : System.IO.Path.GetFullPath(System.IO.Path.Combine(["tools", .. parts]));
    }

    /// <summary>Runs a program with its output shown; the tools' locations go in its environment (TAMRIELMERGE_TOOLS,
    /// HF_HOME for the Hugging Face models, the model Python and Blender for the menu's renders).</summary>
    public static int Run(string exe, IEnumerable<string> args, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TAMRIELMERGE_TOOLS"] = Dependencies.Root;
        psi.Environment["HF_HOME"] = Dependencies.ModelCache;
        if (Dependencies.ModelPython is { } mp) psi.Environment["TAMRIELMERGE_MODEL_PYTHON"] = mp;
        if (Dependencies.Blender is { } bl) psi.Environment["TAMRIELMERGE_BLENDER"] = bl;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
