using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class Cashout : BaseEntity
{
    public Guid DriverId { get; set; }
    public Guid ParkId { get; set; }
    public Guid BankCardId { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public CashoutStatus Status { get; set; } = CashoutStatus.Queued;
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();
    public string? BankTransferId { get; set; }
    public string? YandexTransactionId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Sequential invoice number, assigned when the cashout becomes Completed.
    /// Backed by a Postgres sequence so concurrent saga runs can't collide.
    /// Null until then.
    /// </summary>
    public long? InvoiceNumber { get; set; }

    public Driver Driver { get; set; } = default!;
    public Park Park { get; set; } = default!;
    public BankCard BankCard { get; set; } = default!;
    public ICollection<LedgerEntry> LedgerEntries { get; set; } = new List<LedgerEntry>();
}
