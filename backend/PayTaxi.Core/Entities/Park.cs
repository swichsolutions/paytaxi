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

    // ── Revenue split with Swich (PAYTAXI-CONTEXT.md §2, §5) ──────────
    /// <summary>
    /// The licensee (exclusive operator) that connected this park. Set → the park is settled monthly as
    /// part of that licensee's statement under the licence agreement, and the per-park split fields
    /// below are NOT used. Null → a park Swich connected directly: outside the agreement, settled
    /// monthly on its own with the per-park split below.
    /// </summary>
    public Guid? LicenseeId { get; set; }

    /// <summary>Swich-direct parks only: steady-state percentage of every cashout fee that goes to Swich.</summary>
    public decimal SwichSharePercent { get; set; } = 50m;

    /// <summary>Swich-direct parks only: phase-1 percentage while cumulative fees are under <see cref="Phase1CapGel"/>. Null = no phase 1.</summary>
    public decimal? Phase1SharePercent { get; set; }

    /// <summary>Swich-direct parks only: cumulative fee income (GEL) at which phase 1 ends. Null = no phase 1.</summary>
    public decimal? Phase1CapGel { get; set; }

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

    /// <summary>Why the status last changed (required for a suspension; shown in the console).</summary>
    public string? StatusReason { get; set; }

    /// <summary>When the status last changed through the console; null for parks never changed since creation.</summary>
    public DateTime? StatusChangedAt { get; set; }

    /// <summary>"admin:{email}" of whoever last changed the status.</summary>
    public string? StatusChangedBy { get; set; }

    // ── Annex 3 §2/§3 authorisations and their revocation (Annex 3 §7) ─
    // The park authorises the platform to pay its drivers from its account (§2) and to transfer Swich's monthly share
    // from it (§3). It may revoke both by written notice to the licensee and Swich, at least [30] days ahead;
    // obligations that arose before stay. Swich or the operator records the revocation here. From
    // AuthorisationRevokedFrom on (Tbilisi day) the platform initiates NO transfer from the park's account: no new
    // cashout, no waiting payout is sent, no settlement transfer — the share stays owed (by the licensee, §2.5).
    // Independent of Status: a revoked park may stay Active so its drivers and manager keep their history.

    /// <summary>The day the park's written revocation notice was received.</summary>
    public DateOnly? AuthorisationNoticeOn { get; set; }

    /// <summary>
    /// First Tbilisi day without the authorisations — at least the notice period after <see cref="AuthorisationNoticeOn"/>.
    /// Null = authorised. Withdrawable only before it is reached; from then on it stands.
    /// </summary>
    public DateOnly? AuthorisationRevokedFrom { get; set; }

    /// <summary>What the notice says (or where it is filed) — required with a revocation.</summary>
    public string? AuthorisationRevocationReason { get; set; }

    /// <summary>"admin:{email}" of whoever recorded the revocation, and when.</summary>
    public string? AuthorisationRevocationRecordedBy { get; set; }
    public DateTime? AuthorisationRevocationRecordedAt { get; set; }

    /// <summary>
    /// Legacy boolean kept for backward compatibility during the two-phase migration.
    /// Will be removed in a follow-up migration once no code reads it.
    /// </summary>
    [Obsolete("Use Status (ParkStatus enum) instead. Will be removed after the IsActive→Status cutover.")]
    public bool IsActive { get; set; } = true;

    // ── Navigation ────────────────────────────────────────────────────
    public Licensee? Licensee { get; set; }
    public ICollection<Driver> Drivers { get; set; } = new List<Driver>();
    public ICollection<Cashout> Cashouts { get; set; } = new List<Cashout>();
    public ICollection<ParkBankAccount> BankAccounts { get; set; } = new List<ParkBankAccount>();
    public ICollection<Settlement> Settlements { get; set; } = new List<Settlement>();
}
