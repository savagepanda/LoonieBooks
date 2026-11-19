using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using MapleLedger.Core;
using MapleLedger.Core.Receipts;
using MapleLedger.Data;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace MapleLedger.Web.Receipts;

/// <summary>
/// "Send from phone": the scanner page shows a QR code, the phone opens a small upload page from
/// it, and the photo is handed to the computer's browser. Served by the web host (never the API),
/// held in memory for minutes, and never stored on the server.
/// </summary>
public static class ReceiptRelayEndpoints
{
    /// <summary>The largest photo or PDF accepted, matching the receipt limit.</summary>
    private const int MaxBytes = LedgerService.MaxReceiptBytes;

    /// <param name="BaseUrl">The address phones use, e.g. http://192.168.1.20:7080 (needed in Docker).</param>
    /// <param name="LanPort">A port that listens on the network for phone uploads only (local runs).</param>
    public sealed record Options(string? BaseUrl, int? LanPort);

    public static IServiceCollection AddReceiptRelay(this IServiceCollection services, IConfiguration configuration)
    {
        var lanPort = configuration.GetValue<int?>("ReceiptRelay:LanPort");
        services.AddSingleton(new Options(configuration["ReceiptRelay:BaseUrl"], lanPort is > 0 ? lanPort : null));
        services.AddSingleton(sp => new ReceiptRelayStore(TimeProvider.System));
        services.AddSingleton<CreationThrottle>();
        return services;
    }

    /// <summary>
    /// The phone port answers phone upload pages and nothing else, over plain HTTP: the rest of
    /// the app (and its sign-in cookie) is never served there.
    /// </summary>
    public static IApplicationBuilder UseReceiptRelayPhonePort(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<Options>();
        return app.Use(async (context, next) =>
        {
            if (IsPhonePort(context, options) && !context.Request.Path.StartsWithSegments("/r"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
    }

    /// <summary>HTTPS redirection everywhere except the phone port, which a phone reaches by plain HTTP.</summary>
    public static IApplicationBuilder UseHttpsRedirectionExceptPhonePort(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<Options>();
        return app.UseWhen(context => !IsPhonePort(context, options), branch => branch.UseHttpsRedirection());
    }

    private static bool IsPhonePort(HttpContext context, Options options) =>
        options.LanPort is { } port && context.Connection.LocalPort == port;

    public static IEndpointRouteBuilder MapReceiptRelay(this IEndpointRouteBuilder app)
    {
        var skipStatusPages = new SkipStatusCodePagesAttribute();

        // ---------------------------------------------------------------- the computer

        // Starts a session and returns the QR code. Same-site only: needs the app's custom header.
        app.MapPost("/relay", (HttpContext context, ReceiptRelayStore store, Options options, CreationThrottle throttle) =>
        {
            if (!context.Request.Headers.ContainsKey("X-MapleLedger")) return Results.BadRequest();
            if (!throttle.Allow(context.Connection.RemoteIpAddress))
            {
                return Results.Problem("Too many QR codes in a short time. Wait a minute and try again.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            var baseUrl = ReceiptRelayStore.PhoneBaseUrl(
                options.BaseUrl, context.Request.Scheme, context.Request.Host.Host, context.Request.Host.Port, LanAddress(), options.LanPort);
            if (baseUrl is null)
            {
                return Results.Problem(
                    "Send from phone needs an address your phone can reach. Open LoonieBooks by this computer's network address, or set ReceiptRelay:BaseUrl.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (store.Create() is not { } ticket)
            {
                return Results.Problem("Too many phones are sending right now. Try again in a few minutes.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var uploadUrl = $"{baseUrl}/r/{ticket.UploadToken}";
            return Results.Ok(new ReceiptRelaySession(uploadUrl, ticket.PickupToken, ticket.ExpiresAt, QrSvg(uploadUrl)));
        }).AllowAnonymous().DisableAntiforgery().WithMetadata(skipStatusPages);

        // The computer polls: 204 while waiting, the photo once it has arrived, 404 when expired.
        app.MapGet("/relay/{pickup}", (HttpContext context, string pickup, ReceiptRelayStore store) =>
        {
            if (!context.Request.Headers.ContainsKey("X-MapleLedger")) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return store.TryPickUp(pickup, out var file) switch
            {
                ReceiptRelayStore.PickupState.Waiting => Results.NoContent(),
                ReceiptRelayStore.PickupState.Ready => FileResult(context, file!),
                _ => Results.NotFound(),
            };
        }).AllowAnonymous().WithMetadata(skipStatusPages);

        app.MapDelete("/relay/{pickup}", (HttpContext context, string pickup, ReceiptRelayStore store) =>
        {
            if (!context.Request.Headers.ContainsKey("X-MapleLedger")) return Results.BadRequest();
            store.Cancel(pickup);
            return Results.NoContent();
        }).AllowAnonymous().DisableAntiforgery().WithMetadata(skipStatusPages);

        // ---------------------------------------------------------------- the phone

        app.MapGet("/r/{upload}", (HttpContext context, string upload, ReceiptRelayStore store) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer"; // the token is in the URL
            headers["X-Robots-Tag"] = "noindex";
            headers["X-Content-Type-Options"] = "nosniff";
            headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; img-src blob:; connect-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";

            return store.IsOpen(upload)
                ? Results.Content(PhonePage.Upload(nonce), "text/html; charset=utf-8")
                : Results.Content(PhonePage.Closed(nonce), "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone);
        }).AllowAnonymous().WithMetadata(skipStatusPages);

        app.MapPost("/r/{upload}", async (HttpContext context, string upload, ReceiptRelayStore store, CancellationToken ct) =>
        {
            if (!store.IsOpen(upload)) return Results.Problem("This link has expired or was already used. Make a new QR code on your computer.", statusCode: StatusCodes.Status410Gone);
            if (context.Request.ContentLength is > MaxBytes) return Results.Problem("That file is larger than 5 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxBytes) return Results.Problem("That file is larger than 5 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);

            string contentType;
            try
            {
                contentType = LedgerService.CheckReceiptFile(context.Request.ContentType?.Split(';')[0], buffer.ToArray());
            }
            catch (LedgerValidationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            var name = Uri.UnescapeDataString(context.Request.Headers["X-File-Name"].ToString());
            var file = new RelayedReceipt(string.IsNullOrWhiteSpace(name) ? "receipt" : name, contentType, buffer.ToArray());
            return store.Deposit(upload, file) switch
            {
                ReceiptRelayStore.DepositResult.Accepted => Results.Ok(),
                ReceiptRelayStore.DepositResult.AlreadyUsed => Results.Problem("A photo was already sent with this link.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem("This link has expired. Make a new QR code on your computer.", statusCode: StatusCodes.Status410Gone),
            };
        }).AllowAnonymous().DisableAntiforgery().WithMetadata(skipStatusPages, new RequestSizeLimitAttribute(MaxBytes + 64 * 1024));

        return app;
    }

    private static IResult FileResult(HttpContext context, RelayedReceipt file)
    {
        context.Response.Headers["X-File-Name"] = Uri.EscapeDataString(file.FileName);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Bytes(file.Content, file.ContentType);
    }

    internal static string QrSvg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>
    /// This computer's address on the local network: an IPv4 private address on an interface that
    /// is up and has a gateway (so not a virtual switch for VMs, WSL or Docker).
    /// </summary>
    internal static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                .Select(a => a.ToString())
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>At most six QR codes a minute from one address.</summary>
    public sealed class CreationThrottle
    {
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new();

        public bool Allow(IPAddress? address)
        {
            var key = address?.ToString() ?? "unknown";
            var now = DateTimeOffset.UtcNow;
            var queue = _recent.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
            lock (queue)
            {
                while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1)) queue.Dequeue();
                if (queue.Count >= 6) return false;
                queue.Enqueue(now);
                return true;
            }
        }
    }
}
