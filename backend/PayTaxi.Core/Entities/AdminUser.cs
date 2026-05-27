namespace PayTaxi.Core.Entities;

/// <summary>
/// A human who can log into the admin console.
///
/// <para>
/// Two flavors:
///   - <c>super_admin</c> — full access across all parks (PayTaxi/Swich operators).
///     <c>ParkId</c> is null.
///   - <c>park_admin</c> — scoped to a single park (the taxi-park's manager).
///     <c>ParkId</c> must be set.
/// </para>
/// </summary>
public class AdminUser : BaseEntity
{
    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!; // BCrypt
    public string? Name { get; set; }
    public string Role { get; set; } = default!; // "super_admin" | "park_admin"
    public Guid? ParkId { get; set; }            // null = super_admin; required for park_admin
    public DateTime? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;

    public Park? Park { get; set; }
}
