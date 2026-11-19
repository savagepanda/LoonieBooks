using System.Net.Http.Json;
using MapleLedger.Core;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Has the server read a receipt with Claude. Premium users only, and only when the server has an
/// Anthropic API key. Unlike in-browser OCR this sends the photo to Anthropic, so callers ask first.
/// </summary>
public sealed class CloudReceiptReader(HttpClient http, UserSession session)
{
    /// <summary>Formats Claude reads: photos (not HEIC) and PDFs.</summary>
    public static bool CanRead(string contentType) => contentType is "image/jpeg" or "image/png" or "image/webp" or "application/pdf";

    private bool? _available;

    public async Task<bool> IsAvailableAsync()
    {
        if (!session.IsPremium) return false;
        if (_available is { } known) return known;

        try
        {
            _available = (await http.GetFromJsonAsync<ReceiptReaderStatus>("api/receipts/reader"))?.Available == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            _available = false;
        }

        return _available.Value;
    }

    public async Task<ReceiptFields> ReadAsync(byte[] content, string contentType, string fileName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/receipts/read")
        {
            Content = JsonContent.Create(new UploadReceiptRequest(fileName, contentType, content)),
        };
        request.Headers.Add(ServerLedgerStore.RequestHeader, "1");
        using var response = await http.SendAsync(request);
        await ServerLedgerStore.EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ReceiptFields>() ?? new ReceiptFields(null, null, null);
    }
}
