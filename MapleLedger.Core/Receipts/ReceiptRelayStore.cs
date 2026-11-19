using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MapleLedger.Core.Receipts;

/// <summary>A receipt photo handed over by a phone, waiting for the computer that asked for it.</summary>
public sealed record RelayedReceipt(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Hands receipt photos from a phone to the computer showing the QR code. Held in memory only, for
/// minutes: the photo is given to that computer's browser once and then forgotten, so nothing is
/// stored on the server (free users' ledgers stay in their browser).
/// </summary>
/// <remarks>
/// Each session has two unguessable tokens: the phone gets the upload token (in the QR code), and
/// only the computer that created the session knows the pickup token. Each accepts one photo.
/// </remarks>
public sealed class ReceiptRelayStore(TimeProvider time, TimeSpan? lifetime = null, int maxSessions = 20)
{
    public TimeSpan Lifetime { get; } = lifetime ?? TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Session> _byUpload = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Session> _byPickup = new(StringComparer.Ordinal);

    public sealed record Ticket(string UploadToken, string PickupToken, DateTimeOffset ExpiresAt);

    public enum PickupState
    {
        Waiting,
        Ready,
        Gone,
    }

    public enum DepositResult
    {
        Accepted,
        AlreadyUsed,
        Gone,
    }

    /// <summary>Starts a session; null when too many are already open.</summary>
    public Ticket? Create()
    {
        RemoveExpired();
        if (_byUpload.Count >= maxSessions) return null;

        var session = new Session(NewToken(), NewToken(), time.GetUtcNow() + Lifetime);
        _byUpload[session.UploadToken] = session;
        _byPickup[session.PickupToken] = session;
        return new Ticket(session.UploadToken, session.PickupToken, session.ExpiresAt);
    }

    /// <summary>Whether a phone can still send to this upload token.</summary>
    public bool IsOpen(string uploadToken) =>
        _byUpload.TryGetValue(uploadToken, out var s) && !Expired(s) && s.File is null;

    public DepositResult Deposit(string uploadToken, RelayedReceipt file)
    {
        if (!_byUpload.TryGetValue(uploadToken, out var session) || Expired(session)) return DepositResult.Gone;

        lock (session)
        {
            if (session.File is not null) return DepositResult.AlreadyUsed;
            session.File = file;
            return DepositResult.Accepted;
        }
    }

    /// <summary>Gives the photo to the computer once; the session ends when it does.</summary>
    public PickupState TryPickUp(string pickupToken, out RelayedReceipt? file)
    {
        file = null;
        if (!_byPickup.TryGetValue(pickupToken, out var session) || Expired(session))
        {
            if (session is not null) Remove(session);
            return PickupState.Gone;
        }

        lock (session)
        {
            if (session.File is null) return PickupState.Waiting;
            file = session.File;
        }

        Remove(session);
        return PickupState.Ready;
    }

    public void Cancel(string pickupToken)
    {
        if (_byPickup.TryGetValue(pickupToken, out var session)) Remove(session);
    }

    public int OpenSessions
    {
        get
        {
            RemoveExpired();
            return _byUpload.Count;
        }
    }

    private bool Expired(Session s) => time.GetUtcNow() >= s.ExpiresAt;

    private void RemoveExpired()
    {
        foreach (var session in _byUpload.Values.Where(Expired).ToList()) Remove(session);
    }

    private void Remove(Session session)
    {
        _byUpload.TryRemove(session.UploadToken, out _);
        _byPickup.TryRemove(session.PickupToken, out _);
        session.File = null;
    }

    /// <summary>128 bits, URL-safe.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Session(string uploadToken, string pickupToken, DateTimeOffset expiresAt)
    {
        public string UploadToken { get; } = uploadToken;
        public string PickupToken { get; } = pickupToken;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public RelayedReceipt? File { get; set; }
    }

    /// <summary>
    /// The address a phone should use to reach this site: a configured one; else the address the
    /// computer itself is using, unless that is localhost (which on a phone means the phone);
    /// else this computer's network address on the phone port. Null when there is none.
    /// </summary>
    public static string? PhoneBaseUrl(string? configured, string scheme, string host, int? hostPort, string? lanAddress, int? lanPort)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().TrimEnd('/');
        if (!IsLoopback(host)) return $"{scheme}://{FormatHost(host)}{(hostPort is { } p ? $":{p}" : "")}";
        if (lanAddress is not null && lanPort is { } port) return $"http://{FormatHost(lanAddress)}:{port}";
        return null;
    }

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));

    private static string FormatHost(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
