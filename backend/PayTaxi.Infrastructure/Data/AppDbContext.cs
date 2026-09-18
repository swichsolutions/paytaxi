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
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<BankCard> BankCards => Set<BankCard>();
    public DbSet<Cashout> Cashouts => Set<Cashout>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<ApiAuditLog> ApiAuditLogs => Set<ApiAuditLog>();
    public DbSet<YandexBalanceCache> YandexBalanceCaches => Set<YandexBalanceCache>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationDiscrepancy> ReconciliationDiscrepancies => Set<ReconciliationDiscrepancy>();

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

    /// <summary>
    /// AES-GCM at rest for PII / credential columns. Legacy plaintext is read transparently and
    /// re-encrypted by <see cref="EncryptionMigrator"/> at startup. Never filter these columns by
    /// equality — use the *Hash companions (PhoneHash, IbanHash).
    /// </summary>
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
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown SettlementStatus"),
        };

        public static SettlementStatus ToSettlementStatus(string v) => v switch
        {
            "pending"    => SettlementStatus.Pending,
            "processing" => SettlementStatus.Processing,
            "completed"  => SettlementStatus.Completed,
            "failed"     => SettlementStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown settlement status value"),
        };

        public static ReconciliationStatus ToReconciliationStatus(string v) => v switch
        {
            "running"   => ReconciliationStatus.Running,
            "completed" => ReconciliationStatus.Completed,
            "failed"    => ReconciliationStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v, "Unknown reconciliation_status value"),
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

            // Unique slug for subdomain/path routing
            e.HasIndex(p => p.Slug).IsUnique();

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
            e.Property(s => s.Status)
                .HasConversion(SettlementStatusConverter)
                .HasMaxLength(20)
                .IsRequired();
            e.HasIndex(s => new { s.ParkId, s.SettlementDate }).IsUnique();
            e.HasIndex(s => s.IdempotencyKey).IsUnique();
            e.HasIndex(s => new { s.Status, s.SettlementDate });
            e.HasOne(s => s.Park).WithMany(p => p.Settlements).HasForeignKey(s => s.ParkId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.ParkBankAccount).WithMany().HasForeignKey(s => s.ParkBankAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_Settlements_Status",
                "\"Status\" IN ('pending', 'processing', 'completed', 'failed')"));
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
            e.Property(b => b.IbanHash).HasMaxLength(64).IsRequired().HasDefaultValue("");
            e.Property(b => b.BankCode).HasMaxLength(2).IsRequired();
            e.Property(b => b.HolderName).HasMaxLength(200);
            e.Property(b => b.TokenReferenceEncrypted).HasDefaultValue("").HasConversion(Encrypted);
            e.HasIndex(b => new { b.DriverId, b.IbanHash });
        });

        modelBuilder.Entity<Driver>(e =>
        {
            e.Property(d => d.PhoneEncrypted).HasConversion(Encrypted);
            e.HasIndex(d => d.PhoneHash).IsUnique();
            e.HasIndex(d => new { d.ParkId, d.YandexDriverProfileId });
            e.Property(d => d.PhoneHash).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<Cashout>(e =>
        {
            e.Property(c => c.Amount).HasPrecision(18, 4);
            e.Property(c => c.Fee).HasPrecision(18, 4);
            e.HasIndex(c => c.IdempotencyKey).IsUnique();
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

        modelBuilder.Entity<OtpCode>(e =>
        {
            e.HasIndex(o => o.PhoneHash);
            e.Property(o => o.PhoneHash).HasMaxLength(64).IsRequired();
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
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = DateTime.UtcNow;
    }
}
