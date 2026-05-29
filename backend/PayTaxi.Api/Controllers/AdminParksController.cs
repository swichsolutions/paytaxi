using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Admin endpoints scoped to a park. Validates the full Yandex integration
/// stack: DB park resolution → resilient client (rate-limit, retry, audit) → mock.
///
/// Requires admin role. Super-admins see any park; park-admins can hit any
/// park id today (no per-park gate enforced server-side yet — TODO when more
/// than one park admin exists in production).
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/parks/{parkId:guid}")]
public class AdminParksController : AdminControllerBase
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
        var query = _db.Parks.AsNoTracking().AsQueryable();

        // Park-admins only see their own park; super-admins see all.
        var scoped = ScopedParkId;
        if (scoped is not null) query = query.Where(p => p.Id == scoped);

        var parks = await query
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
        if (!CanAccessPark(parkId)) return Forbid();

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
        if (!CanAccessPark(parkId)) return Forbid();

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
        if (!CanAccessPark(parkId)) return Forbid();

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
    /// <summary>
    /// Headline KPIs for the admin overview page. Single round-trip aggregation
    /// over Cashouts + Drivers for the given park.
    /// </summary>
    [HttpGet("kpis")]
    public async Task<IActionResult> Kpis(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        var now = DateTime.UtcNow;
        var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        // All park cashouts grouped by date bucket in one query.
        var todayCashouts = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.ParkId == parkId && c.CreatedAt >= dayStart)
            .Select(c => new { c.Status, c.Amount, c.Fee })
            .ToListAsync(ct);

        var completedToday = todayCashouts.Where(c => c.Status == Core.Enums.CashoutStatus.Completed).ToList();
        var pendingToday   = todayCashouts.Where(c =>
            c.Status == Core.Enums.CashoutStatus.Queued ||
            c.Status == Core.Enums.CashoutStatus.Processing).ToList();
        var failedToday    = todayCashouts.Where(c =>
            c.Status == Core.Enums.CashoutStatus.Failed ||
            c.Status == Core.Enums.CashoutStatus.ReviewRequired).ToList();

        var driverCounts = await _db.Drivers
            .AsNoTracking()
            .Where(d => d.ParkId == parkId)
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var activeDrivers = driverCounts.FirstOrDefault(d => d.Status == Core.Enums.DriverStatus.Active)?.Count ?? 0;
        var totalDrivers = driverCounts.Sum(d => d.Count);

        return Ok(new
        {
            park = new { id = park.Id, name = park.Name, operatingModel = park.OperatingModel.ToString() },
            cashoutsToday = new
            {
                count = completedToday.Count,
                value = completedToday.Sum(c => c.Amount),
            },
            feesToday = new { value = completedToday.Sum(c => c.Fee) },
            pendingQueue = new
            {
                count = pendingToday.Count,
                value = pendingToday.Sum(c => c.Amount),
            },
            failedToday = new { count = failedToday.Count },
            activeDrivers = new { count = activeDrivers, total = totalDrivers },
            authorizationLimit = park.AuthorizationLimit, // null for Model A
            asOf = now,
        });
    }

    /// <summary>
    /// Recent activity for the park: cashout state changes + new-driver joins,
    /// merged and newest-first. Backs the admin overview's activity feed.
    /// </summary>
    [HttpGet("activity")]
    public async Task<IActionResult> Activity(
        Guid parkId, [FromQuery] int take = 12, CancellationToken ct = default)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        take = Math.Clamp(take, 1, 50);

        // Pull a slightly wider window from each source, merge, then trim — keeps
        // the resulting feed coherent even when one source has dominated lately.
        var window = take * 2;

        var cashoutRows = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.ParkId == parkId)
            .OrderByDescending(c => c.CompletedAt ?? c.CreatedAt)
            .Take(window)
            .Select(c => new
            {
                c.Id,
                At = c.CompletedAt ?? c.CreatedAt,
                c.Status,
                DriverName = c.Driver.Name,
                c.Amount,
                c.FailureReason,
            })
            .ToListAsync(ct);

        var driverRows = await _db.Drivers
            .AsNoTracking()
            .Where(d => d.ParkId == parkId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(window)
            .Select(d => new { d.Id, d.CreatedAt, d.Name })
            .ToListAsync(ct);

        var feed = cashoutRows.Select(c => new ActivityEntry(
                Id: "co_" + c.Id.ToString("N"),
                At: c.At,
                Type: c.Status switch
                {
                    Core.Enums.CashoutStatus.Completed       => "cashout_completed",
                    Core.Enums.CashoutStatus.Failed          => "cashout_failed",
                    Core.Enums.CashoutStatus.ReviewRequired  => "cashout_review",
                    _                                         => "cashout_submitted",
                },
                Severity: c.Status switch
                {
                    Core.Enums.CashoutStatus.Completed       => "success",
                    Core.Enums.CashoutStatus.Failed          => "error",
                    Core.Enums.CashoutStatus.ReviewRequired  => "warning",
                    _                                         => "info",
                },
                Message: c.Status switch
                {
                    Core.Enums.CashoutStatus.Completed      => "Cashout completed",
                    Core.Enums.CashoutStatus.Failed         => $"Cashout failed — {c.FailureReason ?? "see details"}",
                    Core.Enums.CashoutStatus.ReviewRequired => "Cashout needs manual review",
                    _                                        => "Cashout submitted",
                },
                DriverName: c.DriverName,
                Amount: c.Amount))
            .Concat(driverRows.Select(d => new ActivityEntry(
                Id: "drv_" + d.Id.ToString("N"),
                At: d.CreatedAt,
                Type: "driver_joined",
                Severity: "info",
                Message: "Driver onboarded to the park",
                DriverName: d.Name,
                Amount: null)))
            .OrderByDescending(e => e.At)
            .Take(take)
            .ToList();

        return Ok(new { parkId, count = feed.Count, events = feed });
    }

    /// <summary>
    /// Last 12 hours of completed-cashout volume bucketed by hour. Empty hours
    /// return as zero so the chart bar set is always 12 elements wide.
    /// </summary>
    [HttpGet("hourly")]
    public async Task<IActionResult> Hourly(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-12);

        var rows = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.ParkId == parkId
                     && c.Status == Core.Enums.CashoutStatus.Completed
                     && (c.CompletedAt ?? c.CreatedAt) >= windowStart)
            .Select(c => new
            {
                at = c.CompletedAt ?? c.CreatedAt,
                amount = c.Amount,
            })
            .ToListAsync(ct);

        // Bucket by hour-of-day. Each bucket spans [hour, hour+1).
        var buckets = new List<HourBucket>(12);
        for (var i = 11; i >= 0; i--)
        {
            var bucketStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-i);
            var bucketEnd = bucketStart.AddHours(1);
            var inBucket = rows.Where(r => r.at >= bucketStart && r.at < bucketEnd).ToList();
            buckets.Add(new HourBucket(
                Hour: bucketStart.ToString("HH:00"),
                Value: inBucket.Sum(r => r.amount),
                Count: inBucket.Count));
        }

        return Ok(new { parkId, asOf = now, buckets });
    }

    /// <summary>
    /// Financial-report data for the park over an arbitrary window.
    /// Returns headline tiles, day-by-day breakdown, and top drivers in one round trip
    /// so the frontend renders the whole page from a single fetch.
    /// </summary>
    [HttpGet("reports")]
    public async Task<IActionResult> Reports(
        Guid parkId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int topDrivers = 10,
        CancellationToken ct = default)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var now = DateTime.UtcNow;
        var windowTo   = (to   ?? now).ToUniversalTime();
        var windowFrom = (from ?? now.AddDays(-7)).ToUniversalTime();
        if (windowFrom >= windowTo)
            return BadRequest(new { error = "invalid_window", message = "`from` must be earlier than `to`." });

        topDrivers = Math.Clamp(topDrivers, 1, 50);

        var cashouts = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.ParkId == parkId
                     && c.CreatedAt >= windowFrom
                     && c.CreatedAt <  windowTo)
            .Select(c => new
            {
                c.Id,
                c.DriverId,
                DriverName = c.Driver.Name,
                c.BankCard.BankType,
                c.Amount,
                c.Fee,
                c.Status,
                c.CreatedAt,
                c.CompletedAt,
            })
            .ToListAsync(ct);

        var completed = cashouts.Where(c => c.Status == Core.Enums.CashoutStatus.Completed).ToList();
        var failed = cashouts.Where(c =>
            c.Status == Core.Enums.CashoutStatus.Failed ||
            c.Status == Core.Enums.CashoutStatus.ReviewRequired).ToList();

        var summary = new
        {
            count = completed.Count,
            value = completed.Sum(c => c.Amount),
            fees  = completed.Sum(c => c.Fee),
            net   = completed.Sum(c => c.Amount - c.Fee),
            failedCount = failed.Count,
            attempted   = cashouts.Count,
            successRate = cashouts.Count == 0 ? 0m
                : Math.Round((decimal)completed.Count / cashouts.Count * 100m, 1),
            uniqueDrivers = completed.Select(c => c.DriverId).Distinct().Count(),
        };

        // Daily breakdown — one row per day in [from, to). Drives the table + chart.
        var startDay = new DateTime(windowFrom.Year, windowFrom.Month, windowFrom.Day, 0, 0, 0, DateTimeKind.Utc);
        var endDay   = new DateTime(windowTo.Year,   windowTo.Month,   windowTo.Day,   0, 0, 0, DateTimeKind.Utc);
        // If windowTo carries time-of-day, include the partial day at the end.
        if (windowTo > endDay) endDay = endDay.AddDays(1);

        var daily = new List<DailyRow>();
        for (var d = startDay; d < endDay; d = d.AddDays(1))
        {
            var next = d.AddDays(1);
            var inDay = completed.Where(c => c.CreatedAt >= d && c.CreatedAt < next).ToList();
            var failedInDay = failed.Where(c => c.CreatedAt >= d && c.CreatedAt < next).ToList();
            daily.Add(new DailyRow(
                Date: d,
                CashoutsCount: inDay.Count,
                Value: inDay.Sum(c => c.Amount),
                Fees: inDay.Sum(c => c.Fee),
                FailedCount: failedInDay.Count));
        }

        var topByDriver = completed
            .GroupBy(c => new { c.DriverId, c.DriverName })
            .Select(g => new
            {
                driverId = g.Key.DriverId,
                driverName = g.Key.DriverName,
                cashoutsCount = g.Count(),
                totalValue = g.Sum(c => c.Amount),
                totalFees  = g.Sum(c => c.Fee),
            })
            .OrderByDescending(r => r.totalValue)
            .Take(topDrivers)
            .ToList();

        var byBank = completed
            .GroupBy(c => c.BankType)
            .Select(g => new
            {
                bankType = g.Key,
                cashoutsCount = g.Count(),
                value = g.Sum(c => c.Amount),
            })
            .OrderByDescending(g => g.value)
            .ToList();

        return Ok(new
        {
            parkId,
            windowFrom,
            windowTo,
            summary,
            daily,
            topDrivers = topByDriver,
            byBank,
        });
    }

    /// <summary>
    /// Look up a Yandex driver profile when onboarding. Hits the resilient client
    /// (rate-limited + retried + audit-logged) for the park's profiles and filters
    /// by <paramref name="profileId"/>.
    /// </summary>
    [HttpGet("drivers/yandex-lookup")]
    public async Task<IActionResult> YandexLookup(
        Guid parkId, [FromQuery] string profileId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (string.IsNullOrWhiteSpace(profileId))
            return BadRequest(new { error = "profile_id_required" });

        var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
        var match = profiles.FirstOrDefault(p =>
            string.Equals(p.DriverProfileId, profileId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return NotFound(new { error = "yandex_profile_not_found", profileId });

        // Flag if this profile is already linked to a PayTaxi driver in this park
        // so the UI can short-circuit the create step.
        var alreadyLinked = await _db.Drivers.AsNoTracking()
            .AnyAsync(d => d.ParkId == parkId && d.YandexDriverProfileId == match.DriverProfileId, ct);

        return Ok(new
        {
            yandexProfileId = match.DriverProfileId,
            name = match.Name,
            carPlate = match.CarPlate,
            balance = match.Balance,
            currency = match.Currency,
            alreadyLinked,
        });
    }

    /// <summary>
    /// Onboard a new driver: create the Driver row plus default bank cards.
    /// Phone is normalized to E.164 and hashed with SHA-256 for lookup.
    /// Phase 8 will swap the plaintext <c>PhoneEncrypted</c> column for real AES.
    /// </summary>
    [HttpPost("drivers")]
    public async Task<IActionResult> CreateDriver(
        Guid parkId, [FromBody] CreateDriverRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var phone = NormalizePhone(body.Phone);
        if (phone is null) return BadRequest(new { error = "invalid_phone" });
        if (string.IsNullOrWhiteSpace(body.YandexProfileId))
            return BadRequest(new { error = "yandex_profile_id_required" });
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "name_required" });

        var phoneHash = HashPhone(phone);

        if (await _db.Drivers.AsNoTracking().AnyAsync(d => d.PhoneHash == phoneHash, ct))
            return Conflict(new { error = "phone_already_registered" });

        if (await _db.Drivers.AsNoTracking().AnyAsync(d =>
                d.ParkId == parkId && d.YandexDriverProfileId == body.YandexProfileId, ct))
            return Conflict(new { error = "yandex_profile_already_linked" });

        var driver = new Core.Entities.Driver
        {
            ParkId = parkId,
            Name = body.Name.Trim(),
            YandexDriverProfileId = body.YandexProfileId.Trim(),
            PhoneEncrypted = phone, // plaintext until Phase 8
            PhoneHash = phoneHash,
            Status = Core.Enums.DriverStatus.Active,
            ConsentGiven = body.ConsentGiven,
            ConsentTimestamp = body.ConsentGiven ? DateTime.UtcNow : null,
        };
        _db.Drivers.Add(driver);

        // Seed two default mock cards so the driver can cashout immediately.
        // Real onboarding will collect card details via a separate "add card" flow;
        // this is a dev-time shortcut while Phase 4 bank tokenisation isn't wired.
        var rng = new Random(driver.Id.GetHashCode());
        var bogLast4 = rng.Next(1000, 10000).ToString();
        var tbcLast4 = rng.Next(1000, 10000).ToString();
        _db.BankCards.Add(new Core.Entities.BankCard
        {
            DriverId = driver.Id,
            MaskedPan = $"**** {bogLast4}",
            TokenReferenceEncrypted = $"mock_tok_bog_{driver.Id:N}",
            BankType = "BOG",
            IsDefault = true,
            IsActive = true,
        });
        _db.BankCards.Add(new Core.Entities.BankCard
        {
            DriverId = driver.Id,
            MaskedPan = $"**** {tbcLast4}",
            TokenReferenceEncrypted = $"mock_tok_tbc_{driver.Id:N}",
            BankType = "TBC",
            IsDefault = false,
            IsActive = true,
        });

        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Onboarded driver {DriverId} ({Name}) in park {ParkId}",
            driver.Id, driver.Name, parkId);

        return Created($"/api/admin/parks/{parkId}/drivers", new
        {
            id = driver.Id,
            name = driver.Name,
            yandexProfileId = driver.YandexDriverProfileId,
            status = driver.Status.ToString(),
            phone, // normalised — useful to confirm what the SMS will go to
            parkId,
        });
    }

    private static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length < 9) return null;
        if (!hasPlus && !digits.StartsWith("995") && digits.Length <= 10)
            digits = "995" + digits;
        return "+" + digits;
    }

    private static string HashPhone(string phone)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(phone));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [HttpGet("smoke-test")]
    public async Task<IActionResult> SmokeTest(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

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

public record CreateDriverRequest(
    string Phone,
    string YandexProfileId,
    string Name,
    bool ConsentGiven);

public record ActivityEntry(
    string Id,
    DateTime At,
    string Type,
    string Severity,
    string Message,
    string? DriverName,
    decimal? Amount);

public record HourBucket(
    string Hour,
    decimal Value,
    int Count);

public record DailyRow(
    DateTime Date,
    int CashoutsCount,
    decimal Value,
    decimal Fees,
    int FailedCount);
