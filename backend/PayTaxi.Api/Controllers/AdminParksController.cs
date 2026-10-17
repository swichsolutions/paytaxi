using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Banking;
using PayTaxi.Core.Identity;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Admin endpoints scoped to a park. Validates the full Yandex integration
/// stack: DB park resolution → resilient client (rate-limit, retry, audit) → mock.
///
/// Requires admin role. Every action calls <see cref="AdminControllerBase.CanAccessPark"/>:
/// super-admins reach any park, operators the parks of their own licensee in force, park-admins only their own.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/parks/{parkId:guid}")]
public class AdminParksController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly IYandexFleetClient _yandex;
    private readonly Infrastructure.Adapters.Banks.BankRailPolicy _rails;
    private readonly ILogger<AdminParksController> _log;

    public AdminParksController(
        AppDbContext db,
        IYandexFleetClient yandex,
        Infrastructure.Adapters.Banks.BankRailPolicy rails,
        ILogger<AdminParksController> log)
    {
        _db = db;
        _yandex = yandex;
        _rails = rails;
        _log = log;
    }

    /// <summary>List parks (top-level helper so the UI can pick one).</summary>
    [HttpGet("/api/admin/parks")]
    public async Task<IActionResult> ListParks(CancellationToken ct)
    {
        if (ParkScopeMissing) return Forbid();
        var query = _db.Parks.AsNoTracking().AsQueryable();

        // Park-admins see their own park, an operator its licensee's parks (none while the licensee is not in force),
        // Swich every park.
        var parks = ParkFilter;
        if (parks is not null) query = query.Where(p => parks.Contains(p.Id));
        var seesAll = IsSwichOrOperator;
        var today = SettlementService.TodayLocal();

        var rows = await query
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
                // Settled under the licence agreement with this licensee; null = Swich-direct (own split above).
                licenseeId = p.LicenseeId,
                licenseeName = p.Licensee != null ? p.Licensee.Name : null,
                status = p.Status.ToString(),
                statusReason = p.StatusReason,
                statusChangedAt = p.StatusChangedAt,
                // Who pressed the button is Swich/operator business; the park sees why and when.
                statusChangedBy = seesAll ? p.StatusChangedBy : null,
                // Annex 3 §7: the park's revocation of the platform's authorisations, when recorded — shown to everyone who
                // sees the park (from that day nothing is paid or collected from its account); who recorded it, likewise,
                // only to Swich and the operator.
                authorisationNoticeOn = p.AuthorisationNoticeOn,
                authorisationRevokedFrom = p.AuthorisationRevokedFrom,
                authorisationRevoked = p.AuthorisationRevokedFrom != null && p.AuthorisationRevokedFrom <= today,
                authorisationRevocationReason = p.AuthorisationRevocationReason,
                authorisationRevocationRecordedBy = seesAll ? p.AuthorisationRevocationRecordedBy : null,
                authorisationRevocationRecordedAt = p.AuthorisationRevocationRecordedAt,
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

        return Ok(rows);
    }

    /// <summary>
    /// Create a new park (super-admin only) and, optionally, its first park-admin login.
    /// This is the "register a taxi park" step — replaces editing the seed file.
    /// Yandex credentials are stored on the park (plaintext placeholder until Phase 8 AES).
    ///
    /// Which agreement settles it is never left to chance: with exactly one licensee in force the park joins that
    /// licensee's agreement (every park the licensee connects is under it, §2.2) unless Swich sends direct=true;
    /// with several, Swich must name one (licenseeId) or say direct; with none, there is no agreement to join.
    /// A park silently left Swich-direct would be settled under its own split from the next run day — stamping its
    /// cashouts for good, outside the licensee's statement and barrier.
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

        // The park's legal details — validated the same way as the PATCH endpoint, and required: the park is created
        // Active, and the driver's privacy notice names it as the controller of the driver's data (see
        // MissingLegalDetails).
        var taxId = Blank(body.TaxId ?? "");
        if (taxId is not null)
        {
            taxId = NormalizeTaxId(taxId);
            if (taxId is null) return BadRequest(new { error = "invalid_tax_id" });
        }

        string? phone = null;
        if (Blank(body.Phone ?? "") is { } rawPhone)
        {
            phone = NormalizePhone(rawPhone);
            if (phone is null) return BadRequest(new { error = "invalid_phone" });
        }

        var legalEntity = Blank(body.LegalEntityName ?? "");
        var missingLegal = MissingLegalDetails(legalEntity, taxId, phone);
        if (missingLegal.Length > 0) return LegalDetailsRequired(missingLegal);

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

        Guid? licenseeId = null;
        string? licenseeName = null;
        if (body.Direct == true)
        {
            if (body.LicenseeId is not null) return BadRequest(new { error = "licensee_conflict" });
        }
        else if (body.LicenseeId is { } chosen)
        {
            licenseeName = await _db.Licensees.AsNoTracking().Where(l => l.Id == chosen).Select(l => l.Name).FirstOrDefaultAsync(ct);
            if (licenseeName is null) return BadRequest(new { error = "licensee_not_found" });
            licenseeId = chosen;
        }
        else
        {
            var inForce = await _db.Licensees.AsNoTracking().Where(l => l.IsActive)
                .OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).Take(2).ToListAsync(ct);
            if (inForce.Count > 1) return BadRequest(new { error = "licensee_required" });
            if (inForce.Count == 1) (licenseeId, licenseeName) = (inForce[0].Id, inForce[0].Name);
        }

        // The same rules as adding a payout account (RailProblem): a provider this server can pay through, at a bank it pays from.
        if (RailProblem(parkBankCode, body.BankProvider, out var provider) is { } railProblem) return railProblem;
        var credentialsJson = Blank(body.BankCredentialsJson ?? "");
        if (credentialsJson is not null && !IsJsonObject(credentialsJson))
            return BadRequest(new { error = "invalid_credentials_json", message = "credentialsJson must be a JSON object" });
        if (credentialsJson is not null && CredentialsProblem(credentialsJson) is { } createCredentialsProblem)
            return BadRequest(new { error = createCredentialsProblem });

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
            LicenseeId = licenseeId,
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
            CredentialsEncrypted = credentialsJson ?? "{}",
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
        _log.LogInformation("Park {ParkId} ({Name}) created by super-admin; manager={Manager}; settled {Settlement}",
            park.Id, park.Name, managerEmail ?? "(none)", licenseeName is null ? "Swich-direct" : $"under the licence agreement of {licenseeName}");

        return Created($"/api/admin/parks/{park.Id}", new
        {
            id = park.Id,
            name = park.Name,
            slug = park.Slug,
            operatingModel = park.OperatingModel.ToString(),
            managerEmail,
            licenseeId,
            licenseeName,
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
                    isThirdPartyAccount = b.IsThirdPartyAccount,
                    thirdPartyReason = b.ThirdPartyReason,
                    addedBy = b.AddedBy,
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
        // and the settlement months use — not the UTC day (which rolls over at 04:00 local).
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
    /// <summary>Longest report window in days (the console's presets are at most a month; a custom range up to a year).</summary>
    private const int MaxReportDays = 366;

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
        // A year (with a leap day) at most. Unbounded, from=0001-01-01&to=9999-12-31 built 3.6 million daily rows on the
        // single API instance that also runs the payout and settlement workers — any park manager could stall every park.
        if (windowTo - windowFrom > TimeSpan.FromDays(MaxReportDays + 1))
            return BadRequest(new { error = "window_too_large", maxDays = MaxReportDays, message = $"The report window can be at most {MaxReportDays} days." });

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

        // Grouped once (not re-filtered per day): the work is the cashouts plus the days, not their product.
        var completedByDay = completed.GroupBy(c => c.CreatedAt.Date).ToDictionary(g => g.Key, g => g.ToList());
        var failedByDay = failed.GroupBy(c => c.CreatedAt.Date).ToDictionary(g => g.Key, g => g.Count());
        var daily = new List<DailyRow>();
        for (var d = startDay; d < endDay; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            var inDay = completedByDay.TryGetValue(d.Date, out var list) ? list : new();
            daily.Add(new DailyRow(
                Date: d,
                CashoutsCount: inDay.Count,
                Value: inDay.Sum(c => c.Amount),
                Fees: inDay.Sum(c => c.Fee),
                FailedCount: failedByDay.GetValueOrDefault(d.Date)));
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
    /// Phone is normalized to E.164 and given its keyed lookup hash (LookupHash — never a plain SHA-256).
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

        // Optional payout destination collected at onboarding — through the SAME rules as every
        // other destination (ownership check, third-party reason, audit fields). Otherwise the
        // driver adds their own IBAN in the app before the first cashout. One transaction: if the
        // destination is refused, the driver is not created either.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.SaveChangesAsync(ct);

        Core.Entities.BankCard? destination = null;
        if (!string.IsNullOrWhiteSpace(body.Iban))
        {
            var (outcome, error, payload, card) = await DestinationHelper.AddAsync(
                _db, parkId, driver, body.Iban, body.HolderName, makeDefault: true,
                allowThirdParty: true, thirdPartyReason: body.Reason, initiatedBy: ActorLabel, ct);
            if (outcome != DestinationHelper.Outcome.Created)
            {
                await tx.RollbackAsync(ct);
                return outcome == DestinationHelper.Outcome.Conflict
                    ? Conflict(new { error })
                    : BadRequest(payload ?? new { error });
            }
            destination = card;
        }

        await tx.CommitAsync(ct);
        // The park registered where the new driver's money goes: the driver hears of it (also the first sign of the account).
        if (destination is not null)
            await DestinationHelper.NotifyAsync(HttpContext.RequestServices, destination, PayoutAccountChange.Added, byPark: true);

        // Ids only: the log sink outlives the retention periods the privacy notice promises, so no names in it.
        _log.LogInformation("Onboarded driver {DriverId} in park {ParkId} by {Actor}",
            driver.Id, parkId, ActorLabel);

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
        // Phones added in THIS request: two Yandex profiles sharing a number would both pass the
        // database check and the save would then hit the unique phone index (a 500 instead of a skip).
        var phonesInRequest = new HashSet<string>();

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
            if (!phonesInRequest.Add(phoneHash) || await _db.Drivers.AsNoTracking().AnyAsync(d => d.PhoneHash == phoneHash, ct))
            { skipped.Add(new { yandexProfileId = pid, reason = "phone_taken" }); continue; }

            var driver = new Core.Entities.Driver
            {
                ParkId = parkId,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? null : profile.Name.Trim(), // null, never a placeholder — the ownership check must know "no name" from a name
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
    ///
    /// A driver who leaves Active (suspended, pending) or whose phone changes is signed out of every device in the same
    /// save: their sessions are revoked (admin_revoked), so no refresh token works any more, and DriverTokenCheck refuses
    /// the access tokens still out there. A suspension used to end a device's session only if that device happened to
    /// refresh while it lasted — one kept quiet worked again after a reactivation; a phone change touched no session.
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
        var statusBefore = driver.Status;
        var phoneHashBefore = driver.PhoneHash;

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

        var changed = RecordConfigChange(driver, parkId, Core.Entities.ParkConfigSubjects.Driver, driver.Id, DriverWatched);

        var signOut = (driver.Status != statusBefore && driver.Status != Core.Enums.DriverStatus.Active)
                   || driver.PhoneHash != phoneHashBefore;
        var revoked = 0;
        if (signOut)
        {
            var now = DateTime.UtcNow;
            foreach (var session in await _db.DriverSessions.Where(x => x.DriverId == driver.Id && x.RevokedAt == null).ToListAsync(ct))
            {
                session.RevokedAt = now;
                session.RevokedReason = "admin_revoked";
                revoked++;
            }
        }
        await _db.SaveChangesAsync(ct);
        if (signOut)
            await Security.DriverTokenCheck.ForgetAsync(HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), _db, driver.Id, ct);

        _log.LogInformation(
            "Driver {DriverId} updated by {Actor}: {Fields} (status={Status}){SignedOut}",
            driver.Id, ActorLabel, changed ?? "nothing", driver.Status, signOut ? $"; signed out of {revoked} device session(s)" : "");

        return Ok(new
        {
            id = driver.Id,
            name = driver.Name,
            phone = driver.PhoneEncrypted,
            yandexProfileId = driver.YandexDriverProfileId,
            status = driver.Status.ToString(),
            parkId = driver.ParkId,
            sessionsRevoked = revoked,
        });
    }

    /// <summary>
    /// Update the park's account/billing details (legal entity, tax ID, phone, IBAN).
    /// Surfaced on the admin Settings page. Each field is optional; sending an empty
    /// string clears it, omitting it (null) leaves it unchanged. An Active park must come out
    /// of the edit with its legal name, identification code and phone (400 legal_details_required,
    /// nothing saved) — including a park created before that rule, whose next edit completes them.
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
                tax = NormalizeTaxId(tax);
                if (tax is null) return BadRequest(new { error = "invalid_tax_id" });
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

        if (park.Status == Core.Enums.ParkStatus.Active
            && MissingLegalDetails(park.LegalEntityName, park.TaxId, park.Phone) is { Length: > 0 } missing)
            return LegalDetailsRequired(missing);

        var changedFields = RecordConfigChange(park, parkId, Core.Entities.ParkConfigSubjects.Park, null, ParkWatched);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Park {ParkId} account details updated by {Actor}: {Fields}", park.Id, ActorLabel, changedFields ?? "nothing");

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

    /// <summary>
    /// Suspend or reactivate a park. Swich and the operator only — a park manager cannot change
    /// their own park's status. Body: { status: "Suspended" | "Active", reason }; the reason is
    /// required to suspend.
    ///
    /// Suspension stops NEW cashouts (the saga refuses with park_inactive) and nothing else:
    /// payouts already debited from Yandex keep draining from the queue — the driver is owed that
    /// money — and the monthly settlement and reconciliation keep running, so fees already earned
    /// are still collected. Termination is not offered here: it ends a contract and is done
    /// deliberately, outside the console.
    /// </summary>
    [HttpPost("status")]
    public async Task<IActionResult> ChangeParkStatus(
        Guid parkId, [FromBody] ChangeParkStatusRequest body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        if (!Enum.TryParse<Core.Enums.ParkStatus>(body.Status, ignoreCase: true, out var target)
            || target is not (Core.Enums.ParkStatus.Active or Core.Enums.ParkStatus.Suspended))
            return BadRequest(new { error = "invalid_status", allowed = new[] { "Active", "Suspended" } });

        var reason = Blank(body.Reason ?? "");
        if (target == Core.Enums.ParkStatus.Suspended && reason is null)
            return BadRequest(new { error = "reason_required" });
        if (reason is { Length: > 500 })
            return BadRequest(new { error = "reason_too_long", max = 500 });

        var park = await _db.Parks.FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        if (park.Status == Core.Enums.ParkStatus.Terminated)
            return Conflict(new { error = "park_terminated" });
        if (park.Status == Core.Enums.ParkStatus.Pending && target == Core.Enums.ParkStatus.Suspended)
            return Conflict(new { error = "invalid_transition", from = park.Status.ToString(), to = target.ToString() });
        if (target == Core.Enums.ParkStatus.Active && park.Status != Core.Enums.ParkStatus.Active
            && MissingLegalDetails(park.LegalEntityName, park.TaxId, park.Phone) is { Length: > 0 } missingLegal)
            return LegalDetailsRequired(missingLegal);

        // A double click or a second admin asking for the state the park is already in is a no-op:
        // the first change keeps its reason, time and author.
        var changed = park.Status != target;
        if (changed)
        {
            var from = park.Status;
            park.Status = target;
            park.StatusReason = reason;
            park.StatusChangedAt = DateTime.UtcNow;
            park.StatusChangedBy = ActorLabel;
#pragma warning disable CS0618 // legacy bool mirror
            park.IsActive = target == Core.Enums.ParkStatus.Active;
#pragma warning restore CS0618
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Park {ParkId} ({Name}) status {From} → {To} by {Actor}: {Reason}",
                park.Id, park.Name, from, target, ActorLabel, reason ?? "(no reason)");
        }

        // Payouts already debited from Yandex: they still complete while the park is suspended.
        var inFlight = await _db.Cashouts.AsNoTracking()
            .CountAsync(c => c.ParkId == parkId
                          && (c.Status == Core.Enums.CashoutStatus.Queued || c.Status == Core.Enums.CashoutStatus.Processing), ct);

        return Ok(new
        {
            id = park.Id,
            name = park.Name,
            status = park.Status.ToString(),
            statusReason = park.StatusReason,
            statusChangedAt = park.StatusChangedAt,
            statusChangedBy = park.StatusChangedBy,
            changed,
            inFlightCount = inFlight,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Annex 3 §7: the park revokes the platform's authorisations
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Record the park's written revocation of the Annex 3 §2 (payouts) and §3 (monthly transfer) authorisations — by
    /// Swich or the operator, to whom the park writes; never by the park's own manager. Body: noticeOn (the day the
    /// notice was received — not in the future), revokedFrom (the first day without the authorisations: at least
    /// <see cref="ParkAuthorisation.NoticeDays"/> days after the notice, and not already past), reason (what the
    /// notice says, or where it is filed).
    ///
    /// From revokedFrom (Tbilisi day) the platform initiates nothing from the park's account: new cashouts are refused
    /// (authorisation_revoked), no waiting payout is sent (each waits with AUTHORISATION_REVOKED until a person cancels
    /// it — the amount goes back to the driver's Yandex balance), and no settlement share is collected (it is owed by the
    /// licensee under §2.5 and closed as paid outside the platform). What arose before stays owed. Until that day the
    /// revocation can be withdrawn (DELETE); one at a time — withdraw it to change it.
    /// </summary>
    [HttpPut("authorisation-revocation")]
    public async Task<IActionResult> RecordRevocation(Guid parkId, [FromBody] RecordRevocationRequest body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var today = SettlementService.TodayLocal();
        if (!DateOnly.TryParseExact(body.NoticeOn?.Trim() ?? "", "yyyy-MM-dd", out var noticeOn) || noticeOn.Year < 2020 || noticeOn > today)
            return BadRequest(new { error = "invalid_notice_date" });
        if (!DateOnly.TryParseExact(body.RevokedFrom?.Trim() ?? "", "yyyy-MM-dd", out var from))
            return BadRequest(new { error = "invalid_revoked_from" });
        var earliest = ParkAuthorisation.EarliestEffect(noticeOn);
        if (from < earliest)
            return BadRequest(new { error = "revocation_too_early", earliest, noticeDays = ParkAuthorisation.NoticeDays });
        // Recorded ahead, never back-dated: what the platform paid or collected before today it did under the authority
        // it had then — a past date would make the record say otherwise, and nothing could be undone by it anyway.
        if (from < today) return BadRequest(new { error = "revocation_in_past" });
        var reason = Blank(body.Reason ?? "");
        if (reason is null) return BadRequest(new { error = "reason_required" });
        if (reason.Length > 500) return BadRequest(new { error = "reason_too_long", max = 500 });
        if (!await _db.Parks.AsNoTracking().AnyAsync(p => p.Id == parkId, ct)) return NotFound(new { error = "park_not_found" });

        // Written only while none is recorded — checked by the UPDATE itself, so two people recording at once cannot both
        // write theirs (the second is told one exists).
        var now = DateTime.UtcNow;
        var actor = ActorLabel;
        var recorded = await _db.Parks
            .Where(p => p.Id == parkId && p.AuthorisationRevokedFrom == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.AuthorisationNoticeOn, (DateOnly?)noticeOn)
                .SetProperty(p => p.AuthorisationRevokedFrom, (DateOnly?)from)
                .SetProperty(p => p.AuthorisationRevocationReason, reason)
                .SetProperty(p => p.AuthorisationRevocationRecordedBy, actor)
                .SetProperty(p => p.AuthorisationRevocationRecordedAt, (DateTime?)now)
                .SetProperty(p => p.UpdatedAt, now), ct);
        if (recorded == 0) return Conflict(new { error = "revocation_already_recorded" });

        _log.LogWarning("Park {ParkId}: authorisation revoked from {From} (notice received {Notice}) — recorded by {Actor}: {Reason}",
            parkId, from, noticeOn, actor, reason);
        return Ok(await RevocationDtoAsync(parkId, ct));
    }

    /// <summary>
    /// Withdraw a recorded revocation before its day (the park changed its mind, or it was entered wrongly) — Swich or the
    /// operator. From its day on it stands (409 revocation_in_effect): the platform has acted on it. Body: an optional reason.
    /// </summary>
    [HttpDelete("authorisation-revocation")]
    public async Task<IActionResult> WithdrawRevocation(Guid parkId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WithdrawRevocationRequest? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !CanAccessPark(parkId)) return Forbid();
        var reason = Blank(body?.Reason ?? "");
        if (reason is { Length: > 500 }) return BadRequest(new { error = "reason_too_long", max = 500 });
        var park = await _db.Parks.AsNoTracking().Where(p => p.Id == parkId)
            .Select(p => new { p.AuthorisationNoticeOn, p.AuthorisationRevokedFrom, p.AuthorisationRevocationRecordedBy })
            .FirstOrDefaultAsync(ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        var today = SettlementService.TodayLocal();
        // "Not yet in effect" is part of the UPDATE: a withdrawal racing midnight never lifts a revocation that has begun.
        var withdrawn = await _db.Parks
            .Where(p => p.Id == parkId && p.AuthorisationRevokedFrom != null && p.AuthorisationRevokedFrom > today)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.AuthorisationNoticeOn, (DateOnly?)null)
                .SetProperty(p => p.AuthorisationRevokedFrom, (DateOnly?)null)
                .SetProperty(p => p.AuthorisationRevocationReason, (string?)null)
                .SetProperty(p => p.AuthorisationRevocationRecordedBy, (string?)null)
                .SetProperty(p => p.AuthorisationRevocationRecordedAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
        if (withdrawn == 0)
            return park.AuthorisationRevokedFrom is { } from
                ? Conflict(new { error = "revocation_in_effect", revokedFrom = from })
                : Conflict(new { error = "no_revocation" });

        _log.LogWarning("Park {ParkId}: revocation from {From} (notice {Notice}, recorded by {RecordedBy}) withdrawn by {Actor}: {Reason}",
            parkId, park.AuthorisationRevokedFrom, park.AuthorisationNoticeOn, park.AuthorisationRevocationRecordedBy, ActorLabel, reason ?? "(no reason)");
        return Ok(await RevocationDtoAsync(parkId, ct));
    }

    /// <summary>The park's revocation (null when none is recorded) and what is waiting on it: payouts and unpaid shares.</summary>
    private async Task<object> RevocationDtoAsync(Guid parkId, CancellationToken ct)
    {
        var p = await _db.Parks.AsNoTracking().FirstAsync(x => x.Id == parkId, ct);
        var waiting = await _db.Cashouts.AsNoTracking()
            .CountAsync(c => c.ParkId == parkId && c.Status == Core.Enums.CashoutStatus.Queued, ct);
        var unpaid = await _db.Settlements.AsNoTracking()
            .CountAsync(s => s.ParkId == parkId && s.Status == Core.Enums.SettlementStatus.Failed, ct);
        return new
        {
            parkId,
            authorisation = p.AuthorisationRevokedFrom is not { } from ? null : new
            {
                noticeOn = p.AuthorisationNoticeOn,
                revokedFrom = from,
                inEffect = ParkAuthorisation.IsRevoked(from, SettlementService.TodayLocal()),
                reason = p.AuthorisationRevocationReason,
                recordedBy = p.AuthorisationRevocationRecordedBy,
                recordedAt = p.AuthorisationRevocationRecordedAt,
            },
            waitingPayouts = waiting,
            unpaidSettlements = unpaid,
        };
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

        // What this server accepts as a provider (the console offers exactly these): no "mock" with real rails.
        return Ok(new { parkId, count = rows.Count, accounts = rows, allowedProviders = _rails.AllowedProviders });
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

        if (RailProblem(bankCode, body.Provider, out var provider) is { } railProblem) return railProblem;
        var credentials = Blank(body.CredentialsJson ?? "");
        if (credentials is not null && !IsJsonObject(credentials))
            return BadRequest(new { error = "invalid_credentials_json", message = "credentialsJson must be a JSON object" });
        if (credentials is not null && CredentialsProblem(credentials) is { } credentialsProblem)
            return BadRequest(new { error = credentialsProblem });

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
            CredentialsEncrypted = credentials ?? "{}",
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

        _db.ParkConfigEvents.Add(new Core.Entities.ParkConfigEvent
        {
            ParkId = parkId,
            Subject = Core.Entities.ParkConfigSubjects.BankAccount,
            SubjectId = account.Id,
            Action = "created",
            ChangedFields = string.Join(",", new[] { "iban", "provider", credentials is null ? null : "credentials", makePrimary ? "isPrimary" : null }
                .Where(f => f is not null)),
            Actor = ActorLabel,
        });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Park {ParkId}: payout account {Account} added by {Actor} ({Bank}/{Provider}, primary={Primary})",
            parkId, account.Id, ActorLabel, bankCode, provider, makePrimary);

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
        var railBefore = RailOf(account.Provider, account.CredentialsEncrypted);

        if (body.HolderName is not null) account.HolderName = Blank(body.HolderName);
        if (body.Label is not null) account.Label = Blank(body.Label);
        if (!string.IsNullOrWhiteSpace(body.CredentialsJson))
        {
            // Stored as-is and parsed by the bank adapter at payout time — garbage here would surface
            // as a failed payout at 3 a.m., so refuse anything that is not a JSON object now.
            if (!IsJsonObject(body.CredentialsJson))
                return BadRequest(new { error = "invalid_credentials_json", message = "credentialsJson must be a JSON object" });
            if (CredentialsProblem(body.CredentialsJson) is { } credentialsProblem)
                return BadRequest(new { error = credentialsProblem });
            account.CredentialsEncrypted = body.CredentialsJson.Trim();
        }
        // The new-account rule (RailProblem) wherever an edit makes the account pay: a provider named, or the account switched
        // back on. Checking the provider alone let an account saved before the rule — a dry run's, a copied development
        // database's — at a bank without an adapter be named "tbc" (and pass every rail check, which reads the provider), or
        // a mock one come back into routing, where it pays nobody and stops the next start. Tidying one that stays off is fine.
        if (body.Provider is not null || (body.IsActive == true && !account.IsActive))
        {
            if (RailProblem(account.BankCode, body.Provider ?? account.Provider, out var provider) is { } railProblem)
                return railProblem;
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

        // "Primary" must point at an account that can actually pay. Deactivating the primary hands the
        // flag to another active account when there is one; with none left it stays, so re-activating
        // later restores the old state without anyone having to remember which account it was.
        var mirror = account;
        if (!account.IsActive && account.IsPrimary)
        {
            var successor = await _db.ParkBankAccounts
                .Where(a => a.ParkId == parkId && a.Id != accountId && a.IsActive)
                .OrderBy(a => a.BankCode == "TB" ? 0 : 1).ThenBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (successor is not null)
            {
                account.IsPrimary = false;
                successor.IsPrimary = true;
                mirror = successor;
                _log.LogInformation("Park {ParkId}: primary payout account moved {From} → {To} because the primary was deactivated",
                    parkId, account.Id, successor.Id);
            }
        }
        if (mirror.IsPrimary)
        {
            park.BankAccountIban = mirror.Iban;
            park.BankProvider = mirror.Provider;
            park.BankType = mirror.Provider.ToUpperInvariant();
        }

        var changedFields = RecordConfigChange(account, parkId, Core.Entities.ParkConfigSubjects.BankAccount, account.Id, BankAccountWatched);
        if (!ReferenceEquals(mirror, account))
            RecordConfigChange(mirror, parkId, Core.Entities.ParkConfigSubjects.BankAccount, mirror.Id, BankAccountWatched);
        await _db.SaveChangesAsync(ct);
        if (changedFields is not null)
            _log.LogInformation("Park {ParkId}: payout account {Account} changed by {Actor}: {Fields}", parkId, account.Id, ActorLabel, changedFields);

        // Another provider or another bank user (or environment) is another rail: a document sent before, whose answer
        // was lost, would be a NEW order there. Payouts holding one go to review instead of being re-sent. A new password
        // or certificate of the same user changes nothing for the bank's duplicate check.
        if (RailOf(account.Provider, account.CredentialsEncrypted) != railBefore)
        {
            var held = await HttpContext.RequestServices.GetRequiredService<ICashoutOrchestrator>()
                .HoldUnansweredDocumentsAsync(account.Id, $"{ActorLabel} changed the account's provider or bank user", ct);
            if (held > 0)
                _log.LogWarning("Park {ParkId}: {Held} payout(s) with an unanswered bank document held for review after account {Account}'s rail changed",
                    parkId, held, account.Id);
        }

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

    /// <summary>
    /// Add a payout IBAN for a driver on their behalf (onboarding desk). Unlike the driver
    /// app, the park may register an account in someone else's name — but must say why
    /// (<c>reason</c>); the destination is then flagged as third-party everywhere.
    /// </summary>
    [HttpPost("drivers/{driverId:guid}/cards")]
    public async Task<IActionResult> AddDriverCard(
        Guid parkId, Guid driverId, [FromBody] AddDestinationRequest body, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var driver = await _db.Drivers.FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });

        var (result, error, payload, card) = await DestinationHelper.AddAsync(
            _db, parkId, driver, body.Iban, body.HolderName, body.MakeDefault ?? true,
            allowThirdParty: true, thirdPartyReason: body.Reason, initiatedBy: ActorLabel, ct);

        if (result == DestinationHelper.Outcome.Created && card is not null)
        {
            _log.LogInformation("Payout destination added for driver {DriverId} by {Actor} (third-party: {ThirdParty})",
                driverId, ActorLabel, card.IsThirdPartyAccount);
            await DestinationHelper.NotifyAsync(HttpContext.RequestServices, card, PayoutAccountChange.Added, byPark: true);
        }
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
        var removed = await DestinationHelper.DeactivateAsync(_db, driverId, cardId, ct);
        if (removed is null) return NotFound(new { error = "card_not_found" });
        _log.LogInformation("Payout destination {CardId} of driver {DriverId} removed by {Actor}", cardId, driverId, ActorLabel);
        await DestinationHelper.NotifyAsync(HttpContext.RequestServices, removed, PayoutAccountChange.Removed, byPark: true);
        return NoContent();
    }

    // ── Change trail (ParkConfigEvent): which of these properties changed, by name — never the values ──

    private static readonly (string Property, string Field)[] BankAccountWatched =
    {
        (nameof(Core.Entities.ParkBankAccount.Provider), "provider"),
        (nameof(Core.Entities.ParkBankAccount.CredentialsEncrypted), "credentials"),
        (nameof(Core.Entities.ParkBankAccount.IsActive), "isActive"),
        (nameof(Core.Entities.ParkBankAccount.IsPrimary), "isPrimary"),
        (nameof(Core.Entities.ParkBankAccount.HolderName), "holderName"),
    };

    private static readonly (string Property, string Field)[] ParkWatched =
    {
        (nameof(Core.Entities.Park.YandexClientIdEncrypted), "yandexClientId"),
        (nameof(Core.Entities.Park.YandexApiKeyEncrypted), "yandexApiKey"),
        (nameof(Core.Entities.Park.YandexParkId), "yandexParkId"),
        (nameof(Core.Entities.Park.BankAccountIban), "bankAccountIban"),
        (nameof(Core.Entities.Park.CashoutFee), "cashoutFee"),
        (nameof(Core.Entities.Park.MinCashoutAmount), "minCashoutAmount"),
        (nameof(Core.Entities.Park.MaxCashoutAmount), "maxCashoutAmount"),
        (nameof(Core.Entities.Park.DailyCashoutLimitPerDriver), "dailyCashoutLimitPerDriver"),
        (nameof(Core.Entities.Park.SwichSharePercent), "swichSharePercent"),
        (nameof(Core.Entities.Park.Phase1SharePercent), "phase1SharePercent"),
        (nameof(Core.Entities.Park.Phase1CapGel), "phase1CapGel"),
        (nameof(Core.Entities.Park.LegalEntityName), "legalEntityName"),
        (nameof(Core.Entities.Park.TaxId), "taxId"),
        (nameof(Core.Entities.Park.Phone), "phone"),
    };

    private static readonly (string Property, string Field)[] DriverWatched =
    {
        (nameof(Core.Entities.Driver.Name), "name"),
        (nameof(Core.Entities.Driver.PhoneEncrypted), "phone"),
        (nameof(Core.Entities.Driver.YandexDriverProfileId), "yandexProfileId"),
        (nameof(Core.Entities.Driver.Status), "status"),
    };

    /// <summary>
    /// Adds a <see cref="Core.Entities.ParkConfigEvent"/> naming the watched properties that differ from what was loaded
    /// (saved with the change itself). Returns the field list, or null when none changed (then nothing is recorded).
    /// </summary>
    private string? RecordConfigChange(object entity, Guid parkId, string subject, Guid? subjectId, (string Property, string Field)[] watched)
    {
        _db.ChangeTracker.DetectChanges();
        var entry = _db.Entry(entity);
        var fields = watched.Where(w => entry.Property(w.Property).IsModified).Select(w => w.Field).ToList();
        if (fields.Count == 0) return null;
        var list = string.Join(",", fields);
        _db.ParkConfigEvents.Add(new Core.Entities.ParkConfigEvent
        {
            ParkId = parkId,
            Subject = subject,
            SubjectId = subjectId,
            Action = "updated",
            ChangedFields = list,
            Actor = ActorLabel,
        });
        return list;
    }

    /// <summary>
    /// What stored payout credentials may not contain, as an error code (null = fine): a TBC certificatePath outside the
    /// directory the operator configured (none configured = no paths at all), or a certificate larger than a certificate
    /// can be. Park staff write this JSON; checked here so they hear it now, and so the API never opens a file they name
    /// (the adapter refuses the same at load time). Every spelling of the key counts — the adapter reads it
    /// case-insensitively, and a JSON object may repeat a key.
    /// </summary>
    private string? CredentialsProblem(string json)
    {
        // Credentials for a bank's test environment, on a server with real rails: a sandbox would accept the payouts and
        // the settlement, and they would be recorded as paid while no money moved. Omitted environment = production.
        // Sandbox tests of a park's credentials belong on a staging host (BankRailPolicy; the adapter refuses them too).
        if (!_rails.TestEnvironmentAllowed && Infrastructure.Adapters.Banks.Tbc.TbcCredentials.DeclaresTestEnvironment(json))
            return "test_environment_not_allowed";
        var dir = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["BankPayout:Tbc:CertificateDirectory"];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var value = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : null;
            if (string.Equals(p.Name, "certificatePath", StringComparison.OrdinalIgnoreCase)
                && Infrastructure.Adapters.Banks.Tbc.TbcCredentials.CertificatePathProblem(value, dir) is { } pathProblem)
                return pathProblem;
            if (string.Equals(p.Name, "certificatePfxBase64", StringComparison.OrdinalIgnoreCase)
                && value is { Length: > Infrastructure.Adapters.Banks.Tbc.TbcCredentials.MaxCertificateBytes * 4 / 3 + 4 })
                return "certificate_too_large";
        }
        return null;
    }

    /// <summary>
    /// Which rail a payout account sends through, as the bank's duplicate check sees it: the provider plus the bank user
    /// and environment named in the credentials (unreadable credentials count as their own rail).
    /// </summary>
    private static string RailOf(string provider, string? credentialsJson)
    {
        string user = "";
        string? environment = null;   // null: no "environment" key at all
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(credentialsJson) ? "{}" : credentialsJson);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() ?? "" : "";
                if (string.Equals(p.Name, "username", StringComparison.OrdinalIgnoreCase)) user = v.Trim();
                if (string.Equals(p.Name, "environment", StringComparison.OrdinalIgnoreCase)) environment = v;
            }
        }
        catch (System.Text.Json.JsonException) { user = "\u0000unreadable:" + credentialsJson; }
        // Which host the adapter sends to, as TbcCredentials decides it: no key at all is production; a key is production
        // only when it says so ("production"/"prod") — anything else, "" included, is the test host. Reading "" as
        // production here, unlike the adapter, made moving a sandbox user to production look like no change of rail.
        var host = environment is null || Infrastructure.Adapters.Banks.Tbc.TbcCredentials.IsProductionEnvironment(environment) ? "production" : "test";
        return $"{provider.Trim().ToLowerInvariant()}|{user.ToLowerInvariant()}|{host}";
    }

    /// <summary>
    /// The provider a payout account at <paramref name="bankCode"/> will pay through (<paramref name="requested"/>, or the
    /// bank's own when none is named), or the 400 to answer instead. With real rails only TBC and BoG accounts can pay
    /// (unsupported_bank: any other bank used to default to the mock) and "mock" is no provider (invalid_provider);
    /// Development keeps every bank and the mock (BankRailPolicy).
    /// </summary>
    private IActionResult? RailProblem(string bankCode, string? requested, out string provider)
    {
        provider = "";
        var chosen = _rails.MockProviderAllowed || bankCode is "TB" or "BG"
            ? Blank(requested ?? "")?.ToLowerInvariant() ?? _rails.DefaultProviderFor(bankCode)
            : null;
        if (chosen is null)
            return BadRequest(new { error = "unsupported_bank", bankCode, bankLabel = GeorgianIban.BankLabel(bankCode), supported = new[] { "TBC", "BOG" } });
        if (!_rails.AllowedProviders.Contains(chosen))
            return BadRequest(new { error = "invalid_provider", allowed = _rails.AllowedProviders });
        provider = chosen;
        return null;
    }

    /// <summary>True when the text parses as a JSON object (what every bank adapter expects for credentials).</summary>

    private static bool IsJsonObject(string text)

    {

        try

        {

            using var doc = System.Text.Json.JsonDocument.Parse(text);

            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;

        }

        catch (System.Text.Json.JsonException) { return false; }

    }


    private static string? Blank(string s)
    {
        var trimmed = s.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // One normaliser for every phone that ever becomes a PhoneHash (login uses the same).
    private static string? NormalizePhone(string? raw) => Core.Identity.GeorgianPhone.Normalize(raw);

    /// <summary>
    /// A Georgian identification code, digits only: 9 for a company, 11 for an individual entrepreneur (their personal
    /// number). Anything else is null.
    /// </summary>
    private static string? NormalizeTaxId(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length is 9 or 11 ? digits : null;
    }

    /// <summary>
    /// The driver's privacy notice names the park as the controller of the driver's data, and the driver app shows who
    /// that is under Profile → "Your park": the legal name, identification code and phone. A park that takes cashouts
    /// (Active) without them would leave its drivers with a notice that names nobody — so an Active park, or one being
    /// made Active, must have all three. Returns the missing ones in a fixed order.
    /// </summary>
    private static string[] MissingLegalDetails(string? legalEntityName, string? taxId, string? phone)
    {
        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(legalEntityName)) missing.Add("legalEntityName");
        if (string.IsNullOrWhiteSpace(taxId)) missing.Add("taxId");
        if (string.IsNullOrWhiteSpace(phone)) missing.Add("phone");
        return missing.ToArray();
    }

    private BadRequestObjectResult LegalDetailsRequired(string[] missing) => BadRequest(new
    {
        error = "legal_details_required",
        missing,
        message = "An active park needs its legal name, identification code and phone — the drivers' privacy notice names the park by them.",
    });
    private static string HashPhone(string phone) => Infrastructure.Security.LookupHash.Phone(phone);
}

public record CreateDriverRequest(
    string Phone,
    string YandexProfileId,
    string Name,
    bool ConsentGiven,
    string? Iban = null,
    string? HolderName = null,
    string? Reason = null);   // required when HolderName is not the driver (third-party account)

public record AddDestinationRequest(string Iban, string? HolderName, bool? MakeDefault, string? Reason = null);

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

public record ChangeParkStatusRequest(string? Status, string? Reason);

/// <summary>Annex 3 §7 revocation: both dates "YYYY-MM-DD".</summary>
public record RecordRevocationRequest(string? NoticeOn, string? RevokedFrom, string? Reason);

public record WithdrawRevocationRequest(string? Reason);

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
    decimal? DailyCashoutLimitPerDriver = null,
    // The licensee whose agreement settles the park; omitted = the one licensee in force (required when several are).
    Guid? LicenseeId = null,
    // True = Swich connected the park directly: outside the licence agreement, its own split.
    bool? Direct = null);

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
