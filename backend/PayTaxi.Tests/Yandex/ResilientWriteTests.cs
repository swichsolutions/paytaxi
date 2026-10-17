using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Yandex;
using Xunit;

namespace PayTaxi.Tests.Yandex;

/// <summary>A balance write retried under the same token: a decline after an unanswered attempt is "unknown", not "rejected".</summary>
public class ResilientWriteTests
{
    private sealed class Inner : IYandexFleetClient
    {
        public Queue<Func<YandexTransactionResult>> Answers { get; } = new();
        public int Posts { get; private set; }

        private Task<YandexTransactionResult> Next() { Posts++; return Task.FromResult(Answers.Dequeue()()); }

        public Task<YandexTransactionResult> PostCashoutTransactionAsync(Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default) => Next();
        public Task<YandexTransactionResult> PostReversalTransactionAsync(Guid parkId, string driverProfileId, decimal amount, string idempotencyKey, CancellationToken ct = default) => Next();
        public Task<IReadOnlyList<YandexDriverProfile>> GetDriverProfilesAsync(Guid parkId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<decimal> GetDriverBalanceAsync(Guid parkId, string driverProfileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<YandexTransaction>> GetTransactionsAsync(Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<YandexOrder>> GetOrdersAsync(Guid parkId, string driverProfileId, DateTime from, DateTime to, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoLimit : IYandexRateLimiter { public Task AcquireAsync(Guid parkId, CancellationToken ct = default) => Task.CompletedTask; }
    private sealed class NoAudit : IApiAuditLogger { public Task LogAsync(ApiCallRecord record, CancellationToken ct = default) => Task.CompletedTask; }

    private static (ResilientYandexFleetClient client, Inner inner) Client()
    {
        var inner = new Inner();
        var opts = Options.Create(new YandexFleetOptions { MaxRetries = 3, RetryBaseDelayMs = 0 });
        return (new ResilientYandexFleetClient(inner, new NoLimit(), new NoAudit(), opts, NullLogger<ResilientYandexFleetClient>.Instance), inner);
    }

    private static readonly YandexTransactionResult Declined = new(false, null, "duplicate_request", "Request with this token was already processed");

    [Fact]
    public async Task A_decline_after_a_timed_out_attempt_is_an_unknown_outcome()
    {
        var (client, inner) = Client();
        inner.Answers.Enqueue(() => throw new YandexTransientException("Yandex Fleet timed out"));
        inner.Answers.Enqueue(() => Declined);

        await Assert.ThrowsAsync<YandexOutcomeUnknownException>(() =>
            client.PostCashoutTransactionAsync(Guid.NewGuid(), "yp", 10m, "key-1"));
        Assert.Equal(2, inner.Posts);

        inner.Answers.Enqueue(() => throw new YandexTransientException("HTTP 502"));
        inner.Answers.Enqueue(() => Declined);
        await Assert.ThrowsAsync<YandexOutcomeUnknownException>(() =>
            client.PostReversalTransactionAsync(Guid.NewGuid(), "yp", 10m, "rev:key-1"));
    }

    [Fact]
    public async Task A_first_attempt_decline_is_still_a_plain_rejection()
    {
        var (client, inner) = Client();
        inner.Answers.Enqueue(() => Declined);
        var result = await client.PostCashoutTransactionAsync(Guid.NewGuid(), "yp", 10m, "key-2");
        Assert.False(result.Success);
        Assert.Equal("duplicate_request", result.ErrorCode);
    }

    [Fact]
    public async Task A_success_after_a_timed_out_attempt_is_a_success()
    {
        var (client, inner) = Client();
        inner.Answers.Enqueue(() => throw new YandexTransientException("Yandex Fleet timed out"));
        inner.Answers.Enqueue(() => new YandexTransactionResult(true, "tx-1", null, null));
        var result = await client.PostCashoutTransactionAsync(Guid.NewGuid(), "yp", 10m, "key-3");
        Assert.True(result.Success);
        Assert.Equal("tx-1", result.TransactionId);
    }
}
