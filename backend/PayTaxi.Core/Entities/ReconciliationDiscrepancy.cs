namespace PayTaxi.Core.Entities;

/// <summary>
/// A single drift between PayTaxi's records and an external system,
/// detected during a <see cref="ReconciliationRun"/>.
///
/// Kinds (string, evolves over time — kept as a free-form text column with
/// a check constraint instead of an enum to make migrations cheap):
///   - <c>missing_in_bank</c>          — DB says Completed, bank has no transfer (or status≠Completed)
///   - <c>orphaned_bank_send</c>       — Bank shows a transfer, no matching PayTaxi cashout
///   - <c>missing_in_yandex</c>        — DB says Completed, Yandex has no matching debit
///   - <c>orphaned_yandex_debit</c>    — Yandex has a manual-cashout debit, no matching PayTaxi cashout
///   - <c>amount_mismatch_bank</c>     — DB and bank both have it, amounts differ beyond tolerance
///   - <c>amount_mismatch_yandex</c>   — DB and Yandex both have it, amounts differ beyond tolerance
///   - <c>stuck_pending</c>            — Cashout still Processing N hours after creation
/// </summary>
public class ReconciliationDiscrepancy : BaseEntity
{
    public Guid RunId { get; set; }
    public Guid ParkId { get; set; }

    /// <summary>Linked PayTaxi cashout, when one side is ours. Null for orphans on the external side.</summary>
    public Guid? CashoutId { get; set; }

    public string Kind { get; set; } = default!;

    /// <summary>What PayTaxi's ledger thinks the amount is. Null when there's no matching row on our side.</summary>
    public decimal? PaytaxiAmount { get; set; }

    /// <summary>What the external system reports. Null when the external row is missing.</summary>
    public decimal? ExternalAmount { get; set; }

    public string? BankTransferId { get; set; }
    public string? YandexTransactionId { get; set; }

    public string? Notes { get; set; }

    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNotes { get; set; }

    public ReconciliationRun Run { get; set; } = default!;
    public Cashout? Cashout { get; set; }
}
