namespace PayTaxi.Core.Enums;

/// <summary>
/// Incident level per Annex 1 §2 (SLA) of the licence agreement. Set by the platform owner (Swich);
/// in a dispute the deciding fact is whether drivers can cash out.
/// Persisted as snake_case text ("p1") with a Postgres check constraint.
/// </summary>
public enum IncidentLevel
{
    /// <summary>Business stopped: cashouts fail for all or most drivers and there is no workaround.</summary>
    P1,

    /// <summary>Degraded: cashing out is still possible, or a temporary workaround exists.</summary>
    P2,

    /// <summary>Minor: visual or non-critical, the core flow is not affected.</summary>
    P3,
}
