using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class TwoLevelCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two-level categories: Salary, Business Income and Other Income become details of Income;
            // Gas, Car Lease, Parking and Taxi of Auto & Transport; Property Tax of Taxes. Older income
            // details (paycheque, benefits, interest...) map onto Salary and Other Income.
            // SET reads the row's old values, so SubCategory = Category takes the old category.
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
            migrationBuilder.Sql("UPDATE Transactions SET Category = SubCategory WHERE Category = 'Income' AND SubCategory IN ('Salary', 'Business Income', 'Other Income')");
            migrationBuilder.Sql("UPDATE Transactions SET Category = SubCategory WHERE Category = 'Auto & Transport' AND SubCategory IN ('Gas', 'Car Lease', 'Parking', 'Taxi')");
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Property Tax' WHERE Category = 'Taxes' AND SubCategory = 'Property Tax'");
        }
    }
}
