using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Services;
using Xunit;
using static PayTaxi.Infrastructure.Services.PayoutQueueWorker;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Payouts now wait without limit (Annex 3 §5), so one park's empty account can hold hundreds of due
/// rows. The worker must still reach every other park, every other account of the same park, and every
/// transfer pending at the bank on each tick — a dry account delays only its own payouts.
/// </summary>
[Collection("api")]
public class PayoutQueueFairnessTests
{
    private readonly ApiFixture _f;
    public PayoutQueueFairnessTests(ApiFixture f) => _f = f;

    [Fact]
    public async Task Every_park_account_and_pending_poll_gets_its_oldest_row_into_the_tick_however_long_another_backlog_is()
    {
        // Rows are planted due an hour from now and the selection is asked about two hours from now, so the
        // live worker (which asks about "now") never touches them.
        var later = DateTime.UtcNow.AddHours(2);
        var planted = new List<Guid>();
        try
        {
            var lanes = await _f.DbAsync(async db =>
            {
                var tb5 = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-5");
                var batumi = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1");
                var tb5Account = await db.ParkBankAccounts.AsNoTracking().FirstAsync(a => a.ParkId == tb5.Id && a.BankCode == "TB");
                var bTbc = await db.ParkBankAccounts.AsNoTracking().FirstAsync(a => a.ParkId == batumi.Id && a.BankCode == "TB");
                var bBog = await db.ParkBankAccounts.AsNoTracking().FirstAsync(a => a.ParkId == batumi.Id && a.BankCode == "BG");
                var tb5Driver = await db.Drivers.AsNoTracking().FirstAsync(d => d.ParkId == tb5.Id);
                var bDriver = await db.Drivers.AsNoTracking().FirstAsync(d => d.ParkId == batumi.Id);
                var tb5Card = await db.BankCards.AsNoTracking().FirstAsync(c => c.DriverId == tb5Driver.Id);
                var bCard = await db.BankCards.AsNoTracking().FirstAsync(c => c.DriverId == bDriver.Id);

                var t0 = DateTime.UtcNow.AddDays(-3);
                Cashout Row(Park p, Driver d, BankCard card, ParkBankAccount a, int minutes, bool poll) => new()
                {
                    ParkId = p.Id, DriverId = d.Id, BankCardId = card.Id, ParkBankAccountId = a.Id,
                    Amount = 5m, Fee = 0.5m, IdempotencyKey = $"fair:{Guid.NewGuid():N}",
                    Status = poll ? CashoutStatus.Processing : CashoutStatus.Queued,
                    BankTransferId = poll ? $"fair_{Guid.NewGuid():N}"[..20] : null,
                    NextAttemptAt = DateTime.UtcNow.AddHours(1),
                    FailureReason = poll ? null : "INSUFFICIENT_FUNDS: test backlog",
                    CreatedAt = t0.AddMinutes(minutes),
                    InitiatedBy = "test:fairness",
                };
                // Park #5: a long, OLD backlog on its one (dry) account.
                var backlog = Enumerable.Range(0, 6).Select(i => Row(tb5, tb5Driver, tb5Card, tb5Account, i, poll: false)).ToList();
                // Batumi: one newer payout on each account, and one transfer pending at the bank.
                var bTbcRow = Row(batumi, bDriver, bCard, bTbc, 100, poll: false);
                var bBogRow = Row(batumi, bDriver, bCard, bBog, 101, poll: false);
                var bPoll = Row(batumi, bDriver, bCard, bTbc, 102, poll: true);
                var all = backlog.Concat(new[] { bTbcRow, bBogRow, bPoll }).ToList();
                db.Cashouts.AddRange(all);
                await db.SaveChangesAsync();
                planted.AddRange(all.Select(c => c.Id));
                return (backlog, heads: new[] { backlog[0].Id, bTbcRow.Id, bBogRow.Id, bPoll.Id });
            });

            // One query (the parallel flaky-bank host keeps moving its own park #3 rows, so two queries could
            // see different lanes): the order must be round-robin — every lane's first row before any lane's
            // second — however old and long one lane's backlog is.
            var due = await _f.DbAsync(db => SelectDueAsync(db, later, 10_000, CancellationToken.None));
            var indexed = due.Select((d, i) => (d.Lane, i)).GroupBy(x => x.Lane).ToList();
            var lastHead = indexed.Max(g => g.First().i);
            var firstSecond = indexed.Where(g => g.Count() > 1).Select(g => g.ElementAt(1).i).DefaultIfEmpty(int.MaxValue).Min();
            Assert.True(lastHead < firstSecond, $"a lane's second row (at {firstSecond}) came before another lane's first (at {lastHead})");

            // So a tick with room for one row per lane carries the head of each: the backlog's oldest row but
            // not its second, and each Batumi lane although all of them are younger.
            var tick = due.Take(indexed.Count).Select(d => d.Id).ToHashSet();
            Assert.All(lanes.heads, h => Assert.Contains(h, tick));
            Assert.DoesNotContain(lanes.backlog[1].Id, tick);
        }
        finally
        {
            await _f.DbAsync(db => db.Cashouts.Where(c => planted.Contains(c.Id)).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task A_dry_account_stops_only_its_own_lane_the_parks_other_account_and_its_pending_polls_still_run()
    {
        var park = Guid.NewGuid();
        var tbc = Guid.NewGuid();
        var bog = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddDays(-1);
        DueCashout Row(Guid? account, bool poll, int minutes) => new(Guid.NewGuid(), park, account, poll, t0.AddMinutes(minutes));

        // Oldest first, as the selection hands them over: the dry TBC account's payouts are the oldest.
        var t1 = Row(tbc, false, 0); var t2 = Row(tbc, false, 1); var t3 = Row(tbc, false, 2);
        var g1 = Row(bog, false, 3); var g2 = Row(bog, false, 4);
        var p1 = Row(tbc, true, 5); var p2 = Row(tbc, true, 6);
        var rows = new[] { t1, t2, t3, g1, g2, p1, p2 };

        var processed = new List<Guid>();
        Task<CashoutSagaResult> Process(Guid id, CancellationToken _)
        {
            processed.Add(id);
            var r = rows.Single(x => x.Id == id);
            // TBC is empty: every attempt goes back to the queue. BoG pays. Polls: still pending, then executed.
            var status = r.IsPoll ? (r == p1 ? "Processing" : "Completed")
                       : r.ParkBankAccountId == tbc ? "Queued" : "Completed";
            return Task.FromResult(new CashoutSagaResult(id, status, 5m, 0.5m, 4.5m,
                r.IsPoll ? "ord" : null, "yx", status == "Queued" ? "INSUFFICIENT_FUNDS" : null, false));
        }

        await DrainParkAsync(rows, Process, stopOnRequeue: true, spacingMs: 0, NullLogger.Instance, CancellationToken.None);

        // The dry lane was tried once and then left for the next tick; everything else went through, each
        // lane in the order the drivers asked.
        Assert.Contains(t1.Id, processed);
        Assert.DoesNotContain(t2.Id, processed);
        Assert.DoesNotContain(t3.Id, processed);
        Assert.Equal(new[] { g1.Id, g2.Id }, processed.Where(id => id == g1.Id || id == g2.Id).ToArray());
        Assert.Equal(new[] { p1.Id, p2.Id }, processed.Where(id => id == p1.Id || id == p2.Id).ToArray());
        Assert.Equal(5, processed.Count);
    }
}
