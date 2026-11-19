using MapleLedger.Fdx;

namespace MapleLedger.Core.Tax;

/// <summary>
/// The year-end review queue: suggestions waiting for a decision, and spending that might be
/// claimable but was never tagged.
/// </summary>
/// <param name="Pending">Suggested tags, oldest first: the queue to work through.</param>
/// <param name="PossiblyMissed">Untagged spending in categories where claims are common, newest first.</param>
public sealed record TaxReview(
    int Year,
    IReadOnlyList<LedgerTransaction> Pending,
    int Confirmed,
    int Rejected,
    IReadOnlyList<LedgerTransaction> PossiblyMissed)
{
    public int Decided => Confirmed + Rejected;
}

public static class TaxReviewBuilder
{
    public const int MaxPossiblyMissed = 300;

    /// <summary>Spending categories where untagged transactions are worth a second look.</summary>
    public static IReadOnlyList<string> CategoriesToCheck { get; } =
    [
        Categories.HealthAndFitness,
        Categories.GiftsAndDonations,
        Categories.Education,
        Categories.KidsAndFamily,
    ];

    public static TaxReview Build(int year, IEnumerable<LedgerTransaction> transactions)
    {
        var inYear = transactions.Where(t => t.Transaction.EffectiveDate.Year == year).ToList();

        var pending = inYear
            .Where(t => t.Tax?.Status == TaxTagStatus.Suggested)
            .OrderBy(t => t.Transaction.EffectiveDate)
            .ThenBy(t => t.Transaction.TransactionId, StringComparer.Ordinal)
            .ToList();

        // A rejected tag is a decision, so only transactions with no tag at all count as missed.
        var missed = inYear
            .Where(t => t.Tax is null
                && t.Transaction.DebitCreditMemo == DebitCreditMemo.Debit
                && CategoriesToCheck.Contains(t.Transaction.Category ?? ""))
            .OrderByDescending(t => t.Transaction.EffectiveDate)
            .Take(MaxPossiblyMissed)
            .ToList();

        return new TaxReview(
            year,
            pending,
            inYear.Count(t => t.Tax?.Status == TaxTagStatus.Confirmed),
            inYear.Count(t => t.Tax?.Status == TaxTagStatus.Rejected),
            missed);
    }
}
