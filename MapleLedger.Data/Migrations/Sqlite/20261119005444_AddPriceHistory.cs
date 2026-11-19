using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Month = table.Column<long>(type: "INTEGER", nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", precision: 19, scale: 6, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceHistory_OwnerId",
                table: "PriceHistory",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceHistory");
        }
    }
}
