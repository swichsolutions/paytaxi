using System.Net;
using System.Text;
using System.Xml.Linq;

namespace PayTaxi.Tests.Tbc;

/// <summary>
/// Scripted TBC DBI server: inspects the SOAPAction (or the body's root element), records
/// every request, and answers with canned envelopes taken from TBC's public documentation.
/// </summary>
public sealed class FakeTbcHandler : HttpMessageHandler
{
    public const string Ns = "http://www.mygemini.com/schemas/mygemini";

    public List<(string Operation, XDocument Request)> Requests { get; } = new();
    private readonly Dictionary<string, Queue<Func<XDocument, (HttpStatusCode, string)>>> _scripts = new();

    /// <summary>Queue a response for an operation. Multiple calls = successive responses.</summary>
    public FakeTbcHandler On(string operation, Func<XDocument, (HttpStatusCode, string)> responder)
    {
        if (!_scripts.TryGetValue(operation, out var q)) _scripts[operation] = q = new();
        q.Enqueue(responder);
        return this;
    }

    public FakeTbcHandler On(string operation, string bodyXml, HttpStatusCode status = HttpStatusCode.OK) =>
        On(operation, _ => (status, Envelope(bodyXml)));

    public FakeTbcHandler Fault(string operation, string faultCode, string faultString) =>
        On(operation, _ => (HttpStatusCode.InternalServerError, FaultEnvelope(faultCode, faultString)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var text = await request.Content!.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(text);
        var action = request.Headers.TryGetValues("SOAPAction", out var v) ? v.First().Trim('"') : "";
        var op = action.Contains('/') ? action[(action.LastIndexOf('/') + 1)..] : action;
        Requests.Add((op, doc));

        if (!_scripts.TryGetValue(op, out var q) || q.Count == 0)
            throw new InvalidOperationException($"No scripted response for operation '{op}'");

        var (status, body) = q.Count > 1 ? q.Dequeue()(doc) : q.Peek()(doc); // last response repeats
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
    }

    public static string Envelope(string bodyXml) =>
        $"""
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ns2="{Ns}">
          <SOAP-ENV:Header/>
          <SOAP-ENV:Body>{bodyXml}</SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    public static string FaultEnvelope(string code, string message) =>
        $"""
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://schemas.xmlsoap.org/soap/envelope/">
          <SOAP-ENV:Body>
            <SOAP-ENV:Fault>
              <faultcode>{code}</faultcode>
              <faultstring>{message}</faultstring>
            </SOAP-ENV:Fault>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    // ── Canned bodies (shapes from developers.tbcbank.ge) ────────────

    public static string ImportResponse(long paymentId) =>
        $"""<ns2:ImportSinglePaymentOrdersResponseIo><ns2:PaymentOrdersResults><ns2:position>1</ns2:position><ns2:paymentId>{paymentId}</ns2:paymentId></ns2:PaymentOrdersResults></ns2:ImportSinglePaymentOrdersResponseIo>""";

    public static string StatusResponse(string status, string? errorEn = null, string? paymentId = null) =>
        errorEn is null
            ? $"""<ns2:GetPaymentOrderStatusResponseIo><ns2:status>{status}</ns2:status></ns2:GetPaymentOrderStatusResponseIo>"""
            : $"""<ns2:GetPaymentOrderStatusResponseIo><ns2:status>{status}</ns2:status><ns2:singlePaymentData><ns2:paymentId>{paymentId}</ns2:paymentId><ns2:paymentStatus>{status}</ns2:paymentStatus><ns2:errorDetailEN>{errorEn}</ns2:errorDetailEN></ns2:singlePaymentData></ns2:GetPaymentOrderStatusResponseIo>""";

    public static string PaymentIdResponse(long paymentId) =>
        $"""<ns2:GetSinglePaymentIdResponseIo><ns2:paymentId>{paymentId}</ns2:paymentId></ns2:GetSinglePaymentIdResponseIo>""";

    public static string StatementResponse(decimal closing) =>
        $"""<ns2:GetAccountStatementResponseIo><ns2:statement><ns2:openingDate>2026-09-17</ns2:openingDate><ns2:openingBalance>25000.00</ns2:openingBalance><ns2:closingDate>2026-09-17</ns2:closingDate><ns2:closingBalance>{closing.ToString(System.Globalization.CultureInfo.InvariantCulture)}</ns2:closingBalance><ns2:creditSum>0</ns2:creditSum><ns2:debitSum>49.50</ns2:debitSum><ns2:currency>GEL</ns2:currency></ns2:statement></ns2:GetAccountStatementResponseIo>""";

    public static string MovementsResponse(params (string paymentId, decimal amount, int debitCredit)[] rows)
    {
        var sb = new StringBuilder();
        sb.Append($"""<ns2:GetAccountMovementsResponseIo><ns2:result><ns2:pager><ns2:pageIndex>0</ns2:pageIndex><ns2:pageSize>500</ns2:pageSize></ns2:pager><ns2:totalCount>{rows.Length}</ns2:totalCount></ns2:result>""");
        var i = 0;
        foreach (var (pid, amt, dc) in rows)
        {
            i++;
            sb.Append($"""<ns2:accountMovement><ns2:movementId>mv{i}</ns2:movementId><ns2:paymentId>{pid}</ns2:paymentId><ns2:externalPaymentId>ext{i}</ns2:externalPaymentId><ns2:debitCredit>{dc}</ns2:debitCredit><ns2:valueDate>2026-09-17T12:00:00+04:00</ns2:valueDate><ns2:description>PayTaxi cashout</ns2:description><ns2:amount><ns2:amount>{amt.ToString(System.Globalization.CultureInfo.InvariantCulture)}</ns2:amount><ns2:currency>GEL</ns2:currency></ns2:amount><ns2:accountNumber>GE48TB7044436080100017</ns2:accountNumber></ns2:accountMovement>""");
        }
        sb.Append("</ns2:GetAccountMovementsResponseIo>");
        return sb.ToString();
    }
}
