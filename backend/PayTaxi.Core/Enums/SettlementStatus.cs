namespace PayTaxi.Core.Enums;

public enum SettlementStatus
{
    /// <summary>Created, cashouts attached, transfer not yet attempted.</summary>
    Pending,

    /// <summary>Transfer in flight.</summary>
    Processing,

    /// <summary>Transfer confirmed by the bank (or nothing to transfer).</summary>
    Completed,

    /// <summary>
    /// Transfer refused or failed (e.g. insufficient park balance). Cashouts stay attached;
    /// retried every night until it succeeds, or on demand. Never partially taken.
    /// </summary>
    Failed,

    /// <summary>
    /// Final: Swich recorded that the amount was paid OUTSIDE the platform — typically by the licensee
    /// when the park's account could not cover it (licence agreement §2.5, §4.1.2). Never retried, never
    /// collected from the park again; the payment is in the ledger (SettlementPaidExternally).
    /// </summary>
    SettledExternally,
}
