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
    /// Queued with a later <c>NextAttemptAt</c>, or abandons it (Failed / ReviewRequired).
    /// </summary>
    Task<CashoutSagaResult> ProcessQueuedAsync(Guid cashoutId, CancellationToken ct = default);
}

public record CashoutSagaRequest(
    Guid ParkId,
    Guid DriverId,
    Guid BankCardId,
    decimal Amount,
    string IdempotencyKey,
    string? InitiatedBy = null);

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
