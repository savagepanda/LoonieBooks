using Microsoft.JSInterop;

namespace MapleLedger.Web.Client.Services;

/// <summary>Saves files generated in the browser (backups, CSV exports, receipts) as downloads.</summary>
public sealed class FileDownloader(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task DownloadAsync(string fileName, string contentType, string content)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/files.js");
        await _module.InvokeVoidAsync("downloadText", fileName, contentType, content);
    }

    public async Task DownloadBytesAsync(string fileName, string contentType, byte[] content)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/files.js");
        await _module.InvokeVoidAsync("downloadBytes", fileName, contentType, content);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
