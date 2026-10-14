using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TamrielMerge.Gui;

/// <summary>
/// A model turned round: pictures made by the converter (TamrielMerge.Cli model-preview: the model exported and drawn
/// smooth and matte with its own textures from 24 angles), cached under out\model-previews. It turns slowly on its own;
/// dragging turns it by hand.
/// </summary>
public sealed class ModelPreview : UserControl
{
    readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(236, 237, 240) };
    readonly Label caption = new() { Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray };
    readonly System.Windows.Forms.Timer spin = new() { Interval = 120 };
    readonly List<Image> frames = [];
    int frame;
    int? dragX;
    int dragFrame;
    string? showing;
    CancellationTokenSource? loading;

    /// <summary>Converted files not installed yet are looked for here first (the output folder).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string LooseFolder { get; set; } = "";

    public ModelPreview()
    {
        Controls.Add(picture);
        Controls.Add(caption);
        caption.Text = "Choose a model to see it.";
        spin.Tick += (_, _) => { if (dragX is null && frames.Count > 0) Show((frame + 1) % frames.Count); };
        picture.MouseDown += (_, e) => { dragX = e.X; dragFrame = frame; };
        picture.MouseUp += (_, _) => dragX = null;
        picture.MouseMove += (_, e) =>
        {
            if (dragX is null || frames.Count == 0) return;
            int step = (e.X - dragX.Value) / 12;
            Show(((dragFrame - step) % frames.Count + frames.Count) % frames.Count);
        };
        // Closing the window stops a preview still being made (2026-10-14: its frames were loaded into the closed control
        // and its timer kept ticking).
        Disposed += (_, _) => { loading?.Cancel(); spin.Dispose(); Clear(); };
    }

    void Show(int i) { frame = i; picture.Image = frames[i]; }

    void Clear()
    {
        picture.Image = null;
        foreach (var f in frames) f.Dispose();
        frames.Clear();
    }

    /// <summary>Previews being made, by folder: a model asked for again (or by two windows at once) waits for the same run
    /// instead of starting a second one into the same folder.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<int>> Making = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shows <paramref name="model"/> (as records hold it), making its pictures first if they aren't cached.</summary>
    public async void ShowModel(string? model, string? note = null)
    {
        try { await ShowModelAsync(model, note); }
        catch (Exception e) when (e is IOException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or ArgumentException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            // A broken or unreadable preview: say so rather than fail.
            if (IsDisposed) return;
            Clear();
            caption.Text = $"{model}\nNo preview ({e.Message}).";
        }
    }

    async Task ShowModelAsync(string? model, string? note)
    {
        if (model == showing) return;
        showing = model;
        loading?.Cancel();
        var cancel = loading = new CancellationTokenSource();
        spin.Stop();
        Clear();
        if (model is null) { caption.Text = "Choose a model to see it."; return; }
        caption.Text = $"{model}\nMaking its preview…";
        string dir = Path.Combine(MainForm.Workspace, "out", "model-previews",
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(model.ToLowerInvariant())))[..16]);
        // The converter writes done.txt once all the pictures are there (they are made in a folder of their own first).
        string done = Path.Combine(dir, "done.txt");
        if (!File.Exists(done))
        {
            string loose = LooseFolder;
            int code = await Making.GetOrAdd(dir, key => Task.Run(() =>
            {
                try
                {
                    string cli = Path.Combine(AppContext.BaseDirectory, "TamrielMerge.Cli.exe");
                    var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = MainForm.Workspace };
                    foreach (var a in new[] { "model-preview", "--model", model, "--out", dir, "--loose", loose }) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    p.WaitForExit();
                    return p.ExitCode;
                }
                catch (System.ComponentModel.Win32Exception) { return -1; }   // the converter is missing
                finally { Making.TryRemove(dir, out _); }
            }));
            if (cancel.IsCancellationRequested || IsDisposed) return;
            if (code != 0 || !File.Exists(done)) { caption.Text = $"{model}\nNo preview (the model couldn't be read or drawn)."; return; }
        }
        if (cancel.IsCancellationRequested || IsDisposed) return;
        foreach (var f in Directory.GetFiles(dir, "view_*.png").Order())
            using (var img = Image.FromFile(f)) frames.Add(new Bitmap(img));
        if (frames.Count == 0) { caption.Text = $"{model}\nNo preview."; return; }
        Show(0);
        caption.Text = note is null ? $"{model}\nDrag to turn it." : $"{model}\n{note}";
        spin.Start();
    }
}
