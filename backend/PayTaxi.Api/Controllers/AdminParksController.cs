using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Banking;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Admin endpoints scoped to a park. Validates the full Yandex integration
/// stack: DB park resolution → resilient client (rate-limit, retry, audit) → mock.
///
/// Requires admin role. Every action calls <see cref="AdminControllerBase.CanAccessPark"/>:
/// super-admins and operators reach any park, park-admins only their own.
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
                cashoutFee = p.CashoutFee,
                minCashoutAmount = p.MinCashoutAmount,
                maxCashoutAmount = p.MaxCashoutAmount,
                dailyCashoutLimitPerDriver = p.DailyCashoutLimitPerDriver,
                swichSharePercent = p.SwichSharePercent,
                phase1SharePercent = p.Phase1SharePercent,
                phase1CapGel = p.Phase1CapGel,
                status = p.Status.ToString(),
                driverCount = p.Drivers.Count(),
                p.LegalEntityName,
                p.TaxId,
                p.Phone,
                p.BankAccountIban,
                yandexClientId = p.YandexClientIdEncrypted,
                yandexApiKeySet = p.YandexApiKeyEncrypted != null && p.YandexApiKeyEncrypted != "",
                bankAccounts = p.BankAccounts
                    .Where(a => a.IsActive)
                    .OrderByDescending(a => a.IsPrimary)
                    .Select(a => new
                    {
                        id = a.Id,
                        bankCode = a.BankCode,
                        // Translatable inline mapping (a static helper call would not be SQL-translatable here).
                        bankLabel = a.BankCode == "TB" ? "TBC" : a.BankCode == "BG" ? "BOG" : a.BankCode == "LB" ? "LIBERTY" : a.BankCode,
                        provider = a.Provider,
                        iban = a.Iban,
                        holderName = a.HolderName,
                        isPrimary = a.IsPrimary,
                        label = a.Label,
                    }),
            })
            .ToListAsync(ct);

        return Ok(parks);
    }

    /// <summary>
    /// Create a new park (super-admin only) and, optionally, its first park-admin login.
    /// This is the "register a taxi park" step — replaces editing the seed file.
    /// Yandex credentials are stored on the park (plaintext placeholder until Phase 8 AES).
    /// </summary>
    [HttpPost("/api/admin/parks")]
    public async Task<IActionResult> CreatePark([FromBody] CreateParkRequest body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "name_required" });

        var slug = SeedData.ToSlug(string.IsNullOrWhiteSpace(body.Slug) ? name : body.Slug!);
        if (slug.Length == 0) return BadRequest(new { error = "invalid_slug" });
        if (await _db.Parks.AsNoTracking().AnyAsync(p => p.Slug == slug, ct))
            return Conflict(new { error = "slug_taken" });

        if (string.IsNullOrWhiteSpace(body.YandexParkId))
            return BadRequest(new { error = "yandex_park_id_required" });

        // Model A is the launch model (PAYTAXI-CONTEXT.md §3). Other values are
        // accepted only for historical compatibility.
        if (!Enum.TryParse<Core.Enums.OperatingModel>(body.OperatingModel, ignoreCase: true, out var model))
            model = Core.Enums.OperatingModel.ModelA;

        var cashoutFee = body.CashoutFee ?? 0.50m;
        if (cashoutFee < 0 || cashoutFee > 50) return BadRequest(new { error = "invalid_fee" });
        var minCashout = body.MinCashoutAmount ?? 5m;
        if (minCashout <= cashoutFee) return BadRequest(new { error = "invalid_min_cashout" });
        if (body.MaxCashoutAmount is { } maxC && maxC < minCashout) return BadRequest(new { error = "invalid_max_cashout" });
        if (body.DailyCashoutLimitPerDriver is { } dl && dl < minCashout) return BadRequest(new { error = "invalid_daily_limit" });

        // Optional billing fields — validate the same way as the PATCH endpoint.
        var taxId = Blank(body.TaxId ?? "");
        if (taxId is not null)
        {
            var digits = new string(taxId.Where(char.IsDigit).ToArray());
            if (digits.Length is < 9 or > 11) return BadRequest(new { error = "invalid_tax_id" });
            taxId = digits;
        }

        string? phone = null;
        if (Blank(body.Phone ?? "") is { } rawPhone)
        {
            phone = NormalizePhone(rawPhone);
            if (phone is null) return BadRequest(new { error = "invalid_phone" });
        }

        // The park's payout account. Required: without it no driver can be paid.
        if (!GeorgianIban.TryParse(body.BankAccountIban, out var iban, out var parkBankCode, out var ibanError))
            return BadRequest(new { error = ibanError == "iban_required" ? "iban_required" : "invalid_iban" });

        // Manager login is optional but recommended.
        string? managerEmail = Blank(body.ManagerEmail ?? "")?.ToLowerInvariant();
        if (managerEmail is not null)
        {
            if (!managerEmail.Contains('@')) return BadRequest(new { error = "invalid_email" });
            if (string.IsNullOrWhiteSpace(body.ManagerPassword) || body.ManagerPassword!.Length < 8)
                return BadRequest(new { error = "weak_password" });
            if (await _db.AdminUsers.AsNoTracking().AnyAsync(a => a.Email == managerEmail, ct))
                return Conflict(new { error = "email_taken" });
        }

        var provider = (Blank(body.BankProvider ?? "") ?? ProviderForBankCode(parkBankCode)).ToLowerInvariant();
        var legalEntity = Blank(body.LegalEntityName ?? "");
        var park = new Core.Entities.Park
        {
            Name = name,
            Slug = slug,
            LegalEntityName = legalEntity,
            TaxId = taxId,
            Phone = phone,
            YandexParkId = body.YandexParkId.Trim(),
            YandexClientIdEncrypted = body.YandexClientId?.Trim() ?? "",
            YandexApiKeyEncrypted = body.YandexApiKey?.Trim() ?? "",
            OperatingModel = model,
            CashoutFee = cashoutFee,
            MinCashoutAmount = minCashout,
            MaxCashoutAmount = body.MaxCashoutAmount,
            DailyCashoutLimitPerDriver = body.DailyCashoutLimitPerDriver,
            BankProvider = provider,
            BankType = provider.ToUpperInvariant(),
            BankCredentialsEncrypted = "{}",
            BankAccountIban = iban,
            Status = Core.Enums.ParkStatus.Active,
        };
#pragma warning disable CS0618 // legacy bool mirror
        park.IsActive = true;
#pragma warning restore CS0618
        _db.Parks.Add(park);

        _db.ParkBankAccounts.Add(new Core.Entities.ParkBankAccount
        {
            ParkId = park.Id,
            BankCode = parkBankCode,
            Provider = provider,
            Iban = iban,
            HolderName = legalEntity ?? name,
            CredentialsEncrypted = Blank(body.BankCredentialsJson ?? "") ?? "{}",
            IsActive = true,
            IsPrimary = true,
            Label = $"{GeorgianIban.BankLabel(parkBankCode)} business account",
        });

        if (managerEmail is not null)
        {
            _db.AdminUsers.Add(new Core.Entities.AdminUser
            {
                Email = managerEmail,
                Name = Blank(body.ManagerName ?? "") ?? $"Manager · {name}",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.ManagerPassword!),
                Role = "park_admin",
                ParkId = park.Id,
                IsActive = true,
            });
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Park {ParkId} ({Name}) created by super-admin; manager={Manager}",
            park.Id, park.Name, managerEmail ?? "(none)");

        return Created($"/api/admin/parks/{park.Id}", new
        {
            id = park.Id,
            name = park.Name,
            slug = park.Slug,
            operatingModel = park.OperatingModel.ToString(),
            managerEmail,
        });
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
            phone = d.PhoneEncrypted, // plaintext until Phase 8 — same column the JWT uses
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
                    bankCode = b.BankCode,
                    iban = b.Iban,
                    holderName = b.HolderName,
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

        // "Today" is the park's local day (Tbilisi), the same boundary the driver daily limit
        // and the nightly settlement use — not the UTC day (which rolls over at 04:00 local).
        var now = DateTime.UtcNow;
        var tz = PayTaxi.Infrastructure.Services.SettlementService.ResolveTimeZone(null);
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(now, tz).Date, tz);

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

        // Queue depth is not day-bounded: a payout stuck since yesterday still matters.
        var queuedAll = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.ParkId == parkId && c.Status == Core.Enums.CashoutStatus.Queued)
            .Select(c => new { c.Amount, c.Fee, c.CreatedAt })
            .ToListAsync(ct);

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
            cashoutFee = park.CashoutFee,
            queued = new
            {
                count = queuedAll.Count,
                value = queuedAll.Sum(c => c.Amount - c.Fee),
                oldestAt = queuedAll.Count == 0 ? (DateTime?)null : queuedAll.Min(c => c.CreatedAt),
            },
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

        // Optional payout destination collected at onboarding. Otherwise the driver
        // adds their own IBAN in the app before the first cashout.
        if (!string.IsNullOrWhiteSpace(body.Iban))
        {
            if (!GeorgianIban.TryParse(body.Iban, out var iban, out var bankCode, out var ibanError))
                return BadRequest(new { error = ibanError });
            var supported = await _db.ParkBankAccounts.AsNoTracking()
                .AnyAsync(a => a.ParkId == parkId && a.IsActive && a.BankCode == bankCode, ct);
            if (!supported)
                return BadRequest(new { error = "bank_not_supported", bankCode, bankLabel = GeorgianIban.BankLabel(bankCode) });
            _db.BankCards.Add(new Core.Entities.BankCard
            {
                DriverId = driver.Id,
                Iban = iban,
                IbanHash = PayTaxi.Infrastructure.Security.FieldEncryptor.Hash(iban),
                BankCode = bankCode,
                BankType = GeorgianIban.BankLabel(bankCode),
                MaskedPan = GeorgianIban.Mask(iban),
                HolderName = Blank(body.HolderName ?? "") ?? driver.Name,
                TokenReferenceEncrypted = "",
                IsDefault = true,
                IsActive = true,
            });
        }

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

    /// <summary>
    /// The park's full driver roster as Yandex Fleet sees it, flagged with whether
    /// each profile is already onboarded to PayTaxi. Powers the "Sync from Yandex" UI.
    /// </summary>
    [HttpGet("yandex-roster")]
    public async Task<IActionResult> YandexRoster(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);

        var onboarded = (await _db.Drivers.AsNoTracking()
            .Where(d => d.ParkId == parkId)
            .Select(d => d.YandexDriverProfileId)
            .ToListAsync(ct)).ToHashSet();

        var roster = profiles.Select(p => new
        {
            yandexProfileId = p.DriverProfileId,
            name = p.Name,
            carPlate = p.CarPlate,
            phone = p.Phone,
            balance = p.Balance,
            currency = p.Currency,
            alreadyOnboarded = onboarded.Contains(p.DriverProfileId),
        }).ToList();

        return Ok(new { parkId, count = roster.Count, drivers = roster });
    }

    /// <summary>
    /// Bulk-onboard selected drivers straight from the Yandex roster — no CSV.
    /// Pulls name + phone from Yandex, creates Driver rows + default cards, and
    /// reports which profiles were created vs skipped (already onboarded, no phone, etc.).
    /// </summary>
    [HttpPost("drivers/bulk")]
    public async Task<IActionResult> BulkOnboard(
        Guid parkId, [FromBody] BulkOnboardRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body?.YandexProfileIds is null || body.YandexProfileIds.Length == 0)
            return BadRequest(new { error = "no_profiles_selected" });

        var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
        var byId = profiles.ToDictionary(p => p.DriverProfileId);

        var taken = (await _db.Drivers.AsNoTracking()
            .Where(d => d.ParkId == parkId)
            .Select(d => d.YandexDriverProfileId)
            .ToListAsync(ct)).ToHashSet();

        var created = new List<object>();
        var skipped = new List<object>();

        foreach (var pid in body.YandexProfileIds.Distinct())
        {
            if (!byId.TryGetValue(pid, out var profile))
            { skipped.Add(new { yandexProfileId = pid, reason = "not_in_yandex" }); continue; }
            if (taken.Contains(pid))
            { skipped.Add(new { yandexProfileId = pid, reason = "already_onboarded" }); continue; }

            var phone = NormalizePhone(profile.Phone);
            if (phone is null)
            { skipped.Add(new { yandexProfileId = pid, reason = "no_phone" }); continue; }

            var phoneHash = HashPhone(phone);
            if (await _db.Drivers.AsNoTracking().AnyAsync(d => d.PhoneHash == phoneHash, ct))
            { skipped.Add(new { yandexProfileId = pid, reason = "phone_taken" }); continue; }

            var driver = new Core.Entities.Driver
            {
                ParkId = parkId,
                Name = profile.Name?.Trim() ?? "(unnamed)",
                YandexDriverProfileId = pid,
                PhoneEncrypted = phone, // plaintext until Phase 8
                PhoneHash = phoneHash,
                Status = Core.Enums.DriverStatus.Active,
                ConsentGiven = true,
                ConsentTimestamp = DateTime.UtcNow,
            };
            _db.Drivers.Add(driver);
            // No payout destination yet: drivers add their own IBAN in the app
            // (or an operator adds one via POST drivers/{id}/cards).

            taken.Add(pid); // guard against duplicate ids within the same request
            created.Add(new { id = driver.Id, yandexProfileId = pid, name = driver.Name, phone });
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Bulk onboarded {Created} drivers in park {ParkId} ({Skipped} skipped)",
            created.Count, parkId, skipped.Count);

        return Ok(new
        {
            createdCount = created.Count,
            skippedCount = skipped.Count,
            created,
            skipped,
        });
    }

    /// <summary>
    /// Edit an existing driver. Any field omitted from the body is left untouched
    /// (partial update). Re-hashes phone on change and rejects duplicates.
    /// </summary>
    [HttpPatch("drivers/{driverId:guid}")]
    public async Task<IActionResult> UpdateDriver(
        Guid parkId, Guid driverId, [FromBody] UpdateDriverRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var driver = await _db.Drivers
            .FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });

        if (body.Name is not null)
        {
            var trimmed = body.Name.Trim();
            if (trimmed.Length == 0) return BadRequest(new { error = "name_required" });
            driver.Name = trimmed;
        }

        if (body.Phone is not null)
        {
            var phone = NormalizePhone(body.Phone);
            if (phone is null) return BadRequest(new { error = "invalid_phone" });
            var hash = HashPhone(phone);
            if (hash != driver.PhoneHash)
            {
                var taken = await _db.Drivers.AsNoTracking()
                    .AnyAsync(d => d.Id != driverId && d.PhoneHash == hash, ct);
                if (taken) return Conflict(new { error = "phone_already_registered" });
                driver.PhoneEncrypted = phone;
                driver.PhoneHash = hash;
            }
        }

        if (body.YandexProfileId is not null)
        {
            var trimmed = body.YandexProfileId.Trim();
            if (trimmed.Length == 0) return BadRequest(new { error = "yandex_profile_id_required" });
            if (trimmed != driver.YandexDriverProfileId)
            {
                var taken = await _db.Drivers.AsNoTracking()
                    .AnyAsync(d => d.Id != driverId && d.ParkId == parkId && d.YandexDriverProfileId == trimmed, ct);
                if (taken) return Conflict(new { error = "yandex_profile_already_linked" });
                driver.YandexDriverProfileId = trimmed;
            }
        }

        if (body.Status is not null)
        {
            if (!Enum.TryParse<Core.Enums.DriverStatus>(body.Status, ignoreCase: true, out var parsed))
                return BadRequest(new { error = "invalid_status", allowed = Enum.GetNames<Core.Enums.DriverStatus>() });
            driver.Status = parsed;
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Driver {DriverId} updated by admin ({Name} | status={Status})",
            driver.Id, driver.Name, driver.Status);

        return Ok(new
        {
            id = driver.Id,
            name = driver.Name,
            phone = driver.PhoneEncrypted,
            yandexProfileId = driver.YandexDriverProfileId,
            status = driver.Status.ToString(),
            parkId = driver.ParkId,
        });
    }

    /// <summary>
    /// Update the park's account/billing details (legal entity, tax ID, phone, IBAN).
    /// Surfaced on the admin Settings page. Each field is optional; sending an empty
    /// string clears it, omitting it (null) leaves it unchanged.
    /// </summary>
    [HttpPatch("")]
    public async Task<IActionResult> UpdatePark(
        Guid parkId, [FromBody] UpdateParkRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var park = await _db.Parks.FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        if (body.LegalEntityName is not null)
            park.LegalEntityName = Blank(body.LegalEntityName);

        if (body.TaxId is not null)
        {
            var tax = Blank(body.TaxId);
            if (tax is not null)
            {
                var digits = new string(tax.Where(char.IsDigit).ToArray());
                // Georgian company IDs are 9 digits; sole-proprietor personal IDs are 11.
                if (digits.Length is < 9 or > 11) return BadRequest(new { error = "invalid_tax_id" });
                tax = digits;
            }
            park.TaxId = tax;
        }

        if (body.Phone is not null)
        {
            var raw = Blank(body.Phone);
            if (raw is null) park.Phone = null;
            else
            {
                var phone = NormalizePhone(raw);
                if (phone is null) return BadRequest(new { error = "invalid_phone" });
                park.Phone = phone;
            }
        }

        if (body.BankAccountIban is not null)
        {
            var iban = Blank(body.BankAccountIban);
            if (iban is not null)
            {
                iban = iban.Replace(" ", "").ToUpperInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(iban, "^[A-Z]{2}[0-9A-Z]{13,32}$"))
                    return BadRequest(new { error = "invalid_iban" });
            }
            park.BankAccountIban = iban;
        }

        // ── Yandex Fleet credentials ──────────────────────────────────
        // Client ID and Park ID are shown in the UI and editable like normal fields.
        // The API key is write-only: a blank value means "leave unchanged" (the UI
        // never receives the stored key back), a non-blank value replaces it.
        if (body.YandexClientId is not null)
            park.YandexClientIdEncrypted = body.YandexClientId.Trim();

        if (body.YandexParkId is not null)
        {
            var yp = body.YandexParkId.Trim();
            if (yp.Length == 0) return BadRequest(new { error = "yandex_park_id_required" });
            park.YandexParkId = yp;
        }

        if (!string.IsNullOrWhiteSpace(body.YandexApiKey))
            park.YandexApiKeyEncrypted = body.YandexApiKey.Trim();

        // ── Fee (Swich only — contractually fixed per park) & limits ─
        if (body.CashoutFee is { } fee)
        {
            if (!IsSuperAdmin) return Forbid();
            if (fee < 0 || fee > 50) return BadRequest(new { error = "invalid_fee" });
            park.CashoutFee = Math.Round(fee, 2);
        }
        if (body.MinCashoutAmount is { } minC)
        {
            if (minC <= 0) return BadRequest(new { error = "invalid_min_cashout" });
            park.MinCashoutAmount = Math.Round(minC, 2);
        }
        if (body.MaxCashoutAmount is not null)
            park.MaxCashoutAmount = body.MaxCashoutAmount <= 0 ? null : Math.Round(body.MaxCashoutAmount.Value, 2);
        if (body.DailyCashoutLimitPerDriver is not null)
            park.DailyCashoutLimitPerDriver = body.DailyCashoutLimitPerDriver <= 0 ? null : Math.Round(body.DailyCashoutLimitPerDriver.Value, 2);

        // ── Revenue split (Swich only) ────────────────────────────────
        if (body.SwichSharePercent is not null || body.Phase1SharePercent is not null || body.Phase1CapGel is not null)
        {
            if (!IsSuperAdmin) return Forbid();
            if (body.SwichSharePercent is { } sp)
            {
                if (sp < 0 || sp > 100) return BadRequest(new { error = "invalid_share" });
                park.SwichSharePercent = Math.Round(sp, 2);
            }
            if (body.Phase1SharePercent is { } p1)
            {
                // Negative = clear phase 1.
                if (p1 > 100) return BadRequest(new { error = "invalid_share" });
                park.Phase1SharePercent = p1 < 0 ? null : Math.Round(p1, 2);
            }
            if (body.Phase1CapGel is not null)
                park.Phase1CapGel = body.Phase1CapGel <= 0 ? null : Math.Round(body.Phase1CapGel.Value, 2);
            if (park.Phase1CapGel is null) park.Phase1SharePercent = null;
        }

        if (park.MinCashoutAmount <= park.CashoutFee) return BadRequest(new { error = "invalid_min_cashout" });
        if (park.MaxCashoutAmount is { } mx && mx < park.MinCashoutAmount) return BadRequest(new { error = "invalid_max_cashout" });
        if (park.DailyCashoutLimitPerDriver is { } dlim && dlim < park.MinCashoutAmount) return BadRequest(new { error = "invalid_daily_limit" });

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Park {ParkId} account details updated by admin", park.Id);

        return Ok(new
        {
            id = park.Id,
            name = park.Name,
            legalEntityName = park.LegalEntityName,
            taxId = park.TaxId,
            phone = park.Phone,
            bankAccountIban = park.BankAccountIban,
            yandexClientId = park.YandexClientIdEncrypted,
            yandexParkId = park.YandexParkId,
            yandexApiKeySet = !string.IsNullOrEmpty(park.YandexApiKeyEncrypted),
            cashoutFee = park.CashoutFee,
            minCashoutAmount = park.MinCashoutAmount,
            maxCashoutAmount = park.MaxCashoutAmount,
            dailyCashoutLimitPerDriver = park.DailyCashoutLimitPerDriver,
            swichSharePercent = park.SwichSharePercent,
            phase1SharePercent = park.Phase1SharePercent,
            phase1CapGel = park.Phase1CapGel,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Park payout accounts (one per bank; TBC at launch, BoG in Phase 2)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The park's payout accounts with a live balance read where the rail exposes it.
    /// Credentials are never returned — only whether they are set.
    /// </summary>
    [HttpGet("bank-accounts")]
    public async Task<IActionResult> ListBankAccounts(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        var accounts = await _db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == parkId)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.BankCode)
            .ToListAsync(ct);

        var queuedByAccount = await _db.Cashouts.AsNoTracking()
            .Where(c => c.ParkId == parkId && c.Status == Core.Enums.CashoutStatus.Queued && c.ParkBankAccountId != null)
            .GroupBy(c => c.ParkBankAccountId!.Value)
            .Select(g => new { AccountId = g.Key, Count = g.Count(), Net = g.Sum(c => c.Amount - c.Fee) })
            .ToDictionaryAsync(g => g.AccountId, ct);

        var rows = new List<object>(accounts.Count);
        foreach (var a in accounts)
        {
            decimal? balance = null;
            string? balanceError = null;
            if (a.IsActive)
            {
                try
                {
                    var adapter = HttpContext.RequestServices.GetKeyedService<IBankPayoutAdapter>(a.Provider)
                        ?? HttpContext.RequestServices.GetKeyedService<IBankPayoutAdapter>(a.Provider.ToUpperInvariant());
                    if (adapter is not null)
                        balance = await adapter.GetBalanceAsync(new BankAccountContext(
                            parkId, a.Id, a.Provider, a.BankCode, a.Iban,
                            a.HolderName ?? park.LegalEntityName ?? park.Name, a.CredentialsEncrypted), ct);
                }
                catch (NotImplementedException) { balanceError = "adapter_not_implemented"; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Balance read failed for account {Account}", a.Id);
                    balanceError = "balance_unavailable";
                }
            }

            queuedByAccount.TryGetValue(a.Id, out var q);
            rows.Add(new
            {
                id = a.Id,
                bankCode = a.BankCode,
                bankLabel = GeorgianIban.BankLabel(a.BankCode),
                provider = a.Provider,
                iban = a.Iban,
                holderName = a.HolderName,
                label = a.Label,
                isActive = a.IsActive,
                isPrimary = a.IsPrimary,
                credentialsSet = !string.IsNullOrWhiteSpace(a.CredentialsEncrypted) && a.CredentialsEncrypted != "{}",
                balance,
                balanceError,
                queuedCount = q?.Count ?? 0,
                queuedNet = q?.Net ?? 0m,
                createdAt = a.CreatedAt,
            });
        }

        return Ok(new { parkId, count = rows.Count, accounts = rows });
    }

    /// <summary>
    /// Add a payout account. Body: { iban, provider?, holderName?, label?, credentialsJson?, isPrimary? }.
    /// The bank code is read from the IBAN; provider defaults from it (TB→tbc, BG→bog).
    /// </summary>
    [HttpPost("bank-accounts")]
    public async Task<IActionResult> CreateBankAccount(
        Guid parkId, [FromBody] UpsertBankAccountRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var park = await _db.Parks.FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        if (!GeorgianIban.TryParse(body.Iban, out var iban, out var bankCode, out var ibanError))
            return BadRequest(new { error = ibanError });

        if (await _db.ParkBankAccounts.AsNoTracking().AnyAsync(a => a.ParkId == parkId && a.Iban == iban, ct))
            return Conflict(new { error = "iban_already_added" });

        var provider = (Blank(body.Provider ?? "") ?? ProviderForBankCode(bankCode)).ToLowerInvariant();
        if (provider is not ("tbc" or "bog" or "mock"))
            return BadRequest(new { error = "invalid_provider", allowed = new[] { "tbc", "bog", "mock" } });

        var hasAny = await _db.ParkBankAccounts.AnyAsync(a => a.ParkId == parkId && a.IsActive, ct);
        var makePrimary = body.IsPrimary ?? !hasAny;
        if (makePrimary)
        {
            await _db.ParkBankAccounts
                .Where(a => a.ParkId == parkId && a.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsPrimary, false), ct);
        }

        var account = new Core.Entities.ParkBankAccount
        {
            ParkId = parkId,
            BankCode = bankCode,
            Provider = provider,
            Iban = iban,
            HolderName = Blank(body.HolderName ?? "") ?? park.LegalEntityName ?? park.Name,
            Label = Blank(body.Label ?? "") ?? $"{GeorgianIban.BankLabel(bankCode)} account",
            CredentialsEncrypted = Blank(body.CredentialsJson ?? "") ?? "{}",
            IsActive = true,
            IsPrimary = makePrimary,
        };
        _db.ParkBankAccounts.Add(account);

        if (makePrimary)
        {
            park.BankAccountIban = iban;
            park.BankProvider = provider;
            park.BankType = provider.ToUpperInvariant();
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Park {ParkId}: payout account {Account} added ({Bank}/{Provider}, primary={Primary})",
            parkId, account.Id, bankCode, provider, makePrimary);

        return Created($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}", new
        {
            id = account.Id,
            bankCode,
            bankLabel = GeorgianIban.BankLabel(bankCode),
            provider,
            iban,
            holderName = account.HolderName,
            label = account.Label,
            isActive = true,
            isPrimary = makePrimary,
        });
    }

    /// <summary>
    /// Edit a payout account. Credentials are write-only: blank keeps the current value.
    /// Setting isActive=false takes the account out of routing (queued payouts re-route or wait).
    /// </summary>
    [HttpPatch("bank-accounts/{accountId:guid}")]
    public async Task<IActionResult> UpdateBankAccount(
        Guid parkId, Guid accountId, [FromBody] UpsertBankAccountRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var account = await _db.ParkBankAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ParkId == parkId, ct);
        if (account is null) return NotFound(new { error = "account_not_found" });
        var park = await _db.Parks.FirstAsync(p => p.Id == parkId, ct);

        if (body.HolderName is not null) account.HolderName = Blank(body.HolderName);
        if (body.Label is not null) account.Label = Blank(body.Label);
        if (!string.IsNullOrWhiteSpace(body.CredentialsJson)) account.CredentialsEncrypted = body.CredentialsJson.Trim();
        if (body.Provider is not null)
        {
            var provider = body.Provider.Trim().ToLowerInvariant();
            if (provider is not ("tbc" or "bog" or "mock"))
                return BadRequest(new { error = "invalid_provider", allowed = new[] { "tbc", "bog", "mock" } });
            account.Provider = provider;
        }
        if (body.IsActive is { } active) account.IsActive = active;
        if (body.IsPrimary == true && !account.IsPrimary)
        {
            await _db.ParkBankAccounts
                .Where(a => a.ParkId == parkId && a.IsPrimary && a.Id != accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsPrimary, false), ct);
            account.IsPrimary = true;
        }
        if (account.IsPrimary)
        {
            park.BankAccountIban = account.Iban;
            park.BankProvider = account.Provider;
            park.BankType = account.Provider.ToUpperInvariant();
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            id = account.Id,
            bankCode = account.BankCode,
            bankLabel = GeorgianIban.BankLabel(account.BankCode),
            provider = account.Provider,
            iban = account.Iban,
            holderName = account.HolderName,
            label = account.Label,
            isActive = account.IsActive,
            isPrimary = account.IsPrimary,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Driver payout destinations (operator-side)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Add a payout IBAN for a driver on their behalf (onboarding desk).</summary>
    [HttpPost("drivers/{driverId:guid}/cards")]
    public async Task<IActionResult> AddDriverCard(
        Guid parkId, Guid driverId, [FromBody] AddDestinationRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var driver = await _db.Drivers.FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });

        var (result, error, payload) = await DestinationHelper.AddAsync(_db, parkId, driver, body.Iban, body.HolderName, body.MakeDefault ?? true, ct);
        return result switch
        {
            DestinationHelper.Outcome.Created => Created($"/api/admin/parks/{parkId}/drivers/{driverId}/cards", payload),
            DestinationHelper.Outcome.Conflict => Conflict(new { error }),
            _ => BadRequest(payload ?? new { error }),
        };
    }

    /// <summary>Remove (deactivate) a driver's payout destination.</summary>
    [HttpDelete("drivers/{driverId:guid}/cards/{cardId:guid}")]
    public async Task<IActionResult> RemoveDriverCard(Guid parkId, Guid driverId, Guid cardId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        var inPark = await _db.Drivers.AsNoTracking().AnyAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (!inPark) return NotFound(new { error = "driver_not_found" });
        var ok = await DestinationHelper.DeactivateAsync(_db, driverId, cardId, ct);
        return ok ? NoContent() : NotFound(new { error = "card_not_found" });
    }

    private static string ProviderForBankCode(string bankCode) => bankCode switch
    {
        "TB" => "tbc",
        "BG" => "bog",
        _ => "mock",
    };

    private static string? Blank(string s)
    {
        var trimmed = s.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // One normaliser for every phone that ever becomes a PhoneHash (login uses the same).
    private static string? NormalizePhone(string? raw) => Core.Identity.GeorgianPhone.Normalize(raw);
    private static string HashPhone(string phone) => Core.Identity.GeorgianPhone.Hash(phone);
}

public record CreateDriverRequest(
    string Phone,
    string YandexProfileId,
    string Name,
    bool ConsentGiven,
    string? Iban = null,
    string? HolderName = null);

public record AddDestinationRequest(string Iban, string? HolderName, bool? MakeDefault);

public record UpsertBankAccountRequest(
    string? Iban,
    string? Provider,
    string? HolderName,
    string? Label,
    string? CredentialsJson,
    bool? IsPrimary,
    bool? IsActive);

public record UpdateDriverRequest(
    string? Name,
    string? Phone,
    string? YandexProfileId,
    string? Status);

public record UpdateParkRequest(
    string? LegalEntityName,
    string? TaxId,
    string? Phone,
    string? BankAccountIban,
    string? YandexClientId = null,
    string? YandexApiKey = null,
    string? YandexParkId = null,
    decimal? CashoutFee = null,
    decimal? MinCashoutAmount = null,
    decimal? MaxCashoutAmount = null,
    decimal? DailyCashoutLimitPerDriver = null,
    decimal? SwichSharePercent = null,
    decimal? Phase1SharePercent = null,
    decimal? Phase1CapGel = null);

public record CreateParkRequest(
    string? Name,
    string? Slug,
    string? OperatingModel,
    string? BankProvider,
    string? BankCredentialsJson,
    string? YandexClientId,
    string? YandexApiKey,
    string? YandexParkId,
    string? LegalEntityName,
    string? TaxId,
    string? Phone,
    string? BankAccountIban,
    string? ManagerEmail,
    string? ManagerName,
    string? ManagerPassword,
    decimal? CashoutFee = null,
    decimal? MinCashoutAmount = null,
    decimal? MaxCashoutAmount = null,
    decimal? DailyCashoutLimitPerDriver = null);

public record BulkOnboardRequest(string[] YandexProfileIds);

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
