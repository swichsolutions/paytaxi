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
    YandexReversed
}
