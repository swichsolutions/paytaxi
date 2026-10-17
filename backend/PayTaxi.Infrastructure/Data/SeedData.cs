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
/// Phone numbers and IBANs go through the same converters and keyed lookup hashes (LookupHash) as
/// real data: encrypted when Encryption:Key is set, hashed with Encryption:HashKey when that is.
/// </summary>
public static class SeedData
{
    public static async Task EnsureSeededAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await db.Database.MigrateAsync();
        // Before anything below reads or writes an encrypted column: the configured key must be the database's.
        await EncryptionKeyCheck.EnsureAsync(db, log);

        if (await db.Parks.AnyAsync())
        {
            log.LogInformation("Database already seeded — skipping park seed");
            await EnsureParkLegalDetailsBackfilledAsync(db, log);
            await EnsureParkBankAccountsSeededAsync(db, log);
            await EnsureBankCardsSeededAsync(db, log);
            await EnsureAdminUsersSeededAsync(db, log);
            await EnsureOperatorSeededAsync(db, log);
            await EnsureSettlementConfigSeededAsync(db, log);
            await EnsureLicenseeSeededAsync(db, log);
            await EnsureOperatorsBoundAsync(db, log);
            await ReportUnboundOperatorsAsync(db, log);
            await EncryptionMigrator.EnsureEncryptedAsync(db, log);
            return;
        }

        log.LogInformation("Seeding 3 demo parks with drivers");

        var parks = new[]
        {
            BuildPark(
                name: "Tbilisi Auto Park #3",
                yandexParkId: "yx_park_tb3",
                legalEntityName: "შპს თბილისი თაქსი სერვისი",
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
                legalEntityName: "შპს პარკი-5 ოპერეიშენს",
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
                legalEntityName: "შპს ბათუმი თაქსი",
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
        await EnsureLicenseeSeededAsync(db, log);
        await EnsureOperatorsBoundAsync(db, log);
        await ReportUnboundOperatorsAsync(db, log);
        await EncryptionMigrator.EnsureEncryptedAsync(db, log);
    }

    /// <summary>
    /// Development: an operator account works for one licensee and sees no licence-agreement figures without one
    /// (AdminUser.LicenseeId). Operators not bound yet — the seeded one, and those of a database seeded before the binding
    /// existed — are bound to the licensee in force, when there is exactly one. An account already bound is never moved.
    /// </summary>
    private static async Task EnsureOperatorsBoundAsync(AppDbContext db, ILogger log)
    {
        var inForce = await db.Licensees.Where(l => l.IsActive).Select(l => l.Id).Take(2).ToListAsync();
        if (inForce.Count != 1) return;
        var bound = await db.AdminUsers.Where(a => a.Role == "operator" && a.LicenseeId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LicenseeId, (Guid?)inForce[0]));
        if (bound > 0) log.LogInformation("Bound {Count} operator account(s) to the licensee in force", bound);
    }

    /// <summary>
    /// Every start, development and production: the active operator accounts bound to no licensee (AdminUser.LicenseeId),
    /// each logged as an error. Such an account fails closed — it cannot sign in at all (403 licensee_not_in_force), as an
    /// operator of a deactivated licensee cannot — which is safe but silent, and operator accounts are created by hand
    /// (docs/DEPLOY.md, go-live step 7), so a forgotten binding would otherwise surface only as an operator who cannot get in. Not a database CHECK: adding one in the same migration
    /// that adds the column would fail on every existing database holding an operator (the dev seed's among them) before
    /// anything could bind it. Returns the e-mails.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReportUnboundOperatorsAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        var unbound = await db.AdminUsers.AsNoTracking()
            .Where(a => a.Role == "operator" && a.IsActive && a.LicenseeId == null)
            .OrderBy(a => a.Email)
            .Select(a => a.Email)
            .ToListAsync(ct);
        foreach (var email in unbound)
            log.LogError("Operator account {Email} is bound to no licensee (AdminUsers.LicenseeId is empty): it cannot sign in until it is set (docs/DEPLOY.md)", email);
        return unbound;
    }

    /// <summary>
    /// The licensee of the licence agreement, for development: the name stays a placeholder until the
    /// contract names the company, the launch date is the first day of the current month, every demo park is
    /// attached (all of them are "connected by the licensee"), and two shared-cost invoices per side, dated in
    /// the current month, give the running month's statement something to show. Once only — an existing
    /// licensee is never touched.
    /// The launch is deliberately not back-dated into an earlier month: on an existing dev database that month's
    /// fees were already settled by the old nightly rows, so the first run day would freeze a meaningless first
    /// statement for it — no income, the sample invoices used up — before anyone saw them.
    /// The licensee-paid invoices are deliberately small: in the barrier phase they come straight off
    /// Swich's due amount, and a dev month has only a handful of 0.50 fees.
    /// </summary>
    private static async Task EnsureLicenseeSeededAsync(AppDbContext db, ILogger log)
    {
        if (await db.Licensees.AnyAsync()) return;

        var today = Services.SettlementService.TodayLocal();
        var launch = Services.SettlementService.FirstOfMonth(today);
        var licensee = new Licensee
        {
            Name = "ლიცენზიატი (დასახელება დასაზუსტებელია)",
            LaunchDate = launch,
            BarrierGel = 20_000m,
            SwichSharePercent = 50m,
            IsActive = true,
        };
        db.Licensees.Add(licensee);

        var parks = await db.Parks.Where(p => p.LicenseeId == null).ToListAsync();
        foreach (var p in parks) p.LicenseeId = licensee.Id;

        DateOnly Day(int offset) => launch.AddDays(offset) <= today ? launch.AddDays(offset) : today;
        // Entered three days before the seed ran — past the 48-hour window (Settlement:CostCutoffHours) — so the running
        // month's preview shows them counted from the first start, not "waiting" for two days.
        var entered = DateTime.UtcNow.AddDays(-3);
        var costs = new[]
        {
            new CommonCost
            {
                LicenseeId = licensee.Id, PaidBy = CostPaidBy.Licensee, Category = CostCategory.Sms,
                Vendor = "SMSOffice", InvoiceNumber = "SO-24871", InvoiceDate = Day(2), Amount = 2.40m,
                Note = "80 SMS (ერთჯერადი კოდები)", CreatedBy = "seed",
            },
            new CommonCost
            {
                LicenseeId = licensee.Id, PaidBy = CostPaidBy.Licensee, Category = CostCategory.SupportComms,
                Vendor = "Magti", InvoiceNumber = "MG-118204", InvoiceDate = Day(5), Amount = 1.50m,
                Note = "მძღოლების მხარდაჭერის ხაზი", CreatedBy = "seed",
            },
            new CommonCost
            {
                LicenseeId = licensee.Id, PaidBy = CostPaidBy.Swich, Category = CostCategory.Hosting,
                Vendor = "Hetzner Online", InvoiceNumber = "R0024188523", InvoiceDate = Day(1), Amount = 48.00m,
                CreatedBy = "seed",
            },
            new CommonCost
            {
                LicenseeId = licensee.Id, PaidBy = CostPaidBy.Swich, Category = CostCategory.Domain,
                Vendor = "Caucasus Online", InvoiceNumber = "CO-2026-5521", InvoiceDate = Day(3), Amount = 30.00m,
                Note = "paytaxi.ge, 1 წელი", CreatedBy = "seed",
            },
        };
        foreach (var c in costs) { c.CreatedAt = entered; c.EnteredAt = entered; }
        db.CommonCosts.AddRange(costs);

        // The agreement's change history starts with them, like any entry made through the console.
        db.AgreementEvents.Add(Services.SharedCostRules.Event(licensee.Id, "seed", AgreementEventKinds.Terms,
            newValue: Services.SharedCostRules.TermsSnapshot(licensee), at: entered));
        db.AgreementEvents.Add(Services.SharedCostRules.Event(licensee.Id, "seed", AgreementEventKinds.LaunchDate,
            oldValue: Services.SharedCostRules.LaunchSnapshot(null), newValue: Services.SharedCostRules.LaunchSnapshot(launch),
            reason: "Development seed: launched on the first day of the current month", at: entered));
        foreach (var c in costs)
            db.AgreementEvents.Add(Services.SharedCostRules.Event(licensee.Id, "seed", AgreementEventKinds.CostAdded, c.Id,
                newValue: Services.SharedCostRules.Snapshot(c), at: entered));

        await db.SaveChangesAsync();
        log.LogInformation("Seeded the licensee (launch {Launch}, {Parks} parks attached, 4 sample shared costs)", launch, parks.Count);
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
    /// Back-fill for an already-seeded DB: the demo parks' phone, legal name and identification code — the three
    /// details an Active park must have (the drivers' privacy notice names the park by them, Profile → "Your park").
    /// Idempotent — only fills what is still empty, only for the demo parks.
    /// </summary>
    private static async Task EnsureParkLegalDetailsBackfilledAsync(AppDbContext db, ILogger log)
    {
        var detailsBySlug = new Dictionary<string, (string Legal, string TaxId, string Phone)>
        {
            ["tbilisi-auto-park-3"] = ("შპს თბილისი თაქსი სერვისი", "405123456", "+995 322 12 34 56"),
            ["tbilisi-auto-park-5"] = ("შპს პარკი-5 ოპერეიშენს", "404987654", "+995 322 55 88 99"),
            ["batumi-auto-park-1"]  = ("შპს ბათუმი თაქსი", "402555111", "+995 422 77 22 11"),
        };

        var parks = await db.Parks
            .Where(p => p.Phone == null || p.LegalEntityName == null || p.TaxId == null)
            .ToListAsync();
        var filled = 0;
        foreach (var p in parks)
        {
            if (!detailsBySlug.TryGetValue(p.Slug, out var d)) continue;
            p.Phone ??= d.Phone;
            p.LegalEntityName ??= d.Legal;
            p.TaxId ??= d.TaxId;
            filled++;
        }
        if (filled == 0) return;
        await db.SaveChangesAsync();
        log.LogInformation("Back-filled legal details (name, identification code, phone) for {Count} demo parks", filled);
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
            b.IbanHash = LookupHash.Iban(b.Iban);
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
            IbanHash = LookupHash.Iban(iban),
            BankCode = bankCode,
            BankType = GeorgianIban.BankLabel(bankCode),
            MaskedPan = GeorgianIban.Mask(iban),
            HolderName = holderName,
            AddedBy = "seed",
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
        LookupHash.Phone(Core.Identity.GeorgianPhone.Normalize(phone) ?? phone);

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
        // A wrong but well-formed Encryption:Key must stop the start here, before a single row is written under it.
        await EncryptionKeyCheck.EnsureAsync(db, log);
        await EncryptionMigrator.EnsureEncryptedAsync(db, log);
        await ReportUnboundOperatorsAsync(db, log);

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
