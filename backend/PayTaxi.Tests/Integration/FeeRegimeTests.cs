using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using Xunit;
using static PayTaxi.Tests.Integration.SettlementKit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// §2.2: the licence agreement governs the fees of the parks the licensee connected; a park Swich connected directly
/// is outside it. When a park moves between the two, every fee stays under the arrangement in force when it was
/// EARNED: the regime is stamped on the cashout when it completes, and the monthly settlement follows the stamp, not
/// the park's assignment on the run day. Cashouts completed before regimes were stamped follow the assignment at
/// settlement time (the only information there is for them).
/// </summary>
[Collection("api")]
public class FeeRegimeTests
{
    private readonly ApiFixture _f;
    public FeeRegimeTests(ApiFixture f) => _f = f;

    /// <summary>One cashout through the real saga (mock bank, mock Yandex): it completes at once.</summary>
    private async Task<Cashout> CashOutAsync(HttpClient driver, Guid cardId, decimal amount)
    {
        var res = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount, idempotencyKey = Guid.NewGuid().ToString() });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        var id = body.GetProperty("cashoutId").GetGuid();
        return await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == id));
    }

    [Fact]
    public async Task A_park_reassigned_mid_month_splits_its_month_between_the_licensee_statement_and_a_direct_row()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Regime licensee", "2019-03-01");
            await kit.AssignAsync(swich, pb, lic);

            var (driver, _, _) = await _f.DriverClientAsync("yp_bt1_004");
            var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
            var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

            // Earned under the licence agreement: stamped with the licensee when it completes.
            var c1 = await CashOutAsync(driver, cardId, 6m);
            var c2 = await CashOutAsync(driver, cardId, 7m);
            foreach (var c in new[] { c1, c2 })
                Assert.Equal((true, (Guid?)lic), (c.FeeRegimeStamped, c.FeeLicenseeId));

            // Mid-month the park becomes Swich-direct. What it already earned stays under the agreement — the answer says
            // how much of what is not yet settled that is.
            var assign = await swich.PutAsJsonAsync($"/api/admin/parks/{pb}/licensee", new { licenseeId = (Guid?)null });
            var assigned = await assign.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(assign.StatusCode == HttpStatusCode.OK, assigned.ToString());
            Assert.True(assigned.GetProperty("changed").GetBoolean());
            Assert.True(assigned.GetProperty("unsettledEarnedBefore").GetDecimal() >= c1.Fee + c2.Fee);

            // Earned as a Swich-direct park: stamped direct.
            var c3 = await CashOutAsync(driver, cardId, 8m);
            Assert.Equal((true, (Guid?)null), (c3.FeeRegimeStamped, c3.FeeLicenseeId));
            // One from before regimes were stamped: it follows the park's assignment when it is settled (direct now).
            var legacy = (await kit.PlantAsync(pb, 1, 0.50m, Noon(2019, 3, 20))).Single();

            // All of it in one past month nobody else uses.
            var moved = new[] { (c1.Id, Noon(2019, 3, 5)), (c2.Id, Noon(2019, 3, 6)), (c3.Id, Noon(2019, 3, 18)) };
            await _f.DbAsync(async db =>
            {
                foreach (var (id, at) in moved)
                {
                    var c = await db.Cashouts.FirstAsync(x => x.Id == id);
                    c.CompletedAt = at;
                    c.CreatedAt = at.AddMinutes(-1);
                }
                return await db.SaveChangesAsync();
            });

            // The licensee's statement counts what was earned under it — even though the park is direct on the run day …
            var st = OnlyStatement(await RunAsync(swich, "2019-03", licenseeId: lic));
            Assert.Equal(2, st.GetProperty("cashoutCount").GetInt32());
            Assert.Equal(c1.Fee + c2.Fee, D(st, "feeIncome"));
            var line = ParkLine(st, pb);
            Assert.Equal(c1.Fee + c2.Fee, D(line, "fees"));

            // … and the park's own direct row takes the rest of its month: the fee earned as direct and the unstamped one.
            var direct = (await RunAsync(swich, "2019-03", parkId: pb)).GetProperty("direct").EnumerateArray().Single();
            Assert.Equal(2, direct.GetProperty("cashoutCount").GetInt32());
            Assert.Equal(c3.Fee + 0.50m, D(direct, "feeTotal"));

            // Two monthly rows for one park and month — one per regime — and every fee in exactly one of them.
            var (rows, stamps) = await _f.DbAsync(async db =>
            {
                var r = await db.Settlements.AsNoTracking()
                    .Where(s => s.ParkId == pb && s.IsMonthly && s.SettlementDate == new DateOnly(2019, 3, 31)).ToListAsync();
                var ids = new[] { c1.Id, c2.Id, c3.Id, legacy };
                var c = await db.Cashouts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.SettlementId);
                return (r, c);
            });
            Assert.Equal(2, rows.Count);
            var licRow = rows.Single(r => r.MonthlyStatementId is not null);
            var directRow = rows.Single(r => r.MonthlyStatementId is null);
            Assert.Equal(licRow.Id, stamps[c1.Id]);
            Assert.Equal(licRow.Id, stamps[c2.Id]);
            Assert.Equal(directRow.Id, stamps[c3.Id]);
            Assert.Equal(directRow.Id, stamps[legacy]);

            // The park's month adds both up; its months card points at the one row that still needs attention, if any.
            var months = await swich.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{pb}/settlements/months");
            var march = months.GetProperty("months").EnumerateArray().Single(m => S(m, "month") == "2019-03");
            Assert.Equal(2, march.GetProperty("settlements").GetInt32());
            Assert.Equal(c1.Fee + c2.Fee + c3.Fee + 0.50m, D(march, "feeTotal"));

            // Re-running either side settles nothing twice.
            Assert.Equal(D(st, "feeIncome"), D(OnlyStatement(await RunAsync(swich, "2019-03", licenseeId: lic)), "feeIncome"));
            Assert.Equal(directRow.Id, (await RunAsync(swich, "2019-03", parkId: pb)).GetProperty("direct").EnumerateArray().Single().GetProperty("id").GetGuid());

            // The park's monthly invoice carries both rows.
            var pdf = await swich.GetAsync($"/api/admin/parks/{pb}/settlements/invoice.pdf?month=2019-03");
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
        finally { await kit.ReturnAsync(); }
    }

    [Fact]
    public async Task A_park_that_joins_a_licensee_keeps_the_fees_it_earned_as_direct_on_its_own_row()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Joining licensee", "2019-04-01");
            await kit.AssignAsync(swich, pb, null);

            var (driver, _, _) = await _f.DriverClientAsync("yp_bt1_004");
            var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
            var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
            var asDirect = await CashOutAsync(driver, cardId, 9m);
            await kit.AssignAsync(swich, pb, lic);
            var underLicensee = await CashOutAsync(driver, cardId, 10m);
            Assert.Equal((true, (Guid?)null), (asDirect.FeeRegimeStamped, asDirect.FeeLicenseeId));
            Assert.Equal((true, (Guid?)lic), (underLicensee.FeeRegimeStamped, underLicensee.FeeLicenseeId));
            await _f.DbAsync(async db =>
            {
                foreach (var (id, day) in new[] { (asDirect.Id, 7), (underLicensee.Id, 21) })
                {
                    var c = await db.Cashouts.FirstAsync(x => x.Id == id);
                    c.CompletedAt = Noon(2019, 4, day);
                    c.CreatedAt = c.CompletedAt.Value.AddMinutes(-1);
                }
                return await db.SaveChangesAsync();
            });

            // Before the run, Swich's month view already shows both sides: the licensee park still has a direct row to
            // settle (its fee from before it joined), and the statement only what it earned under the agreement.
            var view = await swich.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2019-04");
            var preview = view.GetProperty("direct").EnumerateArray().Single(d => d.GetProperty("parkId").GetGuid() == pb);
            Assert.True(preview.GetProperty("isPreview").GetBoolean());
            Assert.Equal(asDirect.Fee, D(preview, "feeTotal"));
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("blockedReason").ValueKind);
            var statementPreview = view.GetProperty("statements").EnumerateArray().Single(s => s.GetProperty("licenseeId").GetGuid() == lic);
            Assert.Equal(underLicensee.Fee, D(statementPreview, "feeIncome"));

            // A licensee park is not refused a direct run while it has direct fees to settle.
            var direct = (await RunAsync(swich, "2019-04", parkId: pb)).GetProperty("direct").EnumerateArray().Single();
            Assert.Equal(asDirect.Fee, D(direct, "feeTotal"));
            Assert.Equal(underLicensee.Fee, D(OnlyStatement(await RunAsync(swich, "2019-04", licenseeId: lic)), "feeIncome"));

            // Once those are settled it is a licensee park like any other: a direct run for it is refused again.
            var refused = await RunAsync(swich, "2019-05", parkId: pb, expect: HttpStatusCode.Conflict);
            Assert.Equal("park_has_licensee", ApiFixture.Code(refused));
        }
        finally { await kit.ReturnAsync(); }
    }
}
