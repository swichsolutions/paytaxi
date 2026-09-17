using Microsoft.Extensions.Logging;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The one safe way to push a transfer through a bank adapter: send, and if the call
/// throws (timeout / dropped connection) ask the bank for our document id before
/// deciding anything. Never blindly re-fires. Shared by the settlement service; the
/// cashout saga carries the same logic inline because it interleaves state changes.
/// </summary>
public static class BankSendHelper
{
    public static async Task<BankTransferResult> SendWithLookupAsync(
        IBankPayoutAdapter bank, BankTransferRequest request, ILogger log, CancellationToken ct)
    {
        try
        {
            return await bank.SendPayoutAsync(request, ct);
        }
        catch (NotImplementedException ex)
        {
            return new BankTransferResult(false, null, "BANK_ADAPTER_MISSING", ex.Message, IsRetryable: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Bank call threw for document {Key} — checking by document id", request.IdempotencyKey);

            BankTransferLookup? lookup = null;
            try { lookup = await bank.FindTransferByIdempotencyKeyAsync(request.Source, request.IdempotencyKey, ct); }
            catch (Exception lookupEx)
            {
                log.LogWarning(lookupEx, "Status lookup also failed for document {Key}", request.IdempotencyKey);
            }

            if (lookup is not null && lookup.Status is BankTransferStatus.Completed or BankTransferStatus.Pending)
            {
                log.LogInformation("Bank confirms transfer {TransferId} for document {Key} despite the lost response",
                    lookup.TransferId, request.IdempotencyKey);
                return new BankTransferResult(true, lookup.TransferId, null, null);
            }
            if (lookup is not null && lookup.Status == BankTransferStatus.Failed)
                return new BankTransferResult(false, lookup.TransferId, "BANK_REPORTED_FAILED",
                    "Bank reports the transfer failed", IsRetryable: true);

            return new BankTransferResult(false, null, "BANK_EXCEPTION", ex.Message, IsRetryable: true);
        }
    }
}
