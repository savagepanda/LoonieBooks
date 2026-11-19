using System.Net;
using System.Net.NetworkInformation;

namespace MapleLedger.Core.Lan;

/// <summary>
/// Which requests to a shared desktop install come from the computer running it, and so act as
/// its owner without the household passcode.
/// </summary>
public static class LanRequests
{
    /// <summary>The names this computer answers to: localhost, its name, name.local and name.domain.</summary>
    public static IReadOnlyList<string> ThisComputerHostNames() =>
        LanCertificates.HostNames(Environment.MachineName, DomainName());

    /// <summary>
    /// A request this computer made to itself: over loopback, or to one of its own network addresses
    /// (the connection then comes from the address it went to, which another device can't do). The
    /// Host must be one of this computer's names or an IP address: a DNS-rebinding page reaches the
    /// same sockets under its own host name (even one like <c>name.attacker.example</c>), and that
    /// still needs the passcode.
    /// </summary>
    public static bool IsFromThisComputer(IPAddress? remote, IPAddress? local, string host, IReadOnlyCollection<string> hostNames)
    {
        if (remote is null) return false;
        remote = Normalize(remote);
        var sameComputer = IPAddress.IsLoopback(remote) || (local is not null && remote.Equals(Normalize(local)));
        if (!sameComputer) return false;

        host = host.Trim().TrimEnd('.');
        return IPAddress.TryParse(host.Trim('[', ']'), out _)
            || hostNames.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? DomainName()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().DomainName;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
