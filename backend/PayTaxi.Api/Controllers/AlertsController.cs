using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// "Something needs a human" — the feed behind the red banner in the admin console. Computed on
/// demand from the money tables, no extra state. There is no e-mail channel yet, so this is how a
/// failed monthly settlement transfer, a cashout parked for review or payouts waiting for the bank /
/// a top-up get noticed: whoever opens the console
/// sees it at once, and the banner keeps coming back until the underlying row is resolved.
///
/// Scope follows the caller: Swich sees every park, the operator its own licensee's parks, a park manager only their own.
/// Swich and the operator also get one line per park whose revocation of the platform's authorisations is in effect
/// (Annex 3 §7) while payouts or shares still wait on it — they need a person, never a retry or a top-up.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/alerts")]
public class AlertsController : AdminControllerBase
{
    private readonly AppDbContext _db;
    public AlertsController(AppDbContext db) => _db = db;

    /// <summary>A settlement still Processing after this long with no bank id was interrupted; with a bank id it is pending too long.</summary>
    private static readonly TimeSpan StuckNoTransfer = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StuckPending = TimeSpan.FromHours(6);

    /// <summary>
    /// Queued payouts younger than this are a blip the queue worker usually clears on its own; older
    /// ones mean the bank or the park's balance needs someone's attention. Past a day it is danger.
    /// </summary>
    private static readonly TimeSpan PayoutWaitingAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PayoutWaitingDanger = TimeSpan.FromHours(24);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (ParkScopeMissing) return Forbid();
        // null = every park (Swich); otherwise the caller's parks — an operator's licensee's, a manager's own.
        var parks = ParkFilter;
        var scoped = parks is not null;
        var visible = parks ?? Array.Empty<Guid>();
        var now = DateTime.UtcNow;
        var alerts = new List<Alert>();

        // Parks whose revocation of the platform's authorisations is in effect (Annex 3 §7): nothing is sent or collected
        // from their accounts any more. For Swich and the operator their waiting payouts and unpaid shares make ONE line
        // (authorisation_revoked, below), with what to do about each — not "payouts waiting" / "transfer failed", which
        // invite a top-up or a retry that can never help. A park manager keeps the waiting line (with the revocation as its
        // cause) and is not shown a share its park has withdrawn the platform's authority to collect.
        var seesAll = IsSwichOrOperator;
        var today = SettlementService.TodayLocal();
        var revokedParks = await _db.Parks.AsNoTracking()
            .Where(p => (!scoped || visible.Contains(p.Id)) && p.AuthorisationRevokedFrom != null && p.AuthorisationRevokedFrom <= today)
            .Select(p => new { p.Id, p.Name, From = p.AuthorisationRevokedFrom!.Value })
            .ToDictionaryAsync(p => p.Id, ct);
        bool InRevokedLine(Guid parkId) => seesAll && revokedParks.ContainsKey(parkId);

        // ── Settlements: failed, or stuck in Processing ───────────────
        var settlements = await _db.Settlements.AsNoTracking()
            .Where(s => (!scoped || visible.Contains(s.ParkId))
                     && (s.Status == SettlementStatus.Failed || s.Status == SettlementStatus.Processing))
            .Select(s => new { s.Id, s.ParkId, ParkName = s.Park.Name, s.SettlementDate, s.SwichShare, s.Status, s.FailureReason, s.BankTransferId, s.LastAttemptAt, s.UpdatedAt, s.MonthlyStatementId })
            .ToListAsync(ct);

        // A month is settled from the run day of the FOLLOWING month, so a settlement alert is about a past month:
        // the link opens that month (and the banner switches the topbar to the park), where the failed transfer
        // and its Retry are — not the running month's preview.
        static string SettlementsLink(DateOnly settlementDate) =>
            $"/admin/settlements?month={settlementDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";

        foreach (var g in settlements.Where(s => s.Status == SettlementStatus.Failed
                                              && !ParkAuthorisation.IsRevokedReason(s.FailureReason) && !InRevokedLine(s.ParkId))
                                     .GroupBy(s => s.ParkId))
        {
            var latest = g.OrderByDescending(s => s.SettlementDate).First();
            alerts.Add(new Alert(
                Kind: "settlement_failed", Severity: "danger",
                ParkId: g.Key, ParkName: latest.ParkName,
                Count: g.Count(), Amount: g.Sum(s => s.SwichShare),
                Since: g.Min(s => s.SettlementDate).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Code: CodeOf(latest.FailureReason), Detail: latest.FailureReason,
                Link: SettlementsLink(latest.SettlementDate),
                Ids: g.Select(s => s.Id).OrderBy(x => x).ToList()));
        }

        var stuck = settlements.Where(s => s.Status == SettlementStatus.Processing && (
                (s.BankTransferId == null && (s.LastAttemptAt ?? s.UpdatedAt) < now - StuckNoTransfer) ||
                (s.BankTransferId != null && (s.LastAttemptAt ?? s.UpdatedAt) < now - StuckPending))).ToList();
        foreach (var g in stuck.GroupBy(s => s.ParkId))
        {
            var latest = g.OrderByDescending(s => s.SettlementDate).First();
            alerts.Add(new Alert(
                Kind: "settlement_stuck", Severity: "danger",
                ParkId: g.Key, ParkName: latest.ParkName,
                Count: g.Count(), Amount: g.Sum(s => s.SwichShare),
                Since: g.Min(s => s.LastAttemptAt ?? s.UpdatedAt),
                Code: latest.BankTransferId == null ? "INTERRUPTED" : "PENDING_TOO_LONG", Detail: latest.BankTransferId,
                Link: SettlementsLink(latest.SettlementDate),
                Ids: g.Select(s => s.Id).OrderBy(x => x).ToList()));
        }

        // ── Cashouts parked for a human ───────────────────────────────
        var review = await _db.Cashouts.AsNoTracking()
            .Where(c => (!scoped || visible.Contains(c.ParkId)) && c.Status == CashoutStatus.ReviewRequired)
            .Select(c => new { c.Id, c.ParkId, ParkName = c.Park.Name, c.Amount, c.UpdatedAt, c.FailureReason })
            .ToListAsync(ct);
        foreach (var g in review.GroupBy(c => c.ParkId))
        {
            var latest = g.OrderByDescending(c => c.UpdatedAt).First();
            alerts.Add(new Alert(
                Kind: "cashout_review", Severity: "danger",
                ParkId: g.Key, ParkName: latest.ParkName,
                Count: g.Count(), Amount: g.Sum(c => c.Amount),
                Since: g.Min(c => c.UpdatedAt),
                Code: CodeOf(latest.FailureReason), Detail: latest.FailureReason,
                Link: "/admin/cashouts?status=review",
                Ids: g.Select(c => c.Id).OrderBy(x => x).ToList()));
        }

        // ── Payouts waiting in the queue (bank down, park account short, no account) ─
        // Waiting is the contractual behaviour (Annex 3 §5) — nothing is lost, the payouts go out on
        // their own once the cause is gone — but the cause usually needs a person: the park has to top
        // up, or someone has to chase the bank. Amount is NET (what the park's account must cover);
        // the code is the most common reason, which decides the advice the console shows.
        var waitingCutoff = now - PayoutWaitingAfter;
        var waiting = await _db.Cashouts.AsNoTracking()
            .Where(c => (!scoped || visible.Contains(c.ParkId))
                     && c.Status == CashoutStatus.Queued && c.CreatedAt < waitingCutoff)
            .Select(c => new { c.Id, c.ParkId, ParkName = c.Park.Name, Net = c.Amount - c.Fee, c.CreatedAt, c.FailureReason })
            .ToListAsync(ct);
        foreach (var g in waiting.Where(c => !InRevokedLine(c.ParkId)).GroupBy(c => c.ParkId))
        {
            var oldest = g.Min(c => c.CreatedAt);
            var code = g.Select(c => CodeOf(c.FailureReason)).Where(x => x is not null)
                .GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Select(x => x.Key).FirstOrDefault();
            alerts.Add(new Alert(
                Kind: "payout_waiting", Severity: now - oldest > PayoutWaitingDanger ? "danger" : "warning",
                ParkId: g.Key, ParkName: g.First().ParkName,
                Count: g.Count(), Amount: g.Sum(c => c.Net),
                Since: oldest,
                Code: code, Detail: g.OrderBy(c => c.CreatedAt).First().FailureReason,
                Link: "/admin/cashouts?status=queued",
                Ids: g.Select(c => c.Id).OrderBy(x => x).ToList()));
        }

        // ── Revoked authorisations: what still waits on the park's account ─
        // Payouts the drivers were debited for and that will never be sent from it (a person cancels them — the amount goes
        // back to the driver's Yandex balance), and shares that will never be collected from it (owed by the licensee under
        // §2.5 — or by a Swich-direct park itself — and closed as paid outside the platform). Any age: none of it resolves
        // on its own. Danger while drivers wait for money.
        if (seesAll && revokedParks.Count > 0)
        {
            var ids = revokedParks.Keys.ToList();
            var revokedPayouts = await _db.Cashouts.AsNoTracking()
                .Where(c => ids.Contains(c.ParkId) && c.Status == CashoutStatus.Queued)
                .Select(c => new { c.Id, c.ParkId, Net = c.Amount - c.Fee })
                .ToListAsync(ct);
            foreach (var park in revokedParks.Values)
            {
                var payouts = revokedPayouts.Where(c => c.ParkId == park.Id).ToList();
                var owed = settlements.Where(s => s.ParkId == park.Id && s.Status == SettlementStatus.Failed).ToList();
                if (payouts.Count == 0 && owed.Count == 0) continue;
                alerts.Add(new Alert(
                    Kind: "authorisation_revoked", Severity: payouts.Count > 0 ? "danger" : "warning",
                    ParkId: park.Id, ParkName: park.Name,
                    Count: payouts.Count + owed.Count, Amount: null,
                    Since: park.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Code: null, Detail: null,
                    Link: payouts.Count > 0 ? "/admin/cashouts?status=queued" : SettlementsLink(owed.Max(s => s.SettlementDate)),
                    Ids: payouts.Select(c => c.Id).Concat(owed.Select(s => s.Id)).OrderBy(x => x).ToList(),
                    Revocation: new
                    {
                        revokedFrom = park.From,
                        payouts = payouts.Count,
                        payoutsNet = payouts.Sum(c => c.Net),
                        settlements = owed.Count,
                        owed = owed.Sum(s => s.SwichShare),
                        // The part of it the licensee answers for (§2.5); the rest is a Swich-direct park's own.
                        licenseeOwed = owed.Where(s => s.MonthlyStatementId != null).Sum(s => s.SwichShare),
                    }));
            }
        }

        // ── Reconciliation: open discrepancies (warning, not danger) ─
        var open = await _db.ReconciliationDiscrepancies.AsNoTracking()
            .Where(d => (!scoped || visible.Contains(d.ParkId)) && !d.IsResolved)
            .Select(d => new { d.Id, d.ParkId, d.CreatedAt, d.Kind })
            .ToListAsync(ct);
        var parkNames = open.Count == 0 ? new Dictionary<Guid, string>() : await _db.Parks.AsNoTracking()
            .Where(p => open.Select(d => d.ParkId).Distinct().Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        foreach (var g in open.GroupBy(d => d.ParkId))
        {
            alerts.Add(new Alert(
                Kind: "reconciliation_open", Severity: "warning",
                ParkId: g.Key, ParkName: parkNames.GetValueOrDefault(g.Key, "?"),
                Count: g.Count(), Amount: null,
                Since: g.Min(d => d.CreatedAt),
                Code: g.GroupBy(d => d.Kind).OrderByDescending(k => k.Count()).First().Key, Detail: null,
                Link: "/admin/reconciliation",
                Ids: g.Select(d => d.Id).OrderBy(x => x).ToList()));
        }

        // ── Sign-in locked: a driver's phone used up its failed sign-in codes for the day (AuthController) ─
        // A security event: someone may be guessing a driver's code — or the driver lost track of theirs. One line per
        // park while any lock lasts; the count is drivers, never a phone number.
        var locked = await _db.PhoneSignInEvents.AsNoTracking()
            .Where(e => e.Kind == Core.Entities.PhoneSignInEventKinds.Locked && e.Until > now)
            .Join(_db.Drivers.AsNoTracking().Where(d => !scoped || visible.Contains(d.ParkId)),
                e => e.PhoneHash, d => d.PhoneHash, (e, d) => new { DriverId = d.Id, d.ParkId, ParkName = d.Park.Name, e.At })
            .ToListAsync(ct);
        foreach (var g in locked.GroupBy(x => x.ParkId))
        {
            var drivers = g.Select(x => x.DriverId).Distinct().OrderBy(x => x).ToList();
            alerts.Add(new Alert(
                Kind: "signin_locked", Severity: "warning",
                ParkId: g.Key, ParkName: g.First().ParkName,
                Count: drivers.Count, Amount: null,
                Since: g.Min(x => x.At),
                Code: null, Detail: null,
                Link: "/admin/drivers",
                Ids: drivers));
        }

        // ── Suspended parks: cashouts are off until someone reactivates ─
        var suspended = await _db.Parks.AsNoTracking()
            .Where(p => (!scoped || visible.Contains(p.Id)) && p.Status == ParkStatus.Suspended)
            .Select(p => new { p.Id, p.Name, p.StatusReason, p.StatusChangedAt, p.UpdatedAt })
            .ToListAsync(ct);
        foreach (var p in suspended)
        {
            alerts.Add(new Alert(
                Kind: "park_suspended", Severity: "warning",
                ParkId: p.Id, ParkName: p.Name,
                Count: 1, Amount: null,
                Since: p.StatusChangedAt ?? p.UpdatedAt,
                Code: null, Detail: p.StatusReason,
                Link: "/admin/settings",
                Ids: new List<Guid> { p.Id }));
        }

        // ── Open incidents (Annex 1 log): P1 → danger, P2 → warning, P3 stays off the banner ─
        // One line per park (or platform-wide), worst level first. Park managers also see platform-wide
        // incidents — those stop their drivers too.
        var incidents = await _db.Incidents.AsNoTracking()
            .Where(i => i.Status != IncidentStatus.Resolved && i.Level != IncidentLevel.P3
                     && (!scoped || i.ParkId == null || visible.Contains(i.ParkId.Value)))
            .Select(i => new { i.Id, i.ParkId, ParkName = i.Park != null ? i.Park.Name : null, i.Level, i.Status, i.Title, i.ReportedAt })
            .ToListAsync(ct);
        foreach (var g in incidents.GroupBy(i => i.ParkId))
        {
            var worst = g.OrderBy(i => i.Level).ThenBy(i => i.ReportedAt).First();
            // Code = worst level + whether anything in the group still waits for the owner's response,
            // so the banner can say "waiting for Swich" vs "Swich is on it" without another request.
            var awaitingAck = g.Any(i => i.Status == IncidentStatus.Reported);
            alerts.Add(new Alert(
                Kind: "incident_open", Severity: worst.Level == IncidentLevel.P1 ? "danger" : "warning",
                ParkId: g.Key, ParkName: worst.ParkName,
                Count: g.Count(), Amount: null,
                Since: g.Min(i => i.ReportedAt),
                Code: $"{worst.Level}_{(awaitingAck ? "REPORTED" : "ACKNOWLEDGED")}", Detail: worst.Title,
                Link: "/admin/incidents",
                Ids: g.Select(i => i.Id).OrderBy(x => x).ToList()));
        }

        alerts = alerts
            .OrderBy(a => a.Severity == "danger" ? 0 : 1)
            .ThenBy(a => a.Since)
            .ToList();

        // Stable fingerprint of WHAT is alerting and HOW BADLY, so the client can hide a dismissed banner until
        // something new appears: a fresh failure (new ids), or one that got worse while keeping its ids — an
        // incident re-levelled P2 → P1 (its 4-hour response clock now runs), payouts waiting past 24 h. Both
        // turn the line from warning to danger. The rest of the code is left out on purpose: the dominant
        // failure code can flip between retries, and "acknowledged" is good news; neither should re-raise a
        // banner someone dismissed.
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", alerts.Select(a => a.Kind + ":" + a.Severity + ":" + string.Join(",", a.Ids))))))[..16].ToLowerInvariant();

        return Ok(new
        {
            generatedAt = now,
            dangerCount = alerts.Where(a => a.Severity == "danger").Sum(a => a.Count),
            warningCount = alerts.Where(a => a.Severity == "warning").Sum(a => a.Count),
            signature,
            alerts = alerts.Select(a => new
            {
                a.Kind, a.Severity, a.ParkId, a.ParkName, a.Count, a.Amount, a.Since, a.Code, a.Detail, a.Link, a.Revocation,
            }),
        });
    }

    /// <summary>"INSUFFICIENT_PARK_BALANCE: Park account balance…" → "INSUFFICIENT_PARK_BALANCE".</summary>
    private static string? CodeOf(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason)) return null;
        var i = failureReason.IndexOf(':');
        return i > 0 ? failureReason[..i].Trim() : failureReason.Trim();
    }

    /// <summary>ParkId / ParkName are null for a platform-wide incident. Revocation: authorisation_revoked only.</summary>
    private record Alert(
        string Kind, string Severity, Guid? ParkId, string? ParkName, int Count, decimal? Amount,
        DateTime Since, string? Code, string? Detail, string Link, List<Guid> Ids, object? Revocation = null);
}
