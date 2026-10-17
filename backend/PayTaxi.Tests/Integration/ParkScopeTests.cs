using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Who sees what: an operator reaches only the parks of its own licensee, and only while that licensee is in force — never
/// a Swich-direct park or another licensee's (the seed's demo parks are all the seeded licensee's). And a reconciliation
/// item about money is resolved by Swich or that licensee's operator, with a reason; the park's manager adds a note.
/// </summary>
[Collection("api")]
public class ParkScopeTests
{
    private const string Batumi = "batumi-auto-park-1";
    private readonly ApiFixture _f;
    public ParkScopeTests(ApiFixture f) => _f = f;

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync());

    private static Guid[] ParkIds(JsonElement list) => list.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray();

    /// <summary>A Swich-direct park (outside the licence agreement), made through the API and deleted afterwards.</summary>
    private async Task<Guid> DirectParkAsync(HttpClient swich)
    {
        var slug = "direct-" + Guid.NewGuid().ToString("N")[..8];
        var res = await swich.PostAsJsonAsync("/api/admin/parks", new
        {
            name = "Direct " + slug, slug, yandexParkId = "yx_" + slug, legalEntityName = "შპს პირდაპირი", taxId = "405000222",
            phone = "+995555123001", bankAccountIban = ApiFixture.TbcIban("6600" + Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)),
            direct = true,
        });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == HttpStatusCode.Created, body.ToString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("licenseeId").ValueKind);
        return body.GetProperty("id").GetGuid();
    }

    private Task DeleteParkAsync(Guid parkId) => _f.DbAsync(async db =>
    {
        await db.Incidents.Where(i => i.ParkId == parkId).ExecuteDeleteAsync();
        await db.ParkBankAccounts.Where(a => a.ParkId == parkId).ExecuteDeleteAsync();
        return await db.Parks.Where(p => p.Id == parkId).ExecuteDeleteAsync();
    });

    private async Task<(Guid Id, string Email)> OperatorAccountAsync(Guid? licenseeId)
    {
        var email = $"scope-{Guid.NewGuid():N}@operator.local";
        var id = await _f.DbAsync(async db =>
        {
            var u = new AdminUser
            {
                Email = email, Name = "Scope operator", Role = "operator", LicenseeId = licenseeId, IsActive = true,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("operator7!", workFactor: 4),
            };
            db.AdminUsers.Add(u);
            await db.SaveChangesAsync();
            return u.Id;
        });
        return (id, email);
    }

    // ── D1: an operator reaches its own licensee's parks only ───────

    [Fact]
    public async Task An_operator_never_reaches_a_Swich_direct_park()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var direct = await DirectParkAsync(swich);
        var batumi = await ParkIdAsync(Batumi);
        try
        {
            Assert.Contains(direct, ParkIds(await swich.GetFromJsonAsync<JsonElement>("/api/admin/parks")));
            var opParks = ParkIds(await op.GetFromJsonAsync<JsonElement>("/api/admin/parks"));
            Assert.DoesNotContain(direct, opParks);
            Assert.Contains(batumi, opParks);   // its own licensee's

            foreach (var url in new[]
            {
                $"/api/admin/parks/{direct}/drivers", $"/api/admin/parks/{direct}/kpis", $"/api/admin/parks/{direct}/cashouts",
                $"/api/admin/parks/{direct}/bank-accounts", $"/api/admin/parks/{direct}/reports", $"/api/admin/parks/{direct}/settlements/months",
                $"/api/admin/parks/{direct}/reconciliation/discrepancies",
            })
                Assert.True((await op.GetAsync(url)).StatusCode == HttpStatusCode.Forbidden, url);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/admin/parks/{direct}/status", new { status = "Suspended", reason = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PatchAsJsonAsync($"/api/admin/parks/{direct}", new { phone = "+995555123002" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/admin/incidents", new { title = "Direct park down", level = "P2", parkId = direct })).StatusCode);
            var settlements = await op.GetFromJsonAsync<JsonElement>($"/api/admin/settlements?parkId={direct}");
            Assert.Equal(0, settlements.GetProperty("count").GetInt32());

            // Swich's incident at the direct park is not in the operator's log.
            var incident = await swich.PostAsJsonAsync("/api/admin/incidents", new { title = "Direct park only", level = "P3", parkId = direct });
            var incidentId = (await incident.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident").GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/admin/incidents/{incidentId}")).StatusCode);
            Assert.DoesNotContain((await op.GetFromJsonAsync<JsonElement>("/api/admin/incidents?state=all")).GetProperty("incidents").EnumerateArray(),
                i => i.GetProperty("id").GetGuid() == incidentId);
        }
        finally { await DeleteParkAsync(direct); }
    }

    [Fact]
    public async Task Deactivating_a_licensee_cuts_its_operator_accounts_off_until_it_is_in_force_again()
    {
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.DbAsync(db => db.Licensees.AsNoTracking().Where(l => l.IsActive).Select(l => l.Id).SingleAsync());
        var batumi = await ParkIdAsync(Batumi);
        var (opId, email) = await OperatorAccountAsync(licensee);
        const string probeTitle = "Platform down (reported by an ended licensee's operator)";
        try
        {
            var op = await _f.AdminClientAsync(email, "operator7!");
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/admin/parks/{batumi}/drivers")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"/api/admin/licensees/{licensee}", new { isActive = false })).StatusCode);

            // The token in hand stops working everywhere — the parks, and the platform-wide part too: no incident log or
            // metrics, no platform-wide incident (a P1 is a danger banner on every park's console), no notes, no statements
            // or shared costs.
            foreach (var url in new[]
            {
                "/api/admin/parks", $"/api/admin/parks/{batumi}/drivers", $"/api/admin/parks/{batumi}/cashouts",
                "/api/admin/alerts", "/api/admin/incidents?state=all", "/api/admin/incidents/metrics",
                "/api/admin/settlements/month", "/api/admin/licensees", $"/api/admin/licensees/{licensee}/costs",
            })
                Assert.True((await op.GetAsync(url)).StatusCode == HttpStatusCode.Unauthorized, url);
            Assert.Equal(HttpStatusCode.Unauthorized, (await op.PostAsJsonAsync("/api/admin/incidents", new { title = probeTitle, level = "P1" })).StatusCode);
            Assert.False(await _f.DbAsync(db => db.Incidents.AnyAsync(i => i.Title == probeTitle)));
            Assert.Equal(HttpStatusCode.Unauthorized, (await op.PostAsJsonAsync($"/api/admin/incidents/{Guid.NewGuid()}/notes", new { text = "still here" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await op.PostAsJsonAsync($"/api/admin/licensees/{licensee}/costs",
                new { category = "Sms", vendor = "Ended SMS LLC", invoiceNumber = "END-1", invoiceDate = "2026-10-01", amount = 1.23m })).StatusCode);

            // Nor can it sign in again: the password is right, the licence is not in force (said only to who knows the password).
            var login = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email, password = "operator7!" });
            Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
            Assert.Equal("licensee_not_in_force", ApiFixture.Code(await login.Content.ReadFromJsonAsync<JsonElement>()));
            var wrong = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email, password = "not-the-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal("invalid_credentials", ApiFixture.Code(await wrong.Content.ReadFromJsonAsync<JsonElement>()));

            // Swich and the park's manager are not affected.
            Assert.Equal(HttpStatusCode.OK, (await swich.GetAsync($"/api/admin/parks/{batumi}/drivers")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await (await _f.ParkAdminAsync(Batumi)).GetAsync($"/api/admin/parks/{batumi}/drivers")).StatusCode);
        }
        finally
        {
            await swich.PatchAsJsonAsync($"/api/admin/licensees/{licensee}", new { isActive = true });
            await _f.DbAsync(db => db.Incidents.Where(i => i.Title == probeTitle).ExecuteDeleteAsync());
            await _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == opId).ExecuteDeleteAsync());
        }

        // In force again: its operators sign in and are back (the accounts were never deactivated — the check denied).
        var back = await _f.OperatorAsync();
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync($"/api/admin/parks/{batumi}/drivers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/admin/incidents/metrics")).StatusCode);
    }

    [Fact]
    public async Task An_operator_bound_to_no_licensee_cannot_sign_in_and_an_unbound_token_stops()
    {
        var licensee = await _f.DbAsync(db => db.Licensees.AsNoTracking().Where(l => l.IsActive).Select(l => l.Id).SingleAsync());
        var batumi = await ParkIdAsync(Batumi);
        var (unboundId, unboundEmail) = await OperatorAccountAsync(null);
        var (boundId, boundEmail) = await OperatorAccountAsync(licensee);
        try
        {
            var login = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email = unboundEmail, password = "operator7!" });
            Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
            Assert.Equal("licensee_not_in_force", ApiFixture.Code(await login.Content.ReadFromJsonAsync<JsonElement>()));

            // Signed in while bound; unbound by hand afterwards: the token in hand is refused from then on.
            var op = await _f.AdminClientAsync(boundEmail, "operator7!");
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/admin/parks/{batumi}/drivers")).StatusCode);
            await _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == boundId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LicenseeId, (Guid?)null)));
            Assert.Equal(HttpStatusCode.Unauthorized, (await op.GetAsync("/api/admin/parks")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await op.GetAsync("/api/admin/incidents/metrics")).StatusCode);
        }
        finally { await _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == unboundId || a.Id == boundId).ExecuteDeleteAsync()); }
    }

    [Fact]
    public async Task Deactivating_a_licensee_applies_at_once_even_where_the_account_check_is_cached()
    {
        // Production caches the account check for Auth:AdminRecheckSeconds (30 s; the test host 0). The licensee's PATCH drops
        // its operators' cached entries, so "cut off" does not wait for the cache to run out.
        var swich = await _f.SuperAdminAsync();
        var licensee = await _f.DbAsync(db => db.Licensees.AsNoTracking().Where(l => l.IsActive).Select(l => l.Id).SingleAsync());
        var (opId, email) = await OperatorAccountAsync(licensee);
        try
        {
            var token = await AdminTokenAsync(email, "operator7!");
            var cached = TimeSpan.FromSeconds(30);
            Assert.False(await TokenCheckProbe.RefusedAsync(_f, token, Api.Security.AdminTokenCheck.ValidateAsync, cached));

            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"/api/admin/licensees/{licensee}", new { isActive = false })).StatusCode);
            Assert.True(await TokenCheckProbe.RefusedAsync(_f, token, Api.Security.AdminTokenCheck.ValidateAsync, cached));

            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"/api/admin/licensees/{licensee}", new { isActive = true })).StatusCode);
            Assert.False(await TokenCheckProbe.RefusedAsync(_f, token, Api.Security.AdminTokenCheck.ValidateAsync, cached));
        }
        finally
        {
            await swich.PatchAsJsonAsync($"/api/admin/licensees/{licensee}", new { isActive = true });
            await _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == opId).ExecuteDeleteAsync());
        }
    }

    private async Task<string> AdminTokenAsync(string email, string password)
    {
        var res = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    // ── D2: resolving money mismatches ───────────────────────────────

    private Task<Guid[]> DiscrepanciesAsync(Guid parkId, int count) => _f.DbAsync(async db =>
    {
        var run = new ReconciliationRun
        {
            ParkId = parkId, WindowFrom = DateTime.UtcNow.AddHours(-24), WindowTo = DateTime.UtcNow,
            Status = ReconciliationStatus.Completed, FinishedAt = DateTime.UtcNow,
        };
        db.ReconciliationRuns.Add(run);
        var rows = Enumerable.Range(0, count).Select(i => new ReconciliationDiscrepancy
        {
            RunId = run.Id, ParkId = parkId, Kind = "missing_in_bank", PaytaxiAmount = 10m + i,
            BankTransferId = "scope-" + Guid.NewGuid().ToString("N"), Notes = "Cashout marked Completed but no matching bank transfer in window",
        }).ToList();
        db.ReconciliationDiscrepancies.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToArray();
    });

    private Task DeleteDiscrepanciesAsync(Guid[] ids) => _f.DbAsync(async db =>
    {
        var runs = await db.ReconciliationDiscrepancies.Where(d => ids.Contains(d.Id)).Select(d => d.RunId).Distinct().ToListAsync();
        await db.ReconciliationDiscrepancies.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync();
        return await db.ReconciliationRuns.Where(r => runs.Contains(r.Id)).ExecuteDeleteAsync();
    });

    [Fact]
    public async Task A_park_manager_notes_a_money_mismatch_and_only_Swich_or_its_operator_resolve_it_with_a_reason()
    {
        var batumi = await ParkIdAsync(Batumi);
        var ids = await DiscrepanciesAsync(batumi, 2);
        var url = $"/api/admin/parks/{batumi}/reconciliation/discrepancies";
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var resolve = await manager.PostAsJsonAsync($"{url}/{ids[0]}/resolve", new { notes = "We paid this one by hand" });
            Assert.Equal(HttpStatusCode.Forbidden, resolve.StatusCode);

            var note = await manager.PostAsJsonAsync($"{url}/{ids[0]}/note", new { note = "Paid by hand from our TBC app on Monday" });
            Assert.Equal(HttpStatusCode.OK, note.StatusCode);
            Assert.False(await _f.DbAsync(db => db.ReconciliationDiscrepancies.Where(d => d.Id == ids[0]).Select(d => d.IsResolved).FirstAsync()));

            // Still open — on the banner — and Swich reads the note (and whose it is).
            var managerView = await manager.GetFromJsonAsync<JsonElement>(url);
            Assert.False(managerView.GetProperty("canResolve").GetBoolean());
            Assert.True(managerView.GetProperty("canNote").GetBoolean());
            var managerRow = managerView.GetProperty("discrepancies").EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == ids[0]);
            Assert.False(managerRow.GetProperty("isResolved").GetBoolean());
            Assert.Equal("Paid by hand from our TBC app on Monday", managerRow.GetProperty("parkNote").GetString());
            Assert.Equal(JsonValueKind.Null, managerRow.GetProperty("parkNoteBy").ValueKind);   // who wrote it: the platform side's to see
            var swich = await _f.SuperAdminAsync();
            var swichView = await swich.GetFromJsonAsync<JsonElement>(url);
            Assert.True(swichView.GetProperty("canResolve").GetBoolean());
            var seen = swichView.GetProperty("discrepancies").EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == ids[0]);
            Assert.Equal("Paid by hand from our TBC app on Monday", seen.GetProperty("parkNote").GetString());
            Assert.Equal("admin:manager@batumi-auto-park-1.local", seen.GetProperty("parkNoteBy").GetString());
            Assert.Contains((await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")).GetProperty("alerts").EnumerateArray(),
                a => a.GetProperty("kind").GetString() == "reconciliation_open" && a.GetProperty("parkId").GetString() == batumi.ToString());

            // Resolving needs a reason that says something.
            foreach (var bad in new object[] { new { }, new { notes = "" }, new { notes = "ok" }, new { notes = "    " } })
            {
                var res = await swich.PostAsJsonAsync($"{url}/{ids[0]}/resolve", bad);
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
                Assert.Equal("reason_required", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
            }
            Assert.Equal(HttpStatusCode.NoContent, (await swich.PostAsJsonAsync($"{url}/{ids[0]}/resolve", new { notes = "Matched to the bank statement of 14 Oct" })).StatusCode);
            var resolved = await _f.DbAsync(db => db.ReconciliationDiscrepancies.AsNoTracking().FirstAsync(d => d.Id == ids[0]));
            Assert.True(resolved.IsResolved);
            Assert.Equal("Matched to the bank statement of 14 Oct", resolved.ResolutionNotes);
            Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{url}/{ids[0]}/note", new { note = "late" })).StatusCode);

            // The operator of the park's licensee resolves too; the platform side writes a reason, not a park note.
            var op = await _f.OperatorAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"{url}/{ids[1]}/note", new { note = "operator note" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"{url}/{ids[1]}/resolve", new { notes = "Reversal confirmed with the park" })).StatusCode);
        }
        finally { await DeleteDiscrepanciesAsync(ids); }
    }

    [Fact]
    public async Task An_operator_cannot_resolve_a_Swich_direct_park_s_discrepancy()
    {
        var swich = await _f.SuperAdminAsync();
        var direct = await DirectParkAsync(swich);
        var ids = await DiscrepanciesAsync(direct, 1);
        try
        {
            var op = await _f.OperatorAsync();
            var res = await op.PostAsJsonAsync($"/api/admin/parks/{direct}/reconciliation/discrepancies/{ids[0]}/resolve", new { notes = "Not ours to close" });
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.False(await _f.DbAsync(db => db.ReconciliationDiscrepancies.Where(d => d.Id == ids[0]).Select(d => d.IsResolved).FirstAsync()));
        }
        finally
        {
            await DeleteDiscrepanciesAsync(ids);
            await DeleteParkAsync(direct);
        }
    }
}
