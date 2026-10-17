using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// What a monthly-settlement test needs: its own licensee, seeded parks borrowed for the duration
/// (assignment and payout accounts handed back afterwards), back-dated completed cashouts planted in a
/// past month nobody else uses, and the run / statement calls. Settled money rows stay (the ledger is
/// append-only); the test licensee is deactivated so the seeded one remains the console's first.
/// Never borrows Tbilisi #3 — the parallel flaky-bank collection pays its drivers.
///
/// A test licensee is registered INACTIVE: the seeded one is in force, and a second licensee in force is refused
/// (second_active_licensee). The operator account works for one licensee only, so for the test it is bound to the
/// test licensee (the engine settles every licensee, active or not) — and handed back by <see cref="ReturnAsync"/>.
/// An operator works only while its licensee is in force (AdminTokenCheck), so a test that acts as the operator puts its
/// licensee in force (<see cref="InForceAsync"/>) — the seeded one stands down meanwhile.
/// </summary>
internal sealed class SettlementKit
{
    public const string P5 = "tbilisi-auto-park-5", PB = "batumi-auto-park-1";
    public const string OperatorEmail = "levan@operator.local";

    private readonly ApiFixture _f;
    private readonly Dictionary<Guid, Guid?> _assignments = new();
    private readonly List<Guid> _inactivated = new();
    private readonly List<Guid> _licensees = new();
    private readonly List<Guid> _stoodDown = new();
    private bool _operatorBorrowed;
    private Guid? _operatorLicensee;

    public SettlementKit(ApiFixture f) => _f = f;

    public static DateTime Noon(int y, int m, int d) => new(y, m, d, 8, 0, 0, DateTimeKind.Utc);   // Tbilisi noon
    public static decimal D(JsonElement e, string prop) => e.GetProperty(prop).GetDecimal();
    public static string S(JsonElement e, string prop) => e.GetProperty(prop).GetString() ?? "";

    public Task<Guid> ParkIdAsync(string slug) =>
        _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == slug)).Id);

    /// <summary>
    /// A test licensee. A launch date, when given, is written straight to the database: through the API a launch date
    /// can only be set before it passes (and with a reason), and these tests settle months long gone.
    /// </summary>
    public async Task<Guid> CreateLicenseeAsync(HttpClient swich, string name, string? launch, decimal barrier = 20_000m, decimal share = 50m)
    {
        var res = await swich.PostAsJsonAsync("/api/admin/licensees", new { name, barrierGel = barrier, swichSharePercent = share, isActive = false });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == HttpStatusCode.OK, body.ToString());
        var id = body.GetProperty("id").GetGuid();
        _licensees.Add(id);
        if (launch is not null) await SetLaunchAsync(id, DateOnly.ParseExact(launch, "yyyy-MM-dd"));
        await BindOperatorAsync(id);
        return id;
    }

    /// <summary>The operator account works for <paramref name="licenseeId"/> until <see cref="ReturnAsync"/> (looked up per request).</summary>
    public async Task BindOperatorAsync(Guid licenseeId)
    {
        if (!_operatorBorrowed)
        {
            _operatorLicensee = await _f.DbAsync(db => db.AdminUsers.AsNoTracking()
                .Where(a => a.Email == OperatorEmail).Select(a => a.LicenseeId).FirstAsync());
            _operatorBorrowed = true;
        }
        await _f.DbAsync(db => db.AdminUsers.Where(a => a.Email == OperatorEmail)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LicenseeId, (Guid?)licenseeId)));
    }

    /// <summary>
    /// The test licensee is the one in force for the rest of the test — the seeded one stands down (one at a time,
    /// IX_Licensees_SingleActive) and <see cref="ReturnAsync"/> puts it back. Needed wherever the operator must reach the
    /// test licensee's parks: an operator reaches a licensee's parks only while that licensee is in force.
    /// </summary>
    public Task InForceAsync(Guid licenseeId) => _f.DbAsync(async db =>
    {
        foreach (var l in await db.Licensees.Where(x => x.IsActive && x.Id != licenseeId).ToListAsync())
        {
            l.IsActive = false;
            // Only what was in force before the test is put back by ReturnAsync — not an earlier test licensee of its own.
            if (!_licensees.Contains(l.Id)) _stoodDown.Add(l.Id);
        }
        await db.SaveChangesAsync();
        (await db.Licensees.FirstAsync(x => x.Id == licenseeId)).IsActive = true;
        return await db.SaveChangesAsync();
    });

    /// <summary>The launch date as it would stand once agreed and passed (the API locks a passed date).</summary>
    public Task SetLaunchAsync(Guid licenseeId, DateOnly? launch) => _f.DbAsync(db =>
        db.Licensees.Where(l => l.Id == licenseeId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LaunchDate, launch)));

    /// <summary>
    /// A shared cost as if entered this many hours ago — a statement counts it only 48 hours after its entry
    /// (Settlement:CostCutoffHours), and these tests settle right after entering it.
    /// </summary>
    public Task EnteredHoursAgoAsync(Guid costId, int hours) => _f.DbAsync(db =>
        db.CommonCosts.Where(c => c.Id == costId).ExecuteUpdateAsync(s => s.SetProperty(c => c.EnteredAt, DateTime.UtcNow.AddHours(-hours))));

    public async Task AssignAsync(HttpClient swich, Guid parkId, Guid? licenseeId)
    {
        if (!_assignments.ContainsKey(parkId))
            _assignments[parkId] = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Id == parkId)).LicenseeId);
        var res = await swich.PutAsJsonAsync($"/api/admin/parks/{parkId}/licensee", new { licenseeId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>Completed cashouts as the saga leaves them, back-dated into a test month.</summary>
    public Task<List<Guid>> PlantAsync(Guid parkId, int count, decimal fee, DateTime completedAtUtc) => _f.DbAsync(async db =>
    {
        var d = await db.Drivers.AsNoTracking().Where(x => x.ParkId == parkId).OrderBy(x => x.YandexDriverProfileId).FirstAsync();
        var card = await db.BankCards.AsNoTracking().Where(x => x.DriverId == d.Id).OrderByDescending(x => x.IsDefault).FirstAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var at = completedAtUtc.AddMinutes(i);
            var c = new Cashout
            {
                DriverId = d.Id, ParkId = parkId, BankCardId = card.Id, Amount = 10m + fee, Fee = fee,
                Status = CashoutStatus.Completed, CompletedAt = at, CreatedAt = at.AddMinutes(-1), UpdatedAt = at,
                IdempotencyKey = $"planted:{Guid.NewGuid():N}", InitiatedBy = "test", AttemptCount = 1,
            };
            db.Cashouts.Add(c);
            ids.Add(c.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    });

    /// <summary>The park switches its payout accounts off — its settlement transfer fails NO_PARK_ACCOUNT until handed back.</summary>
    public async Task InactivateAccountsAsync(Guid parkId)
    {
        var ids = await _f.DbAsync(async db =>
        {
            var list = await db.ParkBankAccounts.Where(a => a.ParkId == parkId && a.IsActive).ToListAsync();
            foreach (var a in list) a.IsActive = false;
            await db.SaveChangesAsync();
            return list.Select(a => a.Id).ToList();
        });
        _inactivated.AddRange(ids);
    }

    public Task ReactivateAccountsAsync() => _f.DbAsync(async db =>
    {
        foreach (var a in await db.ParkBankAccounts.Where(x => _inactivated.Contains(x.Id)).ToListAsync()) a.IsActive = true;
        return await db.SaveChangesAsync();
    });

    /// <summary>Hand every borrowed park, account, licensee and the operator account back.</summary>
    public Task ReturnAsync() => _f.DbAsync(async db =>
    {
        foreach (var (parkId, licenseeId) in _assignments)
            (await db.Parks.FirstAsync(p => p.Id == parkId)).LicenseeId = licenseeId;
        foreach (var a in await db.ParkBankAccounts.Where(x => _inactivated.Contains(x.Id)).ToListAsync()) a.IsActive = true;
        foreach (var l in await db.Licensees.Where(x => _licensees.Contains(x.Id)).ToListAsync()) l.IsActive = false;
        if (_operatorBorrowed)
            (await db.AdminUsers.FirstAsync(a => a.Email == OperatorEmail)).LicenseeId = _operatorLicensee;
        var saved = await db.SaveChangesAsync();
        // Only once the test licensee is out of force again (the single-active index checks every statement).
        foreach (var l in await db.Licensees.Where(x => _stoodDown.Contains(x.Id)).ToListAsync()) l.IsActive = true;
        return saved + await db.SaveChangesAsync();
    });

    public static async Task<JsonElement> RunAsync(HttpClient c, string month, Guid? licenseeId = null, Guid? parkId = null,
        HttpStatusCode expect = HttpStatusCode.OK)
    {
        var url = $"/api/admin/settlements/run?month={month}"
                  + (licenseeId is { } l ? $"&licenseeId={l}" : "")
                  + (parkId is { } p ? $"&parkId={p}" : "");
        var res = await c.PostAsync(url, null);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.StatusCode == expect, $"{url} → {(int)res.StatusCode}: {body}");
        return body;
    }

    public static JsonElement OnlyStatement(JsonElement run) => run.GetProperty("statements").EnumerateArray().Single();

    public static JsonElement ParkLine(JsonElement statement, Guid parkId) =>
        statement.GetProperty("parks").EnumerateArray().Single(p => p.GetProperty("parkId").GetGuid() == parkId);

    /// <summary>Move a settlement's ledger entries of one kind to a past moment (the history a long-running row would have).</summary>
    public Task BackdateLedgerAsync(Guid settlementId, LedgerEntryType type, DateTime atUtc) => _f.DbAsync(db =>
        db.LedgerEntries.Where(l => l.SettlementId == settlementId && l.EntryType == type)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.CreatedAt, atUtc)));

    /// <summary>A failed attempt as the engine records it (code in Reference), at a past moment.</summary>
    public Task AddFailureAsync(Guid settlementId, string code, DateTime atUtc) => _f.DbAsync(async db =>
    {
        var s = await db.Settlements.AsNoTracking().FirstAsync(x => x.Id == settlementId);
        db.LedgerEntries.Add(new LedgerEntry
        {
            SettlementId = s.Id, ParkId = s.ParkId, EntryType = LedgerEntryType.SettlementFailed,
            Amount = s.SwichShare, Reference = code, Notes = "test: earlier attempt", CreatedAt = atUtc,
        });
        return await db.SaveChangesAsync();
    });

    // ── Holding a statement mid-write ────────────────────────────────

    /// <summary>
    /// Locks the statement table against inserts from a side connection, so a statement run computes
    /// and then waits right where it would write. Released by <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    public static async Task<StatementWriteGate> HoldStatementWritesAsync()
    {
        var conn = new NpgsqlConnection(ApiFixture.ConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("LOCK TABLE \"MonthlyStatements\" IN EXCLUSIVE MODE", conn, tx))
            await cmd.ExecuteNonQueryAsync();
        return new StatementWriteGate(conn, tx);
    }

    internal sealed class StatementWriteGate : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private NpgsqlTransaction? _tx;
        public StatementWriteGate(NpgsqlConnection conn, NpgsqlTransaction tx) { _conn = conn; _tx = tx; }

        /// <summary>Sessions of this database waiting for a lock (table, row or advisory) — the runs held at the gate.</summary>
        public async Task<int> WaitingAsync()
        {
            // pg_stat_activity is snapshotted once per transaction, and the gate's connection lives in one: without
            // clearing it, a run that opened its connection after the first look would never be seen waiting.
            await using (var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", _conn, _tx))
                await clear.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(DISTINCT l.pid) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid " +
                "WHERE NOT l.granted AND a.datname = current_database()", _conn, _tx);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        /// <summary>Wait until at least <paramref name="n"/> sessions are held (or the timeout passes).</summary>
        public async Task<bool> UntilWaitingAsync(int n, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (await WaitingAsync() >= n) return true;
                await Task.Delay(100);
            }
            return false;
        }

        public async Task ReleaseAsync()
        {
            if (_tx is null) return;
            await _tx.CommitAsync();
            await _tx.DisposeAsync();
            _tx = null;
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await _conn.DisposeAsync();
        }
    }
}
