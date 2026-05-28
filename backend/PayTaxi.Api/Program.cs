using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Adapters.Yandex;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Database ─────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── JWT Auth ─────────────────────────────────────────────────────
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"] ?? throw new InvalidOperationException("Jwt:Key is required in config");
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
    });

builder.Services.AddAuthorization();

// ── Bank adapters ────────────────────────────────────────────────
// Keyed registrations let the saga resolve by Park.BankProvider string.
// When BankPayout:UseMock=true (default in dev), all keys point at the mock
// so any park — regardless of its configured BankProvider — uses the mock.
// Phase 4 will flip UseMock=false and the real BOG/TBC adapters take over.
var useMockBank = builder.Configuration.GetValue("BankPayout:UseMock", true);

builder.Services.Configure<MockBankPayoutOptions>(
    builder.Configuration.GetSection(MockBankPayoutOptions.SectionName));

if (useMockBank)
{
    builder.Services.AddSingleton<MockBankPayoutProvider>();
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("MOCK", (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("BOG",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("TBC",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("bog",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
    builder.Services.AddKeyedSingleton<IBankPayoutAdapter>("tbc",  (sp, _) => sp.GetRequiredService<MockBankPayoutProvider>());
}
else
{
    builder.Services.AddKeyedScoped<IBankPayoutAdapter, BogPayoutAdapter>("BOG");
    builder.Services.AddKeyedScoped<IBankPayoutAdapter, TbcPayoutAdapter>("TBC");
    builder.Services.AddKeyedScoped<IBankPayoutAdapter, BogPayoutAdapter>("bog");
    builder.Services.AddKeyedScoped<IBankPayoutAdapter, TbcPayoutAdapter>("tbc");
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
    builder.Services.AddScoped<YandexFleetClient>();
    builder.Services.AddScoped<IYandexFleetClient>(sp =>
        new ResilientYandexFleetClient(
            inner:   sp.GetRequiredService<YandexFleetClient>(),
            limiter: sp.GetRequiredService<IYandexRateLimiter>(),
            audit:   sp.GetRequiredService<IApiAuditLogger>(),
            opts:    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<YandexFleetOptions>>(),
            log:     sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResilientYandexFleetClient>>()));
}

// ── Cashout saga ─────────────────────────────────────────────────
builder.Services.AddScoped<ICashoutOrchestrator, CashoutOrchestrator>();

// ── Balance sync worker ──────────────────────────────────────────
builder.Services.Configure<BalanceSyncOptions>(
    builder.Configuration.GetSection(BalanceSyncOptions.SectionName));
builder.Services.AddHostedService<BalanceSyncWorker>();

// ── Auth ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

// ── Rate limiting ────────────────────────────────────────────────
// "auth" policy guards login / OTP endpoints. Partitioned by source IP so
// one client can't lock everyone out; the per-identifier lockout in
// AdminAuthController complements this by punishing specific accounts.
//   - 10 requests per minute per IP
//   - queue depth 0 → excess gets 429 immediately
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opts.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ── CORS for Angular dev server ──────────────────────────────────
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:4200")
         .AllowAnyHeader()
         .AllowAnyMethod()));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Seed the database on startup (no-op if already seeded) ───────
await SeedData.EnsureSeededAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
