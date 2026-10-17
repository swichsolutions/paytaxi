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
///   "environment": "production" | "test",     // omitted = production (a test host only where allowed)
///   "certificatePfxBase64": "MIIK...",          // or (only inside BankPayout:Tbc:CertificateDirectory)
///   "certificatePath": "/run/secrets/tbc/park-tbc.pfx",
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
    /// <summary>
    /// "production" when omitted. It used to default to "test": a park's real credentials saved without the key went to
    /// the sandbox host, and a sandbox user could be made the default by saying nothing. A test environment is used only
    /// where the server allows one (<see cref="TbcDbiOptions.AllowTestEnvironment"/>; refused on write as well).
    /// </summary>
    [JsonPropertyName("environment")] public string Environment { get; set; } = "production";
    [JsonPropertyName("certificatePfxBase64")] public string? CertificatePfxBase64 { get; set; }
    [JsonPropertyName("certificatePath")] public string? CertificatePath { get; set; }
    [JsonPropertyName("certificatePassword")] public string? CertificatePassword { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("debitCurrency")] public string DebitCurrency { get; set; } = "GEL";

    /// <summary>Set by the adapter from the account context; used for audit logging only (not part of the stored JSON).</summary>
    [JsonIgnore] public Guid? ParkId { get; set; }

    public bool IsProduction => IsProductionEnvironment(Environment);

    /// <summary>"production" or "prod" (any case). Everything else — "test", "", a typo — is not production.</summary>
    public static bool IsProductionEnvironment(string? environment) =>
        string.Equals(environment?.Trim(), "production", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environment?.Trim(), "prod", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when credentials JSON names an environment other than production (any spelling of the key, and a repeated key
    /// counts — the adapter reads it case-insensitively, the last one winning). No "environment" at all is production. Not
    /// an object, or not JSON: false — that is refused (or fails) on its own.
    /// </summary>
    public static bool DeclaresTestEnvironment(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "environment", StringComparison.OrdinalIgnoreCase)
                    && !IsProductionEnvironment(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null))
                    return true;
            return false;
        }
        catch (JsonException) { return false; }
    }

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

    /// <summary>A .pfx is a few KB; anything bigger is not a client certificate (and must not be read into memory).</summary>
    public const int MaxCertificateBytes = 64 * 1024;

    /// <summary>
    /// Why <paramref name="path"/> may not be opened as a client certificate, or null. The credentials JSON is written by
    /// park staff, so a free path made the API open any file of the host: the loader's "could not find" / "cannot read"
    /// answer, stored as the payout's failure reason, told the manager which files exist, and a device such as /dev/zero
    /// would be read without end on every balance check. A path is honoured only inside the directory the operator set
    /// (<c>BankPayout:Tbc:CertificateDirectory</c>) — none set, none honoured — and only as a regular file of sane size.
    /// </summary>
    public static string? CertificatePathProblem(string? path, string? allowedDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (string.IsNullOrWhiteSpace(allowedDirectory)) return "certificate_path_not_allowed";
        string full, root;
        try
        {
            full = Path.GetFullPath(path);
            root = Path.GetFullPath(allowedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "certificate_path_not_allowed"; }
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, cmp)) return "certificate_path_not_allowed";
        return null;
    }

    /// <summary>
    /// Load the client certificate (Standard+ package). Null when none is configured. A path outside
    /// <paramref name="certificateDirectory"/> is refused before anything is opened; a load failure says only that it
    /// failed (TBC_CERT_UNLOADABLE), never the loader's text about the file.
    /// </summary>
    public X509Certificate2? LoadCertificate(string? certificateDirectory)
    {
        if (!HasCertificate) return null;
        var flags = X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(CertificatePfxBase64))
        {
            if (CertificatePfxBase64.Length > MaxCertificateBytes * 4 / 3 + 4)
                throw new TbcConfigurationException("TBC_CERT_UNLOADABLE: the client certificate is larger than a certificate can be");
            try { bytes = Convert.FromBase64String(CertificatePfxBase64); }
            catch (FormatException) { throw new TbcConfigurationException("TBC_CERT_UNLOADABLE: certificatePfxBase64 is not base64"); }
        }
        else
        {
            if (CertificatePathProblem(CertificatePath, certificateDirectory) is not null)
                throw new TbcConfigurationException("TBC_CERT_PATH_NOT_ALLOWED: certificatePath must point into the configured certificate directory");
            try
            {
                var info = new FileInfo(Path.GetFullPath(CertificatePath!));
                // A regular file of certificate size: no link out of the directory, no device, nothing huge.
                if (!info.Exists || info.LinkTarget is not null || info.Length > MaxCertificateBytes
                    || (info.Attributes & (FileAttributes.Device | FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new TbcConfigurationException("TBC_CERT_UNLOADABLE: the client certificate could not be loaded");
                bytes = File.ReadAllBytes(info.FullName);
            }
            catch (Exception ex) when (ex is not TbcConfigurationException)
            {
                throw new TbcConfigurationException("TBC_CERT_UNLOADABLE: the client certificate could not be loaded");
            }
        }
        try
        {
            return new X509Certificate2(bytes, CertificatePassword, flags);
        }
        catch (Exception ex) when (ex is not TbcConfigurationException)
        {
            throw new TbcConfigurationException("TBC_CERT_UNLOADABLE: the client certificate could not be loaded (check it and its password)");
        }
    }

    /// <summary>Stable fingerprint of the credential set — used to key the per-account HttpClient cache.</summary>
    public string CacheKey(string? certificateDirectory)
    {
        // The certificate bytes are part of the key so a rotated .pfx (same path or same
        // length) gets a fresh HttpClient instead of the old client certificate.
        string certFingerprint;
        try { certFingerprint = LoadCertificate(certificateDirectory)?.Thumbprint ?? "none"; }
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
