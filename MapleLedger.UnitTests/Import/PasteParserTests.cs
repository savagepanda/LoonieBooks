using MapleLedger.Core.Import;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests.Import;

public class PasteParserTests
{
    private static readonly PasteParseOptions Options = new() { ReferenceDate = new DateOnly(2026, 9, 30) };

    [Fact]
    public void TabSeparatedTableWithHeaderAndWithdrawalDepositColumns()
    {
        const string text = """
            Account activity
            Date	Description	Withdrawals	Deposits	Balance
            Sep 28, 2026	TIM HORTONS #1234	$4.50		$1,995.50
            Sep 27, 2026	PAYROLL DEP ACME CORP		$2,000.00	$2,000.00
            """;

        var result = PasteParser.Parse(text, Options);

        Assert.Equal(PasteLayout.TabSeparated, result.Layout);
        Assert.Equal([ColumnRole.Date, ColumnRole.Description, ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Balance], result.ColumnRoles);
        Assert.Collection(result.Transactions,
            t =>
            {
                Assert.Equal(new DateOnly(2026, 9, 28), t.Date);
                Assert.Equal("TIM HORTONS #1234", t.Description);
                Assert.Equal(4.50m, t.Amount);
                Assert.Equal(DebitCreditMemo.Debit, t.Direction);
                Assert.Equal(1995.50m, t.Balance);
            },
            t =>
            {
                Assert.Equal(2000m, t.Amount);
                Assert.Equal(DebitCreditMemo.Credit, t.Direction);
            });
        Assert.Equal(1995.50m, result.MostRecentWithBalance?.Balance);
    }

    [Fact]
    public void HeaderlessTableUsesBalancesToOrderDebitAndCreditColumns()
    {
        // Deposits before withdrawals: the opposite of the default assumption.
        const string text = """
            2026-09-01	OPENING DEPOSIT	1000.00		1000.00
            2026-09-02	GROCERIES		50.00	950.00
            2026-09-03	REFUND	10.00		960.00
            """;

        var result = PasteParser.Parse(text, Options);

        Assert.Equal([ColumnRole.Date, ColumnRole.Description, ColumnRole.Credit, ColumnRole.Debit, ColumnRole.Balance], result.ColumnRoles);
        Assert.Equal([DebitCreditMemo.Credit, DebitCreditMemo.Debit, DebitCreditMemo.Credit], result.Transactions.Select(t => t.Direction));
    }

    [Fact]
    public void HeaderlessCsvWithWholeDollarDepositsWithoutCents()
    {
        // TD's CSV export: no header, and whole-dollar amounts lose their ".00".
        const string text = """
            "2026-09-01","E-TRANSFER ***ab1",,"40","1040.00"
            "2026-09-02","HYDRO BPY","230.07",,"809.93"
            "2026-09-03","GC 1305-DEBIT MEMO","100",,"709.93"
            "2026-09-04","CAR LOAN RLS","64.92",,"645.01"
            "2026-09-15","PAYROLL ACME PAY",,"2126","2771.01"
            "2026-09-16","GAS BPY","33.24",,"2737.77"
            "2026-09-30","MONTHLY ACCOUNT FEE","30.95",,"2706.82"
            "2026-09-30","ACCT BAL REBATE",,"30.95","2737.77"
            """;

        var result = PasteParser.Parse(text, Options);

        Assert.Equal([ColumnRole.Date, ColumnRole.Description, ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Balance], result.ColumnRoles);
        Assert.Empty(result.Issues);
        Assert.Equal(8, result.Transactions.Count);
        Assert.Equal(2196.95m, result.TotalCredits);
        Assert.Equal(459.18m, result.TotalDebits);
    }

    [Fact]
    public void SingleSignedAmountColumn()
    {
        const string text = """
            Date	Description	Amount
            2026-09-02	LOBLAWS 1234	-82.13
            2026-09-03	E-TRANSFER FROM JANE	25.00
            """;

        var result = PasteParser.Parse(text, Options);

        Assert.Equal([DebitCreditMemo.Debit, DebitCreditMemo.Credit], result.Transactions.Select(t => t.Direction));
        Assert.Equal([82.13m, 25.00m], result.Transactions.Select(t => t.Amount));
    }

    [Fact]
    public void CreditCardStatementTreatsPositiveAmountsAsCharges()
    {
        const string text = """
            Transaction Date	Posted Date	Description	Amount
            Sep 26	Sep 28	AMAZON.CA	$64.99
            Sep 20	Sep 21	PAYMENT - THANK YOU	-$500.00
            """;

        var result = PasteParser.Parse(text, Options with { IsLiabilityAccount = true });

        Assert.Equal([ColumnRole.Date, ColumnRole.PostedDate, ColumnRole.Description, ColumnRole.Amount], result.ColumnRoles);
        var charge = result.Transactions[0];
        Assert.Equal(DebitCreditMemo.Debit, charge.Direction);
        Assert.Equal(new DateOnly(2026, 9, 26), charge.Date);
        Assert.Equal(new DateOnly(2026, 9, 28), charge.PostedDate);
        Assert.Equal(DebitCreditMemo.Credit, result.Transactions[1].Direction);
    }

    [Fact]
    public void CsvExportWithQuotedFieldsAndRbcStyleHeader()
    {
        const string text = """
            "Account Type","Account Number","Transaction Date","Cheque Number","Description 1","Description 2","CAD$","USD$"
            Chequing,00000-1234567,9/28/2026,,"Contactless Interac purchase - 1234","TIM HORTONS #10",-4.50,
            Chequing,00000-1234567,9/15/2026,,"Payroll Deposit","ACME, INC.",2000.00,
            """;

        var result = PasteParser.Parse(text, Options);

        Assert.Equal(PasteLayout.CommaSeparated, result.Layout);
        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("Contactless Interac purchase - 1234 TIM HORTONS #10", result.Transactions[0].Description);
        Assert.Equal(DebitCreditMemo.Debit, result.Transactions[0].Direction);
        Assert.Equal("Payroll Deposit ACME, INC.", result.Transactions[1].Description);
        Assert.Equal(new DateOnly(2026, 9, 15), result.Transactions[1].Date);
    }

    [Fact]
    public void OneCellPerLineInfersDirectionFromBalances()
    {
        // Copying some bank tables puts each cell on its own line and drops the empty ones.
        const string text = """
            Date
            Description
            Amount
            Balance
            Sep 3, 2026
            Refund - Canadian Tire
            $10.00
            $960.00
            Sep 2, 2026
            Loblaws
            $50.00
            $950.00
            Sep 1, 2026
            Payroll
            $1,000.00
            $1,000.00
            """;

        var result = PasteParser.Parse(text, Options);

        Assert.Equal(PasteLayout.OneCellPerLine, result.Layout);
        Assert.Equal(3, result.Transactions.Count);
        Assert.Equal("Refund - Canadian Tire", result.Transactions[0].Description);
        Assert.Equal(DebitCreditMemo.Credit, result.Transactions[0].Direction);
        Assert.Equal(DebitCreditMemo.Debit, result.Transactions[1].Direction);
        Assert.Equal(960m, result.MostRecentWithBalance?.Balance);
    }

    [Fact]
    public void FreeTextStatementLines()
    {
        const string text = """
            TRANS POSTING DESCRIPTION AMOUNT
            Sep 26 Sep 28 PETRO-CANADA 1234 TORONTO ON $58.20
            Sep 27 Sep 29 PAYMENT THANK YOU 500.00 CR
            Some footer text
            """;

        var result = PasteParser.Parse(text, Options with { IsLiabilityAccount = true });

        Assert.Equal(PasteLayout.FreeText, result.Layout);
        Assert.Collection(result.Transactions,
            t =>
            {
                Assert.Equal("PETRO-CANADA 1234 TORONTO ON", t.Description);
                Assert.Equal(58.20m, t.Amount);
                Assert.Equal(DebitCreditMemo.Debit, t.Direction);
                Assert.Equal(new DateOnly(2026, 9, 28), t.PostedDate);
            },
            t => Assert.Equal(DebitCreditMemo.Credit, t.Direction));
        Assert.Contains(result.Issues, i => i.Message.Contains("Some footer text"));
    }

    [Fact]
    public void FrenchCanadianTable()
    {
        const string text = """
            Date	Description	Retraits	Dépôts	Solde
            28 sept. 2026	PROVIGO #123	45,67 $		1 954,33 $
            """;

        var t = Assert.Single(PasteParser.Parse(text, Options).Transactions);

        Assert.Equal(new DateOnly(2026, 9, 28), t.Date);
        Assert.Equal(45.67m, t.Amount);
        Assert.Equal(DebitCreditMemo.Debit, t.Direction);
        Assert.Equal(1954.33m, t.Balance);
    }

    [Fact]
    public void RejoinsRowsSplitByLineBreaksInsideCells()
    {
        const string text = "Sep 28, 2026\tPurchase\nTIM HORTONS\t4.50\t995.50\nSep 27, 2026\tDeposit\t1000.00\t1000.00";

        var result = PasteParser.Parse(text, Options);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal("Purchase TIM HORTONS", result.Transactions[0].Description);
    }

    [Fact]
    public void ManualColumnRolesOverrideDetection()
    {
        const string text = "2026-09-02\t12.00\tLUNCH";
        var roles = new[] { ColumnRole.Date, ColumnRole.Amount, ColumnRole.Description };

        var t = Assert.Single(PasteParser.Parse(text, Options with { ColumnRoles = roles }).Transactions);

        Assert.Equal("LUNCH", t.Description);
        Assert.Equal(12m, t.Amount);
    }

    [Fact]
    public void ReportsRowsWithoutDatesInsteadOfFailing()
    {
        const string text = "2026-09-02\tLUNCH\t-12.00\nPending transactions\n2026-09-03\tDINNER\t-30.00";

        var result = PasteParser.Parse(text, Options);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Single(result.Issues);
    }

    [Fact]
    public void EmptyAndDatelessInputProduceNoTransactions()
    {
        Assert.Equal(PasteLayout.Empty, PasteParser.Parse("  \n ").Layout);
        Assert.Empty(PasteParser.Parse("hello\tworld", Options).Transactions);
    }
}
