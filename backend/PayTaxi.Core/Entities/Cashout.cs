using PayTaxi.Core.Enums;

namespace PayTaxi.Core.Entities;

public class Cashout : BaseEntity
{
    public Guid DriverId { get; set; }
    public Guid ParkId { get; set; }
    public Guid BankCardId { get; set; }

    /// <summary>The park account the payout was routed from. Null until routing succeeded.</summary>
    public Guid? ParkBankAccountId { get; set; }

    /// <summary>The (monthly) settlement whose transfer covers this cashout's fee. Null until settled — a later month picks it up.</summary>
    public Guid? SettlementId { get; set; }

    // ── Fee regime (licence agreement §2.2), stamped when the cashout completes ──
    // Which arrangement the fee was EARNED under: the park's licensee at that moment, or Swich-direct. A park moved
    // between a licensee and Swich-direct keeps every fee it already earned under the arrangement it was earned under —
    // the monthly settlement reads this stamp, never the park's assignment on the run day.

    /// <summary>
    /// True once the regime below was recorded (at completion). False for cashouts completed before regimes were
    /// recorded: their fee follows the park's assignment at the time it is settled.
    /// </summary>
    public bool FeeRegimeStamped { get; set; }

    /// <summary>With <see cref="FeeRegimeStamped"/>: the licensee whose agreement the fee falls under; null = Swich-direct.</summary>
    public Guid? FeeLicenseeId { get; set; }

    /// <summary>Gross amount debited from the driver's Yandex balance.</summary>
    public decimal Amount { get; set; }

    /// <summary>Flat fee retained by the park. Driver receives Amount − Fee.</summary>
    public decimal Fee { get; set; }

    public CashoutStatus Status { get; set; } = CashoutStatus.Queued;
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();
    public string? BankTransferId { get; set; }
    public string? YandexTransactionId { get; set; }

    /// <summary>Yandex transaction that reversed the debit when the payout was abandoned.</summary>
    public string? YandexReversalTransactionId { get; set; }

    public string? FailureReason { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Who started this cashout: "driver:{id}", "admin", "admin-retry:{id}", "system".</summary>
    public string? InitiatedBy { get; set; }

    /// <summary>
    /// When this cashout was created by "Retry" on a Failed one, the id of that failed cashout.
    /// One live (non-Failed) retry per source — a second click must never pay twice.
    /// </summary>
    public Guid? RetryOfCashoutId { get; set; }

    // ── Payout queue bookkeeping ──────────────────────────────────────
    /// <summary>How many bank payout attempts have been made.</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the queue worker may next try the bank payout (null = not queued).</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// Payout attempts whose outcome stayed unknown (the bank call threw and the lookup by document id
    /// could not answer either) since the bank last answered about the current document. Only such an
    /// answer resets it — see <see cref="UnknownOutcomeDocument"/> for which answers count. Waiting for
    /// money or for a bank that is down never ends a cashout by itself (agreement §5.5, Annex 3 §5);
    /// only this count does, after <c>Cashout:MaxPayoutAttempts</c>, and then into ReviewRequired —
    /// never into a reversal, because the money may already have left.
    /// </summary>
    public int UnresolvedAttempts { get; set; }

    /// <summary>
    /// The bank document (our payment-order id, see <see cref="BankDocumentSeq"/>) that reached — or may
    /// have reached — the bank without the bank ever saying what became of it: the call threw and the
    /// lookup could not answer. While it is set the payout may already be paid, so nothing gives the
    /// money back on Yandex: Cancel refuses, and a permanent refusal goes to ReviewRequired instead of
    /// a reversal. Sticky on purpose: only an answer about THIS document clears it (the bank names the
    /// order — accepted, executed, refused — or the lookup finds it). Answers that never reach the
    /// bank's duplicate check say nothing about it: an expired password, a blocked user, credentials
    /// we cannot parse, a gateway that is down, "no money" at import. Those leave it set.
    /// </summary>
    public string? UnknownOutcomeDocument { get; set; }

    /// <summary>
    /// Which bank document (payment order) the payout is currently on: 0 = the cashout's own
    /// <see cref="IdempotencyKey"/>, n &gt; 0 = "{IdempotencyKey}#n". Bumped only after the bank
    /// accepted an order and then refused it for lack of funds — that order is final and nothing was
    /// paid on it, but a bank such as TBC rejects a second order under the same request id, so the
    /// retry after a top-up must go out as a fresh document. Never bumped on an unknown outcome.
    /// </summary>
    public int BankDocumentSeq { get; set; }

    /// <summary>
    /// Sequential invoice number, assigned when the cashout becomes Completed.
    /// Backed by a Postgres sequence so concurrent saga runs can't collide.
    /// Null until then.
    /// </summary>
    public long? InvoiceNumber { get; set; }

    public Driver Driver { get; set; } = default!;
    public Park Park { get; set; } = default!;
    public BankCard BankCard { get; set; } = default!;
    public ParkBankAccount? ParkBankAccount { get; set; }
    public Settlement? Settlement { get; set; }
    public ICollection<LedgerEntry> LedgerEntries { get; set; } = new List<LedgerEntry>();
}
