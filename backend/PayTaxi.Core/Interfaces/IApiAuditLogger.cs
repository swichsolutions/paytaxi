namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Append-only audit logger for every external API call (Yandex Fleet, BOG, TBC, SMS).
/// Required by CLAUDE.md "Architecture Principles": audit everything money-related, never raw params.
/// </summary>
public interface IApiAuditLogger
{
    Task LogAsync(ApiCallRecord record, CancellationToken ct = default);
}

public record ApiCallRecord(
    string ApiProvider,
    string Endpoint,
    string Method,
    Guid? ParkId,
    Guid? ActorId,
    string ActorType,
    string? RequestParamsHash,
    int ResponseCode,
    bool Success,
    long DurationMs,
    string? CorrelationId);
