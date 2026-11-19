using MapleLedger.Core;
using MapleLedger.Core.Receipts;
using MapleLedger.Fdx;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MapleLedger.UnitTests;

public class ReceiptTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);

    [Fact]
    public void ReadsTotalDateAndMerchantFromATypicalReceipt()
    {
        const string text = """
            GOODLIFE FITNESS
            123 KING ST W, TORONTO ON
            (416) 555-0199
            DATE: 2026-09-10 18:42
            MONTHLY MEMBERSHIP      53.10
            SUBTOTAL                53.10
            HST 13%                  6.90
            TOTAL                   60.00
            VISA ****1234           60.00
            THANK YOU!
            """;

        Assert.Equal(new ReceiptFields(60.00m, new DateOnly(2026, 9, 10), "GOODLIFE FITNESS"), ReceiptText.Parse(text, Today));
    }

    [Fact]
    public void ReadsAFrenchReceiptAndIgnoresCashAndChange()
    {
        const string text = """
            BIENVENUE
            PHARMAPRIX
            1500 RUE PEEL, MONTRÉAL
            10 févr. 2026
            SOUS-TOTAL         21,50 $
            TPS                 1,08 $
            TVQ                 2,14 $
            MONTANT TOTAL      24,72 $
            COMPTANT           40,00 $
            MONNAIE            15,28 $
            """;

        Assert.Equal(new ReceiptFields(24.72m, new DateOnly(2026, 2, 10), "PHARMAPRIX"), ReceiptText.Parse(text, Today));
    }

    [Fact]
    public void SurvivesOcrNoiseAndCardExpiryDates()
    {
        const string text = """
            ~~ ..
            Shoppers Drug Mart #0123
            03/14/26  10:15
            CARD EXP 05/28
            T0TAL   $1,234.56
            """;

        Assert.Equal(new ReceiptFields(1234.56m, new DateOnly(2026, 3, 14), "Shoppers Drug Mart #0123"), ReceiptText.Parse(text, Today));
    }

    [Fact]
    public void AmbiguousNumericDatesPreferTheMostRecentPastDate()
    {
        // 04/11/2026 is April 11 or November 4; both are in the past, and November 4 is more recent.
        Assert.Equal(new DateOnly(2026, 11, 4), ReceiptText.Parse("STORE\n04/11/2026\nTOTAL 5.00", Today).Date);
        // 12/11/2026 could be December 11 (the future) or November 12.
        Assert.Equal(new DateOnly(2026, 11, 12), ReceiptText.Parse("STORE\n12/11/2026\nTOTAL 5.00", Today).Date);
    }

    [Fact]
    public void FallsBackToThePaymentLineThenTheLargestAmount()
    {
        Assert.Equal(18.40m, ReceiptText.Parse("CAFE\nLATTE 5.20\nMUFFIN 3.10\nINTERAC 18.40", Today).Total);
        Assert.Equal(8.30m, ReceiptText.Parse("CAFE\nLATTE 5.20\nMUFFIN 3.10\n8.30", Today).Total);
        Assert.Equal(new ReceiptFields(null, null, null), ReceiptText.Parse("", Today));
    }

    [Fact]
    public void ReadsLargeTotalsWithoutThousandsSeparatorsButNotDottedDates()
    {
        // Regression: "1500.00" was skipped and the largest smaller amount taken instead.
        const string text = "SHOPPERS DRUG MART\n2026.02.10\nADVIL 24CT 11.99\nVITAMIN D 12.45\nTOTAL 1500.00";

        var fields = ReceiptText.Parse(text, Today);

        Assert.Equal(1500.00m, fields.Total);
        Assert.Equal([11.99m, 12.45m], ReceiptText.Amounts("2026.02.10 ADVIL 11.99 VITAMIN 12.45"));
    }

    [Fact]
    public void AmbiguousDatesKeepTheirOtherReading()
    {
        var fields = ReceiptText.Parse("STORE\n02/10/2026 14:05\nTOTAL 5.00", Today);

        Assert.Equal((new DateOnly(2026, 10, 2), new DateOnly(2026, 2, 10)), (fields.Date!.Value, fields.OtherDate!.Value));
        Assert.Null(ReceiptText.Parse("STORE\n2026-02-10\nTOTAL 5.00", Today).OtherDate);
    }

    // ---------------------------------------------------------------- matching

    [Fact]
    public void MatchingTriesBothReadingsOfAnAmbiguousDate()
    {
        var receipt = new ReceiptFields(1500.00m, new DateOnly(2026, 10, 2), "SHOPPERS DRUG MART", OtherDate: new DateOnly(2026, 2, 10));

        var match = Assert.Single(ReceiptMatcher.Rank(receipt, [Tx("feb", "2026-02-11", 1500.00m, "SHOPPERS DRUG MART #0123")]));

        Assert.Contains("Posted a day later", match.Reasons);
    }

    private static LedgerTransaction Tx(string id, string date, decimal amount, string description, string category = Categories.Shopping) => new(new Transaction
    {
        TransactionId = id,
        AccountId = "chq",
        TransactionTimestamp = DateTimeOffset.Parse(date + "T00:00:00Z"),
        Amount = amount,
        DebitCreditMemo = DebitCreditMemo.Debit,
        Description = description,
        Category = category,
    });

    [Fact]
    public void RanksByAmountThenDateThenMerchant()
    {
        LedgerTransaction[] spending =
        [
            Tx("right", "2026-09-11", 60.00m, "GOODLIFE CLUBS #452"),
            Tx("same-amount-other-shop", "2026-09-10", 60.00m, "ESSO 1234"),
            Tx("wrong-amount", "2026-09-10", 61.50m, "GOODLIFE CLUBS #452"),
            Tx("too-late", "2026-10-15", 60.00m, "GOODLIFE CLUBS #452"),
        ];

        var matches = ReceiptMatcher.Rank(new ReceiptFields(60.00m, new DateOnly(2026, 9, 10), "GOODLIFE FITNESS"), spending);

        Assert.Equal(["right", "same-amount-other-shop"], matches.Select(m => m.Item.Transaction.TransactionId));
        Assert.Equal(["Same amount", "Posted a day later", "Merchant name matches"], matches[0].Reasons);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public void RestaurantChargesMayIncludeATip()
    {
        var dinner = Tx("dinner", "2026-09-12", 69.00m, "THE KEG #12", Categories.FoodAndDining);
        var shop = Tx("shop", "2026-09-12", 69.00m, "CANADIAN TIRE", Categories.Shopping);

        var match = Assert.Single(ReceiptMatcher.Rank(new ReceiptFields(60.00m, new DateOnly(2026, 9, 12), null), [dinner, shop]));

        Assert.Equal("dinner", match.Item.Transaction.TransactionId);
        Assert.Contains("15% more: a tip?", match.Reasons);
    }

    [Fact]
    public void WithoutATotalTheDateAndMerchantStillSuggestMatches()
    {
        var matches = ReceiptMatcher.Rank(
            new ReceiptFields(null, new DateOnly(2026, 3, 14), "Shoppers Drug Mart"),
            [Tx("a", "2026-03-15", 12.00m, "SHOPPERS DRUG MART #0123"), Tx("b", "2026-03-15", 9.00m, "TIM HORTONS")]);

        Assert.Equal("a", matches[0].Item.Transaction.TransactionId);
    }

    // ---------------------------------------------------------------- PDF receipts

    [Fact]
    public void ReadsTheTextOfAPdfReceipt()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter);
        string[] lines = ["BEST BUY CANADA", "Order date: Oct 3, 2026", "Subtotal 99.99", "HST 13.00", "Total 112.99"];
        for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 11, new PdfPoint(60, 700 - i * 18), font);

        var text = ReceiptPdf.ReadText(builder.Build());

        Assert.Equal(new ReceiptFields(112.99m, new DateOnly(2026, 10, 3), "BEST BUY CANADA"), ReceiptText.Parse(text, Today));
    }

    [Fact]
    public void ADamagedPdfHasNoText() => Assert.Equal("", ReceiptPdf.ReadText("%PDF-1.4 not really"u8.ToArray()));

    // ---------------------------------------------------------------- Claude (server)

    [Fact]
    public void ReadsClaudesStructuredOutput()
    {
        var fields = MapleLedger.ApiService.Receipts.ClaudeReceiptReader.Parse(
            """{"total": 24.72, "date": "2026-02-10", "merchant": "  Pharmaprix    Peel  "}""", Today);

        Assert.Equal(new ReceiptFields(24.72m, new DateOnly(2026, 2, 10), "Pharmaprix Peel"), fields);
    }

    [Theory]
    [InlineData("""{"total": null, "date": null, "merchant": null}""")]
    [InlineData("""{"total": 0, "date": "2027-05-01", "merchant": ""}""")]   // zero total, future date, blank name
    [InlineData("""{"total": "12.00", "date": "10/02/2026", "merchant": 5}""")] // wrong types
    [InlineData("not json")]
    public void DropsAnythingImplausibleFromClaude(string json) =>
        Assert.Equal(new ReceiptFields(null, null, null), MapleLedger.ApiService.Receipts.ClaudeReceiptReader.Parse(json, Today));
}
