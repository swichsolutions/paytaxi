using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelAFeeIbanPayoutQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_AuthorizationLimit_NonNegative",
                table: "Parks");

            migrationBuilder.DropIndex(
                name: "IX_BankCards_DriverId",
                table: "BankCards");

            // Not a rename: the A.5 authorization limit is retired outright (a rename would
            // have carried the old limits over as per-cashout caps).
            migrationBuilder.DropColumn(
                name: "AuthorizationLimit",
                table: "Parks");

            migrationBuilder.AddColumn<decimal>(
                name: "MaxCashoutAmount",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Business cutover (PAYTAXI-CONTEXT.md §3): every existing park moves to Model A.
            migrationBuilder.Sql("UPDATE \"Parks\" SET \"OperatingModel\" = 'model_a' WHERE \"OperatingModel\" = 'model_a5';");

            migrationBuilder.AddColumn<decimal>(
                name: "CashoutFee",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0.50m);

            migrationBuilder.AddColumn<decimal>(
                name: "DailyCashoutLimitPerDriver",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinCashoutAmount",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 5m);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Cashouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InitiatedBy",
                table: "Cashouts",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "Cashouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "Cashouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParkBankAccountId",
                table: "Cashouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YandexReversalTransactionId",
                table: "Cashouts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TokenReferenceEncrypted",
                table: "BankCards",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "BankCode",
                table: "BankCards",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HolderName",
                table: "BankCards",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "BankCards",
                type: "character varying(34)",
                maxLength: 34,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ParkBankAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    HolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CredentialsEncrypted = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkBankAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkBankAccounts_Parks_ParkId",
                        column: x => x.ParkId,
                        principalTable: "Parks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_CashoutFee_NonNegative",
                table: "Parks",
                sql: "\"CashoutFee\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_MinCashout_Positive",
                table: "Parks",
                sql: "\"MinCashoutAmount\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Cashouts_ParkBankAccountId",
                table: "Cashouts",
                column: "ParkBankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Cashouts_Status_NextAttemptAt",
                table: "Cashouts",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BankCards_DriverId_Iban",
                table: "BankCards",
                columns: new[] { "DriverId", "Iban" });

            migrationBuilder.CreateIndex(
                name: "IX_ParkBankAccounts_ParkId_BankCode",
                table: "ParkBankAccounts",
                columns: new[] { "ParkId", "BankCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ParkBankAccounts_ParkId_IsPrimary",
                table: "ParkBankAccounts",
                columns: new[] { "ParkId", "IsPrimary" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE");

            migrationBuilder.AddForeignKey(
                name: "FK_Cashouts_ParkBankAccounts_ParkBankAccountId",
                table: "Cashouts",
                column: "ParkBankAccountId",
                principalTable: "ParkBankAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cashouts_ParkBankAccounts_ParkBankAccountId",
                table: "Cashouts");

            migrationBuilder.DropTable(
                name: "ParkBankAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_CashoutFee_NonNegative",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_MinCashout_Positive",
                table: "Parks");

            migrationBuilder.DropIndex(
                name: "IX_Cashouts_ParkBankAccountId",
                table: "Cashouts");

            migrationBuilder.DropIndex(
                name: "IX_Cashouts_Status_NextAttemptAt",
                table: "Cashouts");

            migrationBuilder.DropIndex(
                name: "IX_BankCards_DriverId_Iban",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "CashoutFee",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "DailyCashoutLimitPerDriver",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "MinCashoutAmount",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "InitiatedBy",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "ParkBankAccountId",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "YandexReversalTransactionId",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "BankCode",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "HolderName",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "BankCards");

            migrationBuilder.DropColumn(
                name: "MaxCashoutAmount",
                table: "Parks");

            migrationBuilder.AddColumn<decimal>(
                name: "AuthorizationLimit",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TokenReferenceEncrypted",
                table: "BankCards",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_AuthorizationLimit_NonNegative",
                table: "Parks",
                sql: "\"AuthorizationLimit\" IS NULL OR \"AuthorizationLimit\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_BankCards_DriverId",
                table: "BankCards",
                column: "DriverId");
        }
    }
}
