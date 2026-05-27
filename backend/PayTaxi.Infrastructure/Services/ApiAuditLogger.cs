using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Writes audit records via an isolated DI scope per call.
///
/// We deliberately don't share the calling request's DbContext: audit writes
/// are fired-and-forgotten from ResilientYandexFleetClient, which would race
/// the controller's DbContext if they shared one (DbContext is not thread-safe).
/// Owning the scope here lets us run as a singleton and stay fail-soft.
/// </summary>
public class ApiAuditLogger : IApiAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApiAuditLogger> _log;

    public ApiAuditLogger(IServiceScopeFactory scopeFactory, ILogger<ApiAuditLogger> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task LogAsync(ApiCallRecord record, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.ApiAuditLogs.Add(new ApiAuditLog
            {
                Timestamp = DateTime.UtcNow,
                ParkId = record.ParkId,
                ActorId = record.ActorId,
                ActorType = record.ActorType,
                ApiProvider = record.ApiProvider,
                Endpoint = record.Endpoint,
                Method = record.Method,
                RequestParamsHash = record.RequestParamsHash,
                ResponseCode = record.ResponseCode,
                Success = record.Success,
                DurationMs = record.DurationMs,
                CorrelationId = record.CorrelationId,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Audit log write failed for {Provider} {Endpoint} ({CorrelationId})",
                record.ApiProvider, record.Endpoint, record.CorrelationId);
        }
    }
}
