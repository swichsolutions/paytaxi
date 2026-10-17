using System.Globalization;
using System.Text.Json;
using PayTaxi.Core.Entities;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The shared-cost rules a statement applies before it counts an invoice (licence agreement §2.4), in one place
/// so the statement, its preview and the console agree:
///   - only an entry that is not included yet, not deleted and not on hold is counted;
///   - and only once <see cref="SettlementOptions.CostCutoffHours"/> have passed since its owner entered it (or
///     last changed it): the other side's window to see it and, if it disputes it, hold it. An invoice typed in at
///     00:25 for a run at 00:30 would otherwise be settled — and frozen — before anyone could look at it.
/// Plus the snapshots the agreement's change history records (<see cref="AgreementEvent"/>).
/// </summary>
public static class SharedCostRules
{
    // ── States (the console's wording keys) ──────────────────────────

    public const string Included = "included";
    public const string Deleted = "deleted";
    public const string Held = "held";
    /// <summary>Not counted yet: entered less than the cut-off window ago.</summary>
    public const string Waiting = "waiting";
    /// <summary>Counted by the next statement computed now.</summary>
    public const string Pending = "pending";

    /// <summary>The first moment a statement may count an entry made at <paramref name="enteredAtUtc"/>.</summary>
    public static DateTime EligibleFrom(DateTime enteredAtUtc, int cutoffHours) => enteredAtUtc.AddHours(Math.Max(0, cutoffHours));

    /// <summary>Entries made at or before this moment are old enough for a statement computed at <paramref name="computedAtUtc"/>.</summary>
    public static DateTime EnteredBy(DateTime computedAtUtc, int cutoffHours) => computedAtUtc.AddHours(-Math.Max(0, cutoffHours));

    /// <summary>Would a statement computed at <paramref name="computedAtUtc"/> count this entry (invoice date aside)?</summary>
    public static bool Countable(CommonCost c, DateTime computedAtUtc, int cutoffHours) =>
        c.StatementId is null && c.DeletedAt is null && c.HeldAt is null && c.EnteredAt <= EnteredBy(computedAtUtc, cutoffHours);

    public static string State(CommonCost c, DateTime nowUtc, int cutoffHours) =>
        c.StatementId is not null ? Included
        : c.DeletedAt is not null ? Deleted
        : c.HeldAt is not null ? Held
        : c.EnteredAt > EnteredBy(nowUtc, cutoffHours) ? Waiting
        : Pending;

    /// <summary>
    /// A waiting entry that will still be inside its window when the next statement is computed: it goes into the one
    /// after (the console says so on the entry). A held one is shown as held — its release decides, not the clock.
    /// </summary>
    public static bool MissesRun(CommonCost c, DateTime nextRunUtc, int cutoffHours) =>
        c.StatementId is null && c.DeletedAt is null && c.HeldAt is null && EligibleFrom(c.EnteredAt, cutoffHours) > nextRunUtc;

    /// <summary>
    /// When the worker next computes a statement: the next <paramref name="runDayOfMonth"/> (clamped to 1–28, as the
    /// worker does) at <paramref name="runAtLocalTime"/> Tbilisi time, strictly after <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTime NextStatementRunUtc(DateTime nowUtc, int runDayOfMonth, string? runAtLocalTime, TimeZoneInfo tz)
    {
        var day = Math.Clamp(runDayOfMonth, 1, 28);
        var at = TimeOnly.TryParse(runAtLocalTime, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(0, 30);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var candidate = new DateTime(nowLocal.Year, nowLocal.Month, day, at.Hour, at.Minute, 0, DateTimeKind.Unspecified);
        if (candidate <= nowLocal) candidate = candidate.AddMonths(1);
        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }

    public static DateTime NextStatementRunUtc(DateTime nowUtc, SettlementOptions opts) =>
        NextStatementRunUtc(nowUtc, opts.RunDayOfMonth, opts.RunAtLocalTime, SettlementService.ResolveTimeZone(opts.TimeZoneId));

    // ── Change-history snapshots ─────────────────────────────────────

    // Georgian vendor names and notes stay readable in the stored record (the API re-parses it either way).
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What an entry says — the value an <see cref="AgreementEvent"/> records before and after a change.</summary>
    public static string Snapshot(CommonCost c) => JsonSerializer.Serialize(new
    {
        paidBy = c.PaidBy.ToString(),
        category = c.Category.ToString(),
        vendor = c.Vendor,
        invoiceNumber = c.InvoiceNumber,
        invoiceDate = c.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        amount = c.Amount,
        note = c.Note,
        correctsId = c.CorrectsId,
        reason = c.Reason,
    }, Json);

    public static string LaunchSnapshot(DateOnly? launch) =>
        JsonSerializer.Serialize(new { launchDate = launch?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, Json);

    public static string TermsSnapshot(Licensee l) =>
        JsonSerializer.Serialize(new { barrierGel = l.BarrierGel, swichSharePercent = l.SwichSharePercent, isActive = l.IsActive }, Json);

    /// <summary>An entry for the agreement's change history (append-only; saved with the change it records).</summary>
    public static AgreementEvent Event(Guid licenseeId, string actor, string kind, Guid? referenceId = null,
        string? oldValue = null, string? newValue = null, string? reason = null, DateTime? at = null) => new()
    {
        LicenseeId = licenseeId,
        At = at ?? DateTime.UtcNow,
        Actor = actor,
        Kind = kind,
        ReferenceId = referenceId,
        OldValue = oldValue,
        NewValue = newValue,
        Reason = reason,
    };
}
