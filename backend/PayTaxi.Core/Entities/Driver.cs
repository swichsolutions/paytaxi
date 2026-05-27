using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class Driver : BaseEntity
{
    public Guid ParkId { get; set; }
    public string PhoneEncrypted { get; set; } = default!;
    public string PhoneHash { get; set; } = default!; // SHA-256 for lookup
    public string? Name { get; set; }
    public string? YandexDriverProfileId { get; set; }
    public DriverStatus Status { get; set; } = DriverStatus.Active;
    public bool ConsentGiven { get; set; }
    public DateTime? ConsentTimestamp { get; set; }

    public Park Park { get; set; } = default!;
    public ICollection<BankCard> BankCards { get; set; } = new List<BankCard>();
    public ICollection<Cashout> Cashouts { get; set; } = new List<Cashout>();
    public YandexBalanceCache? BalanceCache { get; set; }
}
