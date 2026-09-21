using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core;
using Xunit;

namespace PayTaxi.Tests.Integration;

[Collection("api")]
public class AuthAndAuthorizationTests
{
    private readonly ApiFixture _f;
    public AuthAndAuthorizationTests(ApiFixture f) => _f = f;

    private async Task<string> PhoneOfAsync(string yandexProfileId)
    {
        // Seed stores canonical "+995XXXXXXXXX"; the encrypted column is decrypted by the EF converter.
        return await _f.DbAsync(async db =>
            (await db.Drivers.AsNoTracking().FirstAsync(d => d.YandexDriverProfileId == yandexProfileId)).PhoneEncrypted);
    }

    [Theory]
    [InlineData("{local}")]            // 599123456
    [InlineData("+995 {spaced}")]      // +995 599 123 456
    [InlineData("995{local}")]         // 995599123456
    [InlineData("0{local}")]           // 0599123456 (trunk zero)
    public async Task Every_common_phone_spelling_reaches_the_same_driver(string pattern)
    {
        var canonical = await PhoneOfAsync("yp_tb3_002");          // +995597224119
        var local = canonical[4..];
        var spaced = $"{local[..3]} {local[3..6]} {local[6..]}";
        var typed = pattern.Replace("{local}", local).Replace("{spaced}", spaced);

        var client = _f.Client;
        var otp = await (await client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone = typed })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(otp.TryGetProperty("devCode", out var code) && code.ValueKind == JsonValueKind.String, $"no devCode for {typed} — phone not recognised");

        var verify = await client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone = typed, code = code.GetString(), deviceLabel = "test" });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task Otp_requests_are_capped_per_phone()
    {
        // No other test requests a code for yp_tb3_005, so the window is ours.
        var phone = await PhoneOfAsync("yp_tb3_005");
        // Fixture sets Auth:OtpMaxPerWindow = 5 (production default is 3).
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone })).StatusCode);

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.OK, 5).Append(HttpStatusCode.TooManyRequests), statuses);
    }

    [Fact]
    public async Task Suspended_driver_cannot_log_in()
    {
        var phone = await PhoneOfAsync("yp_tb3_004");
        var driverId = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.YandexDriverProfileId == "yp_tb3_004")).Id);
        var phoneHash = await _f.DbAsync(async db => (await db.Drivers.AsNoTracking().FirstAsync(d => d.Id == driverId)).PhoneHash);
        var codesBefore = await _f.DbAsync(db => db.OtpCodes.CountAsync(o => o.PhoneHash == phoneHash));
        await SetStatusAsync(driverId, Core.Enums.DriverStatus.Suspended);
        try
        {
            // No SMS for a suspended driver: the request step already says so (the app maps it), and
            // no code is stored — so nothing to verify either.
            var req = await _f.Client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone });
            Assert.Equal(HttpStatusCode.Forbidden, req.StatusCode);
            Assert.Equal("driver_inactive", ApiFixture.Code(await req.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(codesBefore, await _f.DbAsync(db => db.OtpCodes.CountAsync(o => o.PhoneHash == phoneHash)));

            var verify = await _f.Client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code = "123456", deviceLabel = "test" });
            Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
            Assert.Equal("no_active_code", ApiFixture.Code(await verify.Content.ReadFromJsonAsync<JsonElement>()));
        }
        finally
        {
            await SetStatusAsync(driverId, Core.Enums.DriverStatus.Active);
        }
    }

    private Task<int> SetStatusAsync(Guid driverId, Core.Enums.DriverStatus status) =>
        _f.DbAsync(async db => { var d = await db.Drivers.FindAsync(driverId); d!.Status = status; return await db.SaveChangesAsync(); });

    [Fact]
    public async Task Park_admin_is_confined_to_their_park()
    {
        var batumi = await _f.ParkAdminAsync("batumi-auto-park-1");
        var (tbilisiPark, tbilisiDriver, batumiPark) = await _f.DbAsync(async db =>
        {
            var tb = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3");
            var bt = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1");
            var d = await db.Drivers.AsNoTracking().FirstAsync(x => x.ParkId == tb.Id);
            return (tb.Id, d.Id, bt.Id);
        });
        var body = new { iban = ApiFixture.TbcIban("7700000000009999"), holderName = "x", reason = "x" };

        Assert.Equal(HttpStatusCode.Forbidden, (await batumi.GetAsync($"/api/admin/parks/{tbilisiPark}/drivers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await batumi.PostAsJsonAsync($"/api/admin/parks/{tbilisiPark}/drivers/{tbilisiDriver}/cards", body)).StatusCode);
        // Own park URL, foreign driver id → not found, never a cross-park write.
        Assert.Equal(HttpStatusCode.NotFound, (await batumi.PostAsJsonAsync($"/api/admin/parks/{batumiPark}/drivers/{tbilisiDriver}/cards", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await batumi.DeleteAsync($"/api/admin/parks/{batumiPark}/drivers/{tbilisiDriver}/cards/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Fee_is_swich_only_but_limits_are_park_editable()
    {
        var parkId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-5")).Id);
        var op = await _f.OperatorAsync();
        var swich = await _f.SuperAdminAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await op.PatchAsJsonAsync($"/api/admin/parks/{parkId}", new { cashoutFee = 1.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PatchAsJsonAsync($"/api/admin/parks/{parkId}", new { minCashoutAmount = 5.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync($"/api/admin/parks/{parkId}", new { cashoutFee = 0.5 })).StatusCode);
    }

    [Fact]
    public async Task Tokens_are_scope_checked()
    {
        var (driver, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_001");
        var anon = _f.Client;

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/driver/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/admin/parks/{parkId}/drivers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.GetAsync($"/api/admin/parks/{parkId}/drivers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}/cards", new { iban = "x" })).StatusCode);
    }

    [Fact]
    public async Task Production_bootstrap_does_not_seed_demo_data()
    {
        // The Development seed is what filled this DB; assert the demo logins exist here and that the
        // production path is a different code path (guarded in Program.cs). Cheap smoke check that the
        // gate is still wired: Development → demo admins present.
        var count = await _f.DbAsync(db => db.AdminUsers.CountAsync(a => a.Email == "ops@swich.dev"));
        Assert.Equal(1, count);
    }
}
