using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Services;
using Xunit;
using static PayTaxi.Tests.Integration.SettlementKit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Annex 3 §7: a park may revoke the §2 (payouts from its account) and §3 (the monthly transfer of Swich's share)
/// authorisations with at least 30 days' written notice; obligations that arose before stay. From the revocation day
/// on the platform initiates nothing from the park's account: no new cashout, no waiting payout is sent (a person
/// cancels it — the money goes back to the driver's Yandex balance), and no settlement transfer — the share is owed by
/// the licensee under §2.5 and closed as paid outside the platform.
///
/// Uses Tbilisi #5 and Batumi #1 (never #3, the flaky-bank collection's), and clears the revocation in every finally.
/// A revocation in effect is written straight to the database: the API only records one ahead of time.
/// </summary>
[Collection("api")]
public class ParkAuthorisationTests
{
    private const string Slug = "tbilisi-auto-park-5";
    private readonly ApiFixture _f;
    public ParkAuthorisationTests(ApiFixture f) => _f = f;

    private static DateOnly Today => SettlementService.TodayLocal();
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private MockBankPayoutProvider Mock => _f.Services.GetRequiredService<MockBankPayoutProvider>();
    private MockBankPayoutOptions MockOpts => _f.Services.GetRequiredService<IOptions<MockBankPayoutOptions>>().Value;
    private CashoutOptions SagaOpts => _f.Services.GetRequiredService<IOptions<CashoutOptions>>().Value;

    private static IEnumerable<JsonElement> Alerts(JsonElement body) => body.GetProperty("alerts").EnumerateArray();
    private static bool IsPark(JsonElement a, Guid parkId) =>
        a.GetProperty("parkId").ValueKind != JsonValueKind.Null && a.GetProperty("parkId").GetGuid() == parkId;

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == slug)).Id);

    /// <summary>A revocation as it stands once its day has come (notice 30 days before).</summary>
    private Task RevokeInDbAsync(Guid parkId, DateOnly from) => _f.DbAsync(db => db.Parks.Where(p => p.Id == parkId)
        .ExecuteUpdateAsync(s => s
            .SetProperty(p => p.AuthorisationNoticeOn, (DateOnly?)from.AddDays(-30))
            .SetProperty(p => p.AuthorisationRevokedFrom, (DateOnly?)from)
            .SetProperty(p => p.AuthorisationRevocationReason, "test: written notice")
            .SetProperty(p => p.AuthorisationRevocationRecordedBy, "admin:test")
            .SetProperty(p => p.AuthorisationRevocationRecordedAt, (DateTime?)DateTime.UtcNow)));

    private Task ClearRevocationAsync(Guid parkId) => _f.DbAsync(db => db.Parks.Where(p => p.Id == parkId)
        .ExecuteUpdateAsync(s => s
            .SetProperty(p => p.AuthorisationNoticeOn, (DateOnly?)null)
            .SetProperty(p => p.AuthorisationRevokedFrom, (DateOnly?)null)
            .SetProperty(p => p.AuthorisationRevocationReason, (string?)null)
            .SetProperty(p => p.AuthorisationRevocationRecordedBy, (string?)null)
            .SetProperty(p => p.AuthorisationRevocationRecordedAt, (DateTime?)null)));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient c, HttpMethod method, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        var res = await c.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<JsonElement> ParkOfAsync(HttpClient c, Guid parkId) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/admin/parks"))!.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == parkId);

    // ═══════════════════════════════════════════════════════════════════
    // Recording and withdrawing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_revocation_needs_the_notice_period_and_a_reason_and_is_withdrawn_only_before_it_takes_effect()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(Slug);
        var parkId = await ParkIdAsync(Slug);
        var url = $"/api/admin/parks/{parkId}/authorisation-revocation";
        try
        {
            // The park writes to the licensee and Swich; one of them records it — not the park's own manager.
            var byManager = await SendAsync(manager, HttpMethod.Put, url,
                new { noticeOn = Iso(Today), revokedFrom = Iso(Today.AddDays(30)), reason = "Letter" });
            Assert.Equal(HttpStatusCode.Forbidden, byManager.Status);

            // At least 30 days after the notice — the earliest day is named.
            var early = await SendAsync(swich, HttpMethod.Put, url,
                new { noticeOn = Iso(Today), revokedFrom = Iso(Today.AddDays(29)), reason = "Letter" });
            Assert.Equal(HttpStatusCode.BadRequest, early.Status);
            Assert.Equal("revocation_too_early", ApiFixture.Code(early.Body));
            Assert.Equal(Iso(Today.AddDays(30)), early.Body.GetProperty("earliest").GetString());
            // A notice not yet received, a day already gone, no reason: refused.
            var future = await SendAsync(swich, HttpMethod.Put, url,
                new { noticeOn = Iso(Today.AddDays(1)), revokedFrom = Iso(Today.AddDays(40)), reason = "Letter" });
            Assert.Equal("invalid_notice_date", ApiFixture.Code(future.Body));
            var past = await SendAsync(swich, HttpMethod.Put, url,
                new { noticeOn = Iso(Today.AddDays(-60)), revokedFrom = Iso(Today.AddDays(-1)), reason = "Letter" });
            Assert.Equal(HttpStatusCode.BadRequest, past.Status);
            Assert.Equal("revocation_in_past", ApiFixture.Code(past.Body));
            var noReason = await SendAsync(swich, HttpMethod.Put, url,
                new { noticeOn = Iso(Today), revokedFrom = Iso(Today.AddDays(30)), reason = "  " });
            Assert.Equal("reason_required", ApiFixture.Code(noReason.Body));
            Assert.Null(await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Id == parkId).Select(p => p.AuthorisationRevokedFrom).FirstAsync()));

            // The operator records it: from the 27th day after a notice received three days ago.
            var from = Today.AddDays(27);
            var ok = await SendAsync(op, HttpMethod.Put, url,
                new { noticeOn = Iso(Today.AddDays(-3)), revokedFrom = Iso(from), reason = "Registered letter of the park, filed as R-17" });
            Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToString());
            var auth = ok.Body.GetProperty("authorisation");
            Assert.Equal(Iso(from), auth.GetProperty("revokedFrom").GetString());
            Assert.False(auth.GetProperty("inEffect").GetBoolean());
            Assert.Equal("admin:levan@operator.local", auth.GetProperty("recordedBy").GetString());

            // One at a time: a second record is refused (withdraw first).
            var again = await SendAsync(swich, HttpMethod.Put, url,
                new { noticeOn = Iso(Today), revokedFrom = Iso(Today.AddDays(31)), reason = "Second letter" });
            Assert.Equal(HttpStatusCode.Conflict, again.Status);
            Assert.Equal("revocation_already_recorded", ApiFixture.Code(again.Body));

            // Everyone who sees the park sees it on the park; who recorded it is Swich's and the operator's business.
            var asSwich = await ParkOfAsync(swich, parkId);
            Assert.Equal(Iso(from), asSwich.GetProperty("authorisationRevokedFrom").GetString());
            Assert.Equal(Iso(Today.AddDays(-3)), asSwich.GetProperty("authorisationNoticeOn").GetString());
            Assert.False(asSwich.GetProperty("authorisationRevoked").GetBoolean());
            Assert.Equal("admin:levan@operator.local", asSwich.GetProperty("authorisationRevocationRecordedBy").GetString());
            var asManager = await ParkOfAsync(manager, parkId);
            Assert.Equal(Iso(from), asManager.GetProperty("authorisationRevokedFrom").GetString());
            Assert.Equal("Registered letter of the park, filed as R-17", asManager.GetProperty("authorisationRevocationReason").GetString());
            Assert.Equal(JsonValueKind.Null, asManager.GetProperty("authorisationRevocationRecordedBy").ValueKind);

            // Before it takes effect it can be withdrawn — not by the park's manager.
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Delete, url)).Status);
            var withdrawn = await SendAsync(swich, HttpMethod.Delete, url, new { reason = "The park changed its mind" });
            Assert.True(withdrawn.Status == HttpStatusCode.OK, withdrawn.Body.ToString());
            Assert.Equal(JsonValueKind.Null, withdrawn.Body.GetProperty("authorisation").ValueKind);
            Assert.Equal(JsonValueKind.Null, (await ParkOfAsync(swich, parkId)).GetProperty("authorisationRevokedFrom").ValueKind);
            var nothing = await SendAsync(swich, HttpMethod.Delete, url);
            Assert.Equal(HttpStatusCode.Conflict, nothing.Status);
            Assert.Equal("no_revocation", ApiFixture.Code(nothing.Body));

            // From its day on it stands: neither withdrawn nor replaced.
            await RevokeInDbAsync(parkId, Today);
            Assert.True((await ParkOfAsync(swich, parkId)).GetProperty("authorisationRevoked").GetBoolean());
            var late = await SendAsync(swich, HttpMethod.Delete, url, new { reason = "Too late" });
            Assert.Equal(HttpStatusCode.Conflict, late.Status);
            Assert.Equal("revocation_in_effect", ApiFixture.Code(late.Body));
            Assert.Equal(Today, await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Id == parkId).Select(p => p.AuthorisationRevokedFrom).FirstAsync()));
        }
        finally { await ClearRevocationAsync(parkId); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Payouts (Annex 3 §2)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_revoked_park_takes_no_new_cashout_and_sends_no_waiting_payout_which_a_person_cancels()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var manager = await _f.ParkAdminAsync(Slug);
        var (parkId, accountId) = await _f.DbAsync(async db =>
        {
            var park = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == Slug);
            var account = await db.ParkBankAccounts.AsNoTracking()
                .Where(a => a.ParkId == park.Id && a.BankCode == "TB" && a.IsActive).OrderByDescending(a => a.IsPrimary).FirstAsync();
            return (park.Id, account.Id);
        });
        var backoff = SagaOpts.BackoffSeconds;
        Guid? waitingId = null;
        try
        {
            // A payout waits for a top-up (Annex 3 §5) — then the park's revocation takes effect.
            MockOpts.EnforceBalance = true;
            Mock.SetBalance(accountId, 0m);
            SagaOpts.BackoffSeconds = new[] { 600 };
            var (waitingDriver, _, _) = await _f.DriverClientAsync("yp_tb5_005");
            var card = (await waitingDriver.GetFromJsonAsync<JsonElement>("/api/driver/me"))
                .GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
            var queued = await waitingDriver.PostAsJsonAsync("/api/driver/cashouts", new { cardId = card, amount = 6m, idempotencyKey = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            waitingId = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cashoutId").GetGuid();
            var before = await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == waitingId));

            await RevokeInDbAsync(parkId, Today);

            // No new cashout: refused before anything moves, with a code the driver app translates.
            var (driver, driverId, _) = await _f.DriverClientAsync("yp_tb5_006");
            var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
            Assert.True(me.GetProperty("park").GetProperty("cashoutsPaused").GetBoolean());
            Assert.Equal("authorisation_revoked", me.GetProperty("park").GetProperty("cashoutsPausedReason").GetString());
            var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
            var key = Guid.NewGuid().ToString();
            var refused = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 6m, idempotencyKey = key });
            var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("cashout_rejected", ApiFixture.Code(refusedBody));
            Assert.Equal("authorisation_revoked", refusedBody.GetProperty("code").GetString());
            Assert.False(await _f.DbAsync(db => db.Cashouts.AnyAsync(c => c.IdempotencyKey == key)));

            // The account is topped up — without the revocation the waiting payout would go out now. It does not: it
            // waits with the revocation's code, nothing is sent to the bank, nothing is given back on its own.
            Mock.SetBalance(accountId, 25_000m);
            await _f.DbAsync(async db =>
            {
                var c = await db.Cashouts.FirstAsync(x => x.Id == waitingId);
                c.NextAttemptAt = DateTime.UtcNow;
                c.CreatedAt = DateTime.UtcNow.AddMinutes(-20);   // past the console's "waiting" threshold
                return await db.SaveChangesAsync();
            });
            var row = await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == waitingId));
            for (var i = 0; i < 60 && !(row.FailureReason ?? "").StartsWith("AUTHORISATION_REVOKED:"); i++)
            {
                await Task.Delay(250);
                row = await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == waitingId));
            }
            Assert.StartsWith("AUTHORISATION_REVOKED:", row.FailureReason);
            Assert.Equal(CashoutStatus.Queued, row.Status);
            Assert.Null(row.BankTransferId);
            Assert.Null(row.YandexReversalTransactionId);
            Assert.Equal(before.AttemptCount, row.AttemptCount);                      // no attempt was made at the bank
            Assert.NotNull(row.NextAttemptAt);
            Assert.False(await _f.DbAsync(db => db.LedgerEntries.AnyAsync(l => l.CashoutId == waitingId && l.EntryType == LedgerEntryType.BankTransferSent)));
            var docKey = row.BankDocumentSeq == 0 ? row.IdempotencyKey : $"{row.IdempotencyKey}#{row.BankDocumentSeq}";
            Assert.Null(await Mock.FindTransferByIdempotencyKeyAsync(default!, docKey));

            // Swich and the operator get one line for the revoked park (its waiting payouts), instead of "payouts waiting";
            // the park's manager keeps the waiting line, with the revocation as its cause.
            var swichAlerts = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var revoked = Alerts(swichAlerts).Single(a => a.GetProperty("kind").GetString() == "authorisation_revoked" && IsPark(a, parkId));
            Assert.Equal("danger", revoked.GetProperty("severity").GetString());
            Assert.Equal(1, revoked.GetProperty("revocation").GetProperty("payouts").GetInt32());
            Assert.Equal(5.50m, revoked.GetProperty("revocation").GetProperty("payoutsNet").GetDecimal());
            Assert.Equal(Iso(Today), revoked.GetProperty("revocation").GetProperty("revokedFrom").GetString());
            Assert.DoesNotContain(Alerts(swichAlerts), a => a.GetProperty("kind").GetString() == "payout_waiting" && IsPark(a, parkId));
            Assert.Contains(Alerts(await op.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "authorisation_revoked" && IsPark(a, parkId));
            var managerAlerts = await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            Assert.DoesNotContain(Alerts(managerAlerts), a => a.GetProperty("kind").GetString() == "authorisation_revoked");
            Assert.Equal("AUTHORISATION_REVOKED", Alerts(managerAlerts).Single(a => a.GetProperty("kind").GetString() == "payout_waiting")
                .GetProperty("code").GetString());

            // The way out is a person's: cancel — the debit goes back to the driver's Yandex balance.
            var cancel = await op.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts/{waitingId}/cancel",
                new { reason = "The park revoked the platform's authorisation" });
            Assert.True(cancel.StatusCode == HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
            var cancelled = await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == waitingId));
            Assert.Equal(CashoutStatus.Failed, cancelled.Status);
            Assert.NotNull(cancelled.YandexReversalTransactionId);
            Assert.DoesNotContain(Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "authorisation_revoked" && IsPark(a, parkId));
        }
        finally
        {
            await ClearRevocationAsync(parkId);
            MockOpts.EnforceBalance = false;
            Mock.SetBalance(accountId, 25_000m);
            SagaOpts.BackoffSeconds = backoff;
            if (waitingId is { } id)
                await _f.DbAsync(async db =>
                {
                    var c = await db.Cashouts.FirstAsync(x => x.Id == id);
                    if (c.Status is CashoutStatus.Queued or CashoutStatus.Processing or CashoutStatus.ReviewRequired)
                    { c.Status = CashoutStatus.Failed; c.NextAttemptAt = null; c.FailureReason = "TEST_CLEANUP"; }
                    return await db.SaveChangesAsync();
                });
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Settlement (Annex 3 §3, §2.5)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_revoked_park_s_share_is_never_collected_from_its_account_and_is_closed_as_paid_by_the_licensee()
    {
        var swich = await _f.SuperAdminAsync();
        var op = await _f.OperatorAsync();
        var kit = new SettlementKit(_f);
        var pb = await kit.ParkIdAsync(PB);
        var p5 = await kit.ParkIdAsync(P5);
        try
        {
            var lic = await kit.CreateLicenseeAsync(swich, "Revoked park licensee", "2019-01-01");
            await kit.AssignAsync(swich, pb, lic);
            await kit.AssignAsync(swich, p5, null);
            await kit.PlantAsync(pb, 4, 0.50m, Noon(2019, 1, 10));
            await kit.PlantAsync(p5, 2, 0.50m, Noon(2019, 1, 11));
            await RevokeInDbAsync(pb, Today);
            await RevokeInDbAsync(p5, Today);

            // The statement is computed — the fees were earned before the revocation and stay owed — but nothing is
            // initiated from the park's account: the row fails with the revocation's code at once.
            var st = OnlyStatement(await RunAsync(swich, "2019-01", licenseeId: lic));
            var line = ParkLine(st, pb);
            Assert.Equal("Failed", S(line, "status"));
            Assert.Equal("AUTHORISATION_REVOKED", S(line, "failureCode"));
            var rowId = line.GetProperty("settlementId").GetGuid();
            var row = await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId));
            Assert.Equal(SettlementStatus.Failed, row.Status);
            Assert.Equal(2.00m, row.FeeTotal);
            Assert.True(row.SwichShare > 0m);
            Assert.Null(await Mock.FindTransferByIdempotencyKeyAsync(default!, row.IdempotencyKey));
            Assert.False(await _f.DbAsync(db => db.LedgerEntries.AnyAsync(l => l.SettlementId == rowId && l.EntryType == LedgerEntryType.SettlementSent)));

            // A Swich-direct park's row the same way (the park itself owes it then).
            var direct = (await RunAsync(swich, "2019-01", parkId: p5)).GetProperty("direct").EnumerateArray().Single();
            Assert.Equal("Failed", S(direct, "status"));
            Assert.StartsWith("AUTHORISATION_REVOKED:", S(direct, "failureReason"));
            var directId = direct.GetProperty("id").GetGuid();

            // Never retried: not by the nightly tick (which retries every other failure), not by the Retry button.
            int Failures(List<LedgerEntry> l) => l.Count(x => x.EntryType == LedgerEntryType.SettlementFailed);
            var ledgerBefore = await _f.DbAsync(db => db.LedgerEntries.AsNoTracking().Where(l => l.SettlementId == rowId).ToListAsync());
            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ISettlementService>().RunDueAsync(new DateOnly(2019, 2, 2), "worker");
            var afterTick = await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId));
            Assert.Equal((row.AttemptCount, row.LastAttemptAt, SettlementStatus.Failed), (afterTick.AttemptCount, afterTick.LastAttemptAt, afterTick.Status));
            Assert.Equal(Failures(ledgerBefore), Failures(await _f.DbAsync(db => db.LedgerEntries.AsNoTracking().Where(l => l.SettlementId == rowId).ToListAsync())));
            foreach (var id in new[] { rowId, directId })
            {
                var retry = await swich.PostAsync($"/api/admin/settlements/{id}/retry", null);
                Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
                Assert.Equal("authorisation_revoked", ApiFixture.Code(await retry.Content.ReadFromJsonAsync<JsonElement>()));
            }
            Assert.Equal(row.AttemptCount, (await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId))).AttemptCount);

            // Swich and the operator see what is owed on the revoked park's line — not as a "failed transfer" to retry;
            // the park's manager is not shown a transfer it has withdrawn the platform's authority for.
            var swichAlerts = await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var revoked = Alerts(swichAlerts).Single(a => a.GetProperty("kind").GetString() == "authorisation_revoked" && IsPark(a, pb));
            Assert.Equal(1, revoked.GetProperty("revocation").GetProperty("settlements").GetInt32());
            Assert.Equal(row.SwichShare, revoked.GetProperty("revocation").GetProperty("owed").GetDecimal());
            Assert.Equal(row.SwichShare, revoked.GetProperty("revocation").GetProperty("licenseeOwed").GetDecimal());
            Assert.DoesNotContain(Alerts(swichAlerts), a => a.GetProperty("kind").GetString() == "settlement_failed" && (IsPark(a, pb) || IsPark(a, p5)));
            // The operator of the park's licensee — while that licensee is in force (it reaches no park otherwise).
            await kit.InForceAsync(lic);
            Assert.Contains(Alerts(await op.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "authorisation_revoked" && IsPark(a, pb));
            var manager = await _f.ParkAdminAsync(PB);
            Assert.DoesNotContain(Alerts(await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "settlement_failed");

            // §2.5: the licensee pays it — recorded as paid outside the platform, which closes the row and the line.
            var paid = await swich.PostAsJsonAsync($"/api/admin/settlements/{rowId}/settle-externally",
                new { payer = "licensee", amount = row.SwichShare, paidOn = Iso(Today), reference = "LIC-PAY-2019-01" });
            Assert.True(paid.StatusCode == HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
            Assert.Equal(SettlementStatus.SettledExternally, (await _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == rowId))).Status);
            var byPark = await swich.PostAsJsonAsync($"/api/admin/settlements/{directId}/settle-externally",
                new { payer = "park", amount = D(direct, "swichShare"), paidOn = Iso(Today), reference = "P5-PAY-2019-01" });
            Assert.True(byPark.StatusCode == HttpStatusCode.OK, await byPark.Content.ReadAsStringAsync());
            Assert.DoesNotContain(Alerts(await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")),
                a => a.GetProperty("kind").GetString() == "authorisation_revoked" && (IsPark(a, pb) || IsPark(a, p5)));
        }
        finally
        {
            await ClearRevocationAsync(pb);
            await ClearRevocationAsync(p5);
            await kit.ReturnAsync();
        }
    }
}
