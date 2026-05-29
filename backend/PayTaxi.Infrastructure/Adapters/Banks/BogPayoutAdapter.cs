using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks;

// Stub — implement when BOG sandbox credentials are available
public class BogPayoutAdapter : IBankPayoutAdapter
{
    public string BankType => "BOG";

    public Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("BOG payout adapter not yet implemented");

    public Task<BankTransferStatus> GetTransferStatusAsync(string transferId, CancellationToken ct = default)
        => throw new NotImplementedException("BOG payout adapter not yet implemented");

    public Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(
        Guid parkId, DateTime from, DateTime to, CancellationToken ct = default)
        => throw new NotImplementedException("BOG payout adapter not yet implemented");
}
