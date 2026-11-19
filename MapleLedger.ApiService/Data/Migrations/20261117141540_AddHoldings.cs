using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Holdings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AsOf = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(24,8)", precision: 24, scale: 8, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(24,8)", precision: 24, scale: 8, nullable: true),
                    MarketValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    BookValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(19,8)", precision: 19, scale: 8, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holdings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Holdings_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Holdings_AccountId",
                table: "Holdings",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Holdings_OwnerId",
                table: "Holdings",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Holdings");
        }
    }
}
