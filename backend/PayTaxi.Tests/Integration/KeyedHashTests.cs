using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayTaxi.Core.Entities;
using PayTaxi.Infrastructure.Security;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Phone numbers and IBANs are encrypted at rest, and the columns rows are found by — Drivers.PhoneHash,
/// OtpCodes.PhoneHash, BankCards.IbanHash — and the stored sign-in codes are keyed hashes (HMAC-SHA256, Encryption:HashKey).
/// A database from before keyed hashing holds plain SHA-256 there; the startup migrator re-hashes those rows from the
/// decrypted values once, and drops the sign-in codes made under them (they cannot be re-hashed: the code is not stored).
/// The fixture runs with a hash key, as production must. Rows are planted on Batumi drivers (never Tbilisi #3, which the
/// flaky-bank host uses) and put back.
/// </summary>
[Collection("api")]
public class KeyedHashTests
{
    private readonly ApiFixture _f;
    public KeyedHashTests(ApiFixture f) => _f = f;

    private static string Sha256Hex(string v) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v))).ToLowerInvariant();

    private Task<LookupHashMigration> MigrateAsync() =>
        _f.DbAsync(db => LookupHashMigrator.EnsureCurrentAsync(db, NullLogger.Instance));

    private async Task<(Driver Driver, List<(Guid Id, string Iban, string IbanHash)> Cards)> LoadAsync(string profile) =>
        await _f.DbAsync(async db =>
        {
            var d = await db.Drivers.AsNoTracking().FirstAsync(x => x.YandexDriverProfileId == profile);
            var cards = (await db.BankCards.AsNoTracking().Where(b => b.DriverId == d.Id).OrderBy(b => b.Id).ToListAsync())
                .Select(b => (b.Id, b.Iban, b.IbanHash)).ToList();
            return (d, cards);
        });

    [Fact]
    public async Task The_host_hashes_with_the_key_and_a_plain_sha256_finds_nobody()
    {
        Assert.True(LookupHash.Enabled, "the test host must run with Encryption:HashKey, as production does");
        var (driver, cards) = await LoadAsync("yp_bt1_006");
        Assert.Equal(LookupHash.Phone(driver.PhoneEncrypted), driver.PhoneHash);
        Assert.NotEqual(Sha256Hex(driver.PhoneEncrypted), driver.PhoneHash);
        var plain = Sha256Hex(driver.PhoneEncrypted);
        Assert.False(await _f.DbAsync(db => db.Drivers.AnyAsync(d => d.PhoneHash == plain)));
        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.Equal(LookupHash.Iban(c.Iban), c.IbanHash));
        Assert.All(cards, c => Assert.NotEqual(Sha256Hex(c.Iban), c.IbanHash));

        // A new sign-in code is stored keyed — not the SHA-256 of six digits, which a million guesses reverse.
        var since = DateTime.UtcNow.AddSeconds(-5);
        var otp = await (await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone = driver.PhoneEncrypted }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var code = otp.GetProperty("devCode").GetString()!;
        var row = await _f.DbAsync(db => db.OtpCodes.AsNoTracking()
            .Where(o => o.PhoneHash == driver.PhoneHash && o.CreatedAt >= since).OrderByDescending(o => o.CreatedAt).FirstAsync());
        Assert.NotEqual(Sha256Hex(code), row.CodeHash);
        Assert.StartsWith(LookupHash.Current.Prefix, row.CodeHash);
        Assert.True(LookupHash.Current.SignInCodeMatches(row.Id, code, row.CodeHash));
        await _f.DbAsync(db => db.OtpCodes.Where(o => o.Id == row.Id).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task Plain_sha256_rows_are_rehashed_once_and_the_driver_signs_in_again()
    {
        var (driver, cards) = await LoadAsync("yp_bt1_006");
        var phone = driver.PhoneEncrypted;   // decrypted by the converter
        var legacyPhoneHash = Sha256Hex(phone);
        var since = DateTime.UtcNow.AddSeconds(-5);

        // What an old database holds: plain SHA-256 phone and IBAN hashes (one IBAN hash not yet back-filled at all),
        // and a pending sign-in code made under them.
        await _f.DbAsync(async db =>
        {
            await db.Drivers.Where(d => d.Id == driver.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.PhoneHash, legacyPhoneHash));
            foreach (var (c, i) in cards.Select((c, i) => (c, i)))
            {
                var legacy = i == 0 ? "" : Sha256Hex(c.Iban);
                await db.BankCards.Where(b => b.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.IbanHash, legacy));
            }
            db.OtpCodes.Add(new OtpCode { PhoneHash = legacyPhoneHash, CodeHash = Sha256Hex("123456"), ExpiresAt = DateTime.UtcNow.AddMinutes(5) });
            return await db.SaveChangesAsync();
        });

        try
        {
            // Before the migrator: the keyed lookup misses the plain hash — the phone is unknown, no code is issued.
            var miss = await (await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone }))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(miss.GetProperty("devCode").ValueKind == JsonValueKind.Null, "a plain SHA-256 hash matched a keyed lookup");

            var first = await MigrateAsync();
            Assert.True(first.Drivers >= 1 && first.PayoutDestinations >= cards.Count && first.SignInCodesDropped >= 1, first.ToString());
            Assert.Equal(0, first.DriversSkipped);

            var (after, afterCards) = await LoadAsync("yp_bt1_006");
            Assert.Equal(LookupHash.Phone(phone), after.PhoneHash);
            Assert.All(afterCards, c => Assert.Equal(LookupHash.Iban(c.Iban), c.IbanHash));
            Assert.False(await _f.DbAsync(db => db.Drivers.AnyAsync(d => d.PhoneHash == legacyPhoneHash)));
            Assert.False(await _f.DbAsync(db => db.OtpCodes.AnyAsync(o => o.PhoneHash == legacyPhoneHash)));

            // Found by phone again: a full sign-in.
            var otp = await (await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone }))
                .Content.ReadFromJsonAsync<JsonElement>();
            var code = otp.GetProperty("devCode").GetString();
            Assert.False(string.IsNullOrEmpty(code));
            var verify = await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code });
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            // ...and the code it issued is current, so a second run leaves it alone.

            // Idempotent: the second run finds nothing to do and changes nothing.
            var second = await MigrateAsync();
            Assert.Equal(new LookupHashMigration(0, 0, 0, 0), second);
            var (again, againCards) = await LoadAsync("yp_bt1_006");
            Assert.Equal(after.PhoneHash, again.PhoneHash);
            Assert.Equal(afterCards.Select(c => c.IbanHash), againCards.Select(c => c.IbanHash));
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                await db.Drivers.Where(d => d.Id == driver.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.PhoneHash, LookupHash.Phone(phone)));
                foreach (var c in cards)
                {
                    var current = LookupHash.Iban(c.Iban);
                    await db.BankCards.Where(b => b.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.IbanHash, current));
                }
                var hashes = new[] { legacyPhoneHash, LookupHash.Phone(phone) };
                await db.OtpCodes.Where(o => hashes.Contains(o.PhoneHash) && o.CreatedAt >= since).ExecuteDeleteAsync();
                await db.DriverSignIns.Where(x => x.DriverId == driver.Id && x.SignedInAt >= since).ExecuteDeleteAsync();
                return await db.DriverSessions.Where(s => s.DriverId == driver.Id && s.CreatedAt >= since).ExecuteDeleteAsync();
            });
        }
    }

    [Fact]
    public async Task A_rehash_that_would_collide_with_another_driver_is_skipped_not_fatal()
    {
        // Two rows whose phones canonicalise to the same number would get the same keyed hash, which the unique index on
        // PhoneHash refuses. The migrator runs at startup: it must leave that row (logged) instead of failing the boot.
        var (owner, _) = await LoadAsync("yp_bt1_007");
        var tag = Guid.NewGuid().ToString("N")[..10];
        var dup = new Driver
        {
            ParkId = owner.ParkId, Name = "Rehash collision " + tag, YandexDriverProfileId = "yp_dup_" + tag,
            PhoneEncrypted = owner.PhoneEncrypted, PhoneHash = Sha256Hex(owner.PhoneEncrypted),
            Status = Core.Enums.DriverStatus.Active, ConsentGiven = true, ConsentTimestamp = DateTime.UtcNow,
        };
        await _f.DbAsync(async db => { db.Drivers.Add(dup); return await db.SaveChangesAsync(); });
        try
        {
            var result = await MigrateAsync();
            Assert.Equal(1, result.DriversSkipped);
            Assert.Equal(0, result.Drivers);

            var (ownerAfter, _) = await LoadAsync("yp_bt1_007");
            Assert.Equal(owner.PhoneHash, ownerAfter.PhoneHash);
            Assert.Equal(Sha256Hex(owner.PhoneEncrypted), await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.Id == dup.Id)).PhoneHash));
        }
        finally
        {
            await _f.DbAsync(db => db.Drivers.Where(d => d.Id == dup.Id).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Of_two_spellings_of_one_number_the_driver_who_could_sign_in_keeps_it_whichever_row_comes_first()
    {
        // Before the one normaliser a number could be stored twice, in two spellings. Sign-in hashed the canonical spelling,
        // so it found the driver whose hash was made from it — the other could not sign in. Only one of them can have the
        // keyed hash (unique index); it must be the one who could sign in, whatever order the database returns them in.
        var (owner, _) = await LoadAsync("yp_bt1_007");
        var digits = "500" + Random.Shared.Next(0, 1_000_000).ToString("000000");   // a number no seeded driver has
        var canonical = "+995" + digits;
        var otherSpelling = $"0{digits[..3]} {digits[3..5]} {digits[5..7]} {digits[7..]}";
        Assert.Equal(canonical, Core.Identity.GeorgianPhone.Normalize(otherSpelling));

        foreach (var canonicalFirst in new[] { false, true })
        {
            var tag = Guid.NewGuid().ToString("N")[..10];
            Driver Make(string spelling, string suffix) => new()
            {
                ParkId = owner.ParkId, Name = "Two spellings " + tag + suffix, YandexDriverProfileId = $"yp_dup_{tag}{suffix}",
                PhoneEncrypted = spelling, PhoneHash = Sha256Hex(spelling),
                Status = Core.Enums.DriverStatus.Active, ConsentGiven = true, ConsentTimestamp = DateTime.UtcNow,
            };
            var signsIn = Make(canonical, "a");     // its plain hash is what the old lookup computed from the number
            var cannot = Make(otherSpelling, "b");  // a hash of a spelling no lookup ever produced
            await _f.DbAsync(async db =>
            {
                db.Drivers.AddRange(canonicalFirst ? new[] { signsIn, cannot } : new[] { cannot, signsIn });
                return await db.SaveChangesAsync();
            });
            try
            {
                var result = await MigrateAsync();
                Assert.Equal((1, 1), (result.Drivers, result.DriversSkipped));
                var hashes = await _f.DbAsync(db => db.Drivers.AsNoTracking().Where(d => d.Id == signsIn.Id || d.Id == cannot.Id)
                    .ToDictionaryAsync(d => d.Id, d => d.PhoneHash));
                Assert.Equal(LookupHash.Phone(canonical), hashes[signsIn.Id]);
                Assert.Equal(Sha256Hex(otherSpelling), hashes[cannot.Id]);
            }
            finally
            {
                await _f.DbAsync(db => db.Drivers.Where(d => d.Id == signsIn.Id || d.Id == cannot.Id).ExecuteDeleteAsync());
            }
        }
    }
}
