namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Wraps the four Yandex Fleet API endpoints we use.
/// Implementations must be idempotent for reads, idempotency-keyed for writes,
/// and rate-limited per park (Yandex empirical limit: 0.5s between calls).
/// </summary>
public interface IYandexFleetClient
{
    /// <summary>List all driver profiles registered to a park, with their current Yandex balance.</summary>
    Task<IReadOnlyList<YandexDriverProfile>> GetDriverProfilesAsync(
        Guid parkId, CancellationToken ct = default);

    /// <summary>Read a single driver's balance.</summary>
    Task<decimal> GetDriverBalanceAsync(
        Guid parkId, string driverProfileId, CancellationToken ct = default);

    /// <summary>Read recent Yandex transactions for a driver in a time window.</summary>
    Task<IReadOnlyList<YandexTransaction>> GetTransactionsAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Read recent ride orders for a driver in a time window.</summary>
    Task<IReadOnlyList<YandexOrder>> GetOrdersAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// THE write endpoint — posts a negative transaction that debits the driver's
    /// Yandex balance to record a manual cashout. Requires an idempotency key.
    /// Blocked when ReadOnlyMode is enabled.
    /// </summary>
    Task<YandexTransactionResult> PostCashoutTransactionAsync(
        Guid parkId,
        string driverProfileId,
        decimal amount,
        string idempotencyKey,
        CancellationToken ct = default);

    /// <summary>
    /// Compensating write: posts a POSITIVE transaction that returns a previously
    /// debited amount to the driver's Yandex balance. Used when the bank payout is
    /// abandoned after the debit already went through. Requires an idempotency key.
    /// </summary>
    Task<YandexTransactionResult> PostReversalTransactionAsync(
        Guid parkId,
        string driverProfileId,
        decimal amount,
        string idempotencyKey,
        CancellationToken ct = default);
}

public record YandexDriverProfile(
    string DriverProfileId,
    string? Name,
    string? CarPlate,
    decimal Balance,
    string Currency,
    string? Phone = null);

public record YandexTransaction(
    string TransactionId,
    decimal Amount,
    string Category,
    string? Description,
    DateTime CreatedAt);

public record YandexOrder(
    string OrderId,
    decimal Amount,
    string? From,
    string? To,
    DateTime CreatedAt);

public record YandexTransactionResult(
    bool Success,
    string? TransactionId,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Thrown when callers attempt to invoke the write endpoint while
/// the client is configured in read-only mode.
/// </summary>
public sealed class YandexReadOnlyModeException : InvalidOperationException
{
    public YandexReadOnlyModeException()
        : base("Yandex Fleet client is in read-only mode — write operations are disabled. " +
               "Set YandexFleet:ReadOnlyMode=false to allow cashout posting.") { }
}

/// <summary>Transient failure raised by the client to trigger retry policy.</summary>
public sealed class YandexTransientException : Exception
{
    public YandexTransientException(string message) : base(message) { }
}
