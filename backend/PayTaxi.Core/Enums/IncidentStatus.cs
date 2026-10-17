namespace PayTaxi.Core.Enums;

/// <summary>
/// Where an incident is in its life. Persisted as snake_case text with a Postgres check constraint.
///
///   Reported --acknowledge (Swich)--> Acknowledged --resolve--> Resolved
///       |                                                          |
///       +-----------------------resolve---------------------------+
///   Resolved --reopen (Swich)--> Reported / Acknowledged (whichever it was before)
/// </summary>
public enum IncidentStatus
{
    /// <summary>Logged (by a person or the monitor); the owner has not responded yet — the response clock runs.</summary>
    Reported,

    /// <summary>
    /// The owner confirmed it, started work and the licensee can see that in the log — the contractual
    /// "response" (Annex 1 §2.2). For a P1 the work then continues without pause until it is fixed.
    /// </summary>
    Acknowledged,

    /// <summary>Fixed (or, for an external cause, the queued requests have been executed).</summary>
    Resolved,
}
