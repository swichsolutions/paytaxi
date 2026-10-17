using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;

namespace PayTaxi.Infrastructure.Services;

/// <summary>State of one SLA clock at a given moment.</summary>
public enum SlaState
{
    /// <summary>Running, deadline not reached.</summary>
    Pending,

    /// <summary>Satisfied on time.</summary>
    Met,

    /// <summary>Still open and past its deadline.</summary>
    Overdue,

    /// <summary>Satisfied, but after the deadline.</summary>
    Missed,

    /// <summary>Resolution of an external-cause incident: the clock waits for the external service (Annex 1 §3.2).</summary>
    Paused,

    /// <summary>P3 resolution: "next scheduled release" — no fixed deadline.</summary>
    NoTarget,
}

/// <summary>
/// One clock (response or resolution). <see cref="MarginMinutes"/> = due − (done ?? now): positive is time
/// left (or how early it was met), negative is how far past the deadline.
/// </summary>
public sealed record SlaClock(SlaState State, DateTime? StartsAt, DateTime? DueAt, DateTime? DoneAt, double? MarginMinutes)
{
    public bool IsBreach => State is SlaState.Overdue or SlaState.Missed;
}

public sealed record IncidentSlaResult(SlaClock Response, SlaClock Resolution);

/// <summary>
/// The SLA engine for Annex 1 §2–§3. Pure: no database, no clock — "now" is always passed in.
///
///   Level | response                                    | resolution
///   P1    | 4 h if reported 09:00–24:00, 12 h if 00:00–09:00 | 24 h (calendar; the platform runs 24/7)
///   P2    | 8 business hours                              | 3 business days (3 × the business day, in business hours)
///   P3    | end of the next business day                  | next scheduled release (no deadline)
///
/// Clock starts:
///   - Response: when the incident reached its current level (<see cref="LevelClockStart"/>) — its report
///     (for an auto incident, when the monitor detected it), or the moment it was raised to that level, the
///     first time if it was raised more than once. "Responded" = acknowledged, also before the raise; an
///     incident resolved before anyone acknowledged it counts as responded at its resolution (it no longer
///     needed a response).
///   - Resolution: the same start; for an external cause (bank, Yandex, park balance — §3.1) it starts at
///     <see cref="Incident.ExternalRestoredAt"/> (not before the level's start) and is Paused until then
///     (§3.2). An external incident resolved without a restoration time is treated as restored at its resolution.
///   - The level and fault are taken as they are now; the start of the level comes from
///     <see cref="Incident.ReachedP1At"/> / <see cref="Incident.ReachedP2At"/>.
///
/// All instants are UTC; the time-of-day rules use the policy's time zone (Tbilisi, no DST).
/// </summary>
public sealed class IncidentSla
{
    private static readonly DayOfWeek[] DefaultBusinessDays =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    private readonly IncidentSlaPolicy _p;
    private readonly TimeZoneInfo _tz;
    private readonly HashSet<DayOfWeek> _days;
    private readonly HashSet<DateOnly> _holidays;

    public IncidentSla(IncidentSlaPolicy policy, TimeZoneInfo tz)
    {
        if (policy.BusinessDayEnd <= policy.BusinessDayStart)
            throw new ArgumentException("Business day must end after it starts", nameof(policy));
        _p = policy;
        _tz = tz;
        _days = new HashSet<DayOfWeek>(policy.BusinessDays is { Count: > 0 } ? policy.BusinessDays : DefaultBusinessDays);
        _holidays = new HashSet<DateOnly>(policy.Holidays ?? new List<DateOnly>());
    }

    public static IncidentSla From(IncidentOptions o) => new(o.Sla, SettlementService.ResolveTimeZone(o.Sla.TimeZoneId));

    public TimeZoneInfo TimeZone => _tz;

    /// <summary>Annex 1 §3.1: bank API, Yandex Fleet, external infrastructure, insufficient park balance.</summary>
    public static bool IsExternalCause(IncidentFault f) => f is IncidentFault.External or IncidentFault.Park;

    /// <summary>
    /// When the clocks of the incident's CURRENT level start: the first time it reached that level (or a worse
    /// one), never before its report. A P1 reported as one counts from its report; a P2 raised to P1 counts
    /// from the raise (Annex 1 §2 — the P1 targets apply to a P1, not to the P2 it was before); a P1 lowered
    /// and raised again counts from the first time it was a P1, so a bounce cannot buy time. Rows without the
    /// level times (older than them) count from the report, as before.
    /// </summary>
    public static DateTime LevelClockStart(Incident i)
    {
        var reached = i.Level switch
        {
            IncidentLevel.P1 => i.ReachedP1At,
            IncidentLevel.P2 => i.ReachedP2At,
            _ => null,
        };
        return reached is { } at && Utc(at) > Utc(i.ReportedAt) ? Utc(at) : Utc(i.ReportedAt);
    }

    // ── Deadlines ────────────────────────────────────────────────────

    public DateTime ResponseDue(IncidentLevel level, DateTime reportedAtUtc) => level switch
    {
        IncidentLevel.P1 => Utc(reportedAtUtc).AddHours(InP1Night(reportedAtUtc) ? _p.P1ResponseNightHours : _p.P1ResponseHours),
        IncidentLevel.P2 => AddBusinessHours(reportedAtUtc, TimeSpan.FromHours(_p.P2ResponseBusinessHours)),
        IncidentLevel.P3 => EndOfNextBusinessDay(reportedAtUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>Null for P3 (next scheduled release — no fixed deadline).</summary>
    public DateTime? ResolutionDue(IncidentLevel level, DateTime clockStartUtc) => level switch
    {
        IncidentLevel.P1 => Utc(clockStartUtc).AddHours(_p.P1ResolutionHours),
        IncidentLevel.P2 => AddBusinessHours(clockStartUtc, (_p.BusinessDayEnd - _p.BusinessDayStart) * _p.P2ResolutionBusinessDays),
        IncidentLevel.P3 => null,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    // ── Evaluation ───────────────────────────────────────────────────

    public IncidentSlaResult Evaluate(Incident i, DateTime nowUtc)
    {
        // A response given before the incident reached this level still counts: the clock is met, early.
        var start = LevelClockStart(i);
        var response = Clock(start, ResponseDue(i.Level, start), i.AcknowledgedAt ?? i.ResolvedAt, nowUtc);

        SlaClock resolution;
        if (i.Level == IncidentLevel.P3)
        {
            resolution = new SlaClock(SlaState.NoTarget, start, null, i.ResolvedAt, null);
        }
        else if (IsExternalCause(i.Fault))
        {
            var restored = i.ExternalRestoredAt ?? i.ResolvedAt;
            // Restored before it became this level: the level's own target cannot start before the level did.
            var from = restored is { } r && Utc(r) > start ? Utc(r) : start;
            resolution = restored is null
                ? new SlaClock(SlaState.Paused, null, null, null, null)
                : Clock(from, ResolutionDue(i.Level, from)!.Value, i.ResolvedAt, nowUtc);
        }
        else
        {
            resolution = Clock(start, ResolutionDue(i.Level, start)!.Value, i.ResolvedAt, nowUtc);
        }

        return new IncidentSlaResult(response, resolution);
    }

    private static SlaClock Clock(DateTime start, DateTime due, DateTime? done, DateTime now)
    {
        var at = done ?? now;
        var margin = (Utc(due) - Utc(at)).TotalMinutes;
        var state = done is not null
            ? (Utc(done.Value) <= Utc(due) ? SlaState.Met : SlaState.Missed)
            : (Utc(now) <= Utc(due) ? SlaState.Pending : SlaState.Overdue);
        return new SlaClock(state, Utc(start), Utc(due), done is null ? null : Utc(done.Value), Math.Round(margin, 1));
    }

    // ── Calendar arithmetic ──────────────────────────────────────────

    public bool IsBusinessDay(DateOnly day) => _days.Contains(day.DayOfWeek) && !_holidays.Contains(day);

    /// <summary>
    /// Adds <paramref name="amount"/> of business time. Outside business hours the clock waits for the next
    /// opening; a deadline landing exactly on closing time stays there (it does not roll to the next morning).
    /// </summary>
    public DateTime AddBusinessHours(DateTime fromUtc, TimeSpan amount)
    {
        var local = ToLocal(fromUtc);
        var remaining = amount;
        // A year of days without a single business day means the configuration is broken, not that the
        // deadline is far away.
        for (var guard = 0; guard < 400; guard++)
        {
            var day = DateOnly.FromDateTime(local);
            var open = local.Date + _p.BusinessDayStart;
            var close = local.Date + _p.BusinessDayEnd;
            if (!IsBusinessDay(day) || local >= close)
            {
                local = local.Date.AddDays(1) + _p.BusinessDayStart;
                continue;
            }
            if (local < open) local = open;

            var available = close - local;
            if (remaining <= available) return ToUtc(local + remaining);
            remaining -= available;
            local = local.Date.AddDays(1) + _p.BusinessDayStart;
        }
        throw new InvalidOperationException("No business day within a year — check Incidents:Sla:BusinessDays / Holidays");
    }

    /// <summary>Closing time of the first business day after the (local) day of <paramref name="fromUtc"/>.</summary>
    public DateTime EndOfNextBusinessDay(DateTime fromUtc)
    {
        var day = DateOnly.FromDateTime(ToLocal(fromUtc)).AddDays(1);
        for (var guard = 0; guard < 400 && !IsBusinessDay(day); guard++) day = day.AddDays(1);
        if (!IsBusinessDay(day))
            throw new InvalidOperationException("No business day within a year — check Incidents:Sla:BusinessDays / Holidays");
        return ToUtc(day.ToDateTime(TimeOnly.MinValue) + _p.BusinessDayEnd);
    }

    private bool InP1Night(DateTime utc)
    {
        var t = ToLocal(utc).TimeOfDay;
        return _p.P1NightStart <= _p.P1NightEnd
            ? t >= _p.P1NightStart && t < _p.P1NightEnd
            : t >= _p.P1NightStart || t < _p.P1NightEnd; // a window that wraps midnight
    }

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), _tz);

    public DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _tz);

    /// <summary>Npgsql hands back Utc kinds; values built in tests may be Unspecified — both mean UTC here.</summary>
    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
