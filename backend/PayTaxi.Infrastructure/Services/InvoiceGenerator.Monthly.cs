using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The monthly invoice PT-YYYY-MM from the platform owner (Swich) to one park — licence agreement
/// §2.5: "a monthly invoice summarises the settlement made". Paperwork follows money: the transfer
/// already carried "inv ref PT-YYYY-MM" in its bank description, so the park's accountant can tie the
/// invoice to the statement line.
///
/// A park of the licensee sees its own part only: its fees, its part of the month's settlement and the
/// transfer — never the licensee's shared costs, totals or barrier phase (those are between the licensee and
/// Swich; the licensee's own report has them). No share percentage, no phase sentence and no barrier/rest
/// split is printed for such a park: with the pro-rata rule each of them lets the park back out the
/// licensee's total fee income, the total due and — with the phase — its costs. Georgian only, like the
/// per-cashout invoice.
/// Months settled under the old nightly rule (legacy rows) still render, one line per day.
/// </summary>
public partial class InvoiceGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] GeorgianMonths =
    {
        "იანვარი", "თებერვალი", "მარტი", "აპრილი", "მაისი", "ივნისი",
        "ივლისი", "აგვისტო", "სექტემბერი", "ოქტომბერი", "ნოემბერი", "დეკემბერი",
    };

    public string MonthlyFileName(string parkSlug, int year, int month)
        => $"PT-{year:D4}-{month:D2}-{parkSlug}.pdf";

    public async Task<byte[]> RenderMonthlyAsync(Guid parkId, int year, int month, CancellationToken ct = default)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));

        var park = await _db.Parks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parkId, ct)
            ?? throw new InvalidOperationException($"Park {parkId} not found");

        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);

        var settlements = await _db.Settlements.AsNoTracking()
            .Where(s => s.ParkId == parkId && s.SettlementDate >= from && s.SettlementDate <= to)
            .OrderBy(s => s.SettlementDate).ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);

        if (settlements.Count == 0)
            throw new InvalidOperationException($"Park {parkId} has no settlement in {year:D4}-{month:D2}");

        var tz = SettlementService.ResolveTimeZone(_settlement.TimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var model = BuildMonthlyModel(park, settlements, from, to, nowLocal, tz);

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

                page.Content().Element(c => ComposeMonthly(c, model));
                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    t.Span($"{model.InvoiceRef} · {model.ParkLegalName} · გვერდი ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();

        _log.LogInformation("Rendered monthly invoice {Ref} for park {Park} ({Rows} settlements, {Bytes} bytes)",
            model.InvoiceRef, park.Slug, settlements.Count, bytes.Length);
        return bytes;
    }

    // ── Model ────────────────────────────────────────────────────────

    private MonthlyModel BuildMonthlyModel(Park park, List<Settlement> rows, DateOnly from, DateOnly to,
        DateTime nowLocal, TimeZoneInfo tz)
    {
        var lines = rows.Select(s => new MonthlyLine(
            Date: s.SettlementDate,
            IsMonthly: s.IsMonthly,
            Cashouts: s.CashoutCount,
            FeeTotal: s.FeeTotal,
            Phase1Fees: s.Phase1Fees,
            Phase2Fees: s.Phase2Fees,
            SwichShare: s.SwichShare,
            Status: s.Status,
            TransferId: s.BankTransferId,
            TransferredAtLocal: s.CompletedAt is { } at ? TimeZoneInfo.ConvertTimeFromUtc(at, tz) : null,
            FailureCode: SettlementMath.FailureCode(s.FailureReason),
            PaidExternallyOn: s.Status == SettlementStatus.SettledExternally ? s.ExternalPaidOn : null,
            ExternalPayer: s.ExternalPayer,
            ViaLicensee: s.MonthlyStatementId is not null)).ToList();

        var completed = lines.Where(l => l.Status == SettlementStatus.Completed).ToList();
        var paidExternally = lines.Where(l => l.Status == SettlementStatus.SettledExternally).ToList();
        var outstanding = lines.Where(l => l.Status is not (SettlementStatus.Completed or SettlementStatus.SettledExternally)).ToList();

        // Phase-1 progress at the end of the month = cumulative before the last settlement + its fees.
        var last = rows[^1];
        var cumulativeAtEnd = last.CumulativeFeesBefore + last.FeeTotal;

        // Whether the month's row is the park's part of a licensee statement (§2.5, pro rata). Only that fact is
        // used — none of the statement's own figures reach the park's document.
        var viaLicensee = rows.Any(r => r.MonthlyStatementId is not null);

        var isCurrentMonth = nowLocal.Year == from.Year && nowLocal.Month == from.Month;

        return new MonthlyModel(
            InvoiceRef: $"PT-{from:yyyy-MM}",
            IssueDate: isCurrentMonth ? DateOnly.FromDateTime(nowLocal) : to,
            PeriodFrom: from,
            PeriodTo: to,
            MonthTitle: $"{from.Year} წლის {GeorgianMonths[from.Month - 1]}",
            IsInterim: isCurrentMonth,
            DueDate: SettlementService.DueDate(from, _settlement.DueDays),
            OperatorName: _opts.OperatingEntityName,
            OperatorTaxId: _opts.OperatingEntityTaxId,
            OperatorIban: string.IsNullOrWhiteSpace(_settlement.SwichIban) ? "—" : _settlement.SwichIban,
            OperatorHolder: _settlement.SwichHolderName,
            ParkName: park.Name,
            ParkLegalName: park.LegalEntityName ?? park.Name,
            ParkTaxId: park.TaxId ?? "—",
            ParkPhone: park.Phone ?? "—",
            Lines: lines,
            CashoutCount: lines.Sum(l => l.Cashouts),
            FeeTotal: lines.Sum(l => l.FeeTotal),
            SwichShareTotal: lines.Sum(l => l.SwichShare),
            ParkShareTotal: lines.Sum(l => l.FeeTotal - l.SwichShare),
            TransferredTotal: completed.Sum(l => l.SwichShare),
            PaidExternallyTotal: paidExternally.Sum(l => l.SwichShare),
            OutstandingTotal: outstanding.Sum(l => l.SwichShare),
            OutstandingCount: outstanding.Count,
            ViaLicensee: viaLicensee,
            PaidOutsideNote: ParkInvoiceText.PaidOutsideNoteFor(rows),
            SwichSharePercent: park.SwichSharePercent,
            Phase1SharePercent: park.Phase1SharePercent,
            Phase1CapGel: park.Phase1CapGel,
            CumulativeFeesAtEnd: cumulativeAtEnd,
            GeneratedAtLocal: nowLocal);
    }

    // ── Composition ──────────────────────────────────────────────────

    private static void ComposeMonthly(IContainer container, MonthlyModel m)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"ინვოისი {m.InvoiceRef}").FontSize(26).Bold();
                    c.Item().Text($"PayTaxi პლატფორმის მომსახურება · {m.MonthTitle}")
                        .FontSize(11).FontColor(Colors.Grey.Darken1);
                    if (m.IsInterim)
                        c.Item().PaddingTop(4).Text("შუალედური — თვე ჯერ არ დასრულებულა, საბოლოო ინვოისი თვის ბოლოს")
                            .FontSize(9).FontColor(Colors.Orange.Darken3);
                });
                row.ConstantItem(150).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text(m.IssueDate.ToString("dd.MM.yyyy", Inv)).FontSize(11).FontColor(Colors.Grey.Darken1);
                    c.Item().AlignRight().Text($"პერიოდი: {m.PeriodFrom.ToString("dd.MM.yyyy", Inv)} – {m.PeriodTo.ToString("dd.MM.yyyy", Inv)}")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });
            });

            col.Item().PaddingTop(22);

            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => ComposeMonthlyParty(c, "მომსახურების მიმწოდებელი:", new[]
                {
                    ("კომპანია:", m.OperatorName),
                    ("ს/კ:", m.OperatorTaxId),
                    ("ანგარიში (IBAN):", m.OperatorIban),
                    ("ანგარიშის მფლობელი:", m.OperatorHolder),
                }));
                row.ConstantItem(20);
                row.RelativeItem().Element(c => ComposeMonthlyParty(c, "დამკვეთი:", new[]
                {
                    ("კომპანია:", m.ParkLegalName),
                    ("ს/კ:", m.ParkTaxId),
                    ("ტელეფონი:", m.ParkPhone),
                    ("პარკი PayTaxi-ზე:", m.ParkName),
                }));
            });

            col.Item().PaddingTop(22);
            col.Item().Element(c => ComposeMonthlyService(c, m));

            col.Item().PaddingTop(18);
            col.Item().Element(c => ComposeMonthlyTable(c, m));

            col.Item().PaddingTop(16);
            col.Item().Element(c => ComposeMonthlyTerms(c, m));

            col.Item().PaddingTop(14);
            col.Item().Element(c => ComposeMonthlyNote(c, m));
        });
    }

    private static void ComposeMonthlyParty(IContainer container, string title, (string Label, string Value)[] rows)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(10).Bold();
            col.Item().PaddingTop(8);
            foreach (var (label, value) in rows)
            {
                col.Item().Row(row =>
                {
                    row.ConstantItem(115).Text(label).FontSize(10).FontColor(Colors.Grey.Darken1);
                    row.RelativeItem().Text(value).FontSize(10).Bold();
                });
                col.Item().PaddingTop(4);
            }
        });
    }

    private static void ComposeMonthlyService(IContainer container, MonthlyModel m)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Text("მომსახურება").FontSize(11).Bold();
                row.ConstantItem(90).AlignRight().Text("თანხა").FontSize(11).Bold();
            });
            col.Item().PaddingVertical(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(m.ViaLicensee
                        ? "PayTaxi პლატფორმის მომსახურება — პლატფორმის მფლობელის კუთვნილი თანხა, პარკის წილი"
                        : "PayTaxi პლატფორმის მომსახურება — გამოტანის საკომისიოს Swich-ის წილი").FontSize(11);
                    c.Item().Text(ParkInvoiceText.ServiceDetail(m.CashoutCount, m.FeeTotal)).FontSize(9).FontColor(Colors.Grey.Darken1);
                });
                row.ConstantItem(90).AlignRight().Text(Gel(m.SwichShareTotal)).FontSize(11).Bold();
            });

            col.Item().PaddingVertical(6).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            col.Item().Row(row =>
            {
                row.RelativeItem().Text("სულ").FontSize(11).Bold();
                row.ConstantItem(90).AlignRight().Text(Gel(m.SwichShareTotal)).FontSize(12).Bold();
            });
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text("მათ შორის უკვე ჩარიცხული (ყოველთვიური ავტომატური ანგარიშსწორება)").FontSize(10).FontColor(Colors.Grey.Darken1);
                row.ConstantItem(90).AlignRight().Text(Gel(m.TransferredTotal)).FontSize(10).Bold();
            });
            if (m.PaidExternallyTotal > 0)
                col.Item().PaddingTop(2).Row(row =>
                {
                    row.RelativeItem().Text("მათ შორის დაფარულია პლატფორმის გარეთ (პარკის ანგარიშიდან აღარ ჩამოიჭრება)").FontSize(10).FontColor(Colors.Grey.Darken1);
                    row.ConstantItem(90).AlignRight().Text(Gel(m.PaidExternallyTotal)).FontSize(10).Bold();
                });
            col.Item().PaddingTop(2).Row(row =>
            {
                var color = m.OutstandingTotal > 0 ? Colors.Red.Darken2 : Colors.Grey.Darken1;
                row.RelativeItem().Text(m.OutstandingCount > 0
                        ? $"გადასარიცხი — გადარიცხვა ვერ შესრულდა ან მიმდინარეობს (ვადა: {m.DueDate.ToString("dd.MM.yyyy", Inv)})"
                        : "გადასარიცხი")
                    .FontSize(10).FontColor(color);
                row.ConstantItem(90).AlignRight().Text(Gel(m.OutstandingTotal)).FontSize(10).Bold().FontColor(color);
            });
        });
    }

    private static void ComposeMonthlyTable(IContainer container, MonthlyModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("ანგარიშსწორება").FontSize(11).Bold();
            col.Item().PaddingTop(6);
            // A licensee park's barrier/rest split is the statement's barrier fraction — the phase — so the column
            // exists only for a Swich-direct park, whose phase 1 / 2 is its own.
            var phaseColumn = !m.ViaLicensee;
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(58);   // period
                    cols.ConstantColumn(52);   // cashouts
                    cols.ConstantColumn(62);   // fees
                    if (phaseColumn) cols.ConstantColumn(78);   // phase split
                    cols.ConstantColumn(66);   // swich share
                    cols.RelativeColumn(1.1f); // status
                    cols.RelativeColumn(1.4f); // transfer ref
                });

                table.Header(h =>
                {
                    h.Cell().Element(Head).Text("პერიოდი");
                    h.Cell().Element(Head).AlignRight().Text("გამოტანა");
                    h.Cell().Element(Head).AlignRight().Text("საკომისიო");
                    if (phaseColumn) h.Cell().Element(Head).AlignRight().Text("ფაზა 1 / 2");
                    h.Cell().Element(Head).AlignRight().Text("Swich-ის წილი");
                    h.Cell().Element(Head).PaddingLeft(8).Text("სტატუსი");
                    h.Cell().Element(Head).PaddingLeft(8).Text("გადარიცხვა");
                });

                foreach (var l in m.Lines)
                {
                    var failed = l.Status == SettlementStatus.Failed;
                    table.Cell().Element(Body).Text(l.IsMonthly ? l.Date.ToString("MM.yyyy", Inv) : l.Date.ToString("dd.MM", Inv));
                    table.Cell().Element(Body).AlignRight().Text(l.Cashouts.ToString(Inv));
                    table.Cell().Element(Body).AlignRight().Text(Gel(l.FeeTotal));
                    if (phaseColumn)
                        table.Cell().Element(Body).AlignRight().Text($"{Num(l.Phase1Fees)} / {Num(l.Phase2Fees)}").FontColor(Colors.Grey.Darken1);
                    table.Cell().Element(Body).AlignRight().Text(Gel(l.SwichShare)).Bold();
                    table.Cell().Element(Body).PaddingLeft(8).Text(StatusLabel(l))
                        .FontColor(failed ? Colors.Red.Darken2 : Colors.Black);
                    table.Cell().Element(Body).PaddingLeft(8).Text(l.TransferId ?? (l.SwichShare == 0 ? "— (გადასარიცხი არ არის)" : "—"))
                        .FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                }

                if (m.Lines.Count > 1)
                {
                    table.Cell().Element(Foot).Text("სულ").Bold();
                    table.Cell().Element(Foot).AlignRight().Text(m.CashoutCount.ToString(Inv)).Bold();
                    table.Cell().Element(Foot).AlignRight().Text(Gel(m.FeeTotal)).Bold();
                    if (phaseColumn)
                        table.Cell().Element(Foot).AlignRight().Text($"{Num(m.Lines.Sum(l => l.Phase1Fees))} / {Num(m.Lines.Sum(l => l.Phase2Fees))}").FontColor(Colors.Grey.Darken1);
                    table.Cell().Element(Foot).AlignRight().Text(Gel(m.SwichShareTotal)).Bold();
                    table.Cell().ColumnSpan(2).Element(Foot).PaddingLeft(8)
                        .Text($"ჩარიცხულია {Gel(m.TransferredTotal)}"
                              + (m.PaidExternallyTotal > 0 ? $" · დაფარულია გარეთ {Gel(m.PaidExternallyTotal)}" : "")
                              + $" · გადასარიცხი {Gel(m.OutstandingTotal)}").FontSize(9);
                }
            });
        });

        static IContainer Head(IContainer c) => c
            .BorderBottom(0.8f).BorderColor(Colors.Grey.Darken1)
            .PaddingVertical(4).DefaultTextStyle(t => t.FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken2));

        static IContainer Body(IContainer c) => c
            .BorderBottom(0.4f).BorderColor(Colors.Grey.Lighten2)
            .PaddingVertical(3.5f).DefaultTextStyle(t => t.FontSize(9));

        static IContainer Foot(IContainer c) => c
            .BorderTop(0.8f).BorderColor(Colors.Grey.Darken1)
            .PaddingVertical(4).DefaultTextStyle(t => t.FontSize(9));
    }

    private static void ComposeMonthlyTerms(IContainer container, MonthlyModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("განაწილების პირობები").FontSize(11).Bold();
            col.Item().PaddingTop(6);
            string terms;
            if (m.ViaLicensee)
            {
                terms = ParkInvoiceText.LicenseeParkTerms;
            }
            else if (m.Phase1CapGel is { } cap && m.Phase1SharePercent is { } p1)
            {
                var reached = m.CumulativeFeesAtEnd >= cap;
                terms =
                    $"ფაზა 1: საკომისიოს {Pct(p1)} Swich-ს, სანამ პარკის დაგროვილი საკომისიო მიაღწევს {Gel(cap)}-ს; " +
                    $"შემდეგ ფაზა 2: {Pct(m.SwichSharePercent)}. " +
                    (reached
                        ? $"ზღვარი მიღწეულია — თვის ბოლოსთვის დაგროვილია {Gel(m.CumulativeFeesAtEnd)}."
                        : $"თვის ბოლოსთვის დაგროვილია {Gel(m.CumulativeFeesAtEnd)} / {Gel(cap)}.");
            }
            else
            {
                terms = $"Swich-ის წილი: თითოეული გამოტანის საკომისიოს {Pct(m.SwichSharePercent)}. დანარჩენი რჩება პარკს.";
            }
            col.Item().Text(terms).FontSize(10).LineHeight(1.4f).FontColor(Colors.Grey.Darken2);
        });
    }

    private static void ComposeMonthlyNote(IContainer container, MonthlyModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("შენიშვნა").FontSize(11).Bold();
            col.Item().PaddingTop(6);
            var note =
                $"თანხა Swich-ს ერიცხება ყოველთვიური ავტომატური ანგარიშსწორებით {m.ParkLegalName}-ის საბანკო ანგარიშიდან, " +
                $"თვის დასრულებიდან არაუგვიანეს {m.DueDate.ToString("dd.MM.yyyy", Inv)}-ისა, დანიშნულებით " +
                $"„PayTaxi settlement YYYY-MM, N tx, inv ref {m.InvoiceRef}“. " +
                "ეს ინვოისი აღრიცხავს უკვე შესრულებულ გადარიცხვას — დამატებითი გადახდა საჭირო არ არის. " +
                (m.OutstandingTotal > 0
                    ? "„გადასარიცხი“ თანხის გადარიცხვა ავტომატურად მეორდება ყოველ ღამე, სანამ პარკის ანგარიშზე საკმარისი ნაშთი არ იქნება. "
                    : string.Empty) +
                (m.PaidOutsideNote is { } outside ? outside + " " : string.Empty) +
                "ანგარიშსწორებით დაფარული გამოტანების სია ხელმისაწვდომია PayTaxi ადმინ-პანელში (ანგარიშსწორებები → ჩანაწერის გახსნა).";
            col.Item().Text(note).FontSize(10).LineHeight(1.4f).FontColor(Colors.Grey.Darken2);
            col.Item().PaddingTop(8);
            col.Item().Text($"გენერირებულია PayTaxi პლატფორმაზე {m.GeneratedAtLocal.ToString("dd.MM.yyyy HH:mm", Inv)}. " +
                            $"პლატფორმის მფლობელი: {m.OperatorName} (ს/კ: {m.OperatorTaxId}).")
                .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string Gel(decimal v) => $"{v.ToString("N2", Inv)}₾";
    private static string Num(decimal v) => v.ToString("0.##", Inv);
    private static string Pct(decimal v) => $"{v.ToString("0.##", Inv)}%";

    private static string StatusLabel(MonthlyLine l) => l.Status switch
    {
        SettlementStatus.Completed => l.TransferredAtLocal is { } at ? $"ჩარიცხულია {at.ToString("dd.MM", Inv)}" : "ჩარიცხულია",
        SettlementStatus.SettledExternally => ParkInvoiceText.PaidOutsideLabel(l.ExternalPayer, l.ViaLicensee)
                                              + (l.PaidExternallyOn is { } on ? $" {on.ToString("dd.MM", Inv)}" : ""),
        SettlementStatus.Failed => string.IsNullOrEmpty(l.FailureCode) ? "ვერ შესრულდა" : $"ვერ შესრულდა ({FailureLabel(l.FailureCode)})",
        SettlementStatus.Processing => "მიმდინარეობს",
        _ => "მოლოდინში",
    };

    private static string FailureLabel(string code) => code switch
    {
        "INSUFFICIENT_PARK_BALANCE" => "არასაკმარისი ნაშთი",
        "NO_PARK_ACCOUNT" => "აქტიური ანგარიში არ არის",
        "SWICH_IBAN_NOT_CONFIGURED" => "მიმღები ანგარიში არ არის მითითებული",
        _ => code,
    };

    private sealed record MonthlyLine(
        DateOnly Date, bool IsMonthly, int Cashouts, decimal FeeTotal, decimal Phase1Fees, decimal Phase2Fees, decimal SwichShare,
        SettlementStatus Status, string? TransferId, DateTime? TransferredAtLocal, string? FailureCode,
        DateOnly? PaidExternallyOn, string? ExternalPayer, bool ViaLicensee);

    private sealed record MonthlyModel(
        string InvoiceRef,
        DateOnly IssueDate,
        DateOnly PeriodFrom,
        DateOnly PeriodTo,
        string MonthTitle,
        bool IsInterim,
        DateOnly DueDate,
        string OperatorName,
        string OperatorTaxId,
        string OperatorIban,
        string OperatorHolder,
        string ParkName,
        string ParkLegalName,
        string ParkTaxId,
        string ParkPhone,
        List<MonthlyLine> Lines,
        int CashoutCount,
        decimal FeeTotal,
        decimal SwichShareTotal,
        decimal ParkShareTotal,
        decimal TransferredTotal,
        decimal PaidExternallyTotal,
        decimal OutstandingTotal,
        int OutstandingCount,
        bool ViaLicensee,
        string? PaidOutsideNote,
        decimal SwichSharePercent,
        decimal? Phase1SharePercent,
        decimal? Phase1CapGel,
        decimal CumulativeFeesAtEnd,
        DateTime GeneratedAtLocal);
}

/// <summary>
/// The free text the park's monthly invoice prints about its share. Built only from the park's OWN figures —
/// by signature, nothing of the licensee's statement (its totals, costs, phase) can reach a park's document.
/// </summary>
public static class ParkInvoiceText
{
    public static string ServiceDetail(int cashoutCount, decimal feeTotal) =>
        $"{cashoutCount} გამოტანა · მძღოლების საკომისიო სულ {feeTotal.ToString("N2", CultureInfo.InvariantCulture)}₾";

    /// <summary>The park learns its own part and the rule — not the licensee's costs, totals or barrier phase.</summary>
    public const string LicenseeParkTerms =
        "პარკი PayTaxi-ს იყენებს ლიცენზიატის მეშვეობით. პლატფორმის მფლობელის კუთვნილი თანხა ყოველთვიურად " +
        "გამოითვლება ლიცენზიატის ყველა პარკისთვის ერთად (სალიცენზიო ხელშეკრულება, მუხლი 2) და ირიცხება იმ " +
        "პარკების ანგარიშებიდან, რომლებზეც საკომისიო დაგროვდა — თითოეულიდან მისი საკომისიო შემოსავლის პროპორციულად. " +
        "დანარჩენი თანხის განაწილება ლიცენზიატსა და პარკს შორის მათი ხელშეკრულებით წესრიგდება.";

    /// <summary>
    /// The note on a debt paid outside the platform. Only a licensee's park is party to the licence agreement, so only
    /// its invoice cites §2.5 (the licensee answering for its park); a Swich-direct park's never names a contract it
    /// did not sign.
    /// </summary>
    public static string PaidOutsideNote(bool viaLicensee) => viaLicensee
        ? "„დაფარულია პლატფორმის გარეთ“ — თანხა პლატფორმის მფლობელს უშუალოდ გადაეცა (სალიცენზიო ხელშეკრულება, მუხლი 2.5); მისი გადარიცხვა პარკის ანგარიშიდან აღარ განხორციელდება."
        : "„დაფარულია პლატფორმის გარეთ“ — თანხა პლატფორმის მფლობელს უშუალოდ გადაეცა; მისი გადარიცხვა პარკის ანგარიშიდან აღარ განხორციელდება.";

    /// <summary>
    /// The note for one invoice's rows; null when none of them was paid outside the platform. It follows the rows that WERE
    /// paid outside: fees are settled under the regime they were earned under, so a park reassigned mid-month has a
    /// Swich-direct row and a licensee row on one invoice — and when only the direct one was paid outside, the note must not
    /// cite §2.5 of an agreement that row is not under, though the month has a licensee row too.
    /// </summary>
    public static string? PaidOutsideNoteFor(IEnumerable<Settlement> rows)
    {
        var outside = rows.Where(r => r.Status == SettlementStatus.SettledExternally).ToList();
        return outside.Count == 0 ? null : PaidOutsideNote(outside.Any(r => r.MonthlyStatementId is not null));
    }

    /// <summary>Who settled the line: "the licensee" only on a licensee park's row (a direct row cannot carry it — and an old one never says it).</summary>
    public static string PaidOutsideLabel(string? payer, bool viaLicensee) =>
        payer == "licensee" && viaLicensee ? "დაფარა ლიცენზიატმა" : "დაფარულია პლატფორმის გარეთ";
}
