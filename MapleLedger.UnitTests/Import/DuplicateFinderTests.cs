using MapleLedger.Core.Import;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests.Import;

public class DuplicateFinderTests
{
    private static readonly Guid Pdf = Guid.NewGuid(), Csv = Guid.NewGuid();

    private static DuplicateCandidate C(string id, string date, decimal amount, string description, Guid? batch, string account = "visa",
        DebitCreditMemo direction = DebitCreditMemo.Debit, bool kept = false) =>
        new(account, id, DateOnly.Parse(date), direction, amount, description, batch, kept);

    private static IEnumerable<(string, string)> Pairs(IEnumerable<DuplicateCandidate> items) =>
        DuplicateFinder.Find(items).Select(m => (m.First.TransactionId, m.Second.TransactionId));

    [Fact]
    public void PairsAPdfAndCsvImportOfTheSameTransactions()
    {
        // The same statement imported from a PDF (with the town) and from a CSV (without).
        var pairs = Pairs(
        [
            C("pdf-refund", "2026-05-13", 1380m, "NORTHERN SHIELD INSU HALIFAX", Pdf, direction: DebitCreditMemo.Credit),
            C("csv-refund", "2026-05-13", 1380m, "NORTHERN SHIELD INSU", Csv, direction: DebitCreditMemo.Credit),
            C("csv-charge", "2026-05-08", 6425m, "NORTHERN SHIELD INSU", Csv),
            C("pdf-charge", "2026-05-08", 6425m, "NORTHERN SHIELD INSU HALIFAX", Pdf),
            C("other", "2026-05-08", 6425m, "SOMETHING ELSE", Pdf),
        ]);

        Assert.Equal([("csv-refund", "pdf-refund"), ("csv-charge", "pdf-charge")], pairs);
    }

    [Fact]
    public void RealRepeatsInOneImportAreNotDuplicates()
    {
        Assert.Empty(Pairs([C("a", "2026-05-01", 2.10m, "TIM HORTONS #1234", Csv), C("b", "2026-05-01", 2.10m, "TIM HORTONS #1234", Csv)]));
    }

    [Fact]
    public void RepeatsImportedTwicePairOneToOneClosestFirst()
    {
        // Two coffees, read from both files, one file dating by posting a day later.
        var pairs = Pairs(
        [
            C("csv1", "2026-05-01", 2.10m, "TIM HORTONS #1234", Csv),
            C("csv2", "2026-05-01", 2.10m, "TIM HORTONS #1234", Csv),
            C("pdf1", "2026-05-02", 2.10m, "TIM HORTONS #1234 BARRIE ON", Pdf),
            C("pdf2", "2026-05-02", 2.10m, "TIM HORTONS #1234 BARRIE ON", Pdf),
        ]);

        Assert.Equal(2, pairs.Count());
        Assert.All(pairs, p => Assert.True(p.Item1.StartsWith("csv") && p.Item2.StartsWith("pdf")));
    }

    [Theory]
    [InlineData("2026-05-05", 6425, "NORTHERN SHIELD INSU", "visa", DebitCreditMemo.Debit)]  // too far apart
    [InlineData("2026-05-01", 6425.01, "NORTHERN SHIELD INSU", "visa", DebitCreditMemo.Debit)] // different amount
    [InlineData("2026-05-01", 6425, "NORTHERN SHIELD INSU", "chequing", DebitCreditMemo.Debit)] // different account
    [InlineData("2026-05-01", 6425, "NORTHERN SHIELD INSU", "visa", DebitCreditMemo.Credit)] // a refund, not the charge
    [InlineData("2026-05-01", 6425, "SECURE STORAGE", "visa", DebitCreditMemo.Debit)]     // different merchant
    public void NeedsTheSameAccountDirectionAmountAndALookAlikeNearbyDescription(string date, decimal amount, string description, string account, DebitCreditMemo direction)
    {
        Assert.Empty(Pairs([C("a", "2026-05-01", 6425m, "NORTHERN SHIELD INSU HALIFAX", Pdf), C("b", date, amount, description, Csv, account, direction)]));
    }

    [Fact]
    public void PairsKeptAsDistinctStayApartUntilANewLookAlikeArrives()
    {
        var a = C("a", "2026-05-01", 50m, "ESSO 1234 TORONTO", Pdf, kept: true);
        var b = C("b", "2026-05-01", 50m, "ESSO", Csv, kept: true);
        Assert.Empty(Pairs([a, b]));
        Assert.Single(Pairs([a, b, C("c", "2026-05-01", 50m, "ESSO TORONTO", Guid.NewGuid())]));
    }

    [Theory]
    [InlineData("NORTHERN SHIELD INSU HALIFAX", "NORTHERN SHIELD INSU", true)]
    [InlineData("AMZN MKTP CA*AB12CD", "Amzn Mktp CA", true)]
    [InlineData("PIZZAVILLE #60 BARRIE", "PIZZAVILLE #69 KINGSTON", true)]
    [InlineData("NORTHERN SHIELD INS", "NORTHERN SHIELD INSU HALIFAX", true)] // cut off mid-word
    [InlineData("BELL", "BELL CANADA", true)]
    [InlineData("ABC", "ABC MARKET", false)] // too short to tell
    [InlineData("SHOPPERS DRUG MART", "SOBEYS", false)]
    [InlineData("12345", "12345", false)]
    public void SimilarDescriptions(string a, string b, bool similar)
    {
        Assert.Equal(similar, DuplicateFinder.SimilarDescriptions(a, b));
        Assert.Equal(similar, DuplicateFinder.SimilarDescriptions(b, a));
    }
}
