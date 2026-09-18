using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks.Tbc;
using Xunit;

namespace PayTaxi.Tests.Tbc;

public class TbcPayoutAdapterTests
{
    private static readonly XNamespace Myg = FakeTbcHandler.Ns;
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    private const string CredsJson = """
        { "username": "MBS_LTD_DBI", "password": "Secret1!", "environment": "test", "debitCurrency": "GEL" }
        """;

    private static readonly BankAccountContext Source = new(
        ParkId: Guid.NewGuid(),
        ParkBankAccountId: Guid.NewGuid(),
        Provider: "tbc",
        BankCode: "TB",
        SourceIban: "GE48TB7044436080100017",
        SourceHolderName: "Tbilisi Taxi Service LLC",
        CredentialsJson: CredsJson);

    private static (TbcPayoutAdapter adapter, FakeTbcHandler fake) Build(int pollAttempts = 3)
    {
        var fake = new FakeTbcHandler();
        var opts = new TbcDbiOptions { StatusPollAttempts = pollAttempts, StatusPollDelayMs = 0 };
        var soap = new TbcSoapClient(opts, NullLogger.Instance, () => fake);
        var adapter = new TbcPayoutAdapter(soap, Options.Create(opts), NullLogger<TbcPayoutAdapter>.Instance);
        return (adapter, fake);
    }

    private static BankTransferRequest Request(string destIban = "GE62TB0011223344556677", decimal amount = 49.50m) => new(
        IdempotencyKey: "smoke-1789650642",
        Source: Source,
        DestinationIban: destIban,
        DestinationName: "გიორგი მამულაშვილი",
        Amount: amount,
        Currency: "GEL",
        Reference: "PayTaxi cashout ebf82eef");

    // ── Envelope shape ───────────────────────────────────────────────

    [Fact]
    public async Task Import_builds_within_bank_order_with_wsse_header_and_soapaction()
    {
        var (adapter, fake) = Build();
        fake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(541202018))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));

        var result = await adapter.SendPayoutAsync(Request());

        Assert.True(result.Success);
        Assert.False(result.IsPending);
        Assert.Equal("541202018", result.TransferId);

        var (op, req) = fake.Requests[0];
        Assert.Equal("ImportSinglePaymentOrders", op);

        var token = req.Descendants(Wsse + "UsernameToken").Single();
        Assert.Equal("MBS_LTD_DBI", (string?)token.Element(Wsse + "Username"));
        Assert.Equal("Secret1!", (string?)token.Element(Wsse + "Password"));
        Assert.Null(token.Element(Wsse + "Nonce")); // certificate package: no Digipass code

        var order = req.Descendants(Myg + "singlePaymentOrder").Single();
        var xsiType = order.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type")!.Value;
        Assert.Equal("myg:TransferWithinBankPaymentOrderIo", xsiType);
        Assert.Equal("GE62TB0011223344556677", (string?)order.Element(Myg + "creditAccount")!.Element(Myg + "accountNumber"));
        Assert.Equal("GE48TB7044436080100017", (string?)order.Element(Myg + "debitAccount")!.Element(Myg + "accountNumber"));
        Assert.Equal("GEL", (string?)order.Element(Myg + "debitAccount")!.Element(Myg + "accountCurrencyCode"));
        Assert.Equal("49.50", (string?)order.Element(Myg + "amount")!.Element(Myg + "amount"));
        Assert.Equal("1", (string?)order.Element(Myg + "position"));
        Assert.Equal("გიორგი მამულაშვილი", (string?)order.Element(Myg + "beneficiaryName"));
        Assert.Equal(TbcSoapClient.RequestIdFromKey("smoke-1789650642").ToString(), (string?)order.Element(Myg + "singlePaymentRequestId"));
    }

    [Fact]
    public async Task Import_to_other_bank_uses_national_currency_type_and_tax_code()
    {
        var (adapter, fake) = Build();
        fake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(1))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));

        var req = Request(destIban: "GE59BG0000000123456789") with { DestinationTaxCode = "01001234567" };
        await adapter.SendPayoutAsync(req);

        var order = fake.Requests[0].Request.Descendants(Myg + "singlePaymentOrder").Single();
        Assert.Contains("TransferToOtherBankNationalCurrencyPaymentOrderIo",
            order.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type")!.Value);
        Assert.Equal("01001234567", (string?)order.Element(Myg + "beneficiaryTaxCode"));
    }

    [Fact]
    public void Nonce_is_included_when_configured()
    {
        var creds = TbcCredentials.Parse("""{ "username": "u", "password": "p", "nonce": "123456" }""");
        var env = TbcSoapClient.BuildEnvelope(creds, new XElement(Myg + "ChangePasswordRequestIo"), includeNonce: true);
        Assert.Equal("123456", (string?)env.Descendants(Wsse + "Nonce").Single());

        var noNonce = TbcSoapClient.BuildEnvelope(creds, new XElement(Myg + "GetPaymentOrderStatusRequestIo"), includeNonce: false);
        Assert.Empty(noNonce.Descendants(Wsse + "Nonce"));
    }

    // ── Asynchronous execution ───────────────────────────────────────

    [Fact]
    public async Task Import_still_processing_after_polls_returns_pending()
    {
        var (adapter, fake) = Build(pollAttempts: 2);
        fake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(77))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("WC"))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("CERT"));

        var result = await adapter.SendPayoutAsync(Request());

        Assert.True(result.Success);
        Assert.True(result.IsPending);
        Assert.Equal("77", result.TransferId);
        Assert.Equal(2, fake.Requests.Count(r => r.Operation == "GetPaymentOrderStatus"));
    }

    [Fact]
    public async Task Import_then_bank_fails_it_is_non_retryable_with_error_detail()
    {
        var (adapter, fake) = Build();
        fake.On("ImportSinglePaymentOrders", FakeTbcHandler.ImportResponse(78))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("FL", "Insufficient funds on account", "78"));

        var result = await adapter.SendPayoutAsync(Request());

        Assert.False(result.Success);
        Assert.False(result.IsRetryable);
        Assert.Equal("TBC_FL", result.ErrorCode);
        Assert.Contains("Insufficient funds", result.ErrorMessage);
        Assert.Equal("78", result.TransferId);
    }

    [Theory]
    [InlineData("F", BankTransferStatus.Completed)]
    [InlineData("FL", BankTransferStatus.Failed)]
    [InlineData("C", BankTransferStatus.Failed)]
    [InlineData("WC", BankTransferStatus.Pending)]
    [InlineData("VERIF", BankTransferStatus.Pending)]
    [InlineData("G", BankTransferStatus.Pending)]
    [InlineData("XYZ", BankTransferStatus.Unknown)]
    public void Status_codes_map_per_documentation(string code, BankTransferStatus expected) =>
        Assert.Equal(expected, TbcPayoutAdapter.MapStatus(code));

    // ── Idempotency / recovery ───────────────────────────────────────

    [Fact]
    public async Task Duplicate_request_recovers_original_payment_id()
    {
        var (adapter, fake) = Build();
        fake.Fault("ImportSinglePaymentOrders", "DUPLICATED_SINGLE_PAYMENT_REQUEST", "There is already imported single payment order with the same request ID")
            .On("GetSinglePaymentId", FakeTbcHandler.PaymentIdResponse(541202011))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));

        var result = await adapter.SendPayoutAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("541202011", result.TransferId);
        Assert.Equal(new[] { "ImportSinglePaymentOrders", "GetSinglePaymentId", "GetPaymentOrderStatus" },
            fake.Requests.Select(r => r.Operation).ToArray());
    }

    [Fact]
    public async Task Find_by_key_returns_null_when_bank_never_saw_it()
    {
        var (adapter, fake) = Build();
        fake.Fault("GetSinglePaymentId", "SINGLE_PAYMENT_REQUEST_NOT_FOUND", "not found");

        var lookup = await adapter.FindTransferByIdempotencyKeyAsync(Source, "never-sent");
        Assert.Null(lookup);
    }

    [Fact]
    public async Task Find_by_key_returns_status_when_found()
    {
        var (adapter, fake) = Build();
        fake.On("GetSinglePaymentId", FakeTbcHandler.PaymentIdResponse(99))
            .On("GetPaymentOrderStatus", FakeTbcHandler.StatusResponse("F"));

        var lookup = await adapter.FindTransferByIdempotencyKeyAsync(Source, "smoke-1789650642");
        Assert.NotNull(lookup);
        Assert.Equal("99", lookup!.TransferId);
        Assert.Equal(BankTransferStatus.Completed, lookup.Status);
    }

    [Fact]
    public void Request_id_is_deterministic_and_fits_xsd_long()
    {
        var a = TbcSoapClient.RequestIdFromKey("abc");
        var b = TbcSoapClient.RequestIdFromKey("abc");
        var c = TbcSoapClient.RequestIdFromKey("abd");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.InRange(a, 1, 999_999_999_999_999_999);
    }

    // ── Faults ───────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_fault_is_non_retryable()
    {
        var (adapter, fake) = Build();
        fake.Fault("ImportSinglePaymentOrders", "VALIDATION_ERROR", "ReceiverIban does not match to ReceiverPersonalNumber");
        var r = await adapter.SendPayoutAsync(Request());
        Assert.False(r.Success);
        Assert.False(r.IsRetryable);
        Assert.Equal("TBC_VALIDATION_ERROR", r.ErrorCode);
    }

    [Fact]
    public async Task Expired_credentials_fault_is_retryable_ops_problem()
    {
        var (adapter, fake) = Build();
        fake.Fault("ImportSinglePaymentOrders", "CREDENTIALS_MUST_BE_CHANGED", "password expired");
        var r = await adapter.SendPayoutAsync(Request());
        Assert.False(r.Success);
        Assert.True(r.IsRetryable);
        Assert.Equal("TBC_CREDENTIALS_MUST_BE_CHANGED", r.ErrorCode);
    }

    [Fact]
    public async Task Missing_credentials_are_retryable_not_reversing()
    {
        var (adapter, _) = Build();
        var r = await adapter.SendPayoutAsync(Request() with { Source = Source with { CredentialsJson = "{}" } });
        Assert.False(r.Success);
        Assert.True(r.IsRetryable);
        Assert.Equal("TBC_NOT_CONFIGURED", r.ErrorCode);
    }

    [Fact]
    public async Task Non_xml_response_throws_so_caller_does_lookup()
    {
        var (adapter, fake) = Build();
        fake.On("ImportSinglePaymentOrders", _ => (HttpStatusCode.BadGateway, "<html>gateway timeout</html>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendPayoutAsync(Request()));
    }

    // ── Reads ────────────────────────────────────────────────────────

    [Fact]
    public async Task Balance_comes_from_statement_closing_balance()
    {
        var (adapter, fake) = Build();
        fake.On("GetAccountStatement", FakeTbcHandler.StatementResponse(24950.50m));
        var b = await adapter.GetBalanceAsync(Source);
        Assert.Equal(24950.50m, b);

        var filter = fake.Requests[0].Request.Descendants(Myg + "filter").Single();
        Assert.Equal("GE48TB7044436080100017", (string?)filter.Element(Myg + "accountNumber"));
        Assert.Equal("GEL", (string?)filter.Element(Myg + "currency"));
    }

    [Fact]
    public async Task List_transfers_returns_debits_keyed_by_payment_id()
    {
        var (adapter, fake) = Build();
        fake.On("GetAccountMovements", FakeTbcHandler.MovementsResponse(("541202018", 49.50m, 0), ("inc1", 120m, 1)));
        var rows = await adapter.ListTransfersAsync(Source, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        var r = Assert.Single(rows);
        Assert.Equal("541202018", r.TransferId);
        Assert.Equal(49.50m, r.Amount);
        Assert.Equal(BankTransferStatus.Completed, r.Status);
    }
}
