using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MapleLedger.Web.Client.Services;

/// <summary>The webcam (or a phone's back camera), for photographing receipts.</summary>
public sealed class Camera(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>Receipt photos are scaled so the longer side is at most this many pixels.</summary>
    public const int MaxSide = 2400;

    private const double Quality = 0.85;

    private IJSObjectReference? _module;
    private ElementReference? _video;

    /// <summary>Starts the camera in a &lt;video&gt; element; returns the resolution, e.g. "1920x1080".</summary>
    public async Task<string> StartAsync(ElementReference video)
    {
        _video = video;
        try
        {
            return await (await Module()).InvokeAsync<string>("start", video);
        }
        catch (JSException ex)
        {
            throw new CameraException(ex.Message switch
            {
                var m when m.Contains("NotAllowedError") || m.Contains("SecurityError") =>
                    "The browser blocked the camera. Allow camera access for this site (usually an icon in the address bar), then try again.",
                var m when m.Contains("NotFoundError") || m.Contains("OverconstrainedError") => "No camera was found.",
                var m when m.Contains("NotReadableError") => "The camera is in use by another app. Close it and try again.",
                var m when m.Contains("unsupported") => "This browser can't use a camera here. Choose a photo instead.",
                _ => "Couldn't start the camera.",
            }, ex);
        }
    }

    public async Task StopAsync()
    {
        if (_module is null) return;
        try
        {
            await _module.InvokeVoidAsync("stop", _video);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>The current frame as JPEG bytes.</summary>
    public async Task<byte[]> CaptureAsync(ElementReference video)
    {
        try
        {
            return await (await Module()).InvokeAsync<byte[]>("capture", video, MaxSide, Quality);
        }
        catch (JSException ex)
        {
            throw new CameraException("The camera isn't ready yet. Wait a moment and try again.", ex);
        }
    }

    /// <summary>
    /// One frame of a window, tab or screen the user chooses to share, as PNG bytes. Sharing stops
    /// as soon as the frame is taken.
    /// </summary>
    public async Task<byte[]> CaptureScreenAsync()
    {
        try
        {
            return await (await Module()).InvokeAsync<byte[]>("captureScreen", TimeSpan.FromMinutes(5));
        }
        catch (JSException ex)
        {
            throw new CameraException(ex.Message switch
            {
                var m when m.Contains("NotAllowedError") || m.Contains("AbortError") => "Screen sharing was cancelled.",
                var m when m.Contains("unsupported") || m.Contains("NotSupportedError") =>
                    "This browser can't capture the screen. Take a screenshot (Windows key + Shift + S) and paste it here instead.",
                _ => "Couldn't capture the screen. Take a screenshot (Windows key + Shift + S) and paste it here instead.",
            }, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new CameraException("Screen sharing took too long to start.", ex);
        }
    }

    /// <summary>A large photo re-encoded as a JPEG of at most <see cref="MaxSide"/> pixels.</summary>
    public async Task<byte[]> ShrinkAsync(byte[] photo, string contentType)
    {
        try
        {
            return await (await Module()).InvokeAsync<byte[]>("shrink", photo, contentType, MaxSide, Quality);
        }
        catch (JSException ex)
        {
            throw new CameraException("This browser can't read that photo. Try a JPEG or PNG.", ex);
        }
    }

    private async Task<IJSObjectReference> Module() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/camera.js");

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try
        {
            await _module.InvokeVoidAsync("stop", _video);
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }
}

/// <summary>A camera problem; <see cref="Exception.Message"/> is safe to show.</summary>
public sealed class CameraException(string message, Exception? inner = null) : Exception(message, inner);
