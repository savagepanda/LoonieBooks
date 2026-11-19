using System.Text;
using MapleLedger.Core.Import;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests.Import;

public class CsvStatementReaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static StatementFile Csv(string name, string text, Encoding? encoding = null) =>
        new(name, (encoding ?? Encoding.UTF8).GetPreamble().Concat((encoding ?? Encoding.UTF8).GetBytes(text)).ToArray());

    [Fact]
    public void OneExportIsReadLikePastedCsv()
    {
        var result = CsvStatementReader.Read([Csv("october.csv", """
            Date,Description,Withdrawals,Deposits,Balance
            2026-10-01,LOBLAWS #1234,45.10,,954.90
            2026-10-15,PAYROLL ACME CORP,,"2,000.00","2,954.90"
            """)], Today);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(2, result.RowCount);

        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today });
        Assert.Equal(PasteLayout.CommaSeparated, parsed.Layout);
        Assert.NotNull(parsed.Header);
        Assert.Collection(parsed.Transactions,
            t => Assert.Equal((45.10m, DebitCreditMemo.Debit), (t.Amount, t.Direction)),
            t => Assert.Equal((2000m, DebitCreditMemo.Credit), (t.Amount, t.Direction)));
    }

    [Fact]
    public void LaterFilesLoseTheirTitleAndHeader()
    {
        var result = CsvStatementReader.Read(
        [
            Csv("09.csv", "Account ****1234\nDate,Description,Amount\n2026-09-03,COFFEE,-4.50\n"),
            Csv("10.csv", "Account ****1234\nDate,Description,Amount\n2026-10-03,TEA,-3.25\n"),
        ], Today);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(2, result.RowCount);
        Assert.Equal("Account ****1234\nDate,Description,Amount\n2026-09-03,COFFEE,-4.50\n2026-10-03,TEA,-3.25\n", result.Text);
        Assert.Equal(2, PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today }).Transactions.Count);
    }

    [Fact]
    public void AccentsSurviveAnyCommonEncoding()
    {
        const string text = "Date,Description,Montant\n2026-10-03,Café Dépôt,-4.50\n";
        foreach (var encoding in new[] { Encoding.UTF8, new UTF8Encoding(false), Encoding.Unicode, Encoding.Latin1 })
        {
            Assert.Contains("Café Dépôt", CsvStatementReader.Read([Csv("fr.csv", text, encoding)], Today).Text);
        }
    }

    [Fact]
    public void FileWithoutDatesIsRejected()
    {
        var ex = Assert.Throws<CsvStatementException>(() => CsvStatementReader.Read([Csv("contacts.csv", "Name,Email\nJo,jo@example.com\n")], Today));
        Assert.Contains("contacts.csv", ex.Message);
    }
}
