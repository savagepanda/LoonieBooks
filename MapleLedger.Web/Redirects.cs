namespace MapleLedger.Web;

internal static class Redirects
{
    /// <summary>Only ever return to a page on this site after signing in.</summary>
    public static string LocalOnly(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
