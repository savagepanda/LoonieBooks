using MapleLedger.Core;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests;

public class TransactionSortTests
{
    private static LedgerTransaction Tx(string id, string date, decimal amount, DebitCreditMemo direction, string description, string category, TaxTag? tax = null) => new(new Transaction
    {
        TransactionId = id,
        AccountId = "chq",
        TransactionTimestamp = DateTimeOffset.Parse(date + "T00:00:00Z"),
        Amount = amount,
        DebitCreditMemo = direction,
        Description = description,
        Category = category,
    }, tax);

    // Newest first, as the ledger returns them.
    private static readonly LedgerTransaction[] Items =
    [
        Tx("pay", "2026-09-05", 2500m, DebitCreditMemo.Credit, "payroll", Categories.Income),
        Tx("gas", "2026-09-04", 60m, DebitCreditMemo.Debit, "Esso", Categories.AutoAndTransport),
        Tx("rx", "2026-09-03", 25m, DebitCreditMemo.Debit, "Pharmacy", "Health & Fitness", new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed)),
        Tx("gift", "2026-09-02", 100m, DebitCreditMemo.Debit, "Red Cross", "Gifts & Donations", new TaxTag(TaxCategories.Charitable, TaxTagStatus.Suggested)),
        Tx("old", "2026-09-01", 60m, DebitCreditMemo.Debit, "ESSO", Categories.AutoAndTransport, new TaxTag(TaxCategories.Medical, TaxTagStatus.Rejected)),
    ];

    private static string[] Ids(TransactionSortColumn column, bool descending) =>
        [.. TransactionSort.Sort(Items, new(column, descending)).Select(t => t.Transaction.TransactionId)];

    [Fact]
    public void SortsEachColumnBothWays()
    {
        Assert.Equal(["pay", "gas", "rx", "gift", "old"], Ids(TransactionSortColumn.Date, true));
        Assert.Equal(["old", "gift", "rx", "gas", "pay"], Ids(TransactionSortColumn.Date, false));

        // Case-insensitive, ties newest first.
        Assert.Equal(["gas", "old", "pay", "rx", "gift"], Ids(TransactionSortColumn.Description, false));

        // By size, in or out; equal amounts newest first in both directions.
        Assert.Equal(["pay", "gift", "gas", "old", "rx"], Ids(TransactionSortColumn.Amount, true));
        Assert.Equal(["rx", "gas", "old", "gift", "pay"], Ids(TransactionSortColumn.Amount, false));

        // Untagged and rejected suggestions go last either way.
        Assert.Equal(["gift", "rx", "pay", "gas", "old"], Ids(TransactionSortColumn.Tax, false));
        Assert.Equal(["rx", "gift", "pay", "gas", "old"], Ids(TransactionSortColumn.Tax, true));
    }

    [Fact]
    public void ClickingAColumnPicksAUsefulDirectionThenFlips()
    {
        var order = new TransactionSortOrder();
        Assert.Equal(new TransactionSortOrder(TransactionSortColumn.Date, true), order);
        Assert.Equal(new TransactionSortOrder(TransactionSortColumn.Date, false), order.Toggle(TransactionSortColumn.Date));
        Assert.Equal(new TransactionSortOrder(TransactionSortColumn.Description, false), order.Toggle(TransactionSortColumn.Description));
        Assert.Equal(new TransactionSortOrder(TransactionSortColumn.Amount, true), order.Toggle(TransactionSortColumn.Amount));
        Assert.Equal(new TransactionSortOrder(TransactionSortColumn.Amount, false), order.Toggle(TransactionSortColumn.Amount).Toggle(TransactionSortColumn.Amount));
    }
}
