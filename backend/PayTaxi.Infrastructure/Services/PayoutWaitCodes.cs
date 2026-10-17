using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>What a queued payout is waiting for, read from its failure code ("CODE: message").</summary>
public enum PayoutWaitCause
{
    /// <summary>The park's account cannot cover it: a top-up releases it.</summary>
    Funds,

    /// <summary>The park has no active payout account at the driver's bank.</summary>
    NoAccount,

    /// <summary>The bank connection needs setting up: our adapter is missing, or credentials / the bank user.</summary>
    Setup,

    /// <summary>The bank never said what became of the last order (it may have been paid).</summary>
    Unresolved,

    /// <summary>The bank itself: its gateway is down or it answers with its own fault.</summary>
    Bank,

    /// <summary>Anything else, including a refusal without a reason.</summary>
    Other,

    /// <summary>
    /// The park revoked the platform's authorisation to pay from its account (Annex 3 §7): it is never sent; a person
    /// cancels it and the amount goes back to the driver's Yandex balance.
    /// </summary>
    Revoked,
}

/// <summary>
/// The one table of queued-payout failure codes. The incident monitor takes an incident's fault from it,
/// and the console's <c>src/app/admin/shared/payout-wait.ts</c> (<c>waitCause</c>) mirrors it word for
/// word, so "Bank not answering" on a cashout and "External" on its incident never disagree.
///
/// Fault (Annex 1 §3.1 lists the causes outside the owner's control: the bank API, Yandex Fleet,
/// external infrastructure, money missing on the park's account):
///   - Funds / NoAccount / Revoked    → Park (a revocation is the park's own act, Annex 3 §7).
///   - Bank                           → External. Only codes that mean the bank itself failed. External
///     pauses the resolution clock, lets the monitor close the incident as "met" once the queue drains,
///     and never counts for §5 or the §7 trial release, so a wrong External costs the licensee.
///   - Setup, our adapter missing     → Platform. Building and supporting the bank integrations is Swich's
///     own obligation (agreement §1.2); a stub adapter is not the bank's outage.
///   - Setup, credentials / bank user → Unknown. Whether the park never handed them over or they were set up
///     wrong is for a person to establish.
///   - Unresolved, Other              → Unknown, for the same reason.
/// </summary>
public static class PayoutWaitCodes
{
    /// <summary>The adapter for the account's bank throws NotImplementedException (BOG today). Nothing reached a bank.</summary>
    public const string AdapterMissing = "BANK_ADAPTER_MISSING";

    /// <summary>Credentials we could not use, or the bank refused the user: someone has to fix the setup.</summary>
    private static readonly HashSet<string> CredentialCodes = new(StringComparer.Ordinal)
    {
        "TBC_NOT_CONFIGURED", "TBC_CREDENTIALS_MUST_BE_CHANGED", "TBC_USER_BLOCKED", "TBC_AUTHENTICATION_FAILED",
    };

    /// <summary>Generic codes that mean the bank's gateway is not answering (BANK_TRANSIENT_503 is the mock's).</summary>
    private static readonly HashSet<string> GatewayDownCodes = new(StringComparer.Ordinal)
    {
        "BANK_UNAVAILABLE", "BANK_TRANSIENT_503",
    };

    /// <summary>A bank's refusal of our data. It is never retried, so these rarely wait, but they are not an outage.</summary>
    private static readonly HashSet<string> RejectedDataCodes = new(StringComparer.Ordinal)
    {
        "TBC_VALIDATION_ERROR", "TBC_INCORRECT_INPUT_DATA",
    };

    /// <summary>
    /// The same rule as the cashout saga's "ambiguous" codes: our call threw and the lookup failed too, or
    /// the bank could not say which order a duplicate was.
    /// </summary>
    public static bool IsUnresolved(string code) =>
        code == "BANK_EXCEPTION" || code.EndsWith("_UNRESOLVED", StringComparison.Ordinal) || code.EndsWith("_UNKNOWN", StringComparison.Ordinal);

    public static PayoutWaitCause CauseOf(string? code)
    {
        if (string.IsNullOrEmpty(code)) return PayoutWaitCause.Other;
        if (code == ParkAuthorisation.RevokedCode) return PayoutWaitCause.Revoked;
        // INSUFFICIENT_PARK_BALANCE is the settlement engine's spelling of the same fact.
        if (code is BankErrorCodes.InsufficientFunds or "INSUFFICIENT_PARK_BALANCE") return PayoutWaitCause.Funds;
        if (code == BankErrorCodes.NoParkAccount) return PayoutWaitCause.NoAccount;
        if (IsUnresolved(code)) return PayoutWaitCause.Unresolved;
        if (code == AdapterMissing || CredentialCodes.Contains(code)) return PayoutWaitCause.Setup;
        if (GatewayDownCodes.Contains(code)) return PayoutWaitCause.Bank;
        // A bank-specific code is the bank's own fault answer (TBC_SERVICE_UNAVAILABLE…). Our generic BANK_
        // codes other than "gateway down" are not: BANK_FAILED / BANK_REPORTED_FAILED say an order failed, not why.
        if ((code.StartsWith("TBC_", StringComparison.Ordinal) || code.StartsWith("BOG_", StringComparison.Ordinal))
            && !RejectedDataCodes.Contains(code))
            return PayoutWaitCause.Bank;
        return PayoutWaitCause.Other;
    }

    public static IncidentFault FaultOf(string? code) => CauseOf(code) switch
    {
        PayoutWaitCause.Funds or PayoutWaitCause.NoAccount or PayoutWaitCause.Revoked => IncidentFault.Park,
        PayoutWaitCause.Bank => IncidentFault.External,
        PayoutWaitCause.Setup when code == AdapterMissing => IncidentFault.Platform,
        _ => IncidentFault.Unknown,
    };
}
