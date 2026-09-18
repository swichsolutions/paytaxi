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
    /// After ImportSinglePaymentOrders, poll GetPaymentOrderStatus this many times (with
    /// <see cref="StatusPollDelayMs"/> between) before returning a Pending result to the saga.
    /// </summary>
    public int StatusPollAttempts { get; set; } = 3;
    public int StatusPollDelayMs { get; set; } = 1500;

    /// <summary>GetAccountMovements page size (bank maximum is 700).</summary>
    public int MovementsPageSize { get; set; } = 500;

    /// <summary>
    /// Test environment only: TBC's test host is signed by its own root (TBCRootCer.cer).
    /// When true, server-certificate validation is skipped for the TEST endpoint. Never
    /// applies to production.
    /// </summary>
    public bool TrustTestServerCertificate { get; set; } = false;
}
