using System.Security.Cryptography;
using System.Text;

namespace PayTaxi.Infrastructure.Security;

/// <summary>
/// AES-256-GCM encryption for PII / credential columns (Georgian Personal Data Protection Law;
/// CLAUDE.md "Encrypt PII at rest"). Applied transparently through EF Core value converters
/// (see <c>AppDbContext</c>), so application code keeps reading and writing plain strings.
///
/// Wire format: <c>enc:v1:</c> + base64(nonce[12] | tag[16] | ciphertext). Anything without
/// the prefix is treated as legacy plaintext and returned as-is, which lets an existing
/// database be encrypted gradually (<see cref="EncryptionMigrator"/> rewrites old rows on
/// startup). Empty strings are stored empty so SQL comparisons like <c>col != ''</c> still work.
///
/// Encrypted columns cannot be filtered by equality (random nonce); every lookup on them uses
/// a separate SHA-256 hash column (PhoneHash, IbanHash).
///
/// The key comes from <c>Encryption:Key</c> (base64, 32 bytes). Rotate by introducing v2 and
/// keeping v1 for decryption. Configured once at startup via <see cref="Configure"/>; the
/// static shape is what EF's model-building expression trees need.
/// </summary>
public static class FieldEncryptor
{
    public const string Prefix = "enc:v1:";
    private static byte[]? _key;

    /// <summary>True once a key is loaded. When false, values pass through unencrypted (dev/test only).</summary>
    public static bool Enabled => _key is not null;

    public static void Configure(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) { _key = null; return; }
        var key = Convert.FromBase64String(base64Key.Trim());
        if (key.Length != 32)
            throw new InvalidOperationException($"Encryption:Key must be 32 bytes (base64); got {key.Length}");
        _key = key;
    }

    /// <summary>For tests: drop the key.</summary>
    public static void Reset() => _key = null;

    public static string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";
        if (_key is null) return plaintext;                       // passthrough when unconfigured
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already encrypted

        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);

        var blob = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    public static string Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored ?? "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plaintext
        if (_key is null)
            throw new InvalidOperationException("Encrypted value encountered but Encryption:Key is not configured");

        byte[] blob;
        try { blob = Convert.FromBase64String(stored[Prefix.Length..]); }
        catch (FormatException ex) { throw new CryptographicException("Encrypted field is not valid base64", ex); }
        if (blob.Length < 28) throw new CryptographicException("Encrypted field is too short");

        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);   // throws CryptographicException on tamper / wrong key
        return Encoding.UTF8.GetString(plain);
    }

    public static bool IsEncrypted(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Lower-hex SHA-256 — the equality-lookup companion of an encrypted column.</summary>
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Generate a fresh key for configuration (dev tooling / runbook).</summary>
    public static string NewKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
