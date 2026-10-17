using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// GET /api/driver/me carries the controller's identity — the park's legal name, identification code
/// and phone — for the "Your park" card in the driver's Profile, which the privacy policy points to
/// (Law on Personal Data Protection, Art. 24(1)(a)). Each driver sees only their own park's details.
/// </summary>
[Collection("api")]
public class DriverParkDetailsTests
{
    private const string BatumiSlug = "batumi-auto-park-1";
    private const string TbilisiSlug = "tbilisi-auto-park-5";

    private readonly ApiFixture _f;
    public DriverParkDetailsTests(ApiFixture f) => _f = f;

    private record ParkDetails(Guid Id, string Name, string? LegalEntityName, string? TaxId, string? Phone);

    private Task<ParkDetails> ParkAsync(string slug) => _f.DbAsync(async db =>
    {
        var p = await db.Parks.AsNoTracking().FirstAsync(x => x.Slug == slug);
        return new ParkDetails(p.Id, p.Name, p.LegalEntityName, p.TaxId, p.Phone);
    });

    private Task<string> AnyDriverOfAsync(Guid parkId) => _f.DbAsync(async db =>
        (await db.Drivers.AsNoTracking()
            .Where(d => d.ParkId == parkId && d.YandexDriverProfileId != null)
            .OrderBy(d => d.YandexDriverProfileId)
            .FirstAsync()).YandexDriverProfileId!);

    private static string? Str(JsonElement park, string name) =>
        park.GetProperty(name).ValueKind == JsonValueKind.Null ? null : park.GetProperty(name).GetString();

    [Fact]
    public async Task Me_returns_the_drivers_own_park_legal_name_id_code_and_phone()
    {
        var batumi = await ParkAsync(BatumiSlug);
        var tbilisi = await ParkAsync(TbilisiSlug);
        // The seed gives every park its legal details; the test is meaningless without them.
        Assert.False(string.IsNullOrWhiteSpace(batumi.LegalEntityName));
        Assert.False(string.IsNullOrWhiteSpace(batumi.TaxId));
        Assert.False(string.IsNullOrWhiteSpace(batumi.Phone));

        var (batumiDriver, _, _) = await _f.DriverClientAsync(await AnyDriverOfAsync(batumi.Id));
        var park = (await batumiDriver.GetFromJsonAsync<JsonElement>("/api/driver/me")).GetProperty("park");
        Assert.Equal(batumi.Id, park.GetProperty("id").GetGuid());
        Assert.Equal(batumi.Name, park.GetProperty("name").GetString());
        Assert.Equal(batumi.LegalEntityName, Str(park, "legalEntityName"));
        Assert.Equal(batumi.TaxId, Str(park, "taxId"));
        Assert.Equal(batumi.Phone, Str(park, "phone"));

        // Another park's driver gets that park's details, never Batumi's.
        var (tbilisiDriver, _, _) = await _f.DriverClientAsync(await AnyDriverOfAsync(tbilisi.Id));
        var other = (await tbilisiDriver.GetFromJsonAsync<JsonElement>("/api/driver/me")).GetProperty("park");
        Assert.Equal(tbilisi.TaxId, Str(other, "taxId"));
        Assert.NotEqual(batumi.TaxId, Str(other, "taxId"));
        Assert.Equal(tbilisi.LegalEntityName, Str(other, "legalEntityName"));
    }

    [Fact]
    public async Task Me_sends_explicit_nulls_while_the_park_has_not_entered_its_legal_details()
    {
        var batumi = await ParkAsync(BatumiSlug);
        var (driver, _, _) = await _f.DriverClientAsync(await AnyDriverOfAsync(batumi.Id));
        await _f.DbAsync(async db =>
        {
            var p = await db.Parks.FirstAsync(x => x.Id == batumi.Id);
            p.LegalEntityName = null; p.TaxId = null; p.Phone = null;
            return await db.SaveChangesAsync();
        });
        try
        {
            var park = (await driver.GetFromJsonAsync<JsonElement>("/api/driver/me")).GetProperty("park");
            // Present and null — the app then falls back to the park's display name.
            Assert.Equal(JsonValueKind.Null, park.GetProperty("legalEntityName").ValueKind);
            Assert.Equal(JsonValueKind.Null, park.GetProperty("taxId").ValueKind);
            Assert.Equal(JsonValueKind.Null, park.GetProperty("phone").ValueKind);
            Assert.Equal(batumi.Name, park.GetProperty("name").GetString());
        }
        finally
        {
            await _f.DbAsync(async db =>
            {
                var p = await db.Parks.FirstAsync(x => x.Id == batumi.Id);
                p.LegalEntityName = batumi.LegalEntityName; p.TaxId = batumi.TaxId; p.Phone = batumi.Phone;
                return await db.SaveChangesAsync();
            });
        }
    }
}
