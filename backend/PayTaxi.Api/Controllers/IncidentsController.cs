using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// The incident log the licence agreement requires (Annex 1 §4): every incident with its level, cause,
/// fault and response / resolution times, readable by the licensee in the admin console. It is the
/// evidence for chronic breach (§5), abandonment (§6) and the trial-period release (§7), so every change
/// lands on an append-only timeline with the actor taken from the token.
///
/// Roles:
///   - super_admin (Swich, the platform owner): everything — the owner sets the level (§2.1), establishes
///     cause and fault, responds (acknowledges), marks the external service restored, resolves, reopens.
///   - operator (the licensee): the incidents of its own licensee's parks and the platform-wide ones, and the monthly
///     contract metrics; REPORTS incidents (it may say how bad it is — the level the owner can change — but not whose
///     fault it is) and adds notes — for those parks, or for all.
///   - park_admin: read-only; their own park's incidents plus platform-wide ones, no contract metrics
///     (those are between Swich and the licensee), and staff e-mails are not shown. Never the cause, and of
///     the timeline only what was written while the incident was visible to their park
///     (<see cref="IncidentUpdate.ParkId"/>) — a moved or merged incident does not carry another park's past.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin/incidents")]
public class IncidentsController : AdminControllerBase
{
    private readonly AppDbContext _db;
    private readonly IncidentOptions _opts;
    private readonly IContractCalendar _contract;
    private readonly ILogger<IncidentsController> _log;

    public IncidentsController(AppDbContext db, IOptions<IncidentOptions> opts, IContractCalendar contract, ILogger<IncidentsController> log)
    {
        _db = db;
        _opts = opts.Value;
        _contract = contract;
        _log = log;
    }

    private IncidentSla Sla => IncidentSla.From(_opts);

    /// <summary>Accepting a few seconds of clock skew between the browser and the server for "when did it start".</summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(2);

    // ═══════════════════════════════════════════════════════════════════
    //  Read
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The log, open incidents first. Filters: <c>state</c> = open | resolved | all, <c>level</c> = P1..P3,
    /// <c>month</c> = yyyy-MM (reported in that Tbilisi month), <c>parkId</c> = a park id or "platform".
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? state, [FromQuery] string? level, [FromQuery] string? month,
        [FromQuery] string? parkId, [FromQuery] int take = 200, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sla = Sla;
        var visible = Visible(_db.Incidents.AsNoTracking());
        var query = visible;

        switch (state?.Trim().ToLowerInvariant())
        {
            case null or "" or "all": break;
            case "open": query = query.Where(i => i.Status != IncidentStatus.Resolved); break;
            case "resolved": query = query.Where(i => i.Status == IncidentStatus.Resolved); break;
            default: return BadRequest(new { error = "invalid_state", allowed = new[] { "open", "resolved", "all" } });
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            if (!TryParseLevel(level, out var lv)) return BadRequest(new { error = "invalid_level", allowed = Enum.GetNames<IncidentLevel>() });
            query = query.Where(i => i.Level == lv);
        }

        if (!string.IsNullOrWhiteSpace(month))
        {
            if (!TryParseMonth(month, out var y, out var m)) return BadRequest(new { error = "invalid_month", format = "yyyy-MM" });
            var (from, to) = IncidentMetrics.MonthBounds(y, m, sla);
            query = query.Where(i => i.ReportedAt >= from && i.ReportedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(parkId))
        {
            if (parkId.Trim().Equals("platform", StringComparison.OrdinalIgnoreCase)) query = query.Where(i => i.ParkId == null);
            else if (Guid.TryParse(parkId, out var pid)) query = query.Where(i => i.ParkId == pid);
            else return BadRequest(new { error = "invalid_park" });
        }

        var rows = await query
            .OrderByDescending(i => i.ReportedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        var names = await ParkNamesAsync(rows, ct);

        // Open first (worst level first), then the rest newest-first.
        var ordered = rows
            .OrderBy(i => i.Status == IncidentStatus.Resolved ? 1 : 0)
            .ThenBy(i => i.Status == IncidentStatus.Resolved ? IncidentLevel.P1 : i.Level)
            .ThenByDescending(i => i.ReportedAt)
            .ToList();

        var openCount = await visible.CountAsync(i => i.Status != IncidentStatus.Resolved, ct);

        return Ok(new
        {
            asOf = now,
            count = ordered.Count,
            openCount,
            incidents = ordered.Select(i => Summary(i, names, sla, now)),
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var incident = await Visible(_db.Incidents.AsNoTracking()).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });
        return Ok(await DetailAsync(incident, ct));
    }

    /// <summary>
    /// Contract metrics for one calendar month (Tbilisi; default the current one): §5 chronic breach,
    /// §6(a) abandonment, and the §7 trial status when a launch date is configured. Swich and the
    /// licensee only — park managers are not party to the licence agreement.
    /// </summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics([FromQuery] string? month, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var now = DateTime.UtcNow;
        var sla = Sla;

        int y, m;
        if (string.IsNullOrWhiteSpace(month))
        {
            var local = sla.ToLocal(now);
            (y, m) = (local.Year, local.Month);
        }
        else if (!TryParseMonth(month, out y, out m)) return BadRequest(new { error = "invalid_month", format = "yyyy-MM" });

        var (from, to) = IncidentMetrics.MonthBounds(y, m, sla);
        // Incidents are rare: every P1 (durations can span months, the trial can be any month) plus the
        // month's own reports is a few dozen rows a year.
        var all = await _db.Incidents.AsNoTracking()
            .Where(i => i.Level == IncidentLevel.P1 || (i.ReportedAt >= from && i.ReportedAt < to))
            .ToListAsync(ct);

        // The agreement covers the licensee's parks. A platform-wide incident stops them too, so it counts.
        // A park Swich connected directly is outside the agreement (§2.2): its incidents are in the log but
        // never feed §5–§7. Without that filter, three owner-fault misses at a direct park would show the
        // licensee a "chronic breach" that releases it from the barrier remainder (8.5.3).
        var covered = (await CoveredParkIdsAsync(ct)).ToHashSet();
        var rows = all.Where(i => i.ParkId is not { } pid || covered.Contains(pid)).ToList();
        var outsideP1 = all.Count(i => i.ParkId is { } pid && !covered.Contains(pid) && i.Level == IncidentLevel.P1
                                    && IncidentSla.LevelClockStart(i) is var p1 && p1 >= from && p1 < to);

        var mm = IncidentMetrics.ForMonth(rows, y, m, now, sla, _opts);
        var trial = IncidentMetrics.Trial(rows, _contract.LaunchDate, _contract.TrialDays, now, sla);

        return Ok(new
        {
            month = mm.Month,
            from = mm.FromUtc,
            to = mm.ToUtc,
            asOf = now,
            isCurrent = now >= mm.FromUtc && now < mm.ToUtc,
            thresholds = new
            {
                chronicP1Count = _opts.ChronicBreachP1Count,
                chronicP1Hours = _opts.ChronicBreachP1Hours,
                abandonmentResponseHours = _opts.AbandonmentResponseHours,
                abandonmentSupportDays = _opts.AbandonmentSupportDays,
                abandonmentCureDays = _opts.AbandonmentCureDays,
            },
            counts = new
            {
                p1 = mm.P1Reported,
                p2 = mm.P2Reported,
                p3 = mm.P3Reported,
                ownerP1 = mm.OwnerP1Count,
                undeterminedP1 = mm.UndeterminedP1Count,
                // Shown apart: P1s reported this month at parks outside the agreement, not counted above.
                outsideAgreementP1 = outsideP1,
            },
            ownerP1Missed = mm.OwnerP1Missed.Select(Citation),
            ownerP1Hours = mm.OwnerP1Hours,
            chronicBreach = new { byCount = mm.ChronicByCount, byHours = mm.ChronicByHours, flag = mm.ChronicBreach },
            abandonment = new { flag = mm.Abandonment, unanswered = mm.UnansweredP1.Select(Citation) },
            trial = new
            {
                state = trial.State,
                launchDate = trial.LaunchDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                trialDays = trial.TrialDays,
                from = trial.FromUtc,
                to = trial.ToUtc,
                day = trial.Day,
                licenseeReleased = trial.LicenseeReleased,
                qualifying = trial.Qualifying.Select(Citation),
                ownerP1Open = trial.OwnerP1Open,
                undeterminedMissed = trial.UndeterminedMissed.Select(Citation),
                undeterminedOpen = trial.UndeterminedOpen,
                undetermined = trial.Undetermined,
            },
        });
    }

    /// <summary>The parks under the licence agreement: the contract calendar's licensee's (any licensee's when none is recorded).</summary>
    private Task<List<Guid>> CoveredParkIdsAsync(CancellationToken ct)
    {
        var parks = _db.Parks.AsNoTracking();
        parks = _contract.LicenseeId is { } licenseeId
            ? parks.Where(p => p.LicenseeId == licenseeId)
            : parks.Where(p => p.LicenseeId != null);
        return parks.Select(p => p.Id).ToListAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Write
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Report an incident. Swich or the licensee; it always starts as Reported (the response clock starts
    /// now). Swich may also set fault and cause and acknowledge it in the same step (<c>acknowledge: true</c>).
    /// The impact may have begun before the report, within a bound per role (<see cref="TooEarly"/>).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateIncidentRequest? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });

        var title = Blank(body.Title);
        if (title is null) return BadRequest(new { error = "title_required" });
        if (title.Length > 200) return BadRequest(new { error = "title_too_long", max = 200 });
        if (!TryParseLevel(body.Level, out var level)) return BadRequest(new { error = "invalid_level", allowed = Enum.GetNames<IncidentLevel>() });
        var description = Blank(body.Description);
        if (description is { Length: > 4000 }) return BadRequest(new { error = "description_too_long", max = 4000 });

        // Whose fault it is and why is the owner's call (Annex 1 §2.1, §3.1); so is the contractual response.
        var fault = IncidentFault.Unknown;
        if (!string.IsNullOrWhiteSpace(body.Fault))
        {
            if (!IsSuperAdmin) return Forbid();
            if (!TryParseFault(body.Fault, out fault)) return BadRequest(new { error = "invalid_fault", allowed = Enum.GetNames<IncidentFault>() });
        }
        var cause = Blank(body.Cause);
        if (cause is not null && !IsSuperAdmin) return Forbid();
        if (cause is { Length: > 1000 }) return BadRequest(new { error = "cause_too_long", max = 1000 });
        if (body.Acknowledge == true && !IsSuperAdmin) return Forbid();

        string? parkName = null;
        if (body.ParkId is { } pid)
        {
            // The operator reports for its own licensee's parks (or for all) — not for a park it cannot see.
            if (!CanAccessPark(pid)) return Forbid();
            parkName = await _db.Parks.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct);
            if (parkName is null) return BadRequest(new { error = "park_not_found" });
        }

        var now = DateTime.UtcNow;
        var startedAt = body.StartedAt?.UtcDateTime ?? now;
        if (startedAt > now + FutureSkew) return BadRequest(new { error = "started_in_future" });
        if (startedAt > now) startedAt = now;
        if (TooEarly(startedAt, now) is { } tooEarly) return tooEarly;

        var incident = new Incident
        {
            Status = IncidentStatus.Reported,
            Title = title,
            Description = description,
            Cause = cause,
            Fault = fault,
            ParkId = body.ParkId,
            StartedAt = startedAt,
            ReportedAt = now,
            ReportedBy = ActorLabel,
            Source = IncidentSource.Manual,
        };
        incident.SetLevel(level, now);
        var scope = parkName ?? "all parks";
        var backdated = startedAt < now.AddMinutes(-1);
        Log(incident, now, "reported", $"Reported as {level} ({scope}): {title}" +
            (backdated ? $". Impact since {Local(startedAt)} (Tbilisi)." : "."),
            new { code = "reported", level = level.ToString(), park = parkName, title, impactSince = backdated ? startedAt : (DateTime?)null });
        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(ct);

        if (body.Acknowledge == true)
        {
            // A second save keeps the timeline ids in order (both entries share one timestamp).
            incident.Status = IncidentStatus.Acknowledged;
            incident.AcknowledgedAt = now;
            incident.AcknowledgedBy = ActorLabel;
            Log(incident, now, "acknowledged", "Acknowledged when reported — work has started.", new { code = "acknowledged_on_report" });
            await _db.SaveChangesAsync(ct);
        }
        _log.LogWarning("Incident {Ref} reported by {Actor}: {Level} {Title}", incident.Ref, ActorLabel, level, title);

        return StatusCode(StatusCodes.Status201Created, await DetailAsync(incident, ct));
    }

    /// <summary>
    /// Change title / description / cause / level / fault / park / impact start. Swich only. Allowed after
    /// resolution too — fault and cause are often only settled in the post-mortem. One timeline entry lists
    /// every field that changed, old → new; a cause change and a change of park get entries of their own,
    /// because they have other readers (<see cref="IncidentUpdate.ParkId"/>): no park manager reads the cause,
    /// and the park an incident moves to reads the move, not what was written before it.
    ///
    /// Raising the level starts that level's clocks now, unless the incident had it before (the first time
    /// counts — <see cref="Incident.SetLevel"/>), the same rule as the monitor's own escalation.
    ///
    /// Moving an incident away from its park (to all parks, or to another park) shows it to other park
    /// managers. The same request must then carry a new title and a new description, neither naming the park
    /// it leaves — the monitor's own text does ("Payouts stuck in the queue — Park B") — or it is refused with
    /// neutral_text_required. Moving a platform-wide incident to one park shows nothing new to anybody and needs
    /// no new text. A moved auto incident is no longer closed, raised or rewritten by the monitor — a person decided
    /// its scope — but it still holds the condition it was opened for (<see cref="IncidentMonitor.MovedKey"/>): no second
    /// incident opens for the same waiting payouts while they wait. Once they no longer do, the monitor lets go of it
    /// and it stays open for Swich to resolve.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateIncidentRequest? body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        if (body is null) return BadRequest(new { error = "missing_body" });
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });

        // ── The scope first: whether the text has to change depends on it ──
        if (body.PlatformWide == true && body.ParkId is not null) return BadRequest(new { error = "park_or_platform" });
        var oldParkId = incident.ParkId;
        var newParkId = oldParkId;
        string? newParkName = null;
        if (body.PlatformWide == true && oldParkId is not null) newParkId = null;
        else if (body.ParkId is { } pid && pid != oldParkId)
        {
            newParkName = await _db.Parks.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct);
            if (newParkName is null) return BadRequest(new { error = "park_not_found" });
            newParkId = pid;
        }
        var rescoped = newParkId != oldParkId;
        if (rescoped && oldParkId is { } leaving)
        {
            var leavingName = await _db.Parks.AsNoTracking().Where(p => p.Id == leaving).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "";
            var missing = NeutralTextMissing(body, incident, leavingName);
            if (missing.Count > 0) return BadRequest(new { error = "neutral_text_required", fields = missing, park = leavingName });
        }

        var changes = new List<string>();
        var data = new List<object>();      // the same changes for the console to word in its language
        string? causeText = null;           // its own entry: park managers never read the cause
        object? causeData = null;
        var now = DateTime.UtcNow;

        if (body.Title is not null)
        {
            var title = Blank(body.Title);
            if (title is null) return BadRequest(new { error = "title_required" });
            if (title.Length > 200) return BadRequest(new { error = "title_too_long", max = 200 });
            if (title != incident.Title)
            {
                changes.Add($"Title: “{incident.Title}” → “{title}”");
                data.Add(new { field = "title", from = incident.Title, to = title });
                incident.Title = title;
            }
        }
        if (body.Description is not null)
        {
            var d = Blank(body.Description);
            if (d is { Length: > 4000 }) return BadRequest(new { error = "description_too_long", max = 4000 });
            if (d != incident.Description) { changes.Add("Description updated"); data.Add(new { field = "description" }); incident.Description = d; }
        }
        if (body.Cause is not null)
        {
            var c = Blank(body.Cause);
            if (c is { Length: > 1000 }) return BadRequest(new { error = "cause_too_long", max = 1000 });
            if (c != incident.Cause) { causeText = $"Cause: {c ?? "(cleared)"}"; causeData = new { field = "cause", to = c }; incident.Cause = c; }
        }
        if (body.Level is not null)
        {
            if (!TryParseLevel(body.Level, out var lv)) return BadRequest(new { error = "invalid_level", allowed = Enum.GetNames<IncidentLevel>() });
            if (lv != incident.Level)
            {
                changes.Add($"Level {incident.Level} → {lv}");
                data.Add(new { field = "level", from = incident.Level.ToString(), to = lv.ToString() });
                incident.SetLevel(lv, now);
            }
        }
        if (body.Fault is not null)
        {
            if (!TryParseFault(body.Fault, out var f)) return BadRequest(new { error = "invalid_fault", allowed = Enum.GetNames<IncidentFault>() });
            if (f != incident.Fault)
            {
                changes.Add($"Fault {incident.Fault} → {f}");
                data.Add(new { field = "fault", from = incident.Fault.ToString(), to = f.ToString() });
                incident.Fault = f;
            }
        }
        if (body.StartedAt is { } sa)
        {
            var started = sa.UtcDateTime;
            if (started > now + FutureSkew || started > incident.ReportedAt + FutureSkew)
                return BadRequest(new { error = "started_after_report" });
            if (started > incident.ReportedAt) started = incident.ReportedAt;
            if (TooEarly(started, incident.ReportedAt) is { } tooEarly) return tooEarly;
            if (Math.Abs((started - incident.StartedAt).TotalSeconds) >= 1)
            {
                changes.Add($"Impact start {Local(incident.StartedAt)} → {Local(started)} (Tbilisi)");
                data.Add(new { field = "started", from = incident.StartedAt, to = started });
                incident.StartedAt = started;
            }
        }

        // Up to three entries, saved one by one so the timeline ids keep their order (they share one timestamp).
        // The first two are written for the incident's readers so far (Log takes its park as it is now)…
        if (changes.Count > 0)
        {
            Log(incident, now, "updated", string.Join("; ", changes), new { code = "updated", changes = data });
            await _db.SaveChangesAsync(ct);
        }
        if (causeText is not null)
        {
            Log(incident, now, "updated", causeText, new { code = "updated", changes = new[] { causeData } });
            await _db.SaveChangesAsync(ct);
        }
        // …the move for its readers from now on. It never says where the incident came from.
        if (rescoped)
        {
            incident.ParkId = newParkId;
            var scopeText = $"Scope: {newParkName ?? "all parks"}";
            var detached = false;
            if (incident.DedupeKey is { } key && IsMonitored(key))
            {
                // The condition stays in the key: the monitor opens nothing beside it while it lasts.
                incident.DedupeKey = IncidentMonitor.MovedKey(incident.Id, key);
                scopeText += " (No longer closed automatically.)";
                detached = true;
            }
            Log(incident, now, "updated", scopeText,
                new { code = "updated", changes = new[] { new { field = "scope", to = newParkName, detached } } });
            await _db.SaveChangesAsync(ct);
        }

        var changed = changes.Count > 0 || causeText is not null || rescoped;
        if (changed)
            _log.LogInformation("Incident {Ref} updated by {Actor}: {Changes}{Cause}{Scope}", incident.Ref, ActorLabel,
                string.Join("; ", changes), causeText is null ? "" : " [cause]", rescoped ? $" [scope → {newParkName ?? "all parks"}]" : "");

        var dto = await DetailAsync(incident, ct);
        return Ok(new { changed, incident = dto });
    }

    /// <summary>
    /// What a move away from a park still lacks: a title and a description, each given in the request,
    /// different from the current one and not naming the park the incident leaves (as a word, any case).
    /// </summary>
    private static List<string> NeutralTextMissing(UpdateIncidentRequest body, Incident incident, string leavingPark)
    {
        static bool Names(string text, string park) =>
            !string.IsNullOrWhiteSpace(park)
            && Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(park.Trim())}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var missing = new List<string>();
        var title = Blank(body.Title);
        if (title is null || title == incident.Title || Names(title, leavingPark)) missing.Add("title");
        var description = Blank(body.Description);
        if (description is null || description == incident.Description || Names(description, leavingPark)) missing.Add("description");
        return missing;
    }

    /// <summary>A key the monitor still owns (not detached by a reopen or a move).</summary>
    private static bool IsMonitored(string key) =>
        key.StartsWith(IncidentMonitor.PayoutQueueKeyPrefix, StringComparison.Ordinal)
        || key.StartsWith(IncidentMonitor.PayoutQueueWideKeyPrefix, StringComparison.Ordinal)
        || key == IncidentMonitor.YandexOutageKey;

    /// <summary>The owner's response (Annex 1 §2.2): confirmed, work started, the licensee sees it here. Swich only.</summary>
    [HttpPost("{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, [FromBody] IncidentNoteRequest? body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });
        if (incident.Status == IncidentStatus.Resolved) return Conflict(new { error = "incident_resolved" });
        if (incident.Status == IncidentStatus.Acknowledged) return Conflict(new { error = "already_acknowledged" });
        var note = Blank(body?.Text);
        if (note is { Length: > 2000 }) return BadRequest(new { error = "note_too_long", max = 2000 });

        var now = DateTime.UtcNow;
        incident.Status = IncidentStatus.Acknowledged;
        incident.AcknowledgedAt = now;
        incident.AcknowledgedBy = ActorLabel;
        Log(incident, now, "acknowledged", note ?? "Acknowledged — work has started.", note is null ? new { code = "acknowledged" } : null);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Incident {Ref} acknowledged by {Actor}", incident.Ref, ActorLabel);
        return Ok(await DetailAsync(incident, ct));
    }

    /// <summary>
    /// External cause only: the bank / Yandex / park balance is back, so the resolution clock starts
    /// (Annex 1 §3.2). <c>at</c> may be earlier than now (when it actually came back) — never later, and
    /// never before the impact began. Swich only.
    /// </summary>
    [HttpPost("{id:guid}/external-restored")]
    public async Task<IActionResult> ExternalRestored(Guid id, [FromBody] ExternalRestoredRequest? body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });
        if (!IncidentSla.IsExternalCause(incident.Fault)) return Conflict(new { error = "not_external" });
        if (incident.Status == IncidentStatus.Resolved) return Conflict(new { error = "incident_resolved" });
        if (incident.ExternalRestoredAt is not null) return Conflict(new { error = "already_restored" });
        var note = Blank(body?.Text);
        if (note is { Length: > 2000 }) return BadRequest(new { error = "note_too_long", max = 2000 });

        var now = DateTime.UtcNow;
        var at = body?.At?.UtcDateTime ?? now;
        if (at > now + FutureSkew || at < incident.StartedAt) return BadRequest(new { error = "invalid_time" });
        if (at > now) at = now;

        incident.ExternalRestoredAt = at;
        incident.ExternalRestoredBy = ActorLabel;
        var earlier = Math.Abs((now - at).TotalMinutes) >= 1;
        Log(incident, now, "external_restored",
            (earlier ? $"External service back since {Local(at)} (Tbilisi). " : "External service back. ") + (note ?? ""),
            new { code = "external_restored", since = earlier ? at : (DateTime?)null, note });
        await _db.SaveChangesAsync(ct);
        return Ok(await DetailAsync(incident, ct));
    }

    /// <summary>Fixed. Swich only. The time is when it is recorded — the timeline shows both.</summary>
    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] IncidentNoteRequest? body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });
        if (incident.Status == IncidentStatus.Resolved) return Conflict(new { error = "already_resolved" });
        var note = Blank(body?.Text);
        if (note is { Length: > 2000 }) return BadRequest(new { error = "note_too_long", max = 2000 });

        var now = DateTime.UtcNow;
        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = now;
        incident.ResolvedBy = ActorLabel;
        Log(incident, now, "resolved", note ?? "Resolved.", note is null ? new { code = "resolved" } : null);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Incident {Ref} resolved by {Actor}", incident.Ref, ActorLabel);
        return Ok(await DetailAsync(incident, ct));
    }

    /// <summary>
    /// It was not fixed after all. Swich only, reason required. Returns to Acknowledged (or Reported if it
    /// was never acknowledged); restoration and acknowledgement times stay — they happened. A reopened auto
    /// incident is detached from the monitor so it is not closed again automatically, and holds no condition:
    /// a moved one that was resolved may have been followed by a fresh incident for the same queue, which keeps it.
    /// </summary>
    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, [FromBody] IncidentNoteRequest? body, CancellationToken ct)
    {
        if (!IsSuperAdmin) return Forbid();
        var reason = Blank(body?.Text);
        if (reason is null) return BadRequest(new { error = "reason_required" });
        if (reason.Length > 2000) return BadRequest(new { error = "note_too_long", max = 2000 });
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });
        if (incident.Status != IncidentStatus.Resolved) return Conflict(new { error = "not_resolved" });

        var now = DateTime.UtcNow;
        incident.Status = incident.AcknowledgedAt is null ? IncidentStatus.Reported : IncidentStatus.Acknowledged;
        incident.ResolvedAt = null;
        incident.ResolvedBy = null;
        var text = reason;
        object? data = null;
        if (incident.DedupeKey is { } key && IsMonitored(key))
        {
            // Keeps the condition in the key (the console still knows what the monitor saw), frees the
            // original key so the monitor can open a fresh incident if the condition comes back.
            incident.DedupeKey = $"reopened:{incident.Id:N}:{key}";
            text += " (No longer closed automatically.)";
            data = new { code = "reopened_detached", reason };
        }
        else if (IncidentMonitor.MovedCondition(incident.DedupeKey) is { } held)
        {
            // Moved earlier, so already out of the monitor's hands (its scope entry said so): it only stops holding the condition.
            incident.DedupeKey = $"reopened:{incident.Id:N}:{held}";
        }
        Log(incident, now, "reopened", text, data);
        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Incident {Ref} reopened by {Actor}: {Reason}", incident.Ref, ActorLabel, reason);
        return Ok(await DetailAsync(incident, ct));
    }

    /// <summary>Progress note on the timeline. Swich and the licensee; open or resolved.</summary>
    [HttpPost("{id:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid id, [FromBody] IncidentNoteRequest? body, CancellationToken ct)
    {
        if (!IsSwichOrOperator) return Forbid();
        var text = Blank(body?.Text);
        if (text is null) return BadRequest(new { error = "text_required" });
        if (text.Length > 2000) return BadRequest(new { error = "note_too_long", max = 2000 });
        var incident = await Visible(_db.Incidents).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound(new { error = "incident_not_found" });

        Log(incident, DateTime.UtcNow, "note", text);
        await _db.SaveChangesAsync(ct);
        return Ok(await DetailAsync(incident, ct));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The impact start may lie before the report, but only so far (§5(b) sums P1 hours from it, month by
    /// month). The licensee gets <see cref="IncidentOptions.LicenseeMaxBackdateHours"/>. Swich gets
    /// <see cref="IncidentOptions.OwnerMaxBackdateDays"/>, because an earlier start only lengthens its own
    /// hours. Null when the start is acceptable.
    /// </summary>
    private IActionResult? TooEarly(DateTime startedUtc, DateTime reportedUtc)
    {
        var max = IsSuperAdmin
            ? TimeSpan.FromDays(Math.Max(0, _opts.OwnerMaxBackdateDays))
            : TimeSpan.FromHours(Math.Max(0, _opts.LicenseeMaxBackdateHours));
        if (startedUtc >= reportedUtc - max) return null;
        return BadRequest(new { error = "started_too_early", maxHours = max.TotalHours, earliest = reportedUtc - max });
    }

    /// <summary>
    /// Swich: every incident. The operator and a park manager: those of their parks (the licensee's, the manager's own)
    /// plus the platform-wide ones; with no park in scope, only the latter.
    /// </summary>
    private IQueryable<Incident> Visible(IQueryable<Incident> q)
    {
        if (ParkFilter is not { } parks) return q;
        return q.Where(i => i.ParkId == null || parks.Contains(i.ParkId.Value));
    }

    /// <summary>
    /// One timeline entry. <paramref name="data"/> (a code plus its figures) is given when the system words
    /// the entry, so the console can show it in Georgian; a person's own words go without it and are shown
    /// as typed. The English text stays the record either way. The entry carries the incident's park as it is
    /// at this moment — who may read it (<see cref="IncidentUpdate.ParkId"/>).
    /// </summary>
    private void Log(Incident incident, DateTime at, string kind, string text, object? data = null) =>
        incident.Updates.Add(new IncidentUpdate
        {
            IncidentId = incident.Id,
            ParkId = incident.ParkId,
            At = at,
            By = ActorLabel,
            Kind = kind,
            Text = text.Length <= 4000 ? text.Trim() : text[..4000],
            Data = data is null ? null : JsonSerializer.Serialize(data),
        });

    private async Task<Dictionary<Guid, string>> ParkNamesAsync(IEnumerable<Incident> rows, CancellationToken ct)
    {
        var ids = rows.Where(i => i.ParkId != null).Select(i => i.ParkId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Parks.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
    }

    private async Task<object> DetailAsync(Incident incident, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var names = await ParkNamesAsync(new[] { incident }, ct);
        var entries = _db.IncidentUpdates.AsNoTracking().Where(u => u.IncidentId == incident.Id);
        if (ParkFilter is { } parks)
        {
            // A park manager — and the operator — reads what was written while the incident was visible to its parks: once
            // park B's incident is moved to all parks (or merged into a platform-wide one), the others read it from then
            // on, never B's figures, failure codes or old title.
            entries = entries.Where(u => u.ParkId == null || parks.Contains(u.ParkId.Value));
        }
        var timeline = await entries
            .OrderBy(u => u.At).ThenBy(u => u.Id)
            .Select(u => new { u.At, u.By, u.Kind, u.Text, u.Data })
            .ToListAsync(ct);
        // …and never the cause: a raw failure reason or Swich's diagnosis of someone's account is not theirs to read.
        if (!IsSwichOrOperator) timeline = timeline.Where(u => !IsCauseOnly(u.Data)).ToList();
        var external = IncidentSla.IsExternalCause(incident.Fault);

        return new
        {
            incident = Summary(incident, names, Sla, now),
            description = incident.Description,
            timeline = timeline.Select(u => new { at = u.At, by = Actor(u.By), kind = u.Kind, text = u.Text, data = TimelineData(u.Data) }),
            can = new
            {
                acknowledge = IsSuperAdmin && incident.Status == IncidentStatus.Reported,
                restoreExternal = IsSuperAdmin && incident.Status != IncidentStatus.Resolved && external && incident.ExternalRestoredAt is null,
                resolve = IsSuperAdmin && incident.Status != IncidentStatus.Resolved,
                reopen = IsSuperAdmin && incident.Status == IncidentStatus.Resolved,
                edit = IsSuperAdmin,
                note = IsSwichOrOperator,
            },
            asOf = now,
        };
    }

    private object Summary(Incident i, IReadOnlyDictionary<Guid, string> parkNames, IncidentSla sla, DateTime now)
    {
        var s = sla.Evaluate(i, now);
        var autoKind = AutoKindOf(i.DedupeKey);
        return new
        {
            id = i.Id,
            number = i.Number,
            @ref = i.Ref,
            level = i.Level.ToString(),
            // When the incident reached its level: its report, or a later raise — the start of its clocks.
            levelSince = IncidentSla.LevelClockStart(i),
            status = i.Status.ToString(),
            title = i.Title,
            // Swich and the licensee only: the cause can be another account's failure detail or Swich's diagnosis.
            cause = IsSwichOrOperator ? i.Cause : null,
            fault = i.Fault.ToString(),
            source = i.Source.ToString(),
            autoKind,
            parkId = i.ParkId,
            parkName = i.ParkId is { } p ? parkNames.GetValueOrDefault(p) : null,
            startedAt = i.StartedAt,
            reportedAt = i.ReportedAt,
            reportedBy = Actor(i.ReportedBy),
            acknowledgedAt = i.AcknowledgedAt,
            acknowledgedBy = Actor(i.AcknowledgedBy),
            externalRestoredAt = i.ExternalRestoredAt,
            externalRestoredBy = Actor(i.ExternalRestoredBy),
            resolvedAt = i.ResolvedAt,
            resolvedBy = Actor(i.ResolvedBy),
            durationMinutes = Math.Round(((i.ResolvedAt ?? now) - i.StartedAt).TotalMinutes),
            sla = new
            {
                response = Clock(s.Response, null),
                resolution = Clock(s.Resolution, s.Resolution.State == SlaState.Paused ? WaitingFor(i, autoKind) : null),
            },
        };
    }

    /// <summary>An "updated" entry whose only change is the cause (Update writes those on their own).</summary>
    private static bool IsCauseOnly(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code) || code.GetString() != "updated"
                || !root.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                return false;
            var fields = changes.EnumerateArray()
                .Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("field", out var f) ? f.GetString() : null)
                .ToList();
            return fields.Count > 0 && fields.All(f => f == "cause");
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The stored JSON as an object for the response; null for a person's words (or a row that cannot be read).</summary>
    private static JsonElement? TimelineData(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static object Clock(SlaClock c, string? waitingFor) => new
    {
        state = c.State.ToString(),
        startsAt = c.StartsAt,
        dueAt = c.DueAt,
        doneAt = c.DoneAt,
        marginMinutes = c.MarginMinutes,
        waitingFor,
    };

    private static object Citation(IncidentCitation c) => new
    {
        id = c.Id,
        @ref = c.Ref,
        level = c.Level.ToString(),
        fault = c.Fault.ToString(),
        reportedAt = c.ReportedAt,
        resolvedAt = c.ResolvedAt,
        resolution = c.Resolution.ToString(),
        hours = c.Hours,
    };

    /// <summary>
    /// What a paused resolution clock is waiting for — the console words its chip after it. Auto incidents
    /// know; for a person's cause text ("TBC gateway returns 503") a keyword is a good enough hint.
    /// </summary>
    private static string WaitingFor(Incident i, string? autoKind)
    {
        if (i.Fault == IncidentFault.Park) return "park";
        if (autoKind == "yandex_outage") return "yandex";
        if (autoKind is "payout_queue" or "payout_queue_wide") return "bank";
        var code = IncidentMonitor.CodeOf(i.Cause);
        if (code is not null && IncidentMonitor.FaultForCode(code) == IncidentFault.External) return "bank";
        var text = $"{i.Cause} {i.Title}";
        if (text.Contains("yandex", StringComparison.OrdinalIgnoreCase)) return "yandex";
        if (new[] { "TBC", "BOG", "bank" }.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))) return "bank";
        return "external";
    }

    /// <summary>What the monitor watched; detached keys ("reopened:…", "rescoped:…", "released:…") keep the condition inside.</summary>
    private static string? AutoKindOf(string? key) =>
        key is null ? null
        : key.Contains(IncidentMonitor.PayoutQueueWideKeyPrefix, StringComparison.Ordinal) ? "payout_queue_wide"
        : key.Contains(IncidentMonitor.PayoutQueueKeyPrefix, StringComparison.Ordinal) ? "payout_queue"
        : key.Contains(IncidentMonitor.YandexOutageKey, StringComparison.Ordinal) ? "yandex_outage"
        : null;

    /// <summary>Park managers see that Swich staff acted, not which person (same rule as park status).</summary>
    private string? Actor(string? by) =>
        by is null ? null : !IsSwichOrOperator && by.StartsWith("admin:", StringComparison.Ordinal) ? "admin" : by;

    private string Local(DateTime utc) => Sla.ToLocal(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static bool TryParseLevel(string? s, out IncidentLevel level) =>
        Enum.TryParse(s?.Trim(), ignoreCase: true, out level) && Enum.IsDefined(level) && !int.TryParse(s, out _);

    private static bool TryParseFault(string? s, out IncidentFault fault) =>
        Enum.TryParse(s?.Trim(), ignoreCase: true, out fault) && Enum.IsDefined(fault) && !int.TryParse(s, out _);

    /// <summary>
    /// The settlements' parser, years 2000–2100. An unbounded year reached
    /// <c>new DateTime(9999, 12, 1).AddMonths(1)</c> in the month bounds and threw: a 500 for any admin.
    /// </summary>
    private static bool TryParseMonth(string s, out int year, out int month)
    {
        year = month = 0;
        if (!SettlementDtos.TryParseMonth(s.Trim(), out var first)) return false;
        (year, month) = (first.Year, first.Month);
        return true;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public record CreateIncidentRequest(
    string? Title, string? Level, string? Description, string? Fault, string? Cause,
    Guid? ParkId, DateTimeOffset? StartedAt, bool? Acknowledge);

public record UpdateIncidentRequest(
    string? Title, string? Description, string? Cause, string? Level, string? Fault,
    Guid? ParkId, bool? PlatformWide, DateTimeOffset? StartedAt);

public record IncidentNoteRequest(string? Text);

public record ExternalRestoredRequest(DateTimeOffset? At, string? Text);
