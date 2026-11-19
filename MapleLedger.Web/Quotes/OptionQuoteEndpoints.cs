using System.Collections.Concurrent;
using MapleLedger.Core.Investments;

namespace MapleLedger.Web.Quotes;

/// <summary>
/// <c>GET /quotes/options?symbols=INTC271217C00035000,…</c>: Yahoo Finance's prices for option
/// contracts, which stocktreemap.com's feed doesn't carry and the browser can't fetch from Yahoo
/// itself. Only OCC option symbols are looked up (so this isn't a general way to reach Yahoo), at
/// most <see cref="YahooQuotes.MaxSymbols"/> at a time, and each answer is kept for a few minutes.
/// Nothing is stored; the browser asks only after the person turned option prices on.
/// </summary>
public static class OptionQuoteEndpoints
{
    private const string ClientName = "yahoo";

    /// <summary>How long Yahoo's answer for a contract (a price, or none) is reused.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, (Quote? Quote, DateTimeOffset At)> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Concurrency = new(4);

    public static IServiceCollection AddOptionQuotes(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            // Yahoo turns away requests without a browser-like User-Agent (429).
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; LoonieBooks)");
        });
        return services;
    }

    public static IEndpointRouteBuilder MapOptionQuotes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/quotes/options", async (string? symbols, IHttpClientFactory http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var wanted = (symbols ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (wanted.Count is 0 or > YahooQuotes.MaxSymbols || !wanted.All(YahooQuotes.IsOccSymbol))
            {
                return Results.BadRequest($"Send 1 to {YahooQuotes.MaxSymbols} OCC option symbols, such as INTC271217C00035000.");
            }

            var client = http.CreateClient(ClientName);
            var logger = logs.CreateLogger(typeof(OptionQuoteEndpoints));
            var quotes = await Task.WhenAll(wanted.Select(s => LookUpAsync(client, s, logger, ct)));
            return Results.Ok(quotes.Where(q => q is not null));
        });

        return app;
    }

    private static async Task<Quote?> LookUpAsync(HttpClient client, string symbol, ILogger logger, CancellationToken ct)
    {
        if (Cache.TryGetValue(symbol, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor) return cached.Quote;

        await Concurrency.WaitAsync(ct);
        try
        {
            using var response = await client.GetAsync(YahooQuotes.ChartUrl(symbol), ct);
            // 404 is Yahoo not knowing the contract; anything else is Yahoo having trouble, not cached.
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation("Yahoo Finance answered {Status} for {Symbol}.", (int)response.StatusCode, symbol);
                return null;
            }

            var quote = response.IsSuccessStatusCode ? YahooQuotes.ParseChart(await response.Content.ReadAsStringAsync(ct), symbol) : null;
            Cache[symbol] = (quote, DateTimeOffset.UtcNow);
            return quote;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Couldn't reach Yahoo Finance for {Symbol}: {Message}", symbol, ex.Message);
            return null;
        }
        finally
        {
            Concurrency.Release();
        }
    }
}
