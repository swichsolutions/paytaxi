using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Infrastructure.Adapters.Sms;
using Xunit;

namespace PayTaxi.Tests.Sms;

/// <summary>
/// The smsoffice.ge adapter against a scripted gateway: what it posts, what it makes of the answers, what it logs — and
/// that nothing it meets ever reaches the caller as an exception.
/// </summary>
public class SmsOfficeSenderTests
{
    private const string Key = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string Phone = "+995599123456";

    private static (SmsOfficeSender Sender, FakeSmsOfficeHandler Gateway, RecordingAlerts Alerts, RecordingLogger<SmsOfficeSender> Log) Make(
        Action<SmsOfficeOptions>? configure = null, string? baseAddress = "https://smsoffice.ge/")
    {
        var gateway = new FakeSmsOfficeHandler();
        var alerts = new RecordingAlerts();
        var log = new RecordingLogger<SmsOfficeSender>();
        var opts = new SmsOptions { Provider = "smsoffice", SmsOffice = { Key = Key, Sender = "PayTaxi" } };
        configure?.Invoke(opts.SmsOffice);
        var sender = new SmsOfficeSender(new SingleClientFactory(gateway, baseAddress), Options.Create(opts), alerts, log);
        return (sender, gateway, alerts, log);
    }

    [Fact]
    public async Task A_message_is_posted_form_encoded_with_the_documented_fields()
    {
        var (sender, gateway, alerts, _) = Make();
        await sender.SendAsync(Phone, "PayTaxi code: 123456. Valid 5 minutes.", "signin", "driver 1");

        var sent = Assert.Single(gateway.Sends);
        Assert.Equal("https://smsoffice.ge/api/v2/send/", sent.Uri.ToString());
        Assert.Equal("application/x-www-form-urlencoded", sent.ContentType);
        Assert.Equal(Key, sent.Form["key"]);
        Assert.Equal("995599123456", sent.Form["destination"]);     // international format, no "+"
        Assert.Equal("PayTaxi", sent.Form["sender"]);
        Assert.Equal("PayTaxi code: 123456. Valid 5 minutes.", sent.Form["content"]);
        var reference = sent.Form["reference"];
        Assert.InRange(reference.Length, 1, SmsReference.MaxLength);
        Assert.StartsWith("signin-", reference);
        Assert.DoesNotContain("599123456", reference);
        // Accepted: whatever stopped sending before is over.
        Assert.Empty(alerts.Raised);
        Assert.Single(alerts.Cleared);
    }

    [Fact]
    public async Task Without_a_base_address_the_configured_host_is_used()
    {
        var (sender, gateway, _, _) = Make(o => o.BaseUrl = "https://sms.example.test", baseAddress: null);
        await sender.SendAsync(Phone, "hi", "signin", "driver 1");
        Assert.Equal("https://sms.example.test/api/v2/send/", Assert.Single(gateway.Sends).Uri.ToString());
    }

    [Fact]
    public void The_reference_is_short_purpose_plus_random_and_never_the_phone()
    {
        var a = SmsReference.New("payout_account_changed");
        var b = SmsReference.New("payout_account_changed");
        Assert.InRange(a.Length, 1, SmsReference.MaxLength);
        Assert.StartsWith("payoutaccou-", a);
        Assert.NotEqual(a, b);
        Assert.Equal(a.Length, System.Text.Encoding.UTF8.GetByteCount(a));   // ≤ 20 UTF-8 characters means ≤ 20 bytes here
        Assert.Matches("^[a-z0-9]+-[a-z0-9]{8}$", SmsReference.New("Sign In!"));
        Assert.StartsWith("sms-", SmsReference.New(""));
    }

    [Fact]
    public async Task Insufficient_balance_is_logged_with_its_code_raises_the_alert_and_does_not_throw()
    {
        var (sender, gateway, alerts, log) = Make();
        gateway.Answer(20, "Insufficient balance");
        await sender.SendAsync(Phone, "PayTaxi code: 123456. Valid 5 minutes.", "signin", "driver 42");

        var alert = Assert.Single(alerts.Raised);
        Assert.Equal(SmsGatewayCodes.InsufficientBalance, alert.Code);
        Assert.Contains("error 20", alert.Detail);
        Assert.Empty(alerts.Cleared);
        var line = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("error 20", line.Message);
        Assert.Contains("insufficient balance", line.Message);
        Assert.Contains("signin", line.Message);
        Assert.Contains("driver 42", line.Message);
    }

    [Theory]
    [InlineData(80, SmsGatewayCodes.BadKey)]
    [InlineData(120, SmsGatewayCodes.ApiDisabled)]
    [InlineData(150, SmsGatewayCodes.SenderRejected)]
    [InlineData(110, SmsGatewayCodes.SenderRejected)]
    public async Task An_account_level_refusal_raises_the_alert(int errorCode, string expected)
    {
        var (sender, gateway, alerts, _) = Make();
        gateway.Answer(errorCode);
        await sender.SendAsync(Phone, "hi", "signin", "driver 1");
        Assert.Equal(expected, Assert.Single(alerts.Raised).Code);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(76)]
    [InlineData(-100)]
    [InlineData(9999)]
    public async Task A_refusal_about_this_message_only_is_logged_without_an_alert(int errorCode)
    {
        var (sender, gateway, alerts, log) = Make();
        gateway.Answer(errorCode);
        await sender.SendAsync(Phone, "hi", "signin", "driver 1");
        Assert.Empty(alerts.Raised);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains($"error {errorCode}"));
    }

    [Fact]
    public async Task The_log_never_holds_the_phone_or_the_text()
    {
        var (sender, gateway, _, log) = Make();
        gateway.Answer(0).Answer(20).Http(HttpStatusCode.InternalServerError).Body("<html>maintenance</html>").Throw(new HttpRequestException("down"));
        for (var i = 0; i < 5; i++)
            await sender.SendAsync(Phone, "PayTaxi code: 987654. Valid 5 minutes.", "signin", "driver 7");
        await sender.SendAsync("+4915112345678", "PayTaxi code: 987654. Valid 5 minutes.", "signin", "driver 7");

        Assert.Equal(6, log.Lines.Count);
        Assert.DoesNotContain("599123456", log.All);
        Assert.DoesNotContain("995599", log.All);
        Assert.DoesNotContain("15112345678", log.All);
        Assert.DoesNotContain("987654", log.All);
        Assert.All(log.Lines, l => Assert.Contains("driver 7", l.Message));
    }

    [Fact]
    public async Task Gateway_failures_never_reach_the_caller()
    {
        var (sender, gateway, alerts, log) = Make();
        gateway.Http(HttpStatusCode.BadGateway)
               .Body("not json")
               .Body("{\"Success\": true}")                                   // no ErrorCode
               .Throw(new HttpRequestException("connection refused"))
               .Throw(new TaskCanceledException("timeout"));                  // what HttpClient throws on its own timeout
        for (var i = 0; i < 5; i++)
            await sender.SendAsync(Phone, "hi", "payout_account_changed", "driver 1");
        Assert.Equal(5, log.Lines.Count(l => l.Level == LogLevel.Warning));
        Assert.Empty(alerts.Raised);   // a gateway down is not the account's fault; the sender's own errors say so
    }

    [Fact]
    public async Task A_cancelled_caller_is_logged_as_such_and_not_thrown()
    {
        var (sender, gateway, _, log) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        gateway.Throw(new OperationCanceledException(cts.Token));
        await sender.SendAsync(Phone, "hi", "signin", "driver 1", cts.Token);
        Assert.Contains(log.Lines, l => l.Message.Contains("gave up waiting"));
    }

    [Theory]
    [InlineData("+4915112345678")]       // Germany
    [InlineData("+79161234567")]         // Russia
    [InlineData("+99532123456")]         // too short for a Georgian number
    [InlineData("")]
    public async Task A_number_outside_Georgia_is_not_sent_and_the_gateway_is_not_called(string phone)
    {
        var (sender, gateway, alerts, log) = Make();
        await sender.SendAsync(phone, "hi", "signin", "driver 1");
        Assert.Empty(gateway.Sends);
        Assert.Empty(alerts.Raised);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("not a Georgian number"));
    }

    [Fact]
    public async Task A_text_over_the_gateway_limit_is_not_sent()
    {
        var (sender, gateway, _, log) = Make();
        await sender.SendAsync(Phone, new string('x', SmsOfficeSender.MaxContentLength + 1), "signin", "driver 1");
        Assert.Empty(gateway.Sends);
        Assert.Contains(log.Lines, l => l.Message.Contains("1001 characters"));
    }

    [Theory]
    [InlineData("+995599123456", "995599123456")]
    [InlineData("995599123456", "995599123456")]
    [InlineData("599 12 34 56", "995599123456")]
    [InlineData("+49 151 12345678", null)]
    public void Destination_is_the_Georgian_number_without_the_plus(string phone, string? expected) =>
        Assert.Equal(expected, SmsOfficeSender.Destination(phone));

    [Theory]
    [InlineData("1234", "1234")]
    [InlineData(" 1234.5 ", "1234.5")]
    [InlineData("\"77\"", "77")]
    [InlineData("{\"Success\":true,\"Message\":\"\",\"Output\":{\"Balance\":312},\"ErrorCode\":0}", "312")]
    [InlineData("{\"Balance\":\"42\"}", "42")]
    [InlineData("{\"Success\":false,\"ErrorCode\":80}", null)]   // an error code is not a balance
    [InlineData("<html>", null)]
    public void The_balance_reply_is_read_as_a_bare_number_or_json(string body, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            SmsOfficeBalanceCheck.ParseBalance(body));

    [Fact]
    public async Task The_balance_check_logs_the_number_and_alerts_below_the_warning_line()
    {
        var gateway = new FakeSmsOfficeHandler().Balance("312").Balance("12").Balance("oops", HttpStatusCode.InternalServerError);
        var alerts = new RecordingAlerts();
        var log = new RecordingLogger<SmsOfficeBalanceCheck>();
        var opts = Options.Create(new SmsOptions { Provider = "smsoffice", SmsOffice = { Key = Key, LowBalanceWarnAt = 50 } });
        var check = new SmsOfficeBalanceCheck(new SingleClientFactory(gateway), opts, alerts, log);

        Assert.Equal(312m, await check.CheckOnceAsync(CancellationToken.None));
        Assert.Empty(alerts.Raised);
        Assert.Single(alerts.Cleared);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && l.Message.Contains("312"));

        Assert.Equal(12m, await check.CheckOnceAsync(CancellationToken.None));
        var alert = Assert.Single(alerts.Raised);
        Assert.Equal(SmsGatewayCodes.LowBalance, alert.Code);
        Assert.Contains("12 messages left", alert.Detail);

        Assert.Null(await check.CheckOnceAsync(CancellationToken.None));   // a failed read only logs
        Assert.Single(alerts.Raised);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("HTTP 500"));

        // The key travels in the query string — the one place it is visible to a proxy — and nowhere in the log.
        Assert.All(gateway.BalanceReads, u => Assert.Contains("key=" + Key, u.Query));
        Assert.DoesNotContain(Key, log.All);
    }
}
