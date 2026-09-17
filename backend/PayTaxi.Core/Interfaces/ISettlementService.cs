using PayTaxi.Core.Entities;

namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Nightly park → Swich fee-share settlement (PAYTAXI-CONTEXT.md §4/§5).
/// All operations are idempotent per (park, settlement date): re-running a night
/// never creates a second settlement for the same cashouts.
/// </summary>
public interface ISettlementService
{
    /// <summary>
    /// Run the settlement for every active park for <paramref name="settlementDate"/>
    /// (a local Tbilisi calendar day): first retries earlier Failed settlements, then
    /// creates + executes the new day's settlement per park.
    /// </summary>
    Task<SettlementRunSummary> RunDueAsync(DateOnly settlementDate, string initiatedBy, CancellationToken ct = default);

    /// <summary>
    /// Create (if needed) and execute the settlement for one park and day. Returns null
    /// when the park has no unsettled completed cashouts for the period.
    /// </summary>
    Task<Settlement?> RunForParkAsync(Guid parkId, DateOnly settlementDate, string initiatedBy, CancellationToken ct = default);

    /// <summary>Re-execute the transfer of a Failed settlement (same cashout set, same amount).</summary>
    Task<Settlement> RetryAsync(Guid settlementId, string initiatedBy, CancellationToken ct = default);
}

public record SettlementRunSummary(
    DateOnly SettlementDate,
    int ParksProcessed,
    int Created,
    int Completed,
    int Failed,
    int Retried,
    int Skipped);
