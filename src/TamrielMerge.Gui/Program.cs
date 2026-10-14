namespace TamrielMerge.Gui;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Errors anywhere in the app (user, 2026-10-14: ".NET runtime errors on the tool in certain areas") are written with
        // their full stack to logs\errors.log beside the app's log, and shown, rather than lost in .NET's own dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception, fatal: false, quiet: true); e.SetObserved(); };
        Application.Run(new MainForm());
    }

    static void Report(Exception? e, bool fatal, bool quiet = false)
    {
        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(fatal ? "FATAL " : "")}{e}{Environment.NewLine}{Environment.NewLine}";
        string file = Path.Combine(MainForm.Workspace, "logs", "errors.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, text);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (quiet) return;
        try
        {
            MessageBox.Show($"Something went wrong:\n\n{e?.Message}\n\nThe details are in {file}.", "TamrielMerge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
