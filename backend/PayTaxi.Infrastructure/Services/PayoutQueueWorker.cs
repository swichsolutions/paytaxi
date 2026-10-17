using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Drains <see cref="CashoutStatus.Queued"/> cashouts — the ones whose Yandex debit
/// succeeded but whose bank payout hit a retryable problem (gateway down, the park's
/// account short, rate limit). They are retried until they go out, however long that
/// takes (agreement §5.5, Annex 3 §5); the gap between attempts never exceeds 15 minutes,
/// so a top-up is picked up within that.
///
/// Because a wait has no end, a backlog can be large — and it must not starve anyone else. Each tick
/// takes the due rows fairly (the oldest of every lane first, see <see cref="SelectDueAsync"/>), where a
/// lane is one park's payouts from one source account, or one park's pending-at-bank polls. Parks run in
/// parallel; inside a park it is one payout at a time, round-robin across its lanes, each lane in creation
/// order so a recovering bank is not hammered and payouts land in the order drivers asked for them. A lane
/// whose payout goes straight back to the queue stops for the tick — only that lane (<see cref="DrainParkAsync"/>).
///
/// The claim itself (Queued → Processing) is an atomic UPDATE inside
/// <see cref="ICashoutOrchestrator.ProcessQueuedAsync"/>, so an overlapping tick or a
/// second API instance can't double-pay.
/// </summary>
public class PayoutQueueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PayoutQueueOptions _opts;
    private readonly ILogger<PayoutQueueWorker> _log;

    public PayoutQueueWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PayoutQueueOptions> opts,
        ILogger<PayoutQueueWorker> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Payout queue worker disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("Payout queue worker started (poll every {Interval}s)", _opts.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Payout queue tick failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Payout queue worker stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<DueCashout> due;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Sagas interrupted mid-flight (crash/deploy) would otherwise sit in Processing forever.
            if (_opts.StaleProcessingMinutes > 0)
            {
                var saga = scope.ServiceProvider.GetRequiredService<ICashoutOrchestrator>();
                var swept = await saga.SweepStaleProcessingAsync(TimeSpan.FromMinutes(_opts.StaleProcessingMinutes), ct);
                if (swept > 0) _log.LogWarning("Payout queue: {Count} interrupted cashout(s) flagged for review", swept);
            }
            due = await SelectDueAsync(db, DateTime.UtcNow, _opts.MaxPerTick, ct);
        }

        if (due.Count == 0) return;

        _log.LogInformation("Payout queue: {Count} cashout(s) due across {Parks} park(s)",
            due.Count, due.Select(d => d.ParkId).Distinct().Count());

        var perPark = due.GroupBy(d => d.ParkId)
            .Select(g => DrainParkAsync(g.ToList(), ProcessAsync, _opts.StopParkOnFirstRequeue, _opts.SpacingMs, _log, ct));
        await Task.WhenAll(perPark);
    }

    private async Task<CashoutSagaResult> ProcessAsync(Guid id, CancellationToken ct)
    {
        // Fresh scope per cashout: isolated DbContext + Yandex client per saga step.
        using var scope = _scopeFactory.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<ICashoutOrchestrator>();
        return await saga.ProcessQueuedAsync(id, ct);
    }

    /// <summary>A due row: a waiting payout to attempt, or a transfer pending at the bank to poll.</summary>
    public sealed record DueCashout(Guid Id, Guid ParkId, Guid? ParkBankAccountId, bool IsPoll, DateTime CreatedAt)
    {
        /// <summary>
        /// What is drained strictly in order: one park's payouts from one source account, or one park's
        /// status polls. A dry account (or a bank that is down for that account) only holds up its own lane.
        /// </summary>
        public (Guid ParkId, Guid? AccountId, bool IsPoll) Lane => (ParkId, ParkBankAccountId, IsPoll);
    }

    /// <summary>Row shape of the selection query (EF materialises raw SQL into settable properties).</summary>
    private sealed class DueRow
    {
        public Guid Id { get; set; }
        public Guid ParkId { get; set; }
        public Guid? ParkBankAccountId { get; set; }
        public bool IsPoll { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// The rows due at <paramref name="now"/>, at most <paramref name="maxPerTick"/>, taken FAIRLY: the oldest
    /// due row of every lane first, then the second of every lane, and so on. Payouts wait without limit now
    /// (Annex 3 §5), so one park with an empty account can hold hundreds of due rows that are all older than
    /// everyone else's; taking the oldest rows overall would let that backlog fill every tick and starve the
    /// other parks — their payouts and their pending-at-bank polls alike — until that one park tops up.
    /// </summary>
    public static async Task<List<DueCashout>> SelectDueAsync(AppDbContext db, DateTime now, int maxPerTick, CancellationToken ct)
    {
        var queued = (int)CashoutStatus.Queued;
        var processing = (int)CashoutStatus.Processing;
        var rows = await db.Database.SqlQuery<DueRow>($"""
            SELECT d."Id", d."ParkId", d."ParkBankAccountId", d."IsPoll", d."CreatedAt"
            FROM (
                SELECT c."Id", c."ParkId", c."ParkBankAccountId", c."CreatedAt",
                       (c."BankTransferId" IS NOT NULL) AS "IsPoll",
                       ROW_NUMBER() OVER (
                           PARTITION BY c."ParkId", c."ParkBankAccountId", (c."BankTransferId" IS NOT NULL)
                           ORDER BY c."CreatedAt", c."Id") AS "LaneRank"
                FROM "Cashouts" AS c
                WHERE (c."Status" = {queued} AND (c."NextAttemptAt" IS NULL OR c."NextAttemptAt" <= {now}))
                   OR (c."Status" = {processing} AND c."BankTransferId" IS NOT NULL
                       AND c."NextAttemptAt" IS NOT NULL AND c."NextAttemptAt" <= {now})
            ) AS d
            ORDER BY d."LaneRank", d."CreatedAt", d."Id"
            LIMIT {maxPerTick}
            """).ToListAsync(ct);
        return rows.Select(r => new DueCashout(r.Id, r.ParkId, r.ParkBankAccountId, r.IsPoll, r.CreatedAt)).ToList();
    }

    /// <summary>
    /// One park's share of a tick, still one payout at a time for the park (bank and Yandex courtesy), but
    /// round-robin across its lanes: the oldest row of each lane, then the next of each, each lane in the
    /// order its drivers asked. A payout that goes back to the queue (with <paramref name="stopOnRequeue"/>)
    /// means its account still cannot pay — the rest of THAT lane waits for the next tick instead of burning
    /// attempts, while the park's other account and its pending-at-bank polls carry on. Polls never stop a
    /// lane: they send nothing, they only ask how an accepted order went.
    /// </summary>
    public static async Task DrainParkAsync(
        IReadOnlyList<DueCashout> rows, Func<Guid, CancellationToken, Task<CashoutSagaResult>> process,
        bool stopOnRequeue, int spacingMs, ILogger log, CancellationToken ct)
    {
        var lanes = rows
            .GroupBy(r => r.Lane)
            .Select(g => new Queue<DueCashout>(g.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)))
            .OrderBy(q => q.Peek().CreatedAt)
            .ToList();
        var first = true;

        while (lanes.Count > 0)
        {
            foreach (var lane in lanes.ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!first && spacingMs > 0) await Task.Delay(spacingMs, ct);
                first = false;

                var row = lane.Dequeue();
                var stopLane = false;
                try
                {
                    var result = await process(row.Id, ct);
                    log.LogInformation("Payout queue: cashout {Id} → {Status} (attempt {N})",
                        row.Id, result.Status, result.AttemptCount);

                    stopLane = stopOnRequeue && !row.IsPoll
                        && result.Status == nameof(CashoutStatus.Queued) && result.BankTransferId is null;
                    if (stopLane && lane.Count > 0)
                        log.LogInformation("Payout queue: park {Park} account {Account} still cannot pay — deferring its remaining {Left} payout(s) to the next tick",
                            row.ParkId, row.ParkBankAccountId, lane.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Payout queue: processing cashout {Id} threw", row.Id);
                }

                if (stopLane || lane.Count == 0) lanes.Remove(lane);
            }
        }
    }
}

public class PayoutQueueOptions
{
    public const string SectionName = "PayoutQueue";

    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 10;
    public int InitialDelaySeconds { get; set; } = 8;

    /// <summary>
    /// Upper bound on rows taken per tick, across all parks. Shared fairly: every lane's oldest due row
    /// comes before any lane's second, so a park with a huge backlog cannot crowd the others out.
    /// </summary>
    public int MaxPerTick { get; set; } = 200;

    /// <summary>Gap between consecutive payouts for the same park (bank + Yandex courtesy spacing).</summary>
    public int SpacingMs { get; set; } = 500;

    /// <summary>
    /// If a payout goes back to the queue (its account still cannot pay, the bank is still down), skip the
    /// rest of that lane — that park account's payouts — this tick. The park's other lanes carry on.
    /// </summary>
    public bool StopParkOnFirstRequeue { get; set; } = true;

    /// <summary>
    /// A cashout Processing with no bank id and no next attempt for this long is treated as an
    /// interrupted saga and flagged ReviewRequired. Must comfortably exceed the Yandex retry budget. 0 disables.
    /// </summary>
    public int StaleProcessingMinutes { get; set; } = 10;
}
