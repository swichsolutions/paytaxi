using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Security;
using PayTaxi.Infrastructure.Services;

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
/// The code reaches the driver as an SMS through <see cref="ISmsSender"/>, in the language the app last used
/// (<see cref="Core.Entities.Driver.PreferredLanguage"/>; Georgian when unknown) — best effort, like the payout-account
/// notices: a gateway that is down or slow never fails the request (the sender logs; this waits at most
/// <see cref="SmsWait"/>, then answers while the SMS finishes). Without a gateway (the log sender,
/// <see cref="ISmsSender.DeliversForReal"/> false — the tests, the e2e runs, a developer without a key) the code is
/// returned as `devCode` instead, and the sender logs it. With a gateway it is returned nowhere, in Development too.
///
/// The code is stored only as a keyed hash (HMAC-SHA256 with the server-held hash key, bound to its row —
/// <see cref="LookupHash.SignInCode"/>), and the phone it was sent to only as the phone's keyed lookup hash. A plain
/// SHA-256 of six digits is reversed by a million guesses, so a database copy alone would have given the codes back;
/// keyed, it does not without the key. Guessing through the API is what the 5-minute expiry, the per-code attempt cap
/// and the per-phone code cap stop. Codes are deleted 24 hours after issue (RetentionWorker, on every poll).
///
/// Across codes, per phone and over a rolling day (PhoneSignInEvents): at most Auth:OtpMaxPerDay codes (default 10, on
/// top of the per-window cap), and after Auth:SignInFailuresBeforeLock failed verifications (default 10) the phone can
/// neither ask for nor enter a code for Auth:SignInLockHours (default 4) — a new code no longer buys a fresh budget of
/// guesses. Both limits count every phone that asks, registered or not, and answer alike (429 + code + retry-after), so
/// neither tells a driver's number from any other. A lockout of a driver's phone is a security event: logged, and shown
/// on that park's alert banner (AlertsController, signin_locked).
/// </summary>
[ApiController]
[Route("api/driver/auth")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private const int CodeExpiryMinutes = 5;
    private const int MaxAttemptsPerCode = 5;
    /// <summary>Advisory-lock class of the per-phone "count codes, then issue one" section (see RequestOtp).</summary>
    private const int OtpLockClass = 0x0779_0001;
    /// <summary>A rotated refresh token that comes back within this many seconds is a racing tab, not a stolen copy (see Refresh).</summary>
    private const int RotationGraceSeconds = 10;
    /// <summary>How long request-otp waits for the SMS gateway before answering anyway (the SMS keeps going; NotificationService does the same).</summary>
    private static readonly TimeSpan SmsWait = TimeSpan.FromSeconds(5);
    // Per-phone OTP cap — Auth:OtpWindowMinutes / Auth:OtpMaxPerWindow (defaults 10 min / 3 codes).
    private readonly int OtpWindowMinutes;
    private readonly int MaxOtpPerWindow;
    /// <summary>The look-back of the per-phone day limits (codes per day, failed verifications before the lock).</summary>
    private static readonly TimeSpan GuardWindow = TimeSpan.FromHours(24);
    // Auth:OtpMaxPerDay (10), Auth:SignInFailuresBeforeLock (10), Auth:SignInLockHours (4).
    private readonly int MaxOtpPerDay;
    private readonly int FailuresBeforeLock;
    private readonly TimeSpan LockFor;

    private readonly AppDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly ISmsSender _sms;
    private readonly ILogger<AuthController> _log;
    private readonly int _trustedDeviceDays;

    public AuthController(
        AppDbContext db,
        IJwtTokenService jwt,
        ISmsSender sms,
        IConfiguration config,
        ILogger<AuthController> log)
    {
        _db = db;
        _jwt = jwt;
        _sms = sms;
        _log = log;
        _trustedDeviceDays = config.GetValue("Auth:TrustedDeviceDays", 90);
        OtpWindowMinutes = config.GetValue("Auth:OtpWindowMinutes", 10);
        MaxOtpPerWindow = config.GetValue("Auth:OtpMaxPerWindow", 3);
        MaxOtpPerDay = Math.Max(1, config.GetValue("Auth:OtpMaxPerDay", 10));
        FailuresBeforeLock = Math.Max(1, config.GetValue("Auth:SignInFailuresBeforeLock", 10));
        LockFor = TimeSpan.FromHours(Math.Max(0.1, config.GetValue("Auth:SignInLockHours", 4.0)));
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
        // an SMS once the gateway is wired. The count and the insert below run under a per-phone
        // transaction lock: without it a burst of parallel requests all counted the same "2 so far"
        // and each issued a code — every one a fresh five-guess budget.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({OtpLockClass}, hashtext({phoneHash}))", ct);
        var now = DateTime.UtcNow;

        // Locked after too many failed verifications: no code either — a fresh code would be a fresh set of guesses.
        if (await LockedUntilAsync(phoneHash, now, ct) is { } lockedUntil)
            return SignInLocked(lockedUntil, now);

        var windowStart = now.AddMinutes(-OtpWindowMinutes);
        var recent = await _db.OtpCodes.CountAsync(o => o.PhoneHash == phoneHash && o.CreatedAt >= windowStart, ct);
        if (recent >= MaxOtpPerWindow)
        {
            _log.LogWarning("OTP rate cap hit for phone hash {Hash}", LookupHash.ForLog(phoneHash));
            Response.Headers.RetryAfter = (OtpWindowMinutes * 60).ToString();
            return StatusCode(429, new { error = "too_many_requests", retryAfterSeconds = OtpWindowMinutes * 60 });
        }

        // The day's cap, from the events every phone leaves (the per-window cap above counts OtpCodes, which only drivers
        // have). Every code is a paid SMS once the gateway is wired; a phone asking for an eleventh in a day is a script.
        var dayStart = now - GuardWindow;
        var issuedToday = await _db.PhoneSignInEvents.AsNoTracking()
            .Where(e => e.PhoneHash == phoneHash && e.Kind == PhoneSignInEventKinds.CodeIssued && e.At > dayStart)
            .OrderBy(e => e.At).Select(e => e.At).ToListAsync(ct);
        if (issuedToday.Count >= MaxOtpPerDay)
        {
            // Free again when enough of the day's codes have aged out to leave room for one more.
            var freeAt = issuedToday[issuedToday.Count - MaxOtpPerDay] + GuardWindow;
            var retryAfter = Math.Max(1, (int)Math.Ceiling((freeAt - now).TotalSeconds));
            _log.LogWarning("OTP daily cap ({Max}) hit for phone hash {Hash}", MaxOtpPerDay, LookupHash.ForLog(phoneHash));
            Response.Headers.RetryAfter = retryAfter.ToString();
            return StatusCode(429, new { error = "daily_code_limit", retryAfterSeconds = retryAfter });
        }

        // A suspended driver is told at this step (the app maps driver_inactive here) and no code is
        // issued — every code costs an SMS, and verify would refuse it anyway. Unknown phones still get
        // the generic "sent" below so the endpoint does not enumerate drivers.
        if (driver is not null && driver.Status != Core.Enums.DriverStatus.Active)
        {
            _log.LogInformation("OTP refused for inactive driver {DriverId} (status={Status})", driver.Id, driver.Status);
            return StatusCode(403, new { error = "driver_inactive", message = "Your account is not active." });
        }

        // We DON'T reveal whether the phone exists — same response either way.
        // The OTP just goes nowhere if there's no driver behind the phone. It counts against the day's cap all the same.
        var code = GenerateCode();
        _db.PhoneSignInEvents.Add(new PhoneSignInEvent { PhoneHash = phoneHash, Kind = PhoneSignInEventKinds.CodeIssued, At = now });
        if (driver is not null)
        {
            var otp = new OtpCode
            {
                PhoneHash = phoneHash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(CodeExpiryMinutes),
            };
            otp.CodeHash = LookupHash.SignInCode(otp.Id, code);   // bound to the row: needs its id first
            _db.OtpCodes.Add(otp);
        }
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (driver is not null)
        {
            // The code itself is never logged here: the log sender logs its text in Development, a gateway never does.
            _log.LogInformation("OTP issued for phone hash {Hash} (expires {Expires:HH:mm:ss})",
                LookupHash.ForLog(phoneHash), DateTime.UtcNow.AddMinutes(CodeExpiryMinutes));
            await SendCodeAsync(phone, driver, code);
        }
        else
        {
            _log.LogWarning("OTP requested for unknown phone (hash {Hash})", LookupHash.ForLog(phoneHash));
        }

        // Without a gateway the code is returned here (the tests and the e2e runs read it; the app shows it as "DEV CODE").
        // With one it is in the SMS only.
        return Ok(new
        {
            sent = true,
            expiresInSeconds = CodeExpiryMinutes * 60,
            devCode = !_sms.DeliversForReal && driver is not null ? code : null,
        });
    }

    /// <summary>
    /// The sign-in SMS, in the driver's language. Best effort: the sender never throws (its contract), but a gateway that
    /// is slow must not hold the sign-in up — after <see cref="SmsWait"/> the request answers "sent" while the SMS finishes
    /// in the background and logs its own outcome. Not cancellable by the request: the code is already issued.
    /// </summary>
    private async Task SendCodeAsync(string phone, Driver driver, string code)
    {
        Task send;
        try
        {
            send = _sms.SendAsync(phone, SignInCodeSms.Text(driver.PreferredLanguage, code, CodeExpiryMinutes),
                SignInCodeSms.Purpose, $"driver {driver.Id}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sign-in SMS for driver {DriverId} could not be sent", driver.Id);
            return;
        }
        _ = send.ContinueWith(t => _log.LogError(t.Exception, "Sign-in SMS for driver {DriverId} could not be sent", driver.Id),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try { await send.WaitAsync(SmsWait, CancellationToken.None); }
        catch (TimeoutException)
        {
            _log.LogWarning("Sign-in SMS for driver {DriverId}: the gateway is slow; the request answers while it is still sending", driver.Id);
        }
        catch (Exception) { /* logged by the continuation above */ }
    }

    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto body, CancellationToken ct)
    {
        var phone = NormalizePhone(body?.Phone);
        if (phone is null) return BadRequest(new { error = "phone_required" });
        if (string.IsNullOrWhiteSpace(body?.Code))
            return BadRequest(new { error = "code_required" });

        var phoneHash = HashPhone(phone);

        // One verification per phone at a time, under the same lock as request-otp: the lockout's count, its check and the
        // code's own attempt counter move together, so no burst of parallel guesses can slip past the tenth failure.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({OtpLockClass}, hashtext({phoneHash}))", ct);
        var now = DateTime.UtcNow;

        // Locked: not even the right code is looked at until the lock ends.
        if (await LockedUntilAsync(phoneHash, now, ct) is { } lockedUntil)
            return SignInLocked(lockedUntil, now);

        // Latest unused, unexpired code matching the phone.
        var otp = await _db.OtpCodes.AsNoTracking()
            .Where(o => o.PhoneHash == phoneHash
                     && !o.Used
                     && o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Id, o.CodeHash })
            .FirstOrDefaultAsync(ct);

        // Every verification that does not sign in counts towards the lock — "no code" too: an unknown phone never has
        // one, and counting only wrong codes would make a registered number the only kind that can be locked.
        if (otp is null)
            return await FailedAsync(tx, phoneHash, now, Unauthorized(new { error = "no_active_code", message = "Request a new code." }), ct);

        // Spend one of the code's attempts BEFORE comparing, in one conditional UPDATE. Read-check-increment-save
        // lost updates under concurrency: forty parallel guesses all read "0 so far", were all compared, and left the
        // counter near 1 — the five-guess cap capped nothing. Now at most five requests ever win an attempt, and only
        // a request that won one gets its guess compared.
        var spent = await _db.OtpCodes
            .Where(o => o.Id == otp.Id && !o.Used && o.ExpiresAt > now && o.AttemptCount < MaxAttemptsPerCode)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.AttemptCount, o => o.AttemptCount + 1), ct);
        if (spent == 0)
        {
            // Out of attempts (or used/expired a moment ago): retire it so the next request asks for a new code.
            await _db.OtpCodes.Where(o => o.Id == otp.Id && !o.Used)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Used, true), ct);
            return await FailedAsync(tx, phoneHash, now, Unauthorized(new { error = "too_many_attempts", message = "Request a new code." }), ct);
        }

        if (!LookupHash.SignInCodeMatches(otp.Id, body.Code, otp.CodeHash))
            return await FailedAsync(tx, phoneHash, now, Unauthorized(new { error = "invalid_code" }), ct);

        // The right code: exactly one request may use it (a double tap must not open two sessions).
        var won = await _db.OtpCodes.Where(o => o.Id == otp.Id && !o.Used)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Used, true), ct);
        if (won == 0)
            return await FailedAsync(tx, phoneHash, now, Unauthorized(new { error = "no_active_code", message = "Request a new code." }), ct);
        await tx.CommitAsync(ct);

        var driver = await _db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.PhoneHash == phoneHash, ct);
        if (driver is null)
            return Unauthorized(new { error = "driver_not_found" });

        if (driver.Status != Core.Enums.DriverStatus.Active)
            return Unauthorized(new { error = "driver_inactive" });

        // New trusted device: 90-day refresh token + short access token. The sign-in itself goes to the sign-in history
        // (kept 2 years — signing in is where the Terms of Use are accepted), which outlives the session (deleted 90 days
        // after its last use; RetentionWorker). A refresh is not a sign-in and writes no history.
        var (session, rawRefresh) = NewSession(driver.Id, driver.ParkId);
        _db.DriverSessions.Add(session);
        _db.DriverSignIns.Add(new DriverSignIn
        {
            DriverId = driver.Id,
            ParkId = driver.ParkId,
            SignedInAt = session.CreatedAt,
            DeviceLabel = session.DeviceLabel,
        });
        await _db.SaveChangesAsync(ct);

        // The access token names its session: signing out, a suspension or a phone change ends both (DriverTokenCheck).
        var token = _jwt.IssueDriverToken(driver.Id, driver.ParkId, phoneHash, session.Id);
        await DriverLanguage.RememberAsync(_db, driver.Id, Request);
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
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("refresh")]
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
            // Except a token rotated a moment ago: that is the other tab of the same app, whose refresh raced the one
            // that won (it read the row after the winner's commit). It gets the same 409 as a request that lost the
            // conditional update below, and picks up the winner's tokens. Nothing is issued to it, so a thief replaying
            // within the grace window gains nothing — and the next replay after it still fires the alarm.
            if (session.RevokedReason == "rotated" && session.RevokedAt > DateTime.UtcNow.AddSeconds(-RotationGraceSeconds))
                return Conflict(new { error = "refresh_in_progress", message = "This session was just refreshed elsewhere." });

            if (session.RevokedReason == "rotated")
            {
                var all = await _db.DriverSessions
                    .Where(s => s.DriverId == session.DriverId && s.RevokedAt == null)
                    .ToListAsync(ct);
                foreach (var s in all) { s.RevokedAt = DateTime.UtcNow; s.RevokedReason = "refresh_reuse_detected"; }
                await _db.SaveChangesAsync(ct);
                await ForgetTokenStateAsync(session.DriverId, ct);   // the access tokens in hand stop now, not in 30 s
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
        // The old token is retired by a conditional UPDATE (still live → rotated), so of two refreshes that arrive
        // together with the same token — two tabs of one app — exactly one wins. The other gets 409, which the app
        // answers by picking up the winner's tokens; it is not a replay, so it must not fire the theft response
        // (and two successors must never both stay alive).
        var (next, rawNext) = NewSession(driver.Id, driver.ParkId);
        next.ExpiresAt = session.ExpiresAt;
        var rotatedAt = DateTime.UtcNow;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var rotated = await _db.DriverSessions
                .Where(s => s.Id == session.Id && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.RevokedAt, rotatedAt)
                    .SetProperty(x => x.RevokedReason, "rotated")
                    .SetProperty(x => x.ReplacedBySessionId, next.Id)
                    .SetProperty(x => x.LastUsedAt, rotatedAt), ct);
            if (rotated == 0)
                return Conflict(new { error = "refresh_in_progress", message = "This session was just refreshed elsewhere." });
            _db.DriverSessions.Add(next);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var token = _jwt.IssueDriverToken(driver.Id, driver.ParkId, driver.PhoneHash, next.Id);
        // Hourly, so the language the server words its SMS in follows the driver's switch in the app within the hour.
        await DriverLanguage.RememberAsync(_db, driver.Id, Request);
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
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("refresh")]
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
                // This device's access token stops with its refresh token — now, not when the cached check runs out.
                await ForgetTokenStateAsync(session.DriverId, ct);
            }
        }
        return NoContent();
    }

    private Task ForgetTokenStateAsync(Guid driverId, CancellationToken ct) => Security.DriverTokenCheck.ForgetAsync(
        HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), _db, driverId, ct);

    // ── Per-phone day limits (PhoneSignInEvents) ─────────────────────

    /// <summary>When the phone's current lockout ends, or null. Called under the per-phone lock.</summary>
    private async Task<DateTime?> LockedUntilAsync(string phoneHash, DateTime now, CancellationToken ct) =>
        await _db.PhoneSignInEvents.AsNoTracking()
            .Where(e => e.PhoneHash == phoneHash && e.Kind == PhoneSignInEventKinds.Locked && e.Until > now)
            .MaxAsync(e => e.Until, ct);

    private ObjectResult SignInLocked(DateTime until, DateTime now)
    {
        var retryAfter = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
        Response.Headers.RetryAfter = retryAfter.ToString();
        return StatusCode(429, new { error = "sign_in_locked", retryAfterSeconds = retryAfter });
    }

    /// <summary>
    /// Records a verification that did not sign in and commits it (still under the per-phone lock). Any that brings the
    /// rolling day's count to Auth:SignInFailuresBeforeLock or more locks the phone and is answered as locked; the lock is a
    /// security event (log; the park's alert banner when the number is a driver's). The count is the whole rolling day's,
    /// not the misses since the last lock: ten wrong codes per rolling 24 hours. Counting from the last lock gave a fresh
    /// ten after every four-hour lock — about sixty guesses a day; now a miss after a lock, while the day's ten still
    /// count, locks again at once, and the full budget returns only as those misses age out of the day.
    /// </summary>
    private async Task<IActionResult> FailedAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, string phoneHash, DateTime now, IActionResult answer, CancellationToken ct)
    {
        var since = now - GuardWindow;
        var earlier = await _db.PhoneSignInEvents.AsNoTracking()
            .CountAsync(e => e.PhoneHash == phoneHash && e.Kind == PhoneSignInEventKinds.Failed && e.At > since, ct);

        _db.PhoneSignInEvents.Add(new PhoneSignInEvent { PhoneHash = phoneHash, Kind = PhoneSignInEventKinds.Failed, At = now });
        var locks = earlier + 1 >= FailuresBeforeLock;
        var until = now + LockFor;
        if (locks)
            _db.PhoneSignInEvents.Add(new PhoneSignInEvent { PhoneHash = phoneHash, Kind = PhoneSignInEventKinds.Locked, At = now, Until = until });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (!locks) return answer;

        var driverId = await _db.Drivers.AsNoTracking().Where(d => d.PhoneHash == phoneHash).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        _log.LogWarning("SECURITY: sign-in locked until {Until:u} for phone hash {Hash} ({Driver}) after {Failures} failed verifications within {Hours} h",
            until, LookupHash.ForLog(phoneHash), driverId is { } id ? $"driver {id}" : "no driver", earlier + 1, GuardWindow.TotalHours);
        return SignInLocked(until, now);
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

    // Refresh tokens only. A plain SHA-256 is enough for them: a token is 256 random bits, there is nothing to
    // enumerate — unlike a phone number or a 6-digit code, which is why those go through LookupHash.
    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ── Helpers ──────────────────────────────────────────────────────

    // Same normaliser and keyed hash as admin onboarding / import / seed, so "599123456", "+995 599 123 456"
    // and "995599123456" all hash to the same driver.
    private static string? NormalizePhone(string? raw) => PayTaxi.Core.Identity.GeorgianPhone.Normalize(raw);
    private static string HashPhone(string phone) => LookupHash.Phone(phone);

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
