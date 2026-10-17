using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks.Tbc;
using Xunit;

namespace PayTaxi.Tests.Tbc;

/// <summary>
/// TBC credentials name their environment, and a server with real rails never calls the sandbox: a payout or a settlement
/// the sandbox accepted would be recorded as paid while no money moved. Omitted, the environment is production.
/// </summary>
public class TbcTestEnvironmentTests
{
    private static BankAccountContext Source(string credentialsJson) => new(
        ParkId: Guid.NewGuid(), ParkBankAccountId: Guid.NewGuid(), Provider: "tbc", BankCode: "TB",
        SourceIban: "GE48TB7044436080100017", SourceHolderName: "Tbilisi Taxi Service LLC", CredentialsJson: credentialsJson);

    private static (TbcPayoutAdapter adapter, FakeTbcHandler fake) Build(bool allowTest)
    {
        var fake = new FakeTbcHandler();
        var opts = new TbcDbiOptions { StatusPollAttempts = 1, StatusPollDelayMs = 0, AllowTestEnvironment = allowTest };
        var soap = new TbcSoapClient(opts, NullLogger.Instance, () => fake);
        return (new TbcPayoutAdapter(soap, Options.Create(opts), NullLogger<TbcPayoutAdapter>.Instance), fake);
    }

    private static BankTransferRequest Payout(string credentialsJson) => new(
        IdempotencyKey: "env-" + Guid.NewGuid().ToString("N"), Source: Source(credentialsJson),
        DestinationIban: "GE62TB0011223344556677", DestinationName: "Driver", Amount: 10m, Currency: "GEL", Reference: "PayTaxi cashout");

    [Fact]
    public void Credentials_without_an_environment_are_production()
    {
        var creds = TbcCredentials.Parse("""{ "username": "u", "password": "p" }""");
        Assert.True(creds.IsProduction);
        Assert.Equal("production", creds.Environment);
        Assert.False(TbcCredentials.DeclaresTestEnvironment("""{ "username": "u", "password": "p" }"""));
    }

    [Theory]
    [InlineData("""{ "environment": "test" }""", true)]
    [InlineData("""{ "Environment": "TEST" }""", true)]
    [InlineData("""{ "environment": "" }""", true)]
    [InlineData("""{ "environment": "production", "ENVIRONMENT": "test" }""", true)]   // a repeated key counts too
    [InlineData("""{ "environment": "production" }""", false)]
    [InlineData("""{ "environment": "Prod" }""", false)]
    [InlineData("""{ "mock": true }""", false)]
    public void A_declared_environment_other_than_production_is_a_test_environment(string json, bool test) =>
        Assert.Equal(test, TbcCredentials.DeclaresTestEnvironment(json));

    [Fact]
    public async Task With_real_rails_a_payout_with_test_credentials_never_reaches_the_bank_and_waits()
    {
        var (adapter, fake) = Build(allowTest: false);
        fake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(1)).On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));

        var result = await adapter.SendPayoutAsync(Payout("""{ "username": "sandbox", "password": "p", "environment": "test" }"""));

        Assert.False(result.Success);
        Assert.True(result.IsRetryable);            // a setup wait: nothing was sent, nothing is given back
        Assert.Equal("TBC_NOT_CONFIGURED", result.ErrorCode);
        Assert.Contains("TBC_TEST_ENVIRONMENT_NOT_ALLOWED", result.ErrorMessage);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task With_real_rails_no_status_poll_or_lookup_of_a_sandbox_order_is_made()
    {
        // The settlement and the saga poll an order the bank accepted: through test credentials, no answer comes back —
        // so a sandbox order can never be turned into a completed payout or settlement.
        var (adapter, fake) = Build(allowTest: false);
        fake.On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F")).On("GetSinglePaymentId", FakeTbcHandler.StatusResponse("F"));
        var source = Source("""{ "username": "sandbox", "password": "p", "environment": "test" }""");

        await Assert.ThrowsAsync<TbcConfigurationException>(() => adapter.GetTransferOutcomeAsync(source, "541202018"));
        await Assert.ThrowsAsync<TbcConfigurationException>(() => adapter.FindTransferByIdempotencyKeyAsync(source, "settle:x"));
        await Assert.ThrowsAsync<TbcConfigurationException>(() => adapter.GetBalanceAsync(source));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Production_credentials_and_an_allowed_sandbox_go_through()
    {
        var (real, realFake) = Build(allowTest: false);
        realFake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(7)).On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));
        Assert.True((await real.SendPayoutAsync(Payout("""{ "username": "u", "password": "p" }"""))).Success);

        var (staging, stagingFake) = Build(allowTest: true);
        stagingFake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(8)).On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));
        Assert.True((await staging.SendPayoutAsync(Payout("""{ "username": "u", "password": "p", "environment": "test" }"""))).Success);
    }
}
