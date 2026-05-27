using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Yandex;

/// <summary>
/// Real HTTP implementation of the Yandex Fleet client.
/// NOT YET IMPLEMENTED — registered as the fallback when YandexFleet:UseMock=false.
/// Implement when the first park's real credentials arrive.
///
/// Notes for the real implementation:
///   - Headers: X-Client-ID, X-API-Key, X-Park-ID (per park, resolved at runtime from parks row)
///   - Base URL: https://fleet-api.taxi.yandex.net/
///   - Use HttpClient via IHttpClientFactory
///   - Don't add retry / rate-limit / audit logging here — those live in ResilientYandexFleetClient
/// </summary>
public class YandexFleetClient : IYandexFleetClient
{
    private const string NotImplementedMessage =
        "Real YandexFleetClient not yet implemented. " +
        "Set YandexFleet:UseMock=true to use MockYandexFleetClient.";

    public Task<IReadOnlyList<YandexDriverProfile>> GetDriverProfilesAsync(
        Guid parkId, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<decimal> GetDriverBalanceAsync(
        Guid parkId, string driverProfileId, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<IReadOnlyList<YandexTransaction>> GetTransactionsAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<IReadOnlyList<YandexOrder>> GetOrdersAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);

    public Task<YandexTransactionResult> PostCashoutTransactionAsync(
        Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default)
        => throw new NotImplementedException(NotImplementedMessage);
}
