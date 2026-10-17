using System.Security.Cryptography;
using System.Text;

namespace PayTaxi.Infrastructure.Security;

/// <summary>
/// Keyed (HMAC-SHA256) hashes for the columns rows are FOUND by while the value itself is encrypted —
/// <c>Drivers.PhoneHash</c>, <c>OtpCodes.PhoneHash</c>, <c>BankCards.IbanHash</c> — and for the stored form of one-time
/// sign-in codes (<c>OtpCodes.CodeHash</c>).
///
/// Why keyed: these hashes are deterministic (that is what makes them searchable), so a plain SHA-256 is reversed by
/// hashing every candidate. Georgian mobile numbers are about a billion candidates — seconds on a laptop — so a copy of
/// the database would give every phone back off its hash, encryption notwithstanding; a 6-digit code is a million
/// candidates. Under HMAC the same search needs the hash key, which lives with the encryption key (configuration /
/// Key Vault: <c>Encryption:HashKey</c>), never in the database. It is NOT irreversible for someone holding the key —
/// they can test guesses exactly as we do — so the key is guarded like the encryption key.
///
/// Stored form: <c>h1:</c> + key id (8 hex) + <c>:</c> + 64 lower-hex HMAC. The key id is derived from the key, so a
/// rotated key can never be mistaken for the old one: <see cref="LookupHashMigrator"/> re-hashes at startup every row
/// whose prefix is not the current key's. Each kind of value is hashed under its own label (phone / IBAN / sign-in code),
/// and a sign-in code also under its row id, so equal codes of two sign-ins do not show as equal hashes.
///
/// Without a key (Development / tests only — Production refuses to start) it is the plain SHA-256 the database held
/// before keyed hashing, so a dev database keeps working.
/// </summary>
public sealed class LookupHasher
{
    /// <summary>Scheme marker of every keyed hash: HMAC-SHA256, layout v1.</summary>
    public const string Scheme = "h1:";

    public static LookupHasher Unkeyed { get; } = new(null);

    private readonly byte[]? _key;

    public LookupHasher(byte[]? key)
    {
        if (key is not null && key.Length != 32)
            throw new InvalidOperationException($"Encryption:HashKey must be 32 bytes (base64); got {key.Length}");
        _key = key?.ToArray();
        if (_key is null) { Prefix = ""; return; }
        KeyId = Hex(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("paytaxi/lookup-hash/key-id")))[..8];
        Prefix = Scheme + KeyId + ":";
    }

    /// <summary>A hasher from a base64 32-byte key; blank → <see cref="Unkeyed"/>.</summary>
    public static LookupHasher FromBase64(string? base64Key) =>
        string.IsNullOrWhiteSpace(base64Key) ? Unkeyed : new LookupHasher(Convert.FromBase64String(base64Key.Trim()));

    public bool IsKeyed => _key is not null;

    /// <summary>8 hex characters identifying the key (not secret); null when unkeyed.</summary>
    public string? KeyId { get; }

    /// <summary>What every hash this key makes starts with ("h1:&lt;keyId&gt;:"); "" when unkeyed.</summary>
    public string Prefix { get; }

    /// <summary>The lookup hash of a CANONICAL phone (<see cref="Core.Identity.GeorgianPhone.Normalize"/>).</summary>
    public string Phone(string canonical) => Compute("phone", canonical);

    /// <summary>The lookup hash of a normalised IBAN (duplicate detection per driver).</summary>
    public string Iban(string iban) => Compute("iban", iban);

    /// <summary>The stored form of a one-time sign-in code, bound to its <c>OtpCodes</c> row.</summary>
    public string SignInCode(Guid codeId, string code) => Compute("sign-in-code:" + codeId.ToString("N"), code);

    /// <summary>A keyed hash of a value that is only ever written to logs (e.g. the identifier typed at a failed admin login):
    /// lines can be correlated, the value cannot be read back.</summary>
    public string LogValue(string label, string value) => Compute("log:" + label, value);

    /// <summary>Whether a typed code is the one stored for this row (constant-time compare).</summary>
    public bool SignInCodeMatches(Guid codeId, string typed, string stored) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(SignInCode(codeId, typed)), Encoding.UTF8.GetBytes(stored ?? ""));

    /// <summary>
    /// True when <paramref name="stored"/> was made by this hasher — for a keyed one: by this key, not by another key and
    /// not unkeyed; for the unkeyed one: plain SHA-256, not keyed. Empty is never current (not back-filled yet).
    /// </summary>
    public bool IsCurrent(string? stored) =>
        !string.IsNullOrEmpty(stored)
        && (IsKeyed ? stored.StartsWith(Prefix, StringComparison.Ordinal) : !stored.StartsWith(Scheme, StringComparison.Ordinal));

    private string Compute(string label, string value)
    {
        if (_key is null) return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));   // legacy form, dev only
        return Prefix + Hex(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(label + "\n" + value)));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>
/// The process-wide <see cref="LookupHasher"/>, configured once at startup from <c>Encryption:HashKey</c> (static for the
/// same reason as <see cref="FieldEncryptor"/>: the seed and the startup migrators are static). Every phone, IBAN and
/// sign-in code hash in the application goes through here.
/// </summary>
public static class LookupHash
{
    public static LookupHasher Current { get; private set; } = LookupHasher.Unkeyed;

    /// <summary>True once a hash key is loaded. When false, hashes are plain SHA-256 (dev/test only).</summary>
    public static bool Enabled => Current.IsKeyed;

    public static void Configure(string? base64Key) => Current = LookupHasher.FromBase64(base64Key);

    public static string Phone(string canonical) => Current.Phone(canonical);
    public static string Iban(string iban) => Current.Iban(iban);
    public static string SignInCode(Guid codeId, string code) => Current.SignInCode(codeId, code);
    public static bool SignInCodeMatches(Guid codeId, string typed, string stored) => Current.SignInCodeMatches(codeId, typed, stored);

    /// <summary>A short, log-safe tag of a hash for correlating log lines: its last 8 hex characters (the prefix only names the key).</summary>
    public static string ForLog(string hash) => hash.Length <= 8 ? hash : hash[^8..];

    /// <summary>A short log-safe tag of a value that must not be logged as typed (<see cref="LookupHasher.LogValue"/>).</summary>
    public static string LogTag(string label, string value) => ForLog(Current.LogValue(label, value));

    /// <summary>Generate a fresh key for configuration (dev tooling / runbook).</summary>
    public static string NewKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
