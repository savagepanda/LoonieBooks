using System.Text.Json;
using System.Text.RegularExpressions;

namespace MapleLedger.Core.Investments;

/// <summary>
/// Option prices from Yahoo Finance's chart endpoint, for the contracts stocktreemap.com doesn't
/// carry. Browsers can't call Yahoo themselves (it allows no other sites), so the web host asks on
/// their behalf, one OCC symbol at a time, and sends back <see cref="Quote"/>s.
/// </summary>
public static partial class YahooQuotes
{
    /// <summary>The most contracts one request to the web host may ask about.</summary>
    public const int MaxSymbols = 25;

    public static string ChartUrl(string occSymbol) =>
        $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(occSymbol)}?range=5d&interval=1d";

    [GeneratedRegex("^[A-Z][A-Z0-9]{0,5}[0-9]{6}[CP][0-9]{8}$")]
    private static partial Regex OccSymbol();

    /// <summary>Whether <paramref name="symbol"/> is an OCC option symbol (INTC271217C00035000), the only kind the web host looks up.</summary>
    public static bool IsOccSymbol(string? symbol) => symbol is not null && OccSymbol().IsMatch(symbol);

    /// <summary>
    /// The quote in a chart response: its <c>meta</c>'s last price, previous close, currency and
    /// time. Null for an error, a symbol Yahoo doesn't know, or a price that isn't positive.
    /// </summary>
    public static Quote? ParseChart(string json, string symbol)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chart", out var chart)
                || !chart.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0
                || !results[0].TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!meta.TryGetProperty("regularMarketPrice", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDecimal(out var price) || price <= 0) return null;
            if (!meta.TryGetProperty("regularMarketTime", out var t) || t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out var seconds) || seconds <= 0) return null;
            var currency = meta.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()?.Trim().ToUpperInvariant() : null;
            if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper)) return null;

            decimal? previous = meta.TryGetProperty("chartPreviousClose", out var pc) && pc.ValueKind == JsonValueKind.Number && pc.TryGetDecimal(out var prev) && prev > 0 ? prev : null;
            return new Quote(symbol, price, previous, currency, DateTimeOffset.FromUnixTimeSeconds(seconds));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }
}
