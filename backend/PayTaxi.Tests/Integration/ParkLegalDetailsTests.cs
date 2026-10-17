using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Enums;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The driver's privacy notice names the park as the controller of the driver's data, through Profile → "Your park":
/// its legal name, identification code and phone. A park that takes cashouts (Active) must therefore carry all three —
/// on creation, on every edit, and before a suspended park is reactivated. Each test uses parks it creates itself
/// (Swich-direct, so no licensee choice is involved) and deletes them afterwards.
/// </summary>
[Collection("api")]
public class ParkLegalDetailsTests
{
    private readonly ApiFixture _f;
    public ParkLegalDetailsTests(ApiFixture f) => _f = f;

    private static readonly string[] AllThree = { "legalEntityName", "taxId", "phone" };

    private static async Task<(HttpStatusCode Status, JsonElement Body)> CreateAsync(HttpClient swich, string tail, object? legal)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = $"Legal park {tail}", ["yandexParkId"] = $"yx_legal_{tail}", ["direct"] = true,
            ["bankAccountIban"] = ApiFixture.TbcIban("7100000000" + tail),
        };
        if (legal is not null)
            foreach (var p in legal.GetType().GetProperties()) body[p.Name] = p.GetValue(legal);
        var res = await swich.PostAsJsonAsync("/api/admin/parks", body);
        return (res.StatusCode, await res.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string[] Missing(JsonElement body) =>
        body.GetProperty("missing").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private Task DeleteAsync(List<Guid> ids) => _f.DbAsync(db => db.Parks.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync());

    [Fact]
    public async Task A_new_park_needs_its_legal_name_identification_code_and_phone()
    {
        var swich = await _f.SuperAdminAsync();
        var created = new List<Guid>();
        var tail = Random.Shared.Next(10_000, 99_999).ToString();
        try
        {
            var (none, noneBody) = await CreateAsync(swich, tail + "0", null);
            Assert.Equal(HttpStatusCode.BadRequest, none);
            Assert.Equal("legal_details_required", ApiFixture.Code(noneBody));
            Assert.Equal(AllThree, Missing(noneBody));

            var (noPhone, noPhoneBody) = await CreateAsync(swich, tail + "1", new { legalEntityName = "შპს ტესტ პარკი", taxId = "405111222", phone = "  " });
            Assert.Equal(HttpStatusCode.BadRequest, noPhone);
            Assert.Equal(new[] { "phone" }, Missing(noPhoneBody));

            // Georgian identification codes: 9 digits (a company) or 11 (an individual entrepreneur's personal number).
            var (ten, tenBody) = await CreateAsync(swich, tail + "2", new { legalEntityName = "შპს ტესტ პარკი", taxId = "4051112223", phone = "+995 322 11 22 33" });
            Assert.Equal(HttpStatusCode.BadRequest, ten);
            Assert.Equal("invalid_tax_id", ApiFixture.Code(tenBody));

            foreach (var (t, taxId) in new[] { ("3", "405 111 222"), ("4", "01001012345") })
            {
                var (ok, okBody) = await CreateAsync(swich, tail + t, new { legalEntityName = "შპს ტესტ პარკი", taxId, phone = "+995 322 11 22 33" });
                Assert.True(ok == HttpStatusCode.Created, okBody.ToString());
                created.Add(okBody.GetProperty("id").GetGuid());
            }
            var stored = await _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => created.Contains(p.Id)).Select(p => p.TaxId).ToListAsync());
            Assert.Equal(new[] { "01001012345", "405111222" }, stored.OrderBy(x => x).ToArray());
        }
        finally { await DeleteAsync(created); }
    }

    [Fact]
    public async Task An_active_park_keeps_its_legal_details_and_a_park_without_them_is_not_reactivated()
    {
        var swich = await _f.SuperAdminAsync();
        var created = new List<Guid>();
        var tail = Random.Shared.Next(10_000, 99_999).ToString();
        try
        {
            var (status, body) = await CreateAsync(swich, tail + "5", new { legalEntityName = "შპს ტესტ პარკი", taxId = "405111222", phone = "+995 322 11 22 33" });
            Assert.True(status == HttpStatusCode.Created, body.ToString());
            var parkId = body.GetProperty("id").GetGuid();
            created.Add(parkId);
            var url = $"/api/admin/parks/{parkId}";

            // Clearing one of them on an active park is refused, and nothing changes.
            var cleared = await swich.PatchAsJsonAsync(url, new { phone = "", minCashoutAmount = 7m });
            Assert.Equal(HttpStatusCode.BadRequest, cleared.StatusCode);
            var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("legal_details_required", ApiFixture.Code(clearedBody));
            Assert.Equal(new[] { "phone" }, Missing(clearedBody));
            var park = await _f.DbAsync(db => db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId));
            Assert.Equal("+995322112233", park.Phone);
            Assert.Equal(5m, park.MinCashoutAmount);

            // A park created before the rule (no legal details) that is still active: any edit must complete them.
            await _f.DbAsync(db => db.Parks.Where(p => p.Id == parkId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LegalEntityName, (string?)null).SetProperty(p => p.TaxId, (string?)null)));
            var edit = await swich.PatchAsJsonAsync(url, new { minCashoutAmount = 7m });
            Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
            Assert.Equal(new[] { "legalEntityName", "taxId" }, Missing(await edit.Content.ReadFromJsonAsync<JsonElement>()));

            // Suspended, it may be edited without them — but it is not reactivated until they are there.
            var suspend = await swich.PostAsJsonAsync($"{url}/status", new { status = "Suspended", reason = "Missing legal details" });
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await swich.PatchAsJsonAsync(url, new { minCashoutAmount = 7m })).StatusCode);
            var reactivate = await swich.PostAsJsonAsync($"{url}/status", new { status = "Active" });
            Assert.Equal(HttpStatusCode.BadRequest, reactivate.StatusCode);
            Assert.Equal("legal_details_required", ApiFixture.Code(await reactivate.Content.ReadFromJsonAsync<JsonElement>()));
            Assert.Equal(ParkStatus.Suspended, (await _f.DbAsync(db => db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId))).Status);

            var filled = await swich.PatchAsJsonAsync(url, new { legalEntityName = "შპს ტესტ პარკი", taxId = "405111222" });
            Assert.Equal(HttpStatusCode.OK, filled.StatusCode);
            var back = await swich.PostAsJsonAsync($"{url}/status", new { status = "Active" });
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
            Assert.Equal("Active", (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }
        finally { await DeleteAsync(created); }
    }
}
