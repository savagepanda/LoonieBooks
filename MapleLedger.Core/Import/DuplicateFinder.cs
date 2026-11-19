using MapleLedger.Core.Categorization;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Import;

/// <summary>What <see cref="DuplicateFinder"/> needs to know about a transaction.</summary>
/// <param name="ImportBatchId">The import that added it, if known.</param>
/// <param name="KeptAsDistinct">The user said it isn't a duplicate of what it was paired with.</param>
public sealed record DuplicateCandidate(
    string AccountId,
    string TransactionId,
    DateOnly Date,
    DebitCreditMemo Direction,
    decimal Amount,
    string? Description,
    Guid? ImportBatchId,
    bool KeptAsDistinct = false);

/// <summary>Two transactions that look like the same one imported twice.</summary>
public sealed record DuplicateMatch(DuplicateCandidate First, DuplicateCandidate Second)
{
    public int DaysApart => Math.Abs(First.Date.DayNumber - Second.Date.DayNumber);
}

/// <summary>
/// Finds transactions imported twice under different ids. An import's ids come from the date,
/// amount and description, so the same purchase read from a PDF statement and from a CSV export
/// ("NORTHERN SHIELD INSU HALIFAX" vs "NORTHERN SHIELD INSU"), or dated by the purchase in one
/// and by the posting in the other, gets two ids and is counted twice.
/// </summary>
/// <remarks>
/// A pair is the same account, direction and amount, at most <see cref="MaxDaysApart"/> days apart,
/// with descriptions that look alike (<see cref="SimilarDescriptions"/>). Two identical
/// descriptions from the same import are real repeats (two coffees on one statement) and never
/// pair. Each transaction is in at most one pair, closest dates first, so two real coffees
/// imported twice make two pairs, not four.
/// </remarks>
public static class DuplicateFinder
{
    public const int MaxDaysApart = 3;

    public static IReadOnlyList<DuplicateMatch> Find(IEnumerable<DuplicateCandidate> transactions)
    {
        var matches = new List<DuplicateMatch>();
        foreach (var group in transactions.GroupBy(t => (t.AccountId, t.Direction, t.Amount)))
        {
            var items = group.OrderBy(t => t.Date).ThenBy(t => t.TransactionId, StringComparer.Ordinal).ToList();
            if (items.Count < 2) continue;

            var pairs = new List<(DuplicateCandidate A, DuplicateCandidate B, int Days)>();
            for (var i = 0; i < items.Count; i++)
            {
                for (var j = i + 1; j < items.Count && items[j].Date.DayNumber - items[i].Date.DayNumber <= MaxDaysApart; j++)
                {
                    if (LookLikeOne(items[i], items[j])) pairs.Add((items[i], items[j], items[j].Date.DayNumber - items[i].Date.DayNumber));
                }
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (a, b, _) in pairs.OrderBy(p => p.Days))
            {
                if (used.Contains(a.TransactionId) || used.Contains(b.TransactionId)) continue;
                used.Add(a.TransactionId);
                used.Add(b.TransactionId);
                matches.Add(new DuplicateMatch(a, b));
            }
        }

        return [.. matches.OrderByDescending(m => m.First.Date).ThenBy(m => m.First.AccountId, StringComparer.Ordinal)];
    }

    private static bool LookLikeOne(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.KeptAsDistinct && b.KeptAsDistinct) return false;

        var sameText = TextNormalizer.ForMatching(a.Description) == TextNormalizer.ForMatching(b.Description);
        if (sameText && a.ImportBatchId is not null && a.ImportBatchId == b.ImportBatchId) return false;

        return sameText || SimilarDescriptions(a.Description, b.Description);
    }

    /// <summary>
    /// Whether two descriptions could be the same transaction as printed by two different sources:
    /// the same merchant (<see cref="MerchantName"/>), or one a shortened form of the other
    /// (a town, province or reference added or cut off).
    /// </summary>
    public static bool SimilarDescriptions(string? a, string? b)
    {
        var keyA = MerchantName.Key(a);
        var keyB = MerchantName.Key(b);
        if (keyA.Length == 0 || keyB.Length == 0) return false;
        if (keyA == keyB) return true;

        var (shorter, longer) = keyA.Length <= keyB.Length ? (keyA, keyB) : (keyB, keyA);
        return shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }
}
