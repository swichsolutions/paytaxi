using System.Net;
using System.Net.Sockets;
using PayTaxi.Infrastructure.Adapters.Sms;
using AspNetIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace PayTaxi.Api.Security;

/// <summary>
/// The settings a host must have before it may serve real data, checked at startup by every environment except
/// Development (the in-process tests, CI and e2e all run as Development). It used to run only when the environment was
/// named exactly "Production" — a host called Staging (docker-compose calls itself a local/staging stack, STATE.md plans
/// a staging environment) started on the public placeholder JWT key from appsettings.json, i.e. anyone who read the
/// repository could sign a super-admin token, and on plain-text PII without an encryption key. Also: no mock rail, no
/// sandbox except on Staging, a named reverse proxy, and an SMS gateway (or an explicit dry-run switch).
///
/// Pure (configuration in, problems out) so the rules are unit-tested; Program.cs throws when the list is not empty.
/// </summary>
public static class StartupGuard
{
    /// <summary>Text that marks a published, non-secret value: placeholders, the dev seed's key, the CI and test-fixture keys.</summary>
    private static readonly string[] PublicKeyMarkers = { "REPLACE_WITH", "dev-only", "ci-only", "integration-tests", "test-only" };

    /// <param name="environmentName">The host's environment; some switches are for a host named "Staging" only.</param>
    public static IReadOnlyList<string> Problems(IConfiguration cfg, string? environmentName = null)
    {
        var problems = new List<string>();

        var conn = cfg.GetConnectionString("DefaultConnection") ?? "";
        if (conn.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase) || conn.Length < 20)
            problems.Add("ConnectionStrings:DefaultConnection");

        var jwt = cfg["Jwt:Key"] ?? "";
        var encKey = cfg["Encryption:Key"]?.Trim() ?? "";
        var hashKey = cfg["Encryption:HashKey"]?.Trim() ?? "";
        if (JwtKeyProblem(jwt) is { } jwtProblem) problems.Add($"Jwt:Key ({jwtProblem})");
        else if (jwt.Trim() == encKey || jwt.Trim() == hashKey) problems.Add("Jwt:Key (must not be one of the encryption keys)");

        if (DataKeyProblem(encKey) is { } encProblem) problems.Add($"Encryption:Key ({encProblem})");
        // Without it phone numbers sit next to plain SHA-256 hashes that give them back in seconds — the encryption would
        // protect nothing, and the privacy policy's "stored encrypted" would not be true.
        if (DataKeyProblem(hashKey) is { } hashProblem) problems.Add($"Encryption:HashKey ({hashProblem})");
        else if (string.Equals(hashKey, encKey, StringComparison.Ordinal)) problems.Add("Encryption:HashKey (must differ from Encryption:Key)");

        var swichIban = cfg["Settlement:SwichIban"] ?? "";
        if (swichIban.Length < 22 || swichIban.StartsWith("GE12TB7100000000000001")) problems.Add("Settlement:SwichIban (real Swich IBAN)");
        // Settling the running month would take the parks' money before the agreement allows (§2.5).
        if (cfg.GetValue<bool>("Settlement:AllowOpenMonthRun")) problems.Add("Settlement:AllowOpenMonthRun (must be false)");

        // A mock rail outside Development completes payouts that move no money (mock bank) or pays real money against
        // invented balances (mock Yandex). docker-compose used to default both on while defaulting to Production. Only a
        // deliberate AllowMockRails=true (a dry run; Program.cs logs it loudly) lets such a host start.
        if (!cfg.GetValue<bool>("AllowMockRails"))
        {
            if (cfg.GetValue("YandexFleet:UseMock", true)) problems.Add("YandexFleet:UseMock (must be false, or set AllowMockRails=true for a dry run)");
            if (cfg.GetValue("BankPayout:UseMock", true)) problems.Add("BankPayout:UseMock (must be false, or set AllowMockRails=true for a dry run)");
        }

        // Sandbox credentials on the live server complete payouts and settlements in the bank's test system: nothing moves
        // while the driver's Yandex balance is debited. A staging host may test a park's credentials against the sandbox
        // (BankPayout:AllowTestEnvironment); no other — there the policy refuses them on write and at the bank call. Keyed on
        // the one name that may, not on "Production": a live host deployed as "Prod" or "Live" used to pass.
        if (cfg.GetValue<bool>("BankPayout:AllowTestEnvironment")
            && !string.Equals(environmentName, "Staging", StringComparison.OrdinalIgnoreCase))
            problems.Add($"BankPayout:AllowTestEnvironment (staging only — allowed on a host named Staging, not {(string.IsNullOrEmpty(environmentName) ? "an unnamed one" : environmentName)})");

        // Trusting X-Forwarded-For from any peer lets every client name its own address — and with it its own rate-limit
        // partition on the sign-in endpoints — wherever the container port is reachable. Trust only the proxy itself.
        if (cfg.GetValue<bool>("Proxy:TrustForwardedHeaders"))
        {
            var (networks, proxies, errors) = ProxyTrust.Read(cfg);
            problems.AddRange(errors);
            if (errors.Count == 0 && networks.Count == 0 && proxies.Count == 0)
                problems.Add("Proxy:KnownNetworks or Proxy:KnownProxies (required with Proxy:TrustForwardedHeaders=true: the address range of the reverse proxy)");
        }

        // Without an SMS gateway no driver can sign in on a new device — and the API hands the sign-in code back in its
        // response instead (the log sender; Development's convenience, read by the tests). Only a deliberate
        // Sms:AllowLogSender=true (a dry run) lets such a host start. A key that is not empty is used as the real one, so a
        // placeholder is refused whatever the switch says.
        var smsProvider = (cfg["Sms:Provider"] ?? SmsOptions.LogProvider).Trim();
        var smsKey = cfg["Sms:SmsOffice:Key"]?.Trim() ?? "";
        var allowLogSender = cfg.GetValue<bool>("Sms:AllowLogSender");
        if (smsProvider.Equals(SmsOptions.SmsOfficeProvider, StringComparison.OrdinalIgnoreCase))
        {
            if (smsKey.Length == 0)
            {
                if (!allowLogSender) problems.Add("Sms:SmsOffice:Key (missing — the API key from the smsoffice.ge profile page; or Sms:AllowLogSender=true for a dry run that sends no SMS)");
            }
            else if (SmsOfficeKeyProblem(smsKey) is { } smsProblem) problems.Add($"Sms:SmsOffice:Key ({smsProblem})");
        }
        else if (smsProvider.Equals(SmsOptions.LogProvider, StringComparison.OrdinalIgnoreCase))
        {
            if (!allowLogSender) problems.Add("Sms:Provider (log sends no SMS and returns the sign-in codes in the response — set smsoffice with Sms:SmsOffice:Key, or Sms:AllowLogSender=true for a dry run)");
        }
        else problems.Add($"Sms:Provider ('{smsProvider}' is neither log nor smsoffice)");

        return problems;
    }

    /// <summary>Why an smsoffice.ge API key is unusable outside Development, or null. The real ones are long opaque tokens.</summary>
    public static string? SmsOfficeKeyProblem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "missing";
        if (key.Any(char.IsWhiteSpace)) return "contains whitespace";
        if (PublicKeyMarkers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase))) return "a placeholder, dev or test value";
        if (key.Length < 16) return "too short to be an smsoffice API key";
        return null;
    }

    /// <summary>Why a JWT signing key is unusable outside Development, or null.</summary>
    public static string? JwtKeyProblem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "missing";
        if (key.Length < 32) return ">= 32 random chars";
        if (PublicKeyMarkers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase))) return "a published placeholder, dev, CI or test key";
        // A random 32+ char key (base64, hex or a password manager's) has far more than ten distinct characters.
        if (key.Distinct().Count() < 10) return "not random";
        return null;
    }

    /// <summary>Why a base64 32-byte data key (Encryption:Key / Encryption:HashKey) is unusable outside Development, or null.</summary>
    public static string? DataKeyProblem(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return "missing (base64, 32 bytes)";
        byte[] key;
        try { key = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { return "not base64"; }
        if (key.Length != 32) return "must be 32 bytes";
        // 32 random bytes are never this regular: about 30 distinct values, and the chance that all 32 happen to be
        // printable characters is ~1e-14. The CI keys (all 0x00, all 0x01) and the test fixtures' ASCII phrases are.
        if (key.Distinct().Count() < 16) return "not random — a published CI or test key?";
        if (key.All(b => b is >= 0x20 and <= 0x7E)) return "not random — text, not 32 random bytes";
        return null;
    }
}

/// <summary>Which peers may set X-Forwarded-For / -Proto (Proxy:KnownNetworks, Proxy:KnownProxies, Proxy:ForwardLimit).</summary>
public static class ProxyTrust
{
    public static (List<AspNetIPNetwork> networks, List<IPAddress> proxies, List<string> errors) Read(IConfiguration cfg)
    {
        var networks = new List<AspNetIPNetwork>();
        var proxies = new List<IPAddress>();
        var errors = new List<string>();
        foreach (var cidr in cfg.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? Array.Empty<string>())
        {
            var parts = cidr.Trim().Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var len)
                && len >= 0 && len <= (prefix.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
                networks.Add(new AspNetIPNetwork(prefix, len));
            else errors.Add($"Proxy:KnownNetworks ('{cidr}' is not an address/prefix-length)");
        }
        foreach (var ip in cfg.GetSection("Proxy:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
        {
            if (IPAddress.TryParse(ip.Trim(), out var a)) proxies.Add(a);
            else errors.Add($"Proxy:KnownProxies ('{ip}' is not an address)");
        }
        return (networks, proxies, errors);
    }

    /// <summary>
    /// The rate limiter's partition for a client address. IPv4 (and IPv4-mapped IPv6) per address; IPv6 per /64 — one
    /// subscriber line holds a whole /64, so keying on the full address would hand every client 2^64 fresh partitions.
    /// </summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
