using MapleLedger.Core.RealEstate;

namespace MapleLedger.Web.Client.Services;

/// <summary>What one refresh of the properties' house price index found.</summary>
/// <param name="Book">The feed, for the list of areas a property can be in; empty when it couldn't be had.</param>
/// <param name="ByProperty">Each property with a value and a city or area, and the region found for it (null when none was).</param>
/// <param name="Applied">What changed in the ledger's readings.</param>
/// <param name="Unreachable">The feed couldn't be downloaded just now (an earlier copy may still have been used).</param>
/// <param name="SaveFailed">Saving the index to the ledger failed.</param>
public sealed record HpiRefresh(HpiBook Book, IReadOnlyDictionary<string, HpiRegion?> ByProperty, ApplyHpiResult Applied, bool Unreachable, bool SaveFailed)
{
    private static readonly IReadOnlyDictionary<string, HpiRegion?> None = new Dictionary<string, HpiRegion?>();

    public static HpiRefresh Off { get; } = new(HpiBook.Empty, None, new ApplyHpiResult(0, 0), false, false);

    public bool Changed => Applied.Readings > 0;

    /// <summary>The region found for a property; null when none was, or it wasn't looked up.</summary>
    public HpiRegion? RegionOf(string propertyId) => ByProperty.GetValueOrDefault(propertyId);

    /// <summary>Whether a property was looked up and no region was found for its city or area.</summary>
    public bool Unmatched(string propertyId) => ByProperty.TryGetValue(propertyId, out var region) && region is null;
}

/// <summary>
/// Keeps the properties' values current from ownquity.com's feed of the CREA MLS® Home Price
/// Index, straight from the browser. The whole feed (every region, every month since 2005) is
/// downloaded and each property's city or area is looked up in it here, so where the properties
/// are is never sent; the request is the same for everyone. The region's months are saved as the
/// property's area's readings, which carry its value forward to today and back through net worth
/// by month. <c>HousePriceIndex:FeedUrl</c> in the client's appsettings.json points it elsewhere,
/// and an empty value turns it off.
/// </summary>
public sealed class HpiFeed(ILedgerStore store, IConfiguration config, ILogger<HpiFeed> logger)
{
    // Not the app's HttpClient: that one points at this site and reports it unreachable on failure.
    // The browser's HTTP cache handles the feed's ETag, so an unchanged feed is a 304.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>How long a download is reused. The index is published once a month.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    // Kept across pages for the life of the tab.
    private static HpiBook? _book;
    private static DateTimeOffset? _fetchedAt;

    public string? FeedUrl => config["HousePriceIndex:FeedUrl"] is { } url ? (string.IsNullOrWhiteSpace(url) ? null : url) : OwnquityHpi.DefaultUrl;

    public bool Enabled => FeedUrl is not null;

    /// <summary>
    /// Downloads the feed (unless the last download is under six hours old), finds each valued
    /// property's region, and saves the index for the ones whose readings changed. With
    /// <paramref name="forAreaList"/> false nothing is downloaded for a ledger without valued
    /// properties; the Accounts page passes true so a new property's city can be picked from the list.
    /// </summary>
    public async Task<HpiRefresh> RefreshAsync(bool forAreaList = false, CancellationToken ct = default)
    {
        if (FeedUrl is not { } url) return HpiRefresh.Off;

        var properties = (await store.GetPropertiesAsync(ct))
            .Where(p => p.Value is not null && !string.IsNullOrWhiteSpace(p.City))
            .ToList();
        if (properties.Count == 0 && !forAreaList) return HpiRefresh.Off;

        var (book, reached) = await GetBookAsync(url, ct);
        if (book is null) return HpiRefresh.Off with { Unreachable = true };

        var byProperty = properties.ToDictionary(p => p.Id, p => book.Find(p.City));
        var series = properties
            .Where(p => byProperty[p.Id] is not null)
            .Select(p => new HpiSeries(PropertyValuation.CleanText(p.City), byProperty[p.Id]!.From, byProperty[p.Id]!.Index))
            .DistinctBy(s => s.Area.ToUpperInvariant())
            .ToList();

        var result = new HpiRefresh(book, byProperty, new ApplyHpiResult(0, 0), !reached, false);
        if (series.Count == 0) return result;

        try
        {
            return result with { Applied = await store.ApplyHpiAsync(new ApplyHpiRequest(series)) };
        }
        catch (Exception ex) when (ex is LedgerStoreException or HttpRequestException)
        {
            logger.LogWarning("Couldn't save the house price index: {Message}", ex.Message);
            return result with { SaveFailed = true };
        }
    }

    /// <summary>
    /// The feed, downloaded at most every <see cref="MaxAge"/>, and whether the download worked.
    /// When it doesn't, this tab's last copy is used, if it has one.
    /// </summary>
    private async Task<(HpiBook? Book, bool Reached)> GetBookAsync(string url, CancellationToken ct)
    {
        if (_book is { Count: > 0 } cached && DateTimeOffset.UtcNow - _fetchedAt < MaxAge) return (cached, true);

        try
        {
            var book = OwnquityHpi.Parse(await Http.GetStringAsync(url, ct));
            if (book.Count == 0)
            {
                logger.LogInformation("The house price index feed at {Url} had no regions in it.", url);
                return (_book, false);
            }

            (_book, _fetchedAt) = (book, DateTimeOffset.UtcNow);
            return (book, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Couldn't get the house price index from {Url}: {Message}", url, ex.Message);
            return (_book, false);
        }
    }
}
