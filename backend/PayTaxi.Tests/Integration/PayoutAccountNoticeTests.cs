using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Every change of a driver's payout accounts — added, made the default, removed; by the driver in the app or by the
/// park's side in the console — reaches the driver: an in-app notice and an SMS in their language, naming the masked
/// account and who made the change. Best effort: a failing SMS never fails the change. Each test makes its own drivers.
/// </summary>
[Collection("api")]
public class PayoutAccountNoticeTests
{
    private const string Batumi = "batumi-auto-park-1";
    private readonly ApiFixture _f;
    public PayoutAccountNoticeTests(ApiFixture f) => _f = f;

    private Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(db => db.Parks.AsNoTracking().Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync());

    private IReadOnlyList<SentSms> SmsFor(Guid driverId) =>
        ((LoggingSmsSender)_f.Services.GetRequiredService<ISmsSender>()).Recent.Where(m => m.Reference == $"driver {driverId}").ToList();

    private Task<List<(string Type, JsonElement Data)>> NoticesAsync(Guid driverId) => _f.DbAsync(async db =>
        (await db.Notifications.AsNoTracking().Where(n => n.DriverId == driverId).OrderBy(n => n.CreatedAt).ToListAsync())
            .Select(n => (n.Type, JsonDocument.Parse(n.Data ?? "{}").RootElement.Clone())).ToList());

    private static string Tail() => Random.Shared.NextInt64(1_000_000_000_000_000, 9_999_999_999_999_999).ToString();

    private static void AssertNotice((string Type, JsonElement Data) n, string change, string by, string destination)
    {
        Assert.Equal("payout_account_changed", n.Type);
        Assert.Equal(change, n.Data.GetProperty("change").GetString());
        Assert.Equal(by, n.Data.GetProperty("by").GetString());
        Assert.Equal(destination, n.Data.GetProperty("destination").GetString());
    }

    [Fact]
    public async Task The_driver_s_own_changes_are_told_to_the_driver_in_the_app_s_language()
    {
        var (driverId, phone, profile) = await TestDrivers.CreateAsync(_f, Batumi, "Nodar Testadze");
        try
        {
            var (driver, _, _) = await _f.DriverClientAsync(profile);
            driver.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");

            var a = await driver.PostAsJsonAsync("/api/driver/me/cards", new { iban = ApiFixture.TbcIban(Tail()), holderName = "Nodar Testadze", makeDefault = true });
            Assert.Equal(HttpStatusCode.Created, a.StatusCode);
            var cardA = await a.Content.ReadFromJsonAsync<JsonElement>();
            var labelA = $"TBC {cardA.GetProperty("maskedPan").GetString()}";
            var b = await driver.PostAsJsonAsync("/api/driver/me/cards", new { iban = ApiFixture.TbcIban(Tail()), holderName = "Nodar Testadze", makeDefault = false });
            var cardB = await b.Content.ReadFromJsonAsync<JsonElement>();
            var labelB = $"TBC {cardB.GetProperty("maskedPan").GetString()}";

            Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync($"/api/driver/me/cards/{cardB.GetProperty("id").GetGuid()}/default", null)).StatusCode);
            // Already the default: nothing changed, nothing to tell.
            Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync($"/api/driver/me/cards/{cardB.GetProperty("id").GetGuid()}/default", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await driver.DeleteAsync($"/api/driver/me/cards/{cardA.GetProperty("id").GetGuid()}")).StatusCode);

            var notices = await NoticesAsync(driverId);
            Assert.Equal(4, notices.Count);
            AssertNotice(notices[0], "added", "driver", labelA);
            AssertNotice(notices[1], "added", "driver", labelB);
            AssertNotice(notices[2], "default", "driver", labelB);
            AssertNotice(notices[3], "removed", "driver", labelA);
            // Added as the default — where the payouts now go — is said so; added beside it is not.
            Assert.True(notices[0].Data.GetProperty("isDefault").GetBoolean());
            Assert.False(notices[1].Data.TryGetProperty("isDefault", out var notDefault) && notDefault.GetBoolean());

            // The app's own inbox shows them (the surface the driver already has).
            var inbox = await driver.GetFromJsonAsync<JsonElement>("/api/driver/me/notifications");
            Assert.Equal(4, inbox.GetProperty("notifications").EnumerateArray().Count(n => n.GetProperty("type").GetString() == "payout_account_changed"));

            // The SMS, in Russian — the language the app said it is showing — to the driver's phone.
            var sms = SmsFor(driverId);
            Assert.Equal(4, sms.Count);
            Assert.All(sms, m => Assert.Equal(phone, m.Phone));
            Assert.Equal($"PayTaxi: Вы добавили счёт {labelA} для выплат и сделали его основным. Если это были не вы, обратитесь в свой таксопарк.", sms[0].Text);
            Assert.Equal($"PayTaxi: Вы добавили банковский счёт {labelB} для выплат. Если это были не вы, обратитесь в свой таксопарк.", sms[1].Text);
            Assert.StartsWith($"PayTaxi: Вы сделали счёт {labelB} основным", sms[2].Text);
            Assert.StartsWith($"PayTaxi: Вы удалили банковский счёт {labelA}.", sms[3].Text);
            Assert.Equal("ru", await _f.DbAsync(db => db.Database
                .SqlQueryRaw<string>("SELECT \"PreferredLanguage\" AS \"Value\" FROM \"Drivers\" WHERE \"Id\" = {0}", driverId).FirstOrDefaultAsync()));
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    [Fact]
    public async Task A_change_from_the_console_is_told_as_made_by_the_taxi_park_in_the_driver_s_language_georgian_by_default()
    {
        var parkId = await ParkIdAsync(Batumi);
        var (driverId, _, _) = await TestDrivers.CreateAsync(_f, Batumi, "Nodar Testadze");
        try
        {
            var manager = await _f.ParkAdminAsync(Batumi);
            var add = await manager.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}/cards",
                new { iban = ApiFixture.TbcIban(Tail()), holderName = "Nodar Testadze", makeDefault = true });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
            var card = await add.Content.ReadFromJsonAsync<JsonElement>();
            var label = $"TBC {card.GetProperty("maskedPan").GetString()}";
            Assert.Equal(HttpStatusCode.NoContent,
                (await manager.DeleteAsync($"/api/admin/parks/{parkId}/drivers/{driverId}/cards/{card.GetProperty("id").GetGuid()}")).StatusCode);

            // Swich (or the operator) acting in the console is "your taxi park" to the driver too — in English once the
            // app has told us that is the driver's language.
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"Drivers\" SET \"PreferredLanguage\" = 'en' WHERE \"Id\" = {0}", driverId));
            var swich = await _f.SuperAdminAsync();
            var third = await swich.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers/{driverId}/cards",
                new { iban = ApiFixture.TbcIban(Tail()), holderName = "Nodar Testadze" });
            Assert.Equal(HttpStatusCode.Created, third.StatusCode);
            var thirdLabel = $"TBC {(await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maskedPan").GetString()}";

            var notices = await NoticesAsync(driverId);
            Assert.Equal(3, notices.Count);
            AssertNotice(notices[0], "added", "park", label);
            AssertNotice(notices[1], "removed", "park", label);
            AssertNotice(notices[2], "added", "park", thirdLabel);

            var sms = SmsFor(driverId);
            Assert.Equal(3, sms.Count);
            // The park's account becomes the default unless it says otherwise: the driver is told the payouts now go there.
            Assert.Equal($"PayTaxi: თქვენმა ტაქსოპარკმა დაგიმატათ ანგარიში {label}; თანხა მასზე ჩაირიცხება. თუ ეს არ გითხოვიათ, დაუკავშირდით ტაქსოპარკს.", sms[0].Text);
            Assert.StartsWith($"PayTaxi: თქვენმა ტაქსოპარკმა წაშალა თქვენი საბანკო ანგარიში {label}.", sms[1].Text);
            Assert.Equal($"PayTaxi: Your taxi park added the bank account {thirdLabel} and made it your default account for payouts. If you did not ask for this, contact your taxi park.", sms[2].Text);
            Assert.True(notices[2].Data.GetProperty("isDefault").GetBoolean());
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    [Fact]
    public async Task An_account_registered_at_onboarding_is_told_to_the_new_driver()
    {
        var parkId = await ParkIdAsync(Batumi);
        var manager = await _f.ParkAdminAsync(Batumi);
        var phone = TestDrivers.FreshPhone();
        var res = await manager.PostAsJsonAsync($"/api/admin/parks/{parkId}/drivers", new
        {
            phone, yandexProfileId = "yp_test_" + Guid.NewGuid().ToString("N")[..10], name = "Gela Onboardadze", consentGiven = true,
            iban = ApiFixture.TbcIban(Tail()), holderName = "Gela Onboardadze",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var driverId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        try
        {
            var notice = Assert.Single(await NoticesAsync(driverId));
            Assert.Equal("added", notice.Data.GetProperty("change").GetString());
            Assert.Equal("park", notice.Data.GetProperty("by").GetString());
            Assert.Equal(phone, Assert.Single(SmsFor(driverId)).Phone);
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    private sealed class FailingSms : ISmsSender
    {
        public bool DeliversForReal => true;
        public Task SendAsync(string phoneE164, string text, string purpose, string reference, CancellationToken ct = default) =>
            throw new HttpRequestException("SMS gateway down");
    }

    [Fact]
    public async Task A_failing_sms_never_fails_the_change_and_the_in_app_notice_still_arrives()
    {
        var (driverId, _, profile) = await TestDrivers.CreateAsync(_f, Batumi, "Nodar Testadze");
        await using var host = _f.Derive(s => s.AddSingleton<ISmsSender, FailingSms>());
        try
        {
            var driver = await _f.DriverClientAsync(host, profile);
            var add = await driver.PostAsJsonAsync("/api/driver/me/cards", new { iban = ApiFixture.TbcIban(Tail()), holderName = "Nodar Testadze" });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
            Assert.True(await _f.DbAsync(db => db.BankCards.AnyAsync(b => b.DriverId == driverId && b.IsActive)));
            Assert.Equal("added", Assert.Single(await NoticesAsync(driverId)).Data.GetProperty("change").GetString());
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    private sealed class SlowSms : ISmsSender
    {
        public bool DeliversForReal => true;
        public readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendAsync(string phoneE164, string text, string purpose, string reference, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
            Sent.TrySetResult();
        }
    }

    [Fact]
    public async Task A_slow_sms_gateway_does_not_hold_up_the_change_and_the_sms_still_goes_out()
    {
        var (driverId, _, _) = await TestDrivers.CreateAsync(_f, Batumi, "Nodar Testadze");
        try
        {
            var slow = new SlowSms();
            var notifier = new NotificationService(_f.Services.GetRequiredService<IServiceScopeFactory>(), slow,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance) { SmsWait = TimeSpan.FromMilliseconds(200) };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await notifier.NotifyPayoutAccountChangedAsync(driverId, PayoutAccountChange.Removed, "TBC ••••4321", byPark: true);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the change waited {watch.Elapsed} for the SMS gateway");
            Assert.Equal("removed", Assert.Single(await NoticesAsync(driverId)).Data.GetProperty("change").GetString());

            // Not dropped: it finishes after the answer.
            await slow.Sent.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }
}
