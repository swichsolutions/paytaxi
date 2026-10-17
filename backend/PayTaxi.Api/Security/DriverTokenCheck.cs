using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Security;

/// <summary>
/// Re-checks a driver's access token on every request (JwtBearer OnTokenValidated), as AdminTokenCheck does for admins.
/// A driver token lives an hour and used to be honoured for all of it: a suspended driver's phone — or a lost one the park
/// had suspended — could still add a payout account until the token ran out, and only a refresh noticed. Now the token is
/// refused once the driver is not Active, once the phone it was issued for is no longer the driver's (a phone change), or
/// once the session it was minted from (the token's sessionId) has ended: signed out, revoked by a suspension or a phone
/// change (admin_revoked), or cut off as a copied token. A rotated session counts as alive while the session it was
/// rotated into is — the app keeps using the last access token for a minute after a refresh in another tab.
///
/// Both lookups are cached for Auth:DriverRecheckSeconds (default 30) — the app's request bursts cost one each — and the
/// driver's entries, its sessions' included, are dropped when the console changes the driver and when a device signs out
/// (<see cref="ForgetAsync"/>), so either applies at once. Dropping only the driver's own entry left a session's cached
/// "alive" in place: a signed-out device's token kept working until the cache ran out, and one from before a suspension the
/// park lifted again within the cache time passed on it. (The cache is per instance: on several instances another one
/// still answers from its own for up to the cache time.)
/// </summary>
public static class DriverTokenCheck
{
    private sealed record DriverState(DriverStatus Status, string PhoneHash);
    private sealed record SessionRow(Guid DriverId, DateTime ExpiresAt, DateTime? RevokedAt, string? RevokedReason, Guid? ReplacedBySessionId);

    /// <summary>How many rotations a token's session may be behind the live one (a few racing tabs at most).</summary>
    private const int MaxRotations = 5;

    private static string DriverKey(Guid driverId) => $"driver-token-state:{driverId:N}";
    private static string SessionKey(Guid sessionId) => $"driver-session-state:{sessionId:N}";

    public static async Task ValidateAsync(TokenValidatedContext ctx, TimeSpan cacheFor)
    {
        var principal = ctx.Principal;
        if (principal is null || !principal.IsInRole("driver")) return;

        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var driverId)) { ctx.Fail("driver token without a driver id"); return; }

        var services = ctx.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var ttl = cacheFor > TimeSpan.Zero ? cacheFor : TimeSpan.FromMilliseconds(1);
        var abort = ctx.HttpContext.RequestAborted;

        var state = await cache.GetOrCreateAsync(DriverKey(driverId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            var db = services.GetRequiredService<AppDbContext>();
            return await db.Drivers.AsNoTracking()
                .Where(d => d.Id == driverId)
                .Select(d => new DriverState(d.Status, d.PhoneHash))
                .FirstOrDefaultAsync(abort);
        });
        if (state is null || state.Status != DriverStatus.Active)
        {
            ctx.Fail("driver is not active");
            return;
        }
        if (!string.Equals(principal.FindFirst("phoneHash")?.Value, state.PhoneHash, StringComparison.Ordinal))
        {
            ctx.Fail("the driver's phone changed since the token was issued");
            return;
        }

        // Tokens minted outside a session (only the tests do) carry none: the driver's state above is all there is to check.
        if (principal.FindFirst(JwtTokenService.DriverSessionClaim)?.Value is not { } sidText) return;
        if (!Guid.TryParse(sidText, out var sessionId)) { ctx.Fail("malformed session claim"); return; }

        var alive = await cache.GetOrCreateAsync(SessionKey(sessionId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            var db = services.GetRequiredService<AppDbContext>();
            var id = sessionId;
            for (var hop = 0; hop <= MaxRotations; hop++)
            {
                var s = await db.DriverSessions.AsNoTracking()
                    .Where(x => x.Id == id)
                    .Select(x => new SessionRow(x.DriverId, x.ExpiresAt, x.RevokedAt, x.RevokedReason, x.ReplacedBySessionId))
                    .FirstOrDefaultAsync(abort);
                if (s is null || s.DriverId != driverId) return false;
                if (s.RevokedAt is null) return s.ExpiresAt > DateTime.UtcNow;
                if (s.RevokedReason != "rotated" || s.ReplacedBySessionId is not { } next) return false;
                id = next;
            }
            return false; // further behind than any app gets: it refreshes
        });
        if (!alive) ctx.Fail("the session this token was issued from has ended");
    }

    /// <summary>
    /// Drop the cached state of a driver just changed (status, phone) or signed out: the driver's and every one of its
    /// sessions' — a token names any session of a rotation chain — so the next request re-reads them.
    /// </summary>
    public static async Task ForgetAsync(IMemoryCache cache, AppDbContext db, Guid driverId, CancellationToken ct = default)
    {
        cache.Remove(DriverKey(driverId));
        foreach (var id in await db.DriverSessions.AsNoTracking().Where(s => s.DriverId == driverId).Select(s => s.Id).ToListAsync(ct))
            cache.Remove(SessionKey(id));
    }
}
