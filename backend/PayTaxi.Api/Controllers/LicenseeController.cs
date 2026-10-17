using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// The licensee (exclusive operator) of the licence agreement, the shared costs of §2.4, which parks the
/// agreement governs — and the agreement's change history, which records every change to what a statement
/// is computed from (§2.6: both sides see identical data).
///
/// Roles:
///   - Swich (super_admin): edits the licensee (launch date — only before it passes and with a reason —, barrier,
///     share); enters, edits and deletes Swich-paid costs; holds licensee-paid costs it disputes; assigns parks.
///   - Operator (the licensee's staff): sees ITS licensee, every cost and the history; enters, edits and deletes
///     licensee-paid costs; holds Swich-paid costs it disputes. Every endpoint is scoped to the licensee the operator
///     account works for (AdminUser.LicenseeId, <see cref="AdminControllerBase.LicenseeScopeAsync"/>): another
///     licensee's is 403 (and absent from the list). Its parks are that licensee's, while it is in force
///     (<see cref="AdminControllerBase.ParkFilter"/>). A second licensee in force is still refused (409
///     second_active_licensee): the contract calendar and the incident metrics assume one.
///   - Park admin: none of it — a park sees its own settlement, never the licensee's costs.
///
/// Neither side changes the other's entries — not even Swich (§2.4: each side records the invoices it paid). A
/// deletion is soft and needs a reason. A cost a statement has included is settled money: immutable (409
/// cost_included); its owner corrects it with a linked correction the next statement counts. Every change is
/// conditional on "not yet included" (and not deleted) in the database itself, so one racing the statement that
/// freezes the cost is refused rather than changing a frozen row — and each is written in one transaction with its
/// <see cref="AgreementEvent"/>, so the history never misses a change nor records one that did not happen.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin")]
public class LicenseeController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly SettlementOptions _opts;
    private readonly ILogger<LicenseeController> _log;

    public LicenseeController(AppDbContext db, IOptions<SettlementOptions> opts, ILogger<LicenseeController> log)
    {
        _db = db;
        _opts = opts.Value;
        _log = log;
    }

    private const decimal MaxCostAmount = 1_000_000m;
    private const int MaxReasonLength = 500;

    private DateOnly TodayLocal => SettlementService.TodayLocal(_opts.TimeZoneId);

    /// <summary>The caller may see and change this licensee's agreement figures (Swich: any; an operator: its own).</summary>
    private async Task<bool> MayAccessAsync(Guid licenseeId, CancellationToken ct) =>
        (await LicenseeScopeAsync(_db, ct)).Allows(licenseeId);

    /// <summary>
    /// Only ONE licensee may be in force. Operators are scoped to their own licensee's parks now (AdminTokenCheck), so a
    /// second licensee's staff would not see the first one's parks; the rule stays because the contract calendar (the
    /// launch date, the §7 trial) and the incident metrics still assume a single licensee (the database's
    /// IX_Licensees_SingleActive enforces it too, against two requests at once).
    /// </summary>
    private ConflictObjectResult SecondActiveLicensee() => Conflict(new
    {
        error = "second_active_licensee",
        message = "Another licensee is in force. Only one licensee can be in force at a time — deactivate the other first.",
    });

    private static bool IsSecondActive(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Licensees_SingleActive" };

    // ═══════════════════════════════════════════════════════════════════
    // Licensee
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The licensee(s) with their parks and barrier progress. Swich: all; the operator: its own.</summary>
    [HttpGet("licensees")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var scope = await LicenseeScopeAsync(_db, ct);
        var ids = await _db.Licensees.AsNoTracking()
            .Where(l => scope.Every || l.Id == scope.Only)
            .OrderByDescending(l => l.IsActive).ThenBy(l => l.Name)
            .Select(l => l.Id).ToListAsync(ct);
        var list = new List<object>();
        foreach (var id in ids) list.Add(await LicenseeDtoAsync(id, ct));
        return Ok(new { count = list.Count, licensees = list });
    }

    [HttpGet("licensees/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        if (!await _db.Licensees.AsNoTracking().AnyAsync(l => l.Id == id, ct)) return NotFound(new { error = "licensee_not_found" });
        return Ok(await LicenseeDtoAsync(id, ct));
    }

    /// <summary>Register the licensee (Swich only) — production starts without one.</summary>
    [HttpPost("licensees")]
    public async Task<IActionResult> Create([FromBody] LicenseeBody body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        if (body is null || string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = "name_required" });
        var l = new Licensee { Name = body.Name.Trim() };
        var events = new List<AgreementEvent>();
        var error = await ApplyAsync(l, body, isNew: true, events, ct);
        if (error is not null) return error;
        if (l.IsActive && await _db.Licensees.AsNoTracking().AnyAsync(x => x.IsActive, ct)) return SecondActiveLicensee();
        _db.Licensees.Add(l);
        _db.AgreementEvents.AddRange(events);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsSecondActive(ex)) { return SecondActiveLicensee(); }
        _log.LogInformation("Licensee {Name} created by {Actor}", l.Name, ActorLabel);
        return Ok(await LicenseeDtoAsync(l.Id, ct));
    }

    /// <summary>
    /// Edit the licensee (Swich only). Every field optional; an empty string clears a text field or the
    /// launch date. Barrier and share changes apply to statements computed from now on — computed ones
    /// keep the values they were computed with. A launch date moves only while it has not passed, with a
    /// reason; each change of the launch date or the terms is recorded in the agreement's history.
    /// </summary>
    [HttpPatch("licensees/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LicenseeBody body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        // The row is locked for the edit, so two edits at once cannot both record the same "before" value.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Licensees\" WHERE \"Id\" = {id} FOR UPDATE", ct);
        var l = await _db.Licensees.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return NotFound(new { error = "licensee_not_found" });
        var wasActive = l.IsActive;
        var events = new List<AgreementEvent>();
        var error = await ApplyAsync(l, body, isNew: false, events, ct);
        if (error is not null) return error;
        if (l.IsActive && !wasActive && await _db.Licensees.AsNoTracking().AnyAsync(x => x.IsActive && x.Id != l.Id, ct))
            return SecondActiveLicensee();
        _db.AgreementEvents.AddRange(events);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsSecondActive(ex)) { return SecondActiveLicensee(); }
        await tx.CommitAsync(ct);
        if (l.IsActive != wasActive)
        {
            // Its operators work only while it is in force (AdminTokenCheck): drop their cached account check, so being cut
            // off — or let back in — applies from the next request, not when the cache runs out.
            var operators = await _db.AdminUsers.AsNoTracking().Where(a => a.LicenseeId == l.Id).Select(a => a.Id).ToListAsync(ct);
            Security.AdminTokenCheck.Forget(HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), operators);
            _log.LogWarning("Licensee {Name} {State} by {Actor}: its {Count} operator account(s) {Effect}", l.Name,
                l.IsActive ? "put in force" : "deactivated", ActorLabel, operators.Count, l.IsActive ? "work again" : "are cut off");
        }
        _log.LogInformation("Licensee {Name} updated by {Actor} (launch {Launch}, barrier {Barrier}, share {Share}%)",
            l.Name, ActorLabel, l.LaunchDate, l.BarrierGel, l.SwichSharePercent);
        return Ok(await LicenseeDtoAsync(l.Id, ct));
    }

    private async Task<IActionResult?> ApplyAsync(Licensee l, LicenseeBody b, bool isNew, List<AgreementEvent> events, CancellationToken ct)
    {
        var reason = Blank(b.Reason);
        if (reason is { Length: > MaxReasonLength }) return BadRequest(new { error = "reason_too_long" });
        var termsBefore = isNew ? null : SharedCostRules.TermsSnapshot(l);

        if (b.Name is not null)
        {
            var name = b.Name.Trim();
            if (name.Length is 0 or > 200) return BadRequest(new { error = "invalid_name" });
            l.Name = name;
        }
        if (b.TaxId is not null)
        {
            var tax = Blank(b.TaxId);
            if (tax is not null)
            {
                var digits = new string(tax.Where(char.IsDigit).ToArray());
                if (digits.Length is < 9 or > 11) return BadRequest(new { error = "invalid_tax_id" });
                tax = digits;
            }
            l.TaxId = tax;
        }
        if (b.Email is not null)
        {
            var email = Blank(b.Email);
            if (email is not null && (email.Length > 200 || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
                return BadRequest(new { error = "invalid_email" });
            l.Email = email;
        }
        if (b.Iban is not null)
        {
            var iban = Blank(b.Iban)?.Replace(" ", "").ToUpperInvariant();
            if (iban is not null && !Regex.IsMatch(iban, "^[A-Z]{2}[0-9A-Z]{13,32}$"))
                return BadRequest(new { error = "invalid_iban" });
            l.Iban = iban;
        }
        if (b.LaunchDate is not null)
        {
            DateOnly? launch = null;
            var today = TodayLocal;
            if (!string.IsNullOrWhiteSpace(b.LaunchDate))
            {
                if (!DateOnly.TryParseExact(b.LaunchDate.Trim(), "yyyy-MM-dd", out var d) || d.Year < 2020 || d > today.AddYears(1))
                    return BadRequest(new { error = "invalid_launch_date" });
                launch = d;
            }
            if (launch != l.LaunchDate)
            {
                // The launch date starts the §7 trial and the barrier, and the two sides confirm it by e-mail. Once its
                // day has passed, moving it would move the trial window over incidents already known — locked. Before
                // that it moves only to a day still ahead, with a reason, and on the record.
                if (l.LaunchDate is { } current && current < today)
                    return Conflict(new { error = "launch_date_locked", launchDate = current });
                if (launch is { } next && next < today)
                    return BadRequest(new { error = "launch_date_in_past" });
                if (reason is null) return BadRequest(new { error = "launch_reason_required" });

                // Statements already computed count from the launch month on. Moving the launch past the
                // first of them (or clearing it) would orphan settled months from the barrier — refuse.
                if (!isNew)
                {
                    var first = await _db.MonthlyStatements.AsNoTracking().Where(m => m.LicenseeId == l.Id)
                        .OrderBy(m => m.Month).Select(m => (DateOnly?)m.Month).FirstOrDefaultAsync(ct);
                    if (first is { } f && (launch is null || SettlementService.FirstOfMonth(launch.Value) > f))
                        return Conflict(new { error = "launch_after_first_statement", firstStatement = SettlementService.InvoiceRefFor(f) });
                }
                events.Add(SharedCostRules.Event(l.Id, ActorLabel, AgreementEventKinds.LaunchDate,
                    oldValue: SharedCostRules.LaunchSnapshot(l.LaunchDate), newValue: SharedCostRules.LaunchSnapshot(launch), reason: reason));
                l.LaunchDate = launch;
            }
        }
        if (b.BarrierGel is { } barrier)
        {
            if (barrier < 0 || barrier > 10_000_000m || decimal.Round(barrier, 2) != barrier) return BadRequest(new { error = "invalid_barrier" });
            l.BarrierGel = barrier;
        }
        if (b.SwichSharePercent is { } share)
        {
            if (share < 0 || share > 100 || decimal.Round(share, 2) != share) return BadRequest(new { error = "invalid_share" });
            l.SwichSharePercent = share;
        }
        if (b.IsActive is { } active) l.IsActive = active;

        // The money terms (and whether the agreement is in force) are on the record too: registered with, and every change.
        var termsAfter = SharedCostRules.TermsSnapshot(l);
        if (termsAfter != termsBefore)
            events.Add(SharedCostRules.Event(l.Id, ActorLabel, AgreementEventKinds.Terms, oldValue: termsBefore, newValue: termsAfter, reason: reason));
        return null;
    }

    private async Task<object> LicenseeDtoAsync(Guid id, CancellationToken ct)
    {
        var l = await _db.Licensees.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var parks = await _db.Parks.AsNoTracking().Where(p => p.LicenseeId == id).OrderBy(p => p.Name)
            .Select(p => new { id = p.Id, name = p.Name, slug = p.Slug, status = p.Status.ToString() })
            .ToListAsync(ct);
        var statements = await _db.MonthlyStatements.AsNoTracking().Where(m => m.LicenseeId == id)
            .OrderBy(m => m.Month)
            .Select(m => new { m.Month, m.Entitlement, m.InvoiceRef, m.CarryOut, m.Phase, m.BarrierFilledBefore }).ToListAsync(ct);
        var sinceLaunch = SettlementService.SinceLaunch(
            statements.Select(m => new SettlementService.BarrierStep(m.Month, m.Entitlement, m.Phase, m.BarrierFilledBefore)), l.LaunchDate);
        var entitled = sinceLaunch.Sum(m => m.Entitlement);
        // §2.3: once reached, the barrier stays reached — a later loss month lowers the total, it does not re-open it.
        var filled = SettlementService.BarrierReached(sinceLaunch, l.BarrierGel);
        var received = await _db.Settlements.AsNoTracking()
            .Where(s => s.MonthlyStatement != null && s.MonthlyStatement.LicenseeId == id
                     && (s.Status == SettlementStatus.Completed || s.Status == SettlementStatus.SettledExternally))
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Amount = g.Sum(s => s.SwichShare) })
            .ToListAsync(ct);
        var transferred = received.Where(r => r.Status == SettlementStatus.Completed).Sum(r => r.Amount);
        var paidExternally = received.Where(r => r.Status == SettlementStatus.SettledExternally).Sum(r => r.Amount);
        var last = statements.LastOrDefault();
        var launchEvents = await _db.AgreementEvents.AsNoTracking()
            .Where(e => e.LicenseeId == id && e.Kind == AgreementEventKinds.LaunchDate)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .ToListAsync(ct);

        return new
        {
            id = l.Id,
            name = l.Name,
            taxId = l.TaxId,
            email = l.Email,
            iban = l.Iban,
            launchDate = l.LaunchDate,
            // From the day after the launch date it can no longer move (the trial and the barrier count from it).
            launchDateLocked = l.LaunchDate is { } ld && ld < TodayLocal,
            launchDateHistory = launchEvents.Select(e => new
            {
                id = e.Id,
                at = e.At,
                actor = e.Actor,
                from = LaunchOf(e.OldValue),
                to = LaunchOf(e.NewValue),
                reason = e.Reason,
            }),
            barrierGel = l.BarrierGel,
            swichSharePercent = l.SwichSharePercent,
            isActive = l.IsActive,
            createdAt = l.CreatedAt,
            updatedAt = l.UpdatedAt,
            parks,
            statementCount = statements.Count,
            lastStatement = last?.InvoiceRef,
            carryOut = last?.CarryOut ?? 0m,
            barrier = new
            {
                entitled,
                transferred,
                // Debts the licensee (or a park) paid Swich outside the platform (§2.5).
                paidExternally,
                remaining = filled ? 0m : Math.Max(0m, l.BarrierGel - entitled),
                filled,
            },
        };
    }

    private static string? LaunchOf(string? snapshot) =>
        Json(snapshot) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("launchDate", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() : null;

    // ═══════════════════════════════════════════════════════════════════
    // Change history
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The agreement's change history, newest first — the same for Swich and the operator (§2.6). <c>kind</c> filters
    /// by one or more kinds (comma-separated). Read-only: no endpoint changes or removes an entry.
    /// </summary>
    [HttpGet("licensees/{id:guid}/events")]
    public async Task<IActionResult> Events(Guid id, [FromQuery] string? kind, [FromQuery] int take = 200, CancellationToken ct = default)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        if (!await _db.Licensees.AsNoTracking().AnyAsync(l => l.Id == id, ct)) return NotFound(new { error = "licensee_not_found" });
        var kinds = (kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (kinds.Any(k => !AgreementEventKinds.All.Contains(k))) return BadRequest(new { error = "invalid_kind", kinds = AgreementEventKinds.All });
        take = Math.Clamp(take, 1, 1000);

        var q = _db.AgreementEvents.AsNoTracking().Where(e => e.LicenseeId == id);
        if (kinds.Count > 0) q = q.Where(e => kinds.Contains(e.Kind));
        var events = await q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(take).ToListAsync(ct);

        var refIds = events.Where(e => e.ReferenceId != null).Select(e => e.ReferenceId!.Value).Distinct().ToList();
        var costs = await _db.CommonCosts.AsNoTracking().Where(c => refIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Vendor, c.InvoiceNumber, c.PaidBy, c.CorrectsId })
            .ToDictionaryAsync(c => c.Id, ct);

        return Ok(new
        {
            count = events.Count,
            kinds = AgreementEventKinds.All,
            events = events.Select(e => new
            {
                id = e.Id,
                at = e.At,
                actor = e.Actor,
                kind = e.Kind,
                referenceId = e.ReferenceId,
                old = Json(e.OldValue),
                @new = Json(e.NewValue),
                reason = e.Reason,
                cost = e.ReferenceId is { } rid && costs.TryGetValue(rid, out var c)
                    ? new { id = c.Id, vendor = c.Vendor, invoiceNumber = c.InvoiceNumber, paidBy = c.PaidBy.ToString(), isCorrection = c.CorrectsId != null }
                    : null,
            }),
        });
    }

    /// <summary>A recorded snapshot as JSON for the console (null when there was none).</summary>
    private static JsonElement? Json(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;
        try { return JsonDocument.Parse(snapshot).RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(snapshot); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Park assignment
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Put a park under the licensee's agreement, or make it Swich-direct (licenseeId null). Swich only.
    /// Applies from now on: every fee is settled under the arrangement in force when it was EARNED (stamped on the
    /// cashout when it completes, §2.2), so fees the park already earned stay with the earlier arrangement — also
    /// those not settled yet; a park moved mid-month has a row on each side for that month. Only cashouts completed
    /// before regimes were stamped follow the park's assignment at settlement time. The response says how much of
    /// each is still unsettled, so the console can tell Swich.
    /// </summary>
    [HttpPut("parks/{parkId:guid}/licensee")]
    public async Task<IActionResult> AssignPark(Guid parkId, [FromBody] AssignParkBody body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var park = await _db.Parks.FirstOrDefaultAsync(p => p.Id == parkId, ct);
        if (park is null) return NotFound(new { error = "park_not_found" });
        string? licenseeName = null;
        if (body?.LicenseeId is { } lid)
        {
            licenseeName = await _db.Licensees.AsNoTracking().Where(l => l.Id == lid).Select(l => l.Name).FirstOrDefaultAsync(ct);
            if (licenseeName is null) return NotFound(new { error = "licensee_not_found" });
        }
        var before = park.LicenseeId;
        park.LicenseeId = body?.LicenseeId;
        await _db.SaveChangesAsync(ct);

        var unsettled = await _db.Cashouts.AsNoTracking()
            .Where(c => c.ParkId == parkId && c.Status == CashoutStatus.Completed && c.SettlementId == null)
            .GroupBy(c => c.FeeRegimeStamped)
            .Select(g => new { Stamped = g.Key, Fees = g.Sum(c => c.Fee) })
            .ToListAsync(ct);
        var earnedBefore = unsettled.Where(u => u.Stamped).Sum(u => u.Fees);
        var unstamped = unsettled.Where(u => !u.Stamped).Sum(u => u.Fees);
        _log.LogInformation("Park {Park} settlement assignment {Before} → {After} by {Actor} ({Earned} GEL of unsettled fees stay where they were earned, {Unstamped} GEL unstamped follow the new assignment)",
            park.Name, before?.ToString() ?? "direct", park.LicenseeId?.ToString() ?? "direct", ActorLabel, earnedBefore, unstamped);

        return Ok(new
        {
            parkId = park.Id,
            parkName = park.Name,
            licenseeId = park.LicenseeId,
            licenseeName,
            changed = before != park.LicenseeId,
            unsettledFees = earnedBefore + unstamped,
            // Earned under an earlier arrangement and not settled yet: stays with it (§2.2).
            unsettledEarnedBefore = earnedBefore,
            // From before regimes were stamped: follows the assignment at settlement time — now this one.
            unsettledUnstamped = unstamped,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Shared costs (§2.4)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The side the caller enters costs for: Swich for Swich, the licensee for the operator.</summary>
    private CostPaidBy? MySide => IsSuperAdmin ? CostPaidBy.Swich : IsOperator ? CostPaidBy.Licensee : null;

    private Task<SettlementDtos.CostViewer> ViewerAsync(IEnumerable<CommonCost> shown, CancellationToken ct) =>
        SettlementDtos.CostViewerAsync(_db, IsSuperAdmin, IsOperator, _opts, shown, ct);

    private async Task<object> CostDtoAsync(Guid costId, CancellationToken ct)
    {
        var c = await _db.CommonCosts.AsNoTracking().Include(x => x.Statement).FirstAsync(x => x.Id == costId, ct);
        return SettlementDtos.Cost(c, await ViewerAsync(new[] { c }, ct));
    }

    /// <summary>
    /// Costs of the licensee. With <c>month</c>: exactly the costs of that month's statement — the
    /// included ones when the month is settled, else those the statement would count if computed now
    /// (not yet included, invoice dated by the month's end, neither deleted nor held, past the 48-hour window).
    /// Without: every entry, deleted ones included, newest first.
    /// </summary>
    [HttpGet("licensees/{id:guid}/costs")]
    public async Task<IActionResult> ListCosts(Guid id, [FromQuery] string? month, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        if (!await _db.Licensees.AsNoTracking().AnyAsync(l => l.Id == id, ct)) return NotFound(new { error = "licensee_not_found" });

        IQueryable<CommonCost> q = _db.CommonCosts.AsNoTracking().Include(c => c.Statement).Where(c => c.LicenseeId == id);
        if (!string.IsNullOrWhiteSpace(month))
        {
            if (!SettlementDtos.TryParseMonth(month, out var m)) return BadRequest(new { error = "invalid_month" });
            var stmt = await _db.MonthlyStatements.AsNoTracking().Where(s => s.LicenseeId == id && s.Month == m)
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            var monthLast = m.AddMonths(1).AddDays(-1);
            var enteredBy = SharedCostRules.EnteredBy(DateTime.UtcNow, _opts.CostCutoffHours);
            q = stmt is { } sid
                ? q.Where(c => c.StatementId == sid)
                : q.Where(c => c.StatementId == null && c.InvoiceDate <= monthLast && c.DeletedAt == null && c.HeldAt == null && c.EnteredAt <= enteredBy);
        }
        var costs = await q.OrderByDescending(c => c.InvoiceDate).ThenByDescending(c => c.CreatedAt).Take(500).ToListAsync(ct);
        var viewer = await ViewerAsync(costs, ct);
        var live = costs.Where(c => c.DeletedAt == null).ToList();

        return Ok(new
        {
            count = costs.Count,
            licenseeCosts = live.Where(c => c.PaidBy == CostPaidBy.Licensee).Sum(c => c.Amount),
            swichCosts = live.Where(c => c.PaidBy == CostPaidBy.Swich).Sum(c => c.Amount),
            costs = costs.Select(c => SettlementDtos.Cost(c, viewer)),
        });
    }

    /// <summary>
    /// Add a shared-cost invoice. Who pays is fixed by who adds it: Swich adds the invoices addressed to
    /// Swich, the operator those addressed to the licensee (§2.4: each side pays its own as they come).
    /// A statement counts it from 48 hours after its entry — the other side's window to see it.
    /// </summary>
    [HttpPost("licensees/{id:guid}/costs")]
    public async Task<IActionResult> AddCost(Guid id, [FromBody] CostBody body, CancellationToken ct)
    {
        if (MySide is not { } side || !await MayAccessAsync(id, ct)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });
        if (!await _db.Licensees.AsNoTracking().AnyAsync(l => l.Id == id, ct)) return NotFound(new { error = "licensee_not_found" });

        var now = DateTime.UtcNow;
        var cost = new CommonCost
        {
            LicenseeId = id,
            PaidBy = side,
            CreatedBy = ActorLabel,
            EnteredAt = now,
        };
        var error = Apply(cost, body, requireAll: true);
        if (error is not null) return error;
        if (await IsDuplicateAsync(cost, ct)) return Conflict(new { error = "duplicate_invoice" });

        _db.CommonCosts.Add(cost);
        _db.AgreementEvents.Add(SharedCostRules.Event(id, ActorLabel, AgreementEventKinds.CostAdded, cost.Id,
            newValue: SharedCostRules.Snapshot(cost), at: now));
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Shared cost {Vendor} #{Invoice} {Amount} GEL ({PaidBy}) added by {Actor}",
            cost.Vendor, cost.InvoiceNumber, cost.Amount, cost.PaidBy, ActorLabel);
        return Ok(await CostDtoAsync(cost.Id, ct));
    }

    /// <summary>
    /// Correct a cost before a statement includes it — by its own side only (Swich its own, the operator the
    /// licensee's). Any change but to the note restarts its 48-hour window: the other side has to see the new figures.
    /// </summary>
    [HttpPatch("licensees/{id:guid}/costs/{costId:guid}")]
    public async Task<IActionResult> UpdateCost(Guid id, Guid costId, [FromBody] CostBody body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });
        var cost = await _db.CommonCosts.AsNoTracking().Include(c => c.Statement).FirstOrDefaultAsync(c => c.Id == costId && c.LicenseeId == id, ct);
        if (cost is null) return NotFound(new { error = "cost_not_found" });
        if (cost.DeletedAt is not null) return Conflict(new { error = "cost_deleted" });
        if (cost.StatementId is not null) return Conflict(new { error = "cost_included", includedIn = cost.Statement?.InvoiceRef });
        if (MySide != cost.PaidBy) return Forbid();

        var before = SharedCostRules.Snapshot(cost);
        var noteBefore = cost.Note;
        IActionResult? error;
        if (cost.CorrectsId is not null)
        {
            // A correction carries its original's category, vendor, invoice number and date — only its own figures change.
            if (body.Category is not null || body.Vendor is not null || body.InvoiceNumber is not null || body.InvoiceDate is not null)
                return BadRequest(new { error = "correction_fields_fixed" });
            error = ApplyCorrection(cost, body.Amount, body.Reason, body.Note, requireAll: false);
        }
        else
        {
            error = Apply(cost, body, requireAll: false);
            if (error is null && await IsDuplicateAsync(cost, ct)) return Conflict(new { error = "duplicate_invoice" });
        }
        if (error is not null) return error;

        var after = SharedCostRules.Snapshot(cost);
        if (after == before) return Ok(await CostDtoAsync(cost.Id, ct));
        // A note is a remark; anything else is the entry itself — the other side gets its full window on the new figures.
        var restart = SharedCostRules.Snapshot(WithNote(cost, noteBefore)) != before;
        var now = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        if (cost.CorrectsId is { } originalId && await CorrectionBreaksFloorAsync(originalId, cost.Amount, excluding: cost.Id, ct))
            return BadRequest(new { error = "correction_below_zero" });
        // Written only while no statement has included it — checked by the UPDATE itself, not by the read above:
        // a statement may have frozen the cost in between, and then the frozen row must not change.
        var enteredAt = restart ? now : cost.EnteredAt;
        var updated = await _db.CommonCosts
            .Where(c => c.Id == cost.Id && c.StatementId == null && c.DeletedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Category, cost.Category)
                .SetProperty(c => c.Vendor, cost.Vendor)
                .SetProperty(c => c.InvoiceNumber, cost.InvoiceNumber)
                .SetProperty(c => c.InvoiceDate, cost.InvoiceDate)
                .SetProperty(c => c.Amount, cost.Amount)
                .SetProperty(c => c.Note, cost.Note)
                .SetProperty(c => c.Reason, cost.Reason)
                .SetProperty(c => c.EnteredAt, enteredAt)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (updated == 0) return await ChangedMeanwhileAsync(cost.Id, ct);
        _db.AgreementEvents.Add(SharedCostRules.Event(id, ActorLabel, AgreementEventKinds.CostEdited, cost.Id,
            oldValue: before, newValue: after, at: now));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Shared cost {Id} edited by {Actor}{Restart}", cost.Id, ActorLabel, restart ? " (48-hour window restarted)" : "");
        return Ok(await CostDtoAsync(cost.Id, ct));
    }

    /// <summary>
    /// Delete a cost before a statement includes it — by its own side, with a reason. Soft: the row stays (struck
    /// through in the console, with who and why) and no statement ever counts it.
    /// </summary>
    [HttpDelete("licensees/{id:guid}/costs/{costId:guid}")]
    public async Task<IActionResult> DeleteCost(Guid id, Guid costId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReasonBody? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        var cost = await _db.CommonCosts.AsNoTracking().Include(c => c.Statement).FirstOrDefaultAsync(c => c.Id == costId && c.LicenseeId == id, ct);
        if (cost is null) return NotFound(new { error = "cost_not_found" });
        if (cost.DeletedAt is not null) return Conflict(new { error = "cost_deleted" });
        if (cost.StatementId is not null) return Conflict(new { error = "cost_included", includedIn = cost.Statement?.InvoiceRef });
        if (MySide != cost.PaidBy) return Forbid();
        var (reason, reasonError) = RequiredReason(body?.Reason);
        if (reasonError is not null) return reasonError;

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // Same rule as editing: the UPDATE itself requires "not yet included" (and not already deleted).
        var deleted = await _db.CommonCosts
            .Where(c => c.Id == cost.Id && c.StatementId == null && c.DeletedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.DeletedAt, (DateTime?)now)
                .SetProperty(c => c.DeletedBy, ActorLabel)
                .SetProperty(c => c.DeleteReason, reason)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (deleted == 0) return await ChangedMeanwhileAsync(cost.Id, ct);
        _db.AgreementEvents.Add(SharedCostRules.Event(id, ActorLabel, AgreementEventKinds.CostDeleted, cost.Id,
            oldValue: SharedCostRules.Snapshot(cost), reason: reason, at: now));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Shared cost {Vendor} #{Invoice} {Amount} GEL ({PaidBy}) deleted by {Actor}: {Reason}",
            cost.Vendor, cost.InvoiceNumber, cost.Amount, cost.PaidBy, ActorLabel, reason);
        return Ok(await CostDtoAsync(cost.Id, ct));
    }

    /// <summary>
    /// The OTHER side disputes a cost: on hold, no statement counts it until the side that held it releases it (or its
    /// owner deletes it). A reason is required — the owner needs to know what to fix.
    /// </summary>
    [HttpPost("licensees/{id:guid}/costs/{costId:guid}/hold")]
    public async Task<IActionResult> HoldCost(Guid id, Guid costId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReasonBody? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        var cost = await _db.CommonCosts.AsNoTracking().Include(c => c.Statement).FirstOrDefaultAsync(c => c.Id == costId && c.LicenseeId == id, ct);
        if (cost is null) return NotFound(new { error = "cost_not_found" });
        if (cost.DeletedAt is not null) return Conflict(new { error = "cost_deleted" });
        if (cost.StatementId is not null) return Conflict(new { error = "cost_included", includedIn = cost.Statement?.InvoiceRef });
        if (MySide is not { } side || side == cost.PaidBy) return Forbid();
        if (cost.HeldAt is not null) return Conflict(new { error = "cost_already_held" });
        var (reason, reasonError) = RequiredReason(body?.Reason);
        if (reasonError is not null) return reasonError;

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // Conditional like every change: a statement that froze the cost meanwhile keeps it (409), it is not held after the fact.
        var held = await _db.CommonCosts
            .Where(c => c.Id == cost.Id && c.StatementId == null && c.DeletedAt == null && c.HeldAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.HeldAt, (DateTime?)now)
                .SetProperty(c => c.HeldBy, ActorLabel)
                .SetProperty(c => c.HoldReason, reason)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (held == 0) return await ChangedMeanwhileAsync(cost.Id, ct);
        _db.AgreementEvents.Add(SharedCostRules.Event(id, ActorLabel, AgreementEventKinds.CostHeld, cost.Id,
            oldValue: SharedCostRules.Snapshot(cost), reason: reason, at: now));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Shared cost {Id} ({PaidBy}) put on hold by {Actor}: {Reason}", cost.Id, cost.PaidBy, ActorLabel, reason);
        return Ok(await CostDtoAsync(cost.Id, ct));
    }

    /// <summary>Release a hold — by the side that put it on (the other side from the cost's owner). The reason is optional.</summary>
    [HttpPost("licensees/{id:guid}/costs/{costId:guid}/release")]
    public async Task<IActionResult> ReleaseCost(Guid id, Guid costId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReasonBody? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        var cost = await _db.CommonCosts.AsNoTracking().Include(c => c.Statement).FirstOrDefaultAsync(c => c.Id == costId && c.LicenseeId == id, ct);
        if (cost is null) return NotFound(new { error = "cost_not_found" });
        if (cost.DeletedAt is not null) return Conflict(new { error = "cost_deleted" });
        if (cost.StatementId is not null) return Conflict(new { error = "cost_included", includedIn = cost.Statement?.InvoiceRef });
        if (MySide is not { } side || side == cost.PaidBy) return Forbid();
        if (cost.HeldAt is null) return Conflict(new { error = "cost_not_held" });
        var reason = Blank(body?.Reason);
        if (reason is { Length: > MaxReasonLength }) return BadRequest(new { error = "reason_too_long" });

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var released = await _db.CommonCosts
            .Where(c => c.Id == cost.Id && c.StatementId == null && c.DeletedAt == null && c.HeldAt != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.HeldAt, (DateTime?)null)
                .SetProperty(c => c.HeldBy, (string?)null)
                .SetProperty(c => c.HoldReason, (string?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
        if (released == 0) return await ChangedMeanwhileAsync(cost.Id, ct);
        // The record keeps what was released: when it was held, by whom and why.
        var hold = JsonSerializer.Serialize(new { heldAt = cost.HeldAt, heldBy = cost.HeldBy, holdReason = cost.HoldReason });
        _db.AgreementEvents.Add(SharedCostRules.Event(id, ActorLabel, AgreementEventKinds.CostReleased, cost.Id,
            oldValue: hold, reason: reason, at: now));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Shared cost {Id} ({PaidBy}) released by {Actor}", cost.Id, cost.PaidBy, ActorLabel);
        return Ok(await CostDtoAsync(cost.Id, ct));
    }

    /// <summary>
    /// Correct an already-included cost (its owner only): settled money never changes, so the difference is a new
    /// entry linked to the original — same side, category, vendor, invoice number and invoice date, the signed amount
    /// and a reason — which the next statement counts (after the usual 48-hour window). Positive or negative, never
    /// taking the original and its corrections below zero together. A correction of a correction corrects the original.
    /// </summary>
    [HttpPost("licensees/{id:guid}/costs/{costId:guid}/corrections")]
    public async Task<IActionResult> AddCorrection(Guid id, Guid costId, [FromBody] CorrectionBody body, CancellationToken ct)
    {
        if (!IsSwichOrOperator || !await MayAccessAsync(id, ct)) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });
        var cost = await _db.CommonCosts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == costId && c.LicenseeId == id, ct);
        if (cost is null) return NotFound(new { error = "cost_not_found" });
        var original = cost.CorrectsId is { } rootId
            ? await _db.CommonCosts.AsNoTracking().FirstAsync(c => c.Id == rootId, ct)
            : cost;
        if (MySide != original.PaidBy) return Forbid();
        if (original.DeletedAt is not null) return Conflict(new { error = "cost_deleted" });
        // Not included yet → it is simply edited; a correction exists for money already settled.
        if (original.StatementId is null) return Conflict(new { error = "cost_not_included" });

        var now = DateTime.UtcNow;
        var correction = new CommonCost
        {
            LicenseeId = id,
            PaidBy = original.PaidBy,
            Category = original.Category,
            Vendor = original.Vendor,
            InvoiceNumber = original.InvoiceNumber,
            // The original's date: already reached by every later statement, so the next one counts it whatever the month.
            InvoiceDate = original.InvoiceDate,
            CorrectsId = original.Id,
            CreatedBy = ActorLabel,
            EnteredAt = now,
        };
        var error = ApplyCorrection(correction, body.Amount, body.Reason, body.Note, requireAll: true);
        if (error is not null) return error;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        if (await CorrectionBreaksFloorAsync(original.Id, correction.Amount, excluding: null, ct))
            return BadRequest(new { error = "correction_below_zero" });
        _db.CommonCosts.Add(correction);
        _db.AgreementEvents.Add(SharedCostRules.Event(id, ActorLabel, AgreementEventKinds.CorrectionAdded, correction.Id,
            newValue: SharedCostRules.Snapshot(correction), reason: correction.Reason, at: now));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Correction {Amount} GEL of shared cost {Vendor} #{Invoice} ({PaidBy}) added by {Actor}: {Reason}",
            correction.Amount, original.Vendor, original.InvoiceNumber, original.PaidBy, ActorLabel, correction.Reason);
        return Ok(await CostDtoAsync(correction.Id, ct));
    }

    /// <summary>
    /// Would the original with all its (not deleted) corrections — <paramref name="amount"/> in place of the one being
    /// edited — come to less than zero? An invoice can be corrected down to nothing, never into a refund nobody paid.
    /// Locks the original's row for the rest of the transaction, so two corrections at once cannot both pass.
    /// </summary>
    private async Task<bool> CorrectionBreaksFloorAsync(Guid originalId, decimal amount, Guid? excluding, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CommonCosts\" WHERE \"Id\" = {originalId} FOR UPDATE", ct);
        var original = await _db.CommonCosts.AsNoTracking().Where(c => c.Id == originalId).Select(c => c.Amount).FirstAsync(ct);
        var others = await _db.CommonCosts.AsNoTracking()
            .Where(c => c.CorrectsId == originalId && c.DeletedAt == null && (excluding == null || c.Id != excluding))
            .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        return original + others + amount < 0m;
    }

    /// <summary>The cost changed under the request (a statement included it, its owner deleted it, someone held it): 409 saying which.</summary>
    private async Task<IActionResult> ChangedMeanwhileAsync(Guid costId, CancellationToken ct)
    {
        var now = await _db.CommonCosts.AsNoTracking().Where(c => c.Id == costId)
            .Select(c => new { includedIn = c.Statement != null ? c.Statement.InvoiceRef : null, c.DeletedAt, c.HeldAt })
            .FirstOrDefaultAsync(ct);
        _log.LogWarning("Shared cost {Id}: change by {Actor} refused — it changed meanwhile ({Ref})", costId, ActorLabel, now?.includedIn);
        if (now?.includedIn is not null) return Conflict(new { error = "cost_included", now.includedIn });
        if (now?.DeletedAt is not null) return Conflict(new { error = "cost_deleted" });
        if (now?.HeldAt is not null) return Conflict(new { error = "cost_already_held" });
        return Conflict(new { error = "cost_not_held" });
    }

    private static CommonCost WithNote(CommonCost c, string? note) => new()
    {
        PaidBy = c.PaidBy, Category = c.Category, Vendor = c.Vendor, InvoiceNumber = c.InvoiceNumber, InvoiceDate = c.InvoiceDate,
        Amount = c.Amount, Note = note, CorrectsId = c.CorrectsId, Reason = c.Reason,
    };

    private (string? Reason, IActionResult? Error) RequiredReason(string? value)
    {
        var reason = Blank(value);
        if (reason is null) return (null, BadRequest(new { error = "reason_required" }));
        if (reason.Length > MaxReasonLength) return (null, BadRequest(new { error = "reason_too_long" }));
        return (reason, null);
    }

    private IActionResult? Apply(CommonCost c, CostBody b, bool requireAll)
    {
        if (b.Category is not null || requireAll)
        {
            if (!TryParseCategory(b.Category, out var cat)) return BadRequest(new { error = "invalid_category" });
            c.Category = cat;
        }
        if (b.Vendor is not null || requireAll)
        {
            var v = b.Vendor?.Trim() ?? "";
            if (v.Length is 0 or > 200) return BadRequest(new { error = "vendor_required" });
            c.Vendor = v;
        }
        if (b.InvoiceNumber is not null || requireAll)
        {
            // §2.4: only costs evidenced by a third-party invoice are shared.
            var n = b.InvoiceNumber?.Trim() ?? "";
            if (n.Length is 0 or > 100) return BadRequest(new { error = "invoice_number_required" });
            c.InvoiceNumber = n;
        }
        if (b.InvoiceDate is not null || requireAll)
        {
            if (!DateOnly.TryParseExact(b.InvoiceDate?.Trim() ?? "", "yyyy-MM-dd", out var d) || d.Year < 2020)
                return BadRequest(new { error = "invalid_invoice_date" });
            if (d > TodayLocal) return BadRequest(new { error = "invoice_date_in_future" });
            c.InvoiceDate = d;
        }
        if (b.Amount is not null || requireAll)
        {
            if (b.Amount is not { } a || a <= 0 || a > MaxCostAmount) return BadRequest(new { error = "invalid_amount" });
            // Tetri-exact, like cashouts: never round what someone typed.
            if (decimal.Round(a, 2) != a) return BadRequest(new { error = "amount_precision" });
            c.Amount = a;
        }
        return ApplyNote(c, b.Note);
    }

    /// <summary>A correction's own figures: a signed, non-zero, tetri-exact amount and a reason (and an optional note).</summary>
    private IActionResult? ApplyCorrection(CommonCost c, decimal? amount, string? reason, string? note, bool requireAll)
    {
        if (amount is not null || requireAll)
        {
            if (amount is not { } a || a == 0 || Math.Abs(a) > MaxCostAmount) return BadRequest(new { error = "invalid_amount" });
            if (decimal.Round(a, 2) != a) return BadRequest(new { error = "amount_precision" });
            c.Amount = a;
        }
        if (reason is not null || requireAll)
        {
            var (r, error) = RequiredReason(reason);
            if (error is not null) return error;
            c.Reason = r;
        }
        return ApplyNote(c, note);
    }

    private IActionResult? ApplyNote(CommonCost c, string? value)
    {
        if (value is null) return null;
        var note = Blank(value);
        if (note is { Length: > 500 }) return BadRequest(new { error = "note_too_long" });
        c.Note = note;
        return null;
    }

    /// <summary>
    /// The same invoice entered twice would be counted twice — same side, vendor and number is refused. A deleted entry
    /// no longer counts (it can be entered again correctly), and a correction carries its original's number on purpose.
    /// </summary>
    private async Task<bool> IsDuplicateAsync(CommonCost c, CancellationToken ct)
    {
        var vendor = c.Vendor.ToLower();
        var number = c.InvoiceNumber.ToLower();
        return await _db.CommonCosts.AsNoTracking().AnyAsync(x =>
            x.Id != c.Id && x.LicenseeId == c.LicenseeId && x.PaidBy == c.PaidBy
            && x.DeletedAt == null && x.CorrectsId == null
            && x.Vendor.ToLower() == vendor && x.InvoiceNumber.ToLower() == number, ct);
    }

    private static bool TryParseCategory(string? value, out CostCategory category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().Replace("_", "");
        return Enum.TryParse(v, ignoreCase: true, out category) && Enum.IsDefined(category);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public record LicenseeBody(
    string? Name = null,
    string? TaxId = null,
    string? Email = null,
    string? Iban = null,
    // "YYYY-MM-DD"; "" clears; null leaves unchanged.
    string? LaunchDate = null,
    decimal? BarrierGel = null,
    decimal? SwichSharePercent = null,
    bool? IsActive = null,
    // Why — required when the launch date changes; recorded with any change of the launch date or the terms.
    string? Reason = null);

public record AssignParkBody(Guid? LicenseeId);

public record CostBody(
    string? Category = null,
    string? Vendor = null,
    string? InvoiceNumber = null,
    // "YYYY-MM-DD".
    string? InvoiceDate = null,
    decimal? Amount = null,
    string? Note = null,
    // A correction's reason (editing a correction before a statement counts it).
    string? Reason = null);

public record ReasonBody(string? Reason = null);

public record CorrectionBody(
    // Signed GEL: negative takes the original down, positive adds to it.
    decimal? Amount = null,
    string? Reason = null,
    string? Note = null);
