using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountHoldersAndEarnedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EarnedBy",
                table: "Transactions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeldBy",
                table: "Accounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EarnedBy",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "HeldBy",
                table: "Accounts");
        }
    }
}
