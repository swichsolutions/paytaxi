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

    /// <summary>Driver's account IBAN — the transfer destination.</summary>
    public string Iban { get; set; } = default!;

    /// <summary>Two-letter bank code parsed from the IBAN ("TB", "BG", …). Drives payout routing.</summary>
    public string BankCode { get; set; } = default!;

    /// <summary>Account holder name, as the bank expects on the transfer order.</summary>
    public string? HolderName { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    public Driver Driver { get; set; } = default!;
}
