using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;

namespace PayTaxi.Infrastructure.Data;

/// <summary>
/// Seeds 3 demo parks and their drivers if the database is empty.
/// Driver YandexDriverProfileIds match the in-memory mock data so the mock
/// Yandex client can resolve balances correctly.
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
            await EnsureBankCardsSeededAsync(db, log);
            await EnsureAdminUsersSeededAsync(db, log);
            return;
        }

        log.LogInformation("Seeding 3 demo parks with drivers");

        // Mix of operating models — Tbilisi #3 = Model A.5 (commercial agent, the primary target),
        // Tbilisi #5 = Model A (pure SaaS), Batumi #1 = Model A.5 with smaller authorization limit.
        var parks = new[]
        {
            BuildPark(
                name: "Tbilisi Auto Park #3",
                yandexParkId: "yx_park_tb3",
                bankProvider: "bog",
                legalEntityName: "Tbilisi Taxi Service LLC",
                taxId: "405123456",
                phone: "+995 322 12 34 56",
                iban: "GE29BG0000000123456789",
                operatingModel: OperatingModel.ModelA5,
                authorizationLimit: 125_000m,
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
                bankProvider: "tbc",
                legalEntityName: "Park-5 Operations Ltd.",
                taxId: "404987654",
                phone: "+995 322 55 88 99",
                iban: "GE65TB0000000987654321",
                operatingModel: OperatingModel.ModelA, // pure SaaS — uses own bank API
                authorizationLimit: null,
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
                bankProvider: "bog",
                legalEntityName: "Batumi Taxi Co.",
                taxId: "402555111",
                phone: "+995 422 77 22 11",
                iban: "GE12BG0000000555111222",
                operatingModel: OperatingModel.ModelA5,
                authorizationLimit: 45_000m, // smaller park, smaller authorization
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
    /// Back-fills 2 mock bank cards per driver (BOG default + TBC) if none exist.
    /// Safe to re-run: only inserts when a driver has zero cards.
    /// Mock tokens are deterministic from driver Id so retries don't duplicate.
    /// </summary>
    private static async Task EnsureBankCardsSeededAsync(AppDbContext db, ILogger log)
    {
        var driversNeedingCards = await db.Drivers
            .Where(d => !d.BankCards.Any())
            .Select(d => new { d.Id, d.Name })
            .ToListAsync();

        if (driversNeedingCards.Count == 0)
        {
            log.LogInformation("Bank cards: all drivers already have at least one card");
            return;
        }

        var rng = new Random(42); // deterministic across reseeds
        var cards = new List<BankCard>(driversNeedingCards.Count * 2);
        foreach (var d in driversNeedingCards)
        {
            var bogLast4 = rng.Next(1000, 10000).ToString();
            var tbcLast4 = rng.Next(1000, 10000).ToString();
            cards.Add(new BankCard
            {
                DriverId = d.Id,
                MaskedPan = $"**** {bogLast4}",
                TokenReferenceEncrypted = $"mock_tok_bog_{d.Id:N}",
                BankType = "BOG",
                IsDefault = true,
                IsActive = true,
            });
            cards.Add(new BankCard
            {
                DriverId = d.Id,
                MaskedPan = $"**** {tbcLast4}",
                TokenReferenceEncrypted = $"mock_tok_tbc_{d.Id:N}",
                BankType = "TBC",
                IsDefault = false,
                IsActive = true,
            });
        }

        db.BankCards.AddRange(cards);
        await db.SaveChangesAsync();
        log.LogInformation("Seeded {Count} bank cards for {DriverCount} drivers",
            cards.Count, driversNeedingCards.Count);
    }

    private static Park BuildPark(
        string name,
        string yandexParkId,
        string bankProvider,
        string legalEntityName,
        string taxId,
        string phone,
        string iban,
        OperatingModel operatingModel,
        decimal? authorizationLimit,
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
            OperatingModel = operatingModel,
            AuthorizationLimit = authorizationLimit,
            BankProvider = bankProvider,
            BankType = bankProvider.ToUpperInvariant(), // legacy mirror, removed in follow-up migration
            BankCredentialsEncrypted = "{\"provider\":\"mock\"}",
            BankAccountIban = iban,
            Status = ParkStatus.Active,
#pragma warning disable CS0618 // legacy bool, see Park.IsActive
            IsActive = true,
#pragma warning restore CS0618
        };

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

    /// <summary>
    /// Convert a park name to a URL-safe slug. Output matches the Park.Slug check constraint
    /// ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ (lowercase, single-hyphen, no leading/trailing dashes).
    /// </summary>
    public static string ToSlug(string name)
    {
        // Strip combining marks / accents (not strictly necessary for the seed but
        // robust for future park names with diacritics)
        var normalised = name.Normalize(NormalizationForm.FormKD);
        var lowered = normalised.ToLowerInvariant();
        var hyphenated = Regex.Replace(lowered, @"[^a-z0-9]+", "-");
        return hyphenated.Trim('-');
    }

    private static string HashPhone(string phone)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(phone));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
