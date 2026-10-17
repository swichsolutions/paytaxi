namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Drops notifications onto a driver's in-app inbox. Fire-and-forget from
/// the saga's perspective — failures are logged but never bubble up.
///
/// Real channels (SMS, push) will plug in as separate side-effects that
/// read from the same Notifications table; this service only writes the row.
/// </summary>
public interface INotificationService
{
    Task NotifyCashoutCompletedAsync(
        Guid driverId, decimal netAmount, string destinationLabel, CancellationToken ct = default);

    /// <summary>Payout is queued (bank down, park account short) — money goes out automatically, no action needed.</summary>
    Task NotifyCashoutQueuedAsync(
        Guid driverId, decimal netAmount, CancellationToken ct = default);

    /// <param name="reason">
    /// A plain sentence for the driver (the English fallback body) — never the stored failure reason,
    /// which can carry a bank's error detail or an admin's note and e-mail.
    /// </param>
    /// <param name="code">
    /// The failure code (e.g. <c>CANCELLED</c>, <c>INVALID_DESTINATION</c>), stored with the notification so
    /// the driver app can show its own translated text instead of the server's English reason.
    /// </param>
    Task NotifyCashoutFailedAsync(
        Guid driverId, decimal amount, string reason, string? code = null, CancellationToken ct = default);

    Task NotifyCashoutReviewRequiredAsync(
        Guid driverId, decimal amount, CancellationToken ct = default);

    /// <summary>
    /// One of the driver's payout accounts was added, made the default or removed — by the driver in the app, or by the
    /// park's side in the console (its manager, the operator or Swich: "your taxi park" to the driver). The driver gets an
    /// in-app notice and an SMS in their language naming the masked account, who made the change, and to contact the
    /// park if it was not them: a payout account is where their money goes, and nothing else tells them it changed.
    /// Best effort: never throws, every failure is logged — the change itself stands either way.
    /// </summary>
    /// <param name="destination">The masked account as the app shows it, e.g. "TBC **** 8298".</param>
    Task NotifyPayoutAccountChangedAsync(
        Guid driverId, PayoutAccountChange change, string destination, bool byPark, CancellationToken ct = default);
}

/// <summary>What happened to a driver's payout account (see <see cref="INotificationService.NotifyPayoutAccountChangedAsync"/>).</summary>
public enum PayoutAccountChange
{
    Added,
    /// <summary>Added and made the default in one step — the payouts go there from now on, so the notice says so.</summary>
    AddedAsDefault,
    MadeDefault,
    Removed,
}
