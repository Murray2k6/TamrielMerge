using TamrielMerge.Skyrim.Models;

namespace TamrielMerge.Gui;

/// <summary>
/// The model viewer and replacer (user, 2026-10-08): every model the converted plugins use, by game and kind,
/// searchable, each shown turning round; any can be replaced by a model from any of the user's games (Skyrim's own, or
/// Daggerfall's, Redguard's, Morrowind's and Oblivion's as converted), chosen from its own preview. The choices are kept
/// in model-overrides.json in the output folder and built into TamrielMerge Models.esp (ModelCatalog), which converting
/// also rebuilds; nothing is overwritten, and leaving the plugin out undoes it.
/// </summary>
public sealed class ModelsDialog : Form
{
    readonly string dir;
    readonly ComboBox gameBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly TextBox searchBox = new() { Width = 300, PlaceholderText = "Search models, e.g. tower, door, palm" };
    readonly CheckBox replacedOnly = new() { Text = "Replaced only", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, VirtualMode = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
    readonly ModelPreview preview = new() { Dock = DockStyle.Fill };
    readonly Button replaceButton = new() { Text = "Replace with…", AutoSize = true, Enabled = false };
    readonly Button undoButton = new() { Text = "Use the original", AutoSize = true, Enabled = false };
    readonly Button showReplacement = new() { Text = "Show the replacement", AutoSize = true, Enabled = false };
    readonly Button texturesButton = new() { Text = "Textures && PBR…", AutoSize = true, Enabled = false };
    readonly Button applyButton = new() { Text = "Build the replacements plugin", AutoSize = true };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) };
    List<ModelCatalog.UsedModel> all = [];
    List<ModelCatalog.UsedModel> shown = [];
    List<ModelCatalog.AvailableModel>? available;
    Dictionary<string, string> overrides;

    public ModelsDialog(string outputDir)
    {
        dir = outputDir;
        // A choices file that can't be read opens the window with none rather than failing.
        try { overrides = ModelCatalog.LoadOverrides(dir); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { overrides = new(StringComparer.OrdinalIgnoreCase); status.Text = $"Your model choices couldn't be read ({e.Message})."; }
        Text = "Models — view and replace";
        Width = 1250; Height = 780; MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        preview.LooseFolder = dir;

        list.Columns.Add("Model", 430); list.Columns.Add("Game", 85); list.Columns.Add("Kind", 110); list.Columns.Add("Used by", 65);
        list.Columns.Add("Replaced with", 260);
        list.RetrieveVirtualItem += (_, e) =>
        {
            var m = shown[e.ItemIndex];
            string game = ModelCatalog.GameOf(m.Model) == "Skyrim" ? $"{m.Game} (Skyrim's)" : m.Game;
            e.Item = new ListViewItem([m.Model, game, m.Kind, m.Records.ToString(), overrides.GetValueOrDefault(m.Model) ?? ""]);
        };
        list.SelectedIndexChanged += (_, _) => OnSelect();

        gameBox.Items.AddRange(["All games", "Daggerfall", "Redguard", "Morrowind", "Oblivion"]);
        gameBox.SelectedIndex = 0;
        gameBox.SelectedIndexChanged += (_, _) => Filter();
        searchBox.TextChanged += (_, _) => Filter();
        replacedOnly.CheckedChanged += (_, _) => Filter();

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        filters.Controls.AddRange([gameBox, searchBox, replacedOnly]);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(list); left.Controls.Add(filters);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange([replaceButton, texturesButton, showReplacement, undoButton]);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(preview); right.Controls.Add(buttons);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 760 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        bottom.Controls.AddRange([applyButton, status]);
        Padding = new Padding(10);
        Controls.Add(split); Controls.Add(bottom);

        replaceButton.Click += (_, _) => Replace();
        undoButton.Click += (_, _) => { if (Selected() is { } m) { overrides.Remove(m.Model); Save(); OnSelect(); } };
        showReplacement.Click += (_, _) => { if (Selected() is { } m && overrides.TryGetValue(m.Model, out var to)) preview.ShowModel(to, "(the replacement)"); };
        applyButton.Click += (_, _) => Apply();
        texturesButton.Click += (_, _) =>
        {
            if (Selected() is not { } m) return;
            using var t = new TexturesDialog(dir, m.Model, overrides.GetValueOrDefault(m.Model) ?? m.Model);
            t.ShowDialog(this);
            if (t.ModelChanged) { overrides = ModelCatalog.LoadOverrides(dir); list.Invalidate(); OnSelect(); }
        };
        Shown += async (_, _) => await Load();
    }

    async Task Load()
    {
        status.Text = "Reading the converted plugins (the first time takes a minute)…";
        UseWaitCursor = true;
        try { all = await Task.Run(() => ModelCatalog.Used(dir)); }
        catch (Exception e) { status.Text = $"Couldn't read the plugins in {dir}: {e.Message}"; return; }
        finally { UseWaitCursor = false; }
        Filter();
    }

    void Filter()
    {
        string game = gameBox.SelectedIndex <= 0 ? "" : (string)gameBox.SelectedItem!;
        var words = searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        shown = all.Where(m => (game == "" || m.Game == game)
                               && (!replacedOnly.Checked || overrides.ContainsKey(m.Model))
                               && words.All(w => m.Model.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Example.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Kind.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        list.SelectedIndices.Clear();
        list.VirtualListSize = shown.Count;
        list.Invalidate();
        status.Text = $"{shown.Count:N0} of {all.Count:N0} models · {overrides.Count} replaced";
        OnSelect();
    }

    ModelCatalog.UsedModel? Selected() => list.SelectedIndices.Count == 1 ? shown[list.SelectedIndices[0]] : null;

    void OnSelect()
    {
        var m = Selected();
        replaceButton.Enabled = texturesButton.Enabled = m is not null;
        undoButton.Enabled = showReplacement.Enabled = m is not null && overrides.ContainsKey(m.Model);
        preview.ShowModel(m?.Model, m is null ? null : $"{m.Kind} · used by {m.Records} record(s), e.g. {m.Example} · drag to turn it");
    }

    bool listingModels;

    async void Replace()
    {
        if (Selected() is not { } m || listingModels) return;
        if (available is null)
        {
            // One listing at a time; nothing shown if the window closed meanwhile; a failure is said, not thrown.
            if (GameInstalls.SkyrimData() is not { } skyrim) { status.Text = "Skyrim Special Edition isn't installed through Steam or GOG."; return; }
            status.Text = "Listing the models your games offer…";
            UseWaitCursor = true;
            listingModels = true;
            try { available = await Task.Run(() => ModelCatalog.Available(skyrim, dir)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (!IsDisposed) status.Text = $"The models couldn't be listed ({e.Message}).";
                return;
            }
            finally { listingModels = false; if (!IsDisposed) UseWaitCursor = false; }
            if (IsDisposed) return;
        }
        using var picker = new ModelPickerDialog(available, dir, m.Model);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen is null) return;
        overrides[m.Model] = picker.Chosen;
        Save();
        list.Invalidate();
        OnSelect();
    }

    void Save()
    {
        try
        {
            ModelCatalog.SaveOverrides(dir, overrides);
            status.Text = $"{shown.Count:N0} of {all.Count:N0} models · {overrides.Count} replaced (build the plugin to use them)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = $"Couldn't save your choice ({e.Message}): is the file open elsewhere?"; }
        list.Invalidate();
    }

    async void Apply()
    {
        applyButton.Enabled = false;
        status.Text = "Building TamrielMerge Models.esp…";
        string? skyrim = GameInstalls.SkyrimData();
        try
        {
            int n = await Task.Run(() => ModelCatalog.Apply(dir, skyrim ?? ""));
            status.Text = n > 0
                ? $"{ModelCatalog.PluginName}: {n} record(s) use their replacements. It's in {dir}; enable it after the converted plugins (Install, or Convert, copies it into the game)."
                : "No replacements: the models plugin is removed.";
        }
        catch (Exception e) { status.Text = $"Couldn't build the plugin: {e.Message}"; }
        finally { applyButton.Enabled = true; }
    }
}

/// <summary>Choosing a replacement: every model the user's games offer, by game, searchable, each previewed.</summary>
public sealed class ModelPickerDialog : Form
{
    readonly List<ModelCatalog.AvailableModel> all;
    List<ModelCatalog.AvailableModel> shown = [];
    readonly ComboBox gameBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly TextBox searchBox = new() { Width = 320, PlaceholderText = "Search, e.g. tower, imperialcity, palm" };
    readonly ListView list = new() { View = View.Details, FullRowSelect = true, VirtualMode = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
    readonly ModelPreview preview = new() { Dock = DockStyle.Fill };
    readonly Button useButton = new() { Text = "Use this model", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };

    public string? Chosen { get; private set; }

    public ModelPickerDialog(List<ModelCatalog.AvailableModel> available, string looseDir, string replacing)
    {
        all = available;
        Text = $"Replace {replacing}";
        Width = 1150; Height = 720;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(10);
        preview.LooseFolder = looseDir;
        list.Columns.Add("Model", 520); list.Columns.Add("Game", 100);
        list.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem([shown[e.ItemIndex].Model, shown[e.ItemIndex].Game]);
        list.SelectedIndexChanged += (_, _) =>
        {
            Chosen = list.SelectedIndices.Count == 1 ? shown[list.SelectedIndices[0]].Model : null;
            useButton.Enabled = Chosen is not null;
            preview.ShowModel(Chosen);
        };
        list.DoubleClick += (_, _) => { if (Chosen is not null) { DialogResult = DialogResult.OK; Close(); } };
        gameBox.Items.AddRange(["All games", "Skyrim", "Daggerfall", "Redguard", "Morrowind", "Oblivion"]);
        gameBox.SelectedIndex = 0;
        gameBox.SelectedIndexChanged += (_, _) => Filter();
        searchBox.TextChanged += (_, _) => Filter();

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        filters.Controls.AddRange([gameBox, searchBox]);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(list); left.Controls.Add(filters);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 640 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(preview);
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
        shown = all.Where(m => (game == "" || m.Game == game) && words.All(w => m.Model.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        list.SelectedIndices.Clear();
        list.VirtualListSize = shown.Count;
        list.Invalidate();
        Text = $"Choose a replacement — {shown.Count:N0} models";
    }
}
