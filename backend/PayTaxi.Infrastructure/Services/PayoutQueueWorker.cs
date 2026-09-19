using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Drains <see cref="CashoutStatus.Queued"/> cashouts — the ones whose Yandex debit
/// succeeded but whose bank payout hit a retryable problem (gateway down, low float,
/// rate limit). Each tick picks every due cashout, groups by park and processes each
/// park's queue strictly one-at-a-time in creation order so a recovering bank is not
/// hammered and payouts land in the order drivers asked for them. Parks run in
/// parallel to each other.
///
/// The claim itself (Queued → Processing) is an atomic UPDATE inside
/// <see cref="ICashoutOrchestrator.ProcessQueuedAsync"/>, so an overlapping tick or a
/// second API instance can't double-pay.
/// </summary>
public class PayoutQueueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PayoutQueueOptions _opts;
    private readonly ILogger<PayoutQueueWorker> _log;

    public PayoutQueueWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PayoutQueueOptions> opts,
        ILogger<PayoutQueueWorker> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Payout queue worker disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("Payout queue worker started (poll every {Interval}s)", _opts.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Payout queue tick failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Payout queue worker stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<(Guid Id, Guid ParkId)> due;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Sagas interrupted mid-flight (crash/deploy) would otherwise sit in Processing forever.
            if (_opts.StaleProcessingMinutes > 0)
            {
                var saga = scope.ServiceProvider.GetRequiredService<ICashoutOrchestrator>();
                var swept = await saga.SweepStaleProcessingAsync(TimeSpan.FromMinutes(_opts.StaleProcessingMinutes), ct);
                if (swept > 0) _log.LogWarning("Payout queue: {Count} interrupted cashout(s) flagged for review", swept);
            }
            var now = DateTime.UtcNow;
            var rows = await db.Cashouts.AsNoTracking()
                .Where(c => (c.Status == CashoutStatus.Queued && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
                         || (c.Status == CashoutStatus.Processing && c.BankTransferId != null
                             && c.NextAttemptAt != null && c.NextAttemptAt <= now))
                .OrderBy(c => c.CreatedAt)
                .Take(_opts.MaxPerTick)
                .Select(c => new { c.Id, c.ParkId })
                .ToListAsync(ct);
            due = rows.Select(r => (r.Id, r.ParkId)).ToList();
        }

        if (due.Count == 0) return;

        _log.LogInformation("Payout queue: {Count} cashout(s) due across {Parks} park(s)",
            due.Count, due.Select(d => d.ParkId).Distinct().Count());

        var perPark = due.GroupBy(d => d.ParkId)
            .Select(g => DrainParkAsync(g.Key, g.Select(x => x.Id).ToList(), ct));
        await Task.WhenAll(perPark);
    }

    private async Task DrainParkAsync(Guid parkId, List<Guid> cashoutIds, CancellationToken ct)
    {
        foreach (var id in cashoutIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Fresh scope per cashout: isolated DbContext + Yandex client per saga step.
                using var scope = _scopeFactory.CreateScope();
                var saga = scope.ServiceProvider.GetRequiredService<ICashoutOrchestrator>();
                var result = await saga.ProcessQueuedAsync(id, ct);
                _log.LogInformation("Payout queue: cashout {Id} → {Status} (attempt {N})",
                    id, result.Status, result.AttemptCount);

                // Stop hammering this park if its bank is still down — the rest of its
                // queue would only burn attempts. They'll be picked up next tick.
                if (result.Status == nameof(CashoutStatus.Queued) && result.BankTransferId is null && _opts.StopParkOnFirstRequeue)
                {
                    _log.LogInformation("Payout queue: park {Park} still failing — deferring its remaining {Left} cashout(s)",
                        parkId, cashoutIds.Count - cashoutIds.IndexOf(id) - 1);
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Payout queue: processing cashout {Id} threw", id);
            }

            if (_opts.SpacingMs > 0)
                await Task.Delay(_opts.SpacingMs, ct);
        }
    }
}

public class PayoutQueueOptions
{
    public const string SectionName = "PayoutQueue";

    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 10;
    public int InitialDelaySeconds { get; set; } = 8;

    /// <summary>Upper bound on rows claimed per tick, across all parks.</summary>
    public int MaxPerTick { get; set; } = 200;

    /// <summary>Gap between consecutive payouts for the same park (bank + Yandex courtesy spacing).</summary>
    public int SpacingMs { get; set; } = 500;

    /// <summary>If the first cashout of a park re-queues (bank still down), skip the rest of that park this tick.</summary>
    public bool StopParkOnFirstRequeue { get; set; } = true;

    /// <summary>
    /// A cashout Processing with no bank id and no next attempt for this long is treated as an
    /// interrupted saga and flagged ReviewRequired. Must comfortably exceed the Yandex retry budget. 0 disables.
    /// </summary>
    public int StaleProcessingMinutes { get; set; } = 10;
}
