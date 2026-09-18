using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Banks.Tbc;
using PayTaxi.Infrastructure.Adapters.Yandex;

namespace PayTaxi.Api.Middleware;

/// <summary>
/// Turns upstream-integration failures (Yandex Fleet, TBC) into clean, non-500 API answers so
/// the admin console and driver app can show "the external service is unavailable" instead of
/// a generic crash. Business exceptions from controllers are handled where they occur.
///   502 upstream_rejected  — the external system answered with a definitive error (bad credentials, validation…)
///   503 upstream_unavailable — the external system is down / rate-limiting / timing out
/// </summary>
public class IntegrationErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IntegrationErrorMiddleware> _log;

    public IntegrationErrorMiddleware(RequestDelegate next, ILogger<IntegrationErrorMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && Map(ex) is { } mapped)
        {
            _log.LogWarning(ex, "Upstream integration failure on {Path}: {Provider} {Code}", ctx.Request.Path, mapped.provider, mapped.code);
            ctx.Response.StatusCode = mapped.status;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = mapped.status == 503 ? "upstream_unavailable" : "upstream_rejected",
                provider = mapped.provider,
                code = mapped.code,
                message = ex.Message,
            });
        }
    }

    private static (int status, string provider, string? code)? Map(Exception ex) => ex switch
    {
        YandexTransientException => (503, "yandex", "transient"),
        YandexReadOnlyModeException => (503, "yandex", "read_only_mode"),
        YandexApiException y => (502, "yandex", y.Code),
        TbcFaultException t => (502, "tbc", t.FaultCode),
        TbcProtocolException => (503, "tbc", "protocol"),
        TbcConfigurationException => (502, "tbc", "not_configured"),
        _ => null,
    };
}
