namespace PayTaxi.Core.Entities;

/// <summary>
/// One sign-in event of a phone number, kept for the rolling day the per-phone limits look back over: a code issued
/// (<see cref="PhoneSignInEventKinds.CodeIssued"/>), a verification that did not sign in
/// (<see cref="PhoneSignInEventKinds.Failed"/>), or the lockout those failures triggered
/// (<see cref="PhoneSignInEventKinds.Locked"/>, until <see cref="Until"/>).
///
/// Keyed by the phone's keyed lookup hash, like OtpCodes — the phone itself is never stored here. Written for EVERY
/// phone that asks, registered or not: the limits must answer an unknown number exactly as they answer a driver's,
/// or the lockout would become a way to tell registered phones apart (OtpCodes rows exist only for drivers, which is
/// why they cannot carry these counts). Deleted a day after the event (RetentionWorker), a lockout not before it ends.
/// </summary>
public class PhoneSignInEvent
{
    public long Id { get; set; }

    /// <summary>The phone's keyed lookup hash (same as Drivers.PhoneHash and OtpCodes.PhoneHash).</summary>
    public string PhoneHash { get; set; } = default!;

    /// <summary><see cref="PhoneSignInEventKinds"/>.</summary>
    public string Kind { get; set; } = default!;

    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>A lockout: when the phone may ask for and enter codes again. Null for the other kinds.</summary>
    public DateTime? Until { get; set; }
}

public static class PhoneSignInEventKinds
{
    public const string CodeIssued = "code_issued";
    public const string Failed = "failed";
    public const string Locked = "locked";
}
