namespace PayTaxi.Core.Entities;

/// <summary>
/// One entry in the licence agreement's change history (§2.6: both sides see identical data). Every change to
/// what the monthly statement is computed from — the launch date, the barrier and share, and each shared-cost
/// invoice (added, edited, deleted, put on hold, released, corrected) — is recorded here with who did it, when,
/// the value before and after, and why.
///
/// Append-only: no endpoint updates or deletes these rows, and the DbContext refuses to save a change to one.
/// Without it a cost deleted or a launch date moved the night before a statement leaves no trace the other
/// side can point to.
///
/// <see cref="OldValue"/> / <see cref="NewValue"/> are JSON snapshots (a cost's fields, {"launchDate":…},
/// {"barrierGel":…}) so the console can render them in the reader's language; the JSON itself stays readable.
/// </summary>
public class AgreementEvent
{
    public long Id { get; set; }
    public Guid LicenseeId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>"admin:{email}" of whoever made the change, or "seed".</summary>
    public string Actor { get; set; } = default!;

    /// <summary>One of <see cref="AgreementEventKinds"/> (text with a check constraint).</summary>
    public string Kind { get; set; } = default!;

    /// <summary>The shared cost the entry is about; null for the licensee's own terms.</summary>
    public Guid? ReferenceId { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>The reason given — required for a launch date, a deletion, a hold and a correction.</summary>
    public string? Reason { get; set; }

    public Licensee Licensee { get; set; } = default!;
}

/// <summary>The kinds of <see cref="AgreementEvent"/>.</summary>
public static class AgreementEventKinds
{
    /// <summary>The launch date was set, moved or cleared (only while it had not passed; always with a reason).</summary>
    public const string LaunchDate = "launch_date";
    /// <summary>The barrier, Swich's share or whether the agreement is in force changed.</summary>
    public const string Terms = "terms";
    public const string CostAdded = "cost_added";
    public const string CostEdited = "cost_edited";
    /// <summary>Soft delete by the cost's owner, before any statement included it.</summary>
    public const string CostDeleted = "cost_deleted";
    /// <summary>The other side disputes the cost: no statement includes it until it is released.</summary>
    public const string CostHeld = "cost_held";
    public const string CostReleased = "cost_released";
    /// <summary>A correction entry for an already-included cost (the next statement includes it).</summary>
    public const string CorrectionAdded = "correction_added";

    /// <summary>
    /// Every kind (the check constraint CK_AgreementEvents_Kind lists the same; CK_AgreementEvents_Reason refuses a launch
    /// date, a deletion, a hold or a correction recorded without a reason).
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        LaunchDate, Terms, CostAdded, CostEdited, CostDeleted, CostHeld, CostReleased, CorrectionAdded,
    };
}
