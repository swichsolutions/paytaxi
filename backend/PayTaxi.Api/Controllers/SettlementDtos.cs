using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Shapes shared by the settlement and licensee endpoints, so the console and the tests see one
/// representation of a cost, an overdue transfer and a month — whichever endpoint returned it.
/// </summary>
internal static class SettlementDtos
{
    /// <summary>"2026-10" → 2026-10-01. Anything else (including "2026-10-05") is refused.</summary>
    public static bool TryParseMonth(string? value, out DateOnly firstDay)
    {
        firstDay = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7) return false;
        return DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out firstDay)
            && firstDay.Year is >= 2000 and <= 2100;
    }

    public static string MonthKey(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Who is looking at shared costs, and when: the entry's state and what this side may do with it (§2.4: each side
    /// keeps its own entries; the other side may hold one it disputes). Carries the entries related to those shown —
    /// the original a correction corrects, the corrections of an original — so each is shown next to the other.
    /// </summary>
    internal sealed class CostViewer
    {
        /// <summary>Swich for a super admin, the licensee for the operator, none for anyone else.</summary>
        public CostPaidBy? Side { get; init; }
        public DateTime NowUtc { get; init; }
        public int CutoffHours { get; init; }
        public DateTime NextRunUtc { get; init; }
        public IReadOnlyDictionary<Guid, CommonCost> Originals { get; init; } = new Dictionary<Guid, CommonCost>();
        public ILookup<Guid, CommonCost> Corrections { get; init; } = Array.Empty<CommonCost>().ToLookup(c => c.Id);

        public bool Owns(CommonCost c) => Side == c.PaidBy;
        public bool IsOtherSide(CommonCost c) => Side is { } s && s != c.PaidBy;
    }

    /// <summary>The viewer for <paramref name="shown"/>, with their originals and corrections loaded (statements included).</summary>
    public static async Task<CostViewer> CostViewerAsync(AppDbContext db, bool isSuperAdmin, bool isOperator, SettlementOptions opts,
        IEnumerable<CommonCost> shown, CancellationToken ct)
    {
        var list = shown.ToList();
        var originalIds = list.Where(c => c.CorrectsId is not null).Select(c => c.CorrectsId!.Value).Distinct().ToList();
        var ids = list.Where(c => c.CorrectsId is null).Select(c => c.Id).Distinct().ToList();
        var originals = originalIds.Count == 0 ? new List<CommonCost>()
            : await db.CommonCosts.AsNoTracking().Include(c => c.Statement).Where(c => originalIds.Contains(c.Id)).ToListAsync(ct);
        var corrections = ids.Count == 0 ? new List<CommonCost>()
            : await db.CommonCosts.AsNoTracking().Include(c => c.Statement).Where(c => c.CorrectsId != null && ids.Contains(c.CorrectsId.Value)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        return new CostViewer
        {
            Side = isSuperAdmin ? CostPaidBy.Swich : isOperator ? CostPaidBy.Licensee : null,
            NowUtc = now,
            CutoffHours = opts.CostCutoffHours,
            NextRunUtc = SharedCostRules.NextStatementRunUtc(now, opts),
            Originals = originals.ToDictionary(c => c.Id),
            Corrections = corrections.ToLookup(c => c.CorrectsId!.Value),
        };
    }

    /// <summary>
    /// One shared-cost entry as both sides see it (§2.6), with its state — included / deleted (struck through, with
    /// who and why) / held (disputed by the other side) / waiting (inside its 48-hour window; <c>missesNextRun</c> when
    /// the next scheduled statement comes before the window ends) / pending (counted if a statement were computed now) —
    /// and what the viewer's side may do with it. <c>includedIn</c> needs the entry's Statement loaded.
    /// </summary>
    public static object Cost(CommonCost c, CostViewer v)
    {
        var state = SharedCostRules.State(c, v.NowUtc, v.CutoffHours);
        var open = c.StatementId is null && c.DeletedAt is null;
        CommonCost? original = c.CorrectsId is { } oid && v.Originals.TryGetValue(oid, out var o) ? o : null;
        return new
        {
            id = c.Id,
            licenseeId = c.LicenseeId,
            paidBy = c.PaidBy.ToString(),
            category = c.Category.ToString(),
            vendor = c.Vendor,
            invoiceNumber = c.InvoiceNumber,
            invoiceDate = c.InvoiceDate,
            amount = c.Amount,
            note = c.Note,
            createdBy = c.CreatedBy,
            createdAt = c.CreatedAt,
            enteredAt = c.EnteredAt,
            eligibleFrom = SharedCostRules.EligibleFrom(c.EnteredAt, v.CutoffHours),
            statementId = c.StatementId,
            includedIn = c.Statement?.InvoiceRef,
            state,
            missesNextRun = state == SharedCostRules.Waiting && SharedCostRules.MissesRun(c, v.NextRunUtc, v.CutoffHours),
            isCorrection = c.CorrectsId is not null,
            correctsId = c.CorrectsId,
            reason = c.Reason,
            corrects = original is null ? null : new
            {
                id = original.Id,
                amount = original.Amount,
                invoiceDate = original.InvoiceDate,
                includedIn = original.Statement?.InvoiceRef,
            },
            corrections = c.CorrectsId is not null ? Array.Empty<object>() : v.Corrections[c.Id]
                .OrderBy(x => x.CreatedAt)
                .Select(x => (object)new
                {
                    id = x.Id,
                    amount = x.Amount,
                    reason = x.Reason,
                    state = SharedCostRules.State(x, v.NowUtc, v.CutoffHours),
                    includedIn = x.Statement?.InvoiceRef,
                    createdBy = x.CreatedBy,
                    createdAt = x.CreatedAt,
                })
                .ToArray(),
            held = c.HeldAt is null ? null : new { at = c.HeldAt, by = c.HeldBy, reason = c.HoldReason },
            deleted = c.DeletedAt is null ? null : new { at = c.DeletedAt, by = c.DeletedBy, reason = c.DeleteReason },
            // Each side keeps its own entries — Swich no more than the operator may change the other's (§2.4).
            canEdit = open && v.Owns(c),
            canDelete = open && v.Owns(c),
            // The other side's recourse: hold what it disputes (released by the side that held it).
            canHold = open && c.HeldAt is null && v.IsOtherSide(c),
            canRelease = open && c.HeldAt is not null && v.IsOtherSide(c),
            canCorrect = c.StatementId is not null && c.CorrectsId is null && v.Owns(c),
        };
    }

    /// <summary>
    /// §4.1.2 information for one park transfer: null when it is not late (or nothing was owed). A transfer paid
    /// late — by the bank or outside the platform — keeps its final figure; an open one counts to today.
    ///
    /// The penalty is one the LICENSEE owes Swich (§4.1.2). It is returned only for a licensee park's row and only
    /// to Swich and the operator (<paramref name="seesContract"/>): a Swich-direct park is outside the agreement
    /// (§2.2), and a park manager sees that its transfer was late — never a penalty Annex 3 does not give the park.
    /// </summary>
    public static object? Overdue(Settlement s, IReadOnlyList<SettlementMath.FailedAttempt>? attempts, SettlementOptions opts,
        DateOnly todayLocal, TimeZoneInfo tz, bool seesContract)
    {
        var info = SettlementOverdue.Of(s, attempts, opts, todayLocal, tz);
        if (info is null) return null;
        if (!seesContract || s.MonthlyStatementId is null)
            return new { dueDate = info.DueDate, daysOverdue = info.DaysOverdue, paid = info.Paid };
        return new
        {
                dueDate = info.DueDate,
                daysOverdue = info.DaysOverdue,
                penaltyDays = info.PenaltyDays,
                technicalDays = info.TechnicalDays,
                accrued = info.Accrued,
                percentPerDay = opts.PenaltyPercentPerDay,
                paid = info.Paid,
        };
    }

    /// <summary>
    /// A payment recorded as made outside the platform (null otherwise). Swich and the operator see the whole
    /// record (§2.6: identical data for both sides); a park manager sees who settled its debt and when.
    /// </summary>
    public static object? External(Settlement s, bool seesContract)
    {
        if (s.Status != SettlementStatus.SettledExternally) return null;
        if (!seesContract) return new { payer = s.ExternalPayer, paidOn = s.ExternalPaidOn };
        return new
        {
                payer = s.ExternalPayer,
                paidOn = s.ExternalPaidOn,
                reference = s.ExternalReference,
                note = s.ExternalNote,
                recordedBy = s.ExternalRecordedBy,
                recordedAt = s.ExternalRecordedAt,
        };
    }

    /// <summary>
    /// One park transfer. A licensee park's phase1Fees/phase2Fees are the statement's barrier fraction — the licensee's
    /// barrier phase, which is between the licensee and Swich (the park invoice leaves it out for the same reason) — so a
    /// park manager gets null for them; Swich and the operator see them. A Swich-direct park's split is its own contract
    /// with Swich and stays visible to it.
    /// </summary>
    public static object Row(Settlement s, string parkName, SettlementOptions opts, DateOnly todayLocal, TimeZoneInfo tz,
        IReadOnlyList<SettlementMath.FailedAttempt>? attempts, bool seesContract, string? sourceIban = null)
    {
        var showsSplit = seesContract || s.MonthlyStatementId is null;
        return new
        {
            id = s.Id,
            parkId = s.ParkId,
            parkName,
            isMonthly = s.IsMonthly,
            monthlyStatementId = s.MonthlyStatementId,
            month = MonthKey(s.SettlementDate),
            settlementDate = s.SettlementDate,
            periodFromUtc = s.PeriodFromUtc,
            periodToUtc = s.PeriodToUtc,
            cashoutCount = s.CashoutCount,
            feeTotal = s.FeeTotal,
            phase1Fees = showsSplit ? s.Phase1Fees : (decimal?)null,
            phase2Fees = showsSplit ? s.Phase2Fees : (decimal?)null,
            swichShare = s.SwichShare,
            parkShare = s.ParkShare,
            cumulativeFeesBefore = s.CumulativeFeesBefore,
            status = s.Status.ToString(),
            bankTransferId = s.BankTransferId,
            failureReason = s.FailureReason,
            failureCode = SettlementMath.FailureCode(s.FailureReason),
            attemptCount = s.AttemptCount,
            lastAttemptAt = s.LastAttemptAt,
            completedAt = s.CompletedAt,
            description = s.Description,
            invoiceRef = s.InvoiceRef,
            sourceIban,
            swichIban = s.SwichIban,
            initiatedBy = s.InitiatedBy,
            createdAt = s.CreatedAt,
            overdue = Overdue(s, attempts, opts, todayLocal, tz, seesContract),
            external = External(s, seesContract),
        };
    }
}
