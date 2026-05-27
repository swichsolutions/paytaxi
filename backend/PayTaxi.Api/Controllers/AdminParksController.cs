using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Admin endpoints scoped to a park. Validates the full Yandex integration
/// stack: DB park resolution → resilient client (rate-limit, retry, audit) → mock.
///
/// Auth deferred — Phase 8 will lock these behind manager-role JWT.
/// </summary>
[ApiController]
[Route("api/admin/parks/{parkId:guid}")]
public class AdminParksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IYandexFleetClient _yandex;
    private readonly ILogger<AdminParksController> _log;

    public AdminParksController(
        AppDbContext db,
        IYandexFleetClient yandex,
        ILogger<AdminParksController> log)
    {
        _db = db;
        _yandex = yandex;
        _log = log;
    }

    /// <summary>List parks (top-level helper so the UI can pick one).</summary>
    [HttpGet("/api/admin/parks")]
    public async Task<IActionResult> ListParks(CancellationToken ct)
    {
        var parks = await _db.Parks
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Slug,
                p.YandexParkId,
                p.BankProvider,
                operatingModel = p.OperatingModel.ToString(),
                authorizationLimit = p.AuthorizationLimit,
                status = p.Status.ToString(),
                driverCount = p.Drivers.Count(),
            })
            .ToListAsync(ct);

        return Ok(parks);
    }

    /// <summary>
    /// Drivers in a park, each with their current Yandex balance.
    /// Yandex calls go through the resilient client (rate-limited per park,
    /// retried on transient failure, audit-logged on every attempt).
    /// </summary>
    [HttpGet("drivers")]
    public async Task<IActionResult> ListDrivers(Guid parkId, CancellationToken ct)
    {
        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found", parkId });

        // Hit Yandex once for the whole park, not once per driver.
        var yandexProfiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
        var balanceByProfile = yandexProfiles.ToDictionary(p => p.DriverProfileId);

        var drivers = await _db.Drivers
            .AsNoTracking()
            .Include(d => d.BankCards.Where(b => b.IsActive))
            .Where(d => d.ParkId == parkId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        var result = drivers.Select(d => new
        {
            id = d.Id,
            name = d.Name,
            yandexProfileId = d.YandexDriverProfileId,
            status = d.Status.ToString(),
            yandex = d.YandexDriverProfileId is not null && balanceByProfile.TryGetValue(d.YandexDriverProfileId, out var yp)
                ? new
                {
                    balance = yp.Balance,
                    currency = yp.Currency,
                    carPlate = yp.CarPlate,
                    name = yp.Name,
                }
                : null,
            cards = d.BankCards
                .OrderByDescending(b => b.IsDefault)
                .Select(b => new
                {
                    id = b.Id,
                    maskedPan = b.MaskedPan,
                    bankType = b.BankType,
                    isDefault = b.IsDefault,
                }),
        });

        return Ok(new
        {
            park = new { park.Id, park.Name, park.YandexParkId },
            driverCount = drivers.Count,
            drivers = result,
        });
    }

    /// <summary>
    /// Read-through balance for one driver.
    /// Useful to sanity-check Yandex live data for a specific driver.
    /// </summary>
    [HttpGet("drivers/{driverId:guid}/balance")]
    public async Task<IActionResult> GetDriverBalance(Guid parkId, Guid driverId, CancellationToken ct)
    {
        var driver = await _db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });
        if (driver.YandexDriverProfileId is null)
            return BadRequest(new { error = "driver_not_linked_to_yandex" });

        var balance = await _yandex.GetDriverBalanceAsync(parkId, driver.YandexDriverProfileId, ct);
        return Ok(new
        {
            driverId,
            driverName = driver.Name,
            yandexProfileId = driver.YandexDriverProfileId,
            balance,
            currency = "GEL",
            fetchedAt = DateTime.UtcNow,
        });
    }

    /// <summary>Recent Yandex transactions for a driver (last 14 days by default).</summary>
    [HttpGet("drivers/{driverId:guid}/transactions")]
    public async Task<IActionResult> GetDriverTransactions(
        Guid parkId, Guid driverId, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        var driver = await _db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });
        if (driver.YandexDriverProfileId is null)
            return BadRequest(new { error = "driver_not_linked_to_yandex" });

        var to = DateTime.UtcNow;
        var from = to.AddDays(-Math.Clamp(days, 1, 90));

        var txs = await _yandex.GetTransactionsAsync(parkId, driver.YandexDriverProfileId, from, to, ct);
        return Ok(new { driverId, from, to, count = txs.Count, transactions = txs });
    }

    /// <summary>
    /// Smoke-test endpoint that exercises the resilient client (and therefore
    /// rate limiter, retry policy and audit log) with parallel requests.
    /// Returns timing data so you can see the 0.5s spacing in action.
    /// </summary>
    [HttpGet("smoke-test")]
    public async Task<IActionResult> SmokeTest(Guid parkId, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Fire 5 concurrent calls — the rate limiter should serialise them at 500ms intervals
        var tasks = Enumerable.Range(0, 5)
            .Select(async i =>
            {
                var t0 = sw.ElapsedMilliseconds;
                var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
                var t1 = sw.ElapsedMilliseconds;
                return new { call = i + 1, startMs = t0, endMs = t1, durationMs = t1 - t0, profileCount = profiles.Count };
            })
            .ToList();

        var results = await Task.WhenAll(tasks);

        return Ok(new
        {
            parkId,
            startedAt,
            totalMs = sw.ElapsedMilliseconds,
            calls = results,
            note = "If rate limiter works, total time should be ~2000ms (4 × 500ms gaps for 5 calls)",
        });
    }
}
