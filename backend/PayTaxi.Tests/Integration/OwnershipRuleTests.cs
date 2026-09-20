using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Payout-account ownership: a driver may only add an account in his own name; the park may
/// register a third party's account with a reason; the flag follows the account everywhere.
/// Mirrors the manual probes from 2026-09-20 so they can never silently regress.
/// </summary>
[Collection("api")]
public class OwnershipRuleTests
{
    private readonly ApiFixture _f;
    public OwnershipRuleTests(ApiFixture f) => _f = f;

    // Seeded: yp_tb3_003 = "Levan Kobakhidze" (Latin), yp_tb3_007 = "ვალერი თავაძე" (Georgian). Both Active.

    [Theory]
    [InlineData("yp_tb3_007", "Valeri Tavadze")]       // Georgian-registered, Latin typed
    [InlineData("yp_tb3_003", "ლევან კობახიძე")]         // Latin-registered, Georgian typed
    [InlineData("yp_tb3_003", "Kobakhidze")]            // surname only
    [InlineData("yp_tb3_003", "L. Kobakhidze")]         // initial + surname
    [InlineData("yp_tb3_003", "Levan Giorgi Kobakhidze")] // extra middle name
    [InlineData("yp_tb3_003", "Levan Kobahidze")]       // kh written h
    [InlineData("yp_tb3_003", "")]                      // empty → defaults to the registered name
    public async Task Driver_can_add_account_in_own_name(string profile, string holder)
    {
        var (client, _, _) = await _f.DriverClientAsync(profile);
        var iban = ApiFixture.TbcIban(Tail());
        var res = await client.PostAsJsonAsync("/api/driver/me/cards", new { iban, holderName = holder });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.False(body.GetProperty("isThirdPartyAccount").GetBoolean());
        Assert.StartsWith("driver:", body.GetProperty("addedBy").GetString());

        await client.DeleteAsync($"/api/driver/me/cards/{body.GetProperty("id").GetGuid()}");
    }

    [Theory]
    [InlineData("yp_tb3_007", "ნინო ბერიძე")]        // different person, same script
    [InlineData("yp_tb3_003", "Nino Beridze")]         // different person, Latin
    [InlineData("yp_tb3_003", "Levan Kobakhadze")]     // typo that changes a sound
    public async Task Driver_is_refused_for_someone_elses_name(string profile, string holder)
    {
        var (client, _, _) = await _f.DriverClientAsync(profile);
        var res = await client.PostAsJsonAsync("/api/driver/me/cards", new { iban = ApiFixture.TbcIban(Tail()), holderName = holder });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("holder_name_mismatch", ApiFixture.Code(body));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("registeredName").GetString()));
    }

    [Fact]
    public async Task Admin_needs_a_reason_for_a_third_party_account_and_the_flag_is_visible_everywhere()
    {
        var (driver, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_003");
        var admin = await _f.SuperAdminAsync();
        var iban = ApiFixture.TbcIban(Tail());
        var url = $"/api/admin/parks/{parkId}/drivers/{driverId}/cards";

        // Without a reason → refused with a typed code.
        var noReason = await admin.PostAsJsonAsync(url, new { iban, holderName = "ნინო ბერიძე" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal("third_party_reason_required", ApiFixture.Code(await noReason.Content.ReadFromJsonAsync<JsonElement>()));

        // Whitespace is not a reason.
        var blank = await admin.PostAsJsonAsync(url, new { iban, holderName = "ნინო ბერიძე", reason = "   " });
        Assert.Equal("third_party_reason_required", ApiFixture.Code(await blank.Content.ReadFromJsonAsync<JsonElement>()));

        // With a reason → created, flagged, attributed to the admin.
        var ok = await admin.PostAsJsonAsync(url, new { iban, holderName = "ნინო ბერიძე", reason = "wife; driver has no account; statement on file" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var card = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(card.GetProperty("isThirdPartyAccount").GetBoolean());
        Assert.Equal("admin:ops@swich.dev", card.GetProperty("addedBy").GetString());
        var cardId = card.GetProperty("id").GetGuid();

        // Driver list carries the flag.
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/drivers");
        var mine = list.GetProperty("drivers").EnumerateArray().First(d => d.GetProperty("id").GetGuid() == driverId);
        var flagged = mine.GetProperty("cards").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == cardId);
        Assert.True(flagged.GetProperty("isThirdPartyAccount").GetBoolean());
        Assert.Equal("ნინო ბერიძე", flagged.GetProperty("holderName").GetString());

        // The driver sees it and may pay out to it.
        var me = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me");
        Assert.Contains(me.GetProperty("cards").EnumerateArray(), c => c.GetProperty("id").GetGuid() == cardId);

        // Laundering: driver removes it and re-adds the same IBAN under his own name → locked.
        (await driver.DeleteAsync($"/api/driver/me/cards/{cardId}")).EnsureSuccessStatusCode();
        var relaunder = await driver.PostAsJsonAsync("/api/driver/me/cards", new { iban, holderName = "Levan Kobakhidze" });
        Assert.Equal(HttpStatusCode.BadRequest, relaunder.StatusCode);
        Assert.Equal("third_party_account_locked", ApiFixture.Code(await relaunder.Content.ReadFromJsonAsync<JsonElement>()));

        // Only the park can re-register it under the driver's own name (clears the flag).
        var readd = await admin.PostAsJsonAsync(url, new { iban, holderName = "Levan Kobakhidze" });
        Assert.Equal(HttpStatusCode.Created, readd.StatusCode);
        var readdBody = await readd.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(readdBody.GetProperty("isThirdPartyAccount").GetBoolean());
        await admin.DeleteAsync($"{url}/{readdBody.GetProperty("id").GetGuid()}");
    }

    [Fact]
    public async Task Overlong_input_is_a_400_not_a_500()
    {
        var (_, driverId, parkId) = await _f.DriverClientAsync("yp_tb3_003");
        var admin = await _f.SuperAdminAsync();
        var url = $"/api/admin/parks/{parkId}/drivers/{driverId}/cards";

        var longHolder = await admin.PostAsJsonAsync(url, new { iban = ApiFixture.TbcIban(Tail()), holderName = new string('N', 250), reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, longHolder.StatusCode);
        Assert.Equal("holder_name_too_long", ApiFixture.Code(await longHolder.Content.ReadFromJsonAsync<JsonElement>()));

        var longReason = await admin.PostAsJsonAsync(url, new { iban = ApiFixture.TbcIban(Tail()), holderName = "ნინო ბერიძე", reason = new string('x', 600) });
        Assert.Equal(HttpStatusCode.BadRequest, longReason.StatusCode);
        Assert.Equal("reason_too_long", ApiFixture.Code(await longReason.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task Unnamed_driver_is_not_blocked_by_the_placeholder()
    {
        var (client, driverId, _) = await _f.DriverClientAsync("yp_tb3_006"); // "Zura Mikeladze"
        var original = await _f.DbAsync(async db => (await db.Drivers.FindAsync(driverId))!.Name);
        await _f.DbAsync(async db => { var d = await db.Drivers.FindAsync(driverId); d!.Name = "(unnamed)"; return await db.SaveChangesAsync(); });
        try
        {
            var res = await client.PostAsJsonAsync("/api/driver/me/cards", new { iban = ApiFixture.TbcIban(Tail()), holderName = "Zura Mikeladze" });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            await client.DeleteAsync($"/api/driver/me/cards/{body.GetProperty("id").GetGuid()}");
        }
        finally
        {
            await _f.DbAsync(async db => { var d = await db.Drivers.FindAsync(driverId); d!.Name = original; return await db.SaveChangesAsync(); });
        }
    }

    private static long _seq = DateTime.UtcNow.Ticks % 1_000_000;
    /// <summary>Unique 16-digit account tail per call so tests never collide on the same IBAN.</summary>
    private static string Tail() => (Interlocked.Increment(ref _seq)).ToString("D16");
}
