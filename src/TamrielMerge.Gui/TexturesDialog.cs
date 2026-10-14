using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TamrielMerge.Skyrim.Models;

namespace TamrielMerge.Gui;

/// <summary>Runs the converter (TamrielMerge.Cli.exe beside the app) for the model tools and returns its output.</summary>
static class Cli
{
    public static Task<(int Code, string Output)> Run(params string[] args) => Task.Run(() =>
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TamrielMerge.Cli.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        psi.WorkingDirectory = MainForm.Workspace;
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, p.ExitCode == 0 ? output : output + err.Result);
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, $"the converter couldn't be started: {e.Message}"); }
    });

    /// <summary>Thumbnails being made, by file: a texture shown twice at once (parts sharing it) waits for one run.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Making = new(StringComparer.OrdinalIgnoreCase);

    public static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..16];

    /// <summary>A texture's picture (cached under out\texture-thumbs), or null.</summary>
    public static async Task<Image?> Thumb(string texture, string dir, int size = 256)
    {
        if (string.IsNullOrEmpty(texture)) return null;
        string file = Path.Combine(MainForm.Workspace, "out", "texture-thumbs", $"{Hash(texture)}_{size}.png");
        if (!File.Exists(file))
            await Making.GetOrAdd(file, f => Task.Run(async () =>
            {
                try { await Run("texture-thumb", "--texture", texture, "--out", f, "--dir", dir, "--size", size.ToString()); }
                finally { Making.TryRemove(f, out _); }
            }));
        if (!File.Exists(file)) return null;
        // A picture that can't be read (2026-10-14: a half-written one threw out of the window) shows as none.
        try { using var img = Image.FromFile(file); return new Bitmap(img); }
        catch (Exception e) when (e is OutOfMemoryException or System.Runtime.InteropServices.ExternalException or IOException or ArgumentException)
        {
            try { File.Delete(file); } catch (IOException) { }
            return null;
        }
    }
}

/// <summary>
/// A model's parts and their texture slots (user, 2026-10-08: "a texture selector with the model viewer"): any slot can
/// take a texture from any of the user's games (kept in texture-overrides.json, applied as texture sets in TamrielMerge
/// Models.esp), and a part's colour texture can be made into a PBR material for Skyrim's own shaders (PbrDialog).
/// </summary>
public sealed class TexturesDialog : Form
{
    readonly string dir, original;
    string model;
    readonly ListBox parts = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TableLayoutPanel slots = new() { Dock = DockStyle.Fill, ColumnCount = 4, AutoScroll = true };
    readonly Button pbrButton = new() { Text = "Make a PBR material from this part's colour…", AutoSize = true, Enabled = false };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) };
    List<TextureTools.ShapeInfo> shapes = [];
    Dictionary<string, Dictionary<int, Dictionary<int, string>>> swaps;
    List<TextureTools.TextureEntry>? available;

    /// <summary>True when the model was changed (a PBR copy made) so the viewer reloads.</summary>
    public bool ModelChanged { get; private set; }

    public TexturesDialog(string outputDir, string originalModel, string currentModel)
    {
        dir = outputDir; original = originalModel; model = currentModel;
        // A choices file that can't be read starts the window empty rather than failing to open it.
        try { swaps = TextureTools.LoadOverrides(dir); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { swaps = []; status.Text = $"Your texture choices couldn't be read ({e.Message})."; }
        Text = $"Textures — {currentModel}";
        Width = 1050; Height = 720; MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(10);
        slots.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        slots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        slots.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        slots.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260 };
        split.Panel1.Controls.Add(parts);
        split.Panel1.Controls.Add(new Label { Text = "Parts", Dock = DockStyle.Top, Height = 22 });
        split.Panel2.Controls.Add(slots);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        bottom.Controls.AddRange([pbrButton, status]);
        Controls.Add(split); Controls.Add(bottom);
        parts.SelectedIndexChanged += (_, _) => ShowPart();
        pbrButton.Click += (_, _) => MakePbr();
        Shown += async (_, _) => await LoadShapes();
    }

    async Task LoadShapes()
    {
        status.Text = "Reading the model…";
        var (code, json) = await Cli.Run("model-textures", "--model", model, "--dir", dir);
        if (IsDisposed) return;
        try { shapes = code == 0 ? JsonSerializer.Deserialize<List<TextureTools.ShapeInfo>>(json) ?? [] : []; }
        catch (JsonException) { shapes = []; }
        parts.Items.Clear();
        foreach (var s in shapes)
            parts.Items.Add($"{s.Index}: {s.Name}" + (s.ShaderType switch { 1 => "  (reflective)", 2 => "  (glow)", 3 => "  (parallax)", _ => "" }));
        status.Text = shapes.Count == 0 ? "This model has no parts with textures (or couldn't be read)." : "Choose a part. Changes go into the models plugin when it's built.";
        if (parts.Items.Count > 0) parts.SelectedIndex = 0;
    }

    Dictionary<int, string> SwapsOf(int part)
    {
        if (!swaps.TryGetValue(ModelCatalog.Key(model), out var p)) swaps[ModelCatalog.Key(model)] = p = [];
        if (!p.TryGetValue(part, out var s)) p[part] = s = [];
        return s;
    }

    int showingPart;

    async void ShowPart()
    {
        // A newer choice (another part clicked) or the window closing stops this one's pictures.
        int ticket = ++showingPart;
        slots.SuspendLayout();
        foreach (Control c in slots.Controls) { if (c is PictureBox pb) pb.Image?.Dispose(); }
        slots.Controls.Clear(); slots.RowStyles.Clear();
        if (parts.SelectedIndex < 0) { slots.ResumeLayout(); return; }
        var shape = shapes[parts.SelectedIndex];
        var mine = SwapsOf(shape.Index);
        pbrButton.Enabled = shape.Textures[0].Length > 0 && shape.ShaderType != 2;
        var pictures = new List<(PictureBox Box, string Texture)>();
        for (int slot = 0; slot < 8; slot++)
        {
            string tex = mine.GetValueOrDefault(slot) ?? shape.Textures[slot];
            if (tex.Length == 0 && slot > 1 && !mine.ContainsKey(slot)) continue;
            var pic = new PictureBox { Width = 92, Height = 92, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(90, 90, 96), Margin = new Padding(2) };
            var label = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(6, 8, 6, 0),
                Text = $"{TextureTools.SlotNames[slot]}{(mine.ContainsKey(slot) ? "  — swapped" : "")}\n{(tex.Length > 0 ? tex : "(none)")}" };
            var change = new Button { Text = "Change…", AutoSize = true, Tag = slot };
            var reset = new Button { Text = "Original", AutoSize = true, Tag = slot, Enabled = mine.ContainsKey(slot) };
            change.Click += async (s, _) => await Change(shape, (int)((Button)s!).Tag!);
            reset.Click += (s, _) => { mine.Remove((int)((Button)s!).Tag!); Save(); ShowPart(); };
            slots.Controls.AddRange([pic, label, change, reset]);
            pictures.Add((pic, tex));
        }
        slots.ResumeLayout();
        foreach (var (box, tex) in pictures)
        {
            var img = await Cli.Thumb(tex, dir, 128);
            if (ticket != showingPart || IsDisposed || box.IsDisposed) { img?.Dispose(); return; }
            box.Image = img;
        }
    }

    Task? listing;

    async Task Change(TextureTools.ShapeInfo shape, int slot)
    {
        if (available is null)
        {
            // One listing at a time (a second Change… waits for it), and nothing shown if the window closed meanwhile.
            status.Text = "Listing the textures your games offer…";
            UseWaitCursor = true;
            try
            {
                string list = Path.Combine(MainForm.Workspace, "out", "textures-available.json");
                listing ??= (!File.Exists(list) || File.GetLastWriteTime(list) < DateTime.Now.AddHours(-12)) ? Cli.Run("textures-index", "--dir", dir, "--out", list) : Task.CompletedTask;
                await listing;
                if (IsDisposed) return;
                try { available = File.Exists(list) ? JsonSerializer.Deserialize<List<TextureTools.TextureEntry>>(File.ReadAllText(list)) ?? [] : []; }
                catch (Exception e) when (e is JsonException or IOException) { available = []; status.Text = $"The texture list couldn't be read ({e.Message})."; return; }
                status.Text = "";
            }
            finally { if (!IsDisposed) UseWaitCursor = false; listing = null; }
        }
        if (IsDisposed) return;
        using var picker = new TexturePickerDialog(available!, dir, $"{TextureTools.SlotNames[slot]} of part {shape.Index}", helpers: slot != 0);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen is null) return;
        SwapsOf(shape.Index)[slot] = @"textures\" + picker.Chosen;
        Save();
        ShowPart();
    }

    void Save()
    {
        try
        {
            TextureTools.SaveOverrides(dir, swaps);
            status.Text = "Saved. Build the replacements plugin in the Models window to use it in the game.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = $"Couldn't save your choice ({e.Message}): is the file open elsewhere?"; }
    }

    void MakePbr()
    {
        if (parts.SelectedIndex < 0) return;
        var shape = shapes[parts.SelectedIndex];
        string colour = SwapsOf(shape.Index).GetValueOrDefault(0) ?? shape.Textures[0];
        using var pbr = new PbrDialog(dir, colour, original, shape.Index);
        if (pbr.ShowDialog(this) != DialogResult.OK) return;
        ModelChanged = true;
        model = ModelCatalog.LoadOverrides(dir).GetValueOrDefault(original) ?? model;
        Text = $"Textures — {model}";
        _ = LoadShapes();
    }
}

/// <summary>Choosing a texture: every one the user's games offer, by game, searchable, each pictured.</summary>
public sealed class TexturePickerDialog : Form
{
    static readonly string[] HelperSuffixes = ["_n.dds", "_msn.dds", "_g.dds", "_m.dds", "_p.dds", "_e.dds", "_s.dds", "_b.dds", "_sk.dds", "_em.dds"];
    readonly List<TextureTools.TextureEntry> all;
    List<TextureTools.TextureEntry> shown = [];
    readonly string dir;
    readonly ComboBox gameBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly TextBox searchBox = new() { Width = 300, PlaceholderText = "Search, e.g. stone wall, wood, imperialcity" };
    readonly CheckBox helpersBox = new() { Text = "Include normal and other helper maps", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, VirtualMode = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
    readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(90, 90, 96) };
    readonly Button useButton = new() { Text = "Use this texture", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
    int showing;

    public string? Chosen { get; private set; }

    public TexturePickerDialog(List<TextureTools.TextureEntry> available, string outputDir, string slot, bool helpers)
    {
        all = available; dir = outputDir;
        Text = $"Choose a texture for the {slot}";
        Width = 1100; Height = 700;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(10);
        helpersBox.Checked = helpers;
        list.Columns.Add("Texture", 560); list.Columns.Add("Game", 100);
        list.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem([shown[e.ItemIndex].Texture, shown[e.ItemIndex].Game]);
        list.SelectedIndexChanged += async (_, _) =>
        {
            Chosen = list.SelectedIndices.Count == 1 ? shown[list.SelectedIndices[0]].Texture : null;
            useButton.Enabled = Chosen is not null;
            int ticket = ++showing;
            var img = Chosen is null ? null : await Cli.Thumb(@"textures\" + Chosen, dir, 384);
            if (ticket != showing) { img?.Dispose(); return; }
            picture.Image?.Dispose(); picture.Image = img;
        };
        list.DoubleClick += (_, _) => { if (Chosen is not null) { DialogResult = DialogResult.OK; Close(); } };
        gameBox.Items.AddRange(["All games", "Skyrim", "Daggerfall", "Redguard", "Morrowind", "Oblivion"]);
        gameBox.SelectedIndex = 0;
        gameBox.SelectedIndexChanged += (_, _) => Filter();
        searchBox.TextChanged += (_, _) => Filter();
        helpersBox.CheckedChanged += (_, _) => Filter();
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        filters.Controls.AddRange([gameBox, searchBox, helpersBox]);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(list); left.Controls.Add(filters);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 680 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(picture);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        bottom.Controls.AddRange([cancel, useButton]);
        Controls.Add(split); Controls.Add(bottom);
        AcceptButton = useButton; CancelButton = cancel;
        Filter();
    }

    void Filter()
    {
        string game = gameBox.SelectedIndex <= 0 ? "" : (string)gameBox.SelectedItem!;
        var words = searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        shown = all.Where(t => (game == "" || t.Game == game)
                               && (helpersBox.Checked || !HelperSuffixes.Any(s => t.Texture.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                               && words.All(w => t.Texture.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        list.SelectedIndices.Clear();
        list.VirtualListSize = shown.Count;
        list.Invalidate();
    }
}

/// <summary>
/// The PBR tool (tools/textures/pbr.py): a part's colour texture made into a physically based set by an AI normals
/// model and tunable maps, shown on a material ball and flat swatch as the sliders move, then baked for Skyrim's own
/// shaders onto a copy of the model (TextureTools.BakePbr) — no Community Shaders needed.
/// </summary>
public sealed class PbrDialog : Form
{
    readonly string dir, texture, model;
    readonly int part;
    readonly string work;
    readonly PictureBox previewBox = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 30, 32) };
    readonly FlowLayoutPanel mapsRow = new() { Dock = DockStyle.Bottom, Height = 150, WrapContents = false, AutoScroll = true };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) };
    readonly Button applyButton = new() { Text = "Bake onto the model", AutoSize = true, Enabled = false };
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 600 };
    readonly Dictionary<string, TrackBar> sliders = [];
    readonly CheckBox parallaxBox = new() { Text = "Parallax depth (from height)", AutoSize = true, Checked = true };
    readonly CheckBox reflectBox = new() { Text = "Reflections (metal, polish)", AutoSize = true, Checked = true };
    bool running, again;

    static readonly (string Key, string Label, float Min, float Max, float Default)[] Params =
    [
        ("normal_strength", "Surface detail", 0, 3, 1), ("height_depth", "Height depth", 0, 3, 1),
        ("rough_base", "Roughness", 0.05f, 1, 0.65f), ("rough_detail", "Roughness variation", 0, 2, 0.5f),
        ("metal", "Metalness", 0, 1, 0), ("metal_from_colour", "Metal from colour (bright grey)", 0, 1, 0),
        ("ao_strength", "Crevice shadow", 0, 1.5f, 0.5f), ("spec_strength", "Shine", 0, 2, 1),
    ];

    public PbrDialog(string outputDir, string colourTexture, string originalModel, int partIndex)
    {
        dir = outputDir; texture = colourTexture; model = originalModel; part = partIndex;
        work = Path.Combine(Environment.CurrentDirectory, "out", "pbr-work", Cli.Hash(texture));
        Text = $"PBR material — {texture}";
        Width = 1100; Height = 760; MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(10);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (var (key, label, min, max, def) in Params)
        {
            var bar = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 300, Value = (int)Math.Round((def - min) / (max - min) * 100) };
            bar.ValueChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
            sliders[key] = bar;
            controls.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            controls.Controls.Add(bar);
        }
        parallaxBox.CheckedChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        reflectBox.CheckedChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        controls.Controls.AddRange([parallaxBox, reflectBox, new Label
        {
            AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 10, 3, 0),
            Text = "Baked for Skyrim's own shaders: crevice shadow into the colour, roughness into the normal map's shine, " +
                   "metal and polish into reflections, height into parallax (one of the two: reflections win when both are on).",
        }]);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(previewBox); right.Controls.Add(mapsRow);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 330 };
        split.Panel1.Controls.Add(controls); split.Panel2.Controls.Add(right);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        var cancel = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        bottom.Controls.AddRange([applyButton, cancel, status]);
        Controls.Add(split); Controls.Add(bottom);
        CancelButton = cancel;
        debounce.Tick += async (_, _) => { debounce.Stop(); await Make(); };
        applyButton.Click += async (_, _) => await Bake();
        Shown += async (_, _) => await Make();
        FormClosed += (_, _) => debounce.Dispose();
    }

    string ParamsJson()
    {
        var p = new Dictionary<string, object>();
        foreach (var (key, _, min, max, _) in Params) p[key] = MathF.Round(min + (max - min) * sliders[key].Value / 100f, 3);
        p["parallax"] = parallaxBox.Checked; p["reflections"] = reflectBox.Checked;
        return JsonSerializer.Serialize(p);
    }

    async Task Make()
    {
        if (running) { again = true; return; }
        running = true; applyButton.Enabled = false;
        status.Text = Directory.Exists(work) && Directory.GetFiles(work, "normals_*.npy").Length > 0 ? "Updating…" : "Working out the surface with the AI (the first time takes a few seconds)…";
        Directory.CreateDirectory(work);
        string paramsFile = Path.Combine(work, "params.json");
        File.WriteAllText(paramsFile, ParamsJson());
        var (code, output) = await Cli.Run("pbr-make", "--texture", texture, "--out", work, "--params", paramsFile, "--dir", dir);
        running = false;
        if (code != 0) { status.Text = "Couldn't make the material: " + output.Split('\n').LastOrDefault(l => l.Trim().Length > 0); return; }
        Show(previewBox, Path.Combine(work, "preview.png"));
        foreach (Control c in mapsRow.Controls) if (c is PictureBox pb) pb.Image?.Dispose();
        mapsRow.Controls.Clear();
        foreach (var name in new[] { "normal", "height", "roughness", "metalness", "occlusion" })
        {
            var box = new PictureBox { Width = 140, Height = 140, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(2) };
            Show(box, Path.Combine(work, $"map_{name}.png"));
            new ToolTip().SetToolTip(box, name);
            mapsRow.Controls.Add(box);
        }
        status.Text = "Left: the material on a ball and a flat swatch. Below: normals, height, roughness, metalness, crevice shadow.";
        applyButton.Enabled = true;
        if (again) { again = false; await Make(); }
    }

    static void Show(PictureBox box, string file)
    {
        if (!File.Exists(file)) return;
        using var img = Image.FromFile(file);
        box.Image?.Dispose();
        box.Image = new Bitmap(img);
    }

    async Task Bake()
    {
        applyButton.Enabled = false;
        status.Text = "Baking onto the model…";
        var (code, output) = await Cli.Run("pbr-apply", "--model", model, "--shape", part.ToString(), "--material", work, "--dir", dir);
        if (code != 0) { status.Text = "Couldn't bake it: " + output.Split('\n').FirstOrDefault(l => l.Contains("Exception")) ; applyButton.Enabled = true; return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}
