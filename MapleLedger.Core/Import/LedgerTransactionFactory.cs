using System.Security.Cryptography;
using System.Text;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.Core.Import;

/// <summary>Converts parsed rows into categorized FDX transactions with stable ids.</summary>
public static class LedgerTransactionFactory
{
    /// <param name="memory">
    /// The user's earlier choices per merchant. A remembered category wins over the built-in rules,
    /// and remembered tags are added. The category still counts as automatic (not reviewed).
    /// </param>
    public static IReadOnlyList<LedgerTransaction> Create(
        string accountId,
        IEnumerable<ParsedTransaction> parsed,
        TransactionCategorizer categorizer,
        TaxSuggester taxSuggester,
        MerchantMemory? memory = null)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<LedgerTransaction>();
        foreach (var p in parsed)
        {
            var identity = IdentityKey(accountId, p);
            var occurrence = occurrences.GetValueOrDefault(identity);
            occurrences[identity] = occurrence + 1;

            var recalled = memory?.Recall(accountId, p.Description, p.Direction);
            var category = recalled?.Category ?? categorizer.Categorize(p.Description, p.Direction);
            var taxCode = taxSuggester.Suggest(p.Description, p.Direction, category.SubCategory);

            var transaction = new Transaction
            {
                TransactionId = CreateId(identity, occurrence),
                AccountId = accountId,
                TransactionTimestamp = AtMidnightUtc(p.Date),
                PostedTimestamp = p.PostedDate is { } posted ? AtMidnightUtc(posted) : null,
                Description = p.Description,
                DebitCreditMemo = p.Direction,
                Amount = p.Amount,
                Status = TransactionStatus.Posted,
                Category = category.Category,
                SubCategory = category.SubCategory,
                RunningBalance = p.Balance,
            };

            result.Add(new LedgerTransaction(transaction, taxCode is null ? null : new TaxTag(taxCode, TaxTagStatus.Suggested), Tags: recalled?.Tags));
        }

        return result;
    }

    /// <summary>
    /// Pasted data has no bank-issued id, so derive one from what identifies the transaction.
    /// Identical transactions on the same day (two $2.10 coffees) are told apart by their
    /// position in the paste. Re-pasting an overlapping date range therefore yields the same ids
    /// and the server can drop the duplicates.
    /// </summary>
    public static string CreateId(string identityKey, int occurrence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{identityKey}|{occurrence}"));
        return "ml-" + Convert.ToHexStringLower(hash)[..24];
    }

    private static string IdentityKey(string accountId, ParsedTransaction p) =>
        $"{accountId}|{p.Date:yyyy-MM-dd}|{p.Direction}|{p.Amount:0.00}|{TextNormalizer.ForMatching(p.Description).Trim()}";

    private static DateTimeOffset AtMidnightUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
