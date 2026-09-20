namespace PayTaxi.Core.Entities;

/// <summary>
/// A driver's payout destination. Historically a tokenised card; under the TBC
/// Business Integration rail it is a bank account identified by IBAN. The entity
/// keeps its original name to avoid a disruptive rename across the codebase.
/// </summary>
public class BankCard : BaseEntity
{
    public Guid DriverId { get; set; }

    /// <summary>Display string, e.g. "**** 4521" (derived from the IBAN's last 4 digits).</summary>
    public string MaskedPan { get; set; } = default!;

    /// <summary>Legacy tokenised-card reference. Empty for IBAN destinations.</summary>
    public string TokenReferenceEncrypted { get; set; } = "";

    /// <summary>Human-readable bank label: "TBC" | "BOG" | "LIBERTY" | … Derived from <see cref="BankCode"/>.</summary>
    public string BankType { get; set; } = default!;

    /// <summary>Driver's account IBAN — the transfer destination. Encrypted at rest.</summary>
    public string Iban { get; set; } = default!;

    /// <summary>SHA-256 of the IBAN — the equality-lookup companion of the encrypted column (duplicate detection).</summary>
    public string IbanHash { get; set; } = "";

    /// <summary>Two-letter bank code parsed from the IBAN ("TB", "BG", …). Drives payout routing.</summary>
    public string BankCode { get; set; } = default!;

    /// <summary>Account holder name, as the bank expects on the transfer order.</summary>
    public string? HolderName { get; set; }

    /// <summary>
    /// The holder name does not look like the driver's registered name (script-blind compare),
    /// i.e. the park knowingly pays this driver into someone else's account. Only a park admin
    /// can create such a destination, and only with a reason; drivers are refused.
    /// </summary>
    public bool IsThirdPartyAccount { get; set; }

    /// <summary>Why the park agreed to pay a third party (admin-entered; e.g. "driver has no account, wife's account, statement on file").</summary>
    public string? ThirdPartyReason { get; set; }

    /// <summary>Who created the destination: "driver:{id}" or "admin:{email}". Audit convenience beside the API audit log.</summary>
    public string? AddedBy { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    public Driver Driver { get; set; } = default!;
}
