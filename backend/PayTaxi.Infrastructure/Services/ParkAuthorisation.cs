using PayTaxi.Core.Entities;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Annex 3 §7: the park may revoke the authorisations of §2 (payouts to its drivers from its account) and §3 (the
/// monthly transfer of Swich's share from it) by written notice to the licensee and Swich at least [30] days ahead;
/// obligations that arose before the revocation stay.
///
/// One rule for every place that would move money out of a park's account — the cashout saga, the payout queue and
/// the settlement engine: from <see cref="Park.AuthorisationRevokedFrom"/> (a Tbilisi calendar day) on, the platform
/// INITIATES nothing there. What it may still do: ask the bank about an order it sent before (a lookup or a status
/// poll moves no money), give a driver's debit back on Yandex (a cancel), and record a payment made outside the
/// platform. Nothing that was owed is forgiven: waiting payouts wait until a person cancels them, and a settlement
/// share stays owed — by the licensee under §2.5 for a licensee park, by the park itself for a Swich-direct one.
/// </summary>
public static class ParkAuthorisation
{
    /// <summary>The notice period in days (Annex 3 §7, "[30]" — still bracketed in the contract).</summary>
    public const int NoticeDays = 30;

    /// <summary>
    /// Failure code of a waiting payout or a settlement transfer that was not initiated because the park revoked the
    /// authorisation. A settlement row carrying it is never retried (it waits for the outside payment), and the §4.1.2
    /// overdue information counts it as the park's side, not a technical fault.
    /// </summary>
    public const string RevokedCode = "AUTHORISATION_REVOKED";

    /// <summary>The revocation is in effect on <paramref name="todayLocal"/> (Tbilisi).</summary>
    public static bool IsRevoked(DateOnly? revokedFrom, DateOnly todayLocal) => revokedFrom is { } from && todayLocal >= from;

    public static bool IsRevoked(Park park, DateOnly todayLocal) => IsRevoked(park.AuthorisationRevokedFrom, todayLocal);

    /// <summary>The earliest day a revocation noticed on <paramref name="noticeOn"/> can take effect.</summary>
    public static DateOnly EarliestEffect(DateOnly noticeOn) => noticeOn.AddDays(NoticeDays);

    /// <summary>"AUTHORISATION_REVOKED: …" — the row's failure reason carries the code first, like every other.</summary>
    public static bool IsRevokedReason(string? failureReason) =>
        failureReason is not null && failureReason.StartsWith(RevokedCode + ":", StringComparison.Ordinal);
}
