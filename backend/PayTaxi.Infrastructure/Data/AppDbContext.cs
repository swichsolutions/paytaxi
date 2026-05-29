using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;

namespace PayTaxi.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Park> Parks => Set<Park>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<BankCard> BankCards => Set<BankCard>();
    public DbSet<Cashout> Cashouts => Set<Cashout>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<ApiAuditLog> ApiAuditLogs => Set<ApiAuditLog>();
    public DbSet<YandexBalanceCache> YandexBalanceCaches => Set<YandexBalanceCache>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Notification> Notifications => Set<Notification>();

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
            e.Property(p => p.BankProvider).HasMaxLength(40).IsRequired();
            e.Property(p => p.BankAccountIban).HasMaxLength(34);
            e.Property(p => p.AuthorizationLimit).HasPrecision(18, 2);

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

                // Authorization limit only meaningful for Model A.5; reject negative values.
                t.HasCheckConstraint("CK_Parks_AuthorizationLimit_NonNegative",
                    "\"AuthorizationLimit\" IS NULL OR \"AuthorizationLimit\" >= 0");
            });
        });

        modelBuilder.Entity<Driver>(e =>
        {
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
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.Property(l => l.Amount).HasPrecision(18, 4);
            e.HasIndex(l => l.CashoutId);
            e.HasIndex(l => new { l.ParkId, l.CreatedAt });
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
                "\"Role\" IN ('super_admin', 'park_admin')"));
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
