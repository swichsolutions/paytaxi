using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Security;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Boots the real API in-process against a throw-away Postgres database. The database is
/// dropped and recreated once per test run, then the Development seed fills it (3 demo parks,
/// drivers, destinations, admin logins) so every test starts from the same known state.
///
/// Connection: <c>PAYTAXI_TEST_DB</c> env var, else the local dev server
/// (localhost, postgres/postgres). CI provides a Postgres service container.
///
/// Mocks are made deterministic (no random transient/ambiguous bank failures, no latency),
/// the nightly/periodic workers are off, and the auth rate limits are raised so tests can log
/// in freely. Everything else is the production wiring.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string TestDbName = "paytaxi_test";

    private WebApplicationFactory<Program>? _factory;
    public HttpClient Client { get; private set; } = default!;
    public IServiceProvider Services => _factory!.Services;

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("PAYTAXI_TEST_DB")
        ?? $"Host=localhost;Database={TestDbName};Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        await RecreateDatabaseAsync();

        var encryptionKey = FieldEncryptor.NewKeyBase64();

        // Program.cs reads several settings eagerly (Jwt:Key into the bearer validation key, the
        // rate-limit permit, the Encryption key). WebApplicationFactory's in-memory overrides are
        // appended only AFTER those top-level statements have run — tokens would then be signed
        // with one key and validated with another. Environment variables are part of the
        // configuration from the first line, so the test settings go in as env vars.
        var settings = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__DefaultConnection"] = ConnectionString,
            ["Jwt__Key"] = "integration-tests-jwt-key-0123456789-abcdefghijklmnop",
            ["Jwt__Issuer"] = "paytaxi-api",
            ["Jwt__Audience"] = "paytaxi-clients",
            ["Encryption__Key"] = encryptionKey,
            ["Cors__AllowedOrigins__0"] = "http://localhost:4200",

            // Deterministic mocks.
            ["YandexFleet__UseMock"] = "true",
            ["YandexFleet__ReadOnlyMode"] = "false",
            ["YandexFleet__MockLatencyMs"] = "0",
            ["YandexFleet__MockTransientFailureRate"] = "0",
            ["YandexFleet__MinIntervalMs"] = "0",
            ["BankPayout__UseMock"] = "true",
            ["BankPayout__Mock__LatencyMs"] = "0",
            ["BankPayout__Mock__TransientFailureRate"] = "0",
            ["BankPayout__Mock__AmbiguousFailureRate"] = "0",
            ["BankPayout__Mock__AsyncExecution"] = "false",

            // Workers: only the payout queue (fast) — the rest would add noise.
            ["PayoutQueue__Enabled"] = "true",
            ["PayoutQueue__PollIntervalSeconds"] = "1",
            ["PayoutQueue__InitialDelaySeconds"] = "0",
            ["PayoutQueue__SpacingMs"] = "0",
            ["BalanceSync__Enabled"] = "false",
            ["Reconciliation__Enabled"] = "false",
            ["Settlement__Enabled"] = "false",
            ["Settlement__SwichIban"] = "GE12TB7100000000000001",

            // Let tests log in as often as they like; the cap itself has its own test.
            ["RateLimit__AuthPerMinute"] = "100000",
            ["Auth__OtpMaxPerWindow"] = "5",   // the 4-spelling login theory needs 4 codes for one phone
            ["Auth__OtpWindowMinutes"] = "10",
        };
        foreach (var (k, v) in settings) Environment.SetEnvironmentVariable(k, v);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));

        Client = _factory.CreateClient();
        Client.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private static async Task RecreateDatabaseAsync()
    {
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString);
        var dbName = csb.Database ?? TestDbName;
        csb.Database = "postgres";
        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", conn))
            await drop.ExecuteNonQueryAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn))
            await create.ExecuteNonQueryAsync();
    }

    // ── Helpers used by every test class ────────────────────────────

    public T Db<T>(Func<AppDbContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A driver JWT minted directly (no OTP), for the seeded driver with this Yandex profile id.</summary>
    public async Task<(HttpClient client, Guid driverId, Guid parkId)> DriverClientAsync(string yandexProfileId)
    {
        var (id, parkId, phoneHash) = await DbAsync(async db =>
        {
            var d = await db.Drivers.AsNoTracking().FirstAsync(x => x.YandexDriverProfileId == yandexProfileId);
            return (d.Id, d.ParkId, d.PhoneHash);
        });
        var jwt = Services.GetRequiredService<IJwtTokenService>();
        var token = jwt.IssueDriverToken(id, parkId, phoneHash);
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return (client, id, parkId);
    }

    /// <summary>An admin client logged in through the real endpoint (seeded dev logins).</summary>
    public async Task<HttpClient> AdminClientAsync(string email, string password)
    {
        var client = _factory!.CreateClient();
        var res = await client.PostAsJsonAsync("/api/admin/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("token").GetString());
        return client;
    }

    public Task<HttpClient> SuperAdminAsync() => AdminClientAsync("ops@swich.dev", "swich2026!");
    public Task<HttpClient> OperatorAsync() => AdminClientAsync("levan@operator.local", "operator1!");

    /// <summary>Park manager for a seeded park slug (passwords are park1!, park2!, park3! by seed order).</summary>
    public async Task<HttpClient> ParkAdminAsync(string parkSlug)
    {
        foreach (var i in new[] { 1, 2, 3 })
        {
            var client = _factory!.CreateClient();
            var res = await client.PostAsJsonAsync("/api/admin/auth/login", new { email = $"manager@{parkSlug}.local", password = $"park{i}!" });
            if (!res.IsSuccessStatusCode) continue;
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("token").GetString());
            return client;
        }
        throw new InvalidOperationException($"No seeded manager login for {parkSlug}");
    }

    /// <summary>A valid Georgian TBC IBAN for a 16-digit account tail (mod-97 check digits computed).</summary>
    public static string TbcIban(string tail16)
    {
        var bban = "TB" + tail16;
        var numeric = string.Concat((bban + "GE00").Select(c => char.IsDigit(c) ? c.ToString() : (c - 'A' + 10).ToString()));
        var rem = (int)(System.Numerics.BigInteger.Parse(numeric) % 97);
        return $"GE{98 - rem:00}{bban}";
    }

    public static string Code(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture> { }
