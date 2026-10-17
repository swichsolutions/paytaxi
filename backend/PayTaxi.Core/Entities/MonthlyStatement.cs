using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// The licensee's monthly settlement under the licence agreement (§2.2–2.5): the fee income of all
/// its parks, the shared costs each side paid, the barrier progress and the ONE amount Swich is due,
/// collected from the parks pro rata to their fees. Every figure is frozen when the statement is
/// computed — the per-park transfers (<see cref="Settlements"/>) only move what this row says, and the
/// licensee's monthly report is rendered from it.
///
/// Signs: <see cref="CarryIn"/>/<see cref="CarryOut"/> are amounts owed TO Swich — negative is a
/// credit to the licensee (a month whose costs exceeded its income), positive a debt that could not
/// be collected yet (a positive due with no fee income to split it by).
/// </summary>
public class MonthlyStatement : BaseEntity
{
    public Guid LicenseeId { get; set; }

    /// <summary>First day of the settled calendar month (Tbilisi local).</summary>
    public DateOnly Month { get; set; }

    /// <summary>UTC bounds of the local month. Fees completed before PeriodToUtc and not yet settled are included.</summary>
    public DateTime PeriodFromUtc { get; set; }
    public DateTime PeriodToUtc { get; set; }

    public int CashoutCount { get; set; }

    /// <summary>F — the fees of every included cashout of the licensee's parks.</summary>
    public decimal FeeIncome { get; set; }

    /// <summary>CL — shared costs the licensee paid (included invoices).</summary>
    public decimal LicenseeCosts { get; set; }

    /// <summary>CS — shared costs Swich paid (included invoices).</summary>
    public decimal SwichCosts { get; set; }

    /// <summary>The previous statement's <see cref="CarryOut"/>.</summary>
    public decimal CarryIn { get; set; }

    /// <summary>Snapshot of the licensee's barrier (B) when computed.</summary>
    public decimal BarrierGel { get; set; }

    /// <summary>Snapshot of Swich's share percentage (s) when computed.</summary>
    public decimal SwichSharePercent { get; set; }

    /// <summary>R — what Swich was entitled to from the licensee's earlier statements since the launch date.</summary>
    public decimal BarrierBefore { get; set; }

    /// <summary>
    /// The barrier had already been reached by an earlier statement (§2.3: "once the barrier is filled, the
    /// 50/50 split of §2.2 applies") — so this month is Normal even when a later loss month has pulled R back
    /// under B. Part of the frozen inputs: the statement re-derives from its own row.
    /// </summary>
    public bool BarrierFilledBefore { get; set; }

    public StatementPhase Phase { get; set; }

    /// <summary>φ — the share of this month's net income that still fell under the barrier (1 = Barrier, 0 = Normal).</summary>
    public decimal BarrierFraction { get; set; }

    /// <summary>E — Swich's entitlement from this month alone, by the phase rule. Negative when costs exceed income.</summary>
    public decimal Entitlement { get; set; }

    /// <summary>D — collected now: E + CarryIn when positive and there is fee income to split it by, else 0.</summary>
    public decimal Due { get; set; }

    public decimal CarryOut { get; set; }

    public MonthlyStatementStatus Status { get; set; } = MonthlyStatementStatus.Pending;

    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the last park transfer was confirmed (or ComputedAt when there was nothing to collect).</summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>"PT-YYYY-MM" — the reference every park transfer of the month carries.</summary>
    public string InvoiceRef { get; set; } = default!;

    public string? InitiatedBy { get; set; }

    public Licensee Licensee { get; set; } = default!;

    /// <summary>One row per park with fees in the statement — that park's pro-rata transfer.</summary>
    public ICollection<Settlement> Settlements { get; set; } = new List<Settlement>();

    public ICollection<CommonCost> Costs { get; set; } = new List<CommonCost>();
}
