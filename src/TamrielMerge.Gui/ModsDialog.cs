using TamrielMerge.Mods;

namespace TamrielMerge.Gui;

/// <summary>
/// The mods (plugins) found in a game's data folder, to choose which are converted. Ticked are those that will be:
/// at first the official game plus what the game itself has enabled. Ticking a plugin ticks the masters it needs;
/// unticking a master unticks what needs it. Standalone total conversions (a master that doesn't use the base game)
/// are marked, since they bring a whole other world.
/// </summary>
public sealed class ModsDialog : Form
{
    readonly Game game;
    readonly string dataDir;
    readonly List<ModCatalog.Plugin> plugins;
    readonly ListView list = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = false, HideSelection = false,
    };
    readonly Label note = new() { AutoSize = true, ForeColor = Color.DarkGoldenrod, MaximumSize = new Size(860, 0) };
    bool updating;

    public ModsDialog(Game game, string dataDir)
    {
        this.game = game;
        this.dataDir = dataDir;
        plugins = ModCatalog.List(game, dataDir);
        Text = $"{game} mods — TamrielMerge";
        Width = 920; Height = 560; MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(0, 0, 0, 8),
            Text = $"Plugins in {dataDir}. Ticked plugins are converted, in {game}'s load order. Each plugin's masters come with it.",
        });

        list.Columns.Add("Plugin", 220);
        list.Columns.Add("Kind", 90);
        list.Columns.Add("In game", 75);
        list.Columns.Add("Needs", 170);
        list.Columns.Add("Script extender", 150);
        list.Columns.Add("Author", 110);
        list.Columns.Add("Description", 220);
        var selected = new HashSet<string>(ModCatalog.Selected(game, dataDir), StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugins)
        {
            string kind = p.Official ? "official" : p.Standalone(game) ? "standalone" : p.IsMaster ? "mod (master)" : "mod";
            var item = new ListViewItem([p.File, kind, p.EnabledInGame ? "enabled" : "", string.Join(", ", p.Masters), p.Official ? "" : "checking…", p.Author, p.Description.Replace('\n', ' ').Replace('\r', ' ')])
            {
                Tag = p, Checked = selected.Contains(p.File),
                ForeColor = p.Official ? Color.Black : p.Standalone(game) ? Color.DarkGoldenrod : Color.MidnightBlue,
                ToolTipText = p.Description,
            };
            list.Items.Add(item);
        }
        list.ShowItemToolTips = true;
        list.ItemChecked += (_, e) => OnChecked(e.Item);
        layout.Controls.Add(list);
        layout.Controls.Add(note);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var gameList = new Button { Text = "As the game has them", AutoSize = true };
        var official = new Button { Text = "Official only", AutoSize = true };
        var all = new Button { Text = "All", AutoSize = true };
        var ok = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        gameList.Click += (_, _) => Tick(p => p.Official || p.EnabledInGame);
        official.Click += (_, _) => Tick(p => p.Official);
        all.Click += (_, _) => Tick(p => !p.Standalone(game) && !p.Masters.Any(m => plugins.FirstOrDefault(x => x.File.Equals(m, StringComparison.OrdinalIgnoreCase))?.Standalone(game) == true));
        ok.Click += (_, _) => SaveChoice();
        buttons.Controls.AddRange([gameList, official, all, new Label { Width = 20 }, ok, cancel]);
        layout.Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        UpdateNote();
        Shown += async (_, _) => await ScanExtenders();
    }

    readonly Dictionary<string, ScriptExtenders.Use?> extenderUse = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which mods' scripts use the script extender (OBSE / MWSE), checked in the background.</summary>
    async Task ScanExtenders()
    {
        foreach (ListViewItem item in list.Items)
        {
            var p = (ModCatalog.Plugin)item.Tag!;
            if (p.Official) continue;
            var use = await Task.Run(() => ScriptExtenders.Scan(game, Path.Combine(dataDir, p.File)));
            if (IsDisposed) return;
            extenderUse[p.File] = use;
            item.UseItemStyleForSubItems = false;
            item.SubItems[4].Text = use is null ? "none" : $"{use.Extender} ({use.Scripts} of {use.TotalScripts} scripts)";
            if (use is not null) item.SubItems[4].ForeColor = Color.Firebrick;
        }
        UpdateNote();
    }

    void Tick(Func<ModCatalog.Plugin, bool> which)
    {
        updating = true;
        foreach (ListViewItem item in list.Items) item.Checked = which((ModCatalog.Plugin)item.Tag!);
        updating = false;
        UpdateNote();
    }

    /// <summary>Ticking brings the masters; unticking takes away what needs it.</summary>
    void OnChecked(ListViewItem item)
    {
        if (updating) return;
        updating = true;
        var p = (ModCatalog.Plugin)item.Tag!;
        if (item.Checked)
        {
            var need = new Queue<string>(p.Masters);
            while (need.Count > 0)
            {
                string m = need.Dequeue();
                foreach (ListViewItem other in list.Items)
                    if (((ModCatalog.Plugin)other.Tag!).File.Equals(m, StringComparison.OrdinalIgnoreCase) && !other.Checked)
                    {
                        other.Checked = true;
                        foreach (var mm in ((ModCatalog.Plugin)other.Tag!).Masters) need.Enqueue(mm);
                    }
            }
        }
        else
        {
            var gone = new Queue<string>([p.File]);
            while (gone.Count > 0)
            {
                string m = gone.Dequeue();
                foreach (ListViewItem other in list.Items)
                    if (other.Checked && ((ModCatalog.Plugin)other.Tag!).Masters.Contains(m, StringComparer.OrdinalIgnoreCase))
                    {
                        other.Checked = false;
                        gone.Enqueue(((ModCatalog.Plugin)other.Tag!).File);
                    }
            }
        }
        updating = false;
        UpdateNote();
    }

    List<string> Chosen() => list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((ModCatalog.Plugin)i.Tag!).File).ToList();

    void UpdateNote()
    {
        var chosen = Chosen();
        var standalone = plugins.Where(p => p.Standalone(game) && chosen.Contains(p.File)).Select(p => p.File).ToList();
        bool baseGame = chosen.Contains(game == Game.Morrowind ? "Morrowind.esm" : "Oblivion.esm", StringComparer.OrdinalIgnoreCase);
        var extender = chosen.Where(c => extenderUse.TryGetValue(c, out var u) && u is not null).ToList();
        note.Text = (extender.Count > 0 ? $"{string.Join(", ", extender)}: scripted with {ScriptExtenders.Name(game)}. Their records convert, but their scripts don't run yet. " : "")
            + (standalone.Count > 0 ? $"{string.Join(", ", standalone)}: a standalone game with its own world, converted alongside {game}. " : "")
            + (!baseGame ? $"{game}.esm isn't ticked: the {game} story won't have its own world. " : "")
            + $"{chosen.Count} of {plugins.Count} plugins will be converted.";
    }

    void SaveChoice()
    {
        var chosen = Chosen();
        var byDefault = ModCatalog.DefaultSelection(game, dataDir);
        // The game's own list is kept as "default", so newly enabled mods are picked up later.
        bool isDefault = chosen.Count == byDefault.Count && chosen.All(c => byDefault.Contains(c, StringComparer.OrdinalIgnoreCase));
        try { ModCatalog.Save(game, isDefault ? null : chosen); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Your mod choice couldn't be saved ({e.Message}). Is TamrielMerge.mods.json open elsewhere?", "TamrielMerge");
        }
    }
}
