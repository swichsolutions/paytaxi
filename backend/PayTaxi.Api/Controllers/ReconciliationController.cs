using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/parks/{parkId:guid}/reconciliation")]
public class ReconciliationController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly ReconciliationWorker _worker;
    private readonly ILogger<ReconciliationController> _log;

    public ReconciliationController(AppDbContext db, ReconciliationWorker worker, ILogger<ReconciliationController> log)
    {
        _db = db;
        _worker = worker;
        _log = log;
    }

    /// <summary>
    /// Run reconciliation for this park now (no grace period, so anything already final is compared).
    /// Swich and the operator only — it hits the bank and Yandex APIs for the whole window.
    /// Returns the run that was just written.
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunNow(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (!SeesAllParks) return Forbid();

        var reconciled = await _worker.RunAsync(parkId, graceMinutes: 0, ct);
        if (reconciled == 0) return NotFound(new { error = "park_not_found_or_inactive" });

        var run = await _db.ReconciliationRuns.AsNoTracking()
            .Where(r => r.ParkId == parkId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new
            {
                id = r.Id,
                status = r.Status.ToString(),
                windowFrom = r.WindowFrom,
                windowTo = r.WindowTo,
                cashoutsScanned = r.CashoutsScanned,
                bankTransfersScanned = r.BankTransfersScanned,
                yandexTxScanned = r.YandexTxScanned,
                discrepanciesFound = r.DiscrepanciesFound,
                error = r.Error,
                startedAt = r.StartedAt,
                finishedAt = r.FinishedAt,
            })
            .FirstAsync(ct);
        _log.LogInformation("Reconciliation run {RunId} for park {ParkId} triggered by {Actor}", run.id, parkId, ActorLabel);
        return Ok(run);
    }

    /// <summary>Recent reconciliation runs for the park, newest-first.</summary>
    [HttpGet("runs")]
    public async Task<IActionResult> ListRuns(Guid parkId, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        take = Math.Clamp(take, 1, 100);

        var rows = await _db.ReconciliationRuns
            .AsNoTracking()
            .Where(r => r.ParkId == parkId)
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .Select(r => new
            {
                id = r.Id,
                windowFrom = r.WindowFrom,
                windowTo = r.WindowTo,
                startedAt = r.StartedAt,
                finishedAt = r.FinishedAt,
                status = r.Status.ToString(),
                cashoutsScanned = r.CashoutsScanned,
                bankTransfersScanned = r.BankTransfersScanned,
                yandexTxScanned = r.YandexTxScanned,
                discrepanciesFound = r.DiscrepanciesFound,
                error = r.Error,
            })
            .ToListAsync(ct);

        return Ok(new { parkId, count = rows.Count, runs = rows });
    }

    /// <summary>Open discrepancies for the park (unresolved). Use <c>?all=true</c> to include resolved ones.</summary>
    [HttpGet("discrepancies")]
    public async Task<IActionResult> ListDiscrepancies(
        Guid parkId,
        [FromQuery] bool all = false,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        take = Math.Clamp(take, 1, 200);

        var query = _db.ReconciliationDiscrepancies
            .AsNoTracking()
            .Where(d => d.ParkId == parkId);
        if (!all) query = query.Where(d => !d.IsResolved);

        var rows = await query
            .OrderByDescending(d => d.CreatedAt)
            .Take(take)
            .Select(d => new
            {
                id = d.Id,
                runId = d.RunId,
                cashoutId = d.CashoutId,
                kind = d.Kind,
                paytaxiAmount = d.PaytaxiAmount,
                externalAmount = d.ExternalAmount,
                bankTransferId = d.BankTransferId,
                yandexTransactionId = d.YandexTransactionId,
                notes = d.Notes,
                isResolved = d.IsResolved,
                resolvedAt = d.ResolvedAt,
                resolvedBy = d.ResolvedBy,
                resolutionNotes = d.ResolutionNotes,
                createdAt = d.CreatedAt,
            })
            .ToListAsync(ct);

        var openCount = await _db.ReconciliationDiscrepancies
            .CountAsync(d => d.ParkId == parkId && !d.IsResolved, ct);

        return Ok(new { parkId, openCount, count = rows.Count, discrepancies = rows });
    }

    /// <summary>Mark a discrepancy resolved. Body: { notes }.</summary>
    [HttpPost("discrepancies/{discrepancyId:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid parkId,
        Guid discrepancyId,
        [FromBody] ResolveDiscrepancyRequest? body,
        CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();

        var discrepancy = await _db.ReconciliationDiscrepancies
            .FirstOrDefaultAsync(d => d.Id == discrepancyId && d.ParkId == parkId, ct);
        if (discrepancy is null) return NotFound(new { error = "discrepancy_not_found" });
        if (discrepancy.IsResolved) return BadRequest(new { error = "already_resolved" });

        discrepancy.IsResolved = true;
        discrepancy.ResolvedAt = DateTime.UtcNow;
        discrepancy.ResolvedBy = ActorLabel;
        discrepancy.ResolutionNotes = body?.Notes;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Discrepancy {Id} ({Kind}) resolved by {Resolver}",
            discrepancy.Id, discrepancy.Kind, discrepancy.ResolvedBy);

        return NoContent();
    }
}

public record ResolveDiscrepancyRequest(string? Notes);
