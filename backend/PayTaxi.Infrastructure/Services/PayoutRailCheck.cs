using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Adapters.Banks.Tbc;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Startup check of the parks' payout accounts against the server's rails (<see cref="BankRailPolicy"/>). With real rails
/// the mock bank is not registered, so an active account still naming it (left from a dry run, or set before the API
/// refused it) could pay nobody: its payouts would wait for ever and its park's settlement would fail every month. That is
/// a configuration error to fix before the server takes traffic — refusing to start says so at once, instead of a queue
/// quietly filling up. A provider with no adapter at all (a typo written straight into the table) is the same error.
///
/// TBC credentials for the test environment are refused at every bank call on such a server; an account holding them is
/// logged as an error (a warning on a staging host that may use the sandbox), so whoever deploys sees which park's
/// credentials still have to be replaced by production ones. Development and AllowMockRails dry runs check nothing.
/// </summary>
public static class PayoutRailCheck
{
    private static readonly HashSet<string> RealProviders = new(StringComparer.OrdinalIgnoreCase) { "tbc", "bog" };

    /// <summary>Throws when an active payout account names a provider this server has no adapter for (real rails only).</summary>
    public static async Task EnsureAsync(AppDbContext db, BankRailPolicy policy, ILogger log, CancellationToken ct = default)
    {
        if (policy.MockProviderAllowed) return;

        var accounts = await db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.IsActive)
            .OrderBy(a => a.ParkId).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.ParkId, a.Provider, a.CredentialsEncrypted })
            .ToListAsync(ct);

        var unpayable = accounts.Where(a => !RealProviders.Contains(a.Provider.Trim())).ToList();
        if (unpayable.Count > 0)
            throw new InvalidOperationException(
                $"Refusing to start with real bank rails: {unpayable.Count} active payout account(s) name a provider this server has no adapter for — " +
                string.Join(", ", unpayable.Select(a => $"account {a.Id} of park {a.ParkId} (provider '{a.Provider}')")) +
                ". Set each one's provider to its bank (tbc / bog) or deactivate it; the mock bank is for testing only.");

        foreach (var a in accounts.Where(a => string.Equals(a.Provider.Trim(), "tbc", StringComparison.OrdinalIgnoreCase)
                                           && TbcCredentials.DeclaresTestEnvironment(a.CredentialsEncrypted)))
        {
            if (policy.TestEnvironmentAllowed)
                log.LogWarning("Payout account {Account} of park {Park} holds TBC test-environment credentials (sandbox testing on this host)", a.Id, a.ParkId);
            else
                log.LogError("Payout account {Account} of park {Park} holds TBC TEST-environment credentials: every bank call through it is refused on this server " +
                             "(TBC_TEST_ENVIRONMENT_NOT_ALLOWED) — its payouts wait and its settlement fails until the park's production credentials are saved", a.Id, a.ParkId);
        }
    }
}
