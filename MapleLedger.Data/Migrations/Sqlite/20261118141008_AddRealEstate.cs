using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapleLedger.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddRealEstate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecuredBy",
                table: "Accounts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HousePriceIndex",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Area = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Month = table.Column<long>(type: "INTEGER", nullable: false),
                    Index = table.Column<decimal>(type: "TEXT", precision: 19, scale: 4, nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HousePriceIndex", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Properties",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Value = table.Column<decimal>(type: "TEXT", precision: 19, scale: 4, nullable: true),
                    ValueAsOf = table.Column<long>(type: "INTEGER", nullable: true),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HousePriceIndex_OwnerId",
                table: "HousePriceIndex",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Properties_OwnerId",
                table: "Properties",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HousePriceIndex");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropColumn(
                name: "SecuredBy",
                table: "Accounts");
        }
    }
}
