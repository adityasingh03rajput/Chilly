using System.Net.Http;
using System.Text.Json;

namespace OrderRestro.Launcher;

/// <summary>
/// Per-user configuration: which LAN/local OrderRestro server this client
/// points at. Stored under %LOCALAPPDATA%\OrderRestro, NOT inside the MSIX
/// install directory (which is read-only after install and not guaranteed
/// writable/stable across updates — see hardening brief section 19).
/// </summary>
internal sealed class AppConfig
{
    public string? ServerUrl { get; set; } = "https://richpic.onrender.com";

    // "embedded" (this PC runs the server, ServerUrl is always
    // localhost:1995 and never user-editable) or null/anything else
    // (thin-client mode — unchanged from before this feature, prompts for
    // a remote address via ServerSettingsForm). Set once at first run via
    // FirstRunChoiceForm.
    public string? Mode { get; set; }

    public bool IsEmbedded => Mode == "embedded";

    // internal, not private: MainForm's "Remove All Local Data & Uninstall"
    // action deletes this whole tree as the single root of everything this
    // app ever writes (config, WebView2 profile, logs, and ServerPaths.
    // DataDir underneath it) — see that method's own comment for why MSIX
    // needs this at all (no uninstall-hook mechanism to do it automatically).
    internal static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrderRestro");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static string WebView2UserDataFolder => Path.Combine(ConfigDir, "WebView2");

    public static string LogDir => Path.Combine(ConfigDir, "logs");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // Corrupt/unreadable config — fall through to first-run defaults
            // rather than crashing the app on launch.
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>
    /// Only accepts http/https URLs pointing at a host — this app is a thin
    /// client for a restaurant's own OrderRestro server (LAN IP, hostname,
    /// or a Tailscale/VPN address), never a place for arbitrary local file
    /// or other-scheme URLs to end up wired into WebView2 navigation.
    /// </summary>
    public static bool IsValidServerUrl(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// The web app's published host port (docker-compose.yml's
    /// HOST_PORT/1995 default, see README.md "Where to run it") — applied
    /// automatically when the user doesn't type a port themselves, so
    /// "192.168.1.50" alone works instead of requiring "192.168.1.50:1995".
    /// </summary>
    public const int DefaultServerPort = 1995;

    /// <summary>
    /// Turns whatever a restaurant employee actually types — "192.168.1.50",
    /// "192.168.1.50:1995", or a full "http://…" URL — into a normalized,
    /// valid server URL. Most non-technical users won't type a scheme or
    /// port unprompted, so both are filled in rather than rejecting the
    /// input outright.
    /// </summary>
    public static bool TryNormalizeServerUrl(string input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "Enter your server's address.";
            return false;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "That doesn't look like a valid address. Example: 192.168.1.50 or 192.168.1.50:1995";
            return false;
        }

        if (uri.IsDefaultPort)
        {
            uri = new UriBuilder(uri) { Port = DefaultServerPort }.Uri;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    /// <summary>
    /// Hits the server's health endpoint (apps/backend's GET /api/health,
    /// reachable through the web app's own origin via next.config.ts's
    /// /api/:path* rewrite) so the connect dialog can tell the user right
    /// away whether the address actually works, instead of only finding
    /// out after WebView2 fails to navigate.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync($"{serverUrl}/api/health", ct);
            if (response.IsSuccessStatusCode) return (true, "Connected.");
            return (false, $"Reached that address, but it didn't respond like an OrderRestro server (HTTP {(int)response.StatusCode}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "The server didn't respond in time. Check that this PC and the server are on the same network.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach that address. Check the IP/hostname, that the server is running, and that this PC is on the same network.\n\n({ex.Message})");
        }
        catch (Exception ex)
        {
            return (false, $"Could not connect: {ex.Message}");
        }
    }
}
