using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks;

/// <summary>
/// Bank of Georgia Business Online API adapter — Phase 2 rail.
/// Docs: https://api.bog.ge/docs/en/bonline/introduction (OAuth2 + JWT, base api.businessonline.ge/api).
/// STUB until pricing/API access is agreed with BoG.
/// </summary>
public class BogPayoutAdapter : IBankPayoutAdapter
{
    private const string Msg = "BOG payout adapter not yet implemented — Phase 2";

    public string BankType => "BOG";

    public Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Msg);

    public Task<BankTransferStatus> GetTransferStatusAsync(BankAccountContext source, string transferId, CancellationToken ct = default)
        => throw new NotImplementedException(Msg);

    public Task<BankTransferLookup?> FindTransferByIdempotencyKeyAsync(BankAccountContext source, string idempotencyKey, CancellationToken ct = default)
        => throw new NotImplementedException(Msg);

    public Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(BankAccountContext source, DateTime from, DateTime to, CancellationToken ct = default)
        => throw new NotImplementedException(Msg);

    public Task<decimal?> GetBalanceAsync(BankAccountContext source, CancellationToken ct = default)
        => throw new NotImplementedException(Msg);
}
