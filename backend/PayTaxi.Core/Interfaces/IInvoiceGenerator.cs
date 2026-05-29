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
}
