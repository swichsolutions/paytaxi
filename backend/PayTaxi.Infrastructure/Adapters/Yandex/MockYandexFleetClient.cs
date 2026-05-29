using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Adapters.Yandex;

/// <summary>
/// In-memory mock of the Yandex Fleet API. Returns realistic data, simulates latency,
/// and (with low probability) raises transient failures to exercise the retry/saga path.
///
/// Maps from our internal Park.Id (Guid) to Yandex's external park identifier
/// (Park.YandexParkId, string) by querying the database. This means the mock and the
/// real client share the same calling shape — only the underlying HTTP/in-memory dispatch differs.
/// </summary>
public class MockYandexFleetClient : IYandexFleetClient
{
    private readonly MockYandexFleetData _data;
    private readonly AppDbContext _db;
    private readonly YandexFleetOptions _opts;
    private readonly ILogger<MockYandexFleetClient> _log;
    private readonly Random _rng;

    public MockYandexFleetClient(
        MockYandexFleetData data,
        AppDbContext db,
        IOptions<YandexFleetOptions> opts,
        ILogger<MockYandexFleetClient> log)
    {
        _data = data;
        _db = db;
        _opts = opts.Value;
        _log = log;
        _rng = new Random();
    }

    public async Task<IReadOnlyList<YandexDriverProfile>> GetDriverProfilesAsync(
        Guid parkId, CancellationToken ct = default)
    {
        await SimulateCallAsync(ct);
        var yandexParkId = await ResolveYandexParkIdAsync(parkId, ct);

        if (!_data.ByPark.TryGetValue(yandexParkId, out var drivers))
            return Array.Empty<YandexDriverProfile>();

        return drivers.Select(d => new YandexDriverProfile(
            DriverProfileId: d.DriverProfileId,
            Name: d.Name,
            CarPlate: d.CarPlate,
            Balance: d.Balance,
            Currency: "GEL",
            Phone: d.Phone)).ToList();
    }

    public async Task<decimal> GetDriverBalanceAsync(
        Guid parkId, string driverProfileId, CancellationToken ct = default)
    {
        await SimulateCallAsync(ct);
        var yandexParkId = await ResolveYandexParkIdAsync(parkId, ct);

        var driver = _data.ByPark.TryGetValue(yandexParkId, out var list)
            ? list.Find(d => d.DriverProfileId == driverProfileId)
            : null;

        if (driver is null) throw new InvalidOperationException(
            $"Driver profile '{driverProfileId}' not found under park '{yandexParkId}'");

        return driver.Balance;
    }

    public async Task<IReadOnlyList<YandexTransaction>> GetTransactionsAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await SimulateCallAsync(ct);
        var yandexParkId = await ResolveYandexParkIdAsync(parkId, ct);

        return _data.Transactions.TryGetValue((yandexParkId, driverProfileId), out var txs)
            ? txs.Where(t => t.CreatedAt >= from && t.CreatedAt <= to).ToList()
            : Array.Empty<YandexTransaction>();
    }

    public async Task<IReadOnlyList<YandexOrder>> GetOrdersAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await SimulateCallAsync(ct);
        var yandexParkId = await ResolveYandexParkIdAsync(parkId, ct);

        return _data.Orders.TryGetValue((yandexParkId, driverProfileId), out var orders)
            ? orders.Where(o => o.CreatedAt >= from && o.CreatedAt <= to).ToList()
            : Array.Empty<YandexOrder>();
    }

    public async Task<YandexTransactionResult> PostCashoutTransactionAsync(
        Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        if (_opts.ReadOnlyMode)
            throw new YandexReadOnlyModeException();

        await SimulateCallAsync(ct);
        var yandexParkId = await ResolveYandexParkIdAsync(parkId, ct);

        var txId = $"yx_tx_{idempotencyKey.Substring(0, Math.Min(12, idempotencyKey.Length))}";

        if (_data.TryDebit(yandexParkId, driverProfileId, amount, txId, out var error))
        {
            _log.LogInformation(
                "Mock Yandex debited {Amount} GEL from driver {Profile} in park {Park}; tx_id={Tx}",
                amount, driverProfileId, yandexParkId, txId);
            return new YandexTransactionResult(true, txId, null, null);
        }

        return new YandexTransactionResult(false, null, error, $"Mock failure: {error}");
    }

    // ── Internals ─────────────────────────────────────────────────────
    private async Task<string> ResolveYandexParkIdAsync(Guid parkId, CancellationToken ct)
    {
        var park = await _db.Parks
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == parkId, ct)
            ?? throw new InvalidOperationException($"Park {parkId} not found in database");
        return park.YandexParkId;
    }

    private async Task SimulateCallAsync(CancellationToken ct)
    {
        // Simulate network latency
        if (_opts.MockLatencyMs > 0)
            await Task.Delay(_opts.MockLatencyMs, ct);

        // Random transient failure to exercise retry policy
        if (_rng.NextDouble() < _opts.MockTransientFailureRate)
        {
            _log.LogWarning("Mock Yandex injecting transient failure");
            throw new YandexTransientException("Simulated Yandex 503 — try again");
        }
    }
}
