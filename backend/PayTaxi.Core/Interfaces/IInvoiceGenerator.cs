namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Renders a per-cashout invoice as PDF. Layout mirrors the paypro reference:
/// driver is the issuer, park is the recipient, PayTaxi is the commercial
/// intermediary executing the payment.
/// </summary>
public interface IInvoiceGenerator
{
    /// <summary>Render the invoice for a completed cashout. Throws if the cashout is not yet Completed.</summary>
    Task<byte[]> RenderAsync(Guid cashoutId, CancellationToken ct = default);

    /// <summary>Computes the standard filename for this cashout's invoice (no path).</summary>
    Task<string> GetFileNameAsync(Guid cashoutId, CancellationToken ct = default);

    /// <summary>
    /// Render the monthly invoice PT-YYYY-MM from the platform operator (Swich) to one park: every
    /// nightly settlement of that calendar month, what was transferred and what is still outstanding.
    /// Paperwork follows money — the document describes transfers that already happened.
    /// Throws <see cref="InvalidOperationException"/> when the park has no settlement in that month.
    /// </summary>
    Task<byte[]> RenderMonthlyAsync(Guid parkId, int year, int month, CancellationToken ct = default);

    /// <summary>Standard filename of the monthly invoice, e.g. "PT-2026-09-tbilisi-auto-park-3.pdf".</summary>
    string MonthlyFileName(string parkSlug, int year, int month);
}
