using MapleLedger.Fdx;

namespace MapleLedger.Core.Tax;

/// <summary>Indexed amounts that change every tax year. Unknown years return null.</summary>
public static class TaxYearAmounts
{
    private static readonly Dictionary<int, decimal> MedicalThresholds = new()
    {
        [2024] = 2759m,
        [2025] = 2834m,
        [2026] = 2890m,
    };

    /// <summary>
    /// The fixed part of the medical expense threshold: claims count above the lesser of this and
    /// 3% of net income.
    /// </summary>
    public static decimal? MedicalExpenseThreshold(int year) => MedicalThresholds.TryGetValue(year, out var amount) ? amount : null;
}

/// <summary>A 12-month period ending in the tax year, and the confirmed medical expenses paid in it.</summary>
public sealed record MedicalWindow(int EndMonth, DateOnly Start, DateOnly End, decimal Total, int Count)
{
    public bool IsCalendarYear => EndMonth == 12;
}

/// <param name="Windows">All twelve windows ending in <see cref="Year"/>, from January to December.</param>
/// <param name="Current">The window that matches what is currently claimed for the year, if any does.</param>
/// <param name="Threshold">The fixed threshold for the year, if LoonieBooks knows it.</param>
/// <param name="AppliedThreshold">The lesser of 3% of net income and the threshold, when net income was given.</param>
/// <param name="EstimatedClaim">The best window's total above the applied threshold. An estimate only.</param>
/// <param name="UsesPreviousYear">Whether the best window includes expenses paid in the previous year.</param>
public sealed record MedicalEstimate(
    int Year,
    IReadOnlyList<MedicalWindow> Windows,
    MedicalWindow Best,
    MedicalWindow? Current,
    decimal? NetIncome,
    decimal? Threshold,
    decimal? AppliedThreshold,
    decimal? EstimatedClaim,
    bool UsesPreviousYear);

/// <summary>
/// Medical expenses can be claimed for any 12-month period ending in the tax year. This finds
/// the period with the most confirmed expenses and estimates the part above the threshold. It is
/// an estimate for record keeping, not tax advice: the claim also depends on whose expenses they
/// are and on amounts already claimed in other years.
/// </summary>
public static class MedicalExpenses
{
    /// <summary>The payment dates that can fall in a window ending in <paramref name="year"/>.</summary>
    public static (DateOnly From, DateOnly To) Range(int year) => (new DateOnly(year - 1, 1, 1), new DateOnly(year, 12, 31));

    /// <summary>
    /// Confirmed medical expenses that could be claimed for <paramref name="year"/>: paid in the
    /// year or the year before, and not already assigned to a different year (other than the next
    /// one, where expenses after a chosen window are parked).
    /// </summary>
    public static IEnumerable<LedgerTransaction> Candidates(int year, IEnumerable<LedgerTransaction> transactions) =>
        transactions.Where(t =>
        {
            if (t.Tax is not { Status: TaxTagStatus.Confirmed } tax || !string.Equals(tax.Code, TaxCategories.Medical, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var paid = t.Transaction.EffectiveDate.Year;
            return (paid == year - 1 && (tax.ClaimYear is null || tax.ClaimYear == year))
                || (paid == year && (tax.ClaimYear is null || tax.ClaimYear == year || tax.ClaimYear == year + 1));
        });

    public static MedicalEstimate Estimate(int year, IEnumerable<LedgerTransaction> transactions, decimal? netIncome = null)
    {
        var candidates = Candidates(year, transactions).ToList();
        var windows = Enumerable.Range(1, 12).Select(month => Window(year, month, candidates)).ToList();

        // Ties go to the calendar year, which needs no expenses moved between returns.
        var best = windows.OrderByDescending(w => w.Total).ThenByDescending(w => w.EndMonth).First();
        // Several windows can match what is claimed when no expenses fall between them; prefer the best.
        var current = windows
            .Where(w => ClaimYearsFor(year, w.EndMonth, candidates).Count == 0)
            .OrderByDescending(w => w.EndMonth == best.EndMonth)
            .ThenByDescending(w => w.EndMonth)
            .FirstOrDefault();

        var threshold = TaxYearAmounts.MedicalExpenseThreshold(year);
        decimal? applied = netIncome is { } income && threshold is { } fixedPart
            ? Math.Min(Math.Round(Math.Max(income, 0) * 0.03m, 2), fixedPart)
            : null;

        return new MedicalEstimate(
            year,
            windows,
            best,
            current,
            netIncome,
            threshold,
            applied,
            applied is { } a ? Math.Max(0, best.Total - a) : null,
            best.Start.Year < year && candidates.Any(t => t.Transaction.EffectiveDate >= best.Start && t.Transaction.EffectiveDate.Year < year));
    }

    /// <summary>
    /// The claim-year changes that make <paramref name="year"/>'s report claim exactly the window
    /// ending in <paramref name="endMonth"/>: expenses inside it are claimed for the year, expenses
    /// paid later in the year move to the next year, and earlier ones go back to their own year.
    /// </summary>
    public static IReadOnlyList<(LedgerTransaction Item, int? ClaimYear)> ClaimYearsFor(int year, int endMonth, IEnumerable<LedgerTransaction> candidates)
    {
        var (start, end) = Bounds(year, endMonth);
        var changes = new List<(LedgerTransaction, int?)>();
        foreach (var item in candidates)
        {
            var date = item.Transaction.EffectiveDate;
            var paid = date.Year;
            int? wanted = date < start ? null
                : date <= end ? (paid == year ? null : year)
                : year + 1;

            if (item.Tax!.ClaimYear != wanted) changes.Add((item, wanted));
        }

        return changes;
    }

    public static (DateOnly Start, DateOnly End) Bounds(int year, int endMonth)
    {
        if (endMonth is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(endMonth));
        var end = new DateOnly(year, endMonth, DateTime.DaysInMonth(year, endMonth));
        return (new DateOnly(year, endMonth, 1).AddMonths(-11), end);
    }

    private static MedicalWindow Window(int year, int endMonth, IReadOnlyList<LedgerTransaction> candidates)
    {
        var (start, end) = Bounds(year, endMonth);
        var inWindow = candidates.Where(t => t.Transaction.EffectiveDate >= start && t.Transaction.EffectiveDate <= end).ToList();
        return new MedicalWindow(endMonth, start, end, inWindow.Sum(t => -t.Transaction.SignedAmount), inWindow.Count);
    }
}
