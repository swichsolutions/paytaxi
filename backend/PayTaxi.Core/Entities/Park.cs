using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class Park : BaseEntity
{
    // ── Identity ──────────────────────────────────────────────────────
    public string Name { get; set; } = default!;

    /// <summary>
    /// URL-safe slug for subdomain routing ({slug}.paytaxi.ge) and path routing.
    /// Must match ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ — check constraint enforced in DB.
    /// </summary>
    public string Slug { get; set; } = default!;

    /// <summary>Legal entity that owns the park (for invoices, agreements). Nullable until onboarding completes.</summary>
    public string? LegalEntityName { get; set; }

    /// <summary>Georgian tax-payer ID (9-digit), nullable until KYC complete.</summary>
    public string? TaxId { get; set; }

    // ── Operating model ───────────────────────────────────────────────
    /// <summary>How money moves for this park's cashouts. See <see cref="OperatingModel"/>.</summary>
    public OperatingModel OperatingModel { get; set; } = OperatingModel.ModelA5;

    /// <summary>
    /// Model A.5 only: the ceiling the park has authorized PayTaxi to spend on their behalf.
    /// Each cashout decrements this; the park signals PayTaxi to refill it.
    /// Null for Model A (park's own bank API gates the limit instead).
    /// </summary>
    public decimal? AuthorizationLimit { get; set; }

    // ── Yandex Fleet ──────────────────────────────────────────────────
    public string YandexClientIdEncrypted { get; set; } = default!;
    public string YandexApiKeyEncrypted { get; set; } = default!;
    public string YandexParkId { get; set; } = default!;

    // ── Bank ──────────────────────────────────────────────────────────
    /// <summary>
    /// Identifier for which bank/PSP integration to use ("bog", "tbc", "paypro_psp", …).
    /// Free-form string rather than enum — we don't yet know all providers we'll integrate with.
    /// </summary>
    public string BankProvider { get; set; } = default!;

    /// <summary>
    /// Legacy column. Will be replaced by <see cref="BankProvider"/> in a follow-up migration
    /// once the codebase no longer references it.
    /// </summary>
    public string BankType { get; set; } = default!;

    /// <summary>Provider-specific credentials blob (JSONB). Structure varies per provider.</summary>
    public string BankCredentialsEncrypted { get; set; } = default!;

    /// <summary>Display-only IBAN of the park's bank account. Reporting and invoices, never used for routing.</summary>
    public string? BankAccountIban { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────
    public ParkStatus Status { get; set; } = ParkStatus.Pending;

    /// <summary>
    /// Legacy boolean kept for backward compatibility during the two-phase migration.
    /// Will be removed in a follow-up migration once no code reads it.
    /// </summary>
    [Obsolete("Use Status (ParkStatus enum) instead. Will be removed after the IsActive→Status cutover.")]
    public bool IsActive { get; set; } = true;

    // ── Navigation ────────────────────────────────────────────────────
    public ICollection<Driver> Drivers { get; set; } = new List<Driver>();
    public ICollection<Cashout> Cashouts { get; set; } = new List<Cashout>();
}
