using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Adapters.Banks.Tbc;

/// <summary>
/// TBC Integration Service (DBI) as a PayTaxi payout rail — the launch bank.
///
/// Semantics mapping (see PAYTAXI-CONTEXT.md §3/§6):
///   • SendPayout → ImportSinglePaymentOrders (one order, position 1). TBC→TBC uses
///     TransferWithinBankPaymentOrderIo; other Georgian banks use
///     TransferToOtherBankNationalCurrencyPaymentOrderIo. Our idempotency key becomes the
///     bank's singlePaymentRequestId, so a repeated send is rejected by the bank as
///     DUPLICATED_SINGLE_PAYMENT_REQUEST and we recover the original paymentId instead.
///   • Execution is asynchronous: after import we poll GetPaymentOrderStatus a few times.
///     F → Completed; FL/C/D/CPE → failed (non-retryable); anything else → Pending, and the
///     saga keeps polling via GetTransferStatusAsync.
///   • FindTransferByIdempotencyKey → GetSinglePaymentId + status (lost-response recovery).
///   • ListTransfers → GetAccountMovements (debits) for reconciliation, keyed by paymentId.
///   • GetBalance → GetAccountStatement(today).closingBalance.
///
/// Status codes (docs "Payment Order Status"): I initial · DR draft · G registered ·
/// D deleted · WC awaiting certification · CERT/VERIF/WS processing · CPE error ·
/// F finished · FL failed · C cancelled.
/// </summary>
public class TbcPayoutAdapter : IBankPayoutAdapter
{
    private readonly TbcSoapClient _soap;
    private readonly TbcDbiOptions _opts;
    private readonly ILogger<TbcPayoutAdapter> _log;

    public string BankType => "TBC";

    public TbcPayoutAdapter(TbcSoapClient soap, IOptions<TbcDbiOptions> opts, ILogger<TbcPayoutAdapter> log)
    {
        _soap = soap;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<BankTransferResult> SendPayoutAsync(BankTransferRequest request, CancellationToken ct = default)
    {
        TbcCredentials creds;
        try { creds = TbcCredentials.Parse(request.Source.CredentialsJson, request.Source.ParkId); }
        catch (TbcConfigurationException ex)
        {
            // Ops problem: keep the cashout queued rather than reversing the driver's balance.
            return new BankTransferResult(false, null, "TBC_NOT_CONFIGURED", ex.Message, IsRetryable: true);
        }

        var requestId = TbcSoapClient.RequestIdFromKey(request.IdempotencyKey);
        var withinTbc = request.DestinationIban.Length >= 6 &&
                        request.DestinationIban.Substring(4, 2).Equals("TB", StringComparison.OrdinalIgnoreCase);

        long paymentId;
        try
        {
            paymentId = await _soap.ImportSinglePaymentAsync(creds, new TbcSoapClient.ImportPaymentOrder(
                SinglePaymentRequestId: requestId,
                DebitIban: request.Source.SourceIban,
                DebitCurrency: creds.DebitCurrency,
                CreditIban: request.DestinationIban,
                Amount: request.Amount,
                Currency: request.Currency,
                Description: request.Reference,
                BeneficiaryName: request.DestinationName ?? "-",
                BeneficiaryTaxCode: request.DestinationTaxCode,
                WithinTbc: withinTbc), ct);
        }
        catch (TbcFaultException ex) when (ex.FaultCode == "DUPLICATED_SINGLE_PAYMENT_REQUEST")
        {
            // We already imported this one (lost response earlier). Recover the bank's id.
            var existing = await _soap.GetSinglePaymentIdAsync(creds, requestId, ct);
            if (existing is null)
                return new BankTransferResult(false, null, "TBC_DUPLICATE_UNRESOLVED",
                    "Bank reports a duplicate request but cannot return its payment id", IsRetryable: true);
            paymentId = existing.Value;
            _log.LogInformation("TBC: request {Req} already imported as payment {Pid}; continuing with status", requestId, paymentId);
        }
        catch (TbcFaultException ex)
        {
            return MapFault(ex);
        }
        catch (TbcProtocolException ex)
        {
            // Outcome unknown → the caller will FindTransferByIdempotencyKey before retrying.
            throw new InvalidOperationException($"TBC import outcome unknown: {ex.Message}", ex);
        }

        // Poll briefly; TBC executes imported orders asynchronously.
        var status = await PollStatusAsync(creds, paymentId, ct);
        return ToResult(paymentId, status);
    }

    public async Task<BankTransferStatus> GetTransferStatusAsync(BankAccountContext source, string transferId, CancellationToken ct = default)
    {
        var creds = TbcCredentials.Parse(source.CredentialsJson, source.ParkId);
        if (!long.TryParse(transferId, out var pid)) return BankTransferStatus.Unknown;
        var s = await _soap.GetPaymentOrderStatusAsync(creds, pid, ct);
        return MapStatus(s.Code);
    }

    public async Task<BankTransferLookup?> FindTransferByIdempotencyKeyAsync(BankAccountContext source, string idempotencyKey, CancellationToken ct = default)
    {
        var creds = TbcCredentials.Parse(source.CredentialsJson, source.ParkId);
        var requestId = TbcSoapClient.RequestIdFromKey(idempotencyKey);
        var pid = await _soap.GetSinglePaymentIdAsync(creds, requestId, ct);
        if (pid is null) return null;
        var s = await _soap.GetPaymentOrderStatusAsync(creds, pid.Value, ct);
        return new BankTransferLookup(pid.Value.ToString(), MapStatus(s.Code), 0m);
    }

    public async Task<IReadOnlyList<BankTransferRecord>> ListTransfersAsync(BankAccountContext source, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var creds = TbcCredentials.Parse(source.CredentialsJson, source.ParkId);
        var movements = await _soap.GetAccountMovementsAsync(creds, source.SourceIban, creds.DebitCurrency, from, to, ct);
        return movements
            .Where(m => m.IsDebit)
            .Select(m => new BankTransferRecord(
                TransferId: m.PaymentId ?? m.ExternalPaymentId,
                ParkId: source.ParkId,
                Amount: m.Amount,
                Currency: m.Currency,
                Status: BankTransferStatus.Completed,
                SentAt: m.ValueDate))
            .ToList();
    }

    public async Task<decimal?> GetBalanceAsync(BankAccountContext source, CancellationToken ct = default)
    {
        var creds = TbcCredentials.Parse(source.CredentialsJson, source.ParkId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)); // Georgia local date
        var st = await _soap.GetAccountStatementAsync(creds, source.SourceIban, creds.DebitCurrency, today, today, ct);
        return st.ClosingBalance;
    }

    // ── Mapping ──────────────────────────────────────────────────────

    private async Task<TbcSoapClient.PaymentStatus> PollStatusAsync(TbcCredentials creds, long paymentId, CancellationToken ct)
    {
        TbcSoapClient.PaymentStatus last = new("I", paymentId.ToString(), null);
        for (var i = 0; i < Math.Max(1, _opts.StatusPollAttempts); i++)
        {
            if (i > 0) await Task.Delay(_opts.StatusPollDelayMs, ct);
            try { last = await _soap.GetPaymentOrderStatusAsync(creds, paymentId, ct); }
            catch (Exception ex) when (ex is TbcFaultException or TbcProtocolException)
            {
                _log.LogWarning(ex, "TBC status poll {N} failed for payment {Pid}", i + 1, paymentId);
                continue;
            }
            if (MapStatus(last.Code) != BankTransferStatus.Pending) break;
        }
        return last;
    }

    private static BankTransferResult ToResult(long paymentId, TbcSoapClient.PaymentStatus s)
    {
        var id = paymentId.ToString();
        return MapStatus(s.Code) switch
        {
            BankTransferStatus.Completed => new BankTransferResult(true, id, null, null),
            BankTransferStatus.Failed => new BankTransferResult(false, id, "TBC_" + s.Code,
                s.ErrorDetail ?? $"Bank rejected the payment (status {s.Code})", IsRetryable: false),
            _ => new BankTransferResult(true, id, null, null, IsRetryable: false, IsPending: true),
        };
    }

    public static BankTransferStatus MapStatus(string code) => code.Trim().ToUpperInvariant() switch
    {
        "F" => BankTransferStatus.Completed,
        "FL" or "C" or "D" or "CPE" => BankTransferStatus.Failed,
        "I" or "DR" or "G" or "WC" or "CERT" or "VERIF" or "WS" => BankTransferStatus.Pending,
        _ => BankTransferStatus.Unknown,
    };

    private static BankTransferResult MapFault(TbcFaultException ex) => ex.FaultCode switch
    {
        // Bad data: never going to work with the same input.
        "VALIDATION_ERROR" or "INCORRECT_INPUT_DATA" =>
            new BankTransferResult(false, null, "TBC_" + ex.FaultCode, ex.Message, IsRetryable: false),
        // Ops must act (password expired / user blocked) — hold the payout, don't reverse the driver.
        "CREDENTIALS_MUST_BE_CHANGED" or "USER_BLOCKED" or "AUTHENTICATION_FAILED" =>
            new BankTransferResult(false, null, "TBC_" + ex.FaultCode, ex.Message, IsRetryable: true),
        _ => new BankTransferResult(false, null, "TBC_" + ex.FaultCode, ex.Message, IsRetryable: true),
    };
}
