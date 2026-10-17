namespace PayTaxi.Api.Controllers;

/// <summary>
/// What a client may send as a cashout's idempotency key. The key is also the payout's bank document id (TBC's
/// singlePaymentRequestId is derived from it; the bank refuses a second order under the same id and the adapter then
/// adopts the existing one), and the server builds its own document ids in the same space: the monthly settlement
/// "settle:{park}:{month}" / "settle:{statement}:{park}", the Yandex reversal "rev:{key}", a manager's retry
/// "retry:{cashout}:{n}", and a later document of a payout "{key}#{n}". A client key shaped like one of those could claim
/// the park → Swich transfer's id ahead of it — the settlement would then "complete" on a 4.50 GEL driver payout while
/// Swich received nothing — or make a driver's own second document collide with an earlier payout. Those shapes are
/// refused; every client in the repo sends a UUID (plus amount and destination), which is never affected.
/// </summary>
public static class IdempotencyKeys
{
    public const int MaxLength = 128;

    private static readonly string[] ReservedPrefixes = { "settle:", "rev:", "retry:", "co:", "st:" };

    /// <summary>The error code for a key a client may not use, or null when it is fine.</summary>
    public static string? Problem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "idempotency_key_required";
        if (key.Length > MaxLength) return "idempotency_key_too_long";
        if (key.Contains('#') || ReservedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return "idempotency_key_reserved";
        return null;
    }
}
