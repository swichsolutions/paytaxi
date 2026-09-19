namespace PayTaxi.Core.Interfaces;

/// <summary>
/// A cashout request that was refused before anything moved (validation, limits, balance,
/// routing). Carries a stable machine code plus the values the message interpolates, so
/// the API can return <c>{ code, params }</c> and the driver app can render a translated
/// message instead of echoing English text with internal ids.
/// </summary>
public sealed class CashoutRejectedException : InvalidOperationException
{
    public string Code { get; }
    public IReadOnlyDictionary<string, object?> Params { get; }

    public CashoutRejectedException(string code, string message, IReadOnlyDictionary<string, object?>? parameters = null)
        : base(message)
    {
        Code = code;
        Params = parameters ?? new Dictionary<string, object?>();
    }

    public static CashoutRejectedException Of(string code, string message, params (string Key, object? Value)[] parameters) =>
        new(code, message, parameters.ToDictionary(p => p.Key, p => p.Value));
}

/// <summary>Stable rejection codes. Keep in sync with the driver app's i18n keys (errCashout_*).</summary>
public static class CashoutRejectionCodes
{
    public const string ParkInactive = "park_inactive";
    public const string DriverInactive = "driver_inactive";
    public const string DriverNotLinked = "driver_not_linked";
    public const string DestinationRemoved = "destination_removed";
    public const string DestinationNoIban = "destination_no_iban";
    public const string BelowMinimum = "below_minimum";
    public const string AboveMaximum = "above_maximum";
    public const string AmountNotAboveFee = "amount_not_above_fee";
    public const string DailyLimitReached = "daily_limit_reached";
    public const string BankNotSupported = "bank_not_supported";
    public const string InsufficientBalance = "insufficient_balance";
    public const string BalanceUnavailable = "balance_unavailable";
    public const string CashoutInFlight = "cashout_in_flight";
    public const string YandexReadOnly = "yandex_read_only";
    public const string IdempotencyKeyConflict = "idempotency_key_conflict";
}
