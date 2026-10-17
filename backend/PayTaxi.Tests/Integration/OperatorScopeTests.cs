using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The operator role is the staff of ONE licensee: every licensee, statement and shared-cost endpoint shows and changes
/// that licensee only (an account bound to none sees none), and its parks are that licensee's (ParkScopeTests). A second
/// licensee in force is still refused: the contract calendar and the incident metrics assume one.
/// </summary>
[Collection("api")]
public class OperatorScopeTests
{
    private readonly ApiFixture _f;
    public OperatorScopeTests(ApiFixture f) => _f = f;

    private Task<Guid> ActiveLicenseeAsync() =>
        _f.DbAsync(db => db.Licensees.AsNoTracking().Where(l => l.IsActive).Select(l => l.Id).SingleAsync());

    [Fact]
    public async Task The_seeded_operator_works_for_the_licensee_in_force()
    {
        var active = await ActiveLicenseeAsync();
        var bound = await _f.DbAsync(db => db.AdminUsers.AsNoTracking()
            .Where(a => a.Email == SettlementKit.OperatorEmail).Select(a => a.LicenseeId).FirstAsync());
        Assert.Equal(active, bound);
    }

    [Fact]
    public async Task An_operator_sees_and_changes_only_its_own_licensee()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var own = await ActiveLicenseeAsync();
        Guid? statementId = null, unboundId = null;
        try
        {
            var created = await swich.PostAsJsonAsync("/api/admin/licensees", new { name = "Another licensee", isActive = false });
            var otherBody = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(created.StatusCode == HttpStatusCode.OK, otherBody.ToString());
            var other = otherBody.GetProperty("id").GetGuid();
            statementId = await _f.DbAsync(async db =>
            {
                var month = new DateOnly(2018, 1, 1);
                var (from, to) = SettlementService.MonthBoundsUtcFor(month, null);
                var st = new MonthlyStatement
                {
                    LicenseeId = other, Month = month, PeriodFromUtc = from, PeriodToUtc = to, BarrierGel = 20_000m,
                    SwichSharePercent = 50m, Phase = StatementPhase.Barrier, BarrierFraction = 1m,
                    Status = MonthlyStatementStatus.Completed, InvoiceRef = SettlementService.InvoiceRefFor(month), InitiatedBy = "test",
                };
                db.MonthlyStatements.Add(st);
                await db.SaveChangesAsync();
                return st.Id;
            });

            // Its own licensee: everything as before.
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/admin/licensees/{own}")).StatusCode);
            var list = await op.GetFromJsonAsync<JsonElement>("/api/admin/licensees");
            Assert.Equal(new[] { own }, list.GetProperty("licensees").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToArray());
            var month = await op.GetFromJsonAsync<JsonElement>("/api/admin/settlements/month?month=2018-01");
            Assert.Equal(new[] { own }, month.GetProperty("statements").EnumerateArray().Select(s => s.GetProperty("licenseeId").GetGuid()).ToArray());
            var stored = await op.GetFromJsonAsync<JsonElement>("/api/admin/settlements/statements");
            Assert.DoesNotContain(stored.GetProperty("statements").EnumerateArray(), s => s.GetProperty("licenseeId").GetGuid() == other);

            // Another licensee's: refused everywhere — reading, and above all changing what its statement is computed from.
            foreach (var url in new[]
            {
                $"/api/admin/licensees/{other}", $"/api/admin/licensees/{other}/events", $"/api/admin/licensees/{other}/costs",
                $"/api/admin/settlements/statements?licenseeId={other}", $"/api/admin/settlements/statements/preview?licenseeId={other}",
                $"/api/admin/settlements/statements/{statementId}", $"/api/admin/settlements/statements/{statementId}/report.pdf",
            })
                Assert.True((await op.GetAsync(url)).StatusCode == HttpStatusCode.Forbidden, url);
            var cost = await op.PostAsJsonAsync($"/api/admin/licensees/{other}/costs",
                new { category = "Sms", vendor = "Cross-tenant SMS", invoiceNumber = "XT-1", invoiceDate = "2018-01-10", amount = 2_000m });
            Assert.Equal(HttpStatusCode.Forbidden, cost.StatusCode);
            Assert.False(await _f.DbAsync(db => db.CommonCosts.AnyAsync(c => c.LicenseeId == other)));

            // Swich sees both.
            var swichList = await swich.GetFromJsonAsync<JsonElement>("/api/admin/licensees");
            Assert.Contains(swichList.GetProperty("licensees").EnumerateArray(), l => l.GetProperty("id").GetGuid() == other);
            Assert.Equal(HttpStatusCode.OK, (await swich.GetAsync($"/api/admin/settlements/statements/{statementId}")).StatusCode);

            // An operator account bound to no licensee sees none of it (fail closed): it cannot even sign in.
            unboundId = await _f.DbAsync(async db =>
            {
                var u = new AdminUser
                {
                    Email = $"unbound-{Guid.NewGuid():N}@operator.local", Name = "Unbound operator",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("operator9!"), Role = "operator", IsActive = true,
                };
                db.AdminUsers.Add(u);
                await db.SaveChangesAsync();
                return u.Id;
            });
            var unboundEmail = await _f.DbAsync(db => db.AdminUsers.AsNoTracking().Where(a => a.Id == unboundId).Select(a => a.Email).FirstAsync());
            var unbound = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email = unboundEmail, password = "operator9!" });
            Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);
            Assert.Equal("licensee_not_in_force", ApiFixture.Code(await unbound.Content.ReadFromJsonAsync<JsonElement>()));

            // …and not silently: operator accounts are created by hand (docs/DEPLOY.md), so every start names the ones bound
            // to no licensee, in the error log, instead of leaving an operator to wonder why it cannot sign in.
            var reported = await _f.DbAsync(db => SeedData.ReportUnboundOperatorsAsync(db, NullLogger.Instance));
            Assert.Contains(unboundEmail, reported);
            Assert.DoesNotContain(SettlementKit.OperatorEmail, reported);
        }
        finally
        {
            if (statementId is { } sid) await _f.DbAsync(db => db.MonthlyStatements.Where(m => m.Id == sid).ExecuteDeleteAsync());
            if (unboundId is { } uid) await _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == uid).ExecuteDeleteAsync());
        }
    }

    /// <summary>
    /// A park's settlement rows are its operator's to read (a park that came from another licensee brings its old rows), but
    /// the licence agreement's part of a row — the barrier split, the §4.1.2 penalty, the record of a payment outside the platform —
    /// only for rows of the operator's own licensee's statements. A row of another (former) licensee's statement reads as
    /// the park's manager reads it, in the list and in the park's months alike.
    /// </summary>
    [Fact]
    public async Task Another_licensees_park_rows_show_an_operator_none_of_the_agreements_figures()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var own = await ActiveLicenseeAsync();
        var batumi = await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == "batumi-auto-park-1").Select(p => p.Id).FirstAsync());
        var created = await swich.PostAsJsonAsync("/api/admin/licensees", new { name = "Former licensee", isActive = false });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var former = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var statements = new List<Guid>();
        var rows = new Dictionary<string, Guid>();
        try
        {
            // One closed month of the former licensee, one of the operator's own: each with Batumi's part, paid outside the
            // platform three weeks late (so the row carries the §4.1.2 figures and the external record).
            foreach (var (key, licenseeId, month) in new[] { ("former", former, new DateOnly(2018, 3, 1)), ("own", own, new DateOnly(2018, 5, 1)) })
            {
                var ids = await _f.DbAsync(async db =>
                {
                    var (from, to) = SettlementService.MonthBoundsUtcFor(month, null);
                    var st = new MonthlyStatement
                    {
                        LicenseeId = licenseeId, Month = month, PeriodFromUtc = from, PeriodToUtc = to, BarrierGel = 20_000m,
                        SwichSharePercent = 50m, Phase = StatementPhase.Barrier, BarrierFraction = 1m,
                        Status = MonthlyStatementStatus.Completed, InvoiceRef = SettlementService.InvoiceRefFor(month), InitiatedBy = "test",
                    };
                    var row = new Settlement
                    {
                        ParkId = batumi, SettlementDate = month, IsMonthly = true, MonthlyStatement = st, PeriodFromUtc = from, PeriodToUtc = to,
                        CashoutCount = 6, FeeTotal = 3m, Phase1Fees = 3m, Phase2Fees = 0m, SwichShare = 3m, ParkShare = 0m,
                        Status = SettlementStatus.SettledExternally, IdempotencyKey = "op-scope:" + Guid.NewGuid().ToString("N"),
                        Description = $"PayTaxi settlement {month:yyyy-MM}", InvoiceRef = SettlementService.InvoiceRefFor(month),
                        ExternalPayer = "licensee", ExternalPaidOn = month.AddMonths(1).AddDays(25), ExternalReference = $"REF-{key}",
                        ExternalNote = $"Paid by the {key} licensee", ExternalRecordedBy = "admin:test", ExternalRecordedAt = DateTime.UtcNow,
                        InitiatedBy = "test",
                    };
                    db.MonthlyStatements.Add(st);
                    db.Settlements.Add(row);
                    await db.SaveChangesAsync();
                    return (st.Id, row.Id);
                });
                statements.Add(ids.Item1);
                rows[key] = ids.Item2;
            }

            static JsonElement RowOf(JsonElement list, Guid id) =>
                list.GetProperty("settlements").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
            static JsonElement MonthOf(JsonElement body, string month) =>
                body.GetProperty("months").EnumerateArray().Single(m => m.GetProperty("month").GetString() == month);
            static bool SeesAgreement(JsonElement row) =>
                row.GetProperty("external").TryGetProperty("reference", out _) && row.GetProperty("overdue").TryGetProperty("accrued", out _);
            var listUrl = $"/api/admin/settlements?parkId={batumi}&status=SettledExternally&take=365";
            var monthsUrl = $"/api/admin/parks/{batumi}/settlements/months";

            // Swich: the whole row of both.
            var swichList = await swich.GetFromJsonAsync<JsonElement>(listUrl);
            var swichMonths = await swich.GetFromJsonAsync<JsonElement>(monthsUrl);
            foreach (var (key, month) in new[] { ("former", "2018-03"), ("own", "2018-05") })
            {
                Assert.True(SeesAgreement(RowOf(swichList, rows[key])), key);
                Assert.Equal(3m, RowOf(swichList, rows[key]).GetProperty("phase1Fees").GetDecimal());
                Assert.True(SeesAgreement(MonthOf(swichMonths, month)), key);
            }

            // The operator: its own licensee's row as Swich sees it…
            var opList = await op.GetFromJsonAsync<JsonElement>(listUrl);
            var opMonths = await op.GetFromJsonAsync<JsonElement>(monthsUrl);
            Assert.True(SeesAgreement(RowOf(opList, rows["own"])));
            Assert.Equal(3m, RowOf(opList, rows["own"]).GetProperty("phase1Fees").GetDecimal());
            Assert.True(SeesAgreement(MonthOf(opMonths, "2018-05")));

            // …the former licensee's as the park's manager does: the park's figures, nothing of that agreement.
            var formerRow = RowOf(opList, rows["former"]);
            Assert.Equal(3m, formerRow.GetProperty("swichShare").GetDecimal());
            Assert.Equal("licensee", formerRow.GetProperty("external").GetProperty("payer").GetString());
            Assert.Equal(JsonValueKind.Null, formerRow.GetProperty("phase1Fees").ValueKind);
            Assert.False(formerRow.GetProperty("external").TryGetProperty("reference", out _));
            Assert.False(formerRow.GetProperty("overdue").TryGetProperty("accrued", out _));
            Assert.DoesNotContain("REF-former", formerRow.ToString());
            var formerMonth = MonthOf(opMonths, "2018-03");
            Assert.False(formerMonth.GetProperty("external").TryGetProperty("reference", out _));
            Assert.False(formerMonth.GetProperty("overdue").TryGetProperty("accrued", out _));
            Assert.DoesNotContain("REF-former", formerMonth.ToString());
        }
        finally
        {
            await _f.DbAsync(db => db.Settlements.Where(s => rows.Values.Contains(s.Id)).ExecuteDeleteAsync());
            await _f.DbAsync(db => db.MonthlyStatements.Where(m => statements.Contains(m.Id)).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task A_second_licensee_in_force_is_refused_until_parks_are_scoped_by_licensee()
    {
        var swich = await _f.SuperAdminAsync();
        var active = await ActiveLicenseeAsync();

        // Registered in force next to the one there is: refused, nothing stored.
        var name = $"Second in force {Guid.NewGuid():N}"[..30];
        var res = await swich.PostAsJsonAsync("/api/admin/licensees", new { name });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("second_active_licensee", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.False(await _f.DbAsync(db => db.Licensees.AnyAsync(l => l.Name == name)));

        // Registered inactive it is fine; activating it is refused the same way.
        var inactive = await swich.PostAsJsonAsync("/api/admin/licensees", new { name, isActive = false });
        Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        var id = (await inactive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var activate = await swich.PatchAsJsonAsync($"/api/admin/licensees/{id}", new { isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);
        Assert.Equal("second_active_licensee", ApiFixture.Code(await activate.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.False(await _f.DbAsync(db => db.Licensees.AsNoTracking().Where(l => l.Id == id).Select(l => l.IsActive).FirstAsync()));
        Assert.Equal(active, await ActiveLicenseeAsync());

        // Editing the one in force (it stays the only one) is unaffected.
        var edit = await swich.PatchAsJsonAsync($"/api/admin/licensees/{active}", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }
}
