using MapleLedger.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MapleLedger.ApiService.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the SQL Server model without starting the Aspire-configured host.
/// The SQL Server migrations live in this assembly; the browser's SQLite ones live in MapleLedger.Data.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlServer(
                "Server=localhost;Database=MapleLedger;Trusted_Connection=True;TrustServerCertificate=True",
                sql => sql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .Options);
}
