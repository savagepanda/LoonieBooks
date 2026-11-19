using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MapleLedger.Core.Investments;

namespace MapleLedger.Web.Client.Services;

/// <summary>What stocktreemap.com said about one holding we asked it to carry.</summary>
/// <param name="Status">Added, Listed, NotFound, Invalid or Limited.</param>
/// <param name="Listing">The listing it found, such as XEQT.TO.</param>
public sealed record ListingRequestOutcome(string Symbol, string Currency, string Status, string? Listing, string? Message, DateTimeOffset At)
{
    public bool Found => Status is "Added" or "Listed";
}

/// <summary>What one press of the button did.</summary>
public sealed record ListingRequestSummary(int Found, int NotFound, int Priced, string? Error);

/// <summary>
/// Asks stocktreemap.com to carry listings its quote feed lacks. Unlike downloading the feed, this
/// does send something about the ledger (the few symbols it can't price, and their currency), so it
/// only happens when the person presses the button, and the page says what will be sent. Nothing
/// else goes: no quantities, values, accounts or other holdings.
///
/// The answers are kept in this browser's preferences, so a symbol stocktreemap couldn't find isn't
/// offered again for a week, and the page can say what happened.
/// </summary>
public sealed class ListingRequests(IConfiguration config, Preferences preferences, ILedgerStore store, ILogger<ListingRequests> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string Preference = "quotes.requests";

    /// <summary>The most the server takes in one request.</summary>
    public const int MaxPerRequest = 10;

    /// <summary>How long an answer stands before the symbol can be asked about again.</summary>
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// <c>Quotes:RequestsUrl</c>, else the feed's address with <c>requests</c> in place of
    /// <c>latest.json</c>; null when prices are off or the feed's address doesn't allow working it out.
    /// </summary>
    public string? Url
    {
        get
        {
            if (config["Quotes:RequestsUrl"] is { } configured) return string.IsNullOrWhiteSpace(configured) ? null : configured;
            var feed = config["Quotes:FeedUrl"] is { } f ? (string.IsNullOrWhiteSpace(f) ? null : f) : StockTreeMapQuotes.DefaultUrl;
            return feed is not null && feed.EndsWith("/latest.json", StringComparison.OrdinalIgnoreCase)
                ? feed[..^"latest.json".Length] + "requests"
                : null;
        }
    }

    public bool Enabled => Url is not null;

    /// <summary>
    /// A holding the feed doesn't price because it doesn't carry the listing (not a fund code or a
    /// doubtful price). Never an option: stocktreemap.com carries listings only, and options are
    /// priced from Yahoo Finance instead.
    /// </summary>
    public static bool CanAsk(QuoteMatch m) => m.Miss is QuoteMiss.NotListed or QuoteMiss.OtherCurrency && m.Holding.Option is null;

    /// <summary>The answers so far, by symbol and currency, from the last month.</summary>
    public async Task<IReadOnlyDictionary<(string Symbol, string Currency), ListingRequestOutcome>> GetOutcomesAsync()
    {
        var saved = await preferences.GetAsync<List<ListingRequestOutcome>>(Preference) ?? [];
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        return saved.Where(o => o.At > since)
            .GroupBy(o => (o.Symbol, o.Currency))
            .ToDictionary(g => g.Key, g => g.MaxBy(o => o.At)!);
    }

    /// <summary>Whether to offer asking about this holding: it's askable, and not asked lately (except when the day's limit stopped it).</summary>
    public static bool ShouldOffer(QuoteMatch m, IReadOnlyDictionary<(string Symbol, string Currency), ListingRequestOutcome> outcomes) =>
        CanAsk(m) && (!outcomes.TryGetValue((m.Holding.Symbol!, m.Holding.Currency), out var o)
            || DateTimeOffset.UtcNow - o.At > (o.Status is "Limited" or "Invalid" ? TimeSpan.FromDays(1) : AskAgainAfter));

    /// <summary>
    /// Sends the holdings' symbols (as the listings each could be) and currencies, saves the
    /// prices of the ones found, and remembers every answer.
    /// </summary>
    public async Task<ListingRequestSummary> RequestAsync(IEnumerable<QuoteMatch> matches, CancellationToken ct = default)
    {
        if (Url is not { } url) return new ListingRequestSummary(0, 0, 0, "Asking for symbols is turned off.");

        var holdings = matches.Where(CanAsk)
            .Select(m => m.Holding)
            .DistinctBy(h => (h.Symbol, h.Currency))
            .Take(MaxPerRequest)
            .ToList();
        var items = holdings
            .Select(h => new RequestItem(QuoteSymbols.Candidates(h.Symbol!, h.Currency).Take(6).ToList(), h.Currency))
            .ToList();
        var askable = items.Select((item, i) => (item, i)).Where(x => x.item.Candidates.Count > 0).ToList();
        if (askable.Count == 0) return new ListingRequestSummary(0, 0, 0, "None of these symbols can be looked up.");

        List<RequestResult>? results;
        try
        {
            using var response = await Http.PostAsJsonAsync(url, new Request(askable.Select(x => x.item).ToList()), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new ListingRequestSummary(0, 0, 0, "stocktreemap.com has had several requests from here lately. Try again in ten minutes.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ListingRequestSummary(0, 0, 0, $"stocktreemap.com couldn't look them up just now ({(int)response.StatusCode}). Try again later.");
            }

            results = await response.Content.ReadFromJsonAsync<List<RequestResult>>(JsonSerializerOptions.Web, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation("Couldn't ask {Url} for listings: {Message}", url, ex.Message);
            return new ListingRequestSummary(0, 0, 0, "Couldn't reach stocktreemap.com. Try again later.");
        }

        if (results is null || results.Count != askable.Count)
        {
            return new ListingRequestSummary(0, 0, 0, "stocktreemap.com's answer wasn't understood.");
        }

        var now = DateTimeOffset.UtcNow;
        var outcomes = new List<ListingRequestOutcome>();
        var quotes = new List<HoldingQuote>();
        for (var k = 0; k < askable.Count; k++)
        {
            var holding = holdings[askable[k].i];
            var r = results[k];
            outcomes.Add(new ListingRequestOutcome(holding.Symbol!, holding.Currency, r.Status ?? "NotFound", r.Listing, r.Message, now));
            if (r.Status is "Added" or "Listed" && r.Price is > 0 && r.UpdatedUtc is { } at
                && string.Equals(r.Currency, holding.Currency, StringComparison.OrdinalIgnoreCase))
            {
                quotes.Add(new HoldingQuote(holding.Symbol!, holding.Currency, r.Price.Value, at));
            }
        }

        // Remember the answers (newest per symbol), keeping a month of them.
        var saved = (await preferences.GetAsync<List<ListingRequestOutcome>>(Preference) ?? [])
            .Where(o => o.At > now.AddDays(-30) && !outcomes.Any(n => n.Symbol == o.Symbol && n.Currency == o.Currency))
            .Concat(outcomes)
            .ToList();
        await preferences.SetAsync(Preference, saved);

        var priced = 0;
        if (quotes.Count > 0)
        {
            try
            {
                priced = (await store.ApplyQuotesAsync(new ApplyQuotesRequest(quotes))).Repriced;
            }
            catch (Exception ex) when (ex is LedgerStoreException or HttpRequestException)
            {
                logger.LogWarning("Couldn't save the prices of the listings found: {Message}", ex.Message);
            }
        }

        return new ListingRequestSummary(outcomes.Count(o => o.Found), outcomes.Count(o => !o.Found), priced, null);
    }

    /// <summary>What an earlier answer means for the holding, for the list of holdings that aren't updated.</summary>
    public static string? Describe(ListingRequestOutcome? o) => o switch
    {
        null => null,
        { Found: true } => $"added to stocktreemap.com as {o.Listing} on {Display.ShortDate(DateOnly.FromDateTime(o.At.LocalDateTime))}; its price joins the feed within minutes",
        { Status: "NotFound" } => $"stocktreemap.com couldn't find it on {Display.ShortDate(DateOnly.FromDateTime(o.At.LocalDateTime))}{(o.Message is { Length: > 0 } m ? $" ({m.TrimEnd('.')})" : "")}",
        { Status: "Limited" } => "stocktreemap.com had added all it could that day; ask again",
        _ => null,
    };

    private sealed record Request(List<RequestItem> Items);

    private sealed record RequestItem(List<string> Candidates, string Currency);

    private sealed record RequestResult(string? Status, string? Currency, string? Listing, decimal? Price, decimal? PreviousClose, DateTimeOffset? UpdatedUtc, string? Message);
}
