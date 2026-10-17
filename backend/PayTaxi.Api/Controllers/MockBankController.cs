using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Development-only controls for a park account served by the MOCK bank, so a demo or an e2e run can
/// show the contract's "insufficient funds → payouts wait → top up → they go out" story (Annex 3 §5)
/// without real money. Swich only. Outside Development, and for any account a real bank adapter
/// serves, every route answers 404 — the endpoint does not exist there.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/parks/{parkId:guid}/bank-accounts/{accountId:guid}")]
public class MockBankController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly IWebHostEnvironment _env;
    private readonly MockBankPayoutOptions _mockOpts;
    private readonly ILogger<MockBankController> _log;

    public MockBankController(
        AppDbContext db, IServiceProvider services, IWebHostEnvironment env,
        IOptions<MockBankPayoutOptions> mockOpts, ILogger<MockBankController> log)
    {
        _db = db;
        _services = services;
        _env = env;
        _mockOpts = mockOpts.Value; // the same instance the singleton mock reads, so a switch takes effect at once
        _log = log;
    }

    /// <summary>
    /// The park tops up its (mock) account. Body: <c>{ amount }</c>. Payouts of this park waiting for
    /// funds on this account are made due at once, so the demo does not sit out the backoff — a real
    /// bank gives no top-up signal; there the next attempt (at most 15 minutes later) notices it.
    /// </summary>
    [HttpPost("mock-topup")]
    public async Task<IActionResult> TopUp(Guid parkId, Guid accountId, [FromBody] MockTopUpRequest? body, CancellationToken ct)
    {
        var (mock, error) = await ResolveAsync(parkId, accountId, ct);
        if (mock is null) return error!;

        var amount = body?.Amount ?? 0m;
        if (amount <= 0 || amount > 1_000_000m) return BadRequest(new { error = "invalid_amount" });
        if (amount != Math.Round(amount, 2)) return BadRequest(new { error = "amount_precision" });

        var balance = mock.TopUp(accountId, amount);
        var released = await ReleaseWaitingAsync(parkId, accountId, ct);
        _log.LogInformation("Mock top-up: park {Park} account {Account} +{Amount} → {Balance} by {Actor}; {Released} waiting payout(s) due now",
            parkId, accountId, amount, balance, ActorLabel, released);
        return Ok(new { accountId, balance, enforceBalance = mock.EnforcesBalance, released });
    }

    /// <summary>
    /// Set the mock balance outright and, optionally, switch the mock's funds check on or off.
    /// Body: <c>{ balance, enforceBalance? }</c>. The switch is process-wide (every mock account) and
    /// stays until changed or the API restarts — an e2e run that turns it on must turn it off again.
    /// </summary>
    [HttpPost("mock-balance")]
    public async Task<IActionResult> SetBalance(Guid parkId, Guid accountId, [FromBody] MockBalanceRequest? body, CancellationToken ct)
    {
        var (mock, error) = await ResolveAsync(parkId, accountId, ct);
        if (mock is null) return error!;

        if (body?.Balance is not { } balance || balance < 0 || balance > 10_000_000m) return BadRequest(new { error = "invalid_balance" });
        if (balance != Math.Round(balance, 2)) return BadRequest(new { error = "amount_precision" });

        if (body.EnforceBalance is { } enforce) _mockOpts.EnforceBalance = enforce;
        mock.SetBalance(accountId, balance);
        var released = await ReleaseWaitingAsync(parkId, accountId, ct);
        _log.LogInformation("Mock balance: park {Park} account {Account} = {Balance} (enforce={Enforce}) by {Actor}",
            parkId, accountId, balance, mock.EnforcesBalance, ActorLabel);
        return Ok(new { accountId, balance = mock.GetBalance(accountId), enforceBalance = mock.EnforcesBalance, released });
    }

    /// <summary>The mock behind this park account, or the response to send instead.</summary>
    private async Task<(MockBankPayoutProvider? Mock, IActionResult? Error)> ResolveAsync(Guid parkId, Guid accountId, CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return (null, NotFound());
        if (!IsSuperAdmin) return (null, Forbid());

        var provider = await _db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.ParkId == parkId)
            .Select(a => a.Provider)
            .FirstOrDefaultAsync(ct);
        if (provider is null) return (null, NotFound(new { error = "account_not_found" }));

        // The adapter the saga would use for this account (same resolution order). With
        // BankPayout:UseMock=true a "tbc" account is served by the mock too; with real rails it is not.
        var adapter = _services.GetKeyedService<IBankPayoutAdapter>(provider)
                   ?? _services.GetKeyedService<IBankPayoutAdapter>(provider.ToUpperInvariant());
        return adapter is MockBankPayoutProvider mock
            ? (mock, null)
            : (null, NotFound(new { error = "not_a_mock_account" }));
    }

    /// <summary>Make this account's payouts that wait for funds due now (the worker picks them up next tick).</summary>
    private Task<int> ReleaseWaitingAsync(Guid parkId, Guid accountId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var prefix = BankErrorCodes.InsufficientFunds + ":";
        return _db.Cashouts
            .Where(c => c.ParkId == parkId && c.Status == CashoutStatus.Queued
                     && (c.ParkBankAccountId == accountId || c.ParkBankAccountId == null)
                     && c.FailureReason != null && c.FailureReason.StartsWith(prefix))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextAttemptAt, now), ct);
    }
}

public record MockTopUpRequest(decimal? Amount);

public record MockBalanceRequest(decimal? Balance, bool? EnforceBalance);
