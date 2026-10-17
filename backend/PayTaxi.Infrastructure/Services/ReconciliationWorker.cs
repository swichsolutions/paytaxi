using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Nightly (or periodically) cross-checks our cashout ledger against the
/// bank and Yandex Fleet. Writes a <see cref="ReconciliationRun"/> per
/// park per tick and a <see cref="ReconciliationDiscrepancy"/> for every
/// drift found.
///
/// What it looks for:
///   1. Completed cashout in our DB → must show up as a completed transfer
///      at the bank, AND a debit at Yandex. Amounts must match within
///      <see cref="ReconciliationOptions.AmountToleranceGel"/>.
///   2. Bank transfer in the window → must have a matching PayTaxi cashout
///      (otherwise our system sent money it doesn't track).
///   3. Cashouts in <c>Processing</c> for longer than <see cref="StuckPendingHours"/> since the bank
///      accepted them → stuck pending. (<c>Queued</c> ones wait by contract; the payout_waiting alert
///      shows them.) A payout may wait for days before it is paid, so no check assumes that a
///      cashout's request and its completion fall into the same window.
///
/// Window: last <see cref="WindowHours"/>, ending at <c>now − GracePeriodMinutes</c>
/// so in-flight cashouts aren't false-flagged.
///
/// Same hosting pattern as <see cref="BalanceSyncWorker"/> — singleton with
/// its own DI scope per tick.
/// </summary>
public class ReconciliationWorker : BackgroundService
{
    private const int StuckPendingHours = 2;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReconciliationOptions _opts;
    private readonly ILogger<ReconciliationWorker> _log;

    public ReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ReconciliationOptions> opts,
        ILogger<ReconciliationWorker> log)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("Reconciliation worker disabled by configuration");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation(
            "Reconciliation worker started (interval {Interval}s, window {Window}h, grace {Grace}m)",
            _opts.IntervalSeconds, _opts.WindowHours, _opts.GracePeriodMinutes);

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Reconciliation tick failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_opts.IntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("Reconciliation worker stopped");
    }

    private Task TickAsync(CancellationToken ct) => RunAsync(onlyParkId: null, graceMinutes: _opts.GracePeriodMinutes, ct);

    /// <summary>
    /// One reconciliation pass — every active park, or a single one. Also the "Run now" button in the
    /// admin console (<paramref name="graceMinutes"/> = 0 there, so a transfer made a minute ago counts).
    /// Returns the number of parks reconciled.
    /// </summary>
    public async Task<int> RunAsync(Guid? onlyParkId, int? graceMinutes, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var yandex = scope.ServiceProvider.GetRequiredService<IYandexFleetClient>();
        var bankResolver = scope.ServiceProvider;

        var now = DateTime.UtcNow;
        var windowTo = now.AddMinutes(-(graceMinutes ?? _opts.GracePeriodMinutes));
        var windowFrom = windowTo.AddHours(-_opts.WindowHours);

        // Suspended parks too: their in-flight payouts keep completing and must still be checked.
        var parks = await db.Parks
            .AsNoTracking()
            .Where(p => (p.Status == ParkStatus.Active || p.Status == ParkStatus.Suspended)
                     && (onlyParkId == null || p.Id == onlyParkId))
            .Select(p => new { p.Id, p.Name, p.LegalEntityName })
            .ToListAsync(ct);

        foreach (var park in parks)
        {
            ct.ThrowIfCancellationRequested();
            var accounts = await db.ParkBankAccounts.AsNoTracking()
                .Where(a => a.ParkId == park.Id && a.IsActive)
                .ToListAsync(ct);
            var contexts = accounts.Select(a => new BankAccountContext(
                ParkId: park.Id,
                ParkBankAccountId: a.Id,
                Provider: a.Provider,
                BankCode: a.BankCode,
                SourceIban: a.Iban,
                SourceHolderName: a.HolderName ?? park.LegalEntityName ?? park.Name,
                CredentialsJson: a.CredentialsEncrypted)).ToList();
            await ReconcileParkAsync(db, yandex, bankResolver, park.Id, park.Name, contexts, windowFrom, windowTo, ct);
        }
        return parks.Count;
    }

    private async Task ReconcileParkAsync(
        AppDbContext db,
        IYandexFleetClient yandex,
        IServiceProvider sp,
        Guid parkId,
        string parkName,
        IReadOnlyList<BankAccountContext> accounts,
        DateTime windowFrom,
        DateTime windowTo,
        CancellationToken ct)
    {
        var run = new ReconciliationRun
        {
            ParkId = parkId,
            WindowFrom = windowFrom,
            WindowTo = windowTo,
            Status = ReconciliationStatus.Running,
        };
        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // ── Side A: our DB ────────────────────────────────────────
            // Window on the moment money moved (CompletedAt), falling back to creation for
            // anything not completed — the bank side is windowed on its execution time.
            var cashouts = await db.Cashouts
                .AsNoTracking()
                .Where(c => c.ParkId == parkId
                         && (c.CompletedAt ?? c.CreatedAt) >= windowFrom
                         && (c.CompletedAt ?? c.CreatedAt) < windowTo)
                .Select(c => new CashoutSlim(
                    c.Id, c.Status, c.Amount, c.Fee, c.BankTransferId,
                    c.YandexTransactionId, c.YandexReversalTransactionId, c.CreatedAt,
                    c.LastAttemptAt, c.UpdatedAt, c.Driver.YandexDriverProfileId))
                .ToListAsync(ct);

            // ── Side B: bank — one listing per park account (TBC, BoG, …) ──
            // Each debit remembers the account whose statement listed it (an outside payment is matched on it below).
            var bankTransfers = new List<BankTransferRecord>();
            var listedFrom = new List<(BankTransferRecord Record, Guid AccountId)>();
            var unreadable = new List<string>();
            foreach (var account in accounts)
            {
                // An account whose provider has no adapter on this server is not read at all — it used to be read from the
                // mock, which agreed with whatever the mock had "paid", so nothing was ever flagged. Unread, the Completed
                // cashouts paid through it come out below as missing_in_bank, and the run says which account it skipped.
                var bank = sp.GetKeyedService<IBankPayoutAdapter>(account.Provider)
                    ?? sp.GetKeyedService<IBankPayoutAdapter>(account.Provider.ToUpperInvariant());
                if (bank is null)
                {
                    unreadable.Add($"account {account.ParkBankAccountId} (provider '{account.Provider}')");
                    _log.LogError("Reconciliation of park {Park}: no bank adapter for provider '{Provider}' of account {Account} — its bank side is not read",
                        parkName, account.Provider, account.ParkBankAccountId);
                    continue;
                }
                var listed = await bank.ListTransfersAsync(account, windowFrom, windowTo, ct);
                bankTransfers.AddRange(listed);
                listedFrom.AddRange(listed.Select(r => (r, account.ParkBankAccountId)));
            }
            var bankById = bankTransfers
                .GroupBy(t => t.TransferId)
                .ToDictionary(g => g.Key, g => g.First());

            // ── Side C: Yandex ────────────────────────────────────────
            // Yandex API requires a per-driver query — ask for each driver that has
            // cashouts in the window. Faster than scanning every driver in the park.
            //
            // A payout can wait for days (Annex 3 §5) while its Yandex debit is posted at the REQUEST. So the
            // drivers are those of the cashouts loaded above (windowed on completion) as well as those who
            // asked inside the window, and each driver's transactions are fetched back to the request of
            // their oldest loaded cashout — otherwise a payout that waited over a weekend and completed
            // today would have its Friday debit outside the window and be reported missing in Yandex.
            var createdInWindow = await db.Cashouts.AsNoTracking()
                .Where(c => c.ParkId == parkId && c.CreatedAt >= windowFrom && c.CreatedAt < windowTo)
                .Select(c => c.Driver.YandexDriverProfileId)
                .Where(p => p != null)
                .Distinct()
                .ToListAsync(ct);
            var fetchFrom = createdInWindow.ToDictionary(p => p!, _ => windowFrom);
            foreach (var c in cashouts.Where(c => c.YandexDriverProfileId is not null))
            {
                // A few minutes of slack: the debit is posted right after the row is created.
                var from = c.CreatedAt < windowFrom ? c.CreatedAt.AddMinutes(-5) : windowFrom;
                var profile = c.YandexDriverProfileId!;
                fetchFrom[profile] = fetchFrom.TryGetValue(profile, out var earlier) && earlier < from ? earlier : from;
            }

            var yandexTxByRef = new Dictionary<string, YandexTransaction>();
            foreach (var (profileId, from) in fetchFrom)
            {
                var txs = await yandex.GetTransactionsAsync(parkId, profileId, from, windowTo, ct);
                foreach (var tx in txs.Where(t => t.Category == "partner_service_manual"))
                {
                    yandexTxByRef[tx.TransactionId] = tx;
                }
            }

            // ── Detect discrepancies ──────────────────────────────────
            var discrepancies = new List<ReconciliationDiscrepancy>();
            var seenBank = new HashSet<string>();
            var seenYandex = new HashSet<string>();

            foreach (var c in cashouts)
            {
                // Only Completed cashouts should have a bank counterpart. In-flight rows
                // (Processing / Queued) legitimately have a Yandex debit but no transfer yet;
                // Failed rows had their debit reversed — the debit+credit pair cancels out.
                if (c.Status != CashoutStatus.Completed)
                {
                    if (c.YandexTransactionId is not null) seenYandex.Add(c.YandexTransactionId);
                    if (c.YandexReversalTransactionId is not null) seenYandex.Add(c.YandexReversalTransactionId);

                    // Queued is not "stuck": it waits for the bank or a top-up for as long as that takes
                    // (Annex 3 §5) and the payout_waiting alert reports it. Only Processing can be stuck —
                    // measured from the attempt the bank accepted (or, with nothing at the bank, the latest
                    // claim of the row, e.g. a console cancel's reversal), never from the request: a payout
                    // that waited three days and was accepted a minute ago is not three days stuck.
                    var processingSince = c.BankTransferId is not null
                        ? c.LastAttemptAt ?? c.CreatedAt
                        : Max(c.LastAttemptAt ?? c.CreatedAt, c.UpdatedAt);
                    if (c.Status == CashoutStatus.Processing &&
                        (DateTime.UtcNow - processingSince) > TimeSpan.FromHours(StuckPendingHours))
                    {
                        discrepancies.Add(new ReconciliationDiscrepancy
                        {
                            RunId = run.Id,
                            ParkId = parkId,
                            CashoutId = c.Id,
                            Kind = "stuck_pending",
                            PaytaxiAmount = c.Amount,
                            Notes = $"Still {c.Status} after {StuckPendingHours}+ hours",
                        });
                    }
                    continue;
                }

                // Bank side
                if (string.IsNullOrEmpty(c.BankTransferId)
                    || !bankById.TryGetValue(c.BankTransferId, out var br))
                {
                    discrepancies.Add(new ReconciliationDiscrepancy
                    {
                        RunId = run.Id,
                        ParkId = parkId,
                        CashoutId = c.Id,
                        Kind = "missing_in_bank",
                        PaytaxiAmount = c.Amount - c.Fee, // net is what the bank should have moved
                        BankTransferId = c.BankTransferId,
                        Notes = "Cashout marked Completed but no matching bank transfer in window",
                    });
                }
                else
                {
                    seenBank.Add(c.BankTransferId!);
                    var expectedNet = c.Amount - c.Fee;
                    if (Math.Abs(br.Amount - expectedNet) > _opts.AmountToleranceGel)
                    {
                        discrepancies.Add(new ReconciliationDiscrepancy
                        {
                            RunId = run.Id,
                            ParkId = parkId,
                            CashoutId = c.Id,
                            Kind = "amount_mismatch_bank",
                            PaytaxiAmount = expectedNet,
                            ExternalAmount = br.Amount,
                            BankTransferId = c.BankTransferId,
                            Notes = $"PayTaxi expected ₾ {expectedNet:F2} net, bank reports ₾ {br.Amount:F2}",
                        });
                    }
                }

                // Yandex side
                if (string.IsNullOrEmpty(c.YandexTransactionId)
                    || !yandexTxByRef.TryGetValue(c.YandexTransactionId, out var yt))
                {
                    discrepancies.Add(new ReconciliationDiscrepancy
                    {
                        RunId = run.Id,
                        ParkId = parkId,
                        CashoutId = c.Id,
                        Kind = "missing_in_yandex",
                        PaytaxiAmount = c.Amount, // gross is what should be debited from Yandex balance
                        YandexTransactionId = c.YandexTransactionId,
                        Notes = "Cashout marked Completed but no matching Yandex debit in window",
                    });
                }
                else
                {
                    seenYandex.Add(c.YandexTransactionId!);
                    // Yandex debits are negative — compare absolute value.
                    var expectedGross = c.Amount;
                    if (Math.Abs(Math.Abs(yt.Amount) - expectedGross) > _opts.AmountToleranceGel)
                    {
                        discrepancies.Add(new ReconciliationDiscrepancy
                        {
                            RunId = run.Id,
                            ParkId = parkId,
                            CashoutId = c.Id,
                            Kind = "amount_mismatch_yandex",
                            PaytaxiAmount = expectedGross,
                            ExternalAmount = Math.Abs(yt.Amount),
                            YandexTransactionId = c.YandexTransactionId,
                            Notes = $"PayTaxi expected ₾ {expectedGross:F2}, Yandex shows ₾ {Math.Abs(yt.Amount):F2}",
                        });
                    }
                }
            }

            // A Yandex transaction of a cashout outside this window (asked inside it and completed after it,
            // or fetched only because the driver's oldest payout waited) belongs to that cashout — known, not
            // an orphan.
            var unmatchedYandex = yandexTxByRef.Keys.Where(k => !seenYandex.Contains(k)).ToList();
            if (unmatchedYandex.Count > 0)
            {
                var known = await db.Cashouts.AsNoTracking()
                    .Where(c => c.ParkId == parkId
                             && ((c.YandexTransactionId != null && unmatchedYandex.Contains(c.YandexTransactionId))
                              || (c.YandexReversalTransactionId != null && unmatchedYandex.Contains(c.YandexReversalTransactionId))))
                    .Select(c => new { c.YandexTransactionId, c.YandexReversalTransactionId })
                    .ToListAsync(ct);
                foreach (var k in known)
                {
                    if (k.YandexTransactionId is not null) seenYandex.Add(k.YandexTransactionId);
                    if (k.YandexReversalTransactionId is not null) seenYandex.Add(k.YandexReversalTransactionId);
                }
            }

            // Orphans: external rows we didn't match. The monthly park → Swich settlement
            // transfers leave the same account and are expected — never orphans (matched on the
            // order id the bank itself returned).
            var settlementTransferIds = await db.Settlements.AsNoTracking()
                .Where(s => s.ParkId == parkId && s.BankTransferId != null)
                .Select(s => s.BankTransferId!)
                .ToListAsync(ct);
            foreach (var id in settlementTransferIds) seenBank.Add(id);

            // So is a debt the park paid Swich by hand from its account, which Swich recorded as paid outside the
            // platform (§2.5) with that payment's bank reference. But the reference is free text a person typed: on its
            // own, a slip that equals the id of some other debit would hide a transfer the platform does not track.
            // A debit is that payment only if everything agrees — the same id, the same amount to the tetri, from the
            // account the debt was owed from (any of the park's accounts when the transfer never got as far as one),
            // and sent in the few days up to the day the money reached Swich.
            var paidOutside = await db.Settlements.AsNoTracking()
                .Where(s => s.ParkId == parkId && s.Status == SettlementStatus.SettledExternally
                         && s.ExternalReference != null && s.ExternalPaidOn != null)
                .Select(s => new { s.ExternalReference, s.SwichShare, s.ParkBankAccountId, s.ExternalPaidOn })
                .ToListAsync(ct);
            if (paidOutside.Count > 0)
            {
                var tz = SettlementService.ResolveTimeZone(sp.GetService<IOptions<SettlementOptions>>()?.Value.TimeZoneId);
                foreach (var e in paidOutside)
                {
                    var paidOn = e.ExternalPaidOn!.Value;
                    foreach (var (br, accountId) in listedFrom.Where(t => t.Record.TransferId == e.ExternalReference))
                    {
                        var sentOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(br.SentAt), tz));
                        if (br.Amount == e.SwichShare
                            && (e.ParkBankAccountId is null || e.ParkBankAccountId == accountId)
                            && sentOn >= paidOn.AddDays(-ExternalPaymentSentDaysBefore)
                            && sentOn <= paidOn.AddDays(ExternalPaymentSentDaysAfter))
                            seenBank.Add(br.TransferId);
                    }
                }
            }

            foreach (var br in bankTransfers.Where(t => !seenBank.Contains(t.TransferId)))
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    RunId = run.Id,
                    ParkId = parkId,
                    Kind = "orphaned_bank_send",
                    ExternalAmount = br.Amount,
                    BankTransferId = br.TransferId,
                    Notes = $"Bank transfer ₾ {br.Amount:F2} has no matching PayTaxi cashout",
                });
            }
            // Only debits inside the window: older ones, fetched for a payout that waited, were judged by earlier runs.
            foreach (var (txId, yt) in yandexTxByRef.Where(kv => !seenYandex.Contains(kv.Key) && kv.Value.Amount < 0
                                                              && kv.Value.CreatedAt >= windowFrom && kv.Value.CreatedAt <= windowTo))
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    RunId = run.Id,
                    ParkId = parkId,
                    Kind = "orphaned_yandex_debit",
                    ExternalAmount = Math.Abs(yt.Amount),
                    YandexTransactionId = txId,
                    Notes = $"Yandex manual-cashout debit ₾ {Math.Abs(yt.Amount):F2} has no matching PayTaxi cashout",
                });
            }

            // A mismatch that is still open from an earlier run is the SAME problem, not a new one.
            // Re-adding it every night would multiply the open count (and the console alert) without
            // adding information. Keep one open row per (kind, subject); refresh its timestamp so
            // "last seen" is honest, and only count genuinely new findings for this run.
            var openExisting = await db.ReconciliationDiscrepancies
                .Where(d => d.ParkId == parkId && !d.IsResolved)
                .ToListAsync(ct);
            string KeyOf(ReconciliationDiscrepancy d) =>
                d.Kind + "|" + (d.CashoutId?.ToString() ?? d.BankTransferId ?? d.YandexTransactionId ?? "");
            var openKeys = openExisting.ToDictionary(KeyOf, d => d);
            var fresh = new List<ReconciliationDiscrepancy>();
            foreach (var d in discrepancies)
            {
                if (openKeys.TryGetValue(KeyOf(d), out var existing))
                {
                    existing.UpdatedAt = DateTime.UtcNow;   // still there tonight
                    existing.RunId = run.Id;                // points at the latest run that saw it
                }
                else fresh.Add(d);
            }
            db.ReconciliationDiscrepancies.AddRange(fresh);

            run.CashoutsScanned = cashouts.Count;
            run.BankTransfersScanned = bankTransfers.Count;
            run.YandexTxScanned = yandexTxByRef.Count;
            run.DiscrepanciesFound = fresh.Count;
            run.Status = ReconciliationStatus.Completed;
            run.FinishedAt = DateTime.UtcNow;
            if (unreadable.Count > 0)
                run.Error = $"Bank side not read — no bank adapter on this server for {string.Join(", ", unreadable)}";

            await db.SaveChangesAsync(ct);

            _log.LogInformation(
                "Reconciliation: park={Park} cashouts={Cashouts} bank={Bank} yandex={Yandex} discrepancies={Discrepancies} (new; {StillOpen} still open from earlier runs)",
                parkName, cashouts.Count, bankTransfers.Count, yandexTxByRef.Count, fresh.Count, discrepancies.Count - fresh.Count);
        }
        catch (Exception ex)
        {
            run.Status = ReconciliationStatus.Failed;
            run.FinishedAt = DateTime.UtcNow;
            run.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            _log.LogError(ex, "Reconciliation failed for park {Park}", parkName);
        }
    }

    private record CashoutSlim(
        Guid Id, CashoutStatus Status, decimal Amount, decimal Fee,
        string? BankTransferId, string? YandexTransactionId, string? YandexReversalTransactionId,
        DateTime CreatedAt, DateTime? LastAttemptAt, DateTime UpdatedAt, string? YandexDriverProfileId);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),   // adapters report UTC
    };

    /// <summary>
    /// A debt paid by hand is matched to a debit sent at most this many days before the day Swich recorded the money
    /// as received (an interbank transfer over a long weekend) …
    /// </summary>
    private const int ExternalPaymentSentDaysBefore = 5;

    /// <summary>… or one day after it (a bank that stamps the execution on the next booking day).</summary>
    private const int ExternalPaymentSentDaysAfter = 1;
}

public class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 86_400; // daily by default
    public int InitialDelaySeconds { get; set; } = 30;

    /// <summary>Look this many hours back from <c>now − grace</c>.</summary>
    public int WindowHours { get; set; } = 24;

    /// <summary>How many minutes of "now" to skip — keeps in-flight cashouts out of the window.</summary>
    public int GracePeriodMinutes { get; set; } = 5;

    /// <summary>Amounts within this GEL tolerance count as a match.</summary>
    public decimal AmountToleranceGel { get; set; } = 0.01m;
}
