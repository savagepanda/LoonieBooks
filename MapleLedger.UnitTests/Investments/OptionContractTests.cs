using MapleLedger.Core.Import;
using MapleLedger.Core.Investments;

namespace MapleLedger.UnitTests.Investments;

public class OptionContractTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static readonly DateTimeOffset At = new(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("INTC 17DEC27 35 C", "INTC271217C00035000")]   // Interactive Brokers
    [InlineData("intc 17dec27 35 c", "INTC271217C00035000")]
    [InlineData("INTC  17DEC27  35  C", "INTC271217C00035000")]
    [InlineData("INTC DEC17'27 35 CALL", "INTC271217C00035000")] // IBKR's website
    [InlineData("SPY 16JAN26 450.5 P", "SPY260116P00450500")]
    [InlineData("F 20JUN25 2.5 C", "F250620C00002500")]
    [InlineData("BRK.B 15JAN27 500 C", "BRKB270115C00500000")]
    [InlineData("INTC271217C00035000", "INTC271217C00035000")]  // OCC
    [InlineData("INTC  271217C00035000", "INTC271217C00035000")] // OCC with the root padded
    [InlineData("INTC17Dec27C35.00", "INTC271217C00035000")]    // Questrade
    public void ReadsOptionSymbols(string symbol, string occ)
    {
        var option = OptionContract.Parse(symbol);
        Assert.NotNull(option);
        Assert.Equal(occ, option.OccSymbol);
    }

    [Fact]
    public void ReadsTheContractsTerms()
    {
        Assert.Equal(new OptionContract("INTC", new DateOnly(2027, 12, 17), 35m, OptionRight.Call), OptionContract.Parse("INTC 17DEC27 35 C"));
        Assert.Equal("INTC Dec 17, 2027 35 call", OptionContract.Parse("INTC 17DEC27 35 C")!.Describe());
    }

    [Theory]
    [InlineData("AAPL")]
    [InlineData("BRK.B")]
    [InlineData("XEQT.TO")]
    [InlineData("TSX:XEQT")]
    [InlineData("TDB902")]
    [InlineData("037833100")]
    [InlineData("CA0679011084")]
    [InlineData("GIC 4.5% 2027")]
    [InlineData("INTC 31FEB27 35 C")]  // no such day
    [InlineData("INTC 17XYZ27 35 C")]  // no such month
    [InlineData("")]
    [InlineData(null)]
    public void OtherSymbolsAreNotOptions(string? symbol) => Assert.Null(OptionContract.Parse(symbol));

    [Fact]
    public void AContractIsWorth100SharesAtItsPrice()
    {
        var option = new Holding("INTC 17DEC27 35 C", null, 3m, 26.75m, 80.25m, null, "USD", 1.4291m);
        Assert.Equal(100, option.Multiplier);
        Assert.True(option.MissesContractMultiplier);
        Assert.Equal(8025m, option.WithContractValue().MarketValue);
        Assert.False(option.WithContractValue().MissesContractMultiplier);   // only once
        Assert.Equal(7755m, option.ValueAt(3m, 25.85m));

        // A stock, or an option already valued right, is left as it is.
        var stock = new Holding("INTC", null, 600m, 55.05m, 33030m, null, "USD");
        Assert.Equal((1, false), (stock.Multiplier, stock.MissesContractMultiplier));
        Assert.Same(stock, stock.WithContractValue());
        Assert.False((option with { MarketValue = 8025m }).MissesContractMultiplier);
    }

    [Fact]
    public void AnImportedOptionIsValuedAt100SharesAContract()
    {
        const string text = """
            Symbol	Quantity	Price	Market Value
            INTC	600	55.05	33030.00
            INTC 17DEC27 35 C	3	26.75	80.25
            AAPL 15JAN27 200 P	2	4.10	820.00
            """;

        var table = HoldingsReader.ReadText(text);
        var result = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers), defaultCurrency: "USD");
        Assert.Equal([33030m, 8025m, 820m], result.Holdings.Select(h => h.MarketValue));

        // No value column: worked out from quantity and price, with the 100 shares.
        var noValue = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers) with { MarketValue = null }, defaultCurrency: "USD");
        Assert.Equal([33030m, 8025m, 820m], noValue.Holdings.Select(h => h.MarketValue));
    }

    [Fact]
    public void OptionsArePricedFromYahooNotTheFeed()
    {
        var option = new Holding("INTC 17DEC27 35 C", null, 3m, 26.75m, 8025m, null, "USD", 1.4291m);
        var feed = new QuoteBook([new Quote("INTC", 55m, null, "USD", At)]);
        var miss = QuoteSymbols.Resolve(option, feed, Today.AddDays(-2), Today);
        Assert.Equal(QuoteMiss.Option, miss.Miss);

        var yahoo = new QuoteBook([new Quote("INTC271217C00035000", 25.85m, 29.17m, "USD", At)]);
        var priced = QuoteSymbols.ResolveOption(option, yahoo, Today);
        Assert.True(priced.Priced);
        Assert.Equal(("INTC271217C00035000", 25.85m), (priced.QuoteSymbol, priced.Quote!.Price));
        Assert.False(priced.Stale);

        Assert.Equal(QuoteMiss.OptionNotFound, QuoteSymbols.ResolveOption(option, QuoteBook.Empty, Today).Miss);
        Assert.Equal(QuoteMiss.OtherCurrency, QuoteSymbols.ResolveOption(option with { Currency = "CAD" }, yahoo, Today).Miss);
        Assert.True(QuoteSymbols.ResolveOption(option, yahoo, Today.AddDays(10)).Stale);
    }

    [Fact]
    public void ReadsYahoosChart()
    {
        const string json = """
            {"chart":{"result":[{"meta":{"currency":"USD","symbol":"INTC271217C00035000","instrumentType":"OPTION",
            "regularMarketTime":1795014305,"regularMarketPrice":25.85,"chartPreviousClose":29.17}}],"error":null}}
            """;
        Assert.Equal(new Quote("INTC271217C00035000", 25.85m, 29.17m, "USD", DateTimeOffset.FromUnixTimeSeconds(1795014305)),
            YahooQuotes.ParseChart(json, "INTC271217C00035000"));

        Assert.Null(YahooQuotes.ParseChart("""{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found"}}}""", "X"));
        Assert.Null(YahooQuotes.ParseChart("""{"chart":{"result":[{"meta":{"currency":"USD","regularMarketTime":1795014305,"regularMarketPrice":0}}]}}""", "X"));
        Assert.Null(YahooQuotes.ParseChart("<html>", "X"));
    }

    [Theory]
    [InlineData("INTC271217C00035000", true)]
    [InlineData("F250620C00002500", true)]
    [InlineData("AAPL", false)]
    [InlineData("INTC271217C0003500", false)]
    [InlineData("../v7/finance/quote", false)]
    [InlineData("intc271217c00035000", false)]
    public void TheWebHostLooksUpOnlyOccSymbols(string symbol, bool allowed) => Assert.Equal(allowed, YahooQuotes.IsOccSymbol(symbol));
}
