using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

/// <summary>
/// One third-party invoice for a shared cost (licence agreement §2.4): bank services, SMS, payment
/// provider, driver-support communications, hosting, domain. Whoever the invoice is addressed to pays
/// it as it comes; at month end the platform folds the two sides' totals into one equalising amount.
///
/// Each side keeps its own entries: Swich enters, edits and deletes the Swich-paid invoices, the operator the
/// licensee-paid ones — neither can change the other's. What the other side can do is put an entry ON HOLD
/// (disputed); a held entry is not counted until the side that held it releases it, or its owner deletes it.
///
/// A cost enters the first statement computed after its invoice date AND at least Settlement:CostCutoffHours
/// (48) after it was entered (late-entered costs roll forward automatically). From then on it is part of a
/// settled statement and immutable — editing or deleting it would silently change money that has already
/// moved; a mistake is fixed by a CORRECTION entry the next statement includes. Deleting before inclusion
/// is soft: the row stays, with who deleted it and why, and is never counted.
/// </summary>
public class CommonCost : BaseEntity
{
    public Guid LicenseeId { get; set; }

    /// <summary>Swich adds Swich-paid costs, the operator adds licensee-paid ones (fixed by the role that adds it).</summary>
    public CostPaidBy PaidBy { get; set; }

    public CostCategory Category { get; set; }

    /// <summary>Who issued the invoice (bank, SMS gateway, hosting company…).</summary>
    public string Vendor { get; set; } = default!;

    /// <summary>The third-party invoice number — required: §2.4 counts only costs evidenced by a third-party invoice.</summary>
    public string InvoiceNumber { get; set; } = default!;

    public DateOnly InvoiceDate { get; set; }

    /// <summary>
    /// GEL, including any VAT on the vendor's invoice. Above zero for an invoice; a correction's may be negative
    /// (never zero, and never taking the original and its corrections below zero together).
    /// </summary>
    public decimal Amount { get; set; }

    public string? Note { get; set; }

    /// <summary>"admin:{email}" of whoever entered it.</summary>
    public string CreatedBy { get; set; } = default!;

    /// <summary>
    /// When its owner last entered its content: on creation, and again on any edit except to the note. A statement
    /// counts it only once Settlement:CostCutoffHours have passed since — the other side's window to see it and, if
    /// it disputes it, put it on hold. Without it an invoice entered minutes before the run would be settled (and
    /// frozen) before anyone could look at it.
    /// </summary>
    public DateTime EnteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The statement that included it; null until then. Non-null = immutable.</summary>
    public Guid? StatementId { get; set; }

    // ── Correction ───────────────────────────────────────────────────

    /// <summary>
    /// Set on a CORRECTION: the already-included cost it corrects (always the original, never another correction).
    /// It carries the original's side, category, vendor, invoice number and invoice date, and the signed difference
    /// as its amount, so the next statement includes it whatever month the invoice was dated in.
    /// </summary>
    public Guid? CorrectsId { get; set; }

    /// <summary>Why the correction was made — required for a correction.</summary>
    public string? Reason { get; set; }

    // ── On hold (the other side disputes it) ─────────────────────────

    public DateTime? HeldAt { get; set; }
    public string? HeldBy { get; set; }
    public string? HoldReason { get; set; }

    // ── Deleted by its owner (soft; never counted) ───────────────────

    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }

    public Licensee Licensee { get; set; } = default!;
    public MonthlyStatement? Statement { get; set; }
    public CommonCost? Corrects { get; set; }
    public ICollection<CommonCost> Corrections { get; set; } = new List<CommonCost>();
}
