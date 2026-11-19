using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class SplitOutGasParkingTaxi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Gas, Parking and Taxi (rideshare) move out of Auto & Transport into their own categories.
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Gas' WHERE Category = 'Auto & Transport' AND SubCategory = 'Gas'");
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Parking' WHERE Category = 'Auto & Transport' AND SubCategory = 'Parking'");
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Taxi' WHERE Category = 'Auto & Transport' AND SubCategory = 'Rideshare'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Transactions SET Category = 'Auto & Transport' WHERE Category IN ('Gas', 'Car Lease', 'Parking', 'Taxi')");
        }
    }
}
