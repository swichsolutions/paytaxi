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
/// The cashout saga — Yandex-first, queue-on-bank-trouble (PAYTAXI-CONTEXT.md §4, §6).
///
/// Steps for a new request:
///   1. Validate park / driver / destination; apply the park's flat fee and limits.
///   2. Idempotency: same key → return the existing cashout, no side effects.
///   3. Route: pick the park bank account whose bank code matches the driver's IBAN
///      (TBC→TBC is free and instant). No matching account → reject up front.
///   4. INSERT the cashout as Processing + CashoutReserved ledger entry.
///   5. Yandex: post −gross to the driver's balance. Rejected → Failed (nothing moved).
///      Thrown after retries → ReviewRequired (outcome unknown; reconciliation resolves).
///   6. Bank: transfer net (gross − fee) from the park account to the driver's IBAN.
///        · success            → Completed (+ BankTransferSent, FeeCollected, CashoutCompleted)
///        · retryable failure  → Queued with a backoff; the PayoutQueueWorker drains it.
///                               Driver sees "processing, arrives in minutes".
///        · thrown / ambiguous → ask the bank by our document id before ever re-sending.
///        · hard failure, or queue exhausted → reverse the Yandex debit (+gross) → Failed.
///          If the reversal itself fails → ReviewRequired.
///
/// Why Yandex first: a debit we can always compensate (post +amount back). A bank
/// transfer we cannot claw back — bank-first would let a driver cash the same balance
/// twice if the Yandex write failed after the money left.
///
/// The 0.50 GEL fee never moves: the bank transfer is for the net amount, so the fee
/// simply stays in the park's account. The nightly settlement engine sweeps Swich's
/// share from the ledger's FeeCollected entries.
/// </summary>
public class CashoutOrchestrator : ICashoutOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly IYandexFleetClient _yandex;
    private readonly INotificationService _notifier;
    private readonly CashoutOptions _opts;
    private readonly ILogger<CashoutOrchestrator> _log;

    public CashoutOrchestrator(
        AppDbContext db,
        IServiceProvider services,
        IYandexFleetClient yandex,
        INotificationService notifier,
        IOptions<CashoutOptions> opts,
        ILogger<CashoutOrchestrator> log)
    {
        _db = db;
        _services = services;
        _yandex = yandex;
        _notifier = notifier;
        _opts = opts.Value;
        _log = log;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  New cashout
    // ═══════════════════════════════════════════════════════════════════

    public async Task<CashoutSagaResult> RunAsync(CashoutSagaRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required", nameof(req));
        if (req.Amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(req));

        // ── Idempotency check ─────────────────────────────────────────
        var existing = await _db.Cashouts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdempotencyKey == req.IdempotencyKey, ct);
        if (existing is not null)
        {
            _log.LogInformation(
                "Saga idempotency hit for key={Key} → returning existing cashout {Id} status={Status}",
                req.IdempotencyKey, existing.Id, existing.Status);
            return ToResult(existing, wasDeduped: true);
        }

        // ── Load park, driver, destination ────────────────────────────
        var park = await _db.Parks
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstOrDefaultAsync(p => p.Id == req.ParkId, ct)
            ?? throw new InvalidOperationException($"Park {req.ParkId} not found");
        var driver = await _db.Drivers.FirstOrDefaultAsync(d => d.Id == req.DriverId && d.ParkId == req.ParkId, ct)
            ?? throw new InvalidOperationException($"Driver {req.DriverId} not found in park {req.ParkId}");
        var card = await _db.BankCards.FirstOrDefaultAsync(b => b.Id == req.BankCardId && b.DriverId == req.DriverId, ct)
            ?? throw new InvalidOperationException($"Payout destination {req.BankCardId} not found for driver {req.DriverId}");

        if (park.Status != ParkStatus.Active)
            throw new InvalidOperationException($"Park {req.ParkId} is not active (status={park.Status})");
        if (driver.Status != DriverStatus.Active)
            throw new InvalidOperationException($"Driver {req.DriverId} is not active (status={driver.Status})");
        if (driver.YandexDriverProfileId is null)
            throw new InvalidOperationException($"Driver {req.DriverId} is not linked to a Yandex profile");
        if (!card.IsActive)
            throw new InvalidOperationException("Payout destination has been removed");
        if (string.IsNullOrWhiteSpace(card.Iban))
            throw new InvalidOperationException("Payout destination has no IBAN — add a bank account first");

        // ── Fee & limits (per-park config) ────────────────────────────
        var fee = Math.Round(park.CashoutFee, 2, MidpointRounding.AwayFromZero);
        var amount = Math.Round(req.Amount, 2, MidpointRounding.AwayFromZero);

        if (amount < park.MinCashoutAmount)
            throw new InvalidOperationException($"Minimum cashout is {park.MinCashoutAmount:F2} GEL");
        if (park.MaxCashoutAmount is { } max && amount > max)
            throw new InvalidOperationException($"Maximum cashout is {max:F2} GEL");
        if (amount <= fee)
            throw new InvalidOperationException($"Amount must exceed the {fee:F2} GEL fee");

        if (park.DailyCashoutLimitPerDriver is { } dailyLimit)
        {
            var dayStart = DateTime.UtcNow.Date;
            var todaySoFar = await _db.Cashouts
                .Where(c => c.DriverId == driver.Id
                         && c.CreatedAt >= dayStart
                         && c.Status != CashoutStatus.Failed)
                .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
            if (todaySoFar + amount > dailyLimit)
                throw new InvalidOperationException(
                    $"Daily cashout limit of {dailyLimit:F2} GEL reached ({todaySoFar:F2} already today)");
        }

        // ── Route to the park account at the driver's bank ────────────
        var account = RouteAccount(park, card.BankCode)
            ?? throw new InvalidOperationException(
                $"bank_not_supported: the park has no payout account at bank '{card.BankCode}'. " +
                $"Supported: {string.Join(", ", park.BankAccounts.Select(a => a.BankCode).Distinct())}");

        // ── Reserve (Processing) ──────────────────────────────────────
        var cashout = new Cashout
        {
            DriverId = driver.Id,
            ParkId = park.Id,
            BankCardId = card.Id,
            ParkBankAccountId = account.Id,
            Amount = amount,
            Fee = fee,
            Status = CashoutStatus.Processing,
            IdempotencyKey = req.IdempotencyKey,
            InitiatedBy = req.InitiatedBy ?? "system",
        };
        _db.Cashouts.Add(cashout);
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.CashoutReserved,
            Amount = amount,
            Reference = req.IdempotencyKey,
            Notes = $"Reserved by {cashout.InitiatedBy}; route {card.BankCode}→{account.BankCode}/{account.Provider}",
        });
        await _db.SaveChangesAsync(ct);

        // ── Step 1: Yandex debit (gross) ──────────────────────────────
        YandexTransactionResult yandexResult;
        try
        {
            yandexResult = await _yandex.PostCashoutTransactionAsync(
                parkId: park.Id,
                driverProfileId: driver.YandexDriverProfileId!,
                amount: amount,
                idempotencyKey: req.IdempotencyKey,
                ct: ct);
        }
        catch (Exception ex)
        {
            // Retries are exhausted inside the resilient client. We don't know whether
            // the debit landed — no money has moved, so park it for a human + reconciliation.
            _log.LogError(ex, "Yandex post threw for cashout {Id} — outcome unknown, flagging for review", cashout.Id);
            await FlagForReviewAsync(cashout,
                "YANDEX_POST_EXCEPTION",
                $"Yandex debit outcome unknown: {ex.Message}. No bank transfer was made.",
                ct);
            return ToResult(cashout);
        }

        if (!yandexResult.Success)
        {
            await FailAsync(cashout,
                yandexResult.ErrorCode ?? "YANDEX_REJECTED",
                yandexResult.ErrorMessage ?? "Yandex rejected the balance debit",
                ct);
            return ToResult(cashout);
        }

        cashout.YandexTransactionId = yandexResult.TransactionId;
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.YandexDeducted,
            Amount = amount,
            Reference = yandexResult.TransactionId,
        });
        await _db.SaveChangesAsync(ct);

        // ── Step 2: bank payout (net) ─────────────────────────────────
        await AttemptPayoutAsync(cashout, park, driver, card, account, ct);
        return ToResult(cashout);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Queue drain (called by PayoutQueueWorker)
    // ═══════════════════════════════════════════════════════════════════

    public async Task<CashoutSagaResult> ProcessQueuedAsync(Guid cashoutId, CancellationToken ct = default)
    {
        // Atomic claim: only one worker/tick may pick this row up.
        var now = DateTime.UtcNow;
        var claimed = await _db.Cashouts
            .Where(c => c.Id == cashoutId
                     && c.Status == CashoutStatus.Queued
                     && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CashoutStatus.Processing), ct);

        var cashout = await _db.Cashouts.FirstOrDefaultAsync(c => c.Id == cashoutId, ct)
            ?? throw new InvalidOperationException($"Cashout {cashoutId} not found");

        if (claimed == 0)
        {
            _log.LogDebug("Cashout {Id} not claimable (status={Status}, next={Next})",
                cashoutId, cashout.Status, cashout.NextAttemptAt);
            return ToResult(cashout);
        }

        var park = await _db.Parks
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstAsync(p => p.Id == cashout.ParkId, ct);
        var driver = await _db.Drivers.FirstAsync(d => d.Id == cashout.DriverId, ct);
        var card = await _db.BankCards.FirstAsync(b => b.Id == cashout.BankCardId, ct);

        // Re-route: the park may have added/removed an account since the cashout was queued.
        var account = park.BankAccounts.FirstOrDefault(a => a.Id == cashout.ParkBankAccountId)
                   ?? RouteAccount(park, card.BankCode);

        if (account is null)
        {
            await RequeueOrAbandonAsync(cashout, park, driver,
                "NO_PARK_ACCOUNT", $"Park has no active payout account at bank '{card.BankCode}'", ct);
            return ToResult(cashout);
        }

        cashout.ParkBankAccountId = account.Id;
        await AttemptPayoutAsync(cashout, park, driver, card, account, ct);
        return ToResult(cashout);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Bank payout attempt
    // ═══════════════════════════════════════════════════════════════════

    private async Task AttemptPayoutAsync(
        Cashout cashout, Park park, Driver driver, BankCard card, ParkBankAccount account, CancellationToken ct)
    {
        var net = cashout.Amount - cashout.Fee;
        var bank = ResolveBankAdapter(account.Provider);
        var source = ToContext(park, account);

        cashout.AttemptCount++;
        cashout.LastAttemptAt = DateTime.UtcNow;
        cashout.Status = CashoutStatus.Processing;
        await _db.SaveChangesAsync(ct);

        BankTransferResult result;
        try
        {
            result = await bank.SendPayoutAsync(new BankTransferRequest(
                IdempotencyKey: cashout.IdempotencyKey,
                Source: source,
                DestinationIban: card.Iban,
                DestinationName: card.HolderName ?? driver.Name,
                Amount: net,
                Currency: "GEL",
                Reference: $"PayTaxi cashout {cashout.Id:N}"), ct);
        }
        catch (NotImplementedException ex)
        {
            // Real adapter not wired yet — a config problem, not a bank problem. Queue, don't reverse.
            _log.LogError(ex, "Bank adapter '{Provider}' is not implemented", account.Provider);
            await RequeueOrAbandonAsync(cashout, park, driver, "BANK_ADAPTER_MISSING", ex.Message, ct);
            return;
        }
        catch (Exception ex)
        {
            // Outcome unknown (timeout, dropped connection). NEVER blindly re-fire:
            // ask the bank whether our document id exists.
            _log.LogWarning(ex, "Bank call threw for cashout {Id} (attempt {N}) — checking by document id",
                cashout.Id, cashout.AttemptCount);

            BankTransferLookup? lookup = null;
            try { lookup = await bank.FindTransferByIdempotencyKeyAsync(source, cashout.IdempotencyKey, ct); }
            catch (Exception lookupEx)
            {
                _log.LogWarning(lookupEx, "Status lookup also failed for cashout {Id}", cashout.Id);
            }

            if (lookup is not null && lookup.Status is BankTransferStatus.Completed or BankTransferStatus.Pending)
            {
                _log.LogInformation("Bank confirms transfer {TransferId} for cashout {Id} despite the lost response",
                    lookup.TransferId, cashout.Id);
                result = new BankTransferResult(true, lookup.TransferId, null, null);
            }
            else if (lookup is not null && lookup.Status == BankTransferStatus.Failed)
            {
                result = new BankTransferResult(false, lookup.TransferId, "BANK_REPORTED_FAILED",
                    "Bank reports the transfer failed", IsRetryable: true);
            }
            else
            {
                result = new BankTransferResult(false, null, "BANK_EXCEPTION", ex.Message, IsRetryable: true);
            }
        }

        if (result.Success)
        {
            await CompleteAsync(cashout, park, driver, card, account, result.TransferId!, ct);
            return;
        }

        var code = result.ErrorCode ?? "BANK_FAILED";
        var message = result.ErrorMessage ?? "Bank transfer failed";

        if (result.IsRetryable)
            await RequeueOrAbandonAsync(cashout, park, driver, code, message, ct);
        else
            await AbandonAsync(cashout, park, driver, code, message, ct);
    }

    private async Task CompleteAsync(
        Cashout cashout, Park park, Driver driver, BankCard card, ParkBankAccount account,
        string transferId, CancellationToken ct)
    {
        var net = cashout.Amount - cashout.Fee;

        cashout.BankTransferId = transferId;
        cashout.Status = CashoutStatus.Completed;
        cashout.CompletedAt = DateTime.UtcNow;
        cashout.NextAttemptAt = null;
        cashout.FailureReason = null;
        cashout.InvoiceNumber = await NextInvoiceNumberAsync(ct);

        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.BankTransferSent,
            Amount = net,
            Reference = transferId,
            Notes = $"{account.Provider} {Mask(account.Iban)} → {Mask(card.Iban)} (attempt {cashout.AttemptCount})",
        });
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.FeeCollected,
            Amount = cashout.Fee,
            Notes = "Flat cashout fee retained in park account",
        });
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.CashoutCompleted,
            Amount = cashout.Amount,
        });
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Cashout {Id} completed: park={Park} driver={Driver} gross={Amount} fee={Fee} net={Net} bank_tx={Bank} yandex_tx={Yandex} attempts={Attempts}",
            cashout.Id, park.Name, driver.Name, cashout.Amount, cashout.Fee, net, transferId,
            cashout.YandexTransactionId, cashout.AttemptCount);

        await _notifier.NotifyCashoutCompletedAsync(driver.Id, net, $"{card.BankType} {card.MaskedPan}", ct);
    }

    /// <summary>Bank said "not now". Park the cashout in the queue unless we've run out of road.</summary>
    private async Task RequeueOrAbandonAsync(
        Cashout cashout, Park park, Driver driver, string code, string message, CancellationToken ct)
    {
        var age = DateTime.UtcNow - cashout.CreatedAt;
        var exhausted = cashout.AttemptCount >= _opts.MaxPayoutAttempts
                     || age > TimeSpan.FromHours(_opts.MaxQueueAgeHours);

        if (exhausted)
        {
            await AbandonAsync(cashout, park, driver, code,
                $"{message} (gave up after {cashout.AttemptCount} attempts over {age.TotalMinutes:F0} min)", ct);
            return;
        }

        var delay = BackoffFor(cashout.AttemptCount);
        cashout.Status = CashoutStatus.Queued;
        cashout.NextAttemptAt = DateTime.UtcNow.Add(delay);
        cashout.FailureReason = $"{code}: {message}";
        await _db.SaveChangesAsync(ct);

        _log.LogWarning(
            "Cashout {Id} queued (attempt {N}, next in {Delay}s) — {Code}: {Message}",
            cashout.Id, cashout.AttemptCount, (int)delay.TotalSeconds, code, message);

        // Tell the driver once — subsequent retries are silent until the outcome.
        if (cashout.AttemptCount == 1)
            await _notifier.NotifyCashoutQueuedAsync(driver.Id, cashout.Amount - cashout.Fee, ct);
    }

    /// <summary>
    /// The payout will not happen. The Yandex debit already went through, so give the
    /// money back on Yandex (+gross). Only after that is the cashout truly Failed.
    /// </summary>
    private async Task AbandonAsync(
        Cashout cashout, Park park, Driver driver, string code, string message, CancellationToken ct)
    {
        if (cashout.YandexTransactionId is null)
        {
            // Nothing was debited (shouldn't reach here, but be safe).
            await FailAsync(cashout, code, message, ct);
            return;
        }

        var reversalKey = $"rev:{cashout.IdempotencyKey}";
        YandexTransactionResult reversal;
        try
        {
            reversal = await _yandex.PostReversalTransactionAsync(
                park.Id, driver.YandexDriverProfileId!, cashout.Amount, reversalKey, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Yandex reversal threw for cashout {Id}", cashout.Id);
            await FlagForReviewAsync(cashout, "REVERSAL_EXCEPTION",
                $"Payout abandoned ({code}: {message}) and Yandex reversal outcome unknown: {ex.Message}", ct);
            return;
        }

        if (!reversal.Success)
        {
            await FlagForReviewAsync(cashout, "REVERSAL_REJECTED",
                $"Payout abandoned ({code}: {message}); Yandex refused the reversal: {reversal.ErrorMessage}", ct);
            return;
        }

        cashout.YandexReversalTransactionId = reversal.TransactionId;
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.YandexReversed,
            Amount = cashout.Amount,
            Reference = reversal.TransactionId,
            Notes = "Debit returned to driver's Yandex balance",
        });
        await FailAsync(cashout, code, message, ct);
    }

    // ── Terminal helpers ─────────────────────────────────────────────

    private async Task FailAsync(Cashout cashout, string code, string message, CancellationToken ct)
    {
        cashout.Status = CashoutStatus.Failed;
        cashout.NextAttemptAt = null;
        cashout.FailureReason = $"{code}: {message}";
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = cashout.ParkId,
            EntryType = LedgerEntryType.CashoutReversed,
            Amount = cashout.Amount,
            Reference = code,
            Notes = message,
        });
        await _db.SaveChangesAsync(ct);

        _log.LogWarning("Cashout {Id} FAILED — {Code}: {Message}", cashout.Id, code, message);
        await _notifier.NotifyCashoutFailedAsync(cashout.DriverId, cashout.Amount, message, ct);
    }

    private async Task FlagForReviewAsync(Cashout cashout, string code, string message, CancellationToken ct)
    {
        cashout.Status = CashoutStatus.ReviewRequired;
        cashout.NextAttemptAt = null;
        cashout.FailureReason = $"{code}: {message}";
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = cashout.ParkId,
            EntryType = LedgerEntryType.CashoutReserved,
            Amount = cashout.Amount,
            Reference = code,
            Notes = $"REVIEW REQUIRED — {message}",
        });
        await _db.SaveChangesAsync(ct);

        _log.LogError("Cashout {Id} flagged ReviewRequired — {Code}: {Message}", cashout.Id, code, message);
        await _notifier.NotifyCashoutReviewRequiredAsync(cashout.DriverId, cashout.Amount, ct);
    }

    // ── Routing / infra helpers ──────────────────────────────────────

    /// <summary>Park account at the same bank as the destination IBAN (intra-bank = free + instant).</summary>
    private static ParkBankAccount? RouteAccount(Park park, string destinationBankCode) =>
        park.BankAccounts
            .Where(a => a.IsActive && string.Equals(a.BankCode, destinationBankCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.IsPrimary)
            .FirstOrDefault();

    private static BankAccountContext ToContext(Park park, ParkBankAccount a) => new(
        ParkId: park.Id,
        ParkBankAccountId: a.Id,
        Provider: a.Provider,
        BankCode: a.BankCode,
        SourceIban: a.Iban,
        SourceHolderName: a.HolderName ?? park.LegalEntityName ?? park.Name,
        CredentialsJson: a.CredentialsEncrypted);

    private IBankPayoutAdapter ResolveBankAdapter(string provider)
    {
        return _services.GetKeyedService<IBankPayoutAdapter>(provider)
            ?? _services.GetKeyedService<IBankPayoutAdapter>(provider.ToUpperInvariant())
            ?? _services.GetKeyedService<IBankPayoutAdapter>("MOCK")
            ?? throw new InvalidOperationException($"No bank adapter registered for provider '{provider}'");
    }

    private TimeSpan BackoffFor(int attempt)
    {
        var schedule = _opts.BackoffSeconds is { Length: > 0 } s ? s : CashoutOptions.DefaultBackoff;
        var idx = Math.Clamp(attempt - 1, 0, schedule.Length - 1);
        return TimeSpan.FromSeconds(schedule[idx]);
    }

    /// <summary>Pulls the next value from the Postgres InvoiceNumberSeq sequence.</summary>
    private async Task<long> NextInvoiceNumberAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT nextval('\"InvoiceNumberSeq\"')";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    private static string Mask(string iban) =>
        string.IsNullOrEmpty(iban) || iban.Length < 4 ? "****" : $"****{iban[^4..]}";

    private static CashoutSagaResult ToResult(Cashout c, bool wasDeduped = false) => new(
        CashoutId: c.Id,
        Status: c.Status.ToString(),
        Amount: c.Amount,
        Fee: c.Fee,
        Net: c.Amount - c.Fee,
        BankTransferId: c.BankTransferId,
        YandexTransactionId: c.YandexTransactionId,
        FailureReason: c.FailureReason,
        WasDeduped: wasDeduped,
        NextAttemptAt: c.NextAttemptAt,
        AttemptCount: c.AttemptCount);
}

/// <summary>Saga tuning — bound from the "Cashout" configuration section.</summary>
public class CashoutOptions
{
    public const string SectionName = "Cashout";

    public static readonly int[] DefaultBackoff = { 30, 60, 120, 300, 600, 900 };

    /// <summary>Bank payout attempts before the Yandex debit is reversed and the cashout fails.</summary>
    public int MaxPayoutAttempts { get; set; } = 30;

    /// <summary>Hard ceiling on how long a cashout may sit in the queue, regardless of attempts.</summary>
    public int MaxQueueAgeHours { get; set; } = 12;

    /// <summary>
    /// Delay before attempt N+1, indexed by attempt number (last value repeats).
    /// Deliberately empty by default: the configuration binder APPENDS array elements to an
    /// existing array, so a non-empty default would silently prepend itself to the configured
    /// schedule. <see cref="DefaultBackoff"/> is applied at use time when nothing is configured.
    /// </summary>
    public int[] BackoffSeconds { get; set; } = Array.Empty<int>();
}
