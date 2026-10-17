using System.Globalization;
using Microsoft.Extensions.Configuration;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// "Incidents" configuration: the SLA table of Annex 1 §2, the contract thresholds of §5–§6 and the
/// incident monitor. The bracketed numbers in the annex ([3], [48], [72], [14], [5]) are still being
/// negotiated, so they live here rather than in code.
/// </summary>
public class IncidentOptions
{
    public const string SectionName = "Incidents";

    public IncidentSlaPolicy Sla { get; set; } = new();

    /// <summary>§5(a): this many owner-fault P1 incidents not resolved within target in one calendar month = chronic breach.</summary>
    public int ChronicBreachP1Count { get; set; } = 3;

    /// <summary>§5(b): owner-fault P1 incidents lasting more than this many hours in total in one calendar month = chronic breach.</summary>
    public double ChronicBreachP1Hours { get; set; } = 48;

    /// <summary>§6(a): no response to a P1 for more than this many hours = abandonment (subject to notice and cure).</summary>
    public double AbandonmentResponseHours { get; set; } = 72;

    /// <summary>§6(b): this many consecutive days without support. Not measurable from the log — shown as a note only.</summary>
    public int AbandonmentSupportDays { get; set; } = 14;

    /// <summary>§6: days the owner has to cure after the licensee's written notice. Informational (no notice is recorded here).</summary>
    public int AbandonmentCureDays { get; set; } = 5;

    /// <summary>
    /// How far before its report the licensee may say an incident's impact began. The impact start drives
    /// §5(b): once Swich calls a P1 its fault, every hour since the start counts, in whatever month it falls.
    /// The licensee is the party that gains from a chronic-breach finding, so an unbounded start would let a
    /// report backdated to January fill every month since with owner-fault hours. Three days covers
    /// "it started on Saturday, we noticed on Monday".
    /// </summary>
    public double LicenseeMaxBackdateHours { get; set; } = 72;

    /// <summary>
    /// Swich's own bound for the impact start, on a report or in an edit (the post-mortem often finds an
    /// earlier start). An earlier start only adds to Swich's own P1 hours, so the bound is a guard against a
    /// mistyped year, not a limit on honesty.
    /// </summary>
    public int OwnerMaxBackdateDays { get; set; } = 31;

    public IncidentMonitorOptions Monitor { get; set; } = new();
}

/// <summary>
/// The response / resolution table of Annex 1 §2 plus the business calendar it needs.
///
/// Business hours default to Mon–Fri 09:00–18:00 Tbilisi time. The annex does not define them; this is an
/// assumption to confirm with the lawyer. Public holidays are NOT pre-loaded: list them in
/// Incidents:Sla:Holidays ("2027-01-01", …) once the parties agree which ones count.
/// </summary>
public class IncidentSlaPolicy
{
    /// <summary>Time zone for the 00:00–09:00 window and business hours. Null = Tbilisi (same resolution as settlements).</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>P1 response when reported 09:00–24:00 (P1 runs 24/7: calendar hours).</summary>
    public double P1ResponseHours { get; set; } = 4;

    /// <summary>P1 response when reported in the night window 00:00–09:00.</summary>
    public double P1ResponseNightHours { get; set; } = 12;

    public TimeSpan P1NightStart { get; set; } = TimeSpan.Zero;
    public TimeSpan P1NightEnd { get; set; } = TimeSpan.FromHours(9);

    /// <summary>P1 resolution target, calendar hours.</summary>
    public double P1ResolutionHours { get; set; } = 24;

    /// <summary>P2 response, business hours.</summary>
    public double P2ResponseBusinessHours { get; set; } = 8;

    /// <summary>P2 resolution, business days — counted as that many full business days of business hours (3 × 9 h).</summary>
    public int P2ResolutionBusinessDays { get; set; } = 3;

    public TimeSpan BusinessDayStart { get; set; } = TimeSpan.FromHours(9);
    public TimeSpan BusinessDayEnd { get; set; } = TimeSpan.FromHours(18);

    /// <summary>
    /// Working weekdays. Empty = Monday–Friday. (Left empty by default on purpose: the configuration binder
    /// appends to a pre-filled list instead of replacing it.)
    /// </summary>
    public List<DayOfWeek> BusinessDays { get; set; } = new();

    /// <summary>Non-working dates (Tbilisi calendar). None by default — see the class comment.</summary>
    public List<DateOnly> Holidays { get; set; } = new();
}

public class IncidentMonitorOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>A park with a payout Queued for longer than this gets an auto incident.</summary>
    public int QueueAgeMinutes { get; set; } = 15;

    /// <summary>
    /// How many distinct drivers of a park must have a payout waiting before the auto incident is a P1.
    /// Annex 1 §2.1 says P1 means cashouts fail "for all or most drivers". Below this, or while more of
    /// the park's drivers are still being paid than are waiting, it is a P2.
    /// </summary>
    public int P1MinWaitingDrivers { get; set; } = 3;

    /// <summary>
    /// How many parks one external or platform cause (the same bank failing, our missing adapter for one bank,
    /// Yandex not answering) must hold at once before it is ONE platform-wide P1 instead of a P2 per park —
    /// "all or most drivers" counted across parks (Annex 1 §2.1). Never fewer than two. The park's side never
    /// aggregates. See <see cref="IncidentMonitor"/>.
    /// </summary>
    public int P1MinParks { get; set; } = 2;

    /// <summary>Platform-wide Yandex Fleet outage detector (from the API audit log).</summary>
    public bool YandexOutageEnabled { get; set; } = true;

    /// <summary>Look-back window for the Yandex detector.</summary>
    public int YandexWindowMinutes { get; set; } = 10;

    /// <summary>No successful Yandex call and at least this many server/transport failures in the window = outage.</summary>
    public int YandexMinFailures { get; set; } = 5;
}

/// <summary>
/// <see cref="IContractCalendar"/> from configuration: Contract:LaunchDate ("yyyy-MM-dd") and
/// Contract:TrialDays (default 14, agreement 8.5.1). Read on every access so a config reload applies.
/// </summary>
public sealed class ConfigurationContractCalendar : IContractCalendar
{
    private readonly IConfiguration _config;
    public ConfigurationContractCalendar(IConfiguration config) => _config = config;

    public DateOnly? LaunchDate =>
        DateOnly.TryParseExact(_config["Contract:LaunchDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    public int TrialDays => Math.Max(0, _config.GetValue("Contract:TrialDays", 14));

    /// <summary>Configuration knows no licensee record.</summary>
    public Guid? LicenseeId => null;
}
