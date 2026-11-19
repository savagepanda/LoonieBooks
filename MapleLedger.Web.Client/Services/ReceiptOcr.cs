using MapleLedger.Core.Import;
using Microsoft.JSInterop;

namespace MapleLedger.Web.Client.Services;

/// <summary>Reads text from receipt photos and statement pictures in the browser (Tesseract.js, English and French).</summary>
public sealed class ReceiptOcr(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    /// <summary>Photo formats the browser can decode for OCR (not HEIC).</summary>
    public static bool CanRead(string contentType) => contentType is "image/jpeg" or "image/png" or "image/webp";

    /// <param name="progress">Called with a status ("loading language traineddata", "recognizing text") and 0 to 1.</param>
    public async Task<string> ReadTextAsync(byte[] photo, string contentType, Action<string, double>? progress = null)
    {
        using var reporter = DotNetObjectReference.Create(new ProgressReporter(progress));
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/ocr.js");
            return await _module.InvokeAsync<string>("recognize", TimeSpan.FromMinutes(3), photo, contentType, reporter);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException)
        {
            throw new CameraException("Couldn't read the receipt. Check the photo is sharp and well lit, or type the details in.", ex);
        }
    }

    /// <summary>
    /// The words in a picture of a statement and where they are, for
    /// <see cref="PdfStatementReader.ReadWords"/> to rebuild its transaction table from.
    /// </summary>
    public async Task<IReadOnlyList<StatementWord>> ReadStatementAsync(byte[] picture, string contentType, Action<string, double>? progress = null)
    {
        using var reporter = DotNetObjectReference.Create(new ProgressReporter(progress));
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/ocr.js");
            var words = await _module.InvokeAsync<OcrWord[]>("recognizeWords", TimeSpan.FromMinutes(5), picture, contentType, reporter);
            return words.Select(w => new StatementWord(w.Text, w.Left, w.Right, w.Baseline, w.Size)).ToList();
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException)
        {
            throw new CameraException("Couldn't read the picture. Check it's sharp and well lit, or paste the transactions as text.", ex);
        }
    }

    private sealed record OcrWord(string Text, double Left, double Right, double Baseline, double Size);

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try
        {
            await _module.InvokeVoidAsync("terminate");
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private sealed class ProgressReporter(Action<string, double>? progress)
    {
        [JSInvokable]
        public void OnOcrProgress(string status, double value) => progress?.Invoke(status, value);
    }
}
