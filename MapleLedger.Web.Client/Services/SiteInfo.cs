namespace MapleLedger.Web.Client.Services;

/// <summary>What the site this client was loaded from can do.</summary>
public static class SiteInfo
{
    /// <summary>
    /// False for the static site (<c>site\publish.ps1</c>, built with <c>-p:StaticSite=true</c>): these files
    /// on a plain web server with no LoonieBooks web host behind them. Nobody signs in there, the ledger
    /// stays in the browser, and nothing that needs the web host (Send from phone) is offered.
    /// </summary>
    public static bool HasWebHost { get; } =
#if STATIC_SITE
        false;
#else
        true;
#endif

    /// <summary>The latest Windows download, offered by the static site (the browser demo).</summary>
    public const string WindowsDownloadUrl = "https://github.com/savagepanda/LoonieBooks/releases/latest";

    /// <summary>The privacy page on the project's website: what's kept where, and what's fetched from other sites.</summary>
    public const string PrivacyUrl = "https://looniebooks.ca/privacy";
}
