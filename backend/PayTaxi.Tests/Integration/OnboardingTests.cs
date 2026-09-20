using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>Park-side driver onboarding, including the IBAN collected at the desk.</summary>
[Collection("api")]
public class OnboardingTests
{
    private readonly ApiFixture _f;
    public OnboardingTests(ApiFixture f) => _f = f;

    private static long _seq = DateTime.UtcNow.Ticks % 100_000;
    private static string NewPhone() => "+9955" + (10_000_000 + Interlocked.Increment(ref _seq) % 89_999_999).ToString("D8");
    private static string NewYandexId() => "yp_test_" + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> ParkAsync(string slug) =>
        await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == slug)).Id);

    [Fact]
    public async Task Onboarding_with_the_drivers_own_iban_creates_an_unflagged_default_account()
    {
        var admin = await _f.SuperAdminAsync();
        var parkId = await ParkAsync("tbilisi-auto-park-5");
        var res = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers", new
        {
            phone = NewPhone(), yandexProfileId = NewYandexId(), name = "თემურ გოგიჩაიშვილი", consentGiven = true,
            iban = ApiFixture.TbcIban(DateTime.UtcNow.Ticks.ToString().PadLeft(16, '0')[^16..]), holderName = "Temur Gogichaishvili",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var driverId = body.GetProperty("id").GetGuid();

        var card = await _f.DbAsync(db => db.BankCards.AsNoTracking().SingleAsync(c => c.DriverId == driverId));
        Assert.True(card.IsDefault);
        Assert.False(card.IsThirdPartyAccount);
        Assert.Equal("Temur Gogichaishvili", card.HolderName);
        Assert.Equal("admin:ops@swich.dev", card.AddedBy);
    }

    [Fact]
    public async Task Onboarding_with_someone_elses_iban_needs_a_reason_and_creates_nothing_without_one()
    {
        var admin = await _f.SuperAdminAsync();
        var parkId = await ParkAsync("tbilisi-auto-park-5");
        var phone = NewPhone();
        var yandexId = NewYandexId();
        var iban = ApiFixture.TbcIban(DateTime.UtcNow.Ticks.ToString().PadLeft(16, '0')[^16..]);

        // No reason → refused, and the driver row must NOT have been created (single transaction).
        var refused = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers", new
        {
            phone, yandexProfileId = yandexId, name = "გია ლომიძე", consentGiven = true,
            iban, holderName = "ნინო ბერიძე",
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("third_party_reason_required", ApiFixture.Code(await refused.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(0, await _f.DbAsync(db => db.Drivers.CountAsync(d => d.YandexDriverProfileId == yandexId)));

        // With a reason → created, and the destination is flagged.
        var ok = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers", new
        {
            phone, yandexProfileId = yandexId, name = "გია ლომიძე", consentGiven = true,
            iban, holderName = "ნინო ბერიძე", reason = "driver has no bank account; wife; statement signed at the desk",
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var driverId = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var card = await _f.DbAsync(db => db.BankCards.AsNoTracking().SingleAsync(c => c.DriverId == driverId));
        Assert.True(card.IsThirdPartyAccount);
        Assert.Contains("wife", card.ThirdPartyReason);
    }

    [Fact]
    public async Task Duplicate_phone_or_yandex_id_is_a_conflict_and_status_values_are_validated()
    {
        var admin = await _f.SuperAdminAsync();
        var parkId = await ParkAsync("tbilisi-auto-park-5");
        var existing = await _f.DbAsync(async db => await db.Drivers.AsNoTracking().FirstAsync(d => d.ParkId == parkId));

        var dupPhone = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers",
            new { phone = existing.PhoneEncrypted, yandexProfileId = NewYandexId(), name = "X Y", consentGiven = true });
        Assert.Equal(HttpStatusCode.Conflict, dupPhone.StatusCode);
        Assert.Equal("phone_already_registered", ApiFixture.Code(await dupPhone.Content.ReadFromJsonAsync<JsonElement>()));

        var dupYandex = await admin.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers",
            new { phone = NewPhone(), yandexProfileId = existing.YandexDriverProfileId, name = "X Y", consentGiven = true });
        Assert.Equal(HttpStatusCode.Conflict, dupYandex.StatusCode);

        var badStatus = await admin.PatchAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{existing.Id}", new { status = "Inactive" });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        var allowed = (await badStatus.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allowed").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(new[] { "Active", "Suspended", "Pending" }, allowed);
    }
}
