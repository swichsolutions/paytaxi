using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using PayTaxi.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PayTaxi.Infrastructure.Adapters.Banks.Tbc;

/// <summary>
/// Thin SOAP 1.1 client for TBC's Integration Service (DBI). Built straight from the
/// bank's WSDL/XSD (DBI_WSDL&amp;XSD 1.14): document/literal, namespace
/// <c>http://www.mygemini.com/schemas/mygemini</c>, WS-Security UsernameToken header,
/// SOAPAction <c>{ns}/{Operation}</c>. Transport is HTTPS with the park's client
/// certificate (mutual TLS) for the Standard+ package.
///
/// Operations used: ImportSinglePaymentOrders, GetPaymentOrderStatus, GetSinglePaymentId,
/// GetAccountMovements, GetAccountStatement, ChangePassword.
///
/// This class knows XML and HTTP only. Mapping to PayTaxi's payout semantics lives in
/// <see cref="TbcPayoutAdapter"/>.
/// </summary>
public class TbcSoapClient
{
    public const string Ns = "http://www.mygemini.com/schemas/mygemini";
    private static readonly XNamespace Myg = Ns;
    private static readonly XNamespace SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private readonly TbcDbiOptions _opts;
    private readonly ILogger _log;
    private readonly Func<HttpMessageHandler>? _handlerFactory; // tests inject a fake transport
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    private readonly IApiAuditLogger? _audit;

    public TbcSoapClient(IOptions<TbcDbiOptions> opts, ILogger<TbcSoapClient> log, IApiAuditLogger? audit = null)
        : this(opts.Value, log, null, audit) { }

    /// <summary>Test seam: supply a handler factory to bypass the network.</summary>
    public TbcSoapClient(TbcDbiOptions opts, ILogger log, Func<HttpMessageHandler>? handlerFactory, IApiAuditLogger? audit = null)
    {
        _opts = opts;
        _log = log;
        _handlerFactory = handlerFactory;
        _audit = audit;
    }

    /// <summary>Every bank call is audit-logged (Yandex rule 3.7 style; CLAUDE.md "audit everything money-related").</summary>
    private void Audit(TbcCredentials creds, string operation, int responseCode, bool success, long ms, string? correlationId)
    {
        if (_audit is null) return;
        _ = _audit.LogAsync(new ApiCallRecord(
            ApiProvider: "TBC",
            Endpoint: operation,
            Method: "SOAP",
            ParkId: creds.ParkId,
            ActorId: null,
            ActorType: "System",
            RequestParamsHash: null,
            ResponseCode: responseCode,
            Success: success,
            DurationMs: ms,
            CorrelationId: correlationId), CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Operations
    // ═══════════════════════════════════════════════════════════════════

    public record ImportPaymentOrder(
        long SinglePaymentRequestId,
        string DebitIban,
        string DebitCurrency,
        string CreditIban,
        decimal Amount,
        string Currency,
        string Description,
        string BeneficiaryName,
        string? BeneficiaryTaxCode,
        bool WithinTbc);

    /// <summary>ImportSinglePaymentOrders with exactly one order. Returns the bank's paymentId.</summary>
    public async Task<long> ImportSinglePaymentAsync(TbcCredentials creds, ImportPaymentOrder order, CancellationToken ct)
    {
        var type = order.WithinTbc ? "TransferWithinBankPaymentOrderIo" : "TransferToOtherBankNationalCurrencyPaymentOrderIo";

        var po = new XElement(Myg + "singlePaymentOrder",
            new XAttribute(Xsi + "type", "myg:" + type),
            new XElement(Myg + "singlePaymentRequestId", order.SinglePaymentRequestId),
            new XElement(Myg + "creditAccount",
                new XElement(Myg + "accountNumber", order.CreditIban)),
            new XElement(Myg + "debitAccount",
                new XElement(Myg + "accountNumber", order.DebitIban),
                new XElement(Myg + "accountCurrencyCode", order.DebitCurrency)),
            new XElement(Myg + "amount",
                new XElement(Myg + "amount", order.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                new XElement(Myg + "currency", order.Currency)),
            new XElement(Myg + "position", 1),
            new XElement(Myg + "description", Sanitize(order.Description, 150)),
            new XElement(Myg + "beneficiaryName", Sanitize(order.BeneficiaryName, 70)));
        if (!string.IsNullOrWhiteSpace(order.BeneficiaryTaxCode))
            po.Add(new XElement(Myg + "beneficiaryTaxCode", order.BeneficiaryTaxCode.Trim()));

        var body = new XElement(Myg + "ImportSinglePaymentOrdersRequestIo", po);
        var resp = await CallAsync(creds, "ImportSinglePaymentOrders", body, includeNonce: true, ct);

        var paymentId = resp.Descendants(Myg + "PaymentOrdersResults")
            .Select(r => (string?)r.Element(Myg + "paymentId"))
            .FirstOrDefault();
        if (!long.TryParse(paymentId, out var id))
            throw new TbcProtocolException("ImportSinglePaymentOrders response had no paymentId");
        return id;
    }

    public record PaymentStatus(string Code, string? PaymentId, string? ErrorDetail);

    public async Task<PaymentStatus> GetPaymentOrderStatusAsync(TbcCredentials creds, long singlePaymentId, CancellationToken ct)
    {
        var body = new XElement(Myg + "GetPaymentOrderStatusRequestIo",
            new XElement(Myg + "singlePaymentId", singlePaymentId));
        var resp = await CallAsync(creds, "GetPaymentOrderStatus", body, includeNonce: false, ct);

        var status = (string?)resp.Descendants(Myg + "status").FirstOrDefault()
            ?? throw new TbcProtocolException("GetPaymentOrderStatus response had no status");
        var data = resp.Descendants(Myg + "singlePaymentData").FirstOrDefault();
        return new PaymentStatus(
            status.Trim(),
            (string?)data?.Element(Myg + "paymentId"),
            (string?)data?.Element(Myg + "errorDetailEN") ?? (string?)data?.Element(Myg + "errorDetailGE"));
    }

    /// <summary>Recover the bank's paymentId for a request id we sent earlier. Null when the bank never saw it.</summary>
    public async Task<long?> GetSinglePaymentIdAsync(TbcCredentials creds, long singlePaymentRequestId, CancellationToken ct)
    {
        var body = new XElement(Myg + "GetSinglePaymentIdRequestIo",
            new XElement(Myg + "singlePaymentRequestId", singlePaymentRequestId));
        try
        {
            var resp = await CallAsync(creds, "GetSinglePaymentId", body, includeNonce: false, ct);
            var id = (string?)resp.Descendants(Myg + "paymentId").FirstOrDefault();
            return long.TryParse(id, out var v) ? v : null;
        }
        catch (TbcFaultException ex) when (ex.FaultCode == "SINGLE_PAYMENT_REQUEST_NOT_FOUND")
        {
            return null;
        }
    }

    public record Movement(
        string MovementId, string? PaymentId, string ExternalPaymentId, bool IsDebit,
        DateTime ValueDate, decimal Amount, string Currency, string? Description,
        string? PartnerAccountNumber, string? PartnerName, string? DocumentNumber);

    /// <summary>All movements on the account in [from, to], following the pager.</summary>
    public async Task<IReadOnlyList<Movement>> GetAccountMovementsAsync(
        TbcCredentials creds, string iban, string currency, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var all = new List<Movement>();
        for (var page = 0; page < 50; page++)
        {
            var filter = new XElement(Myg + "accountMovementFilterIo",
                new XElement(Myg + "pager",
                    new XElement(Myg + "pageIndex", page),
                    new XElement(Myg + "pageSize", _opts.MovementsPageSize)),
                new XElement(Myg + "accountNumber", iban),
                new XElement(Myg + "accountCurrencyCode", currency),
                new XElement(Myg + "periodFrom", FormatDateTime(fromUtc)),
                new XElement(Myg + "periodTo", FormatDateTime(toUtc)));
            var body = new XElement(Myg + "GetAccountMovementsRequestIo", filter);
            var resp = await CallAsync(creds, "GetAccountMovements", body, includeNonce: false, ct);

            var rows = resp.Descendants(Myg + "accountMovement").Select(m => new Movement(
                MovementId: (string?)m.Element(Myg + "movementId") ?? "",
                PaymentId: (string?)m.Element(Myg + "paymentId"),
                ExternalPaymentId: (string?)m.Element(Myg + "externalPaymentId") ?? "",
                IsDebit: (string?)m.Element(Myg + "debitCredit") == "0",
                ValueDate: ParseDateTime((string?)m.Element(Myg + "valueDate")),
                Amount: ParseDecimal((string?)m.Element(Myg + "amount")?.Element(Myg + "amount")),
                Currency: (string?)m.Element(Myg + "amount")?.Element(Myg + "currency") ?? currency,
                Description: (string?)m.Element(Myg + "description"),
                PartnerAccountNumber: (string?)m.Element(Myg + "partnerAccountNumber"),
                PartnerName: (string?)m.Element(Myg + "partnerName"),
                DocumentNumber: (string?)m.Element(Myg + "documentNumber"))).ToList();
            all.AddRange(rows);

            var total = int.TryParse((string?)resp.Descendants(Myg + "totalCount").FirstOrDefault(), out var t) ? t : rows.Count;
            if (rows.Count == 0 || all.Count >= total) break;
        }
        return all;
    }

    public record Statement(DateOnly OpeningDate, decimal OpeningBalance, DateOnly ClosingDate, decimal ClosingBalance, decimal CreditSum, decimal DebitSum, string Currency);

    public async Task<Statement> GetAccountStatementAsync(TbcCredentials creds, string iban, string currency, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var body = new XElement(Myg + "GetAccountStatementRequestIo",
            new XElement(Myg + "filter",
                new XElement(Myg + "periodFrom", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement(Myg + "periodTo", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement(Myg + "accountNumber", iban),
                new XElement(Myg + "currency", currency)));
        var resp = await CallAsync(creds, "GetAccountStatement", body, includeNonce: false, ct);
        var s = resp.Descendants(Myg + "statement").FirstOrDefault()
            ?? throw new TbcProtocolException("GetAccountStatement response had no statement");
        return new Statement(
            ParseDate((string?)s.Element(Myg + "openingDate")),
            ParseDecimal((string?)s.Element(Myg + "openingBalance")),
            ParseDate((string?)s.Element(Myg + "closingDate")),
            ParseDecimal((string?)s.Element(Myg + "closingBalance")),
            ParseDecimal((string?)s.Element(Myg + "creditSum")),
            ParseDecimal((string?)s.Element(Myg + "debitSum")),
            (string?)s.Element(Myg + "currency") ?? currency);
    }

    /// <summary>ChangePassword — required once for the temporary password and on expiry. Needs a Digipass nonce.</summary>
    public async Task<string> ChangePasswordAsync(TbcCredentials creds, string newPassword, CancellationToken ct)
    {
        var body = new XElement(Myg + "ChangePasswordRequestIo", new XElement(Myg + "newPassword", newPassword));
        var resp = await CallAsync(creds, "ChangePassword", body, includeNonce: true, ct);
        return (string?)resp.Descendants(Myg + "message").FirstOrDefault() ?? "ok";
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Envelope / transport
    // ═══════════════════════════════════════════════════════════════════

    public static XDocument BuildEnvelope(TbcCredentials creds, XElement body, bool includeNonce)
    {
        var token = new XElement(Wsse + "UsernameToken",
            new XElement(Wsse + "Username", creds.Username),
            new XElement(Wsse + "Password", creds.Password));
        // Nonce = Digipass one-time code. Required for ChangePassword and, on the Standard
        // (no-certificate) package, for payment import. The certificate package omits it.
        if (includeNonce && !string.IsNullOrWhiteSpace(creds.Nonce))
            token.Add(new XElement(Wsse + "Nonce", creds.Nonce));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(SoapEnv + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", SoapEnv),
                new XAttribute(XNamespace.Xmlns + "myg", Myg),
                new XAttribute(XNamespace.Xmlns + "wsse", Wsse),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XElement(SoapEnv + "Header", new XElement(Wsse + "Security", token)),
                new XElement(SoapEnv + "Body", body)));
    }

    private async Task<XElement> CallAsync(TbcCredentials creds, string operation, XElement body, bool includeNonce, CancellationToken ct)
    {
        var envelope = BuildEnvelope(creds, body, includeNonce);
        var endpoint = creds.IsProduction ? _opts.ProductionEndpoint : _opts.TestEndpoint;
        var client = GetClient(creds);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
        };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{Ns}/{operation}\"");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage res;
        string text;
        try
        {
            res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            text = await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            Audit(creds, operation, 0, false, sw.ElapsedMilliseconds, ex.GetType().Name);
            throw;
        }
        sw.Stop();
        using var _ = res;

        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (Exception ex)
        {
            Audit(creds, operation, (int)res.StatusCode, false, sw.ElapsedMilliseconds, "non-xml");
            _log.LogError("TBC {Op} → HTTP {Status} with non-XML body ({Len} chars)", operation, (int)res.StatusCode, text.Length);
            throw new TbcProtocolException($"TBC returned HTTP {(int)res.StatusCode} with a non-XML body: {ex.Message}");
        }

        var fault = doc.Descendants(SoapEnv + "Fault").FirstOrDefault();
        if (fault is not null)
        {
            var code = (string?)fault.Element("faultcode") ?? (string?)fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultcode") ?? "FAULT";
            var msg = (string?)fault.Element("faultstring") ?? (string?)fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring") ?? "";
            // Fault codes may arrive prefixed (e.g. "soap:Server" / "ns2:VALIDATION_ERROR").
            code = code.Contains(':') ? code[(code.LastIndexOf(':') + 1)..] : code;
            _log.LogWarning("TBC {Op} → SOAP fault {Code}: {Msg} ({Ms} ms)", operation, code, msg, sw.ElapsedMilliseconds);
            Audit(creds, operation, (int)res.StatusCode, false, sw.ElapsedMilliseconds, code);
            throw new TbcFaultException(code, msg);
        }

        if (!res.IsSuccessStatusCode)
        {
            Audit(creds, operation, (int)res.StatusCode, false, sw.ElapsedMilliseconds, "http");
            _log.LogError("TBC {Op} → HTTP {Status} without SOAP fault", operation, (int)res.StatusCode);
            throw new TbcProtocolException($"TBC returned HTTP {(int)res.StatusCode}");
        }

        Audit(creds, operation, (int)res.StatusCode, true, sw.ElapsedMilliseconds, null);
        _log.LogInformation("TBC {Op} ok ({Ms} ms)", operation, sw.ElapsedMilliseconds);
        return doc.Root!;
    }

    private HttpClient GetClient(TbcCredentials creds)
    {
        return _clients.GetOrAdd(creds.CacheKey(), _ =>
        {
            HttpMessageHandler handler;
            if (_handlerFactory is not null)
            {
                handler = _handlerFactory();
            }
            else
            {
                var sockets = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    AutomaticDecompression = DecompressionMethods.All,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        // TBC's DBI server requires TLS 1.2 (docs: Password Change page).
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    },
                };
                var cert = creds.LoadCertificate();
                if (cert is not null)
                {
                    sockets.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
                    sockets.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => cert;
                }
                if (!creds.IsProduction && _opts.TrustTestServerCertificate)
                    sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                handler = sockets;
            }

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, _opts.TimeoutSeconds)),
            };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
            return client;
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Map our string idempotency key onto the bank's <c>singlePaymentRequestId</c> (xsd:long).
    /// Deterministic 18-digit number from SHA-256, so a retry of the same key reproduces the
    /// same request id and the bank's duplicate check (DUPLICATED_SINGLE_PAYMENT_REQUEST) protects us.
    /// </summary>
    public static long RequestIdFromKey(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var n = BitConverter.ToUInt64(hash, 0) % 1_000_000_000_000_000_000UL; // < 10^18
        return n == 0 ? 1 : (long)n;
    }

    /// <summary>TBC accepts Georgian/Latin letters, digits and basic punctuation; trim to the field limit.</summary>
    public static string Sanitize(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "-";
        var cleaned = new string(s.Where(c => !char.IsControl(c) && c != '<' && c != '&' && c != '>').ToArray()).Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }

    private static string FormatDateTime(DateTime utc) =>
        // Request format per docs: yyyy-MM-dd'T'HH:mm:ss.SSS (bank-local, Georgia = UTC+4).
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tbilisi)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static readonly TimeZoneInfo Tbilisi = ResolveTbilisi();
    private static TimeZoneInfo ResolveTbilisi()
    {
        foreach (var id in new[] { "Georgian Standard Time", "Asia/Tbilisi" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Tbilisi", TimeSpan.FromHours(4), "Tbilisi", "Tbilisi");
    }

    private static DateTime ParseDateTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : DateTime.MinValue;

    private static DateOnly ParseDate(string? s) =>
        DateOnly.TryParse(s?.Length >= 10 ? s[..10] : s, CultureInfo.InvariantCulture, out var d) ? d : default;

    private static decimal ParseDecimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}

/// <summary>A SOAP fault from the bank, with TBC's fault code (e.g. VALIDATION_ERROR).</summary>
public sealed class TbcFaultException : Exception
{
    public string FaultCode { get; }
    public TbcFaultException(string code, string message) : base($"{code}: {message}") { FaultCode = code; }
}

/// <summary>Transport or shape problem — the bank answered with something we can't interpret.</summary>
public sealed class TbcProtocolException : Exception
{
    public TbcProtocolException(string message) : base(message) { }
}
