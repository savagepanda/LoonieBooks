using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.ApiService.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccountCategory = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AccountType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AccountNumberDisplay = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ProductName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Nickname = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InstitutionName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BalanceAsOf = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CurrentBalance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    AvailableBalance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    PrincipalBalance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CreditLine = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    AvailableCredit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    InterestRate = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    CurrencyRate = table.Column<decimal>(type: "decimal(19,8)", precision: 19, scale: 8, nullable: true),
                    OriginalCurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Received = table.Column<int>(type: "int", nullable: false),
                    Added = table.Column<int>(type: "int", nullable: false),
                    Duplicates = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportBatches_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PostedTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TransactionTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Memo = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Payee = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CheckNumber = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DebitCreditMemo = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SubCategory = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ForeignAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ForeignCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    RunningBalance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CategoryIsManual = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TaxNote = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    TaxStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => new { x.AccountId, x.TransactionId });
                    table.ForeignKey(
                        name: "FK_Transactions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_AccountId",
                table: "ImportBatches",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TaxCode",
                table: "Transactions",
                column: "TaxCode");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_TransactionTimestamp",
                table: "Transactions",
                column: "TransactionTimestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
