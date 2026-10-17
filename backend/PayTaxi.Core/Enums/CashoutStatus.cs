namespace PayTaxi.Core.Enums;

/// <summary>
/// Cashout lifecycle under the Yandex-first saga:
///
///   Processing --Yandex debit ok--> (bank ok) --> Completed
///        |                          |
///        |                          +--(bank down / no funds / no account)--> Queued --worker--> Completed
///        |                                                          |
///        |                                                          +--(bank rejects for good / admin cancels)--> Failed (Yandex reversed)
///        |                                                          +--(outcome unknown too long, or reversal failed)--> ReviewRequired
///        +--Yandex rejected--> Failed
///
/// <c>Queued</c> means the driver's Yandex balance is already debited and the money is
/// on its way as soon as the park's bank accepts the transfer — the driver sees
/// "on its way, goes out automatically", never a dead failure screen. A queued payout waits
/// as long as its cause lasts (agreement §5.5, Annex 3 §5); only a human ends the wait early.
/// </summary>
public enum CashoutStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    ReviewRequired
}
