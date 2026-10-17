# Points for the lawyer — licence agreement and annexes

Open questions where the platform had to read the licence agreement (Swich = platform owner, licensee = operator)
or its annexes in one particular way. Clause numbers refer to the Georgian draft. Each point says what the platform
does today and what we would like the text to say. Numbers in [brackets] are still bracketed in the draft and are
settings in the platform (`Incidents:*`, `Settlement:*`), not code.

## A. Outages: what waits and what is refused

1. **Annex 1 §3.2 and agreement §5.5 — Yandex outages are refused, bank and park-funds outages wait.**
   The text says that during any external outage (bank API, Yandex Fleet, park balance) the platform queues cashout
   requests and executes them once the service is back. The platform cannot do that for Yandex: a cashout starts by
   reading the driver's Yandex balance, and without it nothing may be debited. So while Yandex Fleet does not answer,
   a request is **refused** (nothing is debited, nothing is kept to send later; the driver is told to try again). Only a
   payout already debited on Yandex **waits** (bank down, park account short, no payout account) and goes out by itself
   once the cause is gone. Proposed: §3.2 to distinguish the two — "requests the platform has already accepted are queued
   and executed automatically; while Yandex Fleet is unavailable, new requests are not accepted".

## B. Incidents and the SLA (Annex 1)

2. **Annex 1 §2 — business hours.** "Business hours / days" are not defined. The platform assumes Mon–Fri 09:00–18:00
   Tbilisi time, with no public holidays until the parties list which count. Please define them (and the holidays).

3. **Annex 1 §3.1 — the park's side counts as external.** §3.1 lists "insufficient balance on the park's account"
   among causes outside the owner's control. The platform treats every park-side cause the same way (park short of
   money, no payout account, the park's revoked authorisation): not the owner's fault, resolution clock paused until the
   park fixes it. Please confirm, or list the park-side causes.

4. **Annex 1 §2.1 — what "all or most drivers" means for P1.** The monitor makes a park's queue incident a P1 when at
   least [3] drivers wait and they are more than half of those who tried since the oldest waiting payout; otherwise P2.
   Please confirm this reading (or give the threshold). A person (Swich) can always set the level; the monitor then
   leaves it alone.

5. **Annex 1 §2 and §5–§6 — P1 clocks after an escalation.** An incident often starts as a P2 and is raised to P1 later
   (by the monitor or by Swich). The platform counts the P1 response and resolution targets, the month it belongs to in
   §5, and the [72]-hour count of §6(a) from the moment it **became** P1 — not from the original report, which would show
   a P2 raised at hour 60 as an "unanswered P1 for 60 hours". A response given before the raise still counts. If an
   incident is lowered and raised again, the clocks count from the **first** time it was a P1 (a bounce cannot buy time).
   For §5(b) hours, a P1 from its report counts from its impact start; a raised one from the raise. Please confirm and
   add a sentence to §2.

6. **Annex 1 §2.1 / §5 — one cause at several parks is one P1.** One bank (or the platform's missing link to one bank)
   failing at several parks would otherwise be one P2 per park and never a P1, though drivers fail platform-wide. The
   platform opens ONE platform-wide P1 once the same external or platform cause holds payouts at [2] or more parks; if one
   of those parks already had an incident, that incident carries on as the platform-wide one (its report, response and P1
   clocks kept) and the others are closed as its duplicates. Park-side causes never aggregate (two parks short of money
   are two facts). Yandex not answering for [2] or more parks is also one platform-wide P1. Please confirm this reading
   of "all or most drivers" across parks, and whether a merged duplicate counts on its own in §5–§7 (today: it counts as
   resolved when merged; the platform-wide incident carries the rest).

7. **Annex 1 §2.2 — "response".** The platform records the response as a person's acknowledgement (never automatic). An
   incident fixed before anyone acknowledged it counts as responded at its resolution. §6(a) counts every P1 whatever the
   fault, because §3.1 still obliges the owner to respond to external incidents. Please confirm both readings.

8. **Annex 1 §4 — who sees the log.** §4.1 makes the log available to the licensee. The platform also shows each park's
   manager their park's incidents and the platform-wide ones — without the cause, without staff names, and only the part
   of the timeline written while the incident was visible to their park (an incident moved to all parks needs new
   neutral text first). Please confirm that parks may see this subset (confidentiality, agreement §6).

9. **Annex 1 §7 / agreement 8.5.1 — trial-period release with the fault undetermined.** Only Swich sets the fault. A P1
   that missed its target with the fault not yet determined is shown apart ("could qualify"). Please say who decides the
   fault in a dispute and by when, since the 10,000 GEL release may depend on it.

## C. Monthly settlement (agreement §2)

10. **§2.3 — the barrier counts entitlement, not receipts.** The text counts amounts the owner "actually received". The
    platform counts the owner's monthly **entitlement** towards the 20,000 GEL, whether or not a park's transfer has
    landed yet (an unpaid amount is a debt, collected later, with the §4.1.2 responsibility and the 0.1 % penalty). Counting
    receipts would let the phase of a month depend on a bank delay. Please confirm or change the wording to "accrued".

11. **§2.5 — pro rata by fees, not by "turnover".** The text takes each park's part "in proportion to its turnover". The
    platform allocates by each park's **fees** in the month (the income being shared), not by cashout volume. Please change
    "turnover" to "fees accrued at the park".

12. **§2.4 / §2.5 — a park may owe more than its own fees.** In the normal phase the owner's share is
    (fees − licensee costs + Swich costs) ÷ 2. When Swich paid large shared costs that month, this can exceed the fees,
    and the pro-rata part of a park then exceeds what that park earned the platform. Does the park's authorisation
    (Annex 3 §3) cover that, or should a park's transfer be capped at its fees, with the rest owed by the licensee (§4.1.2)?

13. **§2.4 — shared-cost records.** Not in the text, decided for the platform: a cost counts in a statement only
    [48] hours after it was entered (an edit restarts the window, a note does not); the other side may hold a cost with a
    reason, and only the holder releases it; each side edits or deletes only its own costs, a deletion keeps the record
    with its reason; an invoice already counted is never changed — its owner adds a signed correction that the next
    statement counts (an invoice and its corrections never go below zero; a side's month total may be negative). Please
    put these rules in §2.4 or an annex.

14. **§2.5 — the park's invoice and confidentiality.** A park's monthly invoice shows its own fees and its own part of the
    transfer. From those two figures a park can still work out the ratio of the owner's share to the licensee's total
    fees (it no longer sees the percentage, the phase or any total). This is inherent in "pro rata": please confirm the
    parks may see their own part, or say what the invoice may show.

15. **§2.5 / §4.1.2 — payments made outside the platform.** When the automatic transfer cannot be made (park account
    short, authorisation revoked, a bank the platform cannot reach), Swich records that the amount was paid outside the
    platform — by the park or by the licensee — with a reference and date; the record closes the debt and stops the
    penalty. A licensee payment can be recorded only for a park under the agreement. Please give these records a basis in
    §2.5 / §4.1.2 (what evidence counts, from which date the penalty stops).

16. **§2.2 — which regime a fee belongs to.** Each fee is stamped, when the cashout completes, with the arrangement in
    force at that moment (the licensee's park, or a park Swich connected directly — outside the agreement). Moving a park
    later does not move fees already earned; older unstamped fees follow the park's assignment on the settlement day.
    Please confirm this is how "parks connected directly by the owner are not governed by the agreement" should apply to
    a park that changes sides mid-month.

17. **8.5.1 — the launch date.** The platform lets the launch date be set or moved only to today or later, with a reason,
    and locks it once it has passed (every change is logged). Please say how the parties fix the launch date (written
    notice?), because the trial and the barrier both run from it.

## D. The parks' authorisations (Annex 3)

18. **Annex 3 §7 — revocation.** The platform records a revocation only with at least [30] days between the notice and
    the effective date, an effective date not in the past, and one at a time; it can be withdrawn until it takes effect. From the effective date: new cashout
    requests of that park are refused up front; a payout already debited on Yandex but not yet at the bank is **not sent and
    not refunded automatically** — it waits until a person cancels it (the amount then goes back to the driver's Yandex
    balance); the monthly transfer from that park is no longer attempted, so the owner's part becomes a debt to settle
    outside the platform (point 15). Please confirm, in particular who decides about debited payouts and whether
    "obligations arising before the revocation remain in force" covers the monthly share for fees earned before it.

## E. Data protection

19. **Annex 3 §6 — the operator's role.** Annex 3 §6 makes the park the controller and the owner the processor of
    drivers' data. The licensee (operator) is not named, yet its staff see drivers, cashouts and incidents of all its parks
    in the console. Please define the licensee's role (sub-processor of the owner, processor of each park, or a joint
    arrangement) and the agreement it needs.

20. **Retention, as the privacy policy will state it.** The platform deletes sign-in codes after 24 h, unused devices
    after 90 days and the external-call log and the sign-in history after 2 years; it keeps every financial record (and
    the payout accounts linked to them) without an automatic end date, and does not yet depersonalise drivers who stopped
    driving. Please give the retention period for financial records and the rule for suspended drivers, so the policy and
    the platform say the same.

21. **Park identity.** A park can only become active with its legal name, identification code and phone recorded,
    because the driver's privacy notice names the park as controller through "Profile → Your park". Please confirm this is
    enough to identify the controller.
