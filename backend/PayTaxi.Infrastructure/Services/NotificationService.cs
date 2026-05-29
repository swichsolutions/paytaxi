using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Inserts <see cref="Notification"/> rows. Uses an isolated DI scope per
/// call so it can be safely invoked fire-and-forget from request-scoped
/// callers (the saga, in particular) without colliding with their DbContext.
/// Registered as a singleton.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public Task NotifyCashoutCompletedAsync(
        Guid driverId, decimal netAmount, string cardMaskedPan, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_completed",
            title: "Cashout sent",
            body: $"₾ {netAmount:F2} sent to {cardMaskedPan}.",
            link: "/history",
            ct);

    public Task NotifyCashoutFailedAsync(
        Guid driverId, decimal amount, string reason, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_failed",
            title: "Cashout failed",
            body: $"₾ {amount:F2} could not be sent: {reason}",
            link: "/history",
            ct);

    public Task NotifyCashoutReviewRequiredAsync(
        Guid driverId, decimal amount, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_review",
            title: "Cashout under review",
            body: $"Your ₾ {amount:F2} cashout needs manual review. We'll follow up shortly.",
            link: "/history",
            ct);

    private async Task InsertAsync(
        Guid driverId, string type, string title, string body, string? link, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Notifications.Add(new Notification
            {
                DriverId = driverId,
                Type = type,
                Title = title,
                Body = body,
                Link = link,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to write notification ({Type}) for driver {DriverId}",
                type, driverId);
        }
    }
}
