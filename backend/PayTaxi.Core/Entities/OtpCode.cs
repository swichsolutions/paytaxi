namespace PayTaxi.Core.Entities;

public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PhoneHash { get; set; } = default!;
    public string CodeHash { get; set; } = default!; // bcrypt of the 6-digit code
    public DateTime ExpiresAt { get; set; }
    public bool Used { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
