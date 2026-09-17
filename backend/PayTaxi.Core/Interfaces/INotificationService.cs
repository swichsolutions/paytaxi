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

    /// <summary>Payout is queued behind a bank hiccup — money is on its way, no action needed.</summary>
    Task NotifyCashoutQueuedAsync(
        Guid driverId, decimal netAmount, CancellationToken ct = default);

    Task NotifyCashoutFailedAsync(
        Guid driverId, decimal amount, string reason, CancellationToken ct = default);

    Task NotifyCashoutReviewRequiredAsync(
        Guid driverId, decimal amount, CancellationToken ct = default);
}
