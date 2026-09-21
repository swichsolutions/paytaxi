using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// "Something needs a human" — the feed behind the red banner in the admin console. Computed on
/// demand from the money tables, no extra state. There is no e-mail channel yet, so this is how a
/// failed nightly settlement or a cashout parked for review gets noticed: whoever opens the console
/// sees it at once, and the banner keeps coming back until the underlying row is resolved.
///
/// Scope follows the caller: Swich and the operator see every park, a park manager only their own.
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

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var scoped = ScopedParkId;
        var now = DateTime.UtcNow;
        var alerts = new List<Alert>();

        // ── Settlements: failed, or stuck in Processing ───────────────
        var settlements = await _db.Settlements.AsNoTracking()
            .Where(s => (scoped == null || s.ParkId == scoped)
                     && (s.Status == SettlementStatus.Failed || s.Status == SettlementStatus.Processing))
            .Select(s => new { s.Id, s.ParkId, ParkName = s.Park.Name, s.SettlementDate, s.SwichShare, s.Status, s.FailureReason, s.BankTransferId, s.LastAttemptAt, s.UpdatedAt })
            .ToListAsync(ct);

        foreach (var g in settlements.Where(s => s.Status == SettlementStatus.Failed).GroupBy(s => s.ParkId))
        {
            var latest = g.OrderByDescending(s => s.SettlementDate).First();
            alerts.Add(new Alert(
                Kind: "settlement_failed", Severity: "danger",
                ParkId: g.Key, ParkName: latest.ParkName,
                Count: g.Count(), Amount: g.Sum(s => s.SwichShare),
                Since: g.Min(s => s.SettlementDate).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Code: CodeOf(latest.FailureReason), Detail: latest.FailureReason,
                Link: "/admin/settlements",
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
                Link: "/admin/settlements",
                Ids: g.Select(s => s.Id).OrderBy(x => x).ToList()));
        }

        // ── Cashouts parked for a human ───────────────────────────────
        var review = await _db.Cashouts.AsNoTracking()
            .Where(c => (scoped == null || c.ParkId == scoped) && c.Status == CashoutStatus.ReviewRequired)
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

        // ── Reconciliation: open discrepancies (warning, not danger) ─
        var open = await _db.ReconciliationDiscrepancies.AsNoTracking()
            .Where(d => (scoped == null || d.ParkId == scoped) && !d.IsResolved)
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

        alerts = alerts
            .OrderBy(a => a.Severity == "danger" ? 0 : 1)
            .ThenBy(a => a.Since)
            .ToList();

        // Stable fingerprint of WHAT is alerting (ids), so the client can hide a dismissed banner until
        // something new appears — a fresh failure re-raises it even if the old ones were dismissed.
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", alerts.Select(a => a.Kind + ":" + string.Join(",", a.Ids))))))[..16].ToLowerInvariant();

        return Ok(new
        {
            generatedAt = now,
            dangerCount = alerts.Where(a => a.Severity == "danger").Sum(a => a.Count),
            warningCount = alerts.Where(a => a.Severity == "warning").Sum(a => a.Count),
            signature,
            alerts = alerts.Select(a => new
            {
                a.Kind, a.Severity, a.ParkId, a.ParkName, a.Count, a.Amount, a.Since, a.Code, a.Detail, a.Link,
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

    private record Alert(
        string Kind, string Severity, Guid ParkId, string ParkName, int Count, decimal? Amount,
        DateTime Since, string? Code, string? Detail, string Link, List<Guid> Ids);
}
