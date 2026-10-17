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
/// The licensee's detailed monthly report (licence agreement §2.6: "the licensee receives a detailed
/// monthly report"). Rendered from one stored <see cref="MonthlyStatement"/> — its frozen figures, never
/// a recomputation — so the document always agrees with what was collected:
///   1. fee income per park; 2. the shared costs of each side, invoice by invoice (§2.4);
///   3. the calculation, written out with the real numbers; 4. barrier progress (§2.3);
///   5. the per-park split and each transfer's status — including a debt paid outside the platform (§2.5) —
///      with §4.1.2 overdue information, the penalty counted only on the days that were the park's side.
/// Georgian only, the same look and fonts as the invoices.
/// </summary>
public partial class InvoiceGenerator
{
    public string LicenseeReportFileName(int year, int month) => $"PT-{year:D4}-{month:D2}-report.pdf";

    public async Task<byte[]> RenderLicenseeReportAsync(Guid statementId, CancellationToken ct = default)
    {
        var st = await _db.MonthlyStatements.AsNoTracking()
            .Include(m => m.Licensee)
            .FirstOrDefaultAsync(m => m.Id == statementId, ct)
            ?? throw new InvalidOperationException($"Statement {statementId} not found");

        var rows = await _db.Settlements.AsNoTracking()
            .Include(s => s.Park)
            .Where(s => s.MonthlyStatementId == statementId)
            .OrderByDescending(s => s.FeeTotal).ThenBy(s => s.Park.Name)
            .ToListAsync(ct);
        var costs = await _db.CommonCosts.AsNoTracking()
            .Where(c => c.StatementId == statementId)
            .OrderBy(c => c.InvoiceDate).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        // A correction is listed with the statement its original went into, so the reader can find what it corrects.
        var correctedIds = costs.Where(c => c.CorrectsId != null).Select(c => c.CorrectsId!.Value).Distinct().ToList();
        var correctedRefs = await _db.CommonCosts.AsNoTracking()
            .Where(c => correctedIds.Contains(c.Id))
            .Select(c => new { c.Id, Ref = c.Statement != null ? c.Statement.InvoiceRef : null })
            .ToDictionaryAsync(c => c.Id, c => c.Ref, ct);

        var tz = SettlementService.ResolveTimeZone(_settlement.TimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(nowLocal);
        var math = SettlementMath.Compute(new SettlementMath.StatementInputs(
            st.FeeIncome, st.LicenseeCosts, st.SwichCosts, st.CarryIn, st.BarrierGel, st.BarrierBefore, st.SwichSharePercent,
            st.BarrierFilledBefore));
        var dueDate = SettlementService.DueDate(st.Month, _settlement.DueDays);
        var failures = await SettlementOverdue.FailedAttemptsAsync(_db, rows.Select(r => r.Id), tz, ct);

        var parkLines = rows.Select(r =>
        {
            DateOnly? paidOn = r.Status switch
            {
                SettlementStatus.Completed => r.CompletedAt is { } at ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at, tz)) : null,
                SettlementStatus.SettledExternally => r.ExternalPaidOn,
                _ => null,
            };
            var overdue = SettlementOverdue.Of(r, failures.GetValueOrDefault(r.Id), _settlement, today, tz);
            return new ReportParkLine(
                ParkName: r.Park.Name,
                LegalName: r.Park.LegalEntityName ?? r.Park.Name,
                Cashouts: r.CashoutCount,
                Fees: r.FeeTotal,
                SharePercent: st.FeeIncome > 0 ? Math.Round(r.FeeTotal / st.FeeIncome * 100m, 2, MidpointRounding.AwayFromZero) : 0m,
                Amount: r.SwichShare,
                Status: r.Status,
                FailureCode: SettlementMath.FailureCode(r.FailureReason),
                TransferId: r.Status == SettlementStatus.SettledExternally ? r.ExternalReference : r.BankTransferId,
                PaidOn: paidOn,
                ExternalPayer: r.ExternalPayer,
                Overdue: overdue);
        }).ToList();

        var model = new ReportModel(
            Statement: st,
            Math: math,
            MonthTitle: $"{st.Month.Year} წლის {GeorgianMonths[st.Month.Month - 1]}",
            DueDate: dueDate,
            OwnerName: _opts.OperatingEntityName,
            OwnerTaxId: _opts.OperatingEntityTaxId,
            OwnerIban: string.IsNullOrWhiteSpace(_settlement.SwichIban) ? "—" : _settlement.SwichIban,
            Parks: parkLines,
            LicenseeCostRows: costs.Where(c => c.PaidBy == CostPaidBy.Licensee).ToList(),
            SwichCostRows: costs.Where(c => c.PaidBy == CostPaidBy.Swich).ToList(),
            CorrectedRefs: correctedRefs,
            ComputedAtLocal: TimeZoneInfo.ConvertTimeFromUtc(st.ComputedAt, tz),
            GeneratedAtLocal: nowLocal);

        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t
                    .FontSize(9.5f)
                    .FontFamily(Fonts.Calibri, "Sylfaen", "Noto Sans Georgian", "Segoe UI"));

                page.Content().Element(c => ComposeReport(c, model));
                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    t.Span($"{st.InvoiceRef} · ყოველთვიური ანგარიშგება · {st.Licensee.Name} · გვერდი ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();

        _log.LogInformation("Rendered licensee report {Ref} for {Licensee} ({Parks} parks, {Costs} costs, {Bytes} bytes)",
            st.InvoiceRef, st.Licensee.Name, rows.Count, costs.Count, bytes.Length);
        return bytes;
    }

    // ── Composition ──────────────────────────────────────────────────

    private static void ComposeReport(IContainer container, ReportModel m)
    {
        var st = m.Statement;
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"ყოველთვიური ანგარიშგება {st.InvoiceRef}").FontSize(20).Bold();
                    c.Item().Text($"სალიცენზიო ხელშეკრულება, მუხლი 2.6 · {m.MonthTitle}")
                        .FontSize(10.5f).FontColor(Colors.Grey.Darken1);
                });
                row.ConstantItem(170).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text($"გაანგარიშდა: {m.ComputedAtLocal.ToString("dd.MM.yyyy HH:mm", Inv)}").FontSize(9).FontColor(Colors.Grey.Darken1);
                    c.Item().AlignRight().Text($"პერიოდი: {st.Month.ToString("dd.MM.yyyy", Inv)} – {st.Month.AddMonths(1).AddDays(-1).ToString("dd.MM.yyyy", Inv)}")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                    c.Item().AlignRight().Text($"გადახდის ვადა: {m.DueDate.ToString("dd.MM.yyyy", Inv)}").FontSize(9).FontColor(Colors.Grey.Darken1);
                });
            });

            col.Item().PaddingTop(16);
            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => ComposeMonthlyParty(c, "პლატფორმის მფლობელი:", new[]
                {
                    ("კომპანია:", m.OwnerName),
                    ("ს/კ:", m.OwnerTaxId),
                    ("ანგარიში (IBAN):", m.OwnerIban),
                }));
                row.ConstantItem(20);
                row.RelativeItem().Element(c => ComposeMonthlyParty(c, "ლიცენზიატი:", new[]
                {
                    ("კომპანია:", st.Licensee.Name),
                    ("ს/კ:", st.Licensee.TaxId ?? "—"),
                    ("ელ. ფოსტა:", st.Licensee.Email ?? "—"),
                    ("გაშვების თარიღი:", st.Licensee.LaunchDate?.ToString("dd.MM.yyyy", Inv) ?? "—"),
                }));
            });

            // Headline: what Swich is due this month and where it stands.
            col.Item().PaddingTop(12).Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("პლატფორმის მფლობელის კუთვნილი თანხა (გამათანაბრებელი თანხა)").FontSize(9).FontColor(Colors.Grey.Darken2);
                    c.Item().Text(Gel(st.Due)).FontSize(18).Bold();
                    c.Item().Text($"{PhaseLabel(st.Phase)} · {StatementStatusLabel(st.Status)}").FontSize(9).FontColor(Colors.Grey.Darken2);
                });
                row.ConstantItem(200).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text($"ჩარიცხულია: {Gel(m.Parks.Where(p => p.Status == SettlementStatus.Completed).Sum(p => p.Amount))}").FontSize(9.5f);
                    var external = m.Parks.Where(p => p.Status == SettlementStatus.SettledExternally).Sum(p => p.Amount);
                    if (external > 0)
                        c.Item().AlignRight().Text($"გადახდილია პლატფორმის გარეთ: {Gel(external)}").FontSize(9.5f);
                    var outstanding = m.Parks.Where(p => !IsSettled(p.Status)).Sum(p => p.Amount);
                    c.Item().AlignRight().Text($"გადასარიცხი: {Gel(outstanding)}").FontSize(9.5f)
                        .FontColor(outstanding > 0 ? Colors.Red.Darken2 : Colors.Grey.Darken2);
                    if (st.CarryOut != 0)
                        c.Item().AlignRight().Text($"მომდევნო თვეზე გადადის: {SignedGel(st.CarryOut)}").FontSize(9.5f);
                });
            });

            col.Item().PaddingTop(16).Element(c => ComposeReportIncome(c, m));
            col.Item().PaddingTop(14).Element(c => ComposeReportCosts(c, m));
            col.Item().PaddingTop(14).Element(c => ComposeReportCalculation(c, m));
            col.Item().PaddingTop(14).Element(c => ComposeReportBarrier(c, m));
            col.Item().PaddingTop(14).Element(c => ComposeReportSplit(c, m));
            col.Item().PaddingTop(14).Element(c => ComposeReportNotes(c, m));
        });
    }

    private static void ComposeReportIncome(IContainer container, ReportModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("1. საკომისიო შემოსავალი").FontSize(11).Bold();
            col.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(c => { c.RelativeColumn(2.4f); c.ConstantColumn(60); c.ConstantColumn(80); c.ConstantColumn(70); });
                table.Header(h =>
                {
                    h.Cell().Element(RHead).Text("ტაქსოპარკი");
                    h.Cell().Element(RHead).AlignRight().Text("გამოტანა");
                    h.Cell().Element(RHead).AlignRight().Text("საკომისიო");
                    h.Cell().Element(RHead).AlignRight().Text("წილი");
                });
                foreach (var p in m.Parks)
                {
                    table.Cell().Element(RBody).Text(p.ParkName);
                    table.Cell().Element(RBody).AlignRight().Text(p.Cashouts.ToString(Inv));
                    table.Cell().Element(RBody).AlignRight().Text(Gel(p.Fees));
                    table.Cell().Element(RBody).AlignRight().Text(Pct(p.SharePercent));
                }
                if (m.Parks.Count == 0)
                    table.Cell().ColumnSpan(4).Element(RBody).Text("ამ თვეში საკომისიო შემოსავალი არ ყოფილა.").FontColor(Colors.Grey.Darken1);
                table.Cell().Element(RFoot).Text("სულ (F)").Bold();
                table.Cell().Element(RFoot).AlignRight().Text(m.Statement.CashoutCount.ToString(Inv)).Bold();
                table.Cell().Element(RFoot).AlignRight().Text(Gel(m.Statement.FeeIncome)).Bold();
                table.Cell().Element(RFoot).AlignRight().Text(m.Parks.Count > 0 ? "100%" : "—");
            });
        });
    }

    private static void ComposeReportCosts(IContainer container, ReportModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("2. საერთო ხარჯები (მუხლი 2.4)").FontSize(11).Bold();
            col.Item().PaddingTop(2).Text("მხოლოდ მესამე პირის ინვოისით დადასტურებული ხარჯები; მხარეთა საკუთარი შრომა და შიდა რესურსი ხარჯად არ ითვლება.")
                .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(6).Element(c => ComposeCostTable(c, "ლიცენზიატის გადახდილი (CL)", m.LicenseeCostRows, m.Statement.LicenseeCosts, m.CorrectedRefs));
            col.Item().PaddingTop(8).Element(c => ComposeCostTable(c, "პლატფორმის მფლობელის გადახდილი (CS)", m.SwichCostRows, m.Statement.SwichCosts, m.CorrectedRefs));
        });
    }

    private static void ComposeCostTable(IContainer container, string title, List<CommonCost> rows, decimal total,
        IReadOnlyDictionary<Guid, string?> correctedRefs)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(9.5f).Bold().FontColor(Colors.Grey.Darken3);
            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(58); c.RelativeColumn(1.4f); c.RelativeColumn(1.4f); c.RelativeColumn(1.1f); c.ConstantColumn(70);
                });
                table.Header(h =>
                {
                    h.Cell().Element(RHead).Text("თარიღი");
                    h.Cell().Element(RHead).Text("კატეგორია");
                    h.Cell().Element(RHead).Text("მიმწოდებელი");
                    h.Cell().Element(RHead).Text("ინვოისი №");
                    h.Cell().Element(RHead).AlignRight().Text("თანხა");
                });
                foreach (var c in rows)
                {
                    table.Cell().Element(RBody).Text(c.InvoiceDate.ToString("dd.MM.yyyy", Inv));
                    table.Cell().Element(RBody).Text(CategoryLabel(c.Category));
                    table.Cell().Element(RBody).Text(LicenseeReportText.CostDescription(c,
                        c.CorrectsId is { } oid ? correctedRefs.GetValueOrDefault(oid) : null));
                    table.Cell().Element(RBody).Text(c.InvoiceNumber);
                    table.Cell().Element(RBody).AlignRight().Text(Gel(c.Amount));
                }
                if (rows.Count == 0)
                    table.Cell().ColumnSpan(5).Element(RBody).Text("ხარჯი არ არის.").FontColor(Colors.Grey.Darken1);
                table.Cell().ColumnSpan(4).Element(RFoot).Text("სულ").Bold();
                table.Cell().Element(RFoot).AlignRight().Text(Gel(total)).Bold();
            });
        });
    }

    private static void ComposeReportCalculation(IContainer container, ReportModel m)
    {
        var st = m.Statement;
        var lines = new List<(string Label, string Expr)>
        {
            ("საკომისიო შემოსავალი (F)", Gel(st.FeeIncome)),
            ("ლიცენზიატის გადახდილი საერთო ხარჯები (CL)", Gel(st.LicenseeCosts)),
            ("პლატფორმის მფლობელის გადახდილი საერთო ხარჯები (CS)", Gel(st.SwichCosts)),
            ("პლატფორმის მფლობელის წილი (s)", Pct(st.SwichSharePercent)),
            ("ფაზა", PhaseLabel(st.Phase)),
        };
        lines.AddRange(FormulaLines(st, m.Math));
        lines.Add(("წინა თვიდან გადმოტანილი", SignedGel(st.CarryIn)));
        lines.Add(("D = E + გადმოტანილი", $"{N(st.Entitlement)} + ({N(st.CarryIn)}) = {Gel(st.Entitlement + st.CarryIn)}"));
        lines.Add(("გადასარიცხი ამ თვეში", Gel(st.Due)));
        lines.Add(("მომდევნო თვეზე გადადის", SignedGel(st.CarryOut)));

        container.Column(col =>
        {
            col.Item().Text("3. გაანგარიშება").FontSize(11).Bold();
            col.Item().PaddingTop(5);
            foreach (var (label, expr) in lines)
            {
                col.Item().BorderBottom(0.4f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).Row(row =>
                {
                    row.RelativeItem(1.15f).Text(label).FontSize(9.5f).FontColor(Colors.Grey.Darken2);
                    row.RelativeItem(1.6f).AlignRight().Text(expr).FontSize(9.5f).Bold();
                });
            }
        });
    }

    /// <summary>The phase formula written out with the statement's own numbers (the console shows the same lines).</summary>
    private static IEnumerable<(string Label, string Expr)> FormulaLines(MonthlyStatement st, SettlementMath.StatementResult math)
    {
        // CL or CS is negative in a month whose only entry of that side is a correction down — bracketed, so
        // "F − CL" never reads "100.00 − -166.50".
        static string O(decimal v) => LicenseeReportText.Operand(v);
        var barrierExpr = $"F − CL = {N(st.FeeIncome)} − {O(st.LicenseeCosts)} = {Gel(math.BarrierEntitlement)}";
        var normalExpr = st.SwichSharePercent == 50m
            ? $"(F − CL + CS) ÷ 2 = ({N(st.FeeIncome)} − {O(st.LicenseeCosts)} + {O(st.SwichCosts)}) ÷ 2 = {Gel(math.NormalEntitlement)}"
            : $"s × (F − CL − CS) + CS = {Pct(st.SwichSharePercent)} × ({N(st.FeeIncome)} − {O(st.LicenseeCosts)} − {O(st.SwichCosts)}) + {O(st.SwichCosts)} = {Gel(math.NormalEntitlement)}";

        switch (st.Phase)
        {
            case StatementPhase.Barrier:
                yield return ("ბარიერის ფაზა (მუხლი 2.3): E", barrierExpr);
                break;
            case StatementPhase.Normal:
                // R can sit under B after a loss month — the barrier, once filled, stays filled (§2.3).
                if (st.BarrierFilledBefore && st.BarrierBefore < st.BarrierGel)
                    yield return ("ბარიერი უკვე შევსებულია (მუხლი 2.3)",
                        $"R = {N(st.BarrierBefore)} < B = {N(st.BarrierGel)}, მაგრამ ბარიერი ადრე შეივსო — ზარალიანი თვე მას ხელახლა არ ხსნის");
                yield return ("გამათანაბრებელი თანხა (მუხლი 2.4): E", normalExpr);
                break;
            default:
                var room = st.BarrierGel - st.BarrierBefore;
                yield return ("ბარიერის დარჩენილი ნაწილი: B − R", $"{N(st.BarrierGel)} − {N(st.BarrierBefore)} = {Gel(room)}");
                yield return ("ბარიერის წესით", barrierExpr);
                yield return ("ჩვეულებრივი წესით", normalExpr);
                yield return ("φ = (B − R) ÷ (F − CL)", $"{N(room)} ÷ {N(math.BarrierEntitlement)} = {st.BarrierFraction.ToString("0.######", Inv)}");
                yield return ("E = (B − R) + (1 − φ) × ჩვეულებრივი", $"{N(room)} + (1 − {st.BarrierFraction.ToString("0.######", Inv)}) × {N(math.NormalEntitlement)} = {Gel(st.Entitlement)}");
                break;
        }
    }

    private static void ComposeReportBarrier(IContainer container, ReportModel m)
    {
        var st = m.Statement;
        var after = st.BarrierBefore + st.Entitlement;
        // Past the barrier the entitlement keeps growing; the barrier itself is simply full — and stays full once
        // reached, even when a later loss month lowers the running total (§2.3).
        var isFilled = st.BarrierFilledBefore || after >= st.BarrierGel;
        var filled = isFilled ? st.BarrierGel : Math.Clamp(after, 0m, st.BarrierGel);
        var pct = st.BarrierGel <= 0 || isFilled ? 1m : Math.Clamp(after / st.BarrierGel, 0m, 1m);
        container.Column(col =>
        {
            col.Item().Text("4. ბარიერი (მუხლი 2.3)").FontSize(11).Bold();
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text($"თვის დასაწყისში: {SignedGel(st.BarrierBefore)} · ამ თვის თანხა: {SignedGel(st.Entitlement)} · შევსებულია: {Gel(filled)} / {Gel(st.BarrierGel)}")
                    .FontSize(9.5f);
            });
            col.Item().PaddingTop(4).Height(7).Row(bar =>
            {
                if (pct > 0) bar.RelativeItem((float)Math.Max(pct, 0.001m)).Background(Colors.Amber.Darken1);
                if (pct < 1) bar.RelativeItem((float)Math.Max(1 - pct, 0.001m)).Background(Colors.Grey.Lighten3);
            });
            col.Item().PaddingTop(3).Text(isFilled
                    ? "ბარიერი შევსებულია — მომდევნო თვეებში მოქმედებს მუხლი 2.2-ის განაწილება."
                    : $"ბარიერის შევსებამდე დარჩა {Gel(st.BarrierGel - after)}.")
                .FontSize(9).FontColor(Colors.Grey.Darken2);
        });
    }

    private static void ComposeReportSplit(IContainer container, ReportModel m)
    {
        container.Column(col =>
        {
            col.Item().Text("5. განაწილება ტაქსოპარკებზე (მუხლი 2.5)").FontSize(11).Bold();
            col.Item().PaddingTop(2).Text("თანხა ირიცხება იმ პარკების ანგარიშებიდან, რომლებზეც საკომისიო დაგროვდა — თითოეულიდან მისი საკომისიო შემოსავლის პროპორციულად.")
                .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(1.5f); c.ConstantColumn(48); c.ConstantColumn(66); c.RelativeColumn(1.1f); c.RelativeColumn(1.2f); c.RelativeColumn(1.3f);
                });
                table.Header(h =>
                {
                    h.Cell().Element(RHead).Text("ტაქსოპარკი");
                    h.Cell().Element(RHead).AlignRight().Text("წილი");
                    h.Cell().Element(RHead).AlignRight().Text("თანხა");
                    h.Cell().Element(RHead).PaddingLeft(6).Text("სტატუსი");
                    h.Cell().Element(RHead).PaddingLeft(6).Text("გადარიცხვა");
                    h.Cell().Element(RHead).PaddingLeft(6).Text("ვადაგადაცილება");
                });
                foreach (var p in m.Parks)
                {
                    var failed = p.Status == SettlementStatus.Failed;
                    table.Cell().Element(RBody).Text(p.ParkName);
                    table.Cell().Element(RBody).AlignRight().Text(Pct(p.SharePercent));
                    table.Cell().Element(RBody).AlignRight().Text(Gel(p.Amount)).Bold();
                    table.Cell().Element(RBody).PaddingLeft(6).Text(ReportStatus(p)).FontColor(failed ? Colors.Red.Darken2 : Colors.Black);
                    table.Cell().Element(RBody).PaddingLeft(6).Text(p.TransferId ?? (p.Amount == 0 ? "— (გადასარიცხი არ არის)" : "—")).FontSize(8).FontColor(Colors.Grey.Darken2);
                    // The penalty is counted on the park's days only; the others were a technical fault (or nothing
                    // had been attempted yet) — §4.1.2: no penalty for those.
                    table.Cell().Element(RBody).PaddingLeft(6).Text(p.Overdue is { } o
                            ? $"{o.DaysOverdue} დღე · {Gel(o.Accrued)}" + (o.TechnicalDays > 0 ? $" ({o.TechnicalDays} დღე ტექნ.)" : "")
                            : "—")
                        .FontSize(8.5f).FontColor(p.Overdue is null ? Colors.Grey.Darken1 : Colors.Red.Darken2);
                }
                if (m.Parks.Count == 0)
                    table.Cell().ColumnSpan(6).Element(RBody).Text("გადასარიცხი არ არის.").FontColor(Colors.Grey.Darken1);
                table.Cell().Element(RFoot).Text("სულ").Bold();
                table.Cell().Element(RFoot).AlignRight().Text("");
                table.Cell().Element(RFoot).AlignRight().Text(Gel(m.Parks.Sum(p => p.Amount))).Bold();
                table.Cell().ColumnSpan(3).Element(RFoot).PaddingLeft(6).Text($"= D {Gel(m.Statement.Due)}").FontSize(9);
            });
        });
    }

    private static void ComposeReportNotes(IContainer container, ReportModel m)
    {
        var st = m.Statement;
        var notes = new List<string>
        {
            "ტრანზაქციების, საკომისიოსა და ხარჯების აღრიცხვის ერთადერთი წყაროა პლატფორმის ლეჯერი; იგივე მონაცემები ორივე მხარისთვის ხელმისაწვდომია ადმინ-პანელში (მუხლი 2.6).",
            "ბარიერის შევსება აღირიცხება პლატფორმის მფლობელის კუთვნილი თანხებით წინა თვეების ანგარიშგებების მიხედვით (გაშვების თარიღიდან).",
        };
        if (st.CarryOut < 0)
            notes.Add($"უარყოფითი შედეგი არ გადაირიცხება: {Gel(-st.CarryOut)} გადადის მომდევნო თვეზე ლიცენზიატის სასარგებლოდ და აკლდება მომდევნო თვის თანხას.");
        else if (st.CarryOut > 0)
            notes.Add($"{Gel(st.CarryOut)} გადადის მომდევნო თვეზე: ამ თვეში საკომისიო შემოსავალი არ ყოფილა, რომლის პროპორციულადაც თანხა პარკებზე გადანაწილდებოდა.");
        if (m.Parks.Any(p => p.Overdue is not null))
            notes.Add($"ვადაგადაცილება (მუხლი 4.1.2): {m.DueDate.ToString("dd.MM.yyyy", Inv)}-ის შემდეგ გადაუხდელ თანხაზე დღეში 0.1% — მოცემულია საინფორმაციოდ და ავტომატურად არ ირიცხება. " +
                      "პირგასამტეხლო ითვლება მხოლოდ იმ დღეებზე, როცა გადარიცხვა ტაქსოპარკის მიზეზით ვერ შესრულდა (არასაკმარისი ნაშთი, აქტიური ანგარიშის არქონა). " +
                      "„ტექნ.“ — დღეები, როცა მიზეზი პლატფორმის ან ბანკის ტექნიკური ხარვეზი იყო (ან გადარიცხვის მცდელობა ჯერ არ ყოფილა); მათზე პირგასამტეხლო არ ერიცხება.");
        if (m.Parks.Any(p => p.Status == SettlementStatus.SettledExternally))
            notes.Add("„გადახდილია პლატფორმის გარეთ“ — ტაქსოპარკის ანგარიშზე თანხა საკმარისი არ იყო და დავალიანება პლატფორმის მფლობელს უშუალოდ გადაუხადა მითითებულმა მხარემ (მუხლი 2.5); " +
                      "ტაქსოპარკის ანგარიშიდან გადარიცხვა აღარ მეორდება. გადახდის საბანკო რეკვიზიტი მითითებულია „გადარიცხვის“ სვეტში.");
        notes.Add("პლატფორმის მფლობელის წილი მოიცავს დღგ-ს, ასეთის გადახდის ვალდებულების წარმოშობის შემთხვევაში (მუხლი 2.7).");
        notes.Add("დანარჩენი თანხის განაწილება ლიცენზიატსა და ტაქსოპარკს შორის მათი ხელშეკრულებით წესრიგდება (მუხლი 2.5).");

        container.Column(col =>
        {
            col.Item().Text("შენიშვნები").FontSize(11).Bold();
            foreach (var n in notes)
                col.Item().PaddingTop(4).Text($"• {n}").FontSize(9).LineHeight(1.35f).FontColor(Colors.Grey.Darken2);
            col.Item().PaddingTop(8).Text($"გენერირებულია PayTaxi პლატფორმაზე {m.GeneratedAtLocal.ToString("dd.MM.yyyy HH:mm", Inv)}.")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static IContainer RHead(IContainer c) => c
        .BorderBottom(0.8f).BorderColor(Colors.Grey.Darken1)
        .PaddingVertical(3).DefaultTextStyle(t => t.FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken2));

    private static IContainer RBody(IContainer c) => c
        .BorderBottom(0.4f).BorderColor(Colors.Grey.Lighten2)
        .PaddingVertical(3).DefaultTextStyle(t => t.FontSize(9));

    private static IContainer RFoot(IContainer c) => c
        .BorderTop(0.8f).BorderColor(Colors.Grey.Darken1)
        .PaddingVertical(3).DefaultTextStyle(t => t.FontSize(9));

    private static string N(decimal v) => v.ToString("N2", Inv);
    private static string SignedGel(decimal v) => v < 0 ? $"−{Gel(-v)}" : Gel(v);

    private static string PhaseLabel(StatementPhase p) => p switch
    {
        StatementPhase.Barrier => "ბარიერის ფაზა",
        StatementPhase.Crossing => "ბარიერის შევსების თვე",
        _ => "50/50 განაწილება",
    };

    private static string StatementStatusLabel(MonthlyStatementStatus s) => s switch
    {
        MonthlyStatementStatus.Completed => "ანგარიშსწორება დასრულებულია",
        MonthlyStatementStatus.Failed => "ზოგიერთი გადარიცხვა ვერ შესრულდა — მეორდება ყოველ ღამე",
        _ => "ანგარიშსწორება მიმდინარეობს",
    };

    private static string CategoryLabel(CostCategory c) => c switch
    {
        CostCategory.Bank => "საბანკო მომსახურება",
        CostCategory.Sms => "SMS",
        CostCategory.PaymentProvider => "საგადახდო პროვაიდერი",
        CostCategory.SupportComms => "მხარდაჭერის კომუნიკაცია",
        CostCategory.Hosting => "ჰოსტინგი",
        CostCategory.Domain => "დომენი",
        _ => c.ToString(),
    };

    private static string ReportStatus(ReportParkLine p) => p.Status switch
    {
        SettlementStatus.Completed => p.PaidOn is { } d ? $"ჩარიცხულია {d.ToString("dd.MM", Inv)}" : "ჩარიცხულია",
        SettlementStatus.Failed => string.IsNullOrEmpty(p.FailureCode) ? "ვერ შესრულდა" : $"ვერ შესრულდა ({FailureLabel(p.FailureCode)})",
        SettlementStatus.SettledExternally => $"გადახდილია პლატფორმის გარეთ ({PayerLabel(p.ExternalPayer)}"
                                              + (p.PaidOn is { } on ? $", {on.ToString("dd.MM", Inv)})" : ")"),
        SettlementStatus.Processing => "მიმდინარეობს",
        _ => "მოლოდინში",
    };

    private static string PayerLabel(string? payer) => payer switch
    {
        "licensee" => "ლიცენზიატი",
        "park" => "ტაქსოპარკი",
        _ => payer ?? "—",
    };

    /// <summary>Nothing left to collect: transferred by the platform, or paid outside it.</summary>
    private static bool IsSettled(SettlementStatus s) => s is SettlementStatus.Completed or SettlementStatus.SettledExternally;

    private sealed record ReportParkLine(
        string ParkName, string LegalName, int Cashouts, decimal Fees, decimal SharePercent, decimal Amount,
        SettlementStatus Status, string? FailureCode, string? TransferId, DateOnly? PaidOn, string? ExternalPayer,
        SettlementMath.OverdueInfo? Overdue);

    private sealed record ReportModel(
        MonthlyStatement Statement,
        SettlementMath.StatementResult Math,
        string MonthTitle,
        DateOnly DueDate,
        string OwnerName,
        string OwnerTaxId,
        string OwnerIban,
        List<ReportParkLine> Parks,
        List<CommonCost> LicenseeCostRows,
        List<CommonCost> SwichCostRows,
        // A correction's original → the statement that included it.
        IReadOnlyDictionary<Guid, string?> CorrectedRefs,
        DateTime ComputedAtLocal,
        DateTime GeneratedAtLocal);
}

/// <summary>Wording of the licensee's monthly report that the tests pin down (Georgian, as the report is).</summary>
public static class LicenseeReportText
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// The "issued by" cell of a counted entry: the vendor and its note — or, for a correction, that it is one, the
    /// statement its original went into, and why it was made (the original itself is listed in that statement).
    /// </summary>
    public static string CostDescription(CommonCost c, string? correctedRef)
    {
        if (c.CorrectsId is not null)
            return $"{c.Vendor} — შესწორება (ორიგინალი: {correctedRef ?? "—"}): {c.Reason}";
        return string.IsNullOrWhiteSpace(c.Note) ? c.Vendor : $"{c.Vendor} — {c.Note}";
    }

    /// <summary>A figure inside a formula: a negative one in brackets.</summary>
    public static string Operand(decimal v) => v < 0 ? $"({v.ToString("N2", Inv)})" : v.ToString("N2", Inv);
}
