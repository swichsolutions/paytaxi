using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Monthly park → Swich settlement under the licence agreement (§2.2–2.5, Annex 3 §3).
///
/// A licensee's parks are settled together: one <see cref="MonthlyStatement"/> per licensee per month,
/// collected from each park pro rata to its fees. Parks Swich connected directly are settled monthly
/// on their own, with their own split. Everything is idempotent: a statement exists at most once per
/// (licensee, month), a direct park's row at most once per (park, month), every transfer carries a
/// deterministic document id — re-running never creates a second settlement or a second transfer.
/// </summary>
public interface ISettlementService
{
    /// <summary>
    /// The nightly tick: recover interrupted transfers, poll ones pending at the bank, retry failed
    /// ones (until they succeed), and — from the configured run day of the month on — settle the
    /// PREVIOUS month for every licensee and every direct park that has not been settled yet.
    /// </summary>
    Task<SettlementRunSummary> RunDueAsync(DateOnly todayLocal, string initiatedBy, CancellationToken ct = default);

    /// <summary>
    /// Settle one month now (Swich's "Settle now"): every licensee and direct park, or only the one
    /// given. Existing statements/rows are not recomputed; their unfinished transfers are executed.
    /// Throws <see cref="SettlementRejectedException"/> when the month cannot be settled (not closed,
    /// before the launch date, a later month already settled, …).
    /// </summary>
    Task<MonthRunResult> RunMonthAsync(DateOnly month, string initiatedBy, Guid? licenseeId = null, Guid? parkId = null, CancellationToken ct = default);

    /// <summary>
    /// What the licensee's statement for <paramref name="month"/> is (if computed) or would be if it
    /// were computed now — any month, including the running one. Nothing is persisted.
    /// </summary>
    Task<StatementPreview> PreviewLicenseeMonthAsync(Guid licenseeId, DateOnly month, CancellationToken ct = default);

    /// <summary>A direct park's monthly settlement as it would be computed now; null when there is nothing to settle.</summary>
    Task<DirectParkPreview?> PreviewDirectParkMonthAsync(Guid parkId, DateOnly month, CancellationToken ct = default);

    /// <summary>
    /// Re-execute the transfer of a Failed park settlement (same cashout set, same amount, same document id). Refused
    /// (<see cref="SettlementRejectionCodes.AuthorisationRevoked"/>) for a transfer the park's revoked authorisation stopped.
    /// </summary>
    Task<Settlement> RetryAsync(Guid settlementId, string initiatedBy, CancellationToken ct = default);

    /// <summary>
    /// Swich records that a FAILED park transfer was paid outside the platform — by the licensee, who is liable when
    /// a park's account cannot cover its part (§2.5, §4.1.2), or by the park itself — for the full amount. The row
    /// becomes SettledExternally: never retried, so the park's account is never debited for it afterwards; the
    /// payment is booked in the ledger and the §4.1.2 overdue count stops at <see cref="ExternalPayment.PaidOn"/>.
    /// Refused (<see cref="SettlementRejectedException"/>) while the park's bank may still execute the transfer.
    /// </summary>
    Task<Settlement> SettleExternallyAsync(Guid settlementId, ExternalPayment payment, string recordedBy, CancellationToken ct = default);
}

/// <summary>A payment made outside the platform: who paid ("licensee" | "park"), the amount (must be the full share), when, and its bank reference.</summary>
public record ExternalPayment(string? Payer, decimal Amount, DateOnly PaidOn, string? Reference, string? Note);

/// <summary>A month that cannot be settled (yet), with a stable machine code for the API and the console.</summary>
public sealed class SettlementRejectedException : InvalidOperationException
{
    public string Code { get; }
    public SettlementRejectedException(string code, string message) : base(message) => Code = code;
}

/// <summary>Stable codes of <see cref="SettlementRejectedException"/>; the console translates them.</summary>
public static class SettlementRejectionCodes
{
    /// <summary>The month has not ended yet (Tbilisi time).</summary>
    public const string MonthNotClosed = "month_not_closed";
    /// <summary>The licensee has no launch date — the agreement's clock has not started.</summary>
    public const string LaunchDateNotSet = "launch_date_not_set";
    /// <summary>The month lies before the licensee's launch month.</summary>
    public const string BeforeLaunch = "before_launch";
    /// <summary>A later month is already settled; this month's fees and costs rolled into it.</summary>
    public const string LaterStatementExists = "later_statement_exists";
    /// <summary>A direct-park run was asked for a park that belongs to a licensee.</summary>
    public const string ParkHasLicensee = "park_has_licensee";
    public const string LicenseeNotFound = "licensee_not_found";
    public const string ParkNotFound = "park_not_found";
    /// <summary>
    /// Fees or costs of the month were claimed or edited while the statement was being written, again and again — nothing
    /// was written. Also: a transfer attempt (or another record) touched a failed transfer while its outside payment was
    /// being recorded — nothing was recorded.
    /// </summary>
    public const string ConcurrentChange = "concurrent_change";

    // Recording a payment made outside the platform.
    public const string SettlementNotFound = "settlement_not_found";
    /// <summary>Only a Failed transfer can be closed by an outside payment.</summary>
    public const string NotFailed = "not_failed";
    /// <summary>The payment must be the transfer's full amount.</summary>
    public const string AmountMismatch = "amount_mismatch";
    /// <summary>The park's bank still holds a live order for the transfer — it may yet be paid from the park's account.</summary>
    public const string TransferAtBank = "transfer_at_bank";
    /// <summary>The park's bank could not be asked about the transfer's document.</summary>
    public const string BankLookupFailed = "bank_lookup_failed";
    public const string InvalidPayer = "invalid_payer";
    /// <summary>
    /// Retry of a transfer the park's revoked authorisation stopped (Annex 3 §7): it is never collected from the park's
    /// account — it is closed by recording the payment made outside the platform.
    /// </summary>
    public const string AuthorisationRevoked = "authorisation_revoked";
    public const string InvalidPaidOn = "invalid_paid_on";
    public const string ReferenceRequired = "reference_required";
    public const string NoteTooLong = "note_too_long";
}

public record SettlementRunSummary(
    DateOnly RunDate,
    // The month settled by this run, or null before the run day.
    DateOnly? MonthSettled,
    int StatementsCreated,
    int DirectSettlementsCreated,
    int TransfersCompleted,
    int TransfersFailed,
    int Retried,
    int Skipped);

public record MonthRunResult(
    DateOnly Month,
    IReadOnlyList<Guid> StatementIds,
    IReadOnlyList<Guid> DirectSettlementIds,
    IReadOnlyList<MonthRunSkip> Skipped);

/// <summary>A licensee or direct park the run left out, and why (a <see cref="SettlementRejectionCodes"/> code or "nothing_to_settle").</summary>
public record MonthRunSkip(string Kind, Guid Id, string Name, string Code);

/// <summary>One park's part of a licensee statement.</summary>
public record ParkAllocation(
    Guid ParkId,
    string ParkName,
    int CashoutCount,
    decimal Fees,
    // Fees × φ: the part of the park's fees that fell under the barrier.
    decimal BarrierFees,
    decimal SwichShare);

/// <summary>A licensee statement computed live (not persisted) — same figures a run would freeze.</summary>
public record StatementPreview(
    Guid LicenseeId,
    DateOnly Month,
    DateTime PeriodFromUtc,
    DateTime PeriodToUtc,
    int CashoutCount,
    decimal FeeIncome,
    decimal LicenseeCosts,
    decimal SwichCosts,
    decimal CarryIn,
    decimal BarrierGel,
    decimal SwichSharePercent,
    decimal BarrierBefore,
    // The barrier was already reached by an earlier statement — §2.2's split applies whatever R is now (§2.3).
    bool BarrierFilledBefore,
    StatementPhase Phase,
    decimal BarrierFraction,
    decimal BarrierEntitlement,
    decimal NormalEntitlement,
    decimal Entitlement,
    decimal Due,
    decimal CarryOut,
    IReadOnlyList<ParkAllocation> Parks,
    IReadOnlyList<Guid> CostIds,
    // Set when a run for this month would be refused now (a SettlementRejectionCodes code).
    string? BlockedReason,
    // The stored statement when the month is already settled (then the figures above are its frozen ones).
    Guid? StatementId);

/// <summary>A direct park's monthly settlement computed live.</summary>
public record DirectParkPreview(
    Guid ParkId,
    string ParkName,
    DateOnly Month,
    int CashoutCount,
    decimal FeeTotal,
    decimal Phase1Fees,
    decimal Phase2Fees,
    decimal SwichShare,
    decimal CumulativeFeesBefore,
    string? BlockedReason);
