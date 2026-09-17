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
    /// <summary>True if the caller is a Swich super-admin (full control).</summary>
    protected bool IsSuperAdmin =>
        User.FindFirst("adminScope")?.Value == "super_admin";

    /// <summary>
    /// True for the exclusive operator's staff (Levan's company): every park is visible,
    /// day-to-day operations are allowed, but Swich-only controls (fee/split config,
    /// settlement retry, park creation) are not.
    /// </summary>
    protected bool IsOperator =>
        User.FindFirst("adminScope")?.Value == "operator";

    /// <summary>True when the caller sees every park (Swich or operator).</summary>
    protected bool SeesAllParks => IsSuperAdmin || IsOperator;

    /// <summary>The park id the caller is scoped to, or null when they see all parks.</summary>
    protected Guid? ScopedParkId
    {
        get
        {
            if (SeesAllParks) return null;
            return Guid.TryParse(User.FindFirst("parkId")?.Value, out var p) ? p : null;
        }
    }

    /// <summary>
    /// Checks if the authenticated admin may access <paramref name="parkId"/>.
    /// Super-admins always can; park-admins only their own; misformed claims fail closed.
    /// </summary>
    protected bool CanAccessPark(Guid parkId)
    {
        if (SeesAllParks) return true;
        return ScopedParkId == parkId;
    }
}
