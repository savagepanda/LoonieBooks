using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHousehold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaxClaimedBy",
                table: "Transactions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HouseholdMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_OwnerId",
                table: "HouseholdMembers",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HouseholdMembers");

            migrationBuilder.DropColumn(
                name: "TaxClaimedBy",
                table: "Transactions");
        }
    }
}
