using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Domain;

/// <summary>
/// Annex 1 §2–§3 response / resolution clocks. Tbilisi is a fixed UTC+4 zone here so the tests do not
/// depend on the host's time-zone database. October 2026: the 12th and 19th are Mondays, the 16th a Friday.
/// </summary>
public class IncidentSlaTests
{
    private static readonly TimeZoneInfo Tbilisi = TimeZoneInfo.CreateCustomTimeZone("Tbilisi-test", TimeSpan.FromHours(4), "Tbilisi", "Tbilisi");
    private static IncidentSla Sla(IncidentSlaPolicy? p = null) => new(p ?? new IncidentSlaPolicy(), Tbilisi);

    /// <summary>A Tbilisi wall-clock time as the UTC instant the database would hold.</summary>
    private static DateTime L(int day, int hour, int minute = 0, int month = 10) =>
        DateTime.SpecifyKind(new DateTime(2026, month, day, hour, minute, 0).AddHours(-4), DateTimeKind.Utc);

    private static Incident Inc(IncidentLevel level, DateTime reported, IncidentFault fault = IncidentFault.Platform) => new()
    {
        Number = 1, Title = "t", ReportedBy = "admin:test", Level = level, Fault = fault,
        StartedAt = reported, ReportedAt = reported,
    };

    // ── Response deadlines ───────────────────────────────────────────

    [Fact]
    public void P1_reported_at_night_gets_twelve_hours()
    {
        Assert.Equal(L(13, 20), Sla().ResponseDue(IncidentLevel.P1, L(13, 8)));
        Assert.Equal(L(13, 12), Sla().ResponseDue(IncidentLevel.P1, L(13, 0)));     // midnight is night
        Assert.Equal(L(13, 20, 59), Sla().ResponseDue(IncidentLevel.P1, L(13, 8, 59)));
    }

    [Fact]
    public void P1_reported_in_the_day_window_gets_four_hours_even_late_in_the_evening()
    {
        Assert.Equal(L(13, 14), Sla().ResponseDue(IncidentLevel.P1, L(13, 10)));
        Assert.Equal(L(13, 13), Sla().ResponseDue(IncidentLevel.P1, L(13, 9)));      // 09:00 is day
        Assert.Equal(L(14, 3, 30), Sla().ResponseDue(IncidentLevel.P1, L(13, 23, 30)));
        // Weekends do not matter for P1 (24/7).
        Assert.Equal(L(17, 15), Sla().ResponseDue(IncidentLevel.P1, L(17, 11)));
    }

    [Fact]
    public void P2_response_is_eight_business_hours_and_waits_over_the_weekend()
    {
        // Friday 15:00 → 3 h on Friday, 5 h on Monday.
        Assert.Equal(L(19, 14), Sla().ResponseDue(IncidentLevel.P2, L(16, 15)));
        // Saturday: the clock starts Monday 09:00.
        Assert.Equal(L(19, 17), Sla().ResponseDue(IncidentLevel.P2, L(17, 11)));
        // Monday 07:00, before opening: 09:00–17:00 the same day.
        Assert.Equal(L(12, 17), Sla().ResponseDue(IncidentLevel.P2, L(12, 7)));
        // Monday 20:00, after closing: Tuesday 09:00–17:00.
        Assert.Equal(L(13, 17), Sla().ResponseDue(IncidentLevel.P2, L(12, 20)));
        // Exactly at opening on a Monday, 8 h fit the day.
        Assert.Equal(L(12, 17), Sla().ResponseDue(IncidentLevel.P2, L(12, 9)));
    }

    [Fact]
    public void P3_response_is_the_end_of_the_next_business_day()
    {
        Assert.Equal(L(13, 18), Sla().ResponseDue(IncidentLevel.P3, L(12, 10)));     // Mon → Tue 18:00
        Assert.Equal(L(19, 18), Sla().ResponseDue(IncidentLevel.P3, L(16, 10)));     // Fri → Mon 18:00
        Assert.Equal(L(19, 18), Sla().ResponseDue(IncidentLevel.P3, L(17, 23)));     // Sat → Mon 18:00
    }

    // ── Resolution deadlines ─────────────────────────────────────────

    [Fact]
    public void P1_resolution_is_24_calendar_hours_P2_three_business_days_P3_none()
    {
        Assert.Equal(L(14, 10), Sla().ResolutionDue(IncidentLevel.P1, L(13, 10)));
        // Monday 14:00 + 3 × 9 business hours = Thursday 14:00.
        Assert.Equal(L(15, 14), Sla().ResolutionDue(IncidentLevel.P2, L(12, 14)));
        // Friday 15:00: Fri 3 h + Mon 9 + Tue 9 + Wed 6 = Wednesday 15:00.
        Assert.Equal(L(21, 15), Sla().ResolutionDue(IncidentLevel.P2, L(16, 15)));
        // Monday 20:00: Tue, Wed, Thu in full — the deadline stays on Thursday's closing time.
        Assert.Equal(L(15, 18), Sla().ResolutionDue(IncidentLevel.P2, L(12, 20)));
        Assert.Null(Sla().ResolutionDue(IncidentLevel.P3, L(12, 14)));
    }

    [Fact]
    public void Holidays_and_business_hours_come_from_the_policy()
    {
        var policy = new IncidentSlaPolicy { Holidays = { new DateOnly(2026, 10, 19) } }; // Monday off
        Assert.Equal(L(20, 14), Sla(policy).ResponseDue(IncidentLevel.P2, L(16, 15)));   // Fri 3 h + Tue 5 h

        var sixDays = new IncidentSlaPolicy { BusinessDays = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday } };
        Assert.Equal(L(17, 14), Sla(sixDays).ResponseDue(IncidentLevel.P2, L(16, 15)));  // Saturday works

        var broken = new IncidentSlaPolicy { BusinessDayStart = TimeSpan.FromHours(18), BusinessDayEnd = TimeSpan.FromHours(9) };
        Assert.Throws<ArgumentException>(() => Sla(broken));
    }

    // ── Evaluation ───────────────────────────────────────────────────

    [Fact]
    public void Response_is_met_by_acknowledging_in_time_and_missed_when_late()
    {
        var i = Inc(IncidentLevel.P1, L(13, 10));                    // due 14:00
        var open = Sla().Evaluate(i, L(13, 12));
        Assert.Equal(SlaState.Pending, open.Response.State);
        Assert.Equal(120, open.Response.MarginMinutes);

        Assert.Equal(SlaState.Overdue, Sla().Evaluate(i, L(13, 15)).Response.State);
        Assert.Equal(-60, Sla().Evaluate(i, L(13, 15)).Response.MarginMinutes);

        i.AcknowledgedAt = L(13, 13, 30);
        Assert.Equal(SlaState.Met, Sla().Evaluate(i, L(13, 20)).Response.State);
        i.AcknowledgedAt = L(13, 15);
        var late = Sla().Evaluate(i, L(13, 20));
        Assert.Equal(SlaState.Missed, late.Response.State);
        Assert.True(late.Response.IsBreach);
    }

    [Fact]
    public void Resolved_before_anyone_acknowledged_counts_as_responded_at_resolution()
    {
        var i = Inc(IncidentLevel.P1, L(13, 10));
        i.Status = IncidentStatus.Resolved;
        i.ResolvedAt = L(13, 10, 20);                                // e.g. the monitor closed it after 20 min
        var r = Sla().Evaluate(i, L(13, 20));
        Assert.Equal(SlaState.Met, r.Response.State);
        Assert.Equal(L(13, 10, 20), r.Response.DoneAt);
        Assert.Equal(SlaState.Met, r.Resolution.State);
    }

    [Fact]
    public void Owner_fault_P1_resolution_runs_from_the_report()
    {
        var i = Inc(IncidentLevel.P1, L(13, 10));                    // due 14th 10:00
        Assert.Equal(SlaState.Pending, Sla().Evaluate(i, L(14, 9)).Resolution.State);
        Assert.Equal(SlaState.Overdue, Sla().Evaluate(i, L(14, 11)).Resolution.State);
        i.ResolvedAt = L(14, 9, 59);
        Assert.Equal(SlaState.Met, Sla().Evaluate(i, L(15, 0)).Resolution.State);
        i.ResolvedAt = L(14, 10, 1);
        Assert.Equal(SlaState.Missed, Sla().Evaluate(i, L(15, 0)).Resolution.State);
    }

    [Fact]
    public void External_cause_pauses_the_resolution_clock_until_restored_then_gives_24_hours()
    {
        var i = Inc(IncidentLevel.P1, L(13, 10), IncidentFault.External);
        i.AcknowledgedAt = L(13, 11);

        // Bank down for 30 hours: no resolution clock, and the response was on time.
        var waiting = Sla().Evaluate(i, L(14, 16));
        Assert.Equal(SlaState.Paused, waiting.Resolution.State);
        Assert.Null(waiting.Resolution.DueAt);
        Assert.Equal(SlaState.Met, waiting.Response.State);

        // Back on the 14th 16:00 → the queue must be worked off by the 15th 16:00.
        i.ExternalRestoredAt = L(14, 16);
        var running = Sla().Evaluate(i, L(14, 20));
        Assert.Equal(SlaState.Pending, running.Resolution.State);
        Assert.Equal(L(14, 16), running.Resolution.StartsAt);
        Assert.Equal(L(15, 16), running.Resolution.DueAt);

        i.ResolvedAt = L(15, 2);                                     // 40 h after the report, 10 h after restoration
        Assert.Equal(SlaState.Met, Sla().Evaluate(i, L(16, 0)).Resolution.State);
        i.ResolvedAt = L(15, 17);
        Assert.Equal(SlaState.Missed, Sla().Evaluate(i, L(16, 0)).Resolution.State);
    }

    [Fact]
    public void Park_side_cause_is_external_too_and_resolving_without_a_restore_time_is_met()
    {
        // Annex 1 §3.1 names the park's insufficient balance among the causes outside the owner's control.
        Assert.True(IncidentSla.IsExternalCause(IncidentFault.Park));
        Assert.False(IncidentSla.IsExternalCause(IncidentFault.Unknown));

        var i = Inc(IncidentLevel.P2, L(12, 10), IncidentFault.Park);
        Assert.Equal(SlaState.Paused, Sla().Evaluate(i, L(20, 10)).Resolution.State);
        i.ResolvedAt = L(20, 11);
        Assert.Equal(SlaState.Met, Sla().Evaluate(i, L(20, 12)).Resolution.State);
    }

    [Fact]
    public void P3_has_no_resolution_target()
    {
        var i = Inc(IncidentLevel.P3, L(12, 10));
        var r = Sla().Evaluate(i, L(30, 10));
        Assert.Equal(SlaState.NoTarget, r.Resolution.State);
        Assert.False(r.Resolution.IsBreach);
        Assert.Equal(SlaState.Overdue, r.Response.State);            // the response still has one
    }

    // ── Level changes: the clocks of a level start when the incident reached it ──

    /// <summary>As the monitor or Swich leaves it: reported at one level, raised later.</summary>
    private static Incident Raised(IncidentLevel from, DateTime reported, IncidentLevel to, DateTime raisedAt, IncidentFault fault = IncidentFault.Platform)
    {
        var i = Inc(from, reported, fault);
        i.SetLevel(from, reported);
        i.SetLevel(to, raisedAt);
        return i;
    }

    [Fact]
    public void A_P2_raised_to_P1_late_gets_its_P1_clocks_from_the_moment_it_became_P1()
    {
        // Reported Friday 15:00 as a P2 (one driver waiting); raised to P1 at 03:00 on Sunday, 60 hours later.
        var i = Raised(IncidentLevel.P2, L(16, 15), IncidentLevel.P1, L(19, 3));
        var r = Sla().Evaluate(i, L(19, 4));

        // Not "56 hours overdue" — it has been a P1 for one hour. Raised in the night window: 12 h to respond.
        Assert.Equal(L(19, 3), r.Response.StartsAt);
        Assert.Equal(L(19, 15), r.Response.DueAt);
        Assert.Equal(SlaState.Pending, r.Response.State);
        Assert.Equal(L(19, 3), r.Resolution.StartsAt);
        Assert.Equal(L(20, 3), r.Resolution.DueAt);                  // 24 h from the escalation
        Assert.Equal(SlaState.Pending, r.Resolution.State);

        // The P1 clocks still run out like any other.
        Assert.Equal(SlaState.Overdue, Sla().Evaluate(i, L(19, 16)).Response.State);
    }

    [Fact]
    public void A_response_given_before_the_escalation_still_counts()
    {
        var i = Raised(IncidentLevel.P2, L(13, 10), IncidentLevel.P1, L(14, 20));
        i.AcknowledgedAt = L(13, 11);                                // Swich answered the P2 the same morning
        i.Status = IncidentStatus.Acknowledged;

        var r = Sla().Evaluate(i, L(15, 2));
        Assert.Equal(SlaState.Met, r.Response.State);
        Assert.Equal(L(13, 11), r.Response.DoneAt);
        Assert.False(r.Response.IsBreach);
    }

    [Fact]
    public void Lowering_a_P1_and_raising_it_again_counts_from_the_first_time_it_was_a_P1()
    {
        // Reported as a P1 at 10:00, lowered to P2 at 13:00, raised again at 20:00: the deadlines stay those of 10:00.
        var reportedP1 = Raised(IncidentLevel.P1, L(13, 10), IncidentLevel.P2, L(13, 13));
        reportedP1.SetLevel(IncidentLevel.P1, L(13, 20));
        var r = Sla().Evaluate(reportedP1, L(13, 21));
        Assert.Equal(L(13, 14), r.Response.DueAt);
        Assert.Equal(SlaState.Overdue, r.Response.State);
        Assert.Equal(L(14, 10), r.Resolution.DueAt);

        // Raised at 12:00, lowered at 13:00, raised again at 20:00: from 12:00.
        var bounced = Raised(IncidentLevel.P2, L(13, 10), IncidentLevel.P1, L(13, 12));
        bounced.SetLevel(IncidentLevel.P2, L(13, 13));
        bounced.SetLevel(IncidentLevel.P1, L(13, 20));
        Assert.Equal(L(13, 12), bounced.ReachedP1At);
        Assert.Equal(L(13, 12), Sla().Evaluate(bounced, L(13, 21)).Response.StartsAt);
        Assert.Equal(L(14, 12), Sla().Evaluate(bounced, L(13, 21)).Resolution.DueAt);

        // While it is a P2 again, the P2 clocks run from its report (it has been a P2 or worse since then).
        bounced.SetLevel(IncidentLevel.P2, L(13, 22));
        Assert.Equal(L(13, 10), Sla().Evaluate(bounced, L(13, 23)).Response.StartsAt);
    }

    [Fact]
    public void A_P3_raised_to_P2_gets_its_P2_clocks_from_the_raise()
    {
        // Reported Monday 10:00 as a P3, raised Wednesday 10:00: 8 business hours from Wednesday 10:00.
        var i = Raised(IncidentLevel.P3, L(12, 10), IncidentLevel.P2, L(14, 10));
        var r = Sla().Evaluate(i, L(14, 11));
        Assert.Equal(L(14, 18), r.Response.DueAt);
        Assert.Equal(SlaState.Pending, r.Response.State);
    }

    [Fact]
    public void An_external_restoration_before_the_escalation_starts_the_P1_resolution_clock_at_the_escalation()
    {
        var i = Raised(IncidentLevel.P2, L(13, 10), IncidentLevel.P1, L(13, 14), IncidentFault.External);
        i.ExternalRestoredAt = L(13, 12);
        var r = Sla().Evaluate(i, L(13, 15));
        Assert.Equal(L(13, 14), r.Resolution.StartsAt);
        Assert.Equal(L(14, 14), r.Resolution.DueAt);
    }

    [Fact]
    public void A_row_without_the_level_times_counts_from_its_report()
    {
        // Incidents written before ReachedP1At / ReachedP2At existed (and test rows) keep the old rule.
        var i = Inc(IncidentLevel.P1, L(13, 10));
        Assert.Null(i.ReachedP1At);
        Assert.Equal(L(13, 10), IncidentSla.LevelClockStart(i));
        Assert.Equal(L(13, 14), Sla().Evaluate(i, L(13, 11)).Response.DueAt);
    }

    [Fact]
    public void A_row_without_the_level_times_keeps_its_report_clocks_through_a_bounce()
    {
        // Such a row has been at its level since its report. Lowered at 13:00 and raised again at 20:00, it is still the
        // P1 of 10:00 — the bounce must not hand it fresh P1 clocks (nor fresh P2 ones while it is lowered).
        var i = Inc(IncidentLevel.P1, L(13, 10));
        i.SetLevel(IncidentLevel.P2, L(13, 13));
        Assert.Equal(L(13, 10), IncidentSla.LevelClockStart(i));
        i.SetLevel(IncidentLevel.P1, L(13, 20));
        Assert.Equal(L(13, 10), IncidentSla.LevelClockStart(i));
        Assert.Equal(L(13, 14), Sla().Evaluate(i, L(13, 21)).Response.DueAt);

        // A P3 row raised later is a P2 from the raise, as for any row.
        var p3 = Inc(IncidentLevel.P3, L(13, 10));
        p3.SetLevel(IncidentLevel.P2, L(13, 15));
        Assert.Equal(L(13, 15), IncidentSla.LevelClockStart(p3));
    }

    // ── Monitor fault mapping ────────────────────────────────────────

    [Fact]
    public void Monitor_takes_the_fault_from_the_most_common_failure_code()
    {
        Assert.Equal((IncidentFault.External, "BANK_UNAVAILABLE"), IncidentMonitor.FaultFromFailureReasons(new[]
            { "BANK_UNAVAILABLE: gateway down", "BANK_UNAVAILABLE: gateway down", "INSUFFICIENT_FUNDS: 3.00 GEL left" }));
        Assert.Equal(IncidentFault.Park, IncidentMonitor.FaultFromFailureReasons(new[] { "INSUFFICIENT_FUNDS: low", null }).Fault);
        Assert.Equal(IncidentFault.Park, IncidentMonitor.FaultForCode("NO_PARK_ACCOUNT"));
        Assert.Equal(IncidentFault.External, IncidentMonitor.FaultForCode("BOG_TIMEOUT"));
        Assert.Equal(IncidentFault.Unknown, IncidentMonitor.FaultForCode("SAGA_INTERRUPTED"));
        Assert.Equal((IncidentFault.Unknown, (string?)null), IncidentMonitor.FaultFromFailureReasons(new string?[] { null, " " }));
        Assert.Equal("BANK_EXCEPTION", IncidentMonitor.CodeOf("BANK_EXCEPTION: timeout: no reply"));
    }

    /// <summary>
    /// External pauses the resolution clock and never counts for §5 / §7, so it is kept for codes that mean
    /// the bank itself failed. A bank integration Swich has not built is Swich's own (§1.2); credentials and
    /// unknown outcomes need a person to find out whose they are.
    /// </summary>
    [Fact]
    public void Monitor_calls_External_only_a_failing_bank_never_a_missing_adapter_credentials_or_an_unknown_outcome()
    {
        // Our own integration is missing (the BOG adapter is a stub): the owner's fault.
        Assert.Equal(IncidentFault.Platform, IncidentMonitor.FaultForCode("BANK_ADAPTER_MISSING"));

        // Credentials / bank-side user setup: whoever set them up — a person decides.
        foreach (var code in new[] { "TBC_NOT_CONFIGURED", "TBC_CREDENTIALS_MUST_BE_CHANGED", "TBC_USER_BLOCKED", "TBC_AUTHENTICATION_FAILED" })
            Assert.Equal(IncidentFault.Unknown, IncidentMonitor.FaultForCode(code));

        // The bank never said what became of the order (our call threw, the lookup failed too).
        foreach (var code in new[] { "BANK_EXCEPTION", "TBC_DUPLICATE_UNRESOLVED", "BOG_STATUS_UNKNOWN" })
            Assert.Equal(IncidentFault.Unknown, IncidentMonitor.FaultForCode(code));

        // A refusal without a reason is not an outage either.
        Assert.Equal(IncidentFault.Unknown, IncidentMonitor.FaultForCode("BANK_FAILED"));
        Assert.Equal(IncidentFault.Unknown, IncidentMonitor.FaultForCode("BANK_REPORTED_FAILED"));

        // The bank's gateway is down or answers with its own fault: outside the owner's control (Annex 1 §3.1).
        foreach (var code in new[] { "BANK_UNAVAILABLE", "TBC_SERVICE_UNAVAILABLE", "BOG_TIMEOUT" })
            Assert.Equal(IncidentFault.External, IncidentMonitor.FaultForCode(code));

        // The park's side — including a park that revoked the platform's authorisation (Annex 3 §7).
        foreach (var code in new[] { "INSUFFICIENT_FUNDS", "INSUFFICIENT_PARK_BALANCE", "NO_PARK_ACCOUNT", "AUTHORISATION_REVOKED" })
            Assert.Equal(IncidentFault.Park, IncidentMonitor.FaultForCode(code));

        // The scenario of the review: every payout of a BOG park requeued as BANK_ADAPTER_MISSING.
        Assert.Equal((IncidentFault.Platform, "BANK_ADAPTER_MISSING"), IncidentMonitor.FaultFromFailureReasons(new[]
            { "BANK_ADAPTER_MISSING: The method or operation is not implemented.", "BANK_ADAPTER_MISSING: The method or operation is not implemented." }));
    }

    /// <summary>
    /// The table the console's payout-wait.ts mirrors (waitCause). The same rows are checked against the
    /// TypeScript copy, so "Bank not answering" on a cashout and "External" on its incident agree.
    /// </summary>
    [Theory]
    [InlineData(null, PayoutWaitCause.Other)]
    [InlineData("INSUFFICIENT_FUNDS", PayoutWaitCause.Funds)]
    [InlineData("INSUFFICIENT_PARK_BALANCE", PayoutWaitCause.Funds)]
    [InlineData("NO_PARK_ACCOUNT", PayoutWaitCause.NoAccount)]
    [InlineData("BANK_EXCEPTION", PayoutWaitCause.Unresolved)]
    [InlineData("TBC_DUPLICATE_UNRESOLVED", PayoutWaitCause.Unresolved)]
    [InlineData("BOG_STATUS_UNKNOWN", PayoutWaitCause.Unresolved)]
    [InlineData("BANK_ADAPTER_MISSING", PayoutWaitCause.Setup)]
    [InlineData("TBC_NOT_CONFIGURED", PayoutWaitCause.Setup)]
    [InlineData("TBC_CREDENTIALS_MUST_BE_CHANGED", PayoutWaitCause.Setup)]
    [InlineData("TBC_USER_BLOCKED", PayoutWaitCause.Setup)]
    [InlineData("TBC_AUTHENTICATION_FAILED", PayoutWaitCause.Setup)]
    [InlineData("BANK_UNAVAILABLE", PayoutWaitCause.Bank)]
    [InlineData("BANK_TRANSIENT_503", PayoutWaitCause.Bank)]
    [InlineData("TBC_SERVICE_UNAVAILABLE", PayoutWaitCause.Bank)]
    [InlineData("BOG_TIMEOUT", PayoutWaitCause.Bank)]
    [InlineData("BANK_FAILED", PayoutWaitCause.Other)]
    [InlineData("BANK_REPORTED_FAILED", PayoutWaitCause.Other)]
    [InlineData("TBC_VALIDATION_ERROR", PayoutWaitCause.Other)]
    [InlineData("SAGA_INTERRUPTED", PayoutWaitCause.Other)]
    [InlineData("AUTHORISATION_REVOKED", PayoutWaitCause.Revoked)]
    public void Payout_wait_codes_are_one_table(string? code, PayoutWaitCause expected) =>
        Assert.Equal(expected, PayoutWaitCodes.CauseOf(code));

    // ── Monitor level: by impact (Annex 1 §2.1, §3.1) ────────────────

    [Fact]
    public void Monitor_opens_a_P1_only_when_several_drivers_wait_and_they_are_most_of_those_who_tried()
    {
        var o = new IncidentMonitorOptions();                       // P1 from 3 waiting drivers
        Assert.Equal(3, o.P1MinWaitingDrivers);

        // One payout (or one driver's two) waiting while the rest of the park is quiet: degraded, not stopped.
        Assert.Equal(IncidentLevel.P2, IncidentMonitor.LevelForImpact(waitingDrivers: 1, paidDrivers: 0, o));
        Assert.Equal(IncidentLevel.P2, IncidentMonitor.LevelForImpact(2, 0, o));
        // Several drivers and nobody gets through: business stopped.
        Assert.Equal(IncidentLevel.P1, IncidentMonitor.LevelForImpact(3, 0, o));
        Assert.Equal(IncidentLevel.P1, IncidentMonitor.LevelForImpact(5, 4, o));       // most of the 9 who tried
        // Several wait, but more drivers were paid meanwhile (e.g. only one bank's cards are stuck): a workaround exists.
        Assert.Equal(IncidentLevel.P2, IncidentMonitor.LevelForImpact(3, 3, o));
        Assert.Equal(IncidentLevel.P2, IncidentMonitor.LevelForImpact(4, 20, o));

        // The threshold is configuration.
        var strict = new IncidentMonitorOptions { P1MinWaitingDrivers = 5 };
        Assert.Equal(IncidentLevel.P2, IncidentMonitor.LevelForImpact(4, 0, strict));
        Assert.Equal(IncidentLevel.P1, IncidentMonitor.LevelForImpact(5, 0, strict));
    }

    // ── Cross-park aggregation: one cause, several parks → one platform-wide P1 ──

    [Fact]
    public void Only_a_bank_outage_or_our_missing_adapter_is_one_cause_across_parks_never_a_park_side_or_undetermined_one()
    {
        // The same bank failing is one cause wherever it shows; a different bank is a different one.
        Assert.Equal("bank:tbc", IncidentMonitor.CauseFamily("BANK_UNAVAILABLE", "tbc"));
        Assert.Equal("bank:tbc", IncidentMonitor.CauseFamily("TBC_SERVICE_UNAVAILABLE", "tbc"));
        Assert.Equal("bank:tbc", IncidentMonitor.CauseFamily("TBC_SERVICE_UNAVAILABLE", null));     // the code names the bank
        Assert.Equal("bank:bog", IncidentMonitor.CauseFamily("BOG_TIMEOUT", null));
        Assert.NotEqual(IncidentMonitor.CauseFamily("BANK_UNAVAILABLE", "tbc"), IncidentMonitor.CauseFamily("BANK_UNAVAILABLE", "bog"));
        // Our own missing integration for a bank is one platform cause for every park at that bank.
        Assert.Equal("adapter:bog", IncidentMonitor.CauseFamily("BANK_ADAPTER_MISSING", "bog"));

        // The park's side never aggregates: two parks short of money are two facts, not one outage.
        foreach (var code in new[] { "INSUFFICIENT_FUNDS", "INSUFFICIENT_PARK_BALANCE", "NO_PARK_ACCOUNT", "AUTHORISATION_REVOKED" })
            Assert.Null(IncidentMonitor.CauseFamily(code, "tbc"));
        // Nor does a cause nobody has established (each park's credentials, an unknown outcome, a refusal without a reason).
        foreach (var code in new[] { "TBC_NOT_CONFIGURED", "TBC_AUTHENTICATION_FAILED", "BANK_EXCEPTION", "BANK_FAILED", "SAGA_INTERRUPTED" })
            Assert.Null(IncidentMonitor.CauseFamily(code, "tbc"));
        Assert.Null(IncidentMonitor.CauseFamily(null, "tbc"));

        // From Incidents:Monitor:P1MinParks parks (2); configurable, but one park is never "several".
        var o = new IncidentMonitorOptions();
        Assert.Equal(2, o.P1MinParks);
        Assert.False(IncidentMonitor.AffectsEnoughParks(1, o));
        Assert.True(IncidentMonitor.AffectsEnoughParks(2, o));
        var three = new IncidentMonitorOptions { P1MinParks = 3 };
        Assert.False(IncidentMonitor.AffectsEnoughParks(2, three));
        Assert.True(IncidentMonitor.AffectsEnoughParks(3, three));
        Assert.False(IncidentMonitor.AffectsEnoughParks(1, new IncidentMonitorOptions { P1MinParks = 1 }));
    }
}
