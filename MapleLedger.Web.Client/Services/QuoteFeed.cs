using System.Net.Http.Json;
using System.Text.Json;
using MapleLedger.Core.Investments;
using Microsoft.AspNetCore.Components;

namespace MapleLedger.Web.Client.Services;

/// <summary>What one refresh of the holdings' prices found.</summary>
/// <param name="ByAccount">Each account's holdings with a symbol and quantity, and their quote or why they have none. Empty when the feed couldn't be had.</param>
/// <param name="AsOf">The newest quote in the feed: when stocktreemap.com last updated its prices. Null when the feed couldn't be had.</param>
/// <param name="CheckedAt">When the feed was last downloaded; null if it never has been in this tab.</param>
/// <param name="LastPriced">The newest price the ledger's holdings have from the feed, saved by this or an earlier refresh.</param>
/// <param name="Unreachable">The feed couldn't be downloaded just now (an earlier copy may still have been used).</param>
/// <param name="SaveFailed">Saving the prices to the ledger failed.</param>
/// <param name="OptionPrices">Option contracts are priced from Yahoo Finance (the person turned it on, and the site allows it).</param>
public sealed record QuoteRefresh(
    IReadOnlyDictionary<string, IReadOnlyList<QuoteMatch>> ByAccount,
    DateTimeOffset? AsOf,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? LastPriced,
    ApplyQuotesResult Applied,
    bool Unreachable,
    bool SaveFailed,
    bool OptionPrices = false)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<QuoteMatch>> None = new Dictionary<string, IReadOnlyList<QuoteMatch>>();

    public static QuoteRefresh Off { get; } = new(None, null, null, null, new ApplyQuotesResult(0, 0), false, false);

    public static QuoteRefresh NothingToPrice(DateTimeOffset? checkedAt) => Off with { CheckedAt = checkedAt };

    public static QuoteRefresh CouldNotReach(DateTimeOffset? lastPriced) => Off with { LastPriced = lastPriced, Unreachable = true };

    public IReadOnlyList<QuoteMatch> For(string accountId) => ByAccount.GetValueOrDefault(accountId) ?? [];

    public IEnumerable<QuoteMatch> All => ByAccount.Values.SelectMany(m => m);

    /// <summary>An option contract that isn't priced only because option prices from Yahoo Finance are off.</summary>
    public static bool AwaitsOptionPrices(QuoteMatch m) => m.Miss == QuoteMiss.Option;

    /// <summary>The feed itself has stopped updating: its newest price is over a few days old.</summary>
    public bool FeedStale => AsOf is { } asOf && QuoteSymbols.IsStale(asOf, Periods.Today);

    /// <summary>
    /// Why a holding's value isn't current from the feed, or null when it is: no price for it, or a
    /// price the feed hasn't updated for days.
    /// </summary>
    public static string? Problem(QuoteMatch m) =>
        m.Miss is not null ? QuoteSymbols.Describe(m)
        : m.Stale && m.Quote is { } q ? $"the feed hasn't updated its price since {Display.Date(DateOnly.FromDateTime(q.AsOf.LocalDateTime))}"
        : null;

    /// <summary>Holdings whose value isn't current from the feed, with why, account by account.</summary>
    public IEnumerable<(string AccountId, QuoteMatch Match, string Problem)> Problems =>
        ByAccount.SelectMany(a => a.Value.Select(m => (a.Key, m, Problem(m))))
            .Where(p => p.Item3 is not null)
            .Select(p => (p.Key, p.m, p.Item3!));
}

/// <summary>
/// Prices the ledger's holdings from stocktreemap.com's quote feed, straight from the browser. The
/// whole feed is downloaded and the holdings are looked up in it here, so nothing about the ledger
/// is sent; the request is the same for everyone. <c>Quotes:FeedUrl</c> in the client's
/// appsettings.json points it elsewhere, and an empty value turns it off.
///
/// Option contracts aren't in that feed. Once the person turns option prices on (a choice kept in
/// this browser), their OCC symbols, and nothing else, go to this site's own server, which asks
/// Yahoo Finance (<c>/quotes/options</c>; <c>Quotes:OptionsUrl</c> moves it, empty turns it off).
/// </summary>
public sealed class QuoteFeed(ILedgerStore store, IConfiguration config, Preferences preferences, NavigationManager navigation, ILogger<QuoteFeed> logger)
{
    // Not the app's HttpClient: that one points at this site and reports it unreachable on failure.
    // The browser's HTTP cache handles the feed's ETag, so an unchanged feed is a 304.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>How long a download is reused. Prices on stocktreemap.com move every two hours.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private const string OptionPreference = "quotes.yahooOptions";

    // Kept across pages for the life of the tab.
    private static QuoteBook? _book;
    private static DateTimeOffset? _fetchedAt;
    private static (string Symbols, QuoteBook Book, DateTimeOffset At)? _options;

    public string? FeedUrl => config["Quotes:FeedUrl"] is { } url ? (string.IsNullOrWhiteSpace(url) ? null : url) : StockTreeMapQuotes.DefaultUrl;

    public bool Enabled => FeedUrl is not null;

    /// <summary>This site's option-price lookup, or null when it's turned off.</summary>
    public string? OptionsUrl => config["Quotes:OptionsUrl"] is { } url
        ? (string.IsNullOrWhiteSpace(url) ? null : url)
        : navigation.ToAbsoluteUri("quotes/options").ToString();

    /// <summary>Whether option contracts are priced from Yahoo Finance: the site allows it and the person turned it on.</summary>
    public async Task<bool> OptionPricesOnAsync() => OptionsUrl is not null && await preferences.GetAsync<bool>(OptionPreference);

    /// <summary>Turns option prices from Yahoo Finance on or off for this browser.</summary>
    public async Task SetOptionPricesAsync(bool on)
    {
        _options = null;
        await preferences.SetAsync(OptionPreference, on);
    }

    /// <summary>
    /// Downloads the feed (unless the last download is under 15 minutes old and <paramref name="force"/>
    /// is false), matches every holding to it, and saves the prices that changed. Nothing is
    /// downloaded for a ledger without holdings.
    /// </summary>
    public async Task<QuoteRefresh> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (FeedUrl is not { } url) return QuoteRefresh.Off;

        var snapshots = await store.GetHoldingsAsync(ct);
        var lastPriced = snapshots.SelectMany(s => s.Holdings)
            .Where(h => h.PriceSource == HoldingPriceSource.Quote)
            .Max(h => h.PriceAsOf);
        if (!snapshots.SelectMany(s => s.Holdings).Any(h => h.Symbol is not null && h.Quantity is not null and not 0))
        {
            return QuoteRefresh.NothingToPrice(_fetchedAt) with { LastPriced = lastPriced };
        }

        var (book, reached) = await GetBookAsync(url, force, ct);
        if (book is null) return QuoteRefresh.CouldNotReach(lastPriced);

        var today = Periods.Today;
        var byAccount = snapshots.ToDictionary(
            s => s.AccountId,
            s => (IReadOnlyList<QuoteMatch>)s.Holdings
                .Select(h => QuoteSymbols.Resolve(h, book, s.AsOf, today))
                .Where(m => m.Miss != QuoteMiss.NotPriceable)
                .ToList());

        var optionPrices = await OptionPricesOnAsync();
        var options = byAccount.Values.SelectMany(m => m).Where(m => m.Miss == QuoteMiss.Option).ToList();
        if (optionPrices && options.Count > 0)
        {
            var yahoo = await GetOptionQuotesAsync(options.Select(m => m.Holding.Option!.OccSymbol), force, ct);
            byAccount = byAccount.ToDictionary(
                a => a.Key,
                a => (IReadOnlyList<QuoteMatch>)a.Value
                    .Select(m => m.Miss != QuoteMiss.Option ? m
                        : yahoo is null ? m with { Miss = QuoteMiss.OptionUnreachable }
                        : QuoteSymbols.ResolveOption(m.Holding, yahoo, today))
                    .ToList());
        }

        var quotes = byAccount.Values.SelectMany(m => m)
            .Where(m => m.Quote is not null)
            .Select(m => new HoldingQuote(m.Holding.Symbol!, m.Holding.Currency, m.Quote!.Price, m.Quote.AsOf))
            .DistinctBy(q => (q.Symbol, q.Currency))
            .ToList();

        var result = new QuoteRefresh(byAccount, book.AsOf, _fetchedAt, lastPriced, new ApplyQuotesResult(0, 0), !reached, false, optionPrices && OptionsUrl is not null);
        // Cash named by its currency ("USD") is saved as cash, and an option imported at 1% of its
        // value is put right, even when nothing else has a price.
        if (quotes.Count == 0 && !snapshots.Any(s => s.Holdings.Any(h => h.NamedCashCurrency is not null || h.MissesContractMultiplier))) return result;

        try
        {
            var applied = await store.ApplyQuotesAsync(new ApplyQuotesRequest(quotes));
            var newest = quotes.Count == 0 ? lastPriced : quotes.Max(q => q.AsOf);
            return result with { Applied = applied, LastPriced = lastPriced is { } last && last > newest ? last : newest };
        }
        catch (Exception ex) when (ex is LedgerStoreException or HttpRequestException)
        {
            logger.LogWarning("Couldn't save the holdings' prices: {Message}", ex.Message);
            return result with { SaveFailed = true };
        }
    }

    /// <summary>
    /// Yahoo Finance's prices for these contracts, through this site's server, reused for
    /// <see cref="MaxAge"/>; null when the server couldn't be reached. Contracts Yahoo doesn't know
    /// are simply missing from the book.
    /// </summary>
    private async Task<QuoteBook?> GetOptionQuotesAsync(IEnumerable<string> occSymbols, bool force, CancellationToken ct)
    {
        if (OptionsUrl is not { } url) return null;
        var symbols = occSymbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var key = string.Join(',', symbols);
        if (!force && _options is { } cached && cached.Symbols == key && DateTimeOffset.UtcNow - cached.At < MaxAge) return cached.Book;

        try
        {
            var quotes = new List<Quote>();
            foreach (var chunk in symbols.Chunk(YahooQuotes.MaxSymbols))
            {
                var found = await Http.GetFromJsonAsync<List<Quote>>($"{url}?symbols={Uri.EscapeDataString(string.Join(',', chunk))}", JsonSerializerOptions.Web, ct);
                quotes.AddRange(found ?? []);
            }

            var book = new QuoteBook(quotes);
            _options = (key, book, DateTimeOffset.UtcNow);
            return book;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger.LogInformation("Couldn't get option prices from {Url}: {Message}", url, ex.Message);
            return _options?.Symbols == key ? _options.Value.Book : null;
        }
    }

    /// <summary>
    /// The quote feed's prices (this tab's copy when it's under <see cref="MaxAge"/> old), for matching
    /// holdings to the feed's symbols; null when it's turned off or couldn't be had.
    /// </summary>
    public async Task<QuoteBook?> GetBookAsync(CancellationToken ct = default) =>
        FeedUrl is { } url ? (await GetBookAsync(url, false, ct)).Book : null;

    /// <summary>
    /// The feed, downloaded at most every <see cref="MaxAge"/>, and whether the download worked.
    /// When it doesn't, this tab's last copy is used, if it has one.
    /// </summary>
    private async Task<(QuoteBook? Book, bool Reached)> GetBookAsync(string url, bool force, CancellationToken ct)
    {
        if (!force && _book is { Count: > 0 } cached && DateTimeOffset.UtcNow - _fetchedAt < MaxAge) return (cached, true);

        try
        {
            var book = StockTreeMapQuotes.Parse(await Http.GetStringAsync(url, ct));
            if (book.Count == 0)
            {
                logger.LogInformation("The quote feed at {Url} had no prices in it.", url);
                return (_book, false);
            }

            (_book, _fetchedAt) = (book, DateTimeOffset.UtcNow);
            return (book, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Couldn't get prices from {Url}: {Message}", url, ex.Message);
            return (_book, false);
        }
    }
}
