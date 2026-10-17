using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// What the monthly statement is computed from, and the record of every change to it (licence agreement §2.3–2.6):
///   - the launch date moves only before it passes, with a reason, and every move is on record;
///   - the change history is the same for both sides and can never be rewritten;
///   - each side changes only its own shared costs, and a deletion keeps the row with its reason;
///   - a cost entered less than 48 hours before the run rolls into the next statement;
///   - the other side can hold a disputed cost out of the statements until it releases it;
///   - an included cost is fixed by a linked correction the next statement counts, negative ones included.
/// Each test has its own licensee with no parks (statements of 2020 months, costs only), so no fee of another
/// test is ever claimed; the licensee is deactivated afterwards.
/// </summary>
[Collection("api")]
public class AgreementRecordsTests
{
    private readonly ApiFixture _f;
    public AgreementRecordsTests(ApiFixture f) => _f = f;

    private static DateOnly Today => SettlementService.TodayLocal();
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string S(JsonElement e, string prop) => e.GetProperty(prop).GetString() ?? "";
    private static decimal D(JsonElement e, string prop) => e.GetProperty(prop).GetDecimal();

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient c, HttpMethod method, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (res.StatusCode, json);
    }

    private static async Task<JsonElement> AddCostAsync(HttpClient c, Guid lic, string category, string vendor, string number, string date, decimal amount)
    {
        var (status, body) = await SendAsync(c, HttpMethod.Post, $"/api/admin/licensees/{lic}/costs",
            new { category, vendor, invoiceNumber = number, invoiceDate = date, amount });
        Assert.True(status == HttpStatusCode.OK, $"cost {number}: {(int)status} {body}");
        return body;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> EventsAsync(HttpClient c, Guid lic, string? kind = null)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/admin/licensees/{lic}/events" + (kind is null ? "" : $"?kind={kind}"));
        return body.GetProperty("events").EnumerateArray().ToList();
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient c, Guid lic, string month) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/preview?licenseeId={lic}&month={month}");

    private static async Task<JsonElement> RunAsync(HttpClient c, Guid lic, string month)
    {
        var (status, body) = await SendAsync(c, HttpMethod.Post, $"/api/admin/settlements/run?month={month}&licenseeId={lic}");
        Assert.True(status == HttpStatusCode.OK, $"run {month}: {(int)status} {body}");
        return body.GetProperty("statements").EnumerateArray().Single();
    }

    private static JsonElement CostIn(JsonElement list, Guid id) =>
        list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);

    private Task<CommonCost> RowAsync(Guid costId) =>
        _f.DbAsync(db => db.CommonCosts.AsNoTracking().FirstAsync(c => c.Id == costId));

    // ═══════════════════════════════════════════════════════════════════
    // Launch date
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task The_launch_date_moves_only_before_it_passes_with_a_reason_and_every_move_is_on_record()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        Guid? guardStatement = null;
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Launch licensee", launch: null);
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            var url = $"/api/admin/licensees/{lic}";
            var first = Iso(Today.AddDays(10));
            var moved = Iso(Today.AddDays(20));

            // No reason, or a day already gone: refused, and nothing is recorded.
            var noReason = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = first });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
            Assert.Equal("launch_reason_required", ApiFixture.Code(noReason.Body));
            var backdated = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = Iso(Today.AddDays(-1)), reason = "Forgot to enter it" });
            Assert.Equal(HttpStatusCode.BadRequest, backdated.Status);
            Assert.Equal("launch_date_in_past", ApiFixture.Code(backdated.Body));
            Assert.Empty(await EventsAsync(op, lic, "launch_date"));

            // Set, then moved while it has not passed: each move is recorded old → new, by whom and why — and the
            // operator reads exactly that.
            var set = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = first, reason = "Agreed by e-mail" });
            Assert.True(set.Status == HttpStatusCode.OK, set.Body.ToString());
            Assert.False(set.Body.GetProperty("launchDateLocked").GetBoolean());
            var move = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = moved, reason = "Bank integration slipped" });
            Assert.True(move.Status == HttpStatusCode.OK, move.Body.ToString());

            var events = await EventsAsync(op, lic, "launch_date");
            Assert.Equal(2, events.Count);
            Assert.Equal(moved, events[0].GetProperty("new").GetProperty("launchDate").GetString());   // newest first
            Assert.Equal(first, events[0].GetProperty("old").GetProperty("launchDate").GetString());
            Assert.Equal("Bank integration slipped", S(events[0], "reason"));
            Assert.Equal("admin:ops@swich.dev", S(events[0], "actor"));
            Assert.Equal(JsonValueKind.Null, events[1].GetProperty("old").GetProperty("launchDate").ValueKind);
            Assert.Equal("Agreed by e-mail", S(events[1], "reason"));
            var card = await op.GetFromJsonAsync<JsonElement>(url);
            Assert.Equal(2, card.GetProperty("launchDateHistory").GetArrayLength());

            // Once its day has passed it is locked: no move, no clearing — and a refused attempt leaves no entry.
            await kit.SetLaunchAsync(lic, Today.AddDays(-1));
            var late = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = Iso(Today.AddDays(5)), reason = "Moving the trial" });
            Assert.Equal(HttpStatusCode.Conflict, late.Status);
            Assert.Equal("launch_date_locked", ApiFixture.Code(late.Body));
            var cleared = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = "", reason = "Moving the trial" });
            Assert.Equal(HttpStatusCode.Conflict, cleared.Status);
            Assert.Equal("launch_date_locked", ApiFixture.Code(cleared.Body));
            Assert.Equal(2, (await EventsAsync(op, lic, "launch_date")).Count);
            // The rest of the card still edits; sending the same date again is no change at all.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(swich, HttpMethod.Patch, url, new { email = "billing@licensee.ge" })).Status);
            var same = await SendAsync(swich, HttpMethod.Patch, url, new { launchDate = Iso(Today.AddDays(-1)) });
            Assert.Equal(HttpStatusCode.OK, same.Status);
            Assert.True(same.Body.GetProperty("launchDateLocked").GetBoolean());

            // A launch date that is still movable cannot move past a statement already on record (the guard stays).
            var lic2 = await kit.CreateLicenseeAsync(swich, "Launch guard licensee", launch: null);
            await kit.SetLaunchAsync(lic2, Today.AddDays(3));
            var nextMonth = SettlementService.FirstOfMonth(Today).AddMonths(1);
            guardStatement = await _f.DbAsync(async db =>
            {
                var (from, to) = SettlementService.MonthBoundsUtcFor(nextMonth, null);
                var st = new MonthlyStatement
                {
                    LicenseeId = lic2, Month = nextMonth, PeriodFromUtc = from, PeriodToUtc = to, BarrierGel = 20_000m,
                    SwichSharePercent = 50m, Phase = StatementPhase.Barrier, BarrierFraction = 1m,
                    Status = MonthlyStatementStatus.Completed, InvoiceRef = SettlementService.InvoiceRefFor(nextMonth), InitiatedBy = "test",
                };
                db.MonthlyStatements.Add(st);
                await db.SaveChangesAsync();
                return st.Id;
            });
            var past = await SendAsync(swich, HttpMethod.Patch, $"/api/admin/licensees/{lic2}",
                new { launchDate = Iso(nextMonth.AddMonths(1)), reason = "Later start" });
            Assert.Equal(HttpStatusCode.Conflict, past.Status);
            Assert.Equal("launch_after_first_statement", ApiFixture.Code(past.Body));
        }
        finally
        {
            if (guardStatement is { } gs) await _f.DbAsync(db => db.MonthlyStatements.Where(m => m.Id == gs).ExecuteDeleteAsync());
            await kit.ReturnAsync();
        }
    }

    [Fact]
    public async Task The_development_seed_puts_its_launch_and_sample_invoices_on_record_and_counts_them_at_once()
    {
        var (costs, events) = await _f.DbAsync(async db =>
        {
            var seeded = await db.Licensees.AsNoTracking().OrderBy(l => l.CreatedAt).FirstAsync();
            var c = await db.CommonCosts.AsNoTracking().Where(x => x.LicenseeId == seeded.Id && x.CreatedBy == "seed").ToListAsync();
            var e = await db.AgreementEvents.AsNoTracking().Where(x => x.LicenseeId == seeded.Id && x.Actor == "seed").ToListAsync();
            return (c, e);
        });
        Assert.NotEmpty(costs);
        // Entered before the 48-hour window, so the running month's preview shows them counted from the first start.
        Assert.All(costs, c => Assert.True(SharedCostRules.Countable(c, DateTime.UtcNow, 48), $"{c.InvoiceNumber} entered {c.EnteredAt:o}"));
        var launch = Assert.Single(events, e => e.Kind == AgreementEventKinds.LaunchDate);
        Assert.False(string.IsNullOrWhiteSpace(launch.Reason));
        Assert.Equal(costs.Select(c => c.Id).OrderBy(x => x), events.Where(e => e.Kind == AgreementEventKinds.CostAdded)
            .Select(e => e.ReferenceId!.Value).OrderBy(x => x));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Change history
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task The_change_history_is_the_same_for_both_sides_and_can_never_be_rewritten()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(SettlementKit.P5);
        var kit = new SettlementKit(_f);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "History licensee", launch: null);
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            var mine = await AddCostAsync(op, lic, "Sms", "SMSOffice", "H-1", "2020-02-03", 1.00m);
            var theirs = await AddCostAsync(swich, lic, "Hosting", "Hetzner", "H-2", "2020-02-04", 2.00m);

            // §2.6: identical data for both sides — newest first, each entry naming its cost and what it was.
            var asOperator = await EventsAsync(op, lic, "cost_added");
            var asSwich = await EventsAsync(swich, lic, "cost_added");
            Assert.Equal(string.Join("|", asSwich.Select(e => e.ToString())), string.Join("|", asOperator.Select(e => e.ToString())));
            Assert.Equal(new[] { Id(theirs), Id(mine) }, asOperator.Select(e => e.GetProperty("referenceId").GetGuid()).ToArray());
            Assert.Equal("admin:levan@operator.local", S(asOperator[1], "actor"));
            Assert.Equal(1.00m, asOperator[1].GetProperty("new").GetProperty("amount").GetDecimal());
            Assert.Equal("H-1", asOperator[1].GetProperty("cost").GetProperty("invoiceNumber").GetString());
            Assert.Contains(await EventsAsync(op, lic), e => S(e, "kind") == "terms");   // the terms it was registered with

            // Not the park's business; and no route rewrites or removes an entry.
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/admin/licensees/{lic}/events")).StatusCode);
            var eventId = asOperator[0].GetProperty("id").GetInt64();
            foreach (var method in new[] { HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Put })
            {
                var (status, _) = await SendAsync(swich, method, $"/api/admin/licensees/{lic}/events/{eventId}", new { reason = "rewritten" });
                Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} → {(int)status}");
            }

            // Nor does the data layer: a changed or deleted entry is refused before it reaches the database.
            await Assert.ThrowsAsync<InvalidOperationException>(() => _f.DbAsync(async db =>
            {
                var e = await db.AgreementEvents.FirstAsync(x => x.Id == eventId);
                e.Reason = "rewritten";
                return await db.SaveChangesAsync();
            }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _f.DbAsync(async db =>
            {
                db.AgreementEvents.Remove(await db.AgreementEvents.FirstAsync(x => x.Id == eventId));
                return await db.SaveChangesAsync();
            }));
            Assert.Equal(2, (await EventsAsync(op, lic, "cost_added")).Count);
        }
        finally { await kit.ReturnAsync(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Ownership and soft delete
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Each_side_changes_only_its_own_costs_and_a_deletion_keeps_the_row_with_its_reason()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Ownership licensee", "2020-03-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            var mine = await AddCostAsync(op, lic, "Sms", "SMSOffice", "O-1", "2020-03-03", 2.50m);
            var theirs = await AddCostAsync(swich, lic, "Hosting", "Hetzner", "O-2", "2020-03-04", 9.99m);
            var (mineId, theirsId) = (Id(mine), Id(theirs));
            var costs = $"/api/admin/licensees/{lic}/costs";

            // Even Swich cannot change the licensee's entries — and the operator cannot change Swich's.
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(swich, HttpMethod.Patch, $"{costs}/{mineId}", new { amount = 0.25m })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(swich, HttpMethod.Delete, $"{costs}/{mineId}", new { reason = "Looks wrong" })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(op, HttpMethod.Patch, $"{costs}/{theirsId}", new { amount = 1m })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(op, HttpMethod.Delete, $"{costs}/{theirsId}", new { reason = "Looks wrong" })).Status);
            Assert.False(mine.GetProperty("canHold").GetBoolean());          // nobody holds their own entry
            Assert.True(mine.GetProperty("canEdit").GetBoolean());

            // The owner's edit is recorded with the amount before and after.
            var edit = await SendAsync(op, HttpMethod.Patch, $"{costs}/{mineId}", new { amount = 2.60m });
            Assert.Equal(HttpStatusCode.OK, edit.Status);
            var edited = Assert.Single(await EventsAsync(swich, lic, "cost_edited"));
            Assert.Equal(2.50m, edited.GetProperty("old").GetProperty("amount").GetDecimal());
            Assert.Equal(2.60m, edited.GetProperty("new").GetProperty("amount").GetDecimal());
            Assert.Equal(mineId, edited.GetProperty("referenceId").GetGuid());

            // A deletion needs a reason; it keeps the row, never counted, struck through with who and why.
            var noReason = await SendAsync(op, HttpMethod.Delete, $"{costs}/{mineId}");
            Assert.Equal(HttpStatusCode.BadRequest, noReason.Status);
            Assert.Equal("reason_required", ApiFixture.Code(noReason.Body));
            var del = await SendAsync(op, HttpMethod.Delete, $"{costs}/{mineId}", new { reason = "Entered twice" });
            Assert.True(del.Status == HttpStatusCode.OK, del.Body.ToString());
            Assert.Equal("deleted", S(del.Body, "state"));
            var row = await RowAsync(mineId);
            Assert.NotNull(row.DeletedAt);
            Assert.Equal("admin:levan@operator.local", row.DeletedBy);
            Assert.Equal("Entered twice", row.DeleteReason);
            var listed = CostIn((await op.GetFromJsonAsync<JsonElement>(costs)).GetProperty("costs"), mineId);
            Assert.Equal("Entered twice", listed.GetProperty("deleted").GetProperty("reason").GetString());
            Assert.False(listed.GetProperty("canEdit").GetBoolean());

            // Gone from the month's figures, shown apart; a deleted entry cannot be edited or deleted again.
            await kit.EnteredHoursAgoAsync(theirsId, 72);
            var preview = await PreviewAsync(swich, lic, "2020-03");
            Assert.Equal(0m, D(preview, "licenseeCosts"));
            Assert.Equal(9.99m, D(preview, "swichCosts"));
            Assert.DoesNotContain(preview.GetProperty("costs").EnumerateArray(), c => c.GetProperty("id").GetGuid() == mineId);
            Assert.Equal("deleted", S(CostIn(preview.GetProperty("deletedCosts"), mineId), "state"));
            Assert.Equal("cost_deleted", ApiFixture.Code((await SendAsync(op, HttpMethod.Patch, $"{costs}/{mineId}", new { amount = 1m })).Body));
            Assert.Equal("cost_deleted", ApiFixture.Code((await SendAsync(op, HttpMethod.Delete, $"{costs}/{mineId}", new { reason = "Again" })).Body));
            // The same invoice can be entered again — the deleted row does not count as a duplicate.
            await AddCostAsync(op, lic, "Sms", "SMSOffice", "O-1", "2020-03-03", 2.60m);

            var deleted = Assert.Single(await EventsAsync(swich, lic, "cost_deleted"));
            Assert.Equal("Entered twice", S(deleted, "reason"));
            Assert.Equal(2.60m, deleted.GetProperty("old").GetProperty("amount").GetDecimal());
        }
        finally { await kit.ReturnAsync(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 48-hour cut-off
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_cost_entered_less_than_48_hours_before_the_run_rolls_into_the_next_statement()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Cut-off licensee", "2020-01-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            var early = await AddCostAsync(op, lic, "Sms", "SMSOffice", "C-1", "2020-01-10", 1.00m);
            await kit.EnteredHoursAgoAsync(Id(early), 49);
            var fresh = await AddCostAsync(op, lic, "Bank", "TBC Bank", "C-2", "2020-01-12", 2.00m);

            // The fresh one is not counted yet: shown apart, with when it will be.
            var preview = await PreviewAsync(op, lic, "2020-01");
            Assert.Equal(1.00m, D(preview, "licenseeCosts"));
            var waiting = CostIn(preview.GetProperty("lateCosts"), Id(fresh));
            Assert.Equal("waiting", S(waiting, "state"));
            var eligibleFrom = waiting.GetProperty("eligibleFrom").GetDateTime();
            Assert.InRange(eligibleFrom, DateTime.UtcNow.AddHours(47), DateTime.UtcNow.AddHours(49));
            // The console says whether it makes the next scheduled run — the month view carries that run's time.
            var view = await op.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2020-01");
            Assert.True(view.GetProperty("nextStatementRunAt").GetDateTime() > DateTime.UtcNow);
            Assert.Equal(48, view.GetProperty("costCutoffHours").GetInt32());
            Assert.Equal(waiting.GetProperty("eligibleFrom").GetDateTime() > view.GetProperty("nextStatementRunAt").GetDateTime(),
                waiting.GetProperty("missesNextRun").GetBoolean());

            var jan = await RunAsync(swich, lic, "2020-01");
            Assert.Equal(1.00m, D(jan, "licenseeCosts"));
            Assert.Null((await RowAsync(Id(fresh))).StatementId);
            Assert.Equal("waiting", S(CostIn(jan.GetProperty("lateCosts"), Id(fresh)), "state"));

            // Two days on, it goes into the next statement.
            await kit.EnteredHoursAgoAsync(Id(fresh), 49);
            var feb = await RunAsync(swich, lic, "2020-02");
            Assert.Equal(2.00m, D(feb, "licenseeCosts"));

            // An edit restarts the window (the other side must see the new figures too) — a note alone does not.
            var edited = await AddCostAsync(op, lic, "Hosting", "Hetzner", "C-3", "2020-03-02", 3.00m);
            var noted = await AddCostAsync(op, lic, "Domain", "Caucasus Online", "C-4", "2020-03-03", 4.00m);
            await kit.EnteredHoursAgoAsync(Id(edited), 72);
            await kit.EnteredHoursAgoAsync(Id(noted), 72);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, HttpMethod.Patch, $"/api/admin/licensees/{lic}/costs/{Id(edited)}", new { amount = 3.30m })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, HttpMethod.Patch, $"/api/admin/licensees/{lic}/costs/{Id(noted)}", new { note = "1 year" })).Status);
            var march = await PreviewAsync(op, lic, "2020-03");
            Assert.Equal(4.00m, D(march, "licenseeCosts"));
            Assert.Equal("waiting", S(CostIn(march.GetProperty("lateCosts"), Id(edited)), "state"));

            // The window is configuration (Settlement:CostCutoffHours): with none, the edited one counts at once.
            using var scope = _f.Services.CreateScope();
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<SettlementOptions>>().Value;
            Assert.Equal(48, opts.CostCutoffHours);
            var noWindow = new SettlementService(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider,
                Options.Create(new SettlementOptions { SwichIban = opts.SwichIban, AllowOpenMonthRun = true, CostCutoffHours = 0 }),
                NullLogger<SettlementService>.Instance);
            Assert.Equal(7.30m, (await noWindow.PreviewLicenseeMonthAsync(lic, new DateOnly(2020, 3, 1))).LicenseeCosts);
        }
        finally { await kit.ReturnAsync(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Hold
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task The_other_side_holds_a_disputed_cost_out_of_the_statements_until_it_releases_it()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Hold licensee", "2020-04-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            var costs = $"/api/admin/licensees/{lic}/costs";
            var hosting = await AddCostAsync(op, lic, "Hosting", "Big Cloud", "D-1", "2020-04-28", 5_000.00m);
            var domain = await AddCostAsync(swich, lic, "Domain", "Caucasus Online", "D-2", "2020-04-05", 30.00m);
            var (hostingId, domainId) = (Id(hosting), Id(domain));
            await kit.EnteredHoursAgoAsync(hostingId, 72);
            await kit.EnteredHoursAgoAsync(domainId, 72);

            // Only the other side holds, and only with a reason.
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(op, HttpMethod.Post, $"{costs}/{hostingId}/hold", new { reason = "Mine" })).Status);
            var noReason = await SendAsync(swich, HttpMethod.Post, $"{costs}/{hostingId}/hold", new { });
            Assert.Equal("reason_required", ApiFixture.Code(noReason.Body));
            var held = await SendAsync(swich, HttpMethod.Post, $"{costs}/{hostingId}/hold", new { reason = "No invoice copy received" });
            Assert.True(held.Status == HttpStatusCode.OK, held.Body.ToString());
            Assert.Equal("held", S(held.Body, "state"));
            Assert.Equal("No invoice copy received", held.Body.GetProperty("held").GetProperty("reason").GetString());
            Assert.Equal("admin:ops@swich.dev", held.Body.GetProperty("held").GetProperty("by").GetString());
            Assert.Equal("cost_already_held", ApiFixture.Code((await SendAsync(swich, HttpMethod.Post, $"{costs}/{hostingId}/hold", new { reason = "Again" })).Body));
            // The owner cannot release the other side's hold.
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(op, HttpMethod.Post, $"{costs}/{hostingId}/release", new { })).Status);

            // Both sides see it prominently; the statement leaves it out — and it stays out after the run.
            foreach (var who in new[] { swich, op })
            {
                var view = await who.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2020-04");
                var st = view.GetProperty("statements").EnumerateArray().Single(s => s.GetProperty("licenseeId").GetGuid() == lic);
                Assert.Equal(hostingId, Assert.Single(st.GetProperty("heldCosts").EnumerateArray()).GetProperty("id").GetGuid());
                Assert.Equal(0m, D(st, "licenseeCosts"));
            }
            var april = await RunAsync(swich, lic, "2020-04");
            Assert.Equal(0m, D(april, "licenseeCosts"));
            Assert.Equal(30.00m, D(april, "swichCosts"));
            var stillHeld = await RowAsync(hostingId);
            Assert.Null(stillHeld.StatementId);
            Assert.NotNull(stillHeld.HeldAt);
            // An included cost can no longer be held.
            Assert.Equal("cost_included", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, $"{costs}/{domainId}/hold", new { reason = "Disputed" })).Body));

            // Released by the side that held it → counted by the next statement.
            var released = await SendAsync(swich, HttpMethod.Post, $"{costs}/{hostingId}/release", new { reason = "Copy received" });
            Assert.True(released.Status == HttpStatusCode.OK, released.Body.ToString());
            Assert.Equal("pending", S(released.Body, "state"));
            var may = await RunAsync(swich, lic, "2020-05");
            Assert.Equal(5_000.00m, D(may, "licenseeCosts"));

            // Held by the operator, deleted by its owner: the hold does not stop the owner withdrawing it.
            var sms = await AddCostAsync(swich, lic, "Sms", "SMSOffice", "D-3", "2020-06-02", 12.00m);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, HttpMethod.Post, $"{costs}/{Id(sms)}/hold", new { reason = "Not our SMS volume" })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(swich, HttpMethod.Delete, $"{costs}/{Id(sms)}", new { reason = "Agreed: billed to Swich alone" })).Status);
            Assert.Equal("cost_deleted", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, $"{costs}/{Id(sms)}/release", new { })).Body));

            var kinds = (await EventsAsync(op, lic)).Select(e => S(e, "kind")).ToList();
            Assert.Equal(2, kinds.Count(k => k == "cost_held"));
            Assert.Equal(1, kinds.Count(k => k == "cost_released"));
            var heldEvent = (await EventsAsync(op, lic, "cost_held")).Last();
            Assert.Equal("No invoice copy received", S(heldEvent, "reason"));
            Assert.Equal("Copy received", S(Assert.Single(await EventsAsync(op, lic, "cost_released")), "reason"));
        }
        finally { await kit.ReturnAsync(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Corrections
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task An_included_cost_is_corrected_by_a_linked_entry_the_next_statement_counts()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Correction licensee", "2020-06-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            var costs = $"/api/admin/licensees/{lic}/costs";
            // The operator types 185.00 for an 18.50 TBC invoice; Swich enters its 10.00 hosting.
            var typo = await AddCostAsync(op, lic, "Bank", "TBC Bank", "K-1", "2020-06-05", 185.00m);
            var hosting = await AddCostAsync(swich, lic, "Hosting", "Hetzner", "K-2", "2020-06-07", 10.00m);
            await kit.EnteredHoursAgoAsync(Id(typo), 72);
            await kit.EnteredHoursAgoAsync(Id(hosting), 72);

            // June is settled with the typo (barrier phase, no income: E = −185.00, carried as the licensee's credit).
            var june = await RunAsync(swich, lic, "2020-06");
            Assert.Equal(185.00m, D(june, "licenseeCosts"));
            Assert.Equal(-185.00m, D(june, "carryOut"));

            // Settled money never changes; only its owner corrects it, with a reason, never below zero in total.
            Assert.Equal("cost_included", ApiFixture.Code((await SendAsync(op, HttpMethod.Patch, $"{costs}/{Id(typo)}", new { amount = 18.50m })).Body));
            var url = $"{costs}/{Id(typo)}/corrections";
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(swich, HttpMethod.Post, url, new { amount = -166.50m, reason = "Typo" })).Status);
            Assert.Equal("invalid_amount", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, url, new { amount = 0m, reason = "Typo" })).Body));
            Assert.Equal("reason_required", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, url, new { amount = -166.50m })).Body));
            Assert.Equal("amount_precision", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, url, new { amount = -166.505m, reason = "Typo" })).Body));
            Assert.Equal("correction_below_zero", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, url, new { amount = -185.01m, reason = "Typo" })).Body));
            var pending = await AddCostAsync(op, lic, "Sms", "SMSOffice", "K-3", "2020-07-01", 3.00m);
            Assert.Equal("cost_not_included", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, $"{costs}/{Id(pending)}/corrections", new { amount = -1m, reason = "Edit it instead" })).Body));
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, HttpMethod.Delete, $"{costs}/{Id(pending)}", new { reason = "Test entry" })).Status);

            var fix = await SendAsync(op, HttpMethod.Post, url, new { amount = -166.50m, reason = "Typo: the invoice is 18.50" });
            Assert.True(fix.Status == HttpStatusCode.OK, fix.Body.ToString());
            var correctionId = Id(fix.Body);
            Assert.Equal(Id(typo), fix.Body.GetProperty("correctsId").GetGuid());
            Assert.Equal("Licensee", S(fix.Body, "paidBy"));
            Assert.Equal("Bank", S(fix.Body, "category"));
            Assert.Equal("K-1", S(fix.Body, "invoiceNumber"));
            Assert.Equal(-166.50m, D(fix.Body, "amount"));
            Assert.Equal("waiting", S(fix.Body, "state"));
            // A second one may not take the original and its corrections below zero together.
            Assert.Equal("correction_below_zero", ApiFixture.Code((await SendAsync(op, HttpMethod.Post, url, new { amount = -18.51m, reason = "Again" })).Body));

            // Shown next to the original.
            var original = CostIn((await op.GetFromJsonAsync<JsonElement>(costs)).GetProperty("costs"), Id(typo));
            var linked = Assert.Single(original.GetProperty("corrections").EnumerateArray());
            Assert.Equal(-166.50m, D(linked, "amount"));
            Assert.Equal("Typo: the invoice is 18.50", S(linked, "reason"));
            Assert.True(original.GetProperty("canCorrect").GetBoolean());

            // July counts it: CL = −166.50 (the month's only licensee entry). Barrier phase, no income:
            // E = F − CL = 166.50, plus June's −185.00 credit → −18.50 carried — exactly what an 18.50 invoice gives.
            await kit.EnteredHoursAgoAsync(correctionId, 72);
            var july = await RunAsync(swich, lic, "2020-07");
            Assert.Equal(-166.50m, D(july, "licenseeCosts"));
            Assert.Equal(166.50m, D(july, "entitlement"));
            Assert.Equal(-18.50m, D(july, "carryOut"));
            Assert.Equal(0m, D(july, "due"));
            var counted = CostIn(july.GetProperty("costs"), correctionId);
            Assert.Equal("PT-2020-07", S(counted, "includedIn"));
            Assert.Equal("PT-2020-06", counted.GetProperty("corrects").GetProperty("includedIn").GetString());
            Assert.Equal("cost_included", ApiFixture.Code((await SendAsync(op, HttpMethod.Patch, $"{costs}/{correctionId}", new { amount = -160m })).Body));

            // Swich corrects its own upward; the report renders both statements.
            var up = await SendAsync(swich, HttpMethod.Post, $"{costs}/{Id(hosting)}/corrections", new { amount = 5.00m, reason = "Second page of the invoice" });
            Assert.True(up.Status == HttpStatusCode.OK, up.Body.ToString());
            await kit.EnteredHoursAgoAsync(Id(up.Body), 72);
            var aug = await RunAsync(swich, lic, "2020-08");
            Assert.Equal(5.00m, D(aug, "swichCosts"));
            var pdf = await swich.GetAsync($"/api/admin/settlements/statements/{july.GetProperty("statementId").GetGuid()}/report.pdf");
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);

            var added = await EventsAsync(op, lic, "correction_added");
            Assert.Equal(2, added.Count);
            Assert.Equal(correctionId, added[1].GetProperty("referenceId").GetGuid());
            Assert.Equal("Typo: the invoice is 18.50", S(added[1], "reason"));
            Assert.Equal(Id(typo), added[1].GetProperty("new").GetProperty("correctsId").GetGuid());
        }
        finally { await kit.ReturnAsync(); }
    }
}
