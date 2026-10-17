using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Opens and closes incidents nobody had to type in, so the Annex 1 log does not depend on someone
/// noticing at 3 a.m. Three conditions, each with a dedupe key so a condition has at most one open incident:
///
///   1. <c>payout-queue:{parkId}</c> — a park has a payout Queued for longer than
///      <see cref="IncidentMonitorOptions.QueueAgeMinutes"/>: drivers were debited on Yandex but the bank
///      transfer is not going through. Impact from the oldest queued payout, fault from the most common
///      failure code (<see cref="PayoutWaitCodes"/>). The level follows the impact, whatever the cause
///      (Annex 1 §3.1): P1 only when cashouts fail for all or most of the park's drivers (§2.1), see
///      <see cref="LevelForImpact"/>; otherwise P2. A P2 the queue outgrows is raised to P1, unless a person
///      has set the level (it is the owner's call, §2.1); its P1 clocks start at the raise. Resolved when the
///      park has no Queued payout left.
///   2. <c>payout-queue-wide:{family}</c> — one external or platform cause (the same bank failing, or our
///      integration for one bank missing: <see cref="CauseFamily"/>) holds the payouts of at least
///      <see cref="IncidentMonitorOptions.P1MinParks"/> parks at once. Per park that would be a P2 each and never
///      a P1, though drivers fail platform-wide. So it is ONE platform-wide P1 with neutral text (no park named).
///      If one of those parks already has its incident, that incident carries on platform-wide (the one that has
///      been a P1 longest, else the first reported): one record of the cause, its response and P1 clocks kept, its
///      park-naming text rewritten. Every other park's own incident is closed as its duplicate, linked on both
///      timelines by entries only that park can read. While it is open, a park whose queue waits mainly for that
///      cause gets no incident of its own. Resolved when no payout anywhere waits for that cause. The park's side (money, a payout
///      account, a revocation) and causes nobody has established never aggregate: two parks short of money are
///      two facts, not one outage.
///   3. <c>yandex-outage</c> — Yandex Fleet calls fail with server/transport errors and none succeeds, either
///      anywhere or for P1MinParks parks at once (from the API audit log): P1, external, platform-wide.
///      Cashout requests are REFUSED meanwhile — the balance cannot be checked, so nothing is debited and
///      nothing waits — hence nothing to queue and no per-park incident to merge. Resolved once a call made
///      after the incident opened succeeds and the outage no longer holds.
///
/// Auto incidents are never auto-acknowledged: acknowledging is the owner's contractual response (§2.2)
/// and must be a person. Closing them automatically is fine — the condition is measurable.
///
/// Swich may move an auto incident to another scope (all parks, or another park). From then on the monitor leaves it
/// alone — it does not close, raise, rewrite or merge it — but it still HOLDS the condition it was opened for
/// (<see cref="MovedKey"/>): while that lasts no second incident opens for the same waiting payouts or the same outage.
/// A queue incident moved to all parks is the platform-wide record of its park's cause once that cause reaches other
/// parks (<see cref="ClaimForWideAsync"/>). When the condition ends, exactly when the monitor would have closed its own,
/// it lets go (<see cref="ReleasedKeyPrefix"/>): the incident waits, open, for a person; the next occurrence is new.
/// A reopened incident holds nothing — reopening is a person saying the monitor's view was not the whole story.
///
/// Every timeline entry carries <see cref="IncidentUpdate.Data"/> (a code and its figures) next to the
/// English text, so the console can show it in Georgian, and the incident's park at the time
/// (<see cref="IncidentUpdate.ParkId"/>), which decides which park managers may read it.
///
/// Hosting follows <see cref="ReconciliationWorker"/>: a singleton with its own DI scope per pass, also
/// callable directly (<see cref="RunOnceAsync(CancellationToken)"/>) so tests need no timers.
/// </summary>
public class IncidentMonitor : BackgroundService
{
    public const string SystemActor = "system";
    public const string PayoutQueueKeyPrefix = "payout-queue:";
    public const string PayoutQueueWideKeyPrefix = "payout-queue-wide:";
    public const string YandexOutageKey = "yandex-outage";

    /// <summary>An auto incident a person moved to another scope: <c>rescoped:{id}:{condition}</c> (<see cref="MovedKey"/>).</summary>
    public const string MovedKeyPrefix = "rescoped:";

    /// <summary>A moved incident whose condition has ended: <c>released:{id}:{condition}</c>. It holds nothing any more.</summary>
    public const string ReleasedKeyPrefix = "released:";

    /// <summary>Both payout-queue conditions start with it (per park and platform-wide).</summary>
    private const string PayoutQueueKeyRoot = "payout-queue";

    /// <summary>
    /// The key of an auto incident a person moved (to all parks, or to another park). The monitor no longer closes, raises or
    /// rewrites it — a person decided its scope — but it still HOLDS the condition the key names: while that condition lasts,
    /// no second incident opens for it (one condition, one record: Annex 1 §4, and a duplicate P1 would run its own §6(a)
    /// count and, owner-fault, count again in §5). When the condition ends — when the monitor would have closed its own —
    /// the key becomes <see cref="ReleasedKeyPrefix"/>: the incident stays open for a person to resolve, and the next
    /// occurrence is a new incident.
    /// </summary>
    public static string MovedKey(Guid incidentId, string key) => $"{MovedKeyPrefix}{incidentId:N}:{key}";

    /// <summary>The condition a moved incident holds ("rescoped:{id}:payout-queue:…" → "payout-queue:…"); null for any other key.</summary>
    public static string? MovedCondition(string? key)
    {
        if (key is null || !key.StartsWith(MovedKeyPrefix, StringComparison.Ordinal)) return null;
        var rest = key[MovedKeyPrefix.Length..];
        var sep = rest.IndexOf(':');
        return sep > 0 && sep < rest.Length - 1 ? rest[(sep + 1)..] : null;
    }

    /// <summary>The platform-wide queue incident's title: neutral — every park manager reads it.</summary>
    public const string WideQueueTitle = "Payouts waiting at several parks";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IncidentOptions _opts;
    private readonly ILogger<IncidentMonitor> _log;

    public IncidentMonitor(IServiceScopeFactory scopeFactory, IOptions<IncidentOptions> opts, ILogger<IncidentMonitor> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Monitor.Enabled)
        {
            _log.LogInformation("Incident monitor disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_opts.Monitor.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("Incident monitor started (every {Interval}s, queue threshold {Minutes} min)",
            _opts.Monitor.IntervalSeconds, _opts.Monitor.QueueAgeMinutes);

        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Incident monitor pass failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _opts.Monitor.IntervalSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Incident monitor stopped");
    }

    /// <summary>One pass over every condition. Returns how many incidents were opened, merged, resolved or let go.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var changes = await CheckPayoutQueuesAsync(now, ct);
        if (_opts.Monitor.YandexOutageEnabled)
            changes += await CheckYandexOutageAsync(now, ct);
        return changes;
    }

    // ── 1–2. Payouts stuck in the parks' queues ──────────────────────

    public async Task<int> CheckPayoutQueuesAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sla = IncidentSla.From(_opts);
        var threshold = nowUtc.AddMinutes(-_opts.Monitor.QueueAgeMinutes);
        var changes = 0;

        // The queue is drained continuously, so this is a small set even on a bad day. The account's provider
        // tells which bank a generic code ("BANK_UNAVAILABLE") is about.
        var queued = await db.Cashouts.AsNoTracking()
            .Where(c => c.Status == CashoutStatus.Queued)
            .Select(c => new QueuedRow(c.ParkId, c.DriverId, c.CreatedAt, c.FailureReason,
                c.ParkBankAccount != null ? c.ParkBankAccount.Provider : null))
            .ToListAsync(ct);
        var byPark = queued.GroupBy(c => c.ParkId).ToDictionary(g => g.Key, g => g.ToList());

        // With their timelines: whether a person has set the level decides if the monitor may still raise it,
        // and a merge adds to the platform-wide incident's timeline. Moved ones too: they still hold their condition.
        var open = await db.Incidents
            .Include(i => i.Updates)
            .Where(i => i.Source == IncidentSource.Auto && i.Status != IncidentStatus.Resolved && i.DedupeKey != null
                     && (i.DedupeKey.StartsWith(PayoutQueueKeyPrefix) || i.DedupeKey.StartsWith(PayoutQueueWideKeyPrefix)
                         || (i.DedupeKey.StartsWith(MovedKeyPrefix) && i.DedupeKey.Contains(":" + PayoutQueueKeyRoot))))
            .ToListAsync(ct);
        var perPark = open.Where(i => i.DedupeKey!.StartsWith(PayoutQueueKeyPrefix, StringComparison.Ordinal)).ToList();
        // The unique index allows one open incident per key, so one per family.
        var wide = open.Where(i => i.DedupeKey!.StartsWith(PayoutQueueWideKeyPrefix, StringComparison.Ordinal))
            .ToDictionary(i => i.DedupeKey![PayoutQueueWideKeyPrefix.Length..], StringComparer.Ordinal);

        // Moved by a person (MovedKey): one park's queue, or one cause. The monitor never touches them, it only opens nothing
        // beside them for what they hold. The first reported wins should an older database hold two for one condition.
        var movedPark = new Dictionary<Guid, Incident>();
        var movedWide = new Dictionary<string, Incident>(StringComparer.Ordinal);
        foreach (var m in open.Where(i => i.DedupeKey!.StartsWith(MovedKeyPrefix, StringComparison.Ordinal)).OrderBy(i => i.Number))
        {
            var condition = MovedCondition(m.DedupeKey);
            if (condition is null) continue;
            if (condition.StartsWith(PayoutQueueWideKeyPrefix, StringComparison.Ordinal))
                movedWide.TryAdd(condition[PayoutQueueWideKeyPrefix.Length..], m);
            else if (condition.StartsWith(PayoutQueueKeyPrefix, StringComparison.Ordinal)
                     && Guid.TryParse(condition[PayoutQueueKeyPrefix.Length..], out var heldPark))
                movedPark.TryAdd(heldPark, m);
        }

        // ── Moved incidents whose condition has ended: let go ──
        // Exactly when the monitor would have closed its own: the park's queue is empty, or nothing anywhere waits for the
        // cause. The incident stays open — a person decided its scope, a person resolves it — and holds nothing any more, so
        // a later occurrence gets an incident of its own instead of being swallowed by one somebody forgot to close.
        foreach (var (heldPark, m) in movedPark.ToList())
        {
            if (byPark.ContainsKey(heldPark)) continue;
            movedPark.Remove(heldPark);
            if (await ReleaseAsync(db, m, ct)) changes++;
        }
        foreach (var (family, m) in movedWide.ToList())
        {
            if (queued.Any(r => FamilyOf(r) == family)) continue;
            movedWide.Remove(family);
            if (await ReleaseAsync(db, m, ct)) changes++;
        }

        // Suspended parks too: their queued payouts are still owed and still drain.
        var parks = await db.Parks.AsNoTracking()
            .Where(p => p.Status == ParkStatus.Active || p.Status == ParkStatus.Suspended)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        var parkNames = parks.ToDictionary(p => p.Id, p => p.Name);

        // The parks whose oldest payout has waited past the threshold, with what their queue mainly waits for.
        var stuck = parks
            .Where(p => byPark.TryGetValue(p.Id, out var rows) && rows.Min(r => r.CreatedAt) <= threshold)
            .Select(p => new StuckPark(p.Id, p.Name, byPark[p.Id], DominantFamily(byPark[p.Id])))
            .ToList();

        // A park whose queue a person moved to ANOTHER park: its payouts are on that incident's record, which no other
        // park's manager reads — the park takes no part in a platform-wide incident while it holds them.
        bool HeldAtAnotherPark(StuckPark s) => movedPark.TryGetValue(s.ParkId, out var m) && m.ParkId is not null;

        // ── One cause at several parks: one platform-wide P1 ──
        // A cause that already has its record — the monitor's platform-wide incident, or one a person moved — gets no
        // second. When one of those parks' queue incidents was moved to all parks by a person, THAT one is the record (it
        // was declared platform-wide already); else when one of them has its own incident, that incident carries on
        // platform-wide (the cause's record from its first report, with any response already given); else a new one opens.
        foreach (var g in stuck.Where(s => s.Family is not null && !HeldAtAnotherPark(s)).GroupBy(s => s.Family!, StringComparer.Ordinal))
        {
            if (wide.ContainsKey(g.Key) || movedWide.ContainsKey(g.Key) || !AffectsEnoughParks(g.Count(), _opts.Monitor)) continue;
            var group = g.ToList();
            var groupParks = group.Select(s => s.ParkId).ToHashSet();

            var declared = PickCarrier(group.Select(s => movedPark.GetValueOrDefault(s.ParkId)).OfType<Incident>().Where(m => m.ParkId is null));
            if (declared is not null)
            {
                if (await ClaimForWideAsync(db, declared, g.Key, group, nowUtc, ct))
                {
                    foreach (var held in movedPark.Where(kv => kv.Value == declared).Select(kv => kv.Key).ToList()) movedPark.Remove(held);
                    movedWide[g.Key] = declared;
                    changes++;
                }
                continue;   // not claimed: a person changed it a moment ago; the next pass sees what it is now
            }

            var carrier = PickCarrier(perPark.Where(i => i.ParkId is { } pid && groupParks.Contains(pid)));
            Incident? platformWide = null;
            if (carrier is not null && await UpgradeToWideAsync(db, carrier, g.Key, group, nowUtc, ct))
            {
                platformWide = carrier;
                perPark.Remove(carrier);    // no longer one park's: the loops below must not close or raise it as such
            }
            else if (carrier is null)
            {
                platformWide = await OpenWideAsync(db, g.Key, group, nowUtc, ct);
            }
            if (platformWide is null) continue;   // another instance did it a moment ago; the next pass links to it
            wide[g.Key] = platformWide;
            changes++;
        }

        // A stuck park whose queue waits mainly for a cause with a platform-wide record is covered by it — the monitor's, or
        // one a person moved to all parks. Its own incident, if any, closes as that one's duplicate below.
        var coveredBy = new Dictionary<Guid, Incident>();
        foreach (var s in stuck.Where(s => s.Family is not null))
        {
            if (wide.TryGetValue(s.Family!, out var w)) coveredBy[s.ParkId] = w;
            else if (movedWide.TryGetValue(s.Family!, out var m) && m.ParkId is null) coveredBy[s.ParkId] = m;
        }

        // Held by an incident a person moved: the park's own queue (wherever it was moved), or the cause it waits for when a
        // person moved that cause's incident to this very park. Nothing new opens for it. (A cause moved to ANOTHER park is
        // not this park's to read: its queue gets an incident of its own.)
        bool HeldByMoved(StuckPark s) =>
            movedPark.ContainsKey(s.ParkId)
            || (s.Family is { } f && movedWide.TryGetValue(f, out var m) && m.ParkId == s.ParkId);

        // ── Per park ──
        var perParkKeys = perPark.Select(i => i.DedupeKey!).ToHashSet(StringComparer.Ordinal);
        foreach (var park in stuck)
        {
            if (coveredBy.ContainsKey(park.ParkId) || HeldByMoved(park)) continue;
            if (perParkKeys.Contains(PayoutQueueKeyPrefix + park.ParkId)) continue;
            if (await OpenParkAsync(db, park, sla, nowUtc, ct)) changes++;
        }

        foreach (var incident in perPark)
        {
            if (incident.ParkId is { } covered && coveredBy.TryGetValue(covered, out var of))
            {
                await CloseAsDuplicateAsync(db, incident, of, parkNames.GetValueOrDefault(covered, "?"), nowUtc, ct);
                changes++;
                continue;
            }

            if (incident.ParkId is { } parkId && byPark.TryGetValue(parkId, out var waiting))
            {
                // Still something waiting. The impact may have grown since the incident opened.
                if (await EscalateAsync(db, incident, waiting, nowUtc, ct)) changes++;
                continue;
            }

            await ResolveAsync(db, incident, nowUtc,
                "Resolved automatically: no payouts are waiting in this park's queue any more.", "auto_queue_resolved", ct);
            changes++;
            _log.LogInformation("Incident {Ref} resolved automatically (payout queue drained)", incident.Ref);
        }

        // ── Platform-wide: closed when no payout anywhere waits for its cause any more ──
        foreach (var (family, incident) in wide)
        {
            if (queued.Any(r => FamilyOf(r) == family)) continue;
            await ResolveAsync(db, incident, nowUtc,
                "Resolved automatically: no payout waits for this cause at any park any more.", "auto_wide_resolved", ct);
            changes++;
            _log.LogInformation("Incident {Ref} resolved automatically ({Family} no longer holds payouts)", incident.Ref, family);
        }

        return changes;
    }

    private async Task<bool> OpenParkAsync(AppDbContext db, StuckPark park, IncidentSla sla, DateTime nowUtc, CancellationToken ct)
    {
        var rows = park.Rows;
        var oldest = rows.Min(r => r.CreatedAt);
        var impact = await ImpactAsync(db, park.ParkId, rows, ct);
        var level = LevelForImpact(impact.WaitingDrivers, impact.PaidDrivers, _opts.Monitor);
        var (fault, code) = FaultFromFailureReasons(rows.Select(r => r.FailureReason));
        var sample = code is null ? null : rows.FirstOrDefault(r => CodeOf(r.FailureReason) == code)?.FailureReason;
        var oldestLocal = sla.ToLocal(oldest);
        var incident = new Incident
        {
            Status = IncidentStatus.Reported,
            Title = Trim($"Payouts stuck in the queue — {park.Name}", 200),
            Description = $"{rows.Count} payout(s) of {impact.WaitingDrivers} driver(s) queued at {park.Name}; the oldest has waited since " +
                          $"{oldestLocal:yyyy-MM-dd HH:mm} (Tbilisi). Most common failure code: {code ?? "none recorded"}.",
            Cause = sample is null ? null : Trim(sample, 1000),
            Fault = fault,
            ParkId = park.ParkId,
            StartedAt = oldest,
            ReportedAt = nowUtc,
            ReportedBy = SystemActor,
            Source = IncidentSource.Auto,
            DedupeKey = PayoutQueueKeyPrefix + park.ParkId,
        };
        incident.SetLevel(level, nowUtc);
        incident.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "reported", ParkId = park.ParkId,
            Text = $"Opened automatically as {level}: {rows.Count} payout(s) of {impact.WaitingDrivers} driver(s) queued, " +
                   $"the oldest for more than {_opts.Monitor.QueueAgeMinutes} min; {impact.PaidDrivers} other driver(s) paid meanwhile. " +
                   $"Fault set to {fault} from the failure code {code ?? "(none)"}.",
            Data = JsonSerializer.Serialize(new
            {
                code = "auto_queue_opened", level = level.ToString(), payouts = rows.Count,
                drivers = impact.WaitingDrivers, paid = impact.PaidDrivers, minutes = _opts.Monitor.QueueAgeMinutes,
                fault = fault.ToString(), failureCode = code,
            }),
        });
        if (!await TrySaveNewAsync(db, incident, ct)) return false;
        _log.LogWarning("Incident opened: {Level} payout queue stuck at {Park} ({Count} queued for {Drivers} drivers, oldest {Oldest:o}, {Code})",
            level, park.Name, rows.Count, impact.WaitingDrivers, oldest, code);
        return true;
    }

    /// <summary>
    /// The platform-wide P1 for one cause. Its title, description and timeline name no park — every park manager
    /// reads them; which parks it holds is in each park's own link entry (<see cref="CloseAsDuplicateAsync"/>).
    /// Null when another instance opened the same one a moment earlier.
    /// </summary>
    private async Task<Incident?> OpenWideAsync(AppDbContext db, string family, List<StuckPark> parks, DateTime nowUtc, CancellationToken ct)
    {
        var w = WideFacts.Of(family, parks, _opts.Monitor.QueueAgeMinutes);
        var incident = new Incident
        {
            Status = IncidentStatus.Reported,
            Title = WideQueueTitle,
            Description = w.Description,
            Cause = w.Cause,
            Fault = w.Fault,
            ParkId = null,
            StartedAt = w.OldestAt,
            ReportedAt = nowUtc,
            ReportedBy = SystemActor,
            Source = IncidentSource.Auto,
            DedupeKey = PayoutQueueWideKeyPrefix + family,
        };
        incident.SetLevel(IncidentLevel.P1, nowUtc);
        incident.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "reported", ParkId = null,
            Text = $"Opened automatically as P1: {w.Payouts} payout(s) of {w.Drivers} driver(s) at {w.Parks} parks have waited more than " +
                   $"{w.Minutes} min for the same cause ({w.Code}, {w.BankText}). Fault set to {w.Fault}.",
            Data = JsonSerializer.Serialize(new
            {
                code = "auto_wide_opened", parks = w.Parks, payouts = w.Payouts, drivers = w.Drivers, minutes = w.Minutes,
                fault = w.Fault.ToString(), failureCode = w.Code, bank = w.Bank,
            }),
        });
        if (!await TrySaveNewAsync(db, incident, ct)) return null;
        _log.LogWarning("Incident opened: P1 platform-wide, {Family} holds payouts at {Parks} parks ({Count} payouts, {Drivers} drivers)",
            family, w.Parks, w.Payouts, w.Drivers);
        return incident;
    }

    /// <summary>
    /// Of the parks' own incidents for one cause, the one that carries on platform-wide: the one that has been a P1
    /// longest (its P1 clocks keep running — a merge must not restart them, Annex 1 §2), else the first reported.
    /// </summary>
    private static Incident? PickCarrier(IEnumerable<Incident> candidates) => candidates
        .OrderBy(i => FirstP1(i) is null ? 1 : 0)
        .ThenBy(i => FirstP1(i) ?? DateTime.MaxValue)
        .ThenBy(i => i.ReportedAt)
        .ThenBy(i => i.Number)
        .FirstOrDefault();

    private static DateTime? FirstP1(Incident i) => i.ReachedP1At ?? (i.Level == IncidentLevel.P1 ? i.ReportedAt : null);

    /// <summary>
    /// One park's queue incident becomes the platform-wide P1 of its cause, in place: the cause keeps one record from
    /// its first report, with the response Swich may already have given (it still counts) and, if it was a P1 before,
    /// its P1 clocks. A P2 becomes a P1 from now (<see cref="Incident.SetLevel"/>) — the hours before were a P2's.
    ///
    /// Every park manager reads it from now on, so what named the park is rewritten (Annex 1 §4 log, neutral text):
    /// the title, the description and the cause (a raw failure reason of that park's account). Those changes are
    /// written for that park's readers (the entry carries its park); the move itself for everybody, naming no park.
    /// The fault follows the cause unless a person set it. The impact start moves to the longest wait for the cause,
    /// which may be another park's — on the record, like a person's change of it: the §5(b) P1 hours count from it.
    ///
    /// One transaction. The key moves first, by a conditional update: another API instance doing the same at the same
    /// moment finds the key gone (or the platform-wide key taken) and leaves it. Had a later write failed with the key
    /// already moved, the incident would have kept the platform-wide key with park B's text and park — later passes find
    /// the family in <c>wide</c> and never retry, and other parks' incidents close as duplicates of one their managers
    /// cannot open. Rolled back, it is still B's incident and the next pass tries again.
    /// </summary>
    private async Task<bool> UpgradeToWideAsync(AppDbContext db, Incident incident, string family, List<StuckPark> parks, DateTime nowUtc, CancellationToken ct)
    {
        var oldKey = incident.DedupeKey!;
        var wideKey = PayoutQueueWideKeyPrefix + family;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var claimed = await db.Incidents
                .Where(i => i.Id == incident.Id && i.DedupeKey == oldKey && i.Status != IncidentStatus.Resolved)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DedupeKey, wideKey), ct);
            if (claimed == 0) return false;
        }
        catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } || ex is Npgsql.PostgresException { SqlState: "23505" })
        {
            _log.LogInformation("Platform-wide incident for {Key} already opened elsewhere", wideKey);
            return false;   // the transaction is rolled back on dispose
        }
        incident.DedupeKey = wideKey;

        var w = WideFacts.Of(family, parks, _opts.Monitor.QueueAgeMinutes);
        var oldPark = incident.ParkId;

        // ── What named the park (and its own impact start): written for its own readers ──
        var changes = new List<string>();
        var data = new List<object>();
        if (incident.Title != WideQueueTitle)
        {
            changes.Add($"Title: “{incident.Title}” → “{WideQueueTitle}”");
            data.Add(new { field = "title", from = incident.Title, to = WideQueueTitle });
            incident.Title = WideQueueTitle;
        }
        if (incident.Description != w.Description)
        {
            changes.Add("Description updated");
            data.Add(new { field = "description" });
            incident.Description = w.Description;
        }
        if (incident.Fault != w.Fault && !SetByPerson(incident, "fault", "Fault "))
        {
            changes.Add($"Fault {incident.Fault} → {w.Fault}");
            data.Add(new { field = "fault", from = incident.Fault.ToString(), to = w.Fault.ToString() });
            incident.Fault = w.Fault;
        }
        MoveStartEarlier(incident, w, changes, data);
        if (changes.Count > 0)
        {
            incident.Updates.Add(new IncidentUpdate
            {
                At = nowUtc, By = SystemActor, Kind = "updated", ParkId = oldPark,
                Text = string.Join("; ", changes),
                Data = JsonSerializer.Serialize(new { code = "updated", changes = data }),
            });
            await db.SaveChangesAsync(ct);   // saved one by one so the timeline ids keep their order (one timestamp)
        }
        if (incident.Cause != w.Cause)
        {
            // Its own entry, like a person's cause change: no park manager reads the cause.
            incident.Cause = w.Cause;
            incident.Updates.Add(new IncidentUpdate
            {
                At = nowUtc, By = SystemActor, Kind = "updated", ParkId = oldPark,
                Text = $"Cause: {w.Cause}",
                Data = JsonSerializer.Serialize(new { code = "updated", changes = new[] { new { field = "cause", to = w.Cause } } }),
            });
            await db.SaveChangesAsync(ct);
        }

        // ── The move, for everybody from now on ──
        incident.ParkId = null;
        var from = CarryOnAsP1(incident, w, nowUtc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogWarning("Incident {Ref} is now platform-wide P1 (was {From} at park {Park}): {Family} holds payouts at {Parks} parks",
            incident.Ref, from, oldPark, family, w.Parks);
        return true;
    }

    /// <summary>
    /// A queue incident a person moved to all parks becomes the record of the cause its park waits for, once that cause
    /// holds several parks: no platform-wide P1 opens beside it, and no other park incident carries on instead (one incident
    /// per cause). Its key names the cause from now on (<see cref="MovedKey"/>), so it holds the cause — every park waiting
    /// mainly for it is covered — until nothing waits for it any more. A person decided its scope and wrote its text when
    /// moving it (neutral already: the move required it), so nothing of that is rewritten. As for any carrier, the level is
    /// raised to P1 (§2.1 counted across parks; its P1 clocks from now), the fault follows the cause unless a person set it,
    /// and an earlier impact start is logged. One transaction, as <see cref="UpgradeToWideAsync"/>.
    /// </summary>
    private async Task<bool> ClaimForWideAsync(AppDbContext db, Incident incident, string family, List<StuckPark> parks, DateTime nowUtc, CancellationToken ct)
    {
        var oldKey = incident.DedupeKey!;
        var newKey = MovedKey(incident.Id, PayoutQueueWideKeyPrefix + family);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Incidents
            .Where(i => i.Id == incident.Id && i.DedupeKey == oldKey && i.ParkId == null && i.Status != IncidentStatus.Resolved)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DedupeKey, newKey), ct);
        if (claimed == 0) return false;
        incident.DedupeKey = newKey;

        var w = WideFacts.Of(family, parks, _opts.Monitor.QueueAgeMinutes);
        // Every park manager reads it already: these entries go to everybody, and name no park.
        var changes = new List<string>();
        var data = new List<object>();
        if (incident.Fault != w.Fault && !SetByPerson(incident, "fault", "Fault "))
        {
            changes.Add($"Fault {incident.Fault} → {w.Fault}");
            data.Add(new { field = "fault", from = incident.Fault.ToString(), to = w.Fault.ToString() });
            incident.Fault = w.Fault;
        }
        MoveStartEarlier(incident, w, changes, data);
        if (changes.Count > 0)
        {
            incident.Updates.Add(new IncidentUpdate
            {
                At = nowUtc, By = SystemActor, Kind = "updated", ParkId = null,
                Text = string.Join("; ", changes),
                Data = JsonSerializer.Serialize(new { code = "updated", changes = data }),
            });
            await db.SaveChangesAsync(ct);   // saved one by one so the timeline ids keep their order (one timestamp)
        }
        var from = CarryOnAsP1(incident, w, nowUtc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _log.LogWarning("Incident {Ref}, moved to all parks by a person, is now the platform-wide P1 of {Family} (was {From}): it holds payouts at {Parks} parks",
            incident.Ref, family, from, w.Parks);
        return true;
    }

    /// <summary>
    /// The impact began with the longest wait for the cause, which may be another park's payout: the start moves there and
    /// the change is listed (field "started", old → new) — §5(b) sums owner-P1 hours from it, so it is never moved silently.
    /// </summary>
    private void MoveStartEarlier(Incident incident, WideFacts w, List<string> changes, List<object> data)
    {
        if (w.OldestAt >= incident.StartedAt) return;
        var sla = IncidentSla.From(_opts);
        changes.Add($"Impact start {sla.ToLocal(incident.StartedAt):yyyy-MM-dd HH:mm} → {sla.ToLocal(w.OldestAt):yyyy-MM-dd HH:mm} (Tbilisi)");
        data.Add(new { field = "started", from = incident.StartedAt, to = w.OldestAt });
        incident.StartedAt = w.OldestAt;
    }

    /// <summary>
    /// The entry every park manager reads when an incident carries on as a cause's platform-wide P1, and the raise. Only a
    /// raise sets the level: already a P1, its clocks stay where they are (a row without ReachedP1At counts from its
    /// report — SetLevel would stamp "now" on it and restart them). Returns the level it had.
    /// </summary>
    private static IncidentLevel CarryOnAsP1(Incident incident, WideFacts w, DateTime nowUtc)
    {
        var from = incident.Level;
        if (from != IncidentLevel.P1) incident.SetLevel(IncidentLevel.P1, nowUtc);
        incident.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "updated", ParkId = null,
            Text = $"Now one incident for all parks: {w.Payouts} payout(s) of {w.Drivers} driver(s) at {w.Parks} parks have waited more than " +
                   $"{w.Minutes} min for the same cause ({w.Code}, {w.BankText})." +
                   (from == IncidentLevel.P1 ? "" : $" Level {from} → P1 automatically."),
            Data = JsonSerializer.Serialize(new
            {
                code = "auto_wide_upgraded", parks = w.Parks, payouts = w.Payouts, drivers = w.Drivers, minutes = w.Minutes,
                from = from == IncidentLevel.P1 ? null : from.ToString(), to = nameof(IncidentLevel.P1),
                fault = incident.Fault.ToString(), failureCode = w.Code, bank = w.Bank,
            }),
        });
        return from;
    }

    /// <summary>
    /// A moved incident's condition has ended: it holds nothing any more (<see cref="ReleasedKeyPrefix"/>). Conditional on
    /// the key read this pass — a person changing it meanwhile wins. The incident itself is left as it is: open, for a
    /// person to resolve (the monitor never closes what a person scoped).
    /// </summary>
    private async Task<bool> ReleaseAsync(AppDbContext db, Incident moved, CancellationToken ct)
    {
        var key = moved.DedupeKey!;
        var released = ReleasedKeyPrefix + key[MovedKeyPrefix.Length..];
        var n = await db.Incidents
            .Where(i => i.Id == moved.Id && i.DedupeKey == key)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DedupeKey, released), ct);
        if (n > 0)
            _log.LogInformation("Incident {Ref} (moved by a person) no longer holds {Condition}: it has ended; the incident stays open for a person to resolve",
                moved.Ref, MovedCondition(key));
        return n > 0;
    }

    /// <summary>
    /// The figures and neutral text of a platform-wide queue incident. Only the payouts that wait for this cause
    /// count towards it: a park's other payouts are its own business. Nothing here names a park.
    ///
    /// The failure code and the bank ARE in the text every park manager reads, deliberately: they are the shared
    /// classification of what every affected park sees (a manager reads its own queue incident's code the same way), built
    /// from rows of several parks. What park managers never read is the Cause field of a park's incident — one account's
    /// raw failure reason — and Swich's own diagnosis; this incident's Cause is only this same code, bank and park count.
    /// </summary>
    private sealed record WideFacts(
        int Parks, int Payouts, int Drivers, int Minutes, IncidentFault Fault, string? Code, string? Bank, string BankText,
        DateTime OldestAt, string Description, string Cause)
    {
        public static WideFacts Of(string family, List<StuckPark> parks, int minutes)
        {
            var rows = parks.SelectMany(p => p.Rows).Where(r => FamilyOf(r) == family).ToList();
            var (fault, code) = FaultFromFailureReasons(rows.Select(r => r.FailureReason));
            var bank = BankOfFamily(family);
            var bankText = bank ?? "the bank";
            var description = (fault == IncidentFault.Platform
                    ? $"Payouts at {parks.Count} parks have waited more than {minutes} min because the platform's connection to {bankText} is missing ({code}). "
                    : $"Payouts at {parks.Count} parks have waited more than {minutes} min because {bankText} is not accepting transfers ({code}). ") +
                "They go out automatically once the cause is gone. Each park's own incident is closed as a duplicate of this one.";
            return new WideFacts(parks.Count, rows.Count, rows.Select(r => r.DriverId).Distinct().Count(), minutes, fault, code, bank, bankText,
                rows.Min(r => r.CreatedAt), description, Trim($"{code} ({bankText}, {parks.Count} parks)", 1000));
        }
    }

    /// <summary>
    /// A park's own queue incident, now covered by the platform-wide one. Both get a link entry carrying the
    /// park's id: only that park's manager (with Swich and the licensee) reads which incident was merged.
    /// </summary>
    private async Task CloseAsDuplicateAsync(AppDbContext db, Incident duplicate, Incident of, string parkName, DateTime nowUtc, CancellationToken ct)
    {
        of.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "note", ParkId = duplicate.ParkId,
            Text = $"{duplicate.Ref} ({parkName}) is closed as a duplicate of this incident: the same cause holds its payouts.",
            Data = JsonSerializer.Serialize(new { code = "auto_merged", incident = duplicate.Ref, park = parkName }),
        });
        await db.SaveChangesAsync(ct);   // saved apart so the timeline ids keep the order (one timestamp)

        duplicate.Status = IncidentStatus.Resolved;
        duplicate.ResolvedAt = nowUtc;
        duplicate.ResolvedBy = SystemActor;
        duplicate.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "resolved", ParkId = duplicate.ParkId,
            Text = $"Closed as a duplicate of {of.Ref} (all parks): the same cause holds payouts at several parks. Follow {of.Ref}.",
            Data = JsonSerializer.Serialize(new { code = "auto_duplicate", of = of.Ref }),
        });
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Incident {Ref} closed as a duplicate of {Of}", duplicate.Ref, of.Ref);
    }

    /// <summary>The condition is gone. An external cause is marked restored first (Annex 1 §3.2: its clock starts there).</summary>
    private static async Task ResolveAsync(AppDbContext db, Incident incident, DateTime nowUtc, string text, string code, CancellationToken ct)
    {
        if (IncidentSla.IsExternalCause(incident.Fault) && incident.ExternalRestoredAt is null)
        {
            incident.ExternalRestoredAt = nowUtc;
            incident.ExternalRestoredBy = SystemActor;
            incident.Updates.Add(new IncidentUpdate
            {
                At = nowUtc, By = SystemActor, Kind = "external_restored", ParkId = incident.ParkId,
                Text = "Payouts are going through again.",
                Data = JsonSerializer.Serialize(new { code = "auto_queue_restored" }),
            });
            // Saved on its own so the timeline ids keep the order (both entries share one timestamp).
            await db.SaveChangesAsync(ct);
        }
        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = nowUtc;
        incident.ResolvedBy = SystemActor;
        incident.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "resolved", ParkId = incident.ParkId,
            Text = text,
            Data = JsonSerializer.Serialize(new { code }),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Annex 1 §2.1: P1 is "business stopped — cashouts fail for all or most drivers, no workaround"; the
    /// level follows the impact whatever the cause (§3.1). The monitor sees the drivers whose payouts wait and
    /// the park's other drivers who were paid while they waited (the workaround: e.g. only one bank's cards
    /// are stuck). P1 when at least <see cref="IncidentMonitorOptions.P1MinWaitingDrivers"/> drivers wait and
    /// they are most (more than half) of those who tried; otherwise P2. One driver's payout waiting for a
    /// top-up is not "business stopped", and a P1 would also start the §6(a) 72-hour count for it.
    /// </summary>
    public static IncidentLevel LevelForImpact(int waitingDrivers, int paidDrivers, IncidentMonitorOptions o) =>
        waitingDrivers >= Math.Max(1, o.P1MinWaitingDrivers) && waitingDrivers > paidDrivers
            ? IncidentLevel.P1
            : IncidentLevel.P2;

    /// <summary>
    /// Whether one cause holds enough parks to be a platform-wide P1: <see cref="IncidentMonitorOptions.P1MinParks"/>,
    /// never fewer than two — one park is that park's incident, however it is configured.
    /// </summary>
    public static bool AffectsEnoughParks(int parks, IncidentMonitorOptions o) => parks >= Math.Max(2, o.P1MinParks);

    /// <summary>
    /// Drivers of the park with a payout waiting, and the park's OTHER drivers whose payout reached the
    /// bank since the oldest waiting one was created (completed, or accepted and pending there).
    /// </summary>
    private static async Task<(int WaitingDrivers, int PaidDrivers)> ImpactAsync(
        AppDbContext db, Guid parkId, IReadOnlyCollection<QueuedRow> rows, CancellationToken ct)
    {
        var waiting = rows.Select(r => r.DriverId).Distinct().ToList();
        var since = rows.Min(r => r.CreatedAt);
        var paid = await db.Cashouts.AsNoTracking()
            .Where(c => c.ParkId == parkId && !waiting.Contains(c.DriverId)
                     && ((c.Status == CashoutStatus.Completed && c.CompletedAt >= since)
                      || (c.Status == CashoutStatus.Processing && c.BankTransferId != null && c.UpdatedAt >= since)))
            .Select(c => c.DriverId)
            .Distinct()
            .CountAsync(ct);
        return (waiting.Count, paid);
    }

    /// <summary>
    /// Raise an open P2 (or P3) queue incident to P1 once the impact is "most drivers". Never lowers it:
    /// the log keeps the worst the incident was. Leaves the level alone once a person has set it — the level
    /// is the owner's call (§2.1), and a Swich downgrade must not be undone every minute. The P1 clocks start
    /// now (<see cref="IncidentSla.LevelClockStart"/>): a raise at hour 60 of a dry weekend is a P1 from now,
    /// not one that has gone unanswered for 60 hours.
    /// </summary>
    private async Task<bool> EscalateAsync(AppDbContext db, Incident incident, List<QueuedRow> rows, DateTime nowUtc, CancellationToken ct)
    {
        if (incident.Level == IncidentLevel.P1 || LevelSetByPerson(incident)) return false;
        var impact = await ImpactAsync(db, incident.ParkId!.Value, rows, ct);
        if (LevelForImpact(impact.WaitingDrivers, impact.PaidDrivers, _opts.Monitor) != IncidentLevel.P1) return false;

        var from = incident.Level;
        incident.SetLevel(IncidentLevel.P1, nowUtc);
        incident.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "updated", ParkId = incident.ParkId,
            Text = $"Level {from} → P1 automatically: payouts of {impact.WaitingDrivers} driver(s) are waiting ({rows.Count} payouts), " +
                   $"{impact.PaidDrivers} other driver(s) were paid meanwhile.",
            Data = JsonSerializer.Serialize(new
            {
                code = "auto_escalated", from = from.ToString(), to = nameof(IncidentLevel.P1),
                drivers = impact.WaitingDrivers, payouts = rows.Count, paid = impact.PaidDrivers,
            }),
        });
        await db.SaveChangesAsync(ct);
        _log.LogWarning("Incident {Ref} raised {From} → P1: {Drivers} drivers waiting, {Paid} paid meanwhile",
            incident.Ref, from, impact.WaitingDrivers, impact.PaidDrivers);
        return true;
    }

    /// <summary>A person's "updated" entry that changed the level (from its data; older rows by their text).</summary>
    private static bool LevelSetByPerson(Incident incident) => SetByPerson(incident, "level", "Level ");

    /// <summary>A person's "updated" entry changed <paramref name="field"/> (older rows without data: by their text).</summary>
    private static bool SetByPerson(Incident incident, string field, string textMarker) => incident.Updates.Any(u =>
        u.Kind == "updated" && u.By != SystemActor &&
        (u.Data is { } data ? ChangesField(data, field) : u.Text.Contains(textMarker, StringComparison.Ordinal)));

    private static bool ChangesField(string data, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
                && changes.EnumerateArray().Any(c => c.TryGetProperty("field", out var f) && f.GetString() == field);
        }
        catch (JsonException) { return false; }
    }

    // ── 3. Yandex Fleet unreachable ──────────────────────────────────

    public async Task<int> CheckYandexOutageAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var minutes = _opts.Monitor.YandexWindowMinutes;
        var since = nowUtc.AddMinutes(-minutes);

        var calls = await db.ApiAuditLogs.AsNoTracking()
            .Where(a => a.ApiProvider == "YandexFleet" && a.Timestamp > since && a.Timestamp <= nowUtc)
            .Select(a => new YandexCall(a.ParkId, a.Timestamp, a.Success, a.ResponseCode))
            .ToListAsync(ct);
        var outage = AssessYandex(calls, _opts.Monitor);

        var open = await db.Incidents.FirstOrDefaultAsync(
            i => i.DedupeKey == YandexOutageKey && i.Status != IncidentStatus.Resolved, ct);

        if (open is null)
        {
            // A person moved the outage incident (to one park): it holds the outage until it ends, as the monitor's own would
            // — no second one opens meanwhile. Then the monitor lets go (a person resolves it) and the next outage is new.
            var moved = await db.Incidents
                .Where(i => i.Status != IncidentStatus.Resolved && i.DedupeKey != null
                         && i.DedupeKey.StartsWith(MovedKeyPrefix) && i.DedupeKey.EndsWith(":" + YandexOutageKey))
                .OrderBy(i => i.Number)
                .FirstOrDefaultAsync(ct);
            if (moved is not null)
            {
                if (outage.Down || await YandexBackAtAsync(db, moved.ReportedAt, nowUtc, ct) is null) return 0;
                return await ReleaseAsync(db, moved, ct) ? 1 : 0;
            }

            if (!outage.Down) return 0;
            // Cashouts start with a balance read on Yandex: while it fails the request is refused, nothing more.
            const string refused = "Cashout requests are refused while it lasts: the balance cannot be checked, so nothing is debited and " +
                                   "nothing is queued; drivers try again once Yandex answers. Payouts already debited are not affected.";
            var incident = new Incident
            {
                Status = IncidentStatus.Reported,
                Title = "Yandex Fleet API unreachable",
                Description = (outage.Parks is { } n
                    ? $"Yandex Fleet calls failed for {n} parks in the last {minutes} min ({outage.Failures} server or network errors) and none of theirs succeeded. "
                    : $"No successful Yandex Fleet call in the last {minutes} min and {outage.Failures} failed (server or network errors). ") + refused,
                Cause = "Yandex Fleet API errors (5xx / transport)",
                Fault = IncidentFault.External,
                ParkId = null,
                StartedAt = outage.FirstFailure!.Value,
                ReportedAt = nowUtc,
                ReportedBy = SystemActor,
                Source = IncidentSource.Auto,
                DedupeKey = YandexOutageKey,
            };
            incident.SetLevel(IncidentLevel.P1, nowUtc);
            incident.Updates.Add(new IncidentUpdate
            {
                At = nowUtc, By = SystemActor, Kind = "reported", ParkId = null,
                Text = (outage.Parks is { } parks
                    ? $"Opened automatically: {outage.Failures} failed Yandex Fleet calls for {parks} parks and none of theirs succeeded in {minutes} min."
                    : $"Opened automatically: {outage.Failures} failed Yandex Fleet calls and none succeeded in {minutes} min.") +
                       " Cashout requests are refused meanwhile: nothing is debited and nothing is queued.",
                Data = JsonSerializer.Serialize(new { code = "auto_yandex_opened", failures = outage.Failures, minutes, parks = outage.Parks }),
            });
            if (!await TrySaveNewAsync(db, incident, ct)) return 0;
            _log.LogWarning("Incident opened: Yandex Fleet outage ({Failures} failures, {Parks} parks, no success in {Window} min)",
                outage.Failures, outage.Parks?.ToString() ?? "all", minutes);
            return 1;
        }

        // Back once a call made after the incident opened succeeded — and the outage no longer holds: one park
        // getting through again while others still fail is not the end of it.
        if (outage.Down) return 0;
        var backAt = await YandexBackAtAsync(db, open.ReportedAt, nowUtc, ct);
        if (backAt is null) return 0;

        if (open.ExternalRestoredAt is null)
        {
            open.ExternalRestoredAt = backAt;
            open.ExternalRestoredBy = SystemActor;
            open.Updates.Add(new IncidentUpdate
            {
                At = nowUtc, By = SystemActor, Kind = "external_restored", ParkId = open.ParkId,
                Text = "Yandex Fleet answers again.",
                Data = JsonSerializer.Serialize(new { code = "auto_yandex_restored" }),
            });
            await db.SaveChangesAsync(ct); // keeps the timeline order, see ResolveAsync
        }
        open.Status = IncidentStatus.Resolved;
        open.ResolvedAt = nowUtc;
        open.ResolvedBy = SystemActor;
        open.Updates.Add(new IncidentUpdate
        {
            At = nowUtc, By = SystemActor, Kind = "resolved", ParkId = open.ParkId,
            Text = "Resolved automatically: Yandex Fleet calls succeed again.",
            Data = JsonSerializer.Serialize(new { code = "auto_yandex_resolved" }),
        });
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Incident {Ref} resolved automatically (Yandex Fleet back)", open.Ref);
        return 1;
    }

    /// <summary>The first successful Yandex Fleet call since the incident was reported (null: none yet).</summary>
    private static Task<DateTime?> YandexBackAtAsync(AppDbContext db, DateTime reportedAt, DateTime nowUtc, CancellationToken ct) =>
        db.ApiAuditLogs.AsNoTracking()
            .Where(a => a.ApiProvider == "YandexFleet" && a.Success && a.Timestamp >= reportedAt && a.Timestamp <= nowUtc)
            .OrderBy(a => a.Timestamp).Select(a => (DateTime?)a.Timestamp).FirstOrDefaultAsync(ct);

    /// <summary>
    /// What the last few minutes of Yandex Fleet calls say. Only server (5xx) and transport failures count: a
    /// 403 is the read-only switch, a business rejection ("insufficient balance") comes back as a successful call.
    ///   - Everywhere: not one call succeeded and at least <see cref="IncidentMonitorOptions.YandexMinFailures"/> failed.
    ///   - Several parks: at least <see cref="IncidentMonitorOptions.P1MinParks"/> parks whose calls only failed,
    ///     with that many failures between them — even while another park still gets through.
    /// <see cref="YandexOutage.Parks"/> is null for "everywhere".
    /// </summary>
    public static YandexOutage AssessYandex(IReadOnlyCollection<YandexCall> calls, IncidentMonitorOptions o)
    {
        static bool ServerFailure(YandexCall c) => !c.Success && c.ResponseCode >= 500;
        var failures = calls.Where(ServerFailure).ToList();
        if (!calls.Any(c => c.Success) && failures.Count >= o.YandexMinFailures)
            return new YandexOutage(true, null, failures.Count, failures.Min(f => f.At));

        var downParks = calls
            .Where(c => c.ParkId is not null)
            .GroupBy(c => c.ParkId!.Value)
            .Where(g => g.Any(ServerFailure) && !g.Any(c => c.Success))
            .Select(g => g.Key)
            .ToHashSet();
        var theirs = failures.Where(f => f.ParkId is { } p && downParks.Contains(p)).ToList();
        return AffectsEnoughParks(downParks.Count, o) && theirs.Count >= o.YandexMinFailures
            ? new YandexOutage(true, downParks.Count, theirs.Count, theirs.Min(f => f.At))
            : new YandexOutage(false, null, failures.Count, null);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Fault from the queued payouts' failure codes ("CODE: message"): the most common code decides, and
    /// <see cref="PayoutWaitCodes"/> maps it. The park's side → Park; the bank itself failing → External;
    /// our own missing adapter → Platform; credentials and unknown outcomes → Unknown, for a person to establish.
    /// </summary>
    public static (IncidentFault Fault, string? Code) FaultFromFailureReasons(IEnumerable<string?> reasons)
    {
        var code = reasons
            .Select(CodeOf)
            .Where(c => c is not null)
            .GroupBy(c => c!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
        return (FaultForCode(code), code);
    }

    public static IncidentFault FaultForCode(string? code) => PayoutWaitCodes.FaultOf(code);

    /// <summary>
    /// The cause a failure code stands for across parks, or null when it is not one cause wherever it shows:
    ///   - "bank:{bank}"    — that bank failing (External): its gateway down, its own fault answers.
    ///   - "adapter:{bank}" — our integration for that bank missing (Platform): every park at that bank waits.
    /// The bank is the park account's adapter ("tbc", "bog", "mock"), else the code's prefix (TBC_/BOG_), else
    /// "unknown". The park's side (money, payout account, a revocation) and undetermined causes (credentials
    /// of each park, unknown outcomes, refusals without a reason) give null: they never aggregate.
    /// </summary>
    public static string? CauseFamily(string? code, string? provider)
    {
        var fault = FaultForCode(code);
        if (fault is not (IncidentFault.External or IncidentFault.Platform)) return null;
        var bank = !string.IsNullOrWhiteSpace(provider) ? provider.Trim().ToLowerInvariant()
            : code!.StartsWith("TBC_", StringComparison.Ordinal) ? "tbc"
            : code.StartsWith("BOG_", StringComparison.Ordinal) ? "bog"
            : "unknown";
        return (fault == IncidentFault.External ? "bank:" : "adapter:") + bank;
    }

    private static string? FamilyOf(QueuedRow r) => CauseFamily(CodeOf(r.FailureReason), r.Provider);

    /// <summary>The family of the code the park's incident is about (its most common code), at the bank most of those rows wait for.</summary>
    private static string? DominantFamily(IReadOnlyCollection<QueuedRow> rows)
    {
        var (_, code) = FaultFromFailureReasons(rows.Select(r => r.FailureReason));
        if (code is null) return null;
        var provider = rows
            .Where(r => CodeOf(r.FailureReason) == code)
            .GroupBy(r => r.Provider)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;
        return CauseFamily(code, provider);
    }

    /// <summary>"bank:tbc" → "TBC"; null when the bank is not known.</summary>
    private static string? BankOfFamily(string family)
    {
        var bank = family[(family.IndexOf(':') + 1)..];
        return bank == "unknown" ? null : bank.ToUpperInvariant();
    }

    /// <summary>"BANK_UNAVAILABLE: Bank gateway…" → "BANK_UNAVAILABLE".</summary>
    public static string? CodeOf(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason)) return null;
        var i = failureReason.IndexOf(':');
        return (i > 0 ? failureReason[..i] : failureReason).Trim();
    }

    /// <summary>
    /// Insert a new auto incident. Another API instance may have opened the same condition a moment
    /// earlier — the filtered unique index on DedupeKey refuses the second one, which is the point.
    /// </summary>
    private async Task<bool> TrySaveNewAsync(AppDbContext db, Incident incident, CancellationToken ct)
    {
        db.Incidents.Add(incident);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _log.LogInformation("Incident for {Key} already opened elsewhere", incident.DedupeKey);
            // Forget only the refused rows: the open incidents loaded earlier in this pass stay tracked.
            foreach (var u in incident.Updates) db.Entry(u).State = EntityState.Detached;
            db.Entry(incident).State = EntityState.Detached;
            return false;
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed record QueuedRow(Guid ParkId, Guid DriverId, DateTime CreatedAt, string? FailureReason, string? Provider);

    private sealed record StuckPark(Guid ParkId, string Name, List<QueuedRow> Rows, string? Family);
}

/// <summary>One Yandex Fleet call from the API audit log, as the outage detector reads it.</summary>
public sealed record YandexCall(Guid? ParkId, DateTime At, bool Success, int ResponseCode);

/// <summary>The detector's verdict. <see cref="Parks"/> = how many parks only failed; null when nothing got through anywhere.</summary>
public sealed record YandexOutage(bool Down, int? Parks, int Failures, DateTime? FirstFailure);
