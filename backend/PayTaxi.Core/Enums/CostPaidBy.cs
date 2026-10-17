namespace PayTaxi.Core.Enums;

/// <summary>Which side paid a shared-cost invoice (licence agreement §2.4: each side pays the invoices addressed to it).</summary>
public enum CostPaidBy
{
    Swich,
    Licensee,
}

/// <summary>
/// The shared-cost categories of licence agreement §2.4. Anything else — notably the parties' own
/// labour and internal resources (development, sales, support time) — is not a shared cost.
/// </summary>
public enum CostCategory
{
    /// <summary>Bank services (e.g. TBC integration fee, Digipass, quarterly service).</summary>
    Bank,

    Sms,

    /// <summary>A payment provider, if one is ever used.</summary>
    PaymentProvider,

    /// <summary>Driver-support communication costs (phone, messaging).</summary>
    SupportComms,

    Hosting,
    Domain,
}
