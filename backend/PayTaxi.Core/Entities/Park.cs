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

    /// <summary>Park's contact phone — collected at onboarding, used on invoices.</summary>
    public string? Phone { get; set; }

    // ── Operating model ───────────────────────────────────────────────
    /// <summary>
    /// How money moves for this park's cashouts. See <see cref="OperatingModel"/>.
    /// Launch default is Model A: the park pays its drivers from its own bank account(s)
    /// via the park's own bank API credentials — PayTaxi never holds funds.
    /// </summary>
    public OperatingModel OperatingModel { get; set; } = OperatingModel.ModelA;

    // ── Fee & limits (per-park config, see PAYTAXI-CONTEXT.md §2, §5) ─
    /// <summary>Flat fee in GEL charged to the driver per cashout. Launch value 0.50.</summary>
    public decimal CashoutFee { get; set; } = 0.50m;

    /// <summary>Smallest gross cashout amount a driver may request.</summary>
    public decimal MinCashoutAmount { get; set; } = 5m;

    /// <summary>Largest gross cashout amount per request. Null = no cap.</summary>
    public decimal? MaxCashoutAmount { get; set; }

    /// <summary>Per-driver rolling-day gross cashout ceiling. Null = no cap.</summary>
    public decimal? DailyCashoutLimitPerDriver { get; set; }

    // ── Yandex Fleet ──────────────────────────────────────────────────
    public string YandexClientIdEncrypted { get; set; } = default!;
    public string YandexApiKeyEncrypted { get; set; } = default!;
    public string YandexParkId { get; set; } = default!;

    // ── Bank (legacy single-provider columns) ─────────────────────────
    /// <summary>
    /// Legacy: identifier for the park's primary bank integration. Superseded by
    /// <see cref="BankAccounts"/> (one row per bank the park holds an account at).
    /// Kept populated as a mirror of the primary account for reporting queries.
    /// </summary>
    public string BankProvider { get; set; } = default!;

    /// <summary>Legacy column mirroring <see cref="BankProvider"/> upper-cased.</summary>
    public string BankType { get; set; } = default!;

    /// <summary>Legacy credentials blob. Real credentials live on <see cref="ParkBankAccount"/>.</summary>
    public string BankCredentialsEncrypted { get; set; } = default!;

    /// <summary>Display-only IBAN of the park's primary bank account (invoices, reports).</summary>
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
    public ICollection<ParkBankAccount> BankAccounts { get; set; } = new List<ParkBankAccount>();
}
