using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks;

/// <summary>
/// In-memory mock of a bank payout rail. Stands in for TBC/BoG until real
/// sandbox credentials are wired up.
///
/// Behaviour knobs (appsettings <c>BankPayout:Mock</c>):
///   - <see cref="MockBankPayoutOptions.LatencyMs"/> simulated network latency
///   - <see cref="MockBankPayoutOptions.TransientFailureRate"/> probability of a
///     retryable "gateway down" result (exercises the payout queue)
///   - <see cref="MockBankPayoutOptions.AmbiguousFailureRate"/> probability of a
///     thrown exception AFTER the transfer was recorded (exercises the
///     status-check-by-document-id path)
///   - <see cref="MockBankPayoutOptions.OutageUntilUtc"/> hard outage window — every
///     payout is retryable-failed until this time (lets you demo the queue draining)
///
/// Idempotency-key deduplication: a repeat call with the same key returns the same
/// result without simulating another transfer.
///
/// Registered as a singleton because the idempotency cache must outlive the
/// per-request DI scope.
/// </summary>
public class MockBankPayoutProvider : IBankPayoutAdapter
{
    private readonly MockBankPayoutOptions _opts;
    private readonly ILogger<MockBankPayoutProvider> _log;
    private static Random _rng => Random.Shared;

    // idempotency_key → previously returned result. Keeps mock retries safe.
    private readonly ConcurrentDictionary<string, BankTransferResult> _seen = new();

    // idempotency_key → transfer record (for FindTransferByIdempotencyKey)
    private readonly ConcurrentDictionary<string, BankTransferRecord> _byKey = new();

    // transfer_id → status, so GetTransferStatusAsync can answer for past transfers.
    private readonly ConcurrentDictionary<string, BankTransferStatus> _statuses = new();

    // Full transfer log so reconciliation can list by park account + window.
    private readonly object _logLock = new();
    private readonly List<(Guid AccountId, BankTransferRecord Record)> _transferLog = new();

    // Running balance per park account (starts at a comfortable float).
    private readonly ConcurrentDictionary<Guid, decimal> _balances = new();

    // Async-execution simulation: transfer_id → remaining status polls before it turns Completed.
    private readonly ConcurrentDictionary<string, int> _pendingPolls = new();

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

        // Hard outage window → everything is "not now".
        // The config binder parses "…Z" timestamps into LOCAL time; normalise before comparing.
        if (_opts.OutageUntilUtc is { } rawUntil && DateTime.UtcNow < ToUtc(rawUntil))
        {
            var until = ToUtc(rawUntil);
            _log.LogWarning("Mock bank in configured outage until {Until:u}", until);
            return new BankTransferResult(false, null, "BANK_UNAVAILABLE",
                "Simulated bank outage — retry later", IsRetryable: true);
            // deliberately NOT cached in _seen: the retry should get a fresh roll
        }

        // Hard validation: the destination must be a plausible Georgian IBAN.
        if (string.IsNullOrWhiteSpace(request.DestinationIban) || request.DestinationIban.Length != 22)
        {
            var bad = new BankTransferResult(false, null, "INVALID_DESTINATION",
                "Destination IBAN rejected by bank", IsRetryable: false);
            _seen[request.IdempotencyKey] = bad;
            return bad;
        }

        if (_rng.NextDouble() < _opts.TransientFailureRate)
        {
            _log.LogWarning(
                "Mock bank injecting transient failure for account={Account} amount={Amount}",
                request.Source.ParkBankAccountId, request.Amount);
            // Not cached: a transient failure must be retryable with the same key.
            return new BankTransferResult(false, null, "BANK_TRANSIENT_503",
                "Simulated bank gateway timeout — retryable", IsRetryable: true);
        }

        var transferId = $"mock_tr_{Guid.NewGuid():N}".Substring(0, 20);
        var record = new BankTransferRecord(
            TransferId: transferId,
            ParkId: request.Source.ParkId,
            Amount: request.Amount,
            Currency: request.Currency,
            Status: BankTransferStatus.Completed,
            SentAt: DateTime.UtcNow);

        _statuses[transferId] = _opts.AsyncExecution ? BankTransferStatus.Pending : BankTransferStatus.Completed;
        if (_opts.AsyncExecution) _pendingPolls[transferId] = Math.Max(1, _opts.AsyncSettleAfterPolls);
        _byKey[request.IdempotencyKey] = record;
        lock (_logLock) _transferLog.Add((request.Source.ParkBankAccountId, record));
        _balances.AddOrUpdate(request.Source.ParkBankAccountId,
            _ => _opts.InitialBalance - request.Amount,
            (_, b) => b - request.Amount);

        // Asynchronous rail simulation (TBC-like): accepted now, executed after a few status polls.
        var result = _opts.AsyncExecution
            ? new BankTransferResult(true, transferId, null, null, IsRetryable: false, IsPending: true)
            : new BankTransferResult(true, transferId, null, null);
        _seen[request.IdempotencyKey] = result;

        // Ambiguous response: the transfer went through but the caller sees an exception.
        // The saga must recover via FindTransferByIdempotencyKeyAsync, not by re-sending.
        if (_rng.NextDouble() < _opts.AmbiguousFailureRate)
        {
            _log.LogWarning(
                "Mock bank: transfer {TransferId} recorded but simulating a lost response",
                transferId);
            throw new TimeoutException("Simulated timeout after the bank accepted the transfer");
        }

        _log.LogInformation(
            "Mock bank transfer OK: {Amount} {Currency} {From} → {To} (transfer_id={TransferId})",
            request.Amount, request.Currency, Mask(request.Source.SourceIban),
            Mask(request.DestinationIban), transferId);
        return result;
    }

    public Task<BankTransferStatus> GetTransferStatusAsync(
        BankAccountContext source, string transferId, CancellationToken ct = default)
    {
        if (_pendingPolls.TryGetValue(transferId, out var left))
        {
            if (left <= 1)
            {
                _pendingPolls.TryRemove(transferId, out _);
                _statuses[transferId] = BankTransferStatus.Completed;
                _log.LogInformation("Mock bank: transfer {TransferId} now executed", transferId);
            }
            else
            {
                _pendingPolls[transferId] = left - 1;
                _log.LogInformation("Mock bank: transfer {TransferId} still processing ({Left} polls left)", transferId, left - 1);
            }
        }
        var status = _statuses.TryGetValue(transferId, out var s) ? s : BankTransferStatus.Unknown;
        return Task.FromResult(status);
    }

    public Task<BankTransferLookup?> FindTransferByIdempotencyKeyAsync(
        BankAccountContext source, string idempotencyKey, CancellationToken ct = default)
    {
        if (_byKey.TryGetValue(idempotencyKey, out var r))
            return Task.FromResult<BankTransferLookup?>(new BankTransferLookup(r.TransferId, r.Status, r.Amount));
        return Task.FromResult<BankTransferLookup?>(null);
    }

    public Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(
        BankAccountContext source, DateTime from, DateTime to, CancellationToken ct = default)
    {
        IReadOnlyList<BankTransferRecord> snapshot;
        lock (_logLock)
        {
            snapshot = _transferLog
                .Where(e => e.AccountId == source.ParkBankAccountId
                         && e.Record.SentAt >= from && e.Record.SentAt < to)
                .Select(e => e.Record)
                .ToList();
        }
        return Task.FromResult(snapshot);
    }

    public Task<decimal?> GetBalanceAsync(BankAccountContext source, CancellationToken ct = default)
    {
        var b = _balances.GetOrAdd(source.ParkBankAccountId, _ => _opts.InitialBalance);
        return Task.FromResult<decimal?>(b);
    }

    private static DateTime ToUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static string Mask(string iban) =>
        string.IsNullOrEmpty(iban) || iban.Length <= 6 ? "******" : $"****{iban[^4..]}";
}

public class MockBankPayoutOptions
{
    public const string SectionName = "BankPayout:Mock";

    /// <summary>Simulated network latency (ms) per call.</summary>
    public int LatencyMs { get; set; } = 250;

    /// <summary>Probability (0..1) of a retryable transient failure response.</summary>
    public double TransientFailureRate { get; set; } = 0.03;

    /// <summary>Probability (0..1) that a successful transfer surfaces as a thrown timeout.</summary>
    public double AmbiguousFailureRate { get; set; } = 0.01;

    /// <summary>If set and in the future, every payout fails retryably until then.</summary>
    public DateTime? OutageUntilUtc { get; set; }

    /// <summary>Starting mock balance per park account.</summary>
    public decimal InitialBalance { get; set; } = 25_000m;

    /// <summary>Simulate an asynchronous rail (like TBC DBI): accept now, execute after N status polls.</summary>
    public bool AsyncExecution { get; set; } = false;
    public int AsyncSettleAfterPolls { get; set; } = 2;
}
