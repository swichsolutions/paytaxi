namespace PayTaxi.Core.Entities;

/// <summary>
/// A trusted device for a driver: the long-lived refresh token issued after a successful
/// OTP login (PAYTAXI-CONTEXT.md §7 — "OTP on login/new device only"). The browser holds
/// the raw token; we store only its SHA-256. Access JWTs are short-lived and re-minted from
/// this session, so the driver is only asked for an SMS code on a new device, after the
/// session expires (90 days), after logout, or after a manager suspends them.
///
/// Refresh tokens rotate: every refresh revokes this row and creates a successor. Presenting
/// a revoked token is treated as theft and revokes every session of the driver.
/// </summary>
public class DriverSession : BaseEntity
{
    public Guid DriverId { get; set; }
    public Guid ParkId { get; set; }

    /// <summary>SHA-256 (lower hex) of the raw refresh token. Never store the raw token.</summary>
    public string TokenHash { get; set; } = default!;

    /// <summary>Short, human-readable device hint from the User-Agent ("Android · Chrome"), for the future "my devices" list.</summary>
    public string? DeviceLabel { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>The session created by rotating this one.</summary>
    public Guid? ReplacedBySessionId { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;

    public Driver Driver { get; set; } = default!;
}
