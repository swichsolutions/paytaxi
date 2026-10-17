namespace PayTaxi.Core.Enums;

/// <summary>How an incident entered the log. Persisted as snake_case text with a Postgres check constraint.</summary>
public enum IncidentSource
{
    /// <summary>Reported by a person in the admin console (Swich or the licensee).</summary>
    Manual,

    /// <summary>Opened by the incident monitor (e.g. payouts stuck in a park's queue).</summary>
    Auto,
}
