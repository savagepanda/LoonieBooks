using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MapleLedger.Core.Desktop;

namespace MapleLedger.Desktop;

/// <summary>A host that failed to start or stopped on its own, with the end of its log.</summary>
public sealed class SiteException(string message, string? log = null) : Exception(message)
{
    public string? Log { get; } = log;

    /// <summary>Whether to tell the user to fix the problem and click Start (not when starting again can't help).</summary>
    public bool Retry { get; init; } = true;
}

/// <summary>
/// Runs the published web host (web\) and, with the ledger in a database, the API (api\) as child processes of
/// the launcher, wired together the way the Aspire AppHost wires them. The web host listens on
/// loopback only unless the site is shared on the network (then over HTTPS, with a passcode for
/// other devices); the API always on loopback, on a random port the browser never sees.
/// </summary>
public sealed class SiteHost(DesktopSettings settings) : IDisposable
{
    private static readonly TimeSpan ApiStartTimeout = TimeSpan.FromMinutes(3); // first start creates and migrates the database
    private static readonly TimeSpan WebStartTimeout = TimeSpan.FromMinutes(1);

    private readonly JobObject _job = new();
    private readonly List<(Process Process, string Name)> _processes = [];
    private volatile bool _stopping;

    /// <summary>Raised (on a worker thread) when a host exits while the site is meant to be running.</summary>
    public event Action<SiteException>? Stopped;

    public static string LogFolder => Path.Combine(DesktopSettings.Folder, "logs");

    /// <summary>While sharing on the network: the certificate authority devices trust.</summary>
    public LanCertificateFiles? Lan { get; private set; }

    public async Task StartAsync(IProgress<string> progress, CancellationToken ct)
    {
        // A fresh key each run: it only has to be shared by the two processes started below.
        var signingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var urls = new List<string> { settings.Url };
        var web = new Dictionary<string, string>
        {
            ["AllowedHosts"] = "localhost;127.0.0.1",
        };

        if (settings.PhonePort > 0)
        {
            // The phone reaches the computer by its network address, so host filtering can't be
            // limited to localhost; the web host only signs in loopback requests to a localhost
            // name instead (DesktopOwnerHandler), and serves nothing but upload pages on this port.
            urls.Add($"http://0.0.0.0:{settings.PhonePort}");
            web["AllowedHosts"] = "*";
            web["ReceiptRelay__LanPort"] = settings.PhonePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (settings.IsSharing)
        {
            // Every address, over HTTPS. Other devices sign in with the household passcode; this
            // computer is still signed in by being on loopback (DesktopOwnerHandler).
            progress.Report("Preparing the certificate for other devices…");
            var lan = Lan = await Task.Run(() => LanSharing.Prepare(settings), ct);
            urls[0] = $"https://*:{settings.Port}";
            web["AllowedHosts"] = "*";
            web["Kestrel__Certificates__Default__Path"] = lan.ServerPfxPath;
            web["Kestrel__Certificates__Default__Password"] = lan.Password;
            web["Desktop__Lan__PasscodeHash"] = settings.LanPasscodeHash!;
            web["Desktop__Lan__CertificatePath"] = LanSharing.AuthorityCerPath;
            web["Desktop__Lan__Port"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture); // plain HTTP there goes to https://
        }

        web["ASPNETCORE_URLS"] = string.Join(';', urls);
        await WaitForFreePortsAsync(ct);

        if (settings.Storage != StorageMode.Browser)
        {
            var apiUrl = $"http://127.0.0.1:{FreePort()}";
            var ownerEmail = $"{settings.OwnerId}@desktop.mapleledger.local";
            Directory.CreateDirectory(DesktopSettings.Folder);
            var apiEnvironment = new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = apiUrl,
                ["AllowedHosts"] = "localhost;127.0.0.1",
                ["Database__Provider"] = "Sqlite",
                ["ConnectionStrings__mapleledger"] = $"Data Source={DesktopSettings.DatabasePath}",
                ["Auth__SigningKey"] = signingKey,
                ["Premium__Emails"] = ownerEmail,
            };

            // An older install kept its ledger in SQL Server: the API copies it in on this start.
            var moveFrom = settings.Storage == StorageMode.SqlServer ? OldConnectionString(settings) : null;
            if (moveFrom is not null) apiEnvironment["Database__ImportFrom"] = moveFrom;
            progress.Report(moveFrom is not null
                ? "Moving your ledger from SQL Server into LoonieBooks' own database…"
                : "Opening your ledger…");

            var api = Start("api", "MapleLedger.ApiService.exe", apiEnvironment);
            await WaitUntilReadyAsync(api, "api", apiUrl + "/", ApiStartTimeout, ct);

            if (settings.Storage == StorageMode.SqlServer)
            {
                settings.Storage = StorageMode.Database;
                settings.ConnectionString = null;
                settings.Save();
                LauncherLog.Write(moveFrom is not null
                    ? $"Moved the ledger from SQL Server into {DesktopSettings.DatabasePath} (see api.log for the row counts). The SQL Server database wasn't changed."
                    : "Switched from SQL Server to LoonieBooks' own database; the SQL Server connection couldn't be read, so nothing was copied.");
            }

            web["services__ledger-api__http__0"] = apiUrl;
            web["Auth__SigningKey"] = signingKey;
            web["Desktop__OwnerId"] = settings.OwnerId;
            web["Desktop__OwnerEmail"] = ownerEmail;
            web["Desktop__OwnerName"] = Environment.UserName;
        }

        progress.Report("Starting LoonieBooks…");
        var site = Start("web", "MapleLedger.Web.exe", web);
        await WaitUntilReadyAsync(site, "web", settings.Url + "/account/me", WebStartTimeout, ct, Lan?.ServerThumbprint);
    }

    public void Stop()
    {
        _stopping = true;
        foreach (var (process, _) in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }

        _processes.Clear();
    }

    public void Dispose()
    {
        Stop();
        Lan?.Authority.Dispose();
        _job.Dispose();
    }

    private Process Start(string name, string exe, Dictionary<string, string> environment)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, name);
        var path = Path.Combine(folder, exe);
        if (!File.Exists(path))
        {
            throw new SiteException($"This copy of LoonieBooks is incomplete: {Path.Combine(name, exe)} is missing. Rebuild it with desktopbuild.bat.");
        }

        var start = new ProcessStartInfo(path)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Nothing from the launcher's own environment should change how the hosts are wired.
        foreach (var key in start.Environment.Keys.ToList())
        {
            if (key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
                || key.Contains("__", StringComparison.Ordinal))
            {
                start.Environment.Remove(key);
            }
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        Directory.CreateDirectory(LogFolder);
        var log = TextWriter.Synchronized(new StreamWriter(Path.Combine(LogFolder, name + ".log"), append: false) { AutoFlush = true });

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
        process.Exited += (_, _) =>
        {
            try
            {
                // Let the output handlers drain before the log is read or closed.
                process.WaitForExit();
                log.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // Stop() already disposed it.
            }

            if (!_stopping)
            {
                Stopped?.Invoke(new SiteException($"LoonieBooks' {name} host stopped unexpectedly.", LogTail(name)));
            }
        };

        process.Start();
        _job.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add((process, name));
        return process;
    }

    private static async Task WaitUntilReadyAsync(Process process, string name, string url, TimeSpan timeout, CancellationToken ct, string? serverThumbprint = null)
    {
        // Over HTTPS, accept exactly the certificate just made, whether or not Windows trusts it yet.
        using var handler = new HttpClientHandler();
        if (serverThumbprint is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                string.Equals(certificate?.Thumbprint, serverThumbprint, StringComparison.OrdinalIgnoreCase);
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                // Give the log writer a moment to flush the reason.
                await Task.Delay(300, ct);
                throw name == "api" && ApiStopMessage(process.ExitCode) is { } upgrade
                    ? new SiteException(upgrade, LogTail(name)) { Retry = false }
                    : new SiteException(name == "api" ? "LoonieBooks couldn't open its database." : "LoonieBooks couldn't start.", LogTail(name));
            }

            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await Task.Delay(250, ct);
        }

        throw new SiteException($"LoonieBooks' {name} host didn't start within {timeout.TotalSeconds:0} seconds.", LogTail(name));
    }

    /// <summary>Why the API stopped while updating the ledger (<see cref="ApiExitCodes"/>), or null for other failures.</summary>
    public static string? ApiStopMessage(int exitCode)
    {
        var backups = Path.Combine(DesktopSettings.Folder, "backups");
        return exitCode switch
        {
            ApiExitCodes.UpgradeFailed =>
                "This version of LoonieBooks couldn't update your ledger, so it put the ledger back exactly as it was: " +
                $"nothing was lost, and your previous version still opens it. A copy from just before is in {backups}. " +
                "The reason is below and in the log folder.",
            ApiExitCodes.UpgradeFailedNotRestored =>
                "This version of LoonieBooks couldn't update your ledger, and couldn't put it back either. Don't start it again yet: " +
                $"a copy from just before the update is in {backups}, and the details below say how to put it back.",
            ApiExitCodes.LedgerFromNewerVersion =>
                "Your ledger was last opened by a newer version of LoonieBooks, so this older version won't open it (it wasn't changed). " +
                "Open the newer version instead.",
            _ => null,
        };
    }

    /// <summary>The last lines of a host's log, which hold the reason it stopped.</summary>
    public static string? LogTail(string name, int lines = 15)
    {
        var path = Path.Combine(LogFolder, name + ".log");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(Environment.NewLine, all.TakeLast(lines).Select(l => l.TrimEnd('\r')));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? OldConnectionString(DesktopSettings settings)
    {
        try
        {
            return settings.ConnectionString;
        }
        catch (CryptographicException)
        {
            // Protected for another Windows user.
            return null;
        }
    }

    /// <summary>
    /// Waits briefly for the site's ports: a copy that was just closed (such as the previous version,
    /// replaced by this one) can take a moment to let go of them. Then says plainly which is taken,
    /// rather than leaving it to the web host's log.
    /// </summary>
    private async Task WaitForFreePortsAsync(CancellationToken ct)
    {
        int[] ports = settings.PhonePort > 0 ? [settings.Port, settings.PhonePort] : [settings.Port];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (ports.FirstOrDefault(IsListening) is var taken and > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SiteException($"Port {taken} is in use by another program, so LoonieBooks can't start. If another copy of LoonieBooks " +
                    "is open, close it; otherwise close the program using the port (or restart the computer).");
            }

            await Task.Delay(250, ct);
        }
    }

    /// <summary>Whether something on this computer listens on the port. Reads the table rather than binding, which could set off a firewall prompt.</summary>
    private static bool IsListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
