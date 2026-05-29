using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CashoutsScanned = table.Column<int>(type: "integer", nullable: false),
                    BankTransfersScanned = table.Column<int>(type: "integer", nullable: false),
                    YandexTxScanned = table.Column<int>(type: "integer", nullable: false),
                    DiscrepanciesFound = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                    table.CheckConstraint("CK_ReconciliationRuns_Status", "\"Status\" IN ('running', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_ReconciliationRuns_Parks_ParkId",
                        column: x => x.ParkId,
                        principalTable: "Parks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationDiscrepancies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: false),
                    CashoutId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PaytaxiAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ExternalAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    BankTransferId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    YandexTransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolutionNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationDiscrepancies", x => x.Id);
                    table.CheckConstraint("CK_ReconciliationDiscrepancies_Kind", "\"Kind\" IN ('missing_in_bank', 'orphaned_bank_send', 'missing_in_yandex', 'orphaned_yandex_debit', 'amount_mismatch_bank', 'amount_mismatch_yandex', 'stuck_pending')");
                    table.ForeignKey(
                        name: "FK_ReconciliationDiscrepancies_Cashouts_CashoutId",
                        column: x => x.CashoutId,
                        principalTable: "Cashouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ReconciliationDiscrepancies_ReconciliationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ReconciliationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationDiscrepancies_CashoutId",
                table: "ReconciliationDiscrepancies",
                column: "CashoutId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationDiscrepancies_ParkId_IsResolved",
                table: "ReconciliationDiscrepancies",
                columns: new[] { "ParkId", "IsResolved" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationDiscrepancies_RunId",
                table: "ReconciliationDiscrepancies",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_ParkId_StartedAt",
                table: "ReconciliationRuns",
                columns: new[] { "ParkId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationDiscrepancies");

            migrationBuilder.DropTable(
                name: "ReconciliationRuns");
        }
    }
}
