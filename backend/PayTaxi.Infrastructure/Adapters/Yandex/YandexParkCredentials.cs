using Microsoft.EntityFrameworkCore;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Adapters.Yandex;

/// <summary>
/// The three values a park copies from fleet.yandex.com → Settings → API.
/// <c>ClientId</c> is the literal "X-Client-ID" Yandex shows there (format "taxi/park/{id}").
/// </summary>
public record YandexParkCredentials(string ClientId, string ApiKey, string YandexParkId);

/// <summary>Resolves a PayTaxi park id to its Yandex Fleet credentials.</summary>
public interface IYandexParkCredentialsProvider
{
    Task<YandexParkCredentials> GetAsync(Guid parkId, CancellationToken ct);
}

/// <summary>Reads the per-park credentials from the parks table (plaintext until Phase 8 AES).</summary>
public class DbYandexParkCredentialsProvider : IYandexParkCredentialsProvider
{
    private readonly AppDbContext _db;
    public DbYandexParkCredentialsProvider(AppDbContext db) => _db = db;

    public async Task<YandexParkCredentials> GetAsync(Guid parkId, CancellationToken ct)
    {
        var p = await _db.Parks.AsNoTracking()
            .Where(x => x.Id == parkId)
            .Select(x => new { x.YandexClientIdEncrypted, x.YandexApiKeyEncrypted, x.YandexParkId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Park {parkId} not found");

        if (string.IsNullOrWhiteSpace(p.YandexClientIdEncrypted) || string.IsNullOrWhiteSpace(p.YandexApiKeyEncrypted)
            || string.IsNullOrWhiteSpace(p.YandexParkId))
            throw new InvalidOperationException($"Park {parkId} has no Yandex Fleet credentials configured");

        return new YandexParkCredentials(p.YandexClientIdEncrypted, p.YandexApiKeyEncrypted, p.YandexParkId);
    }
}
