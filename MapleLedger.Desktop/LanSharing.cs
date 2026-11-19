using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MapleLedger.Core.Lan;

namespace MapleLedger.Desktop;

/// <summary>The certificate files the web host serves HTTPS with while sharing on the network.</summary>
/// <param name="ServerPfxPath">This computer's server certificate and key.</param>
/// <param name="Password">The password of the PFX files.</param>
/// <param name="Authority">The certificate authority (public part) that devices trust.</param>
/// <param name="ServerThumbprint">For the launcher's own readiness check.</param>
public sealed record LanCertificateFiles(string ServerPfxPath, string Password, X509Certificate2 Authority, string ServerThumbprint);

/// <summary>
/// Keeps the files behind <see cref="LanCertificates"/> in %LOCALAPPDATA%\MapleLedger: the
/// authority (made once, trusted by each device) and the server certificate (remade when this
/// computer's names or network addresses change, or it nears expiry).
/// </summary>
public static class LanSharing
{
    /// <summary>The authority's public certificate, which devices install. Also served by the sign-in page.</summary>
    public static string AuthorityCerPath => Path.Combine(DesktopSettings.Folder, "lan-ca.cer");

    private static string AuthorityPfxPath => Path.Combine(DesktopSettings.Folder, "lan-ca.pfx");
    private static string ServerPfxPath => Path.Combine(DesktopSettings.Folder, "lan-server.pfx");

    public static IReadOnlyList<string> HostNames() =>
        LanRequests.ThisComputerHostNames();

    /// <summary>This computer's private IPv4 addresses (and loopback), for devices that use an address.</summary>
    public static IReadOnlyList<IPAddress> Addresses()
    {
        var addresses = new List<IPAddress> { IPAddress.Loopback };
        try
        {
            addresses.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && LanCertificates.IsPermitted(a)));
        }
        catch (NetworkInformationException)
        {
        }

        return addresses.DistinctBy(a => a.ToString()).ToList();
    }

    /// <summary>Makes or reuses the certificates, saving the settings if anything new was made.</summary>
    public static LanCertificateFiles Prepare(DesktopSettings settings)
    {
        string password;
        try
        {
            password = settings.LanCertificatePassword ?? NewPassword(settings);
        }
        catch (CryptographicException)
        {
            // Protected for another Windows user: start again with new certificates.
            password = NewPassword(settings);
        }

        var now = DateTimeOffset.UtcNow;
        var names = HostNames();
        var addresses = Addresses();
        Directory.CreateDirectory(DesktopSettings.Folder);

        var authority = Load(AuthorityPfxPath, password);
        if (authority is null
            || settings.LanAuthorityNames is null
            || !names.All(settings.LanAuthorityNames.Contains)
            || authority.NotAfter.ToUniversalTime() - now.UtcDateTime < LanCertificates.RenewBefore)
        {
            authority?.Dispose();
            authority = LanCertificates.CreateAuthority(Environment.MachineName, names, now);
            File.WriteAllBytes(AuthorityPfxPath, authority.Export(X509ContentType.Pkcs12, password));
            File.WriteAllBytes(AuthorityCerPath, authority.Export(X509ContentType.Cert));
            settings.LanAuthorityNames = [.. names];
            settings.Save();
            File.Delete(ServerPfxPath);
        }
        else if (!File.Exists(AuthorityCerPath))
        {
            File.WriteAllBytes(AuthorityCerPath, authority.Export(X509ContentType.Cert));
        }

        using (authority)
        {
            var server = Load(ServerPfxPath, password);
            if (server is null || !LanCertificates.IsCurrent(server, authority, names, addresses, now))
            {
                server?.Dispose();
                server = LanCertificates.CreateServer(authority, names, addresses, now);
                File.WriteAllBytes(ServerPfxPath, server.Export(X509ContentType.Pkcs12, password));
            }

            using (server)
            {
                return new LanCertificateFiles(
                    ServerPfxPath, password, X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert)), server.Thumbprint);
            }
        }
    }

    /// <summary>Whether this Windows user's browsers already trust the authority.</summary>
    public static bool IsTrustedHere(X509Certificate2 authority)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, authority.Thumbprint, validOnly: false).Count > 0;
    }

    /// <summary>
    /// Adds the authority to this Windows user's trusted roots. Windows asks the user to confirm;
    /// false if they said no.
    /// </summary>
    public static bool TrustHere(X509Certificate2 authority)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(authority);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string NewPassword(DesktopSettings settings)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        settings.LanCertificatePassword = password;
        settings.LanAuthorityNames = null; // the old files can't be opened any more
        settings.Save();
        return password;
    }

    private static X509Certificate2? Load(string path, string password)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
