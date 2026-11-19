using MapleLedger.Core.Tax;

namespace MapleLedger.Core;

/// <summary>A column of the transactions list that it can be sorted by.</summary>
public enum TransactionSortColumn { Date, Description, Category, Tax, Amount }

/// <summary>How the transactions list is sorted (newest first unless the user picks a column).</summary>
public sealed record TransactionSortOrder(TransactionSortColumn Column = TransactionSortColumn.Date, bool Descending = true)
{
    /// <summary>
    /// Clicking a column: the same one again flips the direction; a new one starts with what's most
    /// useful first (newest dates, the biggest amounts, A to Z).
    /// </summary>
    public TransactionSortOrder Toggle(TransactionSortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new(column, column is TransactionSortColumn.Date or TransactionSortColumn.Amount);
}

public static class TransactionSort
{
    /// <summary>
    /// Sorts by the column, ties newest first. Amounts sort by size, in or out, as the list shows
    /// them. Transactions with no tax treatment sort after the tagged ones either way.
    /// </summary>
    public static List<LedgerTransaction> Sort(IEnumerable<LedgerTransaction> items, TransactionSortOrder order)
    {
        int Direction(int compared) => order.Descending ? -compared : compared;

        Comparison<LedgerTransaction> primary = order.Column switch
        {
            TransactionSortColumn.Description => (a, b) => Direction(Text(a.Transaction.Description, b.Transaction.Description)),
            TransactionSortColumn.Category => (a, b) => Direction(Text(CategoryLabel(a), CategoryLabel(b))),
            TransactionSortColumn.Tax => (a, b) => TaxName(a) is { } x
                ? TaxName(b) is { } y ? Direction(Text(x, y)) : -1
                : TaxName(b) is null ? 0 : 1,
            TransactionSortColumn.Amount => (a, b) => Direction(Math.Abs(a.Transaction.Amount).CompareTo(Math.Abs(b.Transaction.Amount))),
            _ => (a, b) => Direction(a.Transaction.EffectiveDate.CompareTo(b.Transaction.EffectiveDate)),
        };

        // List.Sort isn't stable; the original (newest first) position breaks remaining ties.
        var indexed = items.Select((item, i) => (Item: item, Index: i)).ToList();
        indexed.Sort((a, b) =>
        {
            var c = primary(a.Item, b.Item);
            if (c == 0) c = b.Item.Transaction.EffectiveDate.CompareTo(a.Item.Transaction.EffectiveDate);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return indexed.Select(p => p.Item).ToList();
    }

    private static int Text(string? a, string? b) => StringComparer.OrdinalIgnoreCase.Compare(a?.Trim() ?? "", b?.Trim() ?? "");

    private static string CategoryLabel(LedgerTransaction t) =>
        Categories.Label(t.Transaction.Category ?? Categories.Uncategorized, string.IsNullOrEmpty(t.Transaction.SubCategory) ? null : t.Transaction.SubCategory);

    /// <summary>The tax treatment the list shows: none for an untagged or rejected suggestion.</summary>
    private static string? TaxName(LedgerTransaction t) =>
        t.Tax is { Status: not TaxTagStatus.Rejected } tag ? TaxCategories.Find(tag.Code)?.Name ?? tag.Code : null;
}
