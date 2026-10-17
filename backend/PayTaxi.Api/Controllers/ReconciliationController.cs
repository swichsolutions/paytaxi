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
        if (!CanAccessPark(parkId) || !IsSwichOrOperator) return Forbid();

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
        var seesWho = IsSwichOrOperator;

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
                // The park's explanation: everyone who sees the item reads it; who wrote it is the platform side's to see.
                parkNote = d.ParkNote,
                parkNoteAt = d.ParkNoteAt,
                parkNoteBy = seesWho ? d.ParkNoteBy : null,
                createdAt = d.CreatedAt,
            })
            .ToListAsync(ct);

        var openCount = await _db.ReconciliationDiscrepancies
            .CountAsync(d => d.ParkId == parkId && !d.IsResolved, ct);

        return Ok(new
        {
            parkId, openCount, count = rows.Count, discrepancies = rows,
            // What the console offers this caller: Resolve (Swich, the operator of the park's licensee) or Add note (the park).
            canResolve = IsSwichOrOperator,
            canNote = !IsSwichOrOperator,
        });
    }

    /// <summary>A resolution's reason must say something: at least this many characters (trimmed).</summary>
    public const int MinReasonLength = 5;
    private const int MaxTextLength = 1000;

    /// <summary>
    /// Mark a discrepancy resolved. Body: { notes } — the reason, required (at least <see cref="MinReasonLength"/>
    /// characters). Swich, or the operator of the park's licensee (CanAccessPark), only: every kind is about money — a
    /// payout, a bank debit or a Yandex debit that does not match — and the check exists for the platform side, so the
    /// park whose payouts it is about cannot close it (it used to: a resolved item leaves the banner for good). The park's
    /// manager explains instead (<see cref="AddNote"/>); the note stays on the item for whoever resolves it.
    /// </summary>
    [HttpPost("discrepancies/{discrepancyId:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid parkId,
        Guid discrepancyId,
        [FromBody] ResolveDiscrepancyRequest? body,
        CancellationToken ct)
    {
        if (!CanAccessPark(parkId) || !IsSwichOrOperator) return Forbid();
        var reason = body?.Notes?.Trim();
        if (reason is null || reason.Length < MinReasonLength)
            return BadRequest(new { error = "reason_required", minLength = MinReasonLength });
        if (reason.Length > MaxTextLength) return BadRequest(new { error = "reason_too_long", max = MaxTextLength });

        var discrepancy = await _db.ReconciliationDiscrepancies
            .FirstOrDefaultAsync(d => d.Id == discrepancyId && d.ParkId == parkId, ct);
        if (discrepancy is null) return NotFound(new { error = "discrepancy_not_found" });
        if (discrepancy.IsResolved) return BadRequest(new { error = "already_resolved" });

        discrepancy.IsResolved = true;
        discrepancy.ResolvedAt = DateTime.UtcNow;
        discrepancy.ResolvedBy = ActorLabel;
        discrepancy.ResolutionNotes = reason;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Discrepancy {Id} ({Kind}) resolved by {Resolver}",
            discrepancy.Id, discrepancy.Kind, discrepancy.ResolvedBy);

        return NoContent();
    }

    /// <summary>
    /// The park's note on an open discrepancy (its manager: why it happened, what was done). Body: { note }. It resolves
    /// nothing — the item stays open and on the banner until Swich or the operator resolves it, and they read the note when
    /// they do. Replaces an earlier note of the park; refused once the item is resolved.
    /// </summary>
    [HttpPost("discrepancies/{discrepancyId:guid}/note")]
    public async Task<IActionResult> AddNote(
        Guid parkId,
        Guid discrepancyId,
        [FromBody] DiscrepancyNoteRequest? body,
        CancellationToken ct)
    {
        // The platform side writes its word as the resolution's reason; the note is the park's.
        if (!CanAccessPark(parkId) || IsSwichOrOperator) return Forbid();
        var note = body?.Note?.Trim();
        if (string.IsNullOrEmpty(note)) return BadRequest(new { error = "note_required" });
        if (note.Length > MaxTextLength) return BadRequest(new { error = "note_too_long", max = MaxTextLength });

        var discrepancy = await _db.ReconciliationDiscrepancies
            .FirstOrDefaultAsync(d => d.Id == discrepancyId && d.ParkId == parkId, ct);
        if (discrepancy is null) return NotFound(new { error = "discrepancy_not_found" });
        if (discrepancy.IsResolved) return Conflict(new { error = "already_resolved" });

        discrepancy.ParkNote = note;
        discrepancy.ParkNoteBy = ActorLabel;
        discrepancy.ParkNoteAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Discrepancy {Id} ({Kind}) noted by {Actor}", discrepancy.Id, discrepancy.Kind, ActorLabel);

        return Ok(new { id = discrepancy.Id, isResolved = false, parkNote = discrepancy.ParkNote, parkNoteAt = discrepancy.ParkNoteAt });
    }
}

public record ResolveDiscrepancyRequest(string? Notes);

public record DiscrepancyNoteRequest(string? Note);
