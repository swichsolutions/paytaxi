using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// QuestPDF-based renderer for cashout invoices. Modelled after the paypro
/// reference (Georgian only, A4 portrait, faint label color + bold value).
/// </summary>
public class InvoiceGenerator : IInvoiceGenerator
{
    private readonly AppDbContext _db;
    private readonly InvoiceOptions _opts;
    private readonly ILogger<InvoiceGenerator> _log;

    public InvoiceGenerator(
        AppDbContext db,
        IOptions<InvoiceOptions> opts,
        ILogger<InvoiceGenerator> log)
    {
        _db = db;
        _opts = opts.Value;
        _log = log;

        // Community licence — set once per process.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<string> GetFileNameAsync(Guid cashoutId, CancellationToken ct = default)
    {
        var number = await _db.Cashouts.AsNoTracking()
            .Where(c => c.Id == cashoutId)
            .Select(c => c.InvoiceNumber)
            .FirstOrDefaultAsync(ct);
        return $"invoice-{number ?? 0}.pdf";
    }

    public async Task<byte[]> RenderAsync(Guid cashoutId, CancellationToken ct = default)
    {
        var c = await _db.Cashouts.AsNoTracking()
            .Include(c => c.Driver)
            .Include(c => c.Park)
            .Include(c => c.BankCard)
            .FirstOrDefaultAsync(c => c.Id == cashoutId, ct)
            ?? throw new InvalidOperationException($"Cashout {cashoutId} not found");

        if (c.Status != CashoutStatus.Completed)
            throw new InvalidOperationException($"Cashout {cashoutId} is not Completed (status={c.Status})");
        if (c.InvoiceNumber is null)
            throw new InvalidOperationException($"Cashout {cashoutId} has no invoice number");

        var model = BuildModel(c);

        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t
                    .FontSize(10)
                    .FontFamily(Fonts.Calibri, "Sylfaen", "Noto Sans Georgian", "Segoe UI"));

                page.Content().Element(content => Compose(content, model));
            });
        }).GeneratePdf();

        _log.LogInformation("Rendered invoice #{Number} for cashout {Id} ({Bytes} bytes)",
            c.InvoiceNumber, c.Id, bytes.Length);
        return bytes;
    }

    // ── Composition ──────────────────────────────────────────────────
    private static void Compose(IContainer container, InvoiceModel m)
    {
        container.Column(col =>
        {
            // Header
            col.Item().Row(row =>
            {
                row.RelativeItem().Text($"ინვოისი #{m.InvoiceNumber}")
                    .FontSize(26).Bold();
                row.ConstantItem(120).AlignRight().AlignBottom()
                    .Text(m.Date.ToString("dd.MM.yyyy")).FontSize(11).FontColor(Colors.Grey.Darken1);
            });

            col.Item().PaddingTop(25);

            // Issuer + Recipient side-by-side
            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => ComposeIssuer(c, m));
                row.ConstantItem(20);
                row.RelativeItem().Element(c => ComposeRecipient(c, m));
            });

            col.Item().PaddingTop(25);

            // Service line
            col.Item().Element(c => ComposeService(c, m));

            col.Item().PaddingTop(15);

            // Bank details
            col.Item().Element(c => ComposeBank(c, m));

            col.Item().PaddingTop(20);

            // Note
            col.Item().Element(c => ComposeNote(c, m));
        });
    }

    private static void ComposeIssuer(IContainer container, InvoiceModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("მოთხოვნის გამცემი:").FontSize(10).Bold();
            col.Item().PaddingTop(8);
            col.Item().Element(c => KvRow(c, "მძღოლი / კურიერი:", m.DriverName));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "მართვის მოწმობის N:", m.DriverLicenseOrProfile));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "ტელეფონი:", m.DriverPhone));
        });
    }

    private static void ComposeRecipient(IContainer container, InvoiceModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("მოთხოვნის მიმღები:").FontSize(10).Bold();
            col.Item().PaddingTop(8);
            col.Item().Element(c => KvRow(c, "კომპანიის დასახელება:", m.ParkLegalName));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "ს/კ:", m.ParkTaxId));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "ტელეფონი:", m.ParkPhone));
        });
    }

    private static void ComposeService(IContainer container, InvoiceModel m)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("საფასური").FontSize(11).Bold();
                row.ConstantItem(80).AlignRight().Text("თანხა").FontSize(11).Bold();
            });
            col.Item().PaddingVertical(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("ტაქსის/კურიერის მომსახურების განცემა").FontSize(11);
                row.ConstantItem(80).AlignRight()
                    .Text($"{m.NetAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}₾")
                    .FontSize(11).Bold();
            });
        });
    }

    private static void ComposeBank(IContainer container, InvoiceModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("საბანკო რეკვიზიტები").FontSize(11).Bold();
            col.Item().PaddingTop(8);
            col.Item().Element(c => KvRow(c, "ბანკი:", m.DriverBankName));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "ანგარიშის ნომერი (IBAN):", m.DriverIbanOrPan));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "მიმღები:", m.DriverName));
            col.Item().PaddingTop(4);
            col.Item().Element(c => KvRow(c, "დანიშნულება:", m.PaymentPurpose));
        });
    }

    private static void ComposeNote(IContainer container, InvoiceModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("შენიშვნა").FontSize(11).Bold();
            col.Item().PaddingTop(6);
            col.Item().Text(m.Note).FontSize(10).LineHeight(1.4f).FontColor(Colors.Grey.Darken2);
        });
    }

    private static void KvRow(IContainer container, string label, string value)
    {
        container.Row(row =>
        {
            row.ConstantItem(160).Text(label).FontSize(10).FontColor(Colors.Grey.Darken1);
            row.RelativeItem().Text(value).FontSize(10).Bold();
        });
    }

    private InvoiceModel BuildModel(Cashout c)
    {
        var net = c.Amount - c.Fee;
        var purpose =
            $"PayTaxi:{c.InvoiceNumber} გამოშუშავებული თანხის ჩარიცხვა " +
            $"({c.Driver.Name}) პარკი: {c.Park.Name}";

        var note =
            $"მოთხოვნა დაგენერირებულია მოთხოვნის გამცემის მიერ, პლატფორმა PayTaxi-ზე. " +
            $"გადახდას, დამკვეთის სახელით, ახორციელებს კომერციული შუამავალი " +
            $"{_opts.OperatingEntityName} (ს/კ: {_opts.OperatingEntityTaxId})";

        return new InvoiceModel(
            InvoiceNumber: c.InvoiceNumber!.Value,
            Date: c.CompletedAt ?? c.CreatedAt,
            DriverName: c.Driver.Name ?? "(უსახელო)",
            DriverLicenseOrProfile: c.Driver.YandexDriverProfileId ?? "—",
            DriverPhone: c.Driver.PhoneEncrypted,
            ParkLegalName: c.Park.LegalEntityName ?? c.Park.Name,
            ParkTaxId: c.Park.TaxId ?? "—",
            ParkPhone: c.Park.Phone ?? "—",
            NetAmount: net,
            DriverBankName: c.BankCard.BankType,
            // TODO Phase 8: collect real driver IBAN at onboarding. For now, surface
            // the masked PAN so the recipient is at least uniquely identifiable.
            DriverIbanOrPan: c.BankCard.MaskedPan,
            PaymentPurpose: purpose,
            Note: note);
    }

    private record InvoiceModel(
        long InvoiceNumber,
        DateTime Date,
        string DriverName,
        string DriverLicenseOrProfile,
        string DriverPhone,
        string ParkLegalName,
        string ParkTaxId,
        string ParkPhone,
        decimal NetAmount,
        string DriverBankName,
        string DriverIbanOrPan,
        string PaymentPurpose,
        string Note);
}

public class InvoiceOptions
{
    public const string SectionName = "Invoice";

    /// <summary>The legal entity executing payments under commercial agent authority.</summary>
    public string OperatingEntityName { get; set; } = "Swich Solutions LLC";

    /// <summary>Georgian tax-payer ID (s/k) of the operating entity.</summary>
    public string OperatingEntityTaxId { get; set; } = "405848882";
}
