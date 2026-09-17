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
///   3. Cashouts in <c>Processing</c> or <c>Queued</c> for longer than
///      <see cref="StuckPendingHours"/> → stuck pending.
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

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var yandex = scope.ServiceProvider.GetRequiredService<IYandexFleetClient>();
        var bankResolver = scope.ServiceProvider;

        var now = DateTime.UtcNow;
        var windowTo = now.AddMinutes(-_opts.GracePeriodMinutes);
        var windowFrom = windowTo.AddHours(-_opts.WindowHours);

        var parks = await db.Parks
            .AsNoTracking()
            .Where(p => p.Status == ParkStatus.Active)
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
            var cashouts = await db.Cashouts
                .AsNoTracking()
                .Where(c => c.ParkId == parkId
                         && c.CreatedAt >= windowFrom
                         && c.CreatedAt < windowTo)
                .Select(c => new CashoutSlim(
                    c.Id, c.Status, c.Amount, c.Fee, c.BankTransferId,
                    c.YandexTransactionId, c.YandexReversalTransactionId, c.CreatedAt))
                .ToListAsync(ct);

            // ── Side B: bank — one listing per park account (TBC, BoG, …) ──
            var bankTransfers = new List<BankTransferRecord>();
            foreach (var account in accounts)
            {
                var bank = sp.GetKeyedService<IBankPayoutAdapter>(account.Provider)
                    ?? sp.GetKeyedService<IBankPayoutAdapter>(account.Provider.ToUpperInvariant())
                    ?? sp.GetKeyedService<IBankPayoutAdapter>("MOCK")
                    ?? throw new InvalidOperationException($"No bank adapter for provider '{account.Provider}'");
                bankTransfers.AddRange(await bank.ListTransfersAsync(account, windowFrom, windowTo, ct));
            }
            var bankById = bankTransfers
                .GroupBy(t => t.TransferId)
                .ToDictionary(g => g.Key, g => g.First());

            // ── Side C: Yandex ────────────────────────────────────────
            // Yandex API requires a per-driver query — ask for each driver that has
            // cashouts in the window. Faster than scanning every driver in the park.
            var driversInWindow = await db.Cashouts.AsNoTracking()
                .Where(c => c.ParkId == parkId && c.CreatedAt >= windowFrom && c.CreatedAt < windowTo)
                .Select(c => c.Driver.YandexDriverProfileId)
                .Where(p => p != null)
                .Distinct()
                .ToListAsync(ct);

            var yandexTxByRef = new Dictionary<string, YandexTransaction>();
            foreach (var profileId in driversInWindow)
            {
                var txs = await yandex.GetTransactionsAsync(parkId, profileId!, windowFrom, windowTo, ct);
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

                    if ((c.Status == CashoutStatus.Processing || c.Status == CashoutStatus.Queued) &&
                        (DateTime.UtcNow - c.CreatedAt) > TimeSpan.FromHours(StuckPendingHours))
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

            // Orphans: external rows we didn't match
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
            foreach (var (txId, yt) in yandexTxByRef.Where(kv => !seenYandex.Contains(kv.Key) && kv.Value.Amount < 0))
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

            db.ReconciliationDiscrepancies.AddRange(discrepancies);

            run.CashoutsScanned = cashouts.Count;
            run.BankTransfersScanned = bankTransfers.Count;
            run.YandexTxScanned = yandexTxByRef.Count;
            run.DiscrepanciesFound = discrepancies.Count;
            run.Status = ReconciliationStatus.Completed;
            run.FinishedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            _log.LogInformation(
                "Reconciliation: park={Park} cashouts={Cashouts} bank={Bank} yandex={Yandex} discrepancies={Discrepancies}",
                parkName, cashouts.Count, bankTransfers.Count, yandexTxByRef.Count, discrepancies.Count);
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
        DateTime CreatedAt);
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
