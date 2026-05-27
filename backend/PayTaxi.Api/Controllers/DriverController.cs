using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<DriverController> _log;

    public DriverController(
        AppDbContext db,
        IYandexFleetClient yandex,
        ICashoutOrchestrator orchestrator,
        ILogger<DriverController> log)
    {
        _db = db;
        _yandex = yandex;
        _orchestrator = orchestrator;
        _log = log;
    }

    /// <summary>Profile + park + balance + cards for the currently authenticated driver.</summary>
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
            .FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        decimal? balance = null;
        string? carPlate = null;
        if (driver.YandexDriverProfileId is not null)
        {
            // One Yandex call per /me; cached at the resilient-client layer if multiple drivers
            // in the same park hit this endpoint within the rate-limit window.
            var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
            var match = profiles.FirstOrDefault(p => p.DriverProfileId == driver.YandexDriverProfileId);
            balance = match?.Balance;
            carPlate = match?.CarPlate;
        }

        return Ok(new
        {
            driver = new
            {
                id = driver.Id,
                name = driver.Name,
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
            },
            cards = driver.BankCards
                .OrderByDescending(b => b.IsDefault)
                .Select(b => new
                {
                    id = b.Id,
                    maskedPan = b.MaskedPan,
                    bankType = b.BankType,
                    isDefault = b.IsDefault,
                }),
        });
    }

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
                createdAt = c.CreatedAt,
                completedAt = c.CompletedAt,
            })
            .ToListAsync(ct);

        return Ok(new { count = rows.Count, cashouts = rows });
    }

    /// <summary>
    /// Driver-initiated cashout. parkId + driverId come from the JWT.
    /// Body provides amount, cardId, idempotencyKey only.
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

        // Guard against tampered card IDs — a driver may only spend onto their own cards.
        var cardBelongsToDriver = await _db.BankCards.AsNoTracking()
            .AnyAsync(b => b.Id == body.CardId && b.DriverId == driverId, ct);
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
