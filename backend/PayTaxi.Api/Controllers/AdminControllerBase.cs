using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Shared admin-controller helpers. Centralises the park-scope check so the
/// same rule applies everywhere a park-routed endpoint exists.
///
/// Scopes:
///   - <c>super_admin</c>: PayTaxi/Swich operators. See every park.
///   - <c>park_admin</c>:  the taxi-park's own manager. Only their own park's data.
/// </summary>
public abstract class AdminControllerBase : ControllerBase
{
    /// <summary>True if the caller is a super-admin.</summary>
    protected bool IsSuperAdmin =>
        User.FindFirst("adminScope")?.Value == "super_admin";

    /// <summary>The park id the caller is scoped to, or null for super-admins (= all parks).</summary>
    protected Guid? ScopedParkId
    {
        get
        {
            if (IsSuperAdmin) return null;
            return Guid.TryParse(User.FindFirst("parkId")?.Value, out var p) ? p : null;
        }
    }

    /// <summary>
    /// Checks if the authenticated admin may access <paramref name="parkId"/>.
    /// Super-admins always can; park-admins only their own; misformed claims fail closed.
    /// </summary>
    protected bool CanAccessPark(Guid parkId)
    {
        if (IsSuperAdmin) return true;
        return ScopedParkId == parkId;
    }
}
