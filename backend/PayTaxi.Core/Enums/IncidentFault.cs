namespace PayTaxi.Core.Enums;

/// <summary>
/// Whose fault an incident is — what decides whether it counts against the platform owner
/// (Annex 1 §3, §5, §7). Persisted as snake_case text with a Postgres check constraint.
/// </summary>
public enum IncidentFault
{
    /// <summary>The platform owner's (Swich's) fault — the only kind that counts towards §5 and §7.</summary>
    Platform,

    /// <summary>
    /// Outside the owner's control (Annex 1 §3): the bank API, Yandex Fleet, external infrastructure.
    /// Not an owner breach; the resolution target covers only the platform-side remainder and runs from
    /// the moment the external service is back.
    /// </summary>
    External,

    /// <summary>
    /// The park's side, e.g. not enough money on its account or no payout account. Annex 1 §3.1 lists
    /// "insufficient funds on the taxi park's account" among the external causes, so it is treated like
    /// <see cref="External"/> by the SLA clock.
    /// </summary>
    Park,

    /// <summary>Not established yet. Counts as neither; the owner is expected to set it once the cause is found.</summary>
    Unknown,
}
