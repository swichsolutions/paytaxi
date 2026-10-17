using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// §4.1.2 late-payment information for monthly park transfers, computed one way for the console and the
/// licensee's report: the due date from the month (§2.5), the paid date from the row (the bank's completion,
/// or the date of a payment made outside the platform), and every overdue day attributed by the failed
/// attempts the ledger recorded — not by the row's latest failure reason, which a success clears.
/// </summary>
public static class SettlementOverdue
{
    /// <summary>The failed attempts of each settlement (SettlementFailed ledger entries, code in Reference), oldest first, by local day.</summary>
    public static async Task<Dictionary<Guid, List<SettlementMath.FailedAttempt>>> FailedAttemptsAsync(
        AppDbContext db, IEnumerable<Guid> settlementIds, TimeZoneInfo tz, CancellationToken ct)
    {
        var ids = settlementIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(l => l.SettlementId != null && ids.Contains(l.SettlementId.Value) && l.EntryType == LedgerEntryType.SettlementFailed)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new { Id = l.SettlementId!.Value, l.CreatedAt, l.Reference })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g
                .Select(r => new SettlementMath.FailedAttempt(
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc), tz)),
                    r.Reference))
                .ToList());
    }

    /// <summary>
    /// Null when the row is not a monthly transfer, nothing was owed, or it is not late. A transfer paid late keeps
    /// its final figure; an open one counts to today.
    /// </summary>
    public static SettlementMath.OverdueInfo? Of(Settlement s, IReadOnlyList<SettlementMath.FailedAttempt>? attempts,
        SettlementOptions opts, DateOnly todayLocal, TimeZoneInfo tz)
    {
        if (!s.IsMonthly) return null;
        var due = SettlementService.DueDate(SettlementService.FirstOfMonth(s.SettlementDate), opts.DueDays);
        DateOnly? paidOn = s.Status switch
        {
            SettlementStatus.Completed => s.CompletedAt is { } at
                ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), tz))
                : null,
            SettlementStatus.SettledExternally => s.ExternalPaidOn,
            _ => null,
        };
        if (s.Status is SettlementStatus.Completed or SettlementStatus.SettledExternally && paidOn is null) return null;
        return SettlementMath.Overdue(s.SwichShare, due, paidOn, todayLocal, opts.PenaltyPercentPerDay,
            attempts ?? (IReadOnlyList<SettlementMath.FailedAttempt>)Array.Empty<SettlementMath.FailedAttempt>());
    }
}
