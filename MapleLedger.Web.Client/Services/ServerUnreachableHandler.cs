namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Turns "the request never reached the server" into a message people can act on. The browser
/// reports it as "TypeError: NetworkError when attempting to fetch resource" (Firefox) or
/// "Failed to fetch" (Chrome), which says nothing about what to do. The page itself keeps working
/// without the server, so this is what people see when it has stopped: in the desktop app, most
/// often because its window was closed.
/// </summary>
public sealed class ServerUnreachableHandler(UserSession session) : DelegatingHandler(new HttpClientHandler())
{
    private string Message => session.IsDesktop
        ? session.IsLanDevice
            ? "Can't reach LoonieBooks. Its window on the computer that runs it may have been closed, or this device left the network: open LoonieBooks there, then reload this page."
            : "Can't reach LoonieBooks. Its window on this computer may have been closed: open LoonieBooks again, then reload this page."
        : "Can't reach LoonieBooks. Check your internet connection, then try again.";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null && !cancellationToken.IsCancellationRequested)
        {
            // Still an HttpRequestException, so every page's existing error handling shows it.
            throw new HttpRequestException(Message, ex);
        }
    }
}
