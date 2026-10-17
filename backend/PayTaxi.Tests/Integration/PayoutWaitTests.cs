using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Adapters.Yandex;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Cashouts wait for money instead of failing (agreement §5.5, Annex 1 §3.2, Annex 3 §5): a park
/// account that is short, a bank that is down or a missing payout account park the payout in the
/// queue for as long as it takes, and it goes out on its own once the cause is gone. Only a person
/// ends a wait early (Cancel), and an unknown money state still ends in ReviewRequired, never in a
/// reversal.
///
/// Runs against Tbilisi Auto Park #5 (the flaky-bank host owns #3) with the mock bank's funds check
/// switched on and the park's TBC account drained — both restored in every finally block. Steps that
/// need a bank the mock cannot imitate (an order that is accepted and then refused, a dead
/// connection) drive the saga directly with a scripted TBC-shaped bank.
/// </summary>
[Collection("api")]
public class PayoutWaitTests
{
    private const string Slug = "tbilisi-auto-park-5";
    private readonly ApiFixture _f;
    public PayoutWaitTests(ApiFixture f) => _f = f;

    // ── Fixture plumbing ─────────────────────────────────────────────

    private MockBankPayoutProvider Mock => _f.Services.GetRequiredService<MockBankPayoutProvider>();
    private MockBankPayoutOptions MockOpts => _f.Services.GetRequiredService<IOptions<MockBankPayoutOptions>>().Value;
    private CashoutOptions SagaOpts => _f.Services.GetRequiredService<IOptions<CashoutOptions>>().Value;

    private async Task<(Guid parkId, Guid accountId)> ParkAsync() => await _f.DbAsync(async db =>
    {
        var park = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == Slug);
        var account = await db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == park.Id && a.BankCode == "TB" && a.IsActive)
            .OrderByDescending(a => a.IsPrimary).FirstAsync();
        return (park.Id, account.Id);
    });

    /// <summary>
    /// Park #5's account runs dry with the funds check on; the worker's backoff is stretched so it
    /// rarely touches a row between the test's own steps. Returns the undo.
    /// </summary>
    private Func<Task> DrainAccount(Guid accountId)
    {
        var backoff = SagaOpts.BackoffSeconds;
        MockOpts.EnforceBalance = true;
        Mock.SetBalance(accountId, 0m);
        SagaOpts.BackoffSeconds = new[] { 600 };
        return () =>
        {
            MockOpts.EnforceBalance = false;
            MockOpts.AsyncExecution = false;
            MockOpts.AsyncSettleAfterPolls = 2;
            Mock.SetBalance(accountId, 25_000m);
            SagaOpts.BackoffSeconds = backoff;
            return Task.CompletedTask;
        };
    }

    /// <summary>Anything a failed assertion left waiting must not haunt later tests (in-flight checks, alerts).</summary>
    private Task CleanUpAsync(IEnumerable<Guid> ids) => _f.DbAsync(async db =>
    {
        var list = ids.ToList();
        var rows = await db.Cashouts.Where(c => list.Contains(c.Id)
            && (c.Status == CashoutStatus.Queued || c.Status == CashoutStatus.Processing || c.Status == CashoutStatus.ReviewRequired)).ToListAsync();
        foreach (var c in rows) { c.Status = CashoutStatus.Failed; c.NextAttemptAt = null; c.FailureReason = "TEST_CLEANUP"; }
        return await db.SaveChangesAsync();
    });

    private static async Task<Guid> DefaultCardAsync(HttpClient driver)
    {
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        return me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
    }

    /// <summary>A driver cashes out while the park's account is empty: 202 Queued, INSUFFICIENT_FUNDS.</summary>
    private async Task<Guid> QueueForFundsAsync(string profile, decimal amount) =>
        (await QueueForFundsWithRequestAsync(profile, amount)).Id;

    /// <summary>
    /// <see cref="QueueForFundsAsync"/>, plus what it takes to send the very same request again (the
    /// driver's client and the body with its idempotency key) — a lost response, a double tap, a replay.
    /// </summary>
    private async Task<(Guid Id, HttpClient Driver, object Request)> QueueForFundsWithRequestAsync(string profile, decimal amount)
    {
        var (driver, _, _) = await _f.DriverClientAsync(profile);
        var cardId = await DefaultCardAsync(driver);
        var request = new { cardId, amount, idempotencyKey = Guid.NewGuid().ToString() };
        var res = await driver.PostAsJsonAsync("/api/driver/cashouts", request);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == HttpStatusCode.Accepted, $"expected 202 Queued, got {(int)res.StatusCode}: {body}");
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        // The driver gets the code only — the bank's text (here: the park's account balance) is not theirs to read.
        Assert.Equal("INSUFFICIENT_FUNDS", body.GetProperty("failureReason").GetString());
        return (body.GetProperty("cashoutId").GetGuid(), driver, request);
    }

    private Task<Cashout> RowAsync(Guid id) => _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == id));

    private Task<List<LedgerEntryType>> LedgerAsync(Guid id) =>
        _f.DbAsync(db => db.LedgerEntries.Where(l => l.CashoutId == id).OrderBy(l => l.CreatedAt).Select(l => l.EntryType).ToListAsync());

    private Task UpdateAsync(Guid id, Action<Cashout> change) => _f.DbAsync(async db =>
    {
        var c = await db.Cashouts.FirstAsync(x => x.Id == id);
        change(c);
        return await db.SaveChangesAsync();
    });

    private async Task<Cashout> WaitForAsync(Guid id, Func<Cashout, bool> done, int seconds = 15)
    {
        var row = await RowAsync(id);
        for (var i = 0; i < seconds * 4 && !done(row); i++)
        {
            await Task.Delay(250);
            row = await RowAsync(id);
        }
        return row;
    }

    /// <summary>The real saga, wired to a bank of the test's choosing.</summary>
    private static CashoutOrchestrator Saga(IServiceScope scope, IBankPayoutAdapter bank)
    {
        var banks = new ServiceCollection();
        foreach (var key in new[] { "mock", "MOCK", "tbc", "TBC" })
            banks.AddKeyedSingleton(key, bank);
        var sp = scope.ServiceProvider;
        return new CashoutOrchestrator(
            sp.GetRequiredService<AppDbContext>(),
            banks.BuildServiceProvider(),
            sp.GetRequiredService<IYandexFleetClient>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<IOptions<CashoutOptions>>(),
            sp.GetRequiredService<IOptions<YandexFleetOptions>>(),
            NullLogger<CashoutOrchestrator>.Instance);
    }

    /// <summary>
    /// One queue step through <paramref name="bank"/>. The background worker may grab the row first
    /// (with the mock, which only says "insufficient funds" again); then the row is made due and the
    /// step is tried again until the scripted bank has actually been asked.
    /// </summary>
    private async Task<CashoutSagaResult> StepWithAsync(ScriptedBank bank, Guid id, Action<Cashout>? prepare = null)
    {
        for (var i = 0; i < 30; i++)
        {
            await UpdateAsync(id, c =>
            {
                if (c.Status == CashoutStatus.Queued || (c.Status == CashoutStatus.Processing && c.BankTransferId != null))
                {
                    c.NextAttemptAt = DateTime.UtcNow;
                    prepare?.Invoke(c);
                }
            });
            var calls = bank.Calls;
            using var scope = _f.Services.CreateScope();
            var result = await Saga(scope, bank).ProcessQueuedAsync(id);
            if (bank.Calls > calls) return result;
            await Task.Delay(150);
        }
        throw new Xunit.Sdk.XunitException("the scripted bank was never asked — the worker kept winning the claim");
    }

    // ── Insufficient funds: wait, keep the debit, go out after a top-up ──

    [Fact]
    public async Task Insufficient_funds_queues_the_payout_keeps_the_debit_waits_past_the_old_limits_and_goes_out_after_a_top_up()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_004", 7m);
            ids.Add(id);

            // Debited on Yandex and waiting — not Failed, no reversal.
            var row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.False(string.IsNullOrEmpty(row.YandexTransactionId));
            Assert.Null(row.YandexReversalTransactionId);
            Assert.DoesNotContain(LedgerEntryType.YandexReversed, await LedgerAsync(id));
            Assert.Null(row.BankTransferId);

            // Way past the old ceilings (30 attempts / 12 h) the payout still waits. The backoff's last
            // step repeats but never exceeds 15 minutes, so a top-up is noticed promptly.
            SagaOpts.BackoffSeconds = new[] { 3600 };
            await UpdateAsync(id, c => { c.AttemptCount = 40; c.CreatedAt = DateTime.UtcNow.AddDays(-2); c.NextAttemptAt = DateTime.UtcNow; });
            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ICashoutOrchestrator>().ProcessQueuedAsync(id);
            row = await WaitForAsync(id, r => r.AttemptCount >= 41 && r.Status == CashoutStatus.Queued);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.Equal(41, row.AttemptCount);
            Assert.StartsWith("INSUFFICIENT_FUNDS:", row.FailureReason);
            Assert.Null(row.YandexReversalTransactionId);
            var gap = row.NextAttemptAt!.Value - row.LastAttemptAt!.Value;
            Assert.InRange(gap.TotalMinutes, 14.9, 15.1);
            SagaOpts.BackoffSeconds = new[] { 600 };

            // The park tops up (dev endpoint, Swich only): the waiting payout goes out by itself.
            var swich = await _f.SuperAdminAsync();
            var topUp = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{accountId}/mock-topup", new { amount = 100m });
            Assert.Equal(HttpStatusCode.OK, topUp.StatusCode);
            var t = await topUp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(100m, t.GetProperty("balance").GetDecimal());
            Assert.True(t.GetProperty("released").GetInt32() >= 1, "the waiting payout is made due at once");

            row = await WaitForAsync(id, r => r.Status == CashoutStatus.Completed);
            Assert.Equal(CashoutStatus.Completed, row.Status);
            Assert.NotNull(row.BankTransferId);
            Assert.Null(row.YandexReversalTransactionId);
            Assert.Equal(100m - (7m - row.Fee), Mock.GetBalance(accountId));
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task The_mock_topup_endpoint_is_Swich_only_and_refuses_bad_input()
    {
        var (parkId, accountId) = await ParkAsync();
        var url = $"/api/admin/parks/{parkId}/bank-accounts/{accountId}/mock-topup";
        var manager = await _f.ParkAdminAsync(Slug);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(url, new { amount = 10m })).StatusCode);

        var swich = await _f.SuperAdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await swich.PostAsJsonAsync(url, new { amount = 0m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await swich.PostAsJsonAsync(url, new { amount = 1.005m })).StatusCode);
        // Another park's account under this park's route.
        var otherAccount = await _f.DbAsync(db => db.ParkBankAccounts.Where(a => a.ParkId != parkId).Select(a => a.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.NotFound,
            (await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{otherAccount}/mock-topup", new { amount = 10m })).StatusCode);

        var before = Mock.GetBalance(accountId);
        var ok = await swich.PostAsJsonAsync(url, new { amount = 12.34m });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(before + 12.34m, Mock.GetBalance(accountId));
    }

    // ── A real "no" from the bank still fails at once; an unknown outcome never reverses ──

    [Fact]
    public async Task A_bank_rejection_still_fails_at_once_with_a_reversal()
    {
        var (_, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_005", 6m);
            ids.Add(id);

            var bank = new ScriptedBank { RejectForGood = true };
            var result = await StepWithAsync(bank, id);

            Assert.Equal("Failed", result.Status);
            var row = await RowAsync(id);
            Assert.StartsWith("BANK_REJECTED:", row.FailureReason);
            Assert.NotNull(row.YandexReversalTransactionId);
            Assert.Contains(LedgerEntryType.YandexReversed, await LedgerAsync(id));

            // The driver is told it failed and that the money is back — with the code, never the bank's own text.
            var note = await _f.DbAsync(db => db.Notifications.AsNoTracking()
                .Where(n => n.DriverId == row.DriverId && n.Type == "cashout_failed")
                .OrderByDescending(n => n.CreatedAt).FirstAsync());
            Assert.Contains("\"code\":\"BANK_REJECTED\"", note.Data);
            Assert.DoesNotContain("Beneficiary account is closed", note.Data);
            Assert.DoesNotContain("Beneficiary account is closed", note.Body);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task An_unknown_outcome_blocks_cancel_and_after_the_limit_goes_to_review_never_to_a_reversal()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_006", 9m);
            ids.Add(id);

            // The bank's connection dies mid-send and the lookup cannot answer either.
            var bank = new ScriptedBank { Unreachable = true };
            var first = await StepWithAsync(bank, id);
            Assert.Equal("Queued", first.Status);
            var row = await RowAsync(id);
            Assert.Equal(1, row.UnresolvedAttempts);
            Assert.StartsWith("BANK_EXCEPTION:", row.FailureReason);

            // Money may have left: a cancel (= reversal) is refused and the row stays in the queue.
            var swich = await _f.SuperAdminAsync();
            var cancel = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{id}/cancel", new { reason = "driver asked" });
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            Assert.Equal("outcome_unknown", ApiFixture.Code(await cancel.Content.ReadFromJsonAsync<JsonElement>()));
            row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.Null(row.YandexReversalTransactionId);

            // The 30th unanswered attempt in a row: a human must look. Never a reversal. (A high total
            // attempt count from an earlier wait for funds does not matter — only the streak does.)
            var review = await StepWithAsync(bank, id, c => { c.UnresolvedAttempts = 29; c.AttemptCount = 80; });
            Assert.Equal("ReviewRequired", review.Status);
            row = await RowAsync(id);
            Assert.Equal(30, row.UnresolvedAttempts);
            Assert.StartsWith("BANK_EXCEPTION:", row.FailureReason);
            Assert.Null(row.YandexReversalTransactionId);
            var ledger = await LedgerAsync(id);
            Assert.Contains(LedgerEntryType.ReviewFlagged, ledger);
            Assert.DoesNotContain(LedgerEntryType.YandexReversed, ledger);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    // ── The TBC shape: an order accepted, then refused for lack of funds ──

    [Fact]
    public async Task An_order_refused_for_lack_of_funds_is_retried_as_a_new_document_after_the_top_up()
    {
        var (_, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_007", 8m);
            ids.Add(id);
            var key = (await RowAsync(id)).IdempotencyKey;

            // The bank creates order ord1 under our document id and refuses it: not enough money.
            var bank = new ScriptedBank { Funded = false };
            var waiting = await StepWithAsync(bank, id);
            Assert.Equal("Queued", waiting.Status);
            var row = await RowAsync(id);
            Assert.StartsWith("INSUFFICIENT_FUNDS:", row.FailureReason);
            Assert.Equal(1, row.BankDocumentSeq);
            Assert.Null(row.YandexReversalTransactionId);

            // Topped up. The same document id would only fetch the dead order again (TBC answers a
            // repeated request id with the original order), so the retry is a fresh document.
            bank.Funded = true;
            var paid = await StepWithAsync(bank, id);
            Assert.Equal("Completed", paid.Status);
            Assert.Equal(new[] { key, $"{key}#1" }, bank.Sent.Distinct().ToArray());
            Assert.Equal(bank.OrderIdFor($"{key}#1"), (await RowAsync(id)).BankTransferId);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task An_accepted_order_that_dies_for_lack_of_funds_while_pending_waits_on_a_new_document()
    {
        var (_, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_008", 8m);
            ids.Add(id);

            // Accepted (pending at the bank) …
            var bank = new ScriptedBank { Async = true, Funded = false };
            var accepted = await StepWithAsync(bank, id);
            Assert.Equal("Processing", accepted.Status);
            Assert.Equal("ord1", accepted.BankTransferId);

            // … then refused on the status poll: back in the queue with no bank id and a new document.
            var refused = await StepWithAsync(bank, id);
            Assert.Equal("Queued", refused.Status);
            var row = await RowAsync(id);
            Assert.Null(row.BankTransferId);
            Assert.Equal(1, row.BankDocumentSeq);
            Assert.StartsWith("INSUFFICIENT_FUNDS:", row.FailureReason);
            Assert.Contains("ord1", row.FailureReason);
            Assert.Null(row.YandexReversalTransactionId);

            bank.Funded = true;
            var resent = await StepWithAsync(bank, id);   // new document, accepted
            Assert.Equal("ord2", resent.BankTransferId);
            var done = await StepWithAsync(bank, id);     // poll: executed
            Assert.Equal("Completed", done.Status);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task A_payout_that_waited_for_days_and_is_then_accepted_asynchronously_is_polled_to_completion_not_flagged()
    {
        var (_, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_003", 5m);
            ids.Add(id);
            await UpdateAsync(id, c => c.CreatedAt = DateTime.UtcNow.AddDays(-2));

            // Topped up on an asynchronous rail: accepted now, executed after two status polls.
            MockOpts.AsyncExecution = true;
            MockOpts.AsyncSettleAfterPolls = 2;
            Mock.TopUp(accountId, 50m);
            await UpdateAsync(id, c => c.NextAttemptAt = DateTime.UtcNow);
            var row = await WaitForAsync(id, r => r.BankTransferId != null);
            Assert.Equal(CashoutStatus.Processing, row.Status);

            // The request is two days old, but the bank accepted it seconds ago: no BANK_PENDING_TIMEOUT.
            for (var i = 0; i < 6 && row.Status == CashoutStatus.Processing; i++)
            {
                await UpdateAsync(id, c => { if (c.Status == CashoutStatus.Processing) c.NextAttemptAt = DateTime.UtcNow; });
                row = await WaitForAsync(id, r => r.Status != CashoutStatus.Processing || r.NextAttemptAt > DateTime.UtcNow.AddSeconds(1), 5);
            }
            Assert.Equal(CashoutStatus.Completed, row.Status);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    // ── Cancel: the human way out ────────────────────────────────────

    [Fact]
    public async Task Cancel_is_for_Swich_and_the_operator_needs_a_reason_reverses_the_debit_tells_the_driver_and_happens_once()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var (id, driverClient, request) = await QueueForFundsWithRequestAsync("yp_tb5_004", 6m);
            ids.Add(id);
            var url = $"/api/admin/parks/{parkId}/cashouts/{id}/cancel";

            // Park managers may not cancel — not another park's, and not their own park's either: the park is the
            // party obliged to keep the account funded, and a payout short of funds waits for its top-up (Annex 3 §5).
            var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
            Assert.Equal(HttpStatusCode.Forbidden, (await batumiManager.PostAsJsonAsync(url, new { reason = "x" })).StatusCode);
            var batumiId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1")).Id);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await batumiManager.PostAsJsonAsync($"/api/admin/parks/{batumiId}/cashouts/{id}/cancel", new { reason = "x" })).StatusCode);
            var manager = await _f.ParkAdminAsync(Slug);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(url, new { reason = "We cannot top up this week" })).StatusCode);
            var untouched = await RowAsync(id);
            Assert.Equal(CashoutStatus.Queued, untouched.Status);
            Assert.Null(untouched.YandexReversalTransactionId);

            // A reason is required (and bounded).
            var op = await _f.OperatorAsync();
            var noReason = await op.PostAsJsonAsync(url, new { reason = "   " });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            Assert.Equal("reason_required", ApiFixture.Code(await noReason.Content.ReadFromJsonAsync<JsonElement>()));
            var tooLong = await op.PostAsJsonAsync(url, new { reason = new string('r', 501) });
            Assert.Equal("reason_too_long", ApiFixture.Code(await tooLong.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(CashoutStatus.Queued, (await RowAsync(id)).Status);

            // The operator cancels: Yandex debit reversed, Failed, reason and actor on the row.
            var res = await op.PostAsJsonAsync(url, new { reason = "Driver asked to cancel by phone" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Failed", body.GetProperty("status").GetString());
            var row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Failed, row.Status);
            Assert.Equal("CANCELLED: Driver asked to cancel by phone (by admin:levan@operator.local)", row.FailureReason);
            Assert.NotNull(row.YandexReversalTransactionId);
            var ledger = await LedgerAsync(id);
            Assert.Contains(LedgerEntryType.YandexReversed, ledger);
            Assert.Contains(LedgerEntryType.CashoutReversed, ledger);

            // The driver gets the failure/refund notification — coded, without the admin's note or e-mail.
            var note = await _f.DbAsync(db => db.Notifications.AsNoTracking()
                .Where(n => n.DriverId == row.DriverId && n.Type == "cashout_failed")
                .OrderByDescending(n => n.CreatedAt).FirstAsync());
            Assert.Contains("\"code\":\"CANCELLED\"", note.Data);
            Assert.DoesNotContain("@", note.Data);
            Assert.DoesNotContain("by phone", note.Data);

            // Nor does the driver's own cashout history: the API answers with the code only.
            var history = await driverClient.GetFromJsonAsync<JsonElement>("/api/driver/me/cashouts?take=50");
            var listed = history.GetProperty("cashouts").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);
            Assert.Equal("CANCELLED", listed.GetProperty("failureReason").GetString());
            Assert.DoesNotContain("@", listed.GetRawText());
            Assert.DoesNotContain("by phone", listed.GetRawText());

            // Nor does the very same request sent again (a lost response, a double tap, a replay from devtools):
            // the deduplicated result carries the code only.
            var replay = await driverClient.PostAsJsonAsync("/api/driver/cashouts", request);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
            var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(id, replayed.GetProperty("cashoutId").GetGuid());
            Assert.True(replayed.GetProperty("wasDeduped").GetBoolean());
            Assert.Equal("CANCELLED", replayed.GetProperty("failureReason").GetString());
            Assert.DoesNotContain("@", replayed.GetRawText());
            Assert.DoesNotContain("by phone", replayed.GetRawText());

            // A second cancel (another admin, a stale tab) does nothing.
            var swich = await _f.SuperAdminAsync();
            var again = await swich.PostAsJsonAsync(url, new { reason = "duplicate click" });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("already_cancelled", ApiFixture.Code(await again.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(1, (await LedgerAsync(id)).Count(e => e == LedgerEntryType.YandexReversed));

            // Swich may cancel too.
            var second = await QueueForFundsAsync("yp_tb5_004", 5m);
            ids.Add(second);
            var mine = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{second}/cancel", new { reason = "Driver changed their mind" });
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
            Assert.Equal("CANCELLED: Driver changed their mind (by admin:ops@swich.dev)", (await RowAsync(second)).FailureReason);

            // Unknown cashout → 404.
            Assert.Equal(HttpStatusCode.NotFound,
                (await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{Guid.NewGuid()}/cancel", new { reason = "x" })).StatusCode);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task A_transfer_the_bank_already_accepted_cannot_be_cancelled()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            // Money in the account and an asynchronous rail: the order is accepted and pending at the bank.
            Mock.SetBalance(accountId, 1_000m);
            MockOpts.AsyncExecution = true;
            MockOpts.AsyncSettleAfterPolls = 1;
            var (driver, _, _) = await _f.DriverClientAsync("yp_tb5_006");
            var cardId = await DefaultCardAsync(driver);
            var saga = await (await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 5m, idempotencyKey = Guid.NewGuid().ToString() }))
                .Content.ReadFromJsonAsync<JsonElement>();
            var id = saga.GetProperty("cashoutId").GetGuid();
            ids.Add(id);
            Assert.Equal("Processing", saga.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(saga.GetProperty("bankTransferId").GetString()));

            var swich = await _f.SuperAdminAsync();
            var res = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{id}/cancel", new { reason = "too late" });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("not_cancellable", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
            var row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Processing, row.Status);
            Assert.Null(row.YandexReversalTransactionId);

            // Let it finish so the driver is not left in flight.
            await UpdateAsync(id, c => c.NextAttemptAt = DateTime.UtcNow);
            Assert.Equal(CashoutStatus.Completed, (await WaitForAsync(id, r => r.Status == CashoutStatus.Completed)).Status);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    // ── Cancel never reverses a payout an earlier document may already have paid ──

    [Fact]
    public async Task A_lost_response_keeps_cancel_blocked_through_later_definite_answers_until_the_bank_answers_about_that_document()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_005", 6m);
            ids.Add(id);
            var key = (await RowAsync(id)).IdempotencyKey;

            // 1. Document K reaches the bank and is executed — the driver is paid — but the response is lost
            //    and the lookup by document id cannot be answered either.
            var bank = new ScriptedBank { Funded = true, LoseResponse = true, LookupFails = true };
            var first = await StepWithAsync(bank, id);
            Assert.Equal("Queued", first.Status);
            var row = await RowAsync(id);
            Assert.StartsWith("BANK_EXCEPTION:", row.FailureReason);
            Assert.Equal(key, row.UnknownOutcomeDocument);

            // 2. The next attempt gets a definite answer that says nothing about K: the DBI password expired.
            //    The latest reason is no longer "unknown" — but K still is.
            bank.LoseResponse = false;
            bank.LookupFails = false;
            bank.Fault = "TBC_CREDENTIALS_MUST_BE_CHANGED";
            var second = await StepWithAsync(bank, id);
            Assert.Equal("Queued", second.Status);
            row = await RowAsync(id);
            Assert.StartsWith("TBC_CREDENTIALS_MUST_BE_CHANGED:", row.FailureReason);
            Assert.Equal(key, row.UnknownOutcomeDocument);

            // 3. So a cancel (= giving the gross back on Yandex) is still refused: K may have paid.
            var swich = await _f.SuperAdminAsync();
            var cancel = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{id}/cancel", new { reason = "driver asked" });
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            Assert.Equal("outcome_unknown", ApiFixture.Code(await cancel.Content.ReadFromJsonAsync<JsonElement>()));
            row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.Null(row.YandexReversalTransactionId);
            Assert.DoesNotContain(LedgerEntryType.YandexReversed, await LedgerAsync(id));
            var listed = (await swich.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/cashouts?take=100"))
                .GetProperty("cashouts").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);
            Assert.True(listed.GetProperty("outcomeUnknown").GetBoolean(), "the console must not offer Cancel");

            // 4. The bank answers again: the same document goes out, the bank returns order K — paid once, and
            //    only this answer about K clears the marker.
            bank.Fault = null;
            var paid = await StepWithAsync(bank, id);
            Assert.Equal("Completed", paid.Status);
            row = await RowAsync(id);
            Assert.Equal(bank.OrderIdFor(key), row.BankTransferId);
            Assert.Null(row.UnknownOutcomeDocument);
            Assert.Null(row.YandexReversalTransactionId);
            Assert.Equal(new[] { key }, bank.Sent.Distinct().ToArray());
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task A_permanent_refusal_while_an_earlier_document_is_unanswered_goes_to_review_never_to_a_reversal()
    {
        var (_, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_006", 7m);
            ids.Add(id);
            var key = (await RowAsync(id)).IdempotencyKey;

            var bank = new ScriptedBank { Funded = true, LoseResponse = true, LookupFails = true };
            Assert.Equal("Queued", (await StepWithAsync(bank, id)).Status);

            // The bank now refuses for good — but that answer is about this attempt, not about document K,
            // which may have paid. Reversing would pay the driver twice: a human must check K first.
            bank.LoseResponse = false;
            bank.LookupFails = false;
            bank.RejectForGood = true;
            var result = await StepWithAsync(bank, id);
            Assert.Equal("ReviewRequired", result.Status);
            var row = await RowAsync(id);
            Assert.StartsWith("BANK_REJECTED:", row.FailureReason);
            Assert.Contains(key, row.FailureReason);
            Assert.Null(row.YandexReversalTransactionId);
            var ledger = await LedgerAsync(id);
            Assert.Contains(LedgerEntryType.ReviewFlagged, ledger);
            Assert.DoesNotContain(LedgerEntryType.YandexReversed, ledger);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task Cancel_asks_the_bank_about_every_document_of_the_payout_and_refuses_when_one_of_them_exists()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_007", 8m);
            ids.Add(id);
            var key = (await RowAsync(id)).IdempotencyKey;

            // Document K was accepted and refused for lack of funds (dead, paid nothing): the payout moved on to K#1.
            var bank = new ScriptedBank { Funded = false };
            Assert.Equal("Queued", (await StepWithAsync(bank, id)).Status);
            Assert.Equal(1, (await RowAsync(id)).BankDocumentSeq);

            // K#1 exists and was executed at the bank, but nothing on the row says so — a row left by code that
            // predates the marker, or an adapter that misread an answer. The bank itself is the last word.
            bank.Funded = true;
            var planted = await bank.SendPayoutAsync(new BankTransferRequest($"{key}#1",
                new BankAccountContext(parkId, accountId, "mock", "TB", "GE00TB0000000000000000", null, "{}"),
                "GE00TB0000000000000001", "Driver", 8m, "GEL", "planted"));
            Assert.True(planted.Success);

            CashoutCancelRefusedException refused;
            using (var scope = _f.Services.CreateScope())
                refused = await Assert.ThrowsAsync<CashoutCancelRefusedException>(
                    () => Saga(scope, bank).CancelQueuedAsync(parkId, id, "driver asked", "admin:test@swich.dev"));
            Assert.Equal("bank_has_order", refused.Code);
            Assert.Contains(key, bank.LookedUp);
            Assert.Contains($"{key}#1", bank.LookedUp);
            // Back in the queue and due at once (the worker may already be re-trying it with the drained mock).
            var row = await WaitForAsync(id, r => r.Status == CashoutStatus.Queued, 5);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.Null(row.YandexReversalTransactionId);
            Assert.DoesNotContain(LedgerEntryType.YandexReversed, await LedgerAsync(id));
            // The bank's answer is recorded: K#1 is outstanding until the saga hears about it.
            Assert.Equal($"{key}#1", row.UnknownOutcomeDocument);

            // The next attempt re-sends K#1 and the bank returns the executed order: paid once, nothing reversed.
            var paid = await StepWithAsync(bank, id);
            Assert.Equal("Completed", paid.Status);
            row = await RowAsync(id);
            Assert.Equal(bank.OrderIdFor($"{key}#1"), row.BankTransferId);
            Assert.Null(row.UnknownOutcomeDocument);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task Cancel_waits_while_the_bank_cannot_be_asked_and_goes_through_once_it_confirms_every_document_is_dead_or_unknown()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_006", 9m);
            ids.Add(id);
            var key = (await RowAsync(id)).IdempotencyKey;

            // K accepted and refused for lack of funds: dead, and the payout moved on to K#1 (never sent yet).
            var bank = new ScriptedBank { Funded = false };
            Assert.Equal("Queued", (await StepWithAsync(bank, id)).Status);

            // The bank cannot be asked right now: no reversal on faith — the payout keeps waiting, due again.
            bank.LookupFails = true;
            CashoutCancelRefusedException refused;
            using (var scope = _f.Services.CreateScope())
                refused = await Assert.ThrowsAsync<CashoutCancelRefusedException>(
                    () => Saga(scope, bank).CancelQueuedAsync(parkId, id, "driver asked", "admin:test@swich.dev"));
            Assert.Equal("bank_unconfirmed", refused.Code);
            var row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.NotNull(row.NextAttemptAt);
            Assert.Null(row.UnknownOutcomeDocument);
            Assert.Null(row.YandexReversalTransactionId);

            // The bank answers: K is dead, K#1 unknown to it. Now the cancel goes through.
            bank.LookupFails = false;
            bank.LookedUp.Clear();
            await UpdateAsync(id, c => c.NextAttemptAt = DateTime.UtcNow.AddMinutes(10));   // keep the worker off it
            CashoutSagaResult? cancelled;
            using (var scope = _f.Services.CreateScope())
                cancelled = await Saga(scope, bank).CancelQueuedAsync(parkId, id, "driver asked", "admin:test@swich.dev");
            Assert.NotNull(cancelled);
            Assert.Equal("Failed", cancelled!.Status);
            Assert.Equal(new[] { key, $"{key}#1" }, bank.LookedUp.ToArray());
            row = await RowAsync(id);
            Assert.StartsWith("CANCELLED: driver asked", row.FailureReason);
            Assert.NotNull(row.YandexReversalTransactionId);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task A_cancel_whose_Yandex_refund_is_refused_goes_to_review_with_the_debit_kept_and_nothing_credited()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        Guid driverId = Guid.Empty;
        string? profile = null;
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_008", 6m);
            ids.Add(id);
            var queued = await RowAsync(id);
            driverId = queued.DriverId;
            profile = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.Id == driverId)).YandexDriverProfileId);
            var cacheBefore = await _f.DbAsync(db => db.YandexBalanceCaches.AsNoTracking()
                .Where(c => c.DriverId == driverId).Select(c => (decimal?)c.Balance).FirstOrDefaultAsync());

            // While the payout waited, the driver was removed from the park on Yandex: the refund (+gross) is refused.
            await _f.DbAsync(async db =>
            {
                var d = await db.Drivers.FirstAsync(x => x.Id == driverId);
                d.YandexDriverProfileId = $"yp_gone_{Guid.NewGuid():N}"[..20];
                return await db.SaveChangesAsync();
            });

            var swich = await _f.SuperAdminAsync();
            var res = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{id}/cancel", new { reason = "Driver asked to cancel" });
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            Assert.Equal("ReviewRequired", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

            // The cancel stood, but nothing was given back: the debit is kept and a human must settle it.
            var row = await RowAsync(id);
            Assert.Equal(CashoutStatus.ReviewRequired, row.Status);
            Assert.StartsWith("REVERSAL_REJECTED:", row.FailureReason);
            Assert.Contains("CANCELLED", row.FailureReason);
            Assert.Null(row.YandexReversalTransactionId);
            Assert.Null(row.NextAttemptAt);
            var ledger = await LedgerAsync(id);
            Assert.Contains(LedgerEntryType.ReviewFlagged, ledger);
            Assert.DoesNotContain(LedgerEntryType.YandexReversed, ledger);
            Assert.DoesNotContain(LedgerEntryType.CashoutReversed, ledger);
            var cacheAfter = await _f.DbAsync(db => db.YandexBalanceCaches.AsNoTracking()
                .Where(c => c.DriverId == driverId).Select(c => (decimal?)c.Balance).FirstOrDefaultAsync());
            Assert.Equal(cacheBefore, cacheAfter);

            // The driver hears "under review", not "cancelled, money back".
            var note = await _f.DbAsync(db => db.Notifications.AsNoTracking()
                .Where(n => n.DriverId == driverId).OrderByDescending(n => n.CreatedAt).FirstAsync());
            Assert.Equal("cashout_review", note.Type);

            // Nothing more is sent to the bank for it.
            await Task.Delay(1500);
            Assert.Equal(CashoutStatus.ReviewRequired, (await RowAsync(id)).Status);
        }
        finally
        {
            if (profile is not null)
                await _f.DbAsync(async db =>
                {
                    var d = await db.Drivers.FirstAsync(x => x.Id == driverId);
                    d.YandexDriverProfileId = profile;
                    return await db.SaveChangesAsync();
                });
            await undo();
            await CleanUpAsync(ids);
        }
    }

    [Fact]
    public async Task A_cancel_that_arrives_while_the_worker_holds_the_row_waits_for_the_attempt_and_then_cancels()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_003", 6m);
            ids.Add(id);

            // The worker has just claimed the row for an attempt (Queued → Processing, no bank id yet)…
            await UpdateAsync(id, c => { c.Status = CashoutStatus.Processing; c.NextAttemptAt = null; });
            var swich = await _f.SuperAdminAsync();
            var pending = swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{id}/cancel", new { reason = "Driver asked to cancel" });

            // … and a moment later the bank says "insufficient funds" again: back in the queue.
            await Task.Delay(1200);
            await UpdateAsync(id, c => { c.Status = CashoutStatus.Queued; c.NextAttemptAt = DateTime.UtcNow.AddMinutes(10); });

            // The click is not lost to that sub-second race: the cancel waited for the attempt, then went through.
            var res = await pending;
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var row = await RowAsync(id);
            Assert.Equal(CashoutStatus.Failed, row.Status);
            Assert.StartsWith("CANCELLED: Driver asked to cancel", row.FailureReason);
            Assert.NotNull(row.YandexReversalTransactionId);
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }

    // ── Reconciliation knows a payout can wait for days ──────────────

    [Fact]
    public async Task A_payout_that_waited_for_days_raises_no_reconciliation_discrepancy_while_pending_or_after_it_completes()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        try
        {
            var id = await QueueForFundsAsync("yp_tb5_001", 5m);
            ids.Add(id);
            var row = await RowAsync(id);

            // The request — and its Yandex debit — happened four days ago (the reconciliation window is 48 h).
            var fourDaysAgo = DateTime.UtcNow.AddDays(-4);
            await UpdateAsync(id, c => c.CreatedAt = fourDaysAgo);
            var yandexData = _f.Services.GetRequiredService<MockYandexFleetData>();
            var yandexParkId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId)).YandexParkId);
            var profile = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.Id == row.DriverId)).YandexDriverProfileId!);
            var txs = yandexData.Transactions[(yandexParkId, profile)];   // only this test touches this driver's list now
            var i = txs.FindIndex(t => t.TransactionId == row.YandexTransactionId);
            Assert.True(i >= 0, "the mock must hold the debit");
            txs[i] = txs[i] with { CreatedAt = fourDaysAgo };

            // Topped up today on an asynchronous rail: accepted now, still executing.
            MockOpts.AsyncExecution = true;
            MockOpts.AsyncSettleAfterPolls = 1;
            Mock.TopUp(accountId, 50m);
            await UpdateAsync(id, c => c.NextAttemptAt = DateTime.UtcNow);
            row = await WaitForAsync(id, r => r.BankTransferId != null);
            Assert.Equal(CashoutStatus.Processing, row.Status);

            var swich = await _f.SuperAdminAsync();
            async Task<List<string>> KindsForAsync()
            {
                var run = await swich.PostAsync($"/api/admin/parks/{parkId}/reconciliation/run", null);
                Assert.Equal(HttpStatusCode.OK, run.StatusCode);
                return await _f.DbAsync(db => db.ReconciliationDiscrepancies.AsNoTracking()
                    .Where(d => d.CashoutId == id || (d.YandexTransactionId != null && d.YandexTransactionId == row.YandexTransactionId))
                    .Select(d => d.Kind).ToListAsync());
            }

            // Accepted by the bank seconds ago: not "stuck", however old the request.
            Assert.Empty(await KindsForAsync());

            // Executed today; its debit is four days old. Both sides are still found.
            await UpdateAsync(id, c => { if (c.Status == CashoutStatus.Processing) c.NextAttemptAt = DateTime.UtcNow; });
            row = await WaitForAsync(id, r => r.Status == CashoutStatus.Completed);
            Assert.Equal(CashoutStatus.Completed, row.Status);
            Assert.Empty(await KindsForAsync());
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
            await _f.DbAsync(async db =>
            {
                var list = ids.ToList();
                await db.ReconciliationDiscrepancies.Where(d => d.CashoutId != null && list.Contains(d.CashoutId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsResolved, true));
                return 0;
            });
        }
    }

    // ── Visibility: the payout_waiting alert ─────────────────────────

    [Fact]
    public async Task Waiting_payouts_raise_an_alert_for_Swich_and_the_park_with_amount_and_reason_and_it_clears_after_the_drain()
    {
        var (parkId, accountId) = await ParkAsync();
        var undo = DrainAccount(accountId);
        var ids = new List<Guid>();
        static IEnumerable<JsonElement> Waiting(JsonElement body, Guid parkId) => body.GetProperty("alerts").EnumerateArray()
            .Where(a => a.GetProperty("kind").GetString() == "payout_waiting" && a.GetProperty("parkId").GetGuid() == parkId);
        try
        {
            var a = await QueueForFundsAsync("yp_tb5_007", 10m);
            var b = await QueueForFundsAsync("yp_tb5_008", 20m);
            ids.AddRange(new[] { a, b });

            var swich = await _f.SuperAdminAsync();
            var manager = await _f.ParkAdminAsync(Slug);
            var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");

            // A fresh wait is a blip the worker usually clears: no alert in the first 10 minutes.
            Assert.Empty(Waiting(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), parkId));

            await UpdateAsync(a, c => c.CreatedAt = DateTime.UtcNow.AddMinutes(-40));
            await UpdateAsync(b, c => c.CreatedAt = DateTime.UtcNow.AddMinutes(-15));
            var alert = Assert.Single(Waiting(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), parkId));
            Assert.Equal("warning", alert.GetProperty("severity").GetString());
            Assert.Equal(2, alert.GetProperty("count").GetInt32());
            var fee = (await RowAsync(a)).Fee;
            Assert.Equal(30m - 2 * fee, alert.GetProperty("amount").GetDecimal());   // NET: what the account must cover
            Assert.Equal("INSUFFICIENT_FUNDS", alert.GetProperty("code").GetString());
            Assert.Equal("/admin/cashouts?status=queued", alert.GetProperty("link").GetString());

            // The park's manager sees it; another park's manager does not.
            Assert.Single(Waiting(await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), parkId));
            Assert.Empty(Waiting(await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), parkId));

            // Waiting for more than a day is danger.
            await UpdateAsync(a, c => c.CreatedAt = DateTime.UtcNow.AddHours(-25));
            Assert.Equal("danger", Assert.Single(Waiting(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), parkId))
                .GetProperty("severity").GetString());

            // Top up: both go out on their own and the alert is gone.
            await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{accountId}/mock-topup", new { amount = 500m });
            Assert.Equal(CashoutStatus.Completed, (await WaitForAsync(a, r => r.Status == CashoutStatus.Completed)).Status);
            Assert.Equal(CashoutStatus.Completed, (await WaitForAsync(b, r => r.Status == CashoutStatus.Completed)).Status);
            Assert.Empty(Waiting(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), parkId));
        }
        finally
        {
            await undo();
            await CleanUpAsync(ids);
        }
    }
}
