namespace PayTaxi.Core.Entities;

public class YandexBalanceCache
{
    public Guid DriverId { get; set; }
    public Guid ParkId { get; set; }
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "GEL";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Driver Driver { get; set; } = default!;
}
