using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>The money path: rejections, concurrency, idempotency, third-party payout, invoice.</summary>
[Collection("api")]
public class CashoutSagaTests
{
    private readonly ApiFixture _f;
    public CashoutSagaTests(ApiFixture f) => _f = f;

    private static async Task<(Guid cardId, decimal balance)> DefaultCardAsync(HttpClient driver)
    {
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var card = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean());
        var bal = me.GetProperty("driver").GetProperty("balance");
        return (card.GetProperty("id").GetGuid(), bal.ValueKind == JsonValueKind.Number ? bal.GetDecimal() : 0m);
    }

    private static Task<HttpResponseMessage> CashoutAsync(HttpClient driver, Guid cardId, decimal amount, string? key = null) =>
        driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount, idempotencyKey = key ?? Guid.NewGuid().ToString() });

    [Fact]
    public async Task Rejections_are_typed_with_params()
    {
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_002");
        var (cardId, balance) = await DefaultCardAsync(driver);
        Assert.True(balance > 0, "seeded mock balance expected");

        var tooMuch = await CashoutAsync(driver, cardId, Math.Round(balance + 100, 2));
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        var b1 = await tooMuch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_balance", b1.GetProperty("code").GetString());
        Assert.Equal(balance, b1.GetProperty("params").GetProperty("balance").GetDecimal());

        var tooLittle = await CashoutAsync(driver, cardId, 0.70m);
        var b2 = await tooLittle.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("below_minimum", b2.GetProperty("code").GetString());
        Assert.True(b2.GetProperty("params").GetProperty("min").GetDecimal() > 0.70m);
    }

    [Fact]
    public async Task Amounts_with_more_than_two_decimals_are_rejected_not_rounded()
    {
        var (driver, driverId, _) = await _f.DriverClientAsync("yp_tb3_002");
        var (cardId, _) = await DefaultCardAsync(driver);
        var key = Guid.NewGuid().ToString();

        var res = await CashoutAsync(driver, cardId, 5.005m, key);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("amount_precision", body.GetProperty("code").GetString());
        // Nothing was reserved: a rounded 5.01 must not exist under that key.
        Assert.False(await _f.DbAsync(db => db.Cashouts.AnyAsync(c => c.IdempotencyKey == key)));
        Assert.False(await _f.DbAsync(db => db.Cashouts.AnyAsync(c => c.DriverId == driverId && c.Amount == 5.01m)));
    }

    [Fact]
    public async Task Idempotency_key_longer_than_128_chars_is_refused_on_both_endpoints()
    {
        var (driver, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_002");
        var (cardId, _) = await DefaultCardAsync(driver);
        var longKey = new string('k', 129);

        var res = await CashoutAsync(driver, cardId, 5m, longKey);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("idempotency_key_too_long", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));

        var admin = await _f.SuperAdminAsync();
        var manual = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts",
            new { driverId, cardId, amount = 5m, idempotencyKey = longKey });
        Assert.Equal(HttpStatusCode.BadRequest, manual.StatusCode);
        Assert.Equal("idempotency_key_too_long", ApiFixture.Code(await manual.Content.ReadFromJsonAsync<JsonElement>()));

        Assert.False(await _f.DbAsync(db => db.Cashouts.AnyAsync(c => c.IdempotencyKey == longKey)));
    }

    [Fact]
    public async Task Manual_cashout_lookups_fail_with_typed_codes_not_a_generic_rejection()
    {
        var admin = await _f.SuperAdminAsync();
        var (driverA, driverAId, levanId) = await _f.DriverClientAsync("yp_tb3_002");
        var (cardA, _) = await DefaultCardAsync(driverA);
        var (driverB, _, _) = await _f.DriverClientAsync("yp_tb3_004");
        var (cardB, _) = await DefaultCardAsync(driverB);
        var batumiId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1")).Id);

        // Levan's driver posted under Batumi's route: the driver is not in that park.
        var wrongPark = await admin.PostAsJsonAsync($"/api/admin/parks/{batumiId}/cashouts",
            new { driverId = driverAId, cardId = cardA, amount = 5m, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPark.StatusCode);
        var b1 = await wrongPark.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cashout_rejected", ApiFixture.Code(b1));
        Assert.Equal("driver_not_found", b1.GetProperty("code").GetString());

        // Right park, right driver, but another driver's destination.
        var wrongCard = await admin.PostAsJsonAsync($"/api/admin/parks/{levanId}/cashouts",
            new { driverId = driverAId, cardId = cardB, amount = 5m, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCard.StatusCode);
        var b2 = await wrongCard.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("destination_not_found", b2.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Retrying_a_failed_cashout_twice_pays_once()
    {
        var (driver, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_002");
        var (cardId, _) = await DefaultCardAsync(driver);

        // A Failed cashout as the abandon path leaves it (Yandex reversed, bank never paid).
        var sourceId = await _f.DbAsync(async db =>
        {
            var c = new PayTaxi.Core.Entities.Cashout
            {
                DriverId = driverId, ParkId = parkId, BankCardId = cardId, Amount = 7m, Fee = 0.5m,
                Status = PayTaxi.Core.Enums.CashoutStatus.Failed, FailureReason = "test: planted failure",
                IdempotencyKey = $"planted:{Guid.NewGuid():N}", InitiatedBy = "test", AttemptCount = 1,
            };
            db.Cashouts.Add(c); await db.SaveChangesAsync(); return c.Id;
        });

        var manager = await _f.ParkAdminAsync("tbilisi-auto-park-3");
        var first = await manager.PostAsync($"/api/admin/parks/{parkId}/cashouts/{sourceId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var r1 = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", r1.GetProperty("status").GetString());
        var retryId = r1.GetProperty("cashoutId").GetGuid();

        // A second click (double tap, second manager, stale tab) must NOT pay the driver again.
        var second = await manager.PostAsync($"/api/admin/parks/{parkId}/cashouts/{sourceId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var r2 = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_retried", ApiFixture.Code(r2));
        Assert.Equal(retryId, r2.GetProperty("retryCashoutId").GetGuid());

        var retries = await _f.DbAsync(db => db.Cashouts.Where(c => c.RetryOfCashoutId == sourceId).ToListAsync());
        Assert.Single(retries);
        Assert.Equal(retryId, retries[0].Id);

        // The list tells the console which failed row has already been retried (so the button goes away)
        // and which row is the retry.
        var list = await manager.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/cashouts?take=200");
        var rows = list.GetProperty("cashouts").EnumerateArray().ToList();
        var src = rows.Single(c => c.GetProperty("id").GetGuid() == sourceId);
        Assert.Equal(retryId, src.GetProperty("retriedByCashoutId").GetGuid());
        var rty = rows.Single(c => c.GetProperty("id").GetGuid() == retryId);
        Assert.Equal(sourceId, rty.GetProperty("retryOfCashoutId").GetGuid());
    }

    [Fact]
    public async Task A_retry_that_failed_too_may_be_retried_again()
    {
        var (driver, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_002");
        var (cardId, _) = await DefaultCardAsync(driver);
        var (sourceId, firstRetryId) = await _f.DbAsync(async db =>
        {
            var src = new PayTaxi.Core.Entities.Cashout
            {
                DriverId = driverId, ParkId = parkId, BankCardId = cardId, Amount = 6m, Fee = 0.5m,
                Status = PayTaxi.Core.Enums.CashoutStatus.Failed, FailureReason = "test", IdempotencyKey = $"planted:{Guid.NewGuid():N}", InitiatedBy = "test",
            };
            db.Cashouts.Add(src); await db.SaveChangesAsync();
            var retry = new PayTaxi.Core.Entities.Cashout
            {
                DriverId = driverId, ParkId = parkId, BankCardId = cardId, Amount = 6m, Fee = 0.5m,
                Status = PayTaxi.Core.Enums.CashoutStatus.Failed, FailureReason = "test: retry failed too",
                IdempotencyKey = $"retry:{src.Id:N}:1", InitiatedBy = "test", RetryOfCashoutId = src.Id,
            };
            db.Cashouts.Add(retry); await db.SaveChangesAsync();
            return (src.Id, retry.Id);
        });

        var admin = await _f.SuperAdminAsync();
        var res = await admin.PostAsync($"/api/admin/parks/{parkId}/cashouts/{sourceId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.NotEqual(firstRetryId, body.GetProperty("cashoutId").GetGuid());
        var live = await _f.DbAsync(db => db.Cashouts.CountAsync(c => c.RetryOfCashoutId == sourceId && c.Status == PayTaxi.Core.Enums.CashoutStatus.Completed));
        Assert.Equal(1, live);
    }

    [Fact]
    public async Task Two_concurrent_requests_never_pay_more_than_the_balance()
    {
        // Each request asks for more than half the balance. However the two interleave — serialised by
        // the per-driver lock (second sees "cashout_in_flight") or fully sequential (second sees
        // "insufficient_balance" because the first debit already landed) — exactly one may complete,
        // and the other must be REFUSED before anything is reserved (a Failed saga row would mean the
        // balance was read outside the lock — the race this test exists to catch).
        var (driver, driverId, _) = await _f.DriverClientAsync("yp_tb3_004");
        // Another test may have left this driver's previous cashout in flight — the lock would then
        // refuse BOTH of ours. Wait for a quiet moment first.
        for (var i = 0; i < 20; i++)
        {
            var inFlight = await _f.DbAsync(db => db.Cashouts.CountAsync(c => c.DriverId == driverId && (c.Status == PayTaxi.Core.Enums.CashoutStatus.Processing || c.Status == PayTaxi.Core.Enums.CashoutStatus.Queued)));
            if (inFlight == 0) break;
            await Task.Delay(250);
        }
        var fresh = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me?fresh=true");
        var balance = fresh.GetProperty("driver").GetProperty("balance").GetDecimal();
        var max = fresh.GetProperty("park").GetProperty("maxCashoutAmount");
        var (cardId, _) = await DefaultCardAsync(driver);
        var amount = Math.Round(balance * 0.6m, 2);
        if (max.ValueKind == JsonValueKind.Number) Assert.True(amount <= max.GetDecimal(), "test needs 60% of balance within the park max");
        Assert.True(amount >= 5m, "seeded balance too small for this test");

        var results = await Task.WhenAll(CashoutAsync(driver, cardId, amount), CashoutAsync(driver, cardId, amount));
        var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));

        var completed = bodies.Count(x => x.TryGetProperty("status", out var st) && st.GetString() == "Completed");
        var refused = bodies.Where(x => x.TryGetProperty("code", out _)).Select(x => x.GetProperty("code").GetString()).ToList();
        var dump = string.Join(" || ", bodies.Select(b => b.ToString()));
        Assert.True(completed == 1, $"expected exactly one Completed, got {completed}: {dump}");
        Assert.True(refused.Count == 1, $"expected exactly one refusal, got {refused.Count}: {dump}");
        Assert.Contains(refused[0], new[] { "cashout_in_flight", "insufficient_balance" });
        Assert.DoesNotContain(bodies, b => b.TryGetProperty("status", out var st) && st.GetString() == "Failed");

        var after = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me?fresh=true");
        Assert.Equal(balance - amount, after.GetProperty("driver").GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Idempotency_is_scoped_to_the_driver()
    {
        var (driverA, _, _) = await _f.DriverClientAsync("yp_tb3_001");
        var (driverB, _, _) = await _f.DriverClientAsync("yp_tb3_007");
        var (cardA, _) = await DefaultCardAsync(driverA);
        var (cardB, _) = await DefaultCardAsync(driverB);
        var key = Guid.NewGuid().ToString();

        var first = await (await CashoutAsync(driverA, cardA, 6m, key)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await CashoutAsync(driverA, cardA, 6m, key)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("cashoutId").GetGuid(), second.GetProperty("cashoutId").GetGuid());
        Assert.True(second.GetProperty("wasDeduped").GetBoolean());

        var other = await CashoutAsync(driverB, cardB, 6m, key);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal("idempotency_key_conflict", (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cashout_to_a_removed_card_is_a_typed_rejection_and_a_foreign_card_is_forbidden()
    {
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_006");
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var name = me.GetProperty("driver").GetProperty("name").GetString();
        var added = await (await driver.PostAsJsonAsync("/api/driver/me/cards",
            new { iban = ApiFixture.TbcIban(DateTime.UtcNow.Ticks.ToString().PadLeft(16, '0')[^16..]), holderName = name }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var cardId = added.GetProperty("id").GetGuid();
        (await driver.DeleteAsync($"/api/driver/me/cards/{cardId}")).EnsureSuccessStatusCode();

        var removed = await CashoutAsync(driver, cardId, 5m);
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
        Assert.Equal("destination_removed", (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var foreign = await CashoutAsync(driver, Guid.NewGuid(), 5m);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Third_party_account_payout_completes_and_is_flagged_in_queue_and_invoice()
    {
        var (driver, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_008");
        var admin = await _f.SuperAdminAsync();
        var iban = ApiFixture.TbcIban(DateTime.UtcNow.Ticks.ToString().PadLeft(16, '0')[^16..]);

        var card = await (await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}/cards",
            new { iban, holderName = "მარიამ გვინიაშვილი", reason = "wife; statement on file", makeDefault = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var cardId = card.GetProperty("id").GetGuid();

        var res = await CashoutAsync(driver, cardId, 7m);
        var saga = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", saga.GetProperty("status").GetString());
        var cashoutId = saga.GetProperty("cashoutId").GetGuid();

        // Queue row: holder + flag.
        var queue = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/cashouts?take=50");
        var row = queue.GetProperty("cashouts").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == cashoutId);
        Assert.True(row.GetProperty("isThirdPartyAccount").GetBoolean());
        Assert.Equal("მარიამ გვინიაშვილი", row.GetProperty("holderName").GetString());

        // Bank order named the holder, not the driver (mock records the destination name).
        var ledgerNotes = await _f.DbAsync(async db => await db.LedgerEntries
            .Where(l => l.CashoutId == cashoutId).Select(l => l.Notes ?? "").ToListAsync());
        Assert.Contains(ledgerNotes, n => n.Contains("→ ****" + iban[^4..]));

        // Invoice renders and is a PDF.
        var pdf = await admin.GetAsync($"/api/admin/parks/{parkId}/cashouts/{cashoutId}/invoice.pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 10_000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));

        await admin.DeleteAsync($"/api/admin/parks/{parkId}/drivers/{driverId}/cards/{cardId}");
    }

    [Fact]
    public async Task Balance_cache_reflects_a_debit_immediately()
    {
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_001");
        var before = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me?fresh=true");
        var bal0 = before.GetProperty("driver").GetProperty("balance").GetDecimal();
        var (cardId, _) = await DefaultCardAsync(driver);

        var saga = await (await CashoutAsync(driver, cardId, 5m)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", saga.GetProperty("status").GetString());

        // Served from the cache (no ?fresh) — must already show the debit.
        var after = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        Assert.Equal(bal0 - 5m, after.GetProperty("driver").GetProperty("balance").GetDecimal());
    }
}
