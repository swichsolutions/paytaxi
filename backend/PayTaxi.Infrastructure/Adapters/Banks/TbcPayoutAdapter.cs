using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks;

// Stub — implement when TBC sandbox credentials are available
public class TbcPayoutAdapter : IBankPayoutAdapter
{
    public string BankType => "TBC";

    public Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("TBC payout adapter not yet implemented");

    public Task<BankTransferStatus> GetTransferStatusAsync(string transferId, CancellationToken ct = default)
        => throw new NotImplementedException("TBC payout adapter not yet implemented");
}
