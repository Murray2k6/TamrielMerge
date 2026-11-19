using System.Diagnostics;
using TamrielMerge.Setup;

namespace TamrielMerge.Gui;

/// <summary>
/// Log in to Nexus Mods (user, 2026-11-02: "add a login to nexus button on the tool so it can log you in if you are missing
/// something too"): Nexus's API keys page opens in the browser, the member pastes their personal API key here, Nexus checks
/// it (users/validate.json), and it is kept sealed for this Windows user (Nexus.SaveLogin). Nexus's one-click login needs
/// an application registration Nexus staff approve, which TamrielMerge doesn't have, so the key is pasted by hand.
/// </summary>
public sealed class NexusLoginDialog : Form
{
    // Shown as dots: the key is a password to the member's account.
    readonly TextBox keyBox = new() { Width = 560, UseSystemPasswordChar = true };
    readonly Label note = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(600, 0), Margin = new Padding(0, 6, 0, 0) };
    readonly Button login = new() { Text = "Log in", Width = 100, Height = 32 };
    readonly Button cancel = new() { Text = "Cancel", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
    CancellationTokenSource? checking;

    /// <summary>The member logged in as, once Log in succeeded.</summary>
    public Nexus.User? User { get; private set; }

    public NexusLoginDialog()
    {
        Text = "TamrielMerge — log in to Nexus Mods";
        Width = 660; Height = 380; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9.5f);
        MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "TamrielMerge logs in to Nexus Mods with your personal API key, to get what the conversion needs that is only on Nexus.\n\n" +
                   "1. Your browser is showing Nexus's API keys page (log in to Nexus there if it asks).\n" +
                   "2. At the bottom of the page, under Personal API Key, copy your key (click Request API Key if there is none).\n" +
                   "3. Paste it below and click Log in.\n\n" +
                   "The key is kept encrypted for your Windows account only and is sent to nobody but Nexus Mods. Log out deletes it.",
        });
        var openPage = new LinkLabel { Text = "Open the API keys page again", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        openPage.LinkClicked += (_, _) => OpenKeyPage();
        root.Controls.Add(openPage);
        var keyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        keyRow.Controls.AddRange([new Label { Text = "API key:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, keyBox]);
        root.Controls.Add(keyRow);
        root.Controls.Add(note);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, login]);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = login;
        CancelButton = cancel;

        login.Click += async (_, _) => await LogIn();
        Shown += (_, _) => { OpenKeyPage(); keyBox.Focus(); };
        FormClosed += (_, _) => checking?.Cancel();
    }

    void OpenKeyPage()
    {
        try { Process.Start(new ProcessStartInfo(Nexus.KeyPage) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { note.Text = $"Couldn't open the browser ({e.Message}): go to {Nexus.KeyPage} yourself."; }
    }

    async Task LogIn()
    {
        string key = keyBox.Text.Trim();
        if (key.Length == 0) { note.Text = "Paste your Nexus API key first."; return; }
        login.Enabled = keyBox.Enabled = false;
        note.ForeColor = Color.DimGray;
        note.Text = "Checking the key with Nexus Mods…";
        checking = new CancellationTokenSource();
        try
        {
            var user = await Nexus.Validate(key, checking.Token);
            Nexus.SaveLogin(key, user);
            User = user;
            DialogResult = DialogResult.OK;
        }
        catch (Nexus.NexusException e)
        {
            note.ForeColor = Color.Firebrick;
            note.Text = e.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // The key was good but couldn't be kept (the settings file is read-only, or DPAPI refused).
            note.ForeColor = Color.Firebrick;
            note.Text = $"Nexus accepted the key, but it couldn't be saved: {e.Message}";
        }
        finally
        {
            checking?.Dispose(); checking = null;
            if (!IsDisposed && DialogResult != DialogResult.OK) { login.Enabled = keyBox.Enabled = true; keyBox.Focus(); }
        }
    }
}
