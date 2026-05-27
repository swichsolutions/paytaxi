using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <summary>
    /// Adds Park multi-tenancy / operating-model fields per CLAUDE.md
    /// "Business Model and Multi-Tenancy".
    ///
    /// Two-phase pattern: columns are added nullable first, existing rows are
    /// backfilled via SQL, then NOT NULL + check constraints are applied.
    /// This keeps the migration safe to run on a non-empty database.
    ///
    /// IsActive is intentionally NOT dropped here. Status is the new source of
    /// truth and IsActive is backfilled from it; IsActive will be removed in a
    /// follow-up migration once no code reads it.
    /// </summary>
    public partial class AddParkMultiTenancyFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Phase 1: add columns as nullable, no defaults that would break ──
            migrationBuilder.AddColumn<decimal>(
                name: "AuthorizationLimit",
                table: "Parks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankAccountIban",
                table: "Parks",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankProvider",
                table: "Parks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalEntityName",
                table: "Parks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingModel",
                table: "Parks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Parks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Parks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxId",
                table: "Parks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // ── Phase 2: backfill existing rows ─────────────────────────────
            // Slug: lowercase, non-alphanumerics → '-', trim leading/trailing '-'.
            //   "Tbilisi Auto Park #3" → "tbilisi-auto-park-3"
            // Status: from legacy IsActive bool.
            // OperatingModel: default existing parks to model_a (safe, no PayTaxi
            //   commercial-agent involvement) — we can flip to model_a5 manually
            //   per park once authorization limits are signed.
            // BankProvider: lowercase of legacy BankType.
            migrationBuilder.Sql(@"
                UPDATE ""Parks""
                SET
                    ""Slug"" = lower(
                        regexp_replace(
                            regexp_replace(""Name"", '[^a-zA-Z0-9]+', '-', 'g'),
                            '(^-+|-+$)', '', 'g')),
                    ""OperatingModel"" = 'model_a',
                    ""Status"" = CASE WHEN ""IsActive"" THEN 'active' ELSE 'suspended' END,
                    ""BankProvider"" = lower(""BankType"")
                WHERE ""Slug"" IS NULL;
            ");

            // ── Phase 3: enforce NOT NULL on the now-populated columns ──────
            migrationBuilder.AlterColumn<string>(
                name: "Slug", table: "Parks",
                type: "character varying(64)", maxLength: 64, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(64)", oldMaxLength: 64, oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OperatingModel", table: "Parks",
                type: "character varying(20)", maxLength: 20, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(20)", oldMaxLength: 20, oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Status", table: "Parks",
                type: "character varying(20)", maxLength: 20, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(20)", oldMaxLength: 20, oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BankProvider", table: "Parks",
                type: "character varying(40)", maxLength: 40, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(40)", oldMaxLength: 40, oldNullable: true);

            // ── Phase 4: indexes and check constraints (now safe on real data) ──
            migrationBuilder.CreateIndex(
                name: "IX_Parks_Slug",
                table: "Parks",
                column: "Slug",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_AuthorizationLimit_NonNegative",
                table: "Parks",
                sql: "\"AuthorizationLimit\" IS NULL OR \"AuthorizationLimit\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_OperatingModel",
                table: "Parks",
                sql: "\"OperatingModel\" IN ('model_a', 'model_a5', 'model_b')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_SlugFormat",
                table: "Parks",
                sql: "\"Slug\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Parks_Status",
                table: "Parks",
                sql: "\"Status\" IN ('pending', 'active', 'suspended', 'terminated')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Parks_Slug",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_AuthorizationLimit_NonNegative",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_OperatingModel",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_SlugFormat",
                table: "Parks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Parks_Status",
                table: "Parks");

            migrationBuilder.DropColumn(name: "AuthorizationLimit", table: "Parks");
            migrationBuilder.DropColumn(name: "BankAccountIban",    table: "Parks");
            migrationBuilder.DropColumn(name: "BankProvider",       table: "Parks");
            migrationBuilder.DropColumn(name: "LegalEntityName",    table: "Parks");
            migrationBuilder.DropColumn(name: "OperatingModel",     table: "Parks");
            migrationBuilder.DropColumn(name: "Slug",               table: "Parks");
            migrationBuilder.DropColumn(name: "Status",             table: "Parks");
            migrationBuilder.DropColumn(name: "TaxId",              table: "Parks");
        }
    }
}
