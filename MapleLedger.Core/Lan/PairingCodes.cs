using System.Security.Cryptography;

namespace MapleLedger.Core.Lan;

/// <summary>
/// "Connect a phone" on a shared desktop install: the computer shows a QR code holding a one-time
/// link, and the device that opens it is signed in as if it had entered the household passcode.
/// Each code works once, for a few minutes, and only a few are open at a time.
/// </summary>
public sealed class PairingCodes(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxOpen = 5;

    private readonly Dictionary<string, DateTimeOffset> _open = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>A new code and when it expires. Making one when <see cref="MaxOpen"/> are open drops the oldest.</summary>
    public (string Code, DateTimeOffset ExpiresAt) Create()
    {
        var code = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            Trim(now);
            while (_open.Count >= MaxOpen) _open.Remove(_open.MinBy(p => p.Value).Key);

            var expiresAt = now + Lifetime;
            _open[code] = expiresAt;
            return (code, expiresAt);
        }
    }

    /// <summary>Whether <paramref name="code"/> is open; it can't be used again either way.</summary>
    public bool Redeem(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        lock (_lock)
        {
            Trim(clock.GetUtcNow());
            return _open.Remove(code);
        }
    }

    private void Trim(DateTimeOffset now)
    {
        foreach (var (code, expiresAt) in _open.ToList())
        {
            if (expiresAt <= now) _open.Remove(code);
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
