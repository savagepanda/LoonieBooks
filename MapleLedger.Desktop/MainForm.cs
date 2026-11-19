using System.Diagnostics;
using System.Security.Cryptography;
using MapleLedger.Core.Lan;

namespace MapleLedger.Desktop;

/// <summary>
/// The launcher window: where the ledger is kept, whether the site is running, and a button to open
/// it. The site runs for as long as the launcher does: with this window open, or hidden to its icon in
/// the notification area (<see cref="DesktopSettings.OnClose"/>).
/// </summary>
internal sealed class MainForm : Form
{
    private const int ContentWidth = 520;

    private static readonly Color Ok = Color.FromArgb(0x1A, 0x7F, 0x37);
    private static readonly Color Error = Color.FromArgb(0xB4, 0x23, 0x18);

    private readonly DesktopSettings _settings;
    private readonly bool _firstRun;

    private readonly NotifyIcon _tray;
    private readonly ToolStripItem _trayOpen;
    private readonly EventWaitHandle _stopSignal;
    private readonly RegisteredWaitHandle _stopWait;
    private bool _trayTipShown;

    /// <summary>Set once the launcher is stopping on purpose (not the close button): what to log.</summary>
    private string? _exitReason;

    private SiteHost? _host;
    private CancellationTokenSource? _startCancellation;
    private bool _running;
    private bool _busy;

    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 0, 0, 8) };
    private readonly TextBox _details = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false,
        Width = ContentWidth, Height = 130, Margin = new Padding(0, 0, 0, 8), Font = new Font("Consolas", 8.5f),
    };
    private readonly Button _open = new() { Text = "Open LoonieBooks", AutoSize = true, Enabled = false };
    private readonly LinkLabel _logs = new() { Text = "Open log folder", AutoSize = true, Margin = new Padding(12, 9, 0, 0) };

    private readonly RadioButton _database = new() { Text = "On this computer, in LoonieBooks' own database (recommended)", AutoSize = true };
    private readonly RadioButton _browser = new() { Text = "In this browser", AutoSize = true, Margin = new Padding(3, 10, 3, 0) };

    private readonly CheckBox _phone = new() { Text = "Let a phone on this Wi-Fi send receipt photos (Send from phone)", AutoSize = true };

    private readonly CheckBox _share = new() { Text = "Let other devices on this network open LoonieBooks", AutoSize = true };
    private readonly TextBox _passcode = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly Label _shareResult = new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 30, 0), Margin = new Padding(20, 4, 0, 0) };
    private readonly Label _addresses = new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 30, 0), Margin = new Padding(20, 6, 0, 0) };
    private readonly Button _saveCertificate = new() { Text = "Save certificate for other devices…", AutoSize = true, Enabled = false };
    private readonly TextBox _fingerprint = new()
    {
        ReadOnly = true, BorderStyle = BorderStyle.None, Width = ContentWidth - 30, Margin = new Padding(20, 4, 0, 0),
        ForeColor = SystemColors.GrayText, BackColor = SystemColors.Control, Font = new Font("Consolas", 7.5f),
    };

    private readonly Button _apply = new() { Text = "Start", AutoSize = true };
    private readonly ComboBox _onClose = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Margin = new Padding(6, 4, 0, 0) };

    private static readonly (CloseAction Action, string Text)[] CloseChoices =
    [
        (CloseAction.Ask, "Ask"),
        (CloseAction.Tray, "Keep running in the notification area"),
        (CloseAction.Exit, "Stop LoonieBooks"),
    ];

    public MainForm()
    {
        var saved = DesktopSettings.Load();
        _firstRun = saved is null;
        _settings = saved ?? new DesktopSettings();

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LoonieBooks";
        Icon = LoadIcon(null);
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        _status.Font = new Font(Font, FontStyle.Bold);

        var layout = Column();
        layout.Controls.Add(_status);
        layout.Controls.Add(_details);
        layout.Controls.Add(Row(_open, _logs));

        var storage = new GroupBox { Text = "Where to keep your ledger", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(0, 12, 0, 12) };
        var storageLayout = Column();
        storageLayout.Controls.Add(_database);
        storageLayout.Controls.Add(Note($"One ledger for every browser on this computer, and for other devices if you share it below. " +
            $"LoonieBooks looks after the file ({DesktopSettings.DatabasePath}); back it up from the Your data page."));
        storageLayout.Controls.Add(_browser);
        storageLayout.Controls.Add(Note("Each browser keeps its own ledger and nothing else runs. Can't be shared."));
        storage.Controls.Add(storageLayout);
        layout.Controls.Add(storage);

        var share = new GroupBox { Text = "Share on this network", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 12) };
        var shareLayout = Column();
        shareLayout.Controls.Add(_share);
        shareLayout.Controls.Add(Note("Needs the ledger in a database (not in this browser). Others in your home open LoonieBooks in their browser, " +
            "enter the household passcode once on each device, and can see and change the ledger. Anyone who knows the passcode and is on this network can get in."));
        var passcodeLabel = new Label { Text = "Household passcode:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
        var passcodeRow = Row(passcodeLabel, _passcode);
        passcodeRow.Margin = new Padding(20, 6, 0, 0);
        shareLayout.Controls.Add(passcodeRow);
        shareLayout.Controls.Add(_shareResult);
        shareLayout.Controls.Add(_addresses);
        var certificateRow = Row(_saveCertificate);
        certificateRow.Margin = new Padding(20, 8, 0, 0);
        shareLayout.Controls.Add(certificateRow);
        shareLayout.Controls.Add(_fingerprint);
        shareLayout.Controls.Add(Note("Each device trusts LoonieBooks' certificate once, so its browser stops warning: open the address, " +
            "choose \"Install the certificate\" on the passcode page (or copy the saved file over), and check that the fingerprint matches the one above."));
        share.Controls.Add(shareLayout);
        layout.Controls.Add(share);

        var phone = new GroupBox { Text = "Receipts from your phone", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 12) };
        var phoneLayout = Column();
        phoneLayout.Controls.Add(_phone);
        phoneLayout.Controls.Add(Note($"The receipt scanner shows a QR code; your phone opens it and sends the photo straight to this computer. " +
            $"LoonieBooks listens on port {(_settings.PhonePort > 0 ? _settings.PhonePort : DesktopSettings.DefaultPhonePort)} on your network for that upload page only. " +
            "The first time, Windows may ask to let LoonieBooks through the firewall: allow it on private networks."));
        phone.Controls.Add(phoneLayout);
        layout.Controls.Add(phone);

        _onClose.Items.AddRange(CloseChoices.Select(c => (object)c.Text).ToArray());
        layout.Controls.Add(Row(_apply, new Label
        {
            Text = "When this window closes:",
            AutoSize = true,
            Margin = new Padding(24, 8, 0, 0),
        }, _onClose));

        Controls.Add(layout);
        AcceptButton = _apply;
        ResumeLayout(false);
        PerformLayout();

        _browser.Checked = _settings.Storage == StorageMode.Browser;
        _database.Checked = _settings.Storage != StorageMode.Browser;
        _phone.Checked = _settings.PhonePort > 0;
        _share.Checked = _settings.ShareOnNetwork;
        _onClose.SelectedIndex = Array.FindIndex(CloseChoices, c => c.Action == _settings.OnClose);
        _passcode.PlaceholderText = _settings.LanPasscodeHash is null ? $"At least {LanPasscode.MinLength} characters" : "Leave blank to keep the current one";
        _addresses.Text = "Other devices open: " + string.Join("   or   ", _settings.NetworkUrls) + "  (phones: Connect a phone in LoonieBooks' top bar shows a QR code)";
        ShowFingerprint();

        _phone.CheckedChanged += (_, _) => UpdateControls();
        _share.CheckedChanged += (_, _) => { _shareResult.Text = ""; UpdateControls(); };
        _passcode.TextChanged += (_, _) => { _shareResult.Text = ""; UpdateControls(); };
        _saveCertificate.Click += (_, _) => SaveCertificate();
        _browser.CheckedChanged += (_, _) => UpdateControls();
        _database.CheckedChanged += (_, _) => UpdateControls();
        _apply.Click += async (_, _) => await ApplyAsync();
        _open.Click += (_, _) => OpenBrowser();
        _onClose.SelectedIndexChanged += (_, _) => SetOnClose(CloseChoices[_onClose.SelectedIndex].Action);
        _logs.LinkClicked += (_, _) =>
        {
            Directory.CreateDirectory(SiteHost.LogFolder);
            Process.Start(new ProcessStartInfo(SiteHost.LogFolder) { UseShellExecute = true });
        };

        var trayMenu = new ContextMenuStrip();
        var showWindow = trayMenu.Items.Add("Show LoonieBooks window", null, (_, _) => ShowFromTray());
        showWindow.Font = new Font(showWindow.Font, FontStyle.Bold);
        _trayOpen = trayMenu.Items.Add("Open LoonieBooks in the browser", null, (_, _) => OpenBrowser());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit LoonieBooks", null, (_, _) => Exit("Exit chosen from the notification-area icon; stopping LoonieBooks."));
        trayMenu.Opening += (_, _) => _trayOpen.Enabled = _running && !_busy;
        _tray = new NotifyIcon { Icon = LoadIcon(SystemInformation.SmallIconSize), Text = "LoonieBooks", ContextMenuStrip = trayMenu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowFromTray();
        };

        // Another copy taking over, or desktopbuild.bat, asks this one to stop here: closing the
        // window from outside would only hide it, or ask.
        _stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, RunningCopies.StopSignalName(Environment.ProcessId));
        _stopWait = ThreadPool.RegisterWaitForSingleObject(_stopSignal,
            (_, _) => OnUiThread(() => Exit("Asked to stop by another copy of LoonieBooks or by a rebuild; stopping LoonieBooks.")),
            null, Timeout.Infinite, executeOnlyOnce: true);

        Shown += async (_, _) =>
        {
            if (_firstRun)
            {
                SetStatus("Welcome! Choose where to keep your ledger, then click Start.", SystemColors.ControlText);
                UpdateControls();
            }
            else
            {
                await StartAsync(openBrowser: true);
            }
        };

        FormClosing += (_, e) =>
        {
            if (_exitReason is null && e.CloseReason == CloseReason.UserClosing)
            {
                // Nothing to keep running before it has started (or after it failed): just close.
                var action = _running || _busy ? ChooseOnClose() : CloseAction.Exit;
                if (action != CloseAction.Exit)
                {
                    e.Cancel = true;
                    if (action == CloseAction.Tray) HideToTray();
                    return;
                }
            }

            LauncherLog.Write(_exitReason ?? e.CloseReason switch
            {
                CloseReason.UserClosing => "Window closed by the user; stopping LoonieBooks.",
                CloseReason.WindowsShutDown => "Windows is shutting down or signing out; stopping LoonieBooks.",
                CloseReason.TaskManagerClosing => "Closed by another program (such as Task Manager's End task); stopping LoonieBooks.",
                _ => $"Window closing ({e.CloseReason}); stopping LoonieBooks.",
            });
            _startCancellation?.Cancel();
            _host?.Dispose();
            _host = null;
            _tray.Visible = false;
        };

        FormClosed += (_, _) =>
        {
            _stopWait.Unregister(null);
            _stopSignal.Dispose();
            _tray.Dispose();
            trayMenu.Dispose();
        };
    }

    /// <summary>
    /// What the close button does this time: the saved choice, or the user's answer. Null: they
    /// cancelled, so the window stays.
    /// </summary>
    private CloseAction? ChooseOnClose()
    {
        if (_settings.OnClose != CloseAction.Ask) return _settings.OnClose;

        var tray = new TaskDialogCommandLinkButton("Keep running in the notification area",
            "Close this window and leave LoonieBooks running. Its maple leaf icon by the clock shows this window again, " +
            "opens LoonieBooks, or stops it.");
        var stop = new TaskDialogCommandLinkButton("Stop LoonieBooks",
            _settings.IsSharing || _settings.PhonePort > 0
                ? "LoonieBooks stops until you open it again, on this computer and for your other devices."
                : "LoonieBooks stops until you open it again.");
        var page = new TaskDialogPage
        {
            Caption = "LoonieBooks",
            Heading = "Keep LoonieBooks running?",
            Buttons = { tray, stop, TaskDialogButton.Cancel },
            Verification = new TaskDialogVerificationCheckBox("Don't ask again (you can change this at the bottom of this window)"),
            AllowCancel = true,
        };

        var answer = TaskDialog.ShowDialog(this, page);
        if (answer != tray && answer != stop) return null;

        var action = answer == tray ? CloseAction.Tray : CloseAction.Exit;
        if (page.Verification.Checked)
        {
            _onClose.SelectedIndex = Array.FindIndex(CloseChoices, c => c.Action == action);
        }

        return action;
    }

    private void SetOnClose(CloseAction action)
    {
        if (_settings.OnClose == action) return;
        _settings.OnClose = action;
        LauncherLog.Write($"Closing the window now: {action}.");
        // Before the first Start there's nothing saved yet: it's saved with the rest then.
        if (DesktopSettings.Exists) _settings.Save();
    }

    private void HideToTray()
    {
        _tray.Visible = true;
        Hide();
        LauncherLog.Write("Window hidden; LoonieBooks keeps running, with its icon in the notification area.");
        if (!_trayTipShown)
        {
            _trayTipShown = true;
            _tray.ShowBalloonTip(5000, "LoonieBooks is still running",
                "Click the maple leaf by the clock to show its window. Right-click it to open LoonieBooks or exit. " +
                "If it isn't there, look under the ^ arrow.", ToolTipIcon.None);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    /// <summary>Stops LoonieBooks and closes the launcher, without asking.</summary>
    private void Exit(string reason)
    {
        if (IsDisposed) return;
        _exitReason = reason;
        Close();
    }

    private static Icon LoadIcon(Size? size)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("MapleLedger.ico")!;
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }

    private StorageMode SelectedStorage => _database.Checked ? StorageMode.Database : StorageMode.Browser;

    /// <summary>An older SQL Server install becomes <see cref="StorageMode.Database"/> when it starts.</summary>
    private StorageMode SavedStorage => _settings.Storage == StorageMode.SqlServer ? StorageMode.Database : _settings.Storage;

    private bool IsChanged =>
        SelectedStorage != SavedStorage
        || _phone.Checked != (_settings.PhonePort > 0)
        || _share.Checked != _settings.ShareOnNetwork
        || (_share.Checked && _passcode.Text.Length > 0)
;

    private void UpdateControls()
    {
        _browser.Enabled = _database.Enabled = _phone.Enabled = !_busy;
        _share.Enabled = SelectedStorage != StorageMode.Browser && !_busy;
        _passcode.Enabled = _share.Enabled && _share.Checked;
        _addresses.Visible = _share.Checked && SelectedStorage != StorageMode.Browser;
        _apply.Text = _running ? "Save and restart" : "Start";
        _apply.Enabled = !_busy && (!_running || IsChanged);
        _open.Enabled = _running && !_busy;
    }

    private async Task ApplyAsync()
    {
        var sharing = _share.Checked && SelectedStorage != StorageMode.Browser;
        var passcode = _passcode.Text;
        if (sharing && (passcode.Length > 0 || _settings.LanPasscodeHash is null) && passcode.Length < LanPasscode.MinLength)
        {
            _shareResult.ForeColor = Error;
            _shareResult.Text = $"Choose a household passcode of at least {LanPasscode.MinLength} characters. A short phrase is easy to type and hard to guess.";
            _passcode.Focus();
            return;
        }

        // An older SQL Server install that hasn't moved its ledger yet keeps that until it starts.
        if (SelectedStorage != SavedStorage) _settings.Storage = SelectedStorage;
        _settings.PhonePort = _phone.Checked ? (_settings.PhonePort > 0 ? _settings.PhonePort : DesktopSettings.DefaultPhonePort) : 0;
        _settings.ShareOnNetwork = _share.Checked;
        if (sharing && passcode.Length > 0)
        {
            // A new passcode signs every other device out.
            _settings.LanPasscodeHash = await Task.Run(() => LanPasscode.Hash(passcode));
            _passcode.Text = "";
            _passcode.PlaceholderText = "Leave blank to keep the current one";
        }

        _settings.Save();
        await StartAsync(openBrowser: true);
    }

    private async Task StartAsync(bool openBrowser)
    {
        _busy = true;
        _running = false;
        _details.Visible = false;
        UpdateControls();

        _host?.Dispose();
        var host = _host = new SiteHost(_settings);
        host.Stopped += ex => OnUiThread(() =>
        {
            // Failures while starting are reported by StartAsync itself.
            if (_host != host || !_running) return;
            _running = false;
            host.Dispose();
            _host = null;
            ShowError(ex.Message, ex.Log);
            UpdateControls();
        });

        _startCancellation = new CancellationTokenSource();
        try
        {
            await host.StartAsync(new Progress<string>(s => SetStatus(s, SystemColors.ControlText)), _startCancellation.Token);
            _running = true;
            LauncherLog.Write($"Site running at {_settings.BrowseUrl} ({_settings.Storage} storage, phone port {(_settings.PhonePort > 0 ? _settings.PhonePort : "off")}, " +
                $"{(_settings.IsSharing ? "shared on the network" : "this computer only")}).");
            SetStatus($"LoonieBooks is running at {_settings.BrowseUrl}", Ok);
            if (host.Lan is { } lan)
            {
                ShowFingerprint();
                // This computer's browsers open the https address too, so they need to trust the
                // authority. Windows asks the user to confirm.
                if (!LanSharing.IsTrustedHere(lan.Authority) && !LanSharing.TrustHere(lan.Authority))
                {
                    LauncherLog.Write("The user declined to trust LoonieBooks' certificate on this computer.");
                    _shareResult.ForeColor = Error;
                    _shareResult.Text = "Windows wasn't allowed to trust LoonieBooks' certificate, so your browser will warn about the address. Click Save and restart to be asked again.";
                }
            }

            if (openBrowser) OpenBrowser();
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
            return;
        }
        catch (SiteException ex)
        {
            host.Dispose();
            _host = null;
            ShowError(ex.Message, ex.Log, ex.Retry);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            host.Dispose();
            _host = null;
            ShowError("LoonieBooks couldn't start: " + ex.Message, null);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) UpdateControls();
        }
    }

    private void ShowFingerprint()
    {
        var exists = File.Exists(LanSharing.AuthorityCerPath);
        _saveCertificate.Enabled = exists;
        _fingerprint.Visible = exists;
        if (!exists) return;
        try
        {
            using var authority = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(LanSharing.AuthorityCerPath);
            _fingerprint.Text = "SHA-256 fingerprint: " + LanCertificates.Fingerprint(authority);
        }
        catch (CryptographicException)
        {
            _fingerprint.Visible = false;
        }
    }

    private void SaveCertificate()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = "LoonieBooks.cer",
            Filter = "Certificate (*.cer)|*.cer",
            Title = "Save LoonieBooks' certificate for other devices",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            File.Copy(LanSharing.AuthorityCerPath, dialog.FileName, overwrite: true);
        }
    }

    private void ShowError(string message, string? log, bool retry = true)
    {
        LauncherLog.Write("Error shown: " + message);
        // An error while hidden in the notification area: bring the window back so it's seen.
        if (!Visible && !IsDisposed) ShowFromTray();
        SetStatus(retry ? message + " Fix the problem, then click Start." : message, Error);
        _details.Text = log ?? "";
        _details.Visible = !string.IsNullOrWhiteSpace(log);
        if (_details.Visible)
        {
            _details.SelectionStart = _details.TextLength;
            _details.ScrollToCaret();
        }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private void OpenBrowser() => Process.Start(new ProcessStartInfo(_settings.BrowseUrl) { UseShellExecute = true });

    private void OnUiThread(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window closed in the meantime.
        }
    }

    private static TableLayoutPanel Column() => new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth - 30, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(20, 0, 0, 0),
    };
}
