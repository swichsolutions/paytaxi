using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Sign-in limits per phone across codes, over a rolling day: ten verifications that do not sign in lock the phone for
/// hours (no code is issued or checked meanwhile), and at most ten codes a day — both answered the same for a number no
/// driver has, so neither tells registered phones apart. And sign-out that holds: a driver who leaves Active or whose
/// phone changes loses every session and the access token in hand. Every test makes its own drivers and phones.
/// </summary>
[Collection("api")]
public class SignInLimitTests
{
    private const string Batumi = "batumi-auto-park-1";
    private readonly ApiFixture _f;
    public SignInLimitTests(ApiFixture f) => _f = f;

    private Task<HttpResponseMessage> RequestAsync(string phone) => _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone });
    private Task<HttpResponseMessage> VerifyAsync(string phone, string code) => _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code });

    private static async Task<(HttpStatusCode Status, string Code)> AnswerAsync(Task<HttpResponseMessage> call)
    {
        var res = await call;
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? "" : ApiFixture.Code(JsonDocument.Parse(text).RootElement));
    }

    private async Task<string> CodeAsync(string phone)
    {
        var res = await RequestAsync(phone);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("devCode").GetString()!;
    }

    private static string Wrong(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    private Task<int> EventsAsync(string phone, string kind) =>
        _f.DbAsync(db => db.Set<PhoneSignInEvent>().CountAsync(e => e.PhoneHash == TestDrivers.PhoneHash(phone) && e.Kind == kind));

    // ── C1: ten failed verifications lock the phone ─────────────────

    [Fact]
    public async Task Ten_wrong_codes_across_codes_lock_the_phone_and_the_park_sees_it()
    {
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi);
        try
        {
            // Five wrong guesses on each of two codes — a fresh code no longer buys a fresh budget.
            var first = await CodeAsync(phone);
            for (var i = 0; i < 5; i++) Assert.Equal("invalid_code", (await AnswerAsync(VerifyAsync(phone, Wrong(first)))).Code);
            var second = await CodeAsync(phone);
            for (var i = 0; i < 4; i++) Assert.Equal("invalid_code", (await AnswerAsync(VerifyAsync(phone, Wrong(second)))).Code);

            // The tenth locks: 429 with a code and when to try again, as the other too-many answers.
            var tenth = await VerifyAsync(phone, Wrong(second));
            Assert.Equal(HttpStatusCode.TooManyRequests, tenth.StatusCode);
            var body = await tenth.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("sign_in_locked", ApiFixture.Code(body));
            var retry = body.GetProperty("retryAfterSeconds").GetInt32();
            Assert.InRange(retry, 4 * 3600 - 60, 4 * 3600);   // Auth:SignInLockHours, default 4
            Assert.Equal(retry.ToString(), tenth.Headers.RetryAfter?.ToString());

            // Locked: no new code, and not even the right one is looked at.
            Assert.Equal((HttpStatusCode.TooManyRequests, "sign_in_locked"), await AnswerAsync(RequestAsync(phone)));
            Assert.Equal((HttpStatusCode.TooManyRequests, "sign_in_locked"), await AnswerAsync(VerifyAsync(phone, second)));
            Assert.False(await _f.DbAsync(db => db.DriverSessions.AnyAsync(s => s.DriverId == driverId)));

            // A security event on the park's banner (its manager's and Swich's), naming drivers by count only.
            var manager = await _f.ParkAdminAsync(Batumi);
            var alerts = await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
            var line = Assert.Single(alerts.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "signin_locked");
            Assert.Equal(1, line.GetProperty("count").GetInt32());
            Assert.DoesNotContain(phone, alerts.ToString());
            var swich = await _f.SuperAdminAsync();
            Assert.Contains((await swich.GetFromJsonAsync<JsonElement>("/api/admin/alerts")).GetProperty("alerts").EnumerateArray(),
                a => a.GetProperty("kind").GetString() == "signin_locked");

            // When the lock ends the right code signs in, and the banner clears.
            Task EndLockAsync() => _f.DbAsync(db => db.Set<PhoneSignInEvent>().Where(e => e.PhoneHash == TestDrivers.PhoneHash(phone) && e.Kind == PhoneSignInEventKinds.Locked)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Until, DateTime.UtcNow.AddSeconds(-1))));
            await EndLockAsync();
            var third = await CodeAsync(phone);
            Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(phone, third)).StatusCode);
            Assert.DoesNotContain((await manager.GetFromJsonAsync<JsonElement>("/api/admin/alerts")).GetProperty("alerts").EnumerateArray(),
                a => a.GetProperty("kind").GetString() == "signin_locked");

            // But the day's ten misses still count: ten per rolling 24 hours, not ten per lock. The next miss locks again.
            var fourth = await CodeAsync(phone);
            var relock = await AnswerAsync(VerifyAsync(phone, Wrong(fourth)));
            Assert.Equal((HttpStatusCode.TooManyRequests, "sign_in_locked"), relock);
            Assert.Equal(2, await EventsAsync(phone, PhoneSignInEventKinds.Locked));

            // Once those misses are a day old the phone has its full budget again: a miss is just a miss.
            await EndLockAsync();
            await _f.DbAsync(db => db.Set<PhoneSignInEvent>().Where(e => e.PhoneHash == TestDrivers.PhoneHash(phone) && e.Kind == PhoneSignInEventKinds.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.At, DateTime.UtcNow.AddHours(-25))));
            var fifth = await CodeAsync(phone);
            Assert.Equal("invalid_code", (await AnswerAsync(VerifyAsync(phone, Wrong(fifth)))).Code);
            Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(phone, fifth)).StatusCode);
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    [Fact]
    public async Task An_unknown_phone_is_locked_and_answered_exactly_like_a_driver_s()
    {
        var (driverId, registered, _) = await TestDrivers.CreateAsync(_f, Batumi);
        var unknown = TestDrivers.FreshPhone();
        try
        {
            // Ten verifications that do not sign in — a guessed code for the driver, any code for the unknown number.
            foreach (var phone in new[] { registered, unknown })
            {
                await RequestAsync(phone);
                for (var i = 0; i < 9; i++)
                {
                    var (status, _) = await AnswerAsync(VerifyAsync(phone, "000000"));
                    Assert.Equal(HttpStatusCode.Unauthorized, status);
                    if (i == 4) await RequestAsync(phone);
                }
            }
            var lockedRegistered = new[]
            {
                await AnswerAsync(VerifyAsync(registered, "000000")), await AnswerAsync(RequestAsync(registered)), await AnswerAsync(VerifyAsync(registered, "000000")),
            };
            var lockedUnknown = new[]
            {
                await AnswerAsync(VerifyAsync(unknown, "000000")), await AnswerAsync(RequestAsync(unknown)), await AnswerAsync(VerifyAsync(unknown, "000000")),
            };
            Assert.All(lockedRegistered, a => Assert.Equal((HttpStatusCode.TooManyRequests, "sign_in_locked"), a));
            Assert.Equal(lockedRegistered, lockedUnknown);
            Assert.Equal(1, await EventsAsync(unknown, PhoneSignInEventKinds.Locked));
        }
        finally
        {
            await TestDrivers.DeleteAsync(_f, driverId);
            await TestDrivers.ForgetPhoneAsync(_f, TestDrivers.PhoneHash(unknown));
        }
    }

    [Fact]
    public async Task Parallel_wrong_codes_lock_at_exactly_ten_failures()
    {
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi);
        try
        {
            var code = await CodeAsync(phone);
            var answers = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => AnswerAsync(VerifyAsync(phone, Wrong(code)))));
            // Serialised per phone with the lockout's own count: ten answers say why they failed, every later one is locked.
            Assert.Equal(9, answers.Count(a => a.Status == HttpStatusCode.Unauthorized));
            Assert.Equal(21, answers.Count(a => a == (HttpStatusCode.TooManyRequests, "sign_in_locked")));
            Assert.Equal(10, await EventsAsync(phone, PhoneSignInEventKinds.Failed));
            Assert.Equal(1, await EventsAsync(phone, PhoneSignInEventKinds.Locked));
            Assert.Equal((HttpStatusCode.TooManyRequests, "sign_in_locked"), await AnswerAsync(VerifyAsync(phone, code)));
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    // ── C2: at most ten codes per phone per day ──────────────────────

    [Fact]
    public async Task At_most_ten_codes_a_day_per_phone_known_or_not()
    {
        var (driverId, registered, _) = await TestDrivers.CreateAsync(_f, Batumi);
        var unknown = TestDrivers.FreshPhone();
        try
        {
            // Nine codes earlier today (spread out, so the per-10-minute cap is not what answers).
            var earlier = DateTime.UtcNow.AddHours(-20);
            await _f.DbAsync(db =>
            {
                foreach (var phone in new[] { registered, unknown })
                    for (var i = 0; i < 9; i++)
                        db.Set<PhoneSignInEvent>().Add(new PhoneSignInEvent { PhoneHash = TestDrivers.PhoneHash(phone), Kind = PhoneSignInEventKinds.CodeIssued, At = earlier.AddMinutes(30 * i) });
                return db.SaveChangesAsync();
            });

            foreach (var phone in new[] { registered, unknown })
            {
                Assert.Equal(HttpStatusCode.OK, (await RequestAsync(phone)).StatusCode);   // the tenth
                var eleventh = await RequestAsync(phone);
                Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
                var body = await eleventh.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("daily_code_limit", ApiFixture.Code(body));
                // Free again when the oldest of the day's codes is a day old: in about four hours here.
                Assert.InRange(body.GetProperty("retryAfterSeconds").GetInt32(), 4 * 3600 - 120, 4 * 3600 + 60);
                Assert.NotNull(eleventh.Headers.RetryAfter);
            }
            Assert.Equal(1, await _f.DbAsync(db => db.OtpCodes.CountAsync(o => o.PhoneHash == TestDrivers.PhoneHash(registered))));
        }
        finally
        {
            await TestDrivers.DeleteAsync(_f, driverId);
            await TestDrivers.ForgetPhoneAsync(_f, TestDrivers.PhoneHash(unknown));
        }
    }

    // ── C3: sign-out that holds ──────────────────────────────────────

    private async Task<(string Token, string Refresh)> SignInAsync(string phone)
    {
        var code = await CodeAsync(phone);
        var res = await VerifyAsync(phone, code);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<HttpStatusCode> MeAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/driver/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await _f.Client.SendAsync(req)).StatusCode;
    }

    private async Task<(HttpStatusCode, string)> RefreshAsync(string refresh) =>
        await AnswerAsync(_f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refresh }));

    [Fact]
    public async Task A_suspension_signs_the_driver_out_of_every_device_and_a_reactivation_does_not_bring_them_back()
    {
        var parkId = await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == Batumi).Select(p => p.Id).FirstAsync());
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi);
        try
        {
            var a = await SignInAsync(phone);
            var b = await SignInAsync(phone);
            Assert.Equal(HttpStatusCode.OK, await MeAsync(a.Token));
            var manager = await _f.ParkAdminAsync(Batumi);

            var suspend = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}", new { status = "Suspended" });
            Assert.Equal(2, (await suspend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionsRevoked").GetInt32());
            // The token in hand stops at once, and no refresh token works.
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(a.Token));
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(b.Token));
            Assert.Equal((HttpStatusCode.Unauthorized, "session_revoked"), await RefreshAsync(a.Refresh));
            var reasons = await _f.DbAsync(db => db.DriverSessions.AsNoTracking().Where(s => s.DriverId == driverId).Select(s => s.RevokedReason).ToListAsync());
            Assert.All(reasons, r => Assert.Equal("admin_revoked", r));

            // Back to Active: the old devices stay signed out — a new sign-in with an SMS code is needed.
            await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}", new { status = "Active" });
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(b.Token));
            Assert.Equal((HttpStatusCode.Unauthorized, "session_revoked"), await RefreshAsync(b.Refresh));
            var again = await SignInAsync(phone);
            Assert.Equal(HttpStatusCode.OK, await MeAsync(again.Token));
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    [Fact]
    public async Task A_phone_change_signs_the_driver_out_and_a_signed_out_device_s_token_stops_too()
    {
        var parkId = await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == Batumi).Select(p => p.Id).FirstAsync());
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi);
        var newPhone = TestDrivers.FreshPhone();
        try
        {
            var device = await SignInAsync(phone);
            var manager = await _f.ParkAdminAsync(Batumi);
            var change = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}", new { phone = newPhone });
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            Assert.Equal(1, (await change.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionsRevoked").GetInt32());
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(device.Token));
            Assert.Equal((HttpStatusCode.Unauthorized, "session_revoked"), await RefreshAsync(device.Refresh));

            // A name change signs nobody out.
            var onNewPhone = await SignInAsync(newPhone);
            var rename = await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}", new { name = "Nodar Renamed" });
            Assert.Equal(0, (await rename.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionsRevoked").GetInt32());
            Assert.Equal(HttpStatusCode.OK, await MeAsync(onNewPhone.Token));

            // Signing out ends that device's access token as well as its refresh token.
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Client.PostAsJsonAsync("/api/driver/auth/logout", new { refreshToken = onNewPhone.Refresh })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(onNewPhone.Token));
        }
        finally
        {
            await TestDrivers.ForgetPhoneAsync(_f, TestDrivers.PhoneHash(phone));
            await TestDrivers.DeleteAsync(_f, driverId);
        }
    }

    [Fact]
    public async Task Signing_out_and_a_suspension_end_the_token_at_once_where_the_check_is_cached()
    {
        // Production caches the session check for Auth:DriverRecheckSeconds (30 s; the test host 0). Signing out and the
        // console's change drop the driver's cached entries — sessions included — so neither waits for the cache: a
        // signed-out device's token, or one from before a suspension the park has already lifted again, is refused at once.
        var parkId = await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == Batumi).Select(p => p.Id).FirstAsync());
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi);
        var cached = TimeSpan.FromSeconds(30);
        Task<bool> RefusedAsync(string token) => TokenCheckProbe.RefusedAsync(_f, token, Api.Security.DriverTokenCheck.ValidateAsync, cached);
        try
        {
            var a = await SignInAsync(phone);
            Assert.False(await RefusedAsync(a.Token));
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Client.PostAsJsonAsync("/api/driver/auth/logout", new { refreshToken = a.Refresh })).StatusCode);
            Assert.True(await RefusedAsync(a.Token));

            var b = await SignInAsync(phone);
            Assert.False(await RefusedAsync(b.Token));
            var manager = await _f.ParkAdminAsync(Batumi);
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}", new { status = "Suspended" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await manager.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}", new { status = "Active" })).StatusCode);
            Assert.True(await RefusedAsync(b.Token));
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    [Fact]
    public async Task A_refreshed_token_keeps_working_and_the_one_it_replaced_lasts_while_its_chain_does()
    {
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi);
        try
        {
            var first = await SignInAsync(phone);
            var refreshed = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = first.Refresh });
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var next = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
            // A tab still holding the access token from before the refresh is not cut off by the rotation itself.
            Assert.Equal(HttpStatusCode.OK, await MeAsync(first.Token));
            Assert.Equal(HttpStatusCode.OK, await MeAsync(next.GetProperty("token").GetString()!));
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }
}
