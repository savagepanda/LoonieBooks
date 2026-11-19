using System.Globalization;
using MapleLedger.Core.Desktop;
using MapleLedger.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.ApiService.Data;

/// <summary>Opening the ledger stopped before the site could start; <see cref="ExitCode"/> tells the launcher why.</summary>
public sealed class LedgerUpgradeException(int exitCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Brings a desktop ledger (a SQLite file) up to this version's schema when the API starts, so a
/// new version only has to be opened. Before applying any migration to a ledger that already has
/// data, it copies the whole database to <c>backups\</c> beside it (SQLite's online backup, so the
/// WAL is included). If a migration fails, that copy is written back, so the ledger is exactly as
/// it was and the previous version still opens it. A ledger already migrated by a newer version
/// isn't touched at all.
/// </summary>
public static class SqliteLedgerUpgrade
{
    /// <summary>How many pre-update copies to keep; older ones are deleted after a new one is taken.</summary>
    public const int BackupsKept = 5;

    private const string BackupSuffix = "-before-update.db";

    /// <param name="Applied">The migrations this run applied, oldest first (empty when the ledger was up to date).</param>
    /// <param name="BackupPath">The copy taken before migrating, or null when none was needed (up to date, or a new ledger).</param>
    public sealed record Result(IReadOnlyList<string> Applied, string? BackupPath);

    public static async Task<Result> RunAsync(LedgerDbContext db, CancellationToken ct = default)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();

        var unknown = applied.Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new LedgerUpgradeException(ApiExitCodes.LedgerFromNewerVersion,
                $"This ledger was last opened by a newer version of LoonieBooks (it has {string.Join(", ", unknown)}), " +
                "so this version won't open it. Use the newer version. The ledger wasn't changed.");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0) return new Result([], null);

        // A new ledger has nothing to lose.
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The ledger has no connection string.");
        var backup = applied.Count > 0 ? Backup(connectionString) : null;

        try
        {
            await db.Database.MigrateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (backup is null) throw;

            try
            {
                SqliteConnection.ClearAllPools();
                Copy(FileConnection(backup), connectionString);
            }
            catch (Exception restoreError)
            {
                throw new LedgerUpgradeException(ApiExitCodes.UpgradeFailedNotRestored,
                    $"Updating the ledger to this version failed ({ex.Message.TrimEnd('.')}), and putting it back failed too ({restoreError.Message.TrimEnd('.')}). " +
                    $"A copy from just before the update is at {backup}: with LoonieBooks closed, put it in place of the ledger file " +
                    "(and delete the -wal and -shm files beside it), then open the previous version.", ex);
            }

            throw new LedgerUpgradeException(ApiExitCodes.UpgradeFailed,
                $"Updating the ledger to this version failed ({ex.Message.TrimEnd('.')}). It was put back exactly as it was, so the previous version " +
                $"still opens it. A copy from just before the update is also at {backup}.", ex);
        }

        return new Result(pending, backup);
    }

    /// <summary>Where the pre-update copies of a ledger go: <c>backups\</c> beside it.</summary>
    public static string BackupFolder(string connectionString) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DataSource(connectionString)))!, "backups");

    private static string Backup(string connectionString)
    {
        var folder = BackupFolder(connectionString);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(DataSource(connectionString));
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{name}-{stamp}{BackupSuffix}");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name}-{stamp}-{n}{BackupSuffix}");
        }

        Copy(connectionString, FileConnection(path));

        // One self-contained file, without -wal and -shm files beside it (the copy takes the ledger's WAL mode).
        using (var copy = new SqliteConnection(FileConnection(path)))
        {
            copy.Open();
            using var command = copy.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            command.ExecuteNonQuery();
        }

        foreach (var old in Directory.GetFiles(folder, $"{name}-*{BackupSuffix}")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(BackupsKept))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // In use or read-only: try again next update.
            }
        }

        return path;
    }

    /// <summary>
    /// Copies one SQLite database over another with SQLite's backup API, which reads a consistent
    /// snapshot (WAL included) and replaces every page of the destination.
    /// </summary>
    private static void Copy(string sourceConnectionString, string destinationConnectionString)
    {
        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static string FileConnection(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private static string DataSource(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString).DataSource;
}
