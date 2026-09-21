using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CashoutRetryLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfCashoutId",
                table: "Cashouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cashouts_RetryOfCashoutId",
                table: "Cashouts",
                column: "RetryOfCashoutId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cashouts_RetryOfCashoutId",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "RetryOfCashoutId",
                table: "Cashouts");
        }
    }
}
