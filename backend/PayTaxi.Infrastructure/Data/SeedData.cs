using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Banking;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Security;

namespace PayTaxi.Infrastructure.Data;

/// <summary>
/// Seeds 3 demo parks and their drivers if the database is empty.
/// Driver YandexDriverProfileIds match the in-memory mock data so the mock
/// Yandex client can resolve balances correctly.
///
/// Every park gets a TBC payout account (the launch rail) and a BoG one (Phase 2)
/// with provider=mock, so the routing-by-IBAN path is exercised end to end in dev.
/// Every demo driver gets one TBC destination IBAN (default) and one BoG.
///
/// Phone numbers are stored "encrypted" (plaintext placeholder until Phase 8
/// brings real AES + Key Vault). PhoneHash is real SHA-256 — already correct.
/// </summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await db.Database.MigrateAsync();

        if (await db.Parks.AnyAsync())
        {
            log.LogInformation("Database already seeded — skipping park seed");
            await EnsurePhonesBackfilledAsync(db, log);
            await EnsureParkBankAccountsSeededAsync(db, log);
            await EnsureBankCardsSeededAsync(db, log);
            await EnsureAdminUsersSeededAsync(db, log);
            await EnsureOperatorSeededAsync(db, log);
            await EnsureSettlementConfigSeededAsync(db, log);
            await EncryptionMigrator.EnsureEncryptedAsync(db, log);
            return;
        }

        log.LogInformation("Seeding 3 demo parks with drivers");

        var parks = new[]
        {
            BuildPark(
                name: "Tbilisi Auto Park #3",
                yandexParkId: "yx_park_tb3",
                legalEntityName: "Tbilisi Taxi Service LLC",
                taxId: "405123456",
                phone: "+995 322 12 34 56",
                tbcIban: GeorgianIban.Build("TB", "7000000001234567"),
                bogIban: GeorgianIban.Build("BG", "0000000123456789"),
                drivers: new[]
                {
                    ("yp_tb3_001", "გიორგი მამულაშვილი",      "+995599123456"),
                    ("yp_tb3_002", "ნიკა ჯავახიშვილი",        "+995597224119"),
                    ("yp_tb3_003", "Levan Kobakhidze",         "+995555332010"),
                    ("yp_tb3_004", "ბექა გელაშვილი",           "+995599887442"),
                    ("yp_tb3_005", "დავით ჩხეიძე",             "+995593123098"),
                    ("yp_tb3_006", "Zura Mikeladze",           "+995595412776"),
                    ("yp_tb3_007", "ვალერი თავაძე",            "+995591661020"),
                    ("yp_tb3_008", "ალექსანდრე გვინიაშვილი",   "+995599410220"),
                }),

            BuildPark(
                name: "Tbilisi Auto Park #5",
                yandexParkId: "yx_park_tb5",
                legalEntityName: "Park-5 Operations Ltd.",
                taxId: "404987654",
                phone: "+995 322 55 88 99",
                tbcIban: GeorgianIban.Build("TB", "7000000987654321"),
                bogIban: null, // TBC-only park: BoG-card drivers can't be paid here (by design)
                drivers: new[]
                {
                    ("yp_tb5_001", "Badri Macharashvili",      "+995597333001"),
                    ("yp_tb5_002", "გელა ცინცაძე",             "+995593442818"),
                    ("yp_tb5_003", "Dimitri Lomidze",          "+995555600412"),
                    ("yp_tb5_004", "ემზარ ჯაფარიძე",           "+995591808999"),
                    ("yp_tb5_005", "ვახტანგი ჩხარტიშვილი",     "+995599727191"),
                    ("yp_tb5_006", "ზაზა ჯვარაძე",             "+995555311220"),
                    ("yp_tb5_007", "Kakha Chubinidze",         "+995597414808"),
                    ("yp_tb5_008", "მამუკა ხატიური",           "+995593700010"),
                }),

            BuildPark(
                name: "Batumi Auto Park #1",
                yandexParkId: "yx_park_bt1",
                legalEntityName: "Batumi Taxi Co.",
                taxId: "402555111",
                phone: "+995 422 77 22 11",
                tbcIban: GeorgianIban.Build("TB", "7000000555111222"),
                bogIban: GeorgianIban.Build("BG", "0000000555111222"),
                drivers: new[]
                {
                    ("yp_bt1_001", "ნუგზარ შავიშვილი",         "+995599188220"),
                    ("yp_bt1_002", "Otar Gogoberidze",         "+995555220030"),
                    ("yp_bt1_003", "პაატა მარგველაშვილი",      "+995591555717"),
                    ("yp_bt1_004", "რეზო ჩიქოვანი",            "+995597919008"),
                    ("yp_bt1_005", "Soso Kakhiani",            "+995555700080"),
                    ("yp_bt1_006", "ტარიელ ჯანდიერი",          "+995593414200"),
                    ("yp_bt1_007", "იოსები ბერიძე",            "+995599050100"),
                    ("yp_bt1_008", "გიორგი ხუციშვილი",         "+995555980020"),
                }),
        };

        db.Parks.AddRange(parks);
        await db.SaveChangesAsync();
        log.LogInformation("Seed complete: {ParkCount} parks, {DriverCount} drivers",
            parks.Length, parks.Sum(p => p.Drivers.Count));

        await EnsureBankCardsSeededAsync(db, log);
        await EnsureAdminUsersSeededAsync(db, log);
        await EnsureOperatorSeededAsync(db, log);
        await EnsureSettlementConfigSeededAsync(db, log);
        await EncryptionMigrator.EnsureEncryptedAsync(db, log);
    }

    /// <summary>
    /// Demo revenue-split config: Tbilisi #3 plays "Levan's own park" (100% to Swich until
    /// 20,000 GEL of fees, then 50/50); the others are network parks (50/50).
    /// </summary>
    private static async Task EnsureSettlementConfigSeededAsync(AppDbContext db, ILogger log)
    {
        var levan = await db.Parks.FirstOrDefaultAsync(p => p.Slug == "tbilisi-auto-park-3");
        if (levan is null || levan.Phase1CapGel is not null) return;
        levan.Phase1SharePercent = 100m;
        levan.Phase1CapGel = 20_000m;
        levan.SwichSharePercent = 50m;
        await db.SaveChangesAsync();
        log.LogInformation("Seeded phase-1 revenue split on {Park} (100% until 20,000 GEL, then 50/50)", levan.Name);
    }

    /// <summary>Operator login for the exclusive operator's staff (sees all parks, no Swich-only controls).</summary>
    private static async Task EnsureOperatorSeededAsync(AppDbContext db, ILogger log)
    {
        if (await db.AdminUsers.AnyAsync(a => a.Role == "operator")) return;
        db.AdminUsers.Add(new AdminUser
        {
            Email = "levan@operator.local",
            Name = "Levan · Operator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("operator1!"),
            Role = "operator",
            ParkId = null,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        log.LogInformation("Dev creds — operator: levan@operator.local / operator1!");
    }

    /// <summary>
    /// Seeds one super-admin + one park-admin per park, idempotently.
    /// Dev passwords match the spec (super: <c>swich2026!</c>, park: <c>park{n}!</c>).
    /// </summary>
    private static async Task EnsureAdminUsersSeededAsync(AppDbContext db, ILogger log)
    {
        if (await db.AdminUsers.AnyAsync())
        {
            log.LogInformation("Admin users already seeded");
            return;
        }

        var parks = await db.Parks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        if (parks.Count == 0) return;

        var admins = new List<AdminUser>
        {
            new()
            {
                Email = "ops@swich.dev",
                Name = "Swich Operator",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("swich2026!"),
                Role = "super_admin",
                ParkId = null,
                IsActive = true,
            },
        };

        foreach (var (park, idx) in parks.Select((p, i) => (p, i + 1)))
        {
            admins.Add(new AdminUser
            {
                Email = $"manager@{park.Slug}.local",
                Name = $"Manager · {park.Name}",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword($"park{idx}!"),
                Role = "park_admin",
                ParkId = park.Id,
                IsActive = true,
            });
        }

        db.AdminUsers.AddRange(admins);
        await db.SaveChangesAsync();
        log.LogInformation("Seeded {Count} admin users (1 super, {ParkAdmins} per park)",
            admins.Count, admins.Count - 1);
        log.LogInformation("Dev creds — super: ops@swich.dev / swich2026!");
        foreach (var (park, idx) in parks.Select((p, i) => (p, i + 1)))
            log.LogInformation("Dev creds — park {Park}: manager@{Slug}.local / park{Idx}!",
                park.Name, park.Slug, idx);
    }

    /// <summary>
    /// One-off back-fill for the Park.Phone column on an already-seeded DB.
    /// Idempotent — only writes for parks that have no phone yet.
    /// </summary>
    private static async Task EnsurePhonesBackfilledAsync(AppDbContext db, ILogger log)
    {
        var phonesBySlug = new Dictionary<string, string>
        {
            ["tbilisi-auto-park-3"] = "+995 322 12 34 56",
            ["tbilisi-auto-park-5"] = "+995 322 55 88 99",
            ["batumi-auto-park-1"]  = "+995 422 77 22 11",
        };

        var parks = await db.Parks.Where(p => p.Phone == null).ToListAsync();
        if (parks.Count == 0) return;

        foreach (var p in parks)
        {
            if (phonesBySlug.TryGetValue(p.Slug, out var phone))
                p.Phone = phone;
        }
        await db.SaveChangesAsync();
        log.LogInformation("Back-filled phone numbers for {Count} parks", parks.Count);
    }

    /// <summary>
    /// Upgrade path for databases seeded before ParkBankAccounts existed: every park
    /// without any payout account gets a mock TBC account (from its legacy IBAN when
    /// that is a TBC IBAN, else a generated one) and — unless it was seeded as TBC-only —
    /// a mock BoG account too.
    /// </summary>
    private static async Task EnsureParkBankAccountsSeededAsync(AppDbContext db, ILogger log)
    {
        var parks = await db.Parks
            .Where(p => !p.BankAccounts.Any())
            .ToListAsync();
        if (parks.Count == 0) return;

        var rng = new Random(7);
        foreach (var p in parks)
        {
            var legacyIsTbc = p.BankAccountIban is { Length: 22 } && p.BankAccountIban.Substring(4, 2) == "TB";
            var tbcIban = legacyIsTbc ? p.BankAccountIban! : GeorgianIban.Build("TB", RandomDigits(rng, 16));
            db.ParkBankAccounts.Add(NewAccount(p, "TB", "mock", tbcIban, isPrimary: true, label: "TBC business account"));

            if (p.Slug != "tbilisi-auto-park-5")
            {
                var bogIban = p.BankAccountIban is { Length: 22 } && p.BankAccountIban.Substring(4, 2) == "BG"
                    ? p.BankAccountIban
                    : GeorgianIban.Build("BG", RandomDigits(rng, 16));
                db.ParkBankAccounts.Add(NewAccount(p, "BG", "mock", bogIban, isPrimary: false, label: "BoG account (Phase 2)"));
            }

            p.BankAccountIban = tbcIban;
            p.BankProvider = "mock";
            p.BankType = "MOCK";
            p.OperatingModel = OperatingModel.ModelA;
        }
        await db.SaveChangesAsync();
        log.LogInformation("Seeded payout bank accounts for {Count} parks", parks.Count);
    }

    /// <summary>
    /// Back-fills payout destinations for demo drivers that have none: one TBC IBAN
    /// (default) + one BoG IBAN, all with valid check digits. Also upgrades legacy
    /// card rows (empty IBAN) in place so pre-existing cashouts keep their FK.
    /// Safe to re-run.
    /// </summary>
    private static async Task EnsureBankCardsSeededAsync(AppDbContext db, ILogger log)
    {
        var rng = new Random(42); // deterministic across reseeds

        // 1. Legacy rows: token-only cards from before the IBAN column existed.
        var legacy = await db.BankCards.Where(b => b.BankCode == "").ToListAsync(); // Iban is encrypted: never filter on it
        foreach (var b in legacy)
        {
            var code = string.Equals(b.BankType, "TBC", StringComparison.OrdinalIgnoreCase) ? "TB" : "BG";
            b.Iban = GeorgianIban.Build(code, RandomDigits(rng, 16));
            b.IbanHash = FieldEncryptor.Hash(b.Iban);
            b.BankCode = code;
            b.BankType = GeorgianIban.BankLabel(code);
            b.MaskedPan = GeorgianIban.Mask(b.Iban);
        }
        if (legacy.Count > 0)
        {
            await db.SaveChangesAsync();
            log.LogInformation("Upgraded {Count} legacy card rows to IBAN destinations", legacy.Count);
        }

        // 2. Drivers with no destination at all.
        var driversNeedingCards = await db.Drivers
            .Where(d => !d.BankCards.Any())
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();

        if (driversNeedingCards.Count == 0)
        {
            log.LogInformation("Payout destinations: all drivers already have at least one");
            return;
        }

        var cards = new List<BankCard>(driversNeedingCards.Count * 2);
        foreach (var d in driversNeedingCards)
        {
            cards.Add(NewDestination(d.Id, d.Name, "TB", RandomDigits(rng, 16), isDefault: true));
            cards.Add(NewDestination(d.Id, d.Name, "BG", RandomDigits(rng, 16), isDefault: false));
        }

        db.BankCards.AddRange(cards);
        await db.SaveChangesAsync();
        log.LogInformation("Seeded {Count} payout destinations for {DriverCount} drivers",
            cards.Count, driversNeedingCards.Count);
    }

    private static Park BuildPark(
        string name,
        string yandexParkId,
        string legalEntityName,
        string taxId,
        string phone,
        string tbcIban,
        string? bogIban,
        IEnumerable<(string YandexId, string Name, string Phone)> drivers)
    {
        var park = new Park
        {
            Name = name,
            Slug = ToSlug(name),
            LegalEntityName = legalEntityName,
            TaxId = taxId,
            Phone = phone,
            YandexParkId = yandexParkId,
            YandexClientIdEncrypted = "mock_client_id",
            YandexApiKeyEncrypted = "mock_api_key",
            OperatingModel = OperatingModel.ModelA,
            CashoutFee = 0.50m,
            MinCashoutAmount = 5m,
            BankProvider = "mock",
            BankType = "MOCK",
            BankCredentialsEncrypted = "{\"provider\":\"mock\"}",
            BankAccountIban = tbcIban,
            Status = ParkStatus.Active,
#pragma warning disable CS0618 // legacy bool, see Park.IsActive
            IsActive = true,
#pragma warning restore CS0618
        };

        park.BankAccounts.Add(NewAccount(park, "TB", "mock", tbcIban, isPrimary: true, label: "TBC business account"));
        if (bogIban is not null)
            park.BankAccounts.Add(NewAccount(park, "BG", "mock", bogIban, isPrimary: false, label: "BoG account (Phase 2)"));

        foreach (var (yid, name_, driverPhone) in drivers)
        {
            park.Drivers.Add(new Driver
            {
                ParkId = park.Id,
                Park = park,
                Name = name_,
                YandexDriverProfileId = yid,
                PhoneEncrypted = driverPhone, // Plaintext until Phase 8 — value is mock-only
                PhoneHash = HashPhone(driverPhone),
                Status = DriverStatus.Active,
                ConsentGiven = true,
                ConsentTimestamp = DateTime.UtcNow.AddDays(-90),
            });
        }

        return park;
    }

    private static ParkBankAccount NewAccount(Park park, string bankCode, string provider, string iban, bool isPrimary, string label) =>
        new()
        {
            ParkId = park.Id,
            Park = park,
            BankCode = bankCode,
            Provider = provider,
            Iban = iban,
            HolderName = park.LegalEntityName ?? park.Name,
            CredentialsEncrypted = "{\"provider\":\"" + provider + "\"}",
            IsActive = true,
            IsPrimary = isPrimary,
            Label = label,
        };

    /// <summary>Build a driver payout destination from a bank code + 16-digit account part.</summary>
    public static BankCard NewDestination(Guid driverId, string? holderName, string bankCode, string account16, bool isDefault)
    {
        var iban = GeorgianIban.Build(bankCode, account16);
        return new BankCard
        {
            DriverId = driverId,
            Iban = iban,
            IbanHash = FieldEncryptor.Hash(iban),
            BankCode = bankCode,
            BankType = GeorgianIban.BankLabel(bankCode),
            MaskedPan = GeorgianIban.Mask(iban),
            HolderName = holderName,
            TokenReferenceEncrypted = "",
            IsDefault = isDefault,
            IsActive = true,
        };
    }

    public static string RandomDigits(Random rng, int count)
    {
        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++) sb.Append(rng.Next(0, 10));
        return sb.ToString();
    }

    /// <summary>
    /// Convert a park name to a URL-safe slug. Output matches the Park.Slug check constraint
    /// ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ (lowercase, single-hyphen, no leading/trailing dashes).
    /// </summary>
    public static string ToSlug(string name)
    {
        var normalised = name.Normalize(NormalizationForm.FormKD);
        var lowered = normalised.ToLowerInvariant();
        var hyphenated = Regex.Replace(lowered, @"[^a-z0-9]+", "-");
        return hyphenated.Trim('-');
    }

    private static string HashPhone(string phone) =>
        Core.Identity.GeorgianPhone.Hash(Core.Identity.GeorgianPhone.Normalize(phone) ?? phone);

    /// <summary>
    /// Production path: migrations + encryption backfill only — no demo parks, no dev logins.
    /// The very first super-admin is created from configuration
    /// (<c>Bootstrap:SuperAdminEmail</c> / <c>Bootstrap:SuperAdminPassword</c>) when the
    /// AdminUsers table is empty; after that the settings can be removed.
    /// </summary>
    public static async Task EnsureProductionReadyAsync(IServiceProvider services, string? bootstrapEmail, string? bootstrapPassword)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await db.Database.MigrateAsync();
        await EncryptionMigrator.EnsureEncryptedAsync(db, log);

        if (await db.AdminUsers.AnyAsync()) return;

        if (string.IsNullOrWhiteSpace(bootstrapEmail) || string.IsNullOrWhiteSpace(bootstrapPassword) || bootstrapPassword.Length < 12)
        {
            log.LogWarning("No admin users exist and Bootstrap:SuperAdminEmail/Password (>= 12 chars) are not set — nobody can log in to the admin console");
            return;
        }

        db.AdminUsers.Add(new AdminUser
        {
            Email = bootstrapEmail.Trim().ToLowerInvariant(),
            Name = "Swich Admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(bootstrapPassword),
            Role = "super_admin",
            ParkId = null,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        log.LogWarning("Bootstrap super-admin {Email} created — remove Bootstrap:* settings now", bootstrapEmail);
    }
}
