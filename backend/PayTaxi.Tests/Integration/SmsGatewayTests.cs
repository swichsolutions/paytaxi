using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Sms;
using PayTaxi.Tests.Sms;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The sign-in code through a real gateway adapter: with <see cref="SmsOfficeSender"/> registered over a scripted
/// smsoffice (the same API on the same database, <see cref="ApiFixture.Derive"/>), request-otp sends the code as an SMS in
/// the driver's language and shows it nowhere else — and the code that left in the SMS signs the driver in. The main host
/// has no gateway (the fixture pins Sms:Provider=log, whatever a developer's appsettings.Development.json says) and keeps
/// returning devCode for every other test. A gateway refusal opens the platform-wide incident the banner shows, and the
/// next accepted message closes it.
/// </summary>
[Collection("api")]
public class SmsGatewayTests
{
    private const string Batumi = "batumi-auto-park-1";
    private readonly ApiFixture _f;
    public SmsGatewayTests(ApiFixture f) => _f = f;

    private WebApplicationFactory<Program> GatewayHost(FakeSmsOfficeHandler gateway) => _f.Derive(s =>
    {
        s.Configure<SmsOptions>(o => { o.Provider = SmsOptions.SmsOfficeProvider; o.SmsOffice.Key = "test-key-0123456789abcdef"; o.SmsOffice.Sender = "PayTaxi"; });
        s.AddHttpClient(SmsOfficeSender.HttpClientName, c => c.BaseAddress = new Uri("https://smsoffice.ge/"))
            .ConfigurePrimaryHttpMessageHandler(() => gateway);
        s.AddSingleton<ISmsGatewayAlerts, SmsGatewayIncident>();
        s.AddSingleton<ISmsSender, SmsOfficeSender>();
    });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RequestOtpAsync(HttpClient client, string phone)
    {
        var res = await client.PostAsJsonAsync("/api/driver/auth/request-otp", new { phone });
        return (res.StatusCode, await res.Content.ReadFromJsonAsync<JsonElement>());
    }

    private Task DeleteSmsIncidentsAsync() => _f.DbAsync(async db =>
    {
        var ids = await db.Incidents.Where(i => i.DedupeKey == SmsGatewayIncident.Key).Select(i => i.Id).ToListAsync();
        await db.IncidentUpdates.Where(u => ids.Contains(u.IncidentId)).ExecuteDeleteAsync();
        return await db.Incidents.Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync();
    });

    [Fact]
    public async Task With_a_gateway_the_code_goes_out_as_an_SMS_in_the_driver_s_language_and_signs_the_driver_in()
    {
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi, "Nodar Testadze");
        var gateway = new FakeSmsOfficeHandler();
        await using var host = GatewayHost(gateway);
        try
        {
            var client = host.CreateClient();
            var (status, body) = await RequestOtpAsync(client, phone);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body.GetProperty("sent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("devCode").ValueKind);   // with a gateway the code is in the SMS only

            var sms = Assert.Single(gateway.Sends);
            Assert.Equal(phone[1..], sms.Form["destination"]);
            Assert.Equal("PayTaxi", sms.Form["sender"]);
            Assert.StartsWith("signin-", sms.Form["reference"]);
            // Georgian when the app never told us its language; one segment.
            var text = sms.Form["content"];
            Assert.StartsWith("PayTaxi: თქვენი კოდია ", text);
            Assert.Contains("5 წუთი", text);
            Assert.InRange(text.Length, 1, 70);
            var code = Regex.Match(text, @"\b\d{6}\b").Value;
            Assert.Equal(6, code.Length);

            var verify = await client.PostAsJsonAsync("/api/driver/auth/verify-otp", new { phone, code });
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            var signedIn = await verify.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrEmpty(signedIn.GetProperty("token").GetString()));
            Assert.Equal(driverId, signedIn.GetProperty("driver").GetProperty("id").GetGuid());

            // In the language the app last used — and the main host (no gateway) still shows the code to the tests.
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"Drivers\" SET \"PreferredLanguage\" = 'ru' WHERE \"Id\" = {0}", driverId));
            await RequestOtpAsync(client, phone);
            Assert.Matches(@"^PayTaxi: ваш код \d{6}\. Действует 5 минут\.$", gateway.Sends[1].Form["content"]);
            Assert.InRange(gateway.Sends[1].Form["content"].Length, 1, 70);
            await _f.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE \"Drivers\" SET \"PreferredLanguage\" = 'en' WHERE \"Id\" = {0}", driverId));
            await RequestOtpAsync(client, phone);
            Assert.Matches(@"^PayTaxi code: \d{6}\. Valid 5 minutes\.$", gateway.Sends[2].Form["content"]);

            var (_, plain) = await RequestOtpAsync(_f.Client, phone);
            Assert.Equal(JsonValueKind.String, plain.GetProperty("devCode").ValueKind);
            Assert.Equal(3, gateway.Sends.Count);   // the main host sent nothing anywhere
        }
        finally { await TestDrivers.DeleteAsync(_f, driverId); }
    }

    [Fact]
    public async Task An_unknown_phone_gets_the_same_answer_and_no_SMS()
    {
        var gateway = new FakeSmsOfficeHandler();
        await using var host = GatewayHost(gateway);
        var phone = TestDrivers.FreshPhone();
        try
        {
            var (status, body) = await RequestOtpAsync(host.CreateClient(), phone);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body.GetProperty("sent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("devCode").ValueKind);
            Assert.Empty(gateway.Sends);
        }
        finally { await TestDrivers.ForgetPhoneAsync(_f, TestDrivers.PhoneHash(phone)); }
    }

    [Fact]
    public async Task A_gateway_refusal_still_answers_sent_and_opens_the_platform_wide_incident_until_a_message_is_accepted_again()
    {
        await DeleteSmsIncidentsAsync();
        var (driverId, phone, _) = await TestDrivers.CreateAsync(_f, Batumi, "Nodar Testadze");
        var gateway = new FakeSmsOfficeHandler().Answer(20, "Insufficient balance").Answer(20, "Insufficient balance");
        await using var host = GatewayHost(gateway);
        try
        {
            var client = host.CreateClient();
            var (status, body) = await RequestOtpAsync(client, phone);
            Assert.Equal(HttpStatusCode.OK, status);           // best effort: the sign-in request itself never fails
            Assert.True(body.GetProperty("sent").GetBoolean());

            var incident = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates)
                .SingleAsync(i => i.DedupeKey == SmsGatewayIncident.Key && i.Status != IncidentStatus.Resolved));
            Assert.Null(incident.ParkId);
            Assert.Equal(IncidentLevel.P2, incident.Level);
            Assert.Equal(IncidentSource.Auto, incident.Source);
            Assert.Equal(IncidentFault.Platform, incident.Fault);
            Assert.StartsWith(SmsGatewayCodes.InsufficientBalance + ":", incident.Cause);
            Assert.Contains("error 20", incident.Cause);
            Assert.Equal("reported", Assert.Single(incident.Updates).Kind);

            // The same refusal again: still one incident, nothing added to it.
            await RequestOtpAsync(client, phone);
            Assert.Equal(1, await _f.DbAsync(db => db.Incidents.CountAsync(i => i.DedupeKey == SmsGatewayIncident.Key)));
            Assert.Equal(1, await _f.DbAsync(db => db.IncidentUpdates.CountAsync(u => u.IncidentId == incident.Id)));

            // On every console's banner: a platform-wide open incident (no park), for Swich and for a park manager alike.
            foreach (var console in new[] { await _f.SuperAdminAsync(), await _f.ParkAdminAsync(Batumi) })
            {
                var alerts = await console.GetFromJsonAsync<JsonElement>("/api/admin/alerts");
                var line = Assert.Single(alerts.GetProperty("alerts").EnumerateArray(),
                    a => a.GetProperty("kind").GetString() == "incident_open" && a.GetProperty("parkId").ValueKind == JsonValueKind.Null);
                Assert.Equal("P2_REPORTED", line.GetProperty("code").GetString());
                Assert.Equal(SmsGatewayIncident.Title, line.GetProperty("detail").GetString());
            }

            // Accepted again (the account was topped up): resolved by the gateway itself.
            await RequestOtpAsync(client, phone);
            var resolved = await _f.DbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Updates).SingleAsync(i => i.Id == incident.Id));
            Assert.Equal(IncidentStatus.Resolved, resolved.Status);
            Assert.Equal("system", resolved.ResolvedBy);
            Assert.Contains(resolved.Updates, u => u.Kind == "resolved" && u.Text.Contains("accepts messages again"));
        }
        finally
        {
            await TestDrivers.DeleteAsync(_f, driverId);
            await DeleteSmsIncidentsAsync();
        }
    }

    [Fact]
    public async Task A_low_balance_alert_is_not_closed_by_a_sent_message_but_by_a_healthy_balance()
    {
        await DeleteSmsIncidentsAsync();
        var gateway = new FakeSmsOfficeHandler().Balance("7").Balance("700");
        await using var host = GatewayHost(gateway);
        try
        {
            var alerts = host.Services.GetRequiredService<ISmsGatewayAlerts>();
            var check = new SmsOfficeBalanceCheck(host.Services.GetRequiredService<IHttpClientFactory>(),
                host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmsOptions>>(), alerts,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SmsOfficeBalanceCheck>.Instance);

            Assert.Equal(7m, await check.CheckOnceAsync(CancellationToken.None));
            var open = await _f.DbAsync(db => db.Incidents.AsNoTracking().SingleAsync(i => i.DedupeKey == SmsGatewayIncident.Key && i.Status != IncidentStatus.Resolved));
            Assert.Equal(SmsGatewayIncident.LowBalanceTitle, open.Title);
            Assert.StartsWith(SmsGatewayCodes.LowBalance + ":", open.Cause);

            // A message still goes out while the balance is low — that does not make the balance fine.
            await host.Services.GetRequiredService<ISmsSender>().SendAsync("+995599000111", "hi", "signin", "driver test");
            Assert.Equal(IncidentStatus.Reported, await _f.DbAsync(db => db.Incidents.AsNoTracking().Where(i => i.Id == open.Id).Select(i => i.Status).SingleAsync()));

            Assert.Equal(700m, await check.CheckOnceAsync(CancellationToken.None));
            Assert.Equal(IncidentStatus.Resolved, await _f.DbAsync(db => db.Incidents.AsNoTracking().Where(i => i.Id == open.Id).Select(i => i.Status).SingleAsync()));
        }
        finally { await DeleteSmsIncidentsAsync(); }
    }
}
