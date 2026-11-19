using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace MapleLedger.Core.Investments;

/// <summary>
/// One position in an investment account on a statement date, as the broker's holdings report shows
/// it. Amounts are in <see cref="Currency"/>; <see cref="ExchangeRate"/> turns them into Canadian dollars
/// (1 for CAD), at the rate the user entered when importing.
/// </summary>
/// <param name="Symbol">The ticker or fund code, or null for something without one (a GIC, cash).</param>
/// <param name="Quantity">Units or shares; null for cash.</param>
/// <param name="BookValue">What the broker reports the position cost, in <see cref="Currency"/>. Not adjusted cost base for tax.</param>
/// <param name="PriceAsOf">When <see cref="Price"/> was quoted, for a price from the quote feed; null for the statement's.</param>
/// <param name="PriceSource">Where <see cref="Price"/> and <see cref="MarketValue"/> came from.</param>
public sealed partial record Holding(
    string? Symbol,
    string? Description,
    decimal? Quantity,
    decimal? Price,
    decimal MarketValue,
    decimal? BookValue,
    string Currency = Holding.Cad,
    decimal ExchangeRate = 1m,
    DateTimeOffset? PriceAsOf = null,
    HoldingPriceSource PriceSource = HoldingPriceSource.Statement)
{
    public const string Cad = "CAD";
    public const string CashSymbol = "CASH";
    public const int MaxSymbolLength = 32;
    public const int MaxDescriptionLength = 128;
    public const int MaxPerAccount = 500;

    /// <summary>Currencies a broker may hold cash in, for telling a cash row ("USD") from a ticker.</summary>
    private static readonly HashSet<string> CashCurrencies =
    [
        "CAD", "USD", "EUR", "GBP", "JPY", "CHF", "AUD", "NZD", "HKD", "CNH", "CNY", "SGD", "SEK", "NOK", "DKK",
        "MXN", "INR", "KRW", "ZAR", "BRL", "TWD", "TRY", "ILS", "PLN", "CZK", "HUF",
    ];

    /// <summary>
    /// The currency of a cash line named by its code, as Interactive Brokers lists cash ("USD",
    /// "CAD"): the symbol is a currency, with no book value and no description other than one saying
    /// it's cash. Null for anything else, so a fund whose ticker happens to be "USD" still reads as a fund.
    /// </summary>
    public static string? CashCurrencyOf(string? symbol, string? description, decimal? bookValue)
    {
        var code = symbol?.Trim().ToUpperInvariant();
        if (code is null || !CashCurrencies.Contains(code) || bookValue is not null) return null;
        return description is null || description.Contains("cash", StringComparison.OrdinalIgnoreCase) ? code : null;
    }

    /// <summary>
    /// The currency this holding is cash in when it's a cash line named by its code (see
    /// <see cref="CashCurrencyOf"/>); null otherwise, and for holdings already stored as <see cref="CashSymbol"/>.
    /// </summary>
    [JsonIgnore]
    public string? NamedCashCurrency => CashCurrencyOf(Symbol, Description, BookValue);

    /// <summary>
    /// A cash line named by its code (<see cref="NamedCashCurrency"/>) as cash in that currency: the
    /// quantity is the cash, whatever price and value the report gave it in another currency.
    /// <paramref name="rate"/> is what one unit of that currency is worth in CAD, when known. Itself
    /// for anything else.
    /// </summary>
    public Holding AsNamedCash(decimal? rate)
    {
        if (NamedCashCurrency is not { } currency) return this;
        return new Holding(CashSymbol, Description ?? $"{currency} cash", null, null, Quantity ?? MarketValue, null, currency,
            currency == Cad ? 1m : rate ?? (currency == Currency ? ExchangeRate : 1m));
    }

    /// <summary>The option contract the symbol names (INTC 17DEC27 35 C), or null for anything else.</summary>
    [JsonIgnore]
    public OptionContract? Option => OptionContract.Parse(Symbol);

    /// <summary>Shares behind each unit of <see cref="Quantity"/>: 100 for an option contract, else 1.</summary>
    [JsonIgnore]
    public int Multiplier => Option is null ? 1 : OptionContract.SharesPerContract;

    /// <summary>What <paramref name="quantity"/> units at <paramref name="price"/> (per share) are worth, to the cent.</summary>
    public decimal ValueAt(decimal quantity, decimal price) => Math.Round(quantity * price * Multiplier, 2);

    /// <summary>
    /// An option whose statement value is quantity × price, leaving out the 100 shares each contract
    /// covers (reports quote an option's price per share). Its value is then 1% of what it should be.
    /// </summary>
    [JsonIgnore]
    public bool MissesContractMultiplier =>
        PriceSource == HoldingPriceSource.Statement && Quantity is { } q && q != 0 && Price is > 0 && Multiplier > 1
        && Math.Abs(MarketValue - Math.Round(q * Price.Value, 2)) <= 0.01m;

    /// <summary>The holding valued at quantity × price × 100 when <see cref="MissesContractMultiplier"/>; itself otherwise.</summary>
    public Holding WithContractValue() =>
        MissesContractMultiplier ? this with { MarketValue = ValueAt(Quantity!.Value, Price!.Value) } : this;

    /// <summary>A ticker in brackets at the end of a description: "REDDIT INC CDR C$HDG (RDDT)", "GREENLANE RENEWABLES INC (GRN )".</summary>
    [GeneratedRegex(@"\(\s*([A-Z][A-Z0-9]{0,9}(?:[.\-][A-Z0-9]{1,4})?)\s*\)\s*$")]
    private static partial Regex TickerInDescription();

    /// <summary>
    /// The listing a report named only in its description, for reports without symbol or quantity
    /// columns: the symbol from the brackets at the end of the description ("… (RDDT)"), and the
    /// quantity the market value and price imply. Itself when it has both, or for cash and anything
    /// whose description doesn't end in a ticker.
    /// </summary>
    public Holding WithListingFromDescription()
    {
        var symbol = Symbol;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            if (Description is null || Description.Contains("cash", StringComparison.OrdinalIgnoreCase)) return this;
            if (TickerInDescription().Match(Description) is not { Success: true } match) return this;
            symbol = match.Groups[1].Value;
        }

        if (symbol == CashSymbol || NamedCashCurrency is not null) return this;
        var quantity = Quantity ?? (Price is > 0 && MarketValue != 0 ? Math.Round(MarketValue / Price.Value, 4) : null);
        return symbol == Symbol && quantity == Quantity ? this : this with { Symbol = symbol, Quantity = quantity };
    }

    /// <summary>What to call it: the symbol, else the description.</summary>
    [JsonIgnore]
    public string Label => Symbol ?? Description ?? "";

    [JsonIgnore]
    public decimal MarketValueCad => MarketValue * ExchangeRate;

    [JsonIgnore]
    public decimal? BookValueCad => BookValue * ExchangeRate;

    /// <summary>Market value less book value, in Canadian dollars; null without a book value.</summary>
    [JsonIgnore]
    public decimal? GainCad => BookValue is { } book ? (MarketValue - book) * ExchangeRate : null;

    /// <summary>
    /// What the holdings are worth in <paramref name="currency"/> (an account's): those in it at
    /// their own market value, the rest through Canadian dollars at <paramref name="rate"/>, what
    /// one unit of it is worth in CAD. Null when a holding needs converting and there's no rate.
    /// </summary>
    public static decimal? TotalIn(IEnumerable<Holding> holdings, string currency, decimal? rate)
    {
        var total = 0m;
        foreach (var h in holdings)
        {
            if (h.Currency == currency) total += h.MarketValue;
            else if (currency == Cad) total += h.MarketValueCad;
            else if (rate is { } r and > 0) total += h.MarketValueCad / r;
            else return null;
        }

        return Math.Round(total, 2);
    }
}

/// <summary>An account's holdings as of one statement date. Each import replaces the account's snapshot.</summary>
public sealed record HoldingsSnapshot(string AccountId, DateOnly AsOf, IReadOnlyList<Holding> Holdings)
{
    [JsonIgnore]
    public decimal MarketValueCad => Holdings.Sum(h => h.MarketValueCad);

    /// <summary>Book value of the holdings that have one.</summary>
    [JsonIgnore]
    public decimal BookValueCad => Holdings.Sum(h => h.BookValueCad ?? 0);

    /// <summary>Gain over book value of the holdings that have a book value.</summary>
    [JsonIgnore]
    public decimal GainCad => Holdings.Sum(h => h.GainCad ?? 0);

    [JsonIgnore]
    public bool HasBookValues => Holdings.Any(h => h.BookValue is not null);
}

/// <param name="SetBalance">Also set the account balance to the holdings' total, unless the balance is from a later date.</param>
public sealed record ImportHoldingsRequest(DateOnly AsOf, IReadOnlyList<Holding> Holdings, bool SetBalance = true);

public sealed record ImportHoldingsResult(HoldingsSnapshot Snapshot, bool BalanceUpdated);

/// <summary>Where a <see cref="Holding"/>'s price came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HoldingPriceSource>))]
public enum HoldingPriceSource
{
    /// <summary>The broker's statement or holdings report, as imported.</summary>
    Statement,

    /// <summary>The quote feed (stocktreemap.com), applied after the import.</summary>
    Quote,
}

/// <summary>Where an <see cref="ExchangeRate"/> came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExchangeRateSource>))]
public enum ExchangeRateSource
{
    /// <summary>The Bank of Canada's daily average rate, fetched by the app.</summary>
    BankOfCanada,

    /// <summary>Typed in by the user.</summary>
    Entered,
}

/// <summary>
/// What one unit of <see cref="Currency"/> is worth in Canadian dollars. The ledger keeps one rate per
/// currency; holdings in that currency are valued at it, and setting a new one revalues them.
/// </summary>
public sealed record ExchangeRate(string Currency, decimal Rate, DateOnly AsOf, ExchangeRateSource Source = ExchangeRateSource.Entered)
{
    public const string Usd = "USD";
    public const decimal MaxRate = 10_000m;
}

/// <summary>
/// The ledger's exchange rates, for adding up balances kept in their accounts' own currencies in
/// Canadian dollars.
/// </summary>
public sealed class CurrencyRates(IEnumerable<ExchangeRate> rates)
{
    private readonly Dictionary<string, decimal> _rates = rates.ToDictionary(r => r.Currency, r => r.Rate);

    public static CurrencyRates None { get; } = new([]);

    /// <summary>What one unit of the currency is worth in Canadian dollars: 1 for CAD, null without a rate.</summary>
    public decimal? RateOf(string currency) => currency == Holding.Cad ? 1m : _rates.TryGetValue(currency, out var rate) ? rate : null;

    /// <summary>The amount in Canadian dollars; at face value when the ledger has no rate for the currency.</summary>
    public decimal ToCad(decimal amount, string currency) => Math.Round(amount * (RateOf(currency) ?? 1m), 2);

    /// <summary>The account's balance (kept in its own currency) in Canadian dollars; 0 without one.</summary>
    public decimal BalanceInCad(Fdx.Account account) => ToCad(account.CurrentBalance ?? 0, CurrencyOf(account));

    public static string CurrencyOf(Fdx.Account account) => account.Currency?.CurrencyCode ?? Holding.Cad;
}
