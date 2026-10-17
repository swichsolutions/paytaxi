using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Adapters.Yandex;
using PayTaxi.Infrastructure.Data;
using static PayTaxi.Core.Interfaces.CashoutRejectionCodes;

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
///      Yandex not answering at all is caught one step earlier: the balance read under the lock fails and the
///      request is REFUSED (balance_unavailable) — nothing debited, nothing queued; the driver tries again later.
///      Only payouts already debited wait (step 6), never a request Yandex could not take.
///   6. Bank: transfer net (gross − fee) from the park account to the driver's IBAN.
///        · success            → Completed (+ BankTransferSent, FeeCollected, CashoutCompleted)
///        · retryable failure  → Queued with a backoff; the PayoutQueueWorker drains it.
///                               Bank down, park account short (INSUFFICIENT_FUNDS), no payout
///                               account: the payout WAITS, however long that takes — the
///                               contract makes "queue and execute automatically once the cause
///                               is gone" the agreed behaviour (agreement §5.5, Annex 1 §3.2,
///                               Annex 3 §5). Driver sees "on its way, goes out automatically".
///        · thrown / ambiguous → ask the bank by our document id before ever re-sending. If the
///                               outcome stays unknown for Cashout:MaxPayoutAttempts attempts in
///                               a row → ReviewRequired (never a reversal: money may have left).
///        · hard failure (the bank says "never") → reverse the Yandex debit (+gross) → Failed.
///          If the reversal itself fails → ReviewRequired.
///      Nothing that waits is ever given back automatically; a human can (Cancel in the console,
///      <see cref="CancelQueuedAsync"/>), through the same reversal path.
///   Once the park has revoked the platform's authorisation (Annex 3 §7, <see cref="ParkAuthorisation"/>) nothing is
///   sent from its account any more: a new request is refused up front (authorisation_revoked), and a payout not yet
///   at the bank waits Queued with AUTHORISATION_REVOKED until a person cancels it.
///   On completion the fee's regime (the park's licensee, or Swich-direct) is stamped on the cashout (§2.2).
///
/// Why Yandex first: a debit we can always compensate (post +amount back). A bank
/// transfer we cannot claw back — bank-first would let a driver cash the same balance
/// twice if the Yandex write failed after the money left.
///
/// The 0.50 GEL fee never moves: the bank transfer is for the net amount, so the fee
/// simply stays in the park's account. The monthly settlement engine (licence agreement §2.5)
/// collects Swich's share of the month's fees from the park's account.
/// </summary>
public class CashoutOrchestrator : ICashoutOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly IYandexFleetClient _yandex;
    private readonly INotificationService _notifier;
    private readonly CashoutOptions _opts;
    private readonly YandexFleetOptions _yandexOpts;
    private readonly ILogger<CashoutOrchestrator> _log;

    public CashoutOrchestrator(
        AppDbContext db,
        IServiceProvider services,
        IYandexFleetClient yandex,
        INotificationService notifier,
        IOptions<CashoutOptions> opts,
        IOptions<YandexFleetOptions> yandexOpts,
        ILogger<CashoutOrchestrator> log)
    {
        _db = db;
        _services = services;
        _yandex = yandex;
        _notifier = notifier;
        _opts = opts.Value;
        _yandexOpts = yandexOpts.Value;
        _log = log;
    }

    /// <summary>
    /// Bank error codes after which we genuinely don't know whether money left the account
    /// (the call threw AND the follow-up lookup failed). Exhausting retries on one of these
    /// must end in ReviewRequired, never in a Yandex reversal.
    /// </summary>
    private static bool IsAmbiguous(string code) =>
        code is "BANK_EXCEPTION" || code.EndsWith("_UNRESOLVED", StringComparison.Ordinal) || code.EndsWith("_UNKNOWN", StringComparison.Ordinal);

    /// <summary>
    /// True when a document of this cashout went to the bank and the bank never said what became of it
    /// (the sticky marker, the counter, or the recorded reason for rows older than both) — money may
    /// have left, so it can't be cancelled.
    /// </summary>
    public static bool HasUnresolvedOutcome(string? unknownOutcomeDocument, int unresolvedAttempts, string? failureReason)
    {
        if (unknownOutcomeDocument is not null || unresolvedAttempts > 0) return true;
        if (string.IsNullOrEmpty(failureReason)) return false;
        var i = failureReason.IndexOf(':');
        return i > 0 && IsAmbiguous(failureReason[..i].Trim());
    }

    /// <summary>Longest gap between two attempts of a waiting payout: a top-up is noticed within this.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The bank document id for the cashout's current payment order. The first order carries the
    /// cashout's own idempotency key, so every pre-existing row and every lost-response lookup is
    /// unchanged; later ones exist only after the bank refused an accepted order for lack of funds.
    /// </summary>
    private static string DocumentKey(Cashout c) => DocumentKey(c, c.BankDocumentSeq);

    private static string DocumentKey(Cashout c, int seq) =>
        seq == 0 ? c.IdempotencyKey : $"{c.IdempotencyKey}#{seq}";

    // ═══════════════════════════════════════════════════════════════════
    //  New cashout
    // ═══════════════════════════════════════════════════════════════════

    public async Task<CashoutSagaResult> RunAsync(CashoutSagaRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required", nameof(req));
        if (req.Amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(req));

        // ── Idempotency check (scoped: a key only ever belongs to one driver in one park) ──
        var existing = await FindByKeyAsync(req, ct);
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
            ?? throw CashoutRejectedException.Of(DriverNotFound, $"Driver {req.DriverId} not found in park {req.ParkId}");
        var card = await _db.BankCards.FirstOrDefaultAsync(b => b.Id == req.BankCardId && b.DriverId == req.DriverId, ct)
            ?? throw CashoutRejectedException.Of(DestinationNotFound, $"Payout destination {req.BankCardId} not found for driver {req.DriverId}");

        // Annex 3 §7: from the revocation day the platform has no authority to pay from the park's account — refused
        // before anything moves (no Yandex debit to give back). Checked before the status: it is the lasting reason,
        // and reactivating the park would not lift it.
        if (ParkAuthorisation.IsRevoked(park, SettlementService.TodayLocal()))
            throw CashoutRejectedException.Of(AuthorisationRevoked,
                $"The park revoked the platform's authorisation to pay from its account from {park.AuthorisationRevokedFrom:yyyy-MM-dd}",
                ("from", park.AuthorisationRevokedFrom?.ToString("yyyy-MM-dd")));
        if (park.Status != ParkStatus.Active)
            throw CashoutRejectedException.Of(ParkInactive, $"Park is not active (status={park.Status})", ("status", park.Status.ToString()));
        if (driver.Status != DriverStatus.Active)
            throw CashoutRejectedException.Of(DriverInactive, $"Driver is not active (status={driver.Status})", ("status", driver.Status.ToString()));
        if (driver.YandexDriverProfileId is null)
            throw CashoutRejectedException.Of(DriverNotLinked, "Driver is not linked to a Yandex profile");
        if (!card.IsActive)
            throw CashoutRejectedException.Of(DestinationRemoved, "Payout destination has been removed");
        if (string.IsNullOrWhiteSpace(card.Iban))
            throw CashoutRejectedException.Of(DestinationNoIban, "Payout destination has no IBAN — add a bank account first");
        if (_yandexOpts.ReadOnlyMode)
            throw CashoutRejectedException.Of(YandexReadOnly, "Cashouts are temporarily paused (Yandex writes disabled)");

        // ── Fee & limits (per-park config) ────────────────────────────
        var fee = Math.Round(park.CashoutFee, 2, MidpointRounding.AwayFromZero);
        // Money is tetri-exact: a request with more than two decimals is refused, never rounded —
        // 5.005 must not silently become a 5.01 debit.
        if (req.Amount != Math.Round(req.Amount, 2))
            throw CashoutRejectedException.Of(AmountPrecision, "Amount must have at most two decimals", ("amount", req.Amount));
        var amount = Math.Round(req.Amount, 2, MidpointRounding.AwayFromZero);

        if (amount < park.MinCashoutAmount)
            throw CashoutRejectedException.Of(BelowMinimum, $"Minimum cashout is {park.MinCashoutAmount:F2} GEL", ("min", park.MinCashoutAmount));
        if (park.MaxCashoutAmount is { } max && amount > max)
            throw CashoutRejectedException.Of(AboveMaximum, $"Maximum cashout is {max:F2} GEL", ("max", max));
        if (amount <= fee)
            throw CashoutRejectedException.Of(AmountNotAboveFee, $"Amount must exceed the {fee:F2} GEL fee", ("fee", fee));

        // ── Route to the park account at the driver's bank ────────────
        var account = RouteAccount(park, card.BankCode)
            ?? throw CashoutRejectedException.Of(BankNotSupported,
                $"The park has no payout account at bank '{card.BankCode}'",
                ("bankCode", card.BankCode),
                ("supported", park.BankAccounts.Select(a => a.BankCode).Distinct().ToArray()));

        // ── Reserve (Processing) under a per-driver lock ──────────────
        // The advisory lock serialises concurrent requests for the same driver (double tap,
        // two devices). EVERYTHING that decides whether this driver may be paid — the in-flight
        // check, the balance read, the daily limit — runs inside it. Reading the balance before
        // the lock let two simultaneous requests both see the pre-debit balance; the second one
        // then found nothing in flight (the first had already completed) and would have been paid
        // too — real Yandex accepts a negative balance, only the mock refused it.
        Cashout cashout;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey(driver.Id)})", ct);

            // Someone else may have won the race with the same key while we waited.
            var raced = await FindByKeyAsync(req, ct);
            if (raced is not null)
            {
                await tx.RollbackAsync(ct);
                return ToResult(raced, wasDeduped: true);
            }

            var inFlight = await _db.Cashouts.AsNoTracking()
                .AnyAsync(c => c.DriverId == driver.Id
                            && (c.Status == CashoutStatus.Processing || c.Status == CashoutStatus.Queued), ct);
            if (inFlight)
                throw CashoutRejectedException.Of(CashoutInFlight, "A previous cashout is still being processed — wait for it to finish");

            // ── Yandex balance: never debit more than the driver has ───────
            // Yandex Fleet accepts transactions that push a balance negative; only our check
            // stands between a driver and the park's money. Read here, after the lock, so it
            // already reflects any cashout that finished while we were waiting.
            // Yandex not answering ends the request here — REFUSED, not queued (the owner's decision). Without the
            // balance nothing may be debited, and nothing has been: there is no payout to hold until Yandex is back,
            // so the driver is told so and asks again later. Only a payout already debited ever waits (bank down,
            // park account short: RequeueAsync).
            decimal balance;
            try
            {
                balance = await _yandex.GetDriverBalanceAsync(park.Id, driver.YandexDriverProfileId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Balance read failed for driver {Driver}; cashout refused (nothing debited, nothing queued)", driver.Id);
                throw CashoutRejectedException.Of(BalanceUnavailable,
                    "Yandex Fleet is not answering, so the balance cannot be checked: nothing was deducted and the request was not queued. Try again later.");
            }
            if (amount > balance)
                throw CashoutRejectedException.Of(InsufficientBalance,
                    $"Amount {amount:F2} GEL exceeds the available balance {balance:F2} GEL", ("balance", balance));

            if (park.DailyCashoutLimitPerDriver is { } dailyLimit)
            {
                var dayStart = LocalDayStartUtc();
                var todaySoFar = await _db.Cashouts
                    .Where(c => c.DriverId == driver.Id
                             && c.CreatedAt >= dayStart
                             && c.Status != CashoutStatus.Failed)
                    .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
                if (todaySoFar + amount > dailyLimit)
                    throw CashoutRejectedException.Of(DailyLimitReached,
                        $"Daily cashout limit of {dailyLimit:F2} GEL reached ({todaySoFar:F2} already today)",
                        ("limit", dailyLimit), ("usedToday", todaySoFar));
            }

            cashout = new Cashout
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
                RetryOfCashoutId = req.RetryOfCashoutId,
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
            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Same key already used by ANOTHER driver/park (ours would have been found above).
                _log.LogWarning("Idempotency key {Key} collides with a cashout of a different driver", req.IdempotencyKey);
                throw CashoutRejectedException.Of(IdempotencyKeyConflict, "This request id was already used for a different cashout");
            }
        }

        // From here on money is in motion. A client that disconnects must not be able to
        // abort the saga halfway (Yandex debited, bank never asked), so the request token
        // is deliberately dropped: the saga runs to a terminal or queued state regardless.
        ct = CancellationToken.None;

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
        catch (YandexReadOnlyModeException)
        {
            // Kill switch flipped between our pre-check and the post. Nothing moved.
            await FailAsync(cashout, "YANDEX_READ_ONLY", "Cashouts are paused (Yandex writes disabled)", ct);
            return ToResult(cashout);
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
        await AdjustBalanceCacheAsync(driver.Id, -amount, ct);
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
        // Atomic claim: only one worker/tick may pick this row up. Two shapes qualify:
        //   · Queued            — bank said "not now", retry the payout
        //   · Processing + BankTransferId + NextAttemptAt — bank accepted asynchronously, poll its status
        // Clearing NextAttemptAt marks the row as claimed until the step decides what's next.
        var now = DateTime.UtcNow;
        var claimed = await _db.Cashouts
            .Where(c => c.Id == cashoutId
                     && ((c.Status == CashoutStatus.Queued && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
                      || (c.Status == CashoutStatus.Processing && c.BankTransferId != null
                          && c.NextAttemptAt != null && c.NextAttemptAt <= now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CashoutStatus.Processing)
                .SetProperty(c => c.NextAttemptAt, (DateTime?)null), ct);

        var cashout = await _db.Cashouts.FirstOrDefaultAsync(c => c.Id == cashoutId, ct)
            ?? throw new InvalidOperationException($"Cashout {cashoutId} not found");

        if (claimed == 0)
        {
            _log.LogDebug("Cashout {Id} not claimable (status={Status}, next={Next})",
                cashoutId, cashout.Status, cashout.NextAttemptAt);
            return ToResult(cashout);
        }

        // Claimed: finish the step even if the host is shutting down (see RunAsync).
        ct = CancellationToken.None;

        var park = await _db.Parks
            .Include(p => p.BankAccounts.Where(a => a.IsActive))
            .FirstAsync(p => p.Id == cashout.ParkId, ct);
        var driver = await _db.Drivers.FirstAsync(d => d.Id == cashout.DriverId, ct);
        var card = await _db.BankCards.FirstAsync(b => b.Id == cashout.BankCardId, ct);

        // Annex 3 §7: a payout not yet at the bank is never sent once the park has revoked the authorisation (whatever
        // else it waited for). One the bank already accepted is still polled below: asking moves no money.
        if (cashout.BankTransferId is null && ParkAuthorisation.IsRevoked(park, SettlementService.TodayLocal()))
        {
            await HoldForRevocationAsync(cashout, park, ct);
            return ToResult(cashout);
        }

        var original = park.BankAccounts.FirstOrDefault(a => a.Id == cashout.ParkBankAccountId);

        // A document this payout sent may have paid — the bank's answer was lost. Re-sending it is safe only where the
        // bank's duplicate check holds: the same DBI user on the same account. Through another account the same document
        // id is a NEW order there, and the driver could be paid twice from the park's money. So a payout with an
        // unanswered document never re-routes; a human checks the old document first. (A change of the account's
        // provider or bank user is held the same way when it is saved — HoldUnansweredDocumentsAsync.)
        if (original is null && cashout.UnknownOutcomeDocument is { } unanswered && cashout.BankTransferId is null
            && cashout.ParkBankAccountId is { } sentVia)
        {
            await FlagForReviewAsync(cashout, RailChangedCode,
                $"document {unanswered} went to the bank through payout account {sentVia}, which is no longer active, and the bank " +
                "never said what became of it — it is not re-sent through another account. Check it at the bank before resolving.", ct);
            return ToResult(cashout);
        }

        // Re-route: the park may have added/removed an account since the cashout was queued.
        var account = original ?? RouteAccount(park, card.BankCode);

        if (account is null)
        {
            // Park-side and fixable (add or re-activate an account): wait, don't give the money back.
            await RequeueAsync(cashout, driver,
                BankErrorCodes.NoParkAccount, $"Park has no active payout account at bank '{card.BankCode}'", ct);
            return ToResult(cashout);
        }

        cashout.ParkBankAccountId = account.Id;

        if (cashout.BankTransferId is not null)
        {
            // Accepted by an asynchronous rail earlier — ask how it went.
            await CheckPendingAsync(cashout, park, driver, card, account, ct);
            return ToResult(cashout);
        }

        await AttemptPayoutAsync(cashout, park, driver, card, account, ct);
        return ToResult(cashout);
    }

    /// <summary>
    /// The bank accepted the order but executes it asynchronously (TBC: statuses G/WC/CERT/VERIF).
    /// Keep the cashout Processing with the bank id and let the queue worker poll.
    /// </summary>
    private async Task MarkPendingAtBankAsync(Cashout cashout, string transferId, CancellationToken ct)
    {
        cashout.BankTransferId = transferId;
        cashout.Status = CashoutStatus.Processing;
        cashout.NextAttemptAt = DateTime.UtcNow.AddSeconds(_opts.PendingPollSeconds);
        cashout.FailureReason = null;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Cashout {Id}: bank accepted transfer {TransferId}, awaiting execution (poll in {S}s)",
            cashout.Id, transferId, _opts.PendingPollSeconds);
    }

    /// <summary>Poll a pending bank transfer. Money may already have moved, so this never reverses on Unknown.</summary>
    private async Task CheckPendingAsync(
        Cashout cashout, Park park, Driver driver, BankCard card, ParkBankAccount account, CancellationToken ct)
    {
        var bank = ResolveBankAdapter(account.Provider);
        var source = ToContext(park, account);
        BankTransferOutcome outcome;
        try
        {
            // No adapter for the account's provider on this server: nobody can say how the order went, so it stays
            // pending (and goes to review after PendingReviewHours) — never completed, never reversed.
            outcome = bank is null
                ? new BankTransferOutcome(BankTransferStatus.Unknown)
                : await bank.GetTransferOutcomeAsync(source, cashout.BankTransferId!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Status poll failed for cashout {Id} / transfer {TransferId}", cashout.Id, cashout.BankTransferId);
            outcome = new BankTransferOutcome(BankTransferStatus.Unknown);
        }

        switch (outcome.Status)
        {
            case BankTransferStatus.Completed:
                await CompleteAsync(cashout, park, driver, card, account, cashout.BankTransferId!, ct);
                return;

            case BankTransferStatus.Failed when outcome.ErrorCode == BankErrorCodes.InsufficientFunds:
            {
                // Accepted, then refused because the park's account was short. That order is final and
                // paid nothing — so this is a wait for a top-up (Annex 3 §5), not a reason to give the
                // driver's money back. The dead order's id cannot be reused at the bank: the next attempt
                // goes out as a fresh document.
                var deadOrder = cashout.BankTransferId;
                cashout.BankTransferId = null;
                cashout.BankDocumentSeq++;
                _log.LogWarning("Cashout {Id}: bank refused accepted order {TransferId} for lack of funds; next attempt is document #{Seq}",
                    cashout.Id, deadOrder, cashout.BankDocumentSeq);
                await RequeueAsync(cashout, driver, BankErrorCodes.InsufficientFunds,
                    $"{outcome.ErrorMessage ?? "Insufficient funds on the park account"} (bank order {deadOrder} refused)", ct);
                return;
            }

            case BankTransferStatus.Failed:
                // The bank rejected the order after accepting it — nothing was paid out.
                await AbandonAsync(cashout, park, driver, "BANK_REPORTED_FAILED",
                    $"Bank reports transfer {cashout.BankTransferId} failed{(outcome.ErrorMessage is { } why ? $": {why}" : "")}", ct);
                return;

            default:
                // Measured from the attempt the bank accepted, not from the request: a payout that
                // waited two days for a top-up must not be flagged on its first status poll.
                var pendingFor = DateTime.UtcNow - (cashout.LastAttemptAt ?? cashout.CreatedAt);
                if (_opts.PendingReviewHours > 0 && pendingFor > TimeSpan.FromHours(_opts.PendingReviewHours))
                {
                    // Too long in limbo and the money MAY have moved: a human must look, no reversal.
                    await FlagForReviewAsync(cashout, "BANK_PENDING_TIMEOUT",
                        $"Transfer {cashout.BankTransferId} still not final after {pendingFor.TotalHours:F1} h", ct);
                    return;
                }
                cashout.NextAttemptAt = DateTime.UtcNow.AddSeconds(_opts.PendingPollSeconds);
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Cashout {Id}: transfer {TransferId} still {Status}; polling again in {S}s",
                    cashout.Id, cashout.BankTransferId, outcome.Status, _opts.PendingPollSeconds);
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Bank payout attempt
    // ═══════════════════════════════════════════════════════════════════

    private async Task AttemptPayoutAsync(
        Cashout cashout, Park park, Driver driver, BankCard card, ParkBankAccount account, CancellationToken ct)
    {
        // The last gate before money leaves the park's account (a new request that passed RunAsync's check just before
        // the revocation day began lands here too).
        if (ParkAuthorisation.IsRevoked(park, SettlementService.TodayLocal()))
        {
            await HoldForRevocationAsync(cashout, park, ct);
            return;
        }

        var net = cashout.Amount - cashout.Fee;
        var bank = ResolveBankAdapter(account.Provider);
        var source = ToContext(park, account);
        var documentKey = DocumentKey(cashout);

        cashout.AttemptCount++;
        cashout.LastAttemptAt = DateTime.UtcNow;
        cashout.Status = CashoutStatus.Processing;
        await _db.SaveChangesAsync(ct);

        if (bank is null)
        {
            // The account names a provider this server has no adapter for — the mock on a real-rails server (it is not
            // registered there), or a typo. It used to fall back to the mock, which "completed" the payout while no money
            // moved. Now it is the missing-adapter wait: nothing reached a bank, nothing is given back, it goes out once
            // the account's provider is set to its bank.
            _log.LogError("Cashout {Id}: no bank adapter for provider '{Provider}' of payout account {Account} on this server",
                cashout.Id, account.Provider, account.Id);
            await RequeueAsync(cashout, driver, PayoutWaitCodes.AdapterMissing,
                $"No bank adapter for provider '{account.Provider}' on this server — set the payout account's provider to its bank", ct);
            return;
        }

        BankTransferResult result;
        try
        {
            result = await bank.SendPayoutAsync(new BankTransferRequest(
                IdempotencyKey: documentKey,
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
            // Nothing reached a bank, so this says nothing about an earlier document either: the
            // unknown-outcome marker and its count stay as they are.
            _log.LogError(ex, "Bank adapter '{Provider}' is not implemented", account.Provider);
            await RequeueAsync(cashout, driver, "BANK_ADAPTER_MISSING", ex.Message, ct);
            return;
        }
        catch (Exception ex)
        {
            // Outcome unknown (timeout, dropped connection). NEVER blindly re-fire:
            // ask the bank whether our document id exists.
            _log.LogWarning(ex, "Bank call threw for cashout {Id} (attempt {N}) — checking by document id",
                cashout.Id, cashout.AttemptCount);

            BankTransferLookup? lookup = null;
            try { lookup = await bank.FindTransferByIdempotencyKeyAsync(source, documentKey, ct); }
            catch (Exception lookupEx)
            {
                _log.LogWarning(lookupEx, "Status lookup also failed for cashout {Id}", cashout.Id);
            }

            if (lookup is not null && lookup.Status is BankTransferStatus.Completed or BankTransferStatus.Pending)
            {
                _log.LogInformation("Bank confirms transfer {TransferId} ({Status}) for cashout {Id} despite the lost response",
                    lookup.TransferId, lookup.Status, cashout.Id);
                // Pending stays pending: the poll loop decides when it is really Completed.
                result = new BankTransferResult(true, lookup.TransferId, null, null,
                    IsPending: lookup.Status == BankTransferStatus.Pending);
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

        var code = result.Success ? null : result.ErrorCode ?? "BANK_FAILED";

        // What this attempt tells us about the DOCUMENT it sent. An unknown outcome marks the document as
        // possibly paid, and the mark is sticky: a later definite answer to a later attempt says nothing
        // about it. Clearing it — and with it the count of unknown outcomes — takes an answer about this
        // very document: the bank named one of our orders (accepted, executed, refused — from the send,
        // the duplicate-id recovery or the lookup). A refusal that never reached the bank's duplicate check
        // (expired password, blocked user, credentials we could not parse, a gateway that is down, no
        // money at import) leaves both untouched. Without this, "Cancel" could give the gross back on
        // Yandex while document K — sent earlier, answer lost — still pays the driver: paid twice.
        if (code is not null && IsAmbiguous(code))
        {
            cashout.UnknownOutcomeDocument ??= documentKey;
            cashout.UnresolvedAttempts++;
        }
        else if (result.Success || result.TransferId is not null)
        {
            if (cashout.UnknownOutcomeDocument == documentKey) cashout.UnknownOutcomeDocument = null;
            cashout.UnresolvedAttempts = 0;
        }

        if (result.Success && result.IsPending)
        {
            await MarkPendingAtBankAsync(cashout, result.TransferId!, ct);
            return;
        }

        if (result.Success)
        {
            await CompleteAsync(cashout, park, driver, card, account, result.TransferId!, ct);
            return;
        }

        var message = result.ErrorMessage ?? "Bank transfer failed";

        if (code == BankErrorCodes.InsufficientFunds && result.TransferId is not null)
        {
            // The bank created our order and then refused it for lack of funds (an asynchronous rail
            // such as TBC answers this way). That order is final and paid nothing, and the bank will
            // refuse a second order under the same request id — re-sending it would return the same
            // refusal forever, even after a top-up. The next attempt goes out as a fresh document.
            cashout.BankDocumentSeq++;
            message = $"{message} (bank order {result.TransferId} refused)";
            _log.LogWarning("Cashout {Id}: bank refused order {TransferId} for lack of funds; next attempt is document #{Seq}",
                cashout.Id, result.TransferId, cashout.BankDocumentSeq);
        }

        // An unknown outcome never reaches the reversal below, even if an adapter marked it final:
        // reversing Yandex while the money may have left could pay the driver twice.
        if (result.IsRetryable || IsAmbiguous(code!))
            await RequeueAsync(cashout, driver, code!, message, ct);
        else
            await AbandonAsync(cashout, park, driver, code!, message, ct);
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

        // §2.2: the fee is earned now, under the arrangement in force now — the park's licensee, or Swich-direct. The
        // monthly settlement follows this stamp, never the assignment on its run day, so moving a park later leaves what it
        // already earned where it was earned. Read fresh (a long wait in the queue may span a reassignment).
        cashout.FeeRegimeStamped = true;
        cashout.FeeLicenseeId = await _db.Parks.AsNoTracking().Where(p => p.Id == park.Id).Select(p => p.LicenseeId).FirstAsync(ct);

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
            cashout.Id, park.Name, driver.Id, cashout.Amount, cashout.Fee, net, transferId,
            cashout.YandexTransactionId, cashout.AttemptCount);

        await _notifier.NotifyCashoutCompletedAsync(driver.Id, net, $"{card.BankType} {card.MaskedPan}", ct);
    }

    /// <summary>
    /// Bank said "not now" (or could not be asked). Park the cashout in the queue — for as long as the
    /// cause lasts. A bank outage, an empty park account (INSUFFICIENT_FUNDS) or a missing payout account
    /// is not a platform failure, and the contract makes "queue, then execute automatically once the cause
    /// is removed" the agreed behaviour (agreement §5.5, Annex 1 §3.2, Annex 3 §5). So nothing here gives the
    /// driver's money back; a human can (Cancel in the console).
    ///
    /// A Yandex outage never gets here: this runs only after the Yandex debit went through. While Yandex does
    /// not answer, a new request is refused before anything moves (balance_unavailable) — nothing to queue.
    /// Annex 1 §3.2 lists Yandex among the causes whose requests are queued; the lawyer is asked to tell the two
    /// apart (docs/LAWYER-NOTES.md).
    ///
    /// The one exception is an outcome nobody can classify: the bank call threw and the lookup by
    /// document id could not answer either. Re-sending the same document is safe only while the bank
    /// still recognises its id, so after <see cref="CashoutOptions.MaxPayoutAttempts"/> such attempts
    /// since the bank last answered about the document (or past the optional
    /// <see cref="CashoutOptions.MaxQueueAgeHours"/>) a human must look:
    /// ReviewRequired, never a reversal — the money may already have left.
    /// </summary>
    private async Task RequeueAsync(Cashout cashout, Driver driver, string code, string message, CancellationToken ct)
    {
        if (IsAmbiguous(code))
        {
            var age = DateTime.UtcNow - cashout.CreatedAt;
            var tooManyUnknown = cashout.UnresolvedAttempts >= _opts.MaxPayoutAttempts;
            var tooOld = _opts.MaxQueueAgeHours > 0 && age > TimeSpan.FromHours(_opts.MaxQueueAgeHours);
            if (tooManyUnknown || tooOld)
            {
                await FlagForReviewAsync(cashout, code,
                    $"Payout outcome unknown — {message} ({cashout.UnresolvedAttempts} unanswered attempt(s) since the bank last answered about it, {age.TotalHours:F1} h since the request)", ct);
                return;
            }
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
    /// Annex 3 §7: the park revoked the platform's authorisation, so the payout is NOT sent — and not given back either:
    /// the driver was debited for a payout they asked for, and giving it back is a person's decision (Cancel; the console
    /// explains this code). It waits Queued with the revocation's code. No attempt is counted (nothing reached a bank) and
    /// the driver is not notified again. The row is looked at again within the usual backoff, so it moves on by itself
    /// should the revocation ever be lifted by hand.
    ///
    /// The reason is overwritten, so a document whose outcome the bank never told us — recorded only in the reason on rows
    /// older than the sticky marker — is moved to the marker first: Cancel must keep refusing to give back a payout that
    /// may already have been paid.
    /// </summary>
    private async Task HoldForRevocationAsync(Cashout cashout, Park park, CancellationToken ct)
    {
        if (cashout.UnknownOutcomeDocument is null && cashout.UnresolvedAttempts == 0
            && HasUnresolvedOutcome(null, 0, cashout.FailureReason))
            cashout.UnknownOutcomeDocument = DocumentKey(cashout);

        var delay = BackoffFor(Math.Max(1, cashout.AttemptCount));
        cashout.Status = CashoutStatus.Queued;
        cashout.NextAttemptAt = DateTime.UtcNow.Add(delay);
        cashout.FailureReason = $"{ParkAuthorisation.RevokedCode}: The park revoked the platform's authorisation to pay from its account " +
                                $"from {park.AuthorisationRevokedFrom:yyyy-MM-dd} (Annex 3 §7) — not sent; cancel it to return the amount to the driver's Yandex balance";
        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Cashout {Id} held: park {Park} revoked the payout authorisation from {From} — not sent, waits for a cancel",
            cashout.Id, park.Name, park.AuthorisationRevokedFrom);
    }

    /// <summary>
    /// The payout will not happen. The Yandex debit already went through, so give the
    /// money back on Yandex (+gross). Only after that is the cashout truly Failed.
    /// <paramref name="driverMessage"/> replaces <paramref name="message"/> in the driver's
    /// notification when the internal text is not for the driver (an admin's note, their e-mail).
    /// </summary>
    private async Task AbandonAsync(
        Cashout cashout, Park park, Driver driver, string code, string message, CancellationToken ct,
        string? driverMessage = null)
    {
        if (cashout.YandexTransactionId is null)
        {
            // Nothing was debited (shouldn't reach here, but be safe).
            await FailAsync(cashout, code, message, ct, driverMessage);
            return;
        }

        if (cashout.UnknownOutcomeDocument is { } unanswered)
        {
            // Every road to a reversal passes here. "Never" from the bank about THIS attempt is not "never"
            // about the document an earlier attempt sent and never heard back on: it may have paid. Giving
            // the gross back on Yandex now could pay the driver twice, so a human checks that document first.
            await FlagForReviewAsync(cashout, code,
                $"{message} — not given back: document {unanswered} went to the bank earlier and the bank never " +
                "said what became of it. Check it at the bank before resolving.", ct);
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
        await AdjustBalanceCacheAsync(cashout.DriverId, cashout.Amount, ct);
        await FailAsync(cashout, code, message, ct, driverMessage);
    }

    /// <summary>
    /// Keep the driver's cached Yandex balance honest between sync runs: the app reads
    /// <c>/me</c> from the cache for up to two minutes, so a debit (or a reversal) must show
    /// there immediately rather than after the next sync. Best effort — the sync worker and
    /// any live read overwrite it with Yandex's own figure.
    /// </summary>
    private async Task AdjustBalanceCacheAsync(Guid driverId, decimal delta, CancellationToken ct)
    {
        var row = await _db.YandexBalanceCaches.FirstOrDefaultAsync(c => c.DriverId == driverId, ct);
        if (row is null) return;
        row.Balance += delta;
        row.UpdatedAt = DateTime.UtcNow;
    }

    // ── Terminal helpers ─────────────────────────────────────────────

    /// <summary>What the driver is told about a failure: plain words, never our stored reason (bank internals, an admin's note).</summary>
    private const string ReturnedDriverMessage = "The payout could not be sent; the amount is back on your Yandex balance.";
    private const string NotChargedDriverMessage = "The payout could not be sent; your Yandex balance was not charged.";

    private async Task FailAsync(Cashout cashout, string code, string message, CancellationToken ct, string? driverMessage = null)
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
        // The driver's notification carries the code (the app translates it) and a plain sentence. The
        // stored message stays internal: a bank's error detail, a beneficiary check, an admin's note.
        var driverText = driverMessage
            ?? (cashout.YandexReversalTransactionId is not null ? ReturnedDriverMessage : NotChargedDriverMessage);
        await _notifier.NotifyCashoutFailedAsync(cashout.DriverId, cashout.Amount, driverText, code, ct);
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
            EntryType = LedgerEntryType.ReviewFlagged,
            Amount = cashout.Amount,
            Reference = code,
            Notes = $"REVIEW REQUIRED — {message}",
        });
        await _db.SaveChangesAsync(ct);

        _log.LogError("Cashout {Id} flagged ReviewRequired — {Code}: {Message}", cashout.Id, code, message);
        await _notifier.NotifyCashoutReviewRequiredAsync(cashout.DriverId, cashout.Amount, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Stale-row sweeper (called by PayoutQueueWorker)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cashout that is Processing with no bank id and no next attempt is one whose saga
    /// was interrupted (process crash, deploy) between the reserve and a decision. Nothing
    /// will ever pick it up, and its Yandex debit may or may not have landed — so after a
    /// grace period it goes to ReviewRequired for reconciliation to sort out. Never retried
    /// automatically: we can't tell whether the bank was already asked.
    /// </summary>
    public async Task<int> SweepStaleProcessingAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var stale = await _db.Cashouts
            .Where(c => c.Status == CashoutStatus.Processing
                     && c.BankTransferId == null
                     && c.NextAttemptAt == null
                     && c.UpdatedAt < cutoff)
            .ToListAsync(ct);

        foreach (var cashout in stale)
        {
            _log.LogError("Cashout {Id} stuck in Processing since {Since:u} — flagging for review", cashout.Id, cashout.UpdatedAt);
            await FlagForReviewAsync(cashout, "SAGA_INTERRUPTED",
                $"Processing since {cashout.UpdatedAt:u} with no bank transfer; yandex_tx={cashout.YandexTransactionId ?? "none"}. " +
                "Check Yandex for the debit and the bank for a payout before resolving.", CancellationToken.None);
        }
        return stale.Count;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Console cancel — the human way out of an indefinite wait
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Shown to the driver instead of the admin's note (which may be internal, and carries their e-mail).</summary>
    private const string CancelledDriverMessage = "The payout was cancelled and the amount returned to your Yandex balance.";

    /// <summary>
    /// How long a cancel waits for an attempt the queue worker is making right now (the row is claimed,
    /// nothing is at the bank yet). One attempt is one bank round trip — TBC's import plus its short status
    /// poll stays well inside this — so a click that lands mid-attempt is not lost to a sub-second race.
    /// </summary>
    private static readonly TimeSpan CancelWaitForAttempt = TimeSpan.FromSeconds(10);

    public async Task<CashoutSagaResult?> CancelQueuedAsync(
        Guid parkId, Guid cashoutId, string reason, string actor, CancellationToken ct = default)
    {
        var claimed = await ClaimForCancelAsync(parkId, cashoutId, ct);

        // The worker (or a saga run) holds the row for an attempt: wait for it to put the row back, then
        // claim. Its outcome decides — paid or accepted at the bank means not cancellable, back in the
        // queue means the cancel goes ahead (still under every check of the claim).
        var deadline = DateTime.UtcNow + CancelWaitForAttempt;
        while (claimed == 0 && DateTime.UtcNow < deadline && await AttemptInProgressAsync(parkId, cashoutId, ct))
        {
            await Task.Delay(250, ct);
            claimed = await ClaimForCancelAsync(parkId, cashoutId, ct);
        }
        if (claimed == 0) return null;

        // Claimed: finish even if the caller goes away (an abandoned claim would sit in Processing).
        ct = CancellationToken.None;

        var cashout = await _db.Cashouts.FirstAsync(c => c.Id == cashoutId, ct);
        var park = await _db.Parks.FirstAsync(p => p.Id == cashout.ParkId, ct);
        var driver = await _db.Drivers.FirstAsync(d => d.Id == cashout.DriverId, ct);

        // Second opinion from the bank itself, while we hold the claim (nothing can be sent meanwhile).
        await ConfirmNoLiveDocumentAsync(cashout, park, ct);

        _log.LogWarning("Cashout {Id} cancelled from the console by {Actor} after {Attempts} attempt(s): {Reason}",
            cashout.Id, actor, cashout.AttemptCount, reason);
        await AbandonAsync(cashout, park, driver, "CANCELLED", $"{reason} (by {actor})", ct, CancelledDriverMessage);
        return ToResult(cashout);
    }

    /// <summary>
    /// The same atomic claim the queue worker uses (Queued → Processing), narrowed to rows that are safe to
    /// give back: nothing accepted at the bank, and no document whose outcome the bank never told us
    /// (<see cref="Cashout.UnknownOutcomeDocument"/> — sticky, unlike the latest reason). Whoever flips the
    /// row first owns it, so a payout can never be sent by the worker AND reversed here. The unknown-outcome
    /// checks are part of the claim on purpose: read separately, a worker attempt could slip in between and
    /// leave an unanswered transfer behind a reversal (a double payment). UpdatedAt is stamped so the
    /// stale-saga sweeper does not take the claimed row for an interrupted saga while the reversal runs.
    /// The reason check covers rows queued before the marker and the counter existed.
    /// </summary>
    private Task<int> ClaimForCancelAsync(Guid parkId, Guid cashoutId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return _db.Cashouts
            .Where(c => c.Id == cashoutId && c.ParkId == parkId
                     && c.Status == CashoutStatus.Queued
                     && c.BankTransferId == null
                     && c.UnknownOutcomeDocument == null
                     && c.UnresolvedAttempts == 0
                     && (c.FailureReason == null
                         || !(c.FailureReason.StartsWith("BANK_EXCEPTION:")
                              || c.FailureReason.Contains("_UNRESOLVED:")
                              || c.FailureReason.Contains("_UNKNOWN:"))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CashoutStatus.Processing)
                .SetProperty(c => c.NextAttemptAt, (DateTime?)null)
                .SetProperty(c => c.UpdatedAt, now), ct);
    }

    /// <summary>The row is claimed for an attempt right now: Processing, nothing at the bank, no next attempt set.</summary>
    private Task<bool> AttemptInProgressAsync(Guid parkId, Guid cashoutId, CancellationToken ct) =>
        _db.Cashouts.AsNoTracking().AnyAsync(c => c.Id == cashoutId && c.ParkId == parkId
                                               && c.Status == CashoutStatus.Processing
                                               && c.BankTransferId == null
                                               && c.NextAttemptAt == null, ct);

    /// <summary>
    /// Before a cancel gives the gross back, ask the bank about every document this payout was ever sent as
    /// (K, K#1, …): each must be unknown to the bank or definitively failed. The marker already proves this
    /// for every attempt we made under it; the bank is asked as well because it is the last word — for rows
    /// older than the marker, and against an adapter that misread an answer. Throws
    /// <see cref="CashoutCancelRefusedException"/> (the row goes back to the queue, or to review) when a
    /// document is alive at the bank or the bank cannot be asked; a cancel is never worth a double payment,
    /// and the payout itself is safe to keep waiting — it goes out on its own once the cause is gone.
    /// </summary>
    private async Task ConfirmNoLiveDocumentAsync(Cashout cashout, Park park, CancellationToken ct)
    {
        if (cashout.AttemptCount == 0) return; // never sent: no document exists anywhere

        var account = cashout.ParkBankAccountId is { } accountId
            ? await _db.ParkBankAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            : null;
        if (account is null)
        {
            await ReleaseCancelClaimAsync(cashout, null, ct);
            throw new CashoutCancelRefusedException(CashoutCancelRefusedException.BankUnconfirmed,
                "The payout account it was sent from is gone, so the bank cannot be asked whether it was paid.");
        }

        // No adapter for this rail here: nothing could have gone out through it — the send path waits the same way
        // (BANK_ADAPTER_MISSING), as it does for an adapter that throws NotImplementedException below. A change of the
        // account's provider while a document waited for an answer was held for review when it was saved.
        var bank = ResolveBankAdapter(account.Provider);
        if (bank is null) return;
        var source = ToContext(park, account);
        for (var seq = 0; seq <= cashout.BankDocumentSeq; seq++)
        {
            var key = DocumentKey(cashout, seq);
            BankTransferLookup? lookup;
            try { lookup = await bank.FindTransferByIdempotencyKeyAsync(source, key, ct); }
            catch (NotImplementedException)
            {
                // No adapter for this rail: nothing could ever have gone out through it (the send path
                // treats it the same way — BANK_ADAPTER_MISSING).
                continue;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cancel of cashout {Id}: the bank could not be asked about document {Key}", cashout.Id, key);
                await ReleaseCancelClaimAsync(cashout, null, ct);
                throw new CashoutCancelRefusedException(CashoutCancelRefusedException.BankUnconfirmed,
                    "The bank could not be asked whether this payout was already sent. Try again once it answers.");
            }

            if (lookup is null || lookup.Status == BankTransferStatus.Failed) continue;

            // The bank holds this document alive (or cannot say what it is).
            if (seq == cashout.BankDocumentSeq)
            {
                // The current document: the saga sends it again on its next attempt and the bank's duplicate
                // check answers with that order (paid → Completed, pending → polled). Until then it is a
                // document whose outcome we do not know — exactly what the marker is for.
                _log.LogWarning("Cancel of cashout {Id} refused: the bank holds document {Key} as {Status} (order {TransferId})",
                    cashout.Id, key, lookup.Status, lookup.TransferId);
                await ReleaseCancelClaimAsync(cashout, key, ct);
            }
            else
            {
                // An earlier document was taken for dead (refused for lack of funds) and the bank says it is
                // not. Sending the current one could pay twice, reversing could too: a human must look.
                await FlagForReviewAsync(cashout, "CANCEL_REFUSED_LIVE_DOCUMENT",
                    $"Cancel refused: the bank reports earlier document {key} as {lookup.Status} (order {lookup.TransferId}) " +
                    $"while the payout waited on document {DocumentKey(cashout)}. Check both at the bank before resolving.", ct);
            }
            throw new CashoutCancelRefusedException(CashoutCancelRefusedException.BankHasOrder,
                $"The bank holds a payment order for this payout ({key}: {lookup.Status}). Nothing was cancelled.");
        }
    }

    /// <summary>
    /// Hand a claimed row back to the queue. With <paramref name="unknownDocument"/> (the bank holds that
    /// document) it is recorded and the row is due at once, so the next attempt re-sends it and learns its
    /// fate; otherwise the payout keeps its usual rhythm — the bank could not be asked, retrying now is moot.
    /// </summary>
    private async Task ReleaseCancelClaimAsync(Cashout cashout, string? unknownDocument, CancellationToken ct)
    {
        cashout.Status = CashoutStatus.Queued;
        cashout.NextAttemptAt = unknownDocument is not null ? DateTime.UtcNow : DateTime.UtcNow.Add(BackoffFor(cashout.AttemptCount));
        if (unknownDocument is not null) cashout.UnknownOutcomeDocument ??= unknownDocument;
        await _db.SaveChangesAsync(ct);
    }

    // ── Request helpers ──────────────────────────────────────────────

    private Task<Cashout?> FindByKeyAsync(CashoutSagaRequest req, CancellationToken ct) =>
        _db.Cashouts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdempotencyKey == req.IdempotencyKey
                                   && c.DriverId == req.DriverId
                                   && c.ParkId == req.ParkId, ct);

    /// <summary>Review code of a payout held because the rail its unanswered document went out on changed.</summary>
    public const string RailChangedCode = "RAIL_CHANGED_UNKNOWN_OUTCOME";

    /// <inheritdoc />
    public async Task<int> HoldUnansweredDocumentsAsync(Guid parkBankAccountId, string why, CancellationToken ct = default)
    {
        var ids = await _db.Cashouts.AsNoTracking()
            .Where(c => c.ParkBankAccountId == parkBankAccountId && c.Status == CashoutStatus.Queued
                     && c.UnknownOutcomeDocument != null && c.BankTransferId == null)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var held = 0;
        foreach (var id in ids)
        {
            // The same atomic claim the queue worker uses: a row it is attempting right now stays its own.
            var claimed = await _db.Cashouts
                .Where(c => c.Id == id && c.Status == CashoutStatus.Queued && c.UnknownOutcomeDocument != null && c.BankTransferId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, CashoutStatus.Processing)
                    .SetProperty(c => c.NextAttemptAt, (DateTime?)null), ct);
            if (claimed == 0) continue;
            var cashout = await _db.Cashouts.FirstAsync(c => c.Id == id, ct);
            await _db.Entry(cashout).ReloadAsync(ct);
            await FlagForReviewAsync(cashout, RailChangedCode,
                $"document {cashout.UnknownOutcomeDocument} went to the bank and the bank never said what became of it; then {why}. " +
                "Under the new settings the same document id would be a new order — check the old one at the bank before resolving.", ct);
            held++;
        }
        return held;
    }

    /// <summary>Stable 64-bit key for pg_advisory_xact_lock, derived from the driver id.</summary>
    private static long LockKey(Guid driverId) => BitConverter.ToInt64(driverId.ToByteArray(), 0);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    /// <summary>Start of the current day in the park's time zone (Tbilisi), in UTC — the daily limit resets at local midnight.</summary>
    private static DateTime LocalDayStartUtc()
    {
        var tz = SettlementService.ResolveTimeZone(null);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
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

    /// <summary>
    /// The adapter registered for the account's provider, or null — never a stand-in. An unknown provider used to fall back
    /// to the mock bank, whose "transfer" completed the payout while no money moved; on a real-rails server the mock is not
    /// even registered (BankRailPolicy), and every caller treats null as "the bank cannot be reached".
    /// </summary>
    private IBankPayoutAdapter? ResolveBankAdapter(string provider) =>
        _services.GetKeyedService<IBankPayoutAdapter>(provider)
        ?? _services.GetKeyedService<IBankPayoutAdapter>(provider.ToUpperInvariant());

    /// <summary>
    /// Delay before the next attempt: the configured schedule, its last step repeating for as long as
    /// the payout waits, never longer than <see cref="MaxBackoff"/> — a park that tops up its account
    /// sees the waiting payouts go out within 15 minutes, not whenever a long backoff happens to end.
    /// </summary>
    private TimeSpan BackoffFor(int attempt)
    {
        var schedule = _opts.BackoffSeconds is { Length: > 0 } s ? s : CashoutOptions.DefaultBackoff;
        var idx = Math.Clamp(attempt - 1, 0, schedule.Length - 1);
        var delay = TimeSpan.FromSeconds(schedule[idx]);
        return delay > MaxBackoff ? MaxBackoff : delay;
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

    /// <summary>
    /// Payout attempts whose outcome stayed unknown (the bank call threw and the lookup by document id
    /// could not answer), counted since the bank last answered about the payout's current document,
    /// before a human must look: the cashout goes to ReviewRequired, never to a reversal. Attempts the
    /// bank refused before looking at any document — "down", "not enough money", "no account", an
    /// expired password — neither count nor reset the count: those payouts wait without limit and go
    /// out once the cause is removed (agreement §5.5, Annex 1 §3.2, Annex 3 §5).
    /// </summary>
    public int MaxPayoutAttempts { get; set; } = 30;

    /// <summary>
    /// Optional ceiling (hours since the request) for a payout whose latest attempt's outcome is
    /// unknown: past it, such an attempt sends the cashout to ReviewRequired instead of retrying.
    /// 0 = off (default). It never applies to a payout that is merely waiting — waiting for the bank
    /// or for a top-up is the contractual behaviour, however long it lasts.
    /// </summary>
    public int MaxQueueAgeHours { get; set; } = 0;

    /// <summary>How often to poll a bank-accepted-but-not-executed transfer (asynchronous rails such as TBC).</summary>
    public int PendingPollSeconds { get; set; } = 20;

    /// <summary>
    /// A transfer the bank accepted but has not finalised after this many hours (counted from the
    /// attempt it accepted) goes to ReviewRequired: the money may have moved, so a human looks — no
    /// reversal. 0 = keep polling forever.
    /// </summary>
    public int PendingReviewHours { get; set; } = 12;

    /// <summary>
    /// Delay before attempt N+1, indexed by attempt number (last value repeats, capped at 15 minutes).
    /// Deliberately empty by default: the configuration binder APPENDS array elements to an
    /// existing array, so a non-empty default would silently prepend itself to the configured
    /// schedule. <see cref="DefaultBackoff"/> is applied at use time when nothing is configured.
    /// </summary>
    public int[] BackoffSeconds { get; set; } = Array.Empty<int>();
}
