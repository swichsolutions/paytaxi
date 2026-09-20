using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Driver phone+OTP authentication with trusted devices:
///   POST /api/driver/auth/request-otp  → generate + store a one-time code
///   POST /api/driver/auth/verify-otp   → validate the code, return a short JWT + a 90-day refresh token
///   POST /api/driver/auth/refresh      → rotate the refresh token, mint a new JWT (no SMS)
///   POST /api/driver/auth/logout       → revoke the refresh token (this device only)
///
/// The SMS code is therefore needed only on a new device, after 90 days, after logout,
/// or after a manager suspends the driver (refresh checks the driver is still Active).
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
    // Per-phone OTP cap — Auth:OtpWindowMinutes / Auth:OtpMaxPerWindow (defaults 10 min / 3 codes).
    private readonly int OtpWindowMinutes;
    private readonly int MaxOtpPerWindow;

    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AuthController> _log;
    private readonly int _trustedDeviceDays;

    public AuthController(
        AppDbContext db,
        IJwtTokenService jwt,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<AuthController> log)
    {
        _db = db;
        _jwt = jwt;
        _env = env;
        _log = log;
        _trustedDeviceDays = config.GetValue("Auth:TrustedDeviceDays", 90);
        OtpWindowMinutes = config.GetValue("Auth:OtpWindowMinutes", 10);
        MaxOtpPerWindow = config.GetValue("Auth:OtpMaxPerWindow", 3);
    }

    [HttpPost("request-otp")]
    public async Task<IActionResult> RequestOtp([FromBody] RequestOtpDto body, CancellationToken ct)
    {
        var phone = NormalizePhone(body?.Phone);
        if (phone is null) return BadRequest(new { error = "phone_required" });

        var phoneHash = HashPhone(phone);
        var driver = await _db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.PhoneHash == phoneHash, ct);

        // Per-phone cap, independent of the per-IP limiter (which a rotating X-Forwarded-For
        // could dodge): at most MaxOtpPerWindow codes per phone per window. Every code costs
        // an SMS once the gateway is wired.
        var windowStart = DateTime.UtcNow.AddMinutes(-OtpWindowMinutes);
        var recent = await _db.OtpCodes.CountAsync(o => o.PhoneHash == phoneHash && o.CreatedAt >= windowStart, ct);
        if (recent >= MaxOtpPerWindow)
        {
            _log.LogWarning("OTP rate cap hit for phone hash {Hash}", phoneHash[..8]);
            Response.Headers.RetryAfter = (OtpWindowMinutes * 60).ToString();
            return StatusCode(429, new { error = "too_many_requests", retryAfterSeconds = OtpWindowMinutes * 60 });
        }

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
            if (_env.IsDevelopment())
                _log.LogInformation("Dev OTP for {Phone}: {Code} (expires {Expires:HH:mm:ss})",
                    phone, code, DateTime.UtcNow.AddMinutes(CodeExpiryMinutes));
            else
                _log.LogInformation("OTP issued for phone hash {Hash}", phoneHash[..8]);
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

        if (driver.Status != Core.Enums.DriverStatus.Active)
            return Unauthorized(new { error = "driver_inactive" });

        // New trusted device: 90-day refresh token + short access token.
        var (session, rawRefresh) = NewSession(driver.Id, driver.ParkId);
        _db.DriverSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        var token = _jwt.IssueDriverToken(driver.Id, driver.ParkId, phoneHash);
        _log.LogInformation("Driver {DriverId} signed in on a new device ({Device}); session {SessionId} valid until {Exp:u}",
            driver.Id, session.DeviceLabel, session.Id, session.ExpiresAt);

        return Ok(new
        {
            token = token.Token,
            expiresAt = token.ExpiresAt,
            refreshToken = rawRefresh,
            refreshExpiresAt = session.ExpiresAt,
            driver = new
            {
                id = driver.Id,
                parkId = driver.ParkId,
                name = driver.Name,
                yandexProfileId = driver.YandexDriverProfileId,
            },
        });
    }

    /// <summary>
    /// Exchange a refresh token for a new access token. Rotates the refresh token: the old one
    /// is revoked and a successor returned. A revoked token being replayed means the token was
    /// copied — every session of that driver is revoked and the driver must log in again.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshDto body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.RefreshToken))
            return BadRequest(new { error = "refresh_token_required" });

        var hash = Sha256Hex(body.RefreshToken.Trim());
        var session = await _db.DriverSessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null)
            return Unauthorized(new { error = "invalid_refresh_token" });

        if (session.RevokedAt is not null)
        {
            // A token that was ROTATED and comes back is a replay: only a copy could still hold it,
            // so assume compromise and kill every session of this driver.
            //
            // A token revoked by a normal logout (or a suspension) is different: the app that just
            // logged out may well fire one more refresh from its cache, and a stale tab can too.
            // Treating that as theft would sign the driver out of every other phone — refuse this
            // token only.
            if (session.RevokedReason == "rotated")
            {
                var all = await _db.DriverSessions
                    .Where(s => s.DriverId == session.DriverId && s.RevokedAt == null)
                    .ToListAsync(ct);
                foreach (var s in all) { s.RevokedAt = DateTime.UtcNow; s.RevokedReason = "refresh_reuse_detected"; }
                await _db.SaveChangesAsync(ct);
                _log.LogWarning("Refresh token reuse for driver {DriverId} — revoked {Count} session(s)", session.DriverId, all.Count);
            }
            return Unauthorized(new { error = "session_revoked", message = "Please sign in again." });
        }

        if (session.ExpiresAt <= DateTime.UtcNow)
            return Unauthorized(new { error = "session_expired", message = "Please sign in again." });

        var driver = await _db.Drivers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DriverId, ct);
        if (driver is null || driver.Status != Core.Enums.DriverStatus.Active)
        {
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "driver_inactive";
            await _db.SaveChangesAsync(ct);
            return Unauthorized(new { error = "driver_inactive", message = "Your account is not active." });
        }

        // Rotate. The successor keeps the original expiry: 90 days from the SMS login, not forever.
        var (next, rawNext) = NewSession(driver.Id, driver.ParkId);
        next.ExpiresAt = session.ExpiresAt;
        session.RevokedAt = DateTime.UtcNow;
        session.RevokedReason = "rotated";
        session.ReplacedBySessionId = next.Id;
        session.LastUsedAt = DateTime.UtcNow;
        _db.DriverSessions.Add(next);
        await _db.SaveChangesAsync(ct);

        var token = _jwt.IssueDriverToken(driver.Id, driver.ParkId, driver.PhoneHash);
        return Ok(new
        {
            token = token.Token,
            expiresAt = token.ExpiresAt,
            refreshToken = rawNext,
            refreshExpiresAt = next.ExpiresAt,
            driver = new
            {
                id = driver.Id,
                parkId = driver.ParkId,
                name = driver.Name,
                yandexProfileId = driver.YandexDriverProfileId,
            },
        });
    }

    /// <summary>Revoke this device's refresh token. Idempotent; unknown tokens are ignored.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshDto body, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(body?.RefreshToken))
        {
            var hash = Sha256Hex(body.RefreshToken.Trim());
            var session = await _db.DriverSessions.FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, ct);
            if (session is not null)
            {
                session.RevokedAt = DateTime.UtcNow;
                session.RevokedReason = "logout";
                await _db.SaveChangesAsync(ct);
            }
        }
        return NoContent();
    }

    private (DriverSession session, string rawToken) NewSession(Guid driverId, Guid parkId)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var session = new DriverSession
        {
            DriverId = driverId,
            ParkId = parkId,
            TokenHash = Sha256Hex(raw),
            DeviceLabel = DeviceLabel(Request.Headers.UserAgent.ToString()),
            ExpiresAt = DateTime.UtcNow.AddDays(_trustedDeviceDays),
            LastUsedAt = DateTime.UtcNow,
        };
        return (session, raw);
    }

    /// <summary>"Android · Chrome"-style hint from the User-Agent; never the raw UA (PII hygiene).</summary>
    private static string DeviceLabel(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "Unknown device";
        var os = ua.Contains("Android") ? "Android"
               : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
               : ua.Contains("Windows") ? "Windows"
               : ua.Contains("Mac OS") ? "macOS"
               : ua.Contains("Linux") ? "Linux" : "Other";
        var browser = ua.Contains("Edg/") ? "Edge"
                    : ua.Contains("Chrome/") ? "Chrome"
                    : ua.Contains("Firefox/") ? "Firefox"
                    : ua.Contains("Safari/") ? "Safari" : "Browser";
        return $"{os} · {browser}";
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ── Helpers ──────────────────────────────────────────────────────

    // Same normaliser as admin onboarding / import / seed, so "599123456", "+995 599 123 456"
    // and "995599123456" all hash to the same driver.
    private static string? NormalizePhone(string? raw) => PayTaxi.Core.Identity.GeorgianPhone.Normalize(raw);
    private static string HashPhone(string phone) => PayTaxi.Core.Identity.GeorgianPhone.Hash(phone);

    private static string HashCode(string code) => HashPhone(code); // same primitive

    private static string GenerateCode()
    {
        // Cryptographically random 6-digit code (000000–999999).
        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6");
    }

    public record RequestOtpDto(string Phone);
    public record VerifyOtpDto(string Phone, string Code);
    public record RefreshDto(string RefreshToken);
}
