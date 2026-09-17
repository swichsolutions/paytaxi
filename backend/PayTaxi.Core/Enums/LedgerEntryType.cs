namespace PayTaxi.Core.Enums;

public enum LedgerEntryType
{
    CashoutReserved,
    BankTransferSent,
    YandexDeducted,
    CashoutCompleted,
    CashoutReversed,
    FeeCollected,

    /// <summary>Compensating +amount posted to Yandex after an abandoned payout.</summary>
    YandexReversed,

    /// <summary>Nightly park → Swich fee-share transfer confirmed by the bank.</summary>
    SettlementSent,

    /// <summary>Nightly park → Swich transfer refused/failed; will be retried.</summary>
    SettlementFailed,
}
