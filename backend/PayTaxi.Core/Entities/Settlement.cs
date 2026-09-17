using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// One nightly aggregated transfer of Swich's fee share from a park's account to
/// Swich's TBC account (PAYTAXI-CONTEXT.md §4/§5). Covers a fixed set of completed
/// cashouts (<see cref="Cashout.SettlementId"/>), so a dispute is resolved by expanding
/// this one row. A failed transfer keeps its cashout set and is retried on the next
/// nightly run or on demand — never a partial take.
/// </summary>
public class Settlement : BaseEntity
{
    public Guid ParkId { get; set; }

    /// <summary>The local (Tbilisi) calendar day this settlement covers.</summary>
    public DateOnly SettlementDate { get; set; }

    /// <summary>UTC bounds of the covered cashouts' CompletedAt (from = start of unsettled history).</summary>
    public DateTime PeriodFromUtc { get; set; }
    public DateTime PeriodToUtc { get; set; }

    public int CashoutCount { get; set; }

    /// <summary>Sum of the flat fees on the covered cashouts.</summary>
    public decimal FeeTotal { get; set; }

    /// <summary>Portion of FeeTotal that fell under the phase-1 cap (100% to Swich for Levan's park).</summary>
    public decimal Phase1Fees { get; set; }

    /// <summary>Portion of FeeTotal settled at the steady-state share.</summary>
    public decimal Phase2Fees { get; set; }

    /// <summary>What is transferred park → Swich.</summary>
    public decimal SwichShare { get; set; }

    /// <summary>What stays in the park's account (informational).</summary>
    public decimal ParkShare { get; set; }

    /// <summary>Cumulative park fee income BEFORE this settlement — the phase-1 progress marker.</summary>
    public decimal CumulativeFeesBefore { get; set; }

    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;

    /// <summary>Bank transfer document id / idempotency key: "settle:{Id}".</summary>
    public string IdempotencyKey { get; set; } = default!;

    public string? BankTransferId { get; set; }
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Transfer description: "PayTaxi settlement YYYY-MM-DD, N tx, inv ref PT-YYYY-MM".</summary>
    public string Description { get; set; } = default!;

    /// <summary>Monthly invoice reference the transfer is documented under: "PT-YYYY-MM".</summary>
    public string InvoiceRef { get; set; } = default!;

    /// <summary>Park account the transfer was (or will be) sent from.</summary>
    public Guid? ParkBankAccountId { get; set; }

    /// <summary>Snapshot of the receiving Swich IBAN at execution time.</summary>
    public string? SwichIban { get; set; }

    public string? InitiatedBy { get; set; }

    public Park Park { get; set; } = default!;
    public ParkBankAccount? ParkBankAccount { get; set; }
    public ICollection<Cashout> Cashouts { get; set; } = new List<Cashout>();
}
