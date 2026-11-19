using MapleLedger.Core;
using MapleLedger.Core.RealEstate;
using MapleLedger.Data;
using MapleLedger.Fdx;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.UnitTests;

public sealed class RealEstateTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<LedgerDbContext> _options;
    private readonly List<LedgerDbContext> _contexts = [];

    public RealEstateTests()
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

    private static AccountRequest Loan(string name, decimal owed, AccountType type = AccountType.Mortgage) =>
        new(name, "TD Canada Trust", type == AccountType.HomeLineOfCredit ? AccountCategory.LocAccount : AccountCategory.LoanAccount, type, null, owed, null, null);

    private static readonly DateOnly Bought = new(2019, 3, 15);

    // ---------------------------------------------------------------- valuation

    [Fact]
    public void AValueIsCarriedForwardByItsAreasIndex()
    {
        var home = new RealEstateProperty("p", "12 Elm St", "Oakville", 800_000m, Bought, []);
        HpiReading[] readings =
        [
            new("oakville", new DateOnly(2019, 3, 1), 280m),
            new("Oakville", new DateOnly(2026, 10, 1), 350m),
            new("Toronto", new DateOnly(2026, 10, 1), 999m),
        ];

        var estimate = PropertyValuation.Estimate(home, readings)!;

        Assert.Equal(1_000_000m, estimate.Value);
        Assert.Equal(new DateOnly(2026, 10, 1), estimate.AsOf);
        Assert.True(estimate.ByIndex);
        Assert.Equal(0.25m, estimate.IndexChange);
    }

    [Fact]
    public void WithoutAReadingAtOrBeforeTheValuesMonthTheValueIsAsEntered()
    {
        var home = new RealEstateProperty("p", "12 Elm St", "Oakville", 800_000m, Bought, []);

        var estimate = PropertyValuation.Estimate(home, [new HpiReading("Oakville", new DateOnly(2026, 10, 1), 350m)])!;

        Assert.Equal(new PropertyEstimate(800_000m, Bought), estimate);
        Assert.Null(PropertyValuation.Estimate(home with { Value = null }, []));
    }

    [Fact]
    public void EquityIsTheValueLessTheOpenLoansSecuredByIt()
    {
        var term = new Account { AccountId = "term", AccountCategory = AccountCategory.LoanAccount, AccountType = AccountType.Mortgage, CurrentBalance = 591_114.75m };
        var line = new Account { AccountId = "line", AccountCategory = AccountCategory.LocAccount, AccountType = AccountType.HomeLineOfCredit, CurrentBalance = -324.87m };
        var closed = new Account { AccountId = "old", AccountCategory = AccountCategory.LoanAccount, AccountType = AccountType.Mortgage, CurrentBalance = 5_000m, Status = AccountStatus.Closed };
        var home = new RealEstateProperty("p", "12 Elm St", null, 900_000m, Bought, ["term", "line", "old"]);

        var equity = Assert.Single(PropertyValuation.Equities([home], [], [term, line, closed]));

        Assert.Equal(590_789.88m, equity.Owed);
        Assert.Equal(309_210.12m, equity.Equity);
        Assert.Equal(2, equity.Loans.Count);
    }

    // ---------------------------------------------------------------- ledger

    [Fact]
    public async Task APropertyKeepsItsLoansAndALoanBelongsToOnePropertyAtATime()
    {
        var ledger = For("alice");
        var term = await ledger.CreateAccountAsync(Loan("FlexLine term", 591_114.75m));
        var line = await ledger.CreateAccountAsync(Loan("FlexLine revolving", 0m, AccountType.HomeLineOfCredit));

        var first = await ledger.CreatePropertyAsync(new PropertyRequest("  12 Elm   St ", "Oakville", 800_000m, Bought, [term.AccountId, line.AccountId]));
        Assert.Equal("12 Elm St", first.Name);
        Assert.Equal(new[] { line.AccountId, term.AccountId }.Order(StringComparer.Ordinal), first.AccountIds.Order(StringComparer.Ordinal));

        var second = await ledger.CreatePropertyAsync(new PropertyRequest("Cottage", null, null, null, [line.AccountId]));
        var properties = await ledger.GetPropertiesAsync();
        Assert.Equal([term.AccountId], properties.Single(p => p.Id == first.Id).AccountIds);
        Assert.Equal([line.AccountId], properties.Single(p => p.Id == second.Id).AccountIds);

        Assert.True(await ledger.DeletePropertyAsync(second.Id));
        Assert.Equal([first.Id], (await ledger.GetPropertiesAsync()).Select(p => p.Id));
        Assert.Empty(await For("bob").GetPropertiesAsync());
    }

    [Fact]
    public async Task OnlyLoansCanBeSecuredByAProperty()
    {
        var ledger = For("alice");
        var chequing = await ledger.CreateAccountAsync(new("Chequing", null, AccountCategory.DepositAccount, AccountType.Checking, null, 100m, null, null));

        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.CreatePropertyAsync(new PropertyRequest("Home", null, null, null, [chequing.AccountId])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.CreatePropertyAsync(new PropertyRequest(" ", null, null, null, null)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.CreatePropertyAsync(new PropertyRequest("Home", null, -1m, null, null)));
    }

    [Fact]
    public async Task AnAreasReadingForAMonthIsReplacedAndKeepsTheAreasSpelling()
    {
        var ledger = For("alice");
        await ledger.SetHpiReadingAsync(new HpiReading("Oakville", new DateOnly(2026, 10, 1), 340m));
        var replaced = await ledger.SetHpiReadingAsync(new HpiReading(" oakville ", new DateOnly(2026, 10, 20), 350.5m));

        Assert.Equal(new HpiReading("Oakville", new DateOnly(2026, 10, 1), 350.5m), replaced);
        Assert.Equal([replaced], await ledger.GetHpiReadingsAsync());
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.SetHpiReadingAsync(new HpiReading("Oakville", new DateOnly(2026, 10, 1), 0m)));

        Assert.True(await ledger.DeleteHpiReadingAsync("OAKVILLE", new DateOnly(2026, 10, 1)));
        Assert.Empty(await ledger.GetHpiReadingsAsync());
    }

    [Fact]
    public void AValueIsCarriedBackToAMonthBeforeItsDate()
    {
        var home = new RealEstateProperty("p", "12 Elm St", "Oakville", 800_000m, new DateOnly(2024, 6, 15), []);
        HpiReading[] readings =
        [
            new("Oakville", new DateOnly(2020, 1, 1), 300m),
            new("Oakville", new DateOnly(2024, 6, 1), 400m),
            new("Oakville", new DateOnly(2025, 12, 1), 380m),
        ];

        // Back to January 2020 (and on through 2020, until the next reading), forward to the latest.
        Assert.Equal(600_000m, PropertyValuation.EstimateOn(home, readings, new DateOnly(2020, 7, 31))!.Value);
        Assert.Equal(800_000m, PropertyValuation.EstimateOn(home, readings, new DateOnly(2024, 6, 30))!.Value);
        Assert.Equal(760_000m, PropertyValuation.EstimateOn(home, readings, new DateOnly(2026, 11, 18))!.Value);
        Assert.Equal(760_000m, PropertyValuation.Estimate(home, readings)!.Value);
        // Before the first reading there's nothing to carry it by.
        var before = PropertyValuation.EstimateOn(home, readings, new DateOnly(2019, 12, 31))!;
        Assert.Equal((800_000m, false), (before.Value, before.ByIndex));
        Assert.Equal(1_520_000m, PropertyValuation.TotalValueOn([home, home with { Id = "q" }], readings, new DateOnly(2025, 12, 1)));
    }

    [Fact]
    public async Task NetWorthCountsThePropertiesAtTheirIndexedValue()
    {
        var ledger = For("alice");
        var mortgage = await ledger.CreateAccountAsync(Loan("Mortgage", 600_000m));
        await ledger.CreatePropertyAsync(new PropertyRequest("12 Elm St", "Oakville", 800_000m, Bought, [mortgage.AccountId]));
        await ledger.SetHpiReadingAsync(new HpiReading("Oakville", new DateOnly(2019, 3, 1), 280m));
        await ledger.SetHpiReadingAsync(new HpiReading("Oakville", new DateOnly(2026, 10, 1), 350m));

        var all = (await ledger.GetDashboardAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31))).NetWorth;
        Assert.Equal((0m, 1_000_000m, 600_000m, 400_000m), (all.Assets, all.RealEstate, all.Liabilities, all.NetWorth));

        var chequing = await ledger.CreateAccountAsync(new("Chequing", null, AccountCategory.DepositAccount, AccountType.Checking, null, 100m, null, null));
        var filtered = (await ledger.GetDashboardAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), accountIds: [chequing.AccountId])).NetWorth;
        Assert.Equal(0m, filtered.RealEstate);
    }

    [Fact]
    public async Task NetWorthByMonthFollowsTheIndexMonthByMonth()
    {
        var ledger = For("alice");
        await ledger.CreatePropertyAsync(new PropertyRequest("12 Elm St", "Oakville", 800_000m, new DateOnly(2026, 8, 10), []));
        await ledger.ApplyHpiAsync(new ApplyHpiRequest([new HpiSeries("Oakville", new DateOnly(2026, 7, 1), [390m, 400m, null, 410m])]));

        var months = await ledger.GetNetWorthByMonthAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 11, 18));

        // July is carried back from August's value; September has no reading, so August's stands; October and on use October's.
        Assert.Equal([780_000m, 800_000m, 800_000m, 820_000m, 820_000m], months.Select(m => m.RealEstate));
        var tile = (await ledger.GetDashboardAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 11, 18))).NetWorth;
        Assert.Equal(tile.RealEstate, months[^1].RealEstate);
    }

    [Fact]
    public async Task TheFeedsSeriesReplacesAnAreasReadingsAndLeavesOtherAreasAlone()
    {
        var ledger = For("alice");
        // Typed in before the feed: a benchmark price, which mustn't be mixed with the index.
        await ledger.SetHpiReadingAsync(new HpiReading("oakville", new DateOnly(2019, 3, 1), 950_000m));
        await ledger.SetHpiReadingAsync(new HpiReading("oakville", new DateOnly(2004, 1, 1), 600_000m));
        await ledger.SetHpiReadingAsync(new HpiReading("Ottawa", new DateOnly(2019, 3, 1), 250m));

        var series = new HpiSeries(" Oakville ", new DateOnly(2019, 2, 1), [279m, 280m, null, 282.5m]);
        var first = await ledger.ApplyHpiAsync(new ApplyHpiRequest([series]));

        Assert.Equal(new ApplyHpiResult(1, 4), first); // 2019-03 changed, 2004-01 removed, 2019-02 and 2019-05 added
        var readings = await ledger.GetHpiReadingsAsync();
        Assert.Equal(
            [
                new HpiReading("oakville", new DateOnly(2019, 2, 1), 279m),
                new HpiReading("oakville", new DateOnly(2019, 3, 1), 280m),
                new HpiReading("oakville", new DateOnly(2019, 5, 1), 282.5m),
                new HpiReading("Ottawa", new DateOnly(2019, 3, 1), 250m),
            ],
            readings);

        // The same series again changes nothing, and nothing is saved.
        Assert.Equal(new ApplyHpiResult(0, 0), await ledger.ApplyHpiAsync(new ApplyHpiRequest([series])));
        // Another owner's ledger is untouched.
        Assert.Empty(await For("bob").GetHpiReadingsAsync());
    }

    [Fact]
    public async Task ASeriesThatCantBeRightIsRefused()
    {
        var ledger = For("alice");
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(2);
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.ApplyHpiAsync(new ApplyHpiRequest([new HpiSeries("Oakville", future, [300m])])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.ApplyHpiAsync(new ApplyHpiRequest([new HpiSeries("Oakville", new DateOnly(2020, 1, 1), [-1m])])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.ApplyHpiAsync(new ApplyHpiRequest([new HpiSeries("", new DateOnly(2020, 1, 1), [300m])])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.ApplyHpiAsync(new ApplyHpiRequest(
            [new HpiSeries("Oakville", new DateOnly(2020, 1, 1), [300m]), new HpiSeries("OAKVILLE", new DateOnly(2020, 1, 1), [300m])])));
        Assert.Empty(await ledger.GetHpiReadingsAsync());
    }

    [Fact]
    public async Task AFeedsWorthOfReadingsIsRestoredInOneGo()
    {
        var alice = For("alice");
        var index = Enumerable.Range(0, 252).Select(i => (decimal?)(100m + i)).ToList();
        await alice.ApplyHpiAsync(new ApplyHpiRequest([new HpiSeries("Oakville", new DateOnly(2005, 1, 1), index), new HpiSeries("Ottawa", new DateOnly(2005, 1, 1), index)]));
        var backup = await alice.ExportAsync();

        var bob = For("bob");
        await bob.RestoreAsync(backup);
        await bob.RestoreAsync(backup);

        var readings = await bob.GetHpiReadingsAsync();
        Assert.Equal(504, readings.Count);
        Assert.Equal(new HpiReading("Oakville", new DateOnly(2025, 12, 1), 351m), readings.Single(r => r.Area == "Oakville" && r.Month == new DateOnly(2025, 12, 1)));
    }

    [Fact]
    public async Task PropertiesAndReadingsTravelInBackupsAndStartOverClearsThem()
    {
        var alice = For("alice");
        var mortgage = await alice.CreateAccountAsync(Loan("Mortgage", 600_000m));
        await alice.CreatePropertyAsync(new PropertyRequest("12 Elm St", "Oakville", 800_000m, Bought, [mortgage.AccountId]));
        await alice.SetHpiReadingAsync(new HpiReading("Oakville", new DateOnly(2019, 3, 1), 280m));
        var backup = await alice.ExportAsync();

        var bob = For("bob");
        await bob.RestoreAsync(backup);
        await bob.RestoreAsync(backup); // twice adds nothing

        var restored = Assert.Single(await bob.GetPropertiesAsync());
        Assert.Equal(("12 Elm St", "Oakville", 800_000m, (DateOnly?)Bought), (restored.Name, restored.City, restored.Value!.Value, restored.ValueAsOf));
        var bobsMortgage = Assert.Single(await bob.GetAccountsAsync());
        Assert.Equal([bobsMortgage.AccountId], restored.AccountIds);
        Assert.Single(await bob.GetHpiReadingsAsync());

        await bob.ClearAsync();
        Assert.Empty(await bob.GetPropertiesAsync());
        Assert.Empty(await bob.GetHpiReadingsAsync());
        Assert.Single(await alice.GetPropertiesAsync());
    }
}
