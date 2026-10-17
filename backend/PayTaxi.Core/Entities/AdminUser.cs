namespace PayTaxi.Core.Entities;

/// <summary>
/// A human who can log into the admin console.
///
/// <para>
/// Three flavors:
///   - <c>super_admin</c> — full access across all parks (PayTaxi/Swich operators).
///     <c>ParkId</c> is null.
///   - <c>operator</c> — the licensee's staff: the parks of their own licensee while it is in force, no Swich-only
///     controls, and the licence agreement's figures of their own licensee only (<c>LicenseeId</c>, required).
///   - <c>park_admin</c> — scoped to a single park (the taxi-park's manager).
///     <c>ParkId</c> must be set.
/// </para>
/// </summary>
public class AdminUser : BaseEntity
{
    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!; // BCrypt
    public string? Name { get; set; }
    public string Role { get; set; } = default!; // "super_admin" (Swich) | "operator" (Levan's company, all parks, read-only ops) | "park_admin"
    public Guid? ParkId { get; set; }            // null = super_admin; required for park_admin

    /// <summary>
    /// Operator accounts only: the licensee whose staff they are. Every licensee, statement and shared-cost
    /// endpoint shows and changes that licensee only, and the parks reached are this licensee's. The account works
    /// only while this licensee is in force: without one, or once it is deactivated, it can neither sign in nor use
    /// a token it holds (AdminTokenCheck; fail closed). Required for operators — the development seed binds its
    /// operator; production accounts are bound when created (docs/DEPLOY.md).
    /// </summary>
    public Guid? LicenseeId { get; set; }

    public DateTime? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Failed-login counter; reset on success, used to trigger lockout.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>When set in the future, login attempts are rejected until this passes.</summary>
    public DateTime? LockedUntil { get; set; }

    public Park? Park { get; set; }
    public Licensee? Licensee { get; set; }
}
