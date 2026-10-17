using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The Annex 1 incident log: who may do what, the append-only timeline, the monitor opening and closing
/// incidents on its own, the monthly contract metrics and the banner. Every test removes the incidents it
/// created (anything numbered above the high-water mark it started from), so the alert tests that compare
/// banner signatures see the same state as before.
/// </summary>
[Collection("api")]
public class IncidentsTests
{
    private readonly ApiFixture _f;
    public IncidentsTests(ApiFixture f) => _f = f;

    private const string Base = "/api/admin/incidents";

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == slug)).Id);

    private Task<int> HighWaterAsync() =>
        _f.DbAsync(async db => await db.Incidents.MaxAsync(i => (int?)i.Number) ?? 0);

    private Task RemoveIncidentsAboveAsync(int number) =>
        _f.DbAsync(async db =>
        {
            db.Incidents.RemoveRange(await db.Incidents.Where(i => i.Number > number).ToListAsync());
            return await db.SaveChangesAsync();
        });

    private static IEnumerable<JsonElement> Timeline(JsonElement detail) => detail.GetProperty("timeline").EnumerateArray();
    private static JsonElement Inc(JsonElement detail) => detail.GetProperty("incident");

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static IEnumerable<JsonElement> Alerts(JsonElement body) => body.GetProperty("alerts").EnumerateArray();

    [Fact]
    public async Task Role_matrix_and_timeline_from_report_to_resolution()
    {
        var mark = await HighWaterAsync();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        var tb5Manager = await _f.ParkAdminAsync("tbilisi-auto-park-5");
        try
        {
            // ── The licensee reports what it sees; the response clock starts now ──
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            var res = await licensee.PostAsJsonAsync(Base, new { title = "Batumi drivers cannot cash out", level = "P1", parkId = batumi, startedAt, description = "Since 14:00, every attempt fails." });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var created = await Json(res);
            var inc = Inc(created);
            var id = inc.GetProperty("id").GetGuid();
            Assert.Matches("^INC-\\d{4,}$", inc.GetProperty("ref").GetString());
            Assert.Equal("Reported", inc.GetProperty("status").GetString());
            Assert.Equal("P1", inc.GetProperty("level").GetString());
            Assert.Equal("Unknown", inc.GetProperty("fault").GetString());
            Assert.Equal("Manual", inc.GetProperty("source").GetString());
            Assert.Equal("Batumi Auto Park #1", inc.GetProperty("parkName").GetString());
            Assert.Equal("admin:levan@operator.local", inc.GetProperty("reportedBy").GetString());
            Assert.Equal("Pending", inc.GetProperty("sla").GetProperty("response").GetProperty("state").GetString());
            Assert.InRange(inc.GetProperty("durationMinutes").GetDouble(), 29, 31);
            var reported = Assert.Single(Timeline(created));
            Assert.Equal("reported", reported.GetProperty("kind").GetString());
            Assert.Equal("admin:levan@operator.local", reported.GetProperty("by").GetString());
            Assert.False(created.GetProperty("can").GetProperty("acknowledge").GetBoolean());
            Assert.True(created.GetProperty("can").GetProperty("note").GetBoolean());

            // ── What the licensee may NOT do: fault, cause, the response itself, closing, editing ──
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PostAsJsonAsync(Base, new { title = "x", level = "P2", fault = "Platform" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PostAsJsonAsync(Base, new { title = "x", level = "P2", cause = "db" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PostAsJsonAsync($"{Base}/{id}/acknowledge", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PostAsJsonAsync($"{Base}/{id}/resolve", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PostAsJsonAsync($"{Base}/{id}/reopen", new { text = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PostAsJsonAsync($"{Base}/{id}/external-restored", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await licensee.PatchAsJsonAsync($"{Base}/{id}", new { level = "P3" })).StatusCode);
            // …but it may add notes.
            var noted = await licensee.PostAsJsonAsync($"{Base}/{id}/notes", new { text = "Called Swich support, no answer yet." });
            Assert.Equal(HttpStatusCode.OK, noted.StatusCode);

            // ── Park managers: read-only, own park + platform-wide, staff e-mails hidden ──
            var asManager = await Json(await batumiManager.GetAsync($"{Base}/{id}"));
            Assert.Equal("admin", Inc(asManager).GetProperty("reportedBy").GetString());
            Assert.All(Timeline(asManager), e => Assert.DoesNotContain("@", e.GetProperty("by").GetString()));
            Assert.False(asManager.GetProperty("can").GetProperty("note").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await batumiManager.PostAsJsonAsync(Base, new { title = "x", level = "P2" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await batumiManager.PostAsJsonAsync($"{Base}/{id}/notes", new { text = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await batumiManager.GetAsync($"{Base}/metrics")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await tb5Manager.GetAsync($"{Base}/{id}")).StatusCode);
            var tb5List = await Json(await tb5Manager.GetAsync(Base));
            Assert.DoesNotContain(tb5List.GetProperty("incidents").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
            var batumiList = await Json(await batumiManager.GetAsync($"{Base}?state=open"));
            Assert.Contains(batumiList.GetProperty("incidents").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

            // ── Swich: cause and fault, the response, the external restoration, resolution ──
            var patched = await Json(await swich.PatchAsJsonAsync($"{Base}/{id}", new { fault = "External", cause = "TBC gateway returns 503", level = "P1" }));
            Assert.True(patched.GetProperty("changed").GetBoolean());
            var noop = await Json(await swich.PatchAsJsonAsync($"{Base}/{id}", new { fault = "External", level = "P1" }));
            Assert.False(noop.GetProperty("changed").GetBoolean());

            var acked = await Json(await swich.PostAsJsonAsync($"{Base}/{id}/acknowledge", new { text = "On it — TBC is down, we are in touch with them." }));
            Assert.Equal("Acknowledged", Inc(acked).GetProperty("status").GetString());
            Assert.Equal("Met", Inc(acked).GetProperty("sla").GetProperty("response").GetProperty("state").GetString());
            Assert.Equal("Paused", Inc(acked).GetProperty("sla").GetProperty("resolution").GetProperty("state").GetString());
            Assert.Equal("bank", Inc(acked).GetProperty("sla").GetProperty("resolution").GetProperty("waitingFor").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await swich.PostAsJsonAsync($"{Base}/{id}/acknowledge", new { })).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest,
                (await swich.PostAsJsonAsync($"{Base}/{id}/external-restored", new { at = DateTimeOffset.UtcNow.AddHours(-2) })).StatusCode); // before the impact began
            var restored = await Json(await swich.PostAsJsonAsync($"{Base}/{id}/external-restored", new { }));
            var resolution = Inc(restored).GetProperty("sla").GetProperty("resolution");
            Assert.Equal("Pending", resolution.GetProperty("state").GetString());
            Assert.InRange(resolution.GetProperty("marginMinutes").GetDouble(), 24 * 60 - 2, 24 * 60);
            Assert.Equal(HttpStatusCode.Conflict, (await swich.PostAsJsonAsync($"{Base}/{id}/external-restored", new { })).StatusCode);

            var resolved = await Json(await swich.PostAsJsonAsync($"{Base}/{id}/resolve", new { text = "Queue worked off." }));
            Assert.Equal("Resolved", Inc(resolved).GetProperty("status").GetString());
            Assert.Equal("Met", Inc(resolved).GetProperty("sla").GetProperty("resolution").GetProperty("state").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await swich.PostAsJsonAsync($"{Base}/{id}/resolve", new { })).StatusCode);
            Assert.True(resolved.GetProperty("can").GetProperty("reopen").GetBoolean());

            // Reopen needs a reason, goes back to Acknowledged (it was acknowledged before).
            Assert.Equal(HttpStatusCode.BadRequest, (await swich.PostAsJsonAsync($"{Base}/{id}/reopen", new { text = " " })).StatusCode);
            var reopened = await Json(await swich.PostAsJsonAsync($"{Base}/{id}/reopen", new { text = "Drivers still report failures." }));
            Assert.Equal("Acknowledged", Inc(reopened).GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, Inc(reopened).GetProperty("resolvedAt").ValueKind);
            await swich.PostAsJsonAsync($"{Base}/{id}/resolve", new { });

            // ── The timeline: every step, in order, with the actor from the token ──
            var final = await Json(await licensee.GetAsync($"{Base}/{id}"));
            var kinds = Timeline(final).Select(e => e.GetProperty("kind").GetString()).ToList();
            // The cause is an entry of its own (park managers never read it), after the fault change it came with.
            Assert.Equal(new[] { "reported", "note", "updated", "updated", "acknowledged", "external_restored", "resolved", "reopened", "resolved" }, kinds);
            var updates = Timeline(final).Where(e => e.GetProperty("kind").GetString() == "updated").ToList();
            Assert.Contains("Fault Unknown → External", updates[0].GetProperty("text").GetString());
            Assert.DoesNotContain("Cause", updates[0].GetProperty("text").GetString());
            Assert.Equal("Cause: TBC gateway returns 503", updates[1].GetProperty("text").GetString());
            Assert.All(updates, u => Assert.Equal("admin:ops@swich.dev", u.GetProperty("by").GetString()));
            // Batumi's manager reads the fault change, not the cause.
            var managerView = await Json(await batumiManager.GetAsync($"{Base}/{id}"));
            Assert.DoesNotContain("TBC gateway returns 503", managerView.ToString());
            Assert.Single(Timeline(managerView), e => e.GetProperty("kind").GetString() == "updated");
            Assert.Equal("admin:levan@operator.local", Timeline(final).Single(e => e.GetProperty("kind").GetString() == "note").GetProperty("by").GetString());
            Assert.Equal("Drivers still report failures.", Timeline(final).Single(e => e.GetProperty("kind").GetString() == "reopened").GetProperty("text").GetString());

            // ── A platform-wide incident reaches every park manager ──
            var wide = Inc(await Json(await swich.PostAsJsonAsync(Base, new { title = "Admin console slow", level = "P3", acknowledge = true })));
            Assert.Equal("Acknowledged", wide.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, wide.GetProperty("parkId").ValueKind);
            Assert.Equal(HttpStatusCode.OK, (await tb5Manager.GetAsync($"{Base}/{wide.GetProperty("id").GetGuid()}")).StatusCode);
            var platformOnly = await Json(await swich.GetAsync($"{Base}?parkId=platform&level=P3"));
            Assert.All(platformOnly.GetProperty("incidents").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("parkId").ValueKind));
            Assert.Contains(platformOnly.GetProperty("incidents").EnumerateArray(), i => i.GetProperty("id").GetGuid() == wide.GetProperty("id").GetGuid());

            // ── Validation ──
            Assert.Equal("title_required", ApiFixture.Code(await Json(await swich.PostAsJsonAsync(Base, new { title = " ", level = "P2" }))));
            Assert.Equal("invalid_level", ApiFixture.Code(await Json(await swich.PostAsJsonAsync(Base, new { title = "x", level = "P4" }))));
            Assert.Equal("invalid_level", ApiFixture.Code(await Json(await swich.PostAsJsonAsync(Base, new { title = "x", level = "1" }))));
            Assert.Equal("started_in_future", ApiFixture.Code(await Json(await swich.PostAsJsonAsync(Base, new { title = "x", level = "P2", startedAt = DateTimeOffset.UtcNow.AddHours(1) }))));
            Assert.Equal("park_not_found", ApiFixture.Code(await Json(await swich.PostAsJsonAsync(Base, new { title = "x", level = "P2", parkId = Guid.NewGuid() }))));
            Assert.Equal("invalid_month", ApiFixture.Code(await Json(await swich.GetAsync($"{Base}?month=2026-13"))));
            Assert.Equal(HttpStatusCode.NotFound, (await swich.GetAsync($"{Base}/{Guid.NewGuid()}")).StatusCode);

            // ── Not for drivers or anonymous callers ──
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Client.GetAsync(Base)).StatusCode);
            var (driver, _, _) = await _f.DriverClientAsync("yp_bt1_001");
            Assert.Equal(HttpStatusCode.Forbidden, (await driver.GetAsync(Base)).StatusCode);
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    [Fact]
    public async Task Open_P1_is_a_danger_alert_P2_a_warning_P3_stays_off_the_banner()
    {
        var mark = await HighWaterAsync();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        var tb5Manager = await _f.ParkAdminAsync("tbilisi-auto-park-5");
        static JsonElement? Line(JsonElement body, Guid? parkId) => Alerts(body)
            .Where(a => a.GetProperty("kind").GetString() == "incident_open"
                     && (parkId is null ? a.GetProperty("parkId").ValueKind == JsonValueKind.Null
                                        : a.GetProperty("parkId").ValueKind != JsonValueKind.Null && a.GetProperty("parkId").GetGuid() == parkId))
            .Select(a => (JsonElement?)a).SingleOrDefault();
        try
        {
            var p3 = Inc(await Json(await licensee.PostAsJsonAsync(Base, new { title = "Typo on the receipt", level = "P3", parkId = batumi })));
            Assert.Null(Line(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), batumi));

            var p2 = Inc(await Json(await licensee.PostAsJsonAsync(Base, new { title = "Some BoG payouts slow", level = "P2", parkId = batumi })));
            var warn = Line(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), batumi)!.Value;
            Assert.Equal("warning", warn.GetProperty("severity").GetString());
            Assert.Equal("P2_REPORTED", warn.GetProperty("code").GetString());
            Assert.Equal(1, warn.GetProperty("count").GetInt32());
            Assert.Equal("/admin/incidents", warn.GetProperty("link").GetString());

            var p1 = Inc(await Json(await licensee.PostAsJsonAsync(Base, new { title = "No cashouts in Batumi", level = "P1", parkId = batumi })));
            var danger = Line(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), batumi)!.Value;
            Assert.Equal("danger", danger.GetProperty("severity").GetString());
            Assert.Equal("P1_REPORTED", danger.GetProperty("code").GetString());
            Assert.Equal(2, danger.GetProperty("count").GetInt32());     // P1 + P2; the P3 is not counted
            Assert.Equal("No cashouts in Batumi", danger.GetProperty("detail").GetString());

            // Scoped like every other alert: Batumi's manager sees it, Tbilisi #5's does not.
            Assert.NotNull(Line(await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), batumi));
            Assert.Null(Line(await tb5Manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), batumi));

            // Once Swich has responded to both, the banner says so.
            await swich.PostAsJsonAsync($"{Base}/{p1.GetProperty("id").GetGuid()}/acknowledge", new { });
            await swich.PostAsJsonAsync($"{Base}/{p2.GetProperty("id").GetGuid()}/acknowledge", new { });
            Assert.Equal("P1_ACKNOWLEDGED", Line(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), batumi)!.Value.GetProperty("code").GetString());

            // A platform-wide P1 is one line without a park — and park managers see it too.
            var wide = Inc(await Json(await swich.PostAsJsonAsync(Base, new { title = "Login broken everywhere", level = "P1" })));
            var wideLine = Line(await tb5Manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"), null)!.Value;
            Assert.Equal("danger", wideLine.GetProperty("severity").GetString());
            Assert.Equal(JsonValueKind.Null, wideLine.GetProperty("parkName").ValueKind);

            // Resolved → gone.
            foreach (var i in new[] { p1, p2, p3, wide })
                await swich.PostAsJsonAsync($"{Base}/{i.GetProperty("id").GetGuid()}/resolve", new { });
            var after = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.DoesNotContain(Alerts(after), a => a.GetProperty("kind").GetString() == "incident_open");
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    [Fact]
    public async Task Monitor_opens_one_incident_for_a_stuck_payout_queue_and_resolves_it_when_the_queue_drains()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var oldest = DateTime.UtcNow.AddMinutes(-25);
        var planted = new List<Guid>();

        // Payouts the bank keeps refusing, as the saga leaves them. NextAttemptAt far ahead so the payout
        // worker (on in the test host) leaves them alone.
        async Task<Guid> PlantQueuedAsync(Guid parkId, DateTime createdAt, string reason) => await _f.DbAsync(async db =>
        {
            var driver = await db.Drivers.AsNoTracking().Where(d => d.ParkId == parkId && d.BankCards.Any()).OrderBy(d => d.YandexDriverProfileId).FirstAsync();
            var card = await db.BankCards.AsNoTracking().FirstAsync(b => b.DriverId == driver.Id);
            var c = new Cashout
            {
                DriverId = driver.Id, ParkId = parkId, BankCardId = card.Id, Amount = 20m, Fee = 0.5m,
                Status = CashoutStatus.Queued, IdempotencyKey = "incident-test:" + Guid.NewGuid().ToString("N"),
                FailureReason = reason, AttemptCount = 3, CreatedAt = createdAt, UpdatedAt = createdAt,
                NextAttemptAt = DateTime.UtcNow.AddDays(1), InitiatedBy = "test",
            };
            db.Cashouts.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        Task<List<Incident>> AutoIncidentsAsync(Guid parkId) => _f.DbAsync(db => db.Incidents.AsNoTracking()
            .Where(i => i.Number > mark && i.DedupeKey == IncidentMonitor.PayoutQueueKeyPrefix + parkId).ToListAsync());

        try
        {
            planted.Add(await PlantQueuedAsync(tb5, oldest, "BANK_UNAVAILABLE: Bank gateway temporarily unavailable"));
            planted.Add(await PlantQueuedAsync(tb5, oldest.AddMinutes(5), "BANK_UNAVAILABLE: Bank gateway temporarily unavailable"));
            planted.Add(await PlantQueuedAsync(tb5, oldest.AddMinutes(8), "INSUFFICIENT_FUNDS: 3.00 GEL on the account"));
            // Batumi: queued only 5 minutes — not an incident yet.
            planted.Add(await PlantQueuedAsync(batumi, DateTime.UtcNow.AddMinutes(-5), "BANK_UNAVAILABLE: Bank gateway temporarily unavailable"));

            await monitor.RunOnceAsync(default);
            await monitor.RunOnceAsync(default);                         // a second pass must not open another

            var opened = Assert.Single(await AutoIncidentsAsync(tb5));
            // One driver's payouts: degraded, not "business stopped" (Annex 1 §2.1). The escalation test has the P1.
            Assert.Equal(IncidentLevel.P2, opened.Level);
            Assert.Equal(IncidentStatus.Reported, opened.Status);       // never auto-acknowledged
            Assert.Equal(IncidentFault.External, opened.Fault);         // BANK_* is the most common code
            Assert.Equal(IncidentSource.Auto, opened.Source);
            Assert.Equal(IncidentMonitor.SystemActor, opened.ReportedBy);
            Assert.InRange((opened.StartedAt - oldest).Duration().TotalSeconds, 0, 1);
            Assert.StartsWith("BANK_UNAVAILABLE", opened.Cause);
            Assert.Empty(await AutoIncidentsAsync(batumi));

            // The console shows it with its auto kind and the monitor as the reporter; the banner has it.
            var swich = await _f.SuperAdminAsync();
            var detail = await swich.GetFromJsonAsync<JsonElement>($"{Base}/{opened.Id}");
            Assert.Equal("payout_queue", Inc(detail).GetProperty("autoKind").GetString());
            Assert.Equal("system", Timeline(detail).Single().GetProperty("by").GetString());
            var alerts = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.Contains(Alerts(alerts), a => a.GetProperty("kind").GetString() == "incident_open"
                && a.GetProperty("parkId").ValueKind != JsonValueKind.Null && a.GetProperty("parkId").GetGuid() == tb5
                && a.GetProperty("severity").GetString() == "warning");

            // The bank recovers: the worker pays everything out (here: the rows go away).
            await _f.DbAsync(async db =>
            {
                db.Cashouts.RemoveRange(await db.Cashouts.Where(c => c.ParkId == tb5 && planted.Contains(c.Id)).ToListAsync());
                return await db.SaveChangesAsync();
            });
            await monitor.RunOnceAsync(default);

            var closed = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).FirstAsync(i => i.Id == opened.Id));
            Assert.Equal(IncidentStatus.Resolved, closed.Status);
            Assert.Equal(IncidentMonitor.SystemActor, closed.ResolvedBy);
            Assert.NotNull(closed.ExternalRestoredAt);
            Assert.Null(closed.AcknowledgedAt);
            Assert.Equal(new[] { "reported", "external_restored", "resolved" }, closed.Updates.OrderBy(u => u.Id).Select(u => u.Kind));
            Assert.All(closed.Updates, u => Assert.Equal("system", u.By));

            // Batumi's payout is now old enough → its own incident; the resolved Tbilisi key is free again.
            await _f.DbAsync(async db =>
            {
                var c = await db.Cashouts.FirstAsync(x => x.ParkId == batumi && planted.Contains(x.Id));
                c.CreatedAt = DateTime.UtcNow.AddMinutes(-16);
                return await db.SaveChangesAsync();
            });
            await monitor.RunOnceAsync(default);
            Assert.Single(await AutoIncidentsAsync(batumi));
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                db.Cashouts.RemoveRange(await db.Cashouts.Where(c => planted.Contains(c.Id)).ToListAsync());
                return await db.SaveChangesAsync();
            });
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    [Fact]
    public async Task Reopening_an_auto_incident_detaches_it_from_the_monitor()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var key = IncidentMonitor.PayoutQueueKeyPrefix + batumi;
        try
        {
            // As the monitor would have left it: opened and closed again.
            var id = await _f.DbAsync(async db =>
            {
                var now = DateTime.UtcNow;
                var i = new Incident
                {
                    Level = IncidentLevel.P1, Status = IncidentStatus.Resolved, Title = "Payouts stuck in the queue — Batumi",
                    Fault = IncidentFault.External, ParkId = batumi, StartedAt = now.AddHours(-1), ReportedAt = now.AddMinutes(-40),
                    ReportedBy = "system", ResolvedAt = now.AddMinutes(-5), ResolvedBy = "system", Source = IncidentSource.Auto, DedupeKey = key,
                };
                db.Incidents.Add(i);
                await db.SaveChangesAsync();
                return i.Id;
            });
            var swich = await _f.SuperAdminAsync();
            var reopened = await swich.PostAsJsonAsync($"{Base}/{id}/reopen", new { text = "Drivers say payouts still do not arrive." });
            Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
            var body = await Json(reopened);
            Assert.Equal("Reported", Inc(body).GetProperty("status").GetString());   // never acknowledged
            Assert.Equal("payout_queue", Inc(body).GetProperty("autoKind").GetString());
            Assert.Contains("No longer closed automatically", Timeline(body).Last().GetProperty("text").GetString());

            // Batumi has nothing queued, but the monitor no longer owns this incident: it stays open.
            await monitor.RunOnceAsync(default);
            var row = await _f.DbAsync(db => db.Incidents.AsNoTracking().FirstAsync(i => i.Id == id));
            Assert.NotEqual(IncidentStatus.Resolved, row.Status);
            Assert.NotEqual(key, row.DedupeKey);
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    [Fact]
    public async Task Monitor_opens_a_platform_wide_P1_when_Yandex_fails_and_closes_it_when_a_call_succeeds()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        // The detector reads the last 10 minutes of the audit log. A window three hours ahead holds only the
        // rows planted here, whatever the rest of the suite has been calling.
        var now = DateTime.UtcNow.AddHours(3);
        var planted = new List<long>();
        async Task PlantAsync(DateTime at, bool success, int code) => planted.Add(await _f.DbAsync(async db =>
        {
            var row = new ApiAuditLog
            {
                Timestamp = at, ApiProvider = "YandexFleet", Endpoint = "/v1/parks/driver-profiles/list", Method = "POST",
                ActorType = "System", ResponseCode = code, Success = success, CorrelationId = "incident-test",
            };
            db.ApiAuditLogs.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }));
        Task<Incident?> OpenOutageAsync() => _f.DbAsync(db => db.Incidents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Number > mark && i.DedupeKey == IncidentMonitor.YandexOutageKey));

        try
        {
            for (var k = 0; k < 4; k++) await PlantAsync(now.AddMinutes(-8 + k), false, 503);
            await PlantAsync(now.AddMinutes(-3), false, 403);              // read-only switch: not an outage
            Assert.Equal(0, await monitor.CheckYandexOutageAsync(now, default));

            await PlantAsync(now.AddMinutes(-2), false, 500);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now, default));
            var opened = await OpenOutageAsync();
            Assert.NotNull(opened);
            Assert.Null(opened!.ParkId);
            Assert.Equal(IncidentLevel.P1, opened.Level);
            Assert.Equal(IncidentFault.External, opened.Fault);
            Assert.InRange((opened.StartedAt - now.AddMinutes(-8)).Duration().TotalSeconds, 0, 1);
            Assert.Equal(0, await monitor.CheckYandexOutageAsync(now.AddMinutes(1), default)); // still down, no duplicate

            await PlantAsync(now.AddMinutes(2), true, 200);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now.AddMinutes(3), default));
            var closed = await _f.DbAsync(db => db.Incidents.AsNoTracking().FirstAsync(i => i.Id == opened.Id));
            Assert.Equal(IncidentStatus.Resolved, closed.Status);
            Assert.InRange((closed.ExternalRestoredAt!.Value - now.AddMinutes(2)).Duration().TotalSeconds, 0, 1);

            var swich = await _f.SuperAdminAsync();
            var detail = await swich.GetFromJsonAsync<JsonElement>($"{Base}/{opened.Id}");
            Assert.Equal("yandex_outage", Inc(detail).GetProperty("autoKind").GetString());
            // Decided: requests are refused while Yandex does not answer, never queued — the record says so, not just the description.
            var reportedEntry = Timeline(detail).First();
            Assert.Equal("auto_yandex_opened", reportedEntry.GetProperty("data").GetProperty("code").GetString());
            Assert.Contains("refused", reportedEntry.GetProperty("text").GetString());
            Assert.Contains("nothing is queued", reportedEntry.GetProperty("text").GetString());
            Assert.Contains("refused", detail.GetProperty("description").GetString());
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                db.ApiAuditLogs.RemoveRange(await db.ApiAuditLogs.Where(a => planted.Contains(a.Id)).ToListAsync());
                return await db.SaveChangesAsync();
            });
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    [Fact]
    public async Task Monthly_metrics_flag_chronic_breach_and_abandonment_for_the_licensee_only()
    {
        var mark = await HighWaterAsync();
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        // March 2025: before anything else in the test database. All times mid-month UTC.
        DateTime At(int day, int hour) => new(2025, 3, day, hour, 0, 0, DateTimeKind.Utc);
        Incident P1(DateTime reported, double hours, IncidentFault fault, bool ack = true) => new()
        {
            Level = IncidentLevel.P1, Status = IncidentStatus.Resolved, Title = "metrics test", Fault = fault,
            StartedAt = reported, ReportedAt = reported, ReportedBy = "admin:test",
            AcknowledgedAt = ack ? reported.AddHours(1) : null, AcknowledgedBy = ack ? "admin:test" : null,
            ResolvedAt = reported.AddHours(hours), ResolvedBy = "admin:test",
        };
        try
        {
            await _f.DbAsync(async db =>
            {
                db.Incidents.AddRange(
                    P1(At(3, 10), 30, IncidentFault.Platform),
                    P1(At(10, 10), 26, IncidentFault.Platform),
                    P1(At(17, 10), 5, IncidentFault.Platform),
                    P1(At(20, 10), 80, IncidentFault.External, ack: false));   // external — but unanswered for 80 h
                return await db.SaveChangesAsync();
            });

            var m = await licensee.GetFromJsonAsync<JsonElement>($"{Base}/metrics?month=2025-03");
            Assert.Equal("2025-03", m.GetProperty("month").GetString());
            Assert.Equal(4, m.GetProperty("counts").GetProperty("p1").GetInt32());
            Assert.Equal(3, m.GetProperty("counts").GetProperty("ownerP1").GetInt32());
            Assert.Equal(2, m.GetProperty("ownerP1Missed").GetArrayLength());
            Assert.Equal(61, m.GetProperty("ownerP1Hours").GetDouble(), 3);
            Assert.False(m.GetProperty("chronicBreach").GetProperty("byCount").GetBoolean());   // 2 < 3
            Assert.True(m.GetProperty("chronicBreach").GetProperty("byHours").GetBoolean());    // 61 > 48
            Assert.True(m.GetProperty("chronicBreach").GetProperty("flag").GetBoolean());
            Assert.True(m.GetProperty("abandonment").GetProperty("flag").GetBoolean());
            Assert.Equal(80, m.GetProperty("abandonment").GetProperty("unanswered")[0].GetProperty("hours").GetDouble(), 3);
            Assert.Equal(3, m.GetProperty("thresholds").GetProperty("chronicP1Count").GetInt32());
            // §7 reads the launch date from the licensee record (the seeded licensee holds the parks), not configuration.
            var seededLaunch = await _f.DbAsync(async db => (await db.Licensees.AsNoTracking()
                .Where(l => l.IsActive && l.LaunchDate != null)
                .OrderByDescending(l => l.Parks.Count).FirstAsync()).LaunchDate!.Value);
            Assert.Equal(seededLaunch.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                m.GetProperty("trial").GetProperty("launchDate").GetString());
            Assert.NotEqual("not_set", m.GetProperty("trial").GetProperty("state").GetString());

            // The month after is clean; the list filter by month finds exactly the four.
            var april = await swich.GetFromJsonAsync<JsonElement>($"{Base}/metrics?month=2025-04");
            Assert.False(april.GetProperty("chronicBreach").GetProperty("flag").GetBoolean());
            Assert.False(april.GetProperty("abandonment").GetProperty("flag").GetBoolean());
            var march = await swich.GetFromJsonAsync<JsonElement>($"{Base}?month=2025-03&state=resolved");
            Assert.Equal(4, march.GetProperty("count").GetInt32());
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Review fixes
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A payout as the saga leaves it when the bank keeps refusing: Queued, debited on Yandex, the reason
    /// recorded. NextAttemptAt far ahead so the payout worker (on in the test host) leaves it alone.
    /// </summary>
    private Task<Guid> PlantQueuedAsync(Guid driverId, DateTime createdAt, string reason) => _f.DbAsync(async db =>
    {
        var driver = await db.Drivers.AsNoTracking().FirstAsync(d => d.Id == driverId);
        var card = await db.BankCards.AsNoTracking().FirstAsync(b => b.DriverId == driverId);
        var c = new Cashout
        {
            DriverId = driverId, ParkId = driver.ParkId, BankCardId = card.Id, Amount = 20m, Fee = 0.5m,
            Status = CashoutStatus.Queued, IdempotencyKey = "incident-test:" + Guid.NewGuid().ToString("N"),
            FailureReason = reason, AttemptCount = 3, CreatedAt = createdAt, UpdatedAt = createdAt,
            NextAttemptAt = DateTime.UtcNow.AddDays(1), InitiatedBy = "test",
        };
        db.Cashouts.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    });

    private Task RemoveCashoutsAsync(IReadOnlyCollection<Guid> ids) => _f.DbAsync(async db =>
    {
        db.Cashouts.RemoveRange(await db.Cashouts.Where(c => ids.Contains(c.Id)).ToListAsync());
        return await db.SaveChangesAsync();
    });

    private Task<List<Guid>> DriversWithCardsAsync(Guid parkId) => _f.DbAsync(db => db.Drivers.AsNoTracking()
        .Where(d => d.ParkId == parkId && d.BankCards.Any()).OrderBy(d => d.YandexDriverProfileId).Select(d => d.Id).ToListAsync());

    private Task<Incident?> OpenQueueIncidentAsync(int mark, Guid parkId) => _f.DbAsync(db => db.Incidents.AsNoTracking()
        .Include(i => i.Updates)
        .FirstOrDefaultAsync(i => i.Number > mark && i.Status != IncidentStatus.Resolved
                               && i.DedupeKey == IncidentMonitor.PayoutQueueKeyPrefix + parkId));

    private static JsonElement DataOf(IncidentUpdate u) => JsonDocument.Parse(u.Data!).RootElement;

    /// <summary>The licence agreement's launch date lives on the licensee record; a test may move it and puts it back.</summary>
    private async Task<T> WithLaunchDateAsync<T>(DateOnly launch, Func<Task<T>> body)
    {
        var (licenseeId, original) = await _f.DbAsync(async db =>
        {
            var l = await db.Licensees.AsNoTracking().Where(x => x.IsActive && x.LaunchDate != null)
                .OrderByDescending(x => x.Parks.Count).FirstAsync();
            return (l.Id, l.LaunchDate);
        });
        await _f.DbAsync(async db => { (await db.Licensees.FirstAsync(l => l.Id == licenseeId)).LaunchDate = launch; return await db.SaveChangesAsync(); });
        try { return await body(); }
        finally
        {
            await _f.DbAsync(async db => { (await db.Licensees.FirstAsync(l => l.Id == licenseeId)).LaunchDate = original; return await db.SaveChangesAsync(); });
        }
    }

    [Fact]
    public async Task Month_filter_outside_the_supported_years_is_a_400_never_a_500()
    {
        var swich = await _f.SuperAdminAsync();
        var manager = await _f.ParkAdminAsync("batumi-auto-park-1");
        // 9999-12 used to reach new DateTime(9999,12,1).AddMonths(1) and throw — for any admin, park managers included.
        foreach (var month in new[] { "9999-12", "0001-01", "1999-12", "2101-01" })
        {
            var list = await manager.GetAsync($"{Base}?month={month}");
            Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
            Assert.Equal("invalid_month", ApiFixture.Code(await Json(list)));
            var metrics = await swich.GetAsync($"{Base}/metrics?month={month}");
            Assert.Equal(HttpStatusCode.BadRequest, metrics.StatusCode);
            Assert.Equal("invalid_month", ApiFixture.Code(await Json(metrics)));
        }
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{Base}?month=2100-12")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await swich.GetAsync($"{Base}/metrics?month=2000-01")).StatusCode);
    }

    [Fact]
    public async Task The_licensee_backdates_the_impact_start_at_most_a_few_days_Swich_within_its_own_bound()
    {
        var mark = await HighWaterAsync();
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            // The review's scenario: an October report "since 1 January" would put months of P1 hours on §5(b).
            var res = await licensee.PostAsJsonAsync(Base, new { title = "Payout delays", level = "P1", startedAt = now.AddDays(-30) });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var refused = await Json(res);
            Assert.Equal("started_too_early", ApiFixture.Code(refused));
            Assert.Equal(72, refused.GetProperty("maxHours").GetDouble());
            Assert.Equal(HttpStatusCode.Created,
                (await licensee.PostAsJsonAsync(Base, new { title = "Payout delays", level = "P2", startedAt = now.AddHours(-70) })).StatusCode);

            // Swich may go further back — it only lengthens its own P1 hours — but a year's typo is refused.
            var owner = await swich.PostAsJsonAsync(Base, new { title = "Found in the post-mortem", level = "P2", startedAt = now.AddDays(-10) });
            Assert.Equal(HttpStatusCode.Created, owner.StatusCode);
            var id = Inc(await Json(owner)).GetProperty("id").GetGuid();
            Assert.Equal("started_too_early",
                ApiFixture.Code(await Json(await swich.PostAsJsonAsync(Base, new { title = "x", level = "P2", startedAt = now.AddYears(-1) }))));
            var patch = await swich.PatchAsJsonAsync($"{Base}/{id}", new { startedAt = now.AddYears(-1) });
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
            Assert.Equal("started_too_early", ApiFixture.Code(await Json(patch)));
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"{Base}/{id}", new { startedAt = now.AddDays(-20) })).StatusCode);
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    [Fact]
    public async Task A_queue_stuck_on_a_missing_bank_adapter_is_the_owners_fault_and_its_clock_never_pauses()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var planted = new List<Guid>();
        try
        {
            // A BOG account: every payout throws NotImplementedException and is requeued as BANK_ADAPTER_MISSING.
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25),
                "BANK_ADAPTER_MISSING: The method or operation is not implemented."));
            // Credentials the bank refuses: nobody knows yet whose problem that is. (Two rows, so the code
            // dominates whatever another test may have waiting at Batumi.)
            foreach (var minutes in new[] { 25, 24 })
                planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-minutes),
                    "TBC_NOT_CONFIGURED: TBC credentials must include username and password"));
            await monitor.RunOnceAsync(default);

            var adapter = (await OpenQueueIncidentAsync(mark, tb5))!;
            Assert.Equal(IncidentFault.Platform, adapter.Fault);
            Assert.Equal(IncidentFault.Unknown, (await OpenQueueIncidentAsync(mark, batumi))!.Fault);

            var swich = await _f.SuperAdminAsync();
            var detail = await swich.GetFromJsonAsync<JsonElement>($"{Base}/{adapter.Id}");
            Assert.Equal("Pending", Inc(detail).GetProperty("sla").GetProperty("resolution").GetProperty("state").GetString());
            Assert.False(detail.GetProperty("can").GetProperty("restoreExternal").GetBoolean());

            // Someone moves the account and the queue drains: resolved, but with no "external service back" —
            // the platform-side time counts from the report.
            await RemoveCashoutsAsync(planted);
            await monitor.RunOnceAsync(default);
            var closed = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).FirstAsync(i => i.Id == adapter.Id));
            Assert.Equal(IncidentStatus.Resolved, closed.Status);
            Assert.Null(closed.ExternalRestoredAt);
            Assert.Equal(new[] { "reported", "resolved" }, closed.Updates.OrderBy(u => u.Id).Select(u => u.Kind));
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    [Fact]
    public async Task Monitor_levels_a_stuck_queue_by_impact_and_escalates_when_most_of_the_park_waits_unless_Swich_set_the_level()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var drivers = await DriversWithCardsAsync(tb5);
        Assert.True(drivers.Count >= 3, "the seed gives Tbilisi #5 at least three drivers with cards");
        var planted = new List<Guid>();
        const string reason = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        var swich = await _f.SuperAdminAsync();
        try
        {
            // ── One driver's payouts (two of them) have waited 20+ minutes: degraded, not "business stopped" ──
            planted.Add(await PlantQueuedAsync(drivers[0], DateTime.UtcNow.AddMinutes(-25), reason));
            planted.Add(await PlantQueuedAsync(drivers[0], DateTime.UtcNow.AddMinutes(-20), reason));
            await monitor.RunOnceAsync(default);
            var opened = (await OpenQueueIncidentAsync(mark, tb5))!;
            Assert.Equal(IncidentLevel.P2, opened.Level);
            var reported = DataOf(opened.Updates.Single());
            Assert.Equal("auto_queue_opened", reported.GetProperty("code").GetString());
            Assert.Equal(1, reported.GetProperty("drivers").GetInt32());
            Assert.Equal(2, reported.GetProperty("payouts").GetInt32());
            Assert.Equal("P2", reported.GetProperty("level").GetString());
            var line = Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"))
                .Single(a => a.GetProperty("kind").GetString() == "incident_open" && a.GetProperty("parkId").ValueKind != JsonValueKind.Null && a.GetProperty("parkId").GetGuid() == tb5);
            Assert.Equal("warning", line.GetProperty("severity").GetString());
            Assert.Equal(opened.ReportedAt, opened.ReachedP2At);
            Assert.Null(opened.ReachedP1At);

            // A long wait: the P2 has been open for 60 hours (pushed back here) and nobody has answered it yet.
            var reportedLongAgo = await _f.DbAsync(async db =>
            {
                var i = await db.Incidents.FirstAsync(x => x.Id == opened.Id);
                i.ReportedAt = i.ReportedAt.AddHours(-60);
                i.StartedAt = i.StartedAt.AddHours(-60);
                i.ReachedP2At = i.ReportedAt;
                await db.SaveChangesAsync();
                return i.ReportedAt;
            });

            // ── Every driver of the park now waits (the bank is down for all of them): P1, the same incident ──
            foreach (var d in drivers.Skip(1)) planted.Add(await PlantQueuedAsync(d, DateTime.UtcNow.AddMinutes(-2), reason));
            await monitor.RunOnceAsync(default);
            var escalated = (await OpenQueueIncidentAsync(mark, tb5))!;
            Assert.Equal(opened.Id, escalated.Id);
            Assert.Equal(IncidentLevel.P1, escalated.Level);
            Assert.InRange((escalated.ReportedAt - reportedLongAgo).Duration().TotalSeconds, 0, 1);   // the report stays where it was
            // …but it has been a P1 only since now: the P1 response is not "56 hours overdue" the moment it is raised.
            Assert.NotNull(escalated.ReachedP1At);
            Assert.InRange((DateTime.UtcNow - escalated.ReachedP1At!.Value).TotalMinutes, -1, 2);
            var p1 = Inc(await swich.GetFromJsonAsync<JsonElement>($"{Base}/{opened.Id}"));
            Assert.Equal("Pending", p1.GetProperty("sla").GetProperty("response").GetProperty("state").GetString());
            Assert.InRange((p1.GetProperty("sla").GetProperty("response").GetProperty("startsAt").GetDateTime() - escalated.ReachedP1At.Value).Duration().TotalSeconds, 0, 1);
            Assert.InRange((p1.GetProperty("levelSince").GetDateTime() - escalated.ReachedP1At.Value).Duration().TotalSeconds, 0, 1);
            var up = escalated.Updates.OrderBy(u => u.Id).Last();
            Assert.Equal(("updated", IncidentMonitor.SystemActor), (up.Kind, up.By));
            Assert.Equal("auto_escalated", DataOf(up).GetProperty("code").GetString());
            Assert.Equal(drivers.Count, DataOf(up).GetProperty("drivers").GetInt32());
            Assert.Contains("P2 → P1", up.Text);
            await monitor.RunOnceAsync(default);                           // nothing more to do
            Assert.Equal(2, (await OpenQueueIncidentAsync(mark, tb5))!.Updates.Count);

            // ── Swich judges it degraded after all: the level is the owner's (§2.1), the monitor keeps out ──
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"{Base}/{opened.Id}", new { level = "P2" })).StatusCode);
            await monitor.RunOnceAsync(default);
            Assert.Equal(IncidentLevel.P2, (await OpenQueueIncidentAsync(mark, tb5))!.Level);
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    [Fact]
    public async Task Contract_metrics_leave_out_incidents_of_parks_Swich_connected_directly()
    {
        var mark = await HighWaterAsync();
        var licensee = await _f.OperatorAsync();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var tb5Licensee = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Id == tb5)).LicenseeId);
        // February 2025, also the trial window here: nothing else in the test database is that old.
        DateTime At(int day) => new(2025, 2, day, 10, 0, 0, DateTimeKind.Utc);
        Incident Missed(DateTime reported, Guid? parkId) => new()
        {
            Level = IncidentLevel.P1, Status = IncidentStatus.Resolved, Title = "scope test", Fault = IncidentFault.Platform,
            ParkId = parkId, StartedAt = reported, ReportedAt = reported, ReportedBy = "admin:test",
            AcknowledgedAt = reported.AddHours(1), AcknowledgedBy = "admin:test", ResolvedAt = reported.AddHours(30), ResolvedBy = "admin:test",
        };
        try
        {
            await _f.DbAsync(async db =>
            {
                (await db.Parks.FirstAsync(p => p.Id == tb5)).LicenseeId = null;   // Swich connected it itself (§2.2)
                db.Incidents.AddRange(
                    Missed(At(3), tb5), Missed(At(4), tb5), Missed(At(5), tb5),   // a direct park's bad month
                    Missed(At(20), batumi),                                         // the licensee's park
                    Missed(At(21), null));                                          // every park
                return await db.SaveChangesAsync();
            });

            var m = await WithLaunchDateAsync(new DateOnly(2025, 2, 1),
                () => licensee.GetFromJsonAsync<JsonElement>($"{Base}/metrics?month=2025-02"));
            Assert.Equal(2, m.GetProperty("counts").GetProperty("p1").GetInt32());
            Assert.Equal(2, m.GetProperty("counts").GetProperty("ownerP1").GetInt32());
            Assert.Equal(2, m.GetProperty("ownerP1Missed").GetArrayLength());
            Assert.Equal(60, m.GetProperty("ownerP1Hours").GetDouble(), 3);
            Assert.False(m.GetProperty("chronicBreach").GetProperty("byCount").GetBoolean());   // 2 < 3: tb5's three are outside the agreement
            // §7: the direct park's misses inside the trial window do not release the licensee either.
            Assert.Equal("ended", m.GetProperty("trial").GetProperty("state").GetString());
            Assert.False(m.GetProperty("trial").GetProperty("licenseeReleased").GetBoolean());
        }
        finally
        {
            await _f.DbAsync(async db => { (await db.Parks.FirstAsync(p => p.Id == tb5)).LicenseeId = tb5Licensee; return await db.SaveChangesAsync(); });
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    [Fact]
    public async Task The_trial_card_cites_a_P1_whose_fault_is_not_determined_instead_of_a_definite_no()
    {
        var mark = await HighWaterAsync();
        var licensee = await _f.OperatorAsync();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var reported = new DateTime(2025, 2, 4, 10, 0, 0, DateTimeKind.Utc);   // day 3 of a trial from 1 Feb 2025
        try
        {
            // The licensee reported it, it took 30 h, Swich never set the fault.
            var id = await _f.DbAsync(async db =>
            {
                var i = new Incident
                {
                    Level = IncidentLevel.P1, Status = IncidentStatus.Resolved, Title = "trial test", Fault = IncidentFault.Unknown,
                    ParkId = batumi, StartedAt = reported, ReportedAt = reported, ReportedBy = "admin:levan@operator.local",
                    AcknowledgedAt = reported.AddHours(2), AcknowledgedBy = "admin:test", ResolvedAt = reported.AddHours(30), ResolvedBy = "admin:test",
                };
                db.Incidents.Add(i);
                await db.SaveChangesAsync();
                return i.Id;
            });

            var trial = (await WithLaunchDateAsync(new DateOnly(2025, 2, 1),
                () => licensee.GetFromJsonAsync<JsonElement>($"{Base}/metrics?month=2025-02"))).GetProperty("trial");
            Assert.False(trial.GetProperty("licenseeReleased").GetBoolean());
            var cited = Assert.Single(trial.GetProperty("undeterminedMissed").EnumerateArray());
            Assert.Equal(id, cited.GetProperty("id").GetGuid());
            Assert.Equal("Missed", cited.GetProperty("resolution").GetString());
            Assert.Equal(0, trial.GetProperty("undeterminedOpen").GetInt32());
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    [Fact]
    public async Task System_written_timeline_entries_carry_a_code_and_parameters_the_console_translates()
    {
        var mark = await HighWaterAsync();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        static JsonElement Entry(JsonElement detail, string kind) => Timeline(detail).Last(e => e.GetProperty("kind").GetString() == kind);
        static string? CodeOfData(JsonElement entry) =>
            entry.GetProperty("data").ValueKind == JsonValueKind.Object ? entry.GetProperty("data").GetProperty("code").GetString() : null;
        try
        {
            var created = await Json(await licensee.PostAsJsonAsync(Base,
                new { title = "Batumi drivers cannot cash out", level = "P1", parkId = batumi, startedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }));
            var id = Inc(created).GetProperty("id").GetGuid();
            var rep = Entry(created, "reported");
            Assert.Equal("reported", CodeOfData(rep));
            var data = rep.GetProperty("data");
            Assert.Equal("P1", data.GetProperty("level").GetString());
            Assert.Equal("Batumi Auto Park #1", data.GetProperty("park").GetString());
            Assert.Equal("Batumi drivers cannot cash out", data.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.String, data.GetProperty("impactSince").ValueKind);
            Assert.StartsWith("Reported as P1 (Batumi Auto Park #1)", rep.GetProperty("text").GetString());   // the English record stays

            var patched = (await Json(await swich.PatchAsJsonAsync($"{Base}/{id}", new { level = "P2", fault = "External" }))).GetProperty("incident");
            var upd = Entry(patched, "updated");
            Assert.Equal("updated", CodeOfData(upd));
            var changes = upd.GetProperty("data").GetProperty("changes").EnumerateArray().ToList();
            Assert.Contains(changes, c => c.GetProperty("field").GetString() == "level" && c.GetProperty("from").GetString() == "P1" && c.GetProperty("to").GetString() == "P2");
            Assert.Contains(changes, c => c.GetProperty("field").GetString() == "fault" && c.GetProperty("from").GetString() == "Unknown" && c.GetProperty("to").GetString() == "External");

            Assert.Equal("acknowledged", CodeOfData(Entry(await Json(await swich.PostAsJsonAsync($"{Base}/{id}/acknowledge", new { })), "acknowledged")));
            Assert.Equal("external_restored", CodeOfData(Entry(await Json(await swich.PostAsJsonAsync($"{Base}/{id}/external-restored", new { })), "external_restored")));
            Assert.Equal("resolved", CodeOfData(Entry(await Json(await swich.PostAsJsonAsync($"{Base}/{id}/resolve", new { })), "resolved")));

            // A person's words are not translated: notes, and a resolution with its own note, carry no code.
            var noted = await Json(await licensee.PostAsJsonAsync($"{Base}/{id}/notes", new { text = "მძღოლები კვლავ წერენ." }));
            Assert.Null(CodeOfData(Entry(noted, "note")));
            var reopened = await Json(await swich.PostAsJsonAsync($"{Base}/{id}/reopen", new { text = "Not fixed." }));
            Assert.Null(CodeOfData(Entry(reopened, "reopened")));
            Assert.Null(CodeOfData(Entry(await Json(await swich.PostAsJsonAsync($"{Base}/{id}/resolve", new { text = "Fixed for real." })), "resolved")));
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    [Fact]
    public async Task The_banner_signature_changes_when_an_alert_escalates_so_a_dismissed_banner_comes_back()
    {
        var mark = await HighWaterAsync();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        async Task<string> SignatureAsync() => (await licensee.GetFromJsonAsync<JsonElement>("/api/admin/alerts")).GetProperty("signature").GetString()!;
        var planted = new List<Guid>();
        try
        {
            // ── An incident re-levelled P2 → P1 keeps its id, but the banner turns from warning to danger ──
            var p2 = Inc(await Json(await licensee.PostAsJsonAsync(Base, new { title = "Some payouts slow", level = "P2", parkId = batumi })));
            var asP2 = await SignatureAsync();
            Assert.Equal(asP2, await SignatureAsync());                     // stable while nothing changes
            await swich.PatchAsJsonAsync($"{Base}/{p2.GetProperty("id").GetGuid()}", new { level = "P1" });
            Assert.NotEqual(asP2, await SignatureAsync());

            // ── A waiting payout crosses the 24 h danger line: same cashout id, worse alert ──
            var id = await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddHours(-23), "BANK_UNAVAILABLE: Bank gateway temporarily unavailable");
            planted.Add(id);
            var warning = await SignatureAsync();
            await _f.DbAsync(async db => { (await db.Cashouts.FirstAsync(c => c.Id == id)).CreatedAt = DateTime.UtcNow.AddHours(-25); return await db.SaveChangesAsync(); });
            var body = await licensee.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.Equal("danger", Alerts(body).Single(a => a.GetProperty("kind").GetString() == "payout_waiting"
                && a.GetProperty("parkId").ValueKind != JsonValueKind.Null && a.GetProperty("parkId").GetGuid() == tb5).GetProperty("severity").GetString());
            Assert.NotEqual(warning, body.GetProperty("signature").GetString());
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Re-scoping, level clocks, cross-park aggregation (owner's decisions)
    // ═══════════════════════════════════════════════════════════════════

    private Task<string> ParkNameAsync(Guid parkId) =>
        _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId)).Name);

    private static string[] Kinds(JsonElement detail) => Timeline(detail).Select(e => e.GetProperty("kind").GetString()!).ToArray();

    private static string[] Fields(JsonElement body) => body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray();

    /// <summary>
    /// Moving park B's incident to all parks shows it to every park manager. The move needs a new title and
    /// description in the same request, neither naming park B, and the other managers see the incident from
    /// that moment only — not B's earlier timeline (queue figures, failure codes, the old title). No park
    /// manager ever sees the cause. The same for a move to another park.
    /// </summary>
    [Fact]
    public async Task Moving_an_incident_to_all_parks_needs_neutral_text_and_other_park_managers_see_neither_its_past_nor_its_cause()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var tb5Name = await ParkNameAsync(tb5);
        var batumiName = await ParkNameAsync(batumi);
        var swich = await _f.SuperAdminAsync();
        var tb5Manager = await _f.ParkAdminAsync("tbilisi-auto-park-5");
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        var planted = new List<Guid>();
        try
        {
            // The monitor's incident names the park in its title and description; its cause is a raw failure reason.
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25),
                "BANK_UNAVAILABLE: TBC gateway 503 for the park account"));
            await monitor.RunOnceAsync(default);
            var auto = (await OpenQueueIncidentAsync(mark, tb5))!;
            Assert.Contains(tb5Name, auto.Title);
            var url = $"{Base}/{auto.Id}";

            // Park managers never see the cause — not even of their own park's incident.
            var own = await Json(await tb5Manager.GetAsync(url));
            Assert.Equal(JsonValueKind.Null, Inc(own).GetProperty("cause").ValueKind);
            Assert.DoesNotContain("gateway 503", own.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await batumiManager.GetAsync(url)).StatusCode);

            // While it is Tbilisi #5's: a note, and the cause Swich establishes (its own timeline entry).
            await swich.PostAsJsonAsync($"{url}/notes", new { text = "Tbilisi #5: one driver waiting since this morning." });
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync(url, new { cause = "TBC DBI returns 503 for every order" })).StatusCode);
            Assert.Equal(new[] { "reported", "note" }, Kinds(await Json(await tb5Manager.GetAsync(url))));   // the cause entry is not theirs to read

            // ── All parks without new text: refused, nothing changes ──
            var bare = await swich.PatchAsJsonAsync(url, new { platformWide = true });
            Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
            var bareBody = await Json(bare);
            Assert.Equal("neutral_text_required", ApiFixture.Code(bareBody));
            Assert.Equal(new[] { "title", "description" }, Fields(bareBody));
            Assert.Equal(new[] { "description" },
                Fields(await Json(await swich.PatchAsJsonAsync(url, new { platformWide = true, title = "Payouts to TBC accounts are delayed" }))));
            // Text that still names the park it comes from is not neutral (the monitor's own title and description do).
            Assert.Equal(new[] { "title", "description" }, Fields(await Json(await swich.PatchAsJsonAsync(url,
                new { platformWide = true, title = auto.Title, description = auto.Description }))));
            Assert.Equal(new[] { "description" }, Fields(await Json(await swich.PatchAsJsonAsync(url,
                new { platformWide = true, title = "Payouts to TBC accounts are delayed", description = $"First seen at {tb5Name.ToUpperInvariant()}." }))));
            Assert.Equal(tb5, (await _f.DbAsync(db => db.Incidents.AsNoTracking().FirstAsync(i => i.Id == auto.Id))).ParkId);

            // ── With neutral text: moved, and logged ──
            const string newTitle = "Payouts to TBC accounts are delayed";
            const string newDescription = "TBC is not accepting transfers; waiting payouts go out automatically once it does.";
            var moved = await swich.PatchAsJsonAsync(url, new { platformWide = true, title = newTitle, description = newDescription });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

            // Batumi's manager: the incident with its new text, from the move on. Nothing of Tbilisi #5's, no cause.
            var asBatumi = await Json(await batumiManager.GetAsync(url));
            Assert.Equal(JsonValueKind.Null, Inc(asBatumi).GetProperty("parkId").ValueKind);
            Assert.Equal(newTitle, Inc(asBatumi).GetProperty("title").GetString());
            Assert.Equal(newDescription, asBatumi.GetProperty("description").GetString());
            Assert.Equal(JsonValueKind.Null, Inc(asBatumi).GetProperty("cause").ValueKind);
            var scopeEntry = Assert.Single(Timeline(asBatumi));
            Assert.Equal("updated", scopeEntry.GetProperty("kind").GetString());
            var change = Assert.Single(scopeEntry.GetProperty("data").GetProperty("changes").EnumerateArray());
            Assert.Equal("scope", change.GetProperty("field").GetString());
            Assert.Equal(JsonValueKind.Null, change.GetProperty("to").ValueKind);
            foreach (var secret in new[] { tb5Name, "BANK_UNAVAILABLE", "TBC DBI", "gateway 503", "one driver waiting", "Payouts stuck in the queue" })
                Assert.DoesNotContain(secret, asBatumi.ToString());
            var batumiList = await Json(await batumiManager.GetAsync($"{Base}?state=open"));
            var listed = batumiList.GetProperty("incidents").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == auto.Id);
            Assert.Equal(JsonValueKind.Null, listed.GetProperty("cause").ValueKind);

            // Tbilisi #5's manager keeps what was written while it was theirs (the cause still hidden).
            var asTb5 = await Json(await tb5Manager.GetAsync(url));
            Assert.Equal(new[] { "reported", "note", "updated", "updated" }, Kinds(asTb5));
            Assert.DoesNotContain("TBC DBI", asTb5.ToString());

            // Swich and the licensee see all of it, the cause entry included.
            var asSwich = await Json(await swich.GetAsync(url));
            Assert.Equal("TBC DBI returns 503 for every order", Inc(asSwich).GetProperty("cause").GetString());
            Assert.Equal(new[] { "reported", "note", "updated", "updated", "updated" }, Kinds(asSwich));

            // A person decided its scope: the monitor no longer closes it. It still holds Tbilisi #5's waiting payouts,
            // though — the next pass opens no second incident for them (one condition, one record: Annex 1 §4, §5, §6(a)).
            await monitor.RunOnceAsync(default);
            await monitor.RunOnceAsync(default);
            var row = await _f.DbAsync(db => db.Incidents.AsNoTracking().FirstAsync(i => i.Id == auto.Id));
            Assert.NotEqual(IncidentStatus.Resolved, row.Status);
            Assert.Equal(IncidentMonitor.MovedKey(auto.Id, IncidentMonitor.PayoutQueueKeyPrefix + tb5), row.DedupeKey);
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));
            Assert.Empty(await WideQueueIncidentsAsync(mark));
            Assert.Equal("payout_queue", Inc(asSwich).GetProperty("autoKind").GetString());

            // ── To another park: the same rule. The old park's manager loses it; the new one sees it from the move ──
            var manual = Inc(await Json(await swich.PostAsJsonAsync(Base,
                new { title = $"{batumiName}: card payouts slow", level = "P2", parkId = batumi, cause = "BoG card rail" }))).GetProperty("id").GetGuid();
            var other = $"{Base}/{manual}";
            Assert.Equal("neutral_text_required", ApiFixture.Code(await Json(await swich.PatchAsJsonAsync(other, new { parkId = tb5 }))));
            Assert.Equal(HttpStatusCode.OK,
                (await swich.PatchAsJsonAsync(other, new { parkId = tb5, title = "Card payouts slow", description = "Card payouts take longer than usual." })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await batumiManager.GetAsync(other)).StatusCode);
            var asNewPark = await Json(await tb5Manager.GetAsync(other));
            Assert.Equal(tb5Name, Assert.Single(Timeline(asNewPark)).GetProperty("data").GetProperty("changes")[0].GetProperty("to").GetString());
            Assert.DoesNotContain(batumiName, asNewPark.ToString());

            // From all parks to one park nothing new is shown to anybody: no new text needed.
            var wide = Inc(await Json(await swich.PostAsJsonAsync(Base, new { title = "Slow console", level = "P3" }))).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"{Base}/{wide}", new { parkId = batumi })).StatusCode);
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// Swich re-levelling by hand follows the monitor's rule: a P2 raised to P1 gets its P1 clocks from the
    /// raise, and lowering and raising it again counts from the first time it became a P1.
    /// </summary>
    [Fact]
    public async Task Swich_raising_a_P2_to_P1_starts_the_P1_clocks_then_and_a_bounce_does_not_restart_them()
    {
        var mark = await HighWaterAsync();
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.OperatorAsync();
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        try
        {
            var id = Inc(await Json(await licensee.PostAsJsonAsync(Base, new { title = "Some payouts slow", level = "P2", parkId = batumi })))
                .GetProperty("id").GetGuid();
            var url = $"{Base}/{id}";
            // Reported 60 hours ago (pushed back here); nobody answered.
            await _f.DbAsync(async db =>
            {
                var i = await db.Incidents.FirstAsync(x => x.Id == id);
                i.ReportedAt = i.ReportedAt.AddHours(-60);
                i.StartedAt = i.StartedAt.AddHours(-60);
                i.ReachedP2At = i.ReportedAt;
                return await db.SaveChangesAsync();
            });

            var raised = Inc((await Json(await swich.PatchAsJsonAsync(url, new { level = "P1" }))).GetProperty("incident"));
            var response = raised.GetProperty("sla").GetProperty("response");
            Assert.Equal("Pending", response.GetProperty("state").GetString());          // not 56 hours overdue
            var p1Since = response.GetProperty("startsAt").GetDateTime();
            Assert.InRange((DateTime.UtcNow - p1Since).TotalMinutes, -1, 2);
            Assert.Equal(p1Since, raised.GetProperty("levelSince").GetDateTime());
            Assert.Equal(p1Since.AddHours(24), raised.GetProperty("sla").GetProperty("resolution").GetProperty("dueAt").GetDateTime());

            // Lowered again: the P2 clocks are back on the report.
            var lowered = Inc((await Json(await swich.PatchAsJsonAsync(url, new { level = "P2" }))).GetProperty("incident"));
            Assert.InRange((DateTime.UtcNow - lowered.GetProperty("levelSince").GetDateTime()).TotalHours, 59.9, 60.1);

            // Raised again an hour later (the first raise pushed back an hour): still the first time it became a P1.
            var firstP1 = await _f.DbAsync(async db =>
            {
                var i = await db.Incidents.FirstAsync(x => x.Id == id);
                i.ReachedP1At = i.ReachedP1At!.Value.AddHours(-1);
                await db.SaveChangesAsync();
                return i.ReachedP1At.Value;
            });
            var again = Inc((await Json(await swich.PatchAsJsonAsync(url, new { level = "P1" }))).GetProperty("incident"));
            Assert.InRange((again.GetProperty("levelSince").GetDateTime() - firstP1).Duration().TotalSeconds, 0, 1);
            Assert.InRange((again.GetProperty("sla").GetProperty("resolution").GetProperty("dueAt").GetDateTime() - firstP1.AddHours(24)).Duration().TotalSeconds, 0, 1);
        }
        finally { await RemoveIncidentsAboveAsync(mark); }
    }

    private Task<List<Incident>> WideQueueIncidentsAsync(int mark) => _f.DbAsync(db => db.Incidents.AsNoTracking()
        .Include(i => i.Updates)
        .Where(i => i.Number > mark && i.DedupeKey != null && i.DedupeKey.StartsWith(IncidentMonitor.PayoutQueueWideKeyPrefix))
        .ToListAsync());

    /// <summary>
    /// One bank failing at several parks used to open one P2 per park and never a P1. Two parks stuck on the
    /// same cause in one pass, neither with an incident yet: ONE platform-wide P1 with neutral text, and no
    /// incident of their own.
    /// </summary>
    [Fact]
    public async Task Two_parks_stuck_on_the_same_bank_at_once_open_one_platform_wide_P1_with_neutral_text()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var tb5Name = await ParkNameAsync(tb5);
        var batumiName = await ParkNameAsync(batumi);
        var swich = await _f.SuperAdminAsync();
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        var planted = new List<Guid>();
        try
        {
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-20), down));
            await monitor.RunOnceAsync(default);
            await monitor.RunOnceAsync(default);                                  // a second pass changes nothing

            var wide = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.Null(wide.ParkId);
            Assert.Equal(IncidentLevel.P1, wide.Level);
            Assert.Equal(wide.ReportedAt, wide.ReachedP1At);                      // a P1 from its report
            Assert.Equal(IncidentFault.External, wide.Fault);
            Assert.Equal(IncidentSource.Auto, wide.Source);
            Assert.Equal(IncidentStatus.Reported, wide.Status);                   // never auto-acknowledged
            Assert.InRange((wide.StartedAt - DateTime.UtcNow.AddMinutes(-25)).Duration().TotalMinutes, 0, 1);
            Assert.Equal(IncidentMonitor.WideQueueTitle, wide.Title);
            foreach (var name in new[] { tb5Name, batumiName })
            {
                Assert.DoesNotContain(name, wide.Title);
                Assert.DoesNotContain(name, wide.Description);
                Assert.DoesNotContain(name, wide.Cause);
            }
            var opened = DataOf(Assert.Single(wide.Updates));
            Assert.Equal("auto_wide_opened", opened.GetProperty("code").GetString());
            Assert.Equal(2, opened.GetProperty("parks").GetInt32());
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));
            Assert.Null(await OpenQueueIncidentAsync(mark, batumi));

            var batumiView = await Json(await batumiManager.GetAsync($"{Base}/{wide.Id}"));
            Assert.Equal(new[] { "reported" }, Kinds(batumiView));
            Assert.Equal("payout_queue_wide", Inc(batumiView).GetProperty("autoKind").GetString());
            var batumiLine = Alerts(await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"))
                .Single(a => a.GetProperty("kind").GetString() == "incident_open" && a.GetProperty("parkId").ValueKind == JsonValueKind.Null);
            Assert.Equal("danger", batumiLine.GetProperty("severity").GetString());
            Assert.Equal("bank", Inc(await Json(await swich.GetAsync($"{Base}/{wide.Id}"))).GetProperty("sla").GetProperty("resolution").GetProperty("waitingFor").GetString());

            // ── The bank is back: the queues drain and the platform-wide incident closes itself ──
            await RemoveCashoutsAsync(planted);
            await monitor.RunOnceAsync(default);
            var done = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).FirstAsync(i => i.Id == wide.Id));
            Assert.Equal(IncidentStatus.Resolved, done.Status);
            Assert.NotNull(done.ExternalRestoredAt);
            Assert.Equal(new[] { "reported", "external_restored", "resolved" }, done.Updates.OrderBy(u => u.Id).Select(u => u.Kind));
            Assert.Equal("auto_wide_resolved", DataOf(done.Updates.OrderBy(u => u.Id).Last()).GetProperty("code").GetString());
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// Park B's queue incident, then the same bank fails at park A: B's incident BECOMES the platform-wide P1
    /// (one record of the cause, its report and any response kept), its park-naming title, description and cause
    /// rewritten to neutral text. Its P1 clocks start at the raise — it was a P2 until then — and park A's manager
    /// reads it from the raise on, nothing of B's earlier timeline.
    /// </summary>
    [Fact]
    public async Task A_park_incident_whose_cause_reaches_a_second_park_becomes_the_platform_wide_P1_with_its_clocks_from_the_raise()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var tb5Name = await ParkNameAsync(tb5);
        var batumiName = await ParkNameAsync(batumi);
        var swich = await _f.SuperAdminAsync();
        var tb5Manager = await _f.ParkAdminAsync("tbilisi-auto-park-5");
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        var planted = new List<Guid>();
        try
        {
            // ── One park: its own P2, as before ──
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            await monitor.RunOnceAsync(default);
            var tb5Own = (await OpenQueueIncidentAsync(mark, tb5))!;
            Assert.Equal(IncidentLevel.P2, tb5Own.Level);
            Assert.Contains(tb5Name, tb5Own.Title);
            Assert.Empty(await WideQueueIncidentsAsync(mark));
            // It has been open for five hours (pushed back here): a P2's, not a P1's, hours.
            var reportedAt = await _f.DbAsync(async db =>
            {
                var i = await db.Incidents.FirstAsync(x => x.Id == tb5Own.Id);
                i.ReportedAt = i.ReportedAt.AddHours(-5);
                i.ReachedP2At = i.ReportedAt;
                i.StartedAt = i.StartedAt.AddHours(-5);
                await db.SaveChangesAsync();
                return i.ReportedAt;
            });

            // ── The same bank fails at a second park: that incident is now the one platform-wide P1 ──
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-20), down));
            await monitor.RunOnceAsync(default);
            var wide = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.Equal(tb5Own.Id, wide.Id);
            Assert.Null(wide.ParkId);
            Assert.Equal(IncidentLevel.P1, wide.Level);
            Assert.Equal(IncidentStatus.Reported, wide.Status);                   // never auto-acknowledged
            Assert.InRange((wide.ReportedAt - reportedAt).Duration().TotalSeconds, 0, 1);      // its report stays
            Assert.InRange((wide.ReachedP2At!.Value - reportedAt).Duration().TotalSeconds, 0, 1);
            Assert.InRange((DateTime.UtcNow - wide.ReachedP1At!.Value).TotalMinutes, -1, 2);    // a P1 from now
            Assert.Equal(IncidentMonitor.WideQueueTitle, wide.Title);
            foreach (var name in new[] { tb5Name, batumiName })
            {
                Assert.DoesNotContain(name, wide.Title);
                Assert.DoesNotContain(name, wide.Description);
                Assert.DoesNotContain(name, wide.Cause);
            }
            Assert.DoesNotContain("temporarily unavailable", wide.Cause);          // the raw reason of Tbilisi #5's account
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));
            Assert.Null(await OpenQueueIncidentAsync(mark, batumi));               // Batumi is covered, no incident of its own
            var entries = await _f.DbAsync(db => db.IncidentUpdates.AsNoTracking().CountAsync(u => u.IncidentId == wide.Id));
            await monitor.RunOnceAsync(default);                                   // a second pass changes nothing
            Assert.Equal(entries, await _f.DbAsync(db => db.IncidentUpdates.AsNoTracking().CountAsync(u => u.IncidentId == wide.Id)));
            Assert.Single(await WideQueueIncidentsAsync(mark));

            // The P1 response is due 4 h from the raise, not 1 h overdue the moment it is raised.
            var asSwich = await Json(await swich.GetAsync($"{Base}/{wide.Id}"));
            var response = Inc(asSwich).GetProperty("sla").GetProperty("response");
            Assert.Equal("Pending", response.GetProperty("state").GetString());
            Assert.InRange((response.GetProperty("startsAt").GetDateTime() - wide.ReachedP1At.Value).Duration().TotalSeconds, 0, 1);
            Assert.Equal("payout_queue_wide", Inc(asSwich).GetProperty("autoKind").GetString());
            Assert.Equal(new[] { "reported", "updated", "updated", "updated" }, Kinds(asSwich));
            var upgrade = Timeline(asSwich).Last().GetProperty("data");
            Assert.Equal("auto_wide_upgraded", upgrade.GetProperty("code").GetString());
            Assert.Equal(2, upgrade.GetProperty("parks").GetInt32());
            Assert.Equal("P2", upgrade.GetProperty("from").GetString());

            // Tbilisi #5's manager keeps its own history (not the cause entry); Batumi's reads it from the raise on.
            var tb5View = await Json(await tb5Manager.GetAsync($"{Base}/{wide.Id}"));
            Assert.Equal(new[] { "reported", "updated", "updated" }, Kinds(tb5View));
            var batumiView = await Json(await batumiManager.GetAsync($"{Base}/{wide.Id}"));
            Assert.Equal(new[] { "updated" }, Kinds(batumiView));
            Assert.Equal(JsonValueKind.Null, Inc(batumiView).GetProperty("cause").ValueKind);
            foreach (var secret in new[] { tb5Name, "Payouts stuck in the queue", "temporarily unavailable" })
                Assert.DoesNotContain(secret, batumiView.ToString());
            var batumiLine = Alerts(await batumiManager.GetFromJsonAsync<JsonElement>("/api/admin/alerts"))
                .Single(a => a.GetProperty("kind").GetString() == "incident_open" && a.GetProperty("parkId").ValueKind == JsonValueKind.Null);
            Assert.Equal("danger", batumiLine.GetProperty("severity").GetString());
            Assert.DoesNotContain(tb5Name, batumiLine.ToString());

            // ── The bank is back: the queues drain and the platform-wide incident closes itself ──
            await RemoveCashoutsAsync(planted);
            await monitor.RunOnceAsync(default);
            var done = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).FirstAsync(i => i.Id == wide.Id));
            Assert.Equal(IncidentStatus.Resolved, done.Status);
            Assert.NotNull(done.ExternalRestoredAt);
            Assert.Equal(new[] { "external_restored", "resolved" }, done.Updates.OrderBy(u => u.Id).Select(u => u.Kind).TakeLast(2));
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// Both parks already have an incident when their queues come to wait for the same cause. The one that has been
    /// a P1 longest carries on as the platform-wide P1 — with its own clocks and the response Swich already gave — and
    /// the other is closed as its duplicate, linked on both timelines by entries only that park reads. A merge never
    /// restarts a P1 clock.
    /// </summary>
    [Fact]
    public async Task When_several_park_incidents_share_a_cause_the_longest_P1_carries_on_and_the_others_close_as_its_duplicates()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var tb5Name = await ParkNameAsync(tb5);
        var batumiName = await ParkNameAsync(batumi);
        var swich = await _f.SuperAdminAsync();
        var tb5Manager = await _f.ParkAdminAsync("tbilisi-auto-park-5");
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        const string short_ = "INSUFFICIENT_FUNDS: 3.00 GEL on the account";
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        var planted = new List<Guid>();
        try
        {
            // Two parks short of money: two incidents of their own (the park's side never aggregates).
            var tb5Driver = (await DriversWithCardsAsync(tb5))[0];
            var batumiDriver = (await DriversWithCardsAsync(batumi))[0];
            planted.Add(await PlantQueuedAsync(tb5Driver, DateTime.UtcNow.AddMinutes(-25), short_));
            planted.Add(await PlantQueuedAsync(batumiDriver, DateTime.UtcNow.AddMinutes(-25), short_));
            await monitor.RunOnceAsync(default);
            var tb5Own = (await OpenQueueIncidentAsync(mark, tb5))!;
            var batumiOwn = (await OpenQueueIncidentAsync(mark, batumi))!;
            Assert.Empty(await WideQueueIncidentsAsync(mark));

            // Swich made Batumi's a P1 and answered it — three hours ago (pushed back here).
            var bUrl = $"{Base}/{batumiOwn.Id}";
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync(bUrl, new { level = "P1" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await swich.PostAsJsonAsync($"{bUrl}/acknowledge", new { })).StatusCode);
            var (p1Since, answeredAt) = await _f.DbAsync(async db =>
            {
                var i = await db.Incidents.FirstAsync(x => x.Id == batumiOwn.Id);
                i.ReportedAt = i.ReportedAt.AddHours(-3);
                i.ReachedP2At = i.ReportedAt;
                i.ReachedP1At = i.ReachedP1At!.Value.AddHours(-3);
                i.AcknowledgedAt = i.AcknowledgedAt!.Value.AddHours(-3);
                i.StartedAt = i.StartedAt.AddHours(-3);
                await db.SaveChangesAsync();
                return (i.ReachedP1At.Value, i.AcknowledgedAt.Value);
            });

            // ── Now the bank fails for both: most of each queue waits for it ──
            foreach (var d in new[] { tb5Driver, tb5Driver, batumiDriver, batumiDriver })
                planted.Add(await PlantQueuedAsync(d, DateTime.UtcNow.AddMinutes(-20), down));
            await monitor.RunOnceAsync(default);

            var wide = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.Equal(batumiOwn.Id, wide.Id);                                   // the P1, not the P2 reported at the same time
            Assert.Null(wide.ParkId);
            Assert.Equal(IncidentLevel.P1, wide.Level);
            Assert.InRange((wide.ReachedP1At!.Value - p1Since).Duration().TotalSeconds, 0, 1);  // its clocks run on
            Assert.Equal(IncidentStatus.Acknowledged, wide.Status);                // and its response counts
            Assert.InRange((wide.AcknowledgedAt!.Value - answeredAt).Duration().TotalSeconds, 0, 1);
            Assert.Equal(IncidentFault.External, wide.Fault);                      // the bank's now, not the park balance
            foreach (var name in new[] { tb5Name, batumiName })
            {
                Assert.DoesNotContain(name, wide.Title);
                Assert.DoesNotContain(name, wide.Description);
            }
            var asSwich = Inc(await Json(await swich.GetAsync($"{Base}/{wide.Id}")));
            Assert.Equal("Met", asSwich.GetProperty("sla").GetProperty("response").GetProperty("state").GetString());
            Assert.InRange((asSwich.GetProperty("levelSince").GetDateTime() - p1Since).Duration().TotalSeconds, 0, 1);

            // Tbilisi #5's own incident is closed as its duplicate, the link on both timelines.
            var closed = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).FirstAsync(i => i.Id == tb5Own.Id));
            Assert.Equal(IncidentStatus.Resolved, closed.Status);
            var dup = closed.Updates.OrderBy(u => u.Id).Last();
            Assert.Equal(("resolved", IncidentMonitor.SystemActor), (dup.Kind, dup.By));
            Assert.Equal("auto_duplicate", DataOf(dup).GetProperty("code").GetString());
            Assert.Equal(wide.Ref, DataOf(dup).GetProperty("of").GetString());
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));
            Assert.Null(await OpenQueueIncidentAsync(mark, batumi));

            // Tbilisi #5's manager reads the platform-wide P1 from the raise, plus the link to its own incident;
            // Batumi's keeps its own history, without Tbilisi #5's link.
            var tb5View = await Json(await tb5Manager.GetAsync($"{Base}/{wide.Id}"));
            Assert.Equal(new[] { "updated", "note" }, Kinds(tb5View));
            Assert.Equal("auto_merged", Timeline(tb5View).Last().GetProperty("data").GetProperty("code").GetString());
            Assert.DoesNotContain(batumiName, tb5View.ToString());
            Assert.DoesNotContain("Payouts stuck in the queue", tb5View.ToString());
            var batumiView = await Json(await batumiManager.GetAsync($"{Base}/{wide.Id}"));
            Assert.Equal("reported", Kinds(batumiView)[0]);
            Assert.DoesNotContain("note", Kinds(batumiView));
            Assert.DoesNotContain(tb5Name, batumiView.ToString());
            Assert.DoesNotContain(tb5Own.Ref, batumiView.ToString());
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// A park incident that is already a P1 carries on platform-wide without its clocks moving — also a row that has
    /// no ReachedP1At (it counts from its report): carrying on is not a raise.
    /// </summary>
    [Fact]
    public async Task A_P1_carried_on_platform_wide_keeps_its_clocks_even_without_a_recorded_P1_time()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        var planted = new List<Guid>();
        try
        {
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            await monitor.RunOnceAsync(default);
            var own = (await OpenQueueIncidentAsync(mark, tb5))!;
            // A P1 from its report two hours ago, written without the level times.
            var reportedAt = await _f.DbAsync(async db =>
            {
                var i = await db.Incidents.FirstAsync(x => x.Id == own.Id);
                i.Level = IncidentLevel.P1;
                i.ReportedAt = i.ReportedAt.AddHours(-2);
                i.ReachedP1At = null;
                i.ReachedP2At = null;
                await db.SaveChangesAsync();
                return i.ReportedAt;
            });

            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-20), down));
            await monitor.RunOnceAsync(default);
            var wide = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.Equal(own.Id, wide.Id);
            Assert.Equal(IncidentLevel.P1, wide.Level);
            Assert.InRange((IncidentSla.LevelClockStart(wide) - reportedAt).Duration().TotalSeconds, 0, 1);
            Assert.Equal(JsonValueKind.Null, DataOf(wide.Updates.OrderBy(u => u.Id).Last()).GetProperty("from").ValueKind);
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    [Fact]
    public async Task Park_side_causes_never_aggregate_each_park_keeps_its_own_incident()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var planted = new List<Guid>();
        try
        {
            // Two parks short of money at once: two facts about two parks, not one cause.
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), "INSUFFICIENT_FUNDS: 3.00 GEL on the account"));
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-25), "INSUFFICIENT_FUNDS: 1.00 GEL on the account"));
            await monitor.RunOnceAsync(default);
            Assert.Empty(await WideQueueIncidentsAsync(mark));
            Assert.Equal(IncidentFault.Park, (await OpenQueueIncidentAsync(mark, tb5))!.Fault);
            Assert.Equal(IncidentFault.Park, (await OpenQueueIncidentAsync(mark, batumi))!.Fault);
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// Yandex not answering for several parks at once is the same platform-wide P1 as an outage everywhere,
    /// even while another park's calls still get through. Cashout requests are refused meanwhile, so there
    /// are no per-park incidents to merge — the detector counts parks from the API audit log.
    /// </summary>
    [Fact]
    public async Task Yandex_failing_for_several_parks_is_one_platform_wide_P1_even_while_another_park_gets_through()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        // A window five hours ahead holds only the rows planted here (the other Yandex test uses +3 h).
        var now = DateTime.UtcNow.AddHours(5);
        var planted = new List<long>();
        async Task PlantAsync(Guid? parkId, DateTime at, bool success, int code) => planted.Add(await _f.DbAsync(async db =>
        {
            var row = new ApiAuditLog
            {
                Timestamp = at, ParkId = parkId, ApiProvider = "YandexFleet", Endpoint = "/v1/parks/driver-profiles/list", Method = "POST",
                ActorType = "System", ResponseCode = code, Success = success, CorrelationId = "incident-test",
            };
            db.ApiAuditLogs.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }));
        Task<Incident?> OpenOutageAsync() => _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates)
            .FirstOrDefaultAsync(i => i.Number > mark && i.DedupeKey == IncidentMonitor.YandexOutageKey));
        try
        {
            // One park's calls fail while another call gets through: not platform-wide.
            for (var k = 0; k < 3; k++) await PlantAsync(tb5, now.AddMinutes(-8 + k), false, 503);
            await PlantAsync(null, now.AddMinutes(-5), true, 200);
            Assert.Equal(0, await monitor.CheckYandexOutageAsync(now, default));

            // A second park fails too: one platform-wide P1, though a call elsewhere still succeeds.
            await PlantAsync(batumi, now.AddMinutes(-4), false, 500);
            await PlantAsync(batumi, now.AddMinutes(-3), false, 502);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now, default));
            var opened = (await OpenOutageAsync())!;
            Assert.Null(opened.ParkId);
            Assert.Equal((IncidentLevel.P1, IncidentFault.External), (opened.Level, opened.Fault));
            Assert.Equal(2, DataOf(opened.Updates.Single()).GetProperty("parks").GetInt32());
            Assert.Contains("refused", opened.Description);                       // requests are refused, not queued
            Assert.Contains("nothing is debited", opened.Description);
            Assert.Equal(0, await monitor.CheckYandexOutageAsync(now.AddMinutes(1), default));

            // Tbilisi #5 gets through again; Batumi alone is no longer "several parks": resolved.
            await PlantAsync(tb5, now.AddMinutes(1), true, 200);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now.AddMinutes(2), default));
            var closed = (await OpenOutageAsync())!;
            Assert.Equal(IncidentStatus.Resolved, closed.Status);
            Assert.InRange((closed.ExternalRestoredAt!.Value - now.AddMinutes(1)).Duration().TotalSeconds, 0, 1);
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                db.ApiAuditLogs.RemoveRange(await db.ApiAuditLogs.Where(a => planted.Contains(a.Id)).ToListAsync());
                return await db.SaveChangesAsync();
            });
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  A moved incident keeps the condition it was opened for
    // ═══════════════════════════════════════════════════════════════════

    private Task<Incident> IncidentAsync(Guid id) =>
        _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).FirstAsync(i => i.Id == id));

    /// <summary>The family the planted payouts wait for: BANK_UNAVAILABLE, no payout account on the row → bank unknown.</summary>
    private const string PlantedFamily = "bank:unknown";

    private static IEnumerable<JsonElement> Changes(IncidentUpdate u) =>
        u.Kind == "updated" && u.Data is not null && DataOf(u).TryGetProperty("changes", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    /// <summary>
    /// Swich moves park B's queue incident to all parks — it knows the bank is down for everyone. That incident is then
    /// the record of the cause: when the cause reaches a second park, it carries on as the one platform-wide P1 (the
    /// person's text kept) and the second park's own incident closes as its duplicate; no new P1 opens. Once nothing
    /// waits for the cause any more the monitor lets go of it — it stays open, a person decided its scope and a person
    /// resolves it — and the next time park B's queue is stuck is an occurrence of its own.
    /// </summary>
    [Fact]
    public async Task A_queue_incident_moved_to_all_parks_stays_the_record_of_its_cause_until_nothing_waits_for_it()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var swich = await _f.SuperAdminAsync();
        var batumiManager = await _f.ParkAdminAsync("batumi-auto-park-1");
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        const string short_ = "INSUFFICIENT_FUNDS: 3.00 GEL on the account";
        var planted = new List<Guid>();
        try
        {
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            await monitor.RunOnceAsync(default);
            var own = (await OpenQueueIncidentAsync(mark, tb5))!;
            const string title = "Payouts to one bank are delayed";
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"{Base}/{own.Id}", new
            {
                platformWide = true, title, description = "The bank is not accepting transfers; waiting payouts go out once it does.",
            })).StatusCode);

            // Batumi: first a matter of its own (short of money) — its own incident; then the same bank fails for it too.
            var batumiDriver = (await DriversWithCardsAsync(batumi))[0];
            planted.Add(await PlantQueuedAsync(batumiDriver, DateTime.UtcNow.AddMinutes(-25), short_));
            await monitor.RunOnceAsync(default);
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));                  // Tbilisi #5's payouts: on the moved one's record
            var batumiOwn = (await OpenQueueIncidentAsync(mark, batumi))!;
            Assert.Equal(IncidentFault.Park, batumiOwn.Fault);
            for (var k = 0; k < 2; k++) planted.Add(await PlantQueuedAsync(batumiDriver, DateTime.UtcNow.AddMinutes(-20), down));
            await monitor.RunOnceAsync(default);
            await monitor.RunOnceAsync(default);                                   // a second pass changes nothing

            // The moved incident is the one platform-wide P1 of the cause: no new one, its text is still the person's.
            Assert.Empty(await WideQueueIncidentsAsync(mark));
            var carried = await IncidentAsync(own.Id);
            Assert.Equal(IncidentMonitor.MovedKey(own.Id, IncidentMonitor.PayoutQueueWideKeyPrefix + PlantedFamily), carried.DedupeKey);
            Assert.Null(carried.ParkId);
            Assert.NotEqual(IncidentStatus.Resolved, carried.Status);
            Assert.Equal(title, carried.Title);
            Assert.Equal(IncidentLevel.P1, carried.Level);
            Assert.InRange((DateTime.UtcNow - carried.ReachedP1At!.Value).TotalMinutes, -1, 2);   // a P1 from now, as any raise
            var upgrades = carried.Updates.Where(u => u.Data is not null && DataOf(u).GetProperty("code").GetString() == "auto_wide_upgraded").ToList();
            Assert.Equal(2, DataOf(Assert.Single(upgrades)).GetProperty("parks").GetInt32());
            Assert.Null(Assert.Single(upgrades).ParkId);                             // every park manager reads it

            // Batumi's own incident is closed as its duplicate, and Batumi's manager reads the link.
            var dup = await IncidentAsync(batumiOwn.Id);
            Assert.Equal(IncidentStatus.Resolved, dup.Status);
            Assert.Equal(carried.Ref, DataOf(dup.Updates.OrderBy(u => u.Id).Last()).GetProperty("of").GetString());
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));
            Assert.Null(await OpenQueueIncidentAsync(mark, batumi));
            var batumiView = await Json(await batumiManager.GetAsync($"{Base}/{own.Id}"));
            Assert.Equal("auto_merged", Timeline(batumiView).Last().GetProperty("data").GetProperty("code").GetString());

            // ── The bank is back, nothing waits for the cause: the monitor lets go, the incident stays open for Swich ──
            await RemoveCashoutsAsync(planted);
            planted.Clear();
            await monitor.RunOnceAsync(default);
            var released = await IncidentAsync(own.Id);
            Assert.NotEqual(IncidentStatus.Resolved, released.Status);
            Assert.StartsWith(IncidentMonitor.ReleasedKeyPrefix, released.DedupeKey);
            Assert.Equal("payout_queue_wide", Inc(await Json(await swich.GetAsync($"{Base}/{own.Id}"))).GetProperty("autoKind").GetString());

            // Tbilisi #5's queue stuck again later: a new occurrence, with an incident of its own.
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            await monitor.RunOnceAsync(default);
            Assert.NotNull(await OpenQueueIncidentAsync(mark, tb5));
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// The monitor's platform-wide P1, moved by Swich to one park, is still the record of its cause: no second
    /// platform-wide P1 opens for it, and the park it now belongs to gets no incident of its own for the cause. A park
    /// that can no longer read it gets its own. Resolved and reopened by a person, it no longer holds the cause (a
    /// reopened incident never does) — the monitor records it afresh.
    /// </summary>
    [Fact]
    public async Task A_platform_wide_incident_moved_to_one_park_still_holds_its_cause_and_no_second_P1_opens()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        var swich = await _f.SuperAdminAsync();
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        var planted = new List<Guid>();
        try
        {
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-20), down));
            await monitor.RunOnceAsync(default);
            var wide = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"{Base}/{wide.Id}", new { parkId = tb5 })).StatusCode);

            await monitor.RunOnceAsync(default);
            await monitor.RunOnceAsync(default);
            Assert.Empty(await WideQueueIncidentsAsync(mark));                    // no second P1 for the same cause
            var moved = await IncidentAsync(wide.Id);
            Assert.Equal(IncidentMonitor.MovedKey(wide.Id, IncidentMonitor.PayoutQueueWideKeyPrefix + PlantedFamily), moved.DedupeKey);
            Assert.NotEqual(IncidentStatus.Resolved, moved.Status);
            Assert.Equal(tb5, moved.ParkId);
            Assert.Null(await OpenQueueIncidentAsync(mark, tb5));                 // its park now: covered by it
            Assert.NotNull(await OpenQueueIncidentAsync(mark, batumi));           // Batumi can no longer read it: its own

            // ── Resolved and reopened by Swich: a reopened incident holds no condition ──
            Assert.Equal(HttpStatusCode.OK, (await swich.PostAsJsonAsync($"{Base}/{wide.Id}/resolve", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await swich.PostAsJsonAsync($"{Base}/{wide.Id}/reopen", new { text = "Not fixed for Tbilisi #5." })).StatusCode);
            Assert.StartsWith("reopened:", (await IncidentAsync(wide.Id)).DedupeKey);
            await monitor.RunOnceAsync(default);
            var fresh = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.NotEqual(wide.Id, fresh.Id);
            Assert.NotEqual(IncidentStatus.Resolved, (await IncidentAsync(wide.Id)).Status);
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// The Yandex outage incident moved by Swich to one park: no second outage incident opens while Yandex still fails.
    /// Once Yandex answers the monitor lets go of it (Swich resolves it); a later outage is a new incident.
    /// </summary>
    [Fact]
    public async Task A_moved_Yandex_outage_incident_holds_the_outage_until_Yandex_answers()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var swich = await _f.SuperAdminAsync();
        // A window seven hours ahead holds only the rows planted here (the other Yandex tests use +3 h and +5 h).
        var now = DateTime.UtcNow.AddHours(7);
        var planted = new List<long>();
        async Task PlantAsync(DateTime at, bool success, int code) => planted.Add(await _f.DbAsync(async db =>
        {
            var row = new ApiAuditLog
            {
                Timestamp = at, ApiProvider = "YandexFleet", Endpoint = "/v1/parks/driver-profiles/list", Method = "POST",
                ActorType = "System", ResponseCode = code, Success = success, CorrelationId = "incident-test",
            };
            db.ApiAuditLogs.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }));
        Task<List<Incident>> OutagesAsync() => _f.DbAsync(db => db.Incidents.AsNoTracking()
            .Where(i => i.Number > mark && i.DedupeKey == IncidentMonitor.YandexOutageKey).ToListAsync());
        try
        {
            for (var k = 0; k < 5; k++) await PlantAsync(now.AddMinutes(-8 + k), false, 503);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now, default));
            var outage = Assert.Single(await OutagesAsync());
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"{Base}/{outage.Id}", new { parkId = tb5 })).StatusCode);

            Assert.Equal(0, await monitor.CheckYandexOutageAsync(now.AddMinutes(1), default));   // still down: no second one
            Assert.Empty(await OutagesAsync());

            await PlantAsync(now.AddMinutes(2), true, 200);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now.AddMinutes(3), default));
            var released = await IncidentAsync(outage.Id);
            Assert.NotEqual(IncidentStatus.Resolved, released.Status);
            Assert.StartsWith(IncidentMonitor.ReleasedKeyPrefix, released.DedupeKey);

            for (var k = 0; k < 5; k++) await PlantAsync(now.AddMinutes(30 + k), false, 503);
            Assert.Equal(1, await monitor.CheckYandexOutageAsync(now.AddMinutes(35), default));
            Assert.NotEqual(outage.Id, Assert.Single(await OutagesAsync()).Id);
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                db.ApiAuditLogs.RemoveRange(await db.ApiAuditLogs.Where(a => planted.Contains(a.Id)).ToListAsync());
                return await db.SaveChangesAsync();
            });
            await RemoveIncidentsAboveAsync(mark);
        }
    }

    /// <summary>
    /// Carrying park B's incident on platform-wide is one step: when any part of it fails, the incident is still B's —
    /// its key, its park, its text — and the next pass tries again (before, it kept the platform-wide key with B's text
    /// and park, and other parks' incidents were closed as duplicates of one their managers cannot open). And when another
    /// park's payout has waited longer, the impact start moves to it on the record: the §5(b) P1 hours count from it.
    /// </summary>
    [Fact]
    public async Task Carrying_a_park_incident_on_platform_wide_is_all_or_nothing_and_an_earlier_impact_start_is_logged()
    {
        var mark = await HighWaterAsync();
        var monitor = _f.Services.GetRequiredService<IncidentMonitor>();
        var tb5 = await ParkIdAsync("tbilisi-auto-park-5");
        var batumi = await ParkIdAsync("batumi-auto-park-1");
        const string down = "BANK_UNAVAILABLE: Bank gateway temporarily unavailable";
        const string failLast = "CK_test_wide_upgrade_fails";
        var planted = new List<Guid>();
        try
        {
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(tb5))[0], DateTime.UtcNow.AddMinutes(-25), down));
            await monitor.RunOnceAsync(default);
            var own = (await OpenQueueIncidentAsync(mark, tb5))!;
            // Batumi's payout has waited longer than Tbilisi #5's.
            planted.Add(await PlantQueuedAsync((await DriversWithCardsAsync(batumi))[0], DateTime.UtcNow.AddMinutes(-60), down));

            // The last write of the upgrade (its timeline entry for everybody) is refused by the database.
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"IncidentUpdates\" ADD CONSTRAINT \"{failLast}\" CHECK (\"Data\" IS NULL OR \"Data\" NOT LIKE '%auto_wide_upgraded%')"));
            try { await Assert.ThrowsAnyAsync<Exception>(() => monitor.RunOnceAsync(default)); }
            finally { await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"IncidentUpdates\" DROP CONSTRAINT IF EXISTS \"{failLast}\"")); }

            var after = await IncidentAsync(own.Id);
            Assert.Equal(IncidentMonitor.PayoutQueueKeyPrefix + tb5, after.DedupeKey);
            Assert.Equal(tb5, after.ParkId);
            Assert.Equal((own.Title, own.Description, own.Cause, own.Fault, own.Level), (after.Title, after.Description, after.Cause, after.Fault, after.Level));
            Assert.Equal(own.StartedAt, after.StartedAt);
            Assert.Equal(own.Updates.Count, after.Updates.Count);
            Assert.Empty(await WideQueueIncidentsAsync(mark));

            // The next pass carries it on; the earlier start is on its timeline (Swich and the licensee read it).
            await monitor.RunOnceAsync(default);
            var wide = Assert.Single(await WideQueueIncidentsAsync(mark));
            Assert.Equal(own.Id, wide.Id);
            Assert.Null(wide.ParkId);
            Assert.InRange((wide.StartedAt - DateTime.UtcNow.AddMinutes(-60)).Duration().TotalMinutes, 0, 1);
            var started = Assert.Single(wide.Updates.SelectMany(Changes), c => c.GetProperty("field").GetString() == "started");
            Assert.InRange((started.GetProperty("from").GetDateTime() - own.StartedAt).Duration().TotalSeconds, 0, 1);
            Assert.InRange((started.GetProperty("to").GetDateTime() - wide.StartedAt).Duration().TotalSeconds, 0, 1);
        }
        finally
        {
            await RemoveCashoutsAsync(planted);
            await RemoveIncidentsAboveAsync(mark);
        }
    }
}
