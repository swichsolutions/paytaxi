using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Yandex;

/// <summary>
/// Decorator that wraps any IYandexFleetClient with three cross-cutting concerns:
///   1. Rate limiting per park (Yandex spec: 0.5s min between calls)
///   2. Retry with exponential backoff on transient failures
///   3. Audit logging of every attempt (success or failure)
///
/// Designed so the inner client (mock or real) only needs to implement the happy path.
/// Errors raised from the inner client are caught here and either retried or surfaced.
/// </summary>
public class ResilientYandexFleetClient : IYandexFleetClient
{
    private readonly IYandexFleetClient _inner;
    private readonly IYandexRateLimiter _limiter;
    private readonly IApiAuditLogger _audit;
    private readonly YandexFleetOptions _opts;
    private readonly ILogger<ResilientYandexFleetClient> _log;

    public ResilientYandexFleetClient(
        IYandexFleetClient inner,
        IYandexRateLimiter limiter,
        IApiAuditLogger audit,
        IOptions<YandexFleetOptions> opts,
        ILogger<ResilientYandexFleetClient> log)
    {
        _inner = inner;
        _limiter = limiter;
        _audit = audit;
        _opts = opts.Value;
        _log = log;
    }

    public Task<IReadOnlyList<YandexDriverProfile>> GetDriverProfilesAsync(
        Guid parkId, CancellationToken ct = default) =>
        ExecuteAsync(parkId,
            endpoint: "POST /v1/parks/driver-profiles/list",
            paramsObj: new { parkId },
            action: () => _inner.GetDriverProfilesAsync(parkId, ct),
            ct: ct);

    public Task<decimal> GetDriverBalanceAsync(
        Guid parkId, string driverProfileId, CancellationToken ct = default) =>
        ExecuteAsync(parkId,
            endpoint: "POST /v1/parks/driver-profiles/list (balance)",
            paramsObj: new { parkId, driverProfileId },
            action: () => _inner.GetDriverBalanceAsync(parkId, driverProfileId, ct),
            ct: ct);

    public Task<IReadOnlyList<YandexTransaction>> GetTransactionsAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default) =>
        ExecuteAsync(parkId,
            endpoint: "POST /v2/parks/transactions/list",
            paramsObj: new { parkId, driverProfileId, from, to },
            action: () => _inner.GetTransactionsAsync(parkId, driverProfileId, from, to, ct),
            ct: ct);

    public Task<IReadOnlyList<YandexOrder>> GetOrdersAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default) =>
        ExecuteAsync(parkId,
            endpoint: "POST /v2/parks/orders/list",
            paramsObj: new { parkId, driverProfileId, from, to },
            action: () => _inner.GetOrdersAsync(parkId, driverProfileId, from, to, ct),
            ct: ct);

    public Task<YandexTransactionResult> PostCashoutTransactionAsync(
        Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default) =>
        ExecuteAsync(parkId,
            endpoint: "POST /v2/parks/driver-profile/transactions",
            paramsObj: new { parkId, driverProfileId, amount, idempotencyKey },
            action: () => _inner.PostCashoutTransactionAsync(parkId, driverProfileId, amount, idempotencyKey, ct),
            ct: ct);

    // ── Core execution path ───────────────────────────────────────────
    private async Task<T> ExecuteAsync<T>(
        Guid parkId,
        string endpoint,
        object paramsObj,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var paramsHash = HashParams(paramsObj);
        var attempt = 0;
        var maxAttempts = _opts.MaxRetries + 1;

        while (true)
        {
            attempt++;
            await _limiter.AcquireAsync(parkId, ct);

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await action();
                sw.Stop();

                _ = _audit.LogAsync(new ApiCallRecord(
                    ApiProvider: "YandexFleet",
                    Endpoint: endpoint,
                    Method: "POST",
                    ParkId: parkId,
                    ActorId: null,
                    ActorType: "System",
                    RequestParamsHash: paramsHash,
                    ResponseCode: 200,
                    Success: true,
                    DurationMs: sw.ElapsedMilliseconds,
                    CorrelationId: correlationId), ct);

                return result;
            }
            catch (YandexReadOnlyModeException)
            {
                // Programmer error — log and surface immediately, no retry.
                sw.Stop();
                _ = _audit.LogAsync(new ApiCallRecord(
                    "YandexFleet", endpoint, "POST", parkId, null, "System",
                    paramsHash, 403, false, sw.ElapsedMilliseconds, correlationId), ct);
                throw;
            }
            catch (YandexTransientException ex) when (attempt < maxAttempts)
            {
                sw.Stop();
                var delay = TimeSpan.FromMilliseconds(_opts.RetryBaseDelayMs * Math.Pow(2, attempt - 1));
                _log.LogWarning(
                    "{Endpoint} transient failure (attempt {Attempt}/{Max}): {Message}. Retrying in {Delay}ms",
                    endpoint, attempt, maxAttempts, ex.Message, delay.TotalMilliseconds);

                _ = _audit.LogAsync(new ApiCallRecord(
                    "YandexFleet", endpoint, "POST", parkId, null, "System",
                    paramsHash, 503, false, sw.ElapsedMilliseconds, correlationId), ct);

                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(ex, "{Endpoint} failed after {Attempt} attempt(s)", endpoint, attempt);
                _ = _audit.LogAsync(new ApiCallRecord(
                    "YandexFleet", endpoint, "POST", parkId, null, "System",
                    paramsHash, 500, false, sw.ElapsedMilliseconds, correlationId), ct);
                throw;
            }
        }
    }

    private static string HashParams(object paramsObj)
    {
        // Hash the params (never log raw — they may contain PII or driver IDs).
        var json = JsonSerializer.Serialize(paramsObj);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16];
    }
}
