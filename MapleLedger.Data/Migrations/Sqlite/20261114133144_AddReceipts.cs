using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TransactionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Receipts_Transactions_AccountId_TransactionId",
                        columns: x => new { x.AccountId, x.TransactionId },
                        principalTable: "Transactions",
                        principalColumns: new[] { "AccountId", "TransactionId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_AccountId_TransactionId",
                table: "Receipts",
                columns: new[] { "AccountId", "TransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_OwnerId",
                table: "Receipts",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Receipts");
        }
    }
}
