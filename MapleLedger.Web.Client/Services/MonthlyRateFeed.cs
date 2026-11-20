using MapleLedger.Core.Investments;
using MapleLedger.Fdx;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Gets the Bank of Canada's monthly average exchange rates for the accounts' other currencies,
/// straight from the browser, and saves the months the ledger doesn't have yet. Spending in those
/// currencies is converted at its month's rate (the latest rate for the month under way). Only the
/// currency code and a start month are sent.
/// </summary>
public sealed class MonthlyRateFeed(ILedgerStore store, ILogger<MonthlyRateFeed> logger)
{
    // Not the app's HttpClient: that one points at this site and reports it unreachable on failure.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Each currency's first missing month already asked for since the app opened, so a month the Bank
    // hasn't published (or never will) isn't asked for again on every load.
    private readonly HashSet<(string Currency, DateOnly Month)> _asked = [];

    /// <summary>
    /// Fetches the complete months from <paramref name="from"/>'s to last month that the ledger has no
    /// rate for, for each currency other than CAD among <paramref name="accounts"/>. True when any were saved.
    /// </summary>
    public async Task<bool> RefreshAsync(IEnumerable<Account> accounts, DateOnly from)
    {
        var currencies = accounts.Select(CurrencyRates.CurrencyOf).Where(c => c != Holding.Cad).Distinct().ToList();
        if (currencies.Count == 0) return false;

        var lastMonth = new DateOnly(Periods.Today.Year, Periods.Today.Month, 1).AddMonths(-1);
        var first = new DateOnly(from.Year, from.Month, 1);
        if (first > lastMonth) return false;

        var stored = await store.GetMonthlyExchangeRatesAsync();
        var fetched = new List<MonthlyExchangeRate>();
        foreach (var currency in currencies)
        {
            var have = stored.Where(r => r.Currency == currency).Select(r => r.Month).ToHashSet();
            var missing = first;
            while (missing <= lastMonth && have.Contains(missing)) missing = missing.AddMonths(1);
            if (missing > lastMonth || !_asked.Add((currency, missing))) continue;

            try
            {
                var rates = BankOfCanadaMonthlyRates.Parse(await Http.GetStringAsync(BankOfCanadaMonthlyRates.Url(currency, missing)), currency);
                fetched.AddRange(rates.Where(r => r.Month >= missing && r.Month <= lastMonth && !have.Contains(r.Month)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogInformation("Couldn't get the {Currency} monthly rates from the Bank of Canada: {Message}", currency, ex.Message);
                _asked.Remove((currency, missing)); // try again next time
            }
        }

        var saved = 0;
        foreach (var chunk in fetched.Chunk(ApplyMonthlyExchangeRatesRequest.MaxRates))
        {
            saved += (await store.ApplyMonthlyExchangeRatesAsync(new ApplyMonthlyExchangeRatesRequest(chunk))).Rates;
        }

        return saved > 0;
    }
}
