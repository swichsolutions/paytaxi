using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Nightly money: the park → Swich settlement through the API, phase-1 share math on real
/// cashouts, and reconciliation treating the settlement transfer as expected (not an orphan).
/// </summary>
[Collection("api")]
public class SettlementAndReconciliationTests
{
    private readonly ApiFixture _f;
    public SettlementAndReconciliationTests(ApiFixture f) => _f = f;

    private static string Today()
    {
        var tz = PayTaxi.Infrastructure.Services.SettlementService.ResolveTimeZone(null);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz)).ToString("yyyy-MM-dd");
    }

    private async Task<decimal> CompleteCashoutAsync(string profile, decimal amount)
    {
        var (driver, _, _) = await _f.DriverClientAsync(profile);
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        var saga = await (await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount, idempotencyKey = Guid.NewGuid().ToString() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", saga.GetProperty("status").GetString());
        return saga.GetProperty("fee").GetDecimal();
    }

    [Fact]
    public async Task Levans_park_settles_at_phase1_and_a_network_park_at_50_percent()
    {
        var admin = await _f.SuperAdminAsync();
        var (levan, batumi) = await _f.DbAsync(async db => (
            await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3"),
            await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1")));
        Assert.Equal(100m, levan.Phase1SharePercent);
        Assert.Equal(50m, batumi.SwichSharePercent);

        // Make sure each park has at least one completed cashout today that is not yet settled.
        var feeLevan = await CompleteCashoutAsync("yp_tb3_001", 5m);
        var batumiDriver = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.ParkId == batumi.Id && d.Status == PayTaxi.Core.Enums.DriverStatus.Active)).YandexDriverProfileId!);
        var feeBatumi = await CompleteCashoutAsync(batumiDriver, 5m);

        var unsettledLevan = await _f.DbAsync(db => db.Cashouts
            .Where(c => c.ParkId == levan.Id && c.Status == PayTaxi.Core.Enums.CashoutStatus.Completed && c.SettlementId == null)
            .SumAsync(c => c.Fee));

        // Settle Levan's park for today.
        var res = await admin.PostAsync($"/api/admin/settlements/run?parkId={levan.Id}&date={Today()}", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var s = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", s.GetProperty("status").GetString());
        Assert.Equal(unsettledLevan, s.GetProperty("feeTotal").GetDecimal());
        // Phase 1: cumulative fees are far below the 20,000 GEL cap → Swich takes 100%.
        Assert.Equal(s.GetProperty("feeTotal").GetDecimal(), s.GetProperty("swichShare").GetDecimal());
        Assert.Equal(0m, s.GetProperty("parkShare").GetDecimal());
        Assert.False(string.IsNullOrEmpty(s.GetProperty("bankTransferId").GetString()));

        // Idempotent per (park, day): running again returns the same settlement, no second transfer.
        var again = await (await admin.PostAsync($"/api/admin/settlements/run?parkId={levan.Id}&date={Today()}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(s.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());

        // Batumi: 50/50.
        var b = await (await admin.PostAsync($"/api/admin/settlements/run?parkId={batumi.Id}&date={Today()}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", b.GetProperty("status").GetString());
        var fee = b.GetProperty("feeTotal").GetDecimal();
        Assert.Equal(Math.Round(fee / 2, 2, MidpointRounding.AwayFromZero), b.GetProperty("swichShare").GetDecimal());
        Assert.Equal(fee - b.GetProperty("swichShare").GetDecimal(), b.GetProperty("parkShare").GetDecimal());

        // Operators may look but not run.
        var op = await _f.OperatorAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync($"/api/admin/settlements/run?parkId={levan.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/admin/settlements?take=5")).StatusCode);
    }

    [Fact]
    public async Task Reconciliation_does_not_flag_the_settlement_transfer_as_an_orphan()
    {
        var admin = await _f.SuperAdminAsync();
        var levan = await _f.DbAsync(async db => await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3"));

        // Ensure there is a settled settlement with a bank transfer today (from the test above or now).
        await CompleteCashoutAsync("yp_tb3_002", 5m);
        var s = await (await admin.PostAsync($"/api/admin/settlements/run?parkId={levan.Id}&date={Today()}", null)).Content.ReadFromJsonAsync<JsonElement>();
        var transferIds = await _f.DbAsync(db => db.Settlements.Where(x => x.ParkId == levan.Id && x.BankTransferId != null).Select(x => x.BankTransferId!).ToListAsync());
        Assert.NotEmpty(transferIds);

        var run = await admin.PostAsync($"/api/admin/parks/{levan.Id}/reconciliation/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var body = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("bankTransfersScanned").GetInt32() > 0, "the mock bank must have reported transfers");

        var orphans = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{levan.Id}/reconciliation/discrepancies?take=200");
        var orphanIds = orphans.GetProperty("discrepancies").EnumerateArray()
            .Where(d => d.GetProperty("kind").GetString() == "orphaned_bank_send")
            .Select(d => d.GetProperty("bankTransferId").GetString())
            .ToHashSet();
        foreach (var id in transferIds)
            Assert.DoesNotContain(id, orphanIds);

        // Cashouts completed in this test run all matched: no missing_in_bank for them.
        var missing = orphans.GetProperty("discrepancies").EnumerateArray()
            .Where(d => d.GetProperty("kind").GetString() == "missing_in_bank" && d.GetProperty("cashoutId").ValueKind == JsonValueKind.String)
            .Select(d => d.GetProperty("cashoutId").GetGuid()).ToHashSet();
        var mine = await _f.DbAsync(db => db.Cashouts
            .Where(c => c.ParkId == levan.Id && c.Status == PayTaxi.Core.Enums.CashoutStatus.Completed && c.CreatedAt > DateTime.UtcNow.AddMinutes(-10))
            .Select(c => c.Id).ToListAsync());
        Assert.NotEmpty(mine);
        Assert.All(mine, id => Assert.DoesNotContain(id, missing));

        // Park admins cannot trigger a run.
        var manager = await _f.ParkAdminAsync("tbilisi-auto-park-3");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/admin/parks/{levan.Id}/reconciliation/run", null)).StatusCode);
    }

    [Fact]
    public async Task A_second_reconciliation_run_does_not_duplicate_open_discrepancies()
    {
        var admin = await _f.SuperAdminAsync();
        var batumi = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1")).Id);

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
    public async Task Monthly_invoice_lists_the_month_and_renders_a_pdf_for_everyone_who_sees_the_park()
    {
        var admin = await _f.SuperAdminAsync();
        var levan = await _f.DbAsync(db => db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3"));

        // At least one settlement this month: a cashout today, then settle today (idempotent if already done).
        await CompleteCashoutAsync("yp_tb3_002", 5m);
        var run = await (await admin.PostAsync($"/api/admin/settlements/run?parkId={levan.Id}&date={Today()}", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        var month = Today()[..7]; // yyyy-MM

        // The month list carries the invoice figures; they must equal the settlement rows for that month.
        var months = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{levan.Id}/settlements/months");
        var row = months.GetProperty("months").EnumerateArray().Single(m => m.GetProperty("month").GetString() == month);
        Assert.Equal($"PT-{month}", row.GetProperty("invoiceRef").GetString());
        Assert.True(row.GetProperty("isCurrent").GetBoolean());
        var (expectedShare, expectedRows) = await _f.DbAsync(async db =>
        {
            var first = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd");
            var rows = await db.Settlements.AsNoTracking()
                .Where(s => s.ParkId == levan.Id && s.SettlementDate >= first && s.SettlementDate < first.AddMonths(1))
                .ToListAsync();
            return (rows.Sum(s => s.SwichShare), rows.Count);
        });
        Assert.Equal(expectedRows, row.GetProperty("settlements").GetInt32());
        Assert.Equal(expectedShare, row.GetProperty("swichShare").GetDecimal());
        Assert.Equal(row.GetProperty("transferred").GetDecimal() + row.GetProperty("outstanding").GetDecimal(),
            row.GetProperty("swichShare").GetDecimal());

        // The PDF itself.
        var pdf = await admin.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf?month={month}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"PT-{month}-tbilisi-auto-park-3.pdf", pdf.Content.Headers.ContentDisposition!.ToString());
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 5_000, $"pdf too small: {bytes.Length}");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        // No settlement in that month → 404 with a typed code; garbage → 400.
        var none = await admin.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf?month=2020-01");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal("no_settlements_in_month", (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf?month=sept")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf")).StatusCode);

        // The operator and the park's own manager may download it; another park's manager may not.
        var op = await _f.OperatorAsync();
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf?month={month}")).StatusCode);
        var own = await _f.ParkAdminAsync("tbilisi-auto-park-3");
        Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/admin/parks/{levan.Id}/settlements/months")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await own.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf?month={month}")).StatusCode);
        var other = await _f.ParkAdminAsync("batumi-auto-park-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/admin/parks/{levan.Id}/settlements/months")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/admin/parks/{levan.Id}/settlements/invoice.pdf?month={month}")).StatusCode);
    }

    [Fact]
    public async Task Invoice_is_refused_for_a_cashout_that_did_not_complete()
    {
        var admin = await _f.SuperAdminAsync();
        var (parkId, cashoutId) = await _f.DbAsync(async db =>
        {
            var c = await db.Cashouts.AsNoTracking().FirstOrDefaultAsync(x => x.Status != PayTaxi.Core.Enums.CashoutStatus.Completed);
            return c is null ? (Guid.Empty, Guid.Empty) : (c.ParkId, c.Id);
        });
        if (cashoutId == Guid.Empty) return; // seed has none in this state; nothing to assert
        var res = await admin.GetAsync($"/api/admin/parks/{parkId}/cashouts/{cashoutId}/invoice.pdf");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}
