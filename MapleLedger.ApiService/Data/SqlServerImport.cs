using MapleLedger.Data;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.ApiService.Data;

/// <summary>
/// Moves a ledger from SQL Server into this API's (SQLite) database, once: desktop installs that
/// used SQL Server now keep their ledger in a SQLite file LoonieBooks looks after. Every owner's
/// rows are copied with all their LoonieBooks data (tax tags, categories, receipts), plus the
/// users table. The SQL Server database is only read, never changed.
/// <para>
/// Runs only while this database holds no ledger rows, and in one transaction, so a failed or
/// repeated run can't leave half a ledger or duplicates.
/// </para>
/// </summary>
public static class SqlServerImport
{
    /// <summary>The tables with an owner, parents before children.</summary>
    private static readonly string[] OwnedTables = ["Accounts", "ImportBatches", "Transactions", "Receipts", "HouseholdMembers", "CustomCategories", "Tags", "TaxSituations", "Holdings", "ExchangeRates", "Properties", "HousePriceIndex", "PriceHistory"];

    public sealed record Result(bool Copied, IReadOnlyDictionary<string, int> Rows);

    public static async Task<Result> RunAsync(LedgerDbContext target, string sourceConnectionString, CancellationToken ct = default)
    {
        if (await CountAsync(target, ct) is var existing && existing.Values.Any(n => n > 0))
        {
            return new Result(false, existing);
        }

        var sourceOptions = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlServer(sourceConnectionString, sql => sql.MigrationsAssembly(typeof(SqlServerImport).Assembly.GetName().Name))
            .Options;

        await using (var check = new LedgerDbContext(sourceOptions))
        {
            if ((await check.Database.GetPendingMigrationsAsync(ct)).Any())
            {
                throw new InvalidOperationException(
                    "The SQL Server database is from an older LoonieBooks. Open it once with the previous version (or the dev app) to update it, then try again.");
            }
        }

        var owners = await OwnersAsync(sourceOptions, ct);

        await using var transaction = await target.Database.BeginTransactionAsync(ct);
        foreach (var owner in owners)
        {
            await using var source = new LedgerDbContext(sourceOptions) { OwnerId = owner };
            target.OwnerId = owner;
            await CopyAsync(source.Accounts, source, target, ct);
            await CopyAsync(source.ImportBatches, source, target, ct);
            await CopyAsync(source.Transactions, source, target, ct);
            await CopyAsync(source.Receipts, source, target, ct);
            await CopyAsync(source.HouseholdMembers, source, target, ct);
            await CopyAsync(source.CustomCategories, source, target, ct);
            await CopyAsync(source.LedgerTags, source, target, ct);
            await CopyAsync(source.TaxSituations, source, target, ct);
            await CopyAsync(source.Holdings, source, target, ct);
            await CopyAsync(source.ExchangeRates, source, target, ct);
            await CopyAsync(source.Properties, source, target, ct);
            await CopyAsync(source.HousePriceIndex, source, target, ct);
            await CopyAsync(source.PriceHistory, source, target, ct);
        }

        await using (var source = new LedgerDbContext(sourceOptions))
        {
            target.Users.AddRange(await source.Users.AsNoTracking().ToListAsync(ct));
            await target.SaveChangesAsync(ct);
            target.ChangeTracker.Clear();
        }

        target.OwnerId = null;

        // Every row arrived, or nothing is kept.
        await using (var source = new LedgerDbContext(sourceOptions))
        {
            var expected = await CountAsync(source, ct);
            var copied = await CountAsync(target, ct);
            if (!expected.OrderBy(e => e.Key).SequenceEqual(copied.OrderBy(c => c.Key)))
            {
                throw new InvalidOperationException(
                    "Copying the ledger from SQL Server didn't copy every row, so nothing was kept. " +
                    $"Expected {Describe(expected)}; copied {Describe(copied)}.");
            }

            await transaction.CommitAsync(ct);
            return new Result(true, copied);
        }
    }

    /// <summary>
    /// Copies one owner's rows of one table. Rows are read through the owner's query filter, and
    /// LoonieBooks' shadow properties (tax tags, manual categories, import ids) go with them.
    /// </summary>
    private static async Task CopyAsync<T>(IQueryable<T> rows, LedgerDbContext source, LedgerDbContext target, CancellationToken ct)
        where T : class
    {
        foreach (var row in await rows.ToListAsync(ct))
        {
            var shadows = source.Entry(row).Properties
                .Where(p => p.Metadata.IsShadowProperty() && p.Metadata.Name != LedgerDbContext.Owner)
                .Select(p => (p.Metadata.Name, p.CurrentValue))
                .ToList();
            source.Entry(row).State = EntityState.Detached;

            var entry = target.Add(row);
            foreach (var (name, value) in shadows)
            {
                entry.Property(name).CurrentValue = value;
            }
        }

        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
    }

    private static async Task<List<string>> OwnersAsync(DbContextOptions<LedgerDbContext> options, CancellationToken ct)
    {
        await using var source = new LedgerDbContext(options);
        var union = string.Join(" UNION ", OwnedTables.Select(t => $"SELECT OwnerId AS Value FROM {t}"));
        return await source.Database.SqlQueryRaw<string>(union).ToListAsync(ct);
    }

    /// <summary>Rows per table for every owner, counted in SQL so no query filter applies.</summary>
    private static async Task<Dictionary<string, int>> CountAsync(LedgerDbContext db, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>();
        foreach (var table in OwnedTables.Append("Users"))
        {
#pragma warning disable EF1002 // table names come from the constant list above, never from input
            counts[table] = await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").SingleAsync(ct);
#pragma warning restore EF1002
        }

        return counts;
    }

    private static string Describe(IReadOnlyDictionary<string, int> counts) =>
        string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"));
}
