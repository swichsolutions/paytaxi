using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Adapters.Banks.Tbc;
using PayTaxi.Infrastructure.Adapters.Sms;
using PayTaxi.Infrastructure.Adapters.Yandex;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using System.Text;
using System.Threading.RateLimiting;

// Server-side formatting must not depend on the host machine's locale (money strings in
// API messages, logs). Invoices already format explicitly with InvariantCulture.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// ── Utility: `dotnet run -- --new-encryption-key` prints a fresh 32-byte key and exits ──
// (the format of both Encryption:Key and Encryption:HashKey — run it twice, they must differ)
if (args.Contains("--new-encryption-key"))
{
    Console.WriteLine(PayTaxi.Infrastructure.Security.FieldEncryptor.NewKeyBase64());
    return;
}

// ── Secrets: Azure Key Vault when KeyVault:Uri is set (managed identity / DefaultAzureCredential) ──
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new Azure.Identity.DefaultAzureCredential());
}

// ── Field encryption (PII / credentials at rest) ─────────────────
PayTaxi.Infrastructure.Security.FieldEncryptor.Configure(builder.Configuration["Encryption:Key"]);
// The keyed hashes beside the encrypted columns (PhoneHash, IbanHash) and of sign-in codes. A separate key: the HMAC
// key and the AES key are never the same bytes, and the hash key can be rotated alone (the startup migrator re-hashes).
PayTaxi.Infrastructure.Security.LookupHash.Configure(builder.Configuration["Encryption:HashKey"]);

// ── Guard rails: refuse to start half-configured ─────────────────
// Every environment but Development (tests, CI and e2e all run as Development): a host named "Staging" or anything else
// serves real data too, and used to start on the public placeholder JWT key. The rules: PayTaxi.Api.Security.StartupGuard.
if (!builder.Environment.IsDevelopment())
{
    var problems = PayTaxi.Api.Security.StartupGuard.Problems(builder.Configuration, builder.Environment.EnvironmentName);
    if (problems.Count > 0)
        throw new InvalidOperationException($"Refusing to start in {builder.Environment.EnvironmentName} — missing/placeholder settings: " + string.Join(", ", problems));
}

// ── Database ─────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── JWT Auth ─────────────────────────────────────────────────────
var jwtSection = builder.Configuration.GetSection("Jwt");
// appsettings.json ships it blank on purpose: a non-empty placeholder there was a working signing key on any host that
// forgot to set its own. Development gets its key from appsettings.Development.json or the environment.
var jwtKey = jwtSection["Key"];
if (string.IsNullOrWhiteSpace(jwtKey)) throw new InvalidOperationException("Jwt:Key is required in config");
var adminRecheck = TimeSpan.FromSeconds(builder.Configuration.GetValue("Auth:AdminRecheckSeconds", 30));
var driverRecheck = TimeSpan.FromSeconds(builder.Configuration.GetValue("Auth:DriverRecheckSeconds", 30));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
        // An admin token is only as good as its account: deactivated, demoted or moved → refused (cached ~30 s); an
        // operator's also carries the parks of its licensee while that licensee is in force (AdminTokenCheck). A driver
        // token only while the driver is Active, on the phone it was issued for, in a session still alive (DriverTokenCheck).
        opts.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                await PayTaxi.Api.Security.AdminTokenCheck.ValidateAsync(ctx, adminRecheck);
                await PayTaxi.Api.Security.DriverTokenCheck.ValidateAsync(ctx, driverRecheck);
            },
        };
    });

builder.Services.AddAuthorization();

// ── Bank adapters ────────────────────────────────────────────────
// Keyed registrations let the saga resolve by Park.BankProvider string.
// When BankPayout:UseMock=true (default in dev), all keys point at the mock
// so any park — regardless of its configured BankProvider — uses the mock.
// Phase 4 will flip UseMock=false and the real BOG/TBC adapters take over.
var useMockBank = builder.Configuration.GetValue("BankPayout:UseMock", true);
// Which test rails this server may use at all (BankRailPolicy): the mock bank and a bank's sandbox exist for testing. With
// real rails — anything but Development, unless AllowMockRails — the mock is not registered, so nothing can reach it: an
// account whose provider has no adapter here waits (payouts) or fails (settlements), it never "completes".
var railPolicy = BankRailPolicy.From(builder.Environment.IsDevelopment(), builder.Configuration);
builder.Services.AddSingleton(railPolicy);
if (!builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("AllowMockRails")
    && (useMockBank || builder.Configuration.GetValue("YandexFleet:UseMock", true)))
    Console.Error.WriteLine($"WARNING: {builder.Environment.EnvironmentName} is running with a MOCK rail (AllowMockRails=true): " +
        "mock-bank payouts move no money, mock-Yandex balances are invented. Dry runs only.");

builder.Services.Configure<MockBankPayoutOptions>(
    builder.Configuration.GetSection(MockBankPayoutOptions.SectionName));

// The mock is registered under "mock"/"MOCK" only where test rails are allowed (Development, dev demos, an AllowMockRails dry
// run). It used to be registered everywhere "so a park account with provider=mock keeps working" — on the live server that
// let a park manager point its payout account at it: drivers debited on Yandex, nothing paid, Swich's share "sent".
// (UseMock=true outside Development needs AllowMockRails too — the startup guard — so it never registers the mock alone.)
if (railPolicy.MockProviderAllowed || useMockBank) builder.Services.AddSingleton<MockBankPayoutProvider>();
if (railPolicy.MockProviderAllowed)
{
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("MOCK", (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("mock", (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
}

if (useMockBank)
{
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("BOG",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("TBC",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("bog",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("tbc",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
}
else
{
    // Real rails. TBC (launch): SOAP Integration Service with the park's client certificate.
    // Singleton because it caches one HttpClient per park credential set.
    builder.Services.Configure<TbcDbiOptions>(builder.Configuration.GetSection(TbcDbiOptions.SectionName));
    // Whether TBC's sandbox may be called is the rail policy's call, never a setting of its own (it would bypass the guard).
    builder.Services.PostConfigure<TbcDbiOptions>(o => o.AllowTestEnvironment = railPolicy.TestEnvironmentAllowed);
    builder.Services.AddSingleton<TbcSoapClient>();
    builder.Services.AddSingleton<TbcPayoutAdapter>();
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("TBC", (sp, _) => sp.GetRequiredService<TbcPayoutAdapter>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("tbc", (sp, _) => sp.GetRequiredService<TbcPayoutAdapter>());
    builder.Services.AddKeyedScoped<IBankPayoutAdapter, BogPayoutAdapter>("BOG");
    builder.Services.AddKeyedScoped<IBankPayoutAdapter, BogPayoutAdapter>("bog");
}

// ── Yandex Fleet integration ─────────────────────────────────────
// Options bound from "YandexFleet" section in appsettings
builder.Services.Configure<YandexFleetOptions>(
    builder.Configuration.GetSection(YandexFleetOptions.SectionName));

// Shared cross-cutting services
builder.Services.AddSingleton<MockYandexFleetData>();
builder.Services.AddSingleton<IYandexRateLimiter, YandexRateLimiter>();
// Audit logger uses IServiceScopeFactory internally so it can be a singleton
// and never collide with the caller's scoped DbContext.
builder.Services.AddSingleton<IApiAuditLogger, ApiAuditLogger>();

// Pick mock vs real client based on UseMock flag, then wrap with the resilient decorator.
//   inner (Mock | Real) → ResilientYandexFleetClient(rate-limit + retry + audit)
// Consumers depend on IYandexFleetClient and get the wrapped instance.
var yandexOpts = builder.Configuration
    .GetSection(YandexFleetOptions.SectionName)
    .Get<YandexFleetOptions>() ?? new YandexFleetOptions();

if (yandexOpts.UseMock)
{
    builder.Services.AddScoped<MockYandexFleetClient>();
    builder.Services.AddScoped<IYandexFleetClient>(sp =>
        new ResilientYandexFleetClient(
            inner:   sp.GetRequiredService<MockYandexFleetClient>(),
            limiter: sp.GetRequiredService<IYandexRateLimiter>(),
            audit:   sp.GetRequiredService<IApiAuditLogger>(),
            opts:    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<YandexFleetOptions>>(),
            log:     sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientYandexFleetClient>>()));
}
else
{
    // Real HTTP client: one HttpClient (pooled) for the Fleet API; park credentials are read
    // per call from the parks table. Rate limit / retry / audit stay in the resilient wrapper.
    builder.Services.AddScoped<IYandexParkCredentialsProvider, DbYandexParkCredentialsProvider>();
    builder.Services.AddHttpClient<YandexFleetClient>(c =>
    {
        c.BaseAddress = new Uri(yandexOpts.BaseUrl);
        c.Timeout = TimeSpan.FromSeconds(Math.Max(5, yandexOpts.TimeoutSeconds));
    });
    builder.Services.AddScoped<IYandexFleetClient>(sp =>
        new ResilientYandexFleetClient(
            inner:   sp.GetRequiredService<YandexFleetClient>(),
            limiter: sp.GetRequiredService<IYandexRateLimiter>(),
            audit:   sp.GetRequiredService<IApiAuditLogger>(),
            opts:    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<YandexFleetOptions>>(),
            log:     sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientYandexFleetClient>>()));
}

// ── Cashout saga + payout queue + notifications + invoices ───────
builder.Services.Configure<CashoutOptions>(
    builder.Configuration.GetSection(CashoutOptions.SectionName));
builder.Services.AddScoped<ICashoutOrchestrator, CashoutOrchestrator>();
builder.Services.Configure<PayoutQueueOptions>(
    builder.Configuration.GetSection(PayoutQueueOptions.SectionName));
builder.Services.AddHostedService<PayoutQueueWorker>();

// ── Monthly settlement (licence agreement §2.5; nightly retries) ─
builder.Services.Configure<SettlementOptions>(
    builder.Configuration.GetSection(SettlementOptions.SectionName));
builder.Services.AddScoped<ISettlementService, SettlementService>();
builder.Services.AddHostedService<SettlementWorker>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.Configure<InvoiceOptions>(
    builder.Configuration.GetSection(InvoiceOptions.SectionName));
builder.Services.AddScoped<IInvoiceGenerator, InvoiceGenerator>();

// ── SMS: sign-in codes and payout-account notices ────────────────
// smsoffice.ge when Sms:Provider=smsoffice AND a key is set (Sms__SmsOffice__Key); otherwise the sender that only logs —
// and then the sign-in endpoint returns the code in its response (ISmsSender.DeliversForReal). Outside Development the
// startup guard allows the log sender only with Sms:AllowLogSender=true. One platform account, not per park.
builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection(SmsOptions.SectionName));
var smsOpts = builder.Configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();
if (smsOpts.UsesSmsOffice)
{
    // One pooled client for the gateway. The factory's own request logging is off: getBalance carries the API key in
    // its query string, and the sender logs what it needs itself (purpose, reference, error code — never the text).
    builder.Services.AddHttpClient(SmsOfficeSender.HttpClientName, c =>
    {
        c.BaseAddress = new Uri(smsOpts.SmsOffice.BaseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(10);
    }).RemoveAllLoggers();
    // The operator alert for an account that refuses to send (balance, key, API, sender) — a platform-wide incident.
    builder.Services.AddSingleton<ISmsGatewayAlerts, SmsGatewayIncident>();
    builder.Services.AddSingleton<ISmsSender, SmsOfficeSender>();
    builder.Services.AddHostedService<SmsOfficeBalanceCheck>();
}
else
{
    builder.Services.AddSingleton<ISmsSender, LoggingSmsSender>();
    if (!builder.Environment.IsDevelopment())
        Console.Error.WriteLine($"WARNING: {builder.Environment.EnvironmentName} has no SMS gateway (Sms:AllowLogSender=true): " +
            "no SMS leaves this host and sign-in codes are returned in the API response. Dry runs only.");
}

// ── Balance sync worker ──────────────────────────────────────────
builder.Services.Configure<BalanceSyncOptions>(
    builder.Configuration.GetSection(BalanceSyncOptions.SectionName));
builder.Services.AddHostedService<BalanceSyncWorker>();

// ── Reconciliation worker ────────────────────────────────────────
builder.Services.Configure<ReconciliationOptions>(
    builder.Configuration.GetSection(ReconciliationOptions.SectionName));
builder.Services.AddSingleton<ReconciliationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReconciliationWorker>()); // also reachable for "Run now"

// ── Incident log (licence agreement Annex 1 SLA) + its monitor ───
builder.Services.Configure<IncidentOptions>(
    builder.Configuration.GetSection(IncidentOptions.SectionName));
// The launch date lives on the licensee record (Settlements page); configuration is only the fallback.
builder.Services.AddScoped<IContractCalendar, LicenseeContractCalendar>();
builder.Services.AddSingleton<IncidentMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IncidentMonitor>()); // tests call RunOnceAsync directly

// ── Retention (the periods the drivers' privacy notice promises) ─
builder.Services.Configure<RetentionOptions>(
    builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddSingleton<RetentionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionWorker>()); // tests call RunOnceAsync directly

// ── Auth ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

// ── Rate limiting ────────────────────────────────────────────────
// "auth" policy guards login / OTP endpoints. Partitioned by source IP so
// one client can't lock everyone out; the per-identifier lockout in
// AdminAuthController complements this by punishing specific accounts.
//   - 10 requests per minute per IP (IPv6: per /64 — see ProxyTrust.PartitionKey)
//   - queue depth 0 → excess gets 429 immediately
// RateLimit:AuthPerMinute — per-IP permits per minute on the auth endpoints (default 10; tests raise it).
// "refresh" (the driver app's hourly token refresh and logout): its own, wider bucket. Sharing the sign-in bucket meant
// drivers behind one carrier NAT address used up each other's ten, and the app then dropped the 90-day session.
// "driver" (every signed-in driver endpoint): per driver (the token's subject), generous — a driver's app makes a few
// calls a minute; a script making hundreds is held back before it costs the database or the park's Yandex budget.
var authPerMinute = builder.Configuration.GetValue("RateLimit:AuthPerMinute", 10);
var refreshPerMinute = builder.Configuration.GetValue("RateLimit:RefreshPerMinute", 60);
var driverPerMinute = builder.Configuration.GetValue("RateLimit:DriverPerMinute", 300);
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
    opts.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: PayTaxi.Api.Security.ProxyTrust.PartitionKey(ctx.Connection.RemoteIpAddress),
        factory: _ => PerMinute(authPerMinute)));
    opts.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: PayTaxi.Api.Security.ProxyTrust.PartitionKey(ctx.Connection.RemoteIpAddress),
        factory: _ => PerMinute(refreshPerMinute)));
    opts.AddPolicy("driver", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { } sub
            ? "driver:" + sub
            : "ip:" + PayTaxi.Api.Security.ProxyTrust.PartitionKey(ctx.Connection.RemoteIpAddress),
        factory: _ => PerMinute(driverPerMinute)));
});

// ── CORS: allowed frontend origins from configuration (Cors:AllowedOrigins) ──
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p =>
        p.WithOrigins(allowedOrigins)
         .AllowAnyHeader()
         .AllowAnyMethod()));

// Behind App Service / a reverse proxy: trust X-Forwarded-For / -Proto for scheme + client IP
// (the rate limiter keys on it). Only when Proxy:TrustForwardedHeaders=true, and only from the proxy itself:
// Proxy:KnownNetworks (CIDR) / Proxy:KnownProxies (addresses), which every environment but Development must name
// (StartupGuard). Trusting any peer let anyone who reached the container port pick a fresh address — and a fresh
// sign-in rate-limit bucket — per request. Proxy:ForwardLimit = the number of proxies in front (default 1).
var trustProxy = builder.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders");
if (trustProxy)
{
    var (proxyNetworks, proxyAddresses, _) = PayTaxi.Api.Security.ProxyTrust.Read(builder.Configuration);
    var forwardLimit = builder.Configuration.GetValue("Proxy:ForwardLimit", 1);
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                           | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        // Empty lists mean "any peer" to the middleware — allowed in Development only (the guard refuses elsewhere).
        foreach (var n in proxyNetworks) o.KnownNetworks.Add(n);
        foreach (var a in proxyAddresses) o.KnownProxies.Add(a);
        o.ForwardLimit = Math.Max(1, forwardLimit);
    });
}

builder.Services.AddMemoryCache();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Database bootstrap ───────────────────────────────────────────
// Development: migrations + 3 demo parks + dev logins (no-op once seeded).
// Anything else: migrations + encryption backfill only; first super-admin from Bootstrap:* config.
if (app.Environment.IsDevelopment())
    await SeedData.EnsureSeededAsync(app.Services);
else
    await SeedData.EnsureProductionReadyAsync(app.Services,
        app.Configuration["Bootstrap:SuperAdminEmail"], app.Configuration["Bootstrap:SuperAdminPassword"]);

// With real rails: no active payout account may still name the mock (refuses to start — PayoutRailCheck), and every TBC
// account whose credentials are for the test environment is named in the error log.
using (var scope = app.Services.CreateScope())
    await PayoutRailCheck.EnsureAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), railPolicy,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PayoutRailCheck).FullName!));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // A developer's own live test of the gateway: every sign-in code now costs a real SMS to a real phone.
    if (smsOpts.UsesSmsOffice)
        app.Logger.LogWarning("Development is sending REAL SMS through smsoffice.ge (Sms:Provider=smsoffice with a key): " +
            "sign-in codes are not shown in the response or the log — test only with your own number.");
}

if (trustProxy) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// JSON and PDFs only: never let a browser sniff a response into something executable, and never send the API's URLs
// (which carry ids) on to another site as a referrer.
app.Use((ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    return next(ctx);
});
app.UseMiddleware<PayTaxi.Api.Middleware.IntegrationErrorMiddleware>();
app.UseCors();
// Authentication before the rate limiter: the "driver" policy partitions by the token's subject.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>Lets the integration tests host the API in-process (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program { }
