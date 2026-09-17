using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks;

/// <summary>
/// TBC Business Integration Service adapter — the launch rail.
/// Docs: https://developers.tbcbank.ge (sandbox test-api.tbcbank.ge, prod api.tbcbank.ge).
/// Auth: digital certificate issued to the PARK via its business internet bank;
/// the credentials JSON on <c>ParkBankAccount</c> carries client id/secret + cert reference.
///
/// STUB — the real HTTP implementation lands once sandbox access is granted at the
/// TBC branch visit. Keep retry / audit / queueing OUT of here: the saga and the
/// payout queue worker own those.
/// </summary>
public class TbcPayoutAdapter : IBankPayoutAdapter
{
    private const string Msg = "TBC payout adapter not yet implemented — awaiting sandbox credentials";

    public string BankType => "TBC";

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
