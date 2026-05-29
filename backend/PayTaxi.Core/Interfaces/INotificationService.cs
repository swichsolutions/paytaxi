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
        Guid driverId, decimal netAmount, string cardMaskedPan, CancellationToken ct = default);

    Task NotifyCashoutFailedAsync(
        Guid driverId, decimal amount, string reason, CancellationToken ct = default);

    Task NotifyCashoutReviewRequiredAsync(
        Guid driverId, decimal amount, CancellationToken ct = default);
}
