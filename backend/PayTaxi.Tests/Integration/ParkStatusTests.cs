using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Suspending and reactivating a park from the console. Suspension stops NEW cashouts only:
/// payouts already debited from Yandex still complete, and the monthly settlement and
/// reconciliation keep covering the park. Uses Tbilisi #5 (the flaky-bank host runs in
/// parallel against Tbilisi #3, so that park must never be suspended here).
/// </summary>
[Collection("api")]
public class ParkStatusTests
{
    private const string Slug = "tbilisi-auto-park-5";
    private readonly ApiFixture _f;
    public ParkStatusTests(ApiFixture f) => _f = f;

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == slug)).Id);

    /// <summary>Back to Active whatever a failed assertion left behind.</summary>
    private Task RestoreAsync(Guid parkId) => _f.DbAsync(async db =>
    {
        var p = await db.Parks.FirstAsync(x => x.Id == parkId);
        p.Status = ParkStatus.Active; p.StatusReason = null; p.StatusChangedAt = null; p.StatusChangedBy = null;
        return await db.SaveChangesAsync();
    });

    private static IEnumerable<JsonElement> Alerts(JsonElement body) => body.GetProperty("alerts").EnumerateArray();

    [Fact]
    public async Task Only_Swich_and_the_operator_change_a_park_status_and_every_admin_sees_the_suspension()
    {
        var parkId = await ParkIdAsync(Slug);
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(Slug);
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        var url = $"/api/admin/parks/{parkId}/status";

        try
        {
            // The park's own manager cannot suspend (or reactivate) their park.
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(url, new { status = "Suspended", reason = "x" })).StatusCode);

            // Suspension needs a reason; termination is not offered here.
            var noReason = await swich.PostAsJsonAsync(url, new { status = "Suspended", reason = "  " });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            Assert.Equal("reason_required", ApiFixture.Code(await noReason.Content.ReadFromJsonAsync<JsonElement>()));
            var terminate = await swich.PostAsJsonAsync(url, new { status = "Terminated", reason = "contract ended" });
            Assert.Equal(HttpStatusCode.BadRequest, terminate.StatusCode);
            Assert.Equal("invalid_status", ApiFixture.Code(await terminate.Content.ReadFromJsonAsync<JsonElement>()));
            var tooLong = await swich.PostAsJsonAsync(url, new { status = "Suspended", reason = new string('a', 501) });
            Assert.Equal("reason_too_long", ApiFixture.Code(await tooLong.Content.ReadFromJsonAsync<JsonElement>()));

            // The operator suspends; the trail records who, when and why.
            var res = await op.PostAsJsonAsync(url, new { status = "suspended", reason = "Settlement unpaid for 3 days" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Suspended", body.GetProperty("status").GetString());
            Assert.True(body.GetProperty("changed").GetBoolean());
            Assert.Equal("Settlement unpaid for 3 days", body.GetProperty("statusReason").GetString());
            Assert.Equal("admin:levan@operator.local", body.GetProperty("statusChangedBy").GetString());

            // A second click is a no-op that keeps the first reason.
            var again = await (await swich.PostAsJsonAsync(url, new { status = "Suspended", reason = "other" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(again.GetProperty("changed").GetBoolean());
            Assert.Equal("Settlement unpaid for 3 days", again.GetProperty("statusReason").GetString());

            // Swich sees who did it; the park sees why and when, not who.
            var swichView = (await swich.GetFromJsonAsync<JsonElement>("/api/admin/parks")).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == parkId);
            Assert.Equal("Suspended", swichView.GetProperty("status").GetString());
            Assert.Equal("admin:levan@operator.local", swichView.GetProperty("statusChangedBy").GetString());
            var managerView = (await manager.GetFromJsonAsync<JsonElement>("/api/admin/parks")).EnumerateArray().Single();
            Assert.Equal("Settlement unpaid for 3 days", managerView.GetProperty("statusReason").GetString());
            Assert.Equal(JsonValueKind.Null, managerView.GetProperty("statusChangedBy").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, managerView.GetProperty("statusChangedAt").ValueKind);

            // The console banner carries it for Swich and the park, not for another park's manager.
            var swichAlert = Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"))
                .Single(a => a.GetProperty("kind").GetString() == "park_suspended" && a.GetProperty("parkId").GetGuid() == parkId);
            Assert.Equal("warning", swichAlert.GetProperty("severity").GetString());
            Assert.Equal("Settlement unpaid for 3 days", swichAlert.GetProperty("detail").GetString());
            Assert.Equal("/admin/settings", swichAlert.GetProperty("link").GetString());
            Assert.Contains(Alerts(await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "park_suspended");
            Assert.DoesNotContain(Alerts(await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "park_suspended");

            // Swich reactivates (no reason needed); the alert goes away.
            var back = await (await swich.PostAsJsonAsync(url, new { status = "Active" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Active", back.GetProperty("status").GetString());
            Assert.True(back.GetProperty("changed").GetBoolean());
            Assert.Equal("admin:ops@swich.dev", back.GetProperty("statusChangedBy").GetString());
            Assert.DoesNotContain(Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "park_suspended" && a.GetProperty("parkId").GetGuid() == parkId);
        }
        finally
        {
            await RestoreAsync(parkId);
        }
    }

    [Fact]
    public async Task A_terminated_park_cannot_be_reactivated_from_the_console()
    {
        var parkId = await ParkIdAsync(Slug);
        var swich = await _f.SuperAdminAsync();
        await _f.DbAsync(async db => { (await db.Parks.FirstAsync(p => p.Id == parkId)).Status = ParkStatus.Terminated; return await db.SaveChangesAsync(); });
        try
        {
            var res = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/status", new { status = "Active" });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("park_terminated", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
        }
        finally
        {
            await RestoreAsync(parkId);
        }
    }

    [Fact]
    public async Task Suspension_refuses_new_cashouts_but_the_queued_payout_is_still_paid_and_the_park_still_settles()
    {
        var parkId = await ParkIdAsync(Slug);
        var swich = await _f.SuperAdminAsync();
        var profile = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking()
            .Where(d => d.ParkId == parkId && d.Status == DriverStatus.Active
                     && d.BankCards.Any(b => b.IsDefault && b.IsActive && b.BankCode == "TB"))
            .OrderBy(d => d.YandexDriverProfileId)
            .FirstAsync()).YandexDriverProfileId!);
        var (driver, _, _) = await _f.DriverClientAsync(profile);
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        Assert.False(me.GetProperty("park").GetProperty("cashoutsPaused").GetBoolean());
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

        var bankOpts = _f.Services.GetRequiredService<IOptions<MockBankPayoutOptions>>().Value;
        Guid cashoutId = default;
        try
        {
            // The bank is down: the driver's Yandex balance is debited and the payout waits in the queue.
            bankOpts.OutageUntilUtc = DateTime.UtcNow.AddMinutes(10);
            var res = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 6m, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.Equal(202, (int)res.StatusCode);
            var saga = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Queued", saga.GetProperty("status").GetString());
            cashoutId = saga.GetProperty("cashoutId").GetGuid();

            // Suspend: the response says one payout is still in flight.
            var sus = await (await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/status", new { status = "Suspended", reason = "Compliance review" }))
                .Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Suspended", sus.GetProperty("status").GetString());
            Assert.True(sus.GetProperty("inFlightCount").GetInt32() >= 1);

            // The app knows up front, and a new cashout is refused.
            me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
            Assert.True(me.GetProperty("park").GetProperty("cashoutsPaused").GetBoolean());
            var refused = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 6m, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("park_inactive", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            // The bank comes back: the queued payout completes although the park is suspended
            // (skip the backoff wait rather than sleep through it).
            bankOpts.OutageUntilUtc = null;
            await _f.DbAsync(async db => { (await db.Cashouts.FirstAsync(c => c.Id == cashoutId)).NextAttemptAt = DateTime.UtcNow; return await db.SaveChangesAsync(); });
            var status = "";
            for (var i = 0; i < 40 && status != "Completed"; i++)
            {
                await Task.Delay(250);
                status = await _f.DbAsync(async db => (await db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId)).Status.ToString());
            }
            Assert.Equal("Completed", status);

            // The monthly settlement still covers the suspended park: this month's statement (computed
            // live, nothing persisted) carries the fee of the payout made during the suspension —
            // under its licensee, or on its own if it is a Swich-direct park.
            var fee = await _f.DbAsync(async db => (await db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId)).Fee);
            var month = await swich.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month");
            var inStatement = month.GetProperty("statements").EnumerateArray()
                .SelectMany(st => st.GetProperty("parks").EnumerateArray())
                .Where(pk => pk.GetProperty("parkId").GetGuid() == parkId)
                .Select(pk => pk.GetProperty("fees").GetDecimal());
            var direct = month.GetProperty("direct").EnumerateArray()
                .Where(pk => pk.GetProperty("parkId").GetGuid() == parkId)
                .Select(pk => pk.GetProperty("feeTotal").GetDecimal());
            Assert.True(inStatement.Concat(direct).Single() >= fee, "the suspended park's fees must be in this month's settlement");

            // Reconciliation still covers it (before: 404 park_not_found_or_inactive).
            var rec = await swich.PostAsync($"/api/admin/parks/{parkId}/reconciliation/run", null);
            Assert.Equal(HttpStatusCode.OK, rec.StatusCode);

            // Reactivate: cashouts work again.
            await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/status", new { status = "Active" });
            var ok = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 6m, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.Equal("Completed", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }
        finally
        {
            bankOpts.OutageUntilUtc = null;
            await RestoreAsync(parkId);
        }
    }
}
