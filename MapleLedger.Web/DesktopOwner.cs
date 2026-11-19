using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MapleLedger.Web;

/// <summary>
/// The single owner of a desktop install. The LoonieBooks launcher (MapleLedger.Desktop) runs this
/// host on the user's own computer and passes the owner in <c>Desktop:Owner*</c>. There is no
/// sign-in: every request from this computer acts as that owner, and so does another device on the
/// network that entered the household passcode, if the install is shared (<see cref="DesktopLan"/>).
/// Null (the normal web deployment) unless all three values are configured.
/// </summary>
public sealed record DesktopOwner(string Id, string Email, string Name)
{
    public static DesktopOwner? From(IConfiguration configuration) =>
        configuration["Desktop:OwnerId"] is { Length: > 0 } id
        && configuration["Desktop:OwnerEmail"] is { Length: > 0 } email
        && configuration["Desktop:OwnerName"] is { Length: > 0 } name
            ? new DesktopOwner(id, email, name)
            : null;
}

/// <summary>
/// Signs in every request from this computer to a localhost address as the
/// <see cref="DesktopOwner"/>, and, when the install is shared on the network, a device with a
/// current passcode cookie. Anything else stays anonymous: a request from another machine (a
/// phone on the receipt-relay port), one that claims to have been forwarded, or one whose Host
/// isn't localhost. That last check stops a DNS-rebinding page, which reaches the loopback address
/// under its own host name. <c>AllowedHosts</c> can't do it, because the phone port is reached by
/// the computer's network address. (Over the network the site is HTTPS, so such a page fails the
/// certificate check, and the cookie belongs to this computer's own name.)
/// </summary>
public sealed class DesktopOwnerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DesktopOwner owner) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Desktop";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (DesktopLan.IsLocalRequest(Context)) return SignedInAsOwner();

        if (Context.RequestServices.GetService<DesktopLan>() is { } lan)
        {
            var device = await Context.AuthenticateAsync(DesktopLan.CookieScheme);
            if (device.Succeeded && device.Principal.FindFirst(DesktopLan.StampClaim)?.Value == lan.Stamp) return SignedInAsOwner();
        }

        return AuthenticateResult.NoResult();
    }

    private AuthenticateResult SignedInAsOwner()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, owner.Id),
                new Claim(ClaimTypes.Email, owner.Email),
                new Claim(ClaimTypes.Name, owner.Name),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
