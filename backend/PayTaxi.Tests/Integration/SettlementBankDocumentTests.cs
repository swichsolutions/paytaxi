using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Enums;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Services;
using Xunit;

namespace PayTaxi.Tests.Integration;

/// <summary>
/// The monthly park → Swich transfer against a TBC-shaped bank. TBC keeps one payment order per
/// document id: a transfer it refused (the park's account was short) can never succeed under that id —
/// sending it again only returns the dead order. After the park tops up, the retry must go out as a new
/// document, exactly like a waiting cashout does.
/// </summary>
[Collection("api")]
public class SettlementBankDocumentTests
{
    private readonly ApiFixture _f;
    public SettlementBankDocumentTests(ApiFixture f) => _f = f;

    private static SettlementService Service(IServiceScope scope, IBankPayoutAdapter bank)
    {
        var banks = new ServiceCollection();
        foreach (var key in new[] { "mock", "MOCK", "tbc", "TBC" })
            banks.AddKeyedSingleton(key, bank);
        var sp = scope.ServiceProvider;
        return new SettlementService(
            sp.GetRequiredService<AppDbContext>(),
            banks.BuildServiceProvider(),
            sp.GetRequiredService<IOptions<SettlementOptions>>(),
            NullLogger<SettlementService>.Instance);
    }

    /// <summary>
    /// A failed transfer row for Batumi (a legacy-shaped row, so it cannot collide with the monthly tests' rows).
    /// <paramref name="interruptedAgo"/>: instead, a row left Processing with no bank id by a run that died that long ago.
    /// </summary>
    private Task<Guid> PlantAsync(TimeSpan? interruptedAgo = null) => _f.DbAsync(async db =>
    {
        var park = await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1");
        var s = new Settlement
        {
            ParkId = park.Id, IsMonthly = false,
            SettlementDate = new DateOnly(2024, 1, 31),
            PeriodFromUtc = new DateTime(2023, 12, 31, 20, 0, 0, DateTimeKind.Utc),
            PeriodToUtc = new DateTime(2024, 1, 31, 20, 0, 0, DateTimeKind.Utc),
            CashoutCount = 4, FeeTotal = 2m, SwichShare = 1.40m, ParkShare = 0.60m,
            Status = SettlementStatus.Failed, FailureReason = "TEST: planted",
            IdempotencyKey = "settle:doc-test:" + Guid.NewGuid().ToString("N"),
            Description = "PayTaxi settlement 2024-01, 4 tx, inv ref PT-2024-01", InvoiceRef = "PT-2024-01",
        };
        if (interruptedAgo is { } ago)
        {
            s.Status = SettlementStatus.Processing;
            s.FailureReason = null;
            s.AttemptCount = 1;
            s.LastAttemptAt = DateTime.UtcNow - ago;
        }
        db.Settlements.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    });

    /// <summary>An order the bank already holds under <paramref name="key"/> — as if our send reached it before the process died.</summary>
    private static async Task PlaceOrderAsync(ScriptedBank bank, string key)
    {
        var source = new BankAccountContext(Guid.NewGuid(), Guid.NewGuid(), "mock", "TB", "GE00TB0000000000000000", "Test park", "{}");
        await bank.SendPayoutAsync(new BankTransferRequest(key, source, "GE12TB7100000000000001", "Swich", 1.40m, "GEL", "test"));
        bank.Sent.Clear();
        bank.Requests.Clear();
    }

    private Task<Settlement> RowAsync(Guid id) =>
        _f.DbAsync(db => db.Settlements.AsNoTracking().FirstAsync(s => s.Id == id));

    private Task RemoveAsync(Guid id) => _f.DbAsync(async db =>
    {
        await db.LedgerEntries.Where(l => l.SettlementId == id).ExecuteDeleteAsync();
        return await db.Settlements.Where(s => s.Id == id).ExecuteDeleteAsync();
    });

    private async Task<Settlement> RetryAsync(IBankPayoutAdapter bank, Guid id)
    {
        using var scope = _f.Services.CreateScope();
        return await Service(scope, bank).RetryAsync(id, "test");
    }

    [Fact]
    public async Task A_transfer_refused_for_lack_of_funds_is_retried_as_a_new_document_after_the_top_up()
    {
        var id = await PlantAsync();
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;

            // The bank creates an order under our document id and refuses it: the park's account is short.
            var bank = new ScriptedBank { Funded = false };
            var refused = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Failed, refused.Status);
            Assert.StartsWith(BankErrorCodes.InsufficientFunds, refused.FailureReason);

            // Topped up: the same id would only fetch the dead order again, so the retry is a fresh document.
            bank.Funded = true;
            var paid = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Completed, paid.Status);
            Assert.Equal(new[] { key, $"{key}#1" }, bank.Sent.Distinct().ToArray());
            Assert.Equal(bank.OrderIdFor($"{key}#1"), (await RowAsync(id)).BankTransferId);
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task A_transfer_accepted_and_then_refused_on_the_status_poll_is_retried_as_a_new_document()
    {
        var id = await PlantAsync();
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;

            // TBC's asynchronous shape: accepted now, refused for lack of funds when it executes.
            var bank = new ScriptedBank { Funded = false, Async = true };
            var accepted = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Processing, accepted.Status);
            var refused = await RetryAsync(bank, id);                     // the status poll
            Assert.Equal(SettlementStatus.Failed, refused.Status);
            Assert.StartsWith(BankErrorCodes.InsufficientFunds, refused.FailureReason);

            bank.Funded = true;
            var resent = await RetryAsync(bank, id);                       // new document, accepted
            Assert.Equal(SettlementStatus.Processing, resent.Status);
            var done = await RetryAsync(bank, id);                         // its poll: executed
            Assert.Equal(SettlementStatus.Completed, done.Status);
            Assert.Equal(new[] { key, $"{key}#1" }, bank.Sent.Distinct().ToArray());
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task An_unanswered_send_is_never_re_sent_as_a_new_document()
    {
        // The money may have left: the retry must look the SAME document up again, not open a second one.
        var id = await PlantAsync();
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;
            var bank = new ScriptedBank { Unreachable = true };
            var unknown = await RetryAsync(bank, id);                      // send and lookup both throw
            Assert.Equal(SettlementStatus.Failed, unknown.Status);
            Assert.StartsWith("BANK_EXCEPTION", unknown.FailureReason);

            // Back online: the retry repeats the same document id (the bank de-duplicates on it), never "#1".
            bank.Unreachable = false;
            bank.Funded = true;
            var done = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Completed, done.Status);
            Assert.All(bank.Sent, k => Assert.Equal(key, k));
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task An_interrupted_transfer_whose_bank_status_is_unknown_keeps_its_document()
    {
        // The run died mid-send; the bank holds our order but reports a status the adapter cannot map.
        // That order may still execute — a new document now could make the park pay twice.
        var id = await PlantAsync(interruptedAgo: TimeSpan.FromMinutes(30));
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;
            var bank = new ScriptedBank { Async = true };
            await PlaceOrderAsync(bank, key);
            bank.Reports = BankTransferStatus.Unknown;

            var recovered = await RetryAsync(bank, id);                    // lookup by document id: Unknown
            Assert.Equal(SettlementStatus.Processing, recovered.Status);
            Assert.Equal(0, recovered.BankDocumentSeq);
            Assert.Equal(bank.OrderIdFor(key), recovered.BankTransferId);   // adopted: polled from now on

            var stillUnknown = await RetryAsync(bank, id);                 // the poll: still Unknown → still waiting
            Assert.Equal(SettlementStatus.Processing, stillUnknown.Status);

            bank.Reports = null;                                           // the order executes after all
            bank.Funded = true;
            var done = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Completed, done.Status);
            Assert.Equal(bank.OrderIdFor(key), done.BankTransferId);
            Assert.Empty(bank.Sent);                                       // never re-sent, never "#1"
            Assert.Null(bank.OrderIdFor($"{key}#1"));
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task After_an_unanswered_send_the_retry_asks_the_bank_instead_of_trusting_a_balance_the_transfer_itself_emptied()
    {
        // The first send executed but its answer was lost (and the lookup failed): BANK_EXCEPTION. The money left
        // the park's account — so the balance now reads short. Refusing on that balance would leave a paid row
        // Failed (and blame the park for it); repeating the same document lets the bank's de-duplication answer.
        var id = await PlantAsync();
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;
            var bank = new ScriptedBank { Funded = true, LoseResponse = true, LookupFails = true };
            var unknown = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Failed, unknown.Status);
            Assert.StartsWith("BANK_EXCEPTION", unknown.FailureReason);

            bank.LoseResponse = false;
            bank.LookupFails = false;
            bank.Balance = 0m;
            var done = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Completed, done.Status);
            Assert.Equal(bank.OrderIdFor(key), done.BankTransferId);
            Assert.All(bank.Sent, k => Assert.Equal(key, k));
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task A_payment_outside_the_platform_is_refused_while_the_bank_may_still_execute_the_transfer()
    {
        // The park says it paid by hand; but its bank holds a live order for the same transfer. Closing the row
        // now would let that order execute too — Swich paid twice. Only a dead order (or none) may be closed by hand.
        var id = await PlantAsync();
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;
            var bank = new ScriptedBank { Async = true };
            await PlaceOrderAsync(bank, key);
            var payment = new ExternalPayment("park", 1.40m, new DateOnly(2024, 2, 10), "PARK-2024-01", "Paid by hand from the park's account");

            async Task<string> RefusedAsync()
            {
                using var scope = _f.Services.CreateScope();
                var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => Service(scope, bank).SettleExternallyAsync(id, payment, "admin:test"));
                return ex.Code;
            }

            Assert.Equal(SettlementRejectionCodes.TransferAtBank, await RefusedAsync());     // accepted, executing
            bank.Reports = BankTransferStatus.Unknown;
            Assert.Equal(SettlementRejectionCodes.TransferAtBank, await RefusedAsync());     // a status nobody can read
            bank.Reports = null;
            bank.LookupFails = true;
            Assert.Equal(SettlementRejectionCodes.BankLookupFailed, await RefusedAsync());   // the bank cannot be asked
            Assert.Equal(SettlementStatus.Failed, (await RowAsync(id)).Status);

            // The bank refused the order for good: nothing can move any more — the licensee's payment closes it.
            bank.LookupFails = false;
            bank.Reports = BankTransferStatus.Failed;
            using (var scope = _f.Services.CreateScope())
            {
                var closed = await Service(scope, bank).SettleExternallyAsync(id, payment, "admin:test");
                Assert.Equal(SettlementStatus.SettledExternally, closed.Status);
                Assert.Equal("park", closed.ExternalPayer);
                Assert.Equal(new DateOnly(2024, 2, 10), closed.ExternalPaidOn);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => RetryAsync(bank, id));
            Assert.Empty(bank.Sent);
            var booked = await _f.DbAsync(db => db.LedgerEntries.AsNoTracking()
                .Where(l => l.SettlementId == id && l.EntryType == LedgerEntryType.SettlementPaidExternally).ToListAsync());
            Assert.Equal(1.40m, Assert.Single(booked).Amount);
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task A_transfer_attempt_made_while_the_outside_payment_is_being_checked_makes_the_record_fail()
    {
        // The bank is asked first and has nothing under the document. Before the row is closed, the Retry button or the
        // nightly tick claims it, sends that document, loses the answer and puts the row back to Failed under the SAME
        // document id. Closing it now would record the debt as paid while the document may still execute from the
        // park's account — Swich paid twice. The close must see that an attempt ran and refuse (409 concurrent_change).
        var id = await PlantAsync();
        try
        {
            var key = (await RowAsync(id)).IdempotencyKey;
            var bank = new ScriptedBank { Async = true };
            Settlement? meanwhile = null;
            bank.AfterLookup = async _ =>
            {
                bank.LoseResponse = true;
                bank.LookupFails = true;
                meanwhile = await RetryAsync(bank, id);
                bank.LoseResponse = false;
                bank.LookupFails = false;
            };
            var payment = new ExternalPayment("park", 1.40m, new DateOnly(2024, 2, 10), "PARK-2024-01", null);

            using (var scope = _f.Services.CreateScope())
            {
                var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => Service(scope, bank).SettleExternallyAsync(id, payment, "admin:test"));
                Assert.Equal(SettlementRejectionCodes.ConcurrentChange, ex.Code);
            }
            Assert.NotNull(meanwhile);
            Assert.StartsWith("BANK_EXCEPTION", meanwhile!.FailureReason);

            var row = await RowAsync(id);
            Assert.Equal(SettlementStatus.Failed, row.Status);
            Assert.Equal(0, row.BankDocumentSeq);                 // the document the bank may still execute
            Assert.Equal(new[] { key }, bank.Sent.ToArray());
            Assert.False(await _f.DbAsync(db => db.LedgerEntries.AnyAsync(l => l.SettlementId == id && l.EntryType == LedgerEntryType.SettlementPaidExternally)));

            // Asked again, the bank now shows that document alive: still refused, for that reason.
            using (var scope = _f.Services.CreateScope())
            {
                var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => Service(scope, bank).SettleExternallyAsync(id, payment, "admin:test"));
                Assert.Equal(SettlementRejectionCodes.TransferAtBank, ex.Code);
            }
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task A_debt_failing_on_a_bank_the_platform_cannot_talk_to_yet_can_be_recorded_as_paid_outside()
    {
        // A park whose settlement account is at BoG (adapter still a stub) fails every night with BANK_ADAPTER_MISSING:
        // nothing ever reached that bank, so nothing there can still execute. Its debt must be recordable as paid by
        // hand — the stub answering "not implemented" is not "the bank could not be asked" (the cashout cancel treats
        // it the same way).
        var id = await PlantAsync();
        try
        {
            var bog = new PayTaxi.Infrastructure.Adapters.Banks.BogPayoutAdapter();
            var failed = await RetryAsync(bog, id);
            Assert.Equal(SettlementStatus.Failed, failed.Status);
            Assert.StartsWith("BANK_ADAPTER_MISSING", failed.FailureReason);

            using var scope = _f.Services.CreateScope();
            var closed = await Service(scope, bog).SettleExternallyAsync(id,
                new ExternalPayment("park", 1.40m, new DateOnly(2024, 2, 10), "PARK-BOG-2024-01", null), "admin:test");
            Assert.Equal(SettlementStatus.SettledExternally, closed.Status);
        }
        finally { await RemoveAsync(id); }

        // But when an earlier attempt's outcome is unknown, that document went out through a bank that could execute it
        // (the account was switched since): the stub cannot vouch for it, so the record is refused.
        var other = await PlantAsync();
        try
        {
            await _f.DbAsync(async db =>
            {
                var s = await db.Settlements.FirstAsync(x => x.Id == other);
                db.LedgerEntries.Add(new LedgerEntry
                {
                    SettlementId = other, ParkId = s.ParkId, EntryType = LedgerEntryType.SettlementFailed, Amount = s.SwichShare,
                    Reference = "BANK_EXCEPTION", Notes = "TEST: the answer was lost",
                });
                return await db.SaveChangesAsync();
            });
            var bog = new PayTaxi.Infrastructure.Adapters.Banks.BogPayoutAdapter();
            using var scope = _f.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => Service(scope, bog).SettleExternallyAsync(other,
                new ExternalPayment("park", 1.40m, new DateOnly(2024, 2, 10), "PARK-BOG-2024-01", null), "admin:test"));
            Assert.Equal(SettlementRejectionCodes.BankLookupFailed, ex.Code);
            Assert.Equal(SettlementStatus.Failed, (await RowAsync(other)).Status);
        }
        finally { await RemoveAsync(other); }
    }

    [Fact]
    public async Task Only_the_park_can_have_paid_a_debt_outside_the_licence_agreement()
    {
        // A row with no licensee statement (a Swich-direct park, or a legacy nightly row) is outside the licence
        // agreement: §2.5 — the licensee answering for a park's debt — does not apply to it.
        var id = await PlantAsync();
        try
        {
            using var scope = _f.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => Service(scope, new ScriptedBank()).SettleExternallyAsync(id,
                new ExternalPayment("licensee", 1.40m, new DateOnly(2024, 2, 10), "LIC-2024-01", null), "admin:test"));
            Assert.Equal(SettlementRejectionCodes.InvalidPayer, ex.Code);
            Assert.Equal(SettlementStatus.Failed, (await RowAsync(id)).Status);
        }
        finally { await RemoveAsync(id); }
    }

    [Fact]
    public async Task A_revoked_parks_share_that_never_reached_the_bank_is_closed_even_when_its_bank_no_longer_answers()
    {
        // Annex 3 §7: a park that revoked the platform's authorisation may cut its bank access too, and then no lookup by
        // document id can be made. A share whose transfer never reached the bank — stopped by the revocation, or before it by
        // a check that sends nothing — has no order there that could still execute: its outside payment is recorded. One
        // whose earlier attempt may have reached the bank still waits until the bank can be asked (Swich paid twice otherwise).
        var neverSent = await PlantAsync();
        var maybeSent = await PlantAsync();
        var parkId = await _f.DbAsync(async db => (await db.Parks.AsNoTracking().FirstAsync(p => p.Slug == "batumi-auto-park-1")).Id);
        var today = SettlementService.TodayLocal();
        try
        {
            // An earlier attempt of the second one ended without the bank's answer — its order may be alive at the bank.
            await _f.DbAsync(async db =>
            {
                db.LedgerEntries.Add(new LedgerEntry { SettlementId = maybeSent, ParkId = parkId, EntryType = LedgerEntryType.SettlementFailed,
                    Amount = 1.40m, Reference = "BANK_EXCEPTION", Notes = "test: earlier attempt, no answer" });
                return await db.SaveChangesAsync();
            });
            await _f.DbAsync(db => db.Parks.Where(p => p.Id == parkId).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.AuthorisationNoticeOn, (DateOnly?)today.AddDays(-30))
                .SetProperty(p => p.AuthorisationRevokedFrom, (DateOnly?)today)
                .SetProperty(p => p.AuthorisationRevocationReason, "test: written notice")
                .SetProperty(p => p.AuthorisationRevocationRecordedBy, "admin:test")));

            var bank = new ScriptedBank { Funded = true, LookupFails = true };
            foreach (var id in new[] { neverSent, maybeSent })
            {
                var stopped = await RetryAsync(bank, id);
                Assert.StartsWith("AUTHORISATION_REVOKED:", stopped.FailureReason);
            }
            Assert.Empty(bank.Sent);

            var payment = new ExternalPayment("park", 1.40m, today, "PARK-BY-HAND", null);
            using (var scope = _f.Services.CreateScope())
            {
                var closed = await Service(scope, bank).SettleExternallyAsync(neverSent, payment, "admin:test");
                Assert.Equal(SettlementStatus.SettledExternally, closed.Status);
            }
            using (var scope = _f.Services.CreateScope())
            {
                var ex = await Assert.ThrowsAsync<SettlementRejectedException>(() => Service(scope, bank).SettleExternallyAsync(maybeSent, payment, "admin:test"));
                Assert.Equal(SettlementRejectionCodes.BankLookupFailed, ex.Code);
            }
            Assert.Equal(SettlementStatus.Failed, (await RowAsync(maybeSent)).Status);
        }
        finally
        {
            await _f.DbAsync(db => db.Parks.Where(p => p.Id == parkId).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.AuthorisationNoticeOn, (DateOnly?)null)
                .SetProperty(p => p.AuthorisationRevokedFrom, (DateOnly?)null)
                .SetProperty(p => p.AuthorisationRevocationReason, (string?)null)
                .SetProperty(p => p.AuthorisationRevocationRecordedBy, (string?)null)));
            await RemoveAsync(neverSent);
            await RemoveAsync(maybeSent);
        }
    }

    [Fact]
    public async Task The_transfer_to_Swich_names_the_registered_account_holder()
    {
        // Annex 3 §3: the account is held by შპს სვიჩსოლუშენსი — the beneficiary name on every park's order
        // and the holder printed on the invoice must be that registered name, not an English trading name.
        var id = await PlantAsync();
        try
        {
            var bank = new ScriptedBank { Funded = true };
            var done = await RetryAsync(bank, id);
            Assert.Equal(SettlementStatus.Completed, done.Status);
            Assert.Equal("შპს სვიჩსოლუშენსი", Assert.Single(bank.Requests).DestinationName);
            Assert.Equal("შპს სვიჩსოლუშენსი", new SettlementOptions().SwichHolderName);
        }
        finally { await RemoveAsync(id); }
    }
}
