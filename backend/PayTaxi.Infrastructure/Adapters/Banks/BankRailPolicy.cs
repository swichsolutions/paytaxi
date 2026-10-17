using Microsoft.Extensions.Configuration;

namespace PayTaxi.Infrastructure.Adapters.Banks;

/// <summary>
/// Which test rails this server may use. The mock bank and a bank's sandbox are for testing only: a payout "completed"
/// through either moves no money, while the driver's real Yandex balance is debited — and a settlement "sent" through
/// either never pays Swich. So a server that runs with real rails (anything but Development, unless the deliberate dry-run
/// switch AllowMockRails is on) has no mock bank at all — Program.cs does not register it, the API refuses provider "mock"
/// and banks it has no adapter for — and refuses bank credentials for a test environment, on write and in the adapter.
///
/// Testing a park's real TBC credentials against TBC's sandbox happens on a staging deployment: Development, AllowMockRails,
/// or <c>BankPayout:AllowTestEnvironment</c> — the last refused in Production by the startup guard. One singleton, built
/// once in Program.cs; the tests replace it to see a real-rails server's answers.
/// </summary>
public sealed record BankRailPolicy(bool MockProviderAllowed, bool TestEnvironmentAllowed)
{
    /// <summary>Real rails only: no mock provider, no sandbox credentials.</summary>
    public static readonly BankRailPolicy RealRails = new(false, false);

    /// <summary>Development (and the tests): every test rail, as before.</summary>
    public static readonly BankRailPolicy Development = new(true, true);

    public static BankRailPolicy From(bool isDevelopment, IConfiguration cfg)
    {
        var dryRun = cfg.GetValue<bool>("AllowMockRails");
        var mock = isDevelopment || dryRun;
        return new BankRailPolicy(mock, mock || cfg.GetValue<bool>("BankPayout:AllowTestEnvironment"));
    }

    /// <summary>The payout-account providers the API accepts on this server (the console offers these).</summary>
    public IReadOnlyList<string> AllowedProviders => MockProviderAllowed ? new[] { "tbc", "bog", "mock" } : new[] { "tbc", "bog" };

    /// <summary>
    /// The provider for an account at <paramref name="bankCode"/> when none is named: the bank's own adapter for TBC and BoG;
    /// any other bank has none — the mock in Development (as before), nothing on a real-rails server (refused, unsupported_bank).
    /// </summary>
    public string? DefaultProviderFor(string bankCode) => bankCode switch
    {
        "TB" => "tbc",
        "BG" => "bog",
        _ => MockProviderAllowed ? "mock" : null,
    };
}
