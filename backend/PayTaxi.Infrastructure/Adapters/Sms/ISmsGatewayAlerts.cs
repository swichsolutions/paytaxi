namespace PayTaxi.Infrastructure.Adapters.Sms;

/// <summary>
/// How the SMS gateway tells a person that messages are not going out — an answer no retry fixes (the account is out of
/// balance, the key is wrong, the API is off, the sender is not approved) or a balance about to run out. Implemented on
/// the incident log (<see cref="SmsGatewayIncident"/>), which the console's alert banner already shows to everyone; the
/// sender's unit tests record the calls instead.
/// </summary>
public interface ISmsGatewayAlerts
{
    /// <summary>One alert at a time: a second cause while the first is open is added to it, never a second incident.</summary>
    /// <param name="code">What is wrong, as a stable code: <see cref="SmsGatewayCodes"/>.</param>
    /// <param name="detail">One English sentence for the record (no phone, no message text).</param>
    Task RaiseAsync(string code, string detail, CancellationToken ct = default);

    /// <summary>Closes the open alert if its cause is one of <paramref name="codes"/> (a sent message says nothing about a low balance).</summary>
    Task ClearAsync(IReadOnlyCollection<string> codes, string resolvedText, CancellationToken ct = default);
}

/// <summary>The causes the gateway alerts on; the incident's Cause starts with the code ("INSUFFICIENT_BALANCE: …").</summary>
public static class SmsGatewayCodes
{
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";
    public const string BadKey = "BAD_KEY";
    public const string ApiDisabled = "API_DISABLED";
    public const string SenderRejected = "SENDER_REJECTED";
    public const string LowBalance = "LOW_BALANCE";

    /// <summary>Cleared by a message the gateway accepted: whatever stopped sending is over (a low balance may still be low).</summary>
    public static readonly string[] ClearedBySend = { InsufficientBalance, BadKey, ApiDisabled, SenderRejected };

    /// <summary>Cleared by a balance read above the warning line.</summary>
    public static readonly string[] ClearedByBalance = { LowBalance, InsufficientBalance };
}
