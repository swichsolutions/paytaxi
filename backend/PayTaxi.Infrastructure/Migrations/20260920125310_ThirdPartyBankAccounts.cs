using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ThirdPartyBankAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddedBy",
                table: "BankCards",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsThirdPartyAccount",
                table: "BankCards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ThirdPartyReason",
                table: "BankCards",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedBy",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "IsThirdPartyAccount",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "ThirdPartyReason",
                table: "BankCards");
        }
    }
}
