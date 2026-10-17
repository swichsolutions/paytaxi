namespace PayTaxi.Core.Entities;

/// <summary>
/// One entry on an incident's timeline. Append-only: nothing updates or deletes these rows, so the
/// log the licensee reads cannot be rewritten after the fact.
///
/// Kinds (text with a check constraint, like reconciliation discrepancy kinds):
///   - <c>reported</c>           — the incident entered the log
///   - <c>acknowledged</c>       — the owner's response (Annex 1 §2.2)
///   - <c>updated</c>            — title / description / cause / level / fault / park changed (text says what);
///                                 a cause change and a change of park each get an entry of their own, see
///                                 IncidentsController.Update
///   - <c>note</c>               — free-text progress note
///   - <c>external_restored</c>  — the external service is back; the resolution clock starts
///   - <c>resolved</c>           — fixed
///   - <c>reopened</c>           — it was not fixed after all
/// </summary>
public class IncidentUpdate
{
    public long Id { get; set; }
    public Guid IncidentId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>"admin:{email}" or "system" (the monitor).</summary>
    public string By { get; set; } = default!;

    /// <summary>
    /// The incident's park when the entry was written (null = it was platform-wide then) — who could read it
    /// at the time. A park manager sees only the entries written while the incident was visible to their
    /// park: once Swich moves park B's incident to all parks (or to park C), the other managers see it from
    /// that moment on, never B's queue figures, failure codes or old title from before. A snapshot, like the
    /// rest of the row: moving the incident again does not rewrite it.
    /// </summary>
    public Guid? ParkId { get; set; }

    public string Kind { get; set; } = default!;

    /// <summary>
    /// The entry in English, as written at the time. This is the record. The console shows it as written
    /// unless <see cref="Data"/> lets it render the entry in the reader's language.
    /// </summary>
    public string Text { get; set; } = default!;

    /// <summary>
    /// Entries the system words (the monitor, or a fixed phrase such as "Resolved."): a JSON object
    /// <c>{"code":"auto_queue_opened","payouts":3,…}</c> the console translates, the same pattern as the
    /// driver notifications' Data. Null for a person's own words (notes, a reason, a note on an action),
    /// which are shown exactly as typed.
    /// </summary>
    public string? Data { get; set; }

    public Incident Incident { get; set; } = default!;
}
