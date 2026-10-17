using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// One row of the incident log the licence agreement requires (Annex 1 §4): time, level, cause,
/// fault, response and resolution times — visible to the licensee in the admin console. The log is
/// the evidence for chronic breach (§5), abandonment (§6) and the trial-period release (§7), so every
/// change is also written to the append-only <see cref="IncidentUpdate"/> timeline with who did it.
///
/// Clocks (see IncidentSla): the response clock starts when the incident reached its current level —
/// <see cref="ReportedAt"/> unless it was raised later (<see cref="ReachedP1At"/>, <see cref="ReachedP2At"/>);
/// the resolution clock too, except for an external cause (bank, Yandex, park balance), where it starts at
/// <see cref="ExternalRestoredAt"/>.
/// </summary>
public class Incident : BaseEntity
{
    /// <summary>Human number, shown as INC-0042. Assigned by the database (IncidentNumberSeq) on insert.</summary>
    public int Number { get; set; }

    /// <summary>Change it through <see cref="SetLevel"/>, which records when a level was first reached.</summary>
    public IncidentLevel Level { get; set; } = IncidentLevel.P2;
    public IncidentStatus Status { get; set; } = IncidentStatus.Reported;

    /// <summary>
    /// The FIRST time the incident was a P1 (its report, if it was reported as one). The P1 response and
    /// resolution targets and the §5/§6 figures count from here: a P2 raised to P1 at hour 60 of a long wait
    /// has not been an unanswered P1 for 60 hours. Set once and never moved, so lowering a P1 and raising it
    /// again cannot restart its clocks. Null = never a P1 (or a row older than this column: the report counts).
    /// </summary>
    public DateTime? ReachedP1At { get; set; }

    /// <summary>The first time the incident was a P2 or worse — the same rule for a P3 raised to P2.</summary>
    public DateTime? ReachedP2At { get; set; }

    public string Title { get; set; } = default!;
    public string? Description { get; set; }

    /// <summary>What caused it, once known (free text; auto incidents put the dominant failure code here).</summary>
    public string? Cause { get; set; }

    public IncidentFault Fault { get; set; } = IncidentFault.Unknown;

    /// <summary>The affected park, or null when the incident is platform-wide.</summary>
    public Guid? ParkId { get; set; }

    /// <summary>When the impact began (may be earlier than the report, e.g. the oldest stuck payout).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When it entered the log (for auto incidents: when the monitor detected it). Starts the response clock.</summary>
    public DateTime ReportedAt { get; set; }
    public string ReportedBy { get; set; } = default!;

    /// <summary>The owner's response: confirmed, work started, licensee informed (Annex 1 §2.2).</summary>
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }

    /// <summary>External cause only: when the bank / Yandex / park balance was back. Starts the resolution clock (§3.2).</summary>
    public DateTime? ExternalRestoredAt { get; set; }
    public string? ExternalRestoredBy { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    public IncidentSource Source { get; set; } = IncidentSource.Manual;

    /// <summary>
    /// Auto incidents only: what the monitor watches, e.g. "payout-queue:{parkId}". At most one open
    /// incident per key (filtered unique index), so two API instances cannot open the same one twice.
    /// Cleared when a person reopens an auto incident — from then on people, not the monitor, close it.
    /// </summary>
    public string? DedupeKey { get; set; }

    public Park? Park { get; set; }
    public ICollection<IncidentUpdate> Updates { get; set; } = new List<IncidentUpdate>();

    /// <summary>"INC-0042".</summary>
    public string Ref => FormatRef(Number);

    public static string FormatRef(int number) => $"INC-{number:0000}";

    /// <summary>
    /// Sets the level and remembers when it was first reached (<see cref="ReachedP1At"/> /
    /// <see cref="ReachedP2At"/>). Used for the report too (<paramref name="at"/> = the report time), so a
    /// P1 from the start counts from its report.
    /// </summary>
    public void SetLevel(IncidentLevel level, DateTime at)
    {
        // A later change to a row without the level times (written without SetLevel): it has been at its level since
        // its report — the clocks already read it so — so that is recorded first, or lowering and raising it again
        // would hand it fresh clocks.
        if (at > ReportedAt)
        {
            if (Level == IncidentLevel.P1) ReachedP1At ??= ReportedAt;
            if (Level is IncidentLevel.P1 or IncidentLevel.P2) ReachedP2At ??= ReportedAt;
        }
        Level = level;
        if (level == IncidentLevel.P1) ReachedP1At ??= at;
        if (level is IncidentLevel.P1 or IncidentLevel.P2) ReachedP2At ??= at;
    }
}
