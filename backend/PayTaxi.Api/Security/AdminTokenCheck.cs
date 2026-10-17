using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Security;

/// <summary>
/// Re-checks an admin token against its account on every request (JwtBearer OnTokenValidated). Scope and park used to
/// come from the token alone, so a super_admin or manager who was deactivated, demoted or moved to another park kept the
/// old powers until the token expired (60 minutes; a day if Jwt:ExpiryMinutes is unset). Now the token is refused once
/// the account is inactive or its role or park no longer match the token's claims. The account row is cached briefly
/// (Auth:AdminRecheckSeconds, default 30) so the console's request bursts cost one lookup, not one each.
/// Driver tokens are untouched here (DriverTokenCheck).
///
/// An operator account works only while the licensee it works for (<c>AdminUser.LicenseeId</c>) is in force: with none
/// bound, or once that licensee is deactivated, the token is refused outright — and AdminAuthController refuses its
/// sign-in (403 licensee_not_in_force) by the same rule (<see cref="OperatorMayWorkAsync"/>). Denying only its parks was
/// not enough: the platform-wide part of the console asks only "Swich or operator?" — reporting an incident of any level
/// (a platform-wide P1 is a danger banner on every park's console), its notes and metrics, the licensee's statements and
/// shared costs.
/// The accounts themselves are not deactivated: reactivating the licensee restores them. The licensee's PATCH drops its
/// operators' cached entries (<see cref="Forget"/>), so the cut-off does not wait for the cache.
///
/// For an operator in force it also resolves which parks the account reaches: those of its licensee — read from the
/// database with the account, in the same cached entry, and handed to the controllers through
/// <see cref="OperatorParksItem"/> (<c>AdminControllerBase.CanAccessPark</c>). Every operator used to reach every park —
/// Swich-direct parks and another licensee's included — for as long as its token lived.
/// </summary>
public static class AdminTokenCheck
{
    /// <summary>HttpContext.Items key: the park ids an operator reaches this request (an empty set when none).</summary>
    public const string OperatorParksItem = "paytaxi.operator-parks";

    private sealed record AccountState(bool IsActive, string Role, Guid? ParkId, bool LicenseeInForce, IReadOnlyCollection<Guid> OperatorParks);

    private static string Key(Guid adminId) => $"admin-account-state:{adminId:N}";

    public static async Task ValidateAsync(TokenValidatedContext ctx, TimeSpan cacheFor)
    {
        var principal = ctx.Principal;
        if (principal is null || !principal.IsInRole("admin")) return;

        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var adminId)) { ctx.Fail("admin token without an account id"); return; }

        var services = ctx.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var abort = ctx.HttpContext.RequestAborted;
        var state = await cache.GetOrCreateAsync(Key(adminId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = cacheFor > TimeSpan.Zero ? cacheFor : TimeSpan.FromMilliseconds(1);
            var db = services.GetRequiredService<AppDbContext>();
            var account = await db.AdminUsers.AsNoTracking()
                .Where(a => a.Id == adminId)
                .Select(a => new { a.IsActive, a.Role, a.ParkId, a.LicenseeId })
                .FirstOrDefaultAsync(abort);
            if (account is null) return null;
            // An operator with no licensee, or whose licensee is no longer in force, is refused below (fail closed).
            var inForce = false;
            IReadOnlyCollection<Guid> parks = Array.Empty<Guid>();
            if (account.Role == "operator" && account.LicenseeId is { } licenseeId)
            {
                inForce = await db.Licensees.AsNoTracking().AnyAsync(l => l.Id == licenseeId && l.IsActive, abort);
                if (inForce)
                    parks = (await db.Parks.AsNoTracking()
                            .Where(p => p.LicenseeId == licenseeId)
                            .Select(p => p.Id)
                            .ToListAsync(abort))
                        .ToHashSet();
            }
            return new AccountState(account.IsActive, account.Role, account.ParkId, inForce, parks);
        });

        var scope = principal.FindFirst("adminScope")?.Value;
        Guid? parkId = Guid.TryParse(principal.FindFirst("parkId")?.Value, out var p) ? p : null;
        if (state is null || !state.IsActive || state.Role != scope || state.ParkId != parkId)
        {
            ctx.Fail("admin account is inactive or its role or park changed since the token was issued");
            return;
        }
        if (state.Role == "operator")
        {
            if (!state.LicenseeInForce)
            {
                ctx.Fail("operator account whose licensee is not in force (or bound to none)");
                return;
            }
            ctx.HttpContext.Items[OperatorParksItem] = state.OperatorParks;
        }
    }

    /// <summary>
    /// Whether an operator account may work at all: bound to a licensee that is in force. The sign-in asks it after the
    /// password (so it tells nothing to who does not know it); the token check above applies the same rule to every request.
    /// </summary>
    public static Task<bool> OperatorMayWorkAsync(AppDbContext db, Guid? licenseeId, CancellationToken ct) =>
        licenseeId is { } id
            ? db.Licensees.AsNoTracking().AnyAsync(l => l.Id == id && l.IsActive, ct)
            : Task.FromResult(false);

    /// <summary>Drop the cached state of these admin accounts (their licensee changed): the next request re-reads them.</summary>
    public static void Forget(IMemoryCache cache, IEnumerable<Guid> adminIds)
    {
        foreach (var id in adminIds) cache.Remove(Key(id));
    }
}
