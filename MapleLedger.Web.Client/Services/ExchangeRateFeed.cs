using MapleLedger.Core.Investments;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Gets today's exchange rates from the Bank of Canada, straight from the browser, and keeps the
/// ledger's rates up to date with them. Only the currency code is sent.
/// </summary>
public sealed class ExchangeRateFeed(ILedgerStore store, ILogger<ExchangeRateFeed> logger)
{
    // Not the app's HttpClient: that one points at this site and reports it unreachable on failure.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The Bank of Canada's latest rate for the currency, or null when it can't be had.</summary>
    public async Task<ExchangeRate?> FetchAsync(string currency, CancellationToken ct = default)
    {
        try
        {
            return BankOfCanadaRates.Parse(await Http.GetStringAsync(BankOfCanadaRates.LatestUrl(currency), ct), currency);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Couldn't get the {Currency} rate from the Bank of Canada: {Message}", currency, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Saves the Bank of Canada's rate for each currency whose stored rate is older than it (or
    /// missing). Returns the ledger's rates afterwards, and whether any fetch failed.
    /// </summary>
    public async Task<(IReadOnlyList<ExchangeRate> Rates, bool Failed)> RefreshAsync(IEnumerable<string> currencies, IReadOnlyList<ExchangeRate> stored, bool force = false)
    {
        var rates = stored.ToDictionary(r => r.Currency);
        var failed = false;
        foreach (var currency in currencies.Distinct())
        {
            var current = rates.GetValueOrDefault(currency);
            if (!force && current is not null && current.AsOf >= Periods.Today.AddDays(-1)) continue;

            var latest = await FetchAsync(currency);
            if (latest is null)
            {
                failed = true;
                continue;
            }

            if (current is null || latest.AsOf > current.AsOf || (force && latest.Rate != current.Rate))
            {
                rates[currency] = await store.SetExchangeRateAsync(latest);
            }
        }

        return (rates.Values.OrderBy(r => r.Currency, StringComparer.Ordinal).ToList(), failed);
    }
}
