namespace PayTaxi.Core.Interfaces;

/// <summary>
/// The licence agreement's facts the incident log needs: the launch date, the length of the trial period
/// that starts on it (agreement 8.5.1, Annex 1 §7 — two weeks), and which licensee it is.
///
/// The single seam between the incident log and wherever the launch date lives: the licensee record
/// (LicenseeContractCalendar), with configuration (Contract:LaunchDate / Contract:TrialDays) as the fallback.
/// </summary>
public interface IContractCalendar
{
    /// <summary>The platform's launch date for the licensee (Tbilisi calendar day); null while not launched / unknown.</summary>
    DateOnly? LaunchDate { get; }

    /// <summary>Trial-period length in days, counted from <see cref="LaunchDate"/>.</summary>
    int TrialDays { get; }

    /// <summary>
    /// The licensee whose agreement the contract figures are about; null when none is recorded. Only its
    /// parks' incidents (and platform-wide ones) count: a park Swich connected directly is outside the
    /// agreement (§2.2).
    /// </summary>
    Guid? LicenseeId { get; }
}
