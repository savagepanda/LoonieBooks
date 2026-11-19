using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <summary>
    /// Balances are now in their accounts' own currencies. Until now only investment accounts could
    /// be in another currency than CAD, and their balances were kept in Canadian dollars: convert
    /// them at the ledger's rate (one without a rate is left as it is).
    /// </summary>
    public partial class AccountBalancesInAccountCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE a SET a.CurrentBalance = ROUND(a.CurrentBalance / r.Rate, 2)
                FROM Accounts a
                JOIN ExchangeRates r ON r.OwnerId = a.OwnerId AND r.Currency = a.CurrencyCode
                WHERE a.AccountCategory = 'InvestmentAccount' AND a.CurrencyCode <> 'CAD' AND a.CurrentBalance IS NOT NULL AND r.Rate > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE a SET a.CurrentBalance = ROUND(a.CurrentBalance * r.Rate, 2)
                FROM Accounts a
                JOIN ExchangeRates r ON r.OwnerId = a.OwnerId AND r.Currency = a.CurrencyCode
                WHERE a.AccountCategory = 'InvestmentAccount' AND a.CurrencyCode <> 'CAD' AND a.CurrentBalance IS NOT NULL AND r.Rate > 0;
                """);
        }
    }
}
