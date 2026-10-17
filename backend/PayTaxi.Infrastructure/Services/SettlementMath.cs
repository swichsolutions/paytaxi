using PayTaxi.Core.Enums;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The licence agreement's monthly settlement arithmetic (§2.2–2.5), pure and tetri-exact, so every
/// number on a statement can be re-derived by hand from the statement's own inputs:
///
///   F  = fee income of all the licensee's parks       CL = shared costs the licensee paid
///   CS = shared costs Swich paid                      s  = Swich's share (50 %)
///   B  = barrier (20,000 GEL)                         R  = Swich's entitlement from earlier statements
///
///   Barrier rule (§2.3):  E_barrier = F − CL                (Swich carries its own costs while it
///                                                            takes the whole net income)
///   Normal rule (§2.4):   E_normal  = s·(F − CL − CS) + CS  (= (F − CL + CS) ÷ 2 at 50 %: the net
///                                                            split, plus Swich reimbursed for what
///                                                            it paid)
///   Phase:  R ≥ B, or the barrier
///           was reached before   → Normal,   E = E_normal
///           R + E_barrier ≤ B    → Barrier,  E = E_barrier
///           otherwise            → Crossing, φ = (B − R) ÷ E_barrier,  E = (B − R) + (1 − φ)·E_normal
///
///   The barrier is a one-way switch (§2.3: "once the barrier is filled, the 50/50 split of §2.2 applies").
///   A loss month after it (licensee costs above the fees) carries its credit forward and lowers R, but it
///   does not re-open the barrier: the licensee would otherwise pay for the same loss twice — once through
///   the credit, once more as 100 % of the next months' net income until R climbs back.
///
///   D = E + carry-in. A positive D with fee income to split it by is collected; anything else is
///   carried to the next month — a negative D as a credit to the licensee, a positive D with no fee
///   income (nothing to pro-rate) as a debt. A negative amount is never transferred.
///
///   Corrections: an included invoice is never changed; its owner adds a signed correction that the next
///   statement counts. CL and CS are sums of signed entries, so a side's month total can be NEGATIVE when its
///   only entry that month reverses an earlier one (185.00 typed for 18.50 → −166.50). That is deliberately
///   allowed: the formulas are linear in CL and CS, so a negative total reverses exactly what the original took
///   at this month's phase — all of it in the barrier phase, the share of it in the normal one — and whatever E
///   comes out flows through the carry rule above (never a negative transfer). Clamping a side's month at 0 would
///   need a second carry per side and change nothing over time. The floor that does apply is per invoice: an
///   original and its corrections together never go below zero (enforced where corrections are entered), so a
///   side's costs never add up to less than nothing. A correction is priced at the phase of the month that counts
///   it: a barrier-month typo corrected after the barrier filled gives Swich its share back, not all of it — the
///   price of "the next statement includes it" over recomputing a settled month.
/// </summary>
public static class SettlementMath
{
    public sealed record StatementInputs(
        decimal FeeIncome,
        decimal LicenseeCosts,
        decimal SwichCosts,
        decimal CarryIn,
        decimal BarrierGel,
        decimal BarrierBefore,
        decimal SwichSharePercent,
        // An earlier statement since the launch already reached the barrier (whatever R is now).
        bool BarrierFilledBefore = false);

    public sealed record StatementResult(
        StatementPhase Phase,
        decimal BarrierFraction,   // φ rounded to 6 places for display; the entitlement used the exact value

        decimal BarrierEntitlement,
        decimal NormalEntitlement,
        decimal Entitlement,
        decimal Due,
        decimal CarryOut);

    public static StatementResult Compute(StatementInputs i)
    {
        var s = i.SwichSharePercent / 100m;
        var eBarrier = i.FeeIncome - i.LicenseeCosts;
        var eNormal = s * (i.FeeIncome - i.LicenseeCosts - i.SwichCosts) + i.SwichCosts;
        var room = i.BarrierGel - i.BarrierBefore;

        StatementPhase phase;
        decimal phi, entitlement;
        if (i.BarrierFilledBefore || room <= 0)
        {
            phase = StatementPhase.Normal;
            phi = 0m;
            entitlement = eNormal;
        }
        else if (eBarrier <= room)
        {
            // Includes a negative month inside the barrier: the credit simply lowers R next month.
            phase = StatementPhase.Barrier;
            phi = 1m;
            entitlement = eBarrier;
        }
        else
        {
            // eBarrier > room > 0, so 0 < φ < 1: the barrier fills part-way through the month's net
            // income; the part up to B goes to Swich whole, the remainder is split by the normal rule.
            phase = StatementPhase.Crossing;
            phi = room / eBarrier;
            entitlement = room + (1m - phi) * eNormal;
        }

        entitlement = Round2(entitlement);
        var d = entitlement + i.CarryIn;

        decimal due, carryOut;
        if (d <= 0m) { due = 0m; carryOut = d; }                 // credit (or nothing) carried
        else if (i.FeeIncome <= 0m) { due = 0m; carryOut = d; }  // owed, but no park to collect it from pro rata
        else { due = d; carryOut = 0m; }

        return new StatementResult(
            Phase: phase,
            BarrierFraction: Math.Round(phi, 6, MidpointRounding.AwayFromZero),
            BarrierEntitlement: Round2(eBarrier),
            NormalEntitlement: Round2(eNormal),
            Entitlement: entitlement,
            Due: due,
            CarryOut: carryOut);
    }

    /// <summary>
    /// §2.5: the due amount is taken from each park in proportion to its share of the month's fees.
    /// Each part is rounded to the tetri; the rounding remainder goes to the park with the largest
    /// fees (ties: lowest park id, so a re-computation always lands on the same park) — the parts
    /// always add up to <paramref name="due"/> exactly. Parks with no fees pay nothing.
    /// </summary>
    public static IReadOnlyDictionary<Guid, decimal> Allocate(decimal due, IReadOnlyCollection<(Guid ParkId, decimal Fees)> parks)
    {
        var result = parks.ToDictionary(p => p.ParkId, _ => 0m);
        var total = parks.Sum(p => p.Fees);
        if (due <= 0m || total <= 0m) return result;

        foreach (var (parkId, fees) in parks)
            result[parkId] = fees <= 0m ? 0m : Round2(due * fees / total);

        var remainder = due - result.Values.Sum();
        if (remainder != 0m)
        {
            var largest = parks.Where(p => p.Fees > 0m)
                .OrderByDescending(p => p.Fees).ThenBy(p => p.ParkId)
                .First().ParkId;
            result[largest] += remainder;
        }
        return result;
    }

    public static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    // ── §4.1.2 late-payment information ──────────────────────────────

    /// <summary>
    /// How late a park transfer is against the §2.5 due date, and the 0.1 %-per-day penalty the licensee owes
    /// on it under §4.1.2 (simple, on the unpaid amount, until paid). INFORMATION ONLY — nothing is ever
    /// charged automatically. <see cref="PenaltyDays"/> are the overdue days whose cause was the park's
    /// (insufficient balance, no active account); the others (<see cref="TechnicalDays"/>) were a platform or
    /// bank technical fault — or a day nothing had been attempted yet, which is the platform's — and §4.1.2
    /// says no penalty accrues for them.
    /// </summary>
    public sealed record OverdueInfo(DateOnly DueDate, int DaysOverdue, int PenaltyDays, decimal Accrued, bool Paid)
    {
        public int TechnicalDays => DaysOverdue - PenaltyDays;
    }

    /// <summary>One failed attempt of a transfer as the ledger recorded it: its local day and failure code.</summary>
    public sealed record FailedAttempt(DateOnly LocalDay, string? Code);

    /// <param name="paidOnLocal">Local date the transfer completed (or was paid outside the platform); null while it is open (then counted to today).</param>
    /// <param name="attempts">
    /// Every failed attempt, oldest first (the SettlementFailed ledger entries). Each overdue day takes the cause of
    /// the latest attempt made by the end of that day — so a month the bank was down for a week and the park was
    /// short afterwards is split day by day, and the attribution survives the success that finally paid it
    /// (a success clears the row's failure reason; the ledger keeps every attempt).
    /// </param>
    public static OverdueInfo? Overdue(decimal amount, DateOnly dueDate, DateOnly? paidOnLocal, DateOnly todayLocal,
        decimal percentPerDay, IReadOnlyList<FailedAttempt> attempts)
    {
        if (amount <= 0m) return null;
        var end = paidOnLocal ?? todayLocal;
        var days = end.DayNumber - dueDate.DayNumber;
        if (days <= 0) return null;

        var penaltyDays = 0;
        var next = 0;
        string? cause = null;
        var attempted = false;
        for (var day = dueDate.AddDays(1); day <= end; day = day.AddDays(1))
        {
            while (next < attempts.Count && attempts[next].LocalDay <= day)
            {
                cause = attempts[next].Code;
                attempted = true;
                next++;
            }
            if (attempted && IsParkSideFailure(cause)) penaltyDays++;
        }
        return new OverdueInfo(dueDate, days, penaltyDays, Round2(amount * percentPerDay / 100m * penaltyDays), paidOnLocal is not null);
    }

    /// <summary>
    /// "INSUFFICIENT_PARK_BALANCE" / "NO_PARK_ACCOUNT" (or a bank's insufficient-funds code) — the park's side, not a
    /// technical fault. So is AUTHORISATION_REVOKED: the park withdrew the platform's authority to collect (Annex 3 §7),
    /// which is neither a platform nor a bank fault — §4.1.2's exemption does not cover it.
    /// </summary>
    public static bool IsParkSideFailure(string? code) =>
        code is "INSUFFICIENT_PARK_BALANCE" or "NO_PARK_ACCOUNT" or ParkAuthorisation.RevokedCode
        || (code?.Contains("INSUFFICIENT", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>"INSUFFICIENT_PARK_BALANCE: Park account balance…" → "INSUFFICIENT_PARK_BALANCE".</summary>
    public static string? FailureCode(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason)) return null;
        var i = failureReason.IndexOf(':');
        return i > 0 ? failureReason[..i].Trim() : failureReason.Trim();
    }
}
