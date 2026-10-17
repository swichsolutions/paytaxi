using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Security;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// Drivers made for one test and deleted after it, so sign-in counters, sessions and notices never touch a seeded driver
/// another test relies on. Each has a fresh phone and a Yandex profile id the mock does not know (no cashouts).
/// </summary>
internal static class TestDrivers
{
    public static string FreshPhone() => "+99557" + Random.Shared.Next(0, 10_000_000).ToString("D7");

    public static string PhoneHash(string phone) => LookupHash.Phone(phone);

    public static Task<(Guid Id, string Phone, string Profile)> CreateAsync(ApiFixture f, string parkSlug, string name = "Nodar Testadze") =>
        f.DbAsync(async db =>
        {
            var parkId = await db.Parks.AsNoTracking().Where(p => p.Slug == parkSlug).Select(p => p.Id).FirstAsync();
            var phone = FreshPhone();
            var d = new Driver
            {
                ParkId = parkId, Name = name, PhoneEncrypted = phone, PhoneHash = PhoneHash(phone),
                YandexDriverProfileId = "yp_test_" + Guid.NewGuid().ToString("N")[..10],
                Status = DriverStatus.Active, ConsentGiven = true, ConsentTimestamp = DateTime.UtcNow,
            };
            db.Drivers.Add(d);
            await db.SaveChangesAsync();
            return (d.Id, phone, d.YandexDriverProfileId!);
        });

    /// <summary>The driver and everything a test left behind for it (codes, sign-in events, sessions, notices, accounts).</summary>
    public static Task DeleteAsync(ApiFixture f, Guid driverId) => f.DbAsync(async db =>
    {
        var hashes = await db.Drivers.AsNoTracking().Where(d => d.Id == driverId).Select(d => d.PhoneHash).ToListAsync();
        await ForgetPhonesAsync(db, hashes);
        await db.Notifications.Where(n => n.DriverId == driverId).ExecuteDeleteAsync();
        await db.DriverSessions.Where(s => s.DriverId == driverId).ExecuteDeleteAsync();
        await db.DriverSignIns.Where(s => s.DriverId == driverId).ExecuteDeleteAsync();
        await db.BankCards.Where(b => b.DriverId == driverId).ExecuteDeleteAsync();
        return await db.Drivers.Where(d => d.Id == driverId).ExecuteDeleteAsync();
    });

    /// <summary>A phone's sign-in codes and per-phone events (the day limits start from nothing again).</summary>
    public static Task ForgetPhoneAsync(ApiFixture f, string phoneHash) =>
        f.DbAsync(db => ForgetPhonesAsync(db, new[] { phoneHash }));

    private static async Task<int> ForgetPhonesAsync(Infrastructure.Data.AppDbContext db, IReadOnlyCollection<string> hashes)
    {
        await db.OtpCodes.Where(o => hashes.Contains(o.PhoneHash)).ExecuteDeleteAsync();
        return await db.Set<PhoneSignInEvent>().Where(e => hashes.Contains(e.PhoneHash)).ExecuteDeleteAsync();
    }
}
