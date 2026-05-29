using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Cashout endpoints for the manager admin panel. The saga itself lives in
/// <see cref="ICashoutOrchestrator"/>; this controller only translates HTTP.
///
/// Driver-initiated cashouts go through <see cref="DriverController"/>.CreateMyCashout
/// instead, which derives driverId from the JWT.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/parks/{parkId:guid}/cashouts")]
public class CashoutsController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICashoutOrchestrator _orchestrator;
    private readonly IInvoiceGenerator _invoices;
    private readonly ILogger<CashoutsController> _log;

    public CashoutsController(
        AppDbContext db,
        ICashoutOrchestrator orchestrator,
        IInvoiceGenerator invoices,
        ILogger<CashoutsController> log)
    {
        _db = db;
        _orchestrator = orchestrator;
        _invoices = invoices;
        _log = log;
    }

    /// <summary>Recent cashouts for the park, newest first.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid parkId, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (!CanAccessPark(parkId)) return Forbid();

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
                bankType = c.BankCard.BankType,
                maskedPan = c.BankCard.MaskedPan,
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
        if (!CanAccessPark(parkId)) return Forbid();
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

    /// <summary>
    /// Stream the PDF invoice for a completed cashout. Returns 404 if the cashout
    /// doesn't exist for this park, or 409 if it's not in Completed status.
    /// </summary>
    [HttpGet("{cashoutId:guid}/invoice.pdf")]
    public async Task<IActionResult> Invoice(Guid parkId, Guid cashoutId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var exists = await _db.Cashouts.AsNoTracking()
            .AnyAsync(c => c.Id == cashoutId && c.ParkId == parkId, ct);
        if (!exists) return NotFound(new { error = "cashout_not_found" });

        try
        {
            var pdf = await _invoices.RenderAsync(cashoutId, ct);
            var filename = await _invoices.GetFileNameAsync(cashoutId, ct);
            return File(pdf, "application/pdf", filename);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "invoice_not_available", message = ex.Message });
        }
    }

    /// <summary>
    /// Retry a failed cashout. Creates a NEW cashout row (with a fresh
    /// idempotency key) that re-runs the saga using the original
    /// driverId/cardId/amount. The original Failed row is preserved as a
    /// historical record. Only allowed when the source cashout's status
    /// is Failed; for <c>ReviewRequired</c> a human must reconcile first.
    /// </summary>
    [HttpPost("{cashoutId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid parkId, Guid cashoutId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var source = await _db.Cashouts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cashoutId && c.ParkId == parkId, ct);
        if (source is null) return NotFound(new { error = "cashout_not_found" });

        if (source.Status != Core.Enums.CashoutStatus.Failed)
        {
            return BadRequest(new
            {
                error = "retry_not_allowed",
                message = $"Cannot retry a cashout in status '{source.Status}'. Only Failed cashouts are retryable.",
            });
        }

        var newKey = $"retry:{source.Id:N}:{Guid.NewGuid():N}";
        try
        {
            var result = await _orchestrator.RunAsync(new CashoutSagaRequest(
                ParkId: source.ParkId,
                DriverId: source.DriverId,
                BankCardId: source.BankCardId,
                Amount: source.Amount,
                IdempotencyKey: newKey,
                InitiatedBy: $"admin-retry:{source.Id}"), ct);

            _log.LogInformation(
                "Retried cashout {SourceId} → new cashout {NewId} status={Status}",
                source.Id, result.CashoutId, result.Status);

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
            _log.LogWarning(ex, "Cashout retry rejected for source={CashoutId}", source.Id);
            return BadRequest(new { error = "retry_rejected", message = ex.Message });
        }
    }
}

public record CreateCashoutRequest(
    Guid DriverId,
    Guid CardId,
    decimal Amount,
    string IdempotencyKey,
    string? InitiatedBy);
