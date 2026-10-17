using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Monthly settlement engine — the licence agreement's §2.2–2.5 (supersedes the nightly per-park
/// rule of PAYTAXI-CONTEXT.md §4).
///
/// LICENSEE parks, per licensee and local calendar month M:
///   1. F  = fees EARNED under the licensee's agreement (§2.2) — stamped with the licensee when the cashout completed,
///      or, for cashouts from before regimes were stamped, of a park assigned to it now — not yet settled, CompletedAt
///      before the end of M (a cashout missed by an earlier statement simply rolls into this one).
///      CL / CS = the licensee's shared costs not yet in a statement, invoice dated by the end of M, neither
///      deleted nor on hold, and entered at least Settlement:CostCutoffHours (48) before the statement is
///      computed (<see cref="SharedCostRules"/>). Anything else rolls forward the same way. A correction of an
///      already-included cost is signed, so a side's total may be negative in a month: the formulas are linear
///      in CL and CS and apply it exactly (see <see cref="SettlementMath"/>).
///   2. <see cref="SettlementMath.Compute"/> turns F, CL, CS, the carry from the previous statement
///      and the barrier progress R into Swich's entitlement E and the amount D collected now.
///   3. ONE <see cref="MonthlyStatement"/> is written together with one <see cref="Settlement"/> per
///      park (its pro-rata part of D) and every covered cashout and cost is stamped with it — in a
///      single SaveChanges, so the statement either exists completely or not at all.
///   4. Each park row is transferred from that park's primary account to Swich's IBAN with the
///      document id "settle:{statement}:{park}". Success → Completed; failure → Failed with the reason,
///      retried every night until it succeeds — or until Swich records that it was paid outside the
///      platform (§2.5: the licensee pays when the park cannot) → SettledExternally. Never a partial take.
///      Once the park has revoked the platform's authorisation (Annex 3 §7, <see cref="ParkAuthorisation"/>) nothing is
///      initiated from its account: the row fails AUTHORISATION_REVOKED, is never retried, and is closed only by the
///      outside payment.
///
/// Concurrency (the nightly tick, the Retry button and "Settle now" can meet on one row or month):
///   - a statement / direct row is created in ONE transaction, serialised per licensee / park by an advisory
///     lock, and it claims its cashouts and costs conditionally ("… WHERE SettlementId IS NULL"): two runs —
///     or a run and a cost edit — can never count the same fee or invoice twice, or freeze a stale amount;
///   - a transfer is executed only by whoever atomically claims the row (Pending/Failed → Processing), and the
///     row's Status is a concurrency token, so no executor ever writes over a state another one produced.
///
/// SWICH-DIRECT parks (no licensee) are settled monthly too, one row per park, with the park's own
/// phase/rate config (<see cref="ComputeShares"/>), no shared costs and no barrier — over the fees earned as a direct
/// park. A park moved between the two regimes in a month gets a row on each side for it, each over its own fees.
///
/// Barrier progress R counts what Swich was ENTITLED to from the licensee's earlier statements
/// since the launch date (ΣE), not what has arrived on its account. The agreement (§2.3) says
/// "amounts actually received"; counting entitlement means a month whose transfer failed stays a debt
/// at the rate of its own month — it is not charged a second time at 100 % because the barrier
/// still looks unfilled — and the failed transfer is retried (and penalised under §4.1.2) on its own.
/// The two readings differ only while a transfer is outstanding across a barrier month. This
/// interpretation must be confirmed with the lawyer.
/// </summary>
public class SettlementService : ISettlementService
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly SettlementOptions _opts;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        AppDbContext db,
        IServiceProvider services,
        IOptions<SettlementOptions> opts,
        ILogger<SettlementService> log)
    {
        _db = db;
        _services = services;
        _opts = opts.Value;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Nightly tick
    // ═══════════════════════════════════════════════════════════════════

    public async Task<SettlementRunSummary> RunDueAsync(DateOnly todayLocal, string initiatedBy, CancellationToken ct = default)
    {
        int completed = 0, failed = 0, retried = 0, skipped = 0, statementsCreated = 0, directCreated = 0;

        // Every step below is isolated per row/licensee/park: one bad row must not stop the others.

        // 0a. Transfers interrupted mid-flight (Processing, no bank id, untouched for a while):
        //     ask the bank by our document id before deciding anything.
        var interrupted = await _db.Settlements
            .Where(s => s.Status == SettlementStatus.Processing && s.BankTransferId == null
                     && s.LastAttemptAt != null && s.LastAttemptAt < DateTime.UtcNow.AddMinutes(-StaleProcessingMinutes))
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in interrupted)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await _db.Settlements.FirstAsync(x => x.Id == id, ct);
                await ResolveInterruptedAsync(s, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                Forget(id, "recovering an interrupted transfer");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Settlement {Id}: recovering interrupted transfer threw", id);
            }
        }

        // 0b. Transfers the bank accepted asynchronously last time: ask how they ended.
        var pending = await _db.Settlements
            .Where(s => s.Status == SettlementStatus.Processing && s.BankTransferId != null)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await _db.Settlements.FirstAsync(x => x.Id == id, ct);
                await ResolvePendingAsync(s, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                Forget(id, "resolving a pending transfer");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Settlement {Id}: resolving pending transfer threw", id);
            }
        }

        // 1. Earlier failures (any month) — retried every night until they succeed. Rows left Pending by a
        //    run that died between writing the statement and starting the transfer are picked up too
        //    (only once they are clearly abandoned, so a run in progress elsewhere is not doubled).
        //    Not a transfer the park's revoked authorisation stopped (Annex 3 §7): that one can never be initiated
        //    again — retrying would only re-record the refusal every night. It waits for the payment outside the
        //    platform (the licensee's under §2.5). A row that failed for another reason before the revocation is
        //    still picked up once, and ExecuteAsync turns it into that state without touching the bank.
        var staleBefore = DateTime.UtcNow.AddMinutes(-StaleProcessingMinutes);
        var revokedPrefix = ParkAuthorisation.RevokedCode + ":";
        var toRetry = await _db.Settlements
            .Where(s => (s.Status == SettlementStatus.Failed && (s.FailureReason == null || !s.FailureReason.StartsWith(revokedPrefix)))
                     || (s.Status == SettlementStatus.Pending && s.CreatedAt < staleBefore))
            .OrderBy(s => s.SettlementDate).ThenBy(s => s.CreatedAt)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in toRetry)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await RetryAsync(id, initiatedBy, ct);
                retried++;
                if (s.Status == SettlementStatus.Completed) completed++; else if (s.Status == SettlementStatus.Failed) failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Settlement {Id}: retry threw", id);
            }
        }

        // 2. The monthly run: from the run day on, settle the previous month (idempotent — once a
        //    statement/row exists it is not touched again here; its failures are step 1's job).
        DateOnly? monthSettled = null;
        if (todayLocal.Day >= Math.Clamp(_opts.RunDayOfMonth, 1, 28))
        {
            var month = new DateOnly(todayLocal.Year, todayLocal.Month, 1).AddMonths(-1);
            monthSettled = month;
            var result = await RunMonthCoreAsync(month, initiatedBy, licenseeId: null, parkId: null,
                executeExisting: false, throwOnReject: false, ct);
            statementsCreated = result.CreatedStatements;
            directCreated = result.CreatedDirect;
            skipped = result.Skipped.Count;
            completed += result.TransfersCompleted;
            failed += result.TransfersFailed;
        }

        var summary = new SettlementRunSummary(todayLocal, monthSettled, statementsCreated, directCreated, completed, failed, retried, skipped);
        _log.LogInformation("Settlement tick {Date}: {Summary}", todayLocal, summary);
        return summary;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Month runs
    // ═══════════════════════════════════════════════════════════════════

    public async Task<MonthRunResult> RunMonthAsync(DateOnly month, string initiatedBy, Guid? licenseeId = null, Guid? parkId = null, CancellationToken ct = default)
    {
        month = FirstOfMonth(month);
        var r = await RunMonthCoreAsync(month, initiatedBy, licenseeId, parkId, executeExisting: true, throwOnReject: true, ct);
        return new MonthRunResult(month, r.StatementIds, r.DirectIds, r.Skipped);
    }

    private sealed class MonthRunAccumulator
    {
        public List<Guid> StatementIds { get; } = new();
        public List<Guid> DirectIds { get; } = new();
        public List<MonthRunSkip> Skipped { get; } = new();
        public int CreatedStatements;
        public int CreatedDirect;
        public int TransfersCompleted;
        public int TransfersFailed;
    }

    /// <summary>
    /// <paramref name="throwOnReject"/>: an explicit single-target request (Swich's button for one
    /// licensee or park) surfaces the refusal; the all-parties run records it and carries on.
    /// </summary>
    private async Task<MonthRunAccumulator> RunMonthCoreAsync(
        DateOnly month, string initiatedBy, Guid? licenseeId, Guid? parkId, bool executeExisting, bool throwOnReject, CancellationToken ct)
    {
        var acc = new MonthRunAccumulator();
        var singleTarget = licenseeId is not null || parkId is not null;

        if (!IsMonthClosed(month) && !_opts.AllowOpenMonthRun)
            throw new SettlementRejectedException(SettlementRejectionCodes.MonthNotClosed,
                $"{month:yyyy-MM} has not ended yet — it is settled after the month closes");

        // ── Licensees ────────────────────────────────────────────────
        if (parkId is null)
        {
            var licensees = await _db.Licensees.AsNoTracking()
                .Where(l => licenseeId == null || l.Id == licenseeId)
                .Select(l => new { l.Id, l.Name })
                .ToListAsync(ct);
            if (licenseeId is not null && licensees.Count == 0)
                throw new SettlementRejectedException(SettlementRejectionCodes.LicenseeNotFound, "Licensee not found");

            foreach (var l in licensees)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (statement, created) = await RunLicenseeMonthAsync(l.Id, month, initiatedBy, executeExisting, ct);
                    acc.StatementIds.Add(statement.Id);
                    if (created) acc.CreatedStatements++;
                    var rows = await _db.Settlements.AsNoTracking()
                        .Where(s => s.MonthlyStatementId == statement.Id)
                        .Select(s => s.Status).ToListAsync(ct);
                    acc.TransfersCompleted += rows.Count(s => s == SettlementStatus.Completed);
                    acc.TransfersFailed += rows.Count(s => s == SettlementStatus.Failed);
                }
                catch (SettlementRejectedException ex) when (!(throwOnReject && singleTarget))
                {
                    acc.Skipped.Add(new MonthRunSkip("licensee", l.Id, l.Name, ex.Code));
                    LogSkip(ex, "licensee", l.Name, month);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not SettlementRejectedException)
                {
                    _db.ChangeTracker.Clear();
                    acc.Skipped.Add(new MonthRunSkip("licensee", l.Id, l.Name, "error"));
                    _log.LogError(ex, "Statement {Month} for licensee {Licensee} threw — continuing with the others", month, l.Name);
                }
            }
        }

        // ── Swich-direct parks ───────────────────────────────────────
        if (licenseeId is null)
        {
            List<(Guid Id, string Name)> parks;
            if (parkId is { } pid)
            {
                var p = await _db.Parks.AsNoTracking().Where(x => x.Id == pid)
                    .Select(x => new { x.Id, x.Name, x.LicenseeId }).FirstOrDefaultAsync(ct)
                    ?? throw new SettlementRejectedException(SettlementRejectionCodes.ParkNotFound, "Park not found");
                if (p.LicenseeId is not null && !await HasDirectFeesAsync(p.Id, ct))
                    throw new SettlementRejectedException(SettlementRejectionCodes.ParkHasLicensee,
                        "This park is settled as part of its licensee's monthly statement");
                parks = new() { (p.Id, p.Name) };
            }
            else
            {
                // Status deliberately ignored: a suspended or terminated park still owes the fees on
                // payouts it already made. Besides the parks that are Swich-direct now: any park with an unsettled fee it
                // EARNED as a direct park (it joined a licensee since) — that fee is settled under its own regime (§2.2).
                parks = (await _db.Parks.AsNoTracking()
                    .Where(x => x.LicenseeId == null
                             || x.Cashouts.Any(c => c.Status == CashoutStatus.Completed && c.SettlementId == null
                                                 && c.FeeRegimeStamped && c.FeeLicenseeId == null))
                    .Select(x => new { x.Id, x.Name }).ToListAsync(ct))
                    .Select(x => (x.Id, x.Name)).ToList();
            }

            foreach (var (id, name) in parks)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (row, created) = await RunDirectParkMonthAsync(id, month, initiatedBy, executeExisting, ct);
                    if (row is null)
                    {
                        acc.Skipped.Add(new MonthRunSkip("park", id, name, "nothing_to_settle"));
                        continue;
                    }
                    acc.DirectIds.Add(row.Id);
                    if (created) acc.CreatedDirect++;
                    if (row.Status == SettlementStatus.Completed) acc.TransfersCompleted++;
                    else if (row.Status == SettlementStatus.Failed) acc.TransfersFailed++;
                }
                catch (SettlementRejectedException ex) when (!(throwOnReject && singleTarget))
                {
                    acc.Skipped.Add(new MonthRunSkip("park", id, name, ex.Code));
                    LogSkip(ex, "park", name, month);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not SettlementRejectedException)
                {
                    _db.ChangeTracker.Clear();
                    acc.Skipped.Add(new MonthRunSkip("park", id, name, "error"));
                    _log.LogError(ex, "Settlement {Month} for direct park {Park} threw — continuing with the others", month, name);
                }
            }
        }

        _log.LogInformation(
            "Settlement month {Month}: {Statements} statement(s) ({Created} new), {Direct} direct park row(s) ({DirectCreated} new), {Skipped} skipped",
            month.ToString("yyyy-MM"), acc.StatementIds.Count, acc.CreatedStatements, acc.DirectIds.Count, acc.CreatedDirect, acc.Skipped.Count);
        return acc;
    }

    // ── Fee regimes (§2.2) ───────────────────────────────────────────
    // Every fee is settled under the arrangement it was EARNED under, stamped on the cashout when it completed
    // (Cashout.FeeRegimeStamped / FeeLicenseeId): moving a park between a licensee and Swich-direct leaves what it already
    // earned where it was earned. Cashouts completed before regimes were stamped carry no stamp; for them the park's
    // assignment at settlement time is the only information there is, and it decides.

    /// <summary>The fees a licensee's statement covers: stamped with the licensee, or unstamped of a park assigned to it now.</summary>
    private static Expression<Func<Cashout, bool>> EarnedUnderLicensee(Guid licenseeId) =>
        c => (c.FeeRegimeStamped && c.FeeLicenseeId == licenseeId) || (!c.FeeRegimeStamped && c.Park.LicenseeId == licenseeId);

    /// <summary>A park's fees settled on its own Swich-direct row: stamped direct, or unstamped while the park is direct now.</summary>
    private static Expression<Func<Cashout, bool>> EarnedAsDirect(Guid parkId, bool parkIsDirectNow) =>
        c => c.ParkId == parkId && ((c.FeeRegimeStamped && c.FeeLicenseeId == null) || (!c.FeeRegimeStamped && parkIsDirectNow));

    /// <summary>The park (now a licensee's) still has an unsettled fee it earned as a Swich-direct park.</summary>
    private Task<bool> HasDirectFeesAsync(Guid parkId, CancellationToken ct) =>
        _db.Cashouts.AsNoTracking().AnyAsync(c => c.ParkId == parkId && c.Status == CashoutStatus.Completed && c.SettlementId == null
                                               && c.FeeRegimeStamped && c.FeeLicenseeId == null, ct);

    private void LogSkip(SettlementRejectedException ex, string kind, string name, DateOnly month)
    {
        // A licensee without a launch date is worth a warning every night: its fees are piling up unsettled.
        if (ex.Code == SettlementRejectionCodes.LaunchDateNotSet)
            _log.LogWarning("Settlement {Month}: {Kind} {Name} skipped — {Reason}", month.ToString("yyyy-MM"), kind, name, ex.Message);
        else
            _log.LogInformation("Settlement {Month}: {Kind} {Name} skipped — {Code}", month.ToString("yyyy-MM"), kind, name, ex.Code);
    }

    // ── Licensee statement ───────────────────────────────────────────

    /// <summary>How often a statement is recomputed when something it read was changed or claimed while it was being written.</summary>
    private const int MaxComputeAttempts = 3;

    // Advisory-lock classes (the two-key form has its own key space; the payout saga uses the one-key form).
    private const int StatementLockClass = 0x5E77_0001;
    private const int DirectRowLockClass = 0x5E77_0002;

    private async Task<(MonthlyStatement Statement, bool Created)> RunLicenseeMonthAsync(
        Guid licenseeId, DateOnly month, string initiatedBy, bool executeExisting, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (statement, created) = await CreateStatementAsync(licenseeId, month, initiatedBy, ct);
            if (statement is not null)
            {
                // Idempotent per (licensee, month): an existing statement is never recomputed — only its
                // unfinished transfers are executed when asked to.
                if (created)
                    await ExecuteStatementAsync(statement.Id, initiatedBy, onlyStatuses: new[] { SettlementStatus.Pending }, ct);
                else if (executeExisting)
                    await ExecuteStatementAsync(statement.Id, initiatedBy, onlyStatuses: new[] { SettlementStatus.Pending, SettlementStatus.Failed }, ct);
                return (statement, created);
            }
            if (attempt >= MaxComputeAttempts)
                throw new SettlementRejectedException(SettlementRejectionCodes.ConcurrentChange,
                    "Fees or shared costs of this month kept changing while the statement was written — nothing was settled; run it again");
            _log.LogWarning("Statement {Month} for licensee {Licensee}: an input changed while it was written — computing it again",
                month.ToString("yyyy-MM"), licenseeId);
        }
    }

    /// <summary>
    /// Computes and writes the statement in ONE transaction, serialised per licensee by an advisory lock (so a run
    /// for another month of the same licensee waits, then sees this one). Returns the existing statement
    /// (Created = false) when the month is already settled, or (null, false) when something it read was changed
    /// or claimed by someone else before it could be frozen — then nothing was written and the caller computes again.
    /// </summary>
    private async Task<(MonthlyStatement? Statement, bool Created)> CreateStatementAsync(
        Guid licenseeId, DateOnly month, string initiatedBy, CancellationToken ct)
    {
        var existing = await _db.MonthlyStatements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.LicenseeId == licenseeId && m.Month == month, ct);
        if (existing is not null) return (existing, false);

        var licensee = await _db.Licensees.AsNoTracking().FirstOrDefaultAsync(l => l.Id == licenseeId, ct)
            ?? throw new SettlementRejectedException(SettlementRejectionCodes.LicenseeNotFound, "Licensee not found");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync(StatementLockClass, licenseeId, ct);

        // Under the lock: a concurrent run for this licensee may have written this month — or a later one, which
        // the blocked check below refuses (this month's fees went into it).
        existing = await _db.MonthlyStatements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.LicenseeId == licenseeId && m.Month == month, ct);
        if (existing is not null) return (existing, false);

        var blocked = await BlockedReasonAsync(licensee, month, ct);
        if (blocked is not null) throw new SettlementRejectedException(blocked.Value.Code, blocked.Value.Message);

        var comp = await ComputeLicenseeAsync(licensee, month, ct);
        var (fromUtc, toUtc) = MonthBoundsUtc(month);
        var monthLast = month.AddMonths(1).AddDays(-1);
        var invoiceRef = InvoiceRefFor(month);

        var statement = new MonthlyStatement
        {
            LicenseeId = licensee.Id,
            Month = month,
            PeriodFromUtc = fromUtc,
            PeriodToUtc = toUtc,
            CashoutCount = comp.Cashouts.Count,
            FeeIncome = comp.Inputs.FeeIncome,
            LicenseeCosts = comp.Inputs.LicenseeCosts,
            SwichCosts = comp.Inputs.SwichCosts,
            CarryIn = comp.Inputs.CarryIn,
            BarrierGel = comp.Inputs.BarrierGel,
            SwichSharePercent = comp.Inputs.SwichSharePercent,
            BarrierBefore = comp.Inputs.BarrierBefore,
            BarrierFilledBefore = comp.Inputs.BarrierFilledBefore,
            Phase = comp.Result.Phase,
            BarrierFraction = comp.Result.BarrierFraction,
            Entitlement = comp.Result.Entitlement,
            Due = comp.Result.Due,
            CarryOut = comp.Result.CarryOut,
            Status = MonthlyStatementStatus.Pending,
            ComputedAt = DateTime.UtcNow,
            InvoiceRef = invoiceRef,
            InitiatedBy = initiatedBy,
        };
        _db.MonthlyStatements.Add(statement);

        var claims = new List<(Guid RowId, List<Guid> CashoutIds)>();
        foreach (var a in comp.Allocations)
        {
            var parkCashouts = comp.Cashouts.Where(c => c.ParkId == a.ParkId).ToList();
            var cumulativeBefore = await _db.Settlements.Where(s => s.ParkId == a.ParkId)
                .SumAsync(s => (decimal?)s.FeeTotal, ct) ?? 0m;
            var row = new Settlement
            {
                ParkId = a.ParkId,
                SettlementDate = monthLast,
                IsMonthly = true,
                MonthlyStatementId = statement.Id,
                PeriodFromUtc = Min(fromUtc, parkCashouts.Min(c => c.CompletedAt!.Value)),
                PeriodToUtc = toUtc,
                CashoutCount = parkCashouts.Count,
                FeeTotal = a.Fees,
                Phase1Fees = a.BarrierFees,
                Phase2Fees = a.Fees - a.BarrierFees,
                SwichShare = a.SwichShare,
                ParkShare = a.Fees - a.SwichShare,
                CumulativeFeesBefore = cumulativeBefore,
                Status = SettlementStatus.Pending,
                InvoiceRef = invoiceRef,
                InitiatedBy = initiatedBy,
                IdempotencyKey = $"settle:{statement.Id:N}:{a.ParkId:N}",
                Description = $"PayTaxi settlement {month:yyyy-MM}, {parkCashouts.Count} tx, inv ref {invoiceRef}",
            };
            _db.Settlements.Add(row);
            claims.Add((row.Id, parkCashouts.Select(c => c.Id).ToList()));
        }

        // Nothing to transfer at all (no fees, or a credit/debt carried) — the statement is final as computed.
        if (comp.Allocations.Count == 0)
        {
            statement.Status = MonthlyStatementStatus.Completed;
            statement.ExecutedAt = statement.ComputedAt;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A run that did not take the lock wrote the same month first. Its statement is the one; ours never
            // existed — the transaction is rolled back with everything in it.
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            _log.LogWarning("Statement {Month} for licensee {Licensee} was created concurrently — using the existing one", month.ToString("yyyy-MM"), licensee.Name);
            var winner = await _db.MonthlyStatements.AsNoTracking().FirstAsync(m => m.LicenseeId == licenseeId && m.Month == month, ct);
            return (winner, false);
        }

        if (!await ClaimCashoutsAsync(claims, ct) || !await ClaimCostsAsync(statement.Id, comp.Costs, comp.CostsEnteredBy, ct))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return (null, false);
        }
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Statement {Ref} for licensee {Licensee}: {Count} cashouts, F {F} − CL {CL} / CS {CS}, carry-in {CarryIn}, R {R}{Filled} → {Phase} E {E} → due {Due}, carry-out {CarryOut}, {Parks} park transfer(s)",
            invoiceRef, licensee.Name, statement.CashoutCount, statement.FeeIncome, statement.LicenseeCosts, statement.SwichCosts,
            statement.CarryIn, statement.BarrierBefore, statement.BarrierFilledBefore ? " (barrier already filled)" : "",
            statement.Phase, statement.Entitlement, statement.Due, statement.CarryOut, comp.Allocations.Count);
        return (statement, true);
    }

    /// <summary>
    /// Stamps the computed cashouts with their new rows — only those still unclaimed and completed
    /// ("… WHERE SettlementId IS NULL"). A run for another month, a direct-park run after a reassignment, or anything
    /// else that claimed one of them since it was read makes the count fall short: false, and the caller rolls back.
    /// Two statements can therefore never both count one fee.
    /// </summary>
    private async Task<bool> ClaimCashoutsAsync(IEnumerable<(Guid RowId, List<Guid> CashoutIds)> claims, CancellationToken ct)
    {
        foreach (var (rowId, ids) in claims)
        {
            var stamped = await _db.Cashouts
                .Where(c => ids.Contains(c.Id) && c.SettlementId == null && c.Status == CashoutStatus.Completed)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.SettlementId, (Guid?)rowId), ct);
            if (stamped != ids.Count)
            {
                _log.LogWarning("Settlement row {Row}: {Stamped} of {Count} cashouts could still be claimed — another run took the rest", rowId, stamped, ids.Count);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Freezes the shared costs the statement was computed from — only those still countable (not yet included, not
    /// deleted, not on hold, entered by <paramref name="enteredBy"/>), and only as they were read: once our UPDATE holds
    /// them, they are read back and compared. An invoice edited, deleted or held between the statement's read and its
    /// write (the operator correcting 500 to 50 at 00:30, Swich disputing it) would otherwise be frozen with an amount the
    /// money was not computed from, or despite the dispute — false, and the caller computes again. An edit restarts the
    /// entry's window (EnteredAt), so the recomputation leaves an invoice changed at the last minute for the next one.
    /// (Edits, deletes and holds are themselves conditional on "not yet included", so once frozen it can no longer change.)
    /// </summary>
    private async Task<bool> ClaimCostsAsync(Guid statementId, List<CommonCost> costs, DateTime enteredBy, CancellationToken ct)
    {
        if (costs.Count == 0) return true;
        var ids = costs.Select(c => c.Id).ToList();
        var stamped = await _db.CommonCosts
            .Where(c => ids.Contains(c.Id) && c.StatementId == null && c.DeletedAt == null && c.HeldAt == null && c.EnteredAt <= enteredBy)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.StatementId, (Guid?)statementId), ct);
        if (stamped != costs.Count) return false;

        var frozen = await _db.CommonCosts.AsNoTracking()
            .Where(c => c.StatementId == statementId)
            .Select(c => new { c.Id, c.Amount, c.PaidBy, c.InvoiceDate, c.LicenseeId, c.EnteredAt })
            .ToListAsync(ct);
        var read = costs.ToDictionary(c => c.Id);
        var unchanged = frozen.Count == costs.Count && frozen.All(f =>
            read.TryGetValue(f.Id, out var c)
            && c.Amount == f.Amount && c.PaidBy == f.PaidBy && c.InvoiceDate == f.InvoiceDate && c.LicenseeId == f.LicenseeId
            && c.EnteredAt == f.EnteredAt);
        if (!unchanged)
            _log.LogWarning("Statement {Statement}: a shared cost changed after it was read — not frozen", statementId);
        return unchanged;
    }

    /// <summary>Transaction-scoped advisory lock on (class, id) — released by the commit or rollback.</summary>
    private Task LockAsync(int lockClass, Guid id, CancellationToken ct)
    {
        var b = id.ToByteArray();
        var key = BitConverter.ToInt32(b, 0) ^ BitConverter.ToInt32(b, 4) ^ BitConverter.ToInt32(b, 8) ^ BitConverter.ToInt32(b, 12);
        return _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockClass}, {key})", ct);
    }

    private async Task ExecuteStatementAsync(Guid statementId, string initiatedBy, SettlementStatus[] onlyStatuses, CancellationToken ct)
    {
        var rowIds = await _db.Settlements
            .Where(s => s.MonthlyStatementId == statementId && onlyStatuses.Contains(s.Status))
            .OrderByDescending(s => s.SwichShare).ThenBy(s => s.ParkId)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in rowIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The id list is a snapshot: by the time a row's turn comes another executor may have taken it.
                // ExecuteAsync claims it atomically and leaves it alone when it is no longer Pending/Failed.
                var row = await _db.Settlements.FirstAsync(s => s.Id == id, ct);
                await ExecuteAsync(row, initiatedBy, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                Forget(id, $"executing statement {statementId}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One park's account must not stop the others' transfers; the row stays as it is and
                // the nightly retry (or recovery of a Processing row) picks it up.
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Settlement {Id} of statement {Statement} threw — continuing with the other parks", id, statementId);
            }
        }
        await RefreshStatementAsync(statementId, ct);
    }

    /// <summary>
    /// The statement's status is the sum of its park transfers: all Completed (or paid outside the platform)
    /// → Completed, any Failed → Failed.
    /// </summary>
    private async Task RefreshStatementAsync(Guid statementId, CancellationToken ct)
    {
        var st = await _db.MonthlyStatements.FirstOrDefaultAsync(m => m.Id == statementId, ct);
        if (st is null) return;
        var rows = await _db.Settlements.AsNoTracking()
            .Where(s => s.MonthlyStatementId == statementId)
            .Select(s => new { s.Status, s.CompletedAt })
            .ToListAsync(ct);

        var status = rows.Count == 0 || rows.All(r => r.Status is SettlementStatus.Completed or SettlementStatus.SettledExternally)
            ? MonthlyStatementStatus.Completed
            : rows.Any(r => r.Status == SettlementStatus.Failed) ? MonthlyStatementStatus.Failed : MonthlyStatementStatus.Pending;
        if (st.Status == status) return;

        st.Status = status;
        st.ExecutedAt = status == MonthlyStatementStatus.Completed
            ? rows.Select(r => r.CompletedAt).Max() ?? st.ComputedAt
            : null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>A save lost to another executor: drop what this context holds and let the row be re-read.</summary>
    private void Forget(Guid settlementId, string what)
    {
        _db.ChangeTracker.Clear();
        _log.LogInformation("Settlement {Id}: another executor moved the row while {What} — left to it", settlementId, what);
    }

    // ── Licensee computation (shared by run and preview) ─────────────

    private sealed record LicenseeComputation(
        SettlementMath.StatementInputs Inputs,
        SettlementMath.StatementResult Result,
        List<Cashout> Cashouts,
        List<CommonCost> Costs,
        // Costs entered after this moment were too recent to count (Settlement:CostCutoffHours before the computation).
        DateTime CostsEnteredBy,
        List<ParkAllocation> Allocations);

    /// <summary>Everything a statement for <paramref name="month"/> would freeze if computed now (read-only).</summary>
    private async Task<LicenseeComputation> ComputeLicenseeAsync(Licensee licensee, DateOnly month, CancellationToken ct)
    {
        var (_, toUtc) = MonthBoundsUtc(month);
        var monthLast = month.AddMonths(1).AddDays(-1);
        var enteredBy = SharedCostRules.EnteredBy(DateTime.UtcNow, _opts.CostCutoffHours);

        // The fees EARNED under this agreement (§2.2), whichever regime the park is in today: a park that left the
        // licensee keeps its earlier fees here, one that joined it keeps its direct fees on its own row. A park moved
        // in the middle of a month therefore has two rows for it — this statement's and its direct one — each over
        // its own fees; the conditional claim below keeps any fee from being counted by both.
        var cashouts = await _db.Cashouts.AsNoTracking()
            .Where(c => c.Status == CashoutStatus.Completed
                     && c.SettlementId == null
                     && c.CompletedAt != null
                     && c.CompletedAt < toUtc)
            .Where(EarnedUnderLicensee(licensee.Id))
            .OrderBy(c => c.CompletedAt)
            .ToListAsync(ct);
        var parkIds = cashouts.Select(c => c.ParkId).Distinct().ToList();
        var parks = await _db.Parks.AsNoTracking()
            .Where(p => parkIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        // The same rule as SharedCostRules.Countable, as a query: deleted and held entries are never counted, and an entry
        // younger than the cut-off waits for the next statement (the other side's window to see and dispute it).
        var costs = await _db.CommonCosts.AsNoTracking()
            .Where(c => c.LicenseeId == licensee.Id && c.StatementId == null && c.InvoiceDate <= monthLast
                     && c.DeletedAt == null && c.HeldAt == null && c.EnteredAt <= enteredBy)
            .OrderBy(c => c.InvoiceDate).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var earlier = await _db.MonthlyStatements.AsNoTracking()
            .Where(m => m.LicenseeId == licensee.Id && m.Month < month)
            .OrderBy(m => m.Month)
            .Select(m => new BarrierStep(m.Month, m.Entitlement, m.Phase, m.BarrierFilledBefore, m.CarryOut))
            .ToListAsync(ct);
        var sinceLaunch = SinceLaunch(earlier, licensee.LaunchDate);
        var barrierBefore = sinceLaunch.Sum(m => m.Entitlement);
        var carryIn = earlier.Count > 0 ? earlier[^1].CarryOut : 0m;

        var inputs = new SettlementMath.StatementInputs(
            FeeIncome: cashouts.Sum(c => c.Fee),
            LicenseeCosts: costs.Where(c => c.PaidBy == CostPaidBy.Licensee).Sum(c => c.Amount),
            SwichCosts: costs.Where(c => c.PaidBy == CostPaidBy.Swich).Sum(c => c.Amount),
            CarryIn: carryIn,
            BarrierGel: licensee.BarrierGel,
            BarrierBefore: barrierBefore,
            SwichSharePercent: licensee.SwichSharePercent,
            BarrierFilledBefore: BarrierReached(sinceLaunch, licensee.BarrierGel));
        var result = SettlementMath.Compute(inputs);

        var perPark = cashouts.GroupBy(c => c.ParkId)
            .Select(g => (ParkId: g.Key, Fees: g.Sum(c => c.Fee)))
            .ToList();
        var shares = SettlementMath.Allocate(result.Due, perPark);
        var allocations = perPark
            .Select(p => new ParkAllocation(
                ParkId: p.ParkId,
                ParkName: parks.First(x => x.Id == p.ParkId).Name,
                CashoutCount: cashouts.Count(c => c.ParkId == p.ParkId),
                Fees: p.Fees,
                BarrierFees: SettlementMath.Round2(p.Fees * result.BarrierFraction),
                SwichShare: shares[p.ParkId]))
            .OrderByDescending(a => a.Fees).ThenBy(a => a.ParkName)
            .ToList();

        return new LicenseeComputation(inputs, result, cashouts, costs, enteredBy, allocations);
    }

    /// <summary>One earlier statement as the barrier sees it (oldest first).</summary>
    public sealed record BarrierStep(DateOnly Month, decimal Entitlement, StatementPhase Phase, bool FilledBefore, decimal CarryOut = 0m);

    /// <summary>The statements the barrier counts: those from the launch month on (all of them while no launch date is set).</summary>
    public static List<BarrierStep> SinceLaunch(IEnumerable<BarrierStep> statements, DateOnly? launchDate)
    {
        var launchMonth = launchDate is { } ld ? FirstOfMonth(ld) : (DateOnly?)null;
        return statements.Where(m => launchMonth == null || m.Month >= launchMonth).OrderBy(m => m.Month).ToList();
    }

    /// <summary>
    /// §2.3 is a one-way switch: the barrier counts as filled from the first statement since the launch that reached
    /// it — one computed past it (Crossing / Normal, or already marked filled), or one whose running total of
    /// entitlement touched B (a Barrier month ending exactly on it). A later loss month lowers R (its credit is
    /// carried) but never re-opens the barrier.
    /// </summary>
    public static bool BarrierReached(IEnumerable<BarrierStep> sinceLaunchOldestFirst, decimal barrierGel)
    {
        var running = 0m;
        foreach (var m in sinceLaunchOldestFirst)
        {
            running += m.Entitlement;
            if (m.Phase != StatementPhase.Barrier || m.FilledBefore || running >= barrierGel) return true;
        }
        return false;
    }

    /// <summary>Why a statement for this month cannot be computed now, or null when it can.</summary>
    private async Task<(string Code, string Message)?> BlockedReasonAsync(Licensee licensee, DateOnly month, CancellationToken ct)
    {
        if (!IsMonthClosed(month) && !_opts.AllowOpenMonthRun)
            return (SettlementRejectionCodes.MonthNotClosed, $"{month:yyyy-MM} has not ended yet");
        if (licensee.LaunchDate is null)
            return (SettlementRejectionCodes.LaunchDateNotSet, $"Licensee {licensee.Name} has no launch date — the agreement's settlement has not started; fees accumulate unsettled");
        if (month < FirstOfMonth(licensee.LaunchDate.Value))
            return (SettlementRejectionCodes.BeforeLaunch, $"{month:yyyy-MM} is before the launch date {licensee.LaunchDate:yyyy-MM-dd}");
        var later = await _db.MonthlyStatements.AsNoTracking()
            .Where(m => m.LicenseeId == licensee.Id && m.Month > month)
            .OrderBy(m => m.Month).Select(m => m.InvoiceRef).FirstOrDefaultAsync(ct);
        if (later is not null)
            return (SettlementRejectionCodes.LaterStatementExists, $"{later} is already settled; this month's fees and costs rolled into it");
        return null;
    }

    public async Task<StatementPreview> PreviewLicenseeMonthAsync(Guid licenseeId, DateOnly month, CancellationToken ct = default)
    {
        month = FirstOfMonth(month);
        var licensee = await _db.Licensees.AsNoTracking().FirstOrDefaultAsync(l => l.Id == licenseeId, ct)
            ?? throw new SettlementRejectedException(SettlementRejectionCodes.LicenseeNotFound, "Licensee not found");
        var (fromUtc, toUtc) = MonthBoundsUtc(month);

        // Already settled → its frozen figures, never a recomputation.
        var stored = await _db.MonthlyStatements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.LicenseeId == licenseeId && m.Month == month, ct);
        if (stored is not null)
        {
            var mathOfStored = SettlementMath.Compute(new SettlementMath.StatementInputs(
                stored.FeeIncome, stored.LicenseeCosts, stored.SwichCosts, stored.CarryIn,
                stored.BarrierGel, stored.BarrierBefore, stored.SwichSharePercent, stored.BarrierFilledBefore));
            var rows = await _db.Settlements.AsNoTracking()
                .Where(s => s.MonthlyStatementId == stored.Id)
                .Select(s => new ParkAllocation(s.ParkId, s.Park.Name, s.CashoutCount, s.FeeTotal, s.Phase1Fees, s.SwichShare))
                .ToListAsync(ct);
            var costIds = await _db.CommonCosts.AsNoTracking().Where(c => c.StatementId == stored.Id)
                .OrderBy(c => c.InvoiceDate).Select(c => c.Id).ToListAsync(ct);
            return new StatementPreview(
                licenseeId, month, stored.PeriodFromUtc, stored.PeriodToUtc, stored.CashoutCount,
                stored.FeeIncome, stored.LicenseeCosts, stored.SwichCosts, stored.CarryIn,
                stored.BarrierGel, stored.SwichSharePercent, stored.BarrierBefore, stored.BarrierFilledBefore,
                stored.Phase, stored.BarrierFraction, mathOfStored.BarrierEntitlement, mathOfStored.NormalEntitlement,
                stored.Entitlement, stored.Due, stored.CarryOut,
                rows.OrderByDescending(r => r.Fees).ThenBy(r => r.ParkName).ToList(), costIds,
                BlockedReason: null, StatementId: stored.Id);
        }

        var comp = await ComputeLicenseeAsync(licensee, month, ct);
        var blocked = await BlockedReasonAsync(licensee, month, ct);
        return new StatementPreview(
            licenseeId, month, fromUtc, toUtc, comp.Cashouts.Count,
            comp.Inputs.FeeIncome, comp.Inputs.LicenseeCosts, comp.Inputs.SwichCosts, comp.Inputs.CarryIn,
            comp.Inputs.BarrierGel, comp.Inputs.SwichSharePercent, comp.Inputs.BarrierBefore, comp.Inputs.BarrierFilledBefore,
            comp.Result.Phase, comp.Result.BarrierFraction, comp.Result.BarrierEntitlement, comp.Result.NormalEntitlement,
            comp.Result.Entitlement, comp.Result.Due, comp.Result.CarryOut,
            comp.Allocations, comp.Costs.Select(c => c.Id).ToList(),
            BlockedReason: blocked?.Code, StatementId: null);
    }

    // ── Swich-direct parks ───────────────────────────────────────────

    private async Task<(Settlement? Row, bool Created)> RunDirectParkMonthAsync(
        Guid parkId, DateOnly month, string initiatedBy, bool executeExisting, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (row, created, conflict) = await CreateDirectRowAsync(parkId, month, initiatedBy, ct);
            if (!conflict)
            {
                if (row is not null && (created || (executeExisting && row.Status is SettlementStatus.Pending or SettlementStatus.Failed)))
                    row = await ExecuteAsync(row, initiatedBy, ct);
                return (row, created);
            }
            if (attempt >= MaxComputeAttempts)
                throw new SettlementRejectedException(SettlementRejectionCodes.ConcurrentChange,
                    "The park's fees kept changing while its settlement was written — nothing was settled; run it again");
            _log.LogWarning("Direct settlement {Month} for park {Park}: a cashout was claimed while it was written — computing it again",
                month.ToString("yyyy-MM"), parkId);
        }
    }

    /// <summary>
    /// The direct park's row for the month, created in one transaction under a per-park advisory lock, its cashouts
    /// claimed conditionally (a licensee statement can never count them too). It covers the fees the park earned as a
    /// Swich-direct park (<see cref="EarnedAsDirect"/>) — also for a park that has joined a licensee since; that park's
    /// part of the licensee's statement for the same month is a separate row. Row null = nothing to settle; Conflict =
    /// a cashout was claimed by someone else meanwhile, nothing written.
    /// </summary>
    private async Task<(Settlement? Row, bool Created, bool Conflict)> CreateDirectRowAsync(
        Guid parkId, DateOnly month, string initiatedBy, CancellationToken ct)
    {
        var monthLast = month.AddMonths(1).AddDays(-1);

        // Idempotent per (park, month) — the direct row only: the park's row in a licensee statement is another one.
        var existing = await _db.Settlements
            .FirstOrDefaultAsync(s => s.ParkId == parkId && s.IsMonthly && s.SettlementDate == monthLast && s.MonthlyStatementId == null, ct);
        if (existing is not null) return (existing, false, false);

        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct)
            ?? throw new SettlementRejectedException(SettlementRejectionCodes.ParkNotFound, "Park not found");
        if (park.LicenseeId is not null && !await HasDirectFeesAsync(parkId, ct))
            throw new SettlementRejectedException(SettlementRejectionCodes.ParkHasLicensee,
                "This park is settled as part of its licensee's monthly statement");
        if (!IsMonthClosed(month) && !_opts.AllowOpenMonthRun)
            throw new SettlementRejectedException(SettlementRejectionCodes.MonthNotClosed, $"{month:yyyy-MM} has not ended yet");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync(DirectRowLockClass, parkId, ct);
        existing = await _db.Settlements
            .FirstOrDefaultAsync(s => s.ParkId == parkId && s.IsMonthly && s.SettlementDate == monthLast && s.MonthlyStatementId == null, ct);
        if (existing is not null) return (existing, false, false);

        var (fromUtc, toUtc) = MonthBoundsUtc(month);
        var cashouts = await _db.Cashouts.AsNoTracking()
            .Where(c => c.Status == CashoutStatus.Completed
                     && c.SettlementId == null
                     && c.CompletedAt != null
                     && c.CompletedAt < toUtc)
            .Where(EarnedAsDirect(parkId, park.LicenseeId is null))
            .OrderBy(c => c.CompletedAt)
            .ToListAsync(ct);
        if (cashouts.Count == 0) return (null, false, false);

        var cumulativeBefore = await _db.Settlements.Where(s => s.ParkId == parkId)
            .SumAsync(s => (decimal?)s.FeeTotal, ct) ?? 0m;
        var split = ComputeShares(park, cashouts, cumulativeBefore);
        var invoiceRef = InvoiceRefFor(month);

        var row = new Settlement
        {
            ParkId = parkId,
            SettlementDate = monthLast,
            IsMonthly = true,
            MonthlyStatementId = null,
            PeriodFromUtc = Min(fromUtc, cashouts[0].CompletedAt!.Value),
            PeriodToUtc = toUtc,
            CashoutCount = cashouts.Count,
            FeeTotal = split.FeeTotal,
            Phase1Fees = split.Phase1Fees,
            Phase2Fees = split.Phase2Fees,
            SwichShare = split.SwichShare,
            ParkShare = split.FeeTotal - split.SwichShare,
            CumulativeFeesBefore = cumulativeBefore,
            Status = SettlementStatus.Pending,
            InvoiceRef = invoiceRef,
            InitiatedBy = initiatedBy,
            IdempotencyKey = $"settle:{parkId:N}:{month:yyyy-MM}",
            Description = $"PayTaxi settlement {month:yyyy-MM}, {cashouts.Count} tx, inv ref {invoiceRef}",
        };
        _db.Settlements.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            _log.LogWarning("Direct settlement {Month} for park {Park} was created concurrently — using the existing one", month.ToString("yyyy-MM"), park.Name);
            var winner = await _db.Settlements.FirstAsync(s => s.ParkId == parkId && s.IsMonthly && s.SettlementDate == monthLast && s.MonthlyStatementId == null, ct);
            return (winner, false, false);
        }

        if (!await ClaimCashoutsAsync(new[] { (row.Id, cashouts.Select(c => c.Id).ToList()) }, ct))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return (null, false, true);
        }
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Direct settlement {Month} for park {Park}: {Count} cashouts, fees {Fees} → Swich {Swich} (phase1 {P1} / phase2 {P2}, cumulative before {Cum})",
            month.ToString("yyyy-MM"), park.Name, cashouts.Count, split.FeeTotal, split.SwichShare, split.Phase1Fees, split.Phase2Fees, cumulativeBefore);
        return (row, true, false);
    }

    public async Task<DirectParkPreview?> PreviewDirectParkMonthAsync(Guid parkId, DateOnly month, CancellationToken ct = default)
    {
        month = FirstOfMonth(month);
        var monthLast = month.AddMonths(1).AddDays(-1);
        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct)
            ?? throw new SettlementRejectedException(SettlementRejectionCodes.ParkNotFound, "Park not found");

        var stored = await _db.Settlements.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ParkId == parkId && s.IsMonthly && s.SettlementDate == monthLast && s.MonthlyStatementId == null, ct);
        if (stored is not null)
            return new DirectParkPreview(parkId, park.Name, month, stored.CashoutCount, stored.FeeTotal,
                stored.Phase1Fees, stored.Phase2Fees, stored.SwichShare, stored.CumulativeFeesBefore, BlockedReason: null);

        var (_, toUtc) = MonthBoundsUtc(month);
        var cashouts = await _db.Cashouts.AsNoTracking()
            .Where(c => c.Status == CashoutStatus.Completed && c.SettlementId == null
                     && c.CompletedAt != null && c.CompletedAt < toUtc)
            .Where(EarnedAsDirect(parkId, park.LicenseeId is null))
            .OrderBy(c => c.CompletedAt)
            .ToListAsync(ct);
        // Nothing earned as a direct park (a licensee park's fees are in its licensee's statement): no direct row.
        if (cashouts.Count == 0) return null;

        var cumulativeBefore = await _db.Settlements.AsNoTracking().Where(s => s.ParkId == parkId)
            .SumAsync(s => (decimal?)s.FeeTotal, ct) ?? 0m;
        var split = ComputeShares(park, cashouts, cumulativeBefore);
        string? blocked = !IsMonthClosed(month) && !_opts.AllowOpenMonthRun ? SettlementRejectionCodes.MonthNotClosed : null;
        return new DirectParkPreview(parkId, park.Name, month, cashouts.Count, split.FeeTotal,
            split.Phase1Fees, split.Phase2Fees, split.SwichShare, cumulativeBefore, blocked);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Per-park transfer: execute / retry / recover
    // ═══════════════════════════════════════════════════════════════════

    public async Task<Settlement> RetryAsync(Guid settlementId, string initiatedBy, CancellationToken ct = default)
    {
        var s = await _db.Settlements.FirstOrDefaultAsync(x => x.Id == settlementId, ct)
            ?? throw new InvalidOperationException($"Settlement {settlementId} not found");
        if (s.Status == SettlementStatus.Completed)
            throw new InvalidOperationException("Settlement is already completed");
        if (s.Status == SettlementStatus.SettledExternally)
            throw new InvalidOperationException("Settlement was paid outside the platform — it is never collected again");
        if (s.Status == SettlementStatus.Failed && ParkAuthorisation.IsRevokedReason(s.FailureReason))
            throw new SettlementRejectedException(SettlementRejectionCodes.AuthorisationRevoked,
                "The park revoked the platform's authorisation to transfer from its account (Annex 3 §7) — this share is never collected " +
                "from it; record its payment as paid outside the platform (§2.5: the licensee answers for it)");
        try
        {
            if (s.Status == SettlementStatus.Processing && s.BankTransferId is not null)
                return await ResolvePendingAsync(s, ct);
            if (s.Status == SettlementStatus.Processing)
            {
                var stuckFor = DateTime.UtcNow - (s.LastAttemptAt ?? s.UpdatedAt);
                if (stuckFor < TimeSpan.FromMinutes(StaleProcessingMinutes))
                    throw new InvalidOperationException("Settlement is currently processing");
                return await ResolveInterruptedAsync(s, ct);
            }
            return await ExecuteAsync(s, initiatedBy, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another executor (the nightly tick, a second click) moved the row first: its result stands.
            Forget(settlementId, "retrying");
            return await _db.Settlements.FirstAsync(x => x.Id == settlementId, ct);
        }
    }

    /// <summary>A Processing settlement with no bank id, untouched for a while, is treated as retryable this long after its last attempt.</summary>
    private const int StaleProcessingMinutes = 10;

    /// <summary>
    /// The last attempt ended without an answer (the send threw and the lookup could not tell, or the run died):
    /// the transfer may have executed. The next attempt then skips the balance pre-check and repeats the same
    /// document — the bank's de-duplication reveals an order that went through — instead of refusing on a balance
    /// that the transfer itself may have emptied (and leaving a paid row Failed, blamed on the park).
    /// </summary>
    private static readonly HashSet<string> OutcomeUnknownCodes = new(StringComparer.Ordinal) { "BANK_EXCEPTION", "SAGA_INTERRUPTED" };

    /// <summary>The park account a row's transfer goes (or went) out from, and the adapter that talks to its bank.</summary>
    private async Task<(Park Park, ParkBankAccount? Account, IBankPayoutAdapter? Bank, BankAccountContext? Source)> RouteAsync(Settlement s, CancellationToken ct)
    {
        var park = await _db.Parks.AsNoTracking().Include(p => p.BankAccounts).FirstAsync(p => p.Id == s.ParkId, ct);
        var account = park.BankAccounts.FirstOrDefault(a => a.Id == s.ParkBankAccountId)
                   ?? park.BankAccounts.FirstOrDefault(a => a.IsPrimary);
        if (account is null) return (park, null, null, null);
        // Null when this server has no adapter for the provider — never the mock as a stand-in (see ExecuteAsync).
        var bank = ResolveAdapter(account.Provider);
        var source = new BankAccountContext(park.Id, account.Id, account.Provider, account.BankCode, account.Iban,
            account.HolderName ?? park.LegalEntityName ?? park.Name, account.CredentialsEncrypted);
        return (park, account, bank, source);
    }

    /// <summary>
    /// The process died between "Processing" and storing the bank's answer. The bank may or
    /// may not hold our document: look it up by idempotency key. Found and alive → adopt it;
    /// definitively refused → the next attempt is a new document; not found → mark Failed so the
    /// normal retry path sends it (same key, so a late duplicate is impossible).
    /// </summary>
    private async Task<Settlement> ResolveInterruptedAsync(Settlement s, CancellationToken ct)
    {
        var (_, _, bank, source) = await RouteAsync(s, ct);

        BankTransferLookup? lookup = null;
        if (bank is not null && source is not null)
        {
            try { lookup = await bank.FindTransferByIdempotencyKeyAsync(source, DocumentKey(s), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Settlement {Id}: lookup by document id failed; leaving it for the next run", s.Id);
                return s;
            }
        }

        if (lookup is not null && lookup.Status == BankTransferStatus.Completed)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.BankTransferId = lookup.TransferId;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, s.SwichShare, lookup.TransferId,
                $"{s.Description} · recovered after interrupted run (bank confirms completed)"));
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Settlement {Id}: interrupted run recovered — bank shows transfer {Tx} completed", s.Id, lookup.TransferId);
            await RefreshStatementOfAsync(s, ct);
            return s;
        }
        if (lookup is not null && lookup.Status is BankTransferStatus.Pending or BankTransferStatus.Unknown)
        {
            // Pending: accepted, still executing. Unknown: the bank holds the order but reports a status the
            // adapter cannot map — it may still execute. Either way the order is adopted and polled (step 0b)
            // until it is final; a new document now could make the park pay twice.
            s.BankTransferId = lookup.TransferId;
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Settlement {Id}: interrupted run recovered — bank holds transfer {Tx} ({Status}); polling it",
                s.Id, lookup.TransferId, lookup.Status);
            return s;
        }

        // Retire only a document the bank has definitively refused; with nothing found the same id goes again.
        if (lookup is not null) RetireDocument(s, lookup.TransferId);
        await FailAsync(s, "SAGA_INTERRUPTED",
            lookup is not null ? $"Run was interrupted; bank reports transfer {lookup.TransferId} failed — will retry as a new document"
            : bank is null && source is not null ? "Run was interrupted before the bank answered, and this server has no adapter for the account's provider to ask — the same document goes again once it does"
            : "Run was interrupted before the bank answered; no document found at the bank — will retry", ct);
        return s;
    }

    /// <summary>Poll a bank-accepted settlement transfer until it is final (an Unknown status keeps it waiting).</summary>
    private async Task<Settlement> ResolvePendingAsync(Settlement s, CancellationToken ct)
    {
        var (_, _, bank, source) = await RouteAsync(s, ct);
        if (bank is null || source is null) return s;

        BankTransferOutcome outcome;
        try { outcome = await bank.GetTransferOutcomeAsync(source, s.BankTransferId!, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Settlement {Id}: status poll failed for transfer {Tx}", s.Id, s.BankTransferId);
            return s;
        }
        var status = outcome.Status;

        if (status == BankTransferStatus.Completed)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, s.SwichShare, s.BankTransferId,
                $"{s.Description} · confirmed by bank after asynchronous execution"));
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Settlement {Id} confirmed by bank: transfer {Tx}", s.Id, s.BankTransferId);
            await RefreshStatementOfAsync(s, ct);
        }
        else if (status == BankTransferStatus.Failed)
        {
            var dead = s.BankTransferId;
            RetireDocument(s, dead);
            await FailAsync(s, outcome.ErrorCode ?? "BANK_REPORTED_FAILED",
                $"Bank refused transfer {dead}{(outcome.ErrorMessage is { } why ? $" ({why})" : "")} — the retry goes out as a new document", ct);
        }
        return s;
    }

    private async Task<Settlement> ExecuteAsync(Settlement s, string initiatedBy, CancellationToken ct)
    {
        // Atomic claim, the same pattern as the payout queue: only the executor whose UPDATE moves the row from
        // Pending/Failed to Processing runs the transfer. One that loaded the row a moment earlier — the nightly
        // tick next to the Retry button, "Settle now" next to the run that just created the row, an id snapshot
        // taken before another executor finished it — finds nothing to claim and leaves the row alone, instead of
        // overwriting a completed transfer with a failed balance check or booking its SettlementSent twice.
        var now = DateTime.UtcNow;
        var claimed = await _db.Settlements
            .Where(x => x.Id == s.Id && (x.Status == SettlementStatus.Pending || x.Status == SettlementStatus.Failed))
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SettlementStatus.Processing)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.LastAttemptAt, (DateTime?)now)
                .SetProperty(x => x.SwichIban, _opts.SwichIban)
                .SetProperty(x => x.UpdatedAt, now), ct);
        await _db.Entry(s).ReloadAsync(ct);
        if (claimed == 0)
        {
            _log.LogInformation("Settlement {Id} not executed: it is {Status} — another executor has it", s.Id, s.Status);
            return s;
        }
        var previousOutcomeUnknown = SettlementMath.FailureCode(s.FailureReason) is { } last && OutcomeUnknownCodes.Contains(last);

        var park = await _db.Parks.AsNoTracking()
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstAsync(p => p.Id == s.ParkId, ct);

        // Nothing to move (a 0 % share, or a park whose part of the due amount is nil) — still a
        // completed settlement for the records, and its cashouts stay covered.
        if (s.SwichShare < _opts.MinTransferGel)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, 0m, null,
                $"Below minimum transfer ({_opts.MinTransferGel:F2} GEL) — nothing sent"));
            await _db.SaveChangesAsync(ct);
            await RefreshStatementOfAsync(s, ct);
            return s;
        }

        // Annex 3 §7: from the revocation day the platform has no authority to take anything from the park's account —
        // whichever month the share is for (the obligation arose before and stays). Nothing is sent, and the row is not
        // retried by the nightly run or the Retry button (ParkAuthorisation.RevokedCode): it is owed outside the platform —
        // by the licensee under §2.5 for a park of its statement, by the park itself for a Swich-direct one — and closed
        // with "paid outside the platform". Checked before anything else that could fail it: the lasting reason is the one
        // the console must show. (An order already at the bank from before is still resolved by steps 0a/0b: asking moves
        // no money, and the outside-payment check refuses while such an order may still execute.)
        if (ParkAuthorisation.IsRevoked(park, TodayLocal(_opts.TimeZoneId)))
        {
            var owedBy = s.MonthlyStatementId is not null
                ? "it is owed by the licensee under §2.5"
                : "the park owes it directly";
            await FailAsync(s, ParkAuthorisation.RevokedCode,
                $"The park revoked the platform's authorisation to transfer from its account from {park.AuthorisationRevokedFrom:yyyy-MM-dd} " +
                $"(Annex 3 §7) — nothing was initiated; {owedBy}: record its payment as paid outside the platform", ct);
            return s;
        }

        if (string.IsNullOrWhiteSpace(_opts.SwichIban))
        {
            await FailAsync(s, "SWICH_IBAN_NOT_CONFIGURED", "Settlement:SwichIban is not configured", ct);
            return s;
        }

        var account = park.BankAccounts.FirstOrDefault(a => a.IsPrimary) ?? park.BankAccounts.FirstOrDefault();
        if (account is null)
        {
            await FailAsync(s, "NO_PARK_ACCOUNT", "Park has no active payout account to settle from", ct);
            return s;
        }
        s.ParkBankAccountId = account.Id;

        // An account whose provider has no adapter on this server — the mock on a real-rails server, where it is not
        // registered, or a typo — used to fall back to the mock: the share was "transferred" and the row Completed while
        // Swich was never paid. Now the transfer fails before anything is sent and the nightly run tries again.
        var bank = ResolveAdapter(account.Provider);
        if (bank is null)
        {
            await FailAsync(s, PayoutWaitCodes.AdapterMissing,
                $"No bank adapter for provider '{account.Provider}' on this server — set the payout account's provider to its bank", ct);
            return s;
        }

        var source = new BankAccountContext(
            ParkId: park.Id,
            ParkBankAccountId: account.Id,
            Provider: account.Provider,
            BankCode: account.BankCode,
            SourceIban: account.Iban,
            SourceHolderName: account.HolderName ?? park.LegalEntityName ?? park.Name,
            CredentialsJson: account.CredentialsEncrypted);

        // No partial take: if the balance is readable and short, fail before touching the bank — unless the last
        // attempt's outcome is unknown (then the same document goes again and the bank says what became of it).
        if (!previousOutcomeUnknown)
        {
            try
            {
                var balance = await bank.GetBalanceAsync(source, ct);
                if (balance is { } b && b < s.SwichShare)
                {
                    await FailAsync(s, "INSUFFICIENT_PARK_BALANCE",
                        $"Park account balance {b:F2} GEL is below the settlement amount {s.SwichShare:F2} GEL", ct);
                    return s;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Balance pre-check unavailable for account {Account}; attempting transfer anyway", account.Id);
            }
        }

        var result = await BankSendHelper.SendWithLookupAsync(bank, new BankTransferRequest(
            IdempotencyKey: DocumentKey(s),
            Source: source,
            DestinationIban: _opts.SwichIban,
            DestinationName: _opts.SwichHolderName,
            Amount: s.SwichShare,
            Currency: "GEL",
            Reference: s.Description), _log, ct);

        if (result.Success && result.IsPending)
        {
            // Accepted, executing asynchronously. Stays Processing; the next run (or a retry) resolves it.
            s.BankTransferId = result.TransferId;
            s.FailureReason = null;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Settlement {Id}: bank accepted transfer {Tx}, awaiting execution", s.Id, result.TransferId);
            return s;
        }

        if (result.Success)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.BankTransferId = result.TransferId;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, s.SwichShare, result.TransferId,
                $"{s.Description} · {account.Provider} {Mask(account.Iban)} → {Mask(_opts.SwichIban)} (attempt {s.AttemptCount}, by {initiatedBy})"));
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Settlement {Id} for park {Park} completed: {Amount} GEL, bank tx {Tx}",
                s.Id, park.Name, s.SwichShare, result.TransferId);
            await RefreshStatementOfAsync(s, ct);
            return s;
        }

        // A failure that names an order means the bank holds a refused order under this document id — final,
        // nothing moved. Without an order id (rejected before an order existed, or no answer at all) the same
        // id is repeated next time: the bank either has nothing under it or de-duplicates it.
        if (result.TransferId is { } deadOrder) RetireDocument(s, deadOrder);
        await FailAsync(s, result.ErrorCode ?? "BANK_FAILED", result.ErrorMessage ?? "Bank transfer failed", ct);
        return s;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Paid outside the platform (§2.5 / §4.1.2)
    // ═══════════════════════════════════════════════════════════════════

    public static readonly IReadOnlySet<string> ExternalPayers = new HashSet<string>(StringComparer.Ordinal) { "licensee", "park" };

    public async Task<Settlement> SettleExternallyAsync(Guid settlementId, ExternalPayment payment, string recordedBy, CancellationToken ct = default)
    {
        var s = await _db.Settlements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == settlementId, ct)
            ?? throw new SettlementRejectedException(SettlementRejectionCodes.SettlementNotFound, "Settlement not found");

        var payer = payment.Payer?.Trim().ToLowerInvariant() ?? "";
        if (!ExternalPayers.Contains(payer))
            throw new SettlementRejectedException(SettlementRejectionCodes.InvalidPayer, "payer must be \"licensee\" or \"park\"");
        // §2.5 — the licensee answering for its park's debt — is a clause of the licence agreement. A row with no
        // licensee statement (a Swich-direct park, or a legacy nightly row) is outside it: only the park can have paid,
        // and the park's invoice must never say otherwise.
        if (payer == "licensee" && s.MonthlyStatementId is null)
            throw new SettlementRejectedException(SettlementRejectionCodes.InvalidPayer,
                "This park is not under the licence agreement — only the park can have paid its debt (payer must be \"park\")");
        var reference = payment.Reference?.Trim() ?? "";
        if (reference.Length == 0)
            throw new SettlementRejectedException(SettlementRejectionCodes.ReferenceRequired, "The payment's bank reference is required");
        if (reference.Length > 100)
            throw new SettlementRejectedException(SettlementRejectionCodes.ReferenceRequired, "The reference is longer than 100 characters");
        var note = string.IsNullOrWhiteSpace(payment.Note) ? null : payment.Note.Trim();
        if (note is { Length: > 500 })
            throw new SettlementRejectedException(SettlementRejectionCodes.NoteTooLong, "The note is longer than 500 characters");
        // Not before the period it settles (a monthly row: its month; a legacy row: its day), never in the future.
        var earliest = s.IsMonthly ? FirstOfMonth(s.SettlementDate) : s.SettlementDate;
        if (payment.PaidOn < earliest || payment.PaidOn > TodayLocal(_opts.TimeZoneId))
            throw new SettlementRejectedException(SettlementRejectionCodes.InvalidPaidOn,
                $"The payment date must lie between {earliest:yyyy-MM-dd} and today");

        if (s.Status != SettlementStatus.Failed)
            throw new SettlementRejectedException(SettlementRejectionCodes.NotFailed,
                $"Only a failed transfer can be recorded as paid outside the platform (this one is {s.Status})");
        // Never a partial take — and never a partial release: the whole debt or nothing.
        if (payment.Amount != s.SwichShare)
            throw new SettlementRejectedException(SettlementRejectionCodes.AmountMismatch,
                $"The payment must be the transfer's full amount, {s.SwichShare:F2} GEL");

        // The bank must not still be able to execute this transfer. An order under the current document that is
        // accepted, executing, completed or in a state we cannot map — or a bank we cannot ask — means the park
        // may pay it too: then Swich would be paid twice. Only a dead order, or none, may be closed by hand.
        var (_, _, bank, source) = await RouteAsync(s, ct);
        // The account's provider has no adapter on this server, so its bank cannot be asked — as with an adapter that throws
        // NotImplementedException below: nothing it never reached can still execute, unless an earlier attempt (through
        // a rail that worked then) ended without an answer.
        if (bank is null && source is not null && await HadUnknownOutcomeAsync(s.Id, ct))
            throw new SettlementRejectedException(SettlementRejectionCodes.BankLookupFailed,
                "An earlier attempt of this transfer ended without the bank's answer, and its bank cannot be asked from here — check the park's statement and resolve it at the bank");
        if (bank is not null && source is not null)
        {
            BankTransferLookup? lookup;
            try { lookup = await bank.FindTransferByIdempotencyKeyAsync(source, DocumentKey(s), ct); }
            catch (NotImplementedException)
            {
                // No adapter for this account's bank yet (BoG): every send through it failed BANK_ADAPTER_MISSING before
                // reaching a bank, so nothing it holds can still execute — the cashout cancel reads it the same way.
                // Unless an earlier attempt ended without an answer: that one went out through a bank that could execute
                // it (the row's account was switched since), and this stub cannot vouch for that document.
                if (await HadUnknownOutcomeAsync(s.Id, ct))
                    throw new SettlementRejectedException(SettlementRejectionCodes.BankLookupFailed,
                        "An earlier attempt of this transfer ended without the bank's answer, and its bank cannot be asked from here — check the park's statement and resolve it at the bank");
                lookup = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Annex 3 §7: a park that revoked the platform's authorisation may cut its bank access too — then no lookup
                // can ever be made, and its debt could never be closed. A transfer that never reached the bank (every
                // attempt stopped before a document went out: the revocation itself, the balance pre-check, no account,
                // no Swich IBAN) has no order there that could still execute, so closing it cannot pay Swich twice. One
                // whose attempt may have reached the bank still waits until the bank answers.
                if (ParkAuthorisation.IsRevokedReason(s.FailureReason) && !await MayHaveReachedBankAsync(s, ct))
                {
                    _log.LogWarning(ex, "Settlement {Id}: the bank cannot be asked, but the transfer never reached it (the park revoked the authorisation) — recording the outside payment", s.Id);
                    lookup = null;
                }
                else
                {
                    _log.LogWarning(ex, "Settlement {Id}: bank lookup failed while recording an outside payment", s.Id);
                    throw new SettlementRejectedException(SettlementRejectionCodes.BankLookupFailed,
                        "The park's bank could not be asked whether this transfer is still pending there — try again");
                }
            }
            if (lookup is not null && lookup.Status != BankTransferStatus.Failed)
                throw new SettlementRejectedException(SettlementRejectionCodes.TransferAtBank,
                    $"The park's bank holds transfer {lookup.TransferId} for this settlement ({lookup.Status}) — it may still be paid from the park's account; retry it instead");
        }

        // Closed only if NO attempt touched the row since it was read — not merely "still Failed under the same
        // document". The bank was asked above, before this UPDATE: a Retry or the nightly tick could meanwhile claim
        // the row, send this very document, lose the answer and put it back to Failed with the same document id (an
        // unknown outcome retires nothing). Status and BankDocumentSeq would then match, while the document may still
        // execute from the park's account — Swich paid twice. Every claim bumps AttemptCount and stamps LastAttemptAt,
        // so matching both as read makes any attempt in between fail this close (409 concurrent_change); the next
        // try asks the bank again and sees that document. The ledger entry is written in the same transaction.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var closed = await _db.Settlements
            .Where(x => x.Id == s.Id && x.Status == SettlementStatus.Failed && x.BankDocumentSeq == s.BankDocumentSeq
                     && x.AttemptCount == s.AttemptCount && x.LastAttemptAt == s.LastAttemptAt)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SettlementStatus.SettledExternally)
                .SetProperty(x => x.ExternalPayer, payer)
                .SetProperty(x => x.ExternalPaidOn, (DateOnly?)payment.PaidOn)
                .SetProperty(x => x.ExternalReference, reference)
                .SetProperty(x => x.ExternalNote, note)
                .SetProperty(x => x.ExternalRecordedBy, recordedBy)
                .SetProperty(x => x.ExternalRecordedAt, (DateTime?)now)
                .SetProperty(x => x.CompletedAt, (DateTime?)now)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (closed == 0)
            throw new SettlementRejectedException(SettlementRejectionCodes.ConcurrentChange,
                "The transfer changed while the payment was being recorded (an attempt ran, or it was recorded meanwhile) — reload it; the bank is asked again on the next try");
        _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementPaidExternally, s.SwichShare, reference,
            $"{s.Description} · paid outside the platform by the {payer} on {payment.PaidOn:yyyy-MM-dd}, recorded by {recordedBy}"
            + (note is null ? "" : $" — {note}")));
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _log.LogWarning("Settlement {Id} (park {Park}, {Amount} GEL) recorded as paid outside the platform by the {Payer} on {PaidOn} (ref {Ref}) by {Actor}",
            s.Id, s.ParkId, s.SwichShare, payer, payment.PaidOn, reference, recordedBy);

        var row = await _db.Settlements.FirstAsync(x => x.Id == s.Id, ct);
        await RefreshStatementOfAsync(row, ct);
        return row;
    }

    /// <summary>The adapter registered for a provider, or null (no stand-in — see ExecuteAsync).</summary>
    private IBankPayoutAdapter? ResolveAdapter(string provider) =>
        _services.GetKeyedService<IBankPayoutAdapter>(provider)
        ?? _services.GetKeyedService<IBankPayoutAdapter>(provider.ToUpperInvariant());

    /// <summary>Failure codes recorded before any document leaves for the bank: the attempt sent nothing.</summary>
    private static readonly string[] PreSendCodes =
        { ParkAuthorisation.RevokedCode, "INSUFFICIENT_PARK_BALANCE", "NO_PARK_ACCOUNT", "SWICH_IBAN_NOT_CONFIGURED" };

    /// <summary>
    /// Whether any attempt of this transfer may have put a document at the bank: it holds an order id, or one of its recorded
    /// failures came from (or after) a send. Every attempt records its failure in the ledger (SettlementFailed, code in
    /// Reference), so a row whose failures are all <see cref="PreSendCodes"/> never sent anything.
    /// </summary>
    private async Task<bool> MayHaveReachedBankAsync(Settlement s, CancellationToken ct)
    {
        if (s.BankTransferId is not null || s.BankDocumentSeq > 0) return true;
        return await _db.LedgerEntries.AsNoTracking().AnyAsync(l => l.SettlementId == s.Id
            && l.EntryType == LedgerEntryType.SettlementFailed && (l.Reference == null || !PreSendCodes.Contains(l.Reference)), ct);
    }

    /// <summary>Whether any attempt of this transfer ended without the bank's answer (the money may have left).</summary>
    private Task<bool> HadUnknownOutcomeAsync(Guid settlementId, CancellationToken ct)
    {
        var codes = OutcomeUnknownCodes.ToArray();
        return _db.LedgerEntries.AsNoTracking().AnyAsync(l => l.SettlementId == settlementId
            && l.EntryType == LedgerEntryType.SettlementFailed && l.Reference != null && codes.Contains(l.Reference), ct);
    }

    /// <summary>"{IdempotencyKey}" for the first document, "{IdempotencyKey}#n" after n refused ones.</summary>
    private static string DocumentKey(Settlement s) =>
        s.BankDocumentSeq == 0 ? s.IdempotencyKey : $"{s.IdempotencyKey}#{s.BankDocumentSeq}";

    /// <summary>
    /// The bank definitively refused the order it holds under the current document id. Move to the next
    /// document and forget the dead order id, so neither a retry nor a crash recovery ever polls or re-sends
    /// it. (Saved by the FailAsync that always follows.)
    /// </summary>
    private void RetireDocument(Settlement s, string? deadOrderId)
    {
        s.BankDocumentSeq++;
        s.BankTransferId = null;
        _log.LogWarning("Settlement {Id}: bank order {Order} refused; next attempt uses document #{Seq}",
            s.Id, deadOrderId ?? "?", s.BankDocumentSeq);
    }

    private async Task FailAsync(Settlement s, string code, string message, CancellationToken ct)
    {
        s.Status = SettlementStatus.Failed;
        s.FailureReason = $"{code}: {message}";
        _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementFailed, s.SwichShare, code, message));
        await _db.SaveChangesAsync(ct);
        // No e-mail channel yet: the console's alert banner shows the failure to Swich, the operator and
        // the park (settlement_failed), and the nightly run retries it until it succeeds.
        _log.LogError("Settlement {Id} (park {Park}, {Date}) FAILED — {Code}: {Message}",
            s.Id, s.ParkId, s.SettlementDate, code, message);
        await RefreshStatementOfAsync(s, ct);
    }

    private Task RefreshStatementOfAsync(Settlement s, CancellationToken ct) =>
        s.MonthlyStatementId is { } id ? RefreshStatementAsync(id, ct) : Task.CompletedTask;

    // ── Direct-park share computation ────────────────────────────────

    public record ShareSplit(decimal FeeTotal, decimal Phase1Fees, decimal Phase2Fees, decimal SwichShare);

    /// <summary>
    /// Swich-direct parks only. Walk the cashouts in completion order. While cumulative fees are under
    /// the park's Phase1Cap, fees go to Swich at Phase1Share; the remainder at SwichShare. A single
    /// cashout straddling the cap is split at the cap.
    /// </summary>
    public static ShareSplit ComputeShares(Park park, IEnumerable<Cashout> orderedCashouts, decimal cumulativeBefore)
    {
        var steadyRate = park.SwichSharePercent / 100m;
        var phase1Rate = (park.Phase1SharePercent ?? park.SwichSharePercent) / 100m;
        var cap = park.Phase1CapGel;

        decimal feeTotal = 0, p1 = 0, p2 = 0, swich = 0;
        var cumulative = cumulativeBefore;

        foreach (var c in orderedCashouts)
        {
            var fee = c.Fee;
            feeTotal += fee;

            if (cap is { } capValue && cumulative < capValue)
            {
                var under = Math.Min(fee, capValue - cumulative);
                var over = fee - under;
                p1 += under; p2 += over;
                swich += under * phase1Rate + over * steadyRate;
            }
            else
            {
                p2 += fee;
                swich += fee * steadyRate;
            }
            cumulative += fee;
        }

        return new ShareSplit(
            FeeTotal: Math.Round(feeTotal, 2),
            Phase1Fees: Math.Round(p1, 2),
            Phase2Fees: Math.Round(p2, 2),
            SwichShare: Math.Round(swich, 2, MidpointRounding.AwayFromZero));
    }

    // ── Time helpers ─────────────────────────────────────────────────

    public static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    public static string InvoiceRefFor(DateOnly month) => $"PT-{month:yyyy-MM}";

    /// <summary>UTC bounds of a local (Tbilisi) calendar month: [first day 00:00, next month's first day 00:00).</summary>
    public (DateTime FromUtc, DateTime ToUtc) MonthBoundsUtc(DateOnly month)
    {
        var tz = ResolveTimeZone(_opts.TimeZoneId);
        var first = FirstOfMonth(month);
        return (LocalMidnightUtc(first, tz), LocalMidnightUtc(first.AddMonths(1), tz));
    }

    /// <summary>Static form for callers without a service instance (controllers, the invoice renderer).</summary>
    public static (DateTime FromUtc, DateTime ToUtc) MonthBoundsUtcFor(DateOnly month, string? timeZoneId)
    {
        var tz = ResolveTimeZone(timeZoneId);
        var first = FirstOfMonth(month);
        return (LocalMidnightUtc(first, tz), LocalMidnightUtc(first.AddMonths(1), tz));
    }

    public bool IsMonthClosed(DateOnly month) => DateTime.UtcNow >= MonthBoundsUtc(month).ToUtc;

    /// <summary>§2.5: Swich's due amount is collected no later than this day (month end + Settlement:DueDays).</summary>
    public static DateOnly DueDate(DateOnly month, int dueDays) => FirstOfMonth(month).AddMonths(1).AddDays(-1).AddDays(dueDays);

    private static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), tz);

    public static DateOnly TodayLocal(string? timeZoneId = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ResolveTimeZone(timeZoneId)));

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, "Georgian Standard Time", "Asia/Tbilisi" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Georgia is UTC+4 with no DST — a safe last resort.
        return TimeZoneInfo.CreateCustomTimeZone("Tbilisi", TimeSpan.FromHours(4), "Tbilisi", "Tbilisi");
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static LedgerEntry Entry(Settlement s, LedgerEntryType type, decimal amount, string? reference, string? notes) => new()
    {
        SettlementId = s.Id,
        ParkId = s.ParkId,
        EntryType = type,
        Amount = amount,
        Reference = reference,
        Notes = notes,
    };

    private static string Mask(string? iban) =>
        string.IsNullOrEmpty(iban) || iban.Length < 4 ? "****" : $"****{iban[^4..]}";
}

/// <summary>Bound from the "Settlement" configuration section.</summary>
public class SettlementOptions
{
    public const string SectionName = "Settlement";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Swich Solutions' receiving TBC IBAN (Annex 3 §3: the account every park authorises the monthly
    /// transfer to). Must be under შპს სვიჩსოლუშენსი exactly. Configuration only — never hard-coded.
    /// </summary>
    public string SwichIban { get; set; } = "";

    /// <summary>
    /// The registered holder of <see cref="SwichIban"/> — sent as the beneficiary name on every park's transfer
    /// order and printed on the monthly invoice next to the IBAN. It must be the name the account is registered
    /// under (a bank that checks the name rejects anything else), so the default is the registered name.
    /// </summary>
    public string SwichHolderName { get; set; } = "შპს სვიჩსოლუშენსი";

    /// <summary>IANA or Windows id; Georgia has no DST.</summary>
    public string TimeZoneId { get; set; } = "Georgian Standard Time";

    /// <summary>
    /// Local wall-clock time of the nightly tick ("HH:mm"): failed transfers are retried every night;
    /// from <see cref="RunDayOfMonth"/> on, the previous month is settled.
    /// </summary>
    public string RunAtLocalTime { get; set; } = "00:30";

    /// <summary>
    /// Day of the month on which the previous month is settled (default 3). §2.5 requires the money
    /// within <see cref="DueDays"/> days of the month's end; running a couple of days earlier leaves
    /// nights for retries before it is overdue. Clamped to 1–28.
    /// </summary>
    public int RunDayOfMonth { get; set; } = 3;

    /// <summary>§2.5 / §4.1.2: Swich's share is due no later than this many calendar days after the month ends ([5]).</summary>
    public int DueDays { get; set; } = 5;

    /// <summary>§4.1.2: penalty on an amount unpaid after the due date, per day (0.1 %). Shown for information; never charged automatically.</summary>
    public decimal PenaltyPercentPerDay { get; set; } = 0.1m;

    /// <summary>
    /// Development / e2e only: allow "Settle now" for the running month (whatever is unsettled so far;
    /// later cashouts of the month roll into the next statement). Never in production — the agreement
    /// collects only after the month has ended.
    /// </summary>
    public bool AllowOpenMonthRun { get; set; }

    /// <summary>Shares below this are recorded as Completed without a bank transfer.</summary>
    public decimal MinTransferGel { get; set; } = 0.01m;

    /// <summary>
    /// A shared cost is counted in a statement only if it was entered (or last changed by its owner) at least this
    /// many hours before the statement is computed — default 48: the other side's window to see it and, if it
    /// disputes it, put it on hold. A later one rolls into the next statement. 0 switches the window off.
    /// </summary>
    public int CostCutoffHours { get; set; } = 48;

    /// <summary>How often the worker checks whether the nightly tick is due.</summary>
    public int PollIntervalSeconds { get; set; } = 60;
    public int InitialDelaySeconds { get; set; } = 20;
}
