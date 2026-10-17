namespace PayTaxi.Core.Entities;

public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The phone's keyed lookup hash (same as Drivers.PhoneHash) — the phone itself is never stored here.</summary>
    public string PhoneHash { get; set; } = default!;
    /// <summary>The 6-digit code, keyed-hashed and bound to this row (Infrastructure LookupHash.SignInCode); the code itself is never stored.</summary>
    public string CodeHash { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
    public bool Used { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
