using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddCustomCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomCategories",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubCategory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomCategories_OwnerId",
                table: "CustomCategories",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomCategories");
        }
    }
}
