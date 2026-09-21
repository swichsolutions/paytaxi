# TBC branch visit — question list (PayTaxi × Levan's park)

> Take this to the branch with Levan. Every question has the Georgian wording to read aloud, why we ask,
> and what we need to hear back. Write the answer in the box. **Anything that affects money must be in
> writing** (e-mail from the TBC representative is enough) — the waiver above all.
>
> Context for the bank: the park pays its own drivers from its own TBC business account through the
> **Business Integration Service (DBI, "non-standard" package)**, ~70 drivers, up to ~600 transfers per day
> at peak, TBC→TBC only at launch, plus one aggregated transfer per night from the park's account to
> Swich Solutions LLC's TBC account. Technical contact: Beka (Swich Solutions).

Legend: **[BLOCKER]** = we cannot go live without it · **[CODE]** = the answer changes our implementation ·
**[PAPER]** = must be in writing.

---

## A. The one question that matters most

### 1. Does the transfer check the beneficiary name against the account holder? [BLOCKER-ish] [CODE]
**KA:** გადარიცხვისას ამოწმებს თუ არა ბანკი მიმღების სახელს ანგარიშის მფლობელის სახელთან? თუ სახელი არ ემთხვევა IBAN-ის მფლობელს, გადარიცხვა უარყოფილია, სრულდება მაინც, თუ ჩერდება შემოწმებაზე?
**Why:** drivers may only add accounts in their own name; we match the typed name to the park's roster, but the
bank is the only party that knows who owns the IBAN. If TBC rejects a mismatch, the ownership rule is enforced
for real. If TBC ignores the name (most likely), our rule stays documentation plus the park's third-party flag.
**Need:** one of: *rejected* / *executed regardless* / *held for manual review* — and whether this differs for
TBC→TBC vs other-bank transfers.
**Answer:** ______________________________________________

---

## B. Getting the service switched on

### 2. The waiver — no 1% volume fee — in writing. [BLOCKER] [PAPER]
**KA:** გთხოვთ წერილობით დაგვიდასტუროთ შეთანხმებული პირობები: რეგისტრაცია 20 ლარი, Digipass 85 ლარი, 100 ლარი კვარტალში, და **ბრუნვის 1%-იანი საკომისიო არ მოქმედებს**. რა ზღვარზე (რამდენი გადარიცხვა დღეში) გადაიხედება პირობები და როგორ გვეცნობება ეს?
**Why:** the model works at 0.50 GEL flat per cashout only if the bank does not take 1% of volume. The rep said
"fine up to ~200/day, above that = review" — we need the boundary and the review procedure on paper.
**Answer:** ______________________________________________

### 3. Exact package name and what it includes. [PAPER]
**KA:** რა ჰქვია ზუსტად პაკეტს (სტანდარტული / არასტანდარტული / სერტიფიკატით), რა ოპერაციები შედის მასში და რა — არა?
**Why:** our adapter uses `ImportSinglePaymentOrders`, `GetPaymentOrderStatus`, `GetSinglePaymentId`,
`GetAccountMovements`, `GetAccountStatement`, `ChangePassword`. Confirm all six are in the package.
**Answer:** ______________________________________________

### 4. Credentials hand-over: DBI username, temporary password, `.pfx` certificate. [BLOCKER]
**KA:** როდის და როგორ მივიღებთ DBI მომხმარებლის სახელს, დროებით პაროლს და სერტიფიკატს (.pfx)? რამდენ ხანს მოქმედებს სერტიფიკატი და როგორ ხდება მისი განახლება? პაროლის შეცვლას Digipass-ის კოდი სჭირდება — ვისთან იქნება Digipass?
**Why:** first `ChangePassword` needs a Digipass code (`nonce`); after that the certificate authenticates
every call. Certificate expiry decides our rotation runbook. The Digipass stays with the park (Levan), not Swich.
**Need:** timeline, certificate validity period, renewal procedure, who holds the Digipass.
**Answer:** ______________________________________________

### 5. Sandbox access. [BLOCKER]
**KA:** გვჭირდება სატესტო გარემო (`secdbitst` / `test-api.tbcbank.ge`): სატესტო მომხმარებელი, სატესტო სერტიფიკატი, სატესტო ანგარიში ნაშთით. სატესტო გარემო ასახავს თუ არა რეალურ სტატუსებს (მათ შორის წარუმატებელს — FL, C, D)?
**Why:** we test the adapter against a scripted fake today; before real money we need one full run on TBC's test
host — accepted → pending → completed, and at least one failure path.
**Answer:** ______________________________________________

---

## C. How transfers actually execute

### 6. Auto-certification: do imported orders execute without a human approving in internet banking? [BLOCKER] [CODE]
**KA:** სერტიფიკატით შემოტანილი გადახდის დავალებები ავტომატურად სრულდება, თუ ვინმემ ინტერნეტბანკში უნდა დაადასტუროს? თუ დასტური სჭირდება — შეიძლება ავტომატური სერტიფიცირების ჩართვა ამ მომხმარებლისთვის (ლიმიტით ან უფროდ)?
**Why:** a cashout that sits in status `WC` ("awaiting certification") until Levan logs in is not "instant 24/7".
Our saga already handles pending orders, but the product promise depends on auto-execution.
**Answer:** ______________________________________________

### 7. Are TBC→TBC transfers instant around the clock — nights, weekends, holidays? [CODE]
**KA:** TBC-დან TBC-ზე გადარიცხვები სრულდება მყისიერად 24/7 — ღამით, შაბათ-კვირას, დღესასწაულებზე? არის თუ არა ტექნიკური ფანჯრები (მაგ. ღამის 00:00–01:00), როცა სერვისი არ მუშაობს?
**Why:** drivers cash out at 3 a.m.; our nightly settlement runs at **00:30**. If there is a maintenance window
we move the settlement time.
**Answer:** ______________________________________________

### 8. Limits: per transfer, per day (count and amount), per beneficiary. [PAPER] [CODE]
**KA:** რა ლიმიტებია ერთ გადარიცხვაზე, დღეში (რაოდენობა და თანხა) და ერთ მიმღებზე? შეიძლება ლიმიტების გაზრდა 600 გადარიცხვამდე დღეში? მისი გადაჭარბებისას რა შეცდომას ვიღებთ?
**Why:** the park's own `MaxCashoutAmount` / `DailyCashoutLimitPerDriver` must stay under the bank's limits, and
the adapter must recognise the limit error as *not retryable*.
**Answer:** ______________________________________________

### 9. Transfers to private individuals ("third parties") are permitted on this service? [BLOCKER]
**KA:** ინტეგრაციის სერვისით დასაშვებია გადარიცხვები ფიზიკურ პირებზე (მძღოლებზე), რომლებიც არ არიან კომპანიის თანამშრომლები? სჭირდება რამე დამატებითი ნებართვა ან ხელშეკრულება?
**Why:** drivers are contractors, not employees; some banks restrict mass payouts to payroll.
**Answer:** ______________________________________________

### 10. Is the beneficiary's personal number (`beneficiaryTaxCode`) mandatory? [CODE]
**KA:** სავალდებულოა თუ არა მიმღების პირადი ნომერი TBC→TBC გადარიცხვისას? სხვა ბანკში გადარიცხვისას?
**Why:** we do not collect personal numbers today (light KYC). If TBC demands it, the driver profile and the
onboarding form gain a field, and the adapter must send it.
**Answer:** ______________________________________________

### 11. Description / purpose field: max length and allowed characters. [CODE]
**KA:** გადარიცხვის დანიშნულების ველში რა მაქსიმალური სიგრძეა და რომელი სიმბოლოებია დასაშვები — ქართული ასოები, ლათინური, ციფრები, `:` `,` `(` `)` `#`? ჩანს თუ არა დანიშნულება მიმღების (მძღოლის) ამონაწერში სრულად?
**Why:** per-cashout purpose is Georgian ("PayTaxi:123 გამომუშავებული თანხის ჩარიცხვა (name) პარკი: X"), the
settlement description is Latin ("PayTaxi settlement 2026-09-21, 12 tx, inv ref PT-2026-09"). Both are what
the accountant reconciles against.
**Answer:** ______________________________________________

### 12. Idempotency: scope and lifetime of `singlePaymentRequestId`. [CODE]
**KA:** `singlePaymentRequestId` უნიკალური უნდა იყოს მომხმარებლის, ანგარიშის თუ მთელი კომპანიის დონეზე? რამდენ ხანს ინახება — შეიძლება თუ არა ერთი წლის შემდეგ იგივე ნომრით ახალი გადარიცხვა?
**Why:** we derive an 18-digit request id from our idempotency key and rely on `DUPLICATED_SINGLE_PAYMENT_REQUEST`
to recover a lost response. A too-short memory would let a retry pay twice.
**Answer:** ______________________________________________

### 13. What does a failure after acceptance look like in the statement? [CODE]
**KA:** თუ დავალება მიღების შემდეგ წარუმატებელია (სტატუსი FL / C / D), თანხა ჩამოიჭრება და დაბრუნდება (ორი ჩანაწერი ამონაწერში), თუ საერთოდ არ ჩამოიჭრება? რამდენ ხანში ხდება ეს?
**Why:** our reconciliation matches our ledger to the park's statement line by line; a debit+credit pair for a
failed payout must be recognised as one failed transfer, not an orphan and a mystery credit.
**Answer:** ______________________________________________

### 14. Real-time balance: is `GetAccountStatement` current to the second? [CODE]
**KA:** ანგარიშის ნაშთი, რომელსაც API-ით ვკითხულობთ, რეალურ დროშია? ითვალისწინებს თუ არა დაბლოკილ / მიმდინარე გადარიცხვებს?
**Why:** before each nightly settlement we check the park balance and refuse a partial take. A stale balance means
a failed transfer at 00:30 and a red banner in the morning.
**Answer:** ______________________________________________

### 15. Statement / movements API for reconciliation. [CODE]
**KA:** `GetAccountMovements` რამდენი დღით უკან იძლევა მონაცემებს, რა დაყოვნებით ჩნდება ჩანაწერი და შეიცავს თუ არა ის ჩვენს `singlePaymentRequestId`-ს ან paymentId-ს?
**Why:** the nightly reconciliation ties every payout to a statement line; the id on the line is what makes the
match exact.
**Answer:** ______________________________________________

---

## D. Money to Swich, and the next parks

### 16. The nightly park → Swich transfer: anything special? [PAPER]
**KA:** პარკის ანგარიშიდან Swich Solutions-ის (ასევე TBC-ს) ანგარიშზე ყოველღამე ერთი ჯამური გადარიცხვა — სჭირდება თუ არა ამას ცალკე ხელშეკრულება, დანიშნულების კოდი ან სხვა ფორმალობა? საკომისიო 0-ია, როგორც სხვა TBC→TBC გადარიცხვებზე?
**Why:** it is a business-to-business transfer initiated by software under the park's authorisation. The lawyer
covers the authorisation; the bank should confirm nothing else is required.
**Answer:** ______________________________________________

### 17. Onboarding the second park: time and steps. 
**KA:** როცა მეორე პარკი დაემატება — თავიდან იგივე პროცედურაა (ხელშეკრულება, სერტიფიკატი, Digipass) და რამდენი კვირა სჭირდება? შეიძლება Swich-ის ტექნიკური კონტაქტი ყველა პარკზე ერთი და იგივე იყოს?
**Why:** Model A means every park has its own DBI contract; the answer sets our onboarding promise (today we say
1–3 weeks).
**Answer:** ______________________________________________

### 18. Fees to other banks (Phase 2). 
**KA:** სხვა ბანკებში (მაგ. საქართველოს ბანკი) გადარიცხვის საკომისიო რამდენია და რა დროში სრულდება (RTGS-ის საათები)?
**Why:** many drivers hold BoG cards; the fee decides whether Phase 2 is a TBC other-bank transfer or a second
BoG account per park.
**Answer:** ______________________________________________

---

## E. Operations and security

### 19. Support channel and SLA for the integration service.
**KA:** ტექნიკური პრობლემის შემთხვევაში (სერვისი არ პასუხობს, სტატუსი არ იცვლება) ვის მივწეროთ / დავურეკოთ და რა დროში გვპასუხობენ? არის თუ არა 24/7 ტექნიკური მხარდაჭერა?
**Answer:** ______________________________________________

### 20. IP whitelisting, TLS, certificate rotation notice. [CODE]
**KA:** სჭირდება თუ არა სერვისს ჩვენი სერვერის IP-ის დარეგისტრირება? რა TLS ვერსიაა საჭირო? სერტიფიკატის ვადის ამოწურვამდე გვაფრთხილებთ?
**Why:** the API host's outbound IP must be known before we pick the cloud host; the adapter currently forces
TLS 1.2 and a client certificate.
**Answer:** ______________________________________________

### 21. Notifications from the bank side.
**KA:** არსებობს თუ არა webhook / შეტყობინება, როცა გადახდის სტატუსი იცვლება, თუ მხოლოდ ჩვენ უნდა ვკითხოთ სტატუსი (polling)?
**Why:** we poll every 20 s today; a callback would make completion faster and cheaper.
**Answer:** ______________________________________________

---

## Leave the branch with

- [ ] Waiver + boundary + package name **in writing** (Q2, Q3)
- [ ] Dates for: DBI credentials + `.pfx` (Q4), sandbox access (Q5)
- [ ] Answer to Q1 (beneficiary name) and Q6 (auto-certification) — both change the product promise
- [ ] Limits sheet (Q8) and the personal-number rule (Q10)
- [ ] Description field rules (Q11) — otherwise the accountant's reconciliation breaks on day one
- [ ] Support contact (Q19)

## What changes in the code depending on the answers

| Question | If the answer is… | We do |
|---|---|---|
| Q1 name check | rejected on mismatch | keep the own-name rule; treat the bank refusal as a typed `holder_name_mismatch`-class failure; drop the "documented, not enforced" caveat from the lawyer's ToS line |
| Q1 name check | ignored | nothing — current design already assumes this |
| Q6 auto-certification | manual approval needed | pending-at-bank path stays; add a console warning "N payouts awaiting certification" and a morning reminder to the park |
| Q7 maintenance window | exists | move `Settlement:RunAtLocalTime` outside it; pause the payout queue during the window with a driver-facing notice |
| Q8 limits | lower than 600/day | cap `DailyCashoutLimitPerDriver` / park daily total; queue the rest for the next day with a clear status |
| Q10 personal number | mandatory | add `PersonalNumber` to drivers (encrypted), onboarding CSV column, driver profile field; adapter already passes it |
| Q11 description | Georgian not allowed / short | switch the per-cashout purpose to a Latin template and shorten to the limit |
| Q12 idempotency scope | short-lived | prefix request ids with a date component and keep our own dedupe as the last line of defence (already there) |
| Q13 failure shape | debit + refund pair | teach reconciliation to pair them as one failed transfer |
| Q20 IP whitelist | required | fixed outbound IP on the chosen host (NAT gateway / static IP) before go-live |
