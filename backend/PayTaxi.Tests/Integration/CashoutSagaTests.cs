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
    public async Task Two_concurrent_requests_never_pay_more_than_the_balance()
    {
        // Each request asks for more than half the balance. However the two interleave — serialised by
        // the per-driver lock (second sees "cashout_in_flight") or fully sequential (second sees
        // "insufficient_balance" because the first debit already landed) — exactly one may complete.
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_004");
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
        Assert.Equal(1, completed);
        Assert.Single(refused);
        Assert.Contains(refused[0], new[] { "cashout_in_flight", "insufficient_balance" });

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
