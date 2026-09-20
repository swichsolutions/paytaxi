using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>Trusted-device sessions (90-day refresh tokens), notifications, rides.</summary>
[Collection("api")]
public class SessionsAndNotificationsTests
{
    private readonly ApiFixture _f;
    public SessionsAndNotificationsTests(ApiFixture f) => _f = f;

    private async Task<JsonElement> LoginViaOtpAsync(string yandexProfileId, string device)
    {
        var phone = await _f.DbAsync(async db =>
            (await db.Drivers.AsNoTracking().FirstAsync(d => d.YandexDriverProfileId == yandexProfileId)).PhoneEncrypted);
        var otp = await (await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone })).Content.ReadFromJsonAsync<JsonElement>();
        var res = await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp",
            new { phone, code = otp.GetProperty("devCode").GetString(), deviceLabel = device });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpClient WithBearer(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Refresh_rotates_and_a_replayed_token_revokes_every_session()
    {
        // yp_tb3_006 is used here only; two devices.
        var phoneA = await LoginViaOtpAsync("yp_tb3_006", "phone-A");
        var phoneB = await LoginViaOtpAsync("yp_tb3_006", "phone-B");
        var refreshA = phoneA.GetProperty("refreshToken").GetString()!;
        var refreshB = phoneB.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(refreshA, refreshB);
        Assert.True(phoneA.GetProperty("refreshExpiresAt").GetDateTime() > DateTime.UtcNow.AddDays(80), "90-day trusted device");

        // Rotate A: new pair, old one dead.
        var r1 = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refreshA });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var rotated = await r1.Content.ReadFromJsonAsync<JsonElement>();
        var refreshA2 = rotated.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(refreshA, refreshA2);
        Assert.False(string.IsNullOrEmpty(rotated.GetProperty("token").GetString()));

        // Replay the OLD A token → treated as theft: A2 and B are both revoked.
        var replay = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refreshA });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("session_revoked", ApiFixture.Code(await replay.Content.ReadFromJsonAsync<JsonElement>()));

        var a2 = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refreshA2 });
        Assert.Equal(HttpStatusCode.Unauthorized, a2.StatusCode);
        var b = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = refreshB });
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);

        // Nothing left alive for that driver.
        var alive = await _f.DbAsync(async db =>
        {
            var id = (await db.Drivers.AsNoTracking().FirstAsync(d => d.YandexDriverProfileId == "yp_tb3_006")).Id;
            return await db.DriverSessions.CountAsync(s => s.DriverId == id && s.RevokedAt == null);
        });
        Assert.Equal(0, alive);
    }

    [Fact]
    public async Task Logout_kills_only_that_device_and_garbage_tokens_are_rejected()
    {
        // yp_tb3_008: no other test requests OTP codes for this phone (the spelling theory eats 002's quota).
        var a = await LoginViaOtpAsync("yp_tb3_008", "phone-A");
        var b = await LoginViaOtpAsync("yp_tb3_008", "phone-B");

        var client = WithBearer(_f.Client, a.GetProperty("token").GetString()!);
        var logout = await client.PostAsJsonAsync("/api/driver/auth/logout", new { refreshToken = a.GetProperty("refreshToken").GetString() });
        Assert.True(logout.IsSuccessStatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        // The logged-out phone fires one more refresh from its cache (happens in practice): refused,
        // but it must NOT be mistaken for token theft — phone B stays signed in.
        var aAgain = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = a.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, aAgain.StatusCode);
        var bStill = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = b.GetProperty("refreshToken").GetString() });
        Assert.True(bStill.StatusCode == HttpStatusCode.OK, "device B refresh: " + await bStill.Content.ReadAsStringAsync());

        var junk = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = "not-a-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, junk.StatusCode);
        var empty = await _f.Client.PostAsJsonAsync("/api/driver/auth/refresh", new { refreshToken = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Cashout_writes_a_structured_notification_and_read_state_works()
    {
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_007");
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        var card = me.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("isDefault").GetBoolean());
        var cardId = card.GetProperty("id").GetGuid();
        var bank = card.GetProperty("bankType").GetString()!;
        var saga = await (await driver.PostAsJsonAsync("/api/driver/cashouts", new { cardId, amount = 5m, idempotencyKey = Guid.NewGuid().ToString() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", saga.GetProperty("status").GetString());

        var list = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me/notifications?take=5");
        var items = list.GetProperty("notifications").EnumerateArray().ToList();
        var latest = items.First();
        Assert.Equal("cashout_completed", latest.GetProperty("type").GetString());
        Assert.False(latest.GetProperty("isRead").GetBoolean());
        var data = JsonDocument.Parse(latest.GetProperty("data").GetString()!).RootElement;
        Assert.Equal(4.5m, data.GetProperty("amount").GetDecimal());          // net = 5 − 0.50
        Assert.Contains(bank, data.GetProperty("destination").GetString());   // the label names the bank the money went to

        var id = latest.GetProperty("id").GetGuid();
        Assert.True((await driver.PostAsync($"/api/driver/me/notifications/{id}/read", null)).IsSuccessStatusCode);
        var after = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me/notifications?take=5");
        Assert.True(after.GetProperty("notifications").EnumerateArray().First(n => n.GetProperty("id").GetGuid() == id).GetProperty("isRead").GetBoolean());

        Assert.True((await driver.PostAsync("/api/driver/me/notifications/read-all", null)).IsSuccessStatusCode);
        var unread = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me/notifications?unreadOnly=true");
        Assert.Empty(unread.GetProperty("notifications").EnumerateArray());
    }

    [Fact]
    public async Task Rides_come_from_yandex_clamped_and_cached()
    {
        var (driver, _, _) = await _f.DriverClientAsync("yp_tb3_001");
        var r = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me/rides?days=99");
        Assert.Equal(30, r.GetProperty("days").GetInt32());                    // clamped to 30
        Assert.True(r.GetProperty("count").GetInt32() > 0);
        var first = r.GetProperty("rides").EnumerateArray().First();
        foreach (var k in new[] { "orderId", "amount", "createdAt" }) Assert.True(first.TryGetProperty(k, out _), k);

        // Unlinked driver → empty list with a note, never an error.
        var (_, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_004");
        var original = await _f.DbAsync(async db => (await db.Drivers.FindAsync(driverId))!.YandexDriverProfileId);
        await _f.DbAsync(async db => { var d = await db.Drivers.FindAsync(driverId); d!.YandexDriverProfileId = null; return await db.SaveChangesAsync(); });
        try
        {
            var jwt = (PayTaxi.Core.Interfaces.IJwtTokenService)_f.Services.GetService(typeof(PayTaxi.Core.Interfaces.IJwtTokenService))!;
            var phoneHash = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(x => x.Id == driverId)).PhoneHash);
            var c = WithBearer(_f.Client, jwt.IssueDriverToken(driverId, parkId, phoneHash).Token);
            var u = await c.GetFromJsonAsync<JsonElement>("/api/driver/me/rides");
            c.DefaultRequestHeaders.Authorization = null;
            Assert.Equal(0, u.GetProperty("count").GetInt32());
            Assert.Equal("driver_not_linked_to_yandex", u.GetProperty("note").GetString());
        }
        finally
        {
            await _f.DbAsync(async db => { var d = await db.Drivers.FindAsync(driverId); d!.YandexDriverProfileId = original; return await db.SaveChangesAsync(); });
        }
    }
}
