namespace PayTaxi.Core.Enums;

/// <summary>
/// How money flows from a park to its drivers — see CLAUDE.md "Business Model and Multi-Tenancy".
/// Persisted as snake_case text in Postgres with a check constraint, not a Postgres enum,
/// so we can evolve the set without ALTER TYPE migrations.
/// </summary>
public enum OperatingModel
{
    /// <summary>
    /// Pure SaaS — park uses its OWN bank mass-payout API. PayTaxi never touches the money,
    /// never has authorization over the park's funds. Slowest onboarding (4–8 weeks per park
    /// for bank API agreement), but cleanest legally for PayTaxi.
    /// </summary>
    ModelA,

    /// <summary>
    /// Commercial agent — PayTaxi initiates transfers from the park's bank account under a
    /// pre-signed commercial agent authorization (this is what paypro does). Each cashout
    /// debits the park's <see cref="Entities.Park.AuthorizationLimit"/> on PayTaxi.
    /// Primary target model. Onboarding: 1–3 weeks per park.
    /// </summary>
    ModelA5,

    /// <summary>
    /// PSP-licensed fintech — PayTaxi advances its own money, reimbursed by parks later.
    /// Requires a PSP license from the National Bank of Georgia. Documented for completeness
    /// but explicitly NOT in scope for the current build.
    /// </summary>
    ModelB,
}
