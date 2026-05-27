namespace PayTaxi.Core.Enums;

/// <summary>
/// Lifecycle state of a park on the platform.
/// Persisted as snake_case text with a Postgres check constraint.
/// </summary>
public enum ParkStatus
{
    /// <summary>Onboarding in progress — park exists but cannot transact yet.</summary>
    Pending,

    /// <summary>Fully onboarded — drivers can log in, cashouts allowed.</summary>
    Active,

    /// <summary>Temporarily disabled (e.g., compliance review, payment dispute). No new cashouts; existing records preserved.</summary>
    Suspended,

    /// <summary>Contract terminated. Read-only access for audit purposes only.</summary>
    Terminated,
}
