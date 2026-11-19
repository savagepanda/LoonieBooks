using System.Text;
using MapleLedger.Core.Import;
using MapleLedger.Core.Investments;

namespace MapleLedger.UnitTests.Import;

public class HoldingsReaderTests
{
    [Fact]
    public void ReadsABankBrokerageExportWithTitleLinesAndATotal()
    {
        const string csv = """
            "Holdings for account ending 4321"
            "As of November 14, 2026"

            Symbol,Description,Quantity,Price,Book Value,Market Value,Unrealized Gain/Loss ($)
            XEQT,ISHARES CORE EQUITY ETF PORTFOLIO,"1,250",$34.10,"$38,000.00","$42,625.00","$4,625.00"
            ZAG,BMO AGGREGATE BOND INDEX ETF,400.5,13.95,"5,800.00","5,586.98",-213.02
            ,CASH,,,,"1,204.33",
            Total,,,,,"49,416.31",
            """;

        var table = HoldingsReader.ReadText(csv);
        var result = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers));

        Assert.True(table.HasHeader);
        Assert.Equal(new DateOnly(2026, 11, 14), table.AsOf);
        Assert.Equal(3, result.Holdings.Count);
        Assert.Equal(new Holding("XEQT", "ISHARES CORE EQUITY ETF PORTFOLIO", 1250m, 34.10m, 42625.00m, 38000.00m), result.Holdings[0]);
        Assert.Equal(400.5m, result.Holdings[1].Quantity);
        Assert.Equal(new Holding(Holding.CashSymbol, "CASH", null, null, 1204.33m, null), result.Holdings[2]);
        Assert.Single(result.Skipped);
        Assert.Equal(49416.31m, new HoldingsSnapshot("a", new DateOnly(2026, 11, 14), result.Holdings).MarketValueCad);
    }

    [Fact]
    public void ReadsInteractiveBrokersCashRowsAsCashInTheirOwnCurrency()
    {
        // IBKR lists cash per currency, priced at the rate into the base currency (here CAD) and
        // valued in it: 3,737.58 US dollars is "1.4299" and "5,344.37". A USD account reads rows
        // that state no currency as US dollars.
        const string text = """
            Symbol	Quantity	Price	Market Value
            AMBQ	200	44.03	8806.00
            CAD	0.69	1.00	0.69
            USD	3737.58	1.4299	5344.37
            """;

        var table = HoldingsReader.ReadText(text);
        var result = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers), defaultCurrency: "USD");

        Assert.Equal(
            [
                new Holding("AMBQ", null, 200m, 44.03m, 8806.00m, null, "USD"),
                new Holding(Holding.CashSymbol, "CAD cash", null, null, 0.69m, null, "CAD"),
                new Holding(Holding.CashSymbol, "USD cash", null, null, 3737.58m, null, "USD"),
            ],
            result.Holdings);
    }

    [Fact]
    public void AFundWhoseTickerIsACurrencyCodeStaysAFund()
    {
        const string csv = """
            Symbol,Description,Quantity,Price,Book Value,Market Value
            USD,PROSHARES ULTRA SEMICONDUCTORS,10,60.00,500.00,600.00
            """;

        var table = HoldingsReader.ReadText(csv);
        var holding = Assert.Single(HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers), defaultCurrency: "USD").Holdings);

        Assert.Equal(new Holding("USD", "PROSHARES ULTRA SEMICONDUCTORS", 10m, 60m, 600m, 500m, "USD"), holding);
    }

    [Fact]
    public void PicksTheMarketCurrencyColumnsAndFiltersOneAccountOfSeveral()
    {
        // Wealthsimple's report: several accounts, full account numbers, and book value in two currencies.
        const string csv = """
            Account Name,Account Type,Account Classification,Account Number,Symbol,Exchange,Name,Quantity,Market Price,Market Price Currency,Book Value (CAD),Book Value Currency (CAD),Book Value (Market),Book Value Currency (Market),Market Value,Market Value Currency
            TFSA,TFSA,Self Directed,HQ12345678CAD,VFV,TSX,Vanguard S&P 500 Index ETF,10,150.00,CAD,1200.00,CAD,1200.00,CAD,1500.00,CAD
            RRSP,RRSP,Self Directed,HQ87654321CAD,AAPL,NASDAQ,Apple Inc,5,200.00,USD,1300.00,CAD,950.00,USD,1000.00,USD
            TFSA,TFSA,Self Directed,HQ12345678CAD,XIC,TSX,iShares Core S&P/TSX,20,40.00,CAD,700.00,CAD,700.00,CAD,800.00,CAD
            """;

        var table = HoldingsReader.ReadText(csv);
        var columns = HoldingsReader.Guess(table.Headers);
        var accounts = HoldingsReader.Accounts(table, columns);
        var rrsp = HoldingsReader.Parse(table, columns, "HQ87654321CAD");
        var tfsa = HoldingsReader.Parse(table, columns, "HQ12345678CAD");

        Assert.Equal(table.Headers.ToList().IndexOf("Book Value (Market)"), columns.BookValue);
        Assert.Equal(table.Headers.ToList().IndexOf("Market Value Currency"), columns.Currency);
        Assert.Equal(table.Headers.ToList().IndexOf("Market Value"), columns.MarketValue);
        Assert.Equal(table.Headers.ToList().IndexOf("Market Price"), columns.Price);
        Assert.Equal(["TFSA ••••5678", "RRSP ••••4321"], accounts.Select(a => a.Label));
        Assert.Equal(new Holding("AAPL", "Apple Inc", 5m, 200m, 1000m, 950m, "USD"), Assert.Single(rrsp.Holdings));
        Assert.Equal(["VFV", "XIC"], tfsa.Holdings.Select(h => h.Symbol));
    }

    [Fact]
    public void ReadsAPastedTableAndWorksOutAMissingMarketValue()
    {
        var pasted = "Symbol\tName\tQty\tLast Price\tMarket Value (CAD)\n" +
                     "TDB902\tTD US Index - e\t100.123\t25.00\t\n" +
                     "MSFT\tMicrosoft\t3\tUS$400.00\tUSD 1,200.00\n";

        var table = HoldingsReader.ReadText(pasted);
        var result = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers));

        Assert.Equal(2503.08m, result.Holdings[0].MarketValue);
        Assert.Equal("CAD", result.Holdings[0].Currency);
        Assert.Equal("USD", result.Holdings[1].Currency);
        Assert.Equal(1200m, result.Holdings[1].MarketValue);
    }

    [Fact]
    public void HoldingsWithoutAStatedCurrencyAreInTheAccountsCurrency()
    {
        var pasted = "Symbol\tName\tQty\tPrice\tMarket Value\tCurrency\n" +
                     "VOO\tVanguard S&P 500\t2\t600.00\t1200.00\t\n" +
                     "XEQT\tiShares Core Equity\t10\t34.00\t340.00\tCAD\n";

        var table = HoldingsReader.ReadText(pasted);
        var result = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers), defaultCurrency: "USD");

        Assert.Equal(["USD", "CAD"], result.Holdings.Select(h => h.Currency));
        Assert.Equal("CAD", HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers)).Holdings[0].Currency);
    }

    [Fact]
    public void ReadsTheBankOfCanadasLatestRate()
    {
        const string json = """
            {"seriesDetail":{"FXUSDCAD":{"label":"USD/CAD"}},
             "observations":[{"d":"2026-11-13","FXUSDCAD":{"v":"1.4251"}},{"d":"2026-11-16","FXUSDCAD":{"v":"1.4298"}}]}
            """;

        Assert.Equal(new ExchangeRate("USD", 1.4298m, new DateOnly(2026, 11, 16), ExchangeRateSource.BankOfCanada), BankOfCanadaRates.Parse(json, "usd"));
        Assert.Null(BankOfCanadaRates.Parse(json, "EUR"));
        Assert.Null(BankOfCanadaRates.Parse("{\"message\":\"Series not found\"}", "XYZ"));
        Assert.Null(BankOfCanadaRates.Parse("<html>", "USD"));
        Assert.Equal("https://www.bankofcanada.ca/valet/observations/FXUSDCAD/json?recent=1", BankOfCanadaRates.LatestUrl("usd").ToString());
    }

    [Fact]
    public void WithoutAHeaderTheColumnsArePickedByHand()
    {
        var table = HoldingsReader.ReadText("XEQT,100,3410.00\nVCN,50,2500.00\n");
        Assert.False(table.HasHeader);
        Assert.Equal(["Column 1", "Column 2", "Column 3"], table.Headers);
        Assert.Empty(HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers)).Holdings);

        var result = HoldingsReader.Parse(table, new HoldingColumns(Symbol: 0, Quantity: 1, MarketValue: 2));
        Assert.Equal([3410.00m, 2500.00m], result.Holdings.Select(h => h.MarketValue));
    }

    [Fact]
    public void ReadsAUtf16FileFromExcel()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Symbol\tMarket Value\nXBB\t1 234,50 $\n")).ToArray();
        var table = HoldingsReader.ReadFile(bytes);
        Assert.Equal(1234.50m, Assert.Single(HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers)).Holdings).MarketValue);
    }

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("US$", "USD")]
    [InlineData("cad", "CAD")]
    [InlineData("C$", "CAD")]
    [InlineData("", null)]
    [InlineData("Canadian Dollar", null)]
    public void ParsesCurrencyCodes(string text, string? expected) => Assert.Equal(expected, HoldingsReader.ParseCurrency(text));
}
