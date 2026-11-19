using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapleLedger.Desktop;

public enum StorageMode
{
    /// <summary>SQLite in the browser's private storage. Needs no database.</summary>
    Browser,

    /// <summary>
    /// Older installs only: a SQL Server database. On the next start its ledger is copied into
    /// <see cref="Database"/> (the SQL Server database is left as it was) and this becomes that.
    /// </summary>
    SqlServer,

    /// <summary>
    /// A SQLite file LoonieBooks keeps itself (<see cref="DesktopSettings.DatabasePath"/>); the
    /// launcher also runs the API. Can be shared with other devices on the network.
    /// </summary>
    Database,
}

/// <summary>What closing the launcher window does.</summary>
public enum CloseAction
{
    /// <summary>Ask each time (when LoonieBooks is running).</summary>
    Ask,

    /// <summary>Hide the window and keep running, with an icon in the notification area.</summary>
    Tray,

    /// <summary>Stop LoonieBooks.</summary>
    Exit,
}

/// <summary>
/// The launcher's settings, in %LOCALAPPDATA%\MapleLedger\desktop.json. The connection string can
/// hold a password, so it is encrypted for the current Windows user (DPAPI).
/// </summary>
public sealed class DesktopSettings
{
    public const int DefaultPort = 7085;
    public const int DefaultPhonePort = 7086;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public StorageMode Storage { get; set; } = StorageMode.Database;

    /// <summary>
    /// The site's port. The browser keeps its ledger per address, so changing this starts the
    /// browser store from empty (back up first, then restore).
    /// </summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// "Send from phone" on the receipt scanner: a second port that listens on the network, for the
    /// phone upload page only. 0 turns it off.
    /// </summary>
    public int PhonePort { get; set; } = DefaultPhonePort;

    /// <summary>Who owns the rows in the database: everyone using this install shares that one ledger.</summary>
    public string OwnerId { get; set; } = "desktop";

    /// <summary>What the window's close button does. Takes effect at once, without restarting.</summary>
    public CloseAction OnClose { get; set; } = CloseAction.Ask;

    /// <summary>Older installs only: the SQL Server database to move the ledger from (<see cref="StorageMode.SqlServer"/>).</summary>
    public string? ProtectedConnectionString { get; set; }

    /// <summary>
    /// Let other devices on the network open the site, over HTTPS with the launcher's own
    /// certificate and after entering the household passcode. Only with <see cref="StorageMode.Database"/>: a browser
    /// ledger lives in one browser and can't be shared.
    /// </summary>
    public bool ShareOnNetwork { get; set; }

    /// <summary>The household passcode's salted hash (<c>LanPasscode</c>); the passcode itself isn't kept.</summary>
    public string? LanPasscodeHash { get; set; }

    /// <summary>The names the certificate authority was made for; a new computer name needs a new one.</summary>
    public List<string>? LanAuthorityNames { get; set; }

    /// <summary>Protects the certificate files' private keys.</summary>
    public string? ProtectedLanCertificatePassword { get; set; }

    [JsonIgnore]
    public string? ConnectionString
    {
        get => Unprotect(ProtectedConnectionString);
        set => ProtectedConnectionString = Protect(value?.Trim());
    }

    [JsonIgnore]
    public string? LanCertificatePassword
    {
        get => Unprotect(ProtectedLanCertificatePassword);
        set => ProtectedLanCertificatePassword = Protect(value);
    }

    /// <summary>Whether this run shares the site on the network.</summary>
    [JsonIgnore]
    public bool IsSharing => ShareOnNetwork && Storage != StorageMode.Browser && LanPasscodeHash is { Length: > 0 };

    /// <summary>
    /// The address this computer opens. HTTPS while sharing, which is a different address to the
    /// browser: its own ledger kept at the http address doesn't appear there.
    /// </summary>
    [JsonIgnore]
    public string Url => IsSharing ? $"https://localhost:{Port}" : $"http://localhost:{Port}";

    /// <summary>
    /// The address the launcher shows and opens: the network one while sharing, so this computer
    /// uses the same address as everyone else (and can copy it to them), otherwise <see cref="Url"/>.
    /// This computer still skips the passcode there (<c>LanRequests.IsFromThisComputer</c>).
    /// </summary>
    [JsonIgnore]
    public string BrowseUrl => IsSharing ? NetworkUrls[0] : Url;

    /// <summary>The addresses other devices use while sharing: by name on a PC, name.local on a phone or Mac.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> NetworkUrls
    {
        get
        {
            var machine = Environment.MachineName.ToLowerInvariant();
            return [$"https://{machine}:{Port}", $"https://{machine}.local:{Port}"];
        }
    }

    /// <summary>
    /// %LOCALAPPDATA%\MapleLedger: the project's name before it became LoonieBooks. Kept, not renamed,
    /// so every existing install finds its ledger, settings and certificates.
    /// </summary>
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapleLedger");

    /// <summary>The ledger with <see cref="StorageMode.Database"/>.</summary>
    public static string DatabasePath => Path.Combine(Folder, "ledger.db");

    private static string FilePath => Path.Combine(Folder, "desktop.json");

    /// <summary>Whether settings have been saved (false until the first Start).</summary>
    public static bool Exists => File.Exists(FilePath);

    /// <summary>The saved settings, or null on first run.</summary>
    public static DesktopSettings? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Protect(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? value) =>
        value is { Length: > 0 }
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser))
            : null;

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
