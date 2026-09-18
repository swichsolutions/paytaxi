using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Security;

/// <summary>
/// One-time (idempotent) upgrade of a database that predates field encryption: loads every row
/// of the tables with protected columns and re-saves the ones still holding plaintext. Because
/// the value converter encrypts on write, simply marking the property modified is enough.
/// Also back-fills <c>BankCards.IbanHash</c>. Runs at startup after migrations; cheap when there
/// is nothing to do (a few indexed scans on small tables).
/// </summary>
public static class EncryptionMigrator
{
    public static async Task EnsureEncryptedAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        // IbanHash back-fill works with or without a key.
        var cards = await db.BankCards.Where(b => b.IbanHash == "").ToListAsync(ct);
        foreach (var c in cards)
            if (!string.IsNullOrEmpty(c.Iban)) c.IbanHash = FieldEncryptor.Hash(c.Iban);
        if (cards.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Back-filled IbanHash on {Count} payout destinations", cards.Count);
        }

        if (!FieldEncryptor.Enabled)
        {
            log.LogWarning("Encryption:Key is not configured — PII columns are stored in PLAINTEXT. Fine for local dev only.");
            return;
        }

        var touched = 0;

        // Materialised values are already decrypted by the converter; we only need to know
        // whether the STORED form is plaintext, which requires the raw column. Read raw via
        // a projection to the entity's shadow-free property using EF.Property on the untracked query
        // is not possible for converted properties, so we compare by re-encrypting: if the stored
        // string lacks the prefix the converter returned it verbatim, and re-saving encrypts it.
        touched += await ReencryptAsync(db, db.Drivers, d => d.PhoneEncrypted, (d, v) => d.PhoneEncrypted = v, ct);
        touched += await ReencryptAsync(db, db.Parks, p => p.YandexClientIdEncrypted, (p, v) => p.YandexClientIdEncrypted = v, ct);
        touched += await ReencryptAsync(db, db.Parks, p => p.YandexApiKeyEncrypted, (p, v) => p.YandexApiKeyEncrypted = v, ct);
        touched += await ReencryptAsync(db, db.Parks, p => p.BankCredentialsEncrypted, (p, v) => p.BankCredentialsEncrypted = v, ct);
        touched += await ReencryptAsync(db, db.ParkBankAccounts, a => a.CredentialsEncrypted, (a, v) => a.CredentialsEncrypted = v, ct);
        touched += await ReencryptAsync(db, db.BankCards, b => b.Iban, (b, v) => b.Iban = v, ct);
        touched += await ReencryptAsync(db, db.BankCards, b => b.TokenReferenceEncrypted, (b, v) => b.TokenReferenceEncrypted = v, ct);

        if (touched > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Encrypted {Count} previously plaintext field values", touched);
        }
    }

    /// <summary>
    /// Marks the property modified on rows whose stored value is not yet encrypted. Detection uses a
    /// raw SQL scan of the column for the prefix so we don't depend on the converter's round trip.
    /// </summary>
    private static async Task<int> ReencryptAsync<T>(
        AppDbContext db, DbSet<T> set, Func<T, string> get, Action<T, string> setValue, CancellationToken ct)
        where T : Core.Entities.BaseEntity
    {
        var entityType = db.Model.FindEntityType(typeof(T))!;
        var table = entityType.GetTableName()!;
        // Column name from the property the getter targets: derive via a probe on the model.
        var propName = PropertyName(get);
        var column = entityType.FindProperty(propName)!.GetColumnName();

        var ids = await db.Database
            .SqlQueryRaw<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"{table}\" WHERE \"{column}\" <> '' AND \"{column}\" NOT LIKE 'enc:v1:%'")
            .ToListAsync(ct);
        if (ids.Count == 0) return 0;

        var rows = await set.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        foreach (var r in rows)
        {
            var v = get(r);
            setValue(r, v);
            db.Entry(r).Property(propName).IsModified = true;
        }
        return rows.Count;
    }

    private static string PropertyName<T>(Func<T, string> getter)
    {
        // Getters are simple property reads; recover the name by probing a fresh instance with markers.
        var probe = Activator.CreateInstance<T>();
        foreach (var p in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        {
            var marker = "__probe__" + p.Name;
            var old = p.GetValue(probe);
            p.SetValue(probe, marker);
            var hit = false;
            try { hit = getter(probe) == marker; } catch { }
            p.SetValue(probe, old);
            if (hit) return p.Name;
        }
        throw new InvalidOperationException("Could not resolve property name for encryption migrator");
    }
}
