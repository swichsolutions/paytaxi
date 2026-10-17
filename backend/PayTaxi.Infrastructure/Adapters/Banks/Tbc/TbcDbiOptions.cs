namespace PayTaxi.Infrastructure.Adapters.Banks.Tbc;

/// <summary>
/// Global settings for TBC's Integration Service (DBI, SOAP). Per-park secrets live on
/// <c>ParkBankAccount.CredentialsEncrypted</c> (see <see cref="TbcCredentials"/>); this
/// holds only environment-wide knobs. Bound from "BankPayout:Tbc".
/// </summary>
public class TbcDbiOptions
{
    public const string SectionName = "BankPayout:Tbc";

    /// <summary>Standard+ (client certificate) endpoints — https://developers.tbcbank.ge/docs/attach-digital-certificate</summary>
    public string ProductionEndpoint { get; set; } = "https://secdbi.tbconline.ge/dbi/dbiService";
    public string TestEndpoint { get; set; } = "https://secdbitst.tbconline.ge/dbi/dbiService";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// The only directory a payout account's <c>certificatePath</c> may point into (e.g. a mounted secrets volume). Unset —
    /// the default — means paths are refused and certificates come as <c>certificatePfxBase64</c> in the credentials:
    /// park staff write that JSON, and the API must not open whatever file of its host they name.
    /// </summary>
    public string? CertificateDirectory { get; set; }

    /// <summary>
    /// After ImportSinglePaymentOrders, poll GetPaymentOrderStatus this many times (with
    /// <see cref="StatusPollDelayMs"/> between) before returning a Pending result to the saga.
    /// </summary>
    public int StatusPollAttempts { get; set; } = 3;
    public int StatusPollDelayMs { get; set; } = 1500;

    /// <summary>GetAccountMovements page size (bank maximum is 700).</summary>
    public int MovementsPageSize { get; set; } = 500;

    /// <summary>
    /// How TBC says "the park's account cannot cover this payment". Such an answer becomes our
    /// retryable INSUFFICIENT_FUNDS: the payout waits for a top-up instead of being given back
    /// (Annex 3 §5). Each entry is matched exactly (case-insensitive) against the SOAP fault code or
    /// the payment-order status code, and — as a fallback — as a case-insensitive phrase inside the
    /// bank's error text (errorDetailEN / faultstring). So list specific codes and phrases only; a
    /// generic status such as "FL" would turn every failure into a wait.
    ///
    /// UNCONFIRMED: TBC's documentation does not name its insufficient-funds code. The defaults are
    /// educated guesses and MUST be confirmed in the sandbox (secdbitst) with an order larger than
    /// the test account's balance — see docs/TBC-BRANCH-VISIT.md, question 22.
    /// Empty = use <see cref="DefaultInsufficientFundsCodes"/>. (Deliberately empty by default: the
    /// configuration binder appends to a non-empty array instead of replacing it.)
    /// </summary>
    public string[] InsufficientFundsCodes { get; set; } = Array.Empty<string>();

    public static readonly string[] DefaultInsufficientFundsCodes =
    {
        "INSUFFICIENT_FUNDS", "INSUFFICIENT_BALANCE", "NOT_ENOUGH_FUNDS", "NOT_ENOUGH_MONEY",
        "Insufficient funds", "Not enough funds", "Insufficient balance",
        // errorDetailGE is used when the bank sends no English text.
        "არასაკმარისი თანხა", "არასაკმარისი ნაშთი", "თანხა არ არის საკმარისი",
    };

    /// <summary>
    /// Whether credentials for TBC's test environment may be used at all. Never read from configuration: Program.cs sets it
    /// from <see cref="BankRailPolicy.TestEnvironmentAllowed"/> after binding (Development, AllowMockRails, or the staging-only
    /// BankPayout:AllowTestEnvironment). Off, every call with test credentials is refused before anything is sent
    /// (TBC_TEST_ENVIRONMENT_NOT_ALLOWED) — a payout or settlement accepted by the sandbox would otherwise be recorded as
    /// paid while no money moved. Defaults to false so a client built without the policy is the strict one.
    /// </summary>
    public bool AllowTestEnvironment { get; set; }

    /// <summary>
    /// Test environment only: TBC's test host is signed by its own root (TBCRootCer.cer).
    /// When true, server-certificate validation is skipped for the TEST endpoint. Never
    /// applies to production.
    /// </summary>
    public bool TrustTestServerCertificate { get; set; } = false;
}
