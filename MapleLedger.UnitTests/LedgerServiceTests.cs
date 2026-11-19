using MapleLedger.Core;
using MapleLedger.Core.Investments;
using MapleLedger.Core.Reports;
using MapleLedger.Core.Tax;
using MapleLedger.Data;
using MapleLedger.Fdx;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MapleLedger.UnitTests;

/// <summary>
/// Runs the shared ledger logic on SQLite, as the browser does, with the real browser migrations.
/// </summary>
public sealed class LedgerServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<LedgerDbContext> _options;
    private readonly List<LedgerDbContext> _contexts = [];

    public LedgerServiceTests()
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

    private LedgerService For(string? owner)
    {
        var db = new LedgerDbContext(_options) { OwnerId = owner };
        _contexts.Add(db);
        return new LedgerService(db);
    }

    private static AccountRequest Chequing(string name = "Chequing") =>
        new(name, "TD Canada Trust", AccountCategory.DepositAccount, AccountType.Checking, "1234", 1000m, null, null);

    private static LedgerTransaction Tx(string id, string date, decimal amount, DebitCreditMemo direction = DebitCreditMemo.Debit, string description = "COFFEE", string category = Categories.FoodAndDining) => new(new Transaction
    {
        TransactionId = id,
        AccountId = "ignored",
        TransactionTimestamp = DateTimeOffset.Parse(date + "T00:00:00Z"),
        Amount = amount,
        DebitCreditMemo = direction,
        Description = description,
        Category = category,
    });

    [Fact]
    public void BrowserMigrationsMatchTheModel()
    {
        using var db = new LedgerDbContext(_options);
        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --project MapleLedger.Data --output-dir Migrations/Sqlite");
    }

    [Fact]
    public void ServerMigrationsMatchTheModel()
    {
        using var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlServer("Server=unused", sql => sql.MigrationsAssembly("MapleLedger.ApiService"))
            .Options);
        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --project MapleLedger.ApiService --output-dir Data/Migrations");
    }

    [Fact]
    public async Task ImportMapsOlderCategoriesOntoTheTwoLevelTree()
    {
        var account = await For("alice").CreateAccountAsync(Chequing());
        LedgerTransaction With(LedgerTransaction t, string? sub) => t with { Transaction = t.Transaction with { SubCategory = sub } };
        await For("alice").ImportAsync(account.AccountId, new(
        [
            With(Tx("pay", "2026-09-01", 3000m, DebitCreditMemo.Credit, "PAYROLL", "Income"), "Paycheque"),
            With(Tx("biz", "2026-09-02", 900m, DebitCreditMemo.Credit, "INVOICE", "Business Income"), null),
            With(Tx("gas", "2026-09-03", 60m, description: "ESSO", category: "Gas"), null),
        ]));

        var summary = await For("alice").GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        Assert.Equal(
            [(Categories.Income, (string?)Categories.Salary), (Categories.Income, Categories.BusinessIncome)],
            summary.IncomeDetails!.Select(c => (c.Category, c.SubCategory)));
        Assert.Equal([(Categories.AutoAndTransport, (string?)Categories.Gas)], summary.SpendingDetails!.Select(c => (c.Category, c.SubCategory)));
    }

    [Fact]
    public async Task MigrationsMoveExistingRowsIntoTheTwoLevelTree()
    {
        var account = await For("alice").CreateAccountAsync(Chequing());
        await For("alice").ImportAsync(account.AccountId, new(
        [
            Tx("pay", "2026-09-01", 3000m, DebitCreditMemo.Credit), Tx("int", "2026-09-02", 4m, DebitCreditMemo.Credit),
            Tx("biz", "2026-09-02", 500m, DebitCreditMemo.Credit), Tx("gas", "2026-09-03", 60m), Tx("uber", "2026-09-04", 18m),
            Tx("lease", "2026-09-04", 489m), Tx("bus", "2026-09-05", 3m), Tx("ptax", "2026-09-06", 900m),
        ]));

        // Roll back to before income and transport were split up, and file the rows the old ways:
        // as the rules did then, and as the short-lived top-level Salary, Car Lease and Property Tax.
        using (var db = new LedgerDbContext(_options))
        {
            await db.Database.MigrateAsync("20261114133932_AddBusinessUsePercent");
            foreach (var (id, category, sub) in new (string, string, string?)[]
            {
                ("pay", "Income", "Paycheque"), ("int", "Income", "Interest"), ("biz", "Business Income", null), ("gas", "Auto & Transport", "Gas"),
                ("uber", "Auto & Transport", "Rideshare"), ("lease", "Car Lease", null), ("bus", "Auto & Transport", "Public Transit"), ("ptax", "Property Tax", null),
            })
            {
                await db.Database.ExecuteSqlAsync($"UPDATE Transactions SET Category = {category}, SubCategory = {sub} WHERE TransactionId = {id}");
            }

            await db.Database.MigrateAsync();
        }

        var recent = (await For("alice").GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))).RecentTransactions;
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["pay"] = "Income › Salary", ["int"] = "Income › Other Income", ["biz"] = "Income › Business Income",
                ["gas"] = "Auto & Transport › Gas", ["uber"] = "Auto & Transport › Taxi", ["lease"] = "Auto & Transport › Car Lease",
                ["bus"] = "Auto & Transport › Public Transit", ["ptax"] = "Taxes › Property Tax",
            },
            recent.ToDictionary(t => t.Transaction.TransactionId, t => Categories.Label(t.Transaction.Category, t.Transaction.SubCategory)));
    }

    [Fact]
    public async Task ClassifyingChecksTheSubCategoryAndSearchFiltersByIt()
    {
        var account = await For("alice").CreateAccountAsync(Chequing());
        await For("alice").ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 50m), Tx("b", "2026-09-02", 20m), Tx("c", "2026-09-03", 5m)]));

        Assert.True(await For("alice").ClassifyAsync(account.AccountId, "a", new(Categories.AutoAndTransport, Categories.Gas)));
        Assert.Equal(1, await For("alice").ClassifyManyAsync(account.AccountId, new(["b"], Categories.AutoAndTransport, Categories.Parking)));
        Assert.True(await For("alice").ClassifyAsync(account.AccountId, "c", new(Categories.AutoAndTransport, null)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ClassifyAsync(account.AccountId, "a", new(Categories.FoodAndDining, Categories.Gas)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ClassifyManyAsync(account.AccountId, new(["a"], Categories.Income, "Lottery")));

        async Task<string[]> Ids(string? sub) =>
            [.. (await For("alice").SearchTransactionsAsync(new(Category: Categories.AutoAndTransport, SubCategory: sub))).Select(t => t.Transaction.TransactionId).Order()];
        Assert.Equal(["a", "b", "c"], await Ids(null));
        Assert.Equal(["a"], await Ids(Categories.Gas));
        Assert.Equal(["c"], await Ids(Categories.NoSubCategory));

        // The dashboard narrowed to a category, e.g. to chart its spending by month.
        async Task<decimal> Spent(string? category, string? sub) =>
            (await For("alice").GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), category, sub)).Monthly.Single().Expenses;
        Assert.Equal(75m, await Spent(null, null));
        Assert.Equal(75m, await Spent(Categories.AutoAndTransport, null));
        Assert.Equal(50m, await Spent(Categories.AutoAndTransport, Categories.Gas));
        Assert.Equal(5m, await Spent(Categories.AutoAndTransport, Categories.NoSubCategory));
        Assert.Equal(0m, await Spent(Categories.FoodAndDining, null));
    }

    [Fact]
    public async Task DashboardAndSearchCanCoverSomeAccounts()
    {
        var alice = For("alice");
        var chequing = await alice.CreateAccountAsync(Chequing());
        var savings = await alice.CreateAccountAsync(Chequing("Savings") with { CurrentBalance = 5000m });
        var other = await alice.CreateAccountAsync(Chequing("Other") with { CurrentBalance = 200m });
        await alice.ImportAsync(chequing.AccountId, new([Tx("c", "2026-09-01", 10m)]));
        await alice.ImportAsync(savings.AccountId, new([Tx("s", "2026-09-02", 20m)]));
        await alice.ImportAsync(other.AccountId, new([Tx("o", "2026-09-03", 40m)]));

        var both = new[] { chequing.AccountId, savings.AccountId };
        var summary = await alice.GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), accountIds: both);
        Assert.Equal(30m, summary.Expenses);
        Assert.Equal(["s", "c"], summary.RecentTransactions.Select(t => t.Transaction.TransactionId));
        Assert.Equal(6000m, summary.NetWorth.Assets);

        Assert.Equal(70m, (await alice.GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), accountIds: [])).Expenses);
        Assert.Equal(["s", "c"], (await alice.SearchTransactionsAsync(new(AccountIds: both))).Select(t => t.Transaction.TransactionId));
        Assert.Equal(3, (await alice.SearchTransactionsAsync(new(AccountIds: []))).Count);
    }

    [Fact]
    public async Task SearchIgnoresCaseAndMatchesWildcardsLiterally()
    {
        var alice = For("alice");
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new(
        [
            Tx("gondola", "2026-08-13", 129m, description: "BANFF GONDOLA"),
            Tx("percent", "2026-08-14", 10m, description: "50% OFF SALE"),
            Tx("other", "2026-08-15", 20m, description: "LOBLAWS #1043"),
        ]));

        async Task<string[]> Ids(string search) =>
            [.. (await alice.SearchTransactionsAsync(new(Search: search))).Select(t => t.Transaction.TransactionId).Order()];
        Assert.Equal(["gondola"], await Ids("banff"));
        Assert.Equal(["gondola"], await Ids(" Gondola "));
        Assert.Equal(["percent"], await Ids("50%"));
        Assert.Empty(await Ids("_"));
    }

    [Fact]
    public async Task UncategorizedCanBeNarrowedToIncomeOrSpending()
    {
        var alice = For("alice");
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new(
        [
            Tx("in", "2026-09-01", 500m, DebitCreditMemo.Credit, "E-TRANSFER IN", Categories.Uncategorized),
            Tx("out", "2026-09-02", 80m, description: "MYSTERY SHOP", category: Categories.Uncategorized),
            Tx("food", "2026-09-03", 20m),
        ]));

        async Task<string[]> Ids(CategoryKind? kind) =>
            [.. (await alice.SearchTransactionsAsync(new(Category: Categories.Uncategorized, Kind: kind))).Select(t => t.Transaction.TransactionId).Order()];
        Assert.Equal(["in", "out"], await Ids(null));
        Assert.Equal(["in"], await Ids(CategoryKind.Income));
        Assert.Equal(["out"], await Ids(CategoryKind.Expense));

        // The chart for Uncategorized income shows only the money in.
        var month = (await alice.GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Categories.Uncategorized, kind: CategoryKind.Income)).Monthly.Single();
        Assert.Equal((500m, 0m), (month.Income, month.Expenses));

        // Other categories are income or spending by themselves, so the kind changes nothing.
        Assert.Single(await alice.SearchTransactionsAsync(new(Category: Categories.FoodAndDining, Kind: CategoryKind.Income)));
    }

    [Fact]
    public async Task UsersCanAddAndRemoveTheirOwnCategories()
    {
        var alice = For("alice");
        var pets = await alice.AddCustomCategoryAsync(new("  Pets  "));
        var vet = await alice.AddCustomCategoryAsync(new("Pets", "Vet"));
        var rental = await alice.AddCustomCategoryAsync(new(Categories.Income, "Gifts Received"));
        Assert.Equal(("Pets", (string?)null), (pets.Category, pets.SubCategory));

        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddCustomCategoryAsync(new("pets")));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddCustomCategoryAsync(new("Shopping")));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddCustomCategoryAsync(new(Categories.AutoAndTransport, "gas")));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddCustomCategoryAsync(new("Nowhere", "Detail")));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddCustomCategoryAsync(new("A|B")));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddCustomCategoryAsync(new(new string('x', CustomCategory.MaxNameLength + 1))));

        var tree = await alice.GetCategoryTreeAsync();
        Assert.Equal(["Pets", Categories.Uncategorized], tree.TakeLast(2).Select(n => n.Name));
        Assert.Equal(["Vet"], Categories.Find(tree, "Pets")!.SubCategories);
        Assert.Equal("Gifts Received", Categories.Find(tree, Categories.Income)!.SubCategories[^1]);
        Assert.Empty(await For("bob").GetCustomCategoriesAsync());

        // Transactions can be filed under them, and only by their owner.
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("v", "2026-09-01", 90m), Tx("r", "2026-09-02", 1200m, DebitCreditMemo.Credit), Tx("p", "2026-09-03", 30m)]));
        Assert.True(await alice.ClassifyAsync(account.AccountId, "v", new("Pets", "Vet")));
        Assert.True(await alice.ClassifyAsync(account.AccountId, "p", new("Pets", null)));
        Assert.True(await alice.ClassifyAsync(account.AccountId, "r", new(Categories.Income, "Gifts Received")));
        var bobAccount = await For("bob").CreateAccountAsync(Chequing());
        await For("bob").ImportAsync(bobAccount.AccountId, new([Tx("b", "2026-09-01", 5m)]));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("bob").ClassifyAsync(bobAccount.AccountId, "b", new("Pets", null)));

        var summary = await alice.GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        Assert.Equal(1200m, summary.Income);
        Assert.Contains(new CategoryTotal("Pets", 120m, 2), summary.SpendingByCategory);

        // Removing a detail keeps its transactions in the category; removing a category files them as Uncategorized.
        Assert.True(await alice.DeleteCustomCategoryAsync(rental.Id));
        Assert.True(await alice.DeleteCustomCategoryAsync(pets.Id));
        Assert.False(await alice.DeleteCustomCategoryAsync(vet.Id)); // went with Pets
        Assert.False(await For("bob").DeleteCustomCategoryAsync(pets.Id));
        Assert.Empty(await alice.GetCustomCategoriesAsync());
        var labels = (await alice.SearchTransactionsAsync(new())).ToDictionary(t => t.Transaction.TransactionId, t => Categories.Label(t.Transaction.Category, t.Transaction.SubCategory));
        Assert.Equal(Categories.Income, labels["r"]);
        Assert.Equal(Categories.Uncategorized, labels["v"]);
        Assert.Equal(Categories.Uncategorized, labels["p"]);
    }

    [Fact]
    public async Task BackupsCarryTheLedgersOwnCategories()
    {
        var alice = For("alice");
        await alice.AddCustomCategoryAsync(new("Pets"));
        await alice.AddCustomCategoryAsync(new("Pets", "Vet"));
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("v", "2026-09-01", 90m)]));
        await alice.ClassifyAsync(account.AccountId, "v", new("Pets", "Vet"));

        var backup = await alice.ExportAsync();
        Assert.Equal(2, backup.Categories!.Count);

        var bob = For("bob");
        await bob.RestoreAsync(backup);
        await bob.RestoreAsync(backup); // twice adds nothing
        Assert.Equal([("Pets", (string?)null), ("Pets", "Vet")], (await bob.GetCustomCategoriesAsync()).Select(c => (c.Category, c.SubCategory)));
        var restored = Assert.Single(await bob.SearchTransactionsAsync(new()));
        Assert.Equal("Pets › Vet", Categories.Label(restored.Transaction.Category, restored.Transaction.SubCategory));
    }

    [Fact]
    public async Task TagsGroupTransactionsAndFilterSearchesAndTheDashboard()
    {
        var alice = For("alice");
        var rental = await alice.AddTagAsync("  Condo  ");
        var cottage = await alice.AddTagAsync("Cottage");
        Assert.Equal("Condo", rental.Name);
        Assert.Equal(["Condo", "Cottage"], (await alice.GetTagsAsync()).Where(t => !DefaultTags.IsDefault(t.Id)).Select(t => t.Name));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddTagAsync("condo"));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddTagAsync(" "));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddTagAsync(new string('x', LedgerTag.MaxNameLength + 1)));
        Assert.Equal(DefaultTags.All, await For("bob").GetTagsAsync());

        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new(
        [
            Tx("rent", "2026-09-01", 2000m, DebitCreditMemo.Credit, "E-TRANSFER TENANT", Categories.Income),
            Tx("fix", "2026-09-05", 300m, description: "PLUMBER"),
            Tx("dock", "2026-09-06", 150m, description: "MARINA"),
            Tx("coffee", "2026-09-07", 5m),
        ]));
        Assert.True(await alice.SetTagsAsync(account.AccountId, "rent", [rental.Id]));
        Assert.True(await alice.SetTagsAsync(account.AccountId, "fix", [rental.Id, cottage.Id]));
        Assert.Equal(1, await alice.TagManyAsync(new([new(account.AccountId, "dock"), new(account.AccountId, "fix")], cottage.Id))); // fix has it already
        Assert.False(await alice.SetTagsAsync(account.AccountId, "missing", [rental.Id]));

        async Task<string[]> Tagged(params string[] tagIds) =>
            (await alice.SearchTransactionsAsync(new(TagIds: tagIds))).Select(t => t.Transaction.TransactionId).Order().ToArray();
        Assert.Equal(["fix", "rent"], await Tagged(rental.Id));
        Assert.Equal(["dock", "fix", "rent"], await Tagged(rental.Id, cottage.Id)); // any of them
        Assert.Equal(4, (await Tagged()).Length);
        Assert.Equal([rental.Id, cottage.Id], (await alice.SearchTransactionsAsync(new(Search: "PLUMBER"))).Single().Tags);

        // The dashboard for one tag: a rental property's own income and spending.
        var september = (From: new DateOnly(2026, 9, 1), To: new DateOnly(2026, 9, 30));
        var summary = await alice.GetDashboardAsync(september.From, september.To, tagIds: [rental.Id]);
        Assert.Equal((2000m, 300m), (summary.Income, summary.Expenses));
        Assert.Equal(["fix", "rent"], summary.RecentTransactions.Select(t => t.Transaction.TransactionId).Order());
        Assert.Equal(1000m, summary.NetWorth.NetWorth); // balances aren't tagged

        Assert.Equal([("Condo", 2), ("Cottage", 2)], (await alice.GetTagUsageAsync()).Where(u => u.Transactions > 0).Select(u => (u.Tag.Name, u.Transactions)));

        // Another owner's tags can't be used or changed.
        var bob = For("bob");
        var bobAccount = await bob.CreateAccountAsync(Chequing());
        await bob.ImportAsync(bobAccount.AccountId, new([Tx("b", "2026-09-01", 5m)]));
        await Assert.ThrowsAsync<LedgerValidationException>(() => bob.SetTagsAsync(bobAccount.AccountId, "b", [rental.Id]));
        await Assert.ThrowsAsync<LedgerValidationException>(() => bob.TagManyAsync(new([new(bobAccount.AccountId, "b")], rental.Id)));
        Assert.Null(await bob.RenameTagAsync(rental.Id, "Mine"));
        Assert.False(await bob.DeleteTagAsync(rental.Id));
        Assert.Equal(0, await bob.TagManyAsync(new([new(account.AccountId, "coffee")], (await bob.AddTagAsync("Bob's")).Id)));

        // Renaming keeps a tag on its transactions; untagging and deleting take it off.
        Assert.Equal("Rental Condo", (await alice.RenameTagAsync(rental.Id, "Rental Condo"))!.Name);
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.RenameTagAsync(rental.Id, "cottage"));
        Assert.Equal(["fix", "rent"], await Tagged(rental.Id));
        Assert.Equal(1, await alice.TagManyAsync(new([new(account.AccountId, "dock")], cottage.Id, Remove: true)));
        Assert.Null((await alice.SearchTransactionsAsync(new(Search: "MARINA"))).Single().Tags);
        Assert.True(await alice.DeleteTagAsync(cottage.Id));
        Assert.Equal([rental.Id], (await alice.SearchTransactionsAsync(new(Search: "PLUMBER"))).Single().Tags);
        Assert.Empty(await Tagged(cottage.Id));
    }

    [Fact]
    public async Task EveryLedgerHasTheBuiltInTags()
    {
        var alice = For("alice");
        var rental = DefaultTags.All.Single(t => t.Name == "Rental");
        Assert.Equal(DefaultTags.All, await alice.GetTagsAsync());
        Assert.All(DefaultTags.All, t => Assert.Equal(TagList.MaxIdLength, t.Id.Length));

        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("rent", "2026-09-01", 2000m, DebitCreditMemo.Credit, "TENANT", Categories.Income), Tx("tax", "2026-09-02", 400m)]));
        Assert.True(await alice.SetTagsAsync(account.AccountId, "rent", [rental.Id]));
        Assert.Equal(1, await alice.TagManyAsync(new([new(account.AccountId, "tax")], rental.Id)));
        Assert.Equal((2000m, 400m), await alice.GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), tagIds: [rental.Id]) is var d ? (d.Income, d.Expenses) : default);
        Assert.Equal(2, (await alice.GetTagUsageAsync()).Single(u => u.Tag.Id == rental.Id).Transactions);

        // Like built-in categories: no second one of the same name, no renaming or deleting.
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.AddTagAsync("rental"));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.RenameTagAsync(rental.Id, "Rentals"));
        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.DeleteTagAsync(rental.Id));

        // A backup keeps them: another ledger's Rental is the same tag.
        var bob = For("bob");
        await bob.RestoreAsync(await alice.ExportAsync());
        Assert.Equal(DefaultTags.All, await bob.GetTagsAsync());
        Assert.All(await bob.SearchTransactionsAsync(new()), t => Assert.Equal([rental.Id], t.Tags));

        // A tag of its own made before the built-ins existed, with a built-in's name, takes its place.
        var db = new LedgerDbContext(_options) { OwnerId = "carol" };
        _contexts.Add(db);
        db.LedgerTags.Add(new LedgerTag("tg-0123456789ab", "rental"));
        await db.SaveChangesAsync();
        var carolsTags = await For("carol").GetTagsAsync();
        Assert.Equal(DefaultTags.All.Count, carolsTags.Count);
        Assert.Equal("tg-0123456789ab", carolsTags.Single(t => t.Name.Equals("rental", StringComparison.OrdinalIgnoreCase)).Id);
    }

    [Fact]
    public async Task TagsHaveALimitPerTransaction()
    {
        var alice = For("alice");
        var tags = new List<LedgerTag>();
        for (var i = 0; i <= LedgerTag.MaxPerTransaction; i++) tags.Add(await alice.AddTagAsync($"Tag {i}"));
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m)]));

        await Assert.ThrowsAsync<LedgerValidationException>(() => alice.SetTagsAsync(account.AccountId, "a", [.. tags.Select(t => t.Id)]));
        Assert.True(await alice.SetTagsAsync(account.AccountId, "a", [.. tags.Take(LedgerTag.MaxPerTransaction - 1).Select(t => t.Id)]));
        Assert.Equal(1, await alice.TagManyAsync(new([new(account.AccountId, "a")], tags[^2].Id)));
        Assert.Equal(0, await alice.TagManyAsync(new([new(account.AccountId, "a")], tags[^1].Id))); // full
        Assert.Equal(LedgerTag.MaxPerTransaction, (await alice.SearchTransactionsAsync(new())).Single().Tags!.Count);
    }

    [Fact]
    public async Task BackupsAndMergedDuplicatesKeepTags()
    {
        var alice = For("alice");
        var rental = await alice.AddTagAsync("Condo");
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 90m), Tx("b", "2026-09-01", 90m)]));
        await alice.SetTagsAsync(account.AccountId, "b", [rental.Id]);

        // Merging a duplicate brings its tags onto the one kept.
        Assert.True(await alice.MergeDuplicateAsync(account.AccountId, new("a", "b")));
        Assert.Equal([rental.Id], (await alice.SearchTransactionsAsync(new())).Single().Tags);

        var backup = await alice.ExportAsync();
        Assert.Contains("Condo", backup.Tags!.Select(t => t.Name));

        // Restored into a ledger that already has a "condo" tag: the transactions join it.
        var bob = For("bob");
        var bobsRental = await bob.AddTagAsync("condo");
        await bob.RestoreAsync(backup);
        await bob.RestoreAsync(backup); // twice adds nothing
        Assert.Equal([bobsRental], (await bob.GetTagsAsync()).Where(t => !DefaultTags.IsDefault(t.Id)));
        Assert.Equal([bobsRental.Id], (await bob.SearchTransactionsAsync(new())).Single().Tags);

        // Into an empty ledger: the tag is created, under a new id.
        var carol = For("carol");
        await carol.RestoreAsync(backup);
        var carolsRental = Assert.Single(await carol.GetTagsAsync(), t => !DefaultTags.IsDefault(t.Id));
        Assert.NotEqual(rental.Id, carolsRental.Id);
        Assert.Equal([carolsRental.Id], (await carol.SearchTransactionsAsync(new())).Single().Tags);

        // Starting over removes the tags too.
        await carol.ClearAsync();
        Assert.Equal(DefaultTags.All, await carol.GetTagsAsync());
    }

    [Fact]
    public async Task ImportSkipsDuplicatesAndKeepsTheNewestBalance()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing() with { CurrentBalance = null });

        var first = await ledger.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m), Tx("b", "2026-09-02", 6m)], 900m, DateTimeOffset.Parse("2026-09-02T00:00:00Z")));
        var second = await For("alice").ImportAsync(account.AccountId, new([Tx("b", "2026-09-02", 6m), Tx("c", "2026-09-03", 7m)], 800m, DateTimeOffset.Parse("2026-08-01T00:00:00Z")));

        Assert.Equal(new ImportTransactionsResult(first!.BatchId, 2, 2, 0), first);
        Assert.Equal((2, 1, 1), (second!.Received, second.Added, second.Duplicates));
        Assert.Equal(900m, (await For("alice").FindAccountAsync(account.AccountId))!.CurrentBalance); // older balance ignored
    }

    [Fact]
    public async Task UndoImportRemovesOnlyWhatThatImportAdded()
    {
        var account = await For("alice").CreateAccountAsync(Chequing());
        var other = await For("alice").CreateAccountAsync(Chequing("Savings"));
        var first = await For("alice").ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m), Tx("b", "2026-09-02", 6m)]));
        var second = await For("alice").ImportAsync(account.AccountId, new([Tx("b", "2026-09-02", 6m), Tx("c", "2026-09-03", 7m), Tx("d", "2026-09-04", 8m)], Source: "PDF"));
        await For("alice").ImportAsync(other.AccountId, new([Tx("c", "2026-09-03", 7m)]));
        Assert.NotNull(await For("alice").AddReceiptAsync(account.AccountId, "c", new("c.pdf", "application/pdf", Pdf)));

        var imports = await For("alice").GetImportsAsync(account.AccountId);
        // Both imports can share a millisecond here, so their order isn't checked.
        Assert.Equal(new[] { first!.BatchId, second!.BatchId }.Order(), imports.Select(i => i.Id).Order());
        var pdf = imports.Single(i => i.Id == second.BatchId);
        Assert.Equal(("PDF", 2, 1, 2), (pdf.Source, pdf.Added, pdf.Duplicates, pdf.Remaining));

        // Someone else can't undo it.
        Assert.Null(await For("bob").UndoImportAsync(second.BatchId));
        Assert.Empty(await For("bob").GetImportsAsync(account.AccountId));

        Assert.Equal(new UndoImportResult(2), await For("alice").UndoImportAsync(second.BatchId));

        // "b" came from the first import and stays; the other account's "c" is untouched; the receipt went with its transaction.
        var left = await For("alice").SearchTransactionsAsync(new TransactionFilter());
        Assert.Equal(["a", "b", "c"], left.Select(t => $"{t.Transaction.TransactionId}").Order());
        Assert.Equal(other.AccountId, left.Single(t => t.Transaction.TransactionId == "c").Transaction.AccountId);
        Assert.Empty(await For("alice").GetReceiptsAsync(account.AccountId, "c"));
        Assert.Equal([first.BatchId], (await For("alice").GetImportsAsync(account.AccountId)).Select(i => i.Id));
        Assert.Null(await For("alice").UndoImportAsync(second.BatchId));

        // Undone transactions can be imported again, e.g. into the right account.
        var again = await For("alice").ImportAsync(account.AccountId, new([Tx("c", "2026-09-03", 7m), Tx("d", "2026-09-04", 8m)]));
        Assert.Equal(2, again!.Added);
    }

    [Fact]
    public async Task SimilarTransactionsAreFromTheSameMerchantInTheSameAccount()
    {
        var account = await For("alice").CreateAccountAsync(Chequing());
        var other = await For("alice").CreateAccountAsync(Chequing("Visa"));
        await For("alice").ImportAsync(account.AccountId, new(
        [
            Tx("w1", "2026-09-01", 15m, description: "WAL-MART SUPERCENTER#1234 KINGSTON", category: Categories.Uncategorized),
            Tx("w2", "2026-09-10", 29m, description: "WAL-MART SUPERCENTER#1235 KINGSTON", category: Categories.Uncategorized),
            Tx("w3", "2026-09-20", 17m, description: "Wal-Mart Supercenter #1234 Kingston", category: Categories.Shopping),
            Tx("c1", "2026-09-05", 4m, description: "COFFEE SHOP"),
        ]));
        await For("alice").ImportAsync(other.AccountId, new([Tx("w9", "2026-09-02", 9m, description: "WAL-MART SUPERCENTER#1234 KINGSTON", category: Categories.Shopping)]));

        var similar = await For("alice").FindSimilarAsync(account.AccountId, "w1");
        Assert.Equal(["w3", "w2"], similar!.Select(s => s.Transaction.TransactionId)); // newest first, not w1 itself, not the Visa's
        Assert.Null(await For("alice").FindSimilarAsync(account.AccountId, "missing"));
        Assert.Null(await For("bob").FindSimilarAsync(account.AccountId, "w1"));

        Assert.Equal(0, await For("bob").ClassifyManyAsync(account.AccountId, new(["w2", "w3"], Categories.FoodAndDining)));
        Assert.Equal(2, await For("alice").ClassifyManyAsync(account.AccountId, new(["w2", "w3", "w9"], Categories.FoodAndDining)));

        var all = await For("alice").SearchTransactionsAsync(new TransactionFilter());
        Assert.Equal(Categories.FoodAndDining, all.Single(t => t.Transaction.TransactionId == "w2").Transaction.Category);
        Assert.True(all.Single(t => t.Transaction.TransactionId == "w3").CategoryIsManual);
        Assert.Equal(Categories.Uncategorized, all.Single(t => t.Transaction.TransactionId == "w1").Transaction.Category);
        Assert.Equal(Categories.Shopping, all.Single(t => t.Transaction.TransactionId == "w9").Transaction.Category); // other account untouched

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ClassifyManyAsync(account.AccountId, new(["w2"], "Not a category")));
    }

    [Fact]
    public async Task FindsMergesAndKeepsDuplicatesFromTwoImports()
    {
        var account = await For("alice").CreateAccountAsync(Chequing("Visa"));
        // A PDF statement first, then the same month's CSV, which prints the description without the town.
        await For("alice").ImportAsync(account.AccountId, new(
        [
            Tx("pdf-charge", "2026-05-08", 6425m, description: "NORTHERN SHIELD INSU HALIFAX", category: Categories.Uncategorized),
            Tx("pdf-refund", "2026-05-13", 1380m, DebitCreditMemo.Credit, "NORTHERN SHIELD INSU HALIFAX", Categories.Uncategorized),
            Tx("pdf-gas", "2026-05-09", 50m, description: "ESSO 1234 TORONTO"),
        ], Source: "pdf"));
        await For("alice").ImportAsync(account.AccountId, new(
        [
            Tx("csv-charge", "2026-05-08", 6425m, description: "NORTHERN SHIELD INSU", category: Categories.Uncategorized),
            Tx("csv-refund", "2026-05-13", 1380m, DebitCreditMemo.Credit, "NORTHERN SHIELD INSU", Categories.Uncategorized),
            Tx("csv-gas", "2026-05-09", 50m, description: "ESSO"),
        ], Source: "csv"));

        // The user already filed the CSV's charge and attached its receipt; merging keeps that copy.
        await For("alice").ClassifyAsync(account.AccountId, "csv-charge", new(Categories.FoodAndDining, null, new TaxTag(TaxCategories.All[0].Code, TaxTagStatus.Confirmed)));
        Assert.NotNull(await For("alice").AddReceiptAsync(account.AccountId, "pdf-charge", new("policy.pdf", "application/pdf", Pdf)));

        Assert.Empty(await For("bob").FindDuplicatesAsync());
        var found = await For("alice").FindDuplicatesAsync();
        Assert.Equal(3, found.Count);
        Assert.Equal(["pdf-refund", "pdf-gas", "pdf-charge"], found.Select(d => d.Keep.Transaction.TransactionId)); // newest first; longer description by default
        var charge = found.Single(d => d.Keep.Transaction.Amount == 6425m);
        Assert.Equal("csv-charge", charge.Remove.Transaction.TransactionId); // the receipt outweighs the user's category

        Assert.False(await For("bob").MergeDuplicateAsync(account.AccountId, new("pdf-charge", "csv-charge")));
        Assert.True(await For("alice").MergeDuplicateAsync(account.AccountId, new("pdf-charge", "csv-charge")));
        Assert.False(await For("bob").KeepBothAsync(account.AccountId, new("pdf-gas", "csv-gas")));
        Assert.True(await For("alice").KeepBothAsync(account.AccountId, new("pdf-gas", "csv-gas")));

        var left = await For("alice").SearchTransactionsAsync(new TransactionFilter());
        Assert.DoesNotContain(left, t => t.Transaction.TransactionId == "csv-charge");
        var kept = left.Single(t => t.Transaction.TransactionId == "pdf-charge");
        Assert.Equal(Categories.FoodAndDining, kept.Transaction.Category); // the removed copy's choices moved over
        Assert.True(kept.CategoryIsManual);
        Assert.Equal(TaxTagStatus.Confirmed, kept.Tax!.Status);
        Assert.Single(await For("alice").GetReceiptsAsync(account.AccountId, "pdf-charge"));

        Assert.Equal("csv-refund", Assert.Single(await For("alice").FindDuplicatesAsync()).Remove.Transaction.TransactionId);
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").MergeDuplicateAsync(account.AccountId, new("pdf-refund", "pdf-refund")));
    }

    [Fact]
    public async Task ClearDeletesOnlyTheOwnersLedger()
    {
        foreach (var owner in new[] { "alice", "bob" })
        {
            var account = await For(owner).CreateAccountAsync(Chequing());
            await For(owner).ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m), Tx("rx", "2026-09-02", 40m, description: "REXALL")]));
            Assert.NotNull(await For(owner).AddReceiptAsync(account.AccountId, "rx", new("rx.pdf", "application/pdf", Pdf)));
            await For(owner).AddHouseholdMemberAsync("Sam");
        }

        // A context without an owner sees nothing, so deletes nothing.
        Assert.Equal(new ClearLedgerResult(0, 0, 0, 0), await For(null).ClearAsync());

        Assert.Equal(new ClearLedgerResult(1, 2, 1, 1), await For("alice").ClearAsync());

        Assert.Empty(await For("alice").GetAccountsAsync());
        Assert.Empty(await For("alice").SearchTransactionsAsync(new TransactionFilter()));
        Assert.Empty(await For("alice").GetHouseholdAsync());
        var backup = await For("alice").ExportAsync();
        Assert.Empty(backup.Transactions);
        Assert.Empty(backup.Receipts ?? []);

        Assert.Single(await For("bob").GetAccountsAsync());
        Assert.Equal(2, (await For("bob").SearchTransactionsAsync(new TransactionFilter())).Count);
        Assert.Single(await For("bob").GetHouseholdAsync());
        Assert.Single((await For("bob").ExportAsync()).Receipts ?? []);
    }

    [Fact]
    public async Task OwnersCanNeitherSeeNorChangeEachOthersData()
    {
        var alice = For("alice");
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m)]));
        var aliceTx = (await For("alice").SearchTransactionsAsync(new TransactionFilter()))[0].Transaction;

        var bob = For("bob");
        Assert.Empty(await bob.GetAccountsAsync());
        Assert.Empty(await bob.SearchTransactionsAsync(new TransactionFilter()));
        Assert.Null(await bob.FindAccountAsync(account.AccountId));
        Assert.Null(await bob.ImportAsync(account.AccountId, new([Tx("x", "2026-09-01", 1m)])));
        Assert.Null(await bob.UpdateAccountAsync(account.AccountId, Chequing("Hijacked")));
        Assert.False(await bob.ClassifyAsync(account.AccountId, aliceTx.TransactionId, new(Categories.Shopping, null, null)));
        Assert.False(await bob.DeleteAccountAsync(account.AccountId));
        Assert.Null(await bob.GetAccountTransactionsPageAsync(account.AccountId, null, null, 0, 10));

        var after = For("alice");
        Assert.Equal("Chequing", (await after.FindAccountAsync(account.AccountId))!.Nickname);
        Assert.Equal(Categories.FoodAndDining, (await after.SearchTransactionsAsync(new TransactionFilter()))[0].Transaction.Category);
    }

    [Fact]
    public async Task AContextWithoutAnOwnerSeesNothingAndSavesNothing()
    {
        await For("alice").CreateAccountAsync(Chequing());
        var unscoped = For(null);

        Assert.Empty(await unscoped.GetAccountsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => unscoped.CreateAccountAsync(Chequing()));
    }

    [Fact]
    public async Task DateFiltersAndOrderingRunOnSqlite()
    {
        var ledger = For(LedgerDbContext.LocalOwner);
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new([Tx("a", "2026-08-31", 1m), Tx("b", "2026-09-15", 2m), Tx("c", "2026-09-01", 3m), Tx("d", "2026-10-01", 4m)]));

        var september = await For(LedgerDbContext.LocalOwner).SearchTransactionsAsync(new TransactionFilter(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));

        Assert.Equal(["b", "c"], september.Select(t => t.Transaction.TransactionId));
    }

    [Fact]
    public async Task StoredTimestampsStayWithinJavaScriptsSafeIntegerRange()
    {
        // Regression: the browser's SQLite bridge sends integers above 2^53 as text, and stored
        // ticks then never matched a date filter.
        var ledger = For(LedgerDbContext.LocalOwner);
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new([Tx("a", "2099-12-31", 1m)]));

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT MAX(TransactionTimestamp) FROM Transactions";
        var largest = (long)(await command.ExecuteScalarAsync())!;

        Assert.InRange(largest, 0, (1L << 53) - 1);
    }

    [Fact]
    public async Task ClassifyingKeepsTaxTagsAndRejectsUnknownCategories()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new([Tx("rx", "2026-03-01", 40m, description: "REXALL")]));

        Assert.True(await For("alice").ClassifyAsync(account.AccountId, "rx", new(Categories.HealthAndFitness, "Pharmacy", new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed))));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ClassifyAsync(account.AccountId, "rx", new("Nonsense", null, null)));

        var report = await For("alice").GetTaxReportAsync(2026);
        Assert.Equal(40m, Assert.Single(report.Categories).Confirmed);
    }

    [Fact]
    public async Task TaxReviewFindsSuggestionsAndMissedSpendingAndSettingATagKeepsTheCategory()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("rx", "2026-03-01", 40m, description: "REXALL") with { Tax = new TaxTag(TaxCategories.Medical, TaxTagStatus.Suggested) },
            Tx("physio", "2026-04-01", 90m, category: Categories.HealthAndFitness),
            Tx("lunch", "2026-04-02", 15m),
        ]));

        var review = await For("alice").GetTaxReviewAsync(2026);
        Assert.Equal("rx", Assert.Single(review.Pending).Transaction.TransactionId);
        Assert.Equal("physio", Assert.Single(review.PossiblyMissed).Transaction.TransactionId);
        Assert.Empty((await For("bob").GetTaxReviewAsync(2026)).Pending);

        Assert.True(await For("alice").SetTaxTagAsync(account.AccountId, "rx", new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed)));
        Assert.False(await For("bob").SetTaxTagAsync(account.AccountId, "rx", null));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetTaxTagAsync(account.AccountId, "rx", new TaxTag("NOPE", TaxTagStatus.Confirmed)));

        var after = await For("alice").GetTaxReviewAsync(2026);
        Assert.Empty(after.Pending);
        Assert.Equal(1, after.Confirmed);
        var rx = (await For("alice").SearchTransactionsAsync(new TransactionFilter(Search: "REXALL"))).Single();
        Assert.Equal(Categories.FoodAndDining, rx.Transaction.Category);
        Assert.False(rx.CategoryIsManual);
    }

    [Fact]
    public async Task SetsTaxTreatmentOfManyTransactionsAtOnce()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("jan", "2026-01-05", 1800m, description: "MAPLE TRUST MTG"),
            Tx("feb", "2026-02-05", 1800m, description: "MAPLE TRUST MTG") with { Tax = new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed, "split", BusinessUsePercent: 40m) },
            Tx("mar", "2026-03-05", 1800m, description: "MAPLE TRUST MTG") with { Tax = new TaxTag(TaxCategories.Rental, TaxTagStatus.Suggested, ClaimYear: 2026) },
            Tx("apr", "2026-04-05", 1800m, description: "MAPLE TRUST MTG") with { Tax = new TaxTag(TaxCategories.CarryingCharges, TaxTagStatus.Confirmed, "keep me", ClaimYear: 2027) },
            Tx("may", "2026-05-05", 1800m, description: "MAPLE TRUST MTG") with { Tax = new TaxTag(TaxCategories.Rental, TaxTagStatus.Rejected) },
        ]));
        TransactionKey Key(string id) => new(account.AccountId, id);
        string[] all = ["jan", "feb", "mar", "apr", "may"];
        async Task<TaxTag?> Tax(string id) => (await ledger.SearchTransactionsAsync(new())).Single(t => t.Transaction.TransactionId == id).Tax;

        // feb is already claimed and keeps its details; mar's suggestion is confirmed as it is.
        Assert.Equal(4, await ledger.SetTaxManyAsync(new([.. all.Select(Key)], "rental")));
        Assert.Equal(new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed), await Tax("jan"));
        Assert.Equal(new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed, "split", BusinessUsePercent: 40m), await Tax("feb"));
        Assert.Equal(new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed, ClaimYear: 2026), await Tax("mar"));
        Assert.Equal(new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed, "keep me"), await Tax("apr"));
        Assert.Equal(new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed), await Tax("may"));
        Assert.Equal(7200m + 720m, Assert.Single((await ledger.GetTaxReportAsync(2026)).Categories).Confirmed);
        Assert.Equal(0, await ledger.SetTaxManyAsync(new([.. all.Select(Key)], TaxCategories.Rental)));

        // Not claimable rejects, keeping the code so a rule doesn't suggest it again.
        Assert.Equal(2, await ledger.SetTaxManyAsync(new([Key("jan"), Key("feb"), Key("missing")], null)));
        Assert.Equal(TaxTagStatus.Rejected, (await Tax("feb"))!.Status);

        await Assert.ThrowsAsync<LedgerValidationException>(() => ledger.SetTaxManyAsync(new([Key("jan")], "NOPE")));
        Assert.Equal(0, await For("bob").SetTaxManyAsync(new([Key("mar")], TaxCategories.Medical)));
        Assert.Equal(TaxCategories.Rental, (await Tax("mar"))!.Code);
    }

    [Fact]
    public async Task TaxReportIncludesContributionsClaimedFromEarlyNextYear()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("dec", "2026-12-15", 1000m) with { Tax = new TaxTag(TaxCategories.Rrsp, TaxTagStatus.Confirmed) },
            Tx("feb", "2027-02-20", 2000m) with { Tax = new TaxTag(TaxCategories.Rrsp, TaxTagStatus.Confirmed) },
        ]));

        Assert.Equal("feb", Assert.Single((await For("alice").GetTaxReportAsync(2026)).RrspFromNextYear).Transaction.TransactionId);

        Assert.True(await For("alice").SetTaxTagAsync(account.AccountId, "feb", new TaxTag(TaxCategories.Rrsp, TaxTagStatus.Confirmed, ClaimYear: 2026)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetTaxTagAsync(account.AccountId, "feb", new TaxTag(TaxCategories.Rrsp, TaxTagStatus.Confirmed, ClaimYear: 26)));

        var report = await For("alice").GetTaxReportAsync(2026);
        Assert.Equal(3000m, Assert.Single(report.Categories).Confirmed);
        Assert.Empty(report.RrspFromNextYear);
        Assert.Empty((await For("alice").GetTaxReportAsync(2027)).Categories);

        var restored = (await For("alice").ExportAsync()).Transactions.Single(t => t.Transaction.TransactionId == "feb");
        Assert.Equal(2026, restored.Tax!.ClaimYear);
    }

    [Fact]
    public async Task ApplyingAMedicalWindowUpdatesBothYearsReports()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        TaxTag medical = new(TaxCategories.Medical, TaxTagStatus.Confirmed);
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("jan25", "2025-01-10", 300m) with { Tax = medical },
            Tx("oct25", "2025-10-01", 2000m) with { Tax = medical },
            Tx("mar26", "2026-03-01", 1000m) with { Tax = medical },
            Tx("nov26", "2026-11-01", 100m) with { Tax = medical },
        ]));

        var estimate = await For("alice").GetMedicalEstimateAsync(2026, 50_000m);
        Assert.Equal((9, 3000m), (estimate.Best.EndMonth, estimate.Best.Total));

        Assert.Equal(2, await For("alice").ApplyMedicalWindowAsync(2026, estimate.Best.EndMonth));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ApplyMedicalWindowAsync(2026, 13));
        Assert.Equal(0, await For("bob").ApplyMedicalWindowAsync(2026, 9));

        Assert.Equal(3000m, Assert.Single((await For("alice").GetTaxReportAsync(2026)).Categories).Confirmed);
        Assert.Equal(300m, Assert.Single((await For("alice").GetTaxReportAsync(2025)).Categories).Confirmed);
        Assert.Equal(100m, Assert.Single((await For("alice").GetTaxReportAsync(2027)).Categories).Confirmed);
    }

    private static readonly byte[] Pdf = [.. "%PDF-1.7 receipt"u8];

    [Fact]
    public async Task ReceiptsAttachToTransactionsAndShowInTheTaxReport()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("rx", "2026-03-01", 40m) with { Tax = new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed) },
            Tx("gift", "2026-04-01", 25m) with { Tax = new TaxTag(TaxCategories.Charitable, TaxTagStatus.Confirmed) },
        ]));

        var receipt = await For("alice").AddReceiptAsync(account.AccountId, "rx", new(@"C:\scans\rexall march.pdf", "application/pdf", Pdf));
        Assert.Equal("rexall march.pdf", receipt!.FileName);
        Assert.Null(await For("alice").AddReceiptAsync(account.AccountId, "missing", new("a.pdf", "application/pdf", Pdf)));
        Assert.Null(await For("bob").AddReceiptAsync(account.AccountId, "rx", new("a.pdf", "application/pdf", Pdf)));

        var report = await For("alice").GetTaxReportAsync(2026);
        Assert.Equal([receipt], report.Receipts);
        Assert.Equal(1, report.MissingReceipts);
        Assert.Contains("rexall march.pdf", TaxReportBuilder.ToCsv(report, new Dictionary<string, string>()));

        Assert.Equal(Pdf, (await For("alice").GetReceiptAsync(receipt.Id))!.Content);
        Assert.Null(await For("bob").GetReceiptAsync(receipt.Id));
        Assert.False(await For("bob").DeleteReceiptAsync(receipt.Id));
        Assert.True(await For("alice").DeleteReceiptAsync(receipt.Id));
        Assert.Empty(await For("alice").GetReceiptsAsync(account.AccountId, "rx"));
    }

    [Theory]
    [InlineData("text/html", "<html>")]
    [InlineData("application/pdf", "<html>")]   // claims to be a PDF but isn't
    [InlineData("image/png", "")]
    public async Task ReceiptsMustBePdfsOrImages(string contentType, string content)
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new([Tx("rx", "2026-03-01", 40m)]));

        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            For("alice").AddReceiptAsync(account.AccountId, "rx", new("x", contentType, System.Text.Encoding.UTF8.GetBytes(content))));
    }

    [Fact]
    public async Task ReceiptsTravelWithBackupsAndAccountDeletion()
    {
        var local = For(LedgerDbContext.LocalOwner);
        var account = await local.CreateAccountAsync(Chequing());
        await local.ImportAsync(account.AccountId, new([Tx("rx", "2026-03-01", 40m)]));
        await For(LedgerDbContext.LocalOwner).AddReceiptAsync(account.AccountId, "rx", new("rx.pdf", "application/pdf", Pdf));
        var backup = await For(LedgerDbContext.LocalOwner).ExportAsync();

        Assert.Equal(1, (await For("alice").RestoreAsync(backup)).ReceiptsAdded);
        Assert.Equal(0, (await For("alice").RestoreAsync(backup)).ReceiptsAdded);
        // Someone else restoring the same file gets their own copy under a different id.
        Assert.Equal(1, (await For("bob").RestoreAsync(backup)).ReceiptsAdded);

        var aliceAccount = Assert.Single(await For("alice").GetAccountsAsync());
        var aliceReceipt = Assert.Single(await For("alice").GetReceiptsAsync(aliceAccount.AccountId, "rx"));
        var bobAccount = Assert.Single(await For("bob").GetAccountsAsync());
        Assert.NotEqual(aliceReceipt.Id, Assert.Single(await For("bob").GetReceiptsAsync(bobAccount.AccountId, "rx")).Id);

        Assert.True(await For("alice").DeleteAccountAsync(aliceAccount.AccountId));
        Assert.Null(await For("alice").GetReceiptAsync(aliceReceipt.Id));
        Assert.Single(await For("bob").GetReceiptsAsync(bobAccount.AccountId, "rx"));
    }

    [Fact]
    public async Task ClaimsCanBeAssignedToHouseholdMembersAndReportedPerPerson()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("daycare", "2026-03-01", 800m) with { Tax = new TaxTag(TaxCategories.ChildCare, TaxTagStatus.Confirmed) },
            Tx("rx", "2026-04-01", 40m) with { Tax = new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed) },
        ]));

        var sam = await For("alice").AddHouseholdMemberAsync("  Sam ");
        var alex = await For("alice").AddHouseholdMemberAsync("Alex");
        Assert.Equal("Sam", sam.Name);
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").AddHouseholdMemberAsync("sam"));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").AddHouseholdMemberAsync(" "));
        Assert.Equal(["Alex", "Sam"], (await For("alice").GetHouseholdAsync()).Select(m => m.Name));
        Assert.Empty(await For("bob").GetHouseholdAsync());

        Assert.True(await For("alice").SetTaxTagAsync(account.AccountId, "daycare", new TaxTag(TaxCategories.ChildCare, TaxTagStatus.Confirmed, ClaimedBy: sam.Id)));
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            For("alice").SetTaxTagAsync(account.AccountId, "rx", new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed, ClaimedBy: "hm-nobody")));

        Assert.Equal(TaxCategories.ChildCare, Assert.Single((await For("alice").GetTaxReportAsync(2026, sam.Id)).Categories).Category.Code);
        Assert.Empty((await For("alice").GetTaxReportAsync(2026, alex.Id)).Categories);
        Assert.Equal(TaxCategories.Medical, Assert.Single((await For("alice").GetTaxReportAsync(2026, TaxReportBuilder.Unassigned)).Categories).Category.Code);
        Assert.Equal(2, (await For("alice").GetTaxReportAsync(2026)).Categories.Count);
        Assert.Contains(",Sam", await For("alice").GetTaxReportCsvAsync(2026));

        Assert.Equal("Samantha", (await For("alice").RenameHouseholdMemberAsync(sam.Id, "Samantha"))!.Name);
        Assert.Null(await For("bob").RenameHouseholdMemberAsync(sam.Id, "Bob"));
        Assert.False(await For("bob").DeleteHouseholdMemberAsync(sam.Id));

        // Removing a person keeps their claims, unassigned.
        Assert.True(await For("alice").DeleteHouseholdMemberAsync(sam.Id));
        var daycare = (await For("alice").SearchTransactionsAsync(new TransactionFilter(TaxCode: TaxCategories.ChildCare))).Single();
        Assert.Equal(new TaxTag(TaxCategories.ChildCare, TaxTagStatus.Confirmed), daycare.Tax);
    }

    [Fact]
    public async Task BackupsKeepWhoClaimsWhat()
    {
        var local = For(LedgerDbContext.LocalOwner);
        var account = await local.CreateAccountAsync(Chequing());
        var sam = await For(LedgerDbContext.LocalOwner).AddHouseholdMemberAsync("Sam");
        await For(LedgerDbContext.LocalOwner).ImportAsync(account.AccountId, new(
            [Tx("daycare", "2026-03-01", 800m) with { Tax = new TaxTag(TaxCategories.ChildCare, TaxTagStatus.Confirmed, ClaimedBy: sam.Id) }]));
        var backup = await For(LedgerDbContext.LocalOwner).ExportAsync();

        await For("alice").RestoreAsync(backup);
        await For("bob").RestoreAsync(backup);

        foreach (var owner in new[] { "alice", "bob" })
        {
            var member = Assert.Single(await For(owner).GetHouseholdAsync());
            Assert.Equal("Sam", member.Name);
            var claim = (await For(owner).SearchTransactionsAsync(new TransactionFilter())).Single();
            Assert.Equal(member.Id, claim.Tax!.ClaimedBy);
        }

        Assert.NotEqual((await For("alice").GetHouseholdAsync())[0].Id, (await For("bob").GetHouseholdAsync())[0].Id);
    }

    private static LedgerTransaction Income(string id, string date, decimal amount, string subCategory, string description = "PAYROLL ACME") =>
        Tx(id, date, amount, DebitCreditMemo.Credit, description, Categories.Income) is var t
            ? t with { Transaction = t.Transaction with { SubCategory = subCategory } }
            : t;

    private static readonly DateOnly AfterYearEnd = new(2027, 1, 15);

    [Fact]
    public async Task TaxEstimatesComeFromIncomeDepositsAndClaims()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Income("pay1", "2026-03-15", 37_103.07m, Categories.Salary),
            Income("pay2", "2026-09-15", 37_103.06m, Categories.Salary),
            Income("rent", "2026-05-01", 1_000m, Categories.RentalIncome, "E-TRANSFER TENANT"),
            Income("ccb", "2026-05-20", 500m, Categories.OtherIncome, "CANADA CHILD BENEFIT"),
            Tx("rrsp", "2026-02-10", 5_000m) with { Tax = new TaxTag(TaxCategories.Rrsp, TaxTagStatus.Confirmed) },
        ]));

        // No household: one estimate, for whoever keeps the ledger, with all the income and claims.
        var self = Assert.Single((await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd)).People);
        Assert.Equal(TaxSituations.Self, self.PersonId);
        Assert.Null(self.Estimate); // no province yet
        Assert.Equal(74_206.13m, self.Income.Salary);
        Assert.Equal(1_000m, self.Income.Rental);
        Assert.Equal(500m, self.Income.Other);

        await For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation(" on "));
        var estimate = Assert.Single((await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd)).People).Estimate!;
        Assert.Equal("ON", estimate.ProvinceCode);
        // Net pay of a $100,000 Ontario salary works back to it.
        Assert.InRange(Assert.Single(estimate.Income, l => l.Reference == "Line 10100").Amount, 99_999m, 100_001m);
        Assert.Equal(5_000m, Assert.Single(estimate.Deductions, d => d.Reference == "Line 20800").Amount);
        Assert.Contains(estimate.Notes, n => n.Contains("$500.00") && n.Contains("isn't counted"));

        // The year in progress is projected to December 31.
        var midYear = Assert.Single((await For("alice").GetTaxEstimatesAsync(2026, new DateOnly(2026, 7, 2))).People).Income;
        Assert.Equal(37_103.07m, midYear.Salary);
        Assert.True(midYear.IsProjected);

        // The situation carries into later years.
        Assert.Equal(new TaxSituation("ON"), Assert.Single((await For("alice").GetTaxEstimatesAsync(2027, AfterYearEnd)).People).Situation);

        // Payroll plans are kept and carried forward; the notice of assessment's limit is for its year only.
        var plans = await For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self,
            new TaxSituation("ON", EsppPercent: 15, EsppMatchPercent: 0, RrspPercent: 5.004m, RrspMatchPercent: 5, RrspDeductionLimit: 25_000m));
        Assert.Equal(new TaxSituation("ON", EsppPercent: 15, RrspPercent: 5m, RrspMatchPercent: 5, RrspDeductionLimit: 25_000m), plans.Situation);
        var withPlans = Assert.Single((await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd)).People).Estimate!;
        Assert.Equal(25_000m, withPlans.Rrsp!.Limit);
        Assert.False(withPlans.Rrsp.LimitEstimated);
        Assert.Equal(plans.Situation with { RrspDeductionLimit = null }, Assert.Single((await For("alice").GetTaxEstimatesAsync(2027, AfterYearEnd)).People).Situation);
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation("ON", EsppPercent: 40, RrspPercent: 15)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation("ON", RrspDeductionLimit: -1)));

        // A pension plan is kept like the other plans, and counts toward the 50% of salary.
        var pension = await For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self,
            new TaxSituation("ON", PensionPercent: 5.004m, PensionMatchPercent: 0, PensionAdjustment: 9_000.004m));
        Assert.Equal(new TaxSituation("ON", PensionPercent: 5m, PensionAdjustment: 9_000m), pension.Situation);
        Assert.Equal(9_000m, Assert.Single((await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd)).People).Estimate!.Rrsp!.PensionAdjustment);
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation("ON", RrspPercent: 40, PensionPercent: 15)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation("ON", PensionAdjustment: -1)));
        await For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation("ON"));

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, TaxSituations.Self, new TaxSituation("QC")));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, "hm-nobody", new TaxSituation("ON")));
        Assert.Null(Assert.Single((await For("bob").GetTaxEstimatesAsync(2026, AfterYearEnd)).People).Estimate);
    }

    [Fact]
    public async Task HouseholdIncomeFollowsAccountOwnersAndAssignedDeposits()
    {
        var ledger = For("alice");
        var sams = await ledger.CreateAccountAsync(Chequing("Sam's chequing"));
        var joint = await For("alice").CreateAccountAsync(Chequing("Joint"));
        await For("alice").ImportAsync(sams.AccountId, new([Income("pay", "2026-03-15", 50_000m, Categories.Salary)]));
        await For("alice").ImportAsync(joint.AccountId, new(
        [
            Income("fee", "2026-04-01", 20_000m, Categories.BusinessIncome, "CLIENT CO"),
            Income("rent", "2026-04-01", 1_200m, Categories.RentalIncome, "E-TRANSFER TENANT"),
        ]));

        var sam = await For("alice").AddHouseholdMemberAsync("Sam");
        var alex = await For("alice").AddHouseholdMemberAsync("Alex");
        Assert.True(await For("alice").SetAccountHolderAsync(sams.AccountId, sam.Id));
        Assert.False(await For("bob").SetAccountHolderAsync(sams.AccountId, null));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetAccountHolderAsync(joint.AccountId, "hm-nobody"));
        Assert.True(await For("alice").SetEarnedByAsync(joint.AccountId, "fee", alex.Id));
        Assert.Equal(sam.Id, Assert.Single(await For("alice").GetAccountHoldersAsync()).Value);

        var set = await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd);
        Assert.Equal(50_000m, set.People.Single(p => p.PersonId == sam.Id).Income.Salary);
        Assert.Equal(20_000m, set.People.Single(p => p.PersonId == alex.Id).Income.Business);
        Assert.Equal(2, set.SharedIncome!.Count);
        Assert.Equal("rent", Assert.Single(set.UnassignedIncome).Transaction.TransactionId);

        // Backups keep whose accounts and deposits are.
        var backup = await For("alice").ExportAsync();
        await For("bob").RestoreAsync(backup);
        var restored = await For("bob").GetTaxEstimatesAsync(2026, AfterYearEnd);
        Assert.Equal(50_000m, restored.People.Single(p => p.Name == "Sam").Income.Salary);
        Assert.Equal(20_000m, restored.People.Single(p => p.Name == "Alex").Income.Business);

        // Removing a person makes their accounts shared and their deposits unassigned.
        Assert.True(await For("alice").DeleteHouseholdMemberAsync(sam.Id));
        Assert.True(await For("alice").DeleteHouseholdMemberAsync(alex.Id));
        Assert.Empty(await For("alice").GetAccountHoldersAsync());
        Assert.All(await For("alice").SearchTransactionsAsync(new TransactionFilter()), t => Assert.Null(t.EarnedBy));
    }

    [Fact]
    public async Task RentSplitBetweenCoOwnersGoesToEachByTheirShare()
    {
        var joint = await For("alice").CreateAccountAsync(Chequing("Joint"));
        await For("alice").ImportAsync(joint.AccountId, new(
        [
            Income("rent1", "2026-02-01", 2_000m, Categories.RentalIncome, "E-TRANSFER TENANT"),
            Income("rent2", "2026-03-01", 2_000m, Categories.RentalIncome, "E-TRANSFER TENANT"),
            Income("pay", "2026-03-15", 5_000m, Categories.Salary),
            Tx("tax", "2026-03-20", 1_000m, description: "CITY PROPERTY TAX") with { Tax = new TaxTag(TaxCategories.Rental, TaxTagStatus.Confirmed) },
        ]));
        var sam = await For("alice").AddHouseholdMemberAsync("Sam");
        var alex = await For("alice").AddHouseholdMemberAsync("Alex");

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetEarnedByAsync(joint.AccountId, "pay", TaxSituations.Split));
        Assert.True(await For("alice").SetEarnedByAsync(joint.AccountId, "rent1", TaxSituations.Split));
        Assert.True(await For("alice").SetEarnedByAsync(joint.AccountId, "rent2", TaxSituations.Split));
        Assert.False(await For("bob").SetEarnedByAsync(joint.AccountId, "rent1", TaxSituations.Split));

        // Equal shares until someone sets theirs; the unclaimed property tax is shared the same way.
        var set = await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd);
        Assert.All(set.People, p => Assert.Equal((2_000m, 4_000m, 0.5m), (p.Income.Rental, p.Income.SplitRental, p.Income.RentalShare)));
        Assert.All(set.People, p => Assert.Equal(500m, p.Claims[TaxCategories.Rental]));
        Assert.Equal(0, set.UnassignedClaims);
        Assert.Equal("pay", Assert.Single(set.UnassignedIncome).Transaction.TransactionId);

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SaveTaxSituationAsync(2026, sam.Id, new TaxSituation("ON", RentalSharePercent: 120m)));
        await For("alice").SaveTaxSituationAsync(2026, sam.Id, new TaxSituation("ON", RentalSharePercent: 75m));
        await For("alice").SaveTaxSituationAsync(2026, alex.Id, new TaxSituation("ON"));
        set = await For("alice").GetTaxEstimatesAsync(2026, AfterYearEnd);
        var samEstimate = set.People.Single(p => p.PersonId == sam.Id);
        var alexEstimate = set.People.Single(p => p.PersonId == alex.Id);
        Assert.Equal((3_000m, 750m), (samEstimate.Income.Rental, samEstimate.Claims[TaxCategories.Rental]));
        Assert.Equal((1_000m, 250m), (alexEstimate.Income.Rental, alexEstimate.Claims[TaxCategories.Rental]));
        Assert.Equal(2_250m, Assert.Single(samEstimate.Estimate!.Income, l => l.Reference == "Line 12600").Amount);
        Assert.Equal(2_250m, samEstimate.Estimate.NotWithheldTotal);

        // Backups keep the split.
        await For("bob").RestoreAsync(await For("alice").ExportAsync());
        var restored = await For("bob").GetTaxEstimatesAsync(2026, AfterYearEnd);
        Assert.Equal(3_000m, restored.People.Single(p => p.Name == "Sam").Income.Rental);
    }

    [Fact]
    public async Task BackupsKeepEachPersonsSituation()
    {
        var sam = await For(LedgerDbContext.LocalOwner).AddHouseholdMemberAsync("Sam");
        await For(LedgerDbContext.LocalOwner).SaveTaxSituationAsync(2026, sam.Id, new TaxSituation("NS", EmployerWithholds: false));
        var backup = await For(LedgerDbContext.LocalOwner).ExportAsync();

        await For("alice").RestoreAsync(backup);
        await For("bob").RestoreAsync(backup);
        await For("bob").RestoreAsync(backup); // twice adds nothing

        foreach (var owner in new[] { "alice", "bob" })
        {
            var person = Assert.Single((await For(owner).GetTaxEstimatesAsync(2026, AfterYearEnd)).People);
            Assert.True(person.Saved);
            Assert.Equal(new TaxSituation("NS", EmployerWithholds: false), person.Situation);
            Assert.Single((await For(owner).ExportAsync()).TaxSituations!);
        }

        await For("alice").ClearAsync();
        Assert.Empty((await For("alice").ExportAsync()).TaxSituations!);
        Assert.Single((await For("bob").ExportAsync()).TaxSituations!);
    }

    [Fact]
    public async Task BusinessUseSharesCanBeSetPerItemOrPerSpendingCategory()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        TaxTag business = new(TaxCategories.Business, TaxTagStatus.Confirmed);
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("phone", "2026-02-01", 100m, category: Categories.BillsAndUtilities) with { Tax = business },
            Tx("internet", "2026-02-02", 80m, category: Categories.BillsAndUtilities) with { Tax = business },
            Tx("laptop", "2026-03-01", 1500m, category: Categories.Shopping) with { Tax = business },
            Tx("rx", "2026-03-02", 40m) with { Tax = new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed) },
        ]));

        Assert.Equal(2, await For("alice").SetBusinessUseAsync(2026, new(TaxCategories.Business, Categories.BillsAndUtilities, 25m)));
        Assert.Equal(1680m - 135m, Assert.Single((await For("alice").GetTaxReportAsync(2026)).Categories, c => c.Category.Code == TaxCategories.Business).Confirmed);

        Assert.True(await For("alice").SetTaxTagAsync(account.AccountId, "laptop", business with { BusinessUsePercent = 60m }));
        Assert.Equal(45m + 900m, Assert.Single((await For("alice").GetTaxReportAsync(2026)).Categories, c => c.Category.Code == TaxCategories.Business).Confirmed);

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetTaxTagAsync(account.AccountId, "laptop", business with { BusinessUsePercent = 150m }));
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            For("alice").SetTaxTagAsync(account.AccountId, "rx", new TaxTag(TaxCategories.Medical, TaxTagStatus.Confirmed, BusinessUsePercent: 50m)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetBusinessUseAsync(2026, new(TaxCategories.Medical, null, 50m)));
        Assert.Equal(0, await For("bob").SetBusinessUseAsync(2026, new(TaxCategories.Business, null, 10m)));

        // 100% (or clearing it) counts the whole expense again.
        Assert.Equal(3, await For("alice").SetBusinessUseAsync(2026, new(TaxCategories.Business, null, 100m)));
        Assert.Equal(1680m, Assert.Single((await For("alice").GetTaxReportAsync(2026)).Categories, c => c.Category.Code == TaxCategories.Business).Confirmed);
    }

    [Fact]
    public async Task ReceiptMatchesComeFromTheOwnersSpendingAroundTheReceiptDate()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new(
        [
            Tx("gym", "2026-09-11", 60m, description: "GOODLIFE CLUBS #452"),
            Tx("refund", "2026-09-11", 60m, DebitCreditMemo.Credit, "GOODLIFE REFUND"),
            Tx("last-year", "2025-09-11", 60m, description: "GOODLIFE CLUBS #452"),
        ]));

        var receipt = new ReceiptFields(60m, new DateOnly(2026, 9, 10), "GoodLife Fitness");

        Assert.Equal("gym", Assert.Single(await For("alice").FindReceiptMatchesAsync(receipt)).Item.Transaction.TransactionId);
        Assert.Empty(await For("bob").FindReceiptMatchesAsync(receipt));
        Assert.Equal("gym", (await For("alice").FindReceiptMatchesAsync(receipt with { Date = null }, today: new DateOnly(2026, 11, 14)))[0].Item.Transaction.TransactionId);
    }

    [Fact]
    public async Task DeletingAnAccountDeletesItsTransactions()
    {
        var ledger = For("alice");
        var account = await ledger.CreateAccountAsync(Chequing());
        await ledger.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m)]));

        Assert.True(await For("alice").DeleteAccountAsync(account.AccountId));

        Assert.Empty(await For("alice").SearchTransactionsAsync(new TransactionFilter()));
        await using var db = new LedgerDbContext(_options);
        Assert.Equal(0, await db.Transactions.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task RestoringABackupTwiceAddsNothingTheSecondTime()
    {
        var local = For(LedgerDbContext.LocalOwner);
        var account = await local.CreateAccountAsync(Chequing());
        await local.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m) with { Tax = new TaxTag(TaxCategories.Medical, TaxTagStatus.Suggested) }, Tx("b", "2026-09-02", 6m)]));
        var backup = await For(LedgerDbContext.LocalOwner).ExportAsync();

        var first = await For("alice").RestoreAsync(backup);
        var second = await For("alice").RestoreAsync(backup);

        Assert.Equal(new RestoreResult(1, 2, 0), first);
        Assert.Equal(new RestoreResult(0, 0, 2), second);
        var restored = await For("alice").SearchTransactionsAsync(new TransactionFilter());
        Assert.Equal(new TaxTag(TaxCategories.Medical, TaxTagStatus.Suggested), restored.Single(t => t.Transaction.TransactionId == "a").Tax);
    }

    [Fact]
    public async Task RestoringSomeoneElsesAccountIdsCreatesSeparateAccounts()
    {
        var alice = For("alice");
        var account = await alice.CreateAccountAsync(Chequing());
        await alice.ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m)]));
        var aliceBackup = await For("alice").ExportAsync();

        // Bob uploads a file naming Alice's account id: he gets his own copy, Alice is untouched.
        var result = await For("bob").RestoreAsync(aliceBackup);
        var again = await For("bob").RestoreAsync(aliceBackup);

        var bobAccount = Assert.Single(await For("bob").GetAccountsAsync());
        Assert.NotEqual(account.AccountId, bobAccount.AccountId);
        Assert.Equal((1, 1), (result.AccountsAdded, result.TransactionsAdded));
        Assert.Equal((0, 0), (again.AccountsAdded, again.TransactionsAdded));
        Assert.Single(await For("alice").SearchTransactionsAsync(new TransactionFilter()));
    }

    [Fact]
    public async Task RestoreRejectsTransactionsForAccountsNotInTheBackup()
    {
        var backup = new LedgerBackup(1, DateTimeOffset.UtcNow, [], [Tx("a", "2026-09-01", 5m)]);

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").RestoreAsync(backup));
    }

    [Fact]
    public async Task MerchantChoicesRememberTheNewestReviewedCategoryAndTheNewestTags()
    {
        var alice = For("alice");
        var account = await alice.CreateAccountAsync(Chequing());
        var rental = await alice.AddTagAsync("Workshop");
        await alice.ImportAsync(account.AccountId, new(
        [
            Tx("p1", "2026-08-01", 20m, description: "BELLA PIZZA #12 BARRIE", category: Categories.Uncategorized),
            Tx("p2", "2026-09-01", 25m, description: "BELLA PIZZA #40 KINGSTON", category: Categories.Uncategorized),
            Tx("p3", "2026-10-01", 22m, description: "BELLA PIZZA #12 BARRIE", category: Categories.Shopping), // automatic, newest
            Tx("h1", "2026-09-05", 90m, description: "HOME DEPOT #7001", category: Categories.Home),
            Tx("h2", "2026-10-05", 40m, description: "HOME DEPOT #7002", category: Categories.Home),
            Tx("u1", "2026-09-07", 5m, description: "MYSTERY SHOP", category: Categories.Uncategorized),
            Tx("c1", "2026-09-08", 4m, description: "COFFEE SHOP"), // nothing chosen: not remembered
        ]));
        Assert.True(await alice.ClassifyAsync(account.AccountId, "p1", new(Categories.FoodAndDining, "Restaurants", null)));
        Assert.True(await alice.ClassifyAsync(account.AccountId, "p2", new(Categories.Entertainment, null, null)));
        Assert.True(await alice.ClassifyAsync(account.AccountId, "u1", new(Categories.Uncategorized, null, null)));
        Assert.True(await alice.SetTagsAsync(account.AccountId, "h2", [rental.Id]));

        var choices = await alice.GetMerchantChoicesAsync();

        var pizza = Assert.Single(choices, c => c.MerchantKey == "BELLA PIZZA");
        Assert.Equal((Categories.Entertainment, null, new DateOnly(2026, 9, 1)), (pizza.Category, pizza.SubCategory, pizza.ChosenOn)); // p2, not the automatic p3
        var homeDepot = Assert.Single(choices, c => c.MerchantKey == "HOME DEPOT");
        Assert.Null(homeDepot.Category);
        Assert.Equal([rental.Id], homeDepot.Tags);
        Assert.DoesNotContain(choices, c => c.MerchantKey is "MYSTERY SHOP" or "COFFEE SHOP");
        Assert.Empty(await For("bob").GetMerchantChoicesAsync());

        // A newer untagged one means the tag was for that purchase only.
        await alice.ImportAsync(account.AccountId, new([Tx("h3", "2026-11-05", 12m, description: "HOME DEPOT #7003", category: Categories.Home)]));
        Assert.DoesNotContain(await alice.GetMerchantChoicesAsync(), c => c.MerchantKey == "HOME DEPOT");
    }

    [Fact]
    public async Task AutomaticCategoriesNeedReviewUntilConfirmedOrChanged()
    {
        var account = await For("alice").CreateAccountAsync(Chequing());
        await For("alice").ImportAsync(account.AccountId, new([Tx("a", "2026-09-01", 5m), Tx("b", "2026-09-02", 6m), Tx("c", "2026-09-03", 7m)]));
        Assert.True(await For("alice").ClassifyAsync(account.AccountId, "a", new(Categories.Shopping, null, null)));

        async Task<string[]> Ids(bool? reviewed) =>
            [.. (await For("alice").SearchTransactionsAsync(new TransactionFilter(Reviewed: reviewed))).Select(t => t.Transaction.TransactionId).Order()];

        Assert.Equal(["b", "c"], await Ids(false));
        Assert.Equal(["a"], await Ids(true));
        Assert.Equal(["a", "b", "c"], await Ids(null));

        Assert.Equal(0, await For("bob").MarkReviewedAsync(new([new(account.AccountId, "b")])));
        Assert.Equal(1, await For("alice").MarkReviewedAsync(new([new(account.AccountId, "a"), new(account.AccountId, "b"), new(account.AccountId, "missing")])));
        Assert.Equal(["c"], await Ids(false));
        var b = (await For("alice").SearchTransactionsAsync(new TransactionFilter())).Single(t => t.Transaction.TransactionId == "b");
        Assert.Equal(Categories.FoodAndDining, b.Transaction.Category); // confirmed as it was
    }

    private static AccountRequest Tfsa(string name = "TFSA") =>
        new(name, "Wealthsimple", AccountCategory.InvestmentAccount, AccountType.Brokerage, null, null, null, null);

    private static readonly Holding[] TwoHoldings =
    [
        new("XEQT", "iShares Core Equity", 100m, 34.10m, 3410m, 3000m),
        new("AAPL", "Apple", 5m, 200m, 1000m, 950m, "USD", 1.4m),
    ];

    [Fact]
    public async Task ImportingHoldingsReplacesTheSnapshotAndSetsTheBalance()
    {
        var alice = For("alice");
        var account = await alice.CreateAccountAsync(Tfsa());

        var first = await alice.ImportHoldingsAsync(account.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        Assert.True(first!.BalanceUpdated);
        Assert.Equal(4810m, first.Snapshot.MarketValueCad);

        var updated = await alice.FindAccountAsync(account.AccountId);
        Assert.Equal(4810m, updated!.CurrentBalance);
        Assert.Equal(new DateOnly(2026, 10, 31), DateOnly.FromDateTime(updated.BalanceAsOf!.Value.UtcDateTime));

        var snapshot = Assert.Single(await For("alice").GetHoldingsAsync());
        Assert.Equal(new DateOnly(2026, 10, 31), snapshot.AsOf);
        Assert.Equal(TwoHoldings, snapshot.Holdings);

        // A newer report replaces the holdings; an older one replaces them but leaves the newer balance.
        await For("alice").ImportHoldingsAsync(account.AccountId, new(new DateOnly(2026, 11, 14), [TwoHoldings[0] with { MarketValue = 3500m }]));
        var older = await For("alice").ImportHoldingsAsync(account.AccountId, new(new DateOnly(2026, 9, 30), [TwoHoldings[0] with { MarketValue = 3300m }]));
        Assert.False(older!.BalanceUpdated);
        Assert.Equal(3500m, (await For("alice").FindAccountAsync(account.AccountId))!.CurrentBalance);
        Assert.Equal(3300m, Assert.Single(Assert.Single(await For("alice").GetHoldingsAsync()).Holdings).MarketValue);
    }

    [Fact]
    public async Task HoldingsAreTheOwnersOnlyAndOnlyForInvestmentAccounts()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        var chequing = await For("alice").CreateAccountAsync(Chequing());
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));

        Assert.Empty(await For("bob").GetHoldingsAsync());
        Assert.Null(await For("bob").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings)));
        Assert.False(await For("bob").DeleteHoldingsAsync(tfsa.AccountId));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ImportHoldingsAsync(chequing.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings)));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), [TwoHoldings[1] with { ExchangeRate = 0m }])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), [new Holding(null, " ", 1m, 1m, 1m, null)])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ImportHoldingsAsync(tfsa.AccountId, new(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), TwoHoldings)));
        Assert.Equal(TwoHoldings, Assert.Single(await For("alice").GetHoldingsAsync()).Holdings); // a refused import changes nothing

        Assert.True(await For("alice").DeleteHoldingsAsync(tfsa.AccountId));
        Assert.Empty(await For("alice").GetHoldingsAsync());
    }

    [Fact]
    public async Task DeletingTheAccountOrTheLedgerRemovesItsHoldings()
    {
        var first = await For("alice").CreateAccountAsync(Tfsa("TFSA"));
        var second = await For("alice").CreateAccountAsync(Tfsa("RRSP"));
        await For("alice").ImportHoldingsAsync(first.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        await For("alice").ImportHoldingsAsync(second.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));

        Assert.True(await For("alice").DeleteAccountAsync(first.AccountId));
        Assert.Equal(second.AccountId, Assert.Single(await For("alice").GetHoldingsAsync()).AccountId);

        await For("alice").ClearAsync();
        Assert.Empty(await For("alice").GetHoldingsAsync());
    }

    [Fact]
    public async Task HoldingsGoIntoBackupsAndComeBackOnRestore()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));

        // Through JSON, as a backup file or an upload to the server would go.
        var json = JsonSerializer.Serialize(await For("alice").ExportAsync(), JsonSerializerOptions.Web);
        Assert.DoesNotContain("marketValueCad", json);
        var backup = JsonSerializer.Deserialize<LedgerBackup>(json, JsonSerializerOptions.Web)!;

        await For("bob").RestoreAsync(backup);
        await For("bob").RestoreAsync(backup); // twice adds nothing

        var restored = Assert.Single(await For("bob").GetHoldingsAsync());
        Assert.Equal(new DateOnly(2026, 10, 31), restored.AsOf);
        Assert.Equal(TwoHoldings, restored.Holdings);
        Assert.Single(await For("alice").GetHoldingsAsync());
    }

    [Fact]
    public async Task ARateGoesIntoBackupsAndIsRestoredWhereThereIsNone()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        var rate = new ExchangeRate("USD", 1.43m, new DateOnly(2026, 11, 16), ExchangeRateSource.BankOfCanada);
        await For("alice").SetExchangeRateAsync(rate);

        var json = JsonSerializer.Serialize(await For("alice").ExportAsync(), JsonSerializerOptions.Web);
        Assert.Contains("\"bankOfCanada\"", json, StringComparison.OrdinalIgnoreCase);
        var backup = JsonSerializer.Deserialize<LedgerBackup>(json, JsonSerializerOptions.Web)!;

        await For("bob").RestoreAsync(backup);
        Assert.Equal([rate], await For("bob").GetExchangeRatesAsync());
        Assert.Equal(1.43m, Assert.Single(await For("bob").GetHoldingsAsync()).Holdings[1].ExchangeRate);

        // Carol's own rate is kept.
        var mine = rate with { Rate = 1.38m, Source = ExchangeRateSource.Entered };
        await For("carol").SetExchangeRateAsync(mine);
        await For("carol").RestoreAsync(backup);
        Assert.Equal([mine], await For("carol").GetExchangeRatesAsync());
        Assert.Equal(1.38m, Assert.Single(await For("carol").GetHoldingsAsync()).Holdings[1].ExchangeRate);
    }

    [Fact]
    public async Task ANewRateRevaluesTheHoldingsAndTheBalanceThatWasTheirTotal()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa("TFSA"));
        var rrsp = await For("alice").CreateAccountAsync(Tfsa("RRSP"));
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings)); // 3410 + 1000 USD at 1.4
        await For("alice").ImportHoldingsAsync(rrsp.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        await For("alice").UpdateAccountAsync(rrsp.AccountId, Tfsa("RRSP") with { CurrentBalance = 9000m }); // set by hand

        var saved = await For("alice").SetExchangeRateAsync(new ExchangeRate("usd", 1.5m, new DateOnly(2026, 11, 16), ExchangeRateSource.BankOfCanada));
        Assert.Equal("USD", saved.Currency);
        Assert.Equal([saved], await For("alice").GetExchangeRatesAsync());
        Assert.Empty(await For("bob").GetExchangeRatesAsync());

        var snapshots = await For("alice").GetHoldingsAsync();
        Assert.All(snapshots, s => Assert.Equal(4910m, s.MarketValueCad));
        Assert.Equal(4910m, (await For("alice").FindAccountAsync(tfsa.AccountId))!.CurrentBalance);
        Assert.Equal(9000m, (await For("alice").FindAccountAsync(rrsp.AccountId))!.CurrentBalance);

        // An import uses the ledger's rate, whatever the request says.
        var imported = await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 11, 14), [TwoHoldings[1] with { ExchangeRate = 1m }]));
        Assert.Equal(1500m, imported!.Snapshot.MarketValueCad);
        Assert.Equal(1.5m, Assert.Single(Assert.Single(await For("alice").GetHoldingsAsync(tfsa.AccountId)).Holdings).ExchangeRate);

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetExchangeRateAsync(saved with { Currency = "CAD" }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetExchangeRateAsync(saved with { Rate = 0m }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").SetExchangeRateAsync(saved with { AsOf = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5) }));

        await For("alice").ClearAsync();
        Assert.Empty(await For("alice").GetExchangeRatesAsync());
    }

    private static readonly DateTimeOffset Quoted = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AHoldingNamedOnlyInItsDescriptionIsReadAndPricedByItsTicker()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa("TFSA"));
        // Saved as a statement without symbol or quantity columns left it.
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31),
            [new Holding(null, "REDDIT INC CDR C$HDG (RDDT)", null, 8.73m, 4365m, null), new Holding(Holding.CashSymbol, "Cash Equities", null, null, 554.62m, null)]));

        var read = (await For("alice").GetHoldingsAsync(tfsa.AccountId)).Single().Holdings[0];
        Assert.Equal(("RDDT", 500m), (read.Symbol, read.Quantity!.Value));

        Assert.Equal(new ApplyQuotesResult(Repriced: 1, BalancesUpdated: 1), await For("alice").ApplyQuotesAsync(new([new("RDDT", "CAD", 10m, Quoted)])));
        var priced = (await For("alice").GetHoldingsAsync(tfsa.AccountId)).Single().Holdings[0];
        Assert.Equal((5000m, 500m), (priced.MarketValue, priced.Quantity!.Value));
        Assert.Equal(5554.62m, (await For("alice").FindAccountAsync(tfsa.AccountId))!.CurrentBalance);
    }

    [Theory]
    [InlineData("GREENLANE RENEWABLES INC (GRN )", 0.17, 459.0, "GRN", 2700.0)]
    [InlineData("BROOKFIELD RENEWABLE (BEP.UN)", 40.0, 400.0, "BEP.UN", 10.0)]
    [InlineData("Cash Equities (CAD)", null, 100.0, null, null)]
    [InlineData("SOME GIC 4.5% 2027", null, 1000.0, null, null)]
    [InlineData("Lowercase thing (abc)", 1.0, 10.0, null, null)]
    public void ATickerInBracketsEndingTheDescriptionIsTheSymbol(string description, double? price, double value, string? symbol, double? quantity)
    {
        var holding = new Holding(null, description, null, (decimal?)price, (decimal)value, null).WithListingFromDescription();
        Assert.Equal(symbol, holding.Symbol);
        Assert.Equal((decimal?)quantity, holding.Quantity);
    }

    [Fact]
    public async Task AQuoteRepricesHoldingsAndTheBalanceThatWasTheirTotal()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa("TFSA"));
        var rrsp = await For("alice").CreateAccountAsync(Tfsa("RRSP"));
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings)); // 3410 + 1000 USD at 1.4
        await For("alice").ImportHoldingsAsync(rrsp.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        await For("alice").UpdateAccountAsync(rrsp.AccountId, Tfsa("RRSP") with { CurrentBalance = 9000m }); // set by hand

        // XEQT at 40 in both accounts; a CAD quote for AAPL isn't the USD holding's.
        var result = await For("alice").ApplyQuotesAsync(new([new("XEQT", "CAD", 40m, Quoted), new("AAPL", "CAD", 300m, Quoted)]));
        Assert.Equal(new ApplyQuotesResult(Repriced: 2, BalancesUpdated: 1), result);

        var xeqt = (await For("alice").GetHoldingsAsync(tfsa.AccountId)).Single().Holdings[0];
        Assert.Equal(40m, xeqt.Price);
        Assert.Equal(4000m, xeqt.MarketValue);
        Assert.Equal(HoldingPriceSource.Quote, xeqt.PriceSource);
        Assert.Equal(Quoted, xeqt.PriceAsOf);
        Assert.Equal(3000m, xeqt.BookValue);

        var apple = (await For("alice").GetHoldingsAsync(tfsa.AccountId)).Single().Holdings[1];
        Assert.Equal((200m, HoldingPriceSource.Statement), (apple.Price!.Value, apple.PriceSource));

        // The balance that was the total follows (4000 + 1400); its date stays the statement's.
        var account = await For("alice").FindAccountAsync(tfsa.AccountId);
        Assert.Equal(5400m, account!.CurrentBalance);
        Assert.Equal(new DateOnly(2026, 10, 31), DateOnly.FromDateTime(account.BalanceAsOf!.Value.UtcDateTime));
        Assert.Equal(9000m, (await For("alice").FindAccountAsync(rrsp.AccountId))!.CurrentBalance);

        // The same quote again changes nothing, and nobody else's holdings are touched.
        Assert.Equal(new ApplyQuotesResult(0, 0), await For("alice").ApplyQuotesAsync(new([new("XEQT", "CAD", 40m, Quoted)])));
        Assert.Equal(new ApplyQuotesResult(0, 0), await For("bob").ApplyQuotesAsync(new([new("XEQT", "CAD", 1m, Quoted)])));
        Assert.Equal(40m, (await For("alice").GetHoldingsAsync(tfsa.AccountId)).Single().Holdings[0].Price);

        // A new statement brings back its own prices.
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 11, 14), TwoHoldings));
        var reimported = (await For("alice").GetHoldingsAsync(tfsa.AccountId)).Single().Holdings[0];
        Assert.Equal((34.10m, HoldingPriceSource.Statement, (DateTimeOffset?)null), (reimported.Price!.Value, reimported.PriceSource, reimported.PriceAsOf));
    }

    [Fact]
    public async Task CashNamedByItsCurrencyBecomesCashWhenPricesAreUpdated()
    {
        // Interactive Brokers' cash lines in a CAD account, as imported before they were read as
        // cash: US$1,000 priced at the rate into CAD and valued in it, and CA$50 at 1.
        await For("alice").SetExchangeRateAsync(new ExchangeRate("USD", 1.35m, new DateOnly(2026, 11, 16)));
        var ibkr = await For("alice").CreateAccountAsync(Tfsa("IBKR"));
        await For("alice").ImportHoldingsAsync(ibkr.AccountId, new(new DateOnly(2026, 10, 31),
        [
            TwoHoldings[0],
            new("USD", null, 1000m, 1.4m, 1400m, null),
            new("CAD", null, 50m, 1m, 50m, null),
        ]));
        Assert.Equal(4860m, (await For("alice").FindAccountAsync(ibkr.AccountId))!.CurrentBalance);

        // No quotes needed: the cash lines are rewritten, and the balance that was their total follows.
        Assert.Equal(new ApplyQuotesResult(Repriced: 2, BalancesUpdated: 1), await For("alice").ApplyQuotesAsync(new([])));
        var holdings = (await For("alice").GetHoldingsAsync(ibkr.AccountId)).Single().Holdings;
        Assert.Equal(new Holding(Holding.CashSymbol, "USD cash", null, null, 1000m, null, "USD", 1.35m), holdings[1]);
        Assert.Equal(new Holding(Holding.CashSymbol, "CAD cash", null, null, 50m, null), holdings[2]);
        Assert.Equal(3410m + 1350m + 50m, (await For("alice").FindAccountAsync(ibkr.AccountId))!.CurrentBalance);

        Assert.Equal(new ApplyQuotesResult(0, 0), await For("alice").ApplyQuotesAsync(new([])));
    }

    [Fact]
    public async Task AnOptionIsValuedAt100SharesAContract()
    {
        // Interactive Brokers' option line as a report valued it: 3 contracts × US$26.75, without the 100 shares each.
        var ibkr = await For("alice").CreateAccountAsync(Tfsa("IBKR"));
        await For("alice").ImportHoldingsAsync(ibkr.AccountId, new(new DateOnly(2026, 11, 14),
            [new("INTC 17DEC27 35 C", null, 3m, 26.75m, 80.25m, null, "USD", 1.4m)]));
        var imported = (await For("alice").GetHoldingsAsync(ibkr.AccountId)).Single().Holdings.Single();
        Assert.Equal(8025m, imported.MarketValue);

        // Yahoo's price, by the symbol as the report wrote it: 3 × 100 × 25.85.
        Assert.Equal(1, (await For("alice").ApplyQuotesAsync(new([new("INTC 17DEC27 35 C", "USD", 25.85m, Quoted)]))).Repriced);
        var priced = (await For("alice").GetHoldingsAsync(ibkr.AccountId)).Single().Holdings.Single();
        Assert.Equal((25.85m, 7755m, HoldingPriceSource.Quote), (priced.Price!.Value, priced.MarketValue, priced.PriceSource));
    }

    [Fact]
    public async Task AQuoteOlderThanTheStatementIsSkipped()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 11, 17), TwoHoldings));

        Assert.Equal(new ApplyQuotesResult(0, 0), await For("alice").ApplyQuotesAsync(new([new("XEQT", "CAD", 40m, Quoted)])));
        Assert.Equal(34.10m, (await For("alice").GetHoldingsAsync()).Single().Holdings[0].Price);
    }

    [Fact]
    public async Task QuotesThatCantBeRightAreRefused()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));

        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ApplyQuotesAsync(new([new("XEQT", "CAD", 0m, Quoted)])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ApplyQuotesAsync(new([new("XEQT", "CAD", 40m, DateTimeOffset.UtcNow.AddDays(5))])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ApplyQuotesAsync(new([new("XEQT", "C", 40m, Quoted)])));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").ApplyQuotesAsync(new([new("", "CAD", 40m, Quoted)])));
        Assert.Equal(TwoHoldings, (await For("alice").GetHoldingsAsync()).Single().Holdings);
    }

    [Fact]
    public async Task QuotedPricesGoIntoBackupsAndComeBackOnRestore()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        await For("alice").ImportHoldingsAsync(tfsa.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        await For("alice").ApplyQuotesAsync(new([new("XEQT", "CAD", 40m, Quoted)]));

        var json = JsonSerializer.Serialize(await For("alice").ExportAsync(), JsonSerializerOptions.Web);
        Assert.Contains("\"quote\"", json, StringComparison.OrdinalIgnoreCase);
        await For("bob").RestoreAsync(JsonSerializer.Deserialize<LedgerBackup>(json, JsonSerializerOptions.Web)!);

        var restored = (await For("bob").GetHoldingsAsync()).Single().Holdings;
        Assert.Equal((40m, HoldingPriceSource.Quote, (DateTimeOffset?)Quoted), (restored[0].Price!.Value, restored[0].PriceSource, restored[0].PriceAsOf));
        Assert.Equal(HoldingPriceSource.Statement, restored[1].PriceSource);
    }

    [Fact]
    public async Task AnAccountsCurrencyIsSetWhenAddingOrEditingIt()
    {
        var tfsa = await For("alice").CreateAccountAsync(Tfsa());
        Assert.Equal("CAD", tfsa.Currency.CurrencyCode);
        Assert.Equal("USD", (await For("alice").CreateAccountAsync(Chequing("US chequing") with { CurrencyCode = " usd " })).Currency.CurrencyCode);

        Assert.Equal("USD", (await For("alice").UpdateAccountAsync(tfsa.AccountId, Tfsa() with { CurrencyCode = "usd" }))!.Currency.CurrencyCode);
        Assert.Equal("USD", (await For("alice").FindAccountAsync(tfsa.AccountId))!.Currency.CurrencyCode);
        Assert.Null(await For("bob").UpdateAccountAsync(tfsa.AccountId, Tfsa() with { CurrencyCode = "CAD" }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").UpdateAccountAsync(tfsa.AccountId, Tfsa() with { CurrencyCode = "US dollars" }));
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").CreateAccountAsync(Tfsa() with { CurrencyCode = "$" }));

        // An edit that doesn't say keeps the currency.
        await For("alice").UpdateAccountAsync(tfsa.AccountId, Tfsa("Renamed"));
        Assert.Equal("USD", (await For("alice").FindAccountAsync(tfsa.AccountId))!.Currency.CurrencyCode);
    }

    [Fact]
    public async Task AnAccountsBalanceIsInItsCurrencyAndNetWorthInCanadianDollars()
    {
        await For("alice").SetExchangeRateAsync(new ExchangeRate("USD", 1.4m, new DateOnly(2026, 11, 16)));
        var usd = await For("alice").CreateAccountAsync(Tfsa("US TFSA") with { CurrencyCode = "USD" });

        // 1000 USD of AAPL, and 3410 CAD of XEQT that's 2435.71 USD.
        var imported = await For("alice").ImportHoldingsAsync(usd.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        Assert.True(imported!.BalanceUpdated);
        Assert.Equal(3435.71m, (await For("alice").FindAccountAsync(usd.AccountId))!.CurrentBalance);

        // A new USD rate changes what the CAD holding is worth in US dollars, not the USD one.
        await For("alice").SetExchangeRateAsync(new ExchangeRate("USD", 1.5m, new DateOnly(2026, 11, 17)));
        Assert.Equal(3273.33m, (await For("alice").FindAccountAsync(usd.AccountId))!.CurrentBalance);

        await For("alice").CreateAccountAsync(Chequing() with { CurrentBalance = 100m });
        var summary = await For("alice").GetDashboardAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        Assert.Equal(100m + 4910m, summary.NetWorth.Assets); // 3273.33 USD at 1.5 is 4910 CAD (to the cent: 4909.995)
    }

    [Fact]
    public async Task ChangingAnAccountsCurrencyMovesTheHoldingsThatWereInTheOldOne()
    {
        await For("alice").SetExchangeRateAsync(new ExchangeRate("USD", 1.4m, new DateOnly(2026, 11, 16)));
        var account = await For("alice").CreateAccountAsync(Tfsa("IBKR"));

        // Imported while the account was CAD: the XEQT row didn't say, so it was read as CAD.
        await For("alice").ImportHoldingsAsync(account.AccountId, new(new DateOnly(2026, 10, 31), TwoHoldings));
        Assert.Equal(4810m, (await For("alice").FindAccountAsync(account.AccountId))!.CurrentBalance);

        var updated = await For("alice").UpdateAccountAsync(account.AccountId, Tfsa("IBKR") with { CurrentBalance = 4810m, CurrencyCode = "USD" });
        var holdings = Assert.Single(await For("alice").GetHoldingsAsync(account.AccountId)).Holdings;
        Assert.All(holdings, h => Assert.Equal(("USD", 1.4m), (h.Currency, h.ExchangeRate)));
        Assert.Equal(4410m, updated!.CurrentBalance); // the holdings' total, now in US dollars
        Assert.Equal(4410m, (await For("alice").FindAccountAsync(account.AccountId))!.CurrentBalance);

        // A balance the edit changes is kept as entered.
        await For("alice").UpdateAccountAsync(account.AccountId, Tfsa("IBKR") with { CurrentBalance = 5000m, CurrencyCode = "CAD" });
        Assert.Equal(5000m, (await For("alice").FindAccountAsync(account.AccountId))!.CurrentBalance);
        Assert.All(Assert.Single(await For("alice").GetHoldingsAsync(account.AccountId)).Holdings, h => Assert.Equal(("CAD", 1m), (h.Currency, h.ExchangeRate)));

        // Moving holdings to a currency without a rate is refused, and changes nothing.
        await Assert.ThrowsAsync<LedgerValidationException>(() => For("alice").UpdateAccountAsync(account.AccountId, Tfsa("IBKR") with { CurrencyCode = "EUR" }));
        Assert.Equal("CAD", (await For("alice").FindAccountAsync(account.AccountId))!.Currency.CurrencyCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-321)]   // a different month and day of the week
    public async Task TheSampleHouseholdLoadsAsAYearOfRecentActivity(int daysAgo)
    {
        // Holdings can't be dated in the future, so "today" is never later than the real one.
        var today = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysAgo);
        var categorizer = new Core.Categorization.TransactionCategorizer();
        var suggester = new TaxSuggester();
        var ledger = For(LedgerDbContext.LocalOwner);

        Assert.False(Core.Import.SampleHousehold.IsLoaded(await ledger.GetAccountsAsync()));
        var all = new List<LedgerTransaction>();
        foreach (var sample in Core.Import.SampleHousehold.Create(today))
        {
            var account = await ledger.CreateAccountAsync(sample.Account);
            var transactions = Core.Import.SampleHousehold.Transactions(sample, account.AccountId, categorizer, suggester);
            all.AddRange(transactions);
            if (transactions.Count > 0)
            {
                var result = await ledger.ImportAsync(account.AccountId, new ImportTransactionsRequest(transactions, Source: "sample"));
                Assert.Equal(transactions.Count, result!.Added); // ids are unique, so nothing is dropped as a duplicate
            }

            if (sample.Holdings.Count > 0) await ledger.ImportHoldingsAsync(account.AccountId, new(today, sample.Holdings));
        }

        var accounts = await ledger.GetAccountsAsync();
        Assert.True(Core.Import.SampleHousehold.IsLoaded(accounts));
        Assert.Equal(5, accounts.Count);
        Assert.All(accounts, a => Assert.True(a.CurrentBalance > 0, $"{a.Nickname} has a balance"));

        // A year of it, none of it after today, and the same household every time.
        var dates = all.Select(t => DateOnly.FromDateTime(t.Transaction.TransactionTimestamp!.Value.UtcDateTime)).ToList();
        Assert.True(dates.Max() <= today);
        Assert.True(dates.Min() >= new DateOnly(today.Year, today.Month, 1).AddMonths(1 - Core.Import.SampleHousehold.Months));
        Assert.Equal(
            Core.Import.SampleHousehold.Create(today).SelectMany(s => s.Transactions),
            Core.Import.SampleHousehold.Create(today).SelectMany(s => s.Transactions));

        // The built-in rules know most of it, leave one merchant to categorize, and suggest tax claims.
        Assert.Equal(["THE HOLLOW TREE CAFE"], all.Where(t => t.Transaction.Category == Categories.Uncategorized)
            .Select(t => t.Transaction.Description![..20]).Distinct());
        Assert.Equal(5, all.Count(t => t.Tags is { Count: > 0 }));
        Assert.Contains(all, t => t.Transaction.Description == "SUNSHINE DAYCARE INC" && t.Tax is not null);
        Assert.Contains(all, t => t.Transaction.Description == "BRIGHT SMILES DENTAL" && t.Tax is not null);

        var dashboard = await ledger.GetDashboardAsync(today.AddMonths(-11), today);
        Assert.True(dashboard.Income > dashboard.Expenses);
        Assert.True(dashboard.NetWorth.NetWorth > 0);
    }
}
