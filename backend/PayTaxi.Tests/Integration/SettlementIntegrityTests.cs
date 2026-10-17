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
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using Xunit;
using static PayTaxi.Tests.Integration.SettlementKit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The monthly settlement holding its money rules under pressure: the barrier stays reached once it is
/// reached (§2.3), two runs or a run and a cost edit can never count the same fee or invoice twice, two
/// executors never pay or book one park transfer twice, the §4.1.2 penalty follows each day's cause and
/// stays between Swich and the licensee, and a debt the licensee paid outside the platform (§2.5) is
/// closed for good. Each test settles its own licensee in its own past months (2020).
/// </summary>
[Collection("api")]
public class SettlementIntegrityTests
{
    private readonly ApiFixture _f;
    public SettlementIntegrityTests(ApiFixture f) => _f = f;

    /// <summary>A shared cost entered three days ago — past the 48-hour window, so the next statement counts it.</summary>
    private async Task<JsonElement> AddCostAsync(HttpClient c, Guid licenseeId, string category, string vendor, string number,
        string date, decimal amount)
    {
        var res = await c.PostAsJsonAsync($"/api/admin/licensees/{licenseeId}/costs",
            new { category, vendor, invoiceNumber = number, invoiceDate = date, amount });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"cost {number}: {(int)res.StatusCode} {body}");
        await new SettlementKit(_f).EnteredHoursAgoAsync(body.GetProperty("id").GetGuid(), 72);
        return body;
    }

    /// <summary>A month run whose outcome the test inspects itself (it may be refused by design).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> RunRawAsync(HttpClient c, string month, Guid licenseeId)
    {
        var res = await c.PostAsync($"/api/admin/settlements/run?month={month}&licenseeId={licenseeId}", null);
        return (res.StatusCode, await res.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static SettlementService Service(IServiceScope scope, IBankPayoutAdapter bank)
    {
        var banks = new ServiceCollection();
        foreach (var key in new[] { "mock", "MOCK", "tbc", "TBC" })
            banks.AddKeyedSingleton(key, bank);
        var sp = scope.ServiceProvider;
        return new SettlementService(
            sp.GetRequiredService<AppDbContext>(),
            banks.BuildServiceProvider(),
            sp.GetRequiredService<IOptions<SettlementOptions>>(),
            NullLogger<SettlementService>.Instance);
    }

    private static IEnumerable<JsonElement> Alerts(JsonElement body) => body.GetProperty("alerts").EnumerateArray();

    private static bool HasValue(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null;

    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Once_the_barrier_is_reached_a_loss_month_does_not_reopen_it()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Barrier stays licensee", "2020-01-01", barrier: 4m);
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await kit.AssignAsync(swich, pb, lic);

            // Jan: 5.00 of fees crosses the 4.00 barrier — filled.
            await kit.PlantAsync(pb, 10, 0.50m, Noon(2020, 1, 12));
            var jan = OnlyStatement(await RunAsync(swich, "2020-01", lic));
            Assert.Equal("Crossing", S(jan, "phase"));
            Assert.Equal(4.50m, D(jan, "entitlement"));

            // Feb: the licensee's 2.00 bank invoice exceeds the month's 0.50 of fees → ½(0.50 − 2.00) = −0.75,
            // carried as a credit. R drops to 3.75 — below B — but the barrier was reached in January.
            await kit.PlantAsync(pb, 1, 0.50m, Noon(2020, 2, 10));
            await AddCostAsync(op, lic, "Bank", "TBC Bank", "BS-1", "2020-02-05", 2.00m);
            var feb = OnlyStatement(await RunAsync(swich, "2020-02", lic));
            Assert.Equal("Normal", S(feb, "phase"));
            Assert.Equal(-0.75m, D(feb, "entitlement"));
            Assert.Equal(-0.75m, D(feb, "carryOut"));
            var licensee = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/licensees/{lic}");
            Assert.True(licensee.GetProperty("barrier").GetProperty("filled").GetBoolean());
            Assert.Equal(0m, D(licensee.GetProperty("barrier"), "remaining"));

            // Mar: 3.00 of fees is split 50/50 (1.50), less the credit → 0.75. Re-opening the barrier would have
            // charged 0.25 at 100 % first (E 1.63, due 0.88) — the licensee paying for the same loss twice.
            await kit.PlantAsync(pb, 6, 0.50m, Noon(2020, 3, 10));
            var mar = OnlyStatement(await RunAsync(swich, "2020-03", lic));
            Assert.Equal("Normal", S(mar, "phase"));
            Assert.True(mar.GetProperty("barrierFilledBefore").GetBoolean());
            Assert.Equal(1.50m, D(mar, "entitlement"));
            Assert.Equal(-0.75m, D(mar, "carryIn"));
            Assert.Equal(0.75m, D(mar, "due"));
            Assert.Equal(0m, D(mar, "barrierRemaining"));
            Assert.Equal(0.75m, D(ParkLine(mar, pb), "swichShare"));

            // The stored March statement re-derives to the same figures (the preview of a settled month is its frozen one).
            var frozen = await op.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/preview?licenseeId={lic}&month=2020-03");
            Assert.False(frozen.GetProperty("isPreview").GetBoolean());
            Assert.Equal(1.50m, D(frozen, "normalEntitlement"));
            Assert.Equal(0.75m, D(frozen, "due"));
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task Statements_of_two_months_computed_at_once_never_count_the_same_fees()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Overlap licensee", "2020-07-01");
            await kit.AssignAsync(swich, pb, lic);
            await kit.PlantAsync(pb, 4, 0.50m, Noon(2020, 7, 12));   // July 2.00
            await kit.PlantAsync(pb, 2, 0.50m, Noon(2020, 8, 10));   // August 1.00

            // August is computed first (it sweeps July's unsettled fees too) and held right where it writes;
            // July is asked for in the same moment. Only one statement may ever own July's cashouts.
            Task<(HttpStatusCode, JsonElement)> august, july;
            await using (var gate = await HoldStatementWritesAsync())
            {
                august = RunRawAsync(swich, "2020-08", lic);
                Assert.True(await gate.UntilWaitingAsync(1, TimeSpan.FromSeconds(20)), "the August run never reached its write");
                july = RunRawAsync(swich, "2020-07", lic);
                Assert.True(await gate.UntilWaitingAsync(2, TimeSpan.FromSeconds(20)), "the July run never reached its write");
                await gate.ReleaseAsync();
            }
            var results = await Task.WhenAll(august, july);
            Assert.Contains(results, r => r.Item1 == HttpStatusCode.OK);
            Assert.All(results, r => Assert.True(r.Item1 is HttpStatusCode.OK or HttpStatusCode.Conflict, r.Item2.ToString()));

            var stored = await _f.DbAsync(async db =>
            {
                var statements = await db.MonthlyStatements.AsNoTracking().Where(m => m.LicenseeId == lic).ToListAsync();
                var stamped = await db.Cashouts.AsNoTracking()
                    .Where(c => c.Settlement != null && c.Settlement.MonthlyStatement != null && c.Settlement.MonthlyStatement.LicenseeId == lic)
                    .Select(c => new { StatementId = c.Settlement!.MonthlyStatementId!.Value, c.Fee })
                    .ToListAsync();
                return (statements, stamped);
            });
            Assert.Equal(3.00m, stored.statements.Sum(m => m.FeeIncome));            // every fee counted exactly once
            foreach (var m in stored.statements)
                Assert.Equal(m.FeeIncome, stored.stamped.Where(c => c.StatementId == m.Id).Sum(c => c.Fee));
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_cost_edited_while_its_statement_is_being_written_is_never_frozen_with_its_old_amount()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Cost race licensee", "2020-09-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await kit.AssignAsync(swich, pb, lic);
            await kit.PlantAsync(pb, 4, 0.50m, Noon(2020, 10, 12));
            var cost = await AddCostAsync(op, lic, "Sms", "SMSOffice", "CR-1", "2020-10-05", 1.50m);
            var costId = cost.GetProperty("id").GetGuid();

            // The statement has read the 1.50 invoice and is about to write; the operator corrects it to 0.40.
            Task<(HttpStatusCode, JsonElement)> run;
            await using (var gate = await HoldStatementWritesAsync())
            {
                run = RunRawAsync(swich, "2020-10", lic);
                Assert.True(await gate.UntilWaitingAsync(1, TimeSpan.FromSeconds(20)), "the run never reached its write");
                var fix = await op.PatchAsJsonAsync($"/api/admin/licensees/{lic}/costs/{costId}", new { amount = 0.40m });
                Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
                await gate.ReleaseAsync();
            }
            var (status, body) = await run;
            Assert.True(status == HttpStatusCode.OK, body.ToString());

            // The 1.50 the statement read is never frozen. The corrected invoice restarted its 48-hour window (the other
            // side has not seen 0.40 yet), so the recomputed statement leaves it for the next one: whatever got frozen
            // agrees with the rows it froze — and with what the money was computed from.
            var (statement, frozenCosts, invoice) = await _f.DbAsync(async db =>
            {
                var m = await db.MonthlyStatements.AsNoTracking().SingleAsync(x => x.LicenseeId == lic);
                var c = await db.CommonCosts.AsNoTracking().Where(x => x.StatementId == m.Id).ToListAsync();
                var i = await db.CommonCosts.AsNoTracking().SingleAsync(x => x.Id == costId);
                return (m, c, i);
            });
            Assert.Empty(frozenCosts);
            Assert.Equal(0m, statement.LicenseeCosts);
            Assert.Equal(2.00m, statement.Due);
            Assert.Equal(0.40m, invoice.Amount);
            Assert.Null(invoice.StatementId);
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_cost_put_on_hold_while_its_statement_is_being_written_is_left_out()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Hold race licensee", "2023-02-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await kit.AssignAsync(swich, pb, lic);
            await kit.PlantAsync(pb, 4, 0.50m, Noon(2023, 2, 12));   // a month no other test plants in
            var cost = await AddCostAsync(op, lic, "Hosting", "Big Cloud", "HR-1", "2023-02-05", 1.50m);
            var costId = cost.GetProperty("id").GetGuid();

            // The statement has read the 1.50 invoice and is about to write; Swich disputes it and holds it.
            Task<(HttpStatusCode, JsonElement)> run;
            await using (var gate = await HoldStatementWritesAsync())
            {
                run = RunRawAsync(swich, "2023-02", lic);
                Assert.True(await gate.UntilWaitingAsync(1, TimeSpan.FromSeconds(20)), "the run never reached its write");
                var hold = await swich.PostAsJsonAsync($"/api/admin/licensees/{lic}/costs/{costId}/hold", new { reason = "No invoice copy" });
                Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
                await gate.ReleaseAsync();
            }
            var (status, body) = await run;
            Assert.True(status == HttpStatusCode.OK, body.ToString());

            // A held invoice is never frozen: the statement computed again without it.
            var (statement, frozenCosts, invoice) = await _f.DbAsync(async db =>
            {
                var m = await db.MonthlyStatements.AsNoTracking().SingleAsync(x => x.LicenseeId == lic);
                var c = await db.CommonCosts.AsNoTracking().Where(x => x.StatementId == m.Id).ToListAsync();
                var i = await db.CommonCosts.AsNoTracking().SingleAsync(x => x.Id == costId);
                return (m, c, i);
            });
            Assert.Empty(frozenCosts);
            Assert.Equal(0m, statement.LicenseeCosts);
            Assert.Equal(2.00m, statement.Due);
            Assert.NotNull(invoice.HeldAt);
            Assert.Null(invoice.StatementId);
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task Two_executors_of_one_statement_never_pay_or_book_a_park_transfer_twice()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var (p5, pb) = (await kit.ParkIdAsync(P5), await kit.ParkIdAsync(PB));
        var month = new DateOnly(2020, 12, 1);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Claim licensee", "2020-12-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await kit.AssignAsync(swich, p5, lic);
            await kit.AssignAsync(swich, pb, lic);
            await kit.PlantAsync(p5, 6, 0.50m, Noon(2020, 12, 10));   // the larger share — executed first
            await kit.PlantAsync(pb, 2, 0.50m, Noon(2020, 12, 12));

            // Settlement day: both parks' accounts read short → both transfers Failed, nothing sent.
            using (var scope = _f.Services.CreateScope())
                await Service(scope, new ScriptedBank { Balance = 0m }).RunMonthAsync(month, "test", lic);
            var rows = await _f.DbAsync(db => db.Settlements.AsNoTracking()
                .Where(s => s.MonthlyStatement!.LicenseeId == lic).ToListAsync());
            Assert.All(rows, r => Assert.Equal(SettlementStatus.Failed, r.Status));
            var p5Row = rows.Single(r => r.ParkId == p5);
            var pbRow = rows.Single(r => r.ParkId == pb);

            // Executor A ("Settle now" again) takes both failed rows; it is held inside the bank call for Tbilisi #5.
            // Meanwhile executor B (the Retry button) completes Batumi. When A moves on to Batumi it must not
            // execute (or book) that transfer a second time.
            var bank = new ScriptedBank { Funded = true };
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bank.OnSend = async req =>
            {
                if (req.IdempotencyKey != p5Row.IdempotencyKey) return;
                held.TrySetResult();
                await release.Task;
            };

            using var scopeA = _f.Services.CreateScope();
            var executorA = Service(scopeA, bank).RunMonthAsync(month, "worker", lic);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(20));

            using (var scopeB = _f.Services.CreateScope())
            {
                var byButton = await Service(scopeB, bank).RetryAsync(pbRow.Id, "button");
                Assert.Equal(SettlementStatus.Completed, byButton.Status);
            }
            release.SetResult();
            await executorA;

            var (after, sent) = await _f.DbAsync(async db => (
                await db.Settlements.AsNoTracking().Where(s => s.Id == p5Row.Id || s.Id == pbRow.Id).ToListAsync(),
                await db.LedgerEntries.AsNoTracking()
                    .Where(l => (l.SettlementId == p5Row.Id || l.SettlementId == pbRow.Id) && l.EntryType == LedgerEntryType.SettlementSent)
                    .ToListAsync()));
            Assert.All(after, r => Assert.Equal(SettlementStatus.Completed, r.Status));
            Assert.Single(sent, l => l.SettlementId == pbRow.Id);
            Assert.Single(sent, l => l.SettlementId == p5Row.Id);
            Assert.Equal(1, bank.Sent.Count(k => k == pbRow.IdempotencyKey));
            Assert.Equal(2, after.Single(r => r.Id == pbRow.Id).AttemptCount);   // the failed one + the button
            var statement = await _f.DbAsync(db => db.MonthlyStatements.AsNoTracking().SingleAsync(m => m.LicenseeId == lic));
            Assert.Equal(MonthlyStatementStatus.Completed, statement.Status);
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task Penalty_days_follow_each_days_cause_survive_a_late_success_and_stay_between_Swich_and_the_licensee()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(P5);
        var kit = new SettlementKit(_f);
        var (p5, pb) = (await kit.ParkIdAsync(P5), await kit.ParkIdAsync(PB));
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Penalty licensee", "2020-04-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await kit.AssignAsync(swich, p5, lic);
            await kit.PlantAsync(p5, 4, 0.50m, Noon(2020, 4, 10));
            await kit.InactivateAccountsAsync(p5);
            var st = OnlyStatement(await RunAsync(swich, "2020-04", lic));
            var rowId = ParkLine(st, p5).GetProperty("settlementId").GetGuid();

            // The history a long-running debt has: the park had no account (park's side) from settlement day,
            // the bank was down 10–12 May (technical — no penalty under §4.1.2), then the park was short again.
            await kit.BackdateLedgerAsync(rowId, LedgerEntryType.SettlementFailed, Noon(2020, 5, 3));
            await kit.AddFailureAsync(rowId, "BANK_EXCEPTION", Noon(2020, 5, 10));
            await kit.AddFailureAsync(rowId, "INSUFFICIENT_PARK_BALANCE", Noon(2020, 5, 13));

            // Paid at last, today.
            await kit.ReactivateAccountsAsync();
            var retry = await swich.PostAsync($"/api/admin/settlements/{rowId}/retry", null);
            Assert.True(retry.StatusCode == HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());

            var today = SettlementService.TodayLocal();
            var due = new DateOnly(2020, 5, 5);
            var total = today.DayNumber - due.DayNumber;
            var parkSideDays = 4 + (today.DayNumber - new DateOnly(2020, 5, 13).DayNumber + 1);   // 6–9 May, 13 May → today
            var statementId = st.GetProperty("statementId").GetGuid();

            foreach (var who in new[] { swich, op })
            {
                var o = ParkLine(await who.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/{statementId}"), p5).GetProperty("overdue");
                Assert.True(o.GetProperty("paid").GetBoolean());
                Assert.Equal(total, o.GetProperty("daysOverdue").GetInt32());
                Assert.Equal(parkSideDays, o.GetProperty("penaltyDays").GetInt32());
                Assert.Equal(total - parkSideDays, o.GetProperty("technicalDays").GetInt32());
                Assert.Equal(Math.Round(2.00m * 0.001m * parkSideDays, 2, MidpointRounding.AwayFromZero), D(o, "accrued"));
            }

            // The park's manager sees that the transfer was late — not a penalty the park does not owe (Annex 3 has none).
            var month = (await manager.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{p5}/settlements/months"))
                .GetProperty("months").EnumerateArray().Single(m => S(m, "month") == "2020-04");
            Assert.Equal(total, month.GetProperty("overdue").GetProperty("daysOverdue").GetInt32());
            Assert.False(HasValue(month.GetProperty("overdue"), "accrued"));
            Assert.False(HasValue(month.GetProperty("overdue"), "penaltyDays"));
            var row = (await manager.GetFromJsonAsync<JsonElement>("/api/admin/settlements?take=365"))
                .GetProperty("settlements").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == rowId);
            Assert.False(HasValue(row.GetProperty("overdue"), "accrued"));

            // A Swich-direct park is outside the licence agreement: late, but no §4.1.2 penalty — even for Swich.
            await kit.AssignAsync(swich, pb, null);
            await kit.PlantAsync(pb, 2, 0.50m, Noon(2020, 5, 14));
            await kit.InactivateAccountsAsync(pb);
            var direct = Assert.Single((await RunAsync(swich, "2020-05", parkId: pb)).GetProperty("direct").EnumerateArray());
            Assert.Equal("Failed", S(direct, "status"));
            Assert.True(direct.GetProperty("overdue").GetProperty("daysOverdue").GetInt32() > 0);
            Assert.False(HasValue(direct.GetProperty("overdue"), "accrued"));
            var view = await swich.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2020-05");
            var mine = view.GetProperty("direct").EnumerateArray().Single(d => d.GetProperty("parkId").GetGuid() == pb);
            Assert.False(HasValue(mine.GetProperty("overdue"), "accrued"));
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_licensee_payment_outside_the_platform_closes_a_failed_transfer_for_good()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(P5);
        var kit = new SettlementKit(_f);
        var p5 = await kit.ParkIdAsync(P5);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "External payment licensee", "2020-11-01");
            await kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await kit.AssignAsync(swich, p5, lic);
            await kit.PlantAsync(p5, 4, 0.50m, Noon(2020, 11, 10));
            await kit.InactivateAccountsAsync(p5);
            var st = OnlyStatement(await RunAsync(swich, "2020-11", lic));
            var statementId = st.GetProperty("statementId").GetGuid();
            var rowId = ParkLine(st, p5).GetProperty("settlementId").GetGuid();
            Assert.Equal("Failed", S(ParkLine(st, p5), "status"));
            await kit.BackdateLedgerAsync(rowId, LedgerEntryType.SettlementFailed, Noon(2020, 12, 3));

            // The red banner opens the month the failed transfer belongs to, not the running one.
            var failed = Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts"))
                .Single(a => S(a, "kind") == "settlement_failed" && a.GetProperty("parkId").GetGuid() == p5);
            Assert.Equal("/admin/settlements?month=2020-11", S(failed, "link"));

            // §2.5: the park's account could not cover it, so the licensee wired the 2.00 to Swich itself.
            var url = $"/api/admin/settlements/{rowId}/settle-externally";
            object Body(decimal amount, string paidOn) => new { payer = "licensee", amount, paidOn, reference = "LIC-2020-11", note = "Paid by the licensee under §2.5" };
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync(url, Body(2.00m, "2020-12-20"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(url, Body(2.00m, "2020-12-20"))).StatusCode);
            var wrongAmount = await swich.PostAsJsonAsync(url, Body(1.99m, "2020-12-20"));
            Assert.Equal(HttpStatusCode.Conflict, wrongAmount.StatusCode);
            Assert.Equal("amount_mismatch", ApiFixture.Code(await wrongAmount.Content.ReadFromJsonAsync<JsonElement>()));
            var future = await swich.PostAsJsonAsync(url, Body(2.00m, SettlementService.TodayLocal().AddDays(1).ToString("yyyy-MM-dd")));
            Assert.Equal("invalid_paid_on", ApiFixture.Code(await future.Content.ReadFromJsonAsync<JsonElement>()));
            var noRef = await swich.PostAsJsonAsync(url, new { payer = "licensee", amount = 2.00m, paidOn = "2020-12-20", reference = " " });
            Assert.Equal("reference_required", ApiFixture.Code(await noRef.Content.ReadFromJsonAsync<JsonElement>()));

            var ok = await swich.PostAsJsonAsync(url, Body(2.00m, "2020-12-20"));
            var closed = await ok.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(ok.StatusCode == HttpStatusCode.OK, closed.ToString());
            Assert.Equal("SettledExternally", S(closed, "status"));
            Assert.Equal("licensee", S(closed.GetProperty("external"), "payer"));
            Assert.Equal("2020-12-20", S(closed.GetProperty("external"), "paidOn"));
            Assert.Equal("LIC-2020-11", S(closed.GetProperty("external"), "reference"));
            Assert.Equal("admin:ops@swich.dev", S(closed.GetProperty("external"), "recordedBy"));
            var again = await swich.PostAsJsonAsync(url, Body(2.00m, "2020-12-20"));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("not_failed", ApiFixture.Code(await again.Content.ReadFromJsonAsync<JsonElement>()));

            // Never collected again: not by the Retry button, not by the nightly retry once the park's account is back.
            Assert.Equal(HttpStatusCode.BadRequest, (await swich.PostAsync($"/api/admin/settlements/{rowId}/retry", null)).StatusCode);
            await kit.ReactivateAccountsAsync();
            var attemptsBefore = (await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId))).AttemptCount;
            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ISettlementService>().RunDueAsync(new DateOnly(2026, 10, 1), "test");
            var row = await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId));
            Assert.Equal(SettlementStatus.SettledExternally, row.Status);
            Assert.Equal(attemptsBefore, row.AttemptCount);

            // The ledger records the payment once — as received outside the platform, not as a park transfer.
            var ledger = await _f.DbAsync(db => db.LedgerEntries.AsNoTracking().Where(l => l.SettlementId == rowId).ToListAsync());
            var entry = Assert.Single(ledger, l => l.EntryType == LedgerEntryType.SettlementPaidExternally);
            Assert.Equal(2.00m, entry.Amount);
            Assert.Equal("LIC-2020-11", entry.Reference);
            Assert.DoesNotContain(ledger, l => l.EntryType == LedgerEntryType.SettlementSent);

            // The statement is settled; the penalty stopped on the payment date (15 days after 5 December, all the park's side).
            foreach (var who in new[] { swich, op })
            {
                var stored = await who.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/{statementId}");
                Assert.Equal("Completed", S(stored, "status"));
                Assert.Equal(0m, D(stored, "transferred"));
                Assert.Equal(2.00m, D(stored, "paidExternally"));
                Assert.Equal(0m, D(stored, "outstanding"));
                var line = ParkLine(stored, p5);
                Assert.Equal("SettledExternally", S(line, "status"));
                Assert.Equal("LIC-2020-11", S(line.GetProperty("external"), "reference"));
                var o = line.GetProperty("overdue");
                Assert.True(o.GetProperty("paid").GetBoolean());
                Assert.Equal(15, o.GetProperty("daysOverdue").GetInt32());
                Assert.Equal(15, o.GetProperty("penaltyDays").GetInt32());
                Assert.Equal(0.03m, D(o, "accrued"));
            }
            var list = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements?licenseeId={lic}");
            var summary = list.GetProperty("statements").EnumerateArray().Single();
            Assert.Equal(0m, D(summary, "outstanding"));
            Assert.Equal(0, summary.GetProperty("failedCount").GetInt32());

            // Nothing left to alert about; the park's own month shows it paid, nothing outstanding.
            Assert.DoesNotContain(Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => S(a, "kind") == "settlement_failed" && a.GetProperty("parkId").GetGuid() == p5);
            var month = (await manager.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{p5}/settlements/months"))
                .GetProperty("months").EnumerateArray().Single(m => S(m, "month") == "2020-11");
            Assert.Equal("SettledExternally", S(month, "status"));
            Assert.Equal(0m, D(month, "outstanding"));
            Assert.Equal(2.00m, D(month, "paidExternally"));

            // The documents render with it.
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/admin/settlements/statements/{statementId}/report.pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/admin/parks/{p5}/settlements/invoice.pdf?month=2020-11")).StatusCode);
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_debt_the_park_paid_by_hand_from_its_account_is_not_an_orphan_in_reconciliation()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Park pays by hand licensee", "2020-09-01");
            await kit.AssignAsync(swich, pb, lic);
            await kit.PlantAsync(pb, 4, 0.50m, Noon(2020, 9, 15));
            await kit.InactivateAccountsAsync(pb);
            var st = OnlyStatement(await RunAsync(swich, "2020-09", lic));
            var line = ParkLine(st, pb);
            Assert.Equal("Failed", S(line, "status"));
            var rowId = line.GetProperty("settlementId").GetGuid();
            var amount = D(line, "swichShare");
            await kit.ReactivateAccountsAsync();

            // The park wires its debt to Swich by hand, from the very account the platform reads (its bank's
            // statement lists the debit), and Swich records it with that payment's bank reference.
            var account = await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking().FirstAsync(a => a.ParkId == pb && a.IsPrimary));
            var bank = _f.Services.GetKeyedService<IBankPayoutAdapter>(account.Provider) ?? _f.Services.GetRequiredKeyedService<IBankPayoutAdapter>("MOCK");
            var manual = await bank.SendPayoutAsync(new BankTransferRequest(
                $"manual:{Guid.NewGuid():N}",
                new BankAccountContext(pb, account.Id, account.Provider, account.BankCode, account.Iban, account.HolderName ?? "Batumi", account.CredentialsEncrypted),
                "GE12TB7100000000000001", "შპს სვიჩსოლუშენსი", amount, "GEL", "PayTaxi PT-2020-09, paid by hand"));
            Assert.True(manual.Success, manual.ErrorMessage);
            var ok = await swich.PostAsJsonAsync($"/api/admin/settlements/{rowId}/settle-externally",
                new { payer = "park", amount, paidOn = SettlementService.TodayLocal().ToString("yyyy-MM-dd"), reference = manual.TransferId });
            Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

            // Reconciliation reads every debit of the account: this one is the recorded payment, not an orphan.
            var run = await swich.PostAsync($"/api/admin/parks/{pb}/reconciliation/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var discrepancies = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{pb}/reconciliation/discrepancies?take=200");
            Assert.DoesNotContain(discrepancies.GetProperty("discrepancies").EnumerateArray(),
                d => S(d, "kind") == "orphaned_bank_send" && d.GetProperty("bankTransferId").GetString() == manual.TransferId);
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_recorded_outside_payment_hides_only_the_bank_debit_that_matches_it_in_reference_amount_account_and_date()
    {
        // The reference Swich types for an outside payment is free text. Only the debit that IS that payment — the same
        // id, the same amount to the tetri, from the account the debt was owed from, around the day it reached Swich —
        // may leave the orphan list; a mistyped reference must never hide a transfer the platform does not track.
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        var accounts = await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == pb && a.IsActive).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.CreatedAt).ToListAsync());
        Assert.True(accounts.Count >= 2, "Batumi needs its TBC and BoG accounts");
        var (main, other) = (accounts[0], accounts[1]);
        var bank = _f.Services.GetRequiredKeyedService<IBankPayoutAdapter>("MOCK");
        var today = SettlementService.TodayLocal();
        var planted = new List<Guid>();
        var debits = new List<string>();

        async Task<string> DebitAsync(ParkBankAccount from, decimal amount)
        {
            var r = await bank.SendPayoutAsync(new BankTransferRequest($"manual:{Guid.NewGuid():N}",
                new BankAccountContext(pb, from.Id, from.Provider, from.BankCode, from.Iban, from.HolderName ?? "Batumi", from.CredentialsEncrypted),
                "GE12TB7100000000000001", "შპს სვიჩსოლუშენსი", amount, "GEL", "PayTaxi debt paid by hand"));
            Assert.True(r.Success, r.ErrorMessage);
            debits.Add(r.TransferId!);
            return r.TransferId!;
        }
        Task RecordedAsync(decimal share, Guid? account, string reference, DateOnly paidOn) => _f.DbAsync(async db =>
        {
            var row = new Settlement
            {
                ParkId = pb, IsMonthly = false, SettlementDate = new DateOnly(2024, 3, 31),
                PeriodFromUtc = new DateTime(2024, 2, 29, 20, 0, 0, DateTimeKind.Utc), PeriodToUtc = new DateTime(2024, 3, 31, 20, 0, 0, DateTimeKind.Utc),
                CashoutCount = 1, FeeTotal = share * 2, SwichShare = share, ParkShare = share, ParkBankAccountId = account,
                Status = SettlementStatus.SettledExternally, ExternalPayer = "park", ExternalPaidOn = paidOn, ExternalReference = reference,
                ExternalRecordedBy = "admin:test", ExternalRecordedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
                IdempotencyKey = "settle:ext-match:" + Guid.NewGuid().ToString("N"),
                Description = "PayTaxi settlement 2024-03 (test)", InvoiceRef = "PT-2024-03",
            };
            db.Settlements.Add(row);
            planted.Add(row.Id);
            return await db.SaveChangesAsync();
        });

        try
        {
            var paid = await DebitAsync(main, 3.10m);
            await RecordedAsync(3.10m, main.Id, paid, today);                       // the payment itself
            var anyAccount = await DebitAsync(other, 0.90m);
            await RecordedAsync(0.90m, null, anyAccount, today);                    // a debt that never reached an account: any of the park's
            var wrongAmount = await DebitAsync(main, 5.00m);
            await RecordedAsync(4.99m, main.Id, wrongAmount, today);                // the reference names a debit of another amount
            var wrongAccount = await DebitAsync(other, 2.20m);
            await RecordedAsync(2.20m, main.Id, wrongAccount, today);               // ... a debit of the park's other account
            var wrongDay = await DebitAsync(main, 1.30m);
            await RecordedAsync(1.30m, main.Id, wrongDay, today.AddDays(-20));      // ... a debit weeks away from the payment day

            var run = await swich.PostAsync($"/api/admin/parks/{pb}/reconciliation/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var orphans = (await swich.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{pb}/reconciliation/discrepancies?take=200"))
                .GetProperty("discrepancies").EnumerateArray()
                .Where(d => S(d, "kind") == "orphaned_bank_send")
                .Select(d => S(d, "bankTransferId"))
                .ToHashSet();
            Assert.DoesNotContain(paid, orphans);
            Assert.DoesNotContain(anyAccount, orphans);
            Assert.Contains(wrongAmount, orphans);
            Assert.Contains(wrongAccount, orphans);
            Assert.Contains(wrongDay, orphans);
        }
        finally
        {
            await _f.DbAsync(db => db.Settlements.Where(x => planted.Contains(x.Id)).ExecuteDeleteAsync());
            await _f.DbAsync(db => db.ReconciliationDiscrepancies.Where(d => d.BankTransferId != null && debits.Contains(d.BankTransferId)).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task A_park_manager_never_sees_the_licensees_barrier_split_and_a_direct_parks_debt_is_paid_outside_only_by_the_park()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        var (p5, pb) = (await kit.ParkIdAsync(P5), await kit.ParkIdAsync(PB));
        var p5Manager = await _f.ParkAdminAsync(P5);
        var pbManager = await _f.ParkAdminAsync(PB);
        static JsonElement RowOf(JsonElement list, Guid id) =>
            list.GetProperty("settlements").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);
        try
        {
            // A licensee park's barrier/rest split is the statement's barrier fraction — the licensee's phase, which is
            // between the licensee and Swich. The park sees its own fees and its part of the due amount; Swich and the
            // operator see the split (§2.6: identical data for both sides).
            var lic = await kit.CreateLicenseeAsync(swich, "Phase privacy licensee", "2021-06-01");
            await kit.AssignAsync(swich, p5, lic);
            await kit.PlantAsync(p5, 4, 0.50m, Noon(2021, 6, 10));
            var licRow = ParkLine(OnlyStatement(await RunAsync(swich, "2021-06", lic)), p5).GetProperty("settlementId").GetGuid();

            var mine = RowOf(await p5Manager.GetFromJsonAsync<JsonElement>("/api/admin/settlements?take=365"), licRow);
            Assert.False(HasValue(mine, "phase1Fees"));
            Assert.False(HasValue(mine, "phase2Fees"));
            Assert.Equal(2.00m, D(mine, "feeTotal"));
            Assert.True(HasValue(mine, "swichShare"));
            await kit.InForceAsync(lic);   // the operator reaches the licensee's parks while it is in force
            foreach (var who in new[] { swich, op })
            {
                var full = RowOf(await who.GetFromJsonAsync<JsonElement>($"/api/admin/settlements?parkId={p5}&take=365"), licRow);
                Assert.Equal(2.00m, D(full, "phase1Fees") + D(full, "phase2Fees"));
            }

            // A Swich-direct park is outside the licence agreement: its split is its own contract with Swich (shown to it),
            // and §2.5 — the licensee answering for a park — does not apply: only the park can have paid its debt.
            await kit.AssignAsync(swich, pb, null);
            await kit.PlantAsync(pb, 2, 0.50m, Noon(2021, 7, 10));
            await kit.InactivateAccountsAsync(pb);
            var direct = Assert.Single((await RunAsync(swich, "2021-07", parkId: pb)).GetProperty("direct").EnumerateArray());
            Assert.Equal("Failed", S(direct, "status"));
            var directRow = direct.GetProperty("id").GetGuid();
            var share = D(direct, "swichShare");
            Assert.True(HasValue(RowOf(await pbManager.GetFromJsonAsync<JsonElement>("/api/admin/settlements?take=365"), directRow), "phase1Fees"));

            var url = $"/api/admin/settlements/{directRow}/settle-externally";
            var byLicensee = await swich.PostAsJsonAsync(url, new { payer = "licensee", amount = share, paidOn = "2021-08-02", reference = "LIC-2021-07" });
            Assert.Equal(HttpStatusCode.BadRequest, byLicensee.StatusCode);
            Assert.Equal("invalid_payer", ApiFixture.Code(await byLicensee.Content.ReadFromJsonAsync<JsonElement>()));
            var byPark = await swich.PostAsJsonAsync(url, new { payer = "park", amount = share, paidOn = "2021-08-02", reference = "PARK-2021-07" });
            Assert.True(byPark.StatusCode == HttpStatusCode.OK, await byPark.Content.ReadAsStringAsync());
            Assert.Equal("park", S((await byPark.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("external"), "payer"));
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_licensee_created_with_zero_barrier_zero_share_or_inactive_is_stored_as_given()
    {
        // Both licensees are registered inactive and stay that way: a licensee cannot be deleted once the agreement's
        // (append-only) history has an entry for it — its registration terms are the first.
        var swich = await _f.SuperAdminAsync();
        var res = await swich.PostAsJsonAsync("/api/admin/licensees",
            new { name = "No-barrier licensee", barrierGel = 0m, swichSharePercent = 0m, isActive = false });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == HttpStatusCode.OK, body.ToString());
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(0m, D(body, "barrierGel"));
        Assert.Equal(0m, D(body, "swichSharePercent"));
        Assert.False(body.GetProperty("isActive").GetBoolean());

        var stored = await _f.DbAsync(db => db.Licensees.AsNoTracking().FirstAsync(l => l.Id == id));
        Assert.Equal((0m, 0m, false), (stored.BarrierGel, stored.SwichSharePercent, stored.IsActive));

        // Omitted, the agreement's values apply.
        var defaults = await swich.PostAsJsonAsync("/api/admin/licensees", new { name = "Default licensee", isActive = false });
        var d = await defaults.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20_000m, D(d, "barrierGel"));
        Assert.Equal(50m, D(d, "swichSharePercent"));
    }

    [Fact]
    public async Task A_new_park_joins_the_only_active_licensee_unless_Swich_chooses_otherwise()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var created = new List<Guid>();
        // Make "exactly one active licensee" true for the duration (a test elsewhere may have left one active).
        var seeded = await _f.DbAsync(db => db.Licensees.AsNoTracking().OrderBy(l => l.CreatedAt).Select(l => l.Id).FirstAsync());
        var othersActive = await _f.DbAsync(db => db.Licensees.Where(l => l.IsActive && l.Id != seeded).Select(l => l.Id).ToListAsync());
        var seededWasActive = await _f.DbAsync(db => db.Licensees.AsNoTracking().Where(l => l.Id == seeded).Select(l => l.IsActive).FirstAsync());
        await _f.DbAsync(db => db.Licensees.Where(l => othersActive.Contains(l.Id) || l.Id == seeded)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, l => l.Id == seeded)));

        async Task<(HttpStatusCode Status, JsonElement Body)> CreateAsync(string tail, object? extra = null)
        {
            var body = new Dictionary<string, object?>
            {
                ["name"] = $"Default park {tail}", ["yandexParkId"] = $"yx_default_{tail}",
                ["bankAccountIban"] = ApiFixture.TbcIban("70000000000" + tail),
                ["legalEntityName"] = "შპს ნაგულისხმევი პარკი", ["taxId"] = "405999888", ["phone"] = "+995 322 99 88 77",
            };
            if (extra is not null)
                foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
            var res = await swich.PostAsJsonAsync("/api/admin/parks", body);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (res.IsSuccessStatusCode) created.Add(json.GetProperty("id").GetGuid());
            return (res.StatusCode, json);
        }
        Task<Guid?> LicenseeOfAsync(JsonElement park) => _f.DbAsync(db =>
            db.Parks.AsNoTracking().Where(p => p.Id == park.GetProperty("id").GetGuid()).Select(p => p.LicenseeId).FirstAsync());

        try
        {
            var tail = Random.Shared.Next(1_000, 9_999).ToString();   // + one distinct digit per park

            // One licensee in force → the new park is settled under the agreement, and the response says so.
            var (s1, p1) = await CreateAsync(tail + "0");
            Assert.Equal(HttpStatusCode.Created, s1);
            Assert.Equal(seeded, await LicenseeOfAsync(p1));
            Assert.Equal(seeded, p1.GetProperty("licenseeId").GetGuid());

            // Swich-direct only when Swich says so.
            var (s2, p2) = await CreateAsync(tail + "1", new { direct = true });
            Assert.Equal(HttpStatusCode.Created, s2);
            Assert.Null(await LicenseeOfAsync(p2));
            Assert.Equal(JsonValueKind.Null, p2.GetProperty("licenseeId").ValueKind);

            // A second licensee cannot be in force (second_active_licensee — operators are not scoped by park yet), so a
            // registered but inactive one never captures new parks: the one in force still does, and the other only when
            // Swich names it.
            var second = await kit.CreateLicenseeAsync(swich, "Second licensee", "2020-01-01");
            var (s3, p3) = await CreateAsync(tail + "2");
            Assert.Equal(HttpStatusCode.Created, s3);
            Assert.Equal(seeded, await LicenseeOfAsync(p3));
            var (s4, p4) = await CreateAsync(tail + "3", new { licenseeId = second });
            Assert.Equal(HttpStatusCode.Created, s4);
            Assert.Equal(second, await LicenseeOfAsync(p4));
            var (s5, p5) = await CreateAsync(tail + "4", new { licenseeId = Guid.NewGuid() });
            Assert.Equal("licensee_not_found", ApiFixture.Code(p5));
            Assert.NotEqual(HttpStatusCode.Created, s5);
        }
        finally
        {
            await kit.ReturnAsync();
            await _f.DbAsync(db => db.Parks.Where(p => created.Contains(p.Id)).ExecuteDeleteAsync());
            await _f.DbAsync(db => db.Licensees.Where(l => othersActive.Contains(l.Id) || l.Id == seeded)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, l => l.Id == seeded ? seededWasActive : true)));
        }
    }

    [Fact]
    public async Task The_monthly_tick_settles_the_previous_month_from_the_run_day_on_and_skips_what_it_cannot()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Tick licensee", "2020-06-01");
            var unlaunched = await kit.CreateLicenseeAsync(swich, "Unlaunched licensee", launch: null);
            await kit.AssignAsync(swich, pb, lic);
            await kit.PlantAsync(pb, 4, 0.50m, Noon(2020, 6, 14));

            async Task<SettlementRunSummary> TickAsync(int day)
            {
                using var scope = _f.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ISettlementService>()
                    .RunDueAsync(new DateOnly(2020, 7, day), "worker");
            }

            // The day before the run day: retries only, no month.
            var early = await TickAsync(2);
            Assert.Null(early.MonthSettled);
            Assert.False(await _f.DbAsync(db => db.MonthlyStatements.AnyAsync(m => m.LicenseeId == lic)));

            // The run day: June is settled for every licensee that can be — the unlaunched one is skipped
            // without stopping the others.
            var due = await TickAsync(3);
            Assert.Equal(new DateOnly(2020, 6, 1), due.MonthSettled);
            Assert.True(due.StatementsCreated >= 1);
            Assert.True(due.Skipped >= 1);
            var statement = await _f.DbAsync(db => db.MonthlyStatements.AsNoTracking().SingleAsync(m => m.LicenseeId == lic));
            Assert.Equal(new DateOnly(2020, 6, 1), statement.Month);
            Assert.Equal(2.00m, statement.FeeIncome);
            Assert.Equal(MonthlyStatementStatus.Completed, statement.Status);
            Assert.Equal("worker", statement.InitiatedBy);
            Assert.False(await _f.DbAsync(db => db.MonthlyStatements.AnyAsync(m => m.LicenseeId == unlaunched)));

            // Later nights of the month: the settled month is left alone — nothing new, nothing re-sent.
            var sentBefore = await _f.DbAsync(db => db.LedgerEntries.CountAsync(l => l.EntryType == LedgerEntryType.SettlementSent
                                                                                 && l.Settlement!.MonthlyStatementId == statement.Id));
            var next = await TickAsync(4);
            Assert.Equal(new DateOnly(2020, 6, 1), next.MonthSettled);
            Assert.Equal(0, next.StatementsCreated);
            Assert.Equal(0, next.DirectSettlementsCreated);
            Assert.Equal(1, await _f.DbAsync(db => db.MonthlyStatements.CountAsync(m => m.LicenseeId == lic)));
            Assert.Equal(sentBefore, await _f.DbAsync(db => db.LedgerEntries.CountAsync(l => l.EntryType == LedgerEntryType.SettlementSent
                                                                                           && l.Settlement!.MonthlyStatementId == statement.Id)));
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task The_development_seed_launches_the_licensee_at_the_start_of_the_current_month()
    {
        // A back-dated launch made the first start of an existing dev database freeze a meaningless statement for
        // the previous month (its fees long since settled nightly) and use up the sample invoices there.
        var (launch, costDates) = await _f.DbAsync(async db =>
        {
            var seeded = await db.Licensees.AsNoTracking().OrderBy(l => l.CreatedAt).FirstAsync();
            var dates = await db.CommonCosts.AsNoTracking().Where(c => c.LicenseeId == seeded.Id && c.CreatedBy == "seed")
                .Select(c => c.InvoiceDate).ToListAsync();
            return (seeded.LaunchDate, dates);
        });
        var today = SettlementService.TodayLocal();
        Assert.Equal(SettlementService.FirstOfMonth(today), launch);
        Assert.NotEmpty(costDates);
        Assert.All(costDates, d => Assert.True(d >= launch && d <= today, $"{d} is outside {launch}..{today}"));
    }
}
