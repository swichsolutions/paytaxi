using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Yandex;

/// <summary>
/// Real HTTP implementation of the Yandex Fleet API (https://fleet-api.taxi.yandex.net/).
///
/// Endpoints (Fleet API reference, "API for partners", ToS https://yandex.ru/legal/taxi_api_partners/):
///   POST /v1/parks/driver-profiles/list             roster + balances (offset/limit paging)
///   POST /v2/parks/driver-profiles/transactions/list per-driver transactions (cursor paging)
///   POST /v1/parks/orders/list                      per-driver rides (cursor paging)
///   POST /v2/parks/driver-profiles/transactions     THE write: {park_id, driver_profile_id, category_id, amount, description}
///                                                   with X-Idempotency-Token — a repeat with the same token is declined.
/// Headers on every call: X-Client-ID ("taxi/park/{id}" as shown on fleet.yandex.com), X-API-Key,
/// X-Park-ID, Accept-Language.
///
/// This class does HTTP + JSON only. Rate limiting (0.5 s per park), retry with backoff and the
/// audit log live in <see cref="ResilientYandexFleetClient"/>, which wraps it. Transient conditions
/// (429, 5xx, timeouts, connection errors) surface as <see cref="YandexTransientException"/> so the
/// wrapper retries them; everything else is a definitive answer.
///
/// Amounts: Yandex returns and accepts money as decimal strings ("123.45"); we never send floats.
/// </summary>
public class YandexFleetClient : IYandexFleetClient
{
    private readonly HttpClient _http;
    private readonly IYandexParkCredentialsProvider _creds;
    private readonly YandexFleetOptions _opts;
    private readonly ILogger<YandexFleetClient> _log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public YandexFleetClient(
        HttpClient http,
        IYandexParkCredentialsProvider creds,
        IOptions<YandexFleetOptions> opts,
        ILogger<YandexFleetClient> log)
    {
        _http = http;
        _creds = creds;
        _opts = opts.Value;
        _log = log;
        if (_http.BaseAddress is null && Uri.TryCreate(_opts.BaseUrl, UriKind.Absolute, out var baseUri))
            _http.BaseAddress = baseUri;
        if (_http.Timeout == TimeSpan.FromSeconds(100)) // default → apply ours once
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opts.TimeoutSeconds));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Reads
    // ═══════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<YandexDriverProfile>> GetDriverProfilesAsync(Guid parkId, CancellationToken ct = default)
    {
        var creds = await _creds.GetAsync(parkId, ct);
        var result = new List<YandexDriverProfile>();
        var limit = Math.Clamp(_opts.PageSize, 1, 1000);

        for (var offset = 0; offset < 100_000; offset += limit)
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject { ["park"] = new JsonObject { ["id"] = creds.YandexParkId } },
                ["fields"] = ProfileFields(),
                ["limit"] = limit,
                ["offset"] = offset,
            };
            var doc = await PostAsync(creds, "v1/parks/driver-profiles/list", body, idempotencyToken: null, ct);

            var rows = doc["driver_profiles"]?.AsArray() ?? new JsonArray();
            foreach (var row in rows)
                if (row is JsonObject o) result.Add(MapProfile(o));

            var total = doc["total"]?.GetValue<int>() ?? result.Count;
            if (rows.Count == 0 || result.Count >= total) break;
        }
        return result;
    }

    public async Task<decimal> GetDriverBalanceAsync(Guid parkId, string driverProfileId, CancellationToken ct = default)
    {
        var creds = await _creds.GetAsync(parkId, ct);
        var body = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["park"] = new JsonObject
                {
                    ["id"] = creds.YandexParkId,
                    ["driver_profile"] = new JsonObject { ["id"] = new JsonArray(driverProfileId) },
                },
            },
            ["fields"] = ProfileFields(),
            ["limit"] = 1,
            ["offset"] = 0,
        };
        var doc = await PostAsync(creds, "v1/parks/driver-profiles/list", body, null, ct);
        var row = doc["driver_profiles"]?.AsArray().FirstOrDefault() as JsonObject
            ?? throw new InvalidOperationException($"Yandex profile {driverProfileId} not found in park {creds.YandexParkId}");
        return MapProfile(row).Balance;
    }

    public async Task<IReadOnlyList<YandexTransaction>> GetTransactionsAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var creds = await _creds.GetAsync(parkId, ct);
        var result = new List<YandexTransaction>();
        string? cursor = null;

        for (var page = 0; page < 200; page++)
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["park"] = new JsonObject
                    {
                        ["id"] = creds.YandexParkId,
                        ["driver_profile"] = new JsonObject { ["id"] = driverProfileId },
                        ["transaction"] = new JsonObject
                        {
                            ["event_at"] = new JsonObject { ["from"] = Iso(from), ["to"] = Iso(to) },
                        },
                    },
                },
                ["limit"] = Math.Clamp(_opts.PageSize, 1, 1000),
            };
            if (cursor is not null) body["cursor"] = cursor;

            var doc = await PostAsync(creds, "v2/parks/driver-profiles/transactions/list", body, null, ct);
            foreach (var t in doc["transactions"]?.AsArray() ?? new JsonArray())
            {
                if (t is not JsonObject o) continue;
                result.Add(new YandexTransaction(
                    TransactionId: Str(o["id"]) ?? "",
                    Amount: Money(o["amount"]),
                    Category: Str(o["category_id"]) ?? "",
                    Description: Str(o["description"]) ?? Str(o["category_name"]),
                    CreatedAt: Date(o["event_at"])));
            }
            var next = Str(doc["cursor"]);
            if (string.IsNullOrEmpty(next) || next == cursor) break;
            cursor = next;
        }
        return result;
    }

    public async Task<IReadOnlyList<YandexOrder>> GetOrdersAsync(
        Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var creds = await _creds.GetAsync(parkId, ct);
        var result = new List<YandexOrder>();
        string? cursor = null;

        for (var page = 0; page < 200; page++)
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["park"] = new JsonObject
                    {
                        ["id"] = creds.YandexParkId,
                        ["driver_profile"] = new JsonObject { ["id"] = driverProfileId },
                        ["order"] = new JsonObject
                        {
                            ["ended_at"] = new JsonObject { ["from"] = Iso(from), ["to"] = Iso(to) },
                        },
                    },
                },
                ["limit"] = Math.Clamp(_opts.PageSize, 1, 500),
            };
            if (cursor is not null) body["cursor"] = cursor;

            var doc = await PostAsync(creds, "v1/parks/orders/list", body, null, ct);
            foreach (var t in doc["orders"]?.AsArray() ?? new JsonArray())
            {
                if (t is not JsonObject o) continue;
                var route = o["route_points"]?.AsArray();
                result.Add(new YandexOrder(
                    OrderId: Str(o["id"]) ?? "",
                    Amount: Money(o["price"]),
                    From: Str(o["address_from"]?["address"]),
                    To: route is { Count: > 0 } ? Str(route[^1]?["address"]) : null,
                    CreatedAt: Date(o["ended_at"] ?? o["created_at"])));
            }
            var next = Str(doc["cursor"]);
            if (string.IsNullOrEmpty(next) || next == cursor) break;
            cursor = next;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Writes
    // ═══════════════════════════════════════════════════════════════════

    public Task<YandexTransactionResult> PostCashoutTransactionAsync(
        Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default)
        => PostTransactionAsync(parkId, driverProfileId, -Math.Abs(amount), _opts.CashoutCategoryId,
            "Cashout · PayTaxi", idempotencyKey, ct);

    public Task<YandexTransactionResult> PostReversalTransactionAsync(
        Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default)
        => PostTransactionAsync(parkId, driverProfileId, Math.Abs(amount),
            _opts.ReversalCategoryId ?? _opts.CashoutCategoryId, "Cashout reversal · PayTaxi", idempotencyKey, ct);

    private async Task<YandexTransactionResult> PostTransactionAsync(
        Guid parkId, string driverProfileId, decimal signedAmount, string categoryId, string description,
        string idempotencyKey, CancellationToken ct)
    {
        if (_opts.ReadOnlyMode)
            throw new YandexReadOnlyModeException();

        var creds = await _creds.GetAsync(parkId, ct);
        var body = new JsonObject
        {
            ["park_id"] = creds.YandexParkId,
            ["driver_profile_id"] = driverProfileId,
            ["category_id"] = categoryId,
            ["amount"] = signedAmount.ToString("0.00", CultureInfo.InvariantCulture),
            ["description"] = description,
        };

        try
        {
            var doc = await PostAsync(creds, "v2/parks/driver-profiles/transactions", body, IdempotencyToken(idempotencyKey), ct);
            var id = Str(doc["id"]);
            if (string.IsNullOrEmpty(id))
                return new YandexTransactionResult(false, null, "NO_TRANSACTION_ID", "Yandex accepted the request but returned no transaction id");
            return new YandexTransactionResult(true, id, null, null);
        }
        catch (YandexApiException ex)
        {
            // Definitive rejection (400/403/404/409): report, don't retry.
            return new YandexTransactionResult(false, null, ex.Code ?? $"HTTP_{(int)ex.StatusCode}", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Transport
    // ═══════════════════════════════════════════════════════════════════

    private async Task<JsonObject> PostAsync(YandexParkCredentials creds, string path, JsonObject body, string? idempotencyToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.TryAddWithoutValidation("X-Client-ID", creds.ClientId);
        req.Headers.TryAddWithoutValidation("X-API-Key", creds.ApiKey);
        req.Headers.TryAddWithoutValidation("X-Park-ID", creds.YandexParkId);
        req.Headers.TryAddWithoutValidation("Accept-Language", _opts.AcceptLanguage);
        if (idempotencyToken is not null)
            req.Headers.TryAddWithoutValidation("X-Idempotency-Token", idempotencyToken);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new YandexTransientException($"Yandex Fleet unreachable ({path}): {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new YandexTransientException($"Yandex Fleet timed out ({path})");
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);

            if ((int)res.StatusCode == 429 || (int)res.StatusCode >= 500)
            {
                _log.LogWarning("Yandex {Path} → HTTP {Status} (transient)", path, (int)res.StatusCode);
                throw new YandexTransientException($"Yandex Fleet HTTP {(int)res.StatusCode} on {path}");
            }

            if (!res.IsSuccessStatusCode)
            {
                var (code, message) = ParseError(text);
                _log.LogWarning("Yandex {Path} → HTTP {Status} {Code}: {Message}", path, (int)res.StatusCode, code, message);
                throw new YandexApiException(res.StatusCode, code, message);
            }

            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            try
            {
                return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                throw new YandexTransientException($"Yandex Fleet returned non-JSON on {path}: {ex.Message}");
            }
        }
    }

    private static (string? code, string message) ParseError(string text)
    {
        try
        {
            var o = JsonNode.Parse(text) as JsonObject;
            return (Str(o?["code"]), Str(o?["message"]) ?? text);
        }
        catch { return (null, text.Length > 300 ? text[..300] : text); }
    }

    /// <summary>Yandex wants a token, not an arbitrary key; use a stable hash so retries reuse it exactly.</summary>
    public static string IdempotencyToken(string idempotencyKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Mapping helpers
    // ═══════════════════════════════════════════════════════════════════

    private static JsonObject ProfileFields() => new()
    {
        ["driver_profile"] = new JsonArray("id", "first_name", "last_name", "middle_name", "phones", "work_status"),
        ["account"] = new JsonArray("id", "balance", "currency", "type"),
        ["car"] = new JsonArray("id", "number", "normalized_number", "brand", "model"),
    };

    public static YandexDriverProfile MapProfile(JsonObject row)
    {
        var dp = row["driver_profile"] as JsonObject;
        var accounts = row["accounts"]?.AsArray();
        var car = row["car"] as JsonObject;

        // Prefer the "current" account; fall back to the first with a balance.
        var account = accounts?.OfType<JsonObject>().FirstOrDefault(a => Str(a["type"]) == "current")
                   ?? accounts?.OfType<JsonObject>().FirstOrDefault();

        var name = string.Join(' ', new[] { Str(dp?["first_name"]), Str(dp?["last_name"]) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var phone = dp?["phones"]?.AsArray().Select(p => Str(p)).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        return new YandexDriverProfile(
            DriverProfileId: Str(dp?["id"]) ?? "",
            Name: string.IsNullOrWhiteSpace(name) ? null : name,
            CarPlate: Str(car?["number"]) ?? Str(car?["normalized_number"]),
            Balance: Money(account?["balance"]),
            Currency: Str(account?["currency"]) ?? "GEL",
            Phone: phone);
    }

    private static string? Str(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => n.ToJsonString(),
    };

    /// <summary>Yandex money fields are decimal strings ("123.4500"); tolerate numbers too.</summary>
    private static decimal Money(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<double>(out var f)) return (decimal)f;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        }
        return 0m;
    }

    private static DateTime Date(JsonNode? n) =>
        DateTimeOffset.TryParse(Str(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : DateTime.MinValue;

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);
}

/// <summary>A definitive non-2xx answer from Yandex (4xx other than 429).</summary>
public sealed class YandexApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? Code { get; }
    public YandexApiException(HttpStatusCode status, string? code, string message) : base(message)
    {
        StatusCode = status;
        Code = code;
    }
}
