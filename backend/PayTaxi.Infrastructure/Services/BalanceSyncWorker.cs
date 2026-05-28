using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Periodically refreshes <see cref="YandexBalanceCache"/> for every active driver
/// in every active park. Runs as a singleton hosted service and opens its own DI
/// scope per tick so it never collides with request-scoped DbContexts.
///
/// Goes through the standard <see cref="IYandexFleetClient"/>, which means the
/// per-park rate limiter, retries, and audit logging all apply automatically —
/// no special handling here.
/// </summary>
public class BalanceSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BalanceSyncOptions _opts;
    private readonly ILogger<BalanceSyncWorker> _log;

    public BalanceSyncWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<BalanceSyncOptions> opts,
        ILogger<BalanceSyncWorker> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Balance sync worker disabled by configuration");
            return;
        }

        // Stagger startup so the first sync doesn't race the rest of app boot.
        try { await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation(
            "Balance sync worker started (interval {Interval}s)", _opts.IntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Tick failures must not kill the loop.
                _log.LogError(ex, "Balance sync tick failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_opts.IntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Balance sync worker stopped");
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var yandex = scope.ServiceProvider.GetRequiredService<IYandexFleetClient>();

        var parks = await db.Parks
            .AsNoTracking()
            .Where(p => p.Status == ParkStatus.Active)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var totalUpdated = 0;
        var totalSkipped = 0;
        foreach (var park in parks)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<YandexDriverProfile> profiles;
            try
            {
                profiles = await yandex.GetDriverProfilesAsync(park.Id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping park {Park} this tick — Yandex fetch failed", park.Name);
                continue;
            }

            // Translate Yandex profile ids → internal driver ids in one round trip.
            var profileIds = profiles.Select(p => p.DriverProfileId).ToHashSet();
            var driverRows = await db.Drivers
                .AsNoTracking()
                .Where(d => d.ParkId == park.Id
                         && d.YandexDriverProfileId != null
                         && profileIds.Contains(d.YandexDriverProfileId!))
                .Select(d => new { d.Id, d.YandexDriverProfileId })
                .ToListAsync(ct);

            var driverByProfile = driverRows.ToDictionary(d => d.YandexDriverProfileId!, d => d.Id);

            // Load existing cache rows for these drivers once, mutate in-place.
            var driverIds = driverRows.Select(d => d.Id).ToList();
            var existing = await db.YandexBalanceCaches
                .Where(y => driverIds.Contains(y.DriverId))
                .ToDictionaryAsync(y => y.DriverId, ct);

            foreach (var profile in profiles)
            {
                if (!driverByProfile.TryGetValue(profile.DriverProfileId, out var driverId))
                {
                    totalSkipped++;
                    continue;
                }

                if (existing.TryGetValue(driverId, out var row))
                {
                    row.Balance = profile.Balance;
                    row.Currency = profile.Currency;
                    row.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    db.YandexBalanceCaches.Add(new YandexBalanceCache
                    {
                        DriverId = driverId,
                        ParkId = park.Id,
                        Balance = profile.Balance,
                        Currency = profile.Currency,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
                totalUpdated++;
            }

            await db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "Balance sync tick: {Updated} balances refreshed, {Skipped} unmatched profiles, {Parks} parks scanned",
            totalUpdated, totalSkipped, parks.Count);
    }
}

public class BalanceSyncOptions
{
    public const string SectionName = "BalanceSync";

    public bool Enabled { get; set; } = true;

    /// <summary>How often to sync, in seconds.</summary>
    public int IntervalSeconds { get; set; } = 300; // 5 minutes

    /// <summary>Delay between app start and the first tick.</summary>
    public int InitialDelaySeconds { get; set; } = 15;
}
