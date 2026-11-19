using MapleLedger.ApiService.Data;
using MapleLedger.Core;
using MapleLedger.Core.Desktop;
using MapleLedger.Data;
using MapleLedger.Fdx;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.UnitTests;

/// <summary>
/// The desktop app's ledger file being brought up to date when a new version starts: a copy first,
/// put back if updating fails, and a ledger from a newer version left alone.
/// </summary>
public sealed class SqliteLedgerUpgradeTests : IDisposable
{
    /// <summary>The last migration of an "older version" ledger in these tests; three more come after it.</summary>
    private const string OldVersion = "20261115135441_AddKeptAsDistinct";

    private readonly string _folder = Directory.CreateTempSubdirectory("mapleledger-upgrade-").FullName;
    private readonly string _ledger;
    private readonly DbContextOptions<LedgerDbContext> _options;

    public SqliteLedgerUpgradeTests()
    {
        _ledger = Path.Combine(_folder, "ledger.db");
        _options = Options(_ledger);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AnOldLedgerIsCopiedThenUpdatedWithItsDataKept()
    {
        await CreateOldLedgerAsync();

        SqliteLedgerUpgrade.Result result;
        await using (var db = new LedgerDbContext(_options))
        {
            result = await SqliteLedgerUpgrade.RunAsync(db, TestContext.Current.CancellationToken);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(MigrationsAfter(OldVersion), result.Applied);
        Assert.Equal(["Chequing"], (await AccountsAsync()).Select(a => a.Nickname));

        // The copy is the ledger as the old version left it.
        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(Path.Combine(_folder, "backups", "ledger-"), result.BackupPath);
        Assert.EndsWith("-before-update.db", result.BackupPath);
        Assert.Equal(OldVersion, await LastMigrationAsync(result.BackupPath));
        Assert.Equal(1L, await ScalarAsync(result.BackupPath, "SELECT COUNT(*) FROM Accounts"));
        Assert.Equal("delete", await ScalarAsync(result.BackupPath, "PRAGMA journal_mode"));
        Assert.Equal(["ledger.db", "ledger.db-shm", "ledger.db-wal"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
        Assert.Equal([Path.GetFileName(result.BackupPath)], Directory.GetFiles(Path.Combine(_folder, "backups")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnUpToDateLedgerIsLeftAsItIs()
    {
        await CreateOldLedgerAsync();
        await using (var db = new LedgerDbContext(_options))
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = new LedgerDbContext(_options))
        {
            var result = await SqliteLedgerUpgrade.RunAsync(db, TestContext.Current.CancellationToken);
            Assert.Empty(result.Applied);
            Assert.Null(result.BackupPath);
        }

        Assert.False(Directory.Exists(Path.Combine(_folder, "backups")));
    }

    [Fact]
    public async Task ANewLedgerIsCreatedWithoutACopy()
    {
        await using var db = new LedgerDbContext(_options);
        var result = await SqliteLedgerUpgrade.RunAsync(db, TestContext.Current.CancellationToken);

        Assert.Equal(db.Database.GetMigrations(), result.Applied);
        Assert.Null(result.BackupPath);
        Assert.False(Directory.Exists(Path.Combine(_folder, "backups")));
    }

    [Fact]
    public async Task AFailedUpdatePutsTheLedgerBackAsItWas()
    {
        await CreateOldLedgerAsync();

        // Something in the way of the second pending migration (AddTaxSituations creates this
        // table), after the first (AddTags) has already changed the schema.
        await ExecuteAsync(_ledger, "CREATE TABLE TaxSituations (Note TEXT); INSERT INTO TaxSituations VALUES ('in the way');");

        await using (var db = new LedgerDbContext(_options))
        {
            var error = await Assert.ThrowsAsync<LedgerUpgradeException>(() => SqliteLedgerUpgrade.RunAsync(db, TestContext.Current.CancellationToken));
            Assert.Equal(ApiExitCodes.UpgradeFailed, error.ExitCode);
            Assert.Contains("put back exactly as it was", error.Message);
            Assert.Contains("-before-update.db", error.Message);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(OldVersion, await LastMigrationAsync(_ledger));
        Assert.Equal(0L, await ScalarAsync(_ledger, "SELECT COUNT(*) FROM pragma_table_info('Transactions') WHERE name = 'Tags'"));
        Assert.Equal("in the way", await ScalarAsync(_ledger, "SELECT Note FROM TaxSituations"));
        Assert.Equal(1L, await ScalarAsync(_ledger, "SELECT COUNT(*) FROM Accounts"));
        Assert.Equal("ok", await ScalarAsync(_ledger, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task ALedgerFromANewerVersionIsNotTouched()
    {
        await CreateOldLedgerAsync();
        await using (var db = new LedgerDbContext(_options))
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(_ledger, "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '99.0.0')");
        const string history = "SELECT group_concat(MigrationId) FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId)";
        var before = await ScalarAsync(_ledger, history);

        await using (var db = new LedgerDbContext(_options))
        {
            var error = await Assert.ThrowsAsync<LedgerUpgradeException>(() => SqliteLedgerUpgrade.RunAsync(db, TestContext.Current.CancellationToken));
            Assert.Equal(ApiExitCodes.LedgerFromNewerVersion, error.ExitCode);
            Assert.Contains("29991231000000_FromTheFuture", error.Message);
        }

        Assert.Equal(before, await ScalarAsync(_ledger, history));
        Assert.Equal(1L, await ScalarAsync(_ledger, "SELECT COUNT(*) FROM Accounts"));
        Assert.False(Directory.Exists(Path.Combine(_folder, "backups")));
    }

    [Fact]
    public async Task OnlyTheLatestCopiesAreKept()
    {
        var backups = Directory.CreateDirectory(Path.Combine(_folder, "backups")).FullName;
        var old = Enumerable.Range(1, SqliteLedgerUpgrade.BackupsKept + 2)
            .Select(i => Path.Combine(backups, $"ledger-2020-01-0{i}-000000-before-update.db"))
            .ToList();
        for (var i = 0; i < old.Count; i++)
        {
            await File.WriteAllTextAsync(old[i], "old", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(old[i], new DateTime(2020, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }

        var unrelated = Path.Combine(backups, "my own copy.db");
        await File.WriteAllTextAsync(unrelated, "mine", TestContext.Current.CancellationToken);

        await CreateOldLedgerAsync();
        SqliteLedgerUpgrade.Result result;
        await using (var db = new LedgerDbContext(_options))
        {
            result = await SqliteLedgerUpgrade.RunAsync(db, TestContext.Current.CancellationToken);
        }

        var kept = Directory.GetFiles(backups, "ledger-*-before-update.db");
        Assert.Equal(SqliteLedgerUpgrade.BackupsKept, kept.Length);
        Assert.Contains(result.BackupPath, kept);
        Assert.Equal(old.TakeLast(SqliteLedgerUpgrade.BackupsKept - 1).Order(), kept.Where(k => k != result.BackupPath).Order());
        Assert.True(File.Exists(unrelated));
    }

    private static DbContextOptions<LedgerDbContext> Options(string path) =>
        new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite($"Data Source={path}").Options;

    /// <summary>A ledger as an older version left it: in WAL mode like the desktop's, with an account in it.</summary>
    private async Task CreateOldLedgerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = new LedgerDbContext(_options) { OwnerId = "desktop" })
        {
            await db.Database.MigrateAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await new LedgerService(db).CreateAccountAsync(
                new AccountRequest("Chequing", "TD Canada Trust", AccountCategory.DepositAccount, AccountType.Checking, "1234", 1000m, null, null));
        }

        await using (var db = new LedgerDbContext(_options))
        {
            await db.Database.MigrateAsync(OldVersion, ct);
        }

        Assert.Equal(OldVersion, await LastMigrationAsync(_ledger));
    }

    private async Task<IReadOnlyList<Account>> AccountsAsync()
    {
        await using var db = new LedgerDbContext(_options) { OwnerId = "desktop" };
        return await new LedgerService(db).GetAccountsAsync();
    }

    private static List<string> MigrationsAfter(string migration)
    {
        using var db = new LedgerDbContext(Options(":memory:"));
        return db.Database.GetMigrations().SkipWhile(m => m != migration).Skip(1).ToList();
    }

    private static async Task<object?> LastMigrationAsync(string path) =>
        await ScalarAsync(path, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1");

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
