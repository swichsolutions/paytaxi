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
/// The retention periods the drivers' privacy notice promises, kept by the nightly RetentionWorker: sign-in codes go
/// 24 hours after they were issued; a device session 90 days after its last use, or a day after it ended (signed out,
/// cut off, expired) — a rotated token stays while its chain is alive, because it is what exposes a copied token;
/// the log of calls to Yandex and the banks, and the sign-in history, after 2 years. Financial records — cashouts,
/// ledger, settlements, payout accounts, drivers — are never touched by it.
/// Rows are planted for a Batumi driver (never Tbilisi #3, which the flaky-bank host uses in parallel) and removed
/// again; the worker runs with the real clock, so every planted age is relative to now.
/// </summary>
[Collection("api")]
public class RetentionTests
{
    private const string Profile = "yp_bt1_005";
    private readonly ApiFixture _f;
    public RetentionTests(ApiFixture f) => _f = f;

    private Task<Driver> DriverAsync() => _f.DbAsync(db => db.Drivers.AsNoTracking().FirstAsync(d => d.YandexDriverProfileId == Profile));

    private async Task<(int Cashouts, int Ledger, int Settlements, int Statements, int PayoutAccounts, int Drivers, int ParkAccounts)> MoneyCountsAsync() =>
        await _f.DbAsync(async db => (
            await db.Cashouts.CountAsync(), await db.LedgerEntries.CountAsync(), await db.Settlements.CountAsync(),
            await db.MonthlyStatements.CountAsync(), await db.BankCards.CountAsync(), await db.Drivers.CountAsync(),
            await db.ParkBankAccounts.CountAsync()));

    [Fact]
    public async Task Each_kind_of_record_is_deleted_only_after_its_period_and_no_financial_record_ever()
    {
        var driver = await DriverAsync();
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var otpHash = ("retention-" + tag).PadRight(64, '0');

        // ── Plant one row on each side of every limit ────────────────
        var sessions = new Dictionary<string, Guid>();
        DriverSession Session(string name, DateTime createdAt, DateTime? lastUsedAt, DateTime expiresAt, DateTime? revokedAt = null, string? reason = null)
        {
            var s = new DriverSession
            {
                DriverId = driver.Id, ParkId = driver.ParkId, TokenHash = (tag + name).PadRight(64, 'a')[..64], DeviceLabel = "Android · Chrome",
                CreatedAt = createdAt, UpdatedAt = createdAt, LastUsedAt = lastUsedAt, ExpiresAt = expiresAt, RevokedAt = revokedAt, RevokedReason = reason,
            };
            sessions[name] = s.Id;
            return s;
        }
        var oldCashoutId = Guid.Empty;
        var auditIds = new List<long>();
        var signInIds = new List<long>();
        await _f.DbAsync(async db =>
        {
            db.OtpCodes.AddRange(
                new OtpCode { PhoneHash = otpHash, CodeHash = "x", ExpiresAt = now.AddHours(-25).AddMinutes(5), Used = true, CreatedAt = now.AddHours(-25) },
                new OtpCode { PhoneHash = otpHash, CodeHash = "y", ExpiresAt = now.AddHours(-23).AddMinutes(5), CreatedAt = now.AddHours(-23) });

            db.DriverSessions.AddRange(
                // A device unused for more than 90 days (a longer Auth:TrustedDeviceDays would still have it valid): gone.
                Session("idle", now.AddDays(-120), now.AddDays(-91), now.AddDays(30)),
                // Signed in 80 days ago, used an hour ago: stays.
                Session("inUse", now.AddDays(-80), now.AddHours(-1), now.AddDays(10)),
                // Expired two days ago: gone; expired twelve hours ago: stays for the day.
                Session("expired", now.AddDays(-92), now.AddDays(-3), now.AddDays(-2)),
                Session("justExpired", now.AddDays(-90), now.AddDays(-1), now.AddHours(-12)),
                // Signed out two days ago: gone; twelve hours ago: stays for the day.
                Session("loggedOut", now.AddDays(-10), now.AddDays(-2), now.AddDays(80), now.AddDays(-2), "logout"),
                Session("justLoggedOut", now.AddDays(-10), now.AddHours(-12), now.AddDays(80), now.AddHours(-12), "logout"),
                // Rotated two days ago while its chain still lives: stays — presenting it again is what reveals a copied token.
                Session("rotated", now.AddDays(-10), now.AddDays(-2), now.AddDays(80), now.AddDays(-2), "rotated"),
                // Rotated long ago, chain expired two days ago: gone.
                Session("rotatedDead", now.AddDays(-92), now.AddDays(-60), now.AddDays(-2), now.AddDays(-60), "rotated"));

            var oldAudit = new ApiAuditLog { Timestamp = now.AddYears(-2).AddDays(-1), ParkId = driver.ParkId, ActorType = "System", ApiProvider = "YandexFleet", Endpoint = "/test/retention/" + tag, Method = "POST", ResponseCode = 200, Success = true };
            var keptAudit = new ApiAuditLog { Timestamp = now.AddYears(-2).AddDays(1), ParkId = driver.ParkId, ActorType = "System", ApiProvider = "TBC", Endpoint = "/test/retention/" + tag, Method = "POST", ResponseCode = 200, Success = true };
            db.ApiAuditLogs.AddRange(oldAudit, keptAudit);

            var oldSignIn = new DriverSignIn { DriverId = driver.Id, ParkId = driver.ParkId, SignedInAt = now.AddYears(-2).AddDays(-1), DeviceLabel = "retention-" + tag };
            var keptSignIn = new DriverSignIn { DriverId = driver.Id, ParkId = driver.ParkId, SignedInAt = now.AddYears(-2).AddDays(1), DeviceLabel = "retention-" + tag };
            db.DriverSignIns.AddRange(oldSignIn, keptSignIn);

            // A financial record far older than every limit — a cashout, its ledger line, its payout account.
            var card = await db.BankCards.AsNoTracking().Where(c => c.DriverId == driver.Id).OrderByDescending(c => c.IsDefault).FirstAsync();
            var at = now.AddYears(-3);
            var cashout = new Cashout
            {
                DriverId = driver.Id, ParkId = driver.ParkId, BankCardId = card.Id, Amount = 10.50m, Fee = 0.50m,
                Status = CashoutStatus.Completed, CompletedAt = at, CreatedAt = at, UpdatedAt = at,
                IdempotencyKey = "retention:" + tag, InitiatedBy = "test", AttemptCount = 1,
            };
            db.Cashouts.Add(cashout);
            db.LedgerEntries.Add(new LedgerEntry { CashoutId = cashout.Id, ParkId = driver.ParkId, EntryType = LedgerEntryType.CashoutReserved, Amount = 10.50m, Reference = "retention:" + tag, CreatedAt = at });
            oldCashoutId = cashout.Id;
            await db.SaveChangesAsync();
            auditIds.AddRange(new[] { oldAudit.Id, keptAudit.Id });
            signInIds.AddRange(new[] { oldSignIn.Id, keptSignIn.Id });
            return 0;
        });

        try
        {
            var before = await MoneyCountsAsync();
            var result = await _f.Services.GetRequiredService<RetentionWorker>().RunOnceAsync(DateTime.UtcNow);

            var left = await _f.DbAsync(async db => (
                Otps: await db.OtpCodes.AsNoTracking().Where(o => o.PhoneHash == otpHash).Select(o => o.CodeHash).ToListAsync(),
                Sessions: await db.DriverSessions.AsNoTracking().Where(s => sessions.Values.Contains(s.Id)).Select(s => s.Id).ToListAsync(),
                Audit: await db.ApiAuditLogs.AsNoTracking().Where(a => auditIds.Contains(a.Id)).Select(a => a.Id).ToListAsync(),
                SignIns: await db.DriverSignIns.AsNoTracking().Where(x => signInIds.Contains(x.Id)).Select(x => x.Id).ToListAsync()));

            Assert.Equal(new[] { "y" }, left.Otps.ToArray());
            Assert.Equal(
                new[] { "inUse", "justExpired", "justLoggedOut", "rotated" }.Select(n => sessions[n]).OrderBy(x => x),
                left.Sessions.OrderBy(x => x));
            Assert.Equal(new[] { auditIds[1] }, left.Audit.ToArray());
            Assert.Equal(new[] { signInIds[1] }, left.SignIns.ToArray());

            // What it reports (other runs' leftovers may add to these, never subtract).
            Assert.True(result.OtpCodes >= 1 && result.DeviceSessions >= 4 && result.ApiAuditLogs >= 1 && result.SignIns >= 1, result.ToString());

            // Financial records: nothing deleted, however old.
            Assert.Equal(before, await MoneyCountsAsync());
            Assert.True(await _f.DbAsync(db => db.Cashouts.AnyAsync(c => c.Id == oldCashoutId)));
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                await db.LedgerEntries.Where(l => l.CashoutId == oldCashoutId).ExecuteDeleteAsync();
                await db.Cashouts.Where(c => c.Id == oldCashoutId).ExecuteDeleteAsync();
                await db.OtpCodes.Where(o => o.PhoneHash == otpHash).ExecuteDeleteAsync();
                await db.DriverSessions.Where(s => sessions.Values.Contains(s.Id)).ExecuteDeleteAsync();
                await db.ApiAuditLogs.Where(a => auditIds.Contains(a.Id)).ExecuteDeleteAsync();
                return await db.DriverSignIns.Where(x => signInIds.Contains(x.Id)).ExecuteDeleteAsync();
            });
        }
    }

    [Fact]
    public async Task Sign_in_codes_go_on_every_poll_while_the_rest_waits_for_the_nightly_pass()
    {
        // The privacy notice says a sign-in code is deleted within a day of being issued. Deleted only by the nightly pass,
        // a code issued just after it would wait for the next night — up to 48 hours. So the worker deletes codes past
        // their 24 hours on every poll; everything else stays in the nightly pass.
        // A fresh worker (its own "last nightly pass" memory) on the shared database, ticked at fixed Tbilisi times of
        // yesterday — in the past, so it can delete nothing a real pass would not — with the pass at 03:30.
        var driver = await DriverAsync();
        var tz = SettlementService.ResolveTimeZone("Georgian Standard Time");
        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz)).AddDays(-1);
        DateTime TbilisiUtc(int hour) => TimeZoneInfo.ConvertTimeToUtc(yesterday.ToDateTime(new TimeOnly(hour, 0)), tz);
        var beforePass = TbilisiUtc(1);   // the nightly pass is not due yet
        var afterPass = TbilisiUtc(4);    // the nightly pass is due
        var worker = new RetentionWorker(
            _f.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RetentionOptions { RunAtLocalTime = "03:30", TimeZoneId = "Georgian Standard Time" }),
            NullLogger<RetentionWorker>.Instance);

        var tag = Guid.NewGuid().ToString("N")[..12];
        var otpHash = ("retention-poll-" + tag).PadRight(64, '0');
        var auditId = await _f.DbAsync(async db =>
        {
            db.OtpCodes.AddRange(
                new OtpCode { PhoneHash = otpHash, CodeHash = "issued-25h-ago", ExpiresAt = beforePass.AddHours(-25).AddMinutes(5), CreatedAt = beforePass.AddHours(-25) },
                new OtpCode { PhoneHash = otpHash, CodeHash = "issued-23h-ago", ExpiresAt = beforePass.AddHours(-23).AddMinutes(5), CreatedAt = beforePass.AddHours(-23) });
            var oldAudit = new ApiAuditLog { Timestamp = beforePass.AddYears(-2).AddDays(-1), ParkId = driver.ParkId, ActorType = "System", ApiProvider = "YandexFleet", Endpoint = "/test/retention-poll/" + tag, Method = "POST", ResponseCode = 200, Success = true };
            db.ApiAuditLogs.Add(oldAudit);
            await db.SaveChangesAsync();
            return oldAudit.Id;
        });
        Task<string[]> CodesLeftAsync() => _f.DbAsync(async db =>
            (await db.OtpCodes.AsNoTracking().Where(o => o.PhoneHash == otpHash).Select(o => o.CodeHash).ToListAsync()).OrderBy(c => c).ToArray());
        Task<bool> AuditLeftAsync() => _f.DbAsync(db => db.ApiAuditLogs.AnyAsync(a => a.Id == auditId));

        try
        {
            // An ordinary poll: the code past its 24 hours is gone, the call-log row past its 2 years waits for the night.
            await worker.TickAsync(beforePass);
            Assert.Equal(new[] { "issued-23h-ago" }, await CodesLeftAsync());
            Assert.True(await AuditLeftAsync(), "an ordinary poll deleted a call-log row — only the nightly pass may");

            // The poll that runs the nightly pass: the call-log row goes, and the other code — 26 hours old by now — too.
            await worker.TickAsync(afterPass);
            Assert.Empty(await CodesLeftAsync());
            Assert.False(await AuditLeftAsync(), "the nightly pass did not run");
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                await db.OtpCodes.Where(o => o.PhoneHash == otpHash).ExecuteDeleteAsync();
                return await db.ApiAuditLogs.Where(a => a.Id == auditId).ExecuteDeleteAsync();
            });
        }
    }

    [Fact]
    public async Task A_sign_in_with_the_sms_code_is_written_to_the_sign_in_history()
    {
        // The device session is deleted 90 days after its last use; the record that the driver signed in — and so
        // accepted the Terms of Use at that moment — is kept for 2 years in its own history.
        var driver = await DriverAsync();
        var since = DateTime.UtcNow.AddSeconds(-5);
        var phone = driver.PhoneEncrypted;
        var otp = await (await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone })).Content.ReadFromJsonAsync<JsonElement>();
        var res = await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code = otp.GetProperty("devCode").GetString() });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var refresh = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString();

        var history = await _f.DbAsync(db => db.DriverSignIns.AsNoTracking()
            .Where(x => x.DriverId == driver.Id && x.SignedInAt >= since).ToListAsync());
        var signIn = Assert.Single(history);
        Assert.Equal(driver.ParkId, signIn.ParkId);
        Assert.False(string.IsNullOrEmpty(signIn.DeviceLabel));

        // A token refresh is not a sign-in: the history does not grow.
        Assert.Equal(HttpStatusCode.OK, (await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refresh })).StatusCode);
        Assert.Equal(1, await _f.DbAsync(db => db.DriverSignIns.CountAsync(x => x.DriverId == driver.Id && x.SignedInAt >= since)));
    }
}
