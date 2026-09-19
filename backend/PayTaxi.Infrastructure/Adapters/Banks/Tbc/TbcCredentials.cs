using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayTaxi.Infrastructure.Adapters.Banks.Tbc;

/// <summary>
/// The park's TBC Integration Service credentials, stored as JSON on the park's payout
/// account (write-only through the admin API). Issued by the park's banker on activation:
/// username + temporary password (must be changed via ChangePassword once), plus the
/// company's digital certificate (.pfx) for the Standard+ / non-standard package.
///
/// <code>
/// {
///   "username": "MBS_LTD_DBI",
///   "password": "********",
///   "environment": "test" | "production",
///   "certificatePfxBase64": "MIIK...",          // or
///   "certificatePath": "C:\\secrets\\park-tbc.pfx",
///   "certificatePassword": "********",
///   "nonce": "111111",                          // optional: Digipass code for the Standard package;
///                                               //           omit for the certificate package
///   "debitCurrency": "GEL"
/// }
/// </code>
/// </summary>
public sealed class TbcCredentials
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("environment")] public string Environment { get; set; } = "test";
    [JsonPropertyName("certificatePfxBase64")] public string? CertificatePfxBase64 { get; set; }
    [JsonPropertyName("certificatePath")] public string? CertificatePath { get; set; }
    [JsonPropertyName("certificatePassword")] public string? CertificatePassword { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("debitCurrency")] public string DebitCurrency { get; set; } = "GEL";

    /// <summary>Set by the adapter from the account context; used for audit logging only (not part of the stored JSON).</summary>
    [JsonIgnore] public Guid? ParkId { get; set; }

    public bool IsProduction => string.Equals(Environment, "production", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Environment, "prod", StringComparison.OrdinalIgnoreCase);

    public bool HasCertificate =>
        !string.IsNullOrWhiteSpace(CertificatePfxBase64) || !string.IsNullOrWhiteSpace(CertificatePath);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TbcCredentials Parse(string? json, Guid? parkId = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            throw new TbcConfigurationException("TBC credentials are not configured on this park account");

        TbcCredentials? c;
        try { c = JsonSerializer.Deserialize<TbcCredentials>(json, JsonOpts); }
        catch (JsonException ex) { throw new TbcConfigurationException($"TBC credentials JSON is invalid: {ex.Message}"); }

        if (c is null || string.IsNullOrWhiteSpace(c.Username) || string.IsNullOrWhiteSpace(c.Password))
            throw new TbcConfigurationException("TBC credentials must include username and password");
        c.ParkId = parkId;
        return c;
    }

    /// <summary>Load the client certificate (Standard+ package). Null when none is configured.</summary>
    public X509Certificate2? LoadCertificate()
    {
        if (!HasCertificate) return null;
        var flags = X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
        try
        {
            if (!string.IsNullOrWhiteSpace(CertificatePfxBase64))
                return new X509Certificate2(Convert.FromBase64String(CertificatePfxBase64), CertificatePassword, flags);
            return new X509Certificate2(CertificatePath!, CertificatePassword, flags);
        }
        catch (Exception ex) when (ex is not TbcConfigurationException)
        {
            throw new TbcConfigurationException($"TBC client certificate could not be loaded: {ex.Message}");
        }
    }

    /// <summary>Stable fingerprint of the credential set — used to key the per-account HttpClient cache.</summary>
    public string CacheKey()
    {
        // The certificate bytes are part of the key so a rotated .pfx (same path or same
        // length) gets a fresh HttpClient instead of the old client certificate.
        string certFingerprint;
        try { certFingerprint = LoadCertificate()?.Thumbprint ?? "none"; }
        catch { certFingerprint = "unloadable"; }
        var raw = $"{Username}|{Environment}|{CertificatePath}|{certFingerprint}|{CertificatePassword?.Length}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }
}

/// <summary>Bad or missing park-side TBC configuration — an ops problem, not a bank problem.</summary>
public sealed class TbcConfigurationException : Exception
{
    public TbcConfigurationException(string message) : base(message) { }
}
