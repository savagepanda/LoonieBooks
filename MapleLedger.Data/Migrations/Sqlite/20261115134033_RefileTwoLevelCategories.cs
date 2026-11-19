using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class RefileTwoLevelCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TwoLevelCategories again, for rows filed the old way after it ran: an older app writing
            // to a database a newer one had already migrated. Re-running it is harmless.
            migrationBuilder.Sql("UPDATE Transactions SET SubCategory = 'Salary' WHERE Category = 'Income' AND SubCategory = 'Paycheque'");
            migrationBuilder.Sql("UPDATE Transactions SET SubCategory = 'Other Income' WHERE Category = 'Income' AND SubCategory IN ('Government Benefits', 'Tax Refund', 'Interest', 'Dividends')");
            migrationBuilder.Sql("UPDATE Transactions SET SubCategory = Category, Category = 'Income' WHERE Category IN ('Salary', 'Business Income', 'Other Income')");
            migrationBuilder.Sql("UPDATE Transactions SET SubCategory = 'Taxi' WHERE Category = 'Auto & Transport' AND SubCategory = 'Rideshare'");
            migrationBuilder.Sql("UPDATE Transactions SET SubCategory = Category, Category = 'Auto & Transport' WHERE Category IN ('Gas', 'Car Lease', 'Parking', 'Taxi')");
            migrationBuilder.Sql("UPDATE Transactions SET SubCategory = Category, Category = 'Taxes' WHERE Category = 'Property Tax'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: TwoLevelCategories' Down puts rows back.
        }
    }
}
