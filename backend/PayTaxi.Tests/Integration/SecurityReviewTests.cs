using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Identity;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Adapters.Yandex;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Security;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The security review's findings, each pinned by the request that used to get through: concurrent guesses at a sign-in
/// code or an admin password, a park manager poisoning an encrypted column, an unbounded report window, a client key that
/// claims a settlement's bank document, a payout re-sent through another account while an earlier document's outcome is
/// unknown, and the like. Drivers and parks used here are used by no other test (Batumi #1: 002, 003, 008; park #5: 001, who has the balance for two small payouts).
/// </summary>
[Collection("api")]
public class SecurityReviewTests
{
    private const string Batumi = "batumi-auto-park-1";
    private const string Park5 = "tbilisi-auto-park-5";
    private readonly ApiFixture _f;
    public SecurityReviewTests(ApiFixture f) => _f = f;

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync());

    private Task<string> PhoneOfAsync(string profile) =>
        _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.YandexDriverProfileId == profile)).PhoneEncrypted);

    private static string PhoneHash(string phone) => LookupHash.Phone(GeorgianPhone.Normalize(phone)!);

    // The phone's codes and its per-phone sign-in events (the day limits): every test here starts the phone from nothing.
    private Task ClearCodesAsync(string phone) => TestDrivers.ForgetPhoneAsync(_f, PhoneHash(phone));

    private async Task<string> RequestCodeAsync(string phone)
    {
        var res = await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devCode").GetString()!;
    }

    private static string WrongCode(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    // ── Sign-in codes: the per-code and per-phone caps hold under concurrency ──

    [Fact]
    public async Task Concurrent_wrong_codes_get_at_most_five_guesses_and_the_right_code_is_then_refused()
    {
        var phone = await PhoneOfAsync("yp_bt1_002");
        await ClearCodesAsync(phone);
        try
        {
            var code = await RequestCodeAsync(phone);
            var wrong = WrongCode(code);

            var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
            {
                var res = await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code = wrong });
                return ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>());
            }));

            // A guess that was compared answers invalid_code. Each one spends one of the code's five attempts, so at
            // most five can have been compared — whatever the interleaving.
            Assert.True(answers.Count(a => a == "invalid_code") <= 5,
                $"{answers.Count(a => a == "invalid_code")} guesses were compared against one code: {string.Join(",", answers.GroupBy(a => a).Select(g => $"{g.Key}×{g.Count()}"))}");

            // Refused: the code is spent — and forty failures in a row have locked the phone for the day's limit (429).
            var right = await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code });
            Assert.Contains(right.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests });

            var row = await _f.DbAsync(db => db.OtpCodes.AsNoTracking().SingleAsync(o => o.PhoneHash == PhoneHash(phone)));
            Assert.Equal(5, row.AttemptCount);
        }
        finally { await ClearCodesAsync(phone); }
    }

    [Fact]
    public async Task The_right_code_sent_several_times_at_once_signs_in_once()
    {
        var phone = await PhoneOfAsync("yp_bt1_002");
        await ClearCodesAsync(phone);
        try
        {
            var code = await RequestCodeAsync(phone);
            var driverId = await _f.DbAsync(db => db.Drivers.AsNoTracking().Where(d => d.YandexDriverProfileId == "yp_bt1_002").Select(d => d.Id).FirstAsync());
            var before = await _f.DbAsync(db => db.DriverSessions.CountAsync(s => s.DriverId == driverId));

            var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                (await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code })).StatusCode));

            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
            Assert.Equal(before + 1, await _f.DbAsync(db => db.DriverSessions.CountAsync(s => s.DriverId == driverId)));
        }
        finally { await ClearCodesAsync(phone); }
    }

    [Fact]
    public async Task Concurrent_code_requests_issue_no_more_codes_than_the_per_phone_cap()
    {
        var phone = await PhoneOfAsync("yp_bt1_003");
        await ClearCodesAsync(phone);
        try
        {
            // The fixture's cap is Auth:OtpMaxPerWindow = 5 per 10 minutes.
            var statuses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
                (await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone })).StatusCode));

            Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
            Assert.Equal(5, await _f.DbAsync(db => db.OtpCodes.CountAsync(o => o.PhoneHash == PhoneHash(phone))));
        }
        finally { await ClearCodesAsync(phone); }
    }

    // ── Admin sign-in: the 5-strike lockout holds under concurrency; a stale token is re-checked ──

    private async Task<(Guid id, string email)> NewAdminAsync(string role, Guid? parkId, string password)
    {
        var email = $"sec-{Guid.NewGuid():N}@review.local";
        var id = await _f.DbAsync(async db =>
        {
            var u = new AdminUser
            {
                Email = email, Name = "Security review", Role = role, ParkId = parkId, IsActive = true,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4),
            };
            db.AdminUsers.Add(u);
            await db.SaveChangesAsync();
            return u.Id;
        });
        return (id, email);
    }

    private Task DeleteAdminAsync(Guid id) => _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == id).ExecuteDeleteAsync());

    [Fact]
    public async Task Concurrent_wrong_passwords_get_at_most_five_tries_before_the_account_locks()
    {
        var (id, email) = await NewAdminAsync("park_admin", await ParkIdAsync(Batumi), "correct-horse-1");
        try
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
            {
                var res = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email, password = $"wrong-{i}" });
                return res.StatusCode;
            }));

            // invalid_credentials = the password was checked. Five checks, then the lock.
            Assert.True(answers.Count(s => s == HttpStatusCode.Unauthorized) <= 5,
                $"{answers.Count(s => s == HttpStatusCode.Unauthorized)} passwords were checked before the lock");
            var row = await _f.DbAsync(db => db.AdminUsers.AsNoTracking().FirstAsync(a => a.Id == id));
            Assert.NotNull(row.LockedUntil);
            Assert.True(row.LockedUntil > DateTime.UtcNow);

            // Locked: even the right password is refused until the lock lapses.
            var right = await _f.Client.PostAsJsonAsync("/api/admin/auth/login", new { email, password = "correct-horse-1" });
            Assert.Equal((HttpStatusCode)423, right.StatusCode);
        }
        finally { await DeleteAdminAsync(id); }
    }

    [Fact]
    public async Task A_deactivated_admin_token_stops_working_at_once()
    {
        var (id, email) = await NewAdminAsync("park_admin", await ParkIdAsync(Batumi), "correct-horse-2");
        try
        {
            var admin = await _f.AdminClientAsync(email, "correct-horse-2");
            await _f.DbAsync(db => db.AdminUsers.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false)));

            Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/admin/parks")).StatusCode);
        }
        finally { await DeleteAdminAsync(id); }
    }

    [Fact]
    public async Task A_park_manager_token_without_a_park_sees_no_park_list_alerts_or_settlements()
    {
        // Only a hand-made row can look like this (CreatePark always sets the park), but the lists must fail closed.
        var (id, email) = await NewAdminAsync("park_admin", null, "correct-horse-3");
        try
        {
            var admin = await _f.AdminClientAsync(email, "correct-horse-3");
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/parks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/alerts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/settlements")).StatusCode);
        }
        finally { await DeleteAdminAsync(id); }
    }

    // ── Driver refresh: a lost race between two tabs is not "reuse" ──

    [Fact]
    public async Task Two_simultaneous_refreshes_with_one_token_yield_one_successor_and_no_theft_alarm()
    {
        var phone = await PhoneOfAsync("yp_bt1_008");
        await ClearCodesAsync(phone);
        try
        {
            var code = await RequestCodeAsync(phone);
            var login = await (await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code })).Content.ReadFromJsonAsync<JsonElement>();
            var refresh = login.GetProperty("refreshToken").GetString()!;
            var driverId = login.GetProperty("driver").GetProperty("id").GetGuid();

            var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
                (await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refresh })).StatusCode));

            Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
            Assert.All(statuses.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
            var live = await _f.DbAsync(db => db.DriverSessions.CountAsync(s => s.DriverId == driverId && s.RevokedAt == null));
            Assert.Equal(1, live);
            Assert.False(await _f.DbAsync(db => db.DriverSessions.AnyAsync(s => s.DriverId == driverId && s.RevokedReason == "refresh_reuse_detected")));
        }
        finally
        {
            await ClearCodesAsync(phone);
            await _f.DbAsync(db => db.DriverSessions.Where(s => s.Driver.YandexDriverProfileId == "yp_bt1_008")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow).SetProperty(x => x.RevokedReason, "logout")));
        }
    }

    // ── /me?fresh=true: one live Yandex roster read per driver per window ──

    [Fact]
    public async Task Repeated_fresh_balance_reads_reach_Yandex_once_per_window()
    {
        var (driver, _, parkId) = await _f.DriverClientAsync("yp_bt1_008");
        // Start from a quiet slot: an earlier live read of this driver would make the first call below a cached one.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.OK, (await driver.GetAsync("/api/driver/me?fresh=true")).StatusCode);
        await Task.Delay(1000); // the call log is written in the background

        var live = await _f.DbAsync(db => db.ApiAuditLogs.CountAsync(a =>
            a.ParkId == parkId && a.Timestamp >= t0 && a.Endpoint == "POST /v1/parks/driver-profiles/list"));
        Assert.True(live <= 1, $"{live} live roster reads for one driver's 8 refreshes");
    }

    // ── A value that looks encrypted is still encrypted ──

    [Fact]
    public async Task A_park_manager_cannot_store_an_unencrypted_look_alike_and_break_the_park_list()
    {
        var parkId = await ParkIdAsync(Batumi);
        var rawBefore = await RawAsync("SELECT \"YandexClientIdEncrypted\" AS \"Value\" FROM \"Parks\" WHERE \"Id\" = {0}", parkId);
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var patch = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}", new { yandexClientId = "enc:v1:x" });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

            var raw = await RawAsync("SELECT \"YandexClientIdEncrypted\" AS \"Value\" FROM \"Parks\" WHERE \"Id\" = {0}", parkId);
            Assert.NotEqual("enc:v1:x", raw);

            var swich = await _f.SuperAdminAsync();
            var list = await swich.GetAsync("/api/admin/parks");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var mine = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == parkId);
            Assert.Equal("enc:v1:x", mine.GetProperty("yandexClientId").GetString());
        }
        finally
        {
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"Parks\" SET \"YandexClientIdEncrypted\" = {0} WHERE \"Id\" = {1}", rawBefore ?? "", parkId));
        }
    }

    private Task<string?> RawAsync(string sql, Guid id) =>
        _f.DbAsync(db => db.Database.SqlQueryRaw<string>(sql, id).FirstOrDefaultAsync());

    // ── Reports: the window is bounded ──

    [Fact]
    public async Task A_report_window_longer_than_a_year_is_refused()
    {
        var parkId = await ParkIdAsync(Batumi);
        var manager = await _f.ParkAdminAsync(Batumi);
        var res = await manager.GetAsync($"/api/admin/parks/{parkId}/reports?from=2000-01-01&to=2026-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("window_too_large", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));

        var year = await manager.GetAsync($"/api/admin/parks/{parkId}/reports?from=2025-01-01&to=2026-01-01");
        Assert.Equal(HttpStatusCode.OK, year.StatusCode);
    }

    // ── Client idempotency keys cannot take a server-made bank document id ──

    [Theory]
    [InlineData("settle:{park}:2026-10")]
    [InlineData("rev:{park}")]
    [InlineData("retry:{park}:1")]
    [InlineData("abc#1")]
    public async Task A_cashout_key_shaped_like_a_server_document_id_is_refused(string pattern)
    {
        var (driver, _, parkId) = await _f.DriverClientAsync("yp_bt1_003");
        var key = pattern.Replace("{park}", parkId.ToString("N"));
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First().GetProperty("id").GetGuid();

        var res = await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 5m, idempotencyKey = key });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("idempotency_key_reserved", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.False(await _f.DbAsync(db => db.Cashouts.AnyAsync(c => c.IdempotencyKey == key)));

        var manager = await _f.ParkAdminAsync(Batumi);
        var driverId = await _f.DbAsync(db => db.Drivers.AsNoTracking().Where(d => d.YandexDriverProfileId == "yp_bt1_003").Select(d => d.Id).FirstAsync());
        var manual = await manager.PostAsJsonAsync($"/api/admin/parks/{parkId}/cashouts", new { driverId, cardId, amount = 5m, idempotencyKey = key });
        Assert.Equal(HttpStatusCode.BadRequest, manual.StatusCode);
        Assert.Equal("idempotency_key_reserved", ApiFixture.Code(await manual.Content.ReadFromJsonAsync<JsonElement>()));
    }

    // ── TBC credentials: no server-side file paths from a tenant ──

    [Fact]
    public async Task A_park_manager_cannot_point_its_TBC_credentials_at_a_server_file()
    {
        var parkId = await ParkIdAsync(Batumi);
        var accountId = await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.BankCode == "TB").Select(a => a.Id).FirstAsync());
        var rawBefore = await RawAsync("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", accountId);
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var creds = JsonSerializer.Serialize(new { username = "x", password = "y", environment = "test", certificatePath = "/etc/passwd" });
            var res = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{accountId}", new { credentialsJson = creds });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal("certificate_path_not_allowed", ApiFixture.Code(await res.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(rawBefore, await RawAsync("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", accountId));
        }
        finally
        {
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0} WHERE \"Id\" = {1}", rawBefore ?? "{}", accountId));
        }
    }

    // ── The ledger is append-only in code too ──

    [Fact]
    public async Task A_ledger_entry_cannot_be_changed_or_deleted_through_the_context()
    {
        // A failed cashout of 2020 with one ledger row: counted by no report, statement or reconciliation window.
        var parkId = await ParkIdAsync(Batumi);
        var (cashoutId, id) = await _f.DbAsync(async db =>
        {
            var d = await db.Drivers.AsNoTracking().FirstAsync(x => x.YandexDriverProfileId == "yp_bt1_003");
            var card = await db.BankCards.AsNoTracking().Where(x => x.DriverId == d.Id).OrderByDescending(x => x.IsDefault).FirstAsync();
            var at = new DateTime(2020, 1, 2, 10, 0, 0, DateTimeKind.Utc);
            var c = new Cashout
            {
                DriverId = d.Id, ParkId = parkId, BankCardId = card.Id, Amount = 5m, Fee = 0.5m, Status = CashoutStatus.Failed,
                FailureReason = "TEST", CreatedAt = at, UpdatedAt = at, IdempotencyKey = $"ledger-guard:{Guid.NewGuid():N}", InitiatedBy = "test",
            };
            var l = new LedgerEntry { CashoutId = c.Id, ParkId = parkId, EntryType = LedgerEntryType.CashoutReversed, Amount = 5m, Notes = "original", CreatedAt = at, UpdatedAt = at };
            db.Cashouts.Add(c);
            db.LedgerEntries.Add(l);
            await db.SaveChangesAsync();
            return (c.Id, l.Id);
        });
        const string before = "original";
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _f.DbAsync(async db =>
            {
                var row = await db.LedgerEntries.FirstAsync(l => l.Id == id);
                row.Notes = "rewritten";
                return await db.SaveChangesAsync();
            }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _f.DbAsync(async db =>
            {
                db.LedgerEntries.Remove(await db.LedgerEntries.FirstAsync(l => l.Id == id));
                return await db.SaveChangesAsync();
            }));
            Assert.Equal(before, await _f.DbAsync(db => db.LedgerEntries.AsNoTracking().Where(l => l.Id == id).Select(l => l.Notes).FirstAsync()));
        }
        finally
        {
            // Bulk deletes bypass the context's guard on purpose: this is the test's own row.
            await _f.DbAsync(db => db.LedgerEntries.Where(l => l.CashoutId == cashoutId).ExecuteDeleteAsync());
            await _f.DbAsync(db => db.Cashouts.Where(c => c.Id == cashoutId).ExecuteDeleteAsync());
        }
    }

    // ── A document whose outcome is unknown never goes out through another rail ──

    private MockBankPayoutProvider Mock => _f.Services.GetRequiredService<MockBankPayoutProvider>();
    private MockBankPayoutOptions MockOpts => _f.Services.GetRequiredService<IOptions<MockBankPayoutOptions>>().Value;
    private CashoutOptions SagaOpts => _f.Services.GetRequiredService<IOptions<CashoutOptions>>().Value;

    private Func<Task> DrainAccount(Guid accountId)
    {
        var backoff = SagaOpts.BackoffSeconds;
        MockOpts.EnforceBalance = true;
        Mock.SetBalance(accountId, 0m);
        SagaOpts.BackoffSeconds = new[] { 600 };
        return () =>
        {
            MockOpts.EnforceBalance = false;
            Mock.SetBalance(accountId, 25_000m);
            SagaOpts.BackoffSeconds = backoff;
            return Task.CompletedTask;
        };
    }

    private static CashoutOrchestrator Saga(IServiceScope scope, IBankPayoutAdapter bank)
    {
        var banks = new ServiceCollection();
        foreach (var key in new[] { "mock", "MOCK", "tbc", "TBC" })
            banks.AddKeyedSingleton(key, bank);
        var sp = scope.ServiceProvider;
        return new CashoutOrchestrator(
            sp.GetRequiredService<AppDbContext>(),
            banks.BuildServiceProvider(),
            sp.GetRequiredService<IYandexFleetClient>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<IOptions<CashoutOptions>>(),
            sp.GetRequiredService<IOptions<YandexFleetOptions>>(),
            NullLogger<CashoutOrchestrator>.Instance);
    }

    private Task<Cashout> RowAsync(Guid id) => _f.DbAsync(db => db.Cashouts.AsNoTracking().FirstAsync(c => c.Id == id));

    /// <summary>
    /// One queue step through <paramref name="bank"/>: done once the scripted bank was asked or the payout went to review
    /// (retried while the background worker wins the claim).
    /// </summary>
    private async Task<CashoutSagaResult> StepWithAsync(ScriptedBank bank, Guid id)
    {
        var calls = bank.Calls;
        for (var i = 0; i < 30; i++)
        {
            await _f.DbAsync(db => db.Cashouts.Where(c => c.Id == id && c.Status == CashoutStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextAttemptAt, DateTime.UtcNow)));
            using var scope = _f.Services.CreateScope();
            var result = await Saga(scope, bank).ProcessQueuedAsync(id);
            if (bank.Calls > calls || result.Status == "ReviewRequired") return result;
            await Task.Delay(150);
        }
        throw new Xunit.Sdk.XunitException("the queue step never ran");
    }

    private async Task<(Guid cashoutId, Guid accountId, Func<Task> undo)> UnknownOutcomeAsync(ScriptedBank bank, decimal amount)
    {
        var parkId = await ParkIdAsync(Park5);
        var accountId = await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.BankCode == "TB" && a.IsActive).OrderByDescending(a => a.IsPrimary).Select(a => a.Id).FirstAsync());
        var undo = DrainAccount(accountId);
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb5_001");
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var cardId = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        var queued = await (await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount, idempotencyKey = Guid.NewGuid().ToString() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(queued.TryGetProperty("status", out var status) && status.GetString() == "Queued", $"expected Queued: {queued}");
        var id = queued.GetProperty("cashoutId").GetGuid();

        // Document K reaches the bank, the response is lost and the lookup cannot be answered: K may have paid.
        bank.Funded = true; bank.LoseResponse = true; bank.LookupFails = true;
        Assert.Equal("Queued", (await StepWithAsync(bank, id)).Status);
        Assert.NotNull((await RowAsync(id)).UnknownOutcomeDocument);
        bank.LoseResponse = false; bank.LookupFails = false;
        return (id, accountId, undo);
    }

    private Task FailForCleanupAsync(Guid id) => _f.DbAsync(db => db.Cashouts
        .Where(c => c.Id == id && (c.Status == CashoutStatus.Queued || c.Status == CashoutStatus.Processing || c.Status == CashoutStatus.ReviewRequired))
        .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CashoutStatus.Failed).SetProperty(c => c.NextAttemptAt, (DateTime?)null)
            .SetProperty(c => c.FailureReason, "TEST_CLEANUP")));

    [Fact]
    public async Task An_unanswered_document_is_not_re_sent_through_another_account_after_its_own_is_deactivated()
    {
        var bank = new ScriptedBank();
        var (id, accountA, undo) = await UnknownOutcomeAsync(bank, 6m);
        Guid? accountB = null;
        try
        {
            var parkId = await ParkIdAsync(Park5);
            accountB = await _f.DbAsync(async db =>
            {
                var b = new ParkBankAccount
                {
                    ParkId = parkId, BankCode = "TB", Provider = "mock", Iban = ApiFixture.TbcIban("7000000444000222"),
                    HolderName = "Second account", Label = "Second", CredentialsEncrypted = "{}", IsActive = true, IsPrimary = false,
                };
                db.ParkBankAccounts.Add(b);
                await db.SaveChangesAsync();
                await db.ParkBankAccounts.Where(a => a.Id == accountA).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
                return b.Id;
            });

            var sendsBefore = bank.Requests.Count;
            var result = await StepWithAsync(bank, id);
            Assert.Equal("ReviewRequired", result.Status);
            Assert.Equal(sendsBefore, bank.Requests.Count);
            Assert.DoesNotContain(bank.Requests, r => r.Source.ParkBankAccountId == accountB);
        }
        finally
        {
            await FailForCleanupAsync(id);
            await _f.DbAsync(db => db.ParkBankAccounts.Where(a => a.Id == accountA).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, true)));
            if (accountB is { } b) await _f.DbAsync(db => db.ParkBankAccounts.Where(a => a.Id == b).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false)));
            await undo();
        }
    }

    [Fact]
    public async Task An_unanswered_document_is_not_re_sent_after_the_account_s_credentials_changed()
    {
        var bank = new ScriptedBank();
        var (id, accountA, undo) = await UnknownOutcomeAsync(bank, 7m);
        var parkId = await ParkIdAsync(Park5);
        var rawBefore = await RawAsync("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", accountA);
        try
        {
            // Another DBI user at the same bank has no record of K: a re-send there is a second order.
            var manager = await _f.ParkAdminAsync(Park5);
            var creds = JsonSerializer.Serialize(new { username = "other-dbi-user", password = "y", environment = "test" });
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{accountA}", new { credentialsJson = creds })).StatusCode);

            var sendsBefore = bank.Requests.Count;
            var result = await StepWithAsync(bank, id);
            Assert.Equal("ReviewRequired", result.Status);
            Assert.Equal(sendsBefore, bank.Requests.Count);
        }
        finally
        {
            await FailForCleanupAsync(id);
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0} WHERE \"Id\" = {1}", rawBefore ?? "{}", accountA));
            await undo();
        }
    }

    [Fact]
    public async Task A_new_password_for_the_same_bank_user_does_not_hold_the_payout()
    {
        // Same DBI user, same environment: the bank's duplicate check still recognises the document, so the re-send is safe.
        var bank = new ScriptedBank();
        var (id, accountA, undo) = await UnknownOutcomeAsync(bank, 5m);
        var parkId = await ParkIdAsync(Park5);
        var rawBefore = await RawAsync("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", accountA);
        try
        {
            var manager = await _f.ParkAdminAsync(Park5);
            var first = JsonSerializer.Serialize(new { username = "park5-dbi", password = "old", environment = "test" });
            var renewed = JsonSerializer.Serialize(new { username = "park5-dbi", password = "new", environment = "test" });
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0} WHERE \"Id\" = {1}", first, accountA));
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{accountA}", new { credentialsJson = renewed })).StatusCode);

            Assert.Equal(CashoutStatus.Queued, (await RowAsync(id)).Status);
            var paid = await StepWithAsync(bank, id);
            Assert.Equal("Completed", paid.Status);
        }
        finally
        {
            await FailForCleanupAsync(id);
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0} WHERE \"Id\" = {1}", rawBefore ?? "{}", accountA));
            await undo();
        }
    }

    [Fact]
    public async Task An_empty_environment_is_the_sandbox_so_naming_production_holds_the_payout()
    {
        // The adapter reads "environment": "" as not production — the sandbox host, where a server allows one. The rail
        // check used to read it as production, so moving the same DBI user to production was "no change" and a document
        // whose answer the sandbox lost would have been re-sent to the real bank as a new order.
        var bank = new ScriptedBank();
        var (id, accountA, undo) = await UnknownOutcomeAsync(bank, 8m);
        var parkId = await ParkIdAsync(Park5);
        var rawBefore = await RawAsync("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", accountA);
        try
        {
            var manager = await _f.ParkAdminAsync(Park5);
            var blank = JsonSerializer.Serialize(new { username = "park5-dbi", password = "pw", environment = "" });
            var production = JsonSerializer.Serialize(new { username = "park5-dbi", password = "pw", environment = "production" });
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0} WHERE \"Id\" = {1}", blank, accountA));
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{accountA}", new { credentialsJson = production })).StatusCode);

            Assert.Equal(CashoutStatus.ReviewRequired, (await RowAsync(id)).Status);
        }
        finally
        {
            await FailForCleanupAsync(id);
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0} WHERE \"Id\" = {1}", rawBefore ?? "{}", accountA));
            await undo();
        }
    }

    // ── Startup: the encryption key must be the database's; a non-Development host refuses public keys ──

    [Fact]
    public async Task A_well_formed_but_wrong_encryption_key_is_refused_before_anything_is_written()
    {
        // The seeded database holds phones, IBANs and credentials under the fixture's key.
        await _f.DbAsync(async db => { await EncryptionKeyCheck.EnsureAsync(db, NullLogger.Instance); return 0; });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _f.DbAsync(async db => { await EncryptionKeyCheck.EnsureAsync(db, NullLogger.Instance, FieldEncryptor.NewKeyBase64()); return 0; }));
        Assert.Contains("decrypts none", ex.Message);
    }

    [Fact]
    public void A_host_that_is_not_Development_refuses_the_public_test_keys()
    {
        // The fixture's environment variables (the published test JWT and encryption keys) are still set: a host named
        // Staging must not start on them — it used to, because the checks ran only for "Production".
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Staging"));
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var messages = string.Join(" | ", Chain(ex).Select(e => e.Message));
        Assert.Contains("Refusing to start in Staging", messages);
        Assert.Contains("Jwt:Key", messages);
        Assert.Contains("Encryption:Key", messages);

        static IEnumerable<Exception> Chain(Exception? e) { for (; e is not null; e = e.InnerException) yield return e; }
    }

    // ── The change trail: who changed a rail or a credential, and which fields — never the values ──

    [Fact]
    public async Task Changing_a_payout_account_s_provider_and_credentials_leaves_one_attributed_entry_without_values()
    {
        var parkId = await ParkIdAsync(Batumi);
        var account = await _f.DbAsync(db => db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.BankCode == "TB").Select(a => new { a.Id, a.Provider, a.IsPrimary }).FirstAsync());
        var rawCreds = await RawAsync("SELECT \"CredentialsEncrypted\" AS \"Value\" FROM \"ParkBankAccounts\" WHERE \"Id\" = {0}", account.Id);
        var park = await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Id == parkId).Select(p => new { p.BankProvider, p.BankType }).FirstAsync());
        var since = DateTime.UtcNow;
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var secret = "pw-" + Guid.NewGuid().ToString("N");
            var creds = JsonSerializer.Serialize(new { username = "batumi-dbi", password = secret, environment = "test" });
            var res = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}",
                new { provider = account.Provider == "tbc" ? "mock" : "tbc", credentialsJson = creds });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var events = await _f.DbAsync(db => db.ParkConfigEvents.AsNoTracking()
                .Where(e => e.SubjectId == account.Id && e.At >= since).ToListAsync());
            var e = Assert.Single(events);
            Assert.Equal(ParkConfigSubjects.BankAccount, e.Subject);
            Assert.Equal("updated", e.Action);
            Assert.Equal("admin:manager@batumi-auto-park-1.local", e.Actor);
            Assert.Equal(new[] { "credentials", "provider" }, e.ChangedFields.Split(',').OrderBy(x => x).ToArray());
            Assert.DoesNotContain(secret, e.ChangedFields + e.Actor);

            // A label-only edit is not a rail change: nothing recorded.
            await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{account.Id}", new { label = "Main TBC" });
            Assert.Single(await _f.DbAsync(db => db.ParkConfigEvents.AsNoTracking().Where(x => x.SubjectId == account.Id && x.At >= since).ToListAsync()));
        }
        finally
        {
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync(
                "UPDATE \"ParkBankAccounts\" SET \"CredentialsEncrypted\" = {0}, \"Provider\" = {1} WHERE \"Id\" = {2}", rawCreds ?? "{}", account.Provider, account.Id));
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Parks\" SET \"BankProvider\" = {0}, \"BankType\" = {1} WHERE \"Id\" = {2}", park.BankProvider ?? "", park.BankType ?? "", parkId));
        }
    }

    [Fact]
    public async Task A_new_Yandex_API_key_is_recorded_by_name_only()
    {
        var parkId = await ParkIdAsync(Batumi);
        var rawKey = await RawAsync("SELECT \"YandexApiKeyEncrypted\" AS \"Value\" FROM \"Parks\" WHERE \"Id\" = {0}", parkId);
        var since = DateTime.UtcNow;
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var key = "yx-" + Guid.NewGuid().ToString("N");
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}", new { yandexApiKey = key })).StatusCode);
            var e = Assert.Single(await _f.DbAsync(db => db.ParkConfigEvents.AsNoTracking()
                .Where(x => x.ParkId == parkId && x.Subject == ParkConfigSubjects.Park && x.At >= since).ToListAsync()));
            Assert.Equal("yandexApiKey", e.ChangedFields);
            Assert.Equal("admin:manager@batumi-auto-park-1.local", e.Actor);
        }
        finally
        {
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"Parks\" SET \"YandexApiKeyEncrypted\" = {0} WHERE \"Id\" = {1}", rawKey ?? "", parkId));
        }
    }
}
