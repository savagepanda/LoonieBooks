using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddHoldingPriceSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PriceAsOf",
                table: "Holdings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceSource",
                table: "Holdings",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                // Existing rows were imported from statements.
                defaultValue: "Statement");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceAsOf",
                table: "Holdings");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "Holdings");
        }
    }
}
