using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Identity;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Sms;

/// <summary>
/// The smsoffice.ge gateway (public API v2, https://smsoffice.ge/api/v2/send/): one POST per message, form-encoded —
/// key, destination (international format without "+"), sender, content, reference. The reply is JSON with an ErrorCode;
/// 0 means accepted for delivery (not delivered: delivery is reported later, by status call or callback).
///
/// What this adapter promises its callers (the <see cref="ISmsSender"/> contract): it never throws. A refused message,
/// a gateway down or slow, a malformed reply — all are logged and swallowed; a sign-in or a payout-account change must
/// not fail because of them. Answers no retry fixes — out of balance, wrong key, API off, sender unknown — also open the
/// operator alert (<see cref="ISmsGatewayAlerts"/>), so Swich learns that sign-in codes are not going out before the
/// drivers call.
///
/// What goes in the log: the purpose, the caller's reference (a driver id), our reference tag and the error code with its
/// meaning. Never the phone, never the text (the text of a sign-in SMS is the code), never the gateway's own message (it
/// may echo the destination). Not sent locally, without a call: a number outside Georgia (smsoffice delivers to Georgian
/// numbers only — error 10 otherwise) and a text over the 1000-character limit.
///
/// Rate: smsoffice documents no limit for v2/send; one HTTP call per message. Delivery reports (the callback smsoffice
/// posts to a URL of ours) are not wired: there is no public URL yet — see docs/DEPLOY.md, SMS.
/// </summary>
public sealed class SmsOfficeSender : ISmsSender
{
    /// <summary>The named HttpClient (Program.cs: base address from Sms:SmsOffice:BaseUrl, 10 s timeout, no request logging).</summary>
    public const string HttpClientName = "smsoffice";
    public const string SendPath = "api/v2/send/";
    public const int MaxContentLength = 1000;

    private readonly IHttpClientFactory _http;
    private readonly SmsOfficeOptions _opts;
    private readonly ISmsGatewayAlerts _alerts;
    private readonly ILogger<SmsOfficeSender> _log;

    public SmsOfficeSender(IHttpClientFactory http, IOptions<SmsOptions> opts, ISmsGatewayAlerts alerts, ILogger<SmsOfficeSender> log)
    {
        _http = http;
        _opts = opts.Value.SmsOffice;
        _alerts = alerts;
        _log = log;
    }

    public bool DeliversForReal => true;

    public async Task SendAsync(string phoneE164, string text, string purpose, string reference, CancellationToken ct = default)
    {
        var tag = SmsReference.New(purpose);
        var destination = Destination(phoneE164);
        if (destination is null)
        {
            _log.LogWarning("SMS ({Purpose}, {Reference}, tag {Tag}) not sent: not a Georgian number, and smsoffice delivers only to Georgian numbers",
                purpose, reference, tag);
            return;
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxContentLength)
        {
            _log.LogWarning("SMS ({Purpose}, {Reference}, tag {Tag}) not sent: the text is {Length} characters (limit {Limit})",
                purpose, reference, tag, text?.Length ?? 0, MaxContentLength);
            return;
        }

        try
        {
            using var form = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
            {
                new("key", _opts.Key),
                new("destination", destination),
                new("sender", _opts.Sender),
                new("content", text),
                new("reference", tag),
            });
            var client = _http.CreateClient(HttpClientName);
            using var res = await client.PostAsync(SendUri(client), form, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("SMS ({Purpose}, {Reference}, tag {Tag}) not sent: smsoffice answered HTTP {Status}",
                    purpose, reference, tag, (int)res.StatusCode);
                return;
            }

            var reply = SmsOfficeReply.Parse(body);
            if (reply.ErrorCode == 0)
            {
                _log.LogInformation("SMS ({Purpose}, {Reference}, tag {Tag}) accepted by smsoffice", purpose, reference, tag);
                await _alerts.ClearAsync(SmsGatewayCodes.ClearedBySend, "Resolved automatically: smsoffice.ge accepts messages again.", ct);
                return;
            }

            var meaning = SmsOfficeErrors.Meaning(reply.ErrorCode);
            _log.LogWarning("SMS ({Purpose}, {Reference}, tag {Tag}) refused by smsoffice: error {ErrorCode} — {Meaning}",
                purpose, reference, tag, reply.ErrorCode, meaning);
            if (SmsOfficeErrors.AlertCode(reply.ErrorCode) is { } alertCode)
                await _alerts.RaiseAsync(alertCode, $"smsoffice.ge error {reply.ErrorCode}: {meaning}", ct);
        }
        catch (Exception ex)
        {
            // The contract: nothing here fails the caller's action — a timeout (TaskCanceledException), the gateway
            // unreachable (HttpRequestException), a reply that is not the documented JSON, or a bug of ours.
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            _log.LogWarning(ex, "SMS ({Purpose}, {Reference}, tag {Tag}) could not be sent: {Why}",
                purpose, reference, tag, cancelled ? "the caller gave up waiting" : ex.GetType().Name);
        }
    }

    /// <summary>"+995599123456" → "995599123456"; null for anything that is not a Georgian number.</summary>
    public static string? Destination(string phoneE164)
    {
        var canonical = GeorgianPhone.Normalize(phoneE164);
        return canonical is not null && GeorgianPhone.IsGeorgian(canonical) ? canonical[1..] : null;
    }

    /// <summary>Absolute, so a client without a base address (a test's) still reaches the configured host.</summary>
    private Uri SendUri(HttpClient client) =>
        client.BaseAddress is { } b ? new Uri(b, SendPath) : new Uri(new Uri(_opts.BaseUrl.TrimEnd('/') + "/"), SendPath);
}

/// <summary>
/// The tag smsoffice keeps per message (its "reference", ≤ 20 UTF-8 characters, needed for delivery reports): the
/// purpose, trimmed, and a random id — never the phone, never the driver id, so a report or a log line at smsoffice
/// identifies nobody. Example: "signin-k7m2p9qx", "payoutaccou-x2n8r4tw".
/// </summary>
public static class SmsReference
{
    public const int MaxLength = 20;
    private const int RandomLength = 8;
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";   // no 0/o/1/l: a person may have to read it out

    public static string New(string purpose)
    {
        var head = new string((purpose ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        if (head.Length == 0) head = "sms";
        var headMax = MaxLength - RandomLength - 1;
        if (head.Length > headMax) head = head[..headMax];
        return head + "-" + RandomNumberGenerator.GetString(Alphabet, RandomLength);
    }
}

/// <summary>{"Success": bool, "Message": string, "Output": object, "ErrorCode": int} — only the code is read.</summary>
public readonly record struct SmsOfficeReply(int ErrorCode, bool Success)
{
    public static SmsOfficeReply Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("smsoffice reply is not a JSON object");
        var code = root.TryGetProperty("ErrorCode", out var c) ? ReadInt(c) : throw new JsonException("smsoffice reply has no ErrorCode");
        var success = root.TryGetProperty("Success", out var s) && s.ValueKind == JsonValueKind.True;
        return new SmsOfficeReply(code, success);
    }

    // The documentation shows a number; a string "0" is taken too.
    private static int ReadInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var n) => n,
        JsonValueKind.String when int.TryParse(e.GetString(), out var n) => n,
        _ => throw new JsonException("smsoffice ErrorCode is not an integer"),
    };
}

/// <summary>smsoffice's documented error codes (the public API v2 reference, Georgian).</summary>
public static class SmsOfficeErrors
{
    public static string Meaning(int code) => code switch
    {
        0 => "accepted for delivery",
        10 => "destination holds non-Georgian numbers",
        20 => "insufficient balance on the smsoffice account",
        40 => "text too long",
        60 or 800 => "content missing",
        70 or 600 => "destination missing",
        75 => "every destination is on the stop list",
        76 => "every destination is badly formatted",
        77 => "destinations on the stop list or badly formatted",
        80 => "no smsoffice user for this API key",
        110 => "sender value not understood",
        120 => "API use is not enabled on the smsoffice profile",
        150 => "sender not found (not approved for this account)",
        500 => "API key missing",
        700 => "sender missing",
        -100 => "temporary failure at smsoffice — retry later",
        _ => "unknown error code",
    };

    /// <summary>The codes that stop every message until a person acts — the account, not this message, is the problem.</summary>
    public static string? AlertCode(int code) => code switch
    {
        20 => SmsGatewayCodes.InsufficientBalance,
        80 or 500 => SmsGatewayCodes.BadKey,
        120 => SmsGatewayCodes.ApiDisabled,
        110 or 150 or 700 => SmsGatewayCodes.SenderRejected,
        _ => null,
    };
}
