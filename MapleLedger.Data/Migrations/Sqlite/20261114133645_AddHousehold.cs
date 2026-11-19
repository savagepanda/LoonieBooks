using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
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
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HouseholdMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
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
