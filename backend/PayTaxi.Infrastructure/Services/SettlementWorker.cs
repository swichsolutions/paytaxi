using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Fires the nightly settlement once per local day at <see cref="SettlementOptions.RunAtLocalTime"/>
/// (default 00:30 Tbilisi), settling the PREVIOUS local day. The run itself is idempotent
/// per (park, day), so a restart after 00:30 simply re-enters and finds nothing new to do.
/// </summary>
public class SettlementWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettlementOptions _opts;
    private readonly ILogger<SettlementWorker> _log;

    private DateOnly? _lastRunFor;

    public SettlementWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<SettlementOptions> opts,
        ILogger<SettlementWorker> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Settlement worker disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        var tz = SettlementService.ResolveTimeZone(_opts.TimeZoneId);
        var runAt = TimeOnly.TryParse(_opts.RunAtLocalTime, out var t) ? t : new TimeOnly(0, 30);
        _log.LogInformation("Settlement worker started (runs daily at {RunAt} {Tz})", runAt, tz.Id);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                var today = DateOnly.FromDateTime(nowLocal);
                var dueFor = today.AddDays(-1); // the day we settle once RunAt has passed
                var isPastRunTime = TimeOnly.FromDateTime(nowLocal) >= runAt;

                if (isPastRunTime && _lastRunFor != dueFor && !await AlreadyRanAsync(dueFor, ct))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ISettlementService>();
                    var summary = await svc.RunDueAsync(dueFor, "nightly", ct);
                    _lastRunFor = dueFor;
                    _log.LogInformation("Nightly settlement for {Date}: {Summary}", dueFor, summary);
                }
                else if (isPastRunTime)
                {
                    _lastRunFor = dueFor;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Settlement tick failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Settlement worker stopped");
    }

    /// <summary>
    /// After a restart: treat the night as done if any settlement exists for that date.
    /// (Parks with nothing to settle create no row, so a restart can re-run harmlessly.)
    /// </summary>
    private async Task<bool> AlreadyRanAsync(DateOnly date, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Settlements.AsNoTracking().AnyAsync(s => s.SettlementDate == date && s.InitiatedBy == "nightly", ct);
    }
}
