using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MapleLedger.Core.Lan;

/// <summary>
/// HTTPS for a desktop install shared on the home network. Browsers only treat
/// <c>http://localhost</c> as secure, and the browser storage and the secure sign-in cookie need a
/// secure address, so other devices need HTTPS. The launcher makes its own small certificate
/// authority, which each device trusts once, and from it a server certificate for this computer's
/// names and addresses that can be remade whenever they change without asking the devices again.
/// <para>
/// The authority carries name constraints: it can only vouch for this computer's names, localhost
/// and private network addresses, so trusting it on a phone doesn't let anyone who copies its key
/// impersonate other websites to that phone.
/// </para>
/// </summary>
public static class LanCertificates
{
    /// <summary>Apple devices reject server certificates valid for more than 825 days.</summary>
    public static readonly TimeSpan ServerLifetime = TimeSpan.FromDays(800);

    public static readonly TimeSpan AuthorityLifetime = TimeSpan.FromDays(3650);

    /// <summary>Remade this long before it expires.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>Loopback and the private IPv4 ranges home networks use.</summary>
    public static IReadOnlyList<(IPAddress Network, int PrefixLength)> PermittedRanges { get; } =
    [
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
    ];

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string NameConstraintsOid = "2.5.29.30";

    /// <summary>
    /// The names other devices can reach this computer by: its name (Windows PCs), name.local
    /// (phones and Macs, by mDNS), and its full name when it is on a domain.
    /// </summary>
    public static IReadOnlyList<string> HostNames(string machineName, string? domain)
    {
        var machine = machineName.Trim().ToLowerInvariant();
        var names = new List<string> { "localhost", machine, machine + ".local" };
        if (!string.IsNullOrWhiteSpace(domain)) names.Add($"{machine}.{domain.Trim().Trim('.').ToLowerInvariant()}");
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    public static bool IsPermitted(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var value = ToUInt32(address);
        return PermittedRanges.Any(r => (value & Mask(r.PrefixLength)) == ToUInt32(r.Network));
    }

    public static X509Certificate2 CreateAuthority(string machineName, IReadOnlyCollection<string> hostNames, DateTimeOffset now)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=LoonieBooks on {Escape(machineName)}, O=LoonieBooks", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints(hostNames));
        return request.CreateSelfSigned(now.AddDays(-1), now + AuthorityLifetime);
    }

    public static X509Certificate2 CreateServer(
        X509Certificate2 authority, IReadOnlyCollection<string> hostNames, IReadOnlyCollection<IPAddress> addresses, DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Escape(hostNames.First(n => n != "localhost"))}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));

        var names = new SubjectAlternativeNameBuilder();
        foreach (var name in hostNames) names.AddDnsName(name);
        foreach (var address in addresses.Where(IsPermitted)) names.AddIpAddress(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        request.CertificateExtensions.Add(names.Build());

        var notAfter = now + ServerLifetime;
        if (notAfter > authority.NotAfter) notAfter = new DateTimeOffset(authority.NotAfter.ToUniversalTime());
        using var withoutKey = request.Create(authority, now.AddDays(-1), notAfter, RandomNumberGenerator.GetBytes(16));
        return withoutKey.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Whether a server certificate can be kept: issued by this authority, not about to expire,
    /// and for exactly these names and addresses.
    /// </summary>
    public static bool IsCurrent(
        X509Certificate2 server, X509Certificate2 authority, IReadOnlyCollection<string> hostNames, IReadOnlyCollection<IPAddress> addresses, DateTimeOffset now)
    {
        if (server.Issuer != authority.Subject || server.NotAfter.ToUniversalTime() - now.UtcDateTime < RenewBefore) return false;

        var authorityKeyId = authority.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier;
        var issuedBy = server.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        if (authorityKeyId is null || issuedBy is not { } id || !Convert.FromHexString(authorityKeyId).AsSpan().SequenceEqual(id.Span)) return false;

        var san = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return false;
        var wantedAddresses = addresses.Where(IsPermitted).Select(a => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString()).Order(StringComparer.Ordinal);
        return san.EnumerateDnsNames().Order(StringComparer.Ordinal).SequenceEqual(hostNames.Order(StringComparer.Ordinal))
            && san.EnumerateIPAddresses().Select(a => a.ToString()).Order(StringComparer.Ordinal).SequenceEqual(wantedAddresses);
    }

    /// <summary>The SHA-256 fingerprint as phones and browsers show it, e.g. "AB:CD:…".</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c)));

    /// <summary>
    /// RFC 5280 name constraints: only these DNS names (and their subdomains) and the private
    /// address ranges.
    /// </summary>
    private static X509Extension NameConstraints(IEnumerable<string> hostNames)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            foreach (var name in hostNames)
            {
                using (writer.PushSequence())
                {
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
                }
            }

            foreach (var (network, prefix) in PermittedRanges)
            {
                using (writer.PushSequence())
                {
                    var mask = BitConverter.GetBytes(Mask(prefix));
                    if (BitConverter.IsLittleEndian) Array.Reverse(mask);
                    writer.WriteOctetString([.. network.GetAddressBytes(), .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
                }
            }
        }

        return new X509Extension(new Oid(NameConstraintsOid), writer.Encode(), critical: true);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static uint Mask(int prefixLength) => prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);

    /// <summary>Keeps a computer name from adding parts to a distinguished name.</summary>
    private static string Escape(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ').ToArray());
}
