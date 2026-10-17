using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

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
                yandexReversalTransactionId = c.YandexReversalTransactionId,
                failureReason = c.FailureReason,
                initiatedBy = c.InitiatedBy,
                // Retry bookkeeping: which failed row this one retries, and — for a Failed row — the
                // live retry that already exists (so the console hides its Retry button).
                retryOfCashoutId = c.RetryOfCashoutId,
                retriedByCashoutId = _db.Cashouts
                    .Where(r => r.RetryOfCashoutId == c.Id && r.Status != Core.Enums.CashoutStatus.Failed)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefault(),
                attemptCount = c.AttemptCount,
                nextAttemptAt = c.NextAttemptAt,
                unresolvedAttempts = c.UnresolvedAttempts,
                // A document went to the bank and the bank never said what became of it: the payout may be
                // paid, so the console can't offer Cancel (the claim refuses it anyway). Sticky — a later,
                // unrelated answer (an expired password, "no money") does not clear it.
                outcomeUnknown = c.UnknownOutcomeDocument != null || c.UnresolvedAttempts > 0
                    || (c.FailureReason != null && (c.FailureReason.StartsWith("BANK_EXCEPTION:")
                        || c.FailureReason.Contains("_UNRESOLVED:") || c.FailureReason.Contains("_UNKNOWN:"))),
                unknownOutcomeDocument = c.UnknownOutcomeDocument,
                createdAt = c.CreatedAt,
                completedAt = c.CompletedAt,
                bankType = c.BankCard.BankType,
                maskedPan = c.BankCard.MaskedPan,
                destinationIban = c.BankCard.Iban,
                holderName = c.BankCard.HolderName,
                isThirdPartyAccount = c.BankCard.IsThirdPartyAccount,
                sourceIban = c.ParkBankAccount != null ? c.ParkBankAccount.Iban : null,
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
        // Also refuses the shapes of the server's own bank document ids (settle:, rev:, retry:, #n) — see IdempotencyKeys.
        if (IdempotencyKeys.Problem(body.IdempotencyKey) is { } keyProblem)
            return BadRequest(new { error = keyProblem, max = IdempotencyKeys.MaxLength });

        try
        {
            var result = await _orchestrator.RunAsync(new CashoutSagaRequest(
                ParkId: parkId,
                DriverId: body.DriverId,
                BankCardId: body.CardId,
                Amount: body.Amount,
                IdempotencyKey: body.IdempotencyKey,
                InitiatedBy: ActorLabel), ct);

            return SagaResponse(result);
        }
        catch (CashoutRejectedException ex)
        {
            _log.LogWarning("Cashout rejected ({Code}) for park={ParkId} driver={DriverId}: {Message}", ex.Code, parkId, body.DriverId, ex.Message);
            return BadRequest(new { error = "cashout_rejected", code = ex.Code, message = ex.Message, @params = ex.Params });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Cashout rejected for park={ParkId} driver={DriverId}", parkId, body.DriverId);
            return BadRequest(new { error = "cashout_rejected", code = "rejected", message = ex.Message });
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
    /// is Failed; for <c>ReviewRequired</c> a human must reconcile first,
    /// and <c>Queued</c> rows retry themselves (until paid, or cancelled).
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

        // One live retry per source. A second click, a second manager or a stale tab must not pay the
        // driver again; only when the previous retry itself Failed may another be started.
        var priorRetries = await _db.Cashouts.AsNoTracking()
            .Where(c => c.RetryOfCashoutId == source.Id)
            .Select(c => new { c.Id, c.Status })
            .ToListAsync(ct);
        var live = priorRetries.FirstOrDefault(r => r.Status != Core.Enums.CashoutStatus.Failed);
        if (live is not null)
        {
            return Conflict(new
            {
                error = "already_retried",
                retryCashoutId = live.Id,
                status = live.Status.ToString(),
                message = $"This cashout was already retried (cashout {live.Id}, status {live.Status}).",
            });
        }

        // Deterministic key per attempt: two simultaneous clicks collapse onto the same saga run.
        var newKey = $"retry:{source.Id:N}:{priorRetries.Count + 1}";
        try
        {
            var result = await _orchestrator.RunAsync(new CashoutSagaRequest(
                ParkId: source.ParkId,
                DriverId: source.DriverId,
                BankCardId: source.BankCardId,
                Amount: source.Amount,
                IdempotencyKey: newKey,
                InitiatedBy: $"{ActorLabel} retry:{source.Id}",
                RetryOfCashoutId: source.Id), ct);

            _log.LogInformation(
                "Retried cashout {SourceId} → new cashout {NewId} status={Status}",
                source.Id, result.CashoutId, result.Status);

            return SagaResponse(result);
        }
        catch (CashoutRejectedException ex)
        {
            _log.LogWarning("Cashout retry rejected ({Code}) for source={CashoutId}: {Message}", ex.Code, source.Id, ex.Message);
            return BadRequest(new { error = "cashout_rejected", code = ex.Code, message = ex.Message, @params = ex.Params });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Cashout retry rejected for source={CashoutId}", source.Id);
            return BadRequest(new { error = "retry_rejected", message = ex.Message });
        }
    }

    /// <summary>
    /// Nudge a Queued cashout: clear its backoff so the payout worker picks it up on
    /// the next tick (e.g. right after the park topped up its account).
    /// </summary>
    [HttpPost("{cashoutId:guid}/process-now")]
    public async Task<IActionResult> ProcessNow(Guid parkId, Guid cashoutId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var rows = await _db.Cashouts
            .Where(c => c.Id == cashoutId && c.ParkId == parkId && c.Status == Core.Enums.CashoutStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextAttemptAt, DateTime.UtcNow), ct);
        if (rows == 0) return BadRequest(new { error = "not_queued" });

        var result = await _orchestrator.ProcessQueuedAsync(cashoutId, ct);
        return SagaResponse(result);
    }

    /// <summary>
    /// Cancel a payout that is waiting in the queue and give the gross amount back on the driver's
    /// Yandex balance. Waiting has no time limit any more (agreement §5.5, Annex 3 §5), so this is the
    /// only way a waiting payout ends without being paid: a person decides, with a reason that stays on
    /// the row ("CANCELLED: {reason} (by admin:{email})") and in the ledger.
    ///
    /// Swich and the operator only. Not the park's manager: the park is the party obliged to keep its
    /// account funded, and a payout short of funds is queued and executed once topped up (Annex 3 §5) —
    /// a transfer the driver asked for (Annex 3 §2) is not the park's to withdraw instead of topping up.
    /// A driver who wants their request back asks Swich or the operator.
    ///
    /// Only Queued rows with nothing at the bank: a transfer the bank accepted (Processing) can no longer
    /// be stopped from here, and a payout with a document the bank never answered about may already be
    /// paid — reversing it could pay the driver twice. Before the reversal the bank is asked about every
    /// document of the payout as well.
    /// 200 cancelled · 202 cancelled but the Yandex reversal needs review · 409 not cancellable.
    /// </summary>
    [HttpPost("{cashoutId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid parkId, Guid cashoutId, [FromBody] CancelCashoutRequest? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !CanAccessPark(parkId)) return Forbid();

        var reason = body?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) return BadRequest(new { error = "reason_required" });
        if (reason.Length > 500) return BadRequest(new { error = "reason_too_long", max = 500 });

        var exists = await _db.Cashouts.AsNoTracking().AnyAsync(c => c.Id == cashoutId && c.ParkId == parkId, ct);
        if (!exists) return NotFound(new { error = "cashout_not_found" });

        CashoutSagaResult? result;
        try
        {
            result = await _orchestrator.CancelQueuedAsync(parkId, cashoutId, reason, ActorLabel, ct);
        }
        catch (CashoutCancelRefusedException ex)
        {
            // The bank, asked before the reversal, holds one of the payout's documents or could not be asked.
            _log.LogWarning("Cancel of cashout {CashoutId} in park {ParkId} by {Actor} refused ({Code}): {Message}",
                cashoutId, parkId, ActorLabel, ex.Code, ex.Message);
            var status = await _db.Cashouts.AsNoTracking().Where(c => c.Id == cashoutId).Select(c => c.Status).FirstAsync(ct);
            return Conflict(new { error = ex.Code, status = status.ToString(), message = ex.Message });
        }
        if (result is null)
        {
            // Not claimable: re-read, because the worker (or another admin) may have moved it since.
            var now = await _db.Cashouts.AsNoTracking()
                .Where(c => c.Id == cashoutId)
                .Select(c => new { c.Status, c.BankTransferId, c.UnknownOutcomeDocument, c.UnresolvedAttempts, c.FailureReason })
                .FirstAsync(ct);
            if (now.Status == Core.Enums.CashoutStatus.Failed && now.FailureReason?.StartsWith("CANCELLED", StringComparison.Ordinal) == true)
                return Conflict(new { error = "already_cancelled", status = now.Status.ToString(), message = now.FailureReason });
            if (now.Status == Core.Enums.CashoutStatus.Queued
                && CashoutOrchestrator.HasUnresolvedOutcome(now.UnknownOutcomeDocument, now.UnresolvedAttempts, now.FailureReason))
                return Conflict(new
                {
                    error = "outcome_unknown",
                    status = now.Status.ToString(),
                    message = "The bank has not said what became of a payment order already sent for this payout" +
                              (now.UnknownOutcomeDocument is { } doc ? $" (document {doc})" : "") +
                              ". The payout is checked again automatically; it can be cancelled once the bank answers about it.",
                });
            return Conflict(new
            {
                error = "not_cancellable",
                status = now.Status.ToString(),
                message = now.BankTransferId is not null
                    ? $"The bank already accepted this transfer ({now.BankTransferId}); it can no longer be cancelled."
                    : $"Only a payout waiting in the queue can be cancelled (status {now.Status}).",
            });
        }

        _log.LogWarning("Cashout {CashoutId} in park {ParkId} cancelled by {Actor} → {Status}", cashoutId, parkId, ActorLabel, result.Status);
        // ReviewRequired here means the cancel stood but Yandex refused (or did not answer) the reversal.
        return result.Status == "ReviewRequired" ? StatusCode(202, result) : Ok(result);
    }

    private IActionResult SagaResponse(CashoutSagaResult result) => result.Status switch
    {
        "Completed"      => Ok(result),
        "Queued"         => StatusCode(202, result), // accepted; payout worker will finish it
        "ReviewRequired" => StatusCode(202, result), // accepted but needs human follow-up
        "Failed"         => UnprocessableEntity(result),
        _                => Ok(result),
    };
}

public record CancelCashoutRequest(string? Reason);

public record CreateCashoutRequest(
    Guid DriverId,
    Guid CardId,
    decimal Amount,
    string IdempotencyKey,
    string? InitiatedBy);
