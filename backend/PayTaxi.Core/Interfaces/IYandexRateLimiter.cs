namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Enforces Yandex Fleet API rate limits: minimum N milliseconds between calls
/// targeting the same park. Implementations must be process-safe (semaphore per park).
/// </summary>
public interface IYandexRateLimiter
{
    /// <summary>
    /// Waits if necessary so that this call respects the minimum interval since the
    /// previous call against the same park, then records the call timestamp.
    /// </summary>
    Task AcquireAsync(Guid parkId, CancellationToken ct = default);
}
