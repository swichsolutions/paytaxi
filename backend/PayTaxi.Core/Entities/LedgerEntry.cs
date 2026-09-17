using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// Append-only money-movement record. Every entry belongs either to a cashout or to a
/// settlement (never neither) — the two nullable FKs express which.
/// </summary>
public class LedgerEntry : BaseEntity
{
    public Guid? CashoutId { get; set; }
    public Guid? SettlementId { get; set; }
    public Guid ParkId { get; set; }
    public LedgerEntryType EntryType { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "GEL";
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public Cashout? Cashout { get; set; }
    public Settlement? Settlement { get; set; }
}
