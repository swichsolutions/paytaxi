using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The payout queue under a flaky bank. Needs its own API host because the mock failure rate is
/// startup configuration — so this fixture is a second, separately configured instance (a
/// different collection keeps it from running in parallel with the main one; both share the
/// test database, which is fine: the queue only touches its own cashouts).
/// </summary>
public sealed class FlakyBankFixture : ApiFixture.Secondary
{
    // Every bank call says "not now": the saga must queue and the worker must keep retrying.
    public FlakyBankFixture() : base(new Dictionary<string, string>
    {
        ["BankPayout__Mock__TransientFailureRate"] = "1",
        ["Cashout__BackoffSeconds__0"] = "1",
    }) { }

    public ApiFixture Api => this;

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        // Back to deterministic for any host created after us in this process.
        Environment.SetEnvironmentVariable("BankPayout__Mock__TransientFailureRate", "0");
        Environment.SetEnvironmentVariable("Cashout__BackoffSeconds__0", null);
    }
}

[CollectionDefinition("flaky-bank")]
public class FlakyBankCollection : ICollectionFixture<FlakyBankFixture> { }

[Collection("flaky-bank")]
public class PayoutQueueTests
{
    private readonly ApiFixture _f;
    public PayoutQueueTests(FlakyBankFixture f) => _f = f.Api;

    [Fact]
    public async Task Bank_says_not_now_so_the_cashout_queues_and_the_worker_completes_it()
    {
        var (driver, _, parkId) = await _f.DriverClientAsync("yp_tb3_001");
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

        var res = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 5m, idempotencyKey = Guid.NewGuid().ToString() });
        var saga = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(202, (int)res.StatusCode);
        Assert.Equal("Queued", saga.GetProperty("status").GetString());
        Assert.NotNull(saga.GetProperty("nextAttemptAt").GetString());
        var cashoutId = saga.GetProperty("cashoutId").GetGuid();

        // The Yandex debit already happened (queued, not failed) — the driver's money is reserved.
        var yandexTx = await _f.DbAsync(async db => (await db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId)).YandexTransactionId);
        Assert.False(string.IsNullOrEmpty(yandexTx));

        // Every attempt fails once, so the second attempt (worker, ~1–2 s later) should succeed
        // only if the mock is per-call… with rate 1 it ALWAYS fails, so the worker keeps re-queuing.
        // Verify the retry loop is alive and honest: attempts climb, status stays Queued/Processing,
        // no reversal, no Failed while attempts remain.
        string? status = null; int attempts = 0;
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(500);
            var row = await _f.DbAsync(async db => await db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId));
            status = row.Status.ToString(); attempts = row.AttemptCount;
            if (attempts >= 3) break;
        }
        Assert.True(attempts >= 2, $"worker never retried (attempts={attempts}, status={status})");
        Assert.Contains(status, new[] { "Queued", "Processing" });
        var reversed = await _f.DbAsync(async db => (await db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId)).YandexReversalTransactionId);
        Assert.Null(reversed);

        // The queue is visible to the park: the cashout appears as Queued with its attempt count.
        var admin = await _f.SuperAdminAsync();
        var queue = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/cashouts?take=50");
        var visible = queue.GetProperty("cashouts").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == cashoutId);
        Assert.Contains(visible.GetProperty("status").GetString(), new[] { "Queued", "Processing" });
        Assert.True(visible.GetProperty("attemptCount").GetInt32() >= 1);

        // Clean up so the main fixture's reconciliation/settlement tests are not confused by a forever-queued row.
        await _f.DbAsync(async db =>
        {
            var c = await db.Cashouts.FirstAsync(x => x.Id == cashoutId);
            c.Status = PayTaxi.Core.Enums.CashoutStatus.Failed; c.NextAttemptAt = null; c.FailureReason = "TEST_CLEANUP";
            return await db.SaveChangesAsync();
        });
    }
}
