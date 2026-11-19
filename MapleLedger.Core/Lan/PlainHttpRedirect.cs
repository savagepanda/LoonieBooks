using System.Net;
using System.Text;

namespace MapleLedger.Core.Lan;

/// <summary>
/// A shared desktop install serves HTTPS only, but a device that opens <c>192.168.1.20:7085</c>
/// without typing <c>https://</c> sends plain HTTP to that port. Its first byte tells the two apart
/// (a TLS connection starts with a handshake record); a plain request is answered with a redirect
/// to the same host, port and path over HTTPS.
/// </summary>
public static class PlainHttpRedirect
{
    /// <summary>How much of a plain request is read to find its Host and path.</summary>
    public const int MaxHeadLength = 8 * 1024;

    /// <summary>The first byte of every TLS connection: a handshake record (the ClientHello).</summary>
    public static bool IsTlsHandshake(byte first) => first == 0x16;

    /// <summary>Whether <paramref name="data"/> holds a whole request head (it ends with a blank line).</summary>
    public static bool HasWholeHead(ReadOnlySpan<byte> data) => data.IndexOf("\r\n\r\n"u8) >= 0;

    /// <summary>
    /// Where to send a plain HTTP request: https:// on <paramref name="port"/>, with the host it
    /// asked for (or <paramref name="localAddress"/> when its Host isn't a plain name or address) and its
    /// path. Null when the bytes aren't an HTTP request at all.
    /// </summary>
    public static string? Location(ReadOnlySpan<byte> head, IPAddress localAddress, int port)
    {
        var text = Encoding.Latin1.GetString(head[..Math.Min(head.Length, MaxHeadLength)]);
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;

        var target = requestLine[1];
        var path = target.StartsWith('/') && target.All(c => c is > ' ' and < (char)0x7F) ? target : "/";

        string? host = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                host = HostName(line["Host:".Length..].Trim());
                break;
            }
        }

        host ??= Bracketed(localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress);
        return $"https://{host}:{port}{path}";
    }

    /// <summary>The response that sends the browser to <paramref name="location"/> and closes the connection.</summary>
    public static byte[] Response(string location) => Encoding.ASCII.GetBytes(
        "HTTP/1.1 302 Found\r\n"
        + $"Location: {location}\r\n"
        + "Cache-Control: no-store\r\n"
        + "Content-Length: 0\r\n"
        + "Connection: close\r\n"
        + "\r\n");

    /// <summary>The host part of a Host header (without its port), or null unless it's a plain DNS name or IP address.</summary>
    private static string? HostName(string value)
    {
        string name;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0) return null;
            name = value[1..close];
            return IPAddress.TryParse(name, out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? Bracketed(v6)
                : null;
        }

        var colon = value.IndexOf(':');
        name = colon >= 0 ? value[..colon] : value;
        return Uri.CheckHostName(name) is UriHostNameType.Dns or UriHostNameType.IPv4 ? name : null;
    }

    private static string Bracketed(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address.ToString().Split('%')[0]}]"
            : address.ToString();
}
