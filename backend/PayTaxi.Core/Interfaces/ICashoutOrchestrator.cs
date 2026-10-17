namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Orchestrates the cashout saga (Yandex debit first, then bank payout, with
/// compensation when the payout is abandoned) and exposes the single-step
/// entry point the payout queue worker uses to drain <c>Queued</c> cashouts.
///
/// Idempotency: callers MUST supply an idempotency key. Re-invocations
/// with the same key return the existing cashout's current state without
/// re-running side effects.
/// </summary>
public interface ICashoutOrchestrator
{
    /// <summary>Full saga for a brand-new cashout request.</summary>
    Task<CashoutSagaResult> RunAsync(CashoutSagaRequest request, CancellationToken ct = default);

    /// <summary>
    /// Attempt (again) the bank payout for a cashout sitting in <c>Queued</c>.
    /// Called by the payout queue worker. Moves the cashout to Completed, keeps it
    /// Queued with a later <c>NextAttemptAt</c> (for as long as the bank or the park's
    /// balance needs), fails it with a reversal when the bank rejects it for good, or
    /// parks it in ReviewRequired when its outcome stays unknown.
    /// </summary>
    Task<CashoutSagaResult> ProcessQueuedAsync(Guid cashoutId, CancellationToken ct = default);

    /// <summary>
    /// The human way out of an indefinite wait: cancel a <c>Queued</c> payout and give the gross
    /// amount back on the driver's Yandex balance (same reversal path as a bank rejection).
    /// Claims the row atomically, so it can never race the queue worker into paying AND reversing;
    /// a cancel that lands while the worker is mid-attempt waits a few seconds for that attempt.
    /// Returns null when the row could not be claimed: not (or no longer) Queued, something already
    /// at the bank, or a document whose outcome the bank never told us (money may have left — the
    /// cancel waits until the bank gives a definite answer about that document). Throws
    /// <see cref="CashoutCancelRefusedException"/> when, asked before the reversal, the bank holds one
    /// of the payout's documents or cannot be asked; the payout then stays in the queue (or, for a
    /// contradiction only a human can sort out, goes to review).
    /// </summary>
    Task<CashoutSagaResult?> CancelQueuedAsync(
        Guid parkId, Guid cashoutId, string reason, string actor, CancellationToken ct = default);

    /// <summary>
    /// Flag cashouts whose saga was interrupted (Processing, no bank id, no next attempt,
    /// untouched for <paramref name="olderThan"/>) as ReviewRequired. Returns the count.
    /// </summary>
    Task<int> SweepStaleProcessingAsync(TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>
    /// The payout account's provider or bank user changed: every payout queued on it with a document whose outcome the
    /// bank never told us goes to ReviewRequired instead of being re-sent under the new settings, where the bank's
    /// duplicate check would not recognise the document and could pay it a second time. <paramref name="why"/> words
    /// the change for the review note. Returns how many were held.
    /// </summary>
    Task<int> HoldUnansweredDocumentsAsync(Guid parkBankAccountId, string why, CancellationToken ct = default);
}

/// <summary>
/// A console cancel that the bank vetoed: asked about the payout's documents before the reversal, it
/// holds one of them (<see cref="BankHasOrder"/>) or could not be asked (<see cref="BankUnconfirmed"/>).
/// Nothing was given back. <see cref="Code"/> is the API's error code.
/// </summary>
public sealed class CashoutCancelRefusedException : Exception
{
    public const string BankHasOrder = "bank_has_order";
    public const string BankUnconfirmed = "bank_unconfirmed";

    public string Code { get; }

    public CashoutCancelRefusedException(string code, string message) : base(message) => Code = code;
}

public record CashoutSagaRequest(
    Guid ParkId,
    Guid DriverId,
    Guid BankCardId,
    decimal Amount,
    string IdempotencyKey,
    string? InitiatedBy = null,
    Guid? RetryOfCashoutId = null);

public record CashoutSagaResult(
    Guid CashoutId,
    string Status,
    decimal Amount,
    decimal Fee,
    decimal Net,
    string? BankTransferId,
    string? YandexTransactionId,
    string? FailureReason,
    bool WasDeduped,
    DateTime? NextAttemptAt = null,
    int AttemptCount = 0);
