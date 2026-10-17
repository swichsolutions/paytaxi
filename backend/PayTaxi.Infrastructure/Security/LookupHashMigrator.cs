using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Identity;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Security;

/// <summary>
/// Brings every lookup hash in the database to the current hash key (<see cref="LookupHash.Current"/>). Runs at startup
/// after migrations as the first step of <see cref="EncryptionMigrator"/>, and like it is idempotent:
///   - <c>Drivers.PhoneHash</c> and <c>BankCards.IbanHash</c> not made by the current key — plain SHA-256 from before
///     keyed hashing, an earlier key after a rotation, or never back-filled — are recomputed from the decrypted phone /
///     IBAN (so the encryption key must be the right one; with a wrong one nothing could be read anyway);
///   - sign-in codes (<c>OtpCodes</c>) not made by it are deleted. The code itself is never stored, so they cannot be
///     re-hashed, and a lookup under the current key would not find them anyway: at worst a driver in the middle of
///     signing in asks for a new code. The per-phone sign-in events (<c>PhoneSignInEvents</c>) likewise — the phone is
///     not stored either; the day limits start again from nothing for every phone.
/// Every stored hash names the key that made it (<see cref="LookupHasher.Prefix"/>), so each scan returns only stale rows:
/// cheap when there is nothing to do (three prefix scans of small tables), and a run cut short simply continues on the
/// next start. Without a key (dev) "current" is plain SHA-256, so a database once hashed with a key is brought back.
///
/// A phone whose new hash another driver already holds (two spellings of one number, from before the one normaliser) is
/// left as it was and logged as an error: the unique index on PhoneHash would refuse the update, and failing the boot of
/// the whole platform over one row is worse than that one row's driver being unable to sign in until it is fixed. When
/// both rows are stale, the one sign-in found until now keeps the number (<see cref="SignInPreference"/>), so the re-hash
/// never locks out the driver who could sign in in favour of the one who could not.
/// </summary>
public static class LookupHashMigrator
{
    public static async Task<LookupHashMigration> EnsureCurrentAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        var h = LookupHash.Current;
        if (!h.IsKeyed)
            log.LogWarning("Encryption:HashKey is not configured — phone, IBAN and sign-in code hashes are plain SHA-256, which a copy of the database reverses. Fine for local dev only.");

        // ── Drivers.PhoneHash ────────────────────────────────────────
        var drivers = await Stale(db.Drivers, nameof(Driver.PhoneHash), h).ToListAsync(ct);   // PhoneEncrypted decrypted by the converter
        int rehashedDrivers = 0, skipped = 0;
        if (drivers.Count > 0)
        {
            // The hashes the other drivers hold — what the unique index will compare against.
            var staleIds = drivers.Select(d => d.Id).ToList();
            var taken = (await db.Drivers.AsNoTracking().Where(d => !staleIds.Contains(d.Id)).Select(d => d.PhoneHash).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            // Two stale rows with one number: the first to claim the new hash keeps it, so the order decides who can still
            // sign in. Not the database's order (arbitrary): the driver who could sign in so far goes first.
            foreach (var d in drivers.OrderBy(SignInPreference).ThenBy(d => d.CreatedAt).ThenBy(d => d.Id))
            {
                var phone = GeorgianPhone.Normalize(d.PhoneEncrypted) ?? d.PhoneEncrypted;
                if (string.IsNullOrWhiteSpace(phone))
                {
                    skipped++;
                    log.LogError("Lookup hashes: driver {DriverId} has no phone to hash — left as it is", d.Id);
                    continue;
                }
                var next = h.Phone(phone);
                if (!taken.Add(next))
                {
                    skipped++;
                    log.LogError("Lookup hashes: driver {DriverId} has the same phone as another driver — left with its old hash (cannot sign in) until the duplicate is resolved", d.Id);
                    continue;
                }
                d.PhoneHash = next;
                rehashedDrivers++;
            }
            if (rehashedDrivers > 0) await db.SaveChangesAsync(ct);
        }

        // ── BankCards.IbanHash (driver payout destinations) ─────────────
        var cards = await Stale(db.BankCards, nameof(BankCard.IbanHash), h).ToListAsync(ct);   // Iban decrypted by the converter
        var rehashedCards = 0;
        foreach (var c in cards)
        {
            if (string.IsNullOrEmpty(c.Iban)) continue;   // a legacy token-only row: nothing to hash (the dev seed upgrades those)
            c.IbanHash = h.Iban(c.Iban);
            rehashedCards++;
        }
        if (rehashedCards > 0) await db.SaveChangesAsync(ct);

        // ── OtpCodes: cannot be re-hashed, so they go ─────────────────
        var droppedCodes = await Stale(db.OtpCodes, nameof(OtpCode.PhoneHash), h).ExecuteDeleteAsync(ct);
        var droppedEvents = await Stale(db.PhoneSignInEvents, nameof(PhoneSignInEvent.PhoneHash), h).ExecuteDeleteAsync(ct);
        if (droppedEvents > 0)
            log.LogInformation("Lookup hashes: deleted {Events} per-phone sign-in event(s) made under another key (the day limits restart)", droppedEvents);

        var result = new LookupHashMigration(rehashedDrivers, rehashedCards, droppedCodes, skipped);
        if (rehashedDrivers + rehashedCards + droppedCodes > 0)
            log.LogInformation(
                "Lookup hashes brought to {Key}: re-hashed {Drivers} driver phone(s) and {Cards} payout IBAN(s), deleted {Codes} sign-in code(s) made without it",
                h.KeyId is null ? "plain SHA-256 (no hash key)" : "hash key " + h.KeyId, rehashedDrivers, rehashedCards, droppedCodes);
        return result;
    }

    /// <summary>
    /// Which of two drivers sharing one number keeps it (lower first). Sign-in hashes the canonical spelling of what is typed,
    /// so the driver it found before is the one whose stored hash is the plain SHA-256 of the canonical number (the hash
    /// before keyed hashing); next one whose phone is stored in the canonical spelling (written through the one normaliser,
    /// so its hash was made from it — also under an earlier key, which cannot be recomputed here); then an active driver.
    /// The others keep their old hash and are logged, as before.
    /// </summary>
    private static int SignInPreference(Driver d)
    {
        var canonical = GeorgianPhone.Normalize(d.PhoneEncrypted);
        if (canonical is null) return 3;
        if (string.Equals(d.PhoneHash, LookupHasher.Unkeyed.Phone(canonical), StringComparison.Ordinal)) return 0;
        if (string.Equals(d.PhoneEncrypted, canonical, StringComparison.Ordinal)) return 1;
        return d.Status == Core.Enums.DriverStatus.Active ? 2 : 3;
    }

    /// <summary>Rows whose hash in <paramref name="column"/> the current hasher did not make (<see cref="LookupHasher.IsCurrent"/>, in SQL).</summary>
    private static IQueryable<T> Stale<T>(IQueryable<T> rows, string column, LookupHasher h) where T : class
    {
        var prefix = h.Prefix;
        return h.IsKeyed
            ? rows.Where(r => !EF.Property<string>(r, column).StartsWith(prefix))
            : rows.Where(r => EF.Property<string>(r, column) == "" || EF.Property<string>(r, column).StartsWith(LookupHasher.Scheme));
    }
}

/// <summary>What one <see cref="LookupHashMigrator.EnsureCurrentAsync"/> run changed.</summary>
public sealed record LookupHashMigration(int Drivers, int PayoutDestinations, int SignInCodesDropped, int DriversSkipped);
