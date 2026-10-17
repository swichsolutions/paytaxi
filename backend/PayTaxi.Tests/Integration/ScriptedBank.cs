using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks;
using PayTaxi.Infrastructure.Adapters.Yandex;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Shared by the payout-wait and settlement tests. One payment order per document id, as at TBC: sending an id again returns that same order
/// (DUPLICATED_SINGLE_PAYMENT_REQUEST recovery), whatever became of it. Money on or off, an
/// asynchronous mode (accept now, decide on the status poll), a permanent rejection, or a dead
/// connection where the send and the lookup both throw. For the cancel-safety tests also: a lost
/// response (the order exists, the caller sees an exception), a lookup that cannot be answered, and
/// a definite answer that says nothing about any document (expired password, blocked user). For the
/// settlement tests: a status code the adapter cannot map (the lookup and the poll report Unknown while
/// the order may still execute), a readable account balance, and every request as sent.
/// </summary>
internal sealed class ScriptedBank : IBankPayoutAdapter
{
    public bool Funded { get; set; }
    public bool Async { get; set; }
    public bool RejectForGood { get; set; }
    public bool Unreachable { get; set; }
    /// <summary>The order is created (and executed if funded), then the response is lost: the send throws.</summary>
    public bool LoseResponse { get; set; }
    /// <summary>The lookup by document id throws, whatever the send did.</summary>
    public bool LookupFails { get; set; }
    /// <summary>A retryable refusal with no order id (e.g. TBC_CREDENTIALS_MUST_BE_CHANGED): nothing about any document.</summary>
    public string? Fault { get; set; }
    /// <summary>While set, the lookup and the status poll report this status for an existing order (e.g. Unknown: a code the adapter cannot map).</summary>
    public BankTransferStatus? Reports { get; set; }
    /// <summary>What GetBalanceAsync answers (null = the bank does not say).</summary>
    public decimal? Balance { get; set; }
    /// <summary>Awaited at the start of every send — lets a test hold one executor inside its bank call while another runs.</summary>
    public Func<BankTransferRequest, Task>? OnSend { get; set; }
    /// <summary>
    /// One-shot: awaited after the lookup has formed its answer and before it is returned — lets a test run another
    /// executor between "the bank said nothing is there" and whatever the caller does with that answer.
    /// </summary>
    public Func<string, Task>? AfterLookup { get; set; }
    public int Calls { get; private set; }
    public List<string> Sent { get; } = new();
    /// <summary>Every transfer request as it reached the bank.</summary>
    public List<BankTransferRequest> Requests { get; } = new();
    /// <summary>Every document id asked about through the lookup.</summary>
    public List<string> LookedUp { get; } = new();

    private readonly Dictionary<string, Order> _orders = new();
    private int _n;
    private sealed class Order { public string Id = ""; public BankTransferStatus Status; public string? Code; }

    public string BankType => "SCRIPTED";
    public string? OrderIdFor(string key) => _orders.TryGetValue(key, out var o) ? o.Id : null;

    public async Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default)
    {
        if (OnSend is { } hook) await hook(request);
        lock (_orders) return Send(request);
    }

    private BankTransferResult Send(BankTransferRequest request)
    {
        Calls++;
        Sent.Add(request.IdempotencyKey);
        Requests.Add(request);
        if (Unreachable) throw new HttpRequestException("connection reset by peer");
        if (Fault is { } fault)
            return new BankTransferResult(false, null, fault, "Password expired — change it in the bank's portal", IsRetryable: true);
        if (RejectForGood)
            return new BankTransferResult(false, null, "BANK_REJECTED", "Beneficiary account is closed", IsRetryable: false);

        if (!_orders.TryGetValue(request.IdempotencyKey, out var o))
        {
            o = new Order { Id = $"ord{++_n}" };
            if (Async) o.Status = BankTransferStatus.Pending;
            else if (Funded) o.Status = BankTransferStatus.Completed;
            else { o.Status = BankTransferStatus.Failed; o.Code = BankErrorCodes.InsufficientFunds; }
            _orders[request.IdempotencyKey] = o;
        }
        if (LoseResponse) throw new HttpRequestException("response lost after the order was accepted");
        return o.Status switch
        {
            BankTransferStatus.Completed => new BankTransferResult(true, o.Id, null, null),
            BankTransferStatus.Pending => new BankTransferResult(true, o.Id, null, null, IsPending: true),
            _ => new BankTransferResult(false, o.Id, o.Code ?? "BANK_REPORTED_FAILED", "Insufficient funds on account",
                IsRetryable: o.Code == BankErrorCodes.InsufficientFunds),
        };
    }

    public Task<BankTransferOutcome> GetTransferOutcomeAsync(BankAccountContext source, string transferId, CancellationToken ct = default)
    {
        Calls++;
        var o = _orders.Values.First(x => x.Id == transferId);
        if (Reports is { } reported) return Task.FromResult(new BankTransferOutcome(reported, null, null));
        if (o.Status == BankTransferStatus.Pending)
        {
            if (Funded) o.Status = BankTransferStatus.Completed;
            else { o.Status = BankTransferStatus.Failed; o.Code = BankErrorCodes.InsufficientFunds; }
        }
        return Task.FromResult(new BankTransferOutcome(o.Status, o.Code, o.Code is null ? null : "Insufficient funds on account"));
    }

    public async Task<BankTransferStatus> GetTransferStatusAsync(BankAccountContext source, string transferId, CancellationToken ct = default) =>
        (await GetTransferOutcomeAsync(source, transferId, ct)).Status;

    public async Task<BankTransferLookup?> FindTransferByIdempotencyKeyAsync(BankAccountContext source, string idempotencyKey, CancellationToken ct = default)
    {
        LookedUp.Add(idempotencyKey);
        if (Unreachable || LookupFails) throw new HttpRequestException("connection reset by peer");
        var answer = _orders.TryGetValue(idempotencyKey, out var o) ? new BankTransferLookup(o.Id, Reports ?? o.Status, 0m) : null;
        if (AfterLookup is { } hook)
        {
            AfterLookup = null;
            await hook(idempotencyKey);
        }
        return answer;
    }

    public Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(BankAccountContext source, DateTime from, DateTime to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankTransferRecord>>(Array.Empty<BankTransferRecord>());

    public Task<decimal?> GetBalanceAsync(BankAccountContext source, CancellationToken ct = default) =>
        Task.FromResult(Balance);
}
