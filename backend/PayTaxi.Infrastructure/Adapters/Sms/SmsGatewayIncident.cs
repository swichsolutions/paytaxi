using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Infrastructure.Adapters.Sms;

/// <summary>
/// The SMS gateway's operator alert, as one platform-wide auto incident (dedupe key <see cref="Key"/>) — the same record
/// the incident monitor opens for stuck payouts and a Yandex outage, so it reaches people the way everything else does:
/// the console's alert banner (AlertsController, <c>incident_open</c>, shown to Swich, the operator and every park
/// manager, since a platform-wide incident has no park) and the Annex 1 incident log. P2: drivers already signed in and
/// the payouts are not affected; a driver on a new device gets no code until it is fixed. Fault: the platform's — the
/// smsoffice account, its balance and its sender are Swich's to keep in order.
///
/// Opened once: the filtered unique index on DedupeKey refuses a second open incident for the key (two API instances
/// may both see error 20). A second cause while it is open is noted on it. Closed by the gateway itself the moment a
/// message is accepted again (<see cref="SmsGatewayCodes.ClearedBySend"/>) or the balance reads healthy again
/// (<see cref="SmsGatewayCodes.ClearedByBalance"/>) — only an incident still under this key: one a person moved to a park
/// (<see cref="IncidentMonitor.MovedKeyPrefix"/>) is theirs to resolve, like the monitor's. Never auto-acknowledged.
/// </summary>
public sealed class SmsGatewayIncident : ISmsGatewayAlerts
{
    public const string Key = "sms-gateway";
    public const string Title = "SMS gateway: sign-in codes are not going out";
    public const string LowBalanceTitle = "SMS gateway: balance running low";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SmsGatewayIncident> _log;

    // Clearing is called on every accepted message; it reads the database only while an incident may be open — after
    // a raise, and once after start (one left open by an earlier process).
    private volatile bool _mayBeOpen = true;

    public SmsGatewayIncident(IServiceScopeFactory scopeFactory, ILogger<SmsGatewayIncident> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task RaiseAsync(string code, string detail, CancellationToken ct = default)
    {
        _mayBeOpen = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var cause = $"{code}: {detail}";

            var open = await db.Incidents
                .Where(i => i.Status != IncidentStatus.Resolved && i.DedupeKey != null
                         && (i.DedupeKey == Key || (i.DedupeKey.StartsWith(IncidentMonitor.MovedKeyPrefix) && i.DedupeKey.EndsWith(":" + Key))))
                .OrderBy(i => i.Number)
                .FirstOrDefaultAsync(ct);
            if (open is not null)
            {
                // The same cause again: nothing new to record (every refused message would otherwise add a line).
                if (string.Equals(open.Cause, cause, StringComparison.Ordinal)) return;
                open.Cause = cause;
                if (open.DedupeKey == Key && code != SmsGatewayCodes.LowBalance) open.Title = Title;
                open.UpdatedAt = now;
                open.Updates.Add(new IncidentUpdate
                {
                    At = now, By = IncidentMonitor.SystemActor, Kind = "note", ParkId = open.ParkId,
                    Text = $"Now: {detail}",
                    Data = JsonSerializer.Serialize(new { code = "auto_sms_cause", reason = code, detail }),
                });
                await db.SaveChangesAsync(ct);
                return;
            }

            var lowBalance = code == SmsGatewayCodes.LowBalance;
            var incident = new Incident
            {
                Status = IncidentStatus.Reported,
                Title = lowBalance ? LowBalanceTitle : Title,
                Description = (lowBalance
                    ? $"The smsoffice.ge account is about to run out of messages: {detail}. Once it does, no sign-in code and no payout-account notice goes out. "
                    : $"smsoffice.ge refuses to send: {detail}. No sign-in code reaches a driver signing in on a new device until it is fixed, and no payout-account notice. ")
                    + "Drivers already signed in and the payouts are not affected. Fix: docs/DEPLOY.md, SMS (balance top-up at smsoffice.ge, the key in Sms:SmsOffice:Key, the sender's approval).",
                Cause = cause,
                Fault = IncidentFault.Platform,
                ParkId = null,
                StartedAt = now,
                ReportedAt = now,
                ReportedBy = IncidentMonitor.SystemActor,
                Source = IncidentSource.Auto,
                DedupeKey = Key,
            };
            incident.SetLevel(IncidentLevel.P2, now);
            incident.Updates.Add(new IncidentUpdate
            {
                At = now, By = IncidentMonitor.SystemActor, Kind = "reported", ParkId = null,
                Text = $"Opened automatically: {detail}.",
                Data = JsonSerializer.Serialize(new { code = "auto_sms_opened", reason = code, detail }),
            });
            db.Incidents.Add(incident);
            try
            {
                await db.SaveChangesAsync(ct);
                _log.LogError("Incident opened: SMS gateway — {Cause}", cause);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                _log.LogInformation("Incident for {Key} already opened elsewhere", Key);
            }
        }
        catch (Exception ex)
        {
            // The alert is a courtesy on top of the log line the sender already wrote; it must not fail the send.
            _log.LogError(ex, "SMS gateway alert ({Code}) could not be recorded as an incident", code);
        }
    }

    public async Task ClearAsync(IReadOnlyCollection<string> codes, string resolvedText, CancellationToken ct = default)
    {
        if (!_mayBeOpen) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var open = await db.Incidents.FirstOrDefaultAsync(i => i.DedupeKey == Key && i.Status != IncidentStatus.Resolved, ct);
            if (open is null)
            {
                _mayBeOpen = false;
                return;
            }
            var openCode = open.Cause is { } c && c.IndexOf(':') is var i && i > 0 ? c[..i] : open.Cause ?? "";
            if (!codes.Contains(openCode, StringComparer.Ordinal)) return;

            var now = DateTime.UtcNow;
            open.Status = IncidentStatus.Resolved;
            open.ResolvedAt = now;
            open.ResolvedBy = IncidentMonitor.SystemActor;
            open.UpdatedAt = now;
            open.Updates.Add(new IncidentUpdate
            {
                At = now, By = IncidentMonitor.SystemActor, Kind = "resolved", ParkId = open.ParkId,
                Text = resolvedText,
                Data = JsonSerializer.Serialize(new { code = "auto_sms_resolved", reason = openCode }),
            });
            await db.SaveChangesAsync(ct);
            _mayBeOpen = false;
            _log.LogInformation("Incident {Ref} resolved automatically (SMS gateway: {Reason})", open.Ref, openCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMS gateway incident could not be resolved; the next accepted message tries again");
        }
    }
}
