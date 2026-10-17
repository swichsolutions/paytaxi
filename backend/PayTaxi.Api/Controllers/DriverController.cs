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
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("driver")]
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
    public async Task<IActionResult> Me([FromQuery] bool fresh = false, CancellationToken ct = default)
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

        // Balance: serve the sync worker's cache when it is fresh; go live only when it is
        // stale or the caller asked to refresh. A Yandex outage degrades to the last known
        // value (flagged stale) instead of a 500 — the app must never show "0.00" for "unknown".
        decimal? balance = null;
        DateTime? balanceAsOf = null;
        bool balanceStale = false;
        string? balanceError = null;
        string? carPlate = null;
        if (driver.YandexDriverProfileId is not null)
        {
            var now = DateTime.UtcNow;
            var cached = await _db.YandexBalanceCaches.AsNoTracking()
                .FirstOrDefaultAsync(c => c.DriverId == driverId, ct);
            var isFresh = cached is not null && now - cached.UpdatedAt < TimeSpan.FromSeconds(BalanceFreshSeconds);

            // A live read pulls the park's whole roster from Yandex through the park's single 0.5 s call slot — the
            // one the payout debits wait in. So a driver gets at most one live read per LiveReadMinSeconds, however
            // often (or how many times at once) the app asks with fresh=true; in between the last value is served.
            var live = (!isFresh || fresh) && TryClaimLiveRead(driverId, now);
            if (!live && cached is not null)
            {
                balance = cached.Balance;
                balanceAsOf = cached.UpdatedAt;
                balanceStale = !isFresh;
            }
            else if (!live)
            {
                // Another request of this driver is reading Yandex right now and nothing is cached yet.
                balanceError = "yandex_unavailable";
            }
            else
            {
                try
                {
                    var profiles = await _yandex.GetDriverProfilesAsync(parkId, ct);
                    var match = profiles.FirstOrDefault(p => p.DriverProfileId == driver.YandexDriverProfileId);
                    if (match is not null)
                    {
                        balance = match.Balance;
                        balanceAsOf = now;
                        carPlate = match.CarPlate;
                        await UpsertBalanceCacheAsync(driverId, parkId, match.Balance, match.Currency, now, ct);
                    }
                    else
                    {
                        balanceError = "profile_not_found";
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Yandex balance read failed for driver {DriverId}", driverId);
                    balanceError = "yandex_unavailable";
                    if (cached is not null)
                    {
                        balance = cached.Balance;
                        balanceAsOf = cached.UpdatedAt;
                        balanceStale = true;
                    }
                }
            }
        }

        // The same order the saga refuses in: a revoked authorisation first, then a park that is not active.
        var cashoutsPausedReason =
            Infrastructure.Services.ParkAuthorisation.IsRevoked(park, Infrastructure.Services.SettlementService.TodayLocal())
                ? CashoutRejectionCodes.AuthorisationRevoked
                : park.Status != Core.Enums.ParkStatus.Active ? CashoutRejectionCodes.ParkInactive : null;

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
                balanceAsOf,
                balanceStale,
                balanceError,
            },
            park = new
            {
                id = park.Id,
                name = park.Name,
                // The park is the controller of the driver's personal data: the privacy policy sends the
                // driver to Profile → Your park for its legal name, identification code and phone
                // (Law on Personal Data Protection, Art. 24(1)(a)). The admin API requires all three before a
                // park is Active, so null only for parks created before that rule.
                legalEntityName = park.LegalEntityName,
                taxId = park.TaxId,
                phone = park.Phone,
                operatingModel = park.OperatingModel.ToString(),
                cashoutFee = park.CashoutFee,
                minCashoutAmount = park.MinCashoutAmount,
                maxCashoutAmount = park.MaxCashoutAmount,
                dailyCashoutLimitPerDriver = park.DailyCashoutLimitPerDriver,
                supportedBanks,
                // The app says "paused" up front instead of letting the driver key in an amount the
                // saga will refuse. A suspension's reason stays between Swich and the park; the app only
                // tells a park that revoked the platform's authorisation (Annex 3 §7 — lasting, unlike a
                // suspension) apart from one that paused cashouts.
                cashoutsPaused = cashoutsPausedReason is not null,
                cashoutsPausedReason,
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

        // Drivers may only add accounts in their own name; anything else goes through the park.
        var (outcome, err, payload, card) = await DestinationHelper.AddAsync(
            _db, parkId, driver, body.Iban, body.HolderName, body.MakeDefault ?? true,
            allowThirdParty: false, thirdPartyReason: null, initiatedBy: $"driver:{driverId}", ct);
        if (card is not null)
        {
            await DriverLanguage.RememberAsync(_db, driverId, Request);
            await DestinationHelper.NotifyAsync(HttpContext.RequestServices, card, PayoutAccountChange.Added, byPark: false);
        }

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

        var removed = await DestinationHelper.DeactivateAsync(_db, driverId, cardId, ct);
        if (removed is null) return NotFound(new { error = "card_not_found" });
        await DriverLanguage.RememberAsync(_db, driverId, Request);
        await DestinationHelper.NotifyAsync(HttpContext.RequestServices, removed, PayoutAccountChange.Removed, byPark: false);
        return NoContent();
    }

    [HttpPost("me/cards/{cardId:guid}/default")]
    public async Task<IActionResult> SetDefaultCard(Guid cardId, CancellationToken ct)
    {
        if (!TryGetClaims(out var driverId, out _, out var error))
            return Unauthorized(new { error });
        var (card, changed) = await DestinationHelper.SetDefaultAsync(_db, driverId, cardId, ct);
        if (card is null) return NotFound(new { error = "card_not_found" });
        // A tap on the account that already is the default changes nothing — and tells the driver nothing.
        if (changed)
        {
            await DriverLanguage.RememberAsync(_db, driverId, Request);
            await DestinationHelper.NotifyAsync(HttpContext.RequestServices, card, PayoutAccountChange.MadeDefault, byPark: false);
        }
        return NoContent();
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
                // Code only: the stored reason can carry an admin's note and e-mail (a cancellation) or bank internals.
                failureReason = FailureCodeOf(c.FailureReason),
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

    /// <summary>"CANCELLED: reason (by admin:x@y)" → "CANCELLED"; null stays null. What the driver may see of a failure.</summary>
    private static string? FailureCodeOf(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason)) return null;
        var i = failureReason.IndexOf(':');
        return (i > 0 ? failureReason[..i] : failureReason).Trim();
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
        if (IdempotencyKeys.Problem(body.IdempotencyKey) is { } keyProblem)
            return BadRequest(new { error = keyProblem, max = IdempotencyKeys.MaxLength });

        // Guard against tampered card IDs — a driver may only pay out to their own destinations.
        // A destination the driver owns but has removed (stale tab) gets a typed rejection the app can
        // translate; a destination that is not his at all is a plain 403.
        var ownedCard = await _db.BankCards.AsNoTracking()
            .Where(b => b.Id == body.CardId && b.DriverId == driverId)
            .Select(b => new { b.IsActive })
            .FirstOrDefaultAsync(ct);
        if (ownedCard is null)
            return Forbid();
        if (!ownedCard.IsActive)
            return BadRequest(new { error = "cashout_rejected", code = CashoutRejectionCodes.DestinationRemoved,
                                    message = "Payout destination has been removed", @params = new { } });

        try
        {
            var result = await _orchestrator.RunAsync(new CashoutSagaRequest(
                ParkId: parkId,
                DriverId: driverId,
                BankCardId: body.CardId,
                Amount: body.Amount,
                IdempotencyKey: body.IdempotencyKey,
                InitiatedBy: $"driver:{driverId}"), ct);

            // Whatever the saga returns — a fresh result or the stored row behind a repeated key — the driver
            // sees the failure CODE only. The stored reason is internal: a cancellation carries the admin's
            // note and e-mail, a bank failure the bank's detail, a wait the park account's balance.
            var mine = result with { FailureReason = FailureCodeOf(result.FailureReason) };
            return mine.Status switch
            {
                "Completed"      => Ok(mine),
                "Queued"         => StatusCode(202, mine),
                "ReviewRequired" => StatusCode(202, mine),
                "Failed"         => UnprocessableEntity(mine),
                _                => Ok(mine),
            };
        }
        catch (CashoutRejectedException ex)
        {
            _log.LogWarning("Driver cashout rejected ({Code}) for {DriverId}: {Message}", ex.Code, driverId, ex.Message);
            return BadRequest(new { error = "cashout_rejected", code = ex.Code, message = ex.Message, @params = ex.Params });
        }
        catch (InvalidOperationException ex)
        {
            // The text is internal (framework or bank detail): the log keeps it, the driver gets the code.
            _log.LogWarning(ex, "Driver cashout rejected for {DriverId}", driverId);
            return BadRequest(new { error = "cashout_rejected", code = "rejected" });
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
                data = n.Data,
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

    /// <summary>How old the sync worker's cached balance may be before /me goes live to Yandex.</summary>
    private const int BalanceFreshSeconds = 120;

    /// <summary>At most one live Yandex read per driver per this many seconds, fresh=true or not (see <see cref="Me"/>).</summary>
    private const int LiveReadMinSeconds = 30;

    /// <summary>When each driver last went live (one entry per driver; this instance only — the API runs as one).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> LastLiveRead = new();

    /// <summary>Takes this driver's live-read slot if the last live read is older than the window — atomically, so N
    /// simultaneous requests get one live read between them, not N.</summary>
    private static bool TryClaimLiveRead(Guid driverId, DateTime now)
    {
        while (true)
        {
            if (!LastLiveRead.TryGetValue(driverId, out var last))
            {
                if (LastLiveRead.TryAdd(driverId, now)) return true;
                continue;
            }
            if (now - last < TimeSpan.FromSeconds(LiveReadMinSeconds)) return false;
            if (LastLiveRead.TryUpdate(driverId, now, last)) return true;
        }
    }

    private async Task UpsertBalanceCacheAsync(Guid driverId, Guid parkId, decimal balance, string currency, DateTime now, CancellationToken ct)
    {
        try
        {
            var row = await _db.YandexBalanceCaches.FirstOrDefaultAsync(c => c.DriverId == driverId, ct);
            if (row is null)
                _db.YandexBalanceCaches.Add(new Core.Entities.YandexBalanceCache
                {
                    DriverId = driverId, ParkId = parkId, Balance = balance, Currency = currency ?? "GEL", UpdatedAt = now,
                });
            else
            {
                row.Balance = balance;
                row.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Balance cache upsert skipped for driver {DriverId}", driverId);
        }
    }

}

public record CreateMyCashoutRequest(Guid CardId, decimal Amount, string IdempotencyKey);
public record RideDto(string OrderId, decimal Amount, string? From, string? To, DateTime CreatedAt);
public record AddMyCardRequest(string Iban, string? HolderName, bool? MakeDefault);
