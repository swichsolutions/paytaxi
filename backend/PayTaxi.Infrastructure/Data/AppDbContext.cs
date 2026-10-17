using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Security;

namespace PayTaxi.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Park> Parks => Set<Park>();
    public DbSet<ParkBankAccount> ParkBankAccounts => Set<ParkBankAccount>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<Licensee> Licensees => Set<Licensee>();
    public DbSet<CommonCost> CommonCosts => Set<CommonCost>();
    public DbSet<MonthlyStatement> MonthlyStatements => Set<MonthlyStatement>();
    public DbSet<AgreementEvent> AgreementEvents => Set<AgreementEvent>();
    public DbSet<ParkConfigEvent> ParkConfigEvents => Set<ParkConfigEvent>();
    public DbSet<DriverSession> DriverSessions => Set<DriverSession>();
    public DbSet<DriverSignIn> DriverSignIns => Set<DriverSignIn>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<BankCard> BankCards => Set<BankCard>();
    public DbSet<Cashout> Cashouts => Set<Cashout>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<ApiAuditLog> ApiAuditLogs => Set<ApiAuditLog>();
    public DbSet<YandexBalanceCache> YandexBalanceCaches => Set<YandexBalanceCache>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<PhoneSignInEvent> PhoneSignInEvents => Set<PhoneSignInEvent>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationDiscrepancy> ReconciliationDiscrepancies => Set<ReconciliationDiscrepancy>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentUpdate> IncidentUpdates => Set<IncidentUpdate>();

    // ── Enum ↔ snake_case text converters ────────────────────────────
    // Stored as text + Postgres check constraint (not native enum) so we can
    // evolve the set without ALTER TYPE migrations.
    //
    // EF expression trees can't host switch/throw, so the actual mapping lives
    // in static helper methods that the expression simply invokes.
    private static readonly ValueConverter<OperatingModel, string> OperatingModelConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToOperatingModel(v));

    private static readonly ValueConverter<ParkStatus, string> ParkStatusConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToParkStatus(v));

    private static readonly ValueConverter<ReconciliationStatus, string> ReconciliationStatusConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToReconciliationStatus(v));

    private static readonly ValueConverter<SettlementStatus, string> SettlementStatusConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToSettlementStatus(v));

    private static readonly ValueConverter<IncidentLevel, string> IncidentLevelConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToIncidentLevel(v));

    private static readonly ValueConverter<IncidentStatus, string> IncidentStatusConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToIncidentStatus(v));

    private static readonly ValueConverter<IncidentFault, string> IncidentFaultConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToIncidentFault(v));

    private static readonly ValueConverter<IncidentSource, string> IncidentSourceConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToIncidentSource(v));

    private static readonly ValueConverter<CostPaidBy, string> CostPaidByConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToCostPaidBy(v));

    private static readonly ValueConverter<CostCategory, string> CostCategoryConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToCostCategory(v));

    private static readonly ValueConverter<StatementPhase, string> StatementPhaseConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToStatementPhase(v));

    private static readonly ValueConverter<MonthlyStatementStatus, string> MonthlyStatementStatusConverter = new(
        v => Converters.ToText(v),
        v => Converters.ToMonthlyStatementStatus(v));

    /// <summary>
    /// AES-GCM at rest for PII / credential columns. Legacy plaintext is read transparently and
    /// re-encrypted by <see cref="EncryptionMigrator"/> at startup. Never filter these columns by
    /// equality — use the keyed *Hash companions (PhoneHash, IbanHash; <see cref="LookupHash"/>).
    /// </summary>
    /// <summary>
    /// Width of a lookup-hash column: the keyed form is "h1:" + 8-hex key id + ":" + 64 hex = 76 characters
    /// (<see cref="LookupHasher"/>); the plain SHA-256 of before (64) still fits while the startup migrator replaces it.
    /// </summary>
    private const int LookupHashLength = 80;

    private static readonly ValueConverter<string, string> Encrypted = new(
        v => FieldEncryptor.Encrypt(v),
        v => FieldEncryptor.Decrypt(v));

    private static class Converters
    {
        public static string ToText(OperatingModel v) => v switch
        {
            OperatingModel.ModelA  => "model_a",
            OperatingModel.ModelA5 => "model_a5",
            OperatingModel.ModelB  => "model_b",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown OperatingModel"),
        };

        public static OperatingModel ToOperatingModel(string v) => v switch
        {
            "model_a"  => OperatingModel.ModelA,
            "model_a5" => OperatingModel.ModelA5,
            "model_b"  => OperatingModel.ModelB,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown operating_model value"),
        };

        public static string ToText(ParkStatus v) => v switch
        {
            ParkStatus.Pending    => "pending",
            ParkStatus.Active     => "active",
            ParkStatus.Suspended  => "suspended",
            ParkStatus.Terminated => "terminated",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown ParkStatus"),
        };

        public static ParkStatus ToParkStatus(string v) => v switch
        {
            "pending"    => ParkStatus.Pending,
            "active"     => ParkStatus.Active,
            "suspended"  => ParkStatus.Suspended,
            "terminated" => ParkStatus.Terminated,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown park status value"),
        };

        public static string ToText(ReconciliationStatus v) => v switch
        {
            ReconciliationStatus.Running   => "running",
            ReconciliationStatus.Completed => "completed",
            ReconciliationStatus.Failed    => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown ReconciliationStatus"),
        };

        public static string ToText(SettlementStatus v) => v switch
        {
            SettlementStatus.Pending    => "pending",
            SettlementStatus.Processing => "processing",
            SettlementStatus.Completed  => "completed",
            SettlementStatus.Failed     => "failed",
            SettlementStatus.SettledExternally => "settled_externally",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown SettlementStatus"),
        };

        public static SettlementStatus ToSettlementStatus(string v) => v switch
        {
            "pending"    => SettlementStatus.Pending,
            "processing" => SettlementStatus.Processing,
            "completed"  => SettlementStatus.Completed,
            "failed"     => SettlementStatus.Failed,
            "settled_externally" => SettlementStatus.SettledExternally,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown settlement status value"),
        };

        public static ReconciliationStatus ToReconciliationStatus(string v) => v switch
        {
            "running"   => ReconciliationStatus.Running,
            "completed" => ReconciliationStatus.Completed,
            "failed"    => ReconciliationStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown reconciliation_status value"),
        };

        public static string ToText(IncidentLevel v) => v switch
        {
            IncidentLevel.P1 => "p1",
            IncidentLevel.P2 => "p2",
            IncidentLevel.P3 => "p3",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown IncidentLevel"),
        };

        public static IncidentLevel ToIncidentLevel(string v) => v switch
        {
            "p1" => IncidentLevel.P1,
            "p2" => IncidentLevel.P2,
            "p3" => IncidentLevel.P3,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown incident level value"),
        };

        public static string ToText(IncidentStatus v) => v switch
        {
            IncidentStatus.Reported     => "reported",
            IncidentStatus.Acknowledged => "acknowledged",
            IncidentStatus.Resolved     => "resolved",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown IncidentStatus"),
        };

        public static IncidentStatus ToIncidentStatus(string v) => v switch
        {
            "reported"     => IncidentStatus.Reported,
            "acknowledged" => IncidentStatus.Acknowledged,
            "resolved"     => IncidentStatus.Resolved,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown incident status value"),
        };

        public static string ToText(IncidentFault v) => v switch
        {
            IncidentFault.Platform => "platform",
            IncidentFault.External => "external",
            IncidentFault.Park     => "park",
            IncidentFault.Unknown  => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown IncidentFault"),
        };

        public static IncidentFault ToIncidentFault(string v) => v switch
        {
            "platform" => IncidentFault.Platform,
            "external" => IncidentFault.External,
            "park"     => IncidentFault.Park,
            "unknown"  => IncidentFault.Unknown,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown incident fault value"),
        };

        public static string ToText(IncidentSource v) => v switch
        {
            IncidentSource.Manual => "manual",
            IncidentSource.Auto   => "auto",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown IncidentSource"),
        };

        public static IncidentSource ToIncidentSource(string v) => v switch
        {
            "manual" => IncidentSource.Manual,
            "auto"   => IncidentSource.Auto,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown incident source value"),
        };

        public static string ToText(CostPaidBy v) => v switch
        {
            CostPaidBy.Swich    => "swich",
            CostPaidBy.Licensee => "licensee",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown CostPaidBy"),
        };

        public static CostPaidBy ToCostPaidBy(string v) => v switch
        {
            "swich"    => CostPaidBy.Swich,
            "licensee" => CostPaidBy.Licensee,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown cost paid_by value"),
        };

        public static string ToText(CostCategory v) => v switch
        {
            CostCategory.Bank            => "bank",
            CostCategory.Sms             => "sms",
            CostCategory.PaymentProvider => "payment_provider",
            CostCategory.SupportComms    => "support_comms",
            CostCategory.Hosting         => "hosting",
            CostCategory.Domain          => "domain",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown CostCategory"),
        };

        public static CostCategory ToCostCategory(string v) => v switch
        {
            "bank"             => CostCategory.Bank,
            "sms"              => CostCategory.Sms,
            "payment_provider" => CostCategory.PaymentProvider,
            "support_comms"    => CostCategory.SupportComms,
            "hosting"          => CostCategory.Hosting,
            "domain"           => CostCategory.Domain,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown cost category value"),
        };

        public static string ToText(StatementPhase v) => v switch
        {
            StatementPhase.Barrier  => "barrier",
            StatementPhase.Crossing => "crossing",
            StatementPhase.Normal   => "normal",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown StatementPhase"),
        };

        public static StatementPhase ToStatementPhase(string v) => v switch
        {
            "barrier"  => StatementPhase.Barrier,
            "crossing" => StatementPhase.Crossing,
            "normal"   => StatementPhase.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown statement phase value"),
        };

        public static string ToText(MonthlyStatementStatus v) => v switch
        {
            MonthlyStatementStatus.Pending   => "pending",
            MonthlyStatementStatus.Completed => "completed",
            MonthlyStatementStatus.Failed    => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown MonthlyStatementStatus"),
        };

        public static MonthlyStatementStatus ToMonthlyStatementStatus(string v) => v switch
        {
            "pending"   => MonthlyStatementStatus.Pending,
            "completed" => MonthlyStatementStatus.Completed,
            "failed"    => MonthlyStatementStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown statement status value"),
        };
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Park>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.YandexParkId).HasMaxLength(100).IsRequired();
            e.Property(p => p.BankType).HasMaxLength(10).IsRequired();

            // New multi-tenancy fields
            e.Property(p => p.Slug).HasMaxLength(64).IsRequired();
            e.Property(p => p.LegalEntityName).HasMaxLength(200);
            e.Property(p => p.TaxId).HasMaxLength(32);
            e.Property(p => p.Phone).HasMaxLength(30);
            e.Property(p => p.BankProvider).HasMaxLength(40).IsRequired();
            e.Property(p => p.BankAccountIban).HasMaxLength(34);

            // Encrypted at rest
            e.Property(p => p.YandexClientIdEncrypted).HasConversion(Encrypted);
            e.Property(p => p.YandexApiKeyEncrypted).HasConversion(Encrypted);
            e.Property(p => p.BankCredentialsEncrypted).HasConversion(Encrypted);

            // Per-park fee & limits (PAYTAXI-CONTEXT.md §2/§5)
            e.Property(p => p.CashoutFee).HasPrecision(18, 2).HasDefaultValue(0.50m);
            e.Property(p => p.MinCashoutAmount).HasPrecision(18, 2).HasDefaultValue(5m);
            e.Property(p => p.MaxCashoutAmount).HasPrecision(18, 2);
            e.Property(p => p.DailyCashoutLimitPerDriver).HasPrecision(18, 2);
            e.Property(p => p.SwichSharePercent).HasPrecision(5, 2).HasDefaultValue(50m);
            e.Property(p => p.Phase1SharePercent).HasPrecision(5, 2);
            e.Property(p => p.Phase1CapGel).HasPrecision(18, 2);

            // Enums stored as text
            e.Property(p => p.OperatingModel)
                .HasConversion(OperatingModelConverter)
                .HasMaxLength(20)
                .IsRequired();

            e.Property(p => p.Status)
                .HasConversion(ParkStatusConverter)
                .HasMaxLength(20)
                .IsRequired();
            e.Property(p => p.StatusReason).HasMaxLength(500);
            e.Property(p => p.StatusChangedBy).HasMaxLength(200);
            e.Property(p => p.AuthorisationRevocationReason).HasMaxLength(500);
            e.Property(p => p.AuthorisationRevocationRecordedBy).HasMaxLength(200);

            // Unique slug for subdomain/path routing
            e.HasIndex(p => p.Slug).IsUnique();

            // Licensee parks are settled together under the licence agreement; null = Swich-direct.
            // Restrict: a licensee with parks cannot vanish and silently turn them into direct parks.
            e.HasOne(p => p.Licensee).WithMany(l => l.Parks).HasForeignKey(p => p.LicenseeId)
                .OnDelete(DeleteBehavior.Restrict);

            // DB-level guards. Column names are the property names (EF default Pascal case).
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Parks_OperatingModel",
                    "\"OperatingModel\" IN ('model_a', 'model_a5', 'model_b')");

                t.HasCheckConstraint("CK_Parks_Status",
                    "\"Status\" IN ('pending', 'active', 'suspended', 'terminated')");

                t.HasCheckConstraint("CK_Parks_SlugFormat",
                    "\"Slug\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$'");

                t.HasCheckConstraint("CK_Parks_CashoutFee_NonNegative", "\"CashoutFee\" >= 0");
                t.HasCheckConstraint("CK_Parks_MinCashout_Positive", "\"MinCashoutAmount\" > 0");
                t.HasCheckConstraint("CK_Parks_SwichShare_Percent", "\"SwichSharePercent\" >= 0 AND \"SwichSharePercent\" <= 100");
                // A revocation (Annex 3 §7) is never a bare date: it has the notice it follows from, a reason and an author,
                // and it takes effect after the notice. The length of the notice period is the API's rule (the contract's
                // [30] days is still bracketed).
                t.HasCheckConstraint("CK_Parks_AuthorisationRevocation",
                    "\"AuthorisationRevokedFrom\" IS NULL OR (\"AuthorisationNoticeOn\" IS NOT NULL AND \"AuthorisationRevokedFrom\" > \"AuthorisationNoticeOn\" " +
                    "AND \"AuthorisationRevocationReason\" IS NOT NULL AND \"AuthorisationRevocationRecordedBy\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<Settlement>(e =>
        {
            e.Property(s => s.FeeTotal).HasPrecision(18, 4);
            e.Property(s => s.Phase1Fees).HasPrecision(18, 4);
            e.Property(s => s.Phase2Fees).HasPrecision(18, 4);
            e.Property(s => s.SwichShare).HasPrecision(18, 2);
            e.Property(s => s.ParkShare).HasPrecision(18, 2);
            e.Property(s => s.CumulativeFeesBefore).HasPrecision(18, 4);
            e.Property(s => s.IdempotencyKey).HasMaxLength(80).IsRequired();
            e.Property(s => s.BankTransferId).HasMaxLength(100);
            e.Property(s => s.FailureReason).HasMaxLength(1000);
            e.Property(s => s.Description).HasMaxLength(200).IsRequired();
            e.Property(s => s.InvoiceRef).HasMaxLength(20).IsRequired();
            e.Property(s => s.SwichIban).HasMaxLength(34);
            e.Property(s => s.InitiatedBy).HasMaxLength(120);
            // Status is a concurrency token: every tracked save of a settlement is "UPDATE … WHERE Status = <what this
            // executor read>". Two executors (the nightly tick, the Retry button, "Settle now") can never write over a
            // state the other one produced — e.g. turn a transfer the other just completed back into Failed, or book
            // its SettlementSent a second time; the loser's save fails and it re-reads the row instead.
            e.Property(s => s.Status)
                .HasConversion(SettlementStatusConverter)
                .HasMaxLength(20)
                .IsRequired()
                .IsConcurrencyToken();
            e.Property(s => s.ExternalPayer).HasMaxLength(20);
            e.Property(s => s.ExternalReference).HasMaxLength(100);
            e.Property(s => s.ExternalNote).HasMaxLength(500);
            e.Property(s => s.ExternalRecordedBy).HasMaxLength(120);
            // One Swich-direct monthly row per park per month (SettlementDate = the month's last day), and one row per
            // park in a licensee statement. A park moved between the two in a month has one of each: every fee is
            // settled under the regime it was earned under (Cashout.FeeRegimeStamped). Legacy nightly rows
            // (IsMonthly = false) were unique per day under the old rule and stay outside both indexes, so a legacy
            // row dated on a month's last day cannot block that month's settlement.
            e.HasIndex(s => new { s.ParkId, s.SettlementDate })
                .IsUnique()
                .HasFilter("\"IsMonthly\" = TRUE AND \"MonthlyStatementId\" IS NULL");
            e.HasIndex(s => s.IdempotencyKey).IsUnique();
            e.HasIndex(s => new { s.Status, s.SettlementDate });
            e.HasIndex(s => new { s.MonthlyStatementId, s.ParkId }).IsUnique();
            e.HasOne(s => s.Park).WithMany(p => p.Settlements).HasForeignKey(s => s.ParkId)
                .OnDelete(DeleteBehavior.Cascade);
            // Money records: a statement with park transfers is never deleted from under them.
            e.HasOne(s => s.MonthlyStatement).WithMany(m => m.Settlements).HasForeignKey(s => s.MonthlyStatementId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.ParkBankAccount).WithMany().HasForeignKey(s => s.ParkBankAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_Settlements_Status",
                "\"Status\" IN ('pending', 'processing', 'completed', 'failed', 'settled_externally')"));
        });

        modelBuilder.Entity<Licensee>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(200).IsRequired();
            e.Property(l => l.TaxId).HasMaxLength(32);
            e.Property(l => l.Email).HasMaxLength(200);
            e.Property(l => l.Iban).HasMaxLength(34);
            // No database defaults: with HasDefaultValue EF leaves a CLR-default value (0 / false) out of the INSERT,
            // so a licensee registered with no barrier, a 0 % share or inactive would silently be stored as
            // 20,000 / 50 % / active. The entity's initialisers already supply the agreement's values.
            e.Property(l => l.BarrierGel).HasPrecision(18, 2);
            e.Property(l => l.SwichSharePercent).HasPrecision(5, 2);
            // At most ONE licensee in force, here as well as by the API (409 second_active_licensee). Operators are scoped to
            // their own licensee's parks now (AdminTokenCheck); the rule stays while the contract calendar and the incident
            // metrics assume a single licensee. Drop this index when they no longer do.
            e.HasIndex(l => l.IsActive)
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE")
                .HasDatabaseName("IX_Licensees_SingleActive");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Licensees_Barrier_NonNegative", "\"BarrierGel\" >= 0");
                t.HasCheckConstraint("CK_Licensees_SwichShare_Percent", "\"SwichSharePercent\" >= 0 AND \"SwichSharePercent\" <= 100");
            });
        });

        modelBuilder.Entity<CommonCost>(e =>
        {
            e.Property(c => c.PaidBy).HasConversion(CostPaidByConverter).HasMaxLength(20).IsRequired();
            e.Property(c => c.Category).HasConversion(CostCategoryConverter).HasMaxLength(30).IsRequired();
            e.Property(c => c.Vendor).HasMaxLength(200).IsRequired();
            e.Property(c => c.InvoiceNumber).HasMaxLength(100).IsRequired();
            e.Property(c => c.Amount).HasPrecision(18, 2);
            e.Property(c => c.Note).HasMaxLength(500);
            e.Property(c => c.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(c => c.Reason).HasMaxLength(500);
            e.Property(c => c.HeldBy).HasMaxLength(200);
            e.Property(c => c.HoldReason).HasMaxLength(500);
            e.Property(c => c.DeletedBy).HasMaxLength(200);
            e.Property(c => c.DeleteReason).HasMaxLength(500);
            // The statement builder scans "not yet included, invoice dated by month end" per licensee.
            e.HasIndex(c => new { c.LicenseeId, c.StatementId, c.InvoiceDate });
            e.HasIndex(c => c.CorrectsId);
            e.HasOne(c => c.Licensee).WithMany(l => l.Costs).HasForeignKey(c => c.LicenseeId)
                .OnDelete(DeleteBehavior.Restrict);
            // An included cost is part of settled money: the statement cannot be deleted from under it.
            e.HasOne(c => c.Statement).WithMany(m => m.Costs).HasForeignKey(c => c.StatementId)
                .OnDelete(DeleteBehavior.Restrict);
            // A correction points at the included original it corrects; the original can never go from under it.
            e.HasOne(c => c.Corrects).WithMany(c => c.Corrections).HasForeignKey(c => c.CorrectsId)
                .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_CommonCosts_PaidBy", "\"PaidBy\" IN ('swich', 'licensee')");
                t.HasCheckConstraint("CK_CommonCosts_Category",
                    "\"Category\" IN ('bank', 'sms', 'payment_provider', 'support_comms', 'hosting', 'domain')");
                // An invoice is above zero; only a correction may be negative (still never zero).
                t.HasCheckConstraint("CK_CommonCosts_Amount",
                    "\"Amount\" > 0 OR (\"CorrectsId\" IS NOT NULL AND \"Amount\" <> 0)");
                t.HasCheckConstraint("CK_CommonCosts_Correction_Reason", "\"CorrectsId\" IS NULL OR \"Reason\" IS NOT NULL");
                // Deleted and held entries are never counted — a statement can never have included one.
                t.HasCheckConstraint("CK_CommonCosts_Deleted",
                    "\"DeletedAt\" IS NULL OR (\"StatementId\" IS NULL AND \"DeletedBy\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL)");
                t.HasCheckConstraint("CK_CommonCosts_Held",
                    "\"HeldAt\" IS NULL OR (\"StatementId\" IS NULL AND \"HeldBy\" IS NOT NULL AND \"HoldReason\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<AgreementEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.Property(x => x.OldValue).HasMaxLength(4000);
            e.Property(x => x.NewValue).HasMaxLength(4000);
            e.Property(x => x.Reason).HasMaxLength(500);
            // The change history is read per licensee, newest first; a cost's own entries by its id.
            e.HasIndex(x => new { x.LicenseeId, x.At });
            e.HasIndex(x => x.ReferenceId);
            e.HasOne(x => x.Licensee).WithMany(l => l.Events).HasForeignKey(x => x.LicenseeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AgreementEvents_Kind",
                    "\"Kind\" IN ('launch_date', 'terms', 'cost_added', 'cost_edited', 'cost_deleted', 'cost_held', 'cost_released', 'correction_added')");
                t.HasCheckConstraint("CK_AgreementEvents_Reason",
                    "\"Kind\" NOT IN ('launch_date', 'cost_deleted', 'cost_held', 'correction_added') OR \"Reason\" IS NOT NULL");
            });
        });

        modelBuilder.Entity<ParkConfigEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Subject).HasMaxLength(20).IsRequired();
            e.Property(x => x.Action).HasMaxLength(10).IsRequired();
            e.Property(x => x.ChangedFields).HasMaxLength(500).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            // Read per park, newest first; one account's or driver's history by its id. No foreign keys, to the park or
            // the subject: the trail outlives whatever it describes (and never blocks removing it).
            e.HasIndex(x => new { x.ParkId, x.At });
            e.HasIndex(x => x.SubjectId);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ParkConfigEvents_Subject", "\"Subject\" IN ('bank_account', 'park', 'driver')");
                t.HasCheckConstraint("CK_ParkConfigEvents_Action", "\"Action\" IN ('created', 'updated')");
            });
        });

        modelBuilder.Entity<MonthlyStatement>(e =>
        {
            foreach (var money in new[]
            {
                nameof(MonthlyStatement.FeeIncome), nameof(MonthlyStatement.LicenseeCosts), nameof(MonthlyStatement.SwichCosts),
                nameof(MonthlyStatement.CarryIn), nameof(MonthlyStatement.BarrierGel), nameof(MonthlyStatement.BarrierBefore),
                nameof(MonthlyStatement.Entitlement), nameof(MonthlyStatement.Due), nameof(MonthlyStatement.CarryOut),
            })
                e.Property(money).HasPrecision(18, 2);
            e.Property(m => m.SwichSharePercent).HasPrecision(5, 2);
            e.Property(m => m.BarrierFraction).HasPrecision(9, 6);
            e.Property(m => m.InvoiceRef).HasMaxLength(20).IsRequired();
            e.Property(m => m.InitiatedBy).HasMaxLength(120);
            e.Property(m => m.Phase).HasConversion(StatementPhaseConverter).HasMaxLength(20).IsRequired();
            e.Property(m => m.Status).HasConversion(MonthlyStatementStatusConverter).HasMaxLength(20).IsRequired();
            // One statement per licensee per month — the idempotency anchor of the monthly run.
            e.HasIndex(m => new { m.LicenseeId, m.Month }).IsUnique();
            e.HasOne(m => m.Licensee).WithMany(l => l.Statements).HasForeignKey(m => m.LicenseeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_MonthlyStatements_Phase", "\"Phase\" IN ('barrier', 'crossing', 'normal')");
                t.HasCheckConstraint("CK_MonthlyStatements_Status", "\"Status\" IN ('pending', 'completed', 'failed')");
                // Never collect a negative amount: a negative result is carried as a credit instead.
                t.HasCheckConstraint("CK_MonthlyStatements_Due_NonNegative", "\"Due\" >= 0");
            });
        });

        modelBuilder.Entity<ParkBankAccount>(e =>
        {
            e.Property(a => a.BankCode).HasMaxLength(2).IsRequired();
            e.Property(a => a.Provider).HasMaxLength(40).IsRequired();
            e.Property(a => a.Iban).HasMaxLength(34).IsRequired();
            e.Property(a => a.HolderName).HasMaxLength(200);
            e.Property(a => a.Label).HasMaxLength(100);
            e.Property(a => a.CredentialsEncrypted).IsRequired().HasConversion(Encrypted);
            e.HasIndex(a => new { a.ParkId, a.BankCode });
            e.HasIndex(a => new { a.ParkId, a.IsPrimary })
                .IsUnique()
                .HasFilter("\"IsPrimary\" = TRUE");
            e.HasOne(a => a.Park).WithMany(p => p.BankAccounts).HasForeignKey(a => a.ParkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankCard>(e =>
        {
            // IBAN is encrypted (ciphertext is longer than 34 chars) — duplicate checks go through IbanHash.
            e.Property(b => b.Iban).IsRequired().HasConversion(Encrypted);
            e.Property(b => b.IbanHash).HasMaxLength(LookupHashLength).IsRequired().HasDefaultValue("");
            e.Property(b => b.BankCode).HasMaxLength(2).IsRequired();
            e.Property(b => b.HolderName).HasMaxLength(200);
            e.Property(b => b.ThirdPartyReason).HasMaxLength(500);
            e.Property(b => b.AddedBy).HasMaxLength(200);
            e.Property(b => b.IsThirdPartyAccount).HasDefaultValue(false);
            e.Property(b => b.TokenReferenceEncrypted).HasDefaultValue("").HasConversion(Encrypted);
            e.HasIndex(b => new { b.DriverId, b.IbanHash });
        });

        modelBuilder.Entity<Driver>(e =>
        {
            e.Property(d => d.PhoneEncrypted).HasConversion(Encrypted);
            e.HasIndex(d => d.PhoneHash).IsUnique();
            e.HasIndex(d => new { d.ParkId, d.YandexDriverProfileId });
            e.Property(d => d.PhoneHash).HasMaxLength(LookupHashLength).IsRequired();
            e.Property(d => d.PreferredLanguage).HasMaxLength(2);
            e.ToTable(t => t.HasCheckConstraint("CK_Drivers_PreferredLanguage",
                "\"PreferredLanguage\" IS NULL OR \"PreferredLanguage\" IN ('ka', 'en', 'ru')"));
        });

        modelBuilder.Entity<Cashout>(e =>
        {
            e.Property(c => c.Amount).HasPrecision(18, 4);
            e.Property(c => c.Fee).HasPrecision(18, 4);
            e.HasIndex(c => c.IdempotencyKey).IsUnique();
            e.HasIndex(c => c.RetryOfCashoutId);
            e.HasIndex(c => new { c.DriverId, c.Status });
            e.HasIndex(c => new { c.ParkId, c.Status });
            e.HasIndex(c => c.InvoiceNumber).IsUnique();
            // Payout queue worker scans: Queued rows whose NextAttemptAt is due.
            e.HasIndex(c => new { c.Status, c.NextAttemptAt });
            e.Property(c => c.InitiatedBy).HasMaxLength(120);
            e.Property(c => c.YandexReversalTransactionId).HasMaxLength(100);
            e.HasOne(c => c.ParkBankAccount).WithMany().HasForeignKey(c => c.ParkBankAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(c => c.SettlementId);
            e.HasOne(c => c.Settlement).WithMany(s => s.Cashouts).HasForeignKey(c => c.SettlementId)
                .OnDelete(DeleteBehavior.SetNull);
            // The regime a fee was earned under. Restrict: a licensee with fees on record never vanishes from under them.
            e.HasOne<Licensee>().WithMany().HasForeignKey(c => c.FeeLicenseeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Cashouts_FeeRegime",
                "\"FeeLicenseeId\" IS NULL OR \"FeeRegimeStamped\""));
        });

        // Postgres sequence — assigned by SaveChanges in the orchestrator
        // (we don't make the column itself DB-generated so the migration
        // stays simple and we keep the option to backfill).
        modelBuilder.HasSequence<long>("InvoiceNumberSeq").StartsAt(1_000_000).IncrementsBy(1);

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.Property(l => l.Amount).HasPrecision(18, 4);
            e.HasIndex(l => l.CashoutId);
            e.HasIndex(l => l.SettlementId);
            e.HasIndex(l => new { l.ParkId, l.CreatedAt });
            e.HasOne(l => l.Cashout).WithMany(c => c.LedgerEntries).HasForeignKey(l => l.CashoutId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Settlement).WithMany().HasForeignKey(l => l.SettlementId)
                .OnDelete(DeleteBehavior.Cascade);
            // Append-only ledger: every row belongs to exactly one of the two.
            e.ToTable(t => t.HasCheckConstraint(
                "CK_LedgerEntries_Owner",
                "(\"CashoutId\" IS NOT NULL) <> (\"SettlementId\" IS NOT NULL)"));
        });

        modelBuilder.Entity<ApiAuditLog>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).UseIdentityAlwaysColumn();
            e.HasIndex(a => new { a.ParkId, a.Timestamp });
            e.HasIndex(a => a.CorrelationId);
            // The incident monitor asks "any Yandex call in the last few minutes?" every minute.
            e.HasIndex(a => new { a.ApiProvider, a.Timestamp });
            // The nightly retention deletes by age alone (older than 2 years).
            e.HasIndex(a => a.Timestamp);
            e.Property(a => a.ApiProvider).HasMaxLength(50).IsRequired();
            e.Property(a => a.Endpoint).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<YandexBalanceCache>(e =>
        {
            e.HasKey(y => y.DriverId);
            e.Property(y => y.Balance).HasPrecision(18, 4);
            e.HasOne(y => y.Driver).WithOne(d => d.BalanceCache)
                .HasForeignKey<YandexBalanceCache>(y => y.DriverId);
        });

        modelBuilder.Entity<DriverSession>(e =>
        {
            e.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(s => s.DeviceLabel).HasMaxLength(200);
            e.Property(s => s.RevokedReason).HasMaxLength(100);
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => new { s.DriverId, s.ExpiresAt });
            e.HasOne(s => s.Driver).WithMany().HasForeignKey(s => s.DriverId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DriverSignIn>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.DeviceLabel).HasMaxLength(200);
            e.HasIndex(x => new { x.DriverId, x.SignedInAt });
            e.HasIndex(x => x.SignedInAt);   // retention: older than 2 years
            e.HasOne(x => x.Driver).WithMany().HasForeignKey(x => x.DriverId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OtpCode>(e =>
        {
            e.HasIndex(o => o.PhoneHash);
            e.Property(o => o.PhoneHash).HasMaxLength(LookupHashLength).IsRequired();
        });

        modelBuilder.Entity<PhoneSignInEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.PhoneHash).HasMaxLength(LookupHashLength).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            // Every limit reads one phone's events of the last day; the retention deletes by age.
            e.HasIndex(x => new { x.PhoneHash, x.At });
            e.HasIndex(x => x.At);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_PhoneSignInEvents_Kind", "\"Kind\" IN ('code_issued', 'failed', 'locked')");
                t.HasCheckConstraint("CK_PhoneSignInEvents_Until", "(\"Kind\" = 'locked') = (\"Until\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<ReconciliationRun>(e =>
        {
            e.Property(r => r.Status)
                .HasConversion(ReconciliationStatusConverter)
                .HasMaxLength(20)
                .IsRequired();
            e.Property(r => r.Error).HasMaxLength(2000);
            e.HasIndex(r => new { r.ParkId, r.StartedAt });
            e.HasOne(r => r.Park).WithMany().HasForeignKey(r => r.ParkId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_ReconciliationRuns_Status",
                "\"Status\" IN ('running', 'completed', 'failed')"));
        });

        modelBuilder.Entity<ReconciliationDiscrepancy>(e =>
        {
            e.Property(d => d.Kind).HasMaxLength(40).IsRequired();
            e.Property(d => d.PaytaxiAmount).HasPrecision(18, 4);
            e.Property(d => d.ExternalAmount).HasPrecision(18, 4);
            e.Property(d => d.BankTransferId).HasMaxLength(100);
            e.Property(d => d.YandexTransactionId).HasMaxLength(100);
            e.Property(d => d.ResolvedBy).HasMaxLength(200);
            e.Property(d => d.Notes).HasMaxLength(2000);
            e.Property(d => d.ResolutionNotes).HasMaxLength(2000);
            e.Property(d => d.ParkNote).HasMaxLength(1000);
            e.Property(d => d.ParkNoteBy).HasMaxLength(200);
            e.HasIndex(d => new { d.ParkId, d.IsResolved });
            e.HasIndex(d => d.RunId);
            e.HasOne(d => d.Run).WithMany(r => r.Discrepancies).HasForeignKey(d => d.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Cashout).WithMany().HasForeignKey(d => d.CashoutId)
                .OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_ReconciliationDiscrepancies_Kind",
                "\"Kind\" IN ('missing_in_bank', 'orphaned_bank_send', 'missing_in_yandex', " +
                "'orphaned_yandex_debit', 'amount_mismatch_bank', 'amount_mismatch_yandex', 'stuck_pending')"));
        });

        // Incident log (Annex 1 §4). Numbers come from their own sequence as a column default, so the
        // database — not the app — hands out INC-0001, INC-0002… even with two API instances.
        modelBuilder.HasSequence<int>("IncidentNumberSeq").StartsAt(1).IncrementsBy(1);

        modelBuilder.Entity<Incident>(e =>
        {
            e.Property(i => i.Number).HasDefaultValueSql("nextval('\"IncidentNumberSeq\"')");
            e.HasIndex(i => i.Number).IsUnique();
            e.Ignore(i => i.Ref);
            e.Property(i => i.Title).HasMaxLength(200).IsRequired();
            e.Property(i => i.Description).HasMaxLength(4000);
            e.Property(i => i.Cause).HasMaxLength(1000);
            e.Property(i => i.ReportedBy).HasMaxLength(200).IsRequired();
            e.Property(i => i.AcknowledgedBy).HasMaxLength(200);
            e.Property(i => i.ExternalRestoredBy).HasMaxLength(200);
            e.Property(i => i.ResolvedBy).HasMaxLength(200);
            e.Property(i => i.DedupeKey).HasMaxLength(120);
            e.Property(i => i.Level).HasConversion(IncidentLevelConverter).HasMaxLength(10).IsRequired();
            e.Property(i => i.Status).HasConversion(IncidentStatusConverter).HasMaxLength(20).IsRequired();
            e.Property(i => i.Fault).HasConversion(IncidentFaultConverter).HasMaxLength(20).IsRequired();
            e.Property(i => i.Source).HasConversion(IncidentSourceConverter).HasMaxLength(10).IsRequired();
            e.HasIndex(i => new { i.Status, i.ReportedAt });
            e.HasIndex(i => new { i.ParkId, i.ReportedAt });
            // One open incident per monitored condition; a resolved one frees the key for the next occurrence.
            e.HasIndex(i => i.DedupeKey)
                .IsUnique()
                .HasFilter("\"DedupeKey\" IS NOT NULL AND \"Status\" <> 'resolved'");
            e.HasOne(i => i.Park).WithMany().HasForeignKey(i => i.ParkId)
                .OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Incidents_Level", "\"Level\" IN ('p1', 'p2', 'p3')");
                t.HasCheckConstraint("CK_Incidents_Status", "\"Status\" IN ('reported', 'acknowledged', 'resolved')");
                t.HasCheckConstraint("CK_Incidents_Fault", "\"Fault\" IN ('platform', 'external', 'park', 'unknown')");
                t.HasCheckConstraint("CK_Incidents_Source", "\"Source\" IN ('manual', 'auto')");
                // A resolved incident has a resolution time, an open one does not.
                t.HasCheckConstraint("CK_Incidents_Resolved",
                    "(\"Status\" = 'resolved') = (\"ResolvedAt\" IS NOT NULL)");
            });
        });

        modelBuilder.Entity<IncidentUpdate>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).UseIdentityAlwaysColumn();
            e.Property(u => u.By).HasMaxLength(200).IsRequired();
            e.Property(u => u.Kind).HasMaxLength(30).IsRequired();
            e.Property(u => u.Text).HasMaxLength(4000).IsRequired();
            e.HasIndex(u => new { u.IncidentId, u.At });
            e.HasOne(u => u.Incident).WithMany(i => i.Updates).HasForeignKey(u => u.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_IncidentUpdates_Kind",
                "\"Kind\" IN ('reported', 'acknowledged', 'updated', 'note', 'external_restored', 'resolved', 'reopened')"));
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.Property(n => n.Type).HasMaxLength(40).IsRequired();
            e.Property(n => n.Title).HasMaxLength(200).IsRequired();
            e.Property(n => n.Body).HasMaxLength(1000).IsRequired();
            e.Property(n => n.Link).HasMaxLength(200);
            e.HasIndex(n => new { n.DriverId, n.CreatedAt });
            e.HasIndex(n => new { n.DriverId, n.IsRead });
            e.HasOne(n => n.Driver).WithMany().HasForeignKey(n => n.DriverId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminUser>(e =>
        {
            e.Property(a => a.Email).HasMaxLength(200).IsRequired();
            e.Property(a => a.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(a => a.Name).HasMaxLength(200);
            e.Property(a => a.Role).HasMaxLength(20).IsRequired();
            e.HasIndex(a => a.Email).IsUnique();
            e.HasOne(a => a.Park).WithMany().HasForeignKey(a => a.ParkId)
                .OnDelete(DeleteBehavior.SetNull);
            // An operator whose licensee went away sees no licensee figures at all (fail closed), never another's.
            e.HasOne(a => a.Licensee).WithMany().HasForeignKey(a => a.LicenseeId)
                .OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_AdminUsers_Role",
                "\"Role\" IN ('super_admin', 'operator', 'park_admin')"));
        });

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges()
    {
        StampUpdatedAt();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampUpdatedAt();
        return base.SaveChangesAsync(ct);
    }

    private void StampUpdatedAt()
    {
        // The agreement's change history is the record both sides rely on (§2.6): an entry is written once and never
        // rewritten or removed — refused here too, not only by having no endpoint that would.
        foreach (var entry in ChangeTracker.Entries<AgreementEvent>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"Agreement event {entry.Entity.Id} is append-only — it cannot be changed or deleted");
        // Same for the trail of rail and credential changes, and for the money ledger itself: our own truth source for
        // reconciliation is only that if nothing in the application can rewrite or remove a row of it. (No code does
        // today; this makes a future one fail loudly instead of quietly rewriting money history.)
        foreach (var entry in ChangeTracker.Entries<ParkConfigEvent>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"Park configuration event {entry.Entity.Id} is append-only — it cannot be changed or deleted");
        foreach (var entry in ChangeTracker.Entries<LedgerEntry>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"Ledger entry {entry.Entity.Id} is append-only — it cannot be changed or deleted");

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = DateTime.UtcNow;
    }
}
