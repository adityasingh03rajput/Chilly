using System.Runtime.InteropServices;

namespace OrderRestro.Launcher;

/// <summary>
/// First-run (and "change server") dialog: the restaurant's own OrderRestro
/// server address on the LAN, e.g. 192.168.1.50 or 192.168.1.50:1995. No
/// cloud account, no bundled server — this app is a pure client, per the
/// hardening brief's offline-first / LAN-server requirement.
///
/// Designed for non-technical restaurant staff, not IT admins: accepts a
/// bare IP/hostname (fills in http:// and the default port automatically —
/// see AppConfig.TryNormalizeServerUrl), and actually tests the connection
/// (AppConfig.TestConnectionAsync) before accepting it, so a typo is caught
/// here with a plain-language reason instead of surfacing later as a blank
/// WebView2 navigation failure.
/// </summary>
internal sealed class ServerSettingsForm : Form
{
    private readonly TextBox _urlBox;
    private readonly Label _statusLabel;
    private readonly Button _connectButton;
    private readonly LinkLabel _skipTestLink;
    private CancellationTokenSource? _testCts;

    public string? Result { get; private set; }

    public ServerSettingsForm(string? currentUrl)
    {
        Text = "Connect to OrderRestro";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 232);

        var titleLabel = new Label
        {
            Text = "Connect to your restaurant's OrderRestro server",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = false,
            Location = new Point(20, 16),
            Size = new Size(420, 24),
        };

        var helpLabel = new Label
        {
            Text = "Ask your manager or IT admin for this address if you don't have it.\n" +
                   "Just the IP is enough — e.g. 192.168.1.50.",
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
            Location = new Point(20, 44),
            Size = new Size(420, 36),
        };

        _urlBox = new TextBox
        {
            Location = new Point(20, 88),
            Size = new Size(420, 24),
            Text = string.IsNullOrWhiteSpace(currentUrl) ? "https://richpic.onrender.com" : currentUrl,
            Font = new Font("Segoe UI", 10f),
        };
        SetCueBanner(_urlBox, "e.g. 192.168.1.50 or 192.168.1.50:1995");

        _statusLabel = new Label
        {
            AutoSize = false,
            Location = new Point(20, 118),
            Size = new Size(420, 20),
            Font = new Font("Segoe UI", 9f),
        };

        _skipTestLink = new LinkLabel
        {
            Text = "Server's fine, just connect without checking",
            AutoSize = true,
            Location = new Point(20, 144),
            Font = new Font("Segoe UI", 8.5f),
        };
        _skipTestLink.LinkClicked += async (_, _) => await ConnectAsync(skipTest: true);

        _connectButton = new Button
        {
            Text = "Connect",
            Location = new Point(268, 182),
            Size = new Size(84, 30),
            DialogResult = DialogResult.None,
        };
        _connectButton.Click += async (_, _) => await ConnectAsync(skipTest: false);

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(360, 182),
            Size = new Size(80, 30),
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = _connectButton;
        CancelButton = cancelButton;

        Controls.Add(titleLabel);
        Controls.Add(helpLabel);
        Controls.Add(_urlBox);
        Controls.Add(_statusLabel);
        Controls.Add(_skipTestLink);
        Controls.Add(_connectButton);
        Controls.Add(cancelButton);
    }

    private async Task ConnectAsync(bool skipTest)
    {
        if (!AppConfig.TryNormalizeServerUrl(_urlBox.Text, out var normalized, out var error))
        {
            SetStatus(error!, isError: true);
            return;
        }

        if (skipTest)
        {
            Accept(normalized);
            return;
        }

        _testCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _testCts = cts;

        SetBusy(true);
        SetStatus("Checking connection…", isError: false);
        var (ok, message) = await AppConfig.TestConnectionAsync(normalized, cts.Token);
        if (cts.IsCancellationRequested) return; // superseded by a newer attempt

        SetBusy(false);
        if (ok)
        {
            Accept(normalized);
        }
        else
        {
            SetStatus(message, isError: true);
        }
    }

    private void Accept(string normalized)
    {
        Result = normalized;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _urlBox.Enabled = !busy;
        _connectButton.Enabled = !busy;
        _skipTestLink.Enabled = !busy;
        _connectButton.Text = busy ? "Checking…" : "Connect";
    }

    private void SetStatus(string text, bool isError)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = isError ? Color.Firebrick : Color.SeaGreen;
    }

    private static void SetCueBanner(TextBox textBox, string text) =>
        NativeMethods.SendMessage(textBox.Handle, NativeMethods.EM_SETCUEBANNER, IntPtr.Zero, text);

    private static class NativeMethods
    {
        public const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
