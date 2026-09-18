using PayTaxi.Core.Entities;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Domain;

public class SettlementShareTests
{
    private static Cashout Fee(decimal fee) => new() { Amount = 10m + fee, Fee = fee };

    [Fact]
    public void Network_park_is_flat_fifty_percent()
    {
        var park = new Park { SwichSharePercent = 50m };
        var split = SettlementService.ComputeShares(park, new[] { Fee(0.5m), Fee(0.5m), Fee(0.5m) }, cumulativeBefore: 14m);
        Assert.Equal(1.50m, split.FeeTotal);
        Assert.Equal(0m, split.Phase1Fees);
        Assert.Equal(1.50m, split.Phase2Fees);
        Assert.Equal(0.75m, split.SwichShare);
    }

    [Fact]
    public void Levan_park_takes_everything_under_the_cap()
    {
        var park = new Park { SwichSharePercent = 50m, Phase1SharePercent = 100m, Phase1CapGel = 20_000m };
        var split = SettlementService.ComputeShares(park, new[] { Fee(0.5m), Fee(0.5m), Fee(0.5m), Fee(0.5m) }, cumulativeBefore: 10m);
        Assert.Equal(2.00m, split.FeeTotal);
        Assert.Equal(2.00m, split.Phase1Fees);
        Assert.Equal(0m, split.Phase2Fees);
        Assert.Equal(2.00m, split.SwichShare);
    }

    [Fact]
    public void Crossover_day_splits_exactly_at_the_cap()
    {
        // cumulative 15.5, cap 16.7 → 1.2 of the 1.5 fees at 100%, 0.3 at 50% = 1.35
        var park = new Park { SwichSharePercent = 50m, Phase1SharePercent = 100m, Phase1CapGel = 16.7m };
        var split = SettlementService.ComputeShares(park, new[] { Fee(0.5m), Fee(0.5m), Fee(0.5m) }, cumulativeBefore: 15.5m);
        Assert.Equal(1.20m, split.Phase1Fees);
        Assert.Equal(0.30m, split.Phase2Fees);
        Assert.Equal(1.35m, split.SwichShare);
    }

    [Fact]
    public void A_single_cashout_straddling_the_cap_is_split_inside_it()
    {
        var park = new Park { SwichSharePercent = 50m, Phase1SharePercent = 100m, Phase1CapGel = 100.2m };
        var split = SettlementService.ComputeShares(park, new[] { Fee(0.5m) }, cumulativeBefore: 100m);
        Assert.Equal(0.20m, split.Phase1Fees);
        Assert.Equal(0.30m, split.Phase2Fees);
        Assert.Equal(0.35m, split.SwichShare);
    }

    [Fact]
    public void After_the_cap_only_the_steady_share_applies()
    {
        var park = new Park { SwichSharePercent = 50m, Phase1SharePercent = 100m, Phase1CapGel = 20_000m };
        var split = SettlementService.ComputeShares(park, new[] { Fee(0.5m), Fee(0.5m) }, cumulativeBefore: 20_000m);
        Assert.Equal(0m, split.Phase1Fees);
        Assert.Equal(0.50m, split.SwichShare);
    }
}
