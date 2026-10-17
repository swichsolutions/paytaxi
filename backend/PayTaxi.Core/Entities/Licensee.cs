namespace PayTaxi.Core.Entities;

/// <summary>
/// The exclusive operator under the licence agreement with Swich (Levan's company). Every park the
/// licensee connects is settled TOGETHER, once a month, under the agreement's rules (§2.2–2.5):
/// the month's fee income of all its parks, minus the shared costs of §2.4, split by
/// <see cref="SwichSharePercent"/> — after the 20,000 GEL barrier of §2.3 has been filled.
/// Parks Swich connects directly (<see cref="Park.LicenseeId"/> null) are outside the agreement
/// and keep their own per-park split.
/// </summary>
public class Licensee : BaseEntity
{
    /// <summary>Legal name as in the agreement (Georgian), e.g. "შპს …".</summary>
    public string Name { get; set; } = default!;

    /// <summary>Georgian tax-payer id (s/k).</summary>
    public string? TaxId { get; set; }

    /// <summary>Where the monthly report goes (§2.6); also the address that confirms the launch date.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// The licensee's own account — for reference on the report only. Swich never takes money from
    /// it: the platform collects from the parks' accounts (§2.5, Annex 3 §3). When a park cannot pay,
    /// the licensee settles that debt itself (§4.1.2).
    /// </summary>
    public string? Iban { get; set; }

    /// <summary>
    /// "Launch date": the first day the platform was available to drivers, confirmed by e-mail.
    /// The barrier counts from here, the §7 trial runs 14 days from it, and no statement is computed for a
    /// month before it. Null = not launched yet — fees accumulate unsettled and roll into the first statement.
    /// Set or moved only while it has not passed (Tbilisi day), with a reason, and logged as an
    /// <see cref="AgreementEvent"/>; from the day after it, locked.
    /// </summary>
    public DateOnly? LaunchDate { get; set; }

    /// <summary>§2.3: Swich takes the whole net income until it has been entitled to this much. 20,000 GEL.</summary>
    public decimal BarrierGel { get; set; } = 20_000m;

    /// <summary>§2.2: Swich's percentage of the net income after the barrier. 50.</summary>
    public decimal SwichSharePercent { get; set; } = 50m;

    /// <summary>The agreement is in force. Settlement keeps running regardless — fees earned while it ran are still owed (§8.4).</summary>
    public bool IsActive { get; set; } = true;

    public ICollection<Park> Parks { get; set; } = new List<Park>();
    public ICollection<CommonCost> Costs { get; set; } = new List<CommonCost>();
    public ICollection<MonthlyStatement> Statements { get; set; } = new List<MonthlyStatement>();
    public ICollection<AgreementEvent> Events { get; set; } = new List<AgreementEvent>();
}
