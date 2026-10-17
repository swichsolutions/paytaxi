namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Sends one text message to a phone. The registered sender is either the smsoffice.ge gateway adapter (Sms:Provider =
/// smsoffice with an API key) or, without a gateway, a sender that only logs what it would send.
///
/// Callers treat it as best effort: a message that cannot be sent is logged by the sender, never thrown at the action
/// that caused it (a payout-account change must not fail because an SMS did not go out; neither must a sign-in request).
/// </summary>
public interface ISmsSender
{
    /// <summary>
    /// True when messages leave the server (a gateway is wired). The sign-in endpoint shows the code in its response —
    /// Development's convenience, read by the tests and the e2e runs — only while this is false: with a gateway, the
    /// code reaches the driver as an SMS and nowhere else, in Development too.
    /// </summary>
    bool DeliversForReal { get; }

    /// <param name="phoneE164">The recipient, "+995…".</param>
    /// <param name="text">The message, already in the recipient's language.</param>
    /// <param name="purpose">A short tag for the log and the gateway's reports, e.g. "payout_account_changed".</param>
    /// <param name="reference">What it is about, for the log (a driver id) — never the phone or the text.</param>
    Task SendAsync(string phoneE164, string text, string purpose, string reference, CancellationToken ct = default);
}
