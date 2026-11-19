using System.Security.Claims;
using MapleLedger.Web;
using MapleLedger.Web.Components;
using MapleLedger.Web.Quotes;
using MapleLedger.Web.Receipts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Data protection keys encrypt the sign-in cookie and the antiforgery token. Kept in the
// container filesystem they die with the container, signing everyone out on each deploy, so point
// DataProtection:KeyRingPath at a mounted volume in Docker. Left unset on a dev machine, the
// framework default keeps the ring in the user profile, which is already durable.
var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(Directory.CreateDirectory(keyRingPath))
        .SetApplicationName("MapleLedger.Web");
}

// Run by the desktop launcher with its database: one local owner and no sign-in. Optionally
// shared on the home network, where other devices enter a household passcode.
var desktopOwner = DesktopOwner.From(builder.Configuration);
var desktopLan = desktopOwner is null ? null : DesktopLan.From(builder.Configuration);

// Shared on the network, the site is HTTPS only: a device that opens it without typing https://
// is sent there instead of getting a broken connection.
if (desktopLan is not null
    && int.TryParse(builder.Configuration["Desktop:Lan:Port"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var lanPort))
{
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureEndpointDefaults(listen =>
    {
        if (listen.IPEndPoint?.Port == lanPort) listen.UsePlainHttpRedirect();
    }));
}

// Registered users sign in with Google. Without Google credentials configured the app still
// works: everyone is anonymous and keeps their ledger in the browser.
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var signInAvailable = desktopOwner is null && !string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret);

var authentication = builder.Services.AddAuthentication(desktopOwner is null ? CookieAuthenticationDefaults.AuthenticationScheme : DesktopOwnerHandler.SchemeName)
    .AddCookie(options =>
    {
        options.Cookie.Name = "MapleLedger.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.LoginPath = "/account/login";

        // API calls get a status code, not a redirect to Google.
        options.Events.OnRedirectToLogin = context => StatusOrRedirect(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => StatusOrRedirect(context, StatusCodes.Status403Forbidden);
    });

if (signInAvailable)
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = googleClientId!;
        options.ClientSecret = googleClientSecret!;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    });
}

if (desktopOwner is not null)
{
    builder.Services.AddSingleton(desktopOwner);
    authentication.AddScheme<AuthenticationSchemeOptions, DesktopOwnerHandler>(DesktopOwnerHandler.SchemeName, null);
    if (desktopLan is not null) authentication.AddDesktopLan(desktopLan);
}

builder.Services.AddAuthorization();

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// /api and /fdx are forwarded to the API service, found through Aspire service discovery. The
// API itself is never exposed; it only trusts the per-request token added below.
builder.Services.AddHttpForwarderWithServiceDiscovery();

// "Send from phone" for the receipt scanner: photos pass through in memory, never stored.
builder.Services.AddReceiptRelay(builder.Configuration);

// Option prices from Yahoo Finance, which browsers can't fetch themselves (see OptionQuoteEndpoints).
builder.Services.AddOptionQuotes();

var app = builder.Build();

// Phone uploads for the receipt scanner can have a network port of their own; it serves nothing else.
app.UseReceiptRelayPhonePort();

// Trust X-Forwarded-Proto / X-Forwarded-For from the Cloudflare tunnel so the app sees the
// public scheme and client address rather than the container's.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

    // The default HSTS value is 30 days. Not for the desktop app: HSTS would hold for every port of
    // this computer's name, including the plain-HTTP phone port.
    if (desktopOwner is null) app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirectionExceptPhonePort();

// The sign-in cookie rides along on same-site requests, so state-changing API calls must also
// carry a custom header. A cross-site page can't add one without a CORS preflight, which fails.
app.Use(async (context, next) =>
{
    if (IsApiPath(context.Request.Path) && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
        && !context.Request.Headers.ContainsKey("X-MapleLedger"))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    await next();
});

app.UseAuthentication();
if (desktopLan is not null) app.UseDesktopLanGate();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapReceiptRelay();
app.MapOptionQuotes();
if (desktopLan is not null) app.MapDesktopLan();

// ---------------------------------------------------------------- account

app.MapGet("/account/login", (string? returnUrl) =>
    signInAvailable
        ? Results.Challenge(new AuthenticationProperties { RedirectUri = Redirects.LocalOnly(returnUrl) }, [GoogleDefaults.AuthenticationScheme])
        : Results.Redirect("/"));

// A form post from the page. Logging someone out is the worst a forged request could do.
app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    if (desktopLan is not null) await context.SignOutAsync(DesktopLan.CookieScheme);
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapGet("/account/me", (ClaimsPrincipal user, HttpContext context) => new
{
    signInAvailable,
    desktop = desktopOwner is not null,
    // Another device on a shared desktop install: it can sign out (forget the passcode).
    lanDevice = desktopLan is not null && !DesktopLan.IsLocalRequest(context),
    // The computer sharing the site: it can show a QR code that connects a phone.
    lanHost = desktopLan is not null && DesktopLan.IsLocalRequest(context),
    isAuthenticated = user.Identity?.IsAuthenticated == true,
    name = user.FindFirstValue(ClaimTypes.Name),
    email = user.FindFirstValue(ClaimTypes.Email),
});

// ---------------------------------------------------------------- API forwarding

var signingKey = InternalApiToken.SigningKey(app.Configuration);

void ForwardAsSignedInUser(Yarp.ReverseProxy.Transforms.Builder.TransformBuilderContext transforms) =>
    transforms.AddRequestTransform(context =>
    {
        // The API authenticates the user from our token alone; the browser's cookie and any
        // Authorization header it sent never reach it.
        context.ProxyRequest.Headers.Remove("Cookie");
        context.ProxyRequest.Headers.Authorization = null;

        var user = context.HttpContext.User;
        if (signingKey is not null
            && user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } subject
            && user.FindFirstValue(ClaimTypes.Email) is { Length: > 0 } email)
        {
            var token = InternalApiToken.Create(signingKey, subject, email, user.FindFirstValue(ClaimTypes.Name));
            context.ProxyRequest.Headers.Authorization = new("Bearer", token);
        }

        return ValueTask.CompletedTask;
    });

// Signed-in users only; the API then decides whether they are premium. API responses must reach
// the client as-is, not be swapped for the HTML not-found page.
app.MapForwarder("/api/{**catch-all}", "https+http://ledger-api", ForwardAsSignedInUser)
    .RequireAuthorization()
    .WithMetadata(new SkipStatusCodePagesAttribute());
app.MapForwarder("/fdx/{**catch-all}", "https+http://ledger-api", ForwardAsSignedInUser)
    .RequireAuthorization()
    .WithMetadata(new SkipStatusCodePagesAttribute());

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(MapleLedger.Web.Client._Imports).Assembly);

app.MapDefaultEndpoints();

app.Run();

static bool IsApiPath(PathString path) => path.StartsWithSegments("/api") || path.StartsWithSegments("/fdx");

static Task StatusOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int status)
{
    if (IsApiPath(context.Request.Path))
    {
        context.Response.StatusCode = status;
    }
    else
    {
        context.Response.Redirect(context.RedirectUri);
    }

    return Task.CompletedTask;
}

