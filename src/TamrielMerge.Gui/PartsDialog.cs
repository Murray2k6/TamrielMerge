using TamrielMerge.Setup;

namespace TamrielMerge.Gui;

/// <summary>
/// The tool's own mod organizer (user, 2026-10-10: "can we add our own mod organizer to the tool", chose "TamrielMerge's
/// parts"): each part (Setup/Parts) with a switch, its plugins, size and state. Switching turns its plugins on or off in
/// Skyrim's own plugin list and Mod Organizer 2's profile (PluginList), in the parts' order; nothing in the game folder is
/// deleted. Story and travel joins the others and stays on; a part that isn't installed can't be switched on.
/// </summary>
public sealed class PartsDialog : Form
{
    readonly string dataDir;
    readonly ListView list = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    readonly Label note = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(900, 0) };
    readonly Label description = new() { AutoSize = true, MaximumSize = new Size(900, 0), Font = new Font("Segoe UI", 9.5f) };
    bool loading;

    public PartsDialog(string dataDir)
    {
        this.dataDir = dataDir;
        Text = "TamrielMerge — mod organizer";
        Width = 960; Height = 560; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9.5f);
        MinimizeBox = false;
        list.Columns.Add("Part", 200); list.Columns.Add("Plugins", 430); list.Columns.Add("Size", 90, HorizontalAlignment.Right); list.Columns.Add("State", 180);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "Switch TamrielMerge's parts on or off. Off, a part's plugins are unticked (in Skyrim's own list and Mod Organizer 2's), " +
                   "so nothing of it is used; nothing is deleted, and switching it on again brings it back. The load order is the games' story order.",
        });
        root.Controls.Add(list);
        root.Controls.Add(description);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "Close", Width = 100, Height = 32, DialogResult = DialogResult.OK };
        buttons.Controls.Add(close);
        buttons.Controls.Add(note);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = close;

        list.ItemCheck += (_, e) =>
        {
            if (loading) return;
            var part = (Parts.Part)list.Items[e.Index].Tag!;
            var files = Parts.Files(part, dataDir);
            if (part.Core) { e.NewValue = CheckState.Checked; note.Text = $"{part.Name} joins the other parts together and stays on."; return; }
            if (files.Count == 0) { e.NewValue = CheckState.Unchecked; note.Text = $"{part.Name} isn't installed: convert it (and install) first."; return; }
            bool on = e.NewValue == CheckState.Checked;
            try { Switch(part, files, on, e); }
            // Skyrim's or MO2's plugin list couldn't be written (read-only, or open in LOOT, Vortex or MO2): the tick goes
            // back and the reason is shown (2026-10-14: it threw out of the window).
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                e.NewValue = e.CurrentValue;
                note.Text = $"{part.Name} couldn't be switched {(on ? "on" : "off")}: {ex.Message}. Close Mod Organizer 2 (or LOOT, Vortex) and try again.";
            }
        };
        void Switch(Parts.Part part, List<string> files, bool on, ItemCheckEventArgs e)
        {
            if (on)
            {
                // In the parts' order, so a part switched on goes in after the one before it; a plugin whose game is off
                // (or not installed) stays off, or Skyrim would stop at its missing master.
                var ordered = Parts.All.SelectMany(p => Parts.Files(p, dataDir)).Where(files.Contains).ToList();
                var (ready, blocked) = Parts.Switchable(dataDir, ordered, f => PluginList.IsEnabled(f));
                if (ready.Count == 0) { e.NewValue = CheckState.Unchecked; note.Text = $"{part.Name} needs {string.Join(", ", blocked.Select(b => b.Needs).Distinct())} on first."; return; }
                var done = PluginList.SetEnabled(ready, true);
                note.Text = $"{part.Name} switched on: {string.Join("; ", done)}" +
                            (blocked.Count > 0 ? $". Left off, their game is off: {string.Join(", ", blocked.Select(b => $"{b.Plugin} (needs {b.Needs})"))}" : "");
            }
            else
            {
                // What needs this part goes off with it (the creatures over a game switched off).
                var dependents = Parts.Dependents(dataDir, files).Where(f => PluginList.IsEnabled(f)).ToList();
                var done = PluginList.SetEnabled([.. files, .. dependents], false);
                note.Text = $"{part.Name} switched off: {string.Join("; ", done)}" +
                            (dependents.Count > 0 ? $". Also off, they need it: {string.Join(", ", dependents)}" : "");
                if (dependents.Count > 0) BeginInvoke(Fill);
            }
        }
        list.SelectedIndexChanged += (_, _) =>
            description.Text = list.SelectedItems.Count > 0 ? ((Parts.Part)list.SelectedItems[0].Tag!).Description : "";
        Shown += (_, _) => Fill();
    }

    void Fill()
    {
        loading = true;
        list.Items.Clear();
        foreach (var part in Parts.All)
        {
            var files = Parts.Files(part, dataDir);
            bool on = files.Count > 0 && files.All(f => PluginList.IsEnabled(f));
            string state = files.Count == 0 ? "not installed" : part.Core ? "on (needed)" : on ? "on" : "off";
            var item = new ListViewItem([part.Name, files.Count > 0 ? string.Join(", ", files) : string.Join(", ", part.Plugins), files.Count > 0 ? Size(Parts.Size(part, dataDir)) : "", state])
            {
                Tag = part, Checked = on || (part.Core && files.Count > 0),
                ForeColor = files.Count == 0 ? Color.Gray : SystemColors.WindowText,
            };
            list.Items.Add(item);
        }
        loading = false;
        note.Text = $"Skyrim's Data folder: {dataDir}";
    }

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
