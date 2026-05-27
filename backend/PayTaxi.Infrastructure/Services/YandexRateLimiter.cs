using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Yandex;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Process-local rate limiter that enforces a minimum interval between calls
/// targeting the same Yandex park. Uses one SemaphoreSlim per park to serialise
/// concurrent callers, and tracks the last-call timestamp so the limiter only
/// delays when calls actually overlap.
///
/// Note: this is per-process. Horizontal scaling will need a distributed lock
/// (Redis SETNX or similar) — that comes in Phase 8 hardening.
/// </summary>
public class YandexRateLimiter : IYandexRateLimiter
{
    private readonly YandexFleetOptions _opts;
    private readonly ConcurrentDictionary<Guid, ParkSlot> _slots = new();

    public YandexRateLimiter(IOptions<YandexFleetOptions> opts)
    {
        _opts = opts.Value;
    }

    public async Task AcquireAsync(Guid parkId, CancellationToken ct = default)
    {
        var slot = _slots.GetOrAdd(parkId, _ => new ParkSlot());

        await slot.Gate.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - slot.LastCallAt;
            var minInterval = TimeSpan.FromMilliseconds(_opts.MinIntervalMs);
            if (elapsed < minInterval)
            {
                var wait = minInterval - elapsed;
                await Task.Delay(wait, ct);
            }
            slot.LastCallAt = DateTime.UtcNow;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private sealed class ParkSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTime LastCallAt { get; set; } = DateTime.MinValue;
    }
}
