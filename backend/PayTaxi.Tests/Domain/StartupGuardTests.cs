using System.Net;
using Microsoft.Extensions.Configuration;
using PayTaxi.Api.Security;
using PayTaxi.Infrastructure.Security;
using Xunit;

namespace PayTaxi.Tests.Domain;

/// <summary>What a non-Development host must have before it starts (PayTaxi.Api.Security.StartupGuard).</summary>
public class StartupGuardTests
{
    private static readonly string EncKey = FieldEncryptor.NewKeyBase64();
    private static readonly string HashKey = LookupHash.NewKeyBase64();
    private static readonly string JwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    private static Dictionary<string, string?> Valid() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Host=db;Database=paytaxi;Username=paytaxi;Password=s3cret-pw",
        ["Jwt:Key"] = JwtKey,
        ["Encryption:Key"] = EncKey,
        ["Encryption:HashKey"] = HashKey,
        ["Settlement:SwichIban"] = "GE55TB7643536080100006",
        ["YandexFleet:UseMock"] = "false",
        ["BankPayout:UseMock"] = "false",
        ["Sms:Provider"] = "smsoffice",
        ["Sms:SmsOffice:Key"] = SmsKey,
    };

    private const string SmsKey = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";

    private static IReadOnlyList<string> Problems(Action<Dictionary<string, string?>>? change = null)
    {
        var d = Valid();
        change?.Invoke(d);
        return StartupGuard.Problems(new ConfigurationBuilder().AddInMemoryCollection(d).Build());
    }

    [Fact]
    public void A_complete_configuration_passes() => Assert.Empty(Problems());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REPLACE_WITH_ENV_VAR_MIN_32_CHARS_LONG")]                 // the old appsettings.json placeholder
    [InlineData("ci-only-jwt-key-0123456789-abcdefghijklmnopqrstuvwxyz")]  // .github/workflows/ci.yml
    [InlineData("integration-tests-jwt-key-0123456789-abcdefghijklmnop")]  // the test fixture
    [InlineData("local-dev-only-key-0123456789-abcdefghijk")]               // the dev settings' kind
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_missing_public_or_trivial_JWT_key_is_refused(string? key) =>
        Assert.Contains(Problems(d => d["Jwt:Key"] = key), p => p.StartsWith("Jwt:Key"));

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]   // CI_ENC_KEY: 32 × 0x00
    [InlineData("AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=")]   // CI_HASH_KEY: 32 × 0x01
    [InlineData("dGVzdC1vbmx5LWtleS1mb3ItcGF5dGF4aS10ZXN0cyE=")]   // the fixture's "test-only-key-for-paytaxi-tests!"
    [InlineData("dGVzdC1vbmx5LWhhc2gta2V5LXBheXRheGktdGVzdHM=")]   // the fixture's hash key
    [InlineData("c2hvcnQ=")]                                       // not 32 bytes
    [InlineData("not base64 at all")]
    public void A_published_or_malformed_data_key_is_refused(string key)
    {
        Assert.Contains(Problems(d => d["Encryption:Key"] = key), p => p.StartsWith("Encryption:Key"));
        Assert.Contains(Problems(d => d["Encryption:HashKey"] = key), p => p.StartsWith("Encryption:HashKey"));
    }

    [Fact]
    public void The_keys_must_all_differ()
    {
        Assert.Contains(Problems(d => d["Encryption:HashKey"] = EncKey), p => p.Contains("must differ"));
        Assert.Contains(Problems(d => d["Jwt:Key"] = EncKey), p => p.StartsWith("Jwt:Key"));
    }

    [Theory]
    [InlineData("YandexFleet:UseMock")]
    [InlineData("BankPayout:UseMock")]
    public void A_mock_rail_needs_an_explicit_dry_run_switch(string flag)
    {
        Assert.Contains(Problems(d => d[flag] = "true"), p => p.StartsWith(flag));
        Assert.Contains(Problems(d => d.Remove(flag)), p => p.StartsWith(flag));   // both default to the mock when unset
        Assert.Empty(Problems(d => { d[flag] = "true"; d["AllowMockRails"] = "true"; }));
    }

    [Fact]
    public void The_sandbox_switch_is_for_staging_and_refused_in_Production()
    {
        var cfg = Valid();
        cfg["BankPayout:AllowTestEnvironment"] = "true";
        var config = new ConfigurationBuilder().AddInMemoryCollection(cfg).Build();
        Assert.Contains(StartupGuard.Problems(config, "Production"), p => p.StartsWith("BankPayout:AllowTestEnvironment"));
        Assert.Empty(StartupGuard.Problems(config, "Staging"));
        Assert.Empty(StartupGuard.Problems(config, "staging"));
        // Only a host named Staging may: a live host under another name ("Prod", "Live", none) is refused too.
        foreach (var name in new[] { "Prod", "Live", "Production-EU", "", null })
            Assert.Contains(StartupGuard.Problems(config, name), p => p.StartsWith("BankPayout:AllowTestEnvironment"));
        Assert.Empty(Problems());   // off: nothing to say
    }

    [Fact]
    public void A_host_without_an_SMS_gateway_needs_an_explicit_dry_run_switch()
    {
        // The log sender sends nothing and shows the sign-in codes in the API response: Development's convenience only.
        Assert.Contains(Problems(d => d["Sms:Provider"] = "log"), p => p.StartsWith("Sms:Provider"));
        Assert.Contains(Problems(d => d.Remove("Sms:Provider")), p => p.StartsWith("Sms:Provider"));   // defaults to log
        Assert.Empty(Problems(d => { d["Sms:Provider"] = "log"; d["Sms:AllowLogSender"] = "true"; }));
        // smsoffice named but no key: the registration falls back to the log sender, so the same switch is needed.
        Assert.Contains(Problems(d => d["Sms:SmsOffice:Key"] = ""), p => p.StartsWith("Sms:SmsOffice:Key"));
        Assert.Contains(Problems(d => d.Remove("Sms:SmsOffice:Key")), p => p.StartsWith("Sms:SmsOffice:Key"));
        Assert.Empty(Problems(d => { d["Sms:SmsOffice:Key"] = ""; d["Sms:AllowLogSender"] = "true"; }));
        Assert.Contains(Problems(d => d["Sms:Provider"] = "twilio"), p => p.StartsWith("Sms:Provider"));
    }

    [Theory]
    [InlineData("REPLACE_WITH_SMSOFFICE_KEY")]
    [InlineData("dev-only-sms-key-0123456789")]
    [InlineData("test-only-sms-key-0123456789")]
    [InlineData("short")]
    [InlineData("has white space in it 0123456789")]
    public void A_placeholder_smsoffice_key_is_refused_even_for_a_dry_run(string key)
    {
        Assert.Contains(Problems(d => d["Sms:SmsOffice:Key"] = key), p => p.StartsWith("Sms:SmsOffice:Key"));
        // A non-empty key is used as the real one, so the dry-run switch does not excuse it.
        Assert.Contains(Problems(d => { d["Sms:SmsOffice:Key"] = key; d["Sms:AllowLogSender"] = "true"; }), p => p.StartsWith("Sms:SmsOffice:Key"));
    }

    [Fact]
    public void Forwarded_headers_are_trusted_only_from_a_named_proxy()
    {
        Assert.Contains(Problems(d => d["Proxy:TrustForwardedHeaders"] = "true"), p => p.StartsWith("Proxy:Known"));
        Assert.Empty(Problems(d => { d["Proxy:TrustForwardedHeaders"] = "true"; d["Proxy:KnownNetworks:0"] = "10.0.0.0/8"; }));
        Assert.Empty(Problems(d => { d["Proxy:TrustForwardedHeaders"] = "true"; d["Proxy:KnownProxies:0"] = "172.17.0.1"; }));
        Assert.Contains(Problems(d => { d["Proxy:TrustForwardedHeaders"] = "true"; d["Proxy:KnownNetworks:0"] = "10.0.0.0"; }),
            p => p.Contains("KnownNetworks"));
    }

    [Fact]
    public void The_rate_limit_partition_is_the_IPv4_address_or_the_IPv6_slash_64()
    {
        Assert.Equal("203.0.113.7", ProxyTrust.PartitionKey(IPAddress.Parse("203.0.113.7")));
        Assert.Equal("203.0.113.7", ProxyTrust.PartitionKey(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.NotEqual(ProxyTrust.PartitionKey(IPAddress.Parse("203.0.113.7")), ProxyTrust.PartitionKey(IPAddress.Parse("203.0.113.8")));
        // One subscriber line holds a whole /64: every address in it is one client.
        Assert.Equal(ProxyTrust.PartitionKey(IPAddress.Parse("2001:db8:1:2:aaaa::1")), ProxyTrust.PartitionKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff")));
        Assert.NotEqual(ProxyTrust.PartitionKey(IPAddress.Parse("2001:db8:1:2::1")), ProxyTrust.PartitionKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("unknown", ProxyTrust.PartitionKey(null));
    }
}
