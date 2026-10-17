using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Runs one of the API's token re-checks (AdminTokenCheck / DriverTokenCheck) on a real token against the test host's
/// services and memory cache, with a cache time of the test's choosing. The test host re-reads every account (its recheck
/// time is 0, so tests see their database edits at once); this is how a test sees what a production host, which caches the
/// check for 30 s, does with a cached "still fine" when the account changes.
/// </summary>
internal static class TokenCheckProbe
{
    /// <summary>True when <paramref name="check"/> refuses <paramref name="token"/> (it called Fail).</summary>
    public static async Task<bool> RefusedAsync(
        ApiFixture f, string token, Func<TokenValidatedContext, TimeSpan, Task> check, TimeSpan cacheFor)
    {
        var options = f.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        // The bearer handler's own claim mapping ("sub" → NameIdentifier, "role" → Role), as the checks see it in the API.
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, options.TokenValidationParameters, out _);
        using var scope = f.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var ctx = new TokenValidatedContext(http, scheme, options) { Principal = principal };
        await check(ctx, cacheFor);
        return ctx.Result?.Failure is not null;
    }
}
