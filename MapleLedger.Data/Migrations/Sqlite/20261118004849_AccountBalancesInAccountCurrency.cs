using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <summary>
    /// Balances are now in their accounts' own currencies. Until now only investment accounts could
    /// be in another currency than CAD, and their balances were kept in Canadian dollars: convert
    /// them at the ledger's rate (one without a rate is left as it is). Decimals are TEXT here, so
    /// the result is written back as text with two decimals.
    /// </summary>
    public partial class AccountBalancesInAccountCurrency : Migration
    {
        private const string Where = """
            WHERE AccountCategory = 'InvestmentAccount' AND CurrencyCode <> 'CAD' AND CurrentBalance IS NOT NULL
              AND EXISTS (SELECT 1 FROM ExchangeRates r WHERE r.OwnerId = Accounts.OwnerId AND r.Currency = Accounts.CurrencyCode AND CAST(r.Rate AS REAL) > 0)
            """;

        private const string Rate = "(SELECT CAST(r.Rate AS REAL) FROM ExchangeRates r WHERE r.OwnerId = Accounts.OwnerId AND r.Currency = Accounts.CurrencyCode)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE Accounts SET CurrentBalance = printf('%.2f', CAST(CurrentBalance AS REAL) / {Rate}) {Where};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE Accounts SET CurrentBalance = printf('%.2f', CAST(CurrentBalance AS REAL) * {Rate}) {Where};");
        }
    }
}
