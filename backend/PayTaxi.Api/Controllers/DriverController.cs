using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PayTaxi.Core.Banking;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Driver-scoped endpoints. parkId + driverId always come from the JWT —
/// the client cannot ask for someone else's data by passing different ids.
///
/// Open auth endpoints (request-otp, verify-otp) live in AuthController.
/// </summary>
[ApiController]
[Authorize(Roles = "driver")]
[Route("api/driver")]
public class DriverController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IYandexFleetClient _yandex;
    private readonly ICashoutOrchestrator _orchestrator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DriverController> _log;

    public DriverController(
        AppDbContext db,
        IYandexFleetClient yandex,
        ICashoutOrchestrator orchestrator,
        IMemoryCache cache,
        ILogger<DriverController> log)
    {
        _db = db;
        _yandex = yandex;
        _orchestrator = orchestrator;
        _cache = cache;
        _log = log;
    }

    /// <summary>Profile + park config + balance + payout destinations for the authenticated driver.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out var parkId, out var error))
            return Unauthorized(new { error });

        var driver = await _db.Drivers.AsNoTracking()
            .Include(d => d.BankCards.Where(b => b.IsActive))
            .FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });

        var park = await _db.Parks.AsNoTracking()
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        decimal? balance = null;
        string? carPlate = null;
        if (driver.YandexDriverProfileId is not null)
        {
            var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
            var match = profiles.FirstOrDefault(p => p.DriverProfileId == driver.YandexDriverProfileId);
            balance = match?.Balance;
            carPlate = match?.CarPlate;
        }

        var supportedBanks = park.BankAccounts
            .Select(a => a.BankCode)
            .Distinct()
            .Select(code => new { bankCode = code, bankLabel = GeorgianIban.BankLabel(code) })
            .ToList();

        return Ok(new
        {
            driver = new
            {
                id = driver.Id,
                name = driver.Name,
                phone = driver.PhoneEncrypted,
                yandexProfileId = driver.YandexDriverProfileId,
                status = driver.Status.ToString(),
                carPlate,
                balance,
            },
            park = new
            {
                id = park.Id,
                name = park.Name,
                operatingModel = park.OperatingModel.ToString(),
                cashoutFee = park.CashoutFee,
                minCashoutAmount = park.MinCashoutAmount,
                maxCashoutAmount = park.MaxCashoutAmount,
                dailyCashoutLimitPerDriver = park.DailyCashoutLimitPerDriver,
                supportedBanks,
            },
            cards = driver.BankCards
                .OrderByDescending(b => b.IsDefault)
                .ThenByDescending(b => b.CreatedAt)
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
    }

    // ── Payout destinations ──────────────────────────────────────────

    /// <summary>Add a bank account (IBAN) to receive cashouts. Body: { iban, holderName?, makeDefault? }.</summary>
    [HttpPost("me/cards")]
    public async Task<IActionResult> AddCard([FromBody] AddMyCardRequest body, CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out var parkId, out var error))
            return Unauthorized(new { error });
        if (body is null) return BadRequest(new { error = "missing_body" });

        var driver = await _db.Drivers.FirstOrDefaultAsync(d => d.Id == driverId && d.ParkId == parkId, ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });

        var (outcome, err, payload) = await DestinationHelper.AddAsync(
            _db, parkId, driver, body.Iban, body.HolderName, body.MakeDefault ?? true, ct);

        return outcome switch
        {
            DestinationHelper.Outcome.Created => Created("/api/driver/me/cards", payload),
            DestinationHelper.Outcome.Conflict => Conflict(new { error = err }),
            _ => BadRequest(payload ?? new { error = err }),
        };
    }

    [HttpDelete("me/cards/{cardId:guid}")]
    public async Task<IActionResult> RemoveCard(Guid cardId, CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });

        // Don't pull the rug from under an in-flight payout.
        var inFlight = await _db.Cashouts.AsNoTracking().AnyAsync(c =>
            c.BankCardId == cardId && c.DriverId == driverId &&
            (c.Status == Core.Enums.CashoutStatus.Queued || c.Status == Core.Enums.CashoutStatus.Processing), ct);
        if (inFlight) return Conflict(new { error = "card_has_pending_cashout" });

        var ok = await DestinationHelper.DeactivateAsync(_db, driverId, cardId, ct);
        return ok ? NoContent() : NotFound(new { error = "card_not_found" });
    }

    [HttpPost("me/cards/{cardId:guid}/default")]
    public async Task<IActionResult> SetDefaultCard(Guid cardId, CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });
        var ok = await DestinationHelper.SetDefaultAsync(_db, driverId, cardId, ct);
        return ok ? NoContent() : NotFound(new { error = "card_not_found" });
    }

    // ── Rides (Yandex orders) ────────────────────────────────────────

    /// <summary>
    /// The driver's completed rides from Yandex Fleet for the last <paramref name="days"/> days
    /// (max 30). Cached for 60 s per driver so tab switches don't spend the park's Yandex
    /// rate budget; the resilient client below still rate-limits, retries and audit-logs.
    /// </summary>
    [HttpGet("me/rides")]
    public async Task<IActionResult> MyRides([FromQuery] int days = 14, CancellationToken ct = default)
    {
        if (!TryGetClaims(out var driverId, out var parkId, out var error))
            return Unauthorized(new { error });
        days = Math.Clamp(days, 1, 30);

        var driver = await _db.Drivers.AsNoTracking()
            .Where(d => d.Id == driverId && d.ParkId == parkId)
            .Select(d => new { d.YandexDriverProfileId })
            .FirstOrDefaultAsync(ct);
        if (driver is null) return NotFound(new { error = "driver_not_found" });
        if (driver.YandexDriverProfileId is null)
            return Ok(new { days, count = 0, rides = Array.Empty<object>(), note = "driver_not_linked_to_yandex" });

        var to = DateTime.UtcNow;
        var from = to.AddDays(-days);
        var cacheKey = $"rides:{driverId}:{days}";

        var rides = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var orders = await _yandex.GetOrdersAsync(parkId, driver.YandexDriverProfileId, from, to, ct);
            return orders
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => new RideDto(o.OrderId, o.Amount, o.From, o.To, o.CreatedAt))
                .ToList();
        }) ?? new List<RideDto>();

        return Ok(new { days, count = rides.Count, rides });
    }

    // ── Cashouts ─────────────────────────────────────────────────────

    /// <summary>Cashouts for the authenticated driver, newest first.</summary>
    [HttpGet("me/cashouts")]
    public async Task<IActionResult> MyCashouts([FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });

        take = Math.Clamp(take, 1, 200);
        var rows = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.DriverId == driverId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .Select(c => new
            {
                id = c.Id,
                driverId = c.DriverId,
                amount = c.Amount,
                fee = c.Fee,
                status = c.Status.ToString(),
                bankTransferId = c.BankTransferId,
                yandexTransactionId = c.YandexTransactionId,
                failureReason = c.FailureReason,
                attemptCount = c.AttemptCount,
                nextAttemptAt = c.NextAttemptAt,
                createdAt = c.CreatedAt,
                completedAt = c.CompletedAt,
                bankType = c.BankCard.BankType,
                maskedPan = c.BankCard.MaskedPan,
            })
            .ToListAsync(ct);

        return Ok(new { count = rows.Count, cashouts = rows });
    }

    /// <summary>
    /// Driver-initiated cashout. parkId + driverId come from the JWT.
    /// Body provides amount, cardId, idempotencyKey only.
    /// 200 Completed · 202 Queued (money on its way) · 422 Failed.
    /// </summary>
    [HttpPost("cashouts")]
    public async Task<IActionResult> CreateMyCashout(
        [FromBody] CreateMyCashoutRequest body,
        CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out var parkId, out var error))
            return Unauthorized(new { error });
        if (body is null) return BadRequest(new { error = "missing_body" });
        if (body.CardId == Guid.Empty) return BadRequest(new { error = "card_id_required" });
        if (body.Amount <= 0) return BadRequest(new { error = "amount_must_be_positive" });
        if (string.IsNullOrWhiteSpace(body.IdempotencyKey))
            return BadRequest(new { error = "idempotency_key_required" });

        // Guard against tampered card IDs — a driver may only pay out to their own destinations.
        var cardBelongsToDriver = await _db.BankCards.AsNoTracking()
            .AnyAsync(b => b.Id == body.CardId && b.DriverId == driverId && b.IsActive, ct);
        if (!cardBelongsToDriver)
            return Forbid();

        try
        {
            var result = await _orchestrator.RunAsync(new CashoutSagaRequest(
                ParkId: parkId,
                DriverId: driverId,
                BankCardId: body.CardId,
                Amount: body.Amount,
                IdempotencyKey: body.IdempotencyKey,
                InitiatedBy: $"driver:{driverId}"), ct);

            return result.Status switch
            {
                "Completed"      => Ok(result),
                "Queued"         => StatusCode(202, result),
                "ReviewRequired" => StatusCode(202, result),
                "Failed"         => UnprocessableEntity(result),
                _                => Ok(result),
            };
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Driver cashout rejected for {DriverId}", driverId);
            return BadRequest(new { error = "cashout_rejected", message = ex.Message });
        }
    }

    // ── Notifications ────────────────────────────────────────────────

    /// <summary>Notifications inbox for the authenticated driver. Newest-first.</summary>
    [HttpGet("me/notifications")]
    public async Task<IActionResult> ListNotifications(
        [FromQuery] int take = 30,
        [FromQuery] bool unreadOnly = false,
        CancellationToken ct = default)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });

        take = Math.Clamp(take, 1, 100);
        var query = _db.Notifications.AsNoTracking().Where(n => n.DriverId == driverId);
        if (unreadOnly) query = query.Where(n => !n.IsRead);

        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new
            {
                id = n.Id,
                type = n.Type,
                title = n.Title,
                body = n.Body,
                link = n.Link,
                isRead = n.IsRead,
                createdAt = n.CreatedAt,
            })
            .ToListAsync(ct);

        var unreadCount = await _db.Notifications
            .CountAsync(n => n.DriverId == driverId && !n.IsRead, ct);

        return Ok(new { count = rows.Count, unreadCount, notifications = rows });
    }

    [HttpPost("me/notifications/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });

        var rows = await _db.Notifications
            .Where(n => n.Id == id && n.DriverId == driverId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);

        if (rows == 0) return NotFound(new { error = "notification_not_found_or_already_read" });
        return NoContent();
    }

    [HttpPost("me/notifications/read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });

        var rows = await _db.Notifications
            .Where(n => n.DriverId == driverId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);

        return Ok(new { markedRead = rows });
    }

    // ── Claim extraction ─────────────────────────────────────────────
    private bool TryGetClaims(out Guid driverId, out Guid parkId, out string error)
    {
        driverId = Guid.Empty;
        parkId = Guid.Empty;
        error = "";

        var subClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                     ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var parkClaim = User.FindFirst("parkId")?.Value;

        if (!Guid.TryParse(subClaim, out driverId))
        {
            error = "invalid_driver_claim";
            return false;
        }
        if (!Guid.TryParse(parkClaim, out parkId))
        {
            error = "invalid_park_claim";
            return false;
        }
        return true;
    }
}

public record CreateMyCashoutRequest(Guid CardId, decimal Amount, string IdempotencyKey);
public record RideDto(string OrderId, decimal Amount, string? From, string? To, DateTime CreatedAt);
public record AddMyCardRequest(string Iban, string? HolderName, bool? MakeDefault);
