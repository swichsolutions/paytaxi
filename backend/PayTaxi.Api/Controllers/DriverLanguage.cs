using Microsoft.EntityFrameworkCore;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// The language the driver app is showing, as it says on its requests (Accept-Language: the app sets it to its own
/// language — "ka", "en" or "ru"; a browser's default list reads the same way). Remembered on the driver
/// (<see cref="Core.Entities.Driver.PreferredLanguage"/>) at sign-in, at every token refresh and when the driver changes
/// a payout account, so a message the server words itself — the SMS about a payout-account change made from the console
/// — reaches the driver in that language. Only the first language counts, and only one of the app's three.
/// </summary>
public static class DriverLanguage
{
    public static string? Of(HttpRequest request)
    {
        var header = request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header)) return null;
        var first = header.Split(',')[0].Split(';')[0].Trim();
        var primary = first.Split('-')[0].Trim().ToLowerInvariant();
        return primary is "ka" or "en" or "ru" ? primary : null;
    }

    /// <summary>
    /// One conditional UPDATE: writes only when the language is new for this driver. Best effort and not cancellable by the
    /// request: it runs after a sign-in or a payout-account change is already saved, and a language not recorded only means
    /// the next server-worded message falls back to the last one known — never a failed sign-in or a lost notice.
    /// </summary>
    public static async Task RememberAsync(AppDbContext db, Guid driverId, HttpRequest request)
    {
        var language = Of(request);
        if (language is null) return;
        try
        {
            await db.Drivers
                .Where(d => d.Id == driverId && (d.PreferredLanguage == null || d.PreferredLanguage != language))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.PreferredLanguage, language), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            request.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DriverLanguage))
                .LogWarning(ex, "Driver {DriverId}: the app's language could not be recorded", driverId);
        }
    }
}
