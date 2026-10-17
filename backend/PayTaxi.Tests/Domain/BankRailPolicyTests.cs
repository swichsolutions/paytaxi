using Microsoft.Extensions.Configuration;
using PayTaxi.Infrastructure.Adapters.Banks;
using Xunit;

namespace PayTaxi.Tests.Domain;

/// <summary>Which test rails a server may use (BankRailPolicy): the mock bank and a bank's sandbox are for testing only.</summary>
public class BankRailPolicyTests
{
    private static BankRailPolicy From(bool development, params (string Key, string Value)[] settings) =>
        BankRailPolicy.From(development, new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build());

    [Fact]
    public void Development_keeps_every_test_rail()
    {
        var p = From(true);
        Assert.True(p.MockProviderAllowed);
        Assert.True(p.TestEnvironmentAllowed);
        Assert.Equal(new[] { "tbc", "bog", "mock" }, p.AllowedProviders);
        Assert.Equal("mock", p.DefaultProviderFor("LB"));   // as before: another bank's account is served by the mock
    }

    [Fact]
    public void Real_rails_have_no_mock_and_no_sandbox()
    {
        var p = From(false);
        Assert.False(p.MockProviderAllowed);
        Assert.False(p.TestEnvironmentAllowed);
        Assert.Equal(new[] { "tbc", "bog" }, p.AllowedProviders);
        Assert.Equal("tbc", p.DefaultProviderFor("TB"));
        Assert.Equal("bog", p.DefaultProviderFor("BG"));
        Assert.Null(p.DefaultProviderFor("LB"));   // no adapter: refused (unsupported_bank), never the mock
        Assert.Null(p.DefaultProviderFor("PC"));
    }

    [Fact]
    public void A_dry_run_keeps_the_mock_and_a_staging_host_may_use_the_sandbox_only()
    {
        Assert.Equal(BankRailPolicy.Development, From(false, ("AllowMockRails", "true")));
        var staging = From(false, ("BankPayout:AllowTestEnvironment", "true"));
        Assert.False(staging.MockProviderAllowed);
        Assert.True(staging.TestEnvironmentAllowed);
    }
}
