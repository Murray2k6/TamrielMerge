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
        Disposed += (_, _) => { spin.Dispose(); Clear(); };
    }

    void Show(int i) { frame = i; picture.Image = frames[i]; }

    void Clear()
    {
        picture.Image = null;
        foreach (var f in frames) f.Dispose();
        frames.Clear();
    }

    /// <summary>Shows <paramref name="model"/> (as records hold it), making its pictures first if they aren't cached.</summary>
    public async void ShowModel(string? model, string? note = null)
    {
        if (model == showing) return;
        showing = model;
        loading?.Cancel();
        var cancel = loading = new CancellationTokenSource();
        spin.Stop();
        Clear();
        if (model is null) { caption.Text = "Choose a model to see it."; return; }
        caption.Text = $"{model}\nMaking its preview…";
        string dir = Path.Combine(Environment.CurrentDirectory, "out", "model-previews",
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(model.ToLowerInvariant())))[..16]);
        string first = Path.Combine(dir, "view_00.png");
        if (!File.Exists(first))
        {
            int code = await Task.Run(() =>
            {
                string cli = Path.Combine(AppContext.BaseDirectory, "TamrielMerge.Cli.exe");
                var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "model-preview", "--model", model, "--out", dir, "--loose", LooseFolder }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            });
            if (cancel.IsCancellationRequested) return;
            if (code != 0 || !File.Exists(first)) { caption.Text = $"{model}\nNo preview (the model couldn't be read or drawn)."; return; }
        }
        if (cancel.IsCancellationRequested) return;
        foreach (var f in Directory.GetFiles(dir, "view_*.png").Order())
            using (var img = Image.FromFile(f)) frames.Add(new Bitmap(img));
        if (frames.Count == 0) { caption.Text = $"{model}\nNo preview."; return; }
        Show(0);
        caption.Text = note is null ? $"{model}\nDrag to turn it." : $"{model}\n{note}";
        spin.Start();
    }
}
