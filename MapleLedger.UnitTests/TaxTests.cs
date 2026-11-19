using MapleLedger.Core;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests;

public class TaxTests
{
    private static int _next;

    internal static Transaction Tx(string date, decimal amount, string category = Categories.HealthAndFitness, DebitCreditMemo direction = DebitCreditMemo.Debit) => new()
    {
        TransactionId = $"t{Interlocked.Increment(ref _next):D4}",
        AccountId = "chq",
        TransactionTimestamp = DateTimeOffset.Parse(date + "T00:00:00Z"),
        Amount = amount,
        DebitCreditMemo = direction,
        Category = category,
        Description = category,
    };

    private static LedgerTransaction Tagged(string date, decimal amount, string code, TaxTagStatus status = TaxTagStatus.Confirmed) =>
        new(Tx(date, amount), new TaxTag(code, status));

    // ---------------------------------------------------------------- review queue (#21)

    [Fact]
    public void ReviewQueuesSuggestionsOldestFirstAndCountsDecisions()
    {
        LedgerTransaction[] items =
        [
            Tagged("2026-05-01", 10m, TaxCategories.Medical, TaxTagStatus.Suggested),
            Tagged("2026-02-01", 20m, TaxCategories.Charitable, TaxTagStatus.Suggested),
            Tagged("2026-03-01", 30m, TaxCategories.Medical),
            Tagged("2026-03-02", 40m, TaxCategories.Medical, TaxTagStatus.Rejected),
            Tagged("2025-12-31", 50m, TaxCategories.Medical, TaxTagStatus.Suggested), // other year
        ];

        var review = TaxReviewBuilder.Build(2026, items);

        Assert.Equal([20m, 10m], review.Pending.Select(p => p.Transaction.Amount));
        Assert.Equal((1, 1, 2), (review.Confirmed, review.Rejected, review.Decided));
    }

    [Fact]
    public void ReviewFlagsOnlyUntaggedSpendingInLikelyCategories()
    {
        LedgerTransaction[] items =
        [
            new(Tx("2026-04-01", 60m, Categories.HealthAndFitness)),                                        // missed
            new(Tx("2026-04-02", 25m, Categories.GiftsAndDonations)),                                       // missed
            new(Tx("2026-04-03", 80m, Categories.FoodAndDining)),                                           // not a likely category
            new(Tx("2026-04-04", 15m, Categories.HealthAndFitness, DebitCreditMemo.Credit)),                // money in
            new(Tx("2026-04-05", 70m), new TaxTag(TaxCategories.Medical, TaxTagStatus.Rejected)),            // already decided
            new(Tx("2025-04-01", 90m)),                                                                     // other year
        ];

        var review = TaxReviewBuilder.Build(2026, items);

        Assert.Equal([25m, 60m], review.PossiblyMissed.Select(p => p.Transaction.Amount));
    }

    // ---------------------------------------------------------------- claim years and the RRSP window (#24)

    [Theory]
    [InlineData(2026, "2027-03-01")]
    [InlineData(2027, "2028-02-29")] // leap year
    public void RrspDeadlineIsTheSixtiethDayOfTheNextYear(int year, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), TaxReportBuilder.RrspDeadline(year));

    [Fact]
    public void RrspContributionsEarlyNextYearCanBeMovedIntoTheReport()
    {
        var inYear = Tagged("2026-06-01", 1000m, TaxCategories.Rrsp);
        var early = Tagged("2027-02-15", 2000m, TaxCategories.Rrsp);
        var tooLate = Tagged("2027-03-02", 3000m, TaxCategories.Rrsp);
        var moved = Tagged("2027-01-10", 500m, TaxCategories.Rrsp) with { Tax = new TaxTag(TaxCategories.Rrsp, TaxTagStatus.Confirmed, ClaimYear: 2026) };

        var report2026 = TaxReportBuilder.Build(2026, [inYear, early, tooLate, moved]);
        var report2027 = TaxReportBuilder.Build(2027, [inYear, early, tooLate, moved]);

        Assert.Equal(1500m, Assert.Single(report2026.Categories).Confirmed);
        Assert.Equal([early], report2026.RrspFromNextYear);
        Assert.Equal(5000m, Assert.Single(report2027.Categories).Confirmed);
        Assert.Equal([moved], report2027.ClaimedInOtherYears);
    }

    [Fact]
    public void ClaimYearMovesAnyTaggedTransactionBetweenReports()
    {
        var lastYear = Tagged("2025-11-01", 300m, TaxCategories.Medical) with { Tax = new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed, ClaimYear: 2026) };
        var thisYear = Tagged("2026-02-01", 100m, TaxCategories.Medical);

        Assert.Equal(400m, Assert.Single(TaxReportBuilder.Build(2026, [lastYear, thisYear]).Categories).Confirmed);
        var report2025 = TaxReportBuilder.Build(2025, [lastYear, thisYear]);
        Assert.Empty(report2025.Categories);
        Assert.Equal([lastYear], report2025.ClaimedInOtherYears);
    }

    // ---------------------------------------------------------------- medical expense window (#23)

    [Fact]
    public void MedicalWindowFindsTheBestTwelveMonthsAndEstimatesTheClaim()
    {
        LedgerTransaction[] items =
        [
            Tagged("2025-01-15", 500m, TaxCategories.Medical),   // before every window ending in 2026
            Tagged("2025-09-15", 2000m, TaxCategories.Medical),
            Tagged("2026-02-01", 1500m, TaxCategories.Medical),
            Tagged("2026-08-01", 400m, TaxCategories.Medical),
            Tagged("2026-05-01", 999m, TaxCategories.Medical, TaxTagStatus.Suggested), // not confirmed
            Tagged("2026-05-02", 50m, TaxCategories.Charitable),
        ];

        var estimate = MedicalExpenses.Estimate(2026, items, netIncome: 60_000m);

        // Sep 2025 - Aug 2026 holds 2000 + 1500 + 400.
        Assert.Equal((8, 3900m), (estimate.Best.EndMonth, estimate.Best.Total));
        Assert.Equal(new DateOnly(2025, 9, 1), estimate.Best.Start);
        Assert.Equal(12, estimate.Current!.EndMonth); // nothing moved yet: the calendar year
        Assert.Equal(1900m, estimate.Current.Total);
        Assert.Equal(1800m, estimate.AppliedThreshold); // 3% of 60,000 is below $2,890
        Assert.Equal(2100m, estimate.EstimatedClaim);
        Assert.True(estimate.UsesPreviousYear);
    }

    [Fact]
    public void ApplyingAMedicalWindowMovesExpensesOutsideItToTheirNeighbouringYears()
    {
        var march2025 = Tagged("2025-03-01", 500m, TaxCategories.Medical);
        var sept2025 = Tagged("2025-09-15", 2000m, TaxCategories.Medical);
        var feb2026 = Tagged("2026-02-01", 1500m, TaxCategories.Medical);
        var oct2026 = Tagged("2026-10-01", 400m, TaxCategories.Medical);
        LedgerTransaction[] items = [march2025, sept2025, feb2026, oct2026];

        var changes = MedicalExpenses.ClaimYearsFor(2026, 8, items).ToDictionary(c => c.Item, c => c.ClaimYear);

        Assert.Equal(2, changes.Count);
        Assert.Equal(2026, changes[sept2025]);
        Assert.Equal(2027, changes[oct2026]);

        var applied = items.Select(i => changes.TryGetValue(i, out var y) ? i with { Tax = i.Tax! with { ClaimYear = y } } : i).ToList();
        Assert.Equal(3500m, Assert.Single(TaxReportBuilder.Build(2026, applied).Categories).Confirmed);
        Assert.Equal(500m, Assert.Single(TaxReportBuilder.Build(2025, applied).Categories).Confirmed);
        Assert.Equal(8, MedicalExpenses.Estimate(2026, applied).Current!.EndMonth);
        Assert.Empty(MedicalExpenses.ClaimYearsFor(2026, 8, applied));
    }

    [Theory]
    [InlineData(2026, 200_000, 2890)] // capped at the annual amount
    [InlineData(2026, 10_000, 300)]
    public void MedicalThresholdIsTheLesserOfThreePercentAndTheAnnualAmount(int year, int netIncome, int expected) =>
        Assert.Equal(expected, MedicalExpenses.Estimate(year, [], netIncome).AppliedThreshold);

    [Fact]
    public void UnknownYearsHaveNoMedicalThreshold() =>
        Assert.Null(MedicalExpenses.Estimate(2031, [], 50_000m).EstimatedClaim);

    // ---------------------------------------------------------------- household (#25)

    [Fact]
    public void ReportsCanBeFilteredToOnePersonsClaims()
    {
        LedgerTransaction[] items =
        [
            new(Tx("2026-03-01", 800m), new TaxTag(TaxCategories.ChildCare, TaxTagStatus.Confirmed, ClaimedBy: "hm-sam")),
            new(Tx("2026-03-02", 100m), new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed, ClaimedBy: "hm-alex")),
            new(Tx("2026-03-03", 50m), new TaxTag(TaxCategories.Charitable, TaxTagStatus.Confirmed)),
        ];

        Assert.Equal(3, TaxReportBuilder.Build(2026, items).Categories.Count);
        Assert.Equal(800m, Assert.Single(TaxReportBuilder.Build(2026, items, "hm-sam").Categories).Confirmed);
        Assert.Equal(50m, Assert.Single(TaxReportBuilder.Build(2026, items, TaxReportBuilder.Unassigned).Categories).Confirmed);
    }

    // ---------------------------------------------------------------- business-use share (#26)

    [Fact]
    public void BusinessUsePercentCountsOnlyThatShareOfBusinessExpenses()
    {
        LedgerTransaction[] items =
        [
            new(Tx("2026-02-01", 120m, Categories.BillsAndUtilities), new TaxTag(TaxCategories.Business, TaxTagStatus.Confirmed, BusinessUsePercent: 30m)),
            new(Tx("2026-02-02", 50m, Categories.BillsAndUtilities), new TaxTag(TaxCategories.Business, TaxTagStatus.Confirmed)),
            new(Tx("2026-02-03", 10m, Categories.BillsAndUtilities, DebitCreditMemo.Credit), new TaxTag(TaxCategories.Business, TaxTagStatus.Confirmed, BusinessUsePercent: 30m)),
            // Not a business category: the percentage is ignored.
            new(Tx("2026-02-04", 100m), new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed, BusinessUsePercent: 50m)),
        ];

        var report = TaxReportBuilder.Build(2026, items);

        Assert.Equal(36m + 50m - 3m, report.Categories.Single(c => c.Category.Code == TaxCategories.Business).Confirmed);
        Assert.Equal(100m, report.Categories.Single(c => c.Category.Code == TaxCategories.Medical).Confirmed);
        var lines = TaxReportBuilder.ToCsv(report, new Dictionary<string, string>()).Split(Environment.NewLine);
        Assert.EndsWith(",30,36.00", lines.Single(l => l.Contains("2026-02-01")));
        Assert.EndsWith(",,100.00", lines.Single(l => l.Contains("2026-02-04")));
    }

    [Theory]
    [InlineData(TaxCategories.Business, true)]
    [InlineData(TaxCategories.Rental, true)]
    [InlineData(TaxCategories.Employment, true)]
    [InlineData(TaxCategories.Medical, false)]
    [InlineData("NOPE", false)]
    public void OnlyIncomeEarningExpensesAllowABusinessUseShare(string code, bool expected) =>
        Assert.Equal(expected, TaxCategories.AllowsBusinessUse(code));
}
