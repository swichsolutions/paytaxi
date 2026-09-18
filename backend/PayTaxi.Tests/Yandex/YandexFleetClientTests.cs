using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Yandex;
using Xunit;

namespace PayTaxi.Tests.Yandex;

/// <summary>Scripted Fleet API server keyed by request path; records every request.</summary>
public sealed class FakeYandexHandler : HttpMessageHandler
{
    public List<(string Path, JsonObject Body, HttpRequestHeaders Headers)> Requests { get; } = new();
    private readonly Dictionary<string, Queue<(HttpStatusCode, string)>> _scripts = new();

    public FakeYandexHandler On(string path, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (!_scripts.TryGetValue(path, out var q)) _scripts[path] = q = new();
        q.Enqueue((status, json));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
        Requests.Add((path, body, request.Headers));
        if (!_scripts.TryGetValue(path, out var q) || q.Count == 0)
            throw new InvalidOperationException($"No scripted response for {path}");
        var (status, json) = q.Count > 1 ? q.Dequeue() : q.Peek();
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

public sealed class StubCreds : IYandexParkCredentialsProvider
{
    public Task<YandexParkCredentials> GetAsync(Guid parkId, CancellationToken ct) =>
        Task.FromResult(new YandexParkCredentials("taxi/park/abc123", "secret-key", "abc123"));
}

public class YandexFleetClientTests
{
    private static readonly Guid Park = Guid.NewGuid();

    private static (YandexFleetClient client, FakeYandexHandler fake) Build(bool readOnly = false)
    {
        var fake = new FakeYandexHandler();
        var http = new HttpClient(fake) { BaseAddress = new Uri("https://fleet-api.taxi.yandex.net/") };
        var opts = new YandexFleetOptions { ReadOnlyMode = readOnly, PageSize = 2, CashoutCategoryId = "partner_service_manual" };
        return (new YandexFleetClient(http, new StubCreds(), Options.Create(opts), NullLogger<YandexFleetClient>.Instance), fake);
    }

    private const string ProfilesPage1 = """
        {"driver_profiles":[
          {"driver_profile":{"id":"dp1","first_name":"გიორგი","last_name":"მამულაშვილი","phones":["+995599123456"],"work_status":"working"},
           "accounts":[{"id":"a1","balance":"847.5000","currency":"GEL","type":"current"}],
           "car":{"id":"c1","number":"TB-123-AB","brand":"Toyota","model":"Prius"}},
          {"driver_profile":{"id":"dp2","first_name":"Nika","last_name":"Javakhishvili","phones":[]},
           "accounts":[{"id":"a2","balance":"12.00","currency":"GEL","type":"current"}]}
        ],"total":3,"limit":2,"offset":0}
        """;
    private const string ProfilesPage2 = """
        {"driver_profiles":[
          {"driver_profile":{"id":"dp3","first_name":"Levan","last_name":"K"},"accounts":[]}
        ],"total":3,"limit":2,"offset":2}
        """;

    [Fact]
    public async Task Profiles_are_paged_by_offset_and_mapped()
    {
        var (client, fake) = Build();
        fake.On("v1/parks/driver-profiles/list", ProfilesPage1).On("v1/parks/driver-profiles/list", ProfilesPage2);

        var profiles = await client.GetDriverProfilesAsync(Park);

        Assert.Equal(3, profiles.Count);
        Assert.Equal("dp1", profiles[0].DriverProfileId);
        Assert.Equal("გიორგი მამულაშვილი", profiles[0].Name);
        Assert.Equal(847.5m, profiles[0].Balance);
        Assert.Equal("GEL", profiles[0].Currency);
        Assert.Equal("TB-123-AB", profiles[0].CarPlate);
        Assert.Equal("+995599123456", profiles[0].Phone);
        Assert.Equal(0m, profiles[2].Balance); // no accounts → 0

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(0, fake.Requests[0].Body["offset"]!.GetValue<int>());
        Assert.Equal(2, fake.Requests[1].Body["offset"]!.GetValue<int>());
        Assert.Equal("abc123", fake.Requests[0].Body["query"]!["park"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_request_carries_park_headers()
    {
        var (client, fake) = Build();
        fake.On("v1/parks/driver-profiles/list", ProfilesPage2);
        await client.GetDriverProfilesAsync(Park);

        var h = fake.Requests[0].Headers;
        Assert.Equal("taxi/park/abc123", h.GetValues("X-Client-ID").Single());
        Assert.Equal("secret-key", h.GetValues("X-API-Key").Single());
        Assert.Equal("abc123", h.GetValues("X-Park-ID").Single());
        Assert.Equal("en", h.GetValues("Accept-Language").Single());
        Assert.False(h.Contains("X-Idempotency-Token")); // reads carry no token
    }

    [Fact]
    public async Task Balance_query_filters_by_profile_id()
    {
        var (client, fake) = Build();
        fake.On("v1/parks/driver-profiles/list", ProfilesPage1);
        var b = await client.GetDriverBalanceAsync(Park, "dp1");
        Assert.Equal(847.5m, b);
        var ids = fake.Requests[0].Body["query"]!["park"]!["driver_profile"]!["id"]!.AsArray();
        Assert.Equal("dp1", ids[0]!.GetValue<string>());
    }

    [Fact]
    public async Task Cashout_posts_negative_amount_with_category_and_idempotency_token()
    {
        var (client, fake) = Build();
        fake.On("v2/parks/driver-profiles/transactions", """{"id":"tx-001","amount":"-200.00","currency":"GEL","event_at":"2026-09-18T10:00:00+00:00","category_id":"partner_service_manual"}""");

        var r = await client.PostCashoutTransactionAsync(Park, "dp1", 200m, "smoke-key");

        Assert.True(r.Success);
        Assert.Equal("tx-001", r.TransactionId);

        var (path, body, headers) = fake.Requests[0];
        Assert.Equal("v2/parks/driver-profiles/transactions", path);
        Assert.Equal("abc123", body["park_id"]!.GetValue<string>());
        Assert.Equal("dp1", body["driver_profile_id"]!.GetValue<string>());
        Assert.Equal("partner_service_manual", body["category_id"]!.GetValue<string>());
        Assert.Equal("-200.00", body["amount"]!.GetValue<string>());
        Assert.Equal(YandexFleetClient.IdempotencyToken("smoke-key"), headers.GetValues("X-Idempotency-Token").Single());
        Assert.Equal(32, headers.GetValues("X-Idempotency-Token").Single().Length);
    }

    [Fact]
    public async Task Reversal_posts_positive_amount_and_same_token_for_same_key()
    {
        var (client, fake) = Build();
        fake.On("v2/parks/driver-profiles/transactions", """{"id":"tx-rev"}""");
        var r = await client.PostReversalTransactionAsync(Park, "dp1", 200m, "rev:smoke-key");
        Assert.True(r.Success);
        Assert.Equal("200.00", fake.Requests[0].Body["amount"]!.GetValue<string>());
        Assert.Equal(YandexFleetClient.IdempotencyToken("rev:smoke-key"), YandexFleetClient.IdempotencyToken("rev:smoke-key"));
        Assert.NotEqual(YandexFleetClient.IdempotencyToken("smoke-key"), YandexFleetClient.IdempotencyToken("rev:smoke-key"));
    }

    [Fact]
    public async Task Read_only_mode_blocks_writes()
    {
        var (client, _) = Build(readOnly: true);
        await Assert.ThrowsAsync<YandexReadOnlyModeException>(() => client.PostCashoutTransactionAsync(Park, "dp1", 10m, "k"));
    }

    [Fact]
    public async Task Rate_limit_and_server_errors_are_transient_for_the_retry_wrapper()
    {
        var (client, fake) = Build();
        fake.On("v1/parks/driver-profiles/list", """{"code":"too_many_requests","message":"slow down"}""", HttpStatusCode.TooManyRequests);
        await Assert.ThrowsAsync<YandexTransientException>(() => client.GetDriverProfilesAsync(Park));

        var (client2, fake2) = Build();
        fake2.On("v2/parks/driver-profiles/transactions", "<html>bad gateway</html>", HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<YandexTransientException>(() => client2.PostCashoutTransactionAsync(Park, "dp1", 10m, "k"));
    }

    [Fact]
    public async Task Rejected_write_returns_failure_with_yandex_code()
    {
        var (client, fake) = Build();
        fake.On("v2/parks/driver-profiles/transactions", """{"code":"insufficient_balance","message":"Driver balance is too low"}""", HttpStatusCode.BadRequest);
        var r = await client.PostCashoutTransactionAsync(Park, "dp1", 10m, "k");
        Assert.False(r.Success);
        Assert.Equal("insufficient_balance", r.ErrorCode);
        Assert.Contains("too low", r.ErrorMessage);
    }

    [Fact]
    public async Task Transactions_follow_cursor_and_map_amount_strings()
    {
        var (client, fake) = Build();
        fake.On("v2/parks/driver-profiles/transactions/list", """{"transactions":[{"id":"t1","event_at":"2026-09-18T09:00:00+04:00","category_id":"partner_service_manual","category_name":"Manual","amount":"-100.0000","currency":"GEL","description":"Cashout · PayTaxi"}],"cursor":"c2"}""")
            .On("v2/parks/driver-profiles/transactions/list", """{"transactions":[{"id":"t2","event_at":"2026-09-18T10:00:00+04:00","category_id":"partner_ride_fee","amount":"18.49","currency":"GEL"}]}""");

        var txs = await client.GetTransactionsAsync(Park, "dp1", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        Assert.Equal(2, txs.Count);
        Assert.Equal(-100m, txs[0].Amount);
        Assert.Equal("partner_service_manual", txs[0].Category);
        Assert.Equal(new DateTime(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc), txs[0].CreatedAt);
        Assert.Equal("c2", fake.Requests[1].Body["cursor"]!.GetValue<string>());
        Assert.Equal("dp1", fake.Requests[0].Body["query"]!["park"]!["driver_profile"]!["id"]!.GetValue<string>());
        Assert.NotNull(fake.Requests[0].Body["query"]!["park"]!["transaction"]!["event_at"]!["from"]);
    }

    [Fact]
    public async Task Orders_map_price_and_addresses()
    {
        var (client, fake) = Build();
        fake.On("v1/parks/orders/list", """{"orders":[{"id":"o1","status":"complete","ended_at":"2026-09-18T08:30:00+00:00","price":"18.49","address_from":{"address":"Rustaveli Ave 12"},"route_points":[{"address":"Vake Park"}]}]}""");
        var orders = await client.GetOrdersAsync(Park, "dp1", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        var o = Assert.Single(orders);
        Assert.Equal(18.49m, o.Amount);
        Assert.Equal("Rustaveli Ave 12", o.From);
        Assert.Equal("Vake Park", o.To);
    }
}
