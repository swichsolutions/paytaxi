using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Keeps the retention periods the drivers' privacy notice promises. It deletes:
///   - the per-phone sign-in events (PhoneSignInEvents: codes issued, failed verifications, lockouts) a day after they
///     happened — a lockout not before it ends — on every poll as well: they exist only for the rolling day the
///     sign-in limits look back over;
///   - one-time sign-in codes (OtpCodes) 24 hours after they were issued — on EVERY poll
///     (<see cref="RetentionOptions.PollIntervalSeconds"/>, default 5 minutes), because the notice says a code is
///     deleted within a day: purged only once a night, a code issued just after the pass would live until the next
///     night, up to 48 hours. A code is valid for 5 minutes; the rest of the day keeps it countable by the per-phone
///     code cap and by anyone looking into a sign-in problem;
/// and once a night (Tbilisi time, <see cref="RetentionOptions.RunAtLocalTime"/>, default 03:30 — after the settlement
/// tick, away from the evening cashout peak):
///   - driver device sessions 90 days after their last use, and a day after they ended — signed out, cut off (a
///     suspension, a detected token copy) or expired. A ROTATED token is the exception while its chain is alive (it
///     goes with the chain, a day after the chain's expiry, which is at most 90 days after the SMS sign-in): presented
///     again it reveals a copied token and signs every device out, and deleting it earlier would let a thief who
///     refreshed first go unnoticed;
///   - the log of calls to Yandex and the banks (ApiAuditLogs) and the sign-in history (DriverSignIns) after 2 years.
/// Each period is configurable (section "Retention"); a period set to 0 or less (ended sessions: below 0) keeps that
/// kind for good rather than deleting all of it. Every nightly pass logs how many rows of each kind it deleted (the
/// sign-in codes counted since the previous pass, so the polls' deletions are in the nightly line too).
///
/// It never touches a financial record: cashouts, invoices, the ledger, settlements and monthly statements, payout
/// accounts (a removed one stays — cashouts point at it, so it is part of the record) and drivers. When those expire
/// (the accounting period) and how a deactivated driver is de-personalised is a separate job, not done here.
///
/// Restart-safe: every pass deletes by age only, so running it twice — or late, after the server was down at the run
/// time — deletes nothing more than one pass would.
/// </summary>
public class RetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RetentionOptions _opts;
    private readonly ILogger<RetentionWorker> _log;

    private readonly TimeZoneInfo _tz;
    private readonly TimeOnly _runAt;
    private DateOnly? _lastRunFor;
    /// <summary>Sign-in codes the ordinary polls deleted since the last nightly pass — reported in its log line.</summary>
    private int _codesSinceLastPass;

    public RetentionWorker(IServiceScopeFactory scopeFactory, IOptions<RetentionOptions> opts, ILogger<RetentionWorker> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
        _tz = SettlementService.ResolveTimeZone(_opts.TimeZoneId);
        _runAt = TimeOnly.TryParse(_opts.RunAtLocalTime, out var t) ? t : new TimeOnly(3, 30);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Retention worker disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation(
            "Retention worker started (nightly at {RunAt} {Tz}; sign-in codes {Codes} h, idle devices {Idle} d, ended sessions {Ended} d, call log {Calls} months, sign-in history {SignIns} months)",
            _runAt, _tz.Id, _opts.SignInCodeHours, _opts.DeviceIdleDays, _opts.EndedSessionDays, _opts.CallLogMonths, _opts.SignInHistoryMonths);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTime.UtcNow, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Retention pass failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Retention worker stopped");
    }

    /// <summary>
    /// One poll of the loop at <paramref name="nowUtc"/>; also called directly by the tests. The poll that finds the
    /// nightly pass due (at or after RunAtLocalTime, once per Tbilisi day — late after downtime) runs it, sign-in codes
    /// included; every other poll deletes only the sign-in codes past their hours.
    /// </summary>
    public async Task TickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _tz);
        var today = DateOnly.FromDateTime(nowLocal);
        if (TimeOnly.FromDateTime(nowLocal) >= _runAt && _lastRunFor != today)
        {
            await RunOnceAsync(nowUtc, ct);
            _lastRunFor = today;
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var codes = await DeleteSignInCodesAsync(db, nowUtc, ct);
        await DeleteSignInEventsAsync(db, nowUtc, ct);
        if (codes > 0)
        {
            // Debug, not Information: with a 5-minute poll this would be a line every few minutes all day. The
            // nightly line carries the day's total.
            _codesSinceLastPass += codes;
            _log.LogDebug("Retention poll: deleted {Codes} sign-in code(s) older than {Hours} h", codes, _opts.SignInCodeHours);
        }
    }

    /// <summary>One pass at <paramref name="nowUtc"/>. Also called directly by the tests.</summary>
    public async Task<RetentionResult> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = Math.Max(100, _opts.BatchSize);

        var codes = await DeleteSignInCodesAsync(db, nowUtc, ct);
        await DeleteSignInEventsAsync(db, nowUtc, ct);

        var sessions = 0;
        var idleOn = _opts.DeviceIdleDays > 0;
        var endedOn = _opts.EndedSessionDays >= 0;
        if (idleOn || endedOn)
        {
            var idleBefore = nowUtc.AddDays(-Math.Max(_opts.DeviceIdleDays, 0));
            var endedBefore = nowUtc.AddDays(-Math.Max(_opts.EndedSessionDays, 0));
            sessions = await DeleteInBatchesAsync(db.DriverSessions
                .Where(s => (idleOn && (s.LastUsedAt ?? s.CreatedAt) < idleBefore)
                         || (endedOn && s.ExpiresAt < endedBefore)
                         || (endedOn && s.RevokedAt != null && s.RevokedAt < endedBefore && s.RevokedReason != RotatedReason))
                .OrderBy(s => s.Id), batch, ct);
        }

        var calls = 0;
        if (_opts.CallLogMonths > 0)
        {
            var cutoff = nowUtc.AddMonths(-_opts.CallLogMonths);
            calls = await DeleteInBatchesAsync(db.ApiAuditLogs.Where(a => a.Timestamp < cutoff).OrderBy(a => a.Id), batch, ct);
        }

        var signIns = 0;
        if (_opts.SignInHistoryMonths > 0)
        {
            var cutoff = nowUtc.AddMonths(-_opts.SignInHistoryMonths);
            signIns = await DeleteInBatchesAsync(db.DriverSignIns.Where(x => x.SignedInAt < cutoff).OrderBy(x => x.Id), batch, ct);
        }

        var result = new RetentionResult(codes, sessions, calls, signIns);
        var polledCodes = _codesSinceLastPass;
        _codesSinceLastPass = 0;
        _log.LogInformation(
            "Retention pass: deleted {Codes} sign-in code(s) (plus {PolledCodes} on the polls since the previous pass), {Sessions} device session(s), {Calls} call-log row(s), {SignIns} sign-in record(s)",
            codes, polledCodes, sessions, calls, signIns);
        return result;
    }

    /// <summary>
    /// Sign-in codes issued more than <see cref="RetentionOptions.SignInCodeHours"/> ago. Runs every poll; the table holds
    /// at most a day of codes, so the scan is cheap.
    /// </summary>
    private async Task<int> DeleteSignInCodesAsync(AppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        if (_opts.SignInCodeHours <= 0) return 0;
        var cutoff = nowUtc.AddHours(-_opts.SignInCodeHours);
        return await DeleteInBatchesAsync(db.OtpCodes.Where(o => o.CreatedAt < cutoff).OrderBy(o => o.Id), Math.Max(100, _opts.BatchSize), ct);
    }

    /// <summary>How far back the per-phone sign-in limits look (AuthController); their events are not needed longer.</summary>
    private static readonly TimeSpan SignInEventDays = TimeSpan.FromHours(24);

    /// <summary>Sign-in events older than the limits' day, except a lockout that has not ended yet.</summary>
    private static Task<int> DeleteSignInEventsAsync(AppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = nowUtc - SignInEventDays;
        return DeleteInBatchesAsync(db.PhoneSignInEvents.Where(e => e.At < cutoff && (e.Until == null || e.Until < nowUtc)).OrderBy(e => e.Id), 5000, ct);
    }

    /// <summary>The <see cref="Core.Entities.DriverSession.RevokedReason"/> AuthController gives a token it rotated.</summary>
    private const string RotatedReason = "rotated";

    /// <summary>Deletes in bounded chunks, so a first run on a large call log does not hold one huge transaction.</summary>
    private static async Task<int> DeleteInBatchesAsync<T>(IQueryable<T> due, int batch, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var n = await due.Take(batch).ExecuteDeleteAsync(ct);
            total += n;
            if (n < batch) return total;
        }
    }
}

public sealed record RetentionResult(int OtpCodes, int DeviceSessions, int ApiAuditLogs, int SignIns);

/// <summary>Bound from the "Retention" configuration section. Every period: 0 or less (EndedSessionDays: below 0) = keep that kind for good.</summary>
public class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>Local (Tbilisi) wall-clock time of the nightly pass, "HH:mm".</summary>
    public string RunAtLocalTime { get; set; } = "03:30";

    /// <summary>IANA or Windows id; Georgia has no DST.</summary>
    public string TimeZoneId { get; set; } = "Georgian Standard Time";

    /// <summary>One-time sign-in codes are deleted this many hours after they were issued (checked on every poll, not only nightly).</summary>
    public int SignInCodeHours { get; set; } = 24;

    /// <summary>A device session unused for this many days is deleted (the driver signs in with an SMS code again).</summary>
    public int DeviceIdleDays { get; set; } = 90;

    /// <summary>A session that ended — signed out, cut off or expired — is deleted this many days later (rotated tokens: see the class).</summary>
    public int EndedSessionDays { get; set; } = 1;

    /// <summary>The log of calls to Yandex and the banks (ApiAuditLogs) is kept this many months (Yandex may audit API use; ToS 3.7).</summary>
    public int CallLogMonths { get; set; } = 24;

    /// <summary>The sign-in history (DriverSignIns) is kept this many months.</summary>
    public int SignInHistoryMonths { get; set; } = 24;

    /// <summary>Rows deleted per statement.</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>
    /// How often the worker polls: each poll deletes the sign-in codes past <see cref="SignInCodeHours"/> and checks
    /// whether the nightly pass is due. A code therefore lives at most SignInCodeHours plus one interval.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 300;
    public int InitialDelaySeconds { get; set; } = 60;
}
