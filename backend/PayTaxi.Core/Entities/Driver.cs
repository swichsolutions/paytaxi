using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class Driver : BaseEntity
{
    public Guid ParkId { get; set; }
    public string PhoneEncrypted { get; set; } = default!;
    public string PhoneHash { get; set; } = default!; // keyed lookup hash (HMAC, Infrastructure LookupHash) — never a plain SHA-256
    public string? Name { get; set; }
    public string? YandexDriverProfileId { get; set; }
    public DriverStatus Status { get; set; } = DriverStatus.Active;
    public bool ConsentGiven { get; set; }
    public DateTime? ConsentTimestamp { get; set; }

    /// <summary>
    /// The language the driver last used the app in ("ka", "en" or "ru"; null until the app has said), taken from the
    /// app's requests at sign-in, at every token refresh and when the driver changes a payout account. Messages the
    /// server words itself — the SMS about a payout-account change, also one made from the console — go out in it;
    /// Georgian when it is not known.
    /// </summary>
    public string? PreferredLanguage { get; set; }

    public Park Park { get; set; } = default!;
    public ICollection<BankCard> BankCards { get; set; } = new List<BankCard>();
    public ICollection<Cashout> Cashouts { get; set; } = new List<Cashout>();
    public YandexBalanceCache? BalanceCache { get; set; }
}
