using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Monthly settlement under the licence agreement (§2.2–2.6): the licensee's statement (income, the
/// shared costs of both sides, barrier progress, the one due amount) and the park transfers that
/// collect it, plus Swich-direct parks' own monthly rows.
///   - Swich (super_admin): everything; settle a closed month now, retry a failed transfer, record that a
///     failed transfer was paid outside the platform (§2.5: by the licensee when the park could not).
///   - Operator (the licensee's staff): ITS licensee's statements and report — the same figures Swich
///     sees (§2.6: identical data for both sides) — read-only. Another licensee's are 403 / left out
///     (<see cref="AdminControllerBase.LicenseeScopeAsync"/>); park transfers of its own licensee's parks only
///     (<see cref="AdminControllerBase.ParkFilter"/>), the agreement's part of a transfer only on its own licensee's
///     rows (<see cref="ContractViewAsync"/>).
///   - Park admin: only their own park's monthly rows and invoices, never the licensee's statement,
///     its costs or the §4.1.2 penalty (the licensee's to Swich, not the park's).
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin")]
public class SettlementsController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISettlementService _settlements;
    private readonly IInvoiceGenerator _invoices;
    private readonly SettlementOptions _opts;
    private readonly ILogger<SettlementsController> _log;

    public SettlementsController(AppDbContext db, ISettlementService settlements, IInvoiceGenerator invoices,
        IOptions<SettlementOptions> opts, ILogger<SettlementsController> log)
    {
        _db = db;
        _settlements = settlements;
        _invoices = invoices;
        _opts = opts.Value;
        _log = log;
    }

    private TimeZoneInfo Tz => SettlementService.ResolveTimeZone(_opts.TimeZoneId);

    /// <summary>Swich and the operator: the licence agreement's own figures (the §4.1.2 penalty, the outside-payment record).</summary>
    private bool SeesContract => IsSwichOrOperator;

    /// <summary>
    /// Per park row, whether the caller sees the licence agreement's part of it — the barrier split, the §4.1.2 penalty, the
    /// record of a payment outside the platform: Swich always; an operator on a Swich-direct row (as before: no agreement's
    /// figures in it) and on rows of its OWN licensee's statements — a row of another (former) licensee's statement reads as
    /// the park's manager reads it, because those figures are between that licensee and Swich; park managers never.
    /// The rows themselves are those of the operator's parks (a park that came from another licensee brings its old rows).
    /// </summary>
    private async Task<Func<Settlement, bool>> ContractViewAsync(IReadOnlyCollection<Settlement> rows, CancellationToken ct)
    {
        if (!SeesContract) return _ => false;
        var scope = await LicenseeScopeAsync(_db, ct);
        if (scope.Every) return _ => true;
        var ids = rows.Where(r => r.MonthlyStatementId != null).Select(r => r.MonthlyStatementId!.Value).Distinct().ToList();
        var own = ids.Count == 0 || scope.Only is null
            ? new HashSet<Guid>()
            : (await _db.MonthlyStatements.AsNoTracking()
                .Where(m => ids.Contains(m.Id) && m.LicenseeId == scope.Only)
                .Select(m => m.Id)
                .ToListAsync(ct)).ToHashSet();
        return s => s.MonthlyStatementId is not { } sid || own.Contains(sid);
    }

    private Task<Dictionary<Guid, List<SettlementMath.FailedAttempt>>> AttemptsAsync(IEnumerable<Settlement> rows, CancellationToken ct) =>
        SettlementOverdue.FailedAttemptsAsync(_db, rows.Where(r => r.IsMonthly).Select(r => r.Id), Tz, ct);
    private DateOnly TodayLocal => SettlementService.TodayLocal(_opts.TimeZoneId);
    private DateOnly CurrentMonth => SettlementService.FirstOfMonth(TodayLocal);

    // ═══════════════════════════════════════════════════════════════════
    // Park transfers (every role, scoped)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Settlement rows (one per park per month; legacy nightly rows too), newest first. Park admins are scoped to their park.</summary>
    [HttpGet("settlements")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? parkId, [FromQuery] string? status, [FromQuery] int take = 60, CancellationToken ct = default)
    {
        if (ParkScopeMissing) return Forbid();
        take = Math.Clamp(take, 1, 365);
        var query = _db.Settlements.AsNoTracking().AsQueryable();

        // A manager's own park, an operator's licensee's parks, every park for Swich — then the park asked for, if any.
        var parks = ParkFilter;
        if (parks is not null) query = query.Where(s => parks.Contains(s.ParkId));
        if (parkId is not null) query = query.Where(s => s.ParkId == parkId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SettlementStatus>(status, true, out var st))
            query = query.Where(s => s.Status == st);

        var rows = await query
            .Include(s => s.Park)
            .Include(s => s.ParkBankAccount)
            .OrderByDescending(s => s.SettlementDate).ThenByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        var today = TodayLocal;
        var tz = Tz;
        var attempts = await AttemptsAsync(rows, ct);
        var contract = await ContractViewAsync(rows, ct);
        var dtos = rows.Select(s => SettlementDtos.Row(s, s.Park.Name, _opts, today, tz,
            attempts.GetValueOrDefault(s.Id), contract(s), s.ParkBankAccount?.Iban)).ToList();

        var totals = new
        {
            completedSwichShare = rows.Where(r => r.Status == SettlementStatus.Completed).Sum(r => r.SwichShare),
            paidExternallySwichShare = rows.Where(r => r.Status == SettlementStatus.SettledExternally).Sum(r => r.SwichShare),
            failedCount = rows.Count(r => r.Status == SettlementStatus.Failed),
            failedSwichShare = rows.Where(r => r.Status == SettlementStatus.Failed).Sum(r => r.SwichShare),
        };

        return Ok(new { count = dtos.Count, totals, settlements = dtos });
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
    /// The park's settlement months — one row per calendar month that has a settlement, newest first,
    /// with the figures the monthly invoice PT-YYYY-MM carries and, for the monthly row, its transfer
    /// status and §4.1.2 overdue information.
    /// </summary>
    [HttpGet("parks/{parkId:guid}/settlements/months")]
    public async Task<IActionResult> Months(Guid parkId, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (!await _db.Parks.AsNoTracking().AnyAsync(p => p.Id == parkId, ct))
            return NotFound(new { error = "park_not_found" });

        var rows = await _db.Settlements.AsNoTracking()
            .Where(s => s.ParkId == parkId)
            .OrderBy(s => s.SettlementDate).ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);

        var current = CurrentMonth;
        var today = TodayLocal;
        var tz = Tz;
        var attempts = await AttemptsAsync(rows, ct);
        var contract = await ContractViewAsync(rows, ct);

        var months = rows
            .GroupBy(r => new DateOnly(r.SettlementDate.Year, r.SettlementDate.Month, 1))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                // A park moved between a licensee and Swich-direct during the month has two monthly rows for it (each fee
                // settled under the regime it was earned under). The card sums them and points at the one that still
                // needs attention: a failed transfer before one in flight, before a finished one.
                var monthly = g.Where(r => r.IsMonthly)
                    .OrderBy(r => r.Status switch
                    {
                        SettlementStatus.Failed => 0,
                        SettlementStatus.Processing => 1,
                        SettlementStatus.Pending => 2,
                        _ => 3,
                    })
                    .ThenBy(r => r.CreatedAt)
                    .FirstOrDefault();
                return new
                {
                    month = SettlementDtos.MonthKey(g.Key),
                    invoiceRef = SettlementService.InvoiceRefFor(g.Key),
                    isCurrent = g.Key == current,
                    settlements = g.Count(),
                    cashouts = g.Sum(r => r.CashoutCount),
                    feeTotal = g.Sum(r => r.FeeTotal),
                    swichShare = g.Sum(r => r.SwichShare),
                    transferred = g.Where(r => r.Status == SettlementStatus.Completed).Sum(r => r.SwichShare),
                    paidExternally = g.Where(r => r.Status == SettlementStatus.SettledExternally).Sum(r => r.SwichShare),
                    outstanding = g.Where(r => r.Status is not (SettlementStatus.Completed or SettlementStatus.SettledExternally)).Sum(r => r.SwichShare),
                    failedCount = g.Count(r => r.Status == SettlementStatus.Failed),
                    pendingCount = g.Count(r => r.Status == SettlementStatus.Pending || r.Status == SettlementStatus.Processing),
                    settlementId = monthly?.Id,
                    status = monthly?.Status.ToString(),
                    viaLicensee = monthly?.MonthlyStatementId is not null,
                    // Both regimes in this month (a reassignment mid-month): the figures above add up both rows.
                    splitRegimes = g.Any(r => r.IsMonthly && r.MonthlyStatementId is not null) && g.Any(r => r.IsMonthly && r.MonthlyStatementId is null),
                    failureCode = SettlementMath.FailureCode(monthly?.FailureReason),
                    completedAt = monthly?.CompletedAt,
                    bankTransferId = monthly?.BankTransferId,
                    overdue = monthly is null ? null : SettlementDtos.Overdue(monthly, attempts.GetValueOrDefault(monthly.Id), _opts, today, tz, contract(monthly)),
                    external = monthly is null ? null : SettlementDtos.External(monthly, contract(monthly)),
                };
            })
            .ToList();

        return Ok(new { parkId, count = months.Count, months });
    }

    /// <summary>
    /// The monthly invoice PT-YYYY-MM (Swich → park) as PDF: the park's settlement for that month,
    /// what was transferred and what is still outstanding. 404 when the park has no settlement in that
    /// month. Anyone who can see the park may download it.
    /// </summary>
    [HttpGet("parks/{parkId:guid}/settlements/invoice.pdf")]
    public async Task<IActionResult> MonthlyInvoice(Guid parkId, [FromQuery] string? month, CancellationToken ct)
    {
        if (!CanAccessPark(parkId)) return Forbid();
        if (!SettlementDtos.TryParseMonth(month, out var first))
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

    /// <summary>Retry a Failed park transfer now (Swich only). Same amount, same document id — never a second payment.</summary>
    [HttpPost("settlements/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        try
        {
            var s = await _settlements.RetryAsync(id, ActorLabel, ct);
            var dto = await RowAsync(s, ct);
            return s.Status == SettlementStatus.Completed ? Ok(dto) : UnprocessableEntity(dto);
        }
        catch (SettlementRejectedException ex)
        {
            // A transfer the park's revoked authorisation stopped (Annex 3 §7): never collected from its account.
            return Conflict(new { error = ex.Code, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "retry_rejected", message = ex.Message });
        }
    }

    /// <summary>
    /// Record that a FAILED park transfer was paid outside the platform (Swich only) — §2.5: when a park's account
    /// cannot cover its part the licensee is liable for it and may pay Swich directly; or the park paid by hand.
    /// Body: payer ("licensee" | "park"), amount (must be the transfer's full amount), paidOn (YYYY-MM-DD, the day
    /// the money reached Swich), reference (the payment's bank reference), note. The row is closed for good — never
    /// retried, so the park's account is never debited for it once topped up; the payment is booked in the ledger
    /// and the §4.1.2 overdue count stops at paidOn. Refused while the park's bank may still execute the transfer.
    /// </summary>
    [HttpPost("settlements/{id:guid}/settle-externally")]
    public async Task<IActionResult> SettleExternally(Guid id, [FromBody] SettleExternallyBody body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });
        if (body.Amount is not { } amount) return BadRequest(new { error = "amount_required", message = "amount is required" });
        if (!DateOnly.TryParseExact(body.PaidOn?.Trim() ?? "", "yyyy-MM-dd", out var paidOn))
            return BadRequest(new { error = SettlementRejectionCodes.InvalidPaidOn, message = "paidOn must be YYYY-MM-DD" });
        try
        {
            var s = await _settlements.SettleExternallyAsync(id, new ExternalPayment(body.Payer, amount, paidOn, body.Reference, body.Note), ActorLabel, ct);
            return Ok(await RowAsync(s, ct));
        }
        catch (SettlementRejectedException ex)
        {
            return ex.Code switch
            {
                SettlementRejectionCodes.SettlementNotFound => NotFound(new { error = ex.Code, message = ex.Message }),
                SettlementRejectionCodes.InvalidPayer or SettlementRejectionCodes.InvalidPaidOn
                    or SettlementRejectionCodes.ReferenceRequired or SettlementRejectionCodes.NoteTooLong
                    => BadRequest(new { error = ex.Code, message = ex.Message }),
                _ => Conflict(new { error = ex.Code, message = ex.Message }),
            };
        }
    }

    private async Task<object> RowAsync(Settlement s, CancellationToken ct)
    {
        var parkName = await _db.Parks.AsNoTracking().Where(p => p.Id == s.ParkId).Select(p => p.Name).FirstAsync(ct);
        var attempts = await AttemptsAsync(new[] { s }, ct);
        return SettlementDtos.Row(s, parkName, _opts, TodayLocal, Tz, attempts.GetValueOrDefault(s.Id), SeesContract);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Licensee statements (Swich + operator)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One month as the console shows it: each licensee's statement — the stored one when the month is
    /// settled, else a live preview (any month, including the running one) — and, for Swich, the
    /// Swich-direct parks' monthly rows. Default month: the current one.
    /// </summary>
    [HttpGet("settlements/month")]
    public async Task<IActionResult> MonthView([FromQuery] string? month, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var m = CurrentMonth;
        if (!string.IsNullOrWhiteSpace(month) && !SettlementDtos.TryParseMonth(month, out m))
            return BadRequest(new { error = "invalid_month", message = "month must be YYYY-MM" });

        var scope = await LicenseeScopeAsync(_db, ct);
        var licensees = await _db.Licensees.AsNoTracking()
            .Where(l => scope.Every || l.Id == scope.Only)
            .OrderByDescending(l => l.IsActive).ThenBy(l => l.Name)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var statements = new List<object>();
        foreach (var id in licensees) statements.Add(await BuildStatementAsync(id, m, ct));

        var direct = IsSuperAdmin ? await BuildDirectAsync(m, ct) : new List<object>();
        var (_, toUtc) = SettlementService.MonthBoundsUtcFor(m, _opts.TimeZoneId);

        return Ok(new
        {
            month = SettlementDtos.MonthKey(m),
            invoiceRef = SettlementService.InvoiceRefFor(m),
            isCurrent = m == CurrentMonth,
            isClosed = DateTime.UtcNow >= toUtc,
            dueDate = SettlementService.DueDate(m, _opts.DueDays),
            runDayOfMonth = Math.Clamp(_opts.RunDayOfMonth, 1, 28),
            allowOpenMonthRun = _opts.AllowOpenMonthRun,
            // A shared cost counts once this many hours have passed since its entry; whether an entry makes the next
            // scheduled statement is told against nextStatementRunAt.
            costCutoffHours = Math.Max(0, _opts.CostCutoffHours),
            nextStatementRunAt = SharedCostRules.NextStatementRunUtc(DateTime.UtcNow, _opts),
            statements,
            direct,
        });
    }

    /// <summary>Stored statements, newest month first.</summary>
    [HttpGet("settlements/statements")]
    public async Task<IActionResult> Statements([FromQuery] Guid? licenseeId, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var scope = await LicenseeScopeAsync(_db, ct);
        if (licenseeId is { } asked && !scope.Allows(asked)) return Forbid();
        var list = await _db.MonthlyStatements.AsNoTracking()
            .Where(m => licenseeId == null || m.LicenseeId == licenseeId)
            .Where(m => scope.Every || m.LicenseeId == scope.Only)
            .OrderByDescending(m => m.Month).ThenBy(m => m.LicenseeId)
            .Select(m => new
            {
                id = m.Id,
                licenseeId = m.LicenseeId,
                licenseeName = m.Licensee.Name,
                m.Month,
                invoiceRef = m.InvoiceRef,
                cashoutCount = m.CashoutCount,
                feeIncome = m.FeeIncome,
                licenseeCosts = m.LicenseeCosts,
                swichCosts = m.SwichCosts,
                carryIn = m.CarryIn,
                barrierBefore = m.BarrierBefore,
                phase = m.Phase.ToString(),
                entitlement = m.Entitlement,
                due = m.Due,
                carryOut = m.CarryOut,
                status = m.Status.ToString(),
                computedAt = m.ComputedAt,
                executedAt = m.ExecutedAt,
                parkCount = m.Settlements.Count(),
                transferred = m.Settlements.Where(s => s.Status == SettlementStatus.Completed).Sum(s => (decimal?)s.SwichShare) ?? 0m,
                paidExternally = m.Settlements.Where(s => s.Status == SettlementStatus.SettledExternally).Sum(s => (decimal?)s.SwichShare) ?? 0m,
                failedCount = m.Settlements.Count(s => s.Status == SettlementStatus.Failed),
            })
            .ToListAsync(ct);

        return Ok(new
        {
            count = list.Count,
            statements = list.Select(s => new
            {
                s.id, s.licenseeId, s.licenseeName,
                month = SettlementDtos.MonthKey(s.Month),
                s.invoiceRef, s.cashoutCount, s.feeIncome, s.licenseeCosts, s.swichCosts, s.carryIn, s.barrierBefore,
                s.phase, s.entitlement, s.due, s.carryOut, s.status, s.computedAt, s.executedAt, s.parkCount,
                s.transferred, s.paidExternally, outstanding = s.due - s.transferred - s.paidExternally, s.failedCount,
            }),
        });
    }

    /// <summary>One stored statement with its per-park rows and itemised costs.</summary>
    [HttpGet("settlements/statements/{id:guid}")]
    public async Task<IActionResult> Statement(Guid id, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var st = await _db.MonthlyStatements.AsNoTracking().Select(m => new { m.Id, m.LicenseeId, m.Month }).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (st is null) return NotFound(new { error = "statement_not_found" });
        if (!(await LicenseeScopeAsync(_db, ct)).Allows(st.LicenseeId)) return Forbid();
        return Ok(await BuildStatementAsync(st.LicenseeId, st.Month, ct));
    }

    /// <summary>
    /// The licensee's statement for any month computed live, without persisting anything (the stored
    /// statement when the month is already settled). Default licensee: the first one.
    /// </summary>
    [HttpGet("settlements/statements/preview")]
    public async Task<IActionResult> Preview([FromQuery] Guid? licenseeId, [FromQuery] string? month, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var m = CurrentMonth;
        if (!string.IsNullOrWhiteSpace(month) && !SettlementDtos.TryParseMonth(month, out m))
            return BadRequest(new { error = "invalid_month", message = "month must be YYYY-MM" });
        var scope = await LicenseeScopeAsync(_db, ct);
        if (licenseeId is { } asked && !scope.Allows(asked)) return Forbid();
        var id = licenseeId ?? await _db.Licensees.AsNoTracking().Where(l => scope.Every || l.Id == scope.Only)
            .OrderByDescending(l => l.IsActive).ThenBy(l => l.Name)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
        if (id is null || !await _db.Licensees.AsNoTracking().AnyAsync(l => l.Id == id, ct))
            return NotFound(new { error = "licensee_not_found" });
        return Ok(await BuildStatementAsync(id.Value, m, ct));
    }

    /// <summary>The licensee's detailed monthly report (§2.6) as PDF. Swich and the operator.</summary>
    [HttpGet("settlements/statements/{id:guid}/report.pdf")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var st = await _db.MonthlyStatements.AsNoTracking().Select(m => new { m.Id, m.Month, m.LicenseeId }).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (st is null) return NotFound(new { error = "statement_not_found" });
        if (!(await LicenseeScopeAsync(_db, ct)).Allows(st.LicenseeId)) return Forbid();
        var pdf = await _invoices.RenderLicenseeReportAsync(id, ct);
        _log.LogInformation("Licensee report {Month} downloaded by {Actor}", st.Month.ToString("yyyy-MM"), ActorLabel);
        return File(pdf, "application/pdf", _invoices.LicenseeReportFileName(st.Month.Year, st.Month.Month));
    }

    /// <summary>
    /// "Settle now" (Swich only): compute and execute a CLOSED month — by default the previous one —
    /// for every licensee and direct park, or only <paramref name="licenseeId"/> / <paramref name="parkId"/>.
    /// Idempotent: an already settled month is not recomputed; its unfinished transfers are executed.
    /// The nightly worker does the same on the run day; this is for doing it earlier or again.
    /// </summary>
    [HttpPost("settlements/run")]
    public async Task<IActionResult> Run([FromQuery] string? month, [FromQuery] Guid? licenseeId, [FromQuery] Guid? parkId, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var m = CurrentMonth.AddMonths(-1);
        if (!string.IsNullOrWhiteSpace(month) && !SettlementDtos.TryParseMonth(month, out m))
            return BadRequest(new { error = "invalid_month", message = "month must be YYYY-MM" });

        MonthRunResult result;
        try
        {
            result = await _settlements.RunMonthAsync(m, ActorLabel, licenseeId, parkId, ct);
        }
        catch (SettlementRejectedException ex)
        {
            return ex.Code is SettlementRejectionCodes.LicenseeNotFound or SettlementRejectionCodes.ParkNotFound
                ? NotFound(new { error = ex.Code, message = ex.Message })
                : Conflict(new { error = ex.Code, message = ex.Message });
        }

        _log.LogInformation("Settlement {Month} run by {Actor}: {Statements} statement(s), {Direct} direct row(s), {Skipped} skipped",
            SettlementDtos.MonthKey(m), ActorLabel, result.StatementIds.Count, result.DirectSettlementIds.Count, result.Skipped.Count);

        var statements = new List<object>();
        foreach (var sid in result.StatementIds)
        {
            var st = await _db.MonthlyStatements.AsNoTracking().Select(x => new { x.Id, x.LicenseeId, x.Month }).FirstAsync(x => x.Id == sid, ct);
            statements.Add(await BuildStatementAsync(st.LicenseeId, st.Month, ct));
        }
        var directRows = await _db.Settlements.AsNoTracking().Include(s => s.Park)
            .Where(s => result.DirectSettlementIds.Contains(s.Id)).ToListAsync(ct);
        var today = TodayLocal;
        var tz = Tz;
        var attempts = await AttemptsAsync(directRows, ct);

        return Ok(new
        {
            month = SettlementDtos.MonthKey(m),
            statements,
            direct = directRows.Select(s => SettlementDtos.Row(s, s.Park.Name, _opts, today, tz, attempts.GetValueOrDefault(s.Id), SeesContract)),
            skipped = result.Skipped.Select(x => new { kind = x.Kind, id = x.Id, name = x.Name, code = x.Code }),
        });
    }

    // ── View builders ────────────────────────────────────────────────

    private async Task<object> BuildStatementAsync(Guid licenseeId, DateOnly month, CancellationToken ct)
    {
        var p = await _settlements.PreviewLicenseeMonthAsync(licenseeId, month, ct);
        var licensee = await _db.Licensees.AsNoTracking().FirstAsync(l => l.Id == licenseeId, ct);

        MonthlyStatement? stored = p.StatementId is { } sid
            ? await _db.MonthlyStatements.AsNoTracking().FirstAsync(m => m.Id == sid, ct)
            : null;
        var rows = stored is null
            ? new List<Settlement>()
            : await _db.Settlements.AsNoTracking().Where(s => s.MonthlyStatementId == stored.Id).ToListAsync(ct);
        var costs = stored is null
            ? await _db.CommonCosts.AsNoTracking().Include(c => c.Statement).Where(c => p.CostIds.Contains(c.Id)).ToListAsync(ct)
            : await _db.CommonCosts.AsNoTracking().Include(c => c.Statement).Where(c => c.StatementId == stored.Id).ToListAsync(ct);
        // Not counted here, though dated in (or before) the month: for a settled month, invoices entered after it — they go
        // into the next statement; for a preview, those the computation leaves out now — on hold, or still inside their
        // 48-hour window. Shown with the month they belong to, so whoever entered one sees where it goes, and why.
        var monthLast = month.AddMonths(1).AddDays(-1);
        var counted = costs.Select(c => c.Id).ToHashSet();
        var lateCosts = (await _db.CommonCosts.AsNoTracking().Include(c => c.Statement)
                .Where(c => c.LicenseeId == licenseeId && c.StatementId == null && c.DeletedAt == null && c.InvoiceDate <= monthLast)
                .ToListAsync(ct))
            .Where(c => !counted.Contains(c.Id))
            .ToList();
        // Deleted entries of the month stay visible, struck through with who deleted them and why.
        var deletedCosts = await _db.CommonCosts.AsNoTracking().Include(c => c.Statement)
            .Where(c => c.LicenseeId == licenseeId && c.DeletedAt != null && c.InvoiceDate >= month && c.InvoiceDate <= monthLast)
            .ToListAsync(ct);
        // Every disputed entry, whatever its month — both sides see the holds prominently until they are settled.
        var heldCosts = await _db.CommonCosts.AsNoTracking().Include(c => c.Statement)
            .Where(c => c.LicenseeId == licenseeId && c.HeldAt != null && c.DeletedAt == null && c.StatementId == null)
            .ToListAsync(ct);
        var viewer = await SettlementDtos.CostViewerAsync(_db, IsSuperAdmin, IsOperator, _opts,
            costs.Concat(lateCosts).Concat(deletedCosts).Concat(heldCosts), ct);

        var today = TodayLocal;
        var tz = Tz;
        var attempts = await AttemptsAsync(rows, ct);
        var (_, toUtc) = SettlementService.MonthBoundsUtcFor(month, _opts.TimeZoneId);
        var transferred = rows.Where(r => r.Status == SettlementStatus.Completed).Sum(r => r.SwichShare);
        var paidExternally = rows.Where(r => r.Status == SettlementStatus.SettledExternally).Sum(r => r.SwichShare);
        var barrierAfter = p.BarrierBefore + p.Entitlement;
        // §2.3: once reached, the barrier stays reached — a later loss month lowers R but does not re-open it.
        var barrierFilled = p.BarrierFilledBefore || barrierAfter >= p.BarrierGel;

        return new
        {
            isPreview = stored is null,
            statementId = stored?.Id,
            licenseeId,
            licenseeName = licensee.Name,
            launchDate = licensee.LaunchDate,
            month = SettlementDtos.MonthKey(month),
            invoiceRef = SettlementService.InvoiceRefFor(month),
            periodFromUtc = p.PeriodFromUtc,
            periodToUtc = p.PeriodToUtc,
            isClosed = DateTime.UtcNow >= toUtc,
            dueDate = SettlementService.DueDate(month, _opts.DueDays),
            cashoutCount = p.CashoutCount,
            feeIncome = p.FeeIncome,
            licenseeCosts = p.LicenseeCosts,
            swichCosts = p.SwichCosts,
            carryIn = p.CarryIn,
            barrierGel = p.BarrierGel,
            swichSharePercent = p.SwichSharePercent,
            barrierBefore = p.BarrierBefore,
            barrierFilledBefore = p.BarrierFilledBefore,
            barrierAfter,
            barrierFilled,
            barrierRemaining = barrierFilled ? 0m : Math.Max(0m, p.BarrierGel - barrierAfter),
            phase = p.Phase.ToString(),
            barrierFraction = p.BarrierFraction,
            barrierEntitlement = p.BarrierEntitlement,
            normalEntitlement = p.NormalEntitlement,
            entitlement = p.Entitlement,
            due = p.Due,
            carryOut = p.CarryOut,
            status = stored?.Status.ToString(),
            computedAt = stored?.ComputedAt,
            executedAt = stored?.ExecutedAt,
            initiatedBy = stored?.InitiatedBy,
            transferred,
            paidExternally,
            outstanding = p.Due - transferred - paidExternally,
            blockedReason = p.BlockedReason,
            parks = p.Parks.Select(a =>
            {
                var row = rows.FirstOrDefault(r => r.ParkId == a.ParkId);
                return new
                {
                    settlementId = row?.Id,
                    parkId = a.ParkId,
                    parkName = a.ParkName,
                    cashoutCount = a.CashoutCount,
                    fees = a.Fees,
                    feeSharePercent = p.FeeIncome > 0 ? Math.Round(a.Fees / p.FeeIncome * 100m, 2, MidpointRounding.AwayFromZero) : 0m,
                    barrierFees = a.BarrierFees,
                    swichShare = a.SwichShare,
                    status = row?.Status.ToString(),
                    bankTransferId = row?.BankTransferId,
                    failureReason = row?.FailureReason,
                    failureCode = SettlementMath.FailureCode(row?.FailureReason),
                    attemptCount = row?.AttemptCount ?? 0,
                    lastAttemptAt = row?.LastAttemptAt,
                    completedAt = row?.CompletedAt,
                    description = row?.Description,
                    overdue = row is null ? null : SettlementDtos.Overdue(row, attempts.GetValueOrDefault(row.Id), _opts, today, tz, SeesContract),
                    external = row is null ? null : SettlementDtos.External(row, SeesContract),
                };
            }),
            costs = costs
                .OrderBy(c => c.PaidBy).ThenBy(c => c.InvoiceDate).ThenBy(c => c.CreatedAt)
                .Select(c => SettlementDtos.Cost(c, viewer)),
            lateCosts = lateCosts
                .OrderBy(c => c.PaidBy).ThenBy(c => c.InvoiceDate).ThenBy(c => c.CreatedAt)
                .Select(c => SettlementDtos.Cost(c, viewer)),
            deletedCosts = deletedCosts
                .OrderBy(c => c.PaidBy).ThenBy(c => c.InvoiceDate).ThenBy(c => c.CreatedAt)
                .Select(c => SettlementDtos.Cost(c, viewer)),
            heldCosts = heldCosts
                .OrderBy(c => c.HeldAt)
                .Select(c => SettlementDtos.Cost(c, viewer)),
        };
    }

    /// <summary>
    /// Swich-direct rows for the month: the stored monthly row, or a live preview of what is unsettled. A park that is
    /// direct now, and any other park with a direct row for the month or an unsettled fee it earned as a direct park
    /// (it joined a licensee since — that fee is still settled on its own row, §2.2).
    /// </summary>
    private async Task<List<object>> BuildDirectAsync(DateOnly month, CancellationToken ct)
    {
        var monthLast = month.AddMonths(1).AddDays(-1);
        var parks = await _db.Parks.AsNoTracking()
            .Where(p => p.LicenseeId == null
                     || p.Settlements.Any(s => s.IsMonthly && s.SettlementDate == monthLast && s.MonthlyStatementId == null)
                     || p.Cashouts.Any(c => c.Status == CashoutStatus.Completed && c.SettlementId == null
                                         && c.FeeRegimeStamped && c.FeeLicenseeId == null))
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        var today = TodayLocal;
        var tz = Tz;
        var list = new List<object>();

        foreach (var park in parks)
        {
            var row = await _db.Settlements.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ParkId == park.Id && s.IsMonthly && s.SettlementDate == monthLast && s.MonthlyStatementId == null, ct);
            var attempts = row is null ? null : await AttemptsAsync(new[] { row }, ct);
            var preview = row is null ? await _settlements.PreviewDirectParkMonthAsync(park.Id, month, ct) : null;
            if (row is null && preview is null) continue;

            list.Add(new
            {
                isPreview = row is null,
                settlementId = row?.Id,
                parkId = park.Id,
                parkName = park.Name,
                month = SettlementDtos.MonthKey(month),
                swichSharePercent = park.SwichSharePercent,
                phase1SharePercent = park.Phase1SharePercent,
                phase1CapGel = park.Phase1CapGel,
                cashoutCount = row?.CashoutCount ?? preview!.CashoutCount,
                feeTotal = row?.FeeTotal ?? preview!.FeeTotal,
                phase1Fees = row?.Phase1Fees ?? preview!.Phase1Fees,
                phase2Fees = row?.Phase2Fees ?? preview!.Phase2Fees,
                swichShare = row?.SwichShare ?? preview!.SwichShare,
                cumulativeFeesBefore = row?.CumulativeFeesBefore ?? preview!.CumulativeFeesBefore,
                status = row?.Status.ToString(),
                bankTransferId = row?.BankTransferId,
                failureReason = row?.FailureReason,
                failureCode = SettlementMath.FailureCode(row?.FailureReason),
                completedAt = row?.CompletedAt,
                // Outside the licence agreement (§2.2): late, but never a §4.1.2 penalty.
                overdue = row is null ? null : SettlementDtos.Overdue(row, attempts!.GetValueOrDefault(row.Id), _opts, today, tz, SeesContract),
                external = row is null ? null : SettlementDtos.External(row, SeesContract),
                blockedReason = preview?.BlockedReason,
            });
        }
        return list;
    }
}

public record SettleExternallyBody(
    // "licensee" (§2.5 — the licensee paid for the park) | "park" (the park paid by hand).
    string? Payer,
    // Must equal the transfer's amount: a debt is closed whole or not at all.
    decimal? Amount,
    // "YYYY-MM-DD" — the day the money reached Swich.
    string? PaidOn,
    string? Reference,
    string? Note);
