using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Nightly settlement engine (PAYTAXI-CONTEXT.md §4/§5).
///
/// For a park and a local calendar day D:
///   1. Collect the park's Completed cashouts that no settlement covers yet and whose
///      CompletedAt is before local D+1 00:00 (older stragglers roll in too).
///   2. Compute Swich's share cashout by cashout, in completion order, against the park's
///      phase config: while the park's cumulative fee income is under Phase1Cap the fee is
///      shared at Phase1Share (100% for Levan's park), then at SwichShare (50%). A crossover
///      day is split exactly at the cap.
///   3. Create ONE Settlement row, attach the cashouts, and transfer SwichShare from the
///      park's primary account to Swich's IBAN with document id "settle:{id}".
///   4. Success → Completed (+ SettlementSent ledger). Any failure → Failed with the reason,
///      cashouts stay attached, and the next nightly run (or an operator) retries it — the
///      transfer is never partially taken.
/// </summary>
public class SettlementService : ISettlementService
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly SettlementOptions _opts;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        AppDbContext db,
        IServiceProvider services,
        IOptions<SettlementOptions> opts,
        ILogger<SettlementService> log)
    {
        _db = db;
        _services = services;
        _opts = opts.Value;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════════════

    public async Task<SettlementRunSummary> RunDueAsync(DateOnly settlementDate, string initiatedBy, CancellationToken ct = default)
    {
        int created = 0, completed = 0, failed = 0, retried = 0, skipped = 0;

        // 0. Transfers the bank accepted asynchronously last time: ask how they ended.
        var pending = await _db.Settlements
            .Where(s => s.Status == SettlementStatus.Processing && s.BankTransferId != null)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            var s = await _db.Settlements.FirstAsync(x => x.Id == id, ct);
            await ResolvePendingAsync(s, ct);
        }

        // 1. Earlier failures first — "roll into next day".
        var stale = await _db.Settlements
            .Where(s => s.Status == SettlementStatus.Failed && s.SettlementDate < settlementDate)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in stale)
        {
            ct.ThrowIfCancellationRequested();
            var s = await RetryAsync(id, initiatedBy, ct);
            retried++;
            if (s.Status == SettlementStatus.Completed) completed++; else failed++;
        }

        // 2. The day's settlement per park.
        var parks = await _db.Parks.AsNoTracking()
            .Where(p => p.Status == ParkStatus.Active)
            .Select(p => p.Id)
            .ToListAsync(ct);

        foreach (var parkId in parks)
        {
            ct.ThrowIfCancellationRequested();
            var s = await RunForParkAsync(parkId, settlementDate, initiatedBy, ct);
            if (s is null) { skipped++; continue; }
            created++;
            if (s.Status == SettlementStatus.Completed) completed++;
            else if (s.Status == SettlementStatus.Failed) failed++;
        }

        var summary = new SettlementRunSummary(settlementDate, parks.Count, created, completed, failed, retried, skipped);
        _log.LogInformation("Settlement run {Date}: {Summary}", settlementDate, summary);
        return summary;
    }

    public async Task<Settlement?> RunForParkAsync(Guid parkId, DateOnly settlementDate, string initiatedBy, CancellationToken ct = default)
    {
        // Idempotent per (park, day).
        var existing = await _db.Settlements
            .FirstOrDefaultAsync(s => s.ParkId == parkId && s.SettlementDate == settlementDate, ct);
        if (existing is not null)
        {
            if (existing.Status is SettlementStatus.Pending or SettlementStatus.Failed)
                return await ExecuteAsync(existing, initiatedBy, ct);
            return existing;
        }

        var park = await _db.Parks
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstOrDefaultAsync(p => p.Id == parkId, ct)
            ?? throw new InvalidOperationException($"Park {parkId} not found");

        var periodToUtc = LocalDayEndUtc(settlementDate);

        var cashouts = await _db.Cashouts
            .Where(c => c.ParkId == parkId
                     && c.Status == CashoutStatus.Completed
                     && c.SettlementId == null
                     && c.CompletedAt != null
                     && c.CompletedAt < periodToUtc)
            .OrderBy(c => c.CompletedAt)
            .ToListAsync(ct);

        if (cashouts.Count == 0)
        {
            _log.LogInformation("Settlement {Date}: park {Park} has nothing to settle", settlementDate, park.Name);
            return null;
        }

        // Cumulative fee income before this settlement = everything earlier settlements cover.
        var cumulativeBefore = await _db.Settlements
            .Where(s => s.ParkId == parkId)
            .SumAsync(s => (decimal?)s.FeeTotal, ct) ?? 0m;

        var split = ComputeShares(park, cashouts, cumulativeBefore);

        var settlement = new Settlement
        {
            ParkId = parkId,
            SettlementDate = settlementDate,
            PeriodFromUtc = cashouts.Min(c => c.CompletedAt!.Value),
            PeriodToUtc = periodToUtc,
            CashoutCount = cashouts.Count,
            FeeTotal = split.FeeTotal,
            Phase1Fees = split.Phase1Fees,
            Phase2Fees = split.Phase2Fees,
            SwichShare = split.SwichShare,
            ParkShare = split.FeeTotal - split.SwichShare,
            CumulativeFeesBefore = cumulativeBefore,
            Status = SettlementStatus.Pending,
            InvoiceRef = $"PT-{settlementDate:yyyy-MM}",
            InitiatedBy = initiatedBy,
        };
        settlement.IdempotencyKey = $"settle:{settlement.Id:N}";
        settlement.Description = $"PayTaxi settlement {settlementDate:yyyy-MM-dd}, {cashouts.Count} tx, inv ref {settlement.InvoiceRef}";

        _db.Settlements.Add(settlement);
        foreach (var c in cashouts) c.SettlementId = settlement.Id;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Settlement {Date} created for park {Park}: {Count} cashouts, fees {Fees} → Swich {Swich} (phase1 {P1} / phase2 {P2}, cumulative before {Cum})",
            settlementDate, park.Name, cashouts.Count, split.FeeTotal, split.SwichShare, split.Phase1Fees, split.Phase2Fees, cumulativeBefore);

        return await ExecuteAsync(settlement, initiatedBy, ct);
    }

    public async Task<Settlement> RetryAsync(Guid settlementId, string initiatedBy, CancellationToken ct = default)
    {
        var s = await _db.Settlements.FirstOrDefaultAsync(x => x.Id == settlementId, ct)
            ?? throw new InvalidOperationException($"Settlement {settlementId} not found");
        if (s.Status == SettlementStatus.Completed)
            throw new InvalidOperationException("Settlement is already completed");
        if (s.Status == SettlementStatus.Processing && s.BankTransferId is not null)
            return await ResolvePendingAsync(s, ct);
        if (s.Status == SettlementStatus.Processing)
            throw new InvalidOperationException("Settlement is currently processing");
        return await ExecuteAsync(s, initiatedBy, ct);
    }

    /// <summary>Poll a bank-accepted settlement transfer until it is final.</summary>
    private async Task<Settlement> ResolvePendingAsync(Settlement s, CancellationToken ct)
    {
        var park = await _db.Parks.Include(p => p.BankAccounts).FirstAsync(p => p.Id == s.ParkId, ct);
        var account = park.BankAccounts.FirstOrDefault(a => a.Id == s.ParkBankAccountId)
                   ?? park.BankAccounts.FirstOrDefault(a => a.IsPrimary);
        if (account is null) return s;

        var bank = _services.GetKeyedService<IBankPayoutAdapter>(account.Provider)
            ?? _services.GetKeyedService<IBankPayoutAdapter>(account.Provider.ToUpperInvariant())
            ?? _services.GetKeyedService<IBankPayoutAdapter>("MOCK");
        if (bank is null) return s;

        var source = new BankAccountContext(park.Id, account.Id, account.Provider, account.BankCode, account.Iban,
            account.HolderName ?? park.LegalEntityName ?? park.Name, account.CredentialsEncrypted);

        BankTransferStatus status;
        try { status = await bank.GetTransferStatusAsync(source, s.BankTransferId!, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Settlement {Id}: status poll failed for transfer {Tx}", s.Id, s.BankTransferId);
            return s;
        }

        if (status == BankTransferStatus.Completed)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, s.SwichShare, s.BankTransferId,
                $"{s.Description} · confirmed by bank after asynchronous execution"));
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Settlement {Id} confirmed by bank: transfer {Tx}", s.Id, s.BankTransferId);
        }
        else if (status == BankTransferStatus.Failed)
        {
            await FailAsync(s, "BANK_REPORTED_FAILED", $"Bank reports transfer {s.BankTransferId} failed", ct);
        }
        return s;
    }

    // ═══════════════════════════════════════════════════════════════════

    private async Task<Settlement> ExecuteAsync(Settlement s, string initiatedBy, CancellationToken ct)
    {
        var park = await _db.Parks
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstAsync(p => p.Id == s.ParkId, ct);

        s.Status = SettlementStatus.Processing;
        s.AttemptCount++;
        s.LastAttemptAt = DateTime.UtcNow;
        s.SwichIban = _opts.SwichIban;
        await _db.SaveChangesAsync(ct);

        // Nothing to move (e.g. 0% share) — still a completed settlement for the records.
        if (s.SwichShare < _opts.MinTransferGel)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, 0m, null,
                $"Below minimum transfer ({_opts.MinTransferGel:F2} GEL) — nothing sent"));
            await _db.SaveChangesAsync(ct);
            return s;
        }

        if (string.IsNullOrWhiteSpace(_opts.SwichIban))
        {
            await FailAsync(s, "SWICH_IBAN_NOT_CONFIGURED", "Settlement:SwichIban is not configured", ct);
            return s;
        }

        var account = park.BankAccounts.FirstOrDefault(a => a.IsPrimary) ?? park.BankAccounts.FirstOrDefault();
        if (account is null)
        {
            await FailAsync(s, "NO_PARK_ACCOUNT", "Park has no active payout account to settle from", ct);
            return s;
        }
        s.ParkBankAccountId = account.Id;

        var bank = _services.GetKeyedService<IBankPayoutAdapter>(account.Provider)
            ?? _services.GetKeyedService<IBankPayoutAdapter>(account.Provider.ToUpperInvariant())
            ?? _services.GetKeyedService<IBankPayoutAdapter>("MOCK")
            ?? throw new InvalidOperationException($"No bank adapter for provider '{account.Provider}'");

        var source = new BankAccountContext(
            ParkId: park.Id,
            ParkBankAccountId: account.Id,
            Provider: account.Provider,
            BankCode: account.BankCode,
            SourceIban: account.Iban,
            SourceHolderName: account.HolderName ?? park.LegalEntityName ?? park.Name,
            CredentialsJson: account.CredentialsEncrypted);

        // No partial take: if the balance is readable and short, fail before touching the bank.
        try
        {
            var balance = await bank.GetBalanceAsync(source, ct);
            if (balance is { } b && b < s.SwichShare)
            {
                await FailAsync(s, "INSUFFICIENT_PARK_BALANCE",
                    $"Park account balance {b:F2} GEL is below the settlement amount {s.SwichShare:F2} GEL", ct);
                return s;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Balance pre-check unavailable for account {Account}; attempting transfer anyway", account.Id);
        }

        var result = await BankSendHelper.SendWithLookupAsync(bank, new BankTransferRequest(
            IdempotencyKey: s.IdempotencyKey,
            Source: source,
            DestinationIban: _opts.SwichIban,
            DestinationName: _opts.SwichHolderName,
            Amount: s.SwichShare,
            Currency: "GEL",
            Reference: s.Description), _log, ct);

        if (result.Success && result.IsPending)
        {
            // Accepted, executing asynchronously. Stays Processing; the next run (or a retry) resolves it.
            s.BankTransferId = result.TransferId;
            s.FailureReason = null;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Settlement {Id}: bank accepted transfer {Tx}, awaiting execution", s.Id, result.TransferId);
            return s;
        }

        if (result.Success)
        {
            s.Status = SettlementStatus.Completed;
            s.CompletedAt = DateTime.UtcNow;
            s.BankTransferId = result.TransferId;
            s.FailureReason = null;
            _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementSent, s.SwichShare, result.TransferId,
                $"{s.Description} · {account.Provider} {Mask(account.Iban)} → {Mask(_opts.SwichIban)} (attempt {s.AttemptCount}, by {initiatedBy})"));
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Settlement {Id} for park {Park} completed: {Amount} GEL, bank tx {Tx}",
                s.Id, park.Name, s.SwichShare, result.TransferId);
            return s;
        }

        await FailAsync(s, result.ErrorCode ?? "BANK_FAILED", result.ErrorMessage ?? "Bank transfer failed", ct);
        return s;
    }

    private async Task FailAsync(Settlement s, string code, string message, CancellationToken ct)
    {
        s.Status = SettlementStatus.Failed;
        s.FailureReason = $"{code}: {message}";
        _db.LedgerEntries.Add(Entry(s, LedgerEntryType.SettlementFailed, s.SwichShare, code, message));
        await _db.SaveChangesAsync(ct);
        // TODO: alert both sides (Swich ops + park) by email/SMS once a channel exists; the
        // settlements panel shows the failure and the nightly run retries it.
        _log.LogError("Settlement {Id} (park {Park}, {Date}) FAILED — {Code}: {Message}",
            s.Id, s.ParkId, s.SettlementDate, code, message);
    }

    // ── Share computation ────────────────────────────────────────────

    public record ShareSplit(decimal FeeTotal, decimal Phase1Fees, decimal Phase2Fees, decimal SwichShare);

    /// <summary>
    /// Walk the cashouts in completion order. While cumulative fees are under the
    /// park's Phase1Cap, fees go to Swich at Phase1Share; the remainder at SwichShare.
    /// A single cashout straddling the cap is split at the cap.
    /// </summary>
    public static ShareSplit ComputeShares(Park park, IEnumerable<Cashout> orderedCashouts, decimal cumulativeBefore)
    {
        var steadyRate = park.SwichSharePercent / 100m;
        var phase1Rate = (park.Phase1SharePercent ?? park.SwichSharePercent) / 100m;
        var cap = park.Phase1CapGel;

        decimal feeTotal = 0, p1 = 0, p2 = 0, swich = 0;
        var cumulative = cumulativeBefore;

        foreach (var c in orderedCashouts)
        {
            var fee = c.Fee;
            feeTotal += fee;

            if (cap is { } capValue && cumulative < capValue)
            {
                var under = Math.Min(fee, capValue - cumulative);
                var over = fee - under;
                p1 += under; p2 += over;
                swich += under * phase1Rate + over * steadyRate;
            }
            else
            {
                p2 += fee;
                swich += fee * steadyRate;
            }
            cumulative += fee;
        }

        return new ShareSplit(
            FeeTotal: Math.Round(feeTotal, 2),
            Phase1Fees: Math.Round(p1, 2),
            Phase2Fees: Math.Round(p2, 2),
            SwichShare: Math.Round(swich, 2, MidpointRounding.AwayFromZero));
    }

    // ── Time helpers ─────────────────────────────────────────────────

    public DateTime LocalDayEndUtc(DateOnly day)
    {
        var tz = ResolveTimeZone(_opts.TimeZoneId);
        var localNext = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localNext, tz);
    }

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, "Georgian Standard Time", "Asia/Tbilisi" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Georgia is UTC+4 with no DST — a safe last resort.
        return TimeZoneInfo.CreateCustomTimeZone("Tbilisi", TimeSpan.FromHours(4), "Tbilisi", "Tbilisi");
    }

    private static LedgerEntry Entry(Settlement s, LedgerEntryType type, decimal amount, string? reference, string? notes) => new()
    {
        SettlementId = s.Id,
        ParkId = s.ParkId,
        EntryType = type,
        Amount = amount,
        Reference = reference,
        Notes = notes,
    };

    private static string Mask(string? iban) =>
        string.IsNullOrEmpty(iban) || iban.Length < 4 ? "****" : $"****{iban[^4..]}";
}

/// <summary>Bound from the "Settlement" configuration section.</summary>
public class SettlementOptions
{
    public const string SectionName = "Settlement";

    public bool Enabled { get; set; } = true;

    /// <summary>Swich Solutions' receiving TBC IBAN. Must be under Swich Solutions LLC exactly (contract).</summary>
    public string SwichIban { get; set; } = "";
    public string SwichHolderName { get; set; } = "Swich Solutions LLC";

    /// <summary>IANA or Windows id; Georgia has no DST.</summary>
    public string TimeZoneId { get; set; } = "Georgian Standard Time";

    /// <summary>Local wall-clock time the nightly run starts ("HH:mm"). Settles the previous day.</summary>
    public string RunAtLocalTime { get; set; } = "00:30";

    /// <summary>Shares below this are recorded as Completed without a bank transfer.</summary>
    public decimal MinTransferGel { get; set; } = 0.01m;

    /// <summary>How often the worker checks whether the nightly run is due.</summary>
    public int PollIntervalSeconds { get; set; } = 60;
    public int InitialDelaySeconds { get; set; } = 20;
}
