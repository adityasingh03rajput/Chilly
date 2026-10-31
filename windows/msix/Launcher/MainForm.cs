using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OrderRestro.Launcher;

internal sealed class MainForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _errorLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f),
        Visible = false,
    };
    private Panel? _fatalErrorPanel;
    private AppConfig _config;
    private ServerSupervisor? _supervisor;
    private TrayIcon? _tray;
    private bool _allowClose;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _activateWaitHandle;
    private Process? _embeddedWebProcess;

    public MainForm(EventWaitHandle activateEvent)
    {
        Text = "Nodedr OrderRestro";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1360, 860);
        MinimumSize = new Size(900, 600);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        _config = AppConfig.Load();
        _activateEvent = activateEvent;

        BuildMenu();
        Controls.Add(_errorLabel);
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeAsync();
        FormClosing += OnFormClosing;

        // A second launch (see Program.cs's single-instance mutex) signals
        // this event instead of starting its own process — bring THIS
        // window to the front on a thread-pool callback thread, marshaled
        // back to the UI thread the same way ServerSupervisor's own status
        // events already are.
        _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => { if (InvokeRequired) BeginInvoke((Action)RestoreFromTray); else RestoreFromTray(); },
            null,
            Timeout.InfiniteTimeSpan,
            false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activateWaitHandle?.Unregister(null);
            // Safety net for any exit path other than the tray's explicit
            // "Stop Server & Exit" (which already awaits a graceful
            // pg_ctl/backend/web shutdown before closing) — e.g. Windows
            // shutdown/logoff, Task Manager "End task". Not graceful, but
            // Postgres/Node both tolerate an unclean kill; better than
            // orphaning the child processes.
            _tray?.Dispose();
            _supervisor?.Dispose();
            if (_embeddedWebProcess is not null && !_embeddedWebProcess.HasExited) { try { _embeddedWebProcess.Kill(entireProcessTree: true); } catch { } _embeddedWebProcess.Dispose(); }
        }
        base.Dispose(disposing);
    }

    
    private async Task<bool> StartBundledWebServerAsync(string nodeExe, string serverJs)
    {
        ShowStatus("Starting local restaurant UI…");
        var webDir = Path.Combine(AppContext.BaseDirectory, "web");
        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = $"\"{serverJs}\"",
            WorkingDirectory = webDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.Environment["PORT"] = "1995";
        psi.Environment["HOSTNAME"] = "127.0.0.1";
        psi.Environment["NODE_ENV"] = "production";

        try
        {
            _embeddedWebProcess = Process.Start(psi);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var res = await client.GetAsync("http://127.0.0.1:1995/");
                    if (res.IsSuccessStatusCode) return true;
                }
                catch { }
                await Task.Delay(300);
            }
            return true;
        }
        catch (Exception ex)
        {
            ShowFatalError($"Failed to launch local interface: {ex.Message}");
            return false;
        }
    }
    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("File");

        // "Change server address…" only makes sense in thin-client mode —
        // an embedded-mode install always points at its own localhost
        // server, there's nothing to change.
        if (!_config.IsEmbedded)
        {
            var changeServer = new ToolStripMenuItem("Change server address…");
            changeServer.Click += async (_, _) => await ChangeServerAsync();
            fileMenu.DropDownItems.Add(changeServer);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
        }

        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        var removeData = new ToolStripMenuItem("Remove All Local Data && Uninstall…");
        removeData.Click += async (_, _) => await RemoveAllDataAndUninstallAsync();
        fileMenu.DropDownItems.Add(removeData);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => RequestRealExit();
        fileMenu.DropDownItems.Add(exit);

        var viewMenu = new ToolStripMenuItem("View");
        var reload = new ToolStripMenuItem("Reload") { ShortcutKeys = Keys.F5 };
        reload.Click += (_, _) => _webView.Reload();
        viewMenu.DropDownItems.Add(reload);

        menu.Items.Add(fileMenu);
        menu.Items.Add(viewMenu);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private async Task InitializeAsync()
    {
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "node", "node.exe");
        var bundledWebServer = Path.Combine(AppContext.BaseDirectory, "web", "apps", "web", "server.js");

        if (File.Exists(bundledNode) && File.Exists(bundledWebServer))
        {
            _config.ServerUrl = "http://localhost:1995";
            _config.Mode = "bundled";
            if (!await StartBundledWebServerAsync(bundledNode, bundledWebServer))
            {
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(_config.Mode))
        {
            using var choiceDialog = new FirstRunChoiceForm();
            var choiceResult = choiceDialog.ShowDialog(this);
            if (choiceResult != DialogResult.OK || choiceDialog.Result is null)
            {
                Close();
                return;
            }

            if (choiceDialog.Result == FirstRunChoice.Embedded)
            {
                _config.Mode = "embedded";
                _config.ServerUrl = $"http://localhost:{ServerPaths.WebPort}";
                _config.Save();
                // Menu was built before this choice existed — rebuild so
                // "Change server address…" is correctly hidden now.
                Controls.Remove(MainMenuStrip);
                BuildMenu();
            }
            else
            {
                await ChangeServerAsync(isFirstRun: true);
                if (_config.ServerUrl is null) return; // user cancelled — ChangeServerAsync already closed the form
                // Must be non-empty so this choice screen doesn't reappear
                // on every future launch (see the IsNullOrWhiteSpace(Mode)
                // check above) — "embedded" is the only value anything
                // else checks for, so any other non-empty string means
                // "thin client."
                _config.Mode = "remote";
                _config.Save();
            }
        }
        else if (!_config.IsEmbedded &&
                 (string.IsNullOrWhiteSpace(_config.ServerUrl) || !AppConfig.IsValidServerUrl(_config.ServerUrl, out _)))
        {
            await ChangeServerAsync(isFirstRun: true);
        }

        if (_config.IsEmbedded && !await StartEmbeddedServerAsync())
        {
            return; // fatal error already shown
        }

        Directory.CreateDirectory(AppConfig.WebView2UserDataFolder);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppConfig.WebView2UserDataFolder);
            await _webView.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Present on Windows 11 and most Windows 10 systems, but not
            // guaranteed — show an actionable message instead of an
            // unhandled crash (see Program.cs's last-resort handler for
            // anything this doesn't catch).
            ShowFatalError(
                "The Microsoft Edge WebView2 Runtime is required but isn't installed on this PC.\n\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 (Evergreen " +
                "Bootstrapper), then reopen Nodedr OrderRestro.");
            return;
        }
        catch (Exception ex)
        {
            ShowFatalError($"Could not start the embedded browser component.\n\n{ex.Message}");
            return;
        }

        // Minimum-privilege WebView2 settings: no dev tools / no default
        // context menu with "Inspect" for end users, no crash-reporting
        // status page noise. Printing stays enabled — window.print() must
        // keep reaching the native OS print dialog (Windows Print
        // Spooler), which is the whole point of this client.
        var settings = _webView.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsSwipeNavigationEnabled = false;

        // Keep everything in one window instead of spawning stray popups —
        // this must still feel like a normal desktop app, not a browser.
        _webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            _webView.CoreWebView2.Navigate(args.Uri);
        };

        _webView.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                ShowConnectionError();
            }
            else
            {
                _errorLabel.Visible = false;
                _webView.Visible = true;
            }
        };

        Navigate();
    }

    private async Task<bool> StartEmbeddedServerAsync()
    {
        _supervisor = new ServerSupervisor();
        _supervisor.StatusChanged += status =>
        {
            // Control.BeginInvoke's parameter type is the abstract
            // System.Delegate, which a bare lambda/method group can't
            // implicitly convert to — every call here needs an explicit
            // concrete delegate cast (Action).
            if (InvokeRequired) { BeginInvoke((Action)(() => ShowStatus(status))); return; }
            ShowStatus(status);
        };
        _supervisor.Faulted += message =>
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => _tray?.ShowBalloon("Nodedr OrderRestro", message))); return; }
            _tray?.ShowBalloon("Nodedr OrderRestro", message);
        };

        ShowStatus("Starting server…");
        try
        {
            // Generous first-run budget: EnsureRuntimeCopyAsync copies the
            // whole embedded-server payload (Postgres + Node + backend +
            // web — ~2.8GB, confirmed ~5+ minutes on CI hardware) out of
            // the read-only MSIX install location on the very first launch
            // only, before Postgres/migrations/backend/web even start —
            // every launch after that skips it and starts in seconds.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await _supervisor.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // Without this, the ONLY place a startup failure was visible
            // was this window's own UI text — invisible to CI (and to
            // anyone troubleshooting without eyes on the actual screen).
            // A prior CI run's "backend never became healthy" turned out
            // to be exactly this: the supervisor had already failed and
            // given up internally, but nothing on disk showed why.
            try
            {
                File.AppendAllText(
                    Path.Combine(ServerPaths.ServerLogDir, "supervisor-error.log"),
                    $"{DateTime.UtcNow:O} {ex}\n");
            }
            catch { /* best-effort */ }

            ShowFatalError(
                technicalMessage: $"Could not start the embedded server.\n\n{ex.Message}\n\nLogs: {ServerPaths.ServerLogDir}",
                friendlyHeadline: "OrderRestro couldn't start its local database",
                friendlyBody: "Your restaurant data has not been deleted. OrderRestro was unable to start its local database service.",
                retry: async () => { Controls.Remove(_fatalErrorPanel); _fatalErrorPanel = null; await InitializeAsync(); });
            return false;
        }

        // Icon is always set in the constructor (ExtractAssociatedIcon
        // falls back to SystemIcons.Application) — the compiler just can't
        // see that far, hence the null-forgiving operator.
        _tray = new TrayIcon(Icon!);
        _tray.OpenRequested += () =>
        {
            if (InvokeRequired) { BeginInvoke((Action)RestoreFromTray); return; }
            RestoreFromTray();
        };
        _tray.StopAndExitRequested += async () =>
        {
            if (InvokeRequired) { BeginInvoke((Action)(async () => await StopServerAndExitAsync())); return; }
            await StopServerAndExitAsync();
        };
        _tray.RemoveDataAndUninstallRequested += async () =>
        {
            if (InvokeRequired) { BeginInvoke((Action)(async () => await RemoveAllDataAndUninstallAsync())); return; }
            await RemoveAllDataAndUninstallAsync();
        };
        return true;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task StopServerAndExitAsync()
    {
        ShowStatus("Stopping server…");
        if (_supervisor is not null) await _supervisor.StopAsync();
        RequestRealExit();
    }

    private void RequestRealExit()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// MSIX/Desktop Bridge has no uninstall-hook mechanism (unlike a
    /// classic MSI's uninstall custom actions) — Windows only auto-cleans
    /// the package's own sandboxed %LOCALAPPDATA%\Packages\...\LocalState
    /// folder on removal, which this app deliberately doesn't use (see
    /// ServerPaths.cs/AppConfig.cs — writable state must survive an
    /// MSIX upgrade-in-place, which requires a real, stable path). That
    /// means the restaurant database, uploads, and server runtime under
    /// %LOCALAPPDATA%\OrderRestro would otherwise survive an uninstall as
    /// residue. This is the app's own equivalent of an uninstaller's
    /// cleanup step — stop everything, delete it all, then hand off to
    /// Windows' own "Apps & features" to remove the package itself.
    /// </summary>
    private async Task RemoveAllDataAndUninstallAsync()
    {
        RestoreFromTray();

        var confirm = MessageBox.Show(
            this,
            "This permanently deletes ALL local OrderRestro data on this PC — your restaurant's " +
            "database, uploaded images, and settings. This cannot be undone.\n\n" +
            "Only do this if you're uninstalling OrderRestro from this PC, or want to start " +
            "completely fresh.\n\nContinue?",
            "Remove All Local Data",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        ShowStatus("Stopping server and removing local data…");
        if (_supervisor is not null) await _supervisor.StopAsync();

        // The WebView2 control's own profile lives INSIDE the directory
        // we're about to delete (AppConfig.WebView2UserDataFolder) and is
        // held open by WebView2's separate msedgewebview2.exe helper
        // processes for as long as this control exists — deleting the tree
        // while it's still alive would hit the exact same "file in use"
        // class of failure this whole fix is about. Dispose it first, then
        // still retry briefly: WebView2 process teardown on Dispose isn't
        // guaranteed instantaneous.
        _webView.Dispose();

        // AppConfig.ConfigDir (%LOCALAPPDATA%\OrderRestro) is the single
        // root for everything this app ever writes: config.json, the
        // WebView2 profile, logs, and ServerPaths.DataDir (pgdata, uploads,
        // server-runtime, backend.env) all live underneath it — deleting it
        // once here is genuinely everything, not a partial cleanup.
        var dataDir = AppConfig.ConfigDir;
        string? deleteError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
                deleteError = null;
                break;
            }
            catch (Exception ex)
            {
                deleteError = ex.Message;
                await Task.Delay(500);
            }
        }

        _allowClose = true;
        if (deleteError is null)
        {
            MessageBox.Show(
                this,
                "Local data removed. Windows Settings will now open — find \"Nodedr OrderRestro\" " +
                "and click Uninstall to finish removing the app.",
                "Nodedr OrderRestro",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(
                this,
                $"The server was stopped, but some files could not be deleted automatically:\n\n{deleteError}\n\n" +
                $"You can safely delete this folder by hand once OrderRestro is closed: {dataDir}\n\n" +
                "Windows Settings will now open so you can uninstall the app.",
                "Nodedr OrderRestro",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — local data is already fully cleaned up
            // regardless of whether Settings opens; the user can still get
            // there manually.
        }

        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Only embedded-mode installs have anything to keep alive in the
        // background — a pure thin client closes normally, same as before
        // this feature existed.
        if (_config.IsEmbedded && !_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloon(
                "Nodedr OrderRestro",
                "Still running in the background so other devices stay connected. Use the tray icon to reopen or fully stop it.",
                ToolTipIcon.Info);
        }
    }

    private void Navigate()
    {
        if (_config.ServerUrl is null) return;
        _webView.CoreWebView2.Navigate(_config.ServerUrl);
    }

    private void ShowStatus(string message)
    {
        _webView.Visible = false;
        _errorLabel.Text = message;
        _errorLabel.Visible = true;
        _tray?.SetStatus(message);
    }

    private void ShowConnectionError()
    {
        _webView.Visible = false;
        _errorLabel.Text =
            $"Could not reach {_config.ServerUrl}.\n\n" +
            "Check that the OrderRestro server is running and this device is on\n" +
            "the same network, then choose File → Reload, or File → Change server address…";
        _errorLabel.Visible = true;
    }

    /// <summary>
    /// Fatal-error UI: a friendly headline for non-technical users, with
    /// the full technical detail still one click away (Copy Diagnostic
    /// Information) and the log folder itself (Open Logs) — instead of
    /// putting a raw Windows filesystem path front-and-center as the
    /// primary experience. This is presentation only: it never changes
    /// WHETHER something failed or WHY, only how that failure is shown —
    /// the actual startup-lifecycle fix lives in ServerSupervisor.
    /// </summary>
    private void ShowFatalError(
        string technicalMessage,
        string? friendlyHeadline = null,
        string? friendlyBody = null,
        Func<Task>? retry = null)
    {
        _webView.Visible = false;
        _errorLabel.Visible = false;
        _fatalErrorPanel?.Dispose();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var headline = new Label
        {
            Text = friendlyHeadline ?? "OrderRestro couldn't start",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 50,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var body = new Label
        {
            Text = friendlyBody ?? technicalMessage,
            Font = new Font("Segoe UI", 10f),
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 90,
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(24, 0, 24, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Top,
            Anchor = AnchorStyles.None,
        };

        if (retry is not null)
        {
            var retryButton = new Button { Text = "Retry", AutoSize = true, Margin = new Padding(6) };
            retryButton.Click += async (_, _) => await retry();
            buttons.Controls.Add(retryButton);
        }

        var openLogsButton = new Button { Text = "Open Logs", AutoSize = true, Margin = new Padding(6) };
        openLogsButton.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(ServerPaths.ServerLogDir);
                Process.Start(new ProcessStartInfo(ServerPaths.ServerLogDir) { UseShellExecute = true });
            }
            catch
            {
                // best-effort — the log path is also in the copyable diagnostic text below
            }
        };
        buttons.Controls.Add(openLogsButton);

        var copyButton = new Button { Text = "Copy Diagnostic Information", AutoSize = true, Margin = new Padding(6) };
        copyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(technicalMessage); }
            catch { /* best-effort */ }
        };
        buttons.Controls.Add(copyButton);

        var buttonsWrapper = new Panel { Dock = DockStyle.Top, Height = 50 };
        buttons.Left = 0;
        buttons.Top = 0;
        buttonsWrapper.Controls.Add(buttons);
        buttonsWrapper.Resize += (_, _) => buttons.Left = Math.Max(0, (buttonsWrapper.Width - buttons.Width) / 2);

        layout.Controls.Add(headline, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(buttonsWrapper, 0, 2);

        var wrapper = new Panel { Dock = DockStyle.Fill };
        wrapper.Controls.Add(layout);

        Controls.Add(wrapper);
        wrapper.BringToFront();
        _fatalErrorPanel = wrapper;
    }

    private async Task ChangeServerAsync(bool isFirstRun = false)
    {
        using var dialog = new ServerSettingsForm(_config.ServerUrl);
        var result = dialog.ShowDialog(this);
        if (result != DialogResult.OK || dialog.Result is null)
        {
            if (isFirstRun) Close();
            return;
        }
        _config.ServerUrl = dialog.Result;
        _config.Save();
        if (_webView.CoreWebView2 is not null) Navigate();
        await Task.CompletedTask;
    }
}
