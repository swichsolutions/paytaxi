namespace PayTaxi.Core.Entities;

/// <summary>
/// One change to how a park's money moves or whose balance it is — who made it, when, and which fields. Kept for:
/// a payout account's provider, credentials, active and primary flags (created or edited); the park's Yandex
/// credentials, limits and Swich-only fee and split; a driver's phone, name, Yandex profile link and status.
/// Before this, replacing a park's TBC credentials or switching its payout account's provider left no trace at all, and
/// the park's edits logged "updated by admin" with neither actor nor fields: after a misrouted payout nobody could say
/// who changed the rail, or when (CLAUDE.md: audit every credential rotation).
///
/// Field NAMES only, never values: a credential, a phone or an IBAN must not be copied into a second place. Append-only
/// like <see cref="AgreementEvent"/>: no endpoint changes these rows and the DbContext refuses to save a change to one.
/// Not subject to retention (configuration history, no driver data beyond the subject id).
/// </summary>
public class ParkConfigEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public Guid ParkId { get; set; }

    /// <summary>What was changed: one of <see cref="ParkConfigSubjects"/>.</summary>
    public string Subject { get; set; } = default!;

    /// <summary>The payout account or driver changed; null for the park itself.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>"created" or "updated".</summary>
    public string Action { get; set; } = default!;

    /// <summary>The changed properties, comma-separated (e.g. "provider,credentials") — names, never values.</summary>
    public string ChangedFields { get; set; } = default!;

    /// <summary>"admin:{email}" of whoever made the change (from the token, never from the request body).</summary>
    public string Actor { get; set; } = default!;
}

/// <summary>The subjects of <see cref="ParkConfigEvent"/>.</summary>
public static class ParkConfigSubjects
{
    public const string BankAccount = "bank_account";
    public const string Park = "park";
    public const string Driver = "driver";
}
