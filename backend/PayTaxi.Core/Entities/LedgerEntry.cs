using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class LedgerEntry : BaseEntity
{
    public Guid CashoutId { get; set; }
    public Guid ParkId { get; set; }
    public LedgerEntryType EntryType { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "GEL";
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public Cashout Cashout { get; set; } = default!;
}
