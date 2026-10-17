using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Services;
using Xunit;
using static PayTaxi.Infrastructure.Services.SettlementMath;

namespace PayTaxi.Tests.Domain;

/// <summary>
/// The licence agreement's monthly arithmetic (§2.2–2.5) — every case worked by hand first:
/// F fee income, CL licensee-paid costs, CS Swich-paid costs, B barrier, R Swich's entitlement so far.
/// </summary>
public class SettlementMathTests
{
    private static StatementInputs In(decimal f, decimal cl = 0m, decimal cs = 0m, decimal r = 0m,
        decimal carryIn = 0m, decimal barrier = 20_000m, decimal share = 50m) =>
        new(FeeIncome: f, LicenseeCosts: cl, SwichCosts: cs, CarryIn: carryIn, BarrierGel: barrier, BarrierBefore: r, SwichSharePercent: share);

    [Fact]
    public void Barrier_phase_Swich_takes_income_minus_the_licensees_costs_and_carries_its_own()
    {
        // F − CL = 120.00 − 12.40 = 107.60; Swich's own 48.00 hosting does not reduce it (§2.4, barrier clause).
        var r = Compute(In(f: 120m, cl: 12.40m, cs: 48m));
        Assert.Equal(StatementPhase.Barrier, r.Phase);
        Assert.Equal(1m, r.BarrierFraction);
        Assert.Equal(107.60m, r.Entitlement);
        Assert.Equal(107.60m, r.Due);
        Assert.Equal(0m, r.CarryOut);
    }

    [Fact]
    public void Normal_phase_is_the_equalising_amount_of_section_2_4()
    {
        // (F − CL + CS) ÷ 2 = (120.00 − 12.40 + 48.00) ÷ 2 = 77.80 — and the general form gives the same.
        var r = Compute(In(f: 120m, cl: 12.40m, cs: 48m, r: 25_000m));
        Assert.Equal(StatementPhase.Normal, r.Phase);
        Assert.Equal(0m, r.BarrierFraction);
        Assert.Equal(77.80m, r.Entitlement);
        Assert.Equal(0.5m * (120m - 12.40m - 48m) + 48m, r.Entitlement);
        Assert.Equal(77.80m, r.Due);
    }

    [Fact]
    public void Normal_phase_with_another_share_uses_s_times_net_plus_Swich_costs()
    {
        // 40 % × (120.00 − 12.40 − 48.00) + 48.00 = 23.84 + 48.00 = 71.84
        var r = Compute(In(f: 120m, cl: 12.40m, cs: 48m, r: 20_000m, share: 40m));
        Assert.Equal(StatementPhase.Normal, r.Phase);
        Assert.Equal(71.84m, r.Entitlement);
    }

    [Fact]
    public void A_month_that_ends_exactly_on_the_barrier_is_still_barrier_and_the_next_one_is_normal()
    {
        // R + (F − CL) = 19,892.40 + 107.60 = 20,000.00 = B → "≤ B" → Barrier, all 107.60.
        var m1 = Compute(In(f: 120m, cl: 12.40m, cs: 48m, r: 19_892.40m));
        Assert.Equal(StatementPhase.Barrier, m1.Phase);
        Assert.Equal(107.60m, m1.Entitlement);

        // R = 20,000 = B → Normal from the first tetri.
        var m2 = Compute(In(f: 120m, cl: 12.40m, cs: 48m, r: 19_892.40m + m1.Entitlement));
        Assert.Equal(StatementPhase.Normal, m2.Phase);
        Assert.Equal(77.80m, m2.Entitlement);
    }

    [Fact]
    public void Crossing_mid_month_splits_the_net_income_at_the_barrier()
    {
        // B − R = 50.00 still open; F − CL = 107.60 → φ = 50 / 107.60 = 0.464684…
        // E = 50.00 + (1 − φ) × 77.80 = 50.00 + 41.6476 = 91.65
        var r = Compute(In(f: 120m, cl: 12.40m, cs: 48m, r: 19_950m));
        Assert.Equal(StatementPhase.Crossing, r.Phase);
        Assert.Equal(0.464684m, r.BarrierFraction);
        Assert.Equal(107.60m, r.BarrierEntitlement);
        Assert.Equal(77.80m, r.NormalEntitlement);
        Assert.Equal(91.65m, r.Entitlement);
        Assert.Equal(91.65m, r.Due);
        // Swich gets more than the normal split but less than the barrier rule would give.
        Assert.InRange(r.Entitlement, r.NormalEntitlement, r.BarrierEntitlement);
    }

    [Fact]
    public void Crossing_rounds_half_a_tetri_away_from_zero()
    {
        // R 3, B 4, F 4, CS 1: φ = 1/4; normal = ½(4 − 1) + 1 = 2.50; E = 1 + ¾ × 2.50 = 2.875 → 2.88.
        var r = Compute(In(f: 4m, cs: 1m, r: 3m, barrier: 4m));
        Assert.Equal(StatementPhase.Crossing, r.Phase);
        Assert.Equal(0.25m, r.BarrierFraction);
        Assert.Equal(2.88m, r.Entitlement);
    }

    [Fact]
    public void Costs_above_income_are_never_collected_but_carried_as_a_credit_into_next_month()
    {
        // Barrier: 2.00 − 10.00 = −8.00 → nothing transferred, −8.00 carried for the licensee.
        var jan = Compute(In(f: 2m, cl: 10m));
        Assert.Equal(-8m, jan.Entitlement);
        Assert.Equal(0m, jan.Due);
        Assert.Equal(-8m, jan.CarryOut);

        // Next month the credit comes off: 20.00 − 8.00 = 12.00. R is ΣE = −8 (Swich has received nothing yet).
        var feb = Compute(In(f: 20m, r: jan.Entitlement, carryIn: jan.CarryOut));
        Assert.Equal(StatementPhase.Barrier, feb.Phase);
        Assert.Equal(20m, feb.Entitlement);
        Assert.Equal(12m, feb.Due);
        Assert.Equal(0m, feb.CarryOut);
    }

    [Fact]
    public void A_credit_larger_than_the_next_month_keeps_rolling()
    {
        var r = Compute(In(f: 3m, carryIn: -8m));
        Assert.Equal(3m, r.Entitlement);
        Assert.Equal(0m, r.Due);
        Assert.Equal(-5m, r.CarryOut);
    }

    [Fact]
    public void Zero_income_with_Swich_costs_after_the_barrier_is_owed_but_carried_forward()
    {
        // Normal: ½(0 − 1 − 3) + 3 = 1.00 owed to Swich — but no park earned fees to take it from pro rata.
        var r = Compute(In(f: 0m, cl: 1m, cs: 3m, r: 20_000m));
        Assert.Equal(1m, r.Entitlement);
        Assert.Equal(0m, r.Due);
        Assert.Equal(1m, r.CarryOut);

        // Next month it is collected on top: ½ × 1.00 + 1.00 = 1.50.
        var next = Compute(In(f: 1m, r: 20_001m, carryIn: r.CarryOut));
        Assert.Equal(1.50m, next.Due);
        Assert.Equal(0m, next.CarryOut);
    }

    [Fact]
    public void Zero_income_with_licensee_costs_in_the_barrier_phase_is_a_credit()
    {
        var r = Compute(In(f: 0m, cl: 5m, cs: 30m));
        Assert.Equal(StatementPhase.Barrier, r.Phase);
        Assert.Equal(-5m, r.Entitlement);
        Assert.Equal(0m, r.Due);
        Assert.Equal(-5m, r.CarryOut);
    }

    [Fact]
    public void Over_a_run_of_months_the_barrier_is_filled_exactly_once_and_collections_add_up()
    {
        // Twelve identical months of 1,000 GEL net under a 5,000 barrier: five barrier months, one
        // crossing exactly at the boundary (5,000 reached on month 5 — "≤ B"), then the normal split.
        decimal r = 0, collected = 0;
        var phases = new List<StatementPhase>();
        for (var m = 0; m < 12; m++)
        {
            var res = Compute(In(f: 1_000m, barrier: 5_000m, r: r));
            phases.Add(res.Phase);
            r += res.Entitlement;
            collected += res.Due;
        }
        Assert.Equal(5, phases.Count(p => p == StatementPhase.Barrier));
        Assert.Equal(0, phases.Count(p => p == StatementPhase.Crossing));
        Assert.Equal(7, phases.Count(p => p == StatementPhase.Normal));
        Assert.Equal(5_000m + 7 * 500m, collected);
        Assert.Equal(r, collected);
    }

    // ── Pro rata ─────────────────────────────────────────────────────

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void Pro_rata_split_follows_each_parks_fees()
    {
        // 4.30 × 3.50/5.50 = 2.736… → 2.74; 4.30 × 2.00/5.50 = 1.563… → 1.56; sum 4.30.
        var s = Allocate(4.30m, new[] { (A, 3.50m), (B, 2.00m) });
        Assert.Equal(2.74m, s[A]);
        Assert.Equal(1.56m, s[B]);
        Assert.Equal(4.30m, s.Values.Sum());
    }

    [Fact]
    public void Rounding_remainder_goes_to_the_park_with_the_largest_fees()
    {
        // 10.00 over three equal parks: 3.33 × 3 = 9.99 → the missing tetri to the first of the largest (lowest id).
        var even = Allocate(10m, new[] { (C, 1m), (B, 1m), (A, 1m) });
        Assert.Equal(3.34m, even[A]);
        Assert.Equal(3.33m, even[B]);
        Assert.Equal(3.33m, even[C]);

        // 0.02 over 0.25/0.50/0.25: each part rounds up to 0.01 → 0.03, one tetri too many — taken back
        // from the largest park, so the parts still add up to exactly what is due.
        var over = Allocate(0.02m, new[] { (A, 0.25m), (B, 0.50m), (C, 0.25m) });
        Assert.Equal(0.00m, over[B]);
        Assert.Equal(0.01m, over[A]);
        Assert.Equal(0.01m, over[C]);
        Assert.Equal(0.02m, over.Values.Sum());
    }

    [Fact]
    public void Parks_without_fees_pay_nothing_and_nothing_is_split_when_nothing_is_due()
    {
        var s = Allocate(5m, new[] { (A, 0m), (B, 2m), (C, 3m) });
        Assert.Equal(0m, s[A]);
        Assert.Equal(2m, s[B]);
        Assert.Equal(3m, s[C]);

        var none = Allocate(0m, new[] { (A, 1m), (B, 2m) });
        Assert.All(none.Values, v => Assert.Equal(0m, v));
    }

    // ── §4.1.2 overdue information ───────────────────────────────────

    private static FailedAttempt[] Tried(params (int Month, int Day, string? Code)[] attempts) =>
        attempts.Select(x => new FailedAttempt(new DateOnly(2026, x.Month, x.Day), x.Code)).ToArray();

    [Fact]
    public void Overdue_counts_days_after_the_due_date_at_point_one_percent_per_day()
    {
        var due = new DateOnly(2026, 11, 5);
        var parkShort = Tried((11, 3, "INSUFFICIENT_PARK_BALANCE"));
        Assert.Null(Overdue(100m, due, null, new DateOnly(2026, 11, 5), 0.1m, parkShort));   // on the day: not late

        var open = Overdue(100m, due, null, new DateOnly(2026, 11, 15), 0.1m, parkShort)!;
        Assert.Equal(10, open.DaysOverdue);
        Assert.Equal(10, open.PenaltyDays);
        Assert.Equal(1.00m, open.Accrued);
        Assert.False(open.Paid);

        // Paid late: the count stops at the payment date — and the attribution stays (the ledger keeps the attempts).
        var paid = Overdue(100m, due, new DateOnly(2026, 11, 7), new DateOnly(2026, 12, 31), 0.1m, parkShort)!;
        Assert.Equal(2, paid.DaysOverdue);
        Assert.Equal(2, paid.PenaltyDays);
        Assert.Equal(0.20m, paid.Accrued);
        Assert.True(paid.Paid);

        // A bank or platform fault: §4.1.2 — no penalty accrues for it.
        var technical = Overdue(100m, due, null, new DateOnly(2026, 11, 6), 0.1m, Tried((11, 3, "BANK_UNAVAILABLE")))!;
        Assert.Equal(1, technical.DaysOverdue);
        Assert.Equal(0, technical.PenaltyDays);
        Assert.Equal(1, technical.TechnicalDays);
        Assert.Equal(0m, technical.Accrued);
    }

    [Fact]
    public void Each_overdue_day_takes_the_cause_of_the_latest_attempt_made_by_then()
    {
        // Due 5 Nov. Park short from the 3rd, the bank down 10–12 Nov, short again from the 13th, paid on the 20th.
        var due = new DateOnly(2026, 11, 5);
        var history = Tried((11, 3, "INSUFFICIENT_PARK_BALANCE"), (11, 10, "BANK_EXCEPTION"), (11, 11, "BANK_EXCEPTION"),
            (11, 13, "INSUFFICIENT_PARK_BALANCE"), (11, 14, "NO_PARK_ACCOUNT"));
        var o = Overdue(200m, due, new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 31), 0.1m, history)!;
        Assert.Equal(15, o.DaysOverdue);
        Assert.Equal(4 + 8, o.PenaltyDays);          // 6–9 Nov, then 13–20 Nov
        Assert.Equal(3, o.TechnicalDays);            // 10–12 Nov
        Assert.Equal(2.40m, o.Accrued);              // 200 × 0.1 % × 12

        // A month settled late by the platform itself: the days before any attempt are not the park's.
        var late = Overdue(100m, due, null, new DateOnly(2026, 11, 15), 0.1m, Tried((11, 12, "INSUFFICIENT_PARK_BALANCE")))!;
        Assert.Equal(10, late.DaysOverdue);
        Assert.Equal(4, late.PenaltyDays);           // 12–15 Nov
    }

    [Fact]
    public void A_transfer_the_parks_revocation_stopped_is_late_on_the_parks_side_not_a_technical_fault()
    {
        // Annex 3 §7: the park withdrew the platform's authority to collect — neither a platform nor a bank fault, so
        // §4.1.2's exemption does not apply; nothing is retried, and the one refusal stands for every day after it.
        Assert.True(IsParkSideFailure(ParkAuthorisation.RevokedCode));
        var due = new DateOnly(2026, 11, 5);
        var o = Overdue(100m, due, null, new DateOnly(2026, 11, 15), 0.1m, Tried((11, 3, ParkAuthorisation.RevokedCode)))!;
        Assert.Equal(10, o.PenaltyDays);
        Assert.Equal(0, o.TechnicalDays);
        Assert.Equal(1.00m, o.Accrued);
    }

    [Fact]
    public void Once_reached_the_barrier_stays_reached_through_a_loss_month()
    {
        // R = 19,550 after a Normal loss month (barrier reached earlier): F = 3,000, no costs → 50/50, not a re-crossing.
        var reopened = Compute(In(f: 3_000m, r: 19_550m, carryIn: -950m));
        Assert.Equal(StatementPhase.Crossing, reopened.Phase);   // what R alone would say
        var kept = Compute(In(f: 3_000m, r: 19_550m, carryIn: -950m) with { BarrierFilledBefore = true });
        Assert.Equal(StatementPhase.Normal, kept.Phase);
        Assert.Equal(1_500m, kept.Entitlement);
        Assert.Equal(550m, kept.Due);                             // not 775: the loss is charged once, through the credit

        // Deciding "reached" from the earlier statements, oldest first.
        var b = 20_000m;
        Assert.False(SettlementService.BarrierReached(new[] { Step(1, 8_000m, StatementPhase.Barrier), Step(2, 11_999.99m, StatementPhase.Barrier) }, b));
        Assert.True(SettlementService.BarrierReached(new[] { Step(1, 8_000m, StatementPhase.Barrier), Step(2, 12_000m, StatementPhase.Barrier) }, b));   // exactly on it
        Assert.True(SettlementService.BarrierReached(new[] { Step(1, 20_500m, StatementPhase.Crossing), Step(2, -950m, StatementPhase.Normal) }, b));
        Assert.False(SettlementService.BarrierReached(new[] { Step(1, -2m, StatementPhase.Barrier), Step(2, 5m, StatementPhase.Barrier) }, 4m));

        static SettlementService.BarrierStep Step(int m, decimal e, StatementPhase p) => new(new DateOnly(2026, m, 1), e, p, false);
    }

    [Fact]
    public void A_licensee_parks_invoice_wording_carries_none_of_the_licensees_figures()
    {
        // With the pro-rata rule a printed share percentage gives away the licensee's total fee income and due,
        // and the phase its costs. The park's invoice states its own fees and the rule only.
        Assert.Equal("12 გამოტანა · მძღოლების საკომისიო სულ 6.00₾", ParkInvoiceText.ServiceDetail(12, 6.00m));
        Assert.DoesNotContain("%", ParkInvoiceText.LicenseeParkTerms);
        Assert.DoesNotContain("ბარიერ", ParkInvoiceText.LicenseeParkTerms);
        Assert.Contains("პროპორციულად", ParkInvoiceText.LicenseeParkTerms);
    }

    [Fact]
    public void Only_a_licensee_parks_invoice_cites_the_licence_agreement_for_a_payment_outside_the_platform()
    {
        // §2.5 (the licensee answers for its park's debt) is a clause of the licence agreement. A Swich-direct park is
        // not party to it: its invoice says the debt was paid outside the platform, without citing a contract it never
        // signed, and never that a licensee paid it.
        Assert.Contains("2.5", ParkInvoiceText.PaidOutsideNote(viaLicensee: true));
        Assert.DoesNotContain("2.5", ParkInvoiceText.PaidOutsideNote(viaLicensee: false));
        Assert.DoesNotContain("სალიცენზიო", ParkInvoiceText.PaidOutsideNote(viaLicensee: false));
        Assert.Equal("დაფარა ლიცენზიატმა", ParkInvoiceText.PaidOutsideLabel("licensee", viaLicensee: true));
        Assert.Equal("დაფარულია პლატფორმის გარეთ", ParkInvoiceText.PaidOutsideLabel("licensee", viaLicensee: false));
        Assert.Equal("დაფარულია პლატფორმის გარეთ", ParkInvoiceText.PaidOutsideLabel("park", viaLicensee: true));
    }

    [Fact]
    public void In_a_month_under_both_regimes_the_note_cites_the_licence_agreement_only_for_a_licensee_row_paid_outside()
    {
        // A park reassigned mid-month has a Swich-direct row and a licensee row on one invoice (each fee is settled under the
        // regime it was earned under). The note on a payment outside the platform follows the rows that were paid outside:
        // a direct row is not under the licence agreement, so it never cites §2.5 — even when the other row is a licensee's.
        static Settlement Row(SettlementStatus status, bool viaLicensee) =>
            new() { Status = status, MonthlyStatementId = viaLicensee ? Guid.NewGuid() : null };

        var directPaidOutside = ParkInvoiceText.PaidOutsideNoteFor(new[]
            { Row(SettlementStatus.SettledExternally, viaLicensee: false), Row(SettlementStatus.Completed, viaLicensee: true) });
        Assert.Equal(ParkInvoiceText.PaidOutsideNote(viaLicensee: false), directPaidOutside);
        Assert.Equal(ParkInvoiceText.PaidOutsideNote(viaLicensee: true), ParkInvoiceText.PaidOutsideNoteFor(new[]
            { Row(SettlementStatus.Completed, viaLicensee: false), Row(SettlementStatus.SettledExternally, viaLicensee: true) }));
        Assert.Equal(ParkInvoiceText.PaidOutsideNote(viaLicensee: true), ParkInvoiceText.PaidOutsideNoteFor(new[]
            { Row(SettlementStatus.SettledExternally, viaLicensee: false), Row(SettlementStatus.SettledExternally, viaLicensee: true) }));
        Assert.Null(ParkInvoiceText.PaidOutsideNoteFor(new[]
            { Row(SettlementStatus.Completed, viaLicensee: false), Row(SettlementStatus.Failed, viaLicensee: true) }));
    }

    [Fact]
    public void Due_date_is_month_end_plus_the_agreed_days()
    {
        Assert.Equal(new DateOnly(2026, 11, 5), SettlementService.DueDate(new DateOnly(2026, 10, 1), 5));
        Assert.Equal(new DateOnly(2027, 3, 5), SettlementService.DueDate(new DateOnly(2027, 2, 1), 5));
    }
}
