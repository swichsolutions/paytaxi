using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The three licence-agreement features working together, in one park's month:
///   the park's account runs dry → a driver's cashout waits instead of failing (Annex 3 §5)
///   → the incident log opens one automatically (P2: one driver waits), recorded as the PARK's fault, not Swich's (Annex 1 §3)
///   → the park tops up → the cashout goes out by itself and the incident closes itself
///   → the month is settled for the licensee with both sides' shared costs, barrier phase (§2.3–2.5)
///   → the transfer finds the park account short → alert + overdue information (§4.1.2)
///   → top-up → retry → settled, and the statement agrees with the ledger.
/// Batumi is borrowed into a test licensee and handed back; the flaky-bank collection never touches it.
/// </summary>
[Collection("api")]
public class ContractScenarioTests
{
    private readonly ApiFixture _f;
    public ContractScenarioTests(ApiFixture f) => _f = f;

    private MockBankPayoutProvider Mock => _f.Services.GetRequiredService<MockBankPayoutProvider>();
    private MockBankPayoutOptions MockOpts => _f.Services.GetRequiredService<IOptions<MockBankPayoutOptions>>().Value;
    private static IEnumerable<JsonElement> Alerts(JsonElement body) => body.GetProperty("alerts").EnumerateArray();
    private static bool IsPark(JsonElement e, Guid parkId) =>
        e.GetProperty("parkId").ValueKind != JsonValueKind.Null && e.GetProperty("parkId").GetGuid() == parkId;

    [Fact]
    public async Task A_park_that_runs_dry_waits_raises_a_park_fault_incident_recovers_and_settles_its_month()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync("batumi-auto-park-1");

        var (parkId, originalLicensee, accounts) = await _f.DbAsync(async db =>
        {
            var p = await db.Parks.AsNoTracking().FirstAsync(x => x.Slug == "batumi-auto-park-1");
            var a = await db.ParkBankAccounts.AsNoTracking().Where(x => x.ParkId == p.Id && x.IsActive).Select(x => x.Id).ToListAsync();
            return (p.Id, p.LicenseeId, a);
        });
        var driverProfile = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking()
            .Where(d => d.ParkId == parkId && d.Status == DriverStatus.Active
                     && d.BankCards.Any(b => b.IsDefault && b.IsActive && b.Iban != ""))
            .OrderBy(d => d.YandexDriverProfileId).FirstAsync()).YandexDriverProfileId!);
        var (driver, _, _) = await _f.DriverClientAsync(driverProfile);
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

        var thisMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var ownerP1Before = (await swich.GetFromJsonAsync<JsonElement>($"/api/admin/incidents/metrics?month={thisMonth}"))
            .GetProperty("counts").GetProperty("ownerP1").GetInt32();

        Guid? licenseeId = null, cashoutId = null, incidentId = null;
        var kit = new SettlementKit(_f);
        try
        {
            // ── 1. The park's account is empty: the cashout waits, the driver's money is reserved ──
            MockOpts.EnforceBalance = true;
            foreach (var a in accounts) Mock.SetBalance(a, 0m);
            var res = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 6m, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var saga = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Queued", saga.GetProperty("status").GetString());
            cashoutId = saga.GetProperty("cashoutId").GetGuid();
            var row = await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId));
            Assert.StartsWith(BankErrorCodes.InsufficientFunds, row.FailureReason);
            Assert.False(string.IsNullOrEmpty(row.YandexTransactionId));
            Assert.Null(row.YandexReversalTransactionId);

            // ── 2. Fifteen minutes later the monitor opens an incident — the park's fault, not the owner's.
            //       One driver waiting is a P2: cashing out still works for the park's other drivers (Annex 1 §2.1) ──
            await _f.DbAsync(async db =>
            {
                var c = await db.Cashouts.FirstAsync(x => x.Id == cashoutId);
                c.CreatedAt = DateTime.UtcNow.AddMinutes(-20);
                return await db.SaveChangesAsync();
            });
            await _f.Services.GetRequiredService<IncidentMonitor>().CheckPayoutQueuesAsync(DateTime.UtcNow, CancellationToken.None);
            var open = (await swich.GetFromJsonAsync<JsonElement>($"/api/admin/incidents?parkId={parkId}&state=open"))
                .GetProperty("incidents").EnumerateArray().Single(i => i.GetProperty("source").GetString() == "Auto");
            incidentId = open.GetProperty("id").GetGuid();
            Assert.Equal("P2", open.GetProperty("level").GetString());
            Assert.Equal("Park", open.GetProperty("fault").GetString());

            // Everyone who should see it does: the waiting payout and the incident, with top-up advice for the park.
            var swichAlerts = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var waiting = Alerts(swichAlerts).Single(a => a.GetProperty("kind").GetString() == "payout_waiting" && IsPark(a, parkId));
            Assert.Equal(BankErrorCodes.InsufficientFunds, waiting.GetProperty("code").GetString());
            Assert.Contains(Alerts(swichAlerts), a => a.GetProperty("kind").GetString() == "incident_open" && IsPark(a, parkId));
            Assert.Contains(Alerts(await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "payout_waiting");

            // ── 3. The park tops up: the cashout goes out on its own, the incident closes on its own ──
            foreach (var a in accounts) Mock.TopUp(a, 1_000m);
            await _f.DbAsync(async db =>
            {
                var c = await db.Cashouts.FirstAsync(x => x.Id == cashoutId);
                c.NextAttemptAt = DateTime.UtcNow;      // skip the backoff wait; the payout worker does the rest
                return await db.SaveChangesAsync();
            });
            var status = "";
            for (var i = 0; i < 60 && status != "Completed"; i++)
            {
                await Task.Delay(250);
                status = await _f.DbAsync(async db => (await db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId)).Status.ToString());
            }
            Assert.Equal("Completed", status);
            Assert.Null((await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId))).YandexReversalTransactionId);

            await _f.Services.GetRequiredService<IncidentMonitor>().CheckPayoutQueuesAsync(DateTime.UtcNow, CancellationToken.None);
            var closed = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/incidents/{incidentId}");
            var incident = closed.TryGetProperty("incident", out var inner) ? inner : closed;
            Assert.Equal("Resolved", incident.GetProperty("status").GetString());
            var ownerP1After = (await swich.GetFromJsonAsync<JsonElement>($"/api/admin/incidents/metrics?month={thisMonth}"))
                .GetProperty("counts").GetProperty("ownerP1").GetInt32();
            Assert.Equal(ownerP1Before, ownerP1After);   // a dry park is not a breach by Swich (Annex 1 §3)

            // ── 4. Month end for a licensee: the fee, both sides' costs, the barrier phase ──
            // Registered through the kit (a second licensee in force is refused), the operator bound to it, launched long ago
            // (the API only takes a launch date before it passes — the kit writes it) — and put in force, the seeded one
            // standing down: its operator works for it only while it is in force.
            licenseeId = await kit.CreateLicenseeAsync(swich, "Scenario licensee", "2024-01-01");
            await kit.InForceAsync(licenseeId.Value);
            Assert.Equal(HttpStatusCode.OK, (await swich.PutAsJsonAsync($"/api/admin/parks/{parkId}/licensee", new { licenseeId })).StatusCode);

            // The cashout is moved into a closed past month of this licensee (a month nothing else uses).
            await _f.DbAsync(async db =>
            {
                var c = await db.Cashouts.FirstAsync(x => x.Id == cashoutId);
                c.CompletedAt = new DateTime(2024, 3, 15, 8, 0, 0, DateTimeKind.Utc);
                c.CreatedAt = c.CompletedAt.Value.AddMinutes(-5);
                // … as if earned then under this licensee's agreement: the regime a fee is settled under is stamped when
                // the cashout completes (it completed above under the park's seeded licensee).
                Assert.True(c.FeeRegimeStamped);
                c.FeeLicenseeId = licenseeId;
                return await db.SaveChangesAsync();
            });
            var expectedFees = await _f.DbAsync(db => db.Cashouts.AsNoTracking()
                .Where(c => c.ParkId == parkId && c.Status == CashoutStatus.Completed && c.SettlementId == null
                         && c.CompletedAt < new DateTime(2024, 3, 31, 20, 0, 0, DateTimeKind.Utc)
                         && (!c.FeeRegimeStamped || c.FeeLicenseeId == licenseeId))
                .SumAsync(c => c.Fee));
            Assert.True(expectedFees >= 0.50m);

            var swichCost = await swich.PostAsJsonAsync($"/api/admin/licensees/{licenseeId}/costs",
                new { category = "Hosting", vendor = "Scenario host", invoiceNumber = "SC-H-" + Guid.NewGuid().ToString("N")[..6], invoiceDate = "2024-03-10", amount = 0.30m });
            Assert.Equal(HttpStatusCode.OK, swichCost.StatusCode);
            var opCost = await op.PostAsJsonAsync($"/api/admin/licensees/{licenseeId}/costs",
                new { category = "Sms", vendor = "Scenario SMS", invoiceNumber = "SC-S-" + Guid.NewGuid().ToString("N")[..6], invoiceDate = "2024-03-12", amount = 0.20m });
            Assert.Equal(HttpStatusCode.OK, opCost.StatusCode);
            // Both entered days before the run — past the 48-hour window in which the other side can hold them.
            foreach (var entered in new[] { swichCost, opCost })
                await new SettlementKit(_f).EnteredHoursAgoAsync((await entered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), 72);

            // The park's account is short again on settlement day.
            foreach (var a in accounts) Mock.SetBalance(a, 0m);
            var run = await swich.PostAsync($"/api/admin/settlements/run?month=2024-03&licenseeId={licenseeId}", null);
            var runBody = await run.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(run.StatusCode == HttpStatusCode.OK, runBody.ToString());
            var st = runBody.GetProperty("statements").EnumerateArray().Single();
            Assert.Equal("Barrier", st.GetProperty("phase").GetString());
            Assert.Equal(expectedFees, st.GetProperty("feeIncome").GetDecimal());
            Assert.Equal(0.20m, st.GetProperty("licenseeCosts").GetDecimal());
            Assert.Equal(0.30m, st.GetProperty("swichCosts").GetDecimal());
            Assert.Equal(expectedFees - 0.20m, st.GetProperty("due").GetDecimal());   // barrier phase: F − licensee costs
            var line = st.GetProperty("parks").EnumerateArray().Single(p => p.GetProperty("parkId").GetGuid() == parkId);
            Assert.Equal("Failed", line.GetProperty("status").GetString());
            var settlementId = line.GetProperty("settlementId").GetGuid();
            Assert.NotEqual(JsonValueKind.Null, line.GetProperty("overdue").ValueKind);
            Assert.True(line.GetProperty("overdue").GetProperty("daysOverdue").GetInt32() > 0);

            var failedAlert = Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"))
                .Single(a => a.GetProperty("kind").GetString() == "settlement_failed" && IsPark(a, parkId));
            Assert.Contains(failedAlert.GetProperty("code").GetString(), new[] { "INSUFFICIENT_PARK_BALANCE", BankErrorCodes.InsufficientFunds });

            // ── 5. Top-up, retry: settled, and the statement agrees with the ledger ──
            foreach (var a in accounts) Mock.TopUp(a, 1_000m);
            var retry = await swich.PostAsync($"/api/admin/settlements/{settlementId}/retry", null);
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            var settled = await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == settlementId));
            Assert.Equal(SettlementStatus.Completed, settled.Status);
            Assert.Equal(expectedFees - 0.20m, settled.SwichShare);
            var sent = await _f.DbAsync(db => db.LedgerEntries.AsNoTracking()
                .Where(l => l.SettlementId == settlementId && l.EntryType == LedgerEntryType.SettlementSent).SumAsync(l => l.Amount));
            Assert.Equal(settled.SwichShare, sent);
            var after = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/month?month=2024-03");
            var stAfter = after.GetProperty("statements").EnumerateArray().Single(s => s.GetProperty("licenseeId").GetGuid() == licenseeId);
            Assert.Equal("Completed", stAfter.GetProperty("status").GetString());
            Assert.Equal(0m, stAfter.GetProperty("outstanding").GetDecimal());
            Assert.DoesNotContain(Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "settlement_failed" && IsPark(a, parkId));
        }
        finally
        {
            MockOpts.EnforceBalance = false;
            foreach (var a in accounts) Mock.SetBalance(a, 25_000m);
            await _f.DbAsync(async db =>
            {
                (await db.Parks.FirstAsync(p => p.Id == parkId)).LicenseeId = originalLicensee;
                if (licenseeId is { } l) (await db.Licensees.FirstAsync(x => x.Id == l)).IsActive = false;
                if (incidentId is { } i)
                {
                    await db.IncidentUpdates.Where(u => u.IncidentId == i).ExecuteDeleteAsync();
                    await db.Incidents.Where(x => x.Id == i).ExecuteDeleteAsync();
                }
                return await db.SaveChangesAsync();
            });
            await kit.ReturnAsync();
        }
    }
}
