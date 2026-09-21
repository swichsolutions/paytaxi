using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Settlement views + actions.
///   - Swich (super_admin): every park, statuses, retry failed ones, run on demand.
///   - Operator (Levan's company): every park, read-only — recovery progress, history, per-day fees.
///   - Park admin: their own park's rows.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin")]
public class SettlementsController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISettlementService _settlements;
    private readonly IInvoiceGenerator _invoices;
    private readonly ILogger<SettlementsController> _log;

    public SettlementsController(AppDbContext db, ISettlementService settlements, IInvoiceGenerator invoices, ILogger<SettlementsController> log)
    {
        _db = db;
        _settlements = settlements;
        _invoices = invoices;
        _log = log;
    }

    /// <summary>Settlement rows, newest first. Park admins are scoped to their park.</summary>
    [HttpGet("settlements")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? parkId, [FromQuery] string? status, [FromQuery] int take = 60, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 365);
        var query = _db.Settlements.AsNoTracking().AsQueryable();

        var scoped = ScopedParkId;
        if (scoped is not null) query = query.Where(s => s.ParkId == scoped);
        else if (parkId is not null) query = query.Where(s => s.ParkId == parkId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SettlementStatus>(status, true, out var st))
            query = query.Where(s => s.Status == st);

        var rows = await query
            .OrderByDescending(s => s.SettlementDate).ThenByDescending(s => s.CreatedAt)
            .Take(take)
            .Select(s => new
            {
                id = s.Id,
                parkId = s.ParkId,
                parkName = s.Park.Name,
                settlementDate = s.SettlementDate,
                periodFromUtc = s.PeriodFromUtc,
                periodToUtc = s.PeriodToUtc,
                cashoutCount = s.CashoutCount,
                feeTotal = s.FeeTotal,
                phase1Fees = s.Phase1Fees,
                phase2Fees = s.Phase2Fees,
                swichShare = s.SwichShare,
                parkShare = s.ParkShare,
                cumulativeFeesBefore = s.CumulativeFeesBefore,
                status = s.Status.ToString(),
                bankTransferId = s.BankTransferId,
                failureReason = s.FailureReason,
                attemptCount = s.AttemptCount,
                lastAttemptAt = s.LastAttemptAt,
                completedAt = s.CompletedAt,
                description = s.Description,
                invoiceRef = s.InvoiceRef,
                sourceIban = s.ParkBankAccount != null ? s.ParkBankAccount.Iban : null,
                swichIban = s.SwichIban,
                initiatedBy = s.InitiatedBy,
                createdAt = s.CreatedAt,
            })
            .ToListAsync(ct);

        var totals = new
        {
            completedSwichShare = rows.Where(r => r.status == "Completed").Sum(r => r.swichShare),
            failedCount = rows.Count(r => r.status == "Failed"),
            failedSwichShare = rows.Where(r => r.status == "Failed").Sum(r => r.swichShare),
        };

        return Ok(new { count = rows.Count, totals, settlements = rows });
    }

    /// <summary>The covered cashouts of one settlement (dispute resolution = expand one row).</summary>
    [HttpGet("settlements/{id:guid}/cashouts")]
    public async Task<IActionResult> Cashouts(Guid id, CancellationToken ct)
    {
        var s = await _db.Settlements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { error = "settlement_not_found" });
        if (!CanAccessPark(s.ParkId)) return Forbid();

        var rows = await _db.Cashouts.AsNoTracking()
            .Where(c => c.SettlementId == id)
            .OrderBy(c => c.CompletedAt)
            .Select(c => new
            {
                id = c.Id,
                driverName = c.Driver.Name,
                amount = c.Amount,
                fee = c.Fee,
                completedAt = c.CompletedAt,
                bankTransferId = c.BankTransferId,
                invoiceNumber = c.InvoiceNumber,
            })
            .ToListAsync(ct);

        return Ok(new { settlementId = id, count = rows.Count, cashouts = rows });
    }

    /// <summary>
    /// Recovery / revenue summary for one park: phase config, cumulative fees, what has
    /// been settled to Swich, what is waiting for tonight, and per-day fee totals.
    /// </summary>
    [HttpGet("parks/{parkId:guid}/settlements/summary")]
    public async Task<IActionResult> Summary(Guid parkId, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        days = Math.Clamp(days, 1, 90);

        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        var completed = await _db.Cashouts.AsNoTracking()
            .Where(c => c.ParkId == parkId && c.Status == CashoutStatus.Completed)
            .Select(c => new { c.Fee, c.Amount, c.CompletedAt, c.SettlementId })
            .ToListAsync(ct);

        var settlements = await _db.Settlements.AsNoTracking()
            .Where(s => s.ParkId == parkId)
            .Select(s => new { s.Status, s.SwichShare, s.FeeTotal, s.Phase1Fees, s.SettlementDate })
            .ToListAsync(ct);

        var cumulativeFees = completed.Sum(c => c.Fee);
        var settledFees = settlements.Where(s => s.Status == SettlementStatus.Completed).Sum(s => s.FeeTotal);
        var settledToSwich = settlements.Where(s => s.Status == SettlementStatus.Completed).Sum(s => s.SwichShare);
        var failedToSwich = settlements.Where(s => s.Status == SettlementStatus.Failed).Sum(s => s.SwichShare);
        var unsettled = completed.Where(c => c.SettlementId == null).ToList();

        // Phase-1 progress counts every fee ever earned by the park (settled or not) against the cap.
        var cap = park.Phase1CapGel;
        var phase1Progress = cap is { } capValue ? Math.Min(cumulativeFees, capValue) : (decimal?)null;

        var tz = SettlementService.ResolveTimeZone(null);
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var daily = Enumerable.Range(0, days)
            .Select(i => todayLocal.AddDays(-i))
            .Select(d =>
            {
                var inDay = completed.Where(c => c.CompletedAt is { } at &&
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at, tz)) == d).ToList();
                var s = settlements.FirstOrDefault(x => x.SettlementDate == d);
                return new
                {
                    date = d,
                    cashouts = inDay.Count,
                    volume = inDay.Sum(c => c.Amount),
                    fees = inDay.Sum(c => c.Fee),
                    settlementStatus = s?.Status.ToString(),
                    swichShare = s?.SwichShare,
                };
            })
            .ToList();

        return Ok(new
        {
            park = new
            {
                id = park.Id,
                name = park.Name,
                cashoutFee = park.CashoutFee,
                swichSharePercent = park.SwichSharePercent,
                phase1SharePercent = park.Phase1SharePercent,
                phase1CapGel = park.Phase1CapGel,
            },
            cumulativeFees,
            phase1Progress,
            phase1Remaining = cap is { } c2 ? Math.Max(0, c2 - cumulativeFees) : (decimal?)null,
            inPhase1 = cap is { } c3 && cumulativeFees < c3,
            settledFees,
            settledToSwich,
            failedToSwich,
            unsettled = new { count = unsettled.Count, fees = unsettled.Sum(c => c.Fee) },
            daily,
            asOf = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// The park's settlement months — one row per calendar month that has at least one
    /// settlement, newest first, with the figures the monthly invoice PT-YYYY-MM carries.
    /// </summary>
    [HttpGet("parks/{parkId:guid}/settlements/months")]
    public async Task<IActionResult> Months(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (!await _db.Parks.AsNoTracking().AnyAsync(p => p.Id == parkId, ct))
            return NotFound(new { error = "park_not_found" });

        var rows = await _db.Settlements.AsNoTracking()
            .Where(s => s.ParkId == parkId)
            .Select(s => new { s.SettlementDate, s.CashoutCount, s.FeeTotal, s.SwichShare, s.Status })
            .ToListAsync(ct);

        var tz = SettlementService.ResolveTimeZone(null);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);

        var months = rows
            .GroupBy(r => new { r.SettlementDate.Year, r.SettlementDate.Month })
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Select(g => new
            {
                month = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                invoiceRef = $"PT-{g.Key.Year:D4}-{g.Key.Month:D2}",
                isCurrent = g.Key.Year == nowLocal.Year && g.Key.Month == nowLocal.Month,
                settlements = g.Count(),
                cashouts = g.Sum(r => r.CashoutCount),
                feeTotal = g.Sum(r => r.FeeTotal),
                swichShare = g.Sum(r => r.SwichShare),
                transferred = g.Where(r => r.Status == SettlementStatus.Completed).Sum(r => r.SwichShare),
                outstanding = g.Where(r => r.Status != SettlementStatus.Completed).Sum(r => r.SwichShare),
                failedCount = g.Count(r => r.Status == SettlementStatus.Failed),
                pendingCount = g.Count(r => r.Status == SettlementStatus.Pending || r.Status == SettlementStatus.Processing),
            })
            .ToList();

        return Ok(new { parkId, count = months.Count, months });
    }

    /// <summary>
    /// The monthly invoice PT-YYYY-MM (Swich → park) as PDF: every nightly settlement of the
    /// month, what was transferred and what is still outstanding. 404 when the park has no
    /// settlement in that month. Anyone who can see the park may download it.
    /// </summary>
    [HttpGet("parks/{parkId:guid}/settlements/invoice.pdf")]
    public async Task<IActionResult> MonthlyInvoice(Guid parkId, [FromQuery] string? month, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (string.IsNullOrWhiteSpace(month) ||
            !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var first))
            return BadRequest(new { error = "invalid_month", message = "month must be YYYY-MM" });

        var park = await _db.Parks.AsNoTracking().Select(p => new { p.Id, p.Slug }).FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });

        try
        {
            var pdf = await _invoices.RenderMonthlyAsync(parkId, first.Year, first.Month, ct);
            _log.LogInformation("Monthly invoice PT-{Month} for park {Park} downloaded by {Actor}", month, park.Slug, ActorLabel);
            return File(pdf, "application/pdf", _invoices.MonthlyFileName(park.Slug, first.Year, first.Month));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = "no_settlements_in_month", message = ex.Message });
        }
    }

    /// <summary>Retry a Failed settlement (Swich only).</summary>
    [HttpPost("settlements/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        try
        {
            var s = await _settlements.RetryAsync(id, ActorLabel, ct);
            return s.Status == SettlementStatus.Completed
                ? Ok(ToDto(s))
                : UnprocessableEntity(ToDto(s));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "retry_rejected", message = ex.Message });
        }
    }

    /// <summary>
    /// Run the settlement on demand (Swich only): for one park or all, for a given local
    /// date (default: today, i.e. everything completed so far). Idempotent per park/day.
    /// </summary>
    [HttpPost("settlements/run")]
    public async Task<IActionResult> RunNow([FromQuery] Guid? parkId, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var tz = SettlementService.ResolveTimeZone(null);
        var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var by = ActorLabel;

        if (parkId is { } pid)
        {
            var s = await _settlements.RunForParkAsync(pid, day, by, ct);
            return s is null
                ? Ok(new { parkId = pid, settlementDate = day, created = false, message = "Nothing to settle" })
                : Ok(ToDto(s));
        }

        var summary = await _settlements.RunDueAsync(day, by, ct);
        return Ok(summary);
    }

    private static object ToDto(Settlement s) => new
    {
        id = s.Id,
        parkId = s.ParkId,
        settlementDate = s.SettlementDate,
        cashoutCount = s.CashoutCount,
        feeTotal = s.FeeTotal,
        phase1Fees = s.Phase1Fees,
        phase2Fees = s.Phase2Fees,
        swichShare = s.SwichShare,
        parkShare = s.ParkShare,
        cumulativeFeesBefore = s.CumulativeFeesBefore,
        status = s.Status.ToString(),
        bankTransferId = s.BankTransferId,
        failureReason = s.FailureReason,
        attemptCount = s.AttemptCount,
        completedAt = s.CompletedAt,
        description = s.Description,
        invoiceRef = s.InvoiceRef,
    };
}
