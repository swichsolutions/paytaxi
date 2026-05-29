using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks;

/// <summary>
/// In-memory mock of a bank mass-payout provider. Stands in for BOG/TBC until
/// real sandbox credentials are wired up in Phase 4.
///
/// Mirrors MockYandexFleetClient's shape:
///   - simulated latency (configurable)
///   - configurable transient failure rate (default 3%)
///   - idempotency-key deduplication: a repeat call with the same key returns
///     the same result without simulating another transfer
///
/// Registered as a singleton because the idempotency cache must outlive the
/// per-request DI scope (a retried HTTP request opens a new scope).
/// </summary>
public class MockBankPayoutProvider : IBankPayoutAdapter
{
    private readonly MockBankPayoutOptions _opts;
    private readonly ILogger<MockBankPayoutProvider> _log;
    private readonly Random _rng = new();

    // idempotency_key → previously returned result. Keeps mock retries safe.
    private readonly ConcurrentDictionary<string, BankTransferResult> _seen = new();

    // transfer_id → status, so GetTransferStatusAsync can answer for past transfers.
    private readonly ConcurrentDictionary<string, BankTransferStatus> _statuses = new();

    // Full transfer log so reconciliation can list by park + window.
    // Order matters for the window query → use a list under a lock.
    private readonly object _logLock = new();
    private readonly List<BankTransferRecord> _transferLog = new();

    public string BankType => "MOCK";

    public MockBankPayoutProvider(
        IOptions<MockBankPayoutOptions> opts,
        ILogger<MockBankPayoutProvider> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public async Task<BankTransferResult> SendPayoutAsync(
        BankTransferRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required", nameof(request));

        if (_seen.TryGetValue(request.IdempotencyKey, out var prior))
        {
            _log.LogInformation(
                "Mock bank dedup: returning prior result for idempotency_key={Key}",
                request.IdempotencyKey);
            return prior;
        }

        if (_opts.LatencyMs > 0)
            await Task.Delay(_opts.LatencyMs, ct);

        BankTransferResult result;
        if (_rng.NextDouble() < _opts.TransientFailureRate)
        {
            _log.LogWarning(
                "Mock bank injecting transient failure for park={ParkId} amount={Amount}",
                request.ParkId, request.Amount);
            result = new BankTransferResult(
                Success: false,
                TransferId: null,
                ErrorCode: "BANK_TRANSIENT_503",
                ErrorMessage: "Simulated bank gateway timeout — retryable");
        }
        else
        {
            var transferId = $"mock_tr_{Guid.NewGuid():N}".Substring(0, 20);
            _statuses[transferId] = BankTransferStatus.Completed;
            lock (_logLock)
            {
                _transferLog.Add(new BankTransferRecord(
                    TransferId: transferId,
                    ParkId: request.ParkId,
                    Amount: request.Amount,
                    Currency: request.Currency,
                    Status: BankTransferStatus.Completed,
                    SentAt: DateTime.UtcNow));
            }
            _log.LogInformation(
                "Mock bank transfer OK: {Amount} {Currency} → card {CardToken} (transfer_id={TransferId})",
                request.Amount, request.Currency, Mask(request.DestinationCardToken), transferId);
            result = new BankTransferResult(
                Success: true,
                TransferId: transferId,
                ErrorCode: null,
                ErrorMessage: null);
        }

        _seen[request.IdempotencyKey] = result;
        return result;
    }

    public Task<BankTransferStatus> GetTransferStatusAsync(
        string transferId, CancellationToken ct = default)
    {
        var status = _statuses.TryGetValue(transferId, out var s) ? s : BankTransferStatus.Unknown;
        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(
        Guid parkId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        IReadOnlyList<BankTransferRecord> snapshot;
        lock (_logLock)
        {
            snapshot = _transferLog
                .Where(r => r.ParkId == parkId && r.SentAt >= from && r.SentAt < to)
                .ToList();
        }
        return Task.FromResult(snapshot);
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) || token.Length <= 6
            ? "******"
            : $"****{token[^4..]}";
}

public class MockBankPayoutOptions
{
    public const string SectionName = "BankPayout:Mock";

    /// <summary>Simulated network latency (ms) per call.</summary>
    public int LatencyMs { get; set; } = 250;

    /// <summary>Probability (0..1) of a transient failure response.</summary>
    public double TransientFailureRate { get; set; } = 0.03;
}
