namespace PayTaxi.Core.Entities;

public class ApiAuditLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Guid? ParkId { get; set; }
    public Guid? ActorId { get; set; }
    public string ActorType { get; set; } = default!; // "Driver" | "Manager" | "System"
    public string ApiProvider { get; set; } = default!; // "YandexFleet" | "BOG" | "TBC"
    public string Endpoint { get; set; } = default!;
    public string Method { get; set; } = default!;
    public string? RequestParamsHash { get; set; } // SHA-256 of params, never raw
    public int ResponseCode { get; set; }
    public bool Success { get; set; }
    public long DurationMs { get; set; }
    public string? CorrelationId { get; set; }
}
