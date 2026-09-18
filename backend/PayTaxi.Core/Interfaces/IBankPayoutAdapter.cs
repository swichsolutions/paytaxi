namespace PayTaxi.Core.Interfaces;

/// <summary>
/// One implementation per bank rail (TBC, BoG) plus the dev mock. The adapter is
/// stateless with respect to the park: every call carries the source account and
/// its credentials, because credentials belong to the park, not to PayTaxi.
/// </summary>
public interface IBankPayoutAdapter
{
    string BankType { get; }

    /// <summary>
    /// Initiate a transfer from the park's account to the driver's IBAN.
    /// Must be idempotent on <see cref="BankTransferRequest.IdempotencyKey"/>.
    /// Never fire-and-forget: a thrown exception means "outcome unknown" and the
    /// caller will ask <see cref="FindTransferByIdempotencyKeyAsync"/> before retrying.
    /// A rail that executes asynchronously returns <c>Success + IsPending</c>; the caller
    /// then polls <see cref="GetTransferStatusAsync"/> until it settles.
    /// </summary>
    Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default);

    Task<BankTransferStatus> GetTransferStatusAsync(
        BankAccountContext source, string transferId, CancellationToken ct = default);

    /// <summary>
    /// Look up a transfer by the document id we assigned (the idempotency key).
    /// Used after a timeout / ambiguous response so we never blindly re-fire.
    /// Returns null when the bank has no record of it.
    /// </summary>
    Task<BankTransferLookup?> FindTransferByIdempotencyKeyAsync(
        BankAccountContext source, string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// List transfers initiated from the given account within a time window.
    /// Reconciliation calls this nightly to cross-check our ledger against
    /// what the bank actually moved.
    /// </summary>
    Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(
        BankAccountContext source, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Current available balance of the park's account, if the rail exposes it.</summary>
    Task<decimal?> GetBalanceAsync(BankAccountContext source, CancellationToken ct = default);
}

/// <summary>The park-side account a call operates on. Credentials are the park's, resolved per call.</summary>
public record BankAccountContext(
    Guid ParkId,
    Guid ParkBankAccountId,
    string Provider,
    string BankCode,
    string SourceIban,
    string? SourceHolderName,
    string CredentialsJson);

public record BankTransferRecord(
    string TransferId,
    Guid ParkId,
    decimal Amount,
    string Currency,
    BankTransferStatus Status,
    DateTime SentAt);

/// <param name="DestinationTaxCode">
/// Beneficiary tax / personal number. Optional for intra-bank transfers; banks may require it
/// for transfers to another bank (TBC validates it against the IBAN when supplied).
/// </param>
public record BankTransferRequest(
    string IdempotencyKey,
    BankAccountContext Source,
    string DestinationIban,
    string? DestinationName,
    decimal Amount,
    string Currency,
    string Reference,
    string? DestinationTaxCode = null
);

/// <param name="IsRetryable">
/// True when the bank said "not now" (gateway down, insufficient funds, rate limit)
/// rather than "never" (invalid IBAN, closed account). Retryable failures park the
/// cashout in the payout queue; non-retryable ones reverse the Yandex debit.
/// </param>
/// <param name="IsPending">
/// With <c>Success = true</c>: the bank accepted the order but has not executed it yet
/// (asynchronous rails). The caller must poll <see cref="IBankPayoutAdapter.GetTransferStatusAsync"/>
/// before treating the money as moved.
/// </param>
public record BankTransferResult(
    bool Success,
    string? TransferId,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsRetryable = false,
    bool IsPending = false
);

public record BankTransferLookup(
    string TransferId,
    BankTransferStatus Status,
    decimal Amount);

public enum BankTransferStatus
{
    Pending,
    Completed,
    Failed,
    Unknown
}
