using System.Globalization;
using System.Text.Json;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Investments;

/// <summary>
/// The Bank of Canada's average rate for a month: what one unit of <see cref="Currency"/> was worth in
/// Canadian dollars over it. <see cref="Month"/> is the month's first day.
/// </summary>
public sealed record MonthlyExchangeRate(string Currency, DateOnly Month, decimal Rate);

public sealed record ApplyMonthlyExchangeRatesRequest(IReadOnlyList<MonthlyExchangeRate> Rates)
{
    public const int MaxRates = 2000;
}

/// <param name="Rates">Monthly rates added or changed.</param>
public sealed record ApplyMonthlyExchangeRatesResult(int Rates);

/// <summary>
/// Converts money kept in other currencies to Canadian dollars at the rate of the month it moved in:
/// the month's average rate, or the ledger's latest rate for a month without one (the month under way,
/// or a currency the Bank of Canada doesn't publish), or at face value without either. A past month's
/// total then stays put when the dollar moves, so one year compares fairly with the next.
/// </summary>
public sealed class MonthlyCurrencyRates(CurrencyRates latest, IEnumerable<MonthlyExchangeRate> monthly)
{
    private readonly Dictionary<(string Currency, int Year, int Month), decimal> _monthly =
        monthly.GroupBy(r => (r.Currency, r.Month.Year, r.Month.Month)).ToDictionary(g => g.Key, g => g.Last().Rate);

    public static MonthlyCurrencyRates None { get; } = new(CurrencyRates.None, []);

    public CurrencyRates Latest => latest;

    /// <summary>What one unit of the currency was worth in Canadian dollars in the month of <paramref name="date"/>; null without any rate.</summary>
    public decimal? RateOn(string currency, DateOnly date) =>
        currency == Holding.Cad ? 1m
        : _monthly.TryGetValue((currency, date.Year, date.Month), out var rate) ? rate
        : latest.RateOf(currency);

    public decimal ToCad(decimal amount, string currency, DateOnly date) =>
        currency == Holding.Cad ? amount : Math.Round(amount * (RateOn(currency, date) ?? 1m), 2);

    /// <summary>
    /// The transactions with their amounts in Canadian dollars, each at its own month's rate, by the
    /// currency of its account in <paramref name="currencyOf"/> (Canadian dollars for an account not in it).
    /// </summary>
    public IReadOnlyList<Transaction> InCad(IReadOnlyList<Transaction> transactions, IReadOnlyDictionary<string, string> currencyOf)
    {
        if (currencyOf.Values.All(c => c == Holding.Cad)) return transactions;
        return transactions
            .Select(t => currencyOf.TryGetValue(t.AccountId ?? "", out var currency) && currency != Holding.Cad
                ? t with { Amount = ToCad(t.Amount, currency, t.EffectiveDate) }
                : t)
            .ToList();
    }
}

/// <summary>The Bank of Canada's monthly average exchange rates (its Valet API's FXM series). Only the currency is sent.</summary>
public static class BankOfCanadaMonthlyRates
{
    /// <summary>The monthly averages of 1 <paramref name="currency"/> in Canadian dollars from <paramref name="from"/>'s month on.</summary>
    public static Uri Url(string currency, DateOnly from) =>
        new($"https://www.bankofcanada.ca/valet/observations/FXM{currency.ToUpperInvariant()}CAD/json?start_date={from.Year:D4}-{from.Month:D2}-01");

    /// <summary>The months' rates in a Valet response; empty when it has none for the currency.</summary>
    public static IReadOnlyList<MonthlyExchangeRate> Parse(string json, string currency)
    {
        var code = currency.ToUpperInvariant();
        var rates = new List<MonthlyExchangeRate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("observations", out var observations) || observations.ValueKind != JsonValueKind.Array) return rates;
            foreach (var observation in observations.EnumerateArray())
            {
                if (observation.TryGetProperty("d", out var d) && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && observation.TryGetProperty($"FXM{code}CAD", out var series) && series.TryGetProperty("v", out var v)
                    && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
                    && rate is > 0 and <= ExchangeRate.MaxRate)
                {
                    rates.Add(new MonthlyExchangeRate(code, new DateOnly(date.Year, date.Month, 1), rate));
                }
            }
        }
        catch (JsonException)
        {
        }

        return rates;
    }
}
