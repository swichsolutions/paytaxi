using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// <see cref="IContractCalendar"/> backed by the licensee record — the launch date Swich enters on the
/// Settlements page, the same date the settlement engine counts the 20,000 GEL barrier from, so the
/// incident log's trial period (Annex 1 §7) and the money side can never disagree about it.
///
/// There is one licence agreement today. Should a second licensee ever exist, the contract card follows
/// the active licensee holding the most parks (one with a launch date first). With no licensee or no date
/// yet the date falls back to configuration (Contract:LaunchDate). Scoped: read once per request, lazily.
/// </summary>
public sealed class LicenseeContractCalendar : IContractCalendar
{
    private readonly ConfigurationContractCalendar _fallback;
    private readonly Lazy<(Guid? Id, DateOnly? LaunchDate)> _licensee;

    public LicenseeContractCalendar(AppDbContext db, IConfiguration config)
    {
        _fallback = new ConfigurationContractCalendar(config);
        _licensee = new Lazy<(Guid?, DateOnly?)>(() =>
        {
            var l = db.Licensees.AsNoTracking()
                .Where(x => x.IsActive)
                .OrderByDescending(x => x.LaunchDate != null)
                .ThenByDescending(x => x.Parks.Count)
                .ThenBy(x => x.CreatedAt)
                .Select(x => new { x.Id, x.LaunchDate })
                .FirstOrDefault();
            return (l?.Id, l?.LaunchDate ?? _fallback.LaunchDate);
        });
    }

    public DateOnly? LaunchDate => _licensee.Value.LaunchDate;

    public int TrialDays => _fallback.TrialDays;

    public Guid? LicenseeId => _licensee.Value.Id;
}
