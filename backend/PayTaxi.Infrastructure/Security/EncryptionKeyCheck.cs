using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Security;

/// <summary>
/// Startup check that <c>Encryption:Key</c> is the key the database was written with. The <c>enc:v1:</c> marker carries
/// no key id, and nothing else at startup decrypts a row — so a wrong but well-formed key (a regenerated vault secret, the
/// wrong slot, a half-done rotation) used to start cleanly: every read of an existing phone, IBAN or credential then
/// failed, while every new or edited one was written under the new key with the same marker. Rolling back made those
/// unreadable instead. Refusing to start is the only state that loses nothing.
///
/// Samples the most recently written encrypted values of every protected column and refuses when none of them decrypts.
/// Some failing while others decrypt means rows under two keys already exist (or a damaged row): that is logged as an
/// error for a human, not made into an outage. Runs right after the migrations, before anything reads or writes data.
/// </summary>
public static class EncryptionKeyCheck
{
    private const int SamplePerColumn = 5;

    /// <param name="candidateKeyBase64">The key to check; null = the configured one (tests pass another key here).</param>
    public static async Task EnsureAsync(AppDbContext db, ILogger log, string? candidateKeyBase64 = null, CancellationToken ct = default)
    {
        var sampled = 0;
        var readable = 0;
        foreach (var (table, column) in ProtectedColumns(db))
        {
            var values = await db.Database
                .SqlQueryRaw<string>($"SELECT \"{column}\" AS \"Value\" FROM \"{table}\" WHERE \"{column}\" LIKE 'enc:v1:%' ORDER BY \"UpdatedAt\" DESC LIMIT {SamplePerColumn}")
                .ToListAsync(ct);
            foreach (var v in values)
            {
                sampled++;
                if (FieldEncryptor.CanDecrypt(v, candidateKeyBase64)) readable++;
            }
        }

        if (sampled == 0) return; // a new or never-encrypted database: nothing to compare against

        if (candidateKeyBase64 is null && !FieldEncryptor.Enabled)
            throw new InvalidOperationException(
                $"The database holds encrypted values ({sampled} sampled) but Encryption:Key is not configured — refusing to start.");

        if (readable == 0)
            throw new InvalidOperationException(
                $"Encryption:Key decrypts none of the {sampled} encrypted values sampled from the database — wrong or rotated key? " +
                "Refusing to start: rows written now would be under a key the existing ones cannot be read with.");

        if (readable < sampled)
            log.LogError("Encryption key check: {Bad} of {Sampled} sampled encrypted values do not decrypt with the configured key — " +
                         "rows under another key (or damaged rows) exist; find them before they are needed", sampled - readable, sampled);
    }

    /// <summary>The columns the EF converter encrypts — the same list <see cref="EncryptionMigrator"/> upgrades.</summary>
    private static IEnumerable<(string table, string column)> ProtectedColumns(AppDbContext db)
    {
        (string, string) Col<T>(string property)
        {
            var et = db.Model.FindEntityType(typeof(T))!;
            return (et.GetTableName()!, et.FindProperty(property)!.GetColumnName());
        }
        yield return Col<Driver>(nameof(Driver.PhoneEncrypted));
        yield return Col<Park>(nameof(Park.YandexClientIdEncrypted));
        yield return Col<Park>(nameof(Park.YandexApiKeyEncrypted));
        yield return Col<Park>(nameof(Park.BankCredentialsEncrypted));
        yield return Col<ParkBankAccount>(nameof(ParkBankAccount.CredentialsEncrypted));
        yield return Col<BankCard>(nameof(BankCard.Iban));
        yield return Col<BankCard>(nameof(BankCard.TokenReferenceEncrypted));
    }
}
