using MapleLedger.Core.Import;
using MapleLedger.Fdx;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MapleLedger.UnitTests.Import;

public class PdfStatementReaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    [Fact]
    public void ChequingStatementWithDatesOncePerDayAndWrappedDescriptions()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 750, "Your account statement");
        pdf.Text(40, 735, "From December 12, 2025 to January 11, 2026");
        pdf.Text(40, 720, "Account number: ****1234");

        // Column headings sit over right-aligned amounts.
        pdf.Text(40, 690, "Date");
        pdf.Text(100, 690, "Description");
        pdf.Right(380, 690, "Withdrawals ($)");
        pdf.Right(460, 690, "Deposits ($)");
        pdf.Right(540, 690, "Balance ($)");

        pdf.Text(40, 675, "12 Dec");
        pdf.Text(100, 675, "Opening balance");
        pdf.Right(540, 675, "1,000.00");

        pdf.Text(40, 660, "28 Dec");
        pdf.Text(100, 660, "Interac purchase");
        pdf.Right(380, 660, "45.10");
        pdf.Text(100, 648, "LOBLAWS #1234");                 // wraps under the description
        pdf.Text(100, 636, "Payroll deposit ACME CORP");     // same day, no date printed
        pdf.Right(460, 636, "2,000.00");
        pdf.Right(540, 636, "2,954.90");

        pdf.Text(40, 621, "3 Jan");
        pdf.Text(100, 621, "e-Transfer sent J SMITH");
        pdf.Right(380, 621, "100.00");
        pdf.Right(540, 621, "2,854.90");

        pdf.Text(100, 600, "Total withdrawals");
        pdf.Right(380, 600, "145.10");
        pdf.Text(40, 60, "Page 1 of 1");

        var result = PdfStatementReader.Read([pdf.Build("statement.pdf")], Today);

        Assert.Equal(PdfTextLayout.Table, result.Layout);
        Assert.Equal(3, result.RowCount); // the opening balance isn't a transaction
        Assert.DoesNotContain("1234", result.Text.Split('\n')[0]); // the account number line is dropped

        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today });
        Assert.Equal([ColumnRole.Date, ColumnRole.Description, ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Balance], parsed.ColumnRoles);
        Assert.Collection(parsed.Transactions,
            t =>
            {
                // The year comes from the statement period, not from today.
                Assert.Equal(new DateOnly(2025, 12, 28), t.Date);
                Assert.Equal("Interac purchase LOBLAWS #1234", t.Description);
                Assert.Equal(45.10m, t.Amount);
                Assert.Equal(DebitCreditMemo.Debit, t.Direction);
            },
            t =>
            {
                Assert.Equal(new DateOnly(2025, 12, 28), t.Date);
                Assert.Equal("Payroll deposit ACME CORP", t.Description);
                Assert.Equal(2000m, t.Amount);
                Assert.Equal(DebitCreditMemo.Credit, t.Direction);
                Assert.Equal(2954.90m, t.Balance);
            },
            t =>
            {
                Assert.Equal(new DateOnly(2026, 1, 3), t.Date);
                Assert.Equal(100m, t.Amount);
                Assert.Equal(DebitCreditMemo.Debit, t.Direction);
            });
    }

    [Fact]
    public void AmountInADescriptionIsNotAColumnAndSummaryRowsAreSkipped()
    {
        // BMO's layout: no column headings over the amounts, a fee whose description ends with the
        // amount it was charged on, one deposit all month, and dated opening and closing rows.
        var pdf = new StatementPdf();
        pdf.Text(40, 750, "For the period ending April 7, 2026");
        pdf.Text(40, 690, "Mar 7");
        pdf.Text(100, 690, "Opening balance");
        pdf.Right(540, 690, "2,450.00");
        pdf.Text(40, 675, "Mar 9");
        pdf.Text(100, 675, "INTERAC e-Transfer Sent");
        pdf.Right(380, 675, "500.00");
        pdf.Right(540, 675, "1,950.00");
        pdf.Text(40, 660, "Mar 13");
        pdf.Text(100, 660, "Direct Deposit, B/M PAY-PAIE PAY/PAY");
        pdf.Right(460, 660, "3,120.55");
        pdf.Right(540, 660, "5,070.55");
        pdf.Text(40, 645, "Mar 17");
        pdf.Text(100, 645, "Goods and Services Tax, HST.13 X $70.00");
        pdf.Right(380, 645, "9.10");
        pdf.Right(540, 645, "5,061.45");
        pdf.Text(40, 630, "Mar 18");
        pdf.Text(100, 630, "Online Bill Payment, KINGSTON TAX");
        pdf.Right(380, 630, "1,800.00");
        pdf.Right(540, 630, "3,261.45");
        pdf.Text(40, 615, "Apr 7");
        pdf.Text(100, 615, "Closing totals");
        pdf.Right(380, 615, "2,309.10");
        pdf.Right(460, 615, "3,120.55");

        var result = PdfStatementReader.Read([pdf.Build("statement.pdf")], Today);
        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today });

        Assert.Equal([ColumnRole.Date, ColumnRole.Description, ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Balance], parsed.ColumnRoles);
        Assert.Empty(parsed.Issues);
        Assert.Equal(
            [("INTERAC e-Transfer Sent", 500m, DebitCreditMemo.Debit), ("Direct Deposit, B/M PAY-PAIE PAY/PAY", 3120.55m, DebitCreditMemo.Credit),
             ("Goods and Services Tax, HST.13 X $70.00", 9.10m, DebitCreditMemo.Debit), ("Online Bill Payment, KINGSTON TAX", 1800m, DebitCreditMemo.Debit)],
            parsed.Transactions.Select(t => (t.Description, t.Amount, t.Direction)));
        Assert.All(parsed.Transactions, t => Assert.Equal(2026, t.Date.Year));
    }

    [Fact]
    public void CardStatementWithPostingDatesAndForeignCurrencyNotes()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 750, "Statement date: Oct 5, 2026");
        pdf.Text(40, 700, "TRANS DATE");
        pdf.Text(110, 700, "POSTING DATE");
        pdf.Text(190, 700, "DESCRIPTION");
        pdf.Right(540, 700, "AMOUNT ($)");

        pdf.Text(40, 685, "SEP 12");
        pdf.Text(110, 685, "SEP 14");
        pdf.Text(190, 685, "AMAZON.COM AMZN.COM/BILL WA");
        pdf.Right(540, 685, "$41.40");
        pdf.Text(190, 673, "USD 30.00 @ 1.380000");          // an amount, but not in the amount column

        pdf.Text(40, 658, "SEP 20");
        pdf.Text(110, 658, "SEP 21");
        pdf.Text(190, 658, "PAYMENT - THANK YOU");
        pdf.Right(540, 658, "-$500.00");

        pdf.Text(40, 643, "OCT 1");
        pdf.Text(110, 643, "OCT 2");
        pdf.Text(190, 643, "TIM HORTONS #4410");
        pdf.Right(540, 643, "$4.50");

        var result = PdfStatementReader.Read([pdf.Build("visa.pdf")], Today);
        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today, IsLiabilityAccount = true });

        Assert.Equal([ColumnRole.Date, ColumnRole.PostedDate, ColumnRole.Description, ColumnRole.Amount], parsed.ColumnRoles);
        Assert.Collection(parsed.Transactions,
            t =>
            {
                Assert.Equal(new DateOnly(2026, 9, 12), t.Date);
                Assert.Equal(new DateOnly(2026, 9, 14), t.PostedDate);
                Assert.Equal("AMAZON.COM AMZN.COM/BILL WA USD 30.00 @ 1.380000", t.Description);
                Assert.Equal(41.40m, t.Amount);
                Assert.Equal(DebitCreditMemo.Debit, t.Direction);
            },
            t =>
            {
                Assert.Equal(500m, t.Amount);
                Assert.Equal(DebitCreditMemo.Credit, t.Direction);
            },
            t => Assert.Equal(4.50m, t.Amount));
    }

    [Fact]
    public void CreditMarkersStayInTheAmountColumnAndSidebarSentencesAreNotHeadings()
    {
        // BMO's card statement: credits print "CR" past the amount column's right edge, and a
        // sidebar sentence ending in "balance" sits on the column headings' line over the amounts.
        var pdf = new StatementPdf();
        pdf.Text(40, 750, "Statement date: April 2, 2026");
        pdf.Text(40, 700, "TRANS DATE");
        pdf.Text(110, 700, "POSTING DATE");
        pdf.Text(190, 700, "DESCRIPTION");
        pdf.Text(330, 700, "You can view your current BMO Rewards point balance");

        void Row(double y, string date, string posted, string description, string amount, bool credit = false)
        {
            pdf.Text(40, y, date);
            pdf.Text(110, y, posted);
            pdf.Text(190, y, description);
            pdf.Right(460, y, amount);
            if (credit) pdf.Text(463, y, "CR");
        }

        Row(685, "Mar. 27", "Mar. 30", "MAPLE DINER #2040 KINGSTON ON", "18.40");
        Row(670, "Mar. 29", "Mar. 30", "HARBOUR NOODLE OTTAWA ON", "52.30");
        Row(655, "Mar. 30", "Mar. 31", "Cash Back / Remises Rebate MO", "11.00", credit: true);
        Row(640, "Mar. 30", "Mar. 31", "CITY PARKING OTTAWA ON", "11.00");
        Row(625, "Mar. 30", "Mar. 31", "TRSF FROM/DE ACCT/CPT 1234-XXXX-567", "1,200.00", credit: true);

        var result = PdfStatementReader.Read([pdf.Build("bmo.pdf")], Today);
        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today, IsLiabilityAccount = true });

        Assert.Equal([ColumnRole.Date, ColumnRole.PostedDate, ColumnRole.Description, ColumnRole.Amount], parsed.ColumnRoles);
        Assert.Empty(parsed.Issues);
        Assert.Equal(
            [(18.40m, DebitCreditMemo.Debit), (52.30m, DebitCreditMemo.Debit), (11.00m, DebitCreditMemo.Credit), (11.00m, DebitCreditMemo.Debit), (1200m, DebitCreditMemo.Credit)],
            parsed.Transactions.Select(t => (t.Amount, t.Direction)));
    }

    [Fact]
    public void SentencesAreNotColumnHeadings()
    {
        // A pasted table whose header row picked up a note beside the table.
        var text = "Date\tPosted\tDescription\tYou can view your current BMO Rewards point balance\n"
                   + "2026-03-27\t2026-03-30\tMAPLE DINER #2040 KINGSTON ON\t18.40\n"
                   + "2026-03-30\t2026-03-31\tCash Back / Remises Rebate MO\t11.00 CR\n";

        var parsed = PasteParser.Parse(text, new PasteParseOptions { ReferenceDate = Today, IsLiabilityAccount = true });

        Assert.Equal([ColumnRole.Date, ColumnRole.PostedDate, ColumnRole.Description, ColumnRole.Amount], parsed.ColumnRoles);
        Assert.Equal(2, parsed.Transactions.Count);
    }

    [Fact]
    public void YearIsInferredPerStatementSoLastYearStaysLastYear()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 750, "Statement period: Feb 1, 2025 to Feb 28, 2025");
        pdf.Text(40, 700, "Feb 3");
        pdf.Text(100, 700, "GROCERIES");
        pdf.Right(540, 700, "12.00");

        var result = PdfStatementReader.Read([pdf.Build("old.pdf")], Today);
        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today });

        Assert.Equal(new DateOnly(2025, 2, 3), Assert.Single(parsed.Transactions).Date);
    }

    [Fact]
    public void SeveralStatementsAreReadTogether()
    {
        StatementPdf Month(string date, string description, string amount)
        {
            var pdf = new StatementPdf();
            pdf.Text(40, 750, "Statement period ending Oct 31, 2026");
            pdf.Text(40, 700, date);
            pdf.Text(100, 700, description);
            pdf.Right(540, 700, amount);
            pdf.Text(100, 685, "Total");                         // no carried-over date across files
            pdf.Right(540, 685, amount);
            return pdf;
        }

        var result = PdfStatementReader.Read(
            [Month("Sep 3", "RENT", "1,500.00").Build("sep.pdf"), Month("Oct 3", "RENT", "1,550.00").Build("oct.pdf")], Today);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, result.RowCount);
        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today });
        Assert.Equal([1500m, 1550m], parsed.Transactions.Select(t => t.Amount));
    }

    [Fact]
    public void PdfWithoutATableFallsBackToItsLines()
    {
        var pdf = new StatementPdf();
        pdf.Text(40, 700, "Thank you for banking with us.");
        pdf.Text(40, 685, "No transactions this period.");

        var result = PdfStatementReader.Read([pdf.Build("letter.pdf")], Today);

        Assert.Equal(PdfTextLayout.Lines, result.Layout);
        Assert.Equal("Thank you for banking with us.\nNo transactions this period.", result.Text);
    }

    [Fact]
    public void CardStatementWithSidePanelAndShiftedSecondPage()
    {
        // TD's layout: transactions on the left, a payment-information box on the right whose lines
        // fall a point or so off the transactions' baselines, and later pages printed 47pt further left.
        var pdf = new StatementPdf();
        pdf.Text(94, 859, "STATEMENT DATE: March 04, 2026");
        pdf.Text(96, 805, "DATE");
        pdf.Text(144, 805, "DATE");
        pdf.Text(187, 805, "ACTIVITY DESCRIPTION");
        pdf.Right(392, 805, "AMOUNT($)");
        pdf.Text(408, 808, "Previous Balance");
        pdf.Right(624, 808, "96.25");

        void Row(double shift, double y, string date, string posted, string description, string amount)
        {
            pdf.Text(94 - shift, y, date);
            pdf.Text(142 - shift, y, posted);
            pdf.Text(187 - shift, y, description);
            pdf.Right(391 - shift, y, amount);
        }

        void Panel(double y, string label, string? value = null)
        {
            pdf.Text(408, y, label);
            if (value is not null) pdf.Right(624, y, value);
        }

        Row(0, 774, "FEB 4", "FEB 5", "FRESHMART KINGSTON", "$12.35");
        Panel(772.5, "Bonus, Accelerators &", "4.10");
        Row(0, 755, "FEB 4", "FEB 6", "LUCKY DRAGON CAFE HALIFAX", "$18.75");
        Panel(748, "Total Cash Back Dollars", "21.80");
        Row(0, 717, "FEB 5", "FEB 6", "NORTH STAR SUSHI REGINA", "$16.20");
        Panel(713, "Minimum Payment", "$10.00");
        Row(0, 698, "FEB 5", "FEB 9", "SHELL EASYPAY KINGSTON", "$45.00");
        Panel(688, "Credit Limit", "$8,000");
        Row(0, 641, "FEB 7", "FEB 10", "WAL-MART SUPERCENTER#1234", "$23.10");
        Panel(641, "Estimated Time to Pay");
        pdf.Text(187, 631, "KINGSTON");
        Panel(631, "if you pay only the Minimum Payment each month is:");
        Row(0, 583, "FEB 11", "FEB 11", "REWARDS REDEMPTION", "-$50.00");

        pdf.NewPage();
        pdf.Text(49, 616, "DATE");
        pdf.Text(97, 616, "DATE");
        pdf.Text(140, 616, "ACTIVITY DESCRIPTION");
        pdf.Right(345, 616, "AMOUNT($)");
        Row(47, 602, "FEB 14", "FEB 17", "NORTHWIND MOBILE WINNIPEG", "$42.00");
        Row(47, 583, "FEB 25", "FEB 25", "PREAUTHORIZED PAYMENT", "-$1,236.48");
        Row(47, 564, "FEB 26", "FEB 27", "CEDAR PITA HALIFAX", "$9.85");

        var result = PdfStatementReader.Read([pdf.Build("visa.pdf")], Today);
        Assert.Equal(PdfTextLayout.Table, result.Layout);
        Assert.DoesNotContain("Accelerators", result.Text);
        Assert.DoesNotContain("Estimated", result.Text);

        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today, IsLiabilityAccount = true });
        Assert.Empty(parsed.Issues);
        Assert.Equal([ColumnRole.Date, ColumnRole.PostedDate, ColumnRole.Description, ColumnRole.Amount], parsed.ColumnRoles);
        Assert.Equal(9, parsed.Transactions.Count);
        Assert.Equal(167.25m, parsed.TotalDebits);
        Assert.Equal(1286.48m, parsed.TotalCredits);
        Assert.Contains(parsed.Transactions, t => t.Description == "WAL-MART SUPERCENTER#1234 KINGSTON" && t.Amount == 23.10m);
    }

    [Fact]
    public void ScannedPdfHasNoText()
    {
        var result = PdfStatementReader.Read([new StatementPdf().Build("scan.pdf")], Today);

        Assert.Equal(PdfTextLayout.NoText, result.Layout);
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public void FileThatIsNotAPdfIsReportedByName()
    {
        var ex = Assert.Throws<PdfStatementException>(() => PdfStatementReader.Read([new StatementFile("photo.pdf", "not a pdf"u8.ToArray())], Today));

        Assert.Contains("photo.pdf", ex.Message);
    }

    /// <summary>Lays out text on a letter-size page, like a bank's statement generator.</summary>
    [Fact]
    public void ScreenshotsReadByOcrInAnyOrderWithUnevenBaselines()
    {
        // Two screenshots of a card's transaction list, as OCR returns them: pixel positions (y down,
        // so negated), words in block order rather than reading order, and baselines a pixel or two off.
        var first = new OcrPage();
        first.Text(40, 60, "Recent transactions");
        first.Text(40, 120, "Date");
        first.Text(200, 120, "Description");
        first.Right(900, 120, "Amount");
        first.Text(40, 160, "Nov 3");
        first.Text(200, 162, "TIM HORTONS #1234 TORONTO");
        first.Right(900, 159, "$4.56");
        first.Text(40, 200, "Nov 5");
        first.Text(200, 199, "PAYMENT - THANK YOU");
        first.Right(900, 202, "-$500.00");

        var second = new OcrPage();
        second.Text(40, 80, "Nov 9");
        second.Text(200, 81, "AMZN Mktp CA");
        second.Right(900, 78, "$32.10");

        var result = PdfStatementReader.ReadWords([first.Build("screen-1.png"), second.Build("screen-2.png")], Today);

        Assert.Equal(PdfTextLayout.Table, result.Layout);
        Assert.Equal(3, result.RowCount);

        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today, IsLiabilityAccount = true });
        Assert.Collection(parsed.Transactions,
            t =>
            {
                Assert.Equal(new DateOnly(2026, 11, 3), t.Date);
                Assert.Equal("TIM HORTONS #1234 TORONTO", t.Description);
                Assert.Equal(4.56m, t.Amount);
                Assert.Equal(DebitCreditMemo.Debit, t.Direction);
            },
            t =>
            {
                Assert.Equal("PAYMENT - THANK YOU", t.Description);
                Assert.Equal(500m, t.Amount);
                Assert.Equal(DebitCreditMemo.Credit, t.Direction);
            },
            t =>
            {
                Assert.Equal(new DateOnly(2026, 11, 9), t.Date);
                Assert.Equal(32.10m, t.Amount);
            });
    }

    [Fact]
    public void PicturesAtDifferentScalesLineUpTheirColumns()
    {
        // A screenshot, then a closer photo of the next page that has no deposits on it: its columns
        // sit elsewhere in pixels, and there are only two of them.
        var screen = new OcrPage();
        screen.Text(20, 70, "Date");
        screen.Text(140, 70, "Description");
        screen.Right(640, 70, "Withdrawals");
        screen.Right(760, 70, "Deposits");
        screen.Right(880, 70, "Balance");
        screen.Text(20, 105, "Oct 28");
        screen.Text(140, 105, "LOBLAWS #1234");
        screen.Right(640, 105, "45.10");
        screen.Right(880, 105, "2,954.90");
        screen.Text(20, 137, "Oct 30");
        screen.Text(140, 137, "Payroll deposit");
        screen.Right(760, 137, "2,000.00");
        screen.Right(880, 137, "4,954.90");

        var photo = new OcrPage(scale: 1.7);
        photo.Text(150, 200, "Nov 8");
        photo.Text(330, 200, "COSTCO WHOLESALE #552");
        photo.Right(1090, 200, "212.37");
        photo.Right(1500, 200, "4,742.53");
        photo.Text(150, 260, "Nov 12");
        photo.Text(330, 260, "HYDRO ONE");
        photo.Right(1090, 260, "88.20");
        photo.Right(1500, 260, "4,654.33");

        var result = PdfStatementReader.ReadWords([screen.Build("screen.png"), photo.Build("photo.jpg")], Today);

        Assert.Equal(PdfTextLayout.Table, result.Layout);
        var parsed = PasteParser.Parse(result.Text, new PasteParseOptions { ReferenceDate = Today });
        Assert.Equal([ColumnRole.Date, ColumnRole.Description, ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Balance], parsed.ColumnRoles);
        Assert.Collection(parsed.Transactions,
            t => Assert.Equal((45.10m, DebitCreditMemo.Debit, 2954.90m), (t.Amount, t.Direction, t.Balance!.Value)),
            t => Assert.Equal((2000m, DebitCreditMemo.Credit, 4954.90m), (t.Amount, t.Direction, t.Balance!.Value)),
            t => Assert.Equal((212.37m, DebitCreditMemo.Debit, 4742.53m), (t.Amount, t.Direction, t.Balance!.Value)),
            t => Assert.Equal((88.20m, DebitCreditMemo.Debit, 4654.33m), (t.Amount, t.Direction, t.Balance!.Value)));
    }

    [Fact]
    public void OcrWordsWithoutATableComeBackAsLines()
    {
        var page = new OcrPage();
        page.Text(40, 60, "Nothing to see here");
        var result = PdfStatementReader.ReadWords([page.Build("blank.png")], Today);
        Assert.Equal(PdfTextLayout.Lines, result.Layout);
        Assert.Equal("Nothing to see here", result.Text);

        Assert.Equal(PdfTextLayout.NoText, PdfStatementReader.ReadWords([new OcrPage().Build("empty.png")], Today).Layout);
    }

    /// <summary>Words placed the way OCR reports them: pixels, y growing downward, 20 px lines.</summary>
    private sealed class OcrPage(double scale = 1)
    {
        private readonly double LineHeight = 20 * scale, CharWidth = 9 * scale, Space = 6 * scale;
        private readonly List<StatementWord> _words = [];

        public void Text(double left, double y, string text)
        {
            foreach (var word in text.Split(' '))
            {
                var right = left + word.Length * CharWidth;
                _words.Add(new StatementWord(word, left, right, -y, LineHeight));
                left = right + Space;
            }
        }

        public void Right(double right, double y, string text) =>
            Text(right - text.Length * CharWidth - (text.Count(c => c == ' ') * (Space - CharWidth)), y, text);

        // Block order, not reading order: the amounts column comes back first.
        public StatementWords Build(string name) =>
            new(name, [_words.OrderByDescending(w => w.Left).ToList()]);
    }

    internal sealed class StatementPdf
    {
        private const double Size = 9;
        private readonly PdfDocumentBuilder _builder = new();
        private readonly PdfDocumentBuilder.AddedFont _font;
        private PdfPageBuilder _page;

        public StatementPdf()
        {
            _font = _builder.AddStandard14Font(Standard14Font.Helvetica);
            _page = _builder.AddPage(PageSize.Letter);
        }

        public void NewPage() => _page = _builder.AddPage(PageSize.Letter);

        public void Text(double x, double y, string text) => _page.AddText(text, Size, new PdfPoint(x, y), _font);

        /// <summary>Right-aligns text so it ends at <paramref name="right"/>, as statements print amounts.</summary>
        public void Right(double right, double y, string text)
        {
            var letters = _page.MeasureText(text, Size, new PdfPoint(0, y), _font);
            var width = letters[^1].EndBaseLine.X;
            Text(right - width, y, text);
        }

        public StatementFile Build(string name) => new(name, _builder.Build());
    }
}
