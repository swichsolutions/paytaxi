using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Security;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Email + password login for the admin console.
/// </summary>
[ApiController]
[Route("api/admin/auth")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
public class AdminAuthController : ControllerBase
{
    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;

    /// <summary>A BCrypt hash of nothing anyone knows, verified against when the e-mail has no account (equal timing).</summary>
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly ILogger<AdminAuthController> _log;

    public AdminAuthController(
        AppDbContext db,
        IJwtTokenService jwt,
        ILogger<AdminAuthController> log)
    {
        _db = db;
        _jwt = jwt;
        _log = log;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Email) || string.IsNullOrWhiteSpace(body?.Password))
            return BadRequest(new { error = "email_and_password_required" });

        var normalisedEmail = body.Email.Trim().ToLowerInvariant();
        var user = await _db.AdminUsers.AsNoTracking()
            .Include(a => a.Park)
            .FirstOrDefaultAsync(a => a.Email.ToLower() == normalisedEmail && a.IsActive, ct);

        if (user is null)
        {
            // Same work as a wrong password (one BCrypt verification), so the answer time does not tell which e-mails
            // have accounts. What was typed is logged only as a keyed hash: a mistyped identifier is sometimes a password.
            BCrypt.Net.BCrypt.Verify(body.Password, DummyHash);
            _log.LogWarning("Admin login failed for an unknown identifier ({Hash})", LookupHash.LogTag("admin-login", normalisedEmail));
            return Unauthorized(new { error = "invalid_credentials" });
        }

        // Reserve one attempt BEFORE the password is checked, in one conditional UPDATE: refused while locked or while
        // MaxFailedAttempts tries are already counted. Incrementing after the check lost updates — twenty parallel
        // guesses each read "0 failures", each got BCrypt-verified, and the account never locked. Now at most five
        // passwords are ever checked before the lock; the counter is reset only by a correct one.
        var now = DateTime.UtcNow;
        var reserved = await _db.AdminUsers
            .Where(a => a.Id == user.Id && (a.LockedUntil == null || a.LockedUntil <= now) && a.FailedLoginAttempts < MaxFailedAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.FailedLoginAttempts, a => a.FailedLoginAttempts + 1), ct);
        if (reserved == 0)
        {
            var lockedUntil = await _db.AdminUsers.AsNoTracking().Where(a => a.Id == user.Id).Select(a => a.LockedUntil).FirstOrDefaultAsync(ct);
            _log.LogWarning("Admin login blocked: account {AdminId} locked until {Until:O}", user.Id, lockedUntil);
            return StatusCode(StatusCodes.Status423Locked, new
            {
                error = "account_locked",
                lockedUntil = lockedUntil is { } u && u > now ? u : now.AddMinutes(LockoutMinutes),
            });
        }

        if (!BCrypt.Net.BCrypt.Verify(body.Password, user.PasswordHash))
        {
            // The attempt is already counted. The one that reaches the limit locks the account (and starts a fresh count).
            var locked = await _db.AdminUsers
                .Where(a => a.Id == user.Id && a.FailedLoginAttempts >= MaxFailedAttempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.LockedUntil, DateTime.UtcNow.AddMinutes(LockoutMinutes))
                    .SetProperty(a => a.FailedLoginAttempts, 0), ct);
            if (locked > 0)
                _log.LogWarning("Admin account {AdminId} locked for {Minutes} minutes after repeated failures", user.Id, LockoutMinutes);
            _log.LogWarning("Admin login failed for account {AdminId}", user.Id);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        // An operator works only while its licensee is in force (AdminTokenCheck refuses its tokens by the same rule):
        // bound to none, or to a deactivated one, it gets no token. Said only after the password, so it tells nobody else
        // anything. The right password still clears the failure count — this is not a guess.
        if (user.Role == "operator" && !await Security.AdminTokenCheck.OperatorMayWorkAsync(_db, user.LicenseeId, ct))
        {
            await _db.AdminUsers.Where(a => a.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FailedLoginAttempts, 0), ct);
            _log.LogWarning("Admin login refused for operator account {AdminId}: its licensee is not in force (or none is bound)", user.Id);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "licensee_not_in_force",
                message = "This operator account works only while its licensee's agreement is in force.",
            });
        }

        await _db.AdminUsers.Where(a => a.Id == user.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LastLoginAt, DateTime.UtcNow)
                .SetProperty(a => a.FailedLoginAttempts, 0)
                .SetProperty(a => a.LockedUntil, (DateTime?)null), ct);

        var token = _jwt.IssueAdminToken(user.Id, user.Email, user.Role, user.ParkId);

        return Ok(new
        {
            token = token.Token,
            expiresAt = token.ExpiresAt,
            admin = new
            {
                id = user.Id,
                email = user.Email,
                name = user.Name,
                role = user.Role,
                parkId = user.ParkId,
                parkName = user.Park?.Name,
            },
        });
    }

    public record LoginDto(string Email, string Password);
}
