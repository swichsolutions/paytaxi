using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class Cashout : BaseEntity
{
    public Guid DriverId { get; set; }
    public Guid ParkId { get; set; }
    public Guid BankCardId { get; set; }

    /// <summary>The park account the payout was routed from. Null until routing succeeded.</summary>
    public Guid? ParkBankAccountId { get; set; }

    /// <summary>Gross amount debited from the driver's Yandex balance.</summary>
    public decimal Amount { get; set; }

    /// <summary>Flat fee retained by the park. Driver receives Amount − Fee.</summary>
    public decimal Fee { get; set; }

    public CashoutStatus Status { get; set; } = CashoutStatus.Queued;
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();
    public string? BankTransferId { get; set; }
    public string? YandexTransactionId { get; set; }

    /// <summary>Yandex transaction that reversed the debit when the payout was abandoned.</summary>
    public string? YandexReversalTransactionId { get; set; }

    public string? FailureReason { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Who started this cashout: "driver:{id}", "admin", "admin-retry:{id}", "system".</summary>
    public string? InitiatedBy { get; set; }

    // ── Payout queue bookkeeping ──────────────────────────────────────
    /// <summary>How many bank payout attempts have been made.</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the queue worker may next try the bank payout (null = not queued).</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// Sequential invoice number, assigned when the cashout becomes Completed.
    /// Backed by a Postgres sequence so concurrent saga runs can't collide.
    /// Null until then.
    /// </summary>
    public long? InvoiceNumber { get; set; }

    public Driver Driver { get; set; } = default!;
    public Park Park { get; set; } = default!;
    public BankCard BankCard { get; set; } = default!;
    public ParkBankAccount? ParkBankAccount { get; set; }
    public ICollection<LedgerEntry> LedgerEntries { get; set; } = new List<LedgerEntry>();
}
