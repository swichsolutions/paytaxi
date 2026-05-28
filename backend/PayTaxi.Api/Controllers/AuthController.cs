using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Driver phone+OTP authentication. Two endpoints:
///   POST /api/driver/auth/request-otp  → generate + store a one-time code
///   POST /api/driver/auth/verify-otp   → validate the code, return a JWT
///
/// Dev behavior: the generated code is returned in the request-otp response
/// (and logged) so testers can use it without an SMS gateway. Production
/// must remove the `code` field from the response and wire ISmsSender.
///
/// Code is stored as a SHA-256 hash. Good enough given the 5-minute expiry
/// and small (6-digit) search space combined with the per-code attempt cap.
/// </summary>
[ApiController]
[Route("api/driver/auth")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private const int CodeExpiryMinutes = 5;
    private const int MaxAttemptsPerCode = 5;

    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AuthController> _log;

    public AuthController(
        AppDbContext db,
        IJwtTokenService jwt,
        IWebHostEnvironment env,
        ILogger<AuthController> log)
    {
        _db = db;
        _jwt = jwt;
        _env = env;
        _log = log;
    }

    [HttpPost("request-otp")]
    public async Task<IActionResult> RequestOtp([FromBody] RequestOtpDto body, CancellationToken ct)
    {
        var phone = NormalizePhone(body?.Phone);
        if (phone is null) return BadRequest(new { error = "phone_required" });

        var phoneHash = HashPhone(phone);
        var driver = await _db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.PhoneHash == phoneHash, ct);

        // We DON'T reveal whether the phone exists — same response either way.
        // The OTP just goes nowhere if there's no driver behind the phone.
        var code = GenerateCode();
        if (driver is not null)
        {
            _db.OtpCodes.Add(new OtpCode
            {
                PhoneHash = phoneHash,
                CodeHash = HashCode(code),
                ExpiresAt = DateTime.UtcNow.AddMinutes(CodeExpiryMinutes),
            });
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Dev OTP for {Phone}: {Code} (expires {Expires:HH:mm:ss})",
                phone, code, DateTime.UtcNow.AddMinutes(CodeExpiryMinutes));
        }
        else
        {
            _log.LogWarning("OTP requested for unknown phone (hash {Hash})", phoneHash[..8]);
        }

        // Dev convenience: return the code in the response.
        // Production must drop this field and rely on the SMS gateway.
        return Ok(new
        {
            sent = true,
            expiresInSeconds = CodeExpiryMinutes * 60,
            devCode = _env.IsDevelopment() && driver is not null ? code : null,
        });
    }

    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto body, CancellationToken ct)
    {
        var phone = NormalizePhone(body?.Phone);
        if (phone is null) return BadRequest(new { error = "phone_required" });
        if (string.IsNullOrWhiteSpace(body?.Code))
            return BadRequest(new { error = "code_required" });

        var phoneHash = HashPhone(phone);
        var codeHash = HashCode(body.Code);

        // Latest unused, unexpired code matching the phone.
        var otp = await _db.OtpCodes
            .Where(o => o.PhoneHash == phoneHash
                     && !o.Used
                     && o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (otp is null)
            return Unauthorized(new { error = "no_active_code", message = "Request a new code." });

        if (otp.AttemptCount >= MaxAttemptsPerCode)
        {
            otp.Used = true;
            await _db.SaveChangesAsync(ct);
            return Unauthorized(new { error = "too_many_attempts", message = "Request a new code." });
        }

        if (otp.CodeHash != codeHash)
        {
            otp.AttemptCount++;
            await _db.SaveChangesAsync(ct);
            return Unauthorized(new { error = "invalid_code" });
        }

        otp.Used = true;
        await _db.SaveChangesAsync(ct);

        var driver = await _db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.PhoneHash == phoneHash, ct);
        if (driver is null)
            return Unauthorized(new { error = "driver_not_found" });

        var token = _jwt.IssueDriverToken(driver.Id, driver.ParkId, phoneHash);

        return Ok(new
        {
            token = token.Token,
            expiresAt = token.ExpiresAt,
            driver = new
            {
                id = driver.Id,
                parkId = driver.ParkId,
                name = driver.Name,
                yandexProfileId = driver.YandexDriverProfileId,
            },
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Strip everything except digits and a leading +.</summary>
    private static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length < 9) return null;
        return hasPlus ? "+" + digits : digits;
    }

    private static string HashPhone(string phone)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(phone));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashCode(string code) => HashPhone(code); // same primitive

    private static string GenerateCode()
    {
        // Cryptographically random 6-digit code (000000–999999).
        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6");
    }

    public record RequestOtpDto(string Phone);
    public record VerifyOtpDto(string Phone, string Code);
}
