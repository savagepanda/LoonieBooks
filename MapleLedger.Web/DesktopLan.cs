using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MapleLedger.Core;
using MapleLedger.Core.Lan;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MapleLedger.Web;

/// <summary>
/// A desktop install shared on the home network (<c>Desktop:Lan:*</c>, set only by the launcher).
/// This computer is still signed in by being on loopback; another device enters the household
/// passcode once and then keeps a sign-in cookie, which <see cref="DesktopOwnerHandler"/> accepts
/// as the desktop owner. Choosing a new passcode signs every device out (<see cref="Stamp"/>).
/// </summary>
public sealed class DesktopLan
{
    public const string CookieScheme = "DesktopLan";
    public const string StampClaim = "lan";

    private DesktopLan(string passcodeHash, string? certificatePath)
    {
        PasscodeHash = passcodeHash;
        Stamp = LanPasscode.Stamp(passcodeHash);
        if (certificatePath is { Length: > 0 } && File.Exists(certificatePath))
        {
            Certificate = File.ReadAllBytes(certificatePath);
            using var certificate = X509CertificateLoader.LoadCertificate(Certificate);
            Fingerprint = LanCertificates.Fingerprint(certificate);
        }
    }

    public string PasscodeHash { get; }
    public string Stamp { get; }

    /// <summary>The certificate authority devices install so their browser trusts this site.</summary>
    public byte[]? Certificate { get; }
    public string? Fingerprint { get; }

    public static DesktopLan? From(IConfiguration configuration) =>
        configuration["Desktop:Lan:PasscodeHash"] is { Length: > 0 } hash
            ? new DesktopLan(hash, configuration["Desktop:Lan:CertificatePath"])
            : null;

    private static readonly IReadOnlyList<string> ThisComputer = LanRequests.ThisComputerHostNames();

    /// <summary>
    /// A request from this computer to itself, by localhost, its network name or an address (see
    /// <see cref="LanRequests.IsFromThisComputer"/>). Not a DNS-rebinding page, which reaches this
    /// computer under its own host name.
    /// </summary>
    public static bool IsLocalRequest(HttpContext context) =>
        LanRequests.IsFromThisComputer(
            context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress, context.Request.Host.Host, ThisComputer);
}

public static class DesktopLanEndpoints
{
    public static AuthenticationBuilder AddDesktopLan(this AuthenticationBuilder authentication, DesktopLan lan)
    {
        authentication.Services.AddSingleton(lan);
        authentication.Services.AddSingleton(new PasscodeThrottle(TimeProvider.System));
        authentication.Services.AddSingleton(new PairingCodes(TimeProvider.System));
        return authentication.AddCookie(DesktopLan.CookieScheme, options =>
        {
            options.Cookie.Name = "MapleLedger.Lan";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.LoginPath = "/lan/signin";
        });
    }

    /// <summary>
    /// Another device that hasn't entered the passcode gets the passcode page and nothing else:
    /// not the app (which would quietly keep a separate ledger in its browser), not the API. Phone
    /// upload pages stay open; their links carry their own token.
    /// </summary>
    public static IApplicationBuilder UseDesktopLanGate(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (DesktopLan.IsLocalRequest(context)
                || context.User.Identity?.IsAuthenticated == true
                || path.StartsWithSegments("/lan")
                || path.StartsWithSegments("/r"))
            {
                await next();
                return;
            }

            if (HttpMethods.IsGet(context.Request.Method)
                && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var returnUrl = path + context.Request.QueryString;
                context.Response.Redirect($"/lan/signin?returnUrl={Uri.EscapeDataString(returnUrl)}");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

    public static IEndpointRouteBuilder MapDesktopLan(this IEndpointRouteBuilder app)
    {
        app.MapGet("/lan/signin", (HttpContext context, DesktopLan lan, string? returnUrl) =>
            DesktopLan.IsLocalRequest(context) || context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect(Redirects.LocalOnly(returnUrl))
                : Page(context, lan, returnUrl, error: null, StatusCodes.Status200OK));

        app.MapPost("/lan/signin", async (HttpContext context, DesktopLan lan, PasscodeThrottle throttle) =>
        {
            var form = await context.Request.ReadFormAsync();
            var returnUrl = form["returnUrl"].ToString();
            var address = context.Connection.RemoteIpAddress;

            if (throttle.IsLockedOut(address))
            {
                return Page(context, lan, returnUrl,
                    "Too many wrong passcodes. Wait 15 minutes, then try again.", StatusCodes.Status429TooManyRequests);
            }

            if (!LanPasscode.Verify(form["passcode"].ToString(), lan.PasscodeHash))
            {
                throttle.RecordFailure(address);
                return Page(context, lan, returnUrl, "That isn't the passcode. Check it in the LoonieBooks window on the computer.", StatusCodes.Status401Unauthorized);
            }

            throttle.RecordSuccess(address);
            await SignInDeviceAsync(context, lan);
            return Results.Redirect(Redirects.LocalOnly(returnUrl));
        }).DisableAntiforgery();

        // "Connect a phone": this computer asks for a one-time QR code (same-site only: needs the
        // app's custom header) ...
        app.MapPost("/lan/pair", (HttpContext context, PairingCodes codes) =>
        {
            if (!context.Request.Headers.ContainsKey("X-MapleLedger") || !DesktopLan.IsLocalRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (Receipts.ReceiptRelayEndpoints.LanAddress() is not { } address)
            {
                return Results.Problem("This computer doesn't seem to be on a home network, so a phone has no address to open.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var (code, expiresAt) = codes.Create();
            var url = $"https://{address}:{context.Connection.LocalPort}/lan/pair/{code}";
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new LanPairing(url, expiresAt, Receipts.ReceiptRelayEndpoints.QrSvg(url)));
        }).DisableAntiforgery();

        // ... and the phone that opens it is signed in like one that entered the passcode.
        app.MapGet("/lan/pair/{code}", async (HttpContext context, DesktopLan lan, PairingCodes codes, string code) =>
        {
            context.Response.Headers["Referrer-Policy"] = "no-referrer"; // the code is in the URL
            if (!codes.Redeem(code))
            {
                return Page(context, lan, "/", "This QR code has expired or was already used. Make a new one on the computer (Connect a phone), or enter the household passcode.", StatusCodes.Status410Gone);
            }

            await SignInDeviceAsync(context, lan);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Redirect("/");
        });

        app.MapGet("/lan/certificate", (DesktopLan lan) =>
            lan.Certificate is { } certificate
                ? Results.File(certificate, "application/x-x509-ca-cert", "LoonieBooks.cer")
                : Results.NotFound());

        return app;
    }

    private static Task SignInDeviceAsync(HttpContext context, DesktopLan lan)
    {
        var identity = new ClaimsIdentity([new Claim(DesktopLan.StampClaim, lan.Stamp)], DesktopLan.CookieScheme);
        return context.SignInAsync(DesktopLan.CookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }

    private static IResult Page(HttpContext context, DesktopLan lan, string? returnUrl, string? error, int status)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = $"default-src 'none'; style-src 'nonce-{nonce}'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Results.Content(LanSignInPage.Render(nonce, Redirects.LocalOnly(returnUrl), error, lan.Certificate is not null, lan.Fingerprint), "text/html; charset=utf-8", statusCode: status);
    }
}
