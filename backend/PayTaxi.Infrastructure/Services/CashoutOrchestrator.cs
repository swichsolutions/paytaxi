using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The cashout saga.
///
/// Steps:
///   1. Validate park/driver/card; resolve fee.
///   2. Look up by IdempotencyKey — if present, return its current state (no-op).
///   3. INSERT a Pending cashout + CashoutReserved ledger entry.
///   4. For Model A.5: atomically check & decrement Park.AuthorizationLimit.
///      For Model A: skip — the park's own bank gate enforces the limit.
///   5. Call the bank adapter (mock or real). On failure → Failed, compensate
///      the A.5 limit, write CashoutReversed ledger entry, return.
///   6. Call Yandex PostCashoutTransactionAsync. On failure → ReviewRequired
///      (money already left the bank; a human must reconcile). DO NOT roll back
///      the bank transfer — it's not safe to assume the original transfer
///      can be reversed automatically.
///   7. Mark Completed, write BankTransferSent + YandexDeducted + CashoutCompleted
///      + FeeCollected ledger entries, return.
///
/// Concurrency: A.5 limit decrement uses an EF Core SQL update with a WHERE
/// guard so two concurrent saga calls against the same park can't double-spend.
/// </summary>
public class CashoutOrchestrator : ICashoutOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly IYandexFleetClient _yandex;
    private readonly INotificationService _notifier;
    private readonly ILogger<CashoutOrchestrator> _log;

    public CashoutOrchestrator(
        AppDbContext db,
        IServiceProvider services,
        IYandexFleetClient yandex,
        INotificationService notifier,
        ILogger<CashoutOrchestrator> log)
    {
        _db = db;
        _services = services;
        _yandex = yandex;
        _notifier = notifier;
        _log = log;
    }

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

        // ── Load park, driver, card ───────────────────────────────────
        var park = await _db.Parks.FirstOrDefaultAsync(p => p.Id == req.ParkId, ct)
            ?? throw new InvalidOperationException($"Park {req.ParkId} not found");
        var driver = await _db.Drivers.FirstOrDefaultAsync(d => d.Id == req.DriverId && d.ParkId == req.ParkId, ct)
            ?? throw new InvalidOperationException($"Driver {req.DriverId} not found in park {req.ParkId}");
        var card = await _db.BankCards.FirstOrDefaultAsync(b => b.Id == req.BankCardId && b.DriverId == req.DriverId, ct)
            ?? throw new InvalidOperationException($"Bank card {req.BankCardId} not found for driver {req.DriverId}");

        if (driver.Status != DriverStatus.Active)
            throw new InvalidOperationException($"Driver {req.DriverId} is not active (status={driver.Status})");
        if (driver.YandexDriverProfileId is null)
            throw new InvalidOperationException($"Driver {req.DriverId} is not linked to a Yandex profile");
        if (park.Status != ParkStatus.Active)
            throw new InvalidOperationException($"Park {req.ParkId} is not active (status={park.Status})");

        var fee = ComputeFee(req.Amount);

        // ── Reserve cashout (Pending) ─────────────────────────────────
        var cashout = new Cashout
        {
            DriverId = driver.Id,
            ParkId = park.Id,
            BankCardId = card.Id,
            Amount = req.Amount,
            Fee = fee,
            Status = CashoutStatus.Processing,
            IdempotencyKey = req.IdempotencyKey,
        };
        _db.Cashouts.Add(cashout);
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.CashoutReserved,
            Amount = req.Amount,
            Reference = req.IdempotencyKey,
            Notes = $"Reserved by {req.InitiatedBy ?? "system"}",
        });
        await _db.SaveChangesAsync(ct);

        // ── Model A.5 limit check (atomic decrement) ──────────────────
        var requiredLimit = req.Amount; // gross — park's float is debited for the gross amount
        var limitDecremented = false;
        if (park.OperatingModel == OperatingModel.ModelA5)
        {
            var rows = await _db.Parks
                .Where(p => p.Id == park.Id && p.AuthorizationLimit >= requiredLimit)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    p => p.AuthorizationLimit,
                    p => p.AuthorizationLimit!.Value - requiredLimit), ct);

            if (rows == 0)
            {
                await FailAsync(cashout, "INSUFFICIENT_AUTHORIZATION_LIMIT",
                    $"Park authorization limit insufficient for {requiredLimit:F2} GEL", ct);
                return ToResult(cashout);
            }
            limitDecremented = true;
        }

        // ── Bank payout ───────────────────────────────────────────────
        BankTransferResult bankResult;
        try
        {
            var bank = ResolveBankAdapter(park.BankProvider);
            bankResult = await bank.SendPayoutAsync(new BankTransferRequest(
                IdempotencyKey: req.IdempotencyKey,
                DestinationCardToken: card.TokenReferenceEncrypted,
                Amount: req.Amount - fee, // driver receives net
                Currency: "GEL",
                Reference: $"PayTaxi cashout {cashout.Id:N}",
                ParkId: park.Id), ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bank adapter threw for cashout {Id}", cashout.Id);
            if (limitDecremented) await RestoreLimitAsync(park.Id, requiredLimit, ct);
            await FailAsync(cashout, "BANK_EXCEPTION", ex.Message, ct);
            return ToResult(cashout);
        }

        if (!bankResult.Success)
        {
            if (limitDecremented) await RestoreLimitAsync(park.Id, requiredLimit, ct);
            await FailAsync(cashout, bankResult.ErrorCode ?? "BANK_FAILED",
                bankResult.ErrorMessage ?? "Bank transfer failed", ct);
            return ToResult(cashout);
        }

        cashout.BankTransferId = bankResult.TransferId;
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.BankTransferSent,
            Amount = req.Amount - fee,
            Reference = bankResult.TransferId,
            Notes = $"Bank transfer via {park.BankProvider}",
        });
        await _db.SaveChangesAsync(ct);

        // ── Yandex deduct ─────────────────────────────────────────────
        YandexTransactionResult yandexResult;
        try
        {
            yandexResult = await _yandex.PostCashoutTransactionAsync(
                parkId: park.Id,
                driverProfileId: driver.YandexDriverProfileId!,
                amount: req.Amount, // gross — Yandex is told the driver's balance dropped by the gross amount
                idempotencyKey: req.IdempotencyKey,
                ct: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Yandex post threw AFTER bank transfer {TransferId} succeeded — flagging for review",
                bankResult.TransferId);
            await FlagForReviewAsync(cashout,
                "YANDEX_POST_EXCEPTION",
                $"Bank transfer {bankResult.TransferId} sent but Yandex post threw: {ex.Message}",
                ct);
            return ToResult(cashout);
        }

        if (!yandexResult.Success)
        {
            await FlagForReviewAsync(cashout,
                yandexResult.ErrorCode ?? "YANDEX_POST_FAILED",
                $"Bank transfer {bankResult.TransferId} sent but Yandex returned: {yandexResult.ErrorMessage}",
                ct);
            return ToResult(cashout);
        }

        // ── Complete ─────────────────────────────────────────────────
        cashout.YandexTransactionId = yandexResult.TransactionId;
        cashout.Status = CashoutStatus.Completed;
        cashout.CompletedAt = DateTime.UtcNow;

        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.YandexDeducted,
            Amount = req.Amount,
            Reference = yandexResult.TransactionId,
        });
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.FeeCollected,
            Amount = fee,
            Notes = "Park fee on cashout",
        });
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = park.Id,
            EntryType = LedgerEntryType.CashoutCompleted,
            Amount = req.Amount,
        });
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Cashout {Id} completed: park={Park} driver={Driver} amount={Amount} fee={Fee} bank_tx={Bank} yandex_tx={Yandex}",
            cashout.Id, park.Name, driver.Name, req.Amount, fee, bankResult.TransferId, yandexResult.TransactionId);

        await _notifier.NotifyCashoutCompletedAsync(driver.Id, req.Amount - fee, card.MaskedPan, ct);

        return ToResult(cashout);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private IBankPayoutAdapter ResolveBankAdapter(string provider)
    {
        // Try the provider key as-is; fall back to MOCK for safety in dev.
        return _services.GetKeyedService<IBankPayoutAdapter>(provider)
            ?? _services.GetKeyedService<IBankPayoutAdapter>("MOCK")
            ?? throw new InvalidOperationException($"No bank adapter registered for provider '{provider}'");
    }

    private async Task FailAsync(Cashout cashout, string code, string message, CancellationToken ct)
    {
        cashout.Status = CashoutStatus.Failed;
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
        cashout.FailureReason = $"{code}: {message}";
        _db.LedgerEntries.Add(new LedgerEntry
        {
            CashoutId = cashout.Id,
            ParkId = cashout.ParkId,
            EntryType = LedgerEntryType.BankTransferSent, // record the orphaned bank send
            Amount = cashout.Amount - cashout.Fee,
            Reference = cashout.BankTransferId,
            Notes = $"REVIEW REQUIRED — {message}",
        });
        await _db.SaveChangesAsync(ct);

        _log.LogError(
            "Cashout {Id} flagged ReviewRequired — bank transfer {Bank} succeeded but Yandex post failed: {Message}",
            cashout.Id, cashout.BankTransferId, message);

        await _notifier.NotifyCashoutReviewRequiredAsync(cashout.DriverId, cashout.Amount, ct);
    }

    private async Task RestoreLimitAsync(Guid parkId, decimal amount, CancellationToken ct)
    {
        await _db.Parks
            .Where(p => p.Id == parkId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                p => p.AuthorizationLimit,
                p => p.AuthorizationLimit!.Value + amount), ct);
    }

    /// <summary>Fee model placeholder: max(2 GEL, 1% of amount). Replace once <see cref="Park"/> exposes fee config.</summary>
    private static decimal ComputeFee(decimal amount) =>
        Math.Max(2m, Math.Round(amount * 0.01m, 2, MidpointRounding.AwayFromZero));

    private static CashoutSagaResult ToResult(Cashout c, bool wasDeduped = false) => new(
        CashoutId: c.Id,
        Status: c.Status.ToString(),
        Amount: c.Amount,
        Fee: c.Fee,
        Net: c.Amount - c.Fee,
        BankTransferId: c.BankTransferId,
        YandexTransactionId: c.YandexTransactionId,
        FailureReason: c.FailureReason,
        WasDeduped: wasDeduped);
}
