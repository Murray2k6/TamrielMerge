using System.Diagnostics;
using System.Globalization;
using System.Text;
using TamrielMerge.Setup;

namespace TamrielMerge.Gui;

/// <summary>
/// TamrielMerge's window: the games found through Steam or GOG (each can be left out), the tools the converter needs
/// (installable from here; the Creation Kit opened in Steam), the options, and Convert, which runs the command-line
/// converter (TamrielMerge.Cli.exe convert) with them and shows its output as it goes, with a progress bar (from its
/// PROGRESS lines) and Pause / Resume (the whole process tree held where it is). Voice generator runs the converter's
/// voices step on its own: Morrowind's dialogue spoken in Skyrim's voices, days of work, pausable and resumable.
/// </summary>
public sealed class MainForm : Form
{
    static readonly (Game Game, string Label)[] Games =
    [
        (Game.Daggerfall, "Daggerfall"), (Game.Redguard, "Redguard"), (Game.Morrowind, "Morrowind"), (Game.Oblivion, "Oblivion"),
    ];

    readonly string workspace;
    /// <summary>The converter's working folder (tools, output, logs), for the app's error handler.</summary>
    public static string Workspace => FindWorkspace();
    readonly Dictionary<Game, CheckBox> gameBoxes = [];
    readonly FlowLayoutPanel gamesPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
    readonly FlowLayoutPanel toolsPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
    readonly TextBox outputBox = new() { Width = 420 };
    readonly CheckBox voicesBox = new() { Text = "Voices — the games' own voice acting, with lip sync (needs the Creation Kit)", AutoSize = true, Checked = true };
    readonly CheckBox videosBox = new() { Text = "Intro videos — each game's opening cinematic, AI-remastered", AutoSize = true, Checked = true };
    readonly CheckBox modelsBox = new() { Text = "Missing models — seagulls, palms, cacti and more Skyrim lacks, AI-made from Daggerfall's own sprites", AutoSize = true, Checked = true };
    readonly CheckBox gpuBox = new() { Text = "Use the graphics card for the videos and models (much faster, but runs it hot)", AutoSize = true };
    readonly CheckBox installBox = new() { Text = "Install into Skyrim's Data folder when done", AutoSize = true, Checked = true };
    readonly CheckBox creaturesBox = new() { Text = "Procedural creatures — ordinary creatures from all the converted games spawn and respawn in every world (bosses stay put)", AutoSize = true };
    readonly NumericUpDown respawnScale = new() { Minimum = 0.1m, Maximum = 10m, DecimalPlaces = 1, Increment = 0.5m, Value = 1m, Width = 60 };
    readonly Button convertButton = new() { Text = "Convert", Width = 120, Height = 34 };
    readonly Button cancelButton = new() { Text = "Cancel", Width = 100, Height = 34, Enabled = false };
    readonly Button pauseButton = new() { Text = "Pause", Width = 100, Height = 34, Enabled = false };
    readonly Button voicesButton = new() { Text = "Voice generator", Width = 140, Height = 34 };
    readonly Button lodButton = new() { Text = "Distant LOD", Width = 120, Height = 34 };
    readonly Button checkButton = new() { Text = "Check game", Width = 120, Height = 34 };
    // Uninstall (user, 2026-11-02: "i need a uninstall button on prior to older versions of tamreil merge and latest for
    // the skyrim dir"; "add a prompy are you sure for the uninstaller"): this version's and earlier ones' conversions out
    // of Skyrim's Data, after a question that says what goes.
    readonly Button uninstallButton = new() { Text = "Uninstall TamrielMerge", AutoSize = true };
    // The room the games and parts ticked take (user, 2026-11-02: "i need a space requirement of games converted for the
    // tool so it shows a user how much space they need"): measured sizes (Setup/ConversionSize), with each drive's free space.
    readonly Label spaceLabel = new() { AutoSize = true, MaximumSize = new Size(930, 0), Margin = new Padding(0, 4, 0, 4) };
    // Progress (user, 2026-10-10: "the tool needs a progress timer and progress left bar"): the bar with how much is done
    // and left, and under it the time so far, the time left (from the last quarter hour's pace, so a run resumed part-way
    // or a slower step reads true) and when it should be done. A voice run going on without the app shows here too.
    readonly ProgressBar progressBar = new() { Width = 640, Height = 26, Maximum = 1000, Style = ProgressBarStyle.Continuous };
    readonly Label progressLabel = new() { AutoSize = true, ForeColor = Color.DimGray, Text = "" };
    readonly Label timerLabel = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Color.FromArgb(40, 40, 40), Text = "" };
    readonly List<(double At, double P)> samples = [];
    bool watchingBackground;
    DateTime backgroundStarted;
    bool paused;
    DateTime pausedSince;
    TimeSpan pausedTotal;
    double progress;
    readonly Button setupButton = new() { Text = "Install missing tools", AutoSize = true };
    readonly Button ckButton = new() { Text = "Open the Creation Kit in Steam", AutoSize = true };
    readonly Button refreshButton = new() { Text = "Check again", AutoSize = true };
    // Nexus Mods (user, 2026-11-02: "add a login to nexus button on the tool so it can log you in if you are missing
    // something too"): who is logged in, and what a "Get from Nexus" is doing (downloading, or watching Downloads).
    readonly Button nexusButton = new() { Text = "Log in to Nexus", AutoSize = true };
    readonly Label nexusLabel = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 7, 6, 0), MaximumSize = new Size(420, 0) };
    CancellationTokenSource? nexusWork;
    readonly Button modelsButton = new() { Text = "Models…", Width = 110, Height = 34 };
    readonly Button partsButton = new() { Text = "Mod organizer…", Width = 140, Height = 34 };
    readonly Label statusLabel = new() { AutoSize = true, Text = "Ready." };
    readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false,
        Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro,
    };
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    readonly ToolTip tips = new();
    Process? running;
    DateTime started;
    string runningWhat = "";

    public MainForm()
    {
        workspace = FindWorkspace();
        // The converter's working folder: tools, output and the mod selection (TamrielMerge.mods.json) live there.
        Environment.CurrentDirectory = workspace;
        Environment.SetEnvironmentVariable("TAMRIELMERGE_TOOLS", Path.Combine(workspace, "tools"));
        Text = "TamrielMerge — The Elder Scrolls in Skyrim";
        Width = 1320; Height = 960; MinimumSize = new Size(1140, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        // The output folder, remembered (settings "output"; user 2026-10-28: "the tool needs to convert on any drive letter
        // that a user has"): the workspace's out\convert until one is chosen.
        outputBox.Text = PluginList.Setting("output") is { Length: > 0 } savedOut ? savedOut : Path.Combine(workspace, "out", "convert");
        outputBox.Leave += (_, _) => PluginList.SetSetting("output", outputBox.Text.Trim());

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // On the left the tool's logo and what it does (user, 2026-10-09: "on the left of the tool put a readme and
        // logo on this tool on what it does"); everything else to its right.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(Sidebar(), 0, 0);
        outer.Controls.Add(root, 1, 0);
        Controls.Add(outer);

        var header = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        header.Controls.Add(new Label { Text = "TamrielMerge", AutoSize = true, Font = new Font("Segoe UI Semibold", 18f) });
        header.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Color.DimGray,
            Text = "The Elder Scrolls in Skyrim: what it does and how to use it is on the left.",
        });
        root.Controls.Add(header);

        // Games and tools side by side.
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        top.Controls.Add(Group("Games", gamesPanel, fill: true), 0, 0);
        // Scrolls: the tools' rows outgrew the 210-pixel row (the last rows and the buttons under them were cut off).
        var toolsGroup = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        // The Nexus login first, where it is seen without scrolling down the tools.
        var nexusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        nexusRow.Controls.AddRange([nexusLabel, nexusButton]);
        toolsGroup.Controls.Add(nexusRow);
        toolsGroup.Controls.Add(toolsPanel);
        var toolButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        toolButtons.Controls.AddRange([setupButton, ckButton, refreshButton]);
        toolsGroup.Controls.Add(toolButtons);
        top.Controls.Add(Group("Tools", toolsGroup, fill: true), 1, 0);
        root.Controls.Add(top);

        // Options and the buttons.
        var options = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        var outRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var browse = new Button { Text = "Browse…", AutoSize = true };
        outRow.Controls.AddRange([new Label { Text = "Output folder:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, outputBox, browse]);
        options.Controls.Add(outRow);
        // Mod Organizer 2 (optional): installing switches the plugins on in its profile too (Skyrim's own list always).
        var mo2Row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var mo2Box = new TextBox { Width = outputBox.Width, Text = PluginList.Mo2Folder ?? "" };
        var mo2Browse = new Button { Text = "Browse…", AutoSize = true };
        mo2Row.Controls.AddRange([new Label { Text = "Mod Organizer 2:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, mo2Box, mo2Browse]);
        options.Controls.Add(mo2Row);
        // Where a conversion is staged and where temporary files go (settings "build" and "temp", read by the converter
        // through Util.Paths): any drive the user has; empty for the fastest solid-state drive and the user's own temp.
        foreach (var (key, label, tip, desc) in new[]
        {
            ("build", "Build folder:", "Where a conversion is staged while it runs (a fast drive with room: 40 GB and more for Daggerfall); empty builds in the output folder itself", "Where a conversion is staged while it runs"),
            ("temp", "Temp folder:", "Where temporary files go (the voices, models and videos on the way); empty uses the tool's own updates folder", "Where temporary files go"),
        })
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var box = new TextBox { Width = outputBox.Width, Text = PluginList.Setting(key) ?? "" };
            var pick = new Button { Text = "Browse…", AutoSize = true };
            row.Controls.AddRange([new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, box, pick]);
            options.Controls.Add(row);
            tips.SetToolTip(box, tip);
            string k = key, d = desc;
            box.Leave += (_, _) => PluginList.SetSetting(k, box.Text.Trim());
            pick.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog { SelectedPath = box.Text, Description = d };
                if (dialog.ShowDialog(this) == DialogResult.OK) { box.Text = dialog.SelectedPath; PluginList.SetSetting(k, box.Text); }
            };
        }
        tips.SetToolTip(mo2Box, "If you play through Mod Organizer 2: its folder, so installing switches the plugins on in its profile too (leave empty otherwise)");
        mo2Box.Leave += (_, _) => PluginList.Mo2Folder = mo2Box.Text.Trim();
        mo2Browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = mo2Box.Text, Description = "Mod Organizer 2's folder (where ModOrganizer.exe is)" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { mo2Box.Text = dialog.SelectedPath; PluginList.Mo2Folder = mo2Box.Text; }
        };
        options.Controls.AddRange([voicesBox, videosBox, modelsBox, gpuBox, creaturesBox]);
        // Respawn times are each game's own; this scales them (0.5 = twice as often).
        var respawnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(18, 0, 0, 0) };
        respawnRow.Controls.AddRange([new Label { Text = "Respawn time × (each game's own times):", AutoSize = true, Margin = new Padding(0, 5, 4, 0) }, respawnScale]);
        options.Controls.Add(respawnRow);
        respawnScale.Enabled = false;
        creaturesBox.CheckedChanged += (_, _) => respawnScale.Enabled = creaturesBox.Checked;
        // Uninstall beside Install (the action buttons' row is full: it was off the window's edge there).
        var installRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        installBox.Margin = new Padding(3, 7, 12, 0);
        installRow.Controls.AddRange([installBox, uninstallButton]);
        options.Controls.Add(installRow);
        options.Controls.Add(spaceLabel);
        foreach (var b in new[] { voicesBox, videosBox, modelsBox, installBox }) b.CheckedChanged += (_, _) => UpdateSpace();
        outputBox.TextChanged += (_, _) => UpdateSpace();
        // Wraps onto a second line: in one, Mod organizer and the status were past the window's edge (2026-11-02 render).
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(930, 0), Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange([convertButton, voicesButton, lodButton, checkButton, pauseButton, cancelButton, modelsButton, partsButton, statusLabel]);
        tips.SetToolTip(uninstallButton, "Take TamrielMerge's conversions out of Skyrim's Data folder (this version's and older versions'), and their plugins off the plugin lists; asks first, saying what goes. The output folder is kept.");
        uninstallButton.Click += (_, _) => Uninstall();
        tips.SetToolTip(checkButton, "Start Skyrim through SKSE with Papyrus logging and a crash logger, then report what went wrong: how far it loaded, what a crash happened in (which plugin and record), and TamrielMerge's script errors");
        tips.SetToolTip(lodButton, "Make the distant view of the converted worlds again: the land, the buildings and big rocks, and the trees, as Skyrim shows its own far off");
        tips.SetToolTip(partsButton, "Switch TamrielMerge's parts on or off (each game, the voices, the main menu and music, models, creatures)");
        partsButton.Click += (_, _) =>
        {
            if (GameInstalls.SkyrimData() is not { } data) { MessageBox.Show(this, "Skyrim Special Edition isn't installed through Steam or GOG.", "TamrielMerge"); return; }
            using var dialog = new PartsDialog(data);
            dialog.ShowDialog(this);
        };
        tips.SetToolTip(modelsButton, "View the converted models turning round, and replace any with a model from any of your games");
        tips.SetToolTip(voicesButton, "Speak Morrowind's dialogue in Skyrim's own voices (it has none recorded): runs for days, on the graphics card and the CPU together; pause any time, and it resumes where it stopped");
        tips.SetToolTip(pauseButton, "Hold the conversion where it is (every process it started); Resume carries on");
        statusLabel.Margin = new Padding(12, 10, 0, 0);
        options.Controls.Add(actions);
        var progressRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        progressLabel.Margin = new Padding(8, 3, 0, 0);
        progressRow.Controls.AddRange([progressBar, progressLabel]);
        options.Controls.Add(progressRow);
        timerLabel.Margin = new Padding(0, 4, 0, 0);
        options.Controls.Add(timerLabel);
        root.Controls.Add(Group("Options", options));

        root.Controls.Add(Group("Log", log, fill: true));

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = outputBox.Text, Description = "Where the converted plugins and files go" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { outputBox.Text = dialog.SelectedPath; PluginList.SetSetting("output", outputBox.Text); }
        };
        gpuBox.CheckedChanged += (_, _) =>
        {
            if (gpuBox.Checked && MessageBox.Show(this,
                    "Remastering the intro videos and making the models on the graphics card keeps it under load for a long time and can make it run very hot (the model maker waits whenever it passes 78 C).\n\nUse the graphics card anyway?",
                    "TamrielMerge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                gpuBox.Checked = false;
        };
        videosBox.CheckedChanged += (_, _) => gpuBox.Enabled = videosBox.Checked || modelsBox.Checked;
        modelsBox.CheckedChanged += (_, _) => gpuBox.Enabled = videosBox.Checked || modelsBox.Checked;
        refreshButton.Click += (_, _) => RefreshStatus();
        tips.SetToolTip(nexusButton, "Log in to Nexus Mods with your personal API key, so the tool can get what is only published there (the Skyrim voice models)");
        nexusButton.Click += (_, _) =>
        {
            if (!Nexus.LoggedIn) { ShowNexusLogin(); return; }
            nexusWork?.Cancel();
            Nexus.LogOut();
            Append("Logged out of Nexus Mods: the key is deleted.");
            ShowNexusState();
            RefreshStatus();
        };
        FormClosed += (_, _) => nexusWork?.Cancel();
        ShowNexusState();
        ckButton.Click += (_, _) => OpenCreationKitInSteam();
        setupButton.Click += (_, _) => Run("Installing tools", ["setup", .. gpuBox.Checked ? Array.Empty<string>() : ["--cpu"]]);
        convertButton.Click += (_, _) => Convert();
        modelsButton.Click += (_, _) =>
        {
            if (!Directory.Exists(outputBox.Text) || !Directory.EnumerateFiles(outputBox.Text, "*.esm").Any())
            { MessageBox.Show(this, "Convert first: the models are listed from the converted plugins in the output folder.", "TamrielMerge"); return; }
            using var dialog = new ModelsDialog(outputBox.Text);
            dialog.ShowDialog(this);
        };
        cancelButton.Click += (_, _) => CancelRun();
        pauseButton.Click += (_, _) => TogglePause();
        voicesButton.Click += (_, _) => GenerateVoices();
        lodButton.Click += (_, _) => GenerateLod();
        checkButton.Click += (_, _) => Run("Checking the game", ["game-check", "--prepare", "--launch"], false);
        clock.Tick += (_, _) =>
        {
            if (running is null) { WatchBackground(); return; }
            var took = Elapsed();
            statusLabel.Text = paused ? $"Paused — {runningWhat}" : $"{runningWhat}…";
            timerLabel.Text = TimerText(took, paused);
        };
        clock.Start();
        FormClosing += (_, e) =>
        {
            if (running is null) return;
            if (MessageBox.Show(this, "A conversion is still running. Stop it and close?", "TamrielMerge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
            else CancelRun();
        };
        Shown += (_, _) =>
        {
            ActiveControl = convertButton;
            // TamrielMerge.exe --mods <Game> opens that game's mod list straight away.
            var argv = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(argv, a => a.Equals("--mods", StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && at + 1 < argv.Length && Enum.TryParse<Game>(argv[at + 1], true, out var g) && GameInstalls.DataDir(g) is { } dir && Mods.ModCatalog.Supports(g))
            {
                using var dialog = new ModsDialog(g, dir);
                if (dialog.ShowDialog(this) == DialogResult.OK) RefreshStatus();
            }
        };
        RefreshStatus();
    }

    /// <summary>The tool's logo (tools\menu\logo_source.png, built into the program), or null.</summary>
    static Image? Logo()
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream("TamrielMerge.logo.png");
        return s is null ? null : Image.FromStream(s);
    }

    /// <summary>The left column: the logo, and what the tool does and how to use it.</summary>
    Control Sidebar()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 12, 4, 12), BackColor = Color.FromArgb(246, 245, 243) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 290));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (Logo() is { } logo)
        {
            panel.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            using var small = new Bitmap(logo, 64, 64);
            Icon = Icon.FromHandle(small.GetHicon());
        }
        var readme = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = panel.BackColor,
            ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, TabStop = false, Cursor = Cursors.Default,
        };
        var body = new Font("Segoe UI", 9f);
        var heading = new Font("Segoe UI Semibold", 10.5f);
        // Typed in at the end (SelectedText keeps the font and colour set for it; AppendText drops them), a blank line
        // after a paragraph or a list's last item, without a bullet.
        void Add(string text, Font font, Color colour, bool bullet, bool gap)
        {
            readme.SelectionStart = readme.TextLength;
            readme.SelectionLength = 0;
            readme.SelectionFont = font;
            readme.SelectionColor = colour;
            readme.SelectionBullet = bullet;
            readme.SelectionIndent = bullet ? 4 : 0;
            readme.SelectedText = text + "\n";
            if (!gap) return;
            readme.SelectionStart = readme.TextLength;
            readme.SelectionBullet = false;
            readme.SelectionIndent = 0;
            readme.SelectionFont = body;
            readme.SelectedText = "\n";
        }
        void Heading(string text) => Add(text, heading, Color.FromArgb(150, 20, 20), false, false);
        void Para(string text) => Add(text, body, Color.FromArgb(40, 40, 40), false, true);
        void Bullet(string text, bool last = false) => Add(text, body, Color.FromArgb(40, 40, 40), true, last);

        // Filled once the box exists: formatting given before its window is made is lost (the headings came out plain).
        void Fill()
        {
            Heading("What it does");
            Para("TamrielMerge turns the Elder Scrolls games you own into one Skyrim Special Edition playthrough. Daggerfall, " +
                 "Redguard, Morrowind and Oblivion become worlds inside Skyrim, played in story order before Skyrim's own: " +
                 "Redguard (2E 864), Daggerfall (3E 405), Morrowind (3E 427), Oblivion (3E 433), then Skyrim (4E 201). " +
                 "The Travel Through Time power takes you between them.");
            Heading("What it converts");
            Bullet("each game's land, towns, dungeons and interiors, with Skyrim's stairs and paths for its people");
            Bullet("people and creatures, their dialogue in Skyrim's dialogue menu, voiced");
            Bullet("voices the games never recorded are made for them, as a mod of their own (TamrielMerge Voices): switch it off to have the games as they were");
            Bullet("items, factions, music and the games' intro videos, remastered");
            Bullet("each game's classes, attributes, birthsigns, races, skills and level-ups on the Mundus stones (the Tamriel menu's Passives tab), and Daggerfall's climbing");
            Bullet("a main menu of its own: a flyby over the games you converted, with its own theme", last: true);
            Heading("What you need");
            Bullet("Skyrim Special Edition with SKSE and Address Library for SKSE Plugins");
            Bullet("for the climbing animation (Daggerfall) and Morrowind's and Oblivion's people moving with their own games' animations, Open Animation Replacer by Ershin: without it you climb in the falling pose and those people move as Skyrim's do");
            Bullet("any of the games above from Steam or GOG: those not installed are left out, and the rest still work");
            Bullet("for voices, the Creation Kit (free on Steam); for the videos, models and menu, a graphics card", last: true);
            Heading("How to use it");
            Bullet("Install missing tools: fetches what the conversion needs (listed under Tools).");
            Bullet("Tick the games to convert, and the options you want.");
            Bullet("Convert. The plugins and files go to the output folder; Install puts them into Skyrim's Data folder.");
            Bullet("Start a new game in Skyrim: the first story begins with its intro (watch it or skip it).", last: true);
            Para("Everything is built on your machine from your own copies of the games. Their folders are only read, never changed.");
            readme.SelectionStart = 0;
        }
        readme.HandleCreated += (_, _) => Fill();
        panel.Controls.Add(readme, 0, 1);
        return panel;
    }

    static GroupBox Group(string title, Control content, bool fill = false)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(10), AutoSize = !fill, Margin = new Padding(0, 0, 8, 8) };
        content.Dock = DockStyle.Fill;
        g.Controls.Add(content);
        return g;
    }

    /// <summary>The folder holding tools\ and out\: the source tree when run from it (TamrielMerge.slnx above), else the
    /// program's own folder.</summary>
    static string FindWorkspace()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TamrielMerge.slnx"))) return dir.FullName;
        return AppContext.BaseDirectory.TrimEnd('\\');
    }

    /// <summary>Finds the games and checks the tools in the background (the Python check takes a few seconds), then
    /// shows them.</summary>
    async void RefreshStatus()
    {
        // One at a time (a Get from Nexus asks for one after each import): two at once each cleared the lists and both
        // filled them, so a call while one runs makes it run once more at its end instead.
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        refreshButton.Enabled = false;
        // The games first (found in milliseconds), the tools when their check is done: the games no longer wait on the
        // tools (user, 2026-10-10: "why was the detected games list empty in the tool"; no placeholders).
        var (found, skyrim, mods) = await Task.Run(() =>
        {
            var f = GameInstalls.Detect();
            string? s = GameInstalls.SkyrimData();
            var m = new Dictionary<Game, (int Selected, int Total)>();
            foreach (var (g, install) in f)
                if (Mods.ModCatalog.Supports(g))
                    try { m[g] = (Mods.ModCatalog.Selected(g, install.DataDir).Count, Mods.ModCatalog.List(g, install.DataDir).Count); } catch { }
            return (f, s, m);
        });

        gamesPanel.Controls.Clear();
        var skyrimLine = StatusLine(skyrim is not null, "Skyrim Special Edition", skyrim is not null ? "found" : "not installed through Steam or GOG — required");
        if (skyrim is not null) foreach (Control c in skyrimLine.Controls) tips.SetToolTip(c, skyrim);
        gamesPanel.Controls.Add(skyrimLine);
        foreach (var (game, label) in Games)
        {
            bool has = found.TryGetValue(game, out var install);
            if (!gameBoxes.TryGetValue(game, out var box)) { gameBoxes[game] = box = new CheckBox { AutoSize = true, Checked = true }; box.CheckedChanged += (_, _) => UpdateSpace(); }
            box.Text = has ? $"{label}   ({install!.Store})" : $"{label}   (not installed)";
            tips.SetToolTip(box, has ? install!.DataDir : "Not found in Steam or GOG: this story is left out.");
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            row.Controls.Add(box);
            if (has && mods.TryGetValue(game, out var count))
            {
                var link = new LinkLabel { Text = $"Mods ({count.Selected} of {count.Total})", AutoSize = true, Margin = new Padding(8, 5, 0, 0) };
                string dataDir = install!.DataDir;
                link.LinkClicked += (_, _) =>
                {
                    using var dialog = new ModsDialog(game, dataDir);
                    if (dialog.ShowDialog(this) == DialogResult.OK) RefreshStatus();
                };
                tips.SetToolTip(link, $"Choose which of {label}'s plugins (official content and installed mods) are converted.");
                row.Controls.Add(link);
            }
            box.Enabled = has;
            if (!has) box.Checked = false;
            gamesPanel.Controls.Add(row);
        }

        var (tools, ckMissing) = await Task.Run(() => (Dependencies.Check(skyrim ?? ""), Dependencies.CreationKitMissing(skyrim ?? "").Count > 0));
        toolsPanel.Controls.Clear();
        // Nexus rows first, under the Nexus login, with their "Get from Nexus" in sight.
        foreach (var st in tools.OrderBy(t => t.Source is null))
        {
            // A Nexus row says how much of it is there (the voice types with a model), and when it isn't all there, how
            // to get it: from Nexus with the login, or the login first.
            Control line = StatusLine(st.Ready, st.Name, st.Source is not null ? st.Detail.Split("; ")[0] : st.Ready ? "ready" : st.Name.StartsWith("Creation Kit") ? "not installed (free on Steam)" : "missing");
            if (st.Source is not null) foreach (Control c in line.Controls) tips.SetToolTip(c, st.Detail.Replace("; ", "\n"));
            if (st.Source is { } source && !st.Ready)
            {
                var get = new LinkLabel
                {
                    AutoSize = true, Margin = new Padding(22, 0, 0, 4),
                    Text = nexusWork is not null ? "Stop getting from Nexus" : Nexus.LoggedIn ? "Get from Nexus" : "Log in to Nexus first",
                };
                tips.SetToolTip(get, $"{source.Title}: Nexus Mods {source.Game} mod {source.ModId} ({source.FilesPage})");
                get.LinkClicked += (_, _) => { if (nexusWork is not null) nexusWork.Cancel(); else GetFromNexus(source); };
                // On a line of its own under the row: beside it, it went past the Tools box's right edge.
                var both = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                both.Controls.AddRange([line, get]);
                line = both;
            }
            toolsPanel.Controls.Add(line);
        }
        ckButton.Visible = ckMissing;
        // Nexus rows aren't setup's to install: they have their own link.
        setupButton.Enabled = running is null && tools.Any(t => !t.Ready && !t.Name.StartsWith("Creation Kit") && t.Source is null);
        convertButton.Enabled = running is null && skyrim is not null;
        uninstallButton.Enabled = running is null && skyrim is not null;
        UpdateSpace();
        refreshButton.Enabled = true;
        refreshing = false;
        if (refreshAgain && !IsDisposed) { refreshAgain = false; RefreshStatus(); }
    }

    bool refreshing, refreshAgain;

    static FlowLayoutPanel StatusLine(bool ok, string name, string detail)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.Add(new Label { Text = ok ? "✔" : "✖", AutoSize = true, ForeColor = ok ? Color.SeaGreen : Color.Firebrick, Font = new Font("Segoe UI Symbol", 10f, FontStyle.Bold) });
        row.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(4, 2, 6, 0) });
        row.Controls.Add(new Label { Text = detail, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 2, 0, 0), MaximumSize = new Size(380, 0) });
        return row;
    }

    void OpenCreationKitInSteam()
    {
        try { Process.Start(new ProcessStartInfo($"steam://nav/games/details/{Dependencies.CreationKitAppId}") { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Couldn't open Steam. Install \"Skyrim Special Edition: Creation Kit\" from your Steam Library (Tools).", "TamrielMerge"); return; }
        MessageBox.Show(this, "Steam is showing \"Skyrim Special Edition: Creation Kit\" in your Library.\n\n" +
            "1. Click Install there.\n2. When it's installed, start the Creation Kit once (it unpacks its tools and scripts), then close it.\n3. Click \"Check again\" here.",
            "TamrielMerge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>The Nexus line: who is logged in (from the last check with Nexus), and the button to log in or out.</summary>
    void ShowNexusState()
    {
        if (nexusWork is not null) return;   // a Get from Nexus is saying what it is doing
        var user = Nexus.StoredUser;
        nexusLabel.ForeColor = user is null ? Color.DimGray : Color.SeaGreen;
        nexusLabel.Text = user is null ? "Nexus Mods: not logged in" : $"Logged in to Nexus as {user.Name} ({(user.Premium ? "premium" : "free")})";
        nexusButton.Text = user is null ? "Log in to Nexus" : "Log out";
    }

    /// <summary>The login dialog; true when it logged in.</summary>
    bool ShowNexusLogin()
    {
        using var dialog = new NexusLoginDialog();
        bool ok = dialog.ShowDialog(this) == DialogResult.OK && dialog.User is not null;
        if (ok) Append($"Logged in to Nexus Mods as {dialog.User!.Name} ({(dialog.User.Premium ? "premium" : "free account")}).");
        ShowNexusState();
        RefreshStatus();
        return ok;
    }

    /// <summary>
    /// Get from Nexus, for a requirement only published there. Logged out, the login comes first. The key is checked
    /// again (Premium may have started or lapsed). Premium: the mod's files are listed through Nexus's API and those still
    /// wanted downloaded and imported. Free account: Nexus forbids API downloads for it, so the mod's files page opens in
    /// the browser and the Downloads folder is watched for 30 minutes, each archive from the mod imported as it lands.
    /// Either stops once the requirement is met, or on "Stop getting from Nexus".
    /// </summary>
    async void GetFromNexus(NexusSource source)
    {
        if (nexusWork is not null) return;
        if (!Nexus.LoggedIn && !ShowNexusLogin()) return;
        nexusWork = new CancellationTokenSource();
        var ct = nexusWork.Token;
        RefreshStatus();   // the link becomes "Stop getting from Nexus"
        // Each step in the log; the rows re-checked so the count of what is there follows the imports.
        var report = new Progress<string>(line => { Append($"Nexus: {line}"); if (!IsDisposed) RefreshStatus(); });
        try
        {
            nexusLabel.ForeColor = Color.DimGray;
            nexusLabel.Text = "Checking your Nexus account…";
            Nexus.User user;
            try { user = await Nexus.CheckLogin(ct); }
            catch (Nexus.NexusException e) when (e.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                // Revoked or regenerated on Nexus's site since the login: log in again.
                Nexus.LogOut();
                MessageBox.Show(this, e.Message, "TamrielMerge — Nexus Mods", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                nexusLabel.Text = "";
                if (!ShowNexusLogin() || Nexus.StoredUser is not { } again) return;
                user = again;
            }
            if (user.Premium)
            {
                Append($"Nexus: getting {source.Title} (mod {source.ModId}) as {user.Name} (premium)");
                var progress = new Progress<(string File, long Done, long? Total)>(p =>
                    nexusLabel.Text = p.Total is > 0 ? $"Downloading {p.File}: {p.Done * 100 / p.Total.Value}% of {p.Total.Value / 1048576.0:N0} MB"
                                                     : $"Downloading {p.File}: {p.Done / 1048576.0:N0} MB");
                var installed = await Nexus.Fetch(source, report, progress, ct);
                Append($"Nexus: {source.Title}: {(installed.Count > 0 ? $"installed {installed.Count} ({string.Join(", ", installed)})" : "nothing new installed")}.");
            }
            else
            {
                // Listing works for free accounts too: it names the files to download from the page.
                List<Nexus.ModFile>? files = null;
                try
                {
                    var picked = (await Nexus.FilesOf(source.Game, source.ModId, ct)).Where(source.Picks).ToList();
                    files = picked;
                    if ((source.Wanted?.Invoke(picked) ?? picked) is { Count: > 0 } wanted)
                        Append($"Nexus: the files to download: {string.Join(", ", wanted.Select(f => f.Name))}");
                }
                catch (Nexus.NexusException e) when (e.Status != System.Net.HttpStatusCode.Unauthorized) { Append($"Nexus: couldn't list the mod's files ({e.Message}); any archive from mod {source.ModId} is imported."); }
                try { Process.Start(new ProcessStartInfo(source.FilesPage) { UseShellExecute = true }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Append($"Nexus: couldn't open the browser ({e.Message}): go to {source.FilesPage}"); }
                string downloads = Nexus.DownloadsFolder();
                var until = DateTime.Now.AddMinutes(30);
                Append($"Nexus: free accounts can't download through Nexus's API, so {source.Title}' files page is open in your browser: " +
                       $"click Manual download on each file. TamrielMerge watches {downloads} until {until:HH:mm} and imports each archive from mod {source.ModId} as it lands " +
                       "(those already there too).");
                nexusLabel.ForeColor = Color.DimGray;
                nexusLabel.Text = $"Watching Downloads for {source.Title} until {until:HH:mm}: download the files on the page that opened";
                var installed = await Nexus.WatchDownloads(source, files, until - DateTime.Now, report, ct);
                Append(source.Ready() ? $"Nexus: {source.Title}: all there now ({installed.Count} imported)."
                    : $"Nexus: stopped watching Downloads ({installed.Count} imported{(installed.Count > 0 ? ": " + string.Join(", ", installed) : "")}); Get from Nexus again to carry on.");
            }
        }
        catch (OperationCanceledException) { Append($"Nexus: getting {source.Title} stopped."); }
        catch (Nexus.NexusException e)
        {
            Append($"Nexus: {e.Message}");
            if (!IsDisposed) MessageBox.Show(this, e.Message, "TamrielMerge — Nexus Mods", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An archive that didn't unpack or held nothing usable, or a folder that couldn't be written.
            Append($"Nexus: {source.Title} couldn't be installed: {e.Message}");
            if (!IsDisposed) MessageBox.Show(this, e.Message, "TamrielMerge — Nexus Mods", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            nexusWork?.Dispose();
            nexusWork = null;
            if (!IsDisposed) { ShowNexusState(); RefreshStatus(); }
        }
    }

    void Convert()
    {
        string? skyrim = GameInstalls.SkyrimData();
        if (skyrim is null) { MessageBox.Show(this, "Skyrim Special Edition isn't installed through Steam or GOG.", "TamrielMerge"); return; }
        var skip = gameBoxes.Where(kv => !kv.Value.Checked).Select(kv => kv.Key.ToString()).ToList();
        if (!videosBox.Checked) skip.Add("Videos");
        if (!modelsBox.Checked) skip.Add("Models");
        bool voices = voicesBox.Checked;
        // The Creation Kit before anything is converted.
        if (voices && Dependencies.CreationKitMissing(skyrim).Count > 0)
        {
            var answer = MessageBox.Show(this,
                "The Skyrim Special Edition Creation Kit isn't installed. TamrielMerge needs it for the voices and their lip sync.\n\n" +
                "Yes — open it in Steam to install it first\nNo — convert now, without voices\nCancel — do nothing",
                "Creation Kit needed", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes) { OpenCreationKitInSteam(); return; }
            voices = false;
        }
        if (!voices) skip.Add("Voices");
        var args = new List<string> { "convert", "--out", outputBox.Text, "--no-prompt" };
        if (skip.Count > 0) { args.Add("--skip"); args.Add(string.Join(",", skip)); }
        if (installBox.Checked) args.Add("--install");
        if (gpuBox.Checked) args.Add("--gpu");
        if (creaturesBox.Checked)
        {
            args.Add("--procedural-creatures");
            args.Add("--respawn-scale");
            args.Add(respawnScale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        Run("Converting", args);
    }

    TimeSpan Elapsed() => DateTime.Now - started - pausedTotal - (paused ? DateTime.Now - pausedSince : TimeSpan.Zero);

    static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("F1", CultureInfo.InvariantCulture) + " GB";

    /// <summary>The space line: what the games and parts ticked take in the output folder and in Skyrim's Data, the room
    /// a conversion needs beyond that while it runs (measured on this computer's last conversion of those games), and
    /// whether the drives have it.</summary>
    void UpdateSpace()
    {
        var games = gameBoxes.Where(kv => kv.Value.Checked && kv.Value.Enabled).Select(kv => kv.Key.ToString()).ToList();
        if (games.Count == 0) { spaceLabel.Text = "Space: tick a game to see how much room its conversion takes."; spaceLabel.ForeColor = Color.DimGray; return; }
        bool voices = voicesBox.Checked, videos = videosBox.Checked, models = modelsBox.Checked;
        var all = ConversionSize.Estimate(games, voices, videos, models);
        // What every conversion shares, counted once; each game's own on top.
        long shared = ConversionSize.Measured.Where(kv => kv.Key.Game == "Shared" && (kv.Key.Part != ConversionSize.Part.Videos || videos)).Sum(kv => kv.Value.Output);
        var each = games.Select(g => $"{g} {Gb(ConversionSize.Estimate([g], voices, videos, models).Output - shared)[..^3]}");
        long? overhead = ConversionSize.MeasuredOverhead(games);
        var text = new StringBuilder($"Space needed: {Gb(all.Output)} in the output folder ({string.Join(", ", each)}, shared {Gb(shared)[..^3]})");
        if (installBox.Checked) text.Append($" · {Gb(all.Installed)} in Skyrim's Data");
        text.Append(overhead is { } o ? $" · up to {Gb(o)} more while converting (your last conversion of these games)."
            : " · more while converting (the first conversion measures it).");
        // Each drive's free space against what it is asked for (the output's and Skyrim's on one drive add up).
        var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Add(string? folder, long bytes)
        {
            try { if (folder is { Length: > 0 } && Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } r) need[r] = need.GetValueOrDefault(r) + bytes; }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        Add(outputBox.Text, all.Output + (overhead ?? 0));
        if (installBox.Checked) Add(GameInstalls.SkyrimData(), all.Installed);
        bool tooLittle = false;
        foreach (var (root, bytes) in need)
            try
            {
                long free = new DriveInfo(root).AvailableFreeSpace;
                text.Append($"  {root.TrimEnd('\\')} {Gb(free)} free{(free < bytes ? $" ({Gb(bytes - free)} too little)" : "")}.");
                tooLittle |= free < bytes;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        spaceLabel.Text = text.ToString();
        spaceLabel.ForeColor = tooLittle ? Color.Firebrick : Color.FromArgb(40, 40, 40);
    }

    /// <summary>TamrielMerge's files found in Skyrim's Data, said, and removed only if the user says yes.</summary>
    async void Uninstall()
    {
        if (running is not null) return;
        if (GameInstalls.SkyrimData() is not { } data) { MessageBox.Show(this, "Skyrim Special Edition isn't installed through Steam or GOG.", "TamrielMerge"); return; }
        uninstallButton.Enabled = false;
        statusLabel.Text = "Looking for TamrielMerge's files in Skyrim's Data…";
        Skyrim.Uninstaller.Plan plan;
        List<string> kinds;
        try { (plan, kinds) = await Task.Run(() => { var p = Skyrim.Uninstaller.Find(data); return (p, Skyrim.Uninstaller.Describe(p)); }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Skyrim's Data folder couldn't be read: {e.Message}", "TamrielMerge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { uninstallButton.Enabled = running is null; statusLabel.Text = "Ready."; }
        if (plan.Files.Count == 0 && plan.Plugins.Count == 0 && plan.Outside.Count == 0) { MessageBox.Show(this, $"Nothing of TamrielMerge's is installed in\n{data}", "TamrielMerge"); return; }
        if (Process.GetProcessesByName("SkyrimSE").Length > 0) { MessageBox.Show(this, "Skyrim is running: close it first, then uninstall.", "TamrielMerge", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string question = "Are you sure you want to uninstall TamrielMerge from Skyrim?\n\n"
            + $"This removes {plan.Files.Count + plan.Outside.Count:N0} files ({Skyrim.Uninstaller.Size(plan.Bytes)}) that this version or an older one of TamrielMerge put in\n{data},\n"
            + $"and takes its {plan.Plugins.Count} plugins off the plugin lists:\n\n"
            + string.Join("\n", kinds.Select(k => "•  " + k))
            + "\n\nThe plugins come off Skyrim's Plugins.txt and loadorder.txt and Mod Organizer 2's profiles (close Mod Organizer 2 first). Saved games made with them will miss them. "
            + "The converted files in your output folder are kept.\n\nThis can't be undone.";
        if (MessageBox.Show(this, question, "Uninstall TamrielMerge?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        // Crash Logger SSE is another author's SKSE plugin: the game check installs it, but it may have been there before.
        bool crashLogger = Skyrim.Uninstaller.HasCrashLogger(data)
            && MessageBox.Show(this, "Remove Crash Logger SSE too?\n\nTamrielMerge's game check installs it (SKSE\\Plugins\\CrashLogger.dll), but you may have had it before, or use it for other mods. "
                + "Its crash logs in Documents are kept either way.", "Uninstall TamrielMerge", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        Run("Uninstalling", ["uninstall", "--yes", "--data", data, .. crashLogger ? new[] { "--crash-logger" } : []], false);
    }

    static string Remaining(double seconds) =>
        seconds > 86400 * 2 ? $"{seconds / 86400:F1} days"
        : seconds >= 3600 ? $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00} min"
        : $"{Math.Max(1, (int)Math.Ceiling(seconds / 60))} min";

    static string Clock(TimeSpan t) => t.TotalHours >= 24 ? $"{(int)t.TotalDays} d {t:hh\\:mm\\:ss}" : t.ToString(@"hh\:mm\:ss");

    /// <summary>The timer line: time so far, time left and when it should be done.</summary>
    string TimerText(TimeSpan took, bool isPaused)
    {
        string left = isPaused ? "paused" : SecondsLeft(took.TotalSeconds) is { } sec
            ? $"about {Remaining(sec)} left   ·   done around {DateTime.Now.AddSeconds(sec).ToString(sec > 20 * 3600 ? "ddd HH:mm" : "HH:mm")}"
            : "working out the time left";
        return $"⏱ {Clock(took)} so far   ·   {left}";
    }

    /// <summary>The seconds left at the pace of the last quarter hour (at least a minute of it), or null.</summary>
    double? SecondsLeft(double now)
    {
        var recent = samples.Where(x => now - x.At <= 900).ToList();
        if (recent.Count < 2 || recent[^1].At - recent[0].At < 60) return null;
        double rate = (recent[^1].P - recent[0].P) / (recent[^1].At - recent[0].At);
        return rate > 1e-9 ? (1 - progress) / rate : null;
    }

    /// <summary>A progress report shown: the bar, how much is done and left, and a sample for the pace.</summary>
    void ShowProgress(double fraction, string text, double at)
    {
        if (!double.IsFinite(fraction)) return;   // a NaN report (no work to count) moved nothing
        progress = Math.Clamp(fraction, 0, 1);
        progressBar.Value = (int)(progress * 1000);
        progressLabel.Text = $"{progress:P0} done · {1 - progress:P0} left   {text}";
        if (samples.Count == 0 || samples[^1].P != progress || at - samples[^1].At > 30) samples.Add((at, progress));
        samples.RemoveAll(x => at - x.At > 1800);
    }

    /// <summary>A voice run going on without this app (started from the command line, or before the app was opened):
    /// its progress from tools\voice\progress.txt (its process, start, latest report), while that process lives.</summary>
    void WatchBackground()
    {
        string file = Path.Combine(workspace, "tools", "voice", "progress.txt");
        string[] lines;
        try { lines = File.Exists(file) ? File.ReadAllLines(file) : []; }
        catch (IOException) { return; }
        bool alive = lines.Length >= 3 && int.TryParse(lines[0], out int pid) && IsAlive(pid);
        if (!alive)
        {
            if (watchingBackground)
            {
                watchingBackground = false;
                voicesButton.Enabled = true;
                statusLabel.Text = "The voice generator that was running on its own has finished.";
                timerLabel.Text = "";
            }
            return;
        }
        if (!watchingBackground)
        {
            watchingBackground = true;
            samples.Clear();
            backgroundStarted = DateTime.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var st) ? st : DateTime.Now;
            voicesButton.Enabled = false;   // one voice run at a time
        }
        var parts = lines[2].Split(' ', 3);
        var took = DateTime.Now - backgroundStarted;
        if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            ShowProgress(f, parts.Length > 2 ? parts[2] : "", took.TotalSeconds);
        statusLabel.Text = $"Voices running (since {backgroundStarted:HH:mm})";
        timerLabel.Text = TimerText(took, false);
    }

    static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Voice generator: the converter's voices step on its own, for the converted Morrowind (its dialogue) and
    /// Oblivion (the lines it never recorded) in the output folder.</summary>
    void GenerateVoices()
    {
        if (!new[] { "Morrowind.esm", "Oblivion.esm" }.Any(p => File.Exists(Path.Combine(outputBox.Text, p))))
        { MessageBox.Show(this, "Convert Morrowind or Oblivion first: the voice generator speaks Morrowind's dialogue and the lines Oblivion never recorded, from the converted plugins in the output folder.", "TamrielMerge"); return; }
        string? skyrim = GameInstalls.SkyrimData();
        if (skyrim is null || Dependencies.CreationKitMissing(skyrim).Count > 0)
        { MessageBox.Show(this, "The voice files need the Creation Kit's tools (lip sync). Install it first (Open the Creation Kit in Steam).", "TamrielMerge"); return; }
        bool gpu = gpuBox.Checked || MessageBox.Show(this,
            "Speaking the voices on the graphics card is about six times faster (the CPU helps either way). It keeps the card busy for days; it waits whenever it passes 78 C.\n\nUse the graphics card?",
            "TamrielMerge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        var args = new List<string> { "voices", "--out", outputBox.Text };
        if (gpu) args.Add("--gpu");
        if (installBox.Checked) args.Add("--install");
        Run("Generating voices", args, gpu);
    }

    /// <summary>Distant LOD: the converter's lod command for the converted games in the output folder (land, objects and
    /// trees of every converted world), installed with them when Install is ticked.</summary>
    void GenerateLod()
    {
        string[] games = ["Daggerfall.esm", "Redguard.esm", "Morrowind.esm", "Oblivion.esm"];
        if (!games.Any(g => File.Exists(Path.Combine(outputBox.Text, g))))
        { MessageBox.Show(this, "Convert first: the distant view is made from the converted games in the output folder.", "TamrielMerge"); return; }
        var args = new List<string> { "lod", "--dir", outputBox.Text };
        if (installBox.Checked) args.Add("--install");
        Run("Making the distant LOD", args, false);
    }

    /// <summary>Pause holds the converter and everything it started where they are; Resume lets them carry on.
    /// Pausing frees the machine (user, 2026-10-09: "memory shouldnt be held if pausing for something else"): the voice
    /// workers are stopped by the converter (tools\voice\pause; their RAM and graphics memory freed, restarted on
    /// Resume), the rest is held and its RAM trimmed out to the page file.</summary>
    async void TogglePause()
    {
        if (running is null) return;
        var run = running;
        string voice = Path.Combine(workspace, "tools", "voice");
        string pauseFile = Path.Combine(voice, "pause"), workersFile = Path.Combine(voice, "workers.json");
        List<int> Workers()
        {
            try { return File.Exists(workersFile) ? System.Text.Json.JsonSerializer.Deserialize<List<int>>(File.ReadAllText(workersFile)) ?? [] : []; }
            catch { return []; }
        }
        static bool Alive(int id) { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch { return false; } }
        if (!paused)
        {
            paused = true; pausedSince = DateTime.Now;
            pauseButton.Enabled = false;
            pauseButton.Text = "Pausing…";
            try { File.WriteAllText(pauseFile, ""); } catch { }
            // The converter stops the voice workers within a second or so (if it is speaking voices).
            int stopped = Workers().Count(Alive);
            if (stopped > 0) await Task.Run(() => { for (int i = 0; i < 30 && Workers().Any(Alive); i++) Thread.Sleep(500); });
            stopped -= Workers().Count(Alive);
            var ids = ProcessTree.WithDescendants(run.Id);
            ids.AddRange(Workers());
            int n = ProcessTree.Suspend(ids);
            long freed = await Task.Run(() => ProcessTree.Trim(ids));
            pauseButton.Text = "Resume";
            pauseButton.Enabled = running == run;
            Append($"Paused: {(stopped > 0 ? $"{stopped} voice workers stopped, " : "")}{n} processes held, {freed / 1073741824.0:F1} GB of RAM freed. Resume carries on where it stopped.");
        }
        else
        {
            var ids = ProcessTree.WithDescendants(run.Id);
            ids.AddRange(Workers());
            int n = ProcessTree.Resume(ids);
            try { File.Delete(pauseFile); } catch { }
            paused = false; pausedTotal += DateTime.Now - pausedSince;
            pauseButton.Text = "Pause";
            Append($"Resumed ({n} processes{(File.Exists(workersFile) || runningWhat.Contains("voice", StringComparison.OrdinalIgnoreCase) ? "; the voice workers start again" : "")}).");
        }
    }

    /// <summary>A PROGRESS line from the converter: "PROGRESS 0.4210 what is happening".</summary>
    bool ReadProgress(string line)
    {
        if (!line.StartsWith("PROGRESS ", StringComparison.Ordinal)) return false;
        var parts = line.Split(' ', 3);
        if (parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
            SafeInvoke(() => ShowProgress(f, parts.Length > 2 ? parts[2] : "", Elapsed().TotalSeconds));
        return true;
    }

    void Run(string what, List<string> args, bool? allowGpu = null)
    {
        string cli = Path.Combine(AppContext.BaseDirectory, "TamrielMerge.Cli.exe");
        if (!File.Exists(cli)) { MessageBox.Show(this, $"The converter ({cli}) is missing.", "TamrielMerge"); return; }
        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workspace,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TAMRIELMERGE_TOOLS"] = Path.Combine(workspace, "tools");
        if (!(allowGpu ?? gpuBox.Checked)) psi.Environment["CUDA_VISIBLE_DEVICES"] = "";
        Append($"> TamrielMerge {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        try { running = Process.Start(psi)!; }
        catch (Exception e) { Append($"could not start the converter: {e.Message}"); return; }
        running.EnableRaisingEvents = true;
        running.OutputDataReceived += (_, e) => { if (e.Data is not null && !ReadProgress(e.Data)) Append(e.Data); };
        running.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        running.BeginOutputReadLine(); running.BeginErrorReadLine();
        running.StandardInput.Close();
        running.Exited += (_, _) => { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(() => Finished()); } catch (InvalidOperationException) { } };
        void Finished()
        {
            if (IsDisposed) return;
            int code = running?.ExitCode ?? -1;
            var took = DateTime.Now - started;
            running = null;
            convertButton.Enabled = setupButton.Enabled = voicesButton.Enabled = lodButton.Enabled = checkButton.Enabled = uninstallButton.Enabled = true;
            cancelButton.Enabled = pauseButton.Enabled = false;
            if (paused) { paused = false; pauseButton.Text = "Pause"; }
            if (code == 0) { progressBar.Value = 1000; progressLabel.Text = "100% done"; }
            timerLabel.Text = $"⏱ {Clock(took)} in all";
            statusLabel.Text = code == 0 ? $"{what}: done in {(int)took.TotalHours}:{took:mm\\:ss}." : $"{what}: stopped (exit code {code}) after {(int)took.TotalHours}:{took:mm\\:ss}.";
            Append(statusLabel.Text);
            RefreshStatus();
        }
        started = DateTime.Now;
        pausedTotal = TimeSpan.Zero;
        progress = 0; progressBar.Value = 0; progressLabel.Text = ""; timerLabel.Text = ""; samples.Clear();
        watchingBackground = false;
        runningWhat = what;
        convertButton.Enabled = setupButton.Enabled = voicesButton.Enabled = lodButton.Enabled = checkButton.Enabled = uninstallButton.Enabled = false;
        cancelButton.Enabled = pauseButton.Enabled = true;
        clock.Start();
    }

    void CancelRun()
    {
        if (running is null) return;
        if (paused)
        {
            // Held processes resumed so they can be stopped cleanly (the pause flag kept until then, so no worker restarts).
            ProcessTree.Resume(ProcessTree.WithDescendants(running.Id));
            paused = false;
        }
        try { running.Kill(entireProcessTree: true); } catch { }
        try { File.Delete(Path.Combine(workspace, "tools", "voice", "pause")); } catch { }
        Append("Stopped. Everything done so far is kept; converting again picks up where it left off where it can.");
    }

    readonly StringBuilder pending = new();
    bool flushQueued;

    /// <summary>Adds a line to the log (from any thread), batched so heavy output doesn't stall the window.</summary>
    /// <summary>Everything the log shows is also kept in logs\TamrielMerge-&lt;date&gt;.log in the workspace (user, 2026-10-14:
    /// the conversion's LOD errors were only in the window, gone once it closed).</summary>
    string LogFile => Path.Combine(workspace, "logs", $"TamrielMerge-{DateTime.Now:yyyy-MM-dd}.log");
    readonly object logFileLock = new();

    /// <summary>Runs <paramref name="action"/> on the window's thread, unless the window is closing or closed (2026-10-14: a
    /// run ending or printing after the app was closed threw on the process's thread and ended the app).</summary>
    void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }   // closing or closed (ObjectDisposedException is one too)
    }

    void Append(string line)
    {
        try
        {
            lock (logFileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        lock (pending)
        {
            pending.AppendLine(line);
            if (flushQueued) return;
            flushQueued = true;
        }
        SafeInvoke(() =>
        {
            string text;
            lock (pending) { text = pending.ToString(); pending.Clear(); flushQueued = false; }
            if (log.TextLength > 2_000_000) log.Text = log.Text[^1_000_000..];
            log.AppendText(text);
        });
    }
}
