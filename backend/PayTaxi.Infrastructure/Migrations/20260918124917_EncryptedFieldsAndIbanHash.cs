using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EncryptedFieldsAndIbanHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankCards_DriverId_Iban",
                table: "BankCards");

            migrationBuilder.AlterColumn<string>(
                name: "Iban",
                table: "BankCards",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(34)",
                oldMaxLength: 34);

            migrationBuilder.AddColumn<string>(
                name: "IbanHash",
                table: "BankCards",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_BankCards_DriverId_IbanHash",
                table: "BankCards",
                columns: new[] { "DriverId", "IbanHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankCards_DriverId_IbanHash",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "IbanHash",
                table: "BankCards");

            migrationBuilder.AlterColumn<string>(
                name: "Iban",
                table: "BankCards",
                type: "character varying(34)",
                maxLength: 34,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_BankCards_DriverId_Iban",
                table: "BankCards",
                columns: new[] { "DriverId", "Iban" });
        }
    }
}
