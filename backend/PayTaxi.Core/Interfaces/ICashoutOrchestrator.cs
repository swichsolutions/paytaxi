namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Orchestrates the cashout saga: park-level limit check (Model A.5),
/// bank payout, Yandex deduction, with compensation on partial failure.
///
/// Idempotency: callers MUST supply an idempotency key. Re-invocations
/// with the same key (within the same park) return the existing cashout's
/// current state without re-running side effects.
/// </summary>
public interface ICashoutOrchestrator
{
    Task<CashoutSagaResult> RunAsync(CashoutSagaRequest request, CancellationToken ct = default);
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
    bool WasDeduped);
