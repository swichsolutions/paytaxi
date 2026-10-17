using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;

namespace PayTaxi.Infrastructure.Services;

/// <summary>One owner-fault or otherwise contract-relevant incident, as the metrics cite it.</summary>
public sealed record IncidentCitation(Guid Id, string Ref, IncidentLevel Level, IncidentFault Fault, DateTime ReportedAt, DateTime? ResolvedAt, SlaState Resolution, double? Hours);

/// <summary>Annex 1 §5–§6 for one calendar month (Tbilisi).</summary>
public sealed record IncidentMonthMetrics(
    string Month,
    DateTime FromUtc,
    DateTime ToUtc,
    int P1Reported,
    int P2Reported,
    int P3Reported,
    int OwnerP1Count,
    IReadOnlyList<IncidentCitation> OwnerP1Missed,
    double OwnerP1Hours,
    int UndeterminedP1Count,
    bool ChronicByCount,
    bool ChronicByHours,
    IReadOnlyList<IncidentCitation> UnansweredP1)
{
    public bool ChronicBreach => ChronicByCount || ChronicByHours;
    public bool Abandonment => UnansweredP1.Count > 0;
}

/// <summary>Annex 1 §7 / agreement 8.5.1: the two-week trial from the launch date.</summary>
public sealed record TrialStatus(
    string State,                // not_set | upcoming | running | ended
    DateOnly? LaunchDate,
    int TrialDays,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int? Day,                    // 1-based day of the trial while running
    IReadOnlyList<IncidentCitation> Qualifying,
    int OwnerP1Open,             // owner-fault P1s of the trial still open on time — could still qualify
    IReadOnlyList<IncidentCitation> UndeterminedMissed,   // P1s with the fault not determined that missed their target
    int UndeterminedOpen)        // P1s with the fault not determined still open on time
{
    /// <summary>The log shows an owner-fault P1 not resolved within target: the licensee owes nothing if it withdraws.</summary>
    public bool LicenseeReleased => Qualifying.Count > 0;

    /// <summary>
    /// The release cannot be ruled out yet: a P1 whose fault nobody has established missed its target. If
    /// it was the owner's, it qualifies. The card must say so instead of a definite "none qualifies".
    /// </summary>
    public bool Undetermined => !LicenseeReleased && UndeterminedMissed.Count > 0;
}

/// <summary>
/// Contract metrics computed from the incident log. Pure, like <see cref="IncidentSla"/>.
///
/// Readings of Annex 1 (to confirm with the lawyer):
///   - "Owner's fault" = <see cref="IncidentFault.Platform"/>. Unknown fault counts as neither; it is reported
///     separately so an undetermined P1 cannot hide.
///   - A P1 belongs to the month in which it BECAME a P1 (<see cref="IncidentSla.LevelClockStart"/>): its
///     report, or the moment a P2 was raised to P1. The P1 targets and these figures count from there — a
///     P2 raised at hour 60 of a long wait is not a P1 that went unanswered for 60 hours.
///   - §5(a) counts owner-fault P1s of the month whose resolution clock is Missed, or Overdue (still open past
///     the target — the miss is already certain).
///   - §5(b) sums owner-fault P1 durations clipped to the month, so an incident spanning midnight on the 1st is
///     split between the two months. A P1 from its report counts from its impact start (which may be before
///     the report); a raised one from the raise. "Exceeds" = strictly more.
///   - §6(a): any P1 (whatever the fault — §3.1 obliges the owner to respond to external incidents too) of the
///     month whose response took, or has been waiting, more than the threshold since it became a P1. A
///     response given while it was still a P2 counts.
///   - §7: an owner-fault P1 of the trial window whose resolution is Missed or Overdue. P1s of the window whose
///     fault is still Unknown are cited apart (missed / still open): whether the licensee owes the 10,000 GEL
///     (8.5.1) may depend on them, and only Swich can set the fault.
/// </summary>
public static class IncidentMetrics
{
    public static IncidentMonthMetrics ForMonth(
        IEnumerable<Incident> incidents, int year, int month, DateTime nowUtc, IncidentSla sla, IncidentOptions opts)
    {
        var fromUtc = sla.ToUtc(new DateTime(year, month, 1));
        var toUtc = sla.ToUtc(new DateTime(year, month, 1).AddMonths(1));
        var all = incidents.ToList();
        var reported = all.Where(i => i.ReportedAt >= fromUtc && i.ReportedAt < toUtc).ToList();
        var p1 = all
            .Where(i => i.Level == IncidentLevel.P1)
            .Select(i => (i, start: IncidentSla.LevelClockStart(i)))
            .Where(x => x.start >= fromUtc && x.start < toUtc)
            .ToList();

        var ownerP1 = p1.Where(x => x.i.Fault == IncidentFault.Platform).Select(x => x.i).ToList();
        var missed = ownerP1
            .Select(i => (i, sla: sla.Evaluate(i, nowUtc)))
            .Where(x => x.sla.Resolution.IsBreach)
            .Select(x => Cite(x.i, x.sla.Resolution.State, null))
            .ToList();

        // Duration counts for every owner-fault P1 that overlaps the month, wherever it was reported.
        var windowEnd = nowUtc < toUtc ? nowUtc : toUtc;
        var hours = all
            .Where(i => i.Level == IncidentLevel.P1 && i.Fault == IncidentFault.Platform)
            .Sum(i => Overlap(P1HoursFrom(i), i.ResolvedAt ?? nowUtc, fromUtc, windowEnd).TotalHours);

        var unanswered = p1
            .Select(x => (x.i, waited: ((x.i.AcknowledgedAt ?? x.i.ResolvedAt ?? nowUtc) - x.start).TotalHours))
            .Where(x => x.waited > opts.AbandonmentResponseHours)
            .Select(x => Cite(x.i, sla.Evaluate(x.i, nowUtc).Resolution.State, Math.Round(x.waited, 1)))
            .ToList();

        return new IncidentMonthMetrics(
            Month: $"{year:0000}-{month:00}",
            FromUtc: fromUtc,
            ToUtc: toUtc,
            P1Reported: p1.Count,
            P2Reported: reported.Count(i => i.Level == IncidentLevel.P2),
            P3Reported: reported.Count(i => i.Level == IncidentLevel.P3),
            OwnerP1Count: ownerP1.Count,
            OwnerP1Missed: missed,
            OwnerP1Hours: Math.Round(hours, 2),
            UndeterminedP1Count: p1.Count(x => x.i.Fault == IncidentFault.Unknown),
            ChronicByCount: missed.Count >= opts.ChronicBreachP1Count,
            ChronicByHours: hours > opts.ChronicBreachP1Hours,
            UnansweredP1: unanswered);
    }

    /// <summary>
    /// Where a P1's §5(b) hours start: its impact start when it was a P1 from the report (the impact may have
    /// begun before anyone reported it), the raise when it became one later — the hours before were P2 hours.
    /// </summary>
    public static DateTime P1HoursFrom(Incident i)
    {
        var start = IncidentSla.LevelClockStart(i);
        return start > i.ReportedAt ? start : i.StartedAt;
    }

    public static TrialStatus Trial(
        IEnumerable<Incident> incidents, DateOnly? launchDate, int trialDays, DateTime nowUtc, IncidentSla sla)
    {
        if (launchDate is null)
            return new TrialStatus("not_set", null, trialDays, null, null, null, Array.Empty<IncidentCitation>(), 0, Array.Empty<IncidentCitation>(), 0);

        var fromUtc = sla.ToUtc(launchDate.Value.ToDateTime(TimeOnly.MinValue));
        var toUtc = sla.ToUtc(launchDate.Value.AddDays(trialDays).ToDateTime(TimeOnly.MinValue));
        var state = nowUtc < fromUtc ? "upcoming" : nowUtc < toUtc ? "running" : "ended";
        int? day = state == "running" ? (int)Math.Floor((nowUtc - fromUtc).TotalDays) + 1 : null;

        var trialP1 = incidents
            .Where(i => i.Level == IncidentLevel.P1 && i.Fault is IncidentFault.Platform or IncidentFault.Unknown)
            .Where(i => IncidentSla.LevelClockStart(i) is var start && start >= fromUtc && start < toUtc)
            .Select(i => (i, sla: sla.Evaluate(i, nowUtc)))
            .ToList();
        var ownerP1 = trialP1.Where(x => x.i.Fault == IncidentFault.Platform).ToList();
        var qualifying = ownerP1.Where(x => x.sla.Resolution.IsBreach)
            .Select(x => Cite(x.i, x.sla.Resolution.State, null)).ToList();
        var stillOpen = ownerP1.Count(x => x.sla.Resolution.State == SlaState.Pending);

        // Unknown fault: the SLA engine runs its resolution clock from the report, as for the owner's own,
        // so "missed" here is exactly what it would be if Swich set the fault to Platform.
        var undetermined = trialP1.Where(x => x.i.Fault == IncidentFault.Unknown).ToList();
        var undeterminedMissed = undetermined.Where(x => x.sla.Resolution.IsBreach)
            .Select(x => Cite(x.i, x.sla.Resolution.State, null)).ToList();
        var undeterminedOpen = undetermined.Count(x => x.sla.Resolution.State == SlaState.Pending);

        return new TrialStatus(state, launchDate, trialDays, fromUtc, toUtc, day, qualifying, stillOpen, undeterminedMissed, undeterminedOpen);
    }

    /// <summary>The month bounds a list query or the metrics endpoint should load incidents for (reported in it, or overlapping it).</summary>
    public static (DateTime FromUtc, DateTime ToUtc) MonthBounds(int year, int month, IncidentSla sla) =>
        (sla.ToUtc(new DateTime(year, month, 1)), sla.ToUtc(new DateTime(year, month, 1).AddMonths(1)));

    private static TimeSpan Overlap(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo)
    {
        var from = aFrom > bFrom ? aFrom : bFrom;
        var to = aTo < bTo ? aTo : bTo;
        return to > from ? to - from : TimeSpan.Zero;
    }

    private static IncidentCitation Cite(Incident i, SlaState resolution, double? hours) =>
        new(i.Id, i.Ref, i.Level, i.Fault, i.ReportedAt, i.ResolvedAt, resolution, hours);
}
