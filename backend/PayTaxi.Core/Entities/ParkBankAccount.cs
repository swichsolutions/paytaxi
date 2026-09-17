namespace PayTaxi.Core.Entities;

/// <summary>
/// One bank account the park pays drivers from, with the API credentials that
/// authorise PayTaxi to initiate transfers from it. A park holds one row per bank
/// (TBC at launch, BoG in Phase 2). Payouts are routed to the account whose
/// <see cref="BankCode"/> matches the driver's IBAN so that transfers stay
/// intra-bank (zero fee, instant).
/// </summary>
public class ParkBankAccount : BaseEntity
{
    public Guid ParkId { get; set; }

    /// <summary>Two-letter Georgian bank code as it appears in IBANs: "TB" (TBC), "BG" (BoG), …</summary>
    public string BankCode { get; set; } = default!;

    /// <summary>Which <c>IBankPayoutAdapter</c> handles this account: "tbc", "bog", "mock".</summary>
    public string Provider { get; set; } = default!;

    /// <summary>The park's account IBAN — the transfer source.</summary>
    public string Iban { get; set; } = default!;

    /// <summary>Account holder name as registered at the bank (goes on transfer orders).</summary>
    public string? HolderName { get; set; }

    /// <summary>
    /// Provider-specific credentials JSON (client id/secret, certificate reference…).
    /// Plaintext placeholder until Phase 8 brings AES + Key Vault. Never returned by the API.
    /// </summary>
    public string CredentialsEncrypted { get; set; } = "{}";

    /// <summary>Only active accounts are eligible for routing.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The account Yandex settles into and the fallback for reporting. One per park.</summary>
    public bool IsPrimary { get; set; }

    public string? Label { get; set; }

    public Park Park { get; set; } = default!;
}
