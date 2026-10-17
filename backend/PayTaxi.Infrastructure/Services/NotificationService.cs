using Microsoft.EntityFrameworkCore;
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
    private readonly ISmsSender _sms;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(
        IServiceScopeFactory scopeFactory,
        ISmsSender sms,
        ILogger<NotificationService> log)
    {
        _scopeFactory = scopeFactory;
        _sms = sms;
        _log = log;
    }

    /// <summary>
    /// How long a payout-account change waits for the SMS gateway before it answers anyway. The SMS is not cancelled: it
    /// keeps going in the background and logs its own failure. A slow gateway must not hold the change up (best effort).
    /// </summary>
    public TimeSpan SmsWait { get; init; } = TimeSpan.FromSeconds(5);

    public Task NotifyCashoutCompletedAsync(
        Guid driverId, decimal netAmount, string destinationLabel, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_completed",
            title: "Cashout sent",
            body: $"₾ {netAmount:F2} sent to {destinationLabel}.",
            link: "/history",
            data: new { amount = netAmount, destination = destinationLabel },
            ct: ct);

    public Task NotifyCashoutQueuedAsync(
        Guid driverId, decimal netAmount, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_queued",
            title: "Cashout processing",
            body: $"₾ {netAmount:F2} is on its way — it goes out automatically as soon as it can be sent. No action needed.",
            link: "/history",
            data: new { amount = netAmount },
            ct: ct);

    // The driver app renders its own translated text from `code`; `reason` (already a plain, driver-safe
    // sentence from the saga) is only the English fallback in the body. Neither ever carries the stored
    // failure reason — a bank's detail or an admin's note is not the driver's to read.
    public Task NotifyCashoutFailedAsync(
        Guid driverId, decimal amount, string reason, string? code = null, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_failed",
            title: "Cashout failed",
            body: $"₾ {amount:F2}: {reason}",
            link: "/history",
            data: new { amount, code },
            ct: ct);

    public Task NotifyCashoutReviewRequiredAsync(
        Guid driverId, decimal amount, CancellationToken ct = default) =>
        InsertAsync(driverId, "cashout_review",
            title: "Cashout under review",
            body: $"Your ₾ {amount:F2} cashout needs manual review. We'll follow up shortly.",
            link: "/history",
            data: new { amount },
            ct: ct);

    public async Task NotifyPayoutAccountChangedAsync(
        Guid driverId, PayoutAccountChange change, string destination, bool byPark, CancellationToken ct = default)
    {
        // The in-app notice first: it is ours to write and the app shows it in its own language (from type + data). Then
        // the SMS, in the language the driver last used the app in — the console's change reaches a driver who may not
        // open the app for days, and that is the case the SMS is for.
        string? phone = null, language = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var driver = await db.Drivers.AsNoTracking().Where(d => d.Id == driverId)
                .Select(d => new { d.PhoneEncrypted, d.PreferredLanguage })
                .FirstOrDefaultAsync(ct);
            if (driver is null) return;
            (phone, language) = (driver.PhoneEncrypted, driver.PreferredLanguage);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Payout-account notice for driver {DriverId}: the driver could not be read", driverId);
        }

        await InsertAsync(driverId, PayoutAccountNotice.Type,
            title: PayoutAccountNotice.EnglishTitle(change),
            body: PayoutAccountNotice.Text("en", change, byPark, destination),
            link: "/profile",
            data: PayoutAccountNotice.IsDefault(change)
                ? new { destination, change = PayoutAccountNotice.Code(change), by = byPark ? "park" : "driver", isDefault = true }
                : new { destination, change = PayoutAccountNotice.Code(change), by = byPark ? "park" : "driver" } as object,
            ct: ct);

        if (string.IsNullOrWhiteSpace(phone)) return;
        Task send;
        try
        {
            send = _sms.SendAsync(phone, PayoutAccountNotice.Sms(language, change, byPark, destination),
                PayoutAccountNotice.Type, $"driver {driverId}", ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Payout-account SMS ({Change}) for driver {DriverId} could not be sent", change, driverId);
            return;
        }
        // A failure is logged whenever it comes — also one that comes after the change has answered.
        _ = send.ContinueWith(t => _log.LogError(t.Exception, "Payout-account SMS ({Change}) for driver {DriverId} could not be sent", change, driverId),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try { await send.WaitAsync(SmsWait, CancellationToken.None); }
        catch (TimeoutException)
        {
            _log.LogWarning("Payout-account SMS ({Change}) for driver {DriverId}: the gateway is slow; the change answers while it is still sending", change, driverId);
        }
        catch (Exception) { /* logged by the continuation above */ }
    }

    private async Task InsertAsync(
        Guid driverId, string type, string title, string body, string? link, object? data, CancellationToken ct)
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
                Data = data is null ? null : System.Text.Json.JsonSerializer.Serialize(data),
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
