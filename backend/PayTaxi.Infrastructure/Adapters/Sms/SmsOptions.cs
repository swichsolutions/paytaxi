namespace PayTaxi.Infrastructure.Adapters.Sms;

/// <summary>
/// "Sms" configuration: which sender is registered and the smsoffice.ge account. The key is a secret (env
/// <c>Sms__SmsOffice__Key</c> / Key Vault), never in appsettings — outside Development the startup guard refuses a host
/// without one unless <see cref="AllowLogSender"/> says the dry run is deliberate (PayTaxi.Api.Security.StartupGuard).
/// </summary>
public class SmsOptions
{
    public const string SectionName = "Sms";
    public const string LogProvider = "log";
    public const string SmsOfficeProvider = "smsoffice";

    /// <summary>"log" (nothing leaves the server; the sign-in code is shown in the response) or "smsoffice".</summary>
    public string Provider { get; set; } = LogProvider;

    /// <summary>
    /// Outside Development only: lets a host start with the log sender (Provider log, or smsoffice without a key). A host
    /// that sends no SMS signs no driver in on a new device and shows the codes in its API responses — a dry run, never
    /// the live server.
    /// </summary>
    public bool AllowLogSender { get; set; }

    public SmsOfficeOptions SmsOffice { get; set; } = new();

    /// <summary>The gateway adapter is registered only with the provider named AND a key to call it with.</summary>
    public bool UsesSmsOffice =>
        string.Equals(Provider?.Trim(), SmsOfficeProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(SmsOffice.Key);
}

/// <summary>smsoffice.ge (https://smsoffice.ge/api/v2/send/): one account for the whole platform, not per park.</summary>
public class SmsOfficeOptions
{
    /// <summary>The API key from the smsoffice.ge profile page.</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// The sender name shown on the driver's phone: at most 11 characters of [A-Za-z0-9.-]. "smsoffice" is available to
    /// every account; a custom one ("PayTaxi") needs smsoffice's approval first — until then the gateway answers 150.
    /// </summary>
    public string Sender { get; set; } = "smsoffice";

    public string BaseUrl { get; set; } = "https://smsoffice.ge";

    /// <summary>
    /// The balance check (at start, then every 6 h) raises the operator alert when fewer messages than this are left —
    /// before error 20 (insufficient balance) starts refusing the sign-in codes themselves.
    /// </summary>
    public int LowBalanceWarnAt { get; set; } = 50;
}
