using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using PayTaxi.Infrastructure.Adapters.Sms;

namespace PayTaxi.Tests.Sms;

/// <summary>
/// Scripted smsoffice.ge: records every request (the form fields of a send, the query of a balance read) and answers
/// with canned replies in the documented JSON shape. Without a script it accepts everything (ErrorCode 0). No test ever
/// reaches the real gateway: the sender gets this handler through <see cref="SingleClientFactory"/> or the named
/// client's primary handler.
/// </summary>
public sealed class FakeSmsOfficeHandler : HttpMessageHandler
{
    public sealed record Sent(Uri Uri, string? ContentType, Dictionary<string, string> Form);

    public List<Sent> Sends { get; } = new();
    public List<Uri> BalanceReads { get; } = new();

    private readonly Queue<Func<HttpRequestMessage, (HttpStatusCode, string)>> _sendScript = new();
    private readonly Queue<Func<HttpRequestMessage, (HttpStatusCode, string)>> _balanceScript = new();

    public static string Reply(int errorCode, string message = "") =>
        $"{{\"Success\": {(errorCode == 0 ? "true" : "false")}, \"Message\": \"{message}\", \"Output\": null, \"ErrorCode\": {errorCode}}}";

    /// <summary>The next send is answered with this smsoffice error code (0 = accepted).</summary>
    public FakeSmsOfficeHandler Answer(int errorCode, string message = "") { _sendScript.Enqueue(_ => (HttpStatusCode.OK, Reply(errorCode, message))); return this; }
    public FakeSmsOfficeHandler Http(HttpStatusCode status, string body = "") { _sendScript.Enqueue(_ => (status, body)); return this; }
    public FakeSmsOfficeHandler Body(string body) { _sendScript.Enqueue(_ => (HttpStatusCode.OK, body)); return this; }
    public FakeSmsOfficeHandler Throw(Exception ex) { _sendScript.Enqueue(_ => throw ex); return this; }
    /// <summary>The next balance read answers this body (a bare number or JSON).</summary>
    public FakeSmsOfficeHandler Balance(string body, HttpStatusCode status = HttpStatusCode.OK) { _balanceScript.Enqueue(_ => (status, body)); return this; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/getBalance", StringComparison.Ordinal))
        {
            BalanceReads.Add(request.RequestUri);
            var (bs, bb) = _balanceScript.Count > 0 ? _balanceScript.Dequeue()(request) : (HttpStatusCode.OK, "1000");
            return new HttpResponseMessage(bs) { Content = new StringContent(bb, Encoding.UTF8, "application/json") };
        }

        var form = new Dictionary<string, string>();
        if (request.Content is not null)
        {
            var text = await request.Content.ReadAsStringAsync(ct);
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
                var v = eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]);
                form[k] = v;
            }
        }
        Sends.Add(new Sent(request.RequestUri, request.Content?.Headers.ContentType?.MediaType, form));

        var (status, body) = _sendScript.Count > 0 ? _sendScript.Dequeue()(request) : (HttpStatusCode.OK, Reply(0));
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>An IHttpClientFactory that hands out clients over one handler (what the named client does in the API).</summary>
public sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly Uri? _baseAddress;
    public SingleClientFactory(HttpMessageHandler handler, string? baseAddress = "https://smsoffice.ge/")
    {
        _handler = handler;
        _baseAddress = baseAddress is null ? null : new Uri(baseAddress);
    }
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { BaseAddress = _baseAddress, Timeout = TimeSpan.FromSeconds(10) };
}

/// <summary>Records the alerts the sender raises and clears.</summary>
public sealed class RecordingAlerts : ISmsGatewayAlerts
{
    public List<(string Code, string Detail)> Raised { get; } = new();
    public List<IReadOnlyCollection<string>> Cleared { get; } = new();
    public Task RaiseAsync(string code, string detail, CancellationToken ct = default) { Raised.Add((code, detail)); return Task.CompletedTask; }
    public Task ClearAsync(IReadOnlyCollection<string> codes, string resolvedText, CancellationToken ct = default) { Cleared.Add(codes); return Task.CompletedTask; }
}

/// <summary>Keeps every rendered log line, so a test can say what the log holds — and what it must not.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception), exception));
    public string All => string.Join("\n", Lines.Select(l => l.Message));
}
