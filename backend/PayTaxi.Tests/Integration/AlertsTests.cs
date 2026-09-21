using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>The "something needs a human" feed behind the console banner.</summary>
[Collection("api")]
public class AlertsTests
{
    private readonly ApiFixture _f;
    public AlertsTests(ApiFixture f) => _f = f;

    private static IEnumerable<JsonElement> Alerts(JsonElement body) => body.GetProperty("alerts").EnumerateArray();

    /// <summary>Plant a failed settlement for a park (as the nightly worker would leave it) and return its id.</summary>
    private async Task<Guid> PlantFailedSettlementAsync(Guid parkId, decimal swichShare, string reason)
    {
        return await _f.DbAsync(async db =>
        {
            var s = new Settlement
            {
                ParkId = parkId,
                SettlementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
                PeriodFromUtc = DateTime.UtcNow.AddDays(-4), PeriodToUtc = DateTime.UtcNow.AddDays(-3),
                CashoutCount = 3, FeeTotal = swichShare * 2, SwichShare = swichShare, ParkShare = swichShare,
                Status = SettlementStatus.Failed, FailureReason = reason,
                IdempotencyKey = "settle:test:" + Guid.NewGuid().ToString("N"),
                Description = "PayTaxi settlement (test)", InvoiceRef = "PT-TEST-" + Guid.NewGuid().ToString("N")[..6],
                AttemptCount = 1, LastAttemptAt = DateTime.UtcNow.AddHours(-20),
            };
            db.Settlements.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
    }

    private Task RemoveSettlementAsync(Guid id) =>
        _f.DbAsync(async db => { var s = await db.Settlements.FindAsync(id); if (s != null) db.Settlements.Remove(s); return await db.SaveChangesAsync(); });

    [Fact]
    public async Task Failed_settlement_raises_a_danger_alert_scoped_by_role_and_the_signature_tracks_the_set()
    {
        var (tbilisi, batumi) = await _f.DbAsync(async db => (
            (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3")).Id,
            (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1")).Id));
        var swich = await _f.SuperAdminAsync();
        var operatorClient = await _f.OperatorAsync();
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");

        var before = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
        var sigBefore = before.GetProperty("signature").GetString();

        var planted = await PlantFailedSettlementAsync(tbilisi, 12.50m, "INSUFFICIENT_PARK_BALANCE: Park account balance 3.00 GEL is below the settlement amount 12.50 GEL");
        try
        {
            // Swich sees it, with the code the UI turns into advice and a link to the settlements page.
            var body = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var alert = Alerts(body).Single(a => a.GetProperty("kind").GetString() == "settlement_failed" && a.GetProperty("parkId").GetGuid() == tbilisi);
            Assert.Equal("danger", alert.GetProperty("severity").GetString());
            Assert.Equal(1, alert.GetProperty("count").GetInt32());
            Assert.Equal(12.50m, alert.GetProperty("amount").GetDecimal());
            Assert.Equal("INSUFFICIENT_PARK_BALANCE", alert.GetProperty("code").GetString());
            Assert.Equal("/admin/settlements", alert.GetProperty("link").GetString());
            Assert.True(body.GetProperty("dangerCount").GetInt32() >= 1);
            Assert.NotEqual(sigBefore, body.GetProperty("signature").GetString());

            // The operator sees every park too.
            var op = await operatorClient.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.Contains(Alerts(op), a => a.GetProperty("kind").GetString() == "settlement_failed" && a.GetProperty("parkId").GetGuid() == tbilisi);

            // Batumi's manager does NOT see Tbilisi's failure.
            var bm = await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.DoesNotContain(Alerts(bm), a => a.GetProperty("parkId").GetGuid() == tbilisi);

            // Same set → same signature (so a dismissal sticks); a second failure → new signature.
            var again = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.Equal(body.GetProperty("signature").GetString(), again.GetProperty("signature").GetString());
            var second = await PlantFailedSettlementAsync(batumi, 4m, "NO_PARK_ACCOUNT: Park has no active payout account to settle from");
            try
            {
                var changed = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
                Assert.NotEqual(body.GetProperty("signature").GetString(), changed.GetProperty("signature").GetString());
                Assert.Equal(2, Alerts(changed).Count(a => a.GetProperty("kind").GetString() == "settlement_failed"));
                // …and Batumi's own manager now sees exactly their park's.
                var bm2 = await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
                Assert.Single(Alerts(bm2).Where(a => a.GetProperty("kind").GetString() == "settlement_failed"));
            }
            finally { await RemoveSettlementAsync(second); }
        }
        finally { await RemoveSettlementAsync(planted); }

        // Cleared → signature returns to the pre-test value (nothing else changed in between).
        var after = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
        Assert.Equal(sigBefore, after.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Review_required_cashouts_and_open_discrepancies_are_reported()
    {
        var swich = await _f.SuperAdminAsync();
        var tbilisi = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3")).Id);

        // Make a completed cashout, then park it as ReviewRequired the way the saga would (status flip only).
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_002");
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        var saga = await (await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 5m, idempotencyKey = Guid.NewGuid().ToString() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", saga.GetProperty("status").GetString());
        var cashoutId = saga.GetProperty("cashoutId").GetGuid();
        await _f.DbAsync(async db =>
        {
            var c = await db.Cashouts.FirstAsync(x => x.Id == cashoutId);
            c.Status = CashoutStatus.ReviewRequired; c.FailureReason = "BANK_PENDING_TIMEOUT: still not final after 12.3 h";
            return await db.SaveChangesAsync();
        });
        try
        {
            var body = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var review = Alerts(body).Single(a => a.GetProperty("kind").GetString() == "cashout_review" && a.GetProperty("parkId").GetGuid() == tbilisi);
            Assert.Equal("danger", review.GetProperty("severity").GetString());
            Assert.Equal("BANK_PENDING_TIMEOUT", review.GetProperty("code").GetString());
            Assert.Equal("/admin/cashouts?status=review", review.GetProperty("link").GetString());

            // Open discrepancies (the reconciliation tests leave some) are warnings, not dangers.
            var open = await _f.DbAsync(db => db.ReconciliationDiscrepancies.CountAsync(d => d.ParkId == tbilisi && !d.IsResolved));
            var recon = Alerts(body).SingleOrDefault(a => a.GetProperty("kind").GetString() == "reconciliation_open" && a.GetProperty("parkId").GetGuid() == tbilisi);
            if (open > 0)
            {
                Assert.Equal("warning", recon.GetProperty("severity").GetString());
                Assert.Equal(open, recon.GetProperty("count").GetInt32());
            }
            else Assert.Equal(JsonValueKind.Undefined, recon.ValueKind);
        }
        finally
        {
            await _f.DbAsync(async db => { var c = await db.Cashouts.FirstAsync(x => x.Id == cashoutId); c.Status = CashoutStatus.Completed; c.FailureReason = null; return await db.SaveChangesAsync(); });
        }
    }

    [Fact]
    public async Task Alerts_need_an_admin_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client.GetAsync("/api/admin/alerts")).StatusCode);
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_001");
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.GetAsync("/api/admin/alerts")).StatusCode);
    }
}
