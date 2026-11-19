using MapleLedger.Core.Import;
using MapleLedger.Core.Investments;
using StatementPdf = MapleLedger.UnitTests.Import.PdfStatementReaderTests.StatementPdf;

namespace MapleLedger.UnitTests.Import;

public class HoldingsPdfReaderTests
{
    private static readonly DateOnly Today = new(2026, 12, 5);

    [Fact]
    public void ReadsAMonthlyStatementsHoldingsSectionsAndStopsAtTheActivity()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 740, "Self-directed RRSP account");
        pdf.Text(40, 725, "Statement for the period ending November 30, 2026");
        pdf.Text(40, 700, "Your holdings");
        pdf.Text(40, 685, "Canadian dollar holdings");

        // A two-line header: "Market" above "Value".
        Header(pdf, 665);
        Row(pdf, 640, "XEQT", "ISHARES CORE EQUITY ETF", "1,250", "34.10", "42,625.00", "38,000.00");
        Row(pdf, 628, "ZAG", "BMO AGGREGATE BOND", "400.5", "13.95", "5,586.98", "5,800.00");
        pdf.Text(100, 618, "INDEX ETF"); // the description wraps
        pdf.Text(100, 604, "Cash");
        pdf.Right(480, 604, "1,204.33");
        pdf.Text(40, 590, "Total");
        pdf.Right(480, 590, "49,416.31");

        pdf.Text(40, 565, "U.S. dollar holdings");
        Header(pdf, 545);
        Row(pdf, 520, "AAPL", "APPLE INC", "10", "230.00", "2,300.00", "1,500.00");

        pdf.Text(40, 490, "Account activity");
        pdf.Text(40, 475, "Date");
        pdf.Text(100, 475, "Activity");
        pdf.Text(200, 475, "Description");
        pdf.Right(480, 475, "Amount");
        pdf.Text(40, 460, "Nov 3, 2026");
        pdf.Text(100, 460, "Dividend");
        pdf.Text(200, 460, "XEQT");
        pdf.Right(480, 460, "45.00");

        var result = HoldingsPdfReader.Read(pdf.Build("statement.pdf"), Today);

        Assert.True(result.HasText);
        Assert.Equal(2, result.TableCount);
        var table = Assert.IsType<HoldingsTable>(result.Table);
        Assert.Equal(new DateOnly(2026, 11, 30), table.AsOf);
        Assert.Equal(["Symbol", "Description", "Quantity", "Price", "Market Value", "Book Value", "Currency"], table.Headers);

        var parsed = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers));
        Assert.Collection(parsed.Holdings,
            h => Assert.Equal(new Holding("XEQT", "ISHARES CORE EQUITY ETF", 1250m, 34.10m, 42625.00m, 38000.00m), h),
            h => Assert.Equal(new Holding("ZAG", "BMO AGGREGATE BOND INDEX ETF", 400.5m, 13.95m, 5586.98m, 5800.00m), h),
            h => Assert.Equal(new Holding(Holding.CashSymbol, "Cash", null, null, 1204.33m, null), h),
            h => Assert.Equal(new Holding("AAPL", "APPLE INC", 10m, 230.00m, 2300.00m, 1500.00m, "USD"), h));
        Assert.Contains(parsed.Skipped, s => s.StartsWith("Total", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsANamePrintedAboveItsNumbersUnderAOneLineHeader()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 740, "Holdings as of October 31, 2026");
        pdf.Text(40, 710, "Security");
        pdf.Right(330, 710, "Units");
        pdf.Right(400, 710, "Price");
        pdf.Right(480, 710, "Market value");
        pdf.Text(40, 695, "VANGUARD BALANCED ETF PORTFOLIO (VBAL)");
        pdf.Right(330, 685, "812.0000");
        pdf.Right(400, 685, "31.42");
        pdf.Right(480, 685, "25,513.04");
        pdf.Text(40, 670, "MANULIFE DIVIDEND INCOME FUND");
        pdf.Right(330, 660, "1 204.512");
        pdf.Right(400, 660, "—");
        pdf.Right(480, 660, "18 330,77");

        var result = HoldingsPdfReader.Read(pdf.Build("tfsa.pdf"), Today);

        var table = Assert.IsType<HoldingsTable>(result.Table);
        Assert.Equal(new DateOnly(2026, 10, 31), table.AsOf);
        var parsed = HoldingsReader.Parse(table, HoldingsReader.Guess(table.Headers));
        Assert.Collection(parsed.Holdings,
            // No symbol column: the ticker in brackets is the symbol.
            h => Assert.Equal(new Holding("VBAL", "VANGUARD BALANCED ETF PORTFOLIO (VBAL)", 812m, 31.42m, 25513.04m, null), h),
            h => Assert.Equal(new Holding(null, "MANULIFE DIVIDEND INCOME FUND", 1204.512m, null, 18330.77m, null), h));
    }

    [Fact]
    public void AStatementWithoutAHoldingsTableReadsAsNone()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 740, "Chequing statement for the period ending November 30, 2026");
        pdf.Text(40, 710, "Date");
        pdf.Text(100, 710, "Description");
        pdf.Right(480, 710, "Amount");
        pdf.Text(40, 695, "Nov 3, 2026");
        pdf.Text(100, 695, "Payroll");
        pdf.Right(480, 695, "2,000.00");

        var result = HoldingsPdfReader.Read(pdf.Build("chequing.pdf"), Today);

        Assert.True(result.HasText);
        Assert.Null(result.Table);
        Assert.False(HoldingsPdfReader.ReadPages([[]], Today).HasText);
    }

    private static void Header(StatementPdf pdf, double y)
    {
        pdf.Right(480, y, "Market");
        pdf.Right(560, y, "Book");
        pdf.Text(40, y - 10, "Symbol");
        pdf.Text(100, y - 10, "Description");
        pdf.Right(330, y - 10, "Quantity");
        pdf.Right(400, y - 10, "Price");
        pdf.Right(480, y - 10, "Value");
        pdf.Right(560, y - 10, "Value");
    }

    private static void Row(StatementPdf pdf, double y, string symbol, string description, string quantity, string price, string market, string book)
    {
        pdf.Text(40, y, symbol);
        pdf.Text(100, y, description);
        pdf.Right(330, y, quantity);
        pdf.Right(400, y, price);
        pdf.Right(480, y, market);
        pdf.Right(560, y, book);
    }
}
