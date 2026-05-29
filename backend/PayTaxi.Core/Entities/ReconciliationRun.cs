using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// One pass of the reconciliation worker over a time window for a single park.
/// Discrepancies live in a child table — see <see cref="ReconciliationDiscrepancy"/>.
/// </summary>
public class ReconciliationRun : BaseEntity
{
    public Guid ParkId { get; set; }
    public DateTime WindowFrom { get; set; }
    public DateTime WindowTo { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Running;

    public int CashoutsScanned { get; set; }
    public int BankTransfersScanned { get; set; }
    public int YandexTxScanned { get; set; }
    public int DiscrepanciesFound { get; set; }

    public string? Error { get; set; }

    public Park Park { get; set; } = default!;
    public ICollection<ReconciliationDiscrepancy> Discrepancies { get; set; } = new List<ReconciliationDiscrepancy>();
}
