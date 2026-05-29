namespace PayTaxi.Core.Interfaces;

public interface IBankPayoutAdapter
{
    string BankType { get; }
    Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default);
    Task<BankTransferStatus> GetTransferStatusAsync(string transferId, CancellationToken ct = default);

    /// <summary>
    /// List transfers initiated for the given park within a time window.
    /// Reconciliation calls this nightly to cross-check our ledger against
    /// what the bank actually moved.
    /// </summary>
    Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(
        Guid parkId, DateTime from, DateTime to, CancellationToken ct = default);
}

public record BankTransferRecord(
    string TransferId,
    Guid ParkId,
    decimal Amount,
    string Currency,
    BankTransferStatus Status,
    DateTime SentAt);

public record BankTransferRequest(
    string IdempotencyKey,
    string DestinationCardToken,
    decimal Amount,
    string Currency,
    string Reference,
    Guid ParkId
);

public record BankTransferResult(
    bool Success,
    string? TransferId,
    string? ErrorCode,
    string? ErrorMessage
);

public enum BankTransferStatus
{
    Pending,
    Completed,
    Failed,
    Unknown
}
