using MapleLedger.Core.Investments;

namespace MapleLedger.Web.Client.Services;

/// <summary>What one refresh of the holdings' month-end prices did.</summary>
/// <param name="Months">Month files downloaded (the browser may have answered some from its cache).</param>
/// <param name="Applied">Month-end closes added or changed in the ledger.</param>
/// <param name="Unreachable">The feed couldn't be downloaded just now.</param>
public sealed record PriceHistoryRefresh(int Months, ApplyPriceHistoryResult Applied, bool Unreachable)
{
    public static PriceHistoryRefresh None { get; } = new(0, new ApplyPriceHistoryResult(0), false);

    public bool Changed => Applied.Readings > 0;
}

/// <summary>
/// Fills in the holdings' month-end closes from stocktreemap.com's monthly price history feed, so
/// net worth by month follows the market. Straight from the browser, and only for the months the
/// chart shows: each is one small file shared by everyone, the holdings are looked up in it here,
/// and nothing about the ledger is sent. A month already saved for every holding isn't downloaded
/// again. <c>PriceHistory:BaseUrl</c> in the client's appsettings.json points it elsewhere, and an
/// empty value turns it off.
/// </summary>
public sealed class PriceHistoryFeed(ILedgerStore store, QuoteFeed quotes, IConfiguration config, Preferences preferences, ILogger<PriceHistoryFeed> logger)
{
    // Not the app's HttpClient: that one points at this site and reports it unreachable on failure.
    // The browser's HTTP cache keeps the files (a past month for a day), and answers repeats itself.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>How long the list of symbols is reused in this tab.</summary>
    public static readonly TimeSpan SymbolsMaxAge = TimeSpan.FromHours(1);

    /// <summary>The most months one refresh reads: ten years of a chart.</summary>
    public const int MaxMonths = 120;

    /// <summary>
    /// Months already looked up for a listing the file didn't have, with how many symbols the list had
    /// then: a listing past that count was added since, so its month is read again.
    /// </summary>
    private const string MissesPreference = "priceHistory.misses";

    private static HistorySymbols? _symbols;
    private static DateTimeOffset? _symbolsAt;

    // Listings re-read this tab because their price jumped like a split: once is enough.
    private static readonly HashSet<string> Rechecked = new(StringComparer.OrdinalIgnoreCase);

    public string? BaseUrl => config["PriceHistory:BaseUrl"] is { } url
        ? (string.IsNullOrWhiteSpace(url) ? null : url.EndsWith('/') ? url : url + "/")
        : StockTreeMapHistory.DefaultBaseUrl;

    public bool Enabled => BaseUrl is not null && quotes.Enabled;

    /// <summary>
    /// Downloads the month files from <paramref name="from"/> to <paramref name="to"/> that a holding
    /// still needs a close from (months that have ended, before the holding's current price), and
    /// saves the closes found. Nothing is downloaded for a ledger without listed holdings.
    /// </summary>
    public async Task<PriceHistoryRefresh> RefreshAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (BaseUrl is not { } baseUrl) return PriceHistoryRefresh.None;

        var snapshots = await store.GetHoldingsAsync(ct);
        var holdings = snapshots
            .SelectMany(s => s.Holdings.Where(h => HoldingPriceMoves.Priceable(h) && h.Price is not null).Select(h => (Snapshot: s, Holding: h)))
            .ToList();
        if (holdings.Count == 0) return PriceHistoryRefresh.None;

        var book = await quotes.GetBookAsync(ct);
        if (book is null) return PriceHistoryRefresh.None with { Unreachable = true };

        // Each listing held, under the feed's symbol, with the month its current price is from.
        var today = Periods.Today;
        var held = holdings
            .Select(x => (x.Holding, x.Snapshot, Match: QuoteSymbols.Resolve(x.Holding, book, x.Snapshot.AsOf, today)))
            .Where(x => x.Match.Quote is not null)
            .Select(x => new Listing(
                x.Holding.Symbol!, x.Holding.Currency, x.Match.QuoteSymbol!, x.Holding.Price!.Value,
                HoldingPriceMoves.FirstOfMonth(x.Holding.PriceAsOf is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : x.Snapshot.AsOf)))
            .DistinctBy(l => (l.Symbol.ToUpperInvariant(), l.Currency))
            .ToList();
        if (held.Count == 0) return PriceHistoryRefresh.None;

        var months = HoldingPriceMoves.MonthsInView(from, to, today, MaxMonths);
        if (months.Count == 0) return PriceHistoryRefresh.None;

        var saved = (await store.GetPriceHistoryAsync(ct))
            .GroupBy(r => (r.Symbol.ToUpperInvariant(), r.Currency, r.Month))
            .ToDictionary(g => g.Key, g => g.Last().Close);
        var misses = await preferences.GetAsync<Dictionary<string, Dictionary<string, int>>>(MissesPreference) ?? [];

        // A price far from last month's saved close looks like a split the feed has since adjusted for:
        // its months are read again (once a tab).
        var resplit = held
            .Where(l => !Rechecked.Contains(l.FeedSymbol))
            .Where(l => saved
                .Where(s => s.Key.Item1 == l.Symbol.ToUpperInvariant() && s.Key.Currency == l.Currency && s.Key.Month < l.PricedIn)
                .OrderByDescending(s => s.Key.Month)
                .Select(s => (decimal?)s.Value)
                .FirstOrDefault() is { } last && HoldingPriceMoves.LooksSplit(l.Price, last))
            .Select(l => l.FeedSymbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Every month shown already has a close for every holding: nothing to download, not even the list.
        if (!months.Any(m => held.Any(l => m < l.PricedIn && (resplit.Contains(l.FeedSymbol) || !saved.ContainsKey((l.Symbol.ToUpperInvariant(), l.Currency, m))))))
        {
            return PriceHistoryRefresh.None;
        }

        var symbols = await GetSymbolsAsync(baseUrl, ct);
        if (symbols is null) return PriceHistoryRefresh.None with { Unreachable = true };

        bool Needs(Listing l, DateOnly month)
        {
            if (month >= l.PricedIn || symbols.IndexOf(l.FeedSymbol) is not { } index) return false;
            if (resplit.Contains(l.FeedSymbol)) return true;
            if (saved.ContainsKey((l.Symbol.ToUpperInvariant(), l.Currency, month))) return false;
            return !(misses.GetValueOrDefault(Key(month))?.TryGetValue(l.FeedSymbol, out var count) == true && index < count);
        }

        var readings = new List<PriceReading>();
        int downloaded = 0;
        var unreachable = false;
        foreach (var month in months.Where(m => held.Any(l => Needs(l, m))))
        {
            var file = await GetMonthAsync(baseUrl, month, ct);
            if (file is null)
            {
                unreachable |= !_lastWasNotFound;
                continue;
            }

            downloaded++;
            foreach (var l in held.Where(l => Needs(l, month)))
            {
                var index = symbols.IndexOf(l.FeedSymbol)!.Value;
                if (file.CloseAt(index) is { } close)
                {
                    readings.Add(new PriceReading(l.Symbol, l.Currency, month, close));
                }
                else
                {
                    // Not in this month (not trading yet, or added after the file was built).
                    if (!misses.TryGetValue(Key(month), out var seen)) misses[Key(month)] = seen = new(StringComparer.OrdinalIgnoreCase);
                    seen[l.FeedSymbol] = file.Count;
                }
            }
        }

        foreach (var symbol in resplit) Rechecked.Add(symbol);
        if (downloaded > 0) await preferences.SetAsync(MissesPreference, misses);
        if (readings.Count == 0) return new PriceHistoryRefresh(downloaded, new ApplyPriceHistoryResult(0), unreachable);

        try
        {
            var applied = new ApplyPriceHistoryResult(0);
            foreach (var chunk in readings.Chunk(ApplyPriceHistoryRequest.MaxReadings))
            {
                applied = new ApplyPriceHistoryResult(applied.Readings + (await store.ApplyPriceHistoryAsync(new ApplyPriceHistoryRequest(chunk))).Readings);
            }

            return new PriceHistoryRefresh(downloaded, applied, unreachable);
        }
        catch (Exception ex) when (ex is LedgerStoreException or HttpRequestException)
        {
            logger.LogWarning("Couldn't save the holdings' month-end prices: {Message}", ex.Message);
            return new PriceHistoryRefresh(downloaded, new ApplyPriceHistoryResult(0), unreachable);
        }
    }

    private static string Key(DateOnly month) => StockTreeMapHistory.MonthFile(month)[..7];

    private async Task<HistorySymbols?> GetSymbolsAsync(string baseUrl, CancellationToken ct)
    {
        if (_symbols is { Count: > 0 } cached && DateTimeOffset.UtcNow - _symbolsAt < SymbolsMaxAge) return cached;

        try
        {
            var symbols = StockTreeMapHistory.ParseSymbols(await Http.GetStringAsync(baseUrl + StockTreeMapHistory.SymbolsFile, ct));
            if (symbols.Count == 0)
            {
                logger.LogInformation("The price history feed's list of symbols at {Url} was empty.", baseUrl);
                return _symbols;
            }

            (_symbols, _symbolsAt) = (symbols, DateTimeOffset.UtcNow);
            return symbols;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Couldn't get the price history feed's symbols from {Url}: {Message}", baseUrl, ex.Message);
            return _symbols;
        }
    }

    // Whether the last month asked for doesn't exist (before the history starts), rather than couldn't be had.
    private bool _lastWasNotFound;

    private async Task<HistoryMonth?> GetMonthAsync(string baseUrl, DateOnly month, CancellationToken ct)
    {
        _lastWasNotFound = false;
        var url = baseUrl + StockTreeMapHistory.MonthFile(month);
        try
        {
            using var response = await Http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _lastWasNotFound = true;
                return null;
            }

            response.EnsureSuccessStatusCode();
            var file = StockTreeMapHistory.ParseMonth(await response.Content.ReadAsStringAsync(ct));
            return file is not null && file.Month == month ? file : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Couldn't get {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <param name="Symbol">As the report wrote it, which the saved closes are kept under.</param>
    /// <param name="FeedSymbol">The listing's symbol in stocktreemap.com's feeds.</param>
    /// <param name="PricedIn">The month the holding's current price is from: closes are only needed before it.</param>
    private sealed record Listing(string Symbol, string Currency, string FeedSymbol, decimal Price, DateOnly PricedIn);
}
