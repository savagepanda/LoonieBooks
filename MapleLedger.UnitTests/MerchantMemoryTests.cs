using MapleLedger.Core;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Import;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;

namespace MapleLedger.UnitTests;

public class MerchantMemoryTests
{
    private static readonly string Pizza = MerchantName.Key("BELLA PIZZA #12 BARRIE");

    private static MerchantChoice Choice(string account, string? category, string date, IReadOnlyList<string>? tags = null, DebitCreditMemo direction = DebitCreditMemo.Debit) =>
        new(account, Pizza, direction, category, null, category is null ? null : DateOnly.Parse(date), tags);

    [Fact]
    public void CategoryComesFromTheSameAccountFirstThenTheNewestChoiceAnywhere()
    {
        var memory = new MerchantMemory(
        [
            Choice("visa", Categories.Shopping, "2026-09-01"),
            Choice("amex", Categories.Entertainment, "2026-10-01"),
            Choice("mc", Categories.Travel, "2026-08-01"),
        ]);

        Assert.Equal(Categories.Shopping, memory.Recall("visa", "BELLA PIZZA #40 KINGSTON", DebitCreditMemo.Debit).Category?.Category);
        Assert.Equal(Categories.Entertainment, memory.Recall("chequing", "Bella Pizza", DebitCreditMemo.Debit).Category?.Category);
        Assert.Null(memory.Recall("visa", "BELLA PIZZA", DebitCreditMemo.Credit).Category); // a refund isn't the purchase
        Assert.Null(memory.Recall("visa", "OTHER PLACE", DebitCreditMemo.Debit).Category);
    }

    [Fact]
    public void TagsComeOnlyFromTheSameAccount()
    {
        var memory = new MerchantMemory([Choice("rental", null, "", ["rental-tag"])]);

        var here = memory.Recall("rental", "BELLA PIZZA", DebitCreditMemo.Debit);
        Assert.Equal(["rental-tag"], here.Tags);
        Assert.Null(here.Category);
        Assert.Null(memory.Recall("chequing", "BELLA PIZZA", DebitCreditMemo.Debit).Tags);
    }

    [Fact]
    public void ImportUsesTheRememberedCategoryOverTheBuiltInRulesAndKeepsItUnreviewed()
    {
        var memory = new MerchantMemory(
        [
            new MerchantChoice("visa", MerchantName.Key("TIM HORTONS #1"), DebitCreditMemo.Debit, Categories.Shopping, null, new DateOnly(2026, 9, 1), ["work"]),
        ]);
        var parsed = new[]
        {
            new ParsedTransaction(1, new DateOnly(2026, 11, 1), null, "TIM HORTONS #2201 TORONTO", 4.25m, DebitCreditMemo.Debit, null),
            new ParsedTransaction(2, new DateOnly(2026, 11, 2), null, "SHELL C12345 OTTAWA", 60m, DebitCreditMemo.Debit, null),
        };

        var created = LedgerTransactionFactory.Create("visa", parsed, new TransactionCategorizer(), new TaxSuggester(), memory);

        Assert.Equal(Categories.Shopping, created[0].Transaction.Category);
        Assert.Equal(["work"], created[0].Tags);
        Assert.False(created[0].CategoryIsManual);
        Assert.Equal(Categories.AutoAndTransport, created[1].Transaction.Category); // nothing remembered: the rules
        Assert.Null(created[1].Tags);

        var withoutMemory = LedgerTransactionFactory.Create("visa", parsed, new TransactionCategorizer(), new TaxSuggester());
        Assert.Equal(Categories.FoodAndDining, withoutMemory[0].Transaction.Category);
    }
}
