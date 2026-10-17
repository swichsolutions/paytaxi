using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// One aggregated transfer of Swich's fee share from a park's account to Swich's TBC account.
/// Covers a fixed set of completed cashouts (<see cref="Cashout.SettlementId"/>), so a dispute is
/// resolved by expanding this one row. A failed transfer keeps its cashout set and amount and is
/// retried every night (or on demand) until it succeeds — never a partial take.
///
/// Since the licence agreement (§2.5) settlement is MONTHLY (<see cref="IsMonthly"/>): one row per
/// park per month. For a licensee park the row is that park's pro-rata part of the licensee's
/// <see cref="MonthlyStatement"/>; for a park Swich connected directly it stands alone and uses the
/// park's own split. Rows with IsMonthly = false are the nightly per-day settlements made before the
/// agreement — kept for history, never created any more.
/// </summary>
public class Settlement : BaseEntity
{
    public Guid ParkId { get; set; }

    /// <summary>
    /// Monthly rows: the last day of the settled month (Tbilisi local). Legacy rows: the day settled.
    /// </summary>
    public DateOnly SettlementDate { get; set; }

    /// <summary>True for monthly settlements (licence agreement); false for the legacy nightly rows.</summary>
    public bool IsMonthly { get; set; }

    /// <summary>The licensee statement this row is the park's part of; null for Swich-direct parks and legacy rows.</summary>
    public Guid? MonthlyStatementId { get; set; }

    /// <summary>UTC bounds of the covered cashouts' CompletedAt (from = start of unsettled history).</summary>
    public DateTime PeriodFromUtc { get; set; }
    public DateTime PeriodToUtc { get; set; }

    public int CashoutCount { get; set; }

    /// <summary>Sum of the flat fees on the covered cashouts.</summary>
    public decimal FeeTotal { get; set; }

    /// <summary>
    /// Portion of FeeTotal that fell under phase 1 (direct parks) or under the licensee's barrier
    /// (licensee parks: FeeTotal × the statement's barrier fraction).
    /// </summary>
    public decimal Phase1Fees { get; set; }

    /// <summary>Portion of FeeTotal settled at the steady-state share.</summary>
    public decimal Phase2Fees { get; set; }

    /// <summary>
    /// What is transferred park → Swich. For a licensee park: its pro-rata part of the statement's
    /// due amount (which can exceed the park's own fees when Swich paid most of the shared costs).
    /// </summary>
    public decimal SwichShare { get; set; }

    /// <summary>FeeTotal − SwichShare: what the park keeps of its fees (informational; negative when it paid more than its fees).</summary>
    public decimal ParkShare { get; set; }

    /// <summary>Cumulative park fee income BEFORE this settlement — the phase-1 progress marker for direct parks.</summary>
    public decimal CumulativeFeesBefore { get; set; }

    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;

    /// <summary>
    /// Bank document id / idempotency key, deterministic per (statement, park) — "settle:{statement}:{park}" —
    /// or per (park, month) for direct parks — "settle:{park}:{yyyy-MM}"; legacy rows "settle:{Id}".
    /// </summary>
    public string IdempotencyKey { get; set; } = default!;

    public string? BankTransferId { get; set; }

    /// <summary>
    /// Which bank document (payment order) the transfer is on: 0 = <see cref="IdempotencyKey"/>,
    /// n &gt; 0 = "{IdempotencyKey}#n". Bumped only when the bank holds an order under the current id and
    /// has definitively refused it (e.g. the park's account was short) — TBC keeps one order per request
    /// id, so re-sending that id would only return the dead order and the transfer could never succeed.
    /// Never bumped on an unknown outcome: then the same id is repeated and the bank de-duplicates it.
    /// </summary>
    public int BankDocumentSeq { get; set; }
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Transfer description: "PayTaxi settlement YYYY-MM, N tx, inv ref PT-YYYY-MM" (legacy rows: the day).</summary>
    public string Description { get; set; } = default!;

    /// <summary>Monthly invoice reference the transfer is documented under: "PT-YYYY-MM".</summary>
    public string InvoiceRef { get; set; } = default!;

    /// <summary>Park account the transfer was (or will be) sent from.</summary>
    public Guid? ParkBankAccountId { get; set; }

    /// <summary>Snapshot of the receiving Swich IBAN at execution time.</summary>
    public string? SwichIban { get; set; }

    public string? InitiatedBy { get; set; }

    // ── Paid outside the platform (Status = SettledExternally) ──────
    // §2.5: when a park's account cannot cover its part, the licensee is liable for it (§4.1.2) and may
    // pay Swich directly. Swich records that payment here, which closes the row for good: it is never
    // retried, so the park is never debited a second time once its account is topped up.

    /// <summary>Who paid: "licensee" (§2.5) or "park" (by its own manual transfer).</summary>
    public string? ExternalPayer { get; set; }

    /// <summary>Local date the money reached Swich — the §4.1.2 overdue count stops here.</summary>
    public DateOnly? ExternalPaidOn { get; set; }

    /// <summary>The payment's bank reference (or another document that proves it).</summary>
    public string? ExternalReference { get; set; }

    public string? ExternalNote { get; set; }

    /// <summary>"admin:{email}" of the Swich admin who recorded it, and when.</summary>
    public string? ExternalRecordedBy { get; set; }
    public DateTime? ExternalRecordedAt { get; set; }

    public Park Park { get; set; } = default!;
    public MonthlyStatement? MonthlyStatement { get; set; }
    public ParkBankAccount? ParkBankAccount { get; set; }
    public ICollection<Cashout> Cashouts { get; set; } = new List<Cashout>();
}
