using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Infrastructure.Services;
using Xunit;
using static PayTaxi.Infrastructure.Services.SettlementMath;

namespace PayTaxi.Tests.Domain;

/// <summary>
/// The shared-cost rules a statement applies before it counts an invoice (licence agreement §2.4, §2.6): the
/// 48-hour window after an entry, holds, soft deletes, and corrections that may take a side's month total below
/// zero. Pure — the integration tests drive the same rules through the API.
/// </summary>
public class SharedCostRulesTests
{
    private static readonly TimeZoneInfo Tbilisi = SettlementService.ResolveTimeZone("Georgian Standard Time");

    /// <summary>Tbilisi wall-clock time → UTC (Georgia is UTC+4, no DST).</summary>
    private static DateTime Local(int y, int m, int d, int h, int min) => new DateTime(y, m, d, h, min, 0, DateTimeKind.Utc).AddHours(-4);

    private static CommonCost Cost(DateTime enteredAtUtc) => new()
    {
        PaidBy = CostPaidBy.Licensee, Category = CostCategory.Sms, Vendor = "SMSOffice", InvoiceNumber = "1",
        InvoiceDate = new DateOnly(2026, 10, 30), Amount = 10m, CreatedBy = "test", EnteredAt = enteredAtUtc,
    };

    [Fact]
    public void The_next_statement_is_computed_on_the_run_day_at_the_run_time_Tbilisi()
    {
        Assert.Equal(Local(2026, 11, 3, 0, 30), SharedCostRules.NextStatementRunUtc(Local(2026, 11, 2, 12, 0), 3, "00:30", Tbilisi));
        Assert.Equal(Local(2026, 11, 3, 0, 30), SharedCostRules.NextStatementRunUtc(Local(2026, 11, 3, 0, 29), 3, "00:30", Tbilisi));
        // Once it has run, the next one is a month later (December has 31 days; the 3rd is the 3rd).
        Assert.Equal(Local(2026, 12, 3, 0, 30), SharedCostRules.NextStatementRunUtc(Local(2026, 11, 3, 0, 31), 3, "00:30", Tbilisi));
        Assert.Equal(Local(2027, 1, 3, 0, 30), SharedCostRules.NextStatementRunUtc(Local(2026, 12, 20, 9, 0), 3, "00:30", Tbilisi));
        // Same clamp as the worker: a run day past the 28th is the 28th; a broken time is 00:30.
        Assert.Equal(Local(2027, 2, 28, 0, 30), SharedCostRules.NextStatementRunUtc(Local(2027, 2, 1, 9, 0), 31, "nonsense", Tbilisi));
    }

    [Fact]
    public void A_cost_is_counted_only_once_the_window_since_its_entry_has_passed()
    {
        var run = Local(2026, 11, 3, 0, 30);
        // Entered two days and a minute before the run: counted. Entered on the 1st at 00:31: misses it by a minute.
        Assert.True(SharedCostRules.Countable(Cost(run.AddHours(-48)), run, 48));
        Assert.False(SharedCostRules.Countable(Cost(run.AddHours(-48).AddMinutes(1)), run, 48));
        Assert.Equal(run.AddMinutes(1), SharedCostRules.EligibleFrom(run.AddHours(-48).AddMinutes(1), 48));
        // No window configured: counted as soon as it is entered.
        Assert.True(SharedCostRules.Countable(Cost(run), run, 0));
        // A negative setting is no window, never a window into the future.
        Assert.True(SharedCostRules.Countable(Cost(run), run, -5));
    }

    [Fact]
    public void Held_deleted_and_included_costs_are_never_counted_again()
    {
        var run = Local(2026, 11, 3, 0, 30);
        var held = Cost(run.AddDays(-5));
        held.HeldAt = run.AddDays(-1); held.HeldBy = "admin:ops@swich.dev"; held.HoldReason = "No invoice copy";
        var deleted = Cost(run.AddDays(-5));
        deleted.DeletedAt = run.AddDays(-1); deleted.DeletedBy = "admin:levan@operator.local"; deleted.DeleteReason = "Entered twice";
        var included = Cost(run.AddDays(-40));
        included.StatementId = Guid.NewGuid();

        Assert.False(SharedCostRules.Countable(held, run, 48));
        Assert.False(SharedCostRules.Countable(deleted, run, 48));
        Assert.False(SharedCostRules.Countable(included, run, 48));
        Assert.Equal("held", SharedCostRules.State(held, run, 48));
        Assert.Equal("deleted", SharedCostRules.State(deleted, run, 48));
        Assert.Equal("included", SharedCostRules.State(included, run, 48));
        Assert.Equal("waiting", SharedCostRules.State(Cost(run.AddHours(-1)), run, 48));
        Assert.Equal("pending", SharedCostRules.State(Cost(run.AddHours(-49)), run, 48));
    }

    [Fact]
    public void A_cost_entered_the_day_before_the_run_says_it_misses_it()
    {
        var nextRun = SharedCostRules.NextStatementRunUtc(Local(2026, 11, 2, 12, 0), 3, "00:30", Tbilisi);
        Assert.True(SharedCostRules.MissesRun(Cost(Local(2026, 11, 2, 12, 0)), nextRun, 48));
        Assert.False(SharedCostRules.MissesRun(Cost(Local(2026, 10, 31, 12, 0)), nextRun, 48));
        var held = Cost(Local(2026, 10, 20, 12, 0));
        held.HeldAt = Local(2026, 10, 25, 9, 0); held.HeldBy = "x"; held.HoldReason = "y";
        Assert.False(SharedCostRules.MissesRun(held, nextRun, 48));   // a hold is shown as a hold, not as late
    }

    [Fact]
    public void The_licensee_report_names_a_correction_and_brackets_a_negative_side_total()
    {
        // The report lists each counted entry; a correction must read as one, with the statement its original went into
        // and why — and a negative CL inside "F − CL" must not read as "100.00 − -166.50".
        var correction = Cost(DateTime.UtcNow);
        correction.Vendor = "TBC Bank"; correction.CorrectsId = Guid.NewGuid(); correction.Amount = -166.50m;
        correction.Reason = "Typo: the invoice is 18.50";
        Assert.Equal("TBC Bank — შესწორება (ორიგინალი: PT-2020-06): Typo: the invoice is 18.50",
            LicenseeReportText.CostDescription(correction, "PT-2020-06"));
        var invoice = Cost(DateTime.UtcNow);
        invoice.Note = "80 SMS";
        Assert.Equal("SMSOffice — 80 SMS", LicenseeReportText.CostDescription(invoice, null));
        invoice.Note = null;
        Assert.Equal("SMSOffice", LicenseeReportText.CostDescription(invoice, null));
        Assert.Equal("(-166.50)", LicenseeReportText.Operand(-166.50m));
        Assert.Equal("1,234.50", LicenseeReportText.Operand(1234.50m));
    }

    [Fact]
    public void A_correction_below_a_sides_month_costs_applies_the_formulas_as_written()
    {
        // The operator typed 185.00 for an 18.50 invoice; the statement that included it is settled. Its correction
        // of −166.50 is the only licensee entry next month, so CL = −166.50. The formulas are linear in CL and CS, so a
        // negative side total reverses exactly what the original took off at that month's phase:
        //   barrier: E = F − CL = 100.00 + 166.50 = 266.50 (Swich gets back the 166.50 the typo cut from it);
        //   normal:  E = 50 % × (F − CL − CS) + CS = 50 % × (100.00 + 166.50) = 133.25 (its half of it).
        Assert.Equal(266.50m, Compute(new StatementInputs(100m, -166.50m, 0m, 0m, 20_000m, 0m, 50m)).Entitlement);
        Assert.Equal(133.25m, Compute(new StatementInputs(100m, -166.50m, 0m, 0m, 20_000m, 25_000m, 50m)).Entitlement);
        // Swich over-claimed 20.00 of hosting: CS = −20.00 lowers its reimbursement by the licensee's half.
        Assert.Equal(40.00m, Compute(new StatementInputs(100m, 0m, -20m, 0m, 20_000m, 25_000m, 50m)).Entitlement);
        // With no fee income the reversal still lands — as a debt carried to the next month, never a negative transfer.
        var noIncome = Compute(new StatementInputs(0m, -166.50m, 0m, 0m, 20_000m, 0m, 50m));
        Assert.Equal(0m, noIncome.Due);
        Assert.Equal(166.50m, noIncome.CarryOut);
    }
}
