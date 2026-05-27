using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Yandex;

/// <summary>
/// In-memory data store for the mock Yandex Fleet client.
///
/// Mirrors what a real Yandex backend would hold: per-park driver profiles,
/// balances, transactions and orders. Keyed by Yandex park-ID + driver-profile-ID
/// (these are the same IDs the real Yandex API uses, so swapping in the real
/// client is purely a DI change).
///
/// Designed to be:
///  - thread-safe (ConcurrentDictionary)
///  - deterministic at startup (same seed produces same data)
///  - mutable at runtime (write endpoint can append a transaction and adjust balance)
/// </summary>
public class MockYandexFleetData
{
    private readonly object _lock = new();

    /// <summary>YandexParkId → list of driver profiles with current balance.</summary>
    public Dictionary<string, List<DriverState>> ByPark { get; } = new();

    /// <summary>(YandexParkId, DriverProfileId) → recent transactions, newest first.</summary>
    public Dictionary<(string Park, string Driver), List<YandexTransaction>> Transactions { get; } = new();

    /// <summary>(YandexParkId, DriverProfileId) → recent ride orders, newest first.</summary>
    public Dictionary<(string Park, string Driver), List<YandexOrder>> Orders { get; } = new();

    public MockYandexFleetData()
    {
        Seed();
    }

    /// <summary>Atomically debit a driver's balance and append a transaction.</summary>
    public bool TryDebit(string yandexParkId, string driverProfileId, decimal amount, string txId, out string? error)
    {
        lock (_lock)
        {
            if (!ByPark.TryGetValue(yandexParkId, out var drivers))
            {
                error = "park_not_found";
                return false;
            }
            var driver = drivers.Find(d => d.DriverProfileId == driverProfileId);
            if (driver is null)
            {
                error = "driver_not_found";
                return false;
            }
            if (driver.Balance < amount)
            {
                error = "insufficient_balance";
                return false;
            }
            driver.Balance -= amount;
            var key = (yandexParkId, driverProfileId);
            if (!Transactions.TryGetValue(key, out var list))
            {
                list = new();
                Transactions[key] = list;
            }
            list.Insert(0, new YandexTransaction(
                TransactionId: txId,
                Amount: -amount,
                Category: "partner_service_manual",
                Description: "Cashout · PayTaxi",
                CreatedAt: DateTime.UtcNow));
            error = null;
            return true;
        }
    }

    private void Seed()
    {
        // Stable IDs that match the database seed in Data/SeedData.cs
        var parks = new[]
        {
            new ParkSeed("yx_park_tb3",
                ("yp_tb3_001", "გიორგი მამულაშვილი",      "AA-123-BB",  847.50m),
                ("yp_tb3_002", "ნიკა ჯავახიშვილი",       "BB-447-CC", 1240.00m),
                ("yp_tb3_003", "Levan Kobakhidze",        "CC-998-DE",  210.00m),
                ("yp_tb3_004", "ბექა გელაშვილი",          "AA-771-XR",   44.50m),
                ("yp_tb3_005", "დავით ჩხეიძე",            "TT-222-OK", 1980.00m),
                ("yp_tb3_006", "Zura Mikeladze",          "BB-554-PP",  675.20m),
                ("yp_tb3_007", "ვალერი თავაძე",           "AB-101-EF",   92.30m),
                ("yp_tb3_008", "ალექსანდრე გვინიაშვილი",  "DD-301-GH",  330.00m)),

            new ParkSeed("yx_park_tb5",
                ("yp_tb5_001", "Badri Macharashvili",     "FF-808-KL", 1460.75m),
                ("yp_tb5_002", "გელა ცინცაძე",            "GG-915-MN",    0.00m),
                ("yp_tb5_003", "Dimitri Lomidze",         "HH-022-OP",   18.00m),
                ("yp_tb5_004", "ემზარ ჯაფარიძე",          "II-441-QR",  725.50m),
                ("yp_tb5_005", "ვახტანგი ჩხარტიშვილი",    "JJ-616-ST",  130.00m),
                ("yp_tb5_006", "ზაზა ჯვარაძე",            "KK-303-UV",  980.00m),
                ("yp_tb5_007", "Kakha Chubinidze",        "LL-919-WX",  435.30m),
                ("yp_tb5_008", "მამუკა ხატიური",          "MM-525-YZ", 1120.00m)),

            new ParkSeed("yx_park_bt1",
                ("yp_bt1_001", "ნუგზარ შავიშვილი",        "NN-737-AB",   62.00m),
                ("yp_bt1_002", "Otar Gogoberidze",        "OO-148-CD",  285.00m),
                ("yp_bt1_003", "პაატა მარგველაშვილი",     "PP-868-EF",  745.00m),
                ("yp_bt1_004", "რეზო ჩიქოვანი",           "QQ-454-GH", 1540.00m),
                ("yp_bt1_005", "Soso Kakhiani",           "RR-161-IJ",   25.00m),
                ("yp_bt1_006", "ტარიელ ჯანდიერი",         "SS-272-KL",  415.00m),
                ("yp_bt1_007", "იოსები ბერიძე",           "TT-383-MN",   88.50m),
                ("yp_bt1_008", "გიორგი ხუციშვილი",        "UU-494-OP",  650.00m)),
        };

        var rng = new Random(42); // deterministic

        foreach (var park in parks)
        {
            var driverList = park.Drivers
                .Select(d => new DriverState
                {
                    DriverProfileId = d.Id,
                    Name = d.Name,
                    CarPlate = d.Plate,
                    Balance = d.Balance,
                })
                .ToList();
            ByPark[park.YandexParkId] = driverList;

            foreach (var d in driverList)
            {
                var txs = new List<YandexTransaction>();
                var orders = new List<YandexOrder>();
                var now = DateTime.UtcNow;

                // Past 14 days of activity per driver
                for (var dayOffset = 0; dayOffset < 14; dayOffset++)
                {
                    var ridesPerDay = rng.Next(3, 9);
                    for (var r = 0; r < ridesPerDay; r++)
                    {
                        var amount = Math.Round((decimal)(rng.NextDouble() * 28 + 4), 2);
                        var at = now.AddDays(-dayOffset).AddHours(-rng.Next(0, 18)).AddMinutes(-rng.Next(0, 60));
                        orders.Add(new YandexOrder(
                            OrderId: $"ord_{Guid.NewGuid():N}".Substring(0, 16),
                            Amount: amount,
                            From: TbilisiPlaces[rng.Next(TbilisiPlaces.Length)],
                            To:   TbilisiPlaces[rng.Next(TbilisiPlaces.Length)],
                            CreatedAt: at));
                        txs.Add(new YandexTransaction(
                            TransactionId: $"tx_{Guid.NewGuid():N}".Substring(0, 16),
                            Amount: amount,
                            Category: "ride_payment",
                            Description: $"Ride · {at:dd MMM HH:mm}",
                            CreatedAt: at));
                    }
                }

                txs = txs.OrderByDescending(t => t.CreatedAt).ToList();
                orders = orders.OrderByDescending(o => o.CreatedAt).ToList();

                Transactions[(park.YandexParkId, d.DriverProfileId)] = txs;
                Orders[(park.YandexParkId, d.DriverProfileId)] = orders;
            }
        }
    }

    private static readonly string[] TbilisiPlaces =
    {
        "Rustaveli Ave", "Airport", "Vake Park", "Marjanishvili", "Didube",
        "Saburtalo", "Isani", "Gldani", "City Center", "Mtatsminda",
        "Old Tbilisi", "Dezerter Bazaar", "Tbilisi Mall", "East Point",
        "Tbilisi Sea", "Avlabari", "Sololaki",
    };

    public class DriverState
    {
        public string DriverProfileId { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string CarPlate { get; set; } = default!;
        public decimal Balance { get; set; }
    }

    private record ParkSeed(string YandexParkId, params (string Id, string Name, string Plate, decimal Balance)[] Drivers);
}
