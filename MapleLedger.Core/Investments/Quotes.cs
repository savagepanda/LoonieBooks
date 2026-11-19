using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MapleLedger.Core.Investments;

/// <summary>A listing's latest price from the quote feed, in <see cref="Currency"/>.</summary>
/// <param name="Symbol">Yahoo-style: <c>.TO</c> for the TSX, <c>-</c> for share classes and units (<c>BRK-B</c>, <c>REI-UN.TO</c>).</param>
public sealed record Quote(string Symbol, decimal Price, decimal? PreviousClose, string Currency, DateTimeOffset AsOf);

/// <summary>Every price in one download of the quote feed, by symbol.</summary>
public sealed class QuoteBook(IEnumerable<Quote> quotes)
{
    private readonly Dictionary<string, Quote> _quotes = quotes
        .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public static QuoteBook Empty { get; } = new([]);

    public int Count => _quotes.Count;

    /// <summary>The newest quote's time; null for an empty book.</summary>
    public DateTimeOffset? AsOf => _quotes.Count == 0 ? null : _quotes.Values.Max(q => q.AsOf);

    public Quote? Find(string symbol) => _quotes.GetValueOrDefault(symbol);
}

/// <summary>
/// stocktreemap.com's public quote feed (version 1): every listing it carries, as one document. The
/// whole table is downloaded and holdings are looked up here, so nothing about the ledger is ever
/// sent; the request is the same for everyone.
/// </summary>
public static class StockTreeMapQuotes
{
    public const string DefaultUrl = "https://stocktreemap.com/data/quotes/v1/latest.json";

    /// <summary>
    /// The quotes in a feed document. Columns are found by the names in its <c>fields</c>, so a
    /// column added later doesn't break this. Rows that don't make sense (no symbol, a price that
    /// isn't positive, a currency that isn't three letters) are skipped; a document that isn't the
    /// feed gives an empty book.
    /// </summary>
    public static QuoteBook Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("quotes", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return QuoteBook.Empty;
            }

            var names = fields.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() : null).ToList();
            int s = names.IndexOf("s"), p = names.IndexOf("p"), pc = names.IndexOf("pc"), c = names.IndexOf("c"), t = names.IndexOf("t");
            if (s < 0 || p < 0 || c < 0 || t < 0) return QuoteBook.Empty;

            var quotes = new List<Quote>(rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;
                var length = row.GetArrayLength();
                if (length <= Math.Max(Math.Max(s, p), Math.Max(c, t))) continue;

                var symbol = row[s].ValueKind == JsonValueKind.String ? row[s].GetString()?.Trim().ToUpperInvariant() : null;
                var currency = row[c].ValueKind == JsonValueKind.String ? row[c].GetString()?.Trim().ToUpperInvariant() : null;
                if (string.IsNullOrEmpty(symbol) || symbol.Length > Holding.MaxSymbolLength) continue;
                if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper)) continue;
                if (row[p].ValueKind != JsonValueKind.Number || !row[p].TryGetDecimal(out var price) || price <= 0) continue;
                if (row[t].ValueKind != JsonValueKind.Number || !row[t].TryGetInt64(out var seconds) || seconds <= 0) continue;

                decimal? previous = pc >= 0 && pc < length && row[pc].ValueKind == JsonValueKind.Number && row[pc].TryGetDecimal(out var prev) && prev > 0 ? prev : null;
                DateTimeOffset asOf;
                try
                {
                    asOf = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                quotes.Add(new Quote(symbol, price, previous, currency, asOf));
            }

            return new QuoteBook(quotes);
        }
        catch (JsonException)
        {
            return QuoteBook.Empty;
        }
    }
}

/// <summary>Why a holding wasn't given a price from the quote feed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuoteMiss>))]
public enum QuoteMiss
{
    /// <summary>No symbol, or no quantity to multiply (cash, a GIC), or cash named by its currency ("USD").</summary>
    NotPriceable,

    /// <summary>A mutual fund code (Fundserv, such as TDB902). The feed carries listings only.</summary>
    FundCode,

    /// <summary>A CUSIP or ISIN rather than a ticker.</summary>
    SecurityId,

    /// <summary>The feed has no listing by that symbol.</summary>
    NotListed,

    /// <summary>The feed lists the symbol only in another currency, which may be another company.</summary>
    OtherCurrency,

    /// <summary>The feed's price is far from the holding's own recent one: the wrong listing, or a split.</summary>
    PriceLooksWrong,

    /// <summary>An option contract. stocktreemap.com carries listings only; options are priced from Yahoo Finance, once that's turned on.</summary>
    Option,

    /// <summary>An option contract Yahoo Finance has no price for (expired, or a symbol it doesn't know).</summary>
    OptionNotFound,

    /// <summary>An option contract whose price couldn't be had: this site's server couldn't be reached, or Yahoo Finance couldn't.</summary>
    OptionUnreachable,
}

/// <summary>What the quote feed gives one holding: the listing and its quote, or why there's none.</summary>
public sealed record QuoteMatch(Holding Holding, string? QuoteSymbol, Quote? Quote, QuoteMiss? Miss)
{
    public bool Priced => Quote is not null;

    /// <summary>
    /// Priced, but the feed hasn't updated this listing for over <see cref="QuoteSymbols.StaleDays"/>
    /// days: it may have stopped trading, or left the feed's lists.
    /// </summary>
    public bool Stale { get; init; }

    /// <summary>
    /// For <see cref="QuoteMiss.OtherCurrency"/>: the currency of the listing the feed does have
    /// (<see cref="QuoteSymbol"/>), which isn't the holding's.
    /// </summary>
    public string? ListedCurrency { get; init; }
}

/// <summary>
/// Matches a holding, as the broker's report names it, to a listing in the quote feed. Reports
/// write symbols in different ways (<c>XEQT</c>, <c>XEQT.TO</c>, <c>TSX:XEQT</c>, <c>BRK.B</c>) and
/// have no exchange column, so the holding's currency decides where to look: a bare symbol held in
/// Canadian dollars is a Canadian listing. Only a listing in the holding's own currency counts, since
/// the same ticker in another currency is often another company.
/// </summary>
public static partial class QuoteSymbols
{
    /// <summary>A price this far from the holding's own (half or double) is taken for a wrong match.</summary>
    public const decimal MaxMove = 0.5m;

    /// <summary>How recent the holding's own price must be to judge the feed's against it.</summary>
    public const int RecentDays = 60;

    /// <summary>
    /// A quote older than this many days is stale. Long enough for a long weekend (Friday's close
    /// on a holiday Tuesday is four days old), short enough to notice within the week.
    /// </summary>
    public const int StaleDays = 5;

    /// <summary>Whether a quote, or the feed's newest one, is older than <see cref="StaleDays"/> days.</summary>
    public static bool IsStale(DateTimeOffset asOf, DateOnly today) =>
        DateOnly.FromDateTime(asOf.UtcDateTime) < today.AddDays(-StaleDays);

    private enum Venue { Any, Tsx, Venture, Neo, Cse, Us }

    private static readonly Dictionary<string, Venue> Prefixes = new(StringComparer.Ordinal)
    {
        ["TSX"] = Venue.Tsx, ["TSE"] = Venue.Tsx, ["XTSE"] = Venue.Tsx, ["TO"] = Venue.Tsx,
        ["TSXV"] = Venue.Venture, ["CVE"] = Venue.Venture, ["XTSX"] = Venue.Venture, ["V"] = Venue.Venture,
        ["NEO"] = Venue.Neo, ["NEOE"] = Venue.Neo, ["CBOE"] = Venue.Neo,
        ["CSE"] = Venue.Cse, ["CNSX"] = Venue.Cse,
        ["NYSE"] = Venue.Us, ["NASDAQ"] = Venue.Us, ["NYSEARCA"] = Venue.Us, ["ARCA"] = Venue.Us, ["AMEX"] = Venue.Us, ["BATS"] = Venue.Us, ["US"] = Venue.Us,
    };

    // Exchange suffixes, longest first so ".TSXV" isn't read as ".TSX" plus "V".
    private static readonly (string Suffix, Venue Venue)[] Suffixes =
    [
        (".TSXV", Venue.Venture), (".TSX", Venue.Tsx), (".NEO", Venue.Neo),
        (".TO", Venue.Tsx), (".TRT", Venue.Tsx), (".V", Venue.Venture), (".NE", Venue.Neo), (".CN", Venue.Cse),
        ("-T", Venue.Tsx), ("-V", Venue.Venture),
        (" CN", Venue.Tsx), (" CT", Venue.Tsx), (" US", Venue.Us),
        (":CA", Venue.Tsx), (":US", Venue.Us),
    ];

    /// <summary>Three letters and two to five digits: a Fundserv mutual fund code (TDB902, RBF556, FID5982).</summary>
    [GeneratedRegex("^[A-Z]{3}[0-9]{2,5}$")]
    private static partial Regex FundCode();

    /// <summary>A CUSIP (9 characters) or ISIN (2 letters, 10 characters), with digits in it.</summary>
    [GeneratedRegex("^(?=.*[0-9])([A-Z0-9]{8}[0-9]|[A-Z]{2}[A-Z0-9]{9}[0-9])$")]
    private static partial Regex SecurityId();

    [GeneratedRegex("^[A-Z0-9]+([-][A-Z0-9]+)*$")]
    private static partial Regex Ticker();

    /// <summary>
    /// The feed symbols a holding's symbol could be, most likely first, or none for a symbol that
    /// isn't a ticker. Share class and unit separators become <c>-</c> (<c>BRK.B</c> is <c>BRK-B</c>,
    /// <c>REI.UN</c> is <c>REI-UN.TO</c>).
    /// </summary>
    public static IReadOnlyList<string> Candidates(string symbol, string currency)
    {
        var (root, venue) = Split(symbol);
        if (root is null) return [];

        return venue switch
        {
            Venue.Tsx => [root + ".TO"],
            Venue.Venture => [root + ".V"],
            Venue.Neo => [root + ".NE"],
            Venue.Cse => [root + ".CN"],
            Venue.Us => [root],
            // Canadian dollars: a Canadian listing, the main board first. A US listing last, which
            // only matches if the feed quotes it in CAD.
            _ when currency == Holding.Cad => [root + ".TO", root + ".V", root + ".NE", root + ".CN", root],
            // US dollars: a US listing, or a TSX one that trades in USD (DLR.U is DLR-U.TO).
            _ when currency == ExchangeRate.Usd => [root, root + ".TO"],
            _ => [root],
        };
    }

    /// <summary>The feed's price for a holding, or why there's none.</summary>
    /// <param name="statementAsOf">The date of the holding's snapshot.</param>
    /// <param name="today">For judging whether the holding's own price is recent enough to check against.</param>
    public static QuoteMatch Resolve(Holding holding, QuoteBook book, DateOnly statementAsOf, DateOnly today)
    {
        // Cash named by its currency ("USD") isn't a listing, however the report priced it.
        if (string.IsNullOrWhiteSpace(holding.Symbol) || holding.Quantity is not { } quantity || quantity == 0 || holding.NamedCashCurrency is not null)
        {
            return new QuoteMatch(holding, null, null, QuoteMiss.NotPriceable);
        }

        // Options aren't in the feed: see ResolveOption.
        if (holding.Option is not null) return new QuoteMatch(holding, null, null, QuoteMiss.Option);

        var clean = Clean(holding.Symbol);
        if (FundCode().IsMatch(clean)) return new QuoteMatch(holding, null, null, QuoteMiss.FundCode);
        if (SecurityId().IsMatch(clean)) return new QuoteMatch(holding, null, null, QuoteMiss.SecurityId);

        var candidates = Candidates(holding.Symbol, holding.Currency);
        if (candidates.Count == 0) return new QuoteMatch(holding, null, null, QuoteMiss.NotListed);

        Quote? otherCurrency = null;
        foreach (var candidate in candidates)
        {
            if (book.Find(candidate) is not { } quote) continue;
            if (quote.Currency != holding.Currency)
            {
                otherCurrency ??= quote;
                continue;
            }

            return LooksWrong(holding, quote, statementAsOf, today)
                ? new QuoteMatch(holding, quote.Symbol, null, QuoteMiss.PriceLooksWrong)
                : new QuoteMatch(holding, quote.Symbol, quote, null) { Stale = IsStale(quote.AsOf, today) };
        }

        return otherCurrency is not null
            ? new QuoteMatch(holding, otherCurrency.Symbol, null, QuoteMiss.OtherCurrency) { ListedCurrency = otherCurrency.Currency }
            : new QuoteMatch(holding, null, null, QuoteMiss.NotListed);
    }

    /// <summary>
    /// Yahoo Finance's price for an option holding, from <paramref name="yahoo"/> (quotes by OCC
    /// symbol), or why there's none. No check against the holding's own price: options often
    /// double or halve in days, and the OCC symbol names exactly one contract.
    /// </summary>
    public static QuoteMatch ResolveOption(Holding holding, QuoteBook yahoo, DateOnly today)
    {
        if (holding.Option is not { } option) return new QuoteMatch(holding, null, null, QuoteMiss.NotPriceable);

        var symbol = option.OccSymbol;
        return yahoo.Find(symbol) switch
        {
            null => new QuoteMatch(holding, symbol, null, QuoteMiss.OptionNotFound),
            { } quote when quote.Currency != holding.Currency => new QuoteMatch(holding, symbol, null, QuoteMiss.OtherCurrency) { ListedCurrency = quote.Currency },
            { } quote => new QuoteMatch(holding, symbol, quote, null) { Stale = IsStale(quote.AsOf, today) },
        };
    }

    /// <summary>
    /// Why a holding has no price, for the page, naming the listing the feed has when it's in
    /// another currency: that's usually another company with the same ticker (a Canadian CNC held
    /// in CAD, while the feed's CNC is a US stock in USD), so the holding itself isn't in the feed.
    /// </summary>
    public static string Describe(QuoteMatch match) => match switch
    {
        { Miss: QuoteMiss.OtherCurrency, ListedCurrency: { } currency, Holding.Option: not null } =>
            $"an option contract Yahoo Finance prices in {currency}, not {match.Holding.Currency}",
        { Miss: QuoteMiss.OtherCurrency, QuoteSymbol: { } symbol, ListedCurrency: { } currency } =>
            $"not in the price feed (its {symbol} is a {currency} listing, likely a different company)",
        { Miss: { } miss } => Describe(miss),
        _ => "",
    };

    /// <summary>What a miss means, for the page.</summary>
    public static string Describe(QuoteMiss miss) => miss switch
    {
        QuoteMiss.NotPriceable => "no symbol or quantity",
        QuoteMiss.FundCode => "mutual fund code: not a listed price",
        QuoteMiss.SecurityId => "CUSIP or ISIN, not a ticker",
        QuoteMiss.NotListed => "not in the price feed",
        QuoteMiss.OtherCurrency => "listed only in another currency",
        QuoteMiss.PriceLooksWrong => "the feed's price is far from this holding's: check the symbol, or import a new statement after a split",
        QuoteMiss.Option => "an option contract: stocktreemap.com doesn't carry options, but Yahoo Finance does",
        QuoteMiss.OptionNotFound => "an option contract Yahoo Finance has no price for (expired, or check the symbol)",
        QuoteMiss.OptionUnreachable => "an option contract: couldn't get its price from Yahoo Finance just now",
        _ => miss.ToString(),
    };

    private static bool LooksWrong(Holding holding, Quote quote, DateOnly statementAsOf, DateOnly today)
    {
        var own = holding.Price is > 0 ? holding.Price.Value
            : holding.Quantity is { } q && q != 0 && holding.MarketValue / q > 0 ? holding.MarketValue / q / holding.Multiplier
            : 0m;
        if (own <= 0) return false;

        var ownDate = holding.PriceAsOf is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : statementAsOf;
        if (ownDate < today.AddDays(-RecentDays)) return false;

        var move = quote.Price / own - 1;
        return move > 1 || move < -MaxMove;
    }

    private static string Clean(string symbol) =>
        symbol.Trim().ToUpperInvariant().Replace(' ', ' ');

    /// <summary>The ticker without its exchange, separators as <c>-</c>, and the exchange it named.</summary>
    private static (string? Root, Venue Venue) Split(string symbol)
    {
        var s = Clean(symbol);
        var venue = Venue.Any;

        var colon = s.IndexOf(':');
        if (colon > 0 && Prefixes.TryGetValue(s[..colon].Trim(), out var prefixed))
        {
            venue = prefixed;
            s = s[(colon + 1)..].Trim();
        }

        foreach (var (suffix, v) in Suffixes)
        {
            if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.Ordinal))
            {
                if (venue == Venue.Any) venue = v;
                s = s[..^suffix.Length].Trim();
                break;
            }
        }

        // Share classes and units: BRK.B, BRK/B, BRK B, RCI-B and REI.UN all become the feed's dash.
        s = string.Join('-', s.Split(['.', '/', ' ', '-'], StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 0 && s.Length <= Holding.MaxSymbolLength && Ticker().IsMatch(s) ? (s, venue) : (null, venue);
    }
}

/// <summary>A price to apply to every holding with this symbol (as the report wrote it) and currency.</summary>
public sealed record HoldingQuote(string Symbol, string Currency, decimal Price, DateTimeOffset AsOf);

public sealed record ApplyQuotesRequest(IReadOnlyList<HoldingQuote> Quotes)
{
    public const int MaxQuotes = 5000;
}

/// <param name="Repriced">Holdings whose price changed, or that were turned into cash (see <see cref="Holding.AsNamedCash"/>).</param>
/// <param name="BalancesUpdated">Accounts whose balance followed their holdings' new total.</param>
public sealed record ApplyQuotesResult(int Repriced, int BalancesUpdated);
