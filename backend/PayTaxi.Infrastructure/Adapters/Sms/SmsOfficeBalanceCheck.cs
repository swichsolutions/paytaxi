using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PayTaxi.Infrastructure.Adapters.Sms;

/// <summary>
/// Reads the smsoffice.ge balance (GET api/getBalance?key=…: the number of messages left) shortly after start and then
/// every 6 hours, logs it, and opens the operator alert while it is below Sms:SmsOffice:LowBalanceWarnAt — so the
/// account is topped up before error 20 starts refusing sign-in codes. A healthy reading closes a balance alert
/// (<see cref="SmsGatewayCodes.ClearedByBalance"/>). Registered only with the gateway (Sms:Provider = smsoffice). A
/// failed read only logs: the next check is in 6 hours, and the sender's own errors raise their own alert.
/// </summary>
public sealed class SmsOfficeBalanceCheck : BackgroundService
{
    public const string BalancePath = "api/getBalance";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _http;
    private readonly SmsOfficeOptions _opts;
    private readonly ISmsGatewayAlerts _alerts;
    private readonly ILogger<SmsOfficeBalanceCheck> _log;

    public SmsOfficeBalanceCheck(IHttpClientFactory http, IOptions<SmsOptions> opts, ISmsGatewayAlerts alerts, ILogger<SmsOfficeBalanceCheck> log)
    {
        _http = http;
        _opts = opts.Value.SmsOffice;
        _alerts = alerts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(InitialDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            await CheckOnceAsync(ct);
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One read; the balance, or null when it could not be read (logged). Tests call it directly.</summary>
    public async Task<decimal?> CheckOnceAsync(CancellationToken ct)
    {
        decimal? balance;
        try
        {
            var client = _http.CreateClient(SmsOfficeSender.HttpClientName);
            var baseUri = client.BaseAddress ?? new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
            using var res = await client.GetAsync(new Uri(baseUri, BalancePath + "?key=" + Uri.EscapeDataString(_opts.Key)), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("smsoffice balance could not be read: HTTP {Status}", (int)res.StatusCode);
                return null;
            }
            balance = ParseBalance(body);
            if (balance is null)
            {
                _log.LogWarning("smsoffice balance could not be read: the reply is not a number ({Length} chars)", body.Length);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "smsoffice balance could not be read: {Why}", ex.GetType().Name);
            return null;
        }

        if (balance < _opts.LowBalanceWarnAt)
        {
            _log.LogWarning("smsoffice balance is {Balance} messages — below the warning line of {WarnAt}; top up at smsoffice.ge", balance, _opts.LowBalanceWarnAt);
            await _alerts.RaiseAsync(SmsGatewayCodes.LowBalance,
                $"{balance.Value.ToString("0.##", CultureInfo.InvariantCulture)} messages left on the smsoffice.ge account (warning below {_opts.LowBalanceWarnAt})", ct);
        }
        else
        {
            _log.LogInformation("smsoffice balance: {Balance} messages", balance);
            await _alerts.ClearAsync(SmsGatewayCodes.ClearedByBalance,
                $"Resolved automatically: the smsoffice.ge balance is {balance.Value.ToString("0.##", CultureInfo.InvariantCulture)} messages.", ct);
        }
        return balance;
    }

    /// <summary>
    /// The documentation says "returns the number of remaining messages" and shows no envelope, so both are taken: a bare
    /// number ("1234", "1234.5") and JSON — a number, a numeric string, or an object whose first numeric member (at the
    /// top or one level down, e.g. {"Output": {"Balance": 1234}}) is the balance.
    /// </summary>
    public static decimal? ParseBalance(string body)
    {
        var text = body.Trim().Trim('"');
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var plain)) return plain;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Numeric(doc.RootElement, depth: 0);
        }
        catch (JsonException) { return null; }
    }

    private static decimal? Numeric(JsonElement e, int depth)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number when e.TryGetDecimal(out var n): return n;
            case JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s): return s;
            case JsonValueKind.Object when depth < 2:
                // A named balance first; the error code or a success flag must never be read as one.
                foreach (var name in new[] { "Balance", "balance", "Output", "output", "Data", "data" })
                    if (e.TryGetProperty(name, out var p) && Numeric(p, depth + 1) is { } v) return v;
                return null;
            default: return null;
        }
    }
}
