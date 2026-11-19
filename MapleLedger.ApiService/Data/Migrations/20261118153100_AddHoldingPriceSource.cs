using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldingPriceSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceAsOf",
                table: "Holdings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceSource",
                table: "Holdings",
                type: "nvarchar(16)",
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
