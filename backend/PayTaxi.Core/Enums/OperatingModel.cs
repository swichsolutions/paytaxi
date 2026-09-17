namespace PayTaxi.Core.Enums;

/// <summary>
/// How money flows from a park to its drivers — see PAYTAXI-CONTEXT.md §3 and
/// CLAUDE.md "Business Model and Multi-Tenancy".
/// Persisted as snake_case text in Postgres with a check constraint, not a Postgres enum,
/// so we can evolve the set without ALTER TYPE migrations.
/// </summary>
public enum OperatingModel
{
    /// <summary>
    /// LAUNCH MODEL. The park pays drivers from its OWN bank account using its own bank
    /// API credentials (TBC Business Integration Service). PayTaxi orchestrates the
    /// transfer but never holds or has discretionary control over the money, so no
    /// PSP/NBG licence is needed. The 0.50 GEL fee never moves — it stays in the park's
    /// account and is settled to Swich by a nightly aggregated transfer.
    /// </summary>
    ModelA,

    /// <summary>
    /// Legacy: commercial-agent model with a PayTaxi-side authorization limit. Retired in
    /// September 2026 in favour of Model A. Kept so historical rows still deserialise.
    /// </summary>
    ModelA5,

    /// <summary>
    /// PSP-licensed fintech — PayTaxi advances its own money. Requires a licence from the
    /// National Bank of Georgia. Explicitly NOT in scope.
    /// </summary>
    ModelB,
}
