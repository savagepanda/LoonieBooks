using MapleLedger.Core;
using MapleLedger.Core.Categorization;
using MapleLedger.Core.Import;
using MapleLedger.Core.Tax;
using MapleLedger.Fdx;
using Microsoft.Extensions.DependencyInjection;

namespace MapleLedger.UnitTests;

public class CategorizationTests
{
    private readonly TransactionCategorizer _categorizer = new();

    [Theory]
    [InlineData("TIM HORTONS #1234 TORONTO", Categories.FoodAndDining, "Coffee Shops")]
    [InlineData("UBER* EATS PENDING", Categories.FoodAndDining, "Food Delivery")]
    [InlineData("UBER* TRIP", Categories.AutoAndTransport, Categories.Taxi)]
    [InlineData("PAYMENT - THANK YOU", Categories.Transfer, "Credit Card Payment")]
    [InlineData("SHOPPERS DRUG MART #0123", Categories.HealthAndFitness, "Pharmacy")]
    [InlineData("Bell Canada", Categories.BillsAndUtilities, "Mobile & Internet")]
    [InlineData("PETRO-CANADA 4521", Categories.AutoAndTransport, Categories.Gas)]
    [InlineData("SHELL C12345 OTTAWA", Categories.AutoAndTransport, Categories.Gas)]
    [InlineData("IMPARK 00012345", Categories.AutoAndTransport, Categories.Parking)]
    [InlineData("GREEN P PARKING", Categories.AutoAndTransport, Categories.Parking)]
    [InlineData("CO-OP CABS", Categories.AutoAndTransport, Categories.Taxi)]
    [InlineData("BECK TAXI 123", Categories.AutoAndTransport, Categories.Taxi)]
    [InlineData("TOYOTA FINANCIAL SERVICES PAP", Categories.AutoAndTransport, Categories.CarLease)]
    [InlineData("GM FINANCIAL CANADA", Categories.AutoAndTransport, Categories.CarLease)]
    [InlineData("ROGERS CABLE", Categories.BillsAndUtilities, "Mobile & Internet")]
    [InlineData("CITY OF TORONTO PROPERTY TAX", Categories.Taxes, Categories.PropertyTax)]
    [InlineData("OTTAWA PROPERTY TAXES", Categories.Taxes, Categories.PropertyTax)]
    [InlineData("VILLE DE MONTRÉAL TAXES MUNICIPALES", Categories.Taxes, Categories.PropertyTax)]
    [InlineData("TAXE FONCIÈRE", Categories.Taxes, Categories.PropertyTax)]
    public void MatchesCommonCanadianMerchants(string description, string category, string subCategory)
    {
        var result = _categorizer.Categorize(description, DebitCreditMemo.Debit);

        Assert.Equal(category, result.Category);
        Assert.Equal(subCategory, result.SubCategory);
    }

    [Fact]
    public void EveryRuleFilesIntoTheCategoryTree()
    {
        Assert.All(DefaultCanadianRules.Rules, r => Assert.True(Categories.IsValid(r.Category, r.SubCategory), $"{r.Pattern}: {r.Category} › {r.SubCategory}"));
        Assert.Equal(Categories.All.Count, Categories.All.Distinct().Count());
        Assert.All(Categories.Tree, n => Assert.Equal(n.SubCategories.Count, n.SubCategories.Distinct().Count()));
    }

    [Theory]
    [InlineData(Categories.Income, null, true)]
    [InlineData(Categories.Income, Categories.Salary, true)]
    [InlineData(Categories.Income, Categories.Gas, false)]
    [InlineData("Salary", null, false)]
    [InlineData(null, null, false)]
    public void ValidatesBothLevels(string? category, string? subCategory, bool expected) =>
        Assert.Equal(expected, Categories.IsValid(category, subCategory));

    [Fact]
    public void KeepsDefaultRulesWhenResolvedFromDependencyInjection()
    {
        // Regression: an optional IEnumerable<CategoryRule> parameter was satisfied by the
        // container with an empty list, leaving every transaction uncategorized.
        var categorizer = new ServiceCollection().AddSingleton<TransactionCategorizer>().BuildServiceProvider().GetRequiredService<TransactionCategorizer>();

        Assert.Equal(Categories.FoodAndDining, categorizer.Categorize("TIM HORTONS", DebitCreditMemo.Debit).Category);
    }

    [Fact]
    public void MatchesWholeWordsOnly()
    {
        Assert.Equal(Categories.FoodAndDining, _categorizer.Categorize("BELLA PIZZA", DebitCreditMemo.Debit).Category);
        Assert.Equal(Categories.Uncategorized, _categorizer.Categorize("CURRENTLY UNKNOWN", DebitCreditMemo.Debit).Category);
    }

    [Fact]
    public void DirectionalRulesOnlyApplyToTheirDirection()
    {
        Assert.Equal(Categories.OtherIncome, _categorizer.Categorize("INTEREST", DebitCreditMemo.Credit).SubCategory);
        Assert.Equal("Interest Charged", _categorizer.Categorize("PURCHASE INTEREST", DebitCreditMemo.Debit).SubCategory);
    }

    [Theory]
    [InlineData("Income", "Paycheque", Categories.Income, Categories.Salary)]
    [InlineData("Income", "Interest", Categories.Income, Categories.OtherIncome)]
    [InlineData("Income", null, Categories.Income, null)]
    [InlineData("Salary", "Paycheque", Categories.Income, Categories.Salary)]
    [InlineData("Business Income", null, Categories.Income, Categories.BusinessIncome)]
    [InlineData("Other Income", "Interest", Categories.Income, Categories.OtherIncome)]
    [InlineData("Gas", null, Categories.AutoAndTransport, Categories.Gas)]
    [InlineData("Taxi", "Rideshare", Categories.AutoAndTransport, Categories.Taxi)]
    [InlineData("Car Lease", null, Categories.AutoAndTransport, Categories.CarLease)]
    [InlineData(Categories.AutoAndTransport, "Rideshare", Categories.AutoAndTransport, Categories.Taxi)]
    [InlineData(Categories.AutoAndTransport, "Public Transit", Categories.AutoAndTransport, "Public Transit")]
    [InlineData("Property Tax", null, Categories.Taxes, Categories.PropertyTax)]
    [InlineData(Categories.Shopping, "Not a shopping detail", Categories.Shopping, null)]
    [InlineData("Someone's own category", "Detail", "Someone's own category", "Detail")]
    [InlineData(null, null, null, null)]
    public void OlderCategoriesMapOntoTheTwoLevelTree(string? category, string? subCategory, string? expectedCategory, string? expectedSubCategory) =>
        Assert.Equal((expectedCategory, expectedSubCategory), Categories.Normalize(category, subCategory));

    [Fact]
    public void UsersOwnCategoriesJoinTheTree()
    {
        var tree = Categories.WithCustom([new("1", "Pets", null), new("2", "Pets", "Vet"), new("3", Categories.Income, "Gifts Received"), new("5", Categories.Income, "rental income"), new("4", "Pets", "Grooming")]);

        Assert.Equal(Categories.Tree.Count + 1, tree.Count);
        Assert.Equal(["Grooming", "Vet"], Categories.Find(tree, "Pets")!.SubCategories);
        Assert.Equal([Categories.Salary, Categories.BusinessIncome, Categories.RentalIncome, Categories.OtherIncome, "Gifts Received"], Categories.Find(tree, Categories.Income)!.SubCategories);
        Assert.True(Categories.IsValid(tree, "Pets", "Vet"));
        Assert.False(Categories.IsValid("Pets", "Vet"));
        Assert.Equal(("Pets", (string?)"Vet"), Categories.Normalize("Pets", "Vet", tree));
        Assert.Equal((Categories.Income, (string?)"Gifts Received"), Categories.Normalize(Categories.Income, "Gifts Received", tree));
        Assert.Equal((Categories.Income, (string?)null), Categories.Normalize(Categories.Income, "Gifts Received"));
        Assert.Same(Categories.Tree, Categories.WithCustom([]));
    }

    [Fact]
    public void PaychequesAreSalaryAndBenefitsAreOtherIncome()
    {
        Assert.Equal(new CategoryAssignment(Categories.Income, Categories.Salary), _categorizer.Categorize("PAYROLL DEPOSIT ACME CORP", DebitCreditMemo.Credit));
        Assert.Equal(new CategoryAssignment(Categories.Income, Categories.OtherIncome), _categorizer.Categorize("CANADA CHILD BENEFIT", DebitCreditMemo.Credit));
    }

    [Fact]
    public void IgnoresAccentsAndCase()
    {
        Assert.Equal(new CategoryAssignment(Categories.Income, Categories.Salary), _categorizer.Categorize("Dépôt direct - Employeur", DebitCreditMemo.Credit));
    }

    [Theory]
    [InlineData("SHOPPERS DRUG MART", TaxCategories.Medical)]
    [InlineData("SMILE DENTAL CENTRE", TaxCategories.Medical)]
    [InlineData("CANADAHELPS.ORG", TaxCategories.Charitable)]
    [InlineData("LITTLE STARS DAYCARE", TaxCategories.ChildCare)]
    [InlineData("TD RRSP CONTRIBUTION", TaxCategories.Rrsp)]
    [InlineData("NSLSC PAYMENT", TaxCategories.StudentLoanInterest)]
    [InlineData("TIM HORTONS", null)]
    public void SuggestsTaxCategoriesForSpending(string description, string? expected)
    {
        var category = _categorizer.Categorize(description, DebitCreditMemo.Debit);

        Assert.Equal(expected, new TaxSuggester().Suggest(description, DebitCreditMemo.Debit, category.SubCategory));
    }

    [Fact]
    public void TaxCategoriesHaveUniqueCodesAndCompleteDetails()
    {
        Assert.Equal(TaxCategories.All.Count, TaxCategories.All.Select(c => c.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(TaxCategories.All, c =>
        {
            Assert.Matches(@"^(Line \d{5}|Form T\d+)", c.CraReference);
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Guidance));
            Assert.Same(c, TaxCategories.Find(c.Code.ToLowerInvariant()));
        });
    }

    [Fact]
    public void NeverSuggestsTaxCategoriesForMoneyIn()
    {
        Assert.Null(new TaxSuggester().Suggest("SHOPPERS DRUG MART REFUND", DebitCreditMemo.Credit, "Pharmacy"));
    }

    [Fact]
    public void FactoryProducesStableDistinctIdsAndSuggestions()
    {
        var parsed = new[]
        {
            new ParsedTransaction(1, new DateOnly(2026, 9, 2), null, "TIM HORTONS", 2.10m, DebitCreditMemo.Debit, null),
            new ParsedTransaction(2, new DateOnly(2026, 9, 2), null, "TIM HORTONS", 2.10m, DebitCreditMemo.Debit, null),
            new ParsedTransaction(3, new DateOnly(2026, 9, 3), null, "REXALL PHARMACY", 19.99m, DebitCreditMemo.Debit, null),
        };

        var first = LedgerTransactionFactory.Create("acct-1", parsed, _categorizer, new TaxSuggester());
        var again = LedgerTransactionFactory.Create("acct-1", parsed, _categorizer, new TaxSuggester());
        var otherAccount = LedgerTransactionFactory.Create("acct-2", parsed, _categorizer, new TaxSuggester());

        Assert.Equal(first.Select(t => t.Transaction.TransactionId), again.Select(t => t.Transaction.TransactionId));
        Assert.Equal(3, first.Select(t => t.Transaction.TransactionId).Distinct().Count());
        Assert.NotEqual(first[0].Transaction.TransactionId, otherAccount[0].Transaction.TransactionId);
        Assert.Null(first[0].Tax);
        Assert.Equal(new TaxTag(TaxCategories.Medical, TaxTagStatus.Suggested), first[2].Tax);
    }

    [Theory]
    [InlineData("WAL-MART SUPERCENTER#1234 KINGSTON", "Wal-Mart Supercenter #1235 Kingston", true)]
    [InlineData("SHELL EASYPAY C10101 OTTAWA", "SHELL EASYPAY C20202 OTTAWA", true)]
    [InlineData("Café Dépôt", "CAFE DEPOT", true)]
    [InlineData("TD ATM W/D    005555", "TD ATM W/D    001234", true)]
    [InlineData("WAL-MART SUPERCENTER#1234 KINGSTON", "WAL-MART SUPERCENTER#1234 OTTAWA", true)] // town after the store number
    [InlineData("PIZZAVILLE #60", "PIZZAVILLE #69", true)]
    [InlineData("PIZZAVILLE #60 BARRIE ON", "PIZZAVILLE #69 KINGSTON ON", true)]
    [InlineData("E-TRANSFER ***XvQ", "E-TRANSFER ***rc9", true)]
    [InlineData("SEND E-TFR ***rc9", "SEND E-TFR ***XvQ", true)]
    [InlineData("#1 SUSHI", "#2 SUSHI", true)] // nothing before the number, so the rest is the name
    [InlineData("UBER* TRIP", "UBER* EATS", false)] // one asterisk is punctuation, not a masked code
    [InlineData("SHELL EASYPAY C10101 OTTAWA", "SHELL EASYPAY C20202 KINGSTON", false)] // no # to end the name
    [InlineData("SHELL EASYPAY", "SHELL CANADA", false)]
    [InlineData("#123", "**456", false)] // nothing left to compare
    [InlineData("12345", "12345", false)] // nothing left to compare
    public void MerchantNamesIgnoreStoreNumbersAndCodes(string a, string b, bool same) =>
        Assert.Equal(same, MerchantName.Same(a, b));
}
