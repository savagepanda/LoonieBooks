using System.Globalization;
using System.Text.Json;

namespace MapleLedger.Core.Investments;

/// <summary>
/// stocktreemap.com's public monthly price history feed (version 1), for net worth by month: one
/// shared list of every listing it carries (<c>symbols.json</c>) and one file per completed month
/// (<c>2026-10.json</c>) holding each listing's close at the month's end, in the list's order. Every
/// reader downloads the same files and looks up its holdings here, so nothing about the ledger is
/// sent, and only the months a chart shows are downloaded.
/// </summary>
public static class StockTreeMapHistory
{
    public const string DefaultBaseUrl = "https://stocktreemap.com/data/history/v1/monthly/";
    public const string SymbolsFile = "symbols.json";

    /// <summary>The file for a month: <c>2026-10.json</c>.</summary>
    public static string MonthFile(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".json";

    /// <summary>The list of symbols; empty for a document that isn't the feed's.</summary>
    public static HistorySymbols ParseSymbols(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!IsVersion1(root) || !root.TryGetProperty("symbols", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return HistorySymbols.Empty;
            }

            var symbols = new List<string?>(list.GetArrayLength());
            foreach (var s in list.EnumerateArray())
            {
                symbols.Add(s.ValueKind == JsonValueKind.String ? s.GetString()?.Trim().ToUpperInvariant() : null);
            }

            return new HistorySymbols(symbols);
        }
        catch (JsonException)
        {
            return HistorySymbols.Empty;
        }
    }

    /// <summary>A month's closes; null for a document that isn't one.</summary>
    public static HistoryMonth? ParseMonth(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!IsVersion1(root)
                || !root.TryGetProperty("month", out var m) || m.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(m.GetString(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                || !root.TryGetProperty("count", out var c) || !c.TryGetInt32(out var count) || count < 0
                || !root.TryGetProperty("closes", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var closes = new List<decimal?>(list.GetArrayLength());
            foreach (var close in list.EnumerateArray())
            {
                closes.Add(close.ValueKind == JsonValueKind.Number && close.TryGetDecimal(out var value) && value > 0 ? value : null);
            }

            return new HistoryMonth(month, Math.Max(count, closes.Count), closes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsVersion1(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("v", out var v) && v.TryGetInt32(out var version) && version == 1;
}

/// <summary>The feed's symbols by position: a listing keeps its place, and new ones are added at the end.</summary>
public sealed class HistorySymbols(IReadOnlyList<string?> symbols)
{
    private readonly Dictionary<string, int> _index = symbols
        .Select((s, i) => (Symbol: s, Index: i))
        .Where(x => !string.IsNullOrEmpty(x.Symbol))
        .GroupBy(x => x.Symbol!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);

    public static HistorySymbols Empty { get; } = new([]);

    public int Count => symbols.Count;

    /// <summary>A symbol's place in the list, or null when the feed doesn't carry it.</summary>
    public int? IndexOf(string symbol) => _index.TryGetValue(symbol, out var i) ? i : null;
}

/// <param name="Count">How many symbols the list had when the month was built: a listing at or past it isn't in this copy.</param>
public sealed record HistoryMonth(DateOnly Month, int Count, IReadOnlyList<decimal?> Closes)
{
    /// <summary>The close at the month's end for the listing at <paramref name="index"/>; null when it had none.</summary>
    public decimal? CloseAt(int index) => index >= 0 && index < Closes.Count ? Closes[index] : null;
}

/// <summary>
/// A listing's close at the end of a month, for the holdings with this symbol (as the report wrote
/// it) and currency. <see cref="Month"/> is the month's first day.
/// </summary>
public sealed record PriceReading(string Symbol, string Currency, DateOnly Month, decimal Close)
{
    public const decimal MaxClose = 1_000_000_000m;
}

public sealed record ApplyPriceHistoryRequest(IReadOnlyList<PriceReading> Readings)
{
    public const int MaxReadings = 5000;
}

/// <param name="Readings">Month-end closes added or changed.</param>
public sealed record ApplyPriceHistoryResult(int Readings);

/// <summary>
/// How much the holdings were worth at a past month's end compared with today, by their listings'
/// month-end closes: the market's part of an investment account's balance, which its transactions
/// don't show. Each holding is taken as held in the same quantity throughout.
/// </summary>
public static class HoldingPriceMoves
{
    /// <summary>
    /// For each investment account, a function from a month's last day to what its holdings were
    /// worth then less what they're worth at their current price, in Canadian dollars at today's
    /// <paramref name="rates"/>. Zero for the month of a holding's current price and after, and for
    /// a month without a close; null when no holding had a close that month (nothing was estimated).
    /// Accounts without priced holdings are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, Func<DateOnly, decimal?>> ByAccount(
        IEnumerable<HoldingsSnapshot> snapshots, IEnumerable<PriceReading> readings, CurrencyRates rates)
    {
        var closes = readings
            .GroupBy(r => (Symbol: r.Symbol.ToUpperInvariant(), Currency: r.Currency.ToUpperInvariant(), r.Month))
            .ToDictionary(g => g.Key, g => g.Last().Close);
        var result = new Dictionary<string, Func<DateOnly, decimal?>>();
        if (closes.Count == 0) return result;

        foreach (var snapshot in snapshots)
        {
            var held = snapshot.Holdings
                .Where(h => Priceable(h) && h.Price is not null)
                .Select(h => (Holding: h, PricedIn: FirstOfMonth(h.PriceAsOf is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : snapshot.AsOf)))
                .ToList();
            if (held.Count == 0) continue;

            result[snapshot.AccountId] = end =>
            {
                var month = FirstOfMonth(end);
                decimal? move = null;
                foreach (var (h, pricedIn) in held)
                {
                    if (month >= pricedIn) continue;
                    if (!closes.TryGetValue((h.Symbol!.ToUpperInvariant(), h.Currency.ToUpperInvariant(), month), out var close)) continue;
                    var quantity = h.Quantity!.Value;
                    move = (move ?? 0) + rates.ToCad(h.ValueAt(quantity, close) - h.ValueAt(quantity, h.Price!.Value), h.Currency);
                }

                return move;
            };
        }

        return result;
    }

    /// <summary>A holding the feed can price: a listing with a quantity, not cash or an option contract.</summary>
    public static bool Priceable(Holding h) =>
        h.Symbol is not null && h.Symbol != Holding.CashSymbol && h.Quantity is { } q && q != 0
        && h.NamedCashCurrency is null && h.Option is null;

    /// <summary>
    /// Whether a holding's current price is so far from its listing's close a month or so before that
    /// the history may have been adjusted for a split since it was saved (3-for-2 or more either way),
    /// and should be read again. A real move that big only costs reading the months once more.
    /// </summary>
    public static bool LooksSplit(decimal currentPrice, decimal earlierClose) =>
        currentPrice > 0 && earlierClose > 0 && (currentPrice / earlierClose is < 0.7m or > 1.43m);

    public static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>
    /// The first day of each month from <paramref name="from"/> to <paramref name="to"/> that has
    /// ended by <paramref name="today"/>, newest first, at most <paramref name="max"/>: the month files
    /// a chart of that period could need.
    /// </summary>
    public static IReadOnlyList<DateOnly> MonthsInView(DateOnly from, DateOnly to, DateOnly today, int max)
    {
        var current = FirstOfMonth(today);
        var months = new List<DateOnly>();
        for (var m = FirstOfMonth(to < today ? to : today); m >= FirstOfMonth(from) && months.Count < max; m = m.AddMonths(-1))
        {
            if (m < current) months.Add(m);
        }

        return months;
    }
}
