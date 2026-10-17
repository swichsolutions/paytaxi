using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Services;
using Xunit;
using static PayTaxi.Tests.Integration.SettlementKit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// No test rails on the live server: with real rails (<see cref="ApiFixture.RealRails"/>) the API refuses provider "mock",
/// banks it has no adapter for and credentials for a bank's test environment; an account whose provider has no adapter
/// pays nobody — a cashout waits, a settlement fails — instead of "completing" through the mock. Development (the main
/// host) keeps its mock rails as before. Batumi #1's and park #5's accounts are borrowed and handed back; Tbilisi #3 is
/// never touched (the flaky-bank collection pays its drivers).
/// </summary>
[Collection("api")]
public class RealRailsTests
{
    private const string Batumi = "batumi-auto-park-1";
    private readonly ApiFixture _f;
    public RealRailsTests(ApiFixture f) => _f = f;

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync());

    /// <summary>Batumi #1's manager on <paramref name="factory"/> (seed order: park1!, park2!, park3!).</summary>
    private static async Task<HttpClient> BatumiManagerAsync(WebApplicationFactory<Program> factory)
    {
        foreach (var pw in new[] { "park3!", "park2!", "park1!" })
        {
            try { return await ApiFixture.AdminClientAsync(factory, $"manager@{Batumi}.local", pw); }
            catch (HttpRequestException) { }
        }
        throw new InvalidOperationException("no Batumi manager login");
    }

    private Task<(Guid Id, string Provider, string? RawCredentials)> TbAccountAsync(Guid parkId) => _f.DbAsync(async db =>
    {
        var a = await db.ParkBankAccounts.AsNoTracking().Where(x => x.ParkId == parkId && x.BankCode == "TB" && x.IsActive)
            .OrderByDescending(x => x.IsPrimary).FirstAsync();
        var raw = await db.Database.SqlQueryRaw<string>("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", a.Id)
            .FirstOrDefaultAsync();
        return (a.Id, a.Provider, raw);
    });

    private Task RestoreAccountAsync(Guid accountId, string provider, string? rawCredentials) => _f.DbAsync(db =>
        db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"Provider\" = {0}, \"CredentialsEncrypted\" = {1} WHERE \"Id\" = {2}",
            provider, rawCredentials ?? "{}", accountId));

    private Task SetProviderAsync(Guid accountId, string provider) => _f.DbAsync(db =>
        db.ParkBankAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Provider, provider)));

    // ── The API with real rails ──────────────────────────────────────

    [Fact]
    public async Task With_real_rails_a_park_manager_cannot_point_its_payout_account_at_the_mock()
    {
        var parkId = await ParkIdAsync(Batumi);
        var account = await TbAccountAsync(parkId);
        try
        {
            var manager = await BatumiManagerAsync(_f.RealRails);
            var res = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}", new { provider = "mock" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_provider", ApiFixture.Code(body));
            Assert.Equal(new[] { "tbc", "bog" }, body.GetProperty("allowed").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(account.Provider, await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking().Where(a => a.Id == account.Id).Select(a => a.Provider).FirstAsync()));

            // The console is told what this server takes: no mock.
            var list = await manager.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/bank-accounts");
            Assert.Equal(new[] { "tbc", "bog" }, list.GetProperty("allowedProviders").EnumerateArray().Select(x => x.GetString()).ToArray());
        }
        finally { await RestoreAccountAsync(account.Id, account.Provider, account.RawCredentials); }
    }

    [Fact]
    public async Task With_real_rails_an_account_at_a_bank_without_an_adapter_is_refused_instead_of_defaulting_to_the_mock()
    {
        var parkId = await ParkIdAsync(Batumi);
        var liberty = Core.Banking.GeorgianIban.Build("LB", "0000000000004242");
        var manager = await BatumiManagerAsync(_f.RealRails);

        var res = await manager.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts", new { iban = liberty, holderName = "Batumi LLC" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("unsupported_bank", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
        // Naming a provider does not make a Liberty account payable through TBC's or BoG's adapter.
        var named = await manager.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts", new { iban = liberty, provider = "tbc" });
        Assert.Equal("unsupported_bank", ApiFixture.Code(await named.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.False(await _f.DbAsync(db => db.ParkBankAccounts.AnyAsync(a => a.ParkId == parkId && a.Iban == liberty)));
    }

    [Fact]
    public async Task With_real_rails_an_existing_account_cannot_be_named_for_another_bank_s_adapter_or_switched_on_with_the_mock()
    {
        // Accounts saved before the rule (a dry run's, a copied development database's): an edit is held to the same rule
        // as a new account wherever it would make the account pay — a provider named, or the account switched back on.
        var parkId = await ParkIdAsync(Batumi);
        var (liberty, mockTb) = await _f.DbAsync(async db =>
        {
            Core.Entities.ParkBankAccount Legacy(string bank, string iban, string label) => new()
            {
                ParkId = parkId, BankCode = bank, Provider = "mock", Iban = iban, HolderName = "Batumi LLC", Label = label,
                CredentialsEncrypted = "{}", IsActive = false, IsPrimary = false,
            };
            var lb = Legacy("LB", Core.Banking.GeorgianIban.Build("LB", "0000000000004245"), "Legacy Liberty");
            var tb = Legacy("TB", ApiFixture.TbcIban("7700000000004246"), "Legacy mock");
            db.ParkBankAccounts.AddRange(lb, tb);
            await db.SaveChangesAsync();
            return (lb.Id, tb.Id);
        });
        Task<(string Provider, bool IsActive)> StateAsync(Guid id) => _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.Id == id).Select(a => new ValueTuple<string, bool>(a.Provider, a.IsActive)).FirstAsync());
        try
        {
            var manager = await BatumiManagerAsync(_f.RealRails);
            string url(Guid id) => $"/api/admin/parks/{parkId}/bank-accounts/{id}";

            // A Liberty account named "tbc" would pass every rail check, yet TBC's adapter cannot debit it.
            var named = await manager.PatchAsJsonAsync(url(liberty), new { provider = "tbc" });
            Assert.Equal(HttpStatusCode.BadRequest, named.StatusCode);
            Assert.Equal("unsupported_bank", ApiFixture.Code(await named.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(("mock", false), await StateAsync(liberty));

            // A mock account switched back on would pay nobody — and the next start would refuse to run.
            var on = await manager.PatchAsJsonAsync(url(mockTb), new { isActive = true });
            Assert.Equal(HttpStatusCode.BadRequest, on.StatusCode);
            Assert.Equal("invalid_provider", ApiFixture.Code(await on.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(("mock", false), await StateAsync(mockTb));

            // What stays possible: naming the TBC account's own bank, and tidying a legacy account that stays off.
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync(url(mockTb), new { provider = "tbc" })).StatusCode);
            Assert.Equal(("tbc", false), await StateAsync(mockTb));
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync(url(liberty), new { label = "Old Liberty account" })).StatusCode);
        }
        finally { await _f.DbAsync(db => db.ParkBankAccounts.Where(a => a.Id == liberty || a.Id == mockTb).ExecuteDeleteAsync()); }
    }

    [Fact]
    public async Task With_real_rails_credentials_for_a_test_environment_are_refused_and_no_environment_means_production()
    {
        var parkId = await ParkIdAsync(Batumi);
        var account = await TbAccountAsync(parkId);
        try
        {
            var manager = await BatumiManagerAsync(_f.RealRails);
            foreach (var env in new[] { "test", "TEST", "sandbox", "" })
            {
                var creds = JsonSerializer.Serialize(new { username = "batumi-dbi", password = "pw", environment = env });
                var res = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}", new { credentialsJson = creds });
                Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"environment '{env}': {(int)res.StatusCode}");
                Assert.Equal("test_environment_not_allowed", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
            }
            var create = await manager.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts", new
            {
                iban = ApiFixture.TbcIban("7700000000004243"),
                credentialsJson = JsonSerializer.Serialize(new { username = "u", password = "p", environment = "test" }),
            });
            Assert.Equal("test_environment_not_allowed", ApiFixture.Code(await create.Content.ReadFromJsonAsync<JsonElement>()));

            // Production credentials — named, or with no environment at all — are what this server takes.
            foreach (var creds in new[] { """{"username":"batumi-dbi","password":"pw"}""", """{"username":"batumi-dbi","password":"pw","environment":"production"}""" })
            {
                var ok = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}", new { credentialsJson = creds });
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }
        }
        finally { await RestoreAccountAsync(account.Id, account.Provider, account.RawCredentials); }
    }

    [Fact]
    public async Task With_real_rails_a_park_cannot_be_created_on_the_mock_another_bank_or_sandbox_credentials()
    {
        var swich = await ApiFixture.AdminClientAsync(_f.RealRails, "ops@swich.dev", "swich2026!");
        object Park(string slug, string iban, string? provider = null, string? credentials = null) => new
        {
            name = "Rails " + slug, slug, yandexParkId = "yx_rails_" + slug, legalEntityName = "შპს რელსი", taxId = "405000111", phone = "+995555123000",
            bankAccountIban = iban, bankProvider = provider, bankCredentialsJson = credentials, direct = true,
        };
        var cases = new (object Body, string Code)[]
        {
            (Park("rails-mock", ApiFixture.TbcIban("7700000000004244"), provider: "mock"), "invalid_provider"),
            (Park("rails-lb", Core.Banking.GeorgianIban.Build("LB", "0000000000004245")), "unsupported_bank"),
            (Park("rails-test", ApiFixture.TbcIban("7700000000004246"), credentials: """{"username":"u","password":"p","environment":"test"}"""), "test_environment_not_allowed"),
        };
        foreach (var (body, code) in cases)
        {
            var res = await swich.PostAsJsonAsync("/api/admin/parks", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal(code, ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
        }
        Assert.False(await _f.DbAsync(db => db.Parks.AnyAsync(p => p.Slug.StartsWith("rails-"))));
    }

    [Fact]
    public async Task Development_keeps_the_mock_and_the_sandbox_as_before()
    {
        var parkId = await ParkIdAsync(Batumi);
        var account = await TbAccountAsync(parkId);
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var list = await manager.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/bank-accounts");
            Assert.Equal(new[] { "tbc", "bog", "mock" }, list.GetProperty("allowedProviders").EnumerateArray().Select(x => x.GetString()).ToArray());
            var res = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}", new
            {
                provider = "mock",
                credentialsJson = """{"username":"batumi-dbi","password":"pw","environment":"test"}""",
            });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        finally { await RestoreAccountAsync(account.Id, account.Provider, account.RawCredentials); }
    }

    // ── No adapter, no completion ────────────────────────────────────

    [Fact]
    public async Task A_cashout_through_an_account_whose_provider_has_no_adapter_waits_and_never_completes()
    {
        var p5 = await ParkIdAsync("tbilisi-auto-park-5");
        var account = await TbAccountAsync(p5);
        Guid? cashoutId = null;
        try
        {
            // What a mock account becomes on a real-rails server (no mock registered), or a typo: no adapter answers to it.
            await SetProviderAsync(account.Id, "xyz");
            var (driver, _, _) = await _f.DriverClientAsync("yp_tb5_008");
            var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
            var card = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("bankCode").GetString() == "TB").GetProperty("id").GetGuid();

            var res = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId = card, amount = 6m, idempotencyKey = Guid.NewGuid().ToString() });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(res.StatusCode == HttpStatusCode.Accepted, body.ToString());   // queued: on its way, not paid
            cashoutId = body.GetProperty("cashoutId").GetGuid();
            Assert.Equal("Queued", body.GetProperty("status").GetString());
            Assert.Equal(PayoutWaitCodes.AdapterMissing, body.GetProperty("failureReason").GetString());

            // The queue worker keeps trying; nothing completes it while the account names no adapter.
            await Task.Delay(1500);
            var row = await _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == cashoutId));
            Assert.NotEqual(CashoutStatus.Completed, row.Status);
            Assert.Null(row.BankTransferId);
            Assert.False(await _f.DbAsync(db => db.LedgerEntries.AnyAsync(l => l.CashoutId == cashoutId && l.EntryType == LedgerEntryType.BankTransferSent)));
        }
        finally
        {
            // Hand the amount back on Yandex (nothing reached a bank), then the account.
            if (cashoutId is { } id)
            {
                var swich = await _f.SuperAdminAsync();
                // A cancel racing the queue worker's attempt is refused for the moment (409): ask again.
                for (var i = 0; i < 10; i++)
                {
                    var cancel = await swich.PostAsJsonAsync($"/api/admin/parks/{p5}/cashouts/{id}/cancel", new { reason = "test: no adapter" });
                    if (cancel.IsSuccessStatusCode) break;
                    await Task.Delay(300);
                }
            }
            await RestoreAccountAsync(account.Id, account.Provider, account.RawCredentials);
        }
    }

    [Fact]
    public async Task A_settlement_through_an_account_whose_provider_has_no_adapter_fails_instead_of_completing()
    {
        var swich = await _f.SuperAdminAsync();
        var kit = new SettlementKit(_f);
        var p5 = await kit.ParkIdAsync(P5);
        var account = await TbAccountAsync(p5);
        Guid? rowId = null;
        try
        {
            await kit.AssignAsync(swich, p5, null);
            await kit.PlantAsync(p5, 2, 0.50m, Noon(2017, 6, 10));
            await SetProviderAsync(account.Id, "xyz");
            var primary = await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking().Where(a => a.ParkId == p5 && a.IsPrimary).Select(a => a.Id).FirstAsync());
            Assert.Equal(account.Id, primary);

            var direct = (await RunAsync(swich, "2017-06", parkId: p5)).GetProperty("direct").EnumerateArray().Single();
            rowId = direct.GetProperty("id").GetGuid();
            Assert.Equal("Failed", S(direct, "status"));
            Assert.StartsWith(PayoutWaitCodes.AdapterMissing + ":", S(direct, "failureReason"));
            Assert.False(await _f.DbAsync(db => db.LedgerEntries.AnyAsync(l => l.SettlementId == rowId && l.EntryType == LedgerEntryType.SettlementSent)));
        }
        finally
        {
            await RestoreAccountAsync(account.Id, account.Provider, account.RawCredentials);
            // Back on its rail, the same transfer goes through — no failed row is left behind for the park's alerts.
            if (rowId is { } id) await swich.PostAsync($"/api/admin/settlements/{id}/retry", null);
            await kit.ReturnAsync();
        }
    }

    // ── At startup ───────────────────────────────────────────────────

    [Fact]
    public async Task A_real_rails_server_refuses_to_start_while_an_active_account_names_the_mock()
    {
        // The development seed's payout accounts are all provider "mock".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _f.DbAsync(async db => { await PayoutRailCheck.EnsureAsync(db, BankRailPolicy.RealRails, NullLogger.Instance); return 0; }));
        Assert.Contains("Refusing to start", ex.Message);
        Assert.Contains("provider 'mock'", ex.Message);

        // Development and a dry run check nothing.
        await _f.DbAsync(async db => { await PayoutRailCheck.EnsureAsync(db, BankRailPolicy.Development, NullLogger.Instance); return 0; });
    }
}
