using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MapleLedger.Data;

/// <summary>
/// Lets <c>dotnet ef</c> generate the browser (SQLite) migrations, which live in this assembly:
/// <code>dotnet ef migrations add &lt;Name&gt; --project MapleLedger.Data --output-dir Migrations/Sqlite</code>
/// The server's SQL Server migrations live in MapleLedger.ApiService with their own factory.
/// </summary>
internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
