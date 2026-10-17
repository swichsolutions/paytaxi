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

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Monthly money under the licence agreement, through the API against Postgres: the licensee's
/// statement (fees of all its parks, shared costs of both sides, barrier, carry), the pro-rata park
/// transfers, idempotency, late cashouts/costs rolling forward, Swich-direct parks, failures and
/// retries, the role matrix, the documents — and reconciliation treating the settlement transfer as
/// an expected outflow.
///
/// Isolation: every test settles its OWN licensee in its own past months (planted, back-dated
/// cashouts), borrowing seeded parks for the duration and handing them back afterwards. Settled money
/// rows stay (the ledger is append-only); the test licensee is deactivated so the seeded one remains
/// the console's first.
/// </summary>
[Collection("api")]
public class SettlementAndReconciliationTests
{
    private const string P3 = "tbilisi-auto-park-3", P5 = "tbilisi-auto-park-5", PB = "batumi-auto-park-1";

    private readonly ApiFixture _f;
    public SettlementAndReconciliationTests(ApiFixture f) => _f = f;

    // ── Kit ──────────────────────────────────────────────────────────

    private sealed class Borrowed
    {
        public Dictionary<Guid, Guid?> Assignments { get; } = new();
        public Dictionary<Guid, (decimal Share, decimal? P1, decimal? Cap)> Splits { get; } = new();
        public List<Guid> InactivatedAccounts { get; } = new();
        public List<Guid> Licensees { get; } = new();
        /// <summary>Registers the test licensees (inactive, operator bound to them) and hands the operator back.</summary>
        public SettlementKit? Kit { get; set; }
    }

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == slug)).Id);

    /// <summary>Tbilisi local noon of a day, in UTC (Georgia is UTC+4).</summary>
    private static DateTime Noon(int y, int m, int d) => new(y, m, d, 8, 0, 0, DateTimeKind.Utc);

    private static decimal D(JsonElement e, string prop) => e.GetProperty(prop).GetDecimal();
    private static string S(JsonElement e, string prop) => e.GetProperty(prop).GetString() ?? "";

    /// <summary>
    /// A test licensee; its launch date goes straight to the database — through the API a launch date can only be set
    /// before it passes, and these tests settle months long gone.
    /// </summary>
    private async Task<Guid> CreateLicenseeAsync(HttpClient swich, Borrowed b, string name, string? launch, decimal barrier = 20_000m, decimal share = 50m)
    {
        // Through the kit: registered inactive (a second licensee in force is refused) with the operator bound to it.
        b.Kit ??= new SettlementKit(_f);
        var id = await b.Kit.CreateLicenseeAsync(swich, name, launch, barrier, share);
        b.Licensees.Add(id);
        return id;
    }

    private async Task AssignAsync(HttpClient swich, Borrowed b, Guid parkId, Guid? licenseeId)
    {
        if (!b.Assignments.ContainsKey(parkId))
            b.Assignments[parkId] = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId)).LicenseeId);
        var res = await swich.PutAsJsonAsync($"/api/admin/parks/{parkId}/licensee", new { licenseeId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private Task ReturnAsync(Borrowed b) => _f.DbAsync(async db =>
    {
        foreach (var (parkId, licenseeId) in b.Assignments)
            (await db.Parks.FirstAsync(p => p.Id == parkId)).LicenseeId = licenseeId;
        foreach (var (parkId, split) in b.Splits)
        {
            var p = await db.Parks.FirstAsync(x => x.Id == parkId);
            p.SwichSharePercent = split.Share; p.Phase1SharePercent = split.P1; p.Phase1CapGel = split.Cap;
        }
        foreach (var id in b.InactivatedAccounts)
            (await db.ParkBankAccounts.FirstAsync(a => a.Id == id)).IsActive = true;
        foreach (var id in b.Licensees)
            (await db.Licensees.FirstAsync(l => l.Id == id)).IsActive = false;
        var saved = await db.SaveChangesAsync();
        if (b.Kit is not null) await b.Kit.ReturnAsync();
        return saved;
    });

    /// <summary>Completed cashouts as the saga leaves them, back-dated into a test month.</summary>
    private Task<List<Guid>> PlantAsync(Guid parkId, int count, decimal fee, DateTime completedAtUtc) => _f.DbAsync(async db =>
    {
        var d = await db.Drivers.AsNoTracking().Where(x => x.ParkId == parkId).OrderBy(x => x.YandexDriverProfileId).FirstAsync();
        var card = await db.BankCards.AsNoTracking().Where(x => x.DriverId == d.Id).OrderByDescending(x => x.IsDefault).FirstAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var at = completedAtUtc.AddMinutes(i);
            var c = new Cashout
            {
                DriverId = d.Id, ParkId = parkId, BankCardId = card.Id, Amount = 10m + fee, Fee = fee,
                Status = CashoutStatus.Completed, CompletedAt = at, CreatedAt = at.AddMinutes(-1), UpdatedAt = at,
                IdempotencyKey = $"planted:{Guid.NewGuid():N}", InitiatedBy = "test", AttemptCount = 1,
            };
            db.Cashouts.Add(c);
            ids.Add(c.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    });

    /// <summary>A shared cost entered three days ago — past the 48-hour window, so the next statement counts it.</summary>
    private async Task<JsonElement> AddCostAsync(HttpClient c, Guid licenseeId, string category, string vendor, string number,
        string date, decimal amount, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var res = await c.PostAsJsonAsync($"/api/admin/licensees/{licenseeId}/costs",
            new { category, vendor, invoiceNumber = number, invoiceDate = date, amount });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == expect, $"cost {number}: {(int)res.StatusCode} {body}");
        if (res.StatusCode == HttpStatusCode.OK)
            await new SettlementKit(_f).EnteredHoursAgoAsync(body.GetProperty("id").GetGuid(), 72);
        return body;
    }

    private static async Task<JsonElement> RunAsync(HttpClient c, string month, Guid? licenseeId = null, Guid? parkId = null,
        HttpStatusCode expect = HttpStatusCode.OK)
    {
        var url = $"/api/admin/settlements/run?month={month}"
                  + (licenseeId is { } l ? $"&licenseeId={l}" : "")
                  + (parkId is { } p ? $"&parkId={p}" : "");
        var res = await c.PostAsync(url, null);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == expect, $"{url} → {(int)res.StatusCode}: {body}");
        return body;
    }

    private static JsonElement OnlyStatement(JsonElement run) => run.GetProperty("statements").EnumerateArray().Single();

    private static JsonElement ParkLine(JsonElement statement, Guid parkId) =>
        statement.GetProperty("parks").EnumerateArray().Single(p => p.GetProperty("parkId").GetGuid() == parkId);

    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Licensee_statement_adds_up_its_parks_and_both_sides_costs_and_collects_pro_rata_once()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var (p3, pb) = (await ParkIdAsync(P3), await ParkIdAsync(PB));
        var b = new Borrowed();
        try
        {
            var lic = await CreateLicenseeAsync(swich, b, "Test licensee A", "2021-01-01");
            await b.Kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await AssignAsync(swich, b, p3, lic);
            await AssignAsync(swich, b, pb, lic);

            // March 2021: 7 × 0.50 at Tbilisi #3, 4 × 0.50 at Batumi → F = 5.50.
            var plantedMarch = (await PlantAsync(p3, 7, 0.50m, Noon(2021, 3, 10))).Concat(await PlantAsync(pb, 4, 0.50m, Noon(2021, 3, 15))).ToList();
            // Each side enters the invoices addressed to it; who adds decides who paid.
            var cl = await AddCostAsync(op, lic, "Sms", "SMSOffice", "T-A-1", "2021-03-31", 1.20m);
            Assert.Equal("Licensee", S(cl, "paidBy"));
            var cs = await AddCostAsync(swich, lic, "Hosting", "Hetzner", "T-A-2", "2021-03-05", 2.00m);
            Assert.Equal("Swich", S(cs, "paidBy"));
            await AddCostAsync(swich, lic, "Domain", "Caucasus Online", "T-A-3", "2021-04-02", 2.50m); // belongs to April

            // Preview: computed live, nothing written.
            var preview = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/preview?licenseeId={lic}&month=2021-03");
            Assert.True(preview.GetProperty("isPreview").GetBoolean());
            Assert.Equal(5.50m, D(preview, "feeIncome"));
            Assert.Equal(4.30m, D(preview, "due"));
            Assert.Equal(0, await _f.DbAsync(db => db.MonthlyStatements.CountAsync(m => m.LicenseeId == lic)));

            // Settle: barrier phase → Swich is due F − CL = 5.50 − 1.20 = 4.30 (its own 2.00 does not reduce it),
            // taken pro rata to fees: 4.30 × 3.50/5.50 = 2.74 and 4.30 × 2.00/5.50 = 1.56.
            var st = OnlyStatement(await RunAsync(swich, "2021-03", lic));
            Assert.False(st.GetProperty("isPreview").GetBoolean());
            Assert.Equal("PT-2021-03", S(st, "invoiceRef"));
            Assert.Equal(11, st.GetProperty("cashoutCount").GetInt32());
            Assert.Equal(5.50m, D(st, "feeIncome"));
            Assert.Equal(1.20m, D(st, "licenseeCosts"));
            Assert.Equal(2.00m, D(st, "swichCosts"));
            Assert.Equal(0m, D(st, "carryIn"));
            Assert.Equal(0m, D(st, "barrierBefore"));
            Assert.Equal("Barrier", S(st, "phase"));
            Assert.Equal(4.30m, D(st, "entitlement"));
            Assert.Equal(4.30m, D(st, "due"));
            Assert.Equal(0m, D(st, "carryOut"));
            Assert.Equal("Completed", S(st, "status"));
            Assert.Equal(4.30m, D(st, "transferred"));
            Assert.Equal(0m, D(st, "outstanding"));
            Assert.Equal(2, st.GetProperty("costs").GetArrayLength());

            var line3 = ParkLine(st, p3);
            var lineB = ParkLine(st, pb);
            Assert.Equal(3.50m, D(line3, "fees"));
            Assert.Equal(2.74m, D(line3, "swichShare"));
            Assert.Equal(2.00m, D(lineB, "fees"));
            Assert.Equal(1.56m, D(lineB, "swichShare"));
            Assert.Equal("Completed", S(line3, "status"));
            Assert.Equal("Completed", S(lineB, "status"));
            Assert.False(string.IsNullOrEmpty(S(line3, "bankTransferId")));
            Assert.NotEqual(S(line3, "bankTransferId"), S(lineB, "bankTransferId"));    // one transfer per park

            var statementId = st.GetProperty("statementId").GetGuid();
            var (description, stampedCashouts, stampedCosts, sentEntries) = await _f.DbAsync(async db => (
                await db.Settlements.Where(s => s.MonthlyStatementId == statementId && s.ParkId == p3).Select(s => s.Description).SingleAsync(),
                await db.Cashouts.CountAsync(c => plantedMarch.Contains(c.Id) && c.Settlement!.MonthlyStatementId == statementId),
                await db.CommonCosts.CountAsync(c => c.StatementId == statementId),
                await db.LedgerEntries.CountAsync(l => l.EntryType == LedgerEntryType.SettlementSent && l.Settlement!.MonthlyStatementId == statementId)));
            Assert.Equal("PayTaxi settlement 2021-03, 7 tx, inv ref PT-2021-03", description);
            Assert.Equal(11, stampedCashouts);
            Assert.Equal(2, stampedCosts);
            Assert.Equal(2, sentEntries);

            // Re-running is idempotent: same statement, no new rows, no second transfer.
            var again = OnlyStatement(await RunAsync(swich, "2021-03", lic));
            Assert.Equal(statementId, again.GetProperty("statementId").GetGuid());
            Assert.Equal(2, await _f.DbAsync(db => db.Settlements.CountAsync(s => s.MonthlyStatementId == statementId)));
            Assert.Equal(2, await _f.DbAsync(db => db.LedgerEntries.CountAsync(l => l.EntryType == LedgerEntryType.SettlementSent && l.Settlement!.MonthlyStatementId == statementId)));

            // An included cost is settled money: no edit, no delete — by anyone.
            var clId = cl.GetProperty("id").GetGuid();
            var patch = await op.PatchAsJsonAsync($"/api/admin/licensees/{lic}/costs/{clId}", new { amount = 9.99m });
            Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
            Assert.Equal("cost_included", ApiFixture.Code(await patch.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(HttpStatusCode.Conflict, (await swich.DeleteAsync($"/api/admin/licensees/{lic}/costs/{cs.GetProperty("id").GetGuid()}")).StatusCode);

            // A cashout that completed in March but was missed, and a March invoice entered late, both roll into April.
            await PlantAsync(p3, 1, 0.50m, Noon(2021, 3, 28));
            await AddCostAsync(op, lic, "SupportComms", "Magti", "T-A-4", "2021-03-20", 0.30m);
            // The settled March view still shows that late invoice — as going into the next statement.
            var marchView = await op.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/preview?licenseeId={lic}&month=2021-03");
            var late = Assert.Single(marchView.GetProperty("lateCosts").EnumerateArray());
            Assert.Equal("T-A-4", S(late, "invoiceNumber"));
            Assert.True(late.GetProperty("canEdit").GetBoolean());
            Assert.Equal(2, marchView.GetProperty("costs").GetArrayLength());
            await PlantAsync(p3, 2, 0.50m, Noon(2021, 4, 12));
            var april = OnlyStatement(await RunAsync(swich, "2021-04", lic));
            Assert.Equal(3, april.GetProperty("cashoutCount").GetInt32());
            Assert.Equal(1.50m, D(april, "feeIncome"));
            Assert.Equal(0.30m, D(april, "licenseeCosts"));
            Assert.Equal(2.50m, D(april, "swichCosts"));
            Assert.Equal(4.30m, D(april, "barrierBefore"));          // R = what March entitled Swich to
            Assert.Equal(1.20m, D(april, "entitlement"));
            Assert.Equal(1.20m, D(april, "due"));
            var aprilLine = Assert.Single(april.GetProperty("parks").EnumerateArray());  // Batumi had no fees → pays nothing
            Assert.Equal(p3, aprilLine.GetProperty("parkId").GetGuid());
            Assert.Equal(1.20m, D(aprilLine, "swichShare"));

            // March is closed for good: an earlier month can no longer be settled, and the launch date — long passed —
            // is locked (it could not move past the settled month either way).
            var feb = await RunAsync(swich, "2021-02", lic, expect: HttpStatusCode.Conflict);
            Assert.Equal("later_statement_exists", ApiFixture.Code(feb));
            var moved = await swich.PatchAsJsonAsync($"/api/admin/licensees/{lic}", new { launchDate = "2021-04-01", reason = "Move it" });
            Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
            Assert.Equal("launch_date_locked", ApiFixture.Code(await moved.Content.ReadFromJsonAsync<JsonElement>()));
            // A stored month's "preview" is its frozen statement.
            var frozen = await op.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/preview?licenseeId={lic}&month=2021-03");
            Assert.False(frozen.GetProperty("isPreview").GetBoolean());
            Assert.Equal(4.30m, D(frozen, "due"));

            // The park's own view: its month row and its monthly invoice; never the licensee's statement.
            var months = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{p3}/settlements/months");
            var march = months.GetProperty("months").EnumerateArray().Single(m => S(m, "month") == "2021-03");
            Assert.Equal(2.74m, D(march, "swichShare"));
            Assert.Equal("Completed", S(march, "status"));
            Assert.True(march.GetProperty("viaLicensee").GetBoolean());

            var pdf = await swich.GetAsync($"/api/admin/parks/{p3}/settlements/invoice.pdf?month=2021-03");
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            Assert.Contains("PT-2021-03-tbilisi-auto-park-3.pdf", pdf.Content.Headers.ContentDisposition!.ToString());
            var pdfBytes = await pdf.Content.ReadAsByteArrayAsync();
            Assert.True(pdfBytes.Length > 5_000, $"invoice too small: {pdfBytes.Length}");
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 4));

            var report = await op.GetAsync($"/api/admin/settlements/statements/{statementId}/report.pdf");
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
            Assert.Contains("PT-2021-03-report.pdf", report.Content.Headers.ContentDisposition!.ToString());
            var reportBytes = await report.Content.ReadAsByteArrayAsync();
            Assert.True(reportBytes.Length > 5_000, $"report too small: {reportBytes.Length}");
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(reportBytes, 0, 4));

            var own = await _f.ParkAdminAsync(P3);
            Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/admin/parks/{p3}/settlements/months")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/admin/parks/{p3}/settlements/invoice.pdf?month=2021-03")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await own.GetAsync($"/api/admin/settlements/statements/{statementId}/report.pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await own.GetAsync($"/api/admin/settlements/statements/{statementId}")).StatusCode);
            var ownRows = await own.GetFromJsonAsync<JsonElement>("/api/admin/settlements?take=365");
            Assert.All(ownRows.GetProperty("settlements").EnumerateArray(), r => Assert.Equal(p3, r.GetProperty("parkId").GetGuid()));
            var other = await _f.ParkAdminAsync(PB);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/admin/parks/{p3}/settlements/months")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/admin/parks/{p3}/settlements/invoice.pdf?month=2021-03")).StatusCode);

            // Garbage / empty months on the invoice.
            Assert.Equal(HttpStatusCode.BadRequest, (await swich.GetAsync($"/api/admin/parks/{p3}/settlements/invoice.pdf?month=march")).StatusCode);
            var none = await swich.GetAsync($"/api/admin/parks/{p3}/settlements/invoice.pdf?month=2019-01");
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
            Assert.Equal("no_settlements_in_month", ApiFixture.Code(await none.Content.ReadFromJsonAsync<JsonElement>()));
        }
        finally { await ReturnAsync(b); }
    }

    [Fact]
    public async Task A_credit_carries_forward_the_barrier_fills_mid_month_and_then_the_split_is_normal()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var pb = await ParkIdAsync(PB);
        var b = new Borrowed();
        try
        {
            // A 4 GEL barrier makes every phase reachable in a few months of 0.50 fees.
            var lic = await CreateLicenseeAsync(swich, b, "Test licensee B", "2022-01-01", barrier: 4m);
            await b.Kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await AssignAsync(swich, b, pb, lic);

            // Jan: 1.00 income, 3.00 bank invoice paid by the licensee → −2.00: nothing taken, credit carried.
            await PlantAsync(pb, 2, 0.50m, Noon(2022, 1, 12));
            await AddCostAsync(op, lic, "Bank", "TBC Bank", "T-B-1", "2022-01-10", 3.00m);
            var jan = OnlyStatement(await RunAsync(swich, "2022-01", lic));
            Assert.Equal("Barrier", S(jan, "phase"));
            Assert.Equal(-2.00m, D(jan, "entitlement"));
            Assert.Equal(0m, D(jan, "due"));
            Assert.Equal(-2.00m, D(jan, "carryOut"));
            Assert.Equal("Completed", S(jan, "status"));
            var janLine = ParkLine(jan, pb);
            Assert.Equal(0m, D(janLine, "swichShare"));
            Assert.Equal(JsonValueKind.Null, janLine.GetProperty("bankTransferId").ValueKind);   // never a negative or zero transfer

            // Feb: 5.00 income; the credit comes off → 3.00 collected. R counts entitlement: −2.00.
            await PlantAsync(pb, 10, 0.50m, Noon(2022, 2, 10));
            var feb = OnlyStatement(await RunAsync(swich, "2022-02", lic));
            Assert.Equal(-2.00m, D(feb, "barrierBefore"));
            Assert.Equal(-2.00m, D(feb, "carryIn"));
            Assert.Equal("Barrier", S(feb, "phase"));
            Assert.Equal(5.00m, D(feb, "entitlement"));
            Assert.Equal(3.00m, D(feb, "due"));
            Assert.Equal(0m, D(feb, "carryOut"));
            Assert.Equal(3.00m, D(ParkLine(feb, pb), "swichShare"));

            // Mar: R = 3.00, 1.00 of barrier left, F − CL = 4.00 → crossing at φ = ¼;
            // normal = ½(4.00 − 0 − 1.00) + 1.00 = 2.50; E = 1.00 + ¾ × 2.50 = 2.875 → 2.88.
            await PlantAsync(pb, 8, 0.50m, Noon(2022, 3, 10));
            await AddCostAsync(swich, lic, "Hosting", "Hetzner", "T-B-2", "2022-03-03", 1.00m);
            var mar = OnlyStatement(await RunAsync(swich, "2022-03", lic));
            Assert.Equal(3.00m, D(mar, "barrierBefore"));
            Assert.Equal("Crossing", S(mar, "phase"));
            Assert.Equal(0.25m, D(mar, "barrierFraction"));
            Assert.Equal(4.00m, D(mar, "barrierEntitlement"));
            Assert.Equal(2.50m, D(mar, "normalEntitlement"));
            Assert.Equal(2.88m, D(mar, "entitlement"));
            Assert.Equal(2.88m, D(mar, "due"));
            Assert.Equal(5.88m, D(mar, "barrierAfter"));
            Assert.Equal(0m, D(mar, "barrierRemaining"));
            Assert.Equal(1.00m, D(ParkLine(mar, pb), "barrierFees"));   // ¼ of the park's 4.00 fees fell under the barrier

            // Apr: barrier filled → normal split of 1.00 = 0.50.
            await PlantAsync(pb, 2, 0.50m, Noon(2022, 4, 10));
            var apr = OnlyStatement(await RunAsync(swich, "2022-04", lic));
            Assert.Equal("Normal", S(apr, "phase"));
            Assert.Equal(0.50m, D(apr, "due"));

            // May: no fees at all, Swich paid 3.00, the licensee 1.00 → ½(0 − 1 − 3) + 3 = 1.00 owed to Swich,
            // but there is no fee income to take it from pro rata → carried forward as a debt.
            await AddCostAsync(swich, lic, "Domain", "Caucasus Online", "T-B-3", "2022-05-05", 3.00m);
            await AddCostAsync(op, lic, "Sms", "SMSOffice", "T-B-4", "2022-05-06", 1.00m);
            var may = OnlyStatement(await RunAsync(swich, "2022-05", lic));
            Assert.Equal(1.00m, D(may, "entitlement"));
            Assert.Equal(0m, D(may, "due"));
            Assert.Equal(1.00m, D(may, "carryOut"));
            Assert.Equal(0, may.GetProperty("parks").GetArrayLength());
            Assert.Equal("Completed", S(may, "status"));

            // Jun: ½ × 1.00 + the 1.00 carried = 1.50.
            await PlantAsync(pb, 2, 0.50m, Noon(2022, 6, 10));
            var jun = OnlyStatement(await RunAsync(swich, "2022-06", lic));
            Assert.Equal(1.00m, D(jun, "carryIn"));
            Assert.Equal(1.50m, D(jun, "due"));

            // Over the six months Swich was entitled to exactly what was transferred.
            var licensee = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/licensees/{lic}");
            var barrier = licensee.GetProperty("barrier");
            Assert.Equal(-2.00m + 5.00m + 2.88m + 0.50m + 1.00m + 0.50m, D(barrier, "entitled"));
            Assert.Equal(3.00m + 2.88m + 0.50m + 1.50m, D(barrier, "transferred"));
            Assert.True(barrier.GetProperty("filled").GetBoolean());
        }
        finally { await ReturnAsync(b); }
    }

    [Fact]
    public async Task A_Swich_direct_park_settles_monthly_on_its_own_percentage()
    {
        var swich = await _f.SuperAdminAsync();
        var (p5, p3) = (await ParkIdAsync(P5), await ParkIdAsync(P3));
        var b = new Borrowed();
        try
        {
            await AssignAsync(swich, b, p5, null);
            var park = await _f.DbAsync(db => db.Parks.FirstAsync(p => p.Id == p5));
            b.Splits[p5] = (park.SwichSharePercent, park.Phase1SharePercent, park.Phase1CapGel);
            await _f.DbAsync(async db =>
            {
                var p = await db.Parks.FirstAsync(x => x.Id == p5);
                p.SwichSharePercent = 40m; p.Phase1SharePercent = null; p.Phase1CapGel = null;
                return await db.SaveChangesAsync();
            });

            await PlantAsync(p5, 3, 0.50m, Noon(2023, 5, 10));
            var run = await RunAsync(swich, "2023-05", parkId: p5);
            Assert.Empty(run.GetProperty("statements").EnumerateArray());
            var row = Assert.Single(run.GetProperty("direct").EnumerateArray());
            Assert.Equal(1.50m, D(row, "feeTotal"));
            Assert.Equal(0.60m, D(row, "swichShare"));                  // 40 % of 1.50, no costs, no barrier
            Assert.Equal("Completed", S(row, "status"));
            Assert.True(row.GetProperty("isMonthly").GetBoolean());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("monthlyStatementId").ValueKind);
            Assert.Equal("PayTaxi settlement 2023-05, 3 tx, inv ref PT-2023-05", S(row, "description"));

            // Idempotent per (park, month).
            var again = Assert.Single((await RunAsync(swich, "2023-05", parkId: p5)).GetProperty("direct").EnumerateArray());
            Assert.Equal(row.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());

            // A licensee park is never settled on its own.
            var refused = await RunAsync(swich, "2023-05", parkId: p3, expect: HttpStatusCode.Conflict);
            Assert.Equal("park_has_licensee", ApiFixture.Code(refused));

            // Swich sees the direct park in the month view; the licensee's operator does not.
            var view = await swich.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2023-05");
            var mine = view.GetProperty("direct").EnumerateArray().Single(d => d.GetProperty("parkId").GetGuid() == p5);
            Assert.False(mine.GetProperty("isPreview").GetBoolean());
            Assert.Equal(0.60m, D(mine, "swichShare"));
            var op = await _f.OperatorAsync();
            var opView = await op.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2023-05");
            Assert.Empty(opView.GetProperty("direct").EnumerateArray());

            // The parks list says which park is which.
            var parks = await swich.GetFromJsonAsync<JsonElement>("/api/admin/parks");
            Assert.Equal(JsonValueKind.Null, parks.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == p5).GetProperty("licenseeId").ValueKind);
            Assert.Equal(JsonValueKind.String, parks.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == p3).GetProperty("licenseeName").ValueKind);
        }
        finally { await ReturnAsync(b); }
    }

    [Fact]
    public async Task A_failed_park_transfer_alerts_shows_overdue_days_is_retried_nightly_and_completes_on_retry()
    {
        var swich = await _f.SuperAdminAsync();
        var p5 = await ParkIdAsync(P5);
        var b = new Borrowed();
        try
        {
            var lic = await CreateLicenseeAsync(swich, b, "Test licensee D", "2023-07-01");
            await b.Kit.InForceAsync(lic);   // its operator works for it only while it is in force
            await AssignAsync(swich, b, p5, lic);
            await PlantAsync(p5, 4, 0.50m, Noon(2023, 8, 10));

            // The park switched its payout accounts off → the transfer cannot be made.
            var accounts = await _f.DbAsync(async db =>
            {
                var list = await db.ParkBankAccounts.Where(a => a.ParkId == p5 && a.IsActive).ToListAsync();
                foreach (var a in list) a.IsActive = false;
                await db.SaveChangesAsync();
                return list.Select(a => a.Id).ToList();
            });
            b.InactivatedAccounts.AddRange(accounts);

            var st = OnlyStatement(await RunAsync(swich, "2023-08", lic));
            Assert.Equal(2.00m, D(st, "due"));
            Assert.Equal("Failed", S(st, "status"));
            Assert.Equal(0m, D(st, "transferred"));
            Assert.Equal(2.00m, D(st, "outstanding"));
            var line = ParkLine(st, p5);
            Assert.Equal("Failed", S(line, "status"));
            Assert.Equal("NO_PARK_ACCOUNT", S(line, "failureCode"));
            var rowId = line.GetProperty("settlementId").GetGuid();

            // §4.1.2: due 5 days after the month → 2023-09-05; 0.1 % a day since then, for information — counted on
            // the days whose cause was the park's. The month is settled years late here, so the failure is moved
            // to the settlement day it would have happened on (a day without any attempt is the platform's).
            await _f.DbAsync(db => db.LedgerEntries
                .Where(l => l.SettlementId == rowId && l.EntryType == LedgerEntryType.SettlementFailed)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.CreatedAt, Noon(2023, 9, 3))));
            var today = SettlementService.TodayLocal();
            var days = today.DayNumber - new DateOnly(2023, 9, 5).DayNumber;
            var overdue = ParkLine(await swich.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/{st.GetProperty("statementId").GetGuid()}"), p5)
                .GetProperty("overdue");
            Assert.Equal("2023-09-05", S(overdue, "dueDate"));
            Assert.Equal(days, overdue.GetProperty("daysOverdue").GetInt32());
            Assert.Equal(days, overdue.GetProperty("penaltyDays").GetInt32());
            Assert.Equal(0, overdue.GetProperty("technicalDays").GetInt32());
            Assert.Equal(Math.Round(2.00m * 0.001m * days, 2, MidpointRounding.AwayFromZero), D(overdue, "accrued"));

            // The console banner raises it (same alert kind as ever, now for a monthly transfer).
            var alerts = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var alert = alerts.GetProperty("alerts").EnumerateArray()
                .Single(a => S(a, "kind") == "settlement_failed" && a.GetProperty("parkId").GetGuid() == p5);
            Assert.Equal(1, alert.GetProperty("count").GetInt32());
            Assert.Equal(2.00m, D(alert, "amount"));
            Assert.Equal("NO_PARK_ACCOUNT", S(alert, "code"));

            // Only Swich retries.
            var op = await _f.OperatorAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync($"/api/admin/settlements/{rowId}/retry", null)).StatusCode);

            // The nightly tick retries every failed transfer (a day before the run day: retries only).
            using (var scope = _f.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISettlementService>();
                var tick = await svc.RunDueAsync(new DateOnly(2026, 10, 1), "test");
                Assert.Null(tick.MonthSettled);
                Assert.True(tick.Retried >= 1);
            }
            var afterTick = await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId));
            Assert.Equal(SettlementStatus.Failed, afterTick.Status);
            Assert.Equal(2, afterTick.AttemptCount);

            // The park fixes its account; Swich retries → transferred, statement completes, alert gone.
            await _f.DbAsync(async db =>
            {
                foreach (var a in await db.ParkBankAccounts.Where(x => accounts.Contains(x.Id)).ToListAsync()) a.IsActive = true;
                return await db.SaveChangesAsync();
            });
            var retry = await swich.PostAsync($"/api/admin/settlements/{rowId}/retry", null);
            var retried = await retry.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(retry.StatusCode == HttpStatusCode.OK, retried.ToString());
            Assert.Equal("Completed", S(retried, "status"));
            var stored = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/{st.GetProperty("statementId").GetGuid()}");
            Assert.Equal("Completed", S(stored, "status"));
            Assert.Equal(2.00m, D(stored, "transferred"));
            // Paid late: the overdue information is kept, now final — and still the park's side, though the
            // success cleared the failure reason.
            var paidLine = ParkLine(stored, p5);
            Assert.True(paidLine.GetProperty("overdue").GetProperty("paid").GetBoolean());
            Assert.Equal(days, paidLine.GetProperty("overdue").GetProperty("penaltyDays").GetInt32());
            var cleared = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.DoesNotContain(cleared.GetProperty("alerts").EnumerateArray(),
                a => S(a, "kind") == "settlement_failed" && a.GetProperty("parkId").GetGuid() == p5);
        }
        finally { await ReturnAsync(b); }
    }

    [Fact]
    public async Task Licensee_costs_and_statements_follow_the_role_matrix_and_validate_their_input()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(P3);
        var b = new Borrowed();
        try
        {
            var lic = await CreateLicenseeAsync(swich, b, "Test licensee E", launch: null);
            await b.Kit.InForceAsync(lic);   // its operator works for it only while it is in force

            // No launch date → the agreement's settlement has not started; before the launch month → refused.
            Assert.Equal("launch_date_not_set", ApiFixture.Code(await RunAsync(swich, "2024-01", lic, expect: HttpStatusCode.Conflict)));
            // A launch date is agreed ahead: one already gone cannot be entered through the API (the test writes it).
            var backdated = await swich.PatchAsJsonAsync($"/api/admin/licensees/{lic}", new { launchDate = "2024-02-01", reason = "Late entry" });
            Assert.Equal(HttpStatusCode.BadRequest, backdated.StatusCode);
            Assert.Equal("launch_date_in_past", ApiFixture.Code(await backdated.Content.ReadFromJsonAsync<JsonElement>()));
            await new SettlementKit(_f).SetLaunchAsync(lic, new DateOnly(2024, 2, 1));
            Assert.Equal("before_launch", ApiFixture.Code(await RunAsync(swich, "2024-01", lic, expect: HttpStatusCode.Conflict)));

            // The park manager sees none of it.
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/admin/licensees")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/admin/licensees/{lic}/costs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/admin/licensees/{lic}/costs",
                new { category = "Sms", vendor = "X", invoiceNumber = "1", invoiceDate = "2024-02-02", amount = 1m })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/admin/settlements/month")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/admin/settlements/statements/preview?licenseeId={lic}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/admin/settlements/statements")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync("/api/admin/settlements/run?month=2024-02", null)).StatusCode);

            // The operator adds the licensee's invoices; Swich adds its own.
            var mine = await AddCostAsync(op, lic, "Sms", "SMSOffice", "E-1", "2024-02-03", 2.50m);
            Assert.Equal("Licensee", S(mine, "paidBy"));
            Assert.True(mine.GetProperty("canEdit").GetBoolean());
            var theirs = await AddCostAsync(swich, lic, "hosting", "Hetzner", "E-2", "2024-02-04", 9.99m);
            Assert.Equal("Swich", S(theirs, "paidBy"));
            var mineId = mine.GetProperty("id").GetGuid();
            var theirsId = theirs.GetProperty("id").GetGuid();

            // Each side corrects only its own — Swich no more than the operator (§2.4: each side records its invoices).
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PatchAsJsonAsync($"/api/admin/licensees/{lic}/costs/{theirsId}", new { amount = 1m })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.DeleteAsync($"/api/admin/licensees/{lic}/costs/{theirsId}")).StatusCode);
            var fixedAmount = await op.PatchAsJsonAsync($"/api/admin/licensees/{lic}/costs/{mineId}", new { amount = 2.60m });
            Assert.Equal(HttpStatusCode.OK, fixedAmount.StatusCode);
            Assert.Equal(2.60m, D(await fixedAmount.Content.ReadFromJsonAsync<JsonElement>(), "amount"));
            await new SettlementKit(_f).EnteredHoursAgoAsync(mineId, 72);   // the edit restarted its 48-hour window
            Assert.Equal(HttpStatusCode.Forbidden, (await swich.PatchAsJsonAsync($"/api/admin/licensees/{lic}/costs/{mineId}", new { note = "80 SMS" })).StatusCode);

            // Input rules: a third-party invoice number, a past date, tetri-exact amounts, known categories, no duplicates.
            Assert.Equal("invoice_number_required", ApiFixture.Code(await AddCostAsync(op, lic, "Sms", "SMSOffice", "", "2024-02-03", 1m, HttpStatusCode.BadRequest)));
            var tomorrow = SettlementService.TodayLocal().AddDays(1).ToString("yyyy-MM-dd");
            Assert.Equal("invoice_date_in_future", ApiFixture.Code(await AddCostAsync(op, lic, "Sms", "SMSOffice", "E-3", tomorrow, 1m, HttpStatusCode.BadRequest)));
            Assert.Equal("amount_precision", ApiFixture.Code(await AddCostAsync(op, lic, "Sms", "SMSOffice", "E-4", "2024-02-03", 1.005m, HttpStatusCode.BadRequest)));
            Assert.Equal("invalid_amount", ApiFixture.Code(await AddCostAsync(op, lic, "Sms", "SMSOffice", "E-5", "2024-02-03", 0m, HttpStatusCode.BadRequest)));
            Assert.Equal("invalid_category", ApiFixture.Code(await AddCostAsync(op, lic, "Coffee", "Café", "E-6", "2024-02-03", 1m, HttpStatusCode.BadRequest)));
            Assert.Equal("duplicate_invoice", ApiFixture.Code(await AddCostAsync(op, lic, "Sms", "smsoffice", "e-1", "2024-02-05", 1m, HttpStatusCode.Conflict)));

            // The licensee: both sides read it, only Swich edits it.
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/admin/licensees/{lic}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PatchAsJsonAsync($"/api/admin/licensees/{lic}", new { barrierGel = 1m })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync($"/api/admin/parks/{await ParkIdAsync(P3)}/licensee", new { licenseeId = (Guid?)null })).StatusCode);
            Assert.Equal("invalid_share", ApiFixture.Code(await (await swich.PatchAsJsonAsync($"/api/admin/licensees/{lic}", new { swichSharePercent = 101m })).Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal("invalid_barrier", ApiFixture.Code(await (await swich.PatchAsJsonAsync($"/api/admin/licensees/{lic}", new { barrierGel = -1m })).Content.ReadFromJsonAsync<JsonElement>()));

            // The operator sees the same preview Swich does — and cannot run it.
            var preview = await op.GetFromJsonAsync<JsonElement>($"/api/admin/settlements/statements/preview?licenseeId={lic}&month=2024-02");
            Assert.True(preview.GetProperty("isPreview").GetBoolean());
            Assert.Equal(2.60m, D(preview, "licenseeCosts"));
            Assert.Equal(9.99m, D(preview, "swichCosts"));
            Assert.Equal(2, preview.GetProperty("costs").GetArrayLength());
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync($"/api/admin/settlements/run?month=2024-02&licenseeId={lic}", null)).StatusCode);
            var costList = await op.GetFromJsonAsync<JsonElement>($"/api/admin/licensees/{lic}/costs?month=2024-02");
            Assert.Equal(2, costList.GetProperty("count").GetInt32());

            // Settled with no income: −2.60 carried for the licensee; the costs are now locked.
            var st = OnlyStatement(await RunAsync(swich, "2024-02", lic));
            Assert.Equal(-2.60m, D(st, "carryOut"));
            Assert.Equal(0m, D(st, "due"));
            Assert.All(st.GetProperty("costs").EnumerateArray(), c =>
            {
                Assert.Equal("PT-2024-02", S(c, "includedIn"));
                Assert.False(c.GetProperty("canEdit").GetBoolean());
            });
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/admin/licensees/{lic}/costs/{mineId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await swich.PatchAsJsonAsync($"/api/admin/licensees/{lic}/costs/{theirsId}", new { amount = 1m })).StatusCode);
        }
        finally { await ReturnAsync(b); }
    }

    [Fact]
    public async Task The_running_month_is_refused_unless_the_environment_allows_it()
    {
        var licensee = await _f.DbAsync(db => db.Licensees.AsNoTracking().OrderByDescending(l => l.IsActive).Select(l => l.Id).FirstAsync());
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var production = new SettlementService(db, scope.ServiceProvider,
            Options.Create(new SettlementOptions { SwichIban = "GE12TB7100000000000001", AllowOpenMonthRun = false }),
            NullLogger<SettlementService>.Instance);
        var thisMonth = SettlementService.FirstOfMonth(SettlementService.TodayLocal());

        var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => production.RunMonthAsync(thisMonth, "test", licensee));
        Assert.Equal(SettlementRejectionCodes.MonthNotClosed, ex.Code);
        Assert.False(await db.MonthlyStatements.AnyAsync(m => m.Month == thisMonth));

        // The preview of the running month works regardless — it never writes.
        var preview = await production.PreviewLicenseeMonthAsync(licensee, thisMonth);
        Assert.Null(preview.StatementId);
        Assert.Equal(SettlementRejectionCodes.MonthNotClosed, preview.BlockedReason);
    }

    [Fact]
    public async Task Reconciliation_does_not_flag_the_settlement_transfer_as_an_orphan()
    {
        var swich = await _f.SuperAdminAsync();
        var p3 = await ParkIdAsync(P3);
        var b = new Borrowed();
        try
        {
            // A monthly settlement sent from Tbilisi #3's account today (its planted month is long past).
            var lic = await CreateLicenseeAsync(swich, b, "Test licensee F", "2024-05-01");
            await AssignAsync(swich, b, p3, lic);
            await PlantAsync(p3, 2, 0.50m, Noon(2024, 6, 10));
            var st = OnlyStatement(await RunAsync(swich, "2024-06", lic));
            var transferId = S(ParkLine(st, p3), "bankTransferId");
            Assert.False(string.IsNullOrEmpty(transferId));

            await CompleteCashoutAsync("yp_tb3_002", 5m);
            var run = await swich.PostAsync($"/api/admin/parks/{p3}/reconciliation/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var body = await run.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Completed", body.GetProperty("status").GetString());
            Assert.True(body.GetProperty("bankTransfersScanned").GetInt32() > 0, "the mock bank must have reported transfers");

            var discrepancies = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{p3}/reconciliation/discrepancies?take=200");
            var orphanIds = discrepancies.GetProperty("discrepancies").EnumerateArray()
                .Where(d => d.GetProperty("kind").GetString() == "orphaned_bank_send")
                .Select(d => d.GetProperty("bankTransferId").GetString())
                .ToHashSet();
            var allSettlementTransfers = await _f.DbAsync(db => db.Settlements.Where(x => x.ParkId == p3 && x.BankTransferId != null).Select(x => x.BankTransferId!).ToListAsync());
            Assert.Contains(transferId, allSettlementTransfers);
            foreach (var id in allSettlementTransfers)
                Assert.DoesNotContain(id, orphanIds);

            // Cashouts completed in this test run all matched: no missing_in_bank for them.
            var missing = discrepancies.GetProperty("discrepancies").EnumerateArray()
                .Where(d => d.GetProperty("kind").GetString() == "missing_in_bank" && d.GetProperty("cashoutId").ValueKind == JsonValueKind.String)
                .Select(d => d.GetProperty("cashoutId").GetGuid()).ToHashSet();
            var recent = await _f.DbAsync(db => db.Cashouts
                .Where(c => c.ParkId == p3 && c.Status == CashoutStatus.Completed && c.CreatedAt > DateTime.UtcNow.AddMinutes(-10))
                .Select(c => c.Id).ToListAsync());
            Assert.NotEmpty(recent);
            Assert.All(recent, id => Assert.DoesNotContain(id, missing));

            // Park admins cannot trigger a run.
            var manager = await _f.ParkAdminAsync(P3);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/admin/parks/{p3}/reconciliation/run", null)).StatusCode);
        }
        finally { await ReturnAsync(b); }
    }

    private async Task CompleteCashoutAsync(string profile, decimal amount)
    {
        var (driver, _, _) = await _f.DriverClientAsync(profile);
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        var saga = await (await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount, idempotencyKey = Guid.NewGuid().ToString() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", saga.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_second_reconciliation_run_does_not_duplicate_open_discrepancies()
    {
        var admin = await _f.SuperAdminAsync();
        var batumi = await ParkIdAsync(PB);

        // Seeded historical cashouts are unknown to this process's mock bank → they show up as
        // missing_in_bank on every run. That is exactly the "still open tomorrow" case.
        var run1 = await (await admin.PostAsync($"/api/admin/parks/{batumi}/reconciliation/run", null)).Content.ReadFromJsonAsync<JsonElement>();
        var openAfter1 = await _f.DbAsync(db => db.ReconciliationDiscrepancies.CountAsync(d => d.ParkId == batumi && !d.IsResolved));

        var run2 = await (await admin.PostAsync($"/api/admin/parks/{batumi}/reconciliation/run", null)).Content.ReadFromJsonAsync<JsonElement>();
        var openAfter2 = await _f.DbAsync(db => db.ReconciliationDiscrepancies.CountAsync(d => d.ParkId == batumi && !d.IsResolved));

        Assert.Equal(openAfter1, openAfter2);
        Assert.Equal(0, run2.GetProperty("discrepanciesFound").GetInt32());   // nothing NEW the second time
        // …and the still-open rows now point at the latest run that saw them.
        if (openAfter1 > 0)
        {
            var latestRunId = run2.GetProperty("id").GetGuid();
            var pointing = await _f.DbAsync(db => db.ReconciliationDiscrepancies.CountAsync(d => d.ParkId == batumi && !d.IsResolved && d.RunId == latestRunId));
            Assert.Equal(openAfter2, pointing);
        }
    }

    [Fact]
    public async Task Invoice_is_refused_for_a_cashout_that_did_not_complete()
    {
        var admin = await _f.SuperAdminAsync();
        var (parkId, cashoutId) = await _f.DbAsync(async db =>
        {
            var c = await db.Cashouts.AsNoTracking().FirstOrDefaultAsync(x => x.Status != CashoutStatus.Completed);
            return c is null ? (Guid.Empty, Guid.Empty) : (c.ParkId, c.Id);
        });
        if (cashoutId == Guid.Empty) return; // seed has none in this state; nothing to assert
        var res = await admin.GetAsync($"/api/admin/parks/{parkId}/cashouts/{cashoutId}/invoice.pdf");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}
