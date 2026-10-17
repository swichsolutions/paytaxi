using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Shared admin-controller helpers. Centralises the park-scope check so the
/// same rule applies everywhere a park-routed endpoint exists.
///
/// Scopes:
///   - <c>super_admin</c>: PayTaxi/Swich operators. See every park, own the fee/split config.
///   - <c>operator</c>:    the licensee's staff, and only while that licensee is in force: bound to none, or to one
///                         deactivated, the account is cut off altogether (AdminTokenCheck refuses its token, the sign-in
///                         refuses it). The parks of their OWN licensee only (<see cref="ParkFilter"/>, resolved from the
///                         database per request by AdminTokenCheck) — never a Swich-direct park or another licensee's; no
///                         Swich-only controls; the licence agreement's figures (licensee, statements, shared costs) of
///                         their own licensee only (<see cref="LicenseeScopeAsync"/>). Only one licensee may be in force
///                         still (409 second_active_licensee): the contract calendar and the incident metrics assume one.
///   - <c>park_admin</c>:  the taxi-park's own manager. Only their own park's data.
/// </summary>
public abstract class AdminControllerBase : ControllerBase
{
    /// <summary>True if the caller is a Swich super-admin (full control).</summary>
    protected bool IsSuperAdmin =>
        User.FindFirst("adminScope")?.Value == "super_admin";

    /// <summary>
    /// True for the licensee's staff (Levan's company): its own licensee's parks (<see cref="ParkFilter"/>),
    /// day-to-day operations are allowed, but Swich-only controls (fee/split config,
    /// settlement retry, park creation) are not.
    /// </summary>
    protected bool IsOperator =>
        User.FindFirst("adminScope")?.Value == "operator";

    /// <summary>
    /// Swich or the operator — the platform's side, as opposed to a park's manager: the role that may suspend a park, record a
    /// revocation, cancel a payout, resolve a reconciliation item and read who did what. It says nothing about WHICH parks:
    /// every park path checks <see cref="CanAccessPark"/> as well (an operator reaches its own licensee's parks only). An
    /// operator whose licensee is not in force never gets this far: AdminTokenCheck refuses its token.
    /// </summary>
    protected bool IsSwichOrOperator => IsSuperAdmin || IsOperator;

    /// <summary>
    /// "admin:{email}" for audit fields (InitiatedBy / ResolvedBy). Read from the token, never
    /// from the request body, so the trail can't be forged. The JWT handler maps the "email"
    /// claim to ClaimTypes.Email by default; both spellings are tried.
    /// </summary>
    protected string ActorLabel
    {
        get
        {
            var email = User.FindFirst(ClaimTypes.Email)?.Value
                     ?? User.FindFirst("email")?.Value
                     ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("sub")?.Value
                     ?? "unknown";
            return $"admin:{email}";
        }
    }

    /// <summary>
    /// A park manager whose token names no park. <see cref="ScopedParkId"/> is null for it, so a list endpoint that read null
    /// as "no filter" would hand it every park's data. Lists check this first and refuse (fail closed, as
    /// <see cref="CanAccessPark"/> does).
    /// </summary>
    protected bool ParkScopeMissing => !IsSwichOrOperator && ScopedParkId is null;

    /// <summary>A park manager's own park (null for Swich and the operator, and for a manager token without one).</summary>
    protected Guid? ScopedParkId
    {
        get
        {
            if (IsSwichOrOperator) return null;
            return Guid.TryParse(User.FindFirst("parkId")?.Value, out var p) ? p : null;
        }
    }

    /// <summary>
    /// The parks the caller reaches: null = every park (Swich only); otherwise exactly these — an operator: the parks of the
    /// licensee its account works for (AdminTokenCheck reads them with the account, and refuses the token outright when the
    /// account has no licensee or it is not in force — an empty set here is the fail-closed fallback); a park manager: its own
    /// park (none without one). Lists filter by it; <see cref="CanAccessPark"/> checks it.
    /// </summary>
    protected IReadOnlyCollection<Guid>? ParkFilter
    {
        get
        {
            if (IsSuperAdmin) return null;
            if (IsOperator)
                return HttpContext?.Items[Security.AdminTokenCheck.OperatorParksItem] as IReadOnlyCollection<Guid> ?? Array.Empty<Guid>();
            return ScopedParkId is { } own ? new[] { own } : Array.Empty<Guid>();
        }
    }

    /// <summary>
    /// Checks if the authenticated admin may access <paramref name="parkId"/>: Swich always; an operator a park of its own
    /// licensee in force; a park manager its own. Misformed claims and a missing scope fail closed.
    /// </summary>
    protected bool CanAccessPark(Guid parkId) => ParkFilter is not { } parks || parks.Contains(parkId);

    /// <summary>
    /// Whose licence-agreement figures the caller may see and change: every licensee for Swich; for an operator only the
    /// licensee its account works for (<c>AdminUser.LicenseeId</c>) while that licensee is in force — none when the account is
    /// bound to none or the licensee was deactivated (fail closed; AdminTokenCheck refuses such a token anyway); none for a
    /// park manager. Read from the account on every request rather than from the token, so binding an operator to another
    /// licensee, unbinding or deactivating it applies at once — a stale token never reaches another licensee's money.
    /// </summary>
    protected async Task<LicenseeScope> LicenseeScopeAsync(AppDbContext db, CancellationToken ct)
    {
        if (IsSuperAdmin) return LicenseeScope.All;
        if (!IsOperator) return LicenseeScope.None;
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var adminId)) return LicenseeScope.None;
        var licenseeId = await db.AdminUsers.AsNoTracking()
            .Where(a => a.Id == adminId && a.IsActive && a.Role == "operator" && a.Licensee != null && a.Licensee.IsActive)
            .Select(a => a.LicenseeId)
            .FirstOrDefaultAsync(ct);
        return licenseeId is { } id ? new LicenseeScope(false, id) : LicenseeScope.None;
    }
}

/// <summary>Which licensees the caller may see: <see cref="Every"/> one, or <see cref="Only"/> one (none when null).</summary>
public sealed record LicenseeScope(bool Every, Guid? Only)
{
    public static readonly LicenseeScope All = new(true, null);
    public static readonly LicenseeScope None = new(false, null);

    public bool Allows(Guid licenseeId) => Every || Only == licenseeId;
}
