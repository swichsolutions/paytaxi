using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Fires the settlement tick once per local day at <see cref="SettlementOptions.RunAtLocalTime"/>
/// (default 00:30 Tbilisi). Every night it retries failed park transfers (until they succeed) and
/// resolves interrupted / bank-pending ones; from <see cref="SettlementOptions.RunDayOfMonth"/> on
/// (default the 3rd) it also settles the PREVIOUS month — licence agreement §2.5: collected within
/// five days of the month's end.
///
/// Restart-safe by construction: statements are unique per (licensee, month) and direct-park rows per
/// (park, month), every transfer carries a deterministic document id, so a tick re-entered after a
/// restart finds the month already settled and only retries what is still failed. "Run day or later"
/// (not "exactly the run day") means a server that was down on the 3rd settles on the 4th.
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
        _log.LogInformation("Settlement worker started (nightly at {RunAt} {Tz}; previous month settled from day {Day})",
            runAt, tz.Id, Math.Clamp(_opts.RunDayOfMonth, 1, 28));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                var today = DateOnly.FromDateTime(nowLocal);
                var isPastRunTime = TimeOnly.FromDateTime(nowLocal) >= runAt;

                if (isPastRunTime && _lastRunFor != today)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ISettlementService>();
                    var summary = await svc.RunDueAsync(today, "monthly", ct);
                    _lastRunFor = today;
                    _log.LogInformation("Settlement tick for {Date}: {Summary}", today, summary);
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
}
