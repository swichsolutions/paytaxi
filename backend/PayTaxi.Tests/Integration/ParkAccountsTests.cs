using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>The park's payout accounts: credentials must be JSON, and "primary" must always point at an active account.</summary>
[Collection("api")]
public class ParkAccountsTests
{
    private readonly ApiFixture _f;
    public ParkAccountsTests(ApiFixture f) => _f = f;

    private async Task<(Guid parkId, JsonElement[] accounts)> LevanAccountsAsync(HttpClient admin)
    {
        var parkId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-3")).Id);
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/bank-accounts");
        return (parkId, list.GetProperty("accounts").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task Credentials_must_be_a_json_object_on_create_and_update()
    {
        var admin = await _f.SuperAdminAsync();
        var (parkId, accounts) = await LevanAccountsAsync(admin);
        var primary = accounts.First(a => a.GetProperty("isPrimary").GetBoolean());
        var primaryId = primary.GetProperty("id").GetGuid();

        foreach (var bad in new[] { "{not json", "[1,2]", "\"a string\"", "42" })
        {
            var upd = await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{primaryId}", new { credentialsJson = bad });
            Assert.Equal(HttpStatusCode.BadRequest, upd.StatusCode);
            Assert.Equal("invalid_credentials_json", ApiFixture.Code(await upd.Content.ReadFromJsonAsync<JsonElement>()));

            var create = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts",
                new { iban = ApiFixture.TbcIban("9900000000000077"), holderName = "x", credentialsJson = bad });
            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
            Assert.Equal("invalid_credentials_json", ApiFixture.Code(await create.Content.ReadFromJsonAsync<JsonElement>()));
        }

        // The stored value is untouched by the refused updates; a proper object is accepted (blank keeps).
        var ok = await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{primaryId}", new { credentialsJson = "{\"mock\": true}" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var stored = await _f.DbAsync(async db => (await db.ParkBankAccounts.AsNoTracking().FirstAsync(a => a.Id == primaryId)).CredentialsEncrypted);
        Assert.Contains("mock", stored);
    }

    [Fact]
    public async Task Deactivating_the_primary_hands_primary_to_another_active_account()
    {
        var admin = await _f.SuperAdminAsync();
        var (parkId, accounts) = await LevanAccountsAsync(admin);
        var primary = accounts.First(a => a.GetProperty("isPrimary").GetBoolean());
        var primaryId = primary.GetProperty("id").GetGuid();
        var otherActive = accounts.FirstOrDefault(a => !a.GetProperty("isPrimary").GetBoolean() && a.GetProperty("isActive").GetBoolean());
        Assert.True(otherActive.ValueKind == JsonValueKind.Object, "seed gives Levan's park a second active account");
        var otherId = otherActive.GetProperty("id").GetGuid();

        try
        {
            var res = await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{primaryId}", new { isActive = false });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("isActive").GetBoolean());
            Assert.False(body.GetProperty("isPrimary").GetBoolean(), "an inactive account must not stay primary");

            var (_, after) = await LevanAccountsAsync(admin);
            var newPrimary = Assert.Single(after, a => a.GetProperty("isPrimary").GetBoolean());
            Assert.Equal(otherId, newPrimary.GetProperty("id").GetGuid());
            Assert.True(newPrimary.GetProperty("isActive").GetBoolean());

            // The park mirror follows the new primary.
            var park = await _f.DbAsync(async db => await db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId));
            Assert.Equal(newPrimary.GetProperty("iban").GetString(), park.BankAccountIban);
        }
        finally
        {
            await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{primaryId}", new { isActive = true, isPrimary = true });
        }

        var (_, restored) = await LevanAccountsAsync(admin);
        Assert.True(restored.First(a => a.GetProperty("id").GetGuid() == primaryId).GetProperty("isPrimary").GetBoolean());
    }

    [Fact]
    public async Task Deactivating_the_only_active_account_keeps_it_primary_so_nothing_is_lost()
    {
        var admin = await _f.SuperAdminAsync();
        var parkId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "tbilisi-auto-park-5")).Id);
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/parks/{parkId}/bank-accounts");
        var accounts = list.GetProperty("accounts").EnumerateArray().ToArray();
        var active = accounts.Where(a => a.GetProperty("isActive").GetBoolean()).ToArray();
        var primaryId = accounts.First(a => a.GetProperty("isPrimary").GetBoolean()).GetProperty("id").GetGuid();
        try
        {
            foreach (var a in active.Where(a => a.GetProperty("id").GetGuid() != primaryId))
                await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{a.GetProperty("id").GetGuid()}", new { isActive = false });
            var res = await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{primaryId}", new { isActive = false });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("isPrimary").GetBoolean(), "with no other active account the flag has nowhere to go");
        }
        finally
        {
            foreach (var a in active)
                await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{a.GetProperty("id").GetGuid()}", new { isActive = true });
            await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/bank-accounts/{primaryId}", new { isPrimary = true });
        }
    }
}
