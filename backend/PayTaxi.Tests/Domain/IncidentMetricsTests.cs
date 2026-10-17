using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Domain;

/// <summary>Annex 1 §5 (chronic breach), §6(a) (abandonment) and §7 (trial period) computed from the log.</summary>
public class IncidentMetricsTests
{
    private static readonly TimeZoneInfo Tbilisi = TimeZoneInfo.CreateCustomTimeZone("Tbilisi-test", TimeSpan.FromHours(4), "Tbilisi", "Tbilisi");
    private static readonly IncidentSla Sla = new(new IncidentSlaPolicy(), Tbilisi);
    private static readonly IncidentOptions Opts = new();

    private static DateTime L(int month, int day, int hour) =>
        DateTime.SpecifyKind(new DateTime(2026, month, day, hour, 0, 0).AddHours(-4), DateTimeKind.Utc);

    private static int _n;

    /// <summary>A P1 reported at <paramref name="reported"/>, acknowledged an hour later, resolved after <paramref name="hours"/> (null = still open).</summary>
    private static Incident P1(DateTime reported, double? hours, IncidentFault fault = IncidentFault.Platform, bool ack = true) => new()
    {
        Number = ++_n, Title = "t", ReportedBy = "admin:t", Level = IncidentLevel.P1, Fault = fault,
        StartedAt = reported, ReportedAt = reported,
        AcknowledgedAt = ack ? reported.AddHours(1) : null,
        ResolvedAt = hours is null ? null : reported.AddHours(hours.Value),
        Status = hours is null ? (ack ? IncidentStatus.Acknowledged : IncidentStatus.Reported) : IncidentStatus.Resolved,
    };

    private static readonly DateTime EndOfOctober = L(11, 5, 12);

    [Fact]
    public void Three_owner_fault_P1s_over_target_in_a_month_is_a_chronic_breach_two_is_not()
    {
        var log = new List<Incident>
        {
            P1(L(10, 2, 10), 30),                                   // missed (24 h target)
            P1(L(10, 9, 10), 26),                                   // missed
            P1(L(10, 20, 10), 5),                                   // on time
            P1(L(10, 21, 10), 40, IncidentFault.External),          // external: never counts
            P1(L(10, 22, 10), 40, IncidentFault.Unknown),           // undetermined: reported apart
        };
        var m = IncidentMetrics.ForMonth(log, 2026, 10, EndOfOctober, Sla, Opts);
        Assert.Equal(2, m.OwnerP1Missed.Count);
        Assert.False(m.ChronicByCount);
        Assert.Equal(3, m.OwnerP1Count);
        Assert.Equal(1, m.UndeterminedP1Count);
        Assert.Equal(5, m.P1Reported);

        // A third miss — still open past its target counts already.
        log.Add(P1(L(10, 28, 10), null));
        m = IncidentMetrics.ForMonth(log, 2026, 10, EndOfOctober, Sla, Opts);
        Assert.Equal(3, m.OwnerP1Missed.Count);
        Assert.True(m.ChronicByCount);
        Assert.True(m.ChronicBreach);
        Assert.Contains(m.OwnerP1Missed, c => c.Resolution == SlaState.Overdue);

        // The threshold is configuration ([3] is still negotiable).
        var four = new IncidentOptions { ChronicBreachP1Count = 4 };
        Assert.False(IncidentMetrics.ForMonth(log, 2026, 10, EndOfOctober, Sla, four).ChronicByCount);
    }

    [Fact]
    public void Owner_fault_P1_hours_over_48_is_a_chronic_breach_and_are_clipped_to_the_month()
    {
        var log = new List<Incident>
        {
            P1(L(10, 5, 0), 20),                                    // 20 h
            P1(L(10, 12, 0), 20),                                   // 20 h → 40 h
            P1(L(10, 31, 20), 10),                                  // 31st 20:00 → 1 Nov 06:00: 4 h in October, 6 h in November
            P1(L(10, 15, 0), 30, IncidentFault.External),           // ignored
        };
        var oct = IncidentMetrics.ForMonth(log, 2026, 10, EndOfOctober, Sla, Opts);
        Assert.Equal(44, oct.OwnerP1Hours, 3);
        Assert.False(oct.ChronicByHours);

        var nov = IncidentMetrics.ForMonth(log, 2026, 11, EndOfOctober.AddDays(30), Sla, Opts);
        Assert.Equal(6, nov.OwnerP1Hours, 3);
        Assert.Equal(0, nov.P1Reported);                            // reported in October

        log.Add(P1(L(10, 20, 0), 5));                               // 49 h: "exceeds 48"
        Assert.True(IncidentMetrics.ForMonth(log, 2026, 10, EndOfOctober, Sla, Opts).ChronicByHours);

        // An open incident counts up to now (here: the 6th of October 12:00 — 36 h into a P1 from the 5th 00:00).
        var open = new List<Incident> { P1(L(10, 5, 0), null) };
        Assert.Equal(36, IncidentMetrics.ForMonth(open, 2026, 10, L(10, 6, 12), Sla, Opts).OwnerP1Hours, 3);
    }

    [Fact]
    public void A_P1_without_a_response_for_more_than_72_hours_raises_abandonment_whatever_the_fault()
    {
        var unanswered = P1(L(10, 3, 10), null, IncidentFault.External, ack: false);
        var answeredLate = P1(L(10, 10, 10), 100, ack: false);      // resolved after 100 h, never acknowledged
        answeredLate.AcknowledgedAt = L(10, 13, 9);                 // 71 h — within 72
        var log = new List<Incident> { unanswered, answeredLate };

        var early = IncidentMetrics.ForMonth(log, 2026, 10, L(10, 6, 9), Sla, Opts);   // 71 h after the first
        Assert.False(early.Abandonment);

        var m = IncidentMetrics.ForMonth(log, 2026, 10, L(10, 6, 11), Sla, Opts);      // 73 h
        Assert.True(m.Abandonment);
        var cited = Assert.Single(m.UnansweredP1);
        Assert.Equal(unanswered.Id, cited.Id);
        Assert.Equal(73, cited.Hours);
    }

    /// <summary>
    /// A P2 raised to P1 late — the monitor at hour 60 of a long wait, or Swich by hand — is a P1 from that
    /// moment: §6(a) waits 72 hours from it, §5(b) counts the hours it was a P1, and it belongs to the month
    /// it became one. A response given while it was still a P2 counts.
    /// </summary>
    [Fact]
    public void An_escalated_P1_counts_for_abandonment_hours_and_its_month_from_the_moment_it_became_a_P1()
    {
        var late = new Incident
        {
            Number = ++_n, Title = "t", ReportedBy = "system", Fault = IncidentFault.Platform, Status = IncidentStatus.Reported,
            StartedAt = L(10, 3, 9), ReportedAt = L(10, 3, 10),
        };
        late.SetLevel(IncidentLevel.P2, late.ReportedAt);
        late.SetLevel(IncidentLevel.P1, L(10, 5, 22));               // 60 h after the report, nobody has answered yet
        var log = new List<Incident> { late };

        // 72 h after the report, 12 h after it became a P1: not abandoned, 12 owner-fault P1 hours.
        var m = IncidentMetrics.ForMonth(log, 2026, 10, L(10, 6, 10), Sla, Opts);
        Assert.False(m.Abandonment);
        Assert.Equal(12, m.OwnerP1Hours, 3);
        Assert.Equal(1, m.P1Reported);

        // 73 h after the escalation: unanswered P1.
        var cited = Assert.Single(IncidentMetrics.ForMonth(log, 2026, 10, L(10, 8, 23), Sla, Opts).UnansweredP1);
        Assert.Equal(73, cited.Hours);

        // Answered while it was still a P2: never unanswered.
        late.AcknowledgedAt = L(10, 4, 9);
        late.Status = IncidentStatus.Acknowledged;
        Assert.False(IncidentMetrics.ForMonth(log, 2026, 10, L(10, 9, 0), Sla, Opts).Abandonment);

        // Reported on 30 September as a P2, a P1 from 1 October 02:00, fixed 30 h later: an October P1 that missed its target.
        var overMonthEnd = new Incident
        {
            Number = ++_n, Title = "t", ReportedBy = "system", Fault = IncidentFault.Platform, Status = IncidentStatus.Resolved,
            StartedAt = L(9, 30, 20), ReportedAt = L(9, 30, 20), AcknowledgedAt = L(9, 30, 21), ResolvedAt = L(10, 2, 8),
        };
        overMonthEnd.SetLevel(IncidentLevel.P2, overMonthEnd.ReportedAt);
        overMonthEnd.SetLevel(IncidentLevel.P1, L(10, 1, 2));
        var both = new List<Incident> { overMonthEnd };
        Assert.Equal(0, IncidentMetrics.ForMonth(both, 2026, 9, L(10, 5, 0), Sla, Opts).P1Reported);
        var oct = IncidentMetrics.ForMonth(both, 2026, 10, L(10, 5, 0), Sla, Opts);
        Assert.Equal(1, oct.P1Reported);
        Assert.Equal(SlaState.Missed, Assert.Single(oct.OwnerP1Missed).Resolution);
        Assert.Equal(30, oct.OwnerP1Hours, 3);                       // 1 Oct 02:00 → 2 Oct 08:00
        Assert.Equal(0, IncidentMetrics.ForMonth(both, 2026, 9, L(10, 5, 0), Sla, Opts).OwnerP1Hours, 3);
    }

    [Fact]
    public void Trial_period_releases_the_licensee_only_for_an_owner_fault_P1_missed_inside_the_window()
    {
        var launch = new DateOnly(2026, 10, 5);                     // window: 5th 00:00 → 19th 00:00 Tbilisi

        Assert.Equal("not_set", IncidentMetrics.Trial(Array.Empty<Incident>(), null, 14, L(10, 6, 0), Sla).State);
        Assert.Equal("upcoming", IncidentMetrics.Trial(Array.Empty<Incident>(), launch, 14, L(10, 4, 23), Sla).State);

        var log = new List<Incident>
        {
            P1(L(10, 6, 10), 5),                                    // fixed on time
            P1(L(10, 7, 10), 40, IncidentFault.External),           // external: not the owner's
            P1(L(10, 4, 10), 40),                                   // missed, but before launch
        };
        var running = IncidentMetrics.Trial(log, launch, 14, L(10, 8, 12), Sla);
        Assert.Equal("running", running.State);
        Assert.Equal(4, running.Day);
        Assert.False(running.LicenseeReleased);

        log.Add(P1(L(10, 18, 10), null));                           // owner fault, open, target the 19th 10:00
        var stillOpen = IncidentMetrics.Trial(log, launch, 14, L(10, 18, 20), Sla);
        Assert.False(stillOpen.LicenseeReleased);
        Assert.Equal(1, stillOpen.OwnerP1Open);

        var after = IncidentMetrics.Trial(log, launch, 14, L(10, 19, 11), Sla);         // its target passed, trial over
        Assert.Equal("ended", after.State);
        Assert.True(after.LicenseeReleased);
        Assert.Single(after.Qualifying);
    }

    [Fact]
    public void Trial_shows_a_P1_whose_fault_is_not_determined_instead_of_claiming_none_qualifies()
    {
        var launch = new DateOnly(2026, 10, 5);
        // The licensee reports a P1 on day 3; it takes 30 h; Swich never sets the fault.
        var undetermined = P1(L(10, 7, 10), 30, IncidentFault.Unknown);
        var stillOpen = P1(L(10, 17, 10), null, IncidentFault.Unknown);        // target the 18th 10:00
        var log = new List<Incident> { undetermined, stillOpen, P1(L(10, 3, 10), 40, IncidentFault.Unknown) /* before launch */ };

        var t = IncidentMetrics.Trial(log, launch, 14, L(10, 17, 20), Sla);
        Assert.False(t.LicenseeReleased);                                     // not established either way…
        var cited = Assert.Single(t.UndeterminedMissed);                      // …but it is on the card
        Assert.Equal(undetermined.Id, cited.Id);
        Assert.Equal(SlaState.Missed, cited.Resolution);
        Assert.Equal(1, t.UndeterminedOpen);
        Assert.Empty(t.Qualifying);

        // Swich establishes it was the owner's fault: it now qualifies and is no longer "undetermined".
        undetermined.Fault = IncidentFault.Platform;
        var settled = IncidentMetrics.Trial(log, launch, 14, L(10, 17, 20), Sla);
        Assert.True(settled.LicenseeReleased);
        Assert.Empty(settled.UndeterminedMissed);
    }
}
