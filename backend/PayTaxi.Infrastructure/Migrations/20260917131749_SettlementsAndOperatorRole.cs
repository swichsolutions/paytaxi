using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SettlementsAndOperatorRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AdminUsers_Role",
                table: "AdminUsers");

            migrationBuilder.AddColumn<decimal>(
                name: "Phase1CapGel",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Phase1SharePercent",
                table: "Parks",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SwichSharePercent",
                table: "Parks",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 50m);

            migrationBuilder.AlterColumn<Guid>(
                name: "CashoutId",
                table: "LedgerEntries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "SettlementId",
                table: "LedgerEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SettlementId",
                table: "Cashouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Settlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CashoutCount = table.Column<int>(type: "integer", nullable: false),
                    FeeTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Phase1Fees = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Phase2Fees = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SwichShare = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ParkShare = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CumulativeFeesBefore = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BankTransferId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InvoiceRef = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ParkBankAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    SwichIban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    InitiatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settlements", x => x.Id);
                    table.CheckConstraint("CK_Settlements_Status", "\"Status\" IN ('pending', 'processing', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_Settlements_ParkBankAccounts_ParkBankAccountId",
                        column: x => x.ParkBankAccountId,
                        principalTable: "ParkBankAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Settlements_Parks_ParkId",
                        column: x => x.ParkId,
                        principalTable: "Parks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_SwichShare_Percent",
                table: "Parks",
                sql: "\"SwichSharePercent\" >= 0 AND \"SwichSharePercent\" <= 100");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_SettlementId",
                table: "LedgerEntries",
                column: "SettlementId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerEntries_Owner",
                table: "LedgerEntries",
                sql: "(\"CashoutId\" IS NOT NULL) <> (\"SettlementId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Cashouts_SettlementId",
                table: "Cashouts",
                column: "SettlementId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdminUsers_Role",
                table: "AdminUsers",
                sql: "\"Role\" IN ('super_admin', 'operator', 'park_admin')");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_IdempotencyKey",
                table: "Settlements",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_ParkBankAccountId",
                table: "Settlements",
                column: "ParkBankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_ParkId_SettlementDate",
                table: "Settlements",
                columns: new[] { "ParkId", "SettlementDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_Status_SettlementDate",
                table: "Settlements",
                columns: new[] { "Status", "SettlementDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_Cashouts_Settlements_SettlementId",
                table: "Cashouts",
                column: "SettlementId",
                principalTable: "Settlements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntries_Settlements_SettlementId",
                table: "LedgerEntries",
                column: "SettlementId",
                principalTable: "Settlements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cashouts_Settlements_SettlementId",
                table: "Cashouts");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntries_Settlements_SettlementId",
                table: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "Settlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_SwichShare_Percent",
                table: "Parks");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_SettlementId",
                table: "LedgerEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerEntries_Owner",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_Cashouts_SettlementId",
                table: "Cashouts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdminUsers_Role",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "Phase1CapGel",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "Phase1SharePercent",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "SwichSharePercent",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "SettlementId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "SettlementId",
                table: "Cashouts");

            migrationBuilder.AlterColumn<Guid>(
                name: "CashoutId",
                table: "LedgerEntries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdminUsers_Role",
                table: "AdminUsers",
                sql: "\"Role\" IN ('super_admin', 'park_admin')");
        }
    }
}
