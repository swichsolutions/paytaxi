namespace PayTaxi.Core.Interfaces;

public interface IBankPayoutAdapter
{
    string BankType { get; }
    Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default);
    Task<BankTransferStatus> GetTransferStatusAsync(string transferId, CancellationToken ct = default);
}

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
