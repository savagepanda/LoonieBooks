using MapleLedger.Core;
using MapleLedger.Core.Budgets;
using MapleLedger.Core.Investments;
using MapleLedger.Data;
using MapleLedger.Fdx;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.UnitTests;

public sealed class BudgetTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 19);

    private static Transaction Spend(string date, decimal amount, string category = Categories.FoodAndDining, string? sub = null, string account = "chq", DebitCreditMemo direction = DebitCreditMemo.Debit) => new()
    {
        TransactionId = Guid.NewGuid().ToString("N"),
        AccountId = account,
        TransactionTimestamp = DateTimeOffset.Parse(date + "T12:00:00Z"),
        Amount = amount,
        DebitCreditMemo = direction,
        Description = "SHOP",
        Category = category,
        SubCategory = sub,
        Status = TransactionStatus.Posted,
    };

    // ---------------------------------------------------------------- basis and suggestions

    [Fact]
    public void BasisIsLastCalendarYearWhenTheLedgerCoversIt()
    {
        var basis = BudgetCalculator.Basis(2026, new DateOnly(2024, 6, 3), Today);
        Assert.Equal(new BudgetBasis(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), 12), basis);
        Assert.True(basis!.IsCalendarYear);

        // A ledger that starts on Jan 5 still covers the year: the first week counts.
        Assert.True(BudgetCalculator.Basis(2026, new DateOnly(2025, 1, 5), Today)!.IsCalendarYear);
    }

    [Fact]
    public void BasisFallsBackToTheLatestCompleteMonths()
    {
        // Started mid-May: May is partial, so June to October.
        var basis = BudgetCalculator.Basis(2026, new DateOnly(2026, 5, 14), Today);
        Assert.Equal(new BudgetBasis(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 31), 5), basis);
        Assert.False(basis!.IsCalendarYear);

        // Next year's budget, without all of this year yet: the latest 12 complete months.
        var next = BudgetCalculator.Basis(2027, new DateOnly(2024, 1, 1), Today);
        Assert.Equal(new BudgetBasis(new DateOnly(2025, 11, 1), new DateOnly(2026, 10, 31), 12), next);
        Assert.False(next!.IsCalendarYear);
    }

    [Fact]
    public void NoBasisWithoutACompleteMonth()
    {
        Assert.Null(BudgetCalculator.Basis(2026, null, Today));
        Assert.Null(BudgetCalculator.Basis(2026, new DateOnly(2026, 11, 2), Today));
        Assert.Null(BudgetCalculator.Basis(2026, new DateOnly(2026, 10, 9), Today)); // October is partial
    }

    [Theory]
    [InlineData(4321, 12, 4400)]
    [InlineData(612, 12, 650)]
    [InlineData(1000, 5, 2400)]
    [InlineData(23_100, 12, 23_500)]
    [InlineData(61_200, 12, 62_000)]
    public void SuggestionsAreAYearRoundedUp(decimal spent, int months, decimal expected) =>
        Assert.Equal(expected, BudgetCalculator.Suggest(spent, months));

    [Theory]
    [InlineData(8600, 12, 725)] // CA$716.67 a month
    [InlineData(1600, 12, 140)] // CA$133.33
    [InlineData(23_000, 12, 1950)] // CA$1,916.67
    [InlineData(13_800, 12, 1150)] // exactly CA$1,150
    [InlineData(578, 12, 50)]
    [InlineData(1000, 5, 200)]
    public void MonthlySuggestionsAreATidyMonth(decimal spent, int months, decimal expected) =>
        Assert.Equal(expected, BudgetCalculator.SuggestMonthly(spent, months));

    [Fact]
    public void ARowsSuggestionDependsOnItsKind()
    {
        var row = new BudgetRow(Categories.Travel, null, 2047, 2100, null, null, 0, SuggestedMonthly: 175);
        Assert.Equal(2100, row.SuggestionFor(yearly: true));
        Assert.Equal(2100, row.SuggestionFor(yearly: false)); // 175 a month
        Assert.Null((row with { Suggested = null, SuggestedMonthly = null }).SuggestionFor(false));
    }

    [Fact]
    public void CategoriesUnderOnePercentOfSpendingAreMinor()
    {
        var basis = new BudgetBasis(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), 12);
        BudgetRow Top(string category, decimal basisSpent, decimal? target = null, decimal? previous = null) =>
            new(category, null, basisSpent, null, target, previous, 0);
        var plan = new BudgetPlan(2026, Today, basis,
        [
            Top(Categories.Home, 9000),
            Top(Categories.FoodAndDining, 900),
            Top(Categories.Entertainment, 99), // under 1% of 10,000
            Top(Categories.Education, 0),
            Top(Categories.Insurance, 0, previous: 500), // budgeted last year: stays
            Top(Categories.Travel, 1), // a detail has a budget: stays
            new BudgetRow(Categories.Travel, "Lodging", 0, null, 300, null, 0),
        ], 0);

        Assert.Equal(new HashSet<string> { Categories.Entertainment, Categories.Education }, BudgetCalculator.MinorCategories(plan));
        Assert.Empty(BudgetCalculator.MinorCategories(plan with { Rows = [Top(Categories.Education, 0), Top(Categories.Travel, 0)] })); // no spending at all: a new ledger
    }

    [Fact]
    public void SuggestedLinesSkipBudgetedAndMinorCategories()
    {
        var basis = new BudgetBasis(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), 12);
        var plan = new BudgetPlan(2026, Today, basis,
        [
            new(Categories.Home, null, 9000, 9000, null, null, 0, SuggestedMonthly: 750),
            new(Categories.Travel, null, 900, 900, null, null, 0, SuggestYearly: true, SuggestedMonthly: 75),
            new(Categories.FoodAndDining, null, 500, 500, 600, null, 0, SuggestedMonthly: 45), // has a budget
            new(Categories.Shopping, null, 90, 100, null, null, 0, SuggestedMonthly: 10), // under 1%
            new(Categories.Education, null, 0, null, null, null, 0), // no suggestion
        ], 0);

        Assert.Equal(
            [new BudgetLineRequest(Categories.Home, null, 9000, false), new BudgetLineRequest(Categories.Travel, null, 900, true)],
            BudgetCalculator.SuggestedLines(plan));
    }

    [Fact]
    public void NoSuggestionWithoutSpending()
    {
        Assert.Null(BudgetCalculator.Suggest(0, 12));
        Assert.Null(BudgetCalculator.Suggest(-40, 12)); // more refunds than spending
    }

    // ---------------------------------------------------------------- the plan

    [Fact]
    public void PlanListsSpendingCategoriesWithTheirDetailsAndSuggestions()
    {
        var basis = BudgetCalculator.Basis(2026, new DateOnly(2025, 1, 1), Today);
        var transactions = new[]
        {
            Spend("2025-03-01", 300, sub: "Groceries"),
            Spend("2025-08-01", 200, sub: "Restaurants"),
            Spend("2025-08-02", 50, sub: "Restaurants", direction: DebitCreditMemo.Credit), // a refund
            Spend("2026-02-01", 120, sub: "Groceries"),
            Spend("2026-02-03", 999, Categories.Transfer),
            Spend("2026-02-04", 80, Categories.Uncategorized),
            Spend("2026-02-05", 3000, Categories.Income, direction: DebitCreditMemo.Credit),
        };
        var targets = new[]
        {
            new BudgetTarget(2026, Categories.FoodAndDining, null, 600),
            new BudgetTarget(2025, Categories.FoodAndDining, null, 500),
            new BudgetTarget(2026, Categories.FoodAndDining, "Restaurants", 200),
        };

        var plan = BudgetCalculator.Plan(2026, Today, basis, Categories.Tree, targets, transactions);

        Assert.DoesNotContain(plan.Rows, r => r.Category is Categories.Income or Categories.Transfer or Categories.Uncategorized);
        var food = plan.Rows.Single(r => r.Category == Categories.FoodAndDining && r.SubCategory is null);
        Assert.Equal(new BudgetRow(Categories.FoodAndDining, null, 450, 450, 600, 500, 120, SuggestYearly: true, SuggestedMonthly: 40), food);
        Assert.Equal(new BudgetRow(Categories.FoodAndDining, "Restaurants", 150, 150, 200, null, 0, SuggestYearly: true, SuggestedMonthly: 15), plan.Rows.Single(r => r.SubCategory == "Restaurants"));
        Assert.Contains(plan.Rows, r => r.SubCategory == "Groceries");
        Assert.DoesNotContain(plan.Rows, r => r.SubCategory == "Coffee Shops"); // nothing to show
        Assert.Contains(plan.Rows, r => r.Category == Categories.Travel && r.SubCategory is null); // every category is listed
        Assert.Equal(80, plan.Uncategorized);
        Assert.Equal(Today, plan.AsOf);

        // The detail's budget is inside its category's, so it isn't counted twice.
        Assert.Equal(600, plan.Total);
        Assert.Equal(500, plan.PreviousTotal);
    }

    [Fact]
    public void ACategoryBudgetedByItsDetailsTotalsThem() =>
        Assert.Equal(350, BudgetCalculator.Total(
        [
            (Categories.FoodAndDining, "Groceries", 300m),
            (Categories.FoodAndDining, "Restaurants", 50m),
            (Categories.Travel, null, null),
        ]));

    [Fact]
    public void SpendingInAFewMonthsIsSuggestedAYearlyBudget()
    {
        var basis = BudgetCalculator.Basis(2026, new DateOnly(2025, 1, 1), Today);
        var transactions = new List<Transaction>
        {
            Spend("2025-01-15", 2500, Categories.Education, "Tuition"),
            Spend("2025-09-15", 2500, Categories.Education, "Tuition"),
            Spend("2025-03-15", 900, Categories.Taxes, "Income Tax"),
            Spend("2025-06-15", 900, Categories.Taxes, "Income Tax"),
            Spend("2025-09-15", 900, Categories.Taxes, "Income Tax"),
            Spend("2025-12-15", 900, Categories.Taxes, "Income Tax"),
        };
        for (var month = 1; month <= 12; month++) transactions.Add(Spend($"2025-{month:D2}-03", 400, sub: "Groceries"));
        for (var month = 1; month <= 5; month++) transactions.Add(Spend($"2025-{month:D2}-04", 60, Categories.Shopping));
        var targets = new[] { new BudgetTarget(2025, Categories.Taxes, null, 3600, Yearly: true) };

        var plan = BudgetCalculator.Plan(2026, Today, basis, Categories.Tree, targets, transactions);
        BudgetRow Row(string category) => plan.Rows.Single(r => r.Category == category && r.SubCategory is null);

        Assert.True(Row(Categories.Education).SuggestYearly); // 2 months of 12
        Assert.True(Row(Categories.Taxes).SuggestYearly); // 4 of 12
        Assert.True(Row(Categories.Taxes).PreviousYearly);
        Assert.False(Row(Categories.Taxes).Yearly); // no budget this year yet
        Assert.False(Row(Categories.FoodAndDining).SuggestYearly); // every month
        Assert.False(Row(Categories.Shopping).SuggestYearly); // 5 of 12
        Assert.False(Row(Categories.Travel).SuggestYearly); // no spending at all
    }

    [Theory]
    [InlineData(4, 12, true)]
    [InlineData(5, 12, false)]
    [InlineData(0, 12, false)]
    [InlineData(1, 5, false)] // too few months to tell
    [InlineData(2, 6, true)]
    public void IrregularSpendingIsAThirdOfTheMonthsOrFewer(int withSpending, int months, bool irregular) =>
        Assert.Equal(irregular, BudgetCalculator.IsIrregular(withSpending, months));

    // ---------------------------------------------------------------- months

    [Fact]
    public void MonthsShareEachYearsBudgetAndCountItsSpending()
    {
        var transactions = new[]
        {
            Spend("2026-10-02", 40, sub: "Restaurants"),
            Spend("2026-10-03", 70, sub: "Groceries"),
            Spend("2026-10-04", 90, Categories.Shopping),
            Spend("2026-10-05", 25, Categories.Uncategorized),
            Spend("2026-11-01", 30, Categories.Home, "Rent"),
            Spend("2026-11-02", 15, Categories.Home, "Furnishings"),
        };
        var targets = new[]
        {
            new BudgetTarget(2026, Categories.FoodAndDining, null, 1200),
            new BudgetTarget(2026, Categories.FoodAndDining, "Restaurants", 240),
            new BudgetTarget(2026, Categories.Home, "Rent", 24_000),
            new BudgetTarget(2025, Categories.Travel, null, 3000),
        };

        var months = BudgetCalculator.Months(2025, 2026, Today, targets, transactions);

        Assert.Equal(23, months.Count); // Jan 2025 to Nov 2026: no months after today
        Assert.Equal(250, months[0].Budget); // 2025: Travel only
        var october = months.Single(m => m is { Year: 2026, Month: 10 });
        Assert.Equal(100 + 2000, october.Budget); // Food, and Rent; Restaurants is inside Food
        Assert.Equal(110, october.Spent);
        Assert.True(october.Lines.Single(l => l.SubCategory == "Restaurants").Nested);
        Assert.Equal(40, october.Lines.Single(l => l.SubCategory == "Restaurants").Spent);
        Assert.Equal(90, october.Unbudgeted);
        Assert.Equal(25, october.Uncategorized);

        Assert.False(october.Lines.Single(l => l.SubCategory is null).Yearly);
        var november = months[^1];
        Assert.Equal(30, november.Lines.Single(l => l.SubCategory == "Rent").Spent);
        Assert.Equal(15, november.Unbudgeted); // Home is budgeted only by Rent
    }

    // ---------------------------------------------------------------- monthly exchange rates

    [Fact]
    public void ForeignSpendingIsConvertedAtItsMonthsRate()
    {
        var rates = new MonthlyCurrencyRates(
            new CurrencyRates([new ExchangeRate("USD", 1.40m, Today)]),
            [new MonthlyExchangeRate("USD", new DateOnly(2026, 3, 1), 1.30m)]);
        var transactions = new[] { Spend("2026-03-15", 100, account: "usd"), Spend("2026-04-15", 100, account: "usd"), Spend("2026-03-15", 100) };

        var cad = rates.InCad(transactions, new Dictionary<string, string> { ["usd"] = "USD", ["chq"] = "CAD" });

        Assert.Equal([130m, 140m, 100m], cad.Select(t => t.Amount));
        Assert.Same(transactions, MonthlyCurrencyRates.None.InCad(transactions, new Dictionary<string, string> { ["chq"] = "CAD" }));
        Assert.Equal(55m, MonthlyCurrencyRates.None.ToCad(55m, "EUR", Today)); // no rate at all: at face value
    }

    [Fact]
    public void ReadsTheBankOfCanadasMonthlySeries()
    {
        const string json = """
            {"observations":[
              {"d":"2026-09-01","FXMUSDCAD":{"v":"1.3802"}},
              {"d":"2026-10-01","FXMUSDCAD":{"v":"1.3778"}},
              {"d":"2026-11-01","FXMEURCAD":{"v":"1.6"}},
              {"d":"bad","FXMUSDCAD":{"v":"1.1"}}
            ]}
            """;
        Assert.Equal(
            [new MonthlyExchangeRate("USD", new DateOnly(2026, 9, 1), 1.3802m), new MonthlyExchangeRate("USD", new DateOnly(2026, 10, 1), 1.3778m)],
            BankOfCanadaMonthlyRates.Parse(json, "usd"));
        Assert.Empty(BankOfCanadaMonthlyRates.Parse("not json", "USD"));
        Assert.Equal("https://www.bankofcanada.ca/valet/observations/FXMUSDCAD/json?start_date=2025-01-01", BankOfCanadaMonthlyRates.Url("usd", new DateOnly(2025, 1, 20)).ToString());
    }

    // ---------------------------------------------------------------- the ledger

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<LedgerDbContext> _options;
    private readonly List<LedgerDbContext> _contexts = [];

    public BudgetTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite(_connection).Options;
        using var db = new LedgerDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        foreach (var db in _contexts) db.Dispose();
        _connection.Dispose();
    }

    private LedgerService For(string owner)
    {
        var db = new LedgerDbContext(_options) { OwnerId = owner };
        _contexts.Add(db);
        return new LedgerService(db);
    }

    private static int ThisYear => DateTime.UtcNow.Year;

    private static AccountRequest Account(string name, string currency = "CAD") =>
        new(name, "Bank", AccountCategory.DepositAccount, AccountType.Checking, "1234", 0m, null, null, currency);

    private static LedgerTransaction Bought(DateOnly date, decimal amount, string category = Categories.FoodAndDining) =>
        new(Spend(date.ToString("yyyy-MM-dd"), amount, category) with { AccountId = "ignored" });

    [Fact]
    public async Task SetsAndRemovesBudgetLinesPerOwner()
    {
        var alice = For("alice");
        var set = await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest(
        [
            new BudgetLineRequest(Categories.FoodAndDining, null, 6000.004m),
            new BudgetLineRequest(Categories.FoodAndDining, "Restaurants", 1200),
            new BudgetLineRequest(Categories.Travel, null, 0), // nothing to remove
        ]));
        Assert.Equal([new BudgetTarget(ThisYear, Categories.FoodAndDining, null, 6000m), new BudgetTarget(ThisYear, Categories.FoodAndDining, "Restaurants", 1200m)], set);

        set = await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.FoodAndDining, "Restaurants", null), new BudgetLineRequest(Categories.FoodAndDining, null, 6500)]));
        Assert.Equal([new BudgetTarget(ThisYear, Categories.FoodAndDining, null, 6500m)], set);
        Assert.Empty(await For("bob").GetBudgetTargetsAsync());
    }

    [Fact]
    public async Task AYearlyBudgetStaysYearlyUntilChanged()
    {
        var alice = For("alice");
        await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.Education, null, 5000, Yearly: true)]));
        var set = await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.Education, null, 5500)])); // no Yearly: kept
        Assert.Equal([new BudgetTarget(ThisYear, Categories.Education, null, 5500m, Yearly: true)], set);

        set = await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.Education, null, 5500, Yearly: false)]));
        Assert.False(set.Single().Yearly);

        await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.Taxes, null, 4000, Yearly: true)]));
        var months = await alice.GetBudgetMonthsAsync(ThisYear, ThisYear);
        Assert.True(months[0].Lines.Single(l => l.Category == Categories.Taxes).Yearly);

        var bob = For("bob");
        await bob.RestoreAsync(await alice.ExportAsync());
        Assert.True((await bob.GetBudgetTargetsAsync()).Single(t => t.Category == Categories.Taxes).Yearly);
    }

    [Theory]
    [InlineData(Categories.Income, null, 100)]
    [InlineData(Categories.Transfer, null, 100)]
    [InlineData(Categories.Uncategorized, null, 100)]
    [InlineData("Not a category", null, 100)]
    [InlineData(Categories.FoodAndDining, "Not a detail", 100)]
    [InlineData(Categories.FoodAndDining, null, -5)]
    public async Task RefusesBudgetsThatCantBe(string category, string? sub, decimal amount) =>
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            For("alice").SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(category, sub, amount)])));

    [Fact]
    public async Task RefusesYearsOutOfRange() =>
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").GetBudgetPlanAsync(ThisYear + 2));

    [Fact]
    public async Task PlanSuggestsFromLastYearInCanadianDollars()
    {
        var alice = For("alice");
        var chequing = await alice.CreateAccountAsync(Account("Chequing"));
        var card = await alice.CreateAccountAsync(Account("US card", "USD"));
        await alice.ImportAsync(chequing.AccountId, new([Bought(new DateOnly(ThisYear - 1, 1, 2), 1000), Bought(new DateOnly(ThisYear - 1, 6, 2), 1000)]));
        await alice.ImportAsync(card.AccountId, new([Bought(new DateOnly(ThisYear - 1, 3, 2), 100)]));
        await alice.SetExchangeRateAsync(new ExchangeRate("USD", 1.40m, DateOnly.FromDateTime(DateTime.UtcNow)));
        await alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("USD", new DateOnly(ThisYear - 1, 3, 1), 1.25m)]));

        var plan = await alice.GetBudgetPlanAsync(ThisYear);

        Assert.True(plan.Basis!.IsCalendarYear);
        var food = plan.Rows.Single(r => r.Category == Categories.FoodAndDining && r.SubCategory is null);
        Assert.Equal(2125m, food.BasisSpent); // the US$100 at March's average, not today's rate
        Assert.Equal(2200m, food.Suggested);

        // The dashboard adds it up the same way.
        var dashboard = await alice.GetDashboardAsync(new DateOnly(ThisYear - 1, 1, 1), new DateOnly(ThisYear - 1, 12, 31));
        Assert.Equal(2125m, dashboard.Expenses);
        Assert.Equal(100m, dashboard.RecentTransactions.Single(t => t.Transaction.AccountId == card.AccountId).Transaction.Amount); // listed as recorded
    }

    [Fact]
    public async Task MonthlyRatesAreSavedOncePerMonthAndChecked()
    {
        var alice = For("alice");
        var month = new DateOnly(ThisYear - 1, 5, 1);
        Assert.Equal(1, (await alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("usd", month, 1.3m)]))).Rates);
        Assert.Equal(0, (await alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("USD", month.AddDays(9), 1.3m)]))).Rates);
        Assert.Equal(1, (await alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("USD", month, 1.31m)]))).Rates);
        Assert.Equal([new MonthlyExchangeRate("USD", month, 1.31m)], await alice.GetMonthlyExchangeRatesAsync());

        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("CAD", month, 1m)])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("USD", new DateOnly(ThisYear + 1, 1, 1), 1.3m)])));
    }

    [Fact]
    public async Task BudgetsTravelInBackupsAndAreClearedOnStartOver()
    {
        var alice = For("alice");
        await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.Travel, null, 3000)]));
        await alice.SetBudgetAsync(ThisYear - 1, new SetBudgetRequest([new BudgetLineRequest(Categories.Travel, "Lodging", 900)]));
        var backup = await alice.ExportAsync();

        var bob = For("bob");
        await bob.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.Travel, null, 5000)]));
        await bob.RestoreAsync(backup);
        await bob.RestoreAsync(backup);
        Assert.Equal(
            [new BudgetTarget(ThisYear - 1, Categories.Travel, "Lodging", 900m), new BudgetTarget(ThisYear, Categories.Travel, null, 5000m)], // bob's own budget is kept
            await bob.GetBudgetTargetsAsync());

        await alice.ApplyMonthlyExchangeRatesAsync(new([new MonthlyExchangeRate("USD", new DateOnly(ThisYear - 1, 5, 1), 1.3m)]));
        await alice.ClearAsync();
        Assert.Empty(await alice.GetBudgetTargetsAsync());
        Assert.Empty(await alice.GetMonthlyExchangeRatesAsync());
        Assert.Equal(2, (await bob.GetBudgetTargetsAsync()).Count);
    }

    [Fact]
    public async Task MonthsCoverTheYearsUpToNow()
    {
        var alice = For("alice");
        var chequing = await alice.CreateAccountAsync(Account("Chequing"));
        await alice.ImportAsync(chequing.AccountId, new([Bought(new DateOnly(ThisYear, 1, 10), 50)]));
        await alice.SetBudgetAsync(ThisYear, new SetBudgetRequest([new BudgetLineRequest(Categories.FoodAndDining, null, 1200)]));

        var months = await alice.GetBudgetMonthsAsync(ThisYear - 1, ThisYear);

        Assert.Equal(12 + DateTime.UtcNow.Month, months.Count);
        Assert.Equal(new BudgetLineAmount(Categories.FoodAndDining, null, 100, 50), months[12].Lines.Single());
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.GetBudgetMonthsAsync(ThisYear, ThisYear - 1));
    }
}
