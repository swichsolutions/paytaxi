using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Email + password login for the admin console.
/// </summary>
[ApiController]
[Route("api/admin/auth")]
public class AdminAuthController : ControllerBase
{
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
        var user = await _db.AdminUsers
            .Include(a => a.Park)
            .FirstOrDefaultAsync(a => a.Email.ToLower() == normalisedEmail && a.IsActive, ct);

        // Generic error either way — no enumeration leak.
        if (user is null || !BCrypt.Net.BCrypt.Verify(body.Password, user.PasswordHash))
        {
            _log.LogWarning("Admin login failed for {Email}", normalisedEmail);
            return Unauthorized(new { error = "invalid_credentials" });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

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
