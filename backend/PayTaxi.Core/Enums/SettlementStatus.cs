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
    /// retried on the next nightly run or on demand. Never partially taken.
    /// </summary>
    Failed,
}
