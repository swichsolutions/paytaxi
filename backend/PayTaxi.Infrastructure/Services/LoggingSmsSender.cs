using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The SMS sender without a gateway (Sms:Provider = log, or smsoffice without a key): nothing leaves the server, and the
/// sign-in endpoint shows the code in its response instead (<see cref="ISmsSender.DeliversForReal"/> is false).
/// Development logs the recipient and the text and keeps the last messages in memory, so a developer — and the tests —
/// can read what a driver would have received. Elsewhere only the fact is logged, with the purpose and the reference: a
/// phone number and a message about someone's bank account have no place in a log that outlives the retention periods.
/// </summary>
public sealed class LoggingSmsSender : ISmsSender
{
    private const int Kept = 200;
    private readonly ILogger<LoggingSmsSender> _log;
    private readonly bool _development;
    private readonly ConcurrentQueue<SentSms> _recent = new();

    public LoggingSmsSender(ILogger<LoggingSmsSender> log, IHostEnvironment env)
    {
        _log = log;
        _development = env.IsDevelopment();
    }

    public bool DeliversForReal => false;

    /// <summary>Development only (empty elsewhere): the messages "sent" since start, oldest first, at most the last 200.</summary>
    public IReadOnlyList<SentSms> Recent => _recent.ToArray();

    public Task SendAsync(string phoneE164, string text, string purpose, string reference, CancellationToken ct = default)
    {
        if (_development)
        {
            _recent.Enqueue(new SentSms(phoneE164, text, purpose, reference, DateTime.UtcNow));
            while (_recent.Count > Kept && _recent.TryDequeue(out _)) { }
            _log.LogInformation("Dev SMS ({Purpose}, {Reference}) to {Phone}: {Text}", purpose, reference, phoneE164, text);
        }
        else
        {
            _log.LogWarning("No SMS gateway is configured — {Purpose} SMS for {Reference} not sent", purpose, reference);
        }
        return Task.CompletedTask;
    }
}

public sealed record SentSms(string Phone, string Text, string Purpose, string Reference, DateTime At);
