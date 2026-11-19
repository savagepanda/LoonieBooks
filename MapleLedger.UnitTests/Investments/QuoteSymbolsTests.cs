using MapleLedger.Core.Investments;

namespace MapleLedger.UnitTests.Investments;

public class QuoteSymbolsTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static readonly DateTimeOffset At = new(2026, 11, 18, 15, 1, 27, TimeSpan.Zero);

    private static readonly QuoteBook Book = new(
    [
        new Quote("XEQT.TO", 44.13m, 43.9m, "CAD", At),
        new Quote("AAPL", 355.575m, 351.91m, "USD", At),
        new Quote("AAPL.NE", 31.2m, null, "CAD", At),          // the Cboe Canada CDR
        new Quote("BRK-B", 480m, null, "USD", At),
        new Quote("REI-UN.TO", 21.535m, 21.43m, "CAD", At),
        new Quote("DLR-U.TO", 10.02m, null, "USD", At),
        new Quote("CM", 70m, null, "USD", At),                // the NYSE listing of a TSX bank
        new Quote("CASH.TO", 50.065m, null, "CAD", At),
    ]);

    /// <summary>Without a price, nothing to judge the feed's against.</summary>
    private static Holding Held(string symbol, string currency = Holding.Cad, decimal quantity = 10m, decimal? price = null) =>
        new(symbol, null, quantity, price, quantity * (price ?? 0m), null, currency);

    private static QuoteMatch Resolve(Holding h, DateOnly? statement = null) =>
        QuoteSymbols.Resolve(h, Book, statement ?? Today.AddDays(-30), Today);

    [Theory]
    [InlineData("XEQT", "CAD", "XEQT.TO")]
    [InlineData("xeqt", "CAD", "XEQT.TO")]
    [InlineData("XEQT.TO", "CAD", "XEQT.TO")]
    [InlineData("TSX:XEQT", "CAD", "XEQT.TO")]
    [InlineData("XEQT-T", "CAD", "XEQT.TO")]
    [InlineData("XEQT CN", "CAD", "XEQT.TO")]
    [InlineData("REI.UN", "CAD", "REI-UN.TO")]
    [InlineData("REI.UN.TO", "CAD", "REI-UN.TO")]
    [InlineData("AAPL", "USD", "AAPL")]
    [InlineData("NASDAQ:AAPL", "USD", "AAPL")]
    [InlineData("AAPL", "CAD", "AAPL.NE")]                     // held in CAD: the Canadian CDR, not the US stock
    [InlineData("BRK.B", "USD", "BRK-B")]
    [InlineData("BRK/B", "USD", "BRK-B")]
    [InlineData("BRK B", "USD", "BRK-B")]
    [InlineData("DLR.U", "USD", "DLR-U.TO")]                   // a TSX unit that trades in USD
    [InlineData("CASH", "CAD", "CASH.TO")]                      // the Global X ETF, when it has a quantity
    public void FindsTheListingInTheHoldingsCurrency(string symbol, string currency, string expected)
    {
        var match = Resolve(Held(symbol, currency));
        Assert.True(match.Priced, match.Miss?.ToString());
        Assert.Equal(expected, match.QuoteSymbol);
        Assert.Equal(currency, match.Quote!.Currency);
    }

    [Theory]
    [InlineData("TDB902", QuoteMiss.FundCode)]
    [InlineData("RBF556", QuoteMiss.FundCode)]
    [InlineData("FID5982", QuoteMiss.FundCode)]
    [InlineData("037833100", QuoteMiss.SecurityId)]             // a CUSIP
    [InlineData("CA0679011084", QuoteMiss.SecurityId)]          // an ISIN
    [InlineData("ZZZZ", QuoteMiss.NotListed)]
    [InlineData("CM", QuoteMiss.OtherCurrency)]                 // CIBC held in CAD: only the NYSE (USD) listing is in the feed
    [InlineData("GIC 4.5% 2027", QuoteMiss.NotListed)]
    public void SaysWhyAHoldingIsntPriced(string symbol, QuoteMiss miss)
    {
        var match = Resolve(Held(symbol));
        Assert.False(match.Priced);
        Assert.Equal(miss, match.Miss);
    }

    [Fact]
    public void AListingOnlyInAnotherCurrencyIsNamedAsLikelyAnotherCompany()
    {
        var match = Resolve(Held("CM"));
        Assert.Equal(("CM", "USD"), (match.QuoteSymbol, match.ListedCurrency));
        Assert.Equal("not in the price feed (its CM is a USD listing, likely a different company)", QuoteSymbols.Describe(match));
        Assert.Equal("not in the price feed", QuoteSymbols.Describe(Resolve(Held("ZZZZ"))));
    }

    [Fact]
    public void CashAndHoldingsWithoutAQuantityArentPriceable()
    {
        Assert.Equal(QuoteMiss.NotPriceable, Resolve(new Holding(Holding.CashSymbol, "Cash", null, null, 500m, null)).Miss);
        Assert.Equal(QuoteMiss.NotPriceable, Resolve(new Holding(null, "GIC", 1m, 1000m, 1000m, null)).Miss);

        // Interactive Brokers' cash lines, as imported before they were read as cash.
        Assert.Equal(QuoteMiss.NotPriceable, Resolve(new Holding("USD", null, 3737.58m, 1.4299m, 5344.37m, null, "USD", 1.4291m)).Miss);
        Assert.Equal(QuoteMiss.NotPriceable, Resolve(new Holding("CAD", null, 0.69m, 1m, 0.69m, null, "USD", 1.4291m)).Miss);
    }

    [Fact]
    public void APriceFarFromTheHoldingsOwnRecentOneIsntUsed()
    {
        // Within half to double of the statement's 40: used.
        Assert.True(Resolve(Held("XEQT", price: 40m)).Priced);
        Assert.True(Resolve(Held("XEQT", price: 25m)).Priced);

        // A tenth of it (a wrong listing, or a split): left out, naming the listing.
        var wrong = Resolve(Held("XEQT", price: 400m));
        Assert.Equal((QuoteMiss.PriceLooksWrong, "XEQT.TO"), (wrong.Miss, wrong.QuoteSymbol));
        Assert.Equal(QuoteMiss.PriceLooksWrong, Resolve(Held("XEQT", price: 10m)).Miss);

        // Against a statement from a year ago there's nothing recent to judge by.
        Assert.True(Resolve(Held("XEQT", price: 10m), statement: Today.AddYears(-1)).Priced);

        // A price quoted lately counts as recent, whatever the statement's date.
        var quoted = Held("XEQT", price: 10m) with { PriceAsOf = At.AddDays(-1), PriceSource = HoldingPriceSource.Quote };
        Assert.Equal(QuoteMiss.PriceLooksWrong, Resolve(quoted, statement: Today.AddYears(-1)).Miss);
    }

    [Fact]
    public void AQuoteTheFeedHasntUpdatedForDaysIsPricedButFlaggedStale()
    {
        // Friday's close on the Tuesday after a long weekend is current; a week-old one isn't.
        var tuesday = new DateOnly(2026, 11, 17);
        Assert.False(QuoteSymbols.IsStale(new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.Zero), tuesday));
        Assert.True(QuoteSymbols.IsStale(new DateTimeOffset(2026, 11, 10, 21, 0, 0, TimeSpan.Zero), tuesday));

        var book = new QuoteBook([new Quote("OLD.TO", 5m, null, "CAD", new DateTimeOffset(2026, 11, 10, 21, 0, 0, TimeSpan.Zero)), Book.Find("XEQT.TO")!]);
        var old = QuoteSymbols.Resolve(Held("OLD"), book, tuesday.AddDays(-30), tuesday);
        Assert.True(old.Priced);
        Assert.True(old.Stale);
        Assert.False(QuoteSymbols.Resolve(Held("XEQT"), book, tuesday.AddDays(-30), new DateOnly(2026, 11, 18)).Stale);
    }

    [Fact]
    public void ReadsTheFeedByItsFieldNames()
    {
        const string json = """
            {"v":1,"source":"stocktreemap.com","asOfUtc":"2026-11-18T15:01:27Z",
             "fields":["c","s","t","p","pc","new"],
             "quotes":[["CAD","XEQT.TO",1795014087,44.13,43.9,"x"],
                       ["USD","AAPL",1795014087,355.575,null,"x"],
                       ["CAD","BAD",1795014087,0,null,"x"],
                       ["CA","BAD2",1795014087,1,null,"x"],
                       ["CAD","",1795014087,1,null,"x"],
                       "not a row",
                       ["CAD","SHORT"]]}
            """;

        var book = StockTreeMapQuotes.Parse(json);
        Assert.Equal(2, book.Count);
        Assert.Equal(new Quote("XEQT.TO", 44.13m, 43.9m, "CAD", DateTimeOffset.FromUnixTimeSeconds(1795014087)), book.Find("xeqt.to"));
        Assert.Null(book.Find("AAPL")!.PreviousClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795014087), book.AsOf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("""{"v":2,"fields":["s","p","c","t"],"quotes":[["A",1,"USD",1]]}""")]
    [InlineData("""{"v":1,"fields":["s","p"],"quotes":[["A",1]]}""")]
    [InlineData("""{"v":1,"quotes":[]}""")]
    public void AnythingElseIsAnEmptyBook(string json) =>
        Assert.Equal(0, StockTreeMapQuotes.Parse(json).Count);
}
