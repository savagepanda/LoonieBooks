using System.Globalization;
using System.Text.Json;

namespace MapleLedger.Core.Investments;

/// <summary>
/// The Bank of Canada's daily average exchange rates (its Valet API, open to any site). Only the
/// currency is sent; nothing about the ledger. The rate for a day is published late that afternoon,
/// on business days.
/// </summary>
public static class BankOfCanadaRates
{
    /// <summary>The latest observation of 1 <paramref name="currency"/> in Canadian dollars.</summary>
    public static Uri LatestUrl(string currency) =>
        new($"https://www.bankofcanada.ca/valet/observations/FX{currency.ToUpperInvariant()}CAD/json?recent=1");

    /// <summary>The rate in a Valet response, or null when it has none for the currency.</summary>
    public static ExchangeRate? Parse(string json, string currency)
    {
        var code = currency.ToUpperInvariant();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("observations", out var observations) || observations.ValueKind != JsonValueKind.Array) return null;

            ExchangeRate? latest = null;
            foreach (var observation in observations.EnumerateArray())
            {
                if (observation.TryGetProperty("d", out var d) && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && observation.TryGetProperty($"FX{code}CAD", out var series) && series.TryGetProperty("v", out var v)
                    && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
                    && rate is > 0 and <= ExchangeRate.MaxRate
                    && (latest is null || date > latest.AsOf))
                {
                    latest = new ExchangeRate(code, rate, date, ExchangeRateSource.BankOfCanada);
                }
            }

            return latest;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
