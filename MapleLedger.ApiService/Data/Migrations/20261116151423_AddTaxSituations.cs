using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxSituations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaxSituations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    PersonId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxSituations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxSituations_OwnerId_Year_PersonId",
                table: "TaxSituations",
                columns: new[] { "OwnerId", "Year", "PersonId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxSituations");
        }
    }
}
