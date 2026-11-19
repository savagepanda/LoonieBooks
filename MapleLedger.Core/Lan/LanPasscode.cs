using System.Security.Cryptography;
using System.Text;

namespace MapleLedger.Core.Lan;

/// <summary>
/// The household passcode that lets other devices on the network open a desktop install. The
/// launcher keeps only a salted PBKDF2 hash and hands that to the web host, which checks it.
/// Format: <c>pbkdf2-sha256$iterations$salt$hash</c> (base64).
/// </summary>
public static class LanPasscode
{
    public const int MinLength = 8;
    private const int Iterations = 600_000;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string passcode)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passcode), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string passcode, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passcode), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A short value that changes with the passcode. Devices keep it in their sign-in cookie, so
    /// choosing a new passcode signs every device out.
    /// </summary>
    public static string Stamp(string stored) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored)), 0, 12);
}
