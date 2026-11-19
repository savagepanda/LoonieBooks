using System.Net;
using System.Security.Cryptography.X509Certificates;
using MapleLedger.Core.Lan;

namespace MapleLedger.UnitTests;

public class LanTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> Names = LanCertificates.HostNames("FAMILY-PC", null);
    private static readonly IPAddress[] Addresses = [IPAddress.Loopback, IPAddress.Parse("192.168.1.20")];

    // ---------------------------------------------------------------- passcode

    [Fact]
    public void OnlyTheRightPasscodeMatchesItsHash()
    {
        var hash = LanPasscode.Hash("maple syrup pancakes");

        Assert.DoesNotContain("maple", hash);
        Assert.True(LanPasscode.Verify("maple syrup pancakes", hash));
        Assert.False(LanPasscode.Verify("maple syrup pancake", hash));
        Assert.False(LanPasscode.Verify("", hash));
        Assert.False(LanPasscode.Verify("maple syrup pancakes", "not a hash"));
        Assert.False(LanPasscode.Verify("maple syrup pancakes", "pbkdf2-sha256$1$%%%$%%%"));
    }

    [Fact]
    public void ChoosingThePasscodeAgainSignsDevicesOut()
    {
        // Same passcode, new salt: devices signed in with the old one must enter it again.
        var first = LanPasscode.Hash("maple syrup pancakes");
        var second = LanPasscode.Hash("maple syrup pancakes");

        Assert.Equal(LanPasscode.Stamp(first), LanPasscode.Stamp(first));
        Assert.NotEqual(LanPasscode.Stamp(first), LanPasscode.Stamp(second));
    }

    // ---------------------------------------------------------------- guessing

    [Fact]
    public void ADeviceIsLockedOutAfterAFewWrongPasscodesForAWhile()
    {
        var clock = new Clock();
        var throttle = new PasscodeThrottle(clock);
        var guesser = IPAddress.Parse("192.168.1.66");
        var family = IPAddress.Parse("192.168.1.30");

        for (var i = 0; i < PasscodeThrottle.PerAddress; i++)
        {
            Assert.False(throttle.IsLockedOut(guesser));
            throttle.RecordFailure(guesser);
        }

        Assert.True(throttle.IsLockedOut(guesser));
        Assert.True(throttle.IsLockedOut(IPAddress.Parse("::ffff:192.168.1.66")));
        Assert.False(throttle.IsLockedOut(family));

        clock.Now += PasscodeThrottle.Window;
        Assert.False(throttle.IsLockedOut(guesser));
    }

    [Fact]
    public void ChangingAddressDoesntGetPastTheOverallLimit()
    {
        var clock = new Clock();
        var throttle = new PasscodeThrottle(clock);

        for (var i = 0; i < PasscodeThrottle.Overall; i++)
        {
            throttle.RecordFailure(IPAddress.Parse($"10.0.{i / 250}.{i % 250 + 1}"));
        }

        Assert.True(throttle.IsLockedOut(IPAddress.Parse("10.9.9.9")));
        clock.Now += PasscodeThrottle.Window;
        Assert.False(throttle.IsLockedOut(IPAddress.Parse("10.9.9.9")));
    }

    [Fact]
    public void TheRightPasscodeClearsThatDevicesMistakes()
    {
        var throttle = new PasscodeThrottle(new Clock());
        var phone = IPAddress.Parse("192.168.1.31");
        for (var i = 0; i < PasscodeThrottle.PerAddress - 1; i++) throttle.RecordFailure(phone);

        throttle.RecordSuccess(phone);
        throttle.RecordFailure(phone);

        Assert.False(throttle.IsLockedOut(phone));
    }

    // ---------------------------------------------------------------- this computer

    [Theory]
    // Loopback, by any of this computer's names or a loopback address.
    [InlineData("127.0.0.1", "127.0.0.1", "localhost", true)]
    [InlineData("::1", "::1", "[::1]", true)]
    [InlineData("::1", "::1", "family-pc", true)]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1", "LOCALHOST", true)]
    // This computer to its own network address, by name, .local name or address.
    [InlineData("192.168.1.20", "192.168.1.20", "family-pc", true)]
    [InlineData("192.168.1.20", "192.168.1.20", "Family-PC.local", true)]
    [InlineData("192.168.1.20", "192.168.1.20", "192.168.1.20", true)]
    [InlineData("::ffff:192.168.1.20", "192.168.1.20", "family-pc", true)]
    // Another device, whatever it calls this computer.
    [InlineData("192.168.1.31", "192.168.1.20", "family-pc", false)]
    [InlineData("192.168.1.31", "192.168.1.20", "localhost", false)]
    [InlineData("192.168.1.31", "192.168.1.20", "192.168.1.20", false)]
    // DNS rebinding: this computer's own sockets, but under someone else's name.
    [InlineData("127.0.0.1", "127.0.0.1", "rebind.example", false)]
    [InlineData("192.168.1.20", "192.168.1.20", "family-pc.rebind.example", false)]
    [InlineData("192.168.1.20", "192.168.1.20", "localhost.rebind.example", false)]
    public void OnlyThisComputerByItsOwnNameSkipsThePasscode(string remote, string local, string host, bool expected)
    {
        Assert.Equal(expected, LanRequests.IsFromThisComputer(IPAddress.Parse(remote), IPAddress.Parse(local), host, Names));
    }

    [Fact]
    public void ARequestWithoutAnAddressIsNotThisComputer()
    {
        Assert.False(LanRequests.IsFromThisComputer(null, IPAddress.Loopback, "localhost", Names));
    }

    // ---------------------------------------------------------------- certificates

    [Fact]
    public void TheComputerIsReachableByItsNameItsLocalNameAndLocalhost()
    {
        Assert.Equal(["localhost", "family-pc", "family-pc.local"], Names);
        Assert.Equal(["localhost", "family-pc", "family-pc.local", "family-pc.home.arpa"], LanCertificates.HostNames("Family-PC", "home.arpa."));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("fe80::1", false)]
    public void OnlyLoopbackAndPrivateAddressesGoInTheCertificate(string address, bool permitted) =>
        Assert.Equal(permitted, LanCertificates.IsPermitted(IPAddress.Parse(address)));

    [Fact]
    public void TheServerCertificateIsTrustedThroughTheAuthorityForTheComputersNames()
    {
        using var authority = LanCertificates.CreateAuthority("FAMILY-PC", Names, Now);
        using var server = LanCertificates.CreateServer(authority, Names, Addresses, Now);

        Assert.True(server.HasPrivateKey);
        Assert.True(server.NotAfter.ToUniversalTime() - Now.UtcDateTime <= TimeSpan.FromDays(825));
        Assert.Equal("1.3.6.1.5.5.7.3.1", server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages[0].Value);
        var names = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(Names, names.EnumerateDnsNames());
        Assert.Equal(["127.0.0.1", "192.168.1.20"], names.EnumerateIPAddresses().Select(a => a.ToString()));

        using var chain = TrustingOnly(authority);
        Assert.True(chain.Build(server), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Fact]
    public void TheAuthorityCantVouchForOtherWebsites()
    {
        // Name constraints: whoever copies the authority's key still can't impersonate a bank to a
        // phone that trusts it.
        using var authority = LanCertificates.CreateAuthority("FAMILY-PC", Names, Now);
        var constraints = authority.Extensions["2.5.29.30"];
        Assert.NotNull(constraints);
        Assert.True(constraints.Critical);

        using var forged = LanCertificates.CreateServer(authority, ["localhost", "www.example-bank.com"], [IPAddress.Parse("8.8.8.8")], Now);
        Assert.Empty(forged.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses()); // public addresses are left out

        using var chain = TrustingOnly(authority);
        Assert.False(chain.Build(forged));
        Assert.Contains(chain.ChainStatus, s => s.Status.HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint));
    }

    [Fact]
    public void TheServerCertificateIsRemadeWhenTheComputersAddressOrNameChanges()
    {
        using var authority = LanCertificates.CreateAuthority("FAMILY-PC", Names, Now);
        using var other = LanCertificates.CreateAuthority("FAMILY-PC", Names, Now);
        using var server = LanCertificates.CreateServer(authority, Names, Addresses, Now);

        Assert.True(LanCertificates.IsCurrent(server, authority, Names, Addresses, Now));
        Assert.True(LanCertificates.IsCurrent(server, authority, Names, [.. Addresses.Reverse()], Now));
        Assert.False(LanCertificates.IsCurrent(server, authority, Names, [IPAddress.Loopback, IPAddress.Parse("192.168.1.21")], Now));
        Assert.False(LanCertificates.IsCurrent(server, authority, LanCertificates.HostNames("NEW-PC", null), Addresses, Now));
        Assert.False(LanCertificates.IsCurrent(server, other, Names, Addresses, Now));
        Assert.False(LanCertificates.IsCurrent(server, authority, Names, Addresses, Now + LanCertificates.ServerLifetime - LanCertificates.RenewBefore + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void TheFingerprintReadsLikeAPhoneShowsIt() =>
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", LanCertificates.Fingerprint(LanCertificates.CreateAuthority("FAMILY-PC", Names, Now)));

    private static X509Chain TrustingOnly(X509Certificate2 authority)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert)));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.UtcDateTime.AddHours(1);
        return chain;
    }

    // ---------------------------------------------------------------- connect a phone

    [Fact]
    public void APairingCodeWorksOnceAndOnlyUntilItExpires()
    {
        var clock = new Clock();
        var codes = new PairingCodes(clock);

        var (code, expiresAt) = codes.Create();
        Assert.Equal(clock.Now + PairingCodes.Lifetime, expiresAt);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", code); // 32 random bytes, safe in a URL path
        Assert.False(codes.Redeem(code + "x"));
        Assert.False(codes.Redeem(null));
        Assert.True(codes.Redeem(code));
        Assert.False(codes.Redeem(code));

        var (late, _) = codes.Create();
        clock.Now += PairingCodes.Lifetime;
        Assert.False(codes.Redeem(late));
    }

    [Fact]
    public void OnlyAFewPairingCodesAreOpenAtOnce()
    {
        var clock = new Clock();
        var codes = new PairingCodes(clock);
        var made = new List<string>();
        for (var i = 0; i <= PairingCodes.MaxOpen; i++)
        {
            made.Add(codes.Create().Code);
            clock.Now += TimeSpan.FromSeconds(1);
        }

        Assert.False(codes.Redeem(made[0])); // the oldest made way for the newest
        Assert.All(made.Skip(1), code => Assert.True(codes.Redeem(code)));
    }

    // ---------------------------------------------------------------- plain HTTP to the HTTPS port

    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.20");

    private static string? Redirect(string head) =>
        PlainHttpRedirect.Location(System.Text.Encoding.Latin1.GetBytes(head), Local, 7085);

    [Fact]
    public void TlsAndPlainHttpAreToldApartByTheFirstByte()
    {
        Assert.True(PlainHttpRedirect.IsTlsHandshake(0x16));
        Assert.False(PlainHttpRedirect.IsTlsHandshake((byte)'G'));
        Assert.False(PlainHttpRedirect.HasWholeHead("GET / HTTP/1.1\r\nHost: x\r\n"u8));
        Assert.True(PlainHttpRedirect.HasWholeHead("GET / HTTP/1.1\r\nHost: x\r\n\r\n"u8));
    }

    [Fact]
    public void PlainHttpGoesToTheSameHostPortAndPathOverHttps()
    {
        Assert.Equal("https://192.168.1.20:7085/transactions?month=11",
            Redirect("GET /transactions?month=11 HTTP/1.1\r\nHost: 192.168.1.20:7085\r\nAccept: */*\r\n\r\n"));
        Assert.Equal("https://family-pc.local:7085/",
            Redirect("GET / HTTP/1.1\r\nhost: family-pc.local:7085\r\n\r\n"));
        Assert.Equal("https://[fe80::1]:7085/", Redirect("GET / HTTP/1.1\r\nHost: [fe80::1]:7085\r\n\r\n"));
    }

    [Fact]
    public void ARedirectNeverCarriesAnOddHostOrPath()
    {
        // No usable Host: the address the connection came in on.
        Assert.Equal("https://192.168.1.20:7085/", Redirect("GET / HTTP/1.0\r\n\r\n"));
        Assert.Equal("https://192.168.1.20:7085/", Redirect("GET / HTTP/1.1\r\nHost: a b\r\n\r\n"));
        Assert.Equal("https://192.168.1.20:7085/", Redirect("GET / HTTP/1.1\r\nHost: user@evil.example\r\n\r\n"));
        Assert.Equal("https://192.168.1.20:7085/", Redirect("GET / HTTP/1.1\r\nHost: [not-an-address]\r\n\r\n"));

        // Only an origin-form path: not an absolute URL or one with control characters.
        Assert.Equal("https://family-pc:7085/", Redirect("GET http://elsewhere/ HTTP/1.1\r\nHost: family-pc\r\n\r\n"));
        Assert.Equal("https://family-pc:7085/", Redirect("GET /a\u0001b HTTP/1.1\r\nHost: family-pc\r\n\r\n"));

        // Not HTTP at all: no answer.
        Assert.Null(Redirect("SSH-2.0-OpenSSH_9.6\r\n"));
        Assert.Null(Redirect("\r\n\r\n"));
    }

    [Fact]
    public void TheRedirectResponseClosesTheConnection()
    {
        var response = System.Text.Encoding.ASCII.GetString(PlainHttpRedirect.Response("https://family-pc:7085/"));

        Assert.StartsWith("HTTP/1.1 302 Found\r\n", response);
        Assert.Contains("\r\nLocation: https://family-pc:7085/\r\n", response);
        Assert.Contains("\r\nConnection: close\r\n", response);
        Assert.EndsWith("\r\n\r\n", response);
    }
}
