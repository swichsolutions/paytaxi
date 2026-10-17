using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContractAlignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Settlements_ParkId_SettlementDate",
                table: "Settlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Settlements_Status",
                table: "Settlements");

            migrationBuilder.CreateSequence<int>(
                name: "IncidentNumberSeq");

            migrationBuilder.AddColumn<int>(
                name: "BankDocumentSeq",
                table: "Settlements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExternalNote",
                table: "Settlements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExternalPaidOn",
                table: "Settlements",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalPayer",
                table: "Settlements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalRecordedAt",
                table: "Settlements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRecordedBy",
                table: "Settlements",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalReference",
                table: "Settlements",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMonthly",
                table: "Settlements",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MonthlyStatementId",
                table: "Settlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParkNote",
                table: "ReconciliationDiscrepancies",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParkNoteAt",
                table: "ReconciliationDiscrepancies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParkNoteBy",
                table: "ReconciliationDiscrepancies",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AuthorisationNoticeOn",
                table: "Parks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorisationRevocationReason",
                table: "Parks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthorisationRevocationRecordedAt",
                table: "Parks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorisationRevocationRecordedBy",
                table: "Parks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AuthorisationRevokedFrom",
                table: "Parks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LicenseeId",
                table: "Parks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PhoneHash",
                table: "OtpCodes",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "PhoneHash",
                table: "Drivers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "Drivers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BankDocumentSeq",
                table: "Cashouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "FeeLicenseeId",
                table: "Cashouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FeeRegimeStamped",
                table: "Cashouts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UnknownOutcomeDocument",
                table: "Cashouts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnresolvedAttempts",
                table: "Cashouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "IbanHash",
                table: "BankCards",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldDefaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "LicenseeId",
                table: "AdminUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DriverSignIns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    DriverId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverSignIns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverSignIns_Drivers_DriverId",
                        column: x => x.DriverId,
                        principalTable: "Drivers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"IncidentNumberSeq\"')"),
                    Level = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReachedP1At = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReachedP2At = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Cause = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Fault = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExternalRestoredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExternalRestoredBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.CheckConstraint("CK_Incidents_Fault", "\"Fault\" IN ('platform', 'external', 'park', 'unknown')");
                    table.CheckConstraint("CK_Incidents_Level", "\"Level\" IN ('p1', 'p2', 'p3')");
                    table.CheckConstraint("CK_Incidents_Resolved", "(\"Status\" = 'resolved') = (\"ResolvedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_Incidents_Source", "\"Source\" IN ('manual', 'auto')");
                    table.CheckConstraint("CK_Incidents_Status", "\"Status\" IN ('reported', 'acknowledged', 'resolved')");
                    table.ForeignKey(
                        name: "FK_Incidents_Parks_ParkId",
                        column: x => x.ParkId,
                        principalTable: "Parks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Licensees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TaxId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    LaunchDate = table.Column<DateOnly>(type: "date", nullable: true),
                    BarrierGel = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SwichSharePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licensees", x => x.Id);
                    table.CheckConstraint("CK_Licensees_Barrier_NonNegative", "\"BarrierGel\" >= 0");
                    table.CheckConstraint("CK_Licensees_SwichShare_Percent", "\"SwichSharePercent\" >= 0 AND \"SwichSharePercent\" <= 100");
                });

            migrationBuilder.CreateTable(
                name: "ParkConfigEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ChangedFields = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkConfigEvents", x => x.Id);
                    table.CheckConstraint("CK_ParkConfigEvents_Action", "\"Action\" IN ('created', 'updated')");
                    table.CheckConstraint("CK_ParkConfigEvents_Subject", "\"Subject\" IN ('bank_account', 'park', 'driver')");
                });

            migrationBuilder.CreateTable(
                name: "PhoneSignInEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    PhoneHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhoneSignInEvents", x => x.Id);
                    table.CheckConstraint("CK_PhoneSignInEvents_Kind", "\"Kind\" IN ('code_issued', 'failed', 'locked')");
                    table.CheckConstraint("CK_PhoneSignInEvents_Until", "(\"Kind\" = 'locked') = (\"Until\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "IncidentUpdates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    By = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParkId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentUpdates", x => x.Id);
                    table.CheckConstraint("CK_IncidentUpdates_Kind", "\"Kind\" IN ('reported', 'acknowledged', 'updated', 'note', 'external_restored', 'resolved', 'reopened')");
                    table.ForeignKey(
                        name: "FK_IncidentUpdates_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgreementEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    LicenseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    OldValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgreementEvents", x => x.Id);
                    table.CheckConstraint("CK_AgreementEvents_Kind", "\"Kind\" IN ('launch_date', 'terms', 'cost_added', 'cost_edited', 'cost_deleted', 'cost_held', 'cost_released', 'correction_added')");
                    table.CheckConstraint("CK_AgreementEvents_Reason", "\"Kind\" NOT IN ('launch_date', 'cost_deleted', 'cost_held', 'correction_added') OR \"Reason\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AgreementEvents_Licensees_LicenseeId",
                        column: x => x.LicenseeId,
                        principalTable: "Licensees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyStatements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CashoutCount = table.Column<int>(type: "integer", nullable: false),
                    FeeIncome = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LicenseeCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SwichCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CarryIn = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BarrierGel = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SwichSharePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    BarrierBefore = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BarrierFilledBefore = table.Column<bool>(type: "boolean", nullable: false),
                    Phase = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BarrierFraction = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    Entitlement = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Due = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CarryOut = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InvoiceRef = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InitiatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyStatements", x => x.Id);
                    table.CheckConstraint("CK_MonthlyStatements_Due_NonNegative", "\"Due\" >= 0");
                    table.CheckConstraint("CK_MonthlyStatements_Phase", "\"Phase\" IN ('barrier', 'crossing', 'normal')");
                    table.CheckConstraint("CK_MonthlyStatements_Status", "\"Status\" IN ('pending', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_MonthlyStatements_Licensees_LicenseeId",
                        column: x => x.LicenseeId,
                        principalTable: "Licensees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommonCosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaidBy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrectsId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HeldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeldBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HoldReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DeleteReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommonCosts", x => x.Id);
                    table.CheckConstraint("CK_CommonCosts_Amount", "\"Amount\" > 0 OR (\"CorrectsId\" IS NOT NULL AND \"Amount\" <> 0)");
                    table.CheckConstraint("CK_CommonCosts_Category", "\"Category\" IN ('bank', 'sms', 'payment_provider', 'support_comms', 'hosting', 'domain')");
                    table.CheckConstraint("CK_CommonCosts_Correction_Reason", "\"CorrectsId\" IS NULL OR \"Reason\" IS NOT NULL");
                    table.CheckConstraint("CK_CommonCosts_Deleted", "\"DeletedAt\" IS NULL OR (\"StatementId\" IS NULL AND \"DeletedBy\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_CommonCosts_Held", "\"HeldAt\" IS NULL OR (\"StatementId\" IS NULL AND \"HeldBy\" IS NOT NULL AND \"HoldReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_CommonCosts_PaidBy", "\"PaidBy\" IN ('swich', 'licensee')");
                    table.ForeignKey(
                        name: "FK_CommonCosts_CommonCosts_CorrectsId",
                        column: x => x.CorrectsId,
                        principalTable: "CommonCosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommonCosts_Licensees_LicenseeId",
                        column: x => x.LicenseeId,
                        principalTable: "Licensees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommonCosts_MonthlyStatements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "MonthlyStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_MonthlyStatementId_ParkId",
                table: "Settlements",
                columns: new[] { "MonthlyStatementId", "ParkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_ParkId_SettlementDate",
                table: "Settlements",
                columns: new[] { "ParkId", "SettlementDate" },
                unique: true,
                filter: "\"IsMonthly\" = TRUE AND \"MonthlyStatementId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Settlements_Status",
                table: "Settlements",
                sql: "\"Status\" IN ('pending', 'processing', 'completed', 'failed', 'settled_externally')");

            migrationBuilder.CreateIndex(
                name: "IX_Parks_LicenseeId",
                table: "Parks",
                column: "LicenseeId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_AuthorisationRevocation",
                table: "Parks",
                sql: "\"AuthorisationRevokedFrom\" IS NULL OR (\"AuthorisationNoticeOn\" IS NOT NULL AND \"AuthorisationRevokedFrom\" > \"AuthorisationNoticeOn\" AND \"AuthorisationRevocationReason\" IS NOT NULL AND \"AuthorisationRevocationRecordedBy\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Drivers_PreferredLanguage",
                table: "Drivers",
                sql: "\"PreferredLanguage\" IS NULL OR \"PreferredLanguage\" IN ('ka', 'en', 'ru')");

            migrationBuilder.CreateIndex(
                name: "IX_Cashouts_FeeLicenseeId",
                table: "Cashouts",
                column: "FeeLicenseeId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cashouts_FeeRegime",
                table: "Cashouts",
                sql: "\"FeeLicenseeId\" IS NULL OR \"FeeRegimeStamped\"");

            migrationBuilder.CreateIndex(
                name: "IX_ApiAuditLogs_ApiProvider_Timestamp",
                table: "ApiAuditLogs",
                columns: new[] { "ApiProvider", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiAuditLogs_Timestamp",
                table: "ApiAuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_LicenseeId",
                table: "AdminUsers",
                column: "LicenseeId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementEvents_LicenseeId_At",
                table: "AgreementEvents",
                columns: new[] { "LicenseeId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AgreementEvents_ReferenceId",
                table: "AgreementEvents",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_CommonCosts_CorrectsId",
                table: "CommonCosts",
                column: "CorrectsId");

            migrationBuilder.CreateIndex(
                name: "IX_CommonCosts_LicenseeId_StatementId_InvoiceDate",
                table: "CommonCosts",
                columns: new[] { "LicenseeId", "StatementId", "InvoiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CommonCosts_StatementId",
                table: "CommonCosts",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverSignIns_DriverId_SignedInAt",
                table: "DriverSignIns",
                columns: new[] { "DriverId", "SignedInAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverSignIns_SignedInAt",
                table: "DriverSignIns",
                column: "SignedInAt");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_DedupeKey",
                table: "Incidents",
                column: "DedupeKey",
                unique: true,
                filter: "\"DedupeKey\" IS NOT NULL AND \"Status\" <> 'resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Number",
                table: "Incidents",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ParkId_ReportedAt",
                table: "Incidents",
                columns: new[] { "ParkId", "ReportedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Status_ReportedAt",
                table: "Incidents",
                columns: new[] { "Status", "ReportedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentUpdates_IncidentId_At",
                table: "IncidentUpdates",
                columns: new[] { "IncidentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Licensees_SingleActive",
                table: "Licensees",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyStatements_LicenseeId_Month",
                table: "MonthlyStatements",
                columns: new[] { "LicenseeId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkConfigEvents_ParkId_At",
                table: "ParkConfigEvents",
                columns: new[] { "ParkId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ParkConfigEvents_SubjectId",
                table: "ParkConfigEvents",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneSignInEvents_At",
                table: "PhoneSignInEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_PhoneSignInEvents_PhoneHash_At",
                table: "PhoneSignInEvents",
                columns: new[] { "PhoneHash", "At" });

            migrationBuilder.AddForeignKey(
                name: "FK_AdminUsers_Licensees_LicenseeId",
                table: "AdminUsers",
                column: "LicenseeId",
                principalTable: "Licensees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Cashouts_Licensees_FeeLicenseeId",
                table: "Cashouts",
                column: "FeeLicenseeId",
                principalTable: "Licensees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Parks_Licensees_LicenseeId",
                table: "Parks",
                column: "LicenseeId",
                principalTable: "Licensees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Settlements_MonthlyStatements_MonthlyStatementId",
                table: "Settlements",
                column: "MonthlyStatementId",
                principalTable: "MonthlyStatements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdminUsers_Licensees_LicenseeId",
                table: "AdminUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_Cashouts_Licensees_FeeLicenseeId",
                table: "Cashouts");

            migrationBuilder.DropForeignKey(
                name: "FK_Parks_Licensees_LicenseeId",
                table: "Parks");

            migrationBuilder.DropForeignKey(
                name: "FK_Settlements_MonthlyStatements_MonthlyStatementId",
                table: "Settlements");

            migrationBuilder.DropTable(
                name: "AgreementEvents");

            migrationBuilder.DropTable(
                name: "CommonCosts");

            migrationBuilder.DropTable(
                name: "DriverSignIns");

            migrationBuilder.DropTable(
                name: "IncidentUpdates");

            migrationBuilder.DropTable(
                name: "ParkConfigEvents");

            migrationBuilder.DropTable(
                name: "PhoneSignInEvents");

            migrationBuilder.DropTable(
                name: "MonthlyStatements");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "Licensees");

            migrationBuilder.DropIndex(
                name: "IX_Settlements_MonthlyStatementId_ParkId",
                table: "Settlements");

            migrationBuilder.DropIndex(
                name: "IX_Settlements_ParkId_SettlementDate",
                table: "Settlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Settlements_Status",
                table: "Settlements");

            migrationBuilder.DropIndex(
                name: "IX_Parks_LicenseeId",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_AuthorisationRevocation",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Drivers_PreferredLanguage",
                table: "Drivers");

            migrationBuilder.DropIndex(
                name: "IX_Cashouts_FeeLicenseeId",
                table: "Cashouts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cashouts_FeeRegime",
                table: "Cashouts");

            migrationBuilder.DropIndex(
                name: "IX_ApiAuditLogs_ApiProvider_Timestamp",
                table: "ApiAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_ApiAuditLogs_Timestamp",
                table: "ApiAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AdminUsers_LicenseeId",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "BankDocumentSeq",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ExternalNote",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ExternalPaidOn",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ExternalPayer",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ExternalRecordedAt",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ExternalRecordedBy",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ExternalReference",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "IsMonthly",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "MonthlyStatementId",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ParkNote",
                table: "ReconciliationDiscrepancies");

            migrationBuilder.DropColumn(
                name: "ParkNoteAt",
                table: "ReconciliationDiscrepancies");

            migrationBuilder.DropColumn(
                name: "ParkNoteBy",
                table: "ReconciliationDiscrepancies");

            migrationBuilder.DropColumn(
                name: "AuthorisationNoticeOn",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "AuthorisationRevocationReason",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "AuthorisationRevocationRecordedAt",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "AuthorisationRevocationRecordedBy",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "AuthorisationRevokedFrom",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "LicenseeId",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "Drivers");

            migrationBuilder.DropColumn(
                name: "BankDocumentSeq",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "FeeLicenseeId",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "FeeRegimeStamped",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "UnknownOutcomeDocument",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "UnresolvedAttempts",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "LicenseeId",
                table: "AdminUsers");

            migrationBuilder.DropSequence(
                name: "IncidentNumberSeq");

            migrationBuilder.AlterColumn<string>(
                name: "PhoneHash",
                table: "OtpCodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "PhoneHash",
                table: "Drivers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "IbanHash",
                table: "BankCards",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldDefaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_ParkId_SettlementDate",
                table: "Settlements",
                columns: new[] { "ParkId", "SettlementDate" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Settlements_Status",
                table: "Settlements",
                sql: "\"Status\" IN ('pending', 'processing', 'completed', 'failed')");
        }
    }
}
