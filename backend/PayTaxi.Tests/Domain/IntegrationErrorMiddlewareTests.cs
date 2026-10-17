using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PayTaxi.Api.Middleware;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Yandex;
using Xunit;

namespace PayTaxi.Tests.Domain;

/// <summary>What an upstream failure tells the caller: a code always, the upstream's own words only to the console.</summary>
public class IntegrationErrorMiddlewareTests
{
    private static async Task<(int status, string body)> RunAsync(string path, Exception thrown)
    {
        var mw = new IntegrationErrorMiddleware(_ => throw thrown, NullLogger<IntegrationErrorMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData("/api/driver/me/rides")]
    [InlineData("/api/driver/me")]
    public async Task A_driver_gets_the_code_but_never_the_upstream_text(string path)
    {
        var (status, body) = await RunAsync(path, new YandexApiException(HttpStatusCode.BadRequest, "bad_request", "secret-detail from Yandex"));
        Assert.Equal(502, status);
        Assert.Contains("upstream_rejected", body);
        Assert.Contains("bad_request", body);
        Assert.DoesNotContain("secret-detail", body);

        (status, body) = await RunAsync(path, new YandexTransientException("Yandex Fleet unreachable (v2/parks/orders/list): No such host is known. (fleet-api.internal:443)"));
        Assert.Equal(503, status);
        Assert.DoesNotContain("fleet-api.internal", body);
    }

    [Fact]
    public async Task The_admin_console_still_gets_the_upstream_text_it_shows_park_staff()
    {
        // Park staff fix their own Yandex credentials from this text ("invalid API key"); it is their park's answer.
        var (_, body) = await RunAsync("/api/admin/parks/00000000-0000-0000-0000-000000000001/yandex/drivers",
            new YandexApiException(HttpStatusCode.Forbidden, "forbidden", "API key is not valid for this park"));
        Assert.Contains("API key is not valid", body);
    }
}
