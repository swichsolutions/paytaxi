using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Cashout endpoints for the manager admin panel. The saga itself lives in
/// <see cref="ICashoutOrchestrator"/>; this controller only translates HTTP.
///
/// Auth deferred to Phase 8 along with the rest of the admin surface.
/// </summary>
[ApiController]
[Route("api/admin/parks/{parkId:guid}/cashouts")]
public class CashoutsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICashoutOrchestrator _orchestrator;
    private readonly ILogger<CashoutsController> _log;

    public CashoutsController(
        AppDbContext db,
        ICashoutOrchestrator orchestrator,
        ILogger<CashoutsController> log)
    {
        _db = db;
        _orchestrator = orchestrator;
        _log = log;
    }

    /// <summary>Recent cashouts for the park, newest first.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid parkId, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var rows = await _db.Cashouts
            .AsNoTracking()
            .Where(c => c.ParkId == parkId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .Select(c => new
            {
                id = c.Id,
                driverId = c.DriverId,
                driverName = c.Driver.Name,
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

        return Ok(new { parkId, count = rows.Count, cashouts = rows });
    }

    /// <summary>
    /// Manager-initiated cashout. Body: { driverId, amount, cardId, idempotencyKey, initiatedBy? }.
    /// Idempotent on idempotencyKey — a repeat call returns the existing cashout.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid parkId,
        [FromBody] CreateCashoutRequest body,
        CancellationToken ct)
    {
        if (body is null) return BadRequest(new { error = "missing_body" });
        if (body.DriverId == Guid.Empty) return BadRequest(new { error = "driver_id_required" });
        if (body.CardId == Guid.Empty)   return BadRequest(new { error = "card_id_required" });
        if (body.Amount <= 0)            return BadRequest(new { error = "amount_must_be_positive" });
        if (string.IsNullOrWhiteSpace(body.IdempotencyKey))
            return BadRequest(new { error = "idempotency_key_required" });

        try
        {
            var result = await _orchestrator.RunAsync(new CashoutSagaRequest(
                ParkId: parkId,
                DriverId: body.DriverId,
                BankCardId: body.CardId,
                Amount: body.Amount,
                IdempotencyKey: body.IdempotencyKey,
                InitiatedBy: body.InitiatedBy ?? "admin"), ct);

            return result.Status switch
            {
                "Completed"      => Ok(result),
                "ReviewRequired" => StatusCode(202, result), // accepted but needs human follow-up
                "Failed"         => UnprocessableEntity(result),
                _                => Ok(result),
            };
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Cashout rejected for park={ParkId} driver={DriverId}", parkId, body.DriverId);
            return BadRequest(new { error = "cashout_rejected", message = ex.Message });
        }
    }
}

public record CreateCashoutRequest(
    Guid DriverId,
    Guid CardId,
    decimal Amount,
    string IdempotencyKey,
    string? InitiatedBy);
