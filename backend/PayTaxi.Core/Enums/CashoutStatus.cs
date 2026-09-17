namespace PayTaxi.Core.Enums;

/// <summary>
/// Cashout lifecycle under the Yandex-first saga:
///
///   Processing --Yandex debit ok--> (bank ok) --> Completed
///        |                          |
///        |                          +--(bank retryable / down)--> Queued --worker--> Completed
///        |                                                          |
///        |                                                          +--(exhausted / hard fail)--> Failed (Yandex reversed)
///        |                                                                                     +--> ReviewRequired (reversal failed)
///        +--Yandex rejected--> Failed
///
/// <c>Queued</c> means the driver's Yandex balance is already debited and the money is
/// on its way as soon as the park's bank accepts the transfer — the driver sees
/// "processing, arrives in minutes", never a dead failure screen.
/// </summary>
public enum CashoutStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    ReviewRequired
}
