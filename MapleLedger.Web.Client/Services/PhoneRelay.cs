using System.Net;
using System.Net.Http.Json;
using MapleLedger.Core;
using MapleLedger.Core.Receipts;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// "Send from phone": asks the web host for a one-time QR code and collects the photo the phone
/// sends to it. The photo only passes through the web host's memory on its way to this browser.
/// </summary>
public sealed class PhoneRelay(HttpClient http)
{
    public async Task<ReceiptRelaySession> StartAsync()
    {
        using var response = await Send(HttpMethod.Post, "relay");
        await ServerLedgerStore.EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ReceiptRelaySession>() ?? throw new LedgerStoreException("The server returned an empty response.");
    }

    /// <summary>The photo once the phone has sent it; null while waiting.</summary>
    /// <exception cref="LedgerStoreException">The link expired or was cancelled.</exception>
    public async Task<RelayedReceipt?> CheckAsync(ReceiptRelaySession session, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, $"relay/{Uri.EscapeDataString(session.PickupToken)}", ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new LedgerStoreException("The QR code expired. Make a new one to try again.");
        await ServerLedgerStore.EnsureSuccess(response);

        var name = response.Headers.TryGetValues("X-File-Name", out var names) ? Uri.UnescapeDataString(names.First()) : "receipt";
        var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new RelayedReceipt(name, type, await response.Content.ReadAsByteArrayAsync(ct));
    }

    public async Task CancelAsync(ReceiptRelaySession session)
    {
        try
        {
            using var _ = await Send(HttpMethod.Delete, $"relay/{Uri.EscapeDataString(session.PickupToken)}");
        }
        catch (HttpRequestException)
        {
            // It expires on its own anyway.
        }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ServerLedgerStore.RequestHeader, "1");
        return await http.SendAsync(request, ct);
    }
}
