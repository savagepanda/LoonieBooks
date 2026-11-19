using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitIncomeCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The single "Income" category splits into Salary, Business Income and Other Income.
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Salary' WHERE Category = 'Income' AND SubCategory = 'Paycheque'");
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Other Income' WHERE Category = 'Income'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Income' WHERE Category IN ('Salary', 'Business Income', 'Other Income')");
        }
    }
}
