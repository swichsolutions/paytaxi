# PayTaxi — Business & Integration Context (Sep 2026)

> Merge into CLAUDE.md (or keep as linked context file). This supersedes earlier assumptions
> from the ideation phase. The business model, partner agreement, and bank rail are now DECIDED.

## 1. Business model (final — license structure, NOT a joint company)

- **Swich Solutions owns the platform** (code, brand, domain). No co-ownership, ever.
- **Levan's company is the exclusive operator** (licensee) for the taxi vertical in Georgia:
  - He signs taxi parks, does sales/marketing, driver onboarding, first-line driver support.
  - Swich does development, maintenance, hosting, second-line technical support.
  - Exclusivity is conditional on minimum transaction volumes (exact numbers TBD in lawyer's doc).
- **One license agreement covers ALL parks.** Adding a park = Levan signs them commercially,
  we provision a tenant. No new Swich-side agreement per park.
- Lawyer is drafting: license agreement + duties/consequences both ways + park-authorization
  clauses. **Do not consider anything live-ready until signed.**

## 2. Money (agreed with Levan, verbally — pending written contract)

- Driver cashout fee: **0.50 GEL flat per cashout** (market comp: paypro 0.50, ProRide 0.55).
  Network parks keep nothing (no 0.10 park cut — that idea was dropped).
- **Levan's own park:** first **20,000 GEL** of fee income goes 100% to Swich; after that **50/50**.
- **All other (network) parks: 50/50 split of the fee** — 50% of every cashout fee to Swich,
  50% to Levan's side. Agreed as a PERCENTAGE of the fee, not a fixed tetri amount.
- Cost allocation (agreed): **Levan pays** bank fees/service, SMS, and support costs
  (mobile/SMS/calls, driver support). **Swich pays** hosting + domain + all technical/dev side.
  Costs are each side's own — NOT deducted from the pool before splitting.
- Bank-fee revision clause: current TBC terms hold; if bank cost exceeds ~100 GEL/quarter
  per park, the clause reopens and both sides decide jointly.
- VAT note: Swich's turnover crosses the 100k GEL/12mo VAT threshold at scale;
  fee cannot be raised (driver pays flat 0.50), so VAT comes out of Swich's share. Known, accepted.

## 3. Bank rail (TBC — decided for launch)

- **Model A everywhere:** each park pays its own drivers **from its own bank account**.
  No prepaid float, nobody holds anyone's money, no PSP/NBG license needed (lawyer to confirm formally).
- **TBC Business Integration Service, "non-standard" package** on the PARK's account:
  - Negotiated waiver: **no 1% volume fee for now** — 20 GEL registration + 85 GEL Digipass
    + 100 GEL/quarter. Written confirmation pending. Fee review is volume-triggered
    (TBC rep, in writing: fine up to ~200 transfers/day; above that = review).
  - Auth: digital certificate (automated transfers, no per-transaction Digipass).
  - Docs: https://developers.tbcbank.ge — sandbox `test-api.tbcbank.ge`, prod `api.tbcbank.ge`.
  - Credentials (client id/secret/cert) belong to the park, issued via their business internet bank.
- **TBC→TBC transfers: 0 fee.** Launch strategy: TBC-only; drivers required to have a TBC card.
- **Swich receives on its existing TBC business account** — receiving needs no API, no approval.
  Optional later (≥3 parks): statements-read API on Swich account for auto-reconciliation.
- **BoG = Phase 2** (many drivers hold BoG cards). BoG Business Online API exists
  (docs: https://api.bog.ge/docs/en/bonline/introduction, OAuth2 + JWT, base
  `api.businessonline.ge/api`). Pricing/classification not yet obtained — pending call.

## 4. Money flow per cashout (the core mechanic)

Driver has 100 GEL Yandex balance, cashes out:
1. App shows: receive 99.50, fee 0.50.
2. Platform posts −100 to driver balance via **Yandex Fleet API** (park's keys).
   TODO: confirm the transaction category paypro uses (visible in park's Fleet history).
3. Platform initiates transfer **99.50 park account → driver card** via park's TBC API creds.
4. The 0.50 never moves — it stays in the park's account (money that didn't leave).

**Nightly settlement (cron ~00:30), one aggregated transfer per park per day:**
- Compute Swich's share from ledger (phase logic below) → one transfer park → Swich TBC IBAN.
- Description format: `PayTaxi settlement YYYY-MM-DD, N tx, inv ref PT-YYYY-MM`.
- Insufficient park balance → NO partial take; mark failed, alert both sides, roll into next day.
- Store covered cashout IDs on each settlement record (dispute resolution = expand one row).
- Monthly invoice from Swich to park documents the already-settled amounts (paperwork follows money).

## 5. Phase / rate engine (config per park, NOT hardcoded)

```
park_config:
  levan_park:   { phase1_share: 100%, phase1_cap: 20000, phase2_share: 50% }
  network_park: { swich_share: 50% }         # percentage of the fee, not fixed tetri
fee_config:
  cashout_fee: 0.50                          # per-park overridable if ever needed
```
- Crossover day splits: part of the day at phase1 rate until cap reached, remainder at phase2.
- Admin panels must show the same numbers both sides: Levan sees recovery progress
  (`X / 20,000`), settlement history, per-day fee totals. Swich panel: all parks + settlement
  statuses (pending/completed/failed) + retry action for failed ones only.

## 6. Multi-tenant & multi-bank architecture (build now, even single-bank)

- One codebase, one deployment. Park = tenant: park_id scoping, per-park encrypted
  credentials (Yandex Fleet keys + bank API creds), subdomain routing. NO copy-paste deploys.
- **`IBankProvider` abstraction from day one** (TbcProvider first, BogProvider later):
  `InitiateTransfer(from, toIban, toName, amount, description)`, `GetBalance`, `GetStatus`.
- Park bank credentials = a LIST (bank-tagged), not a single credential.
- **Routing by driver IBAN** (bank code is inside Georgian IBANs) → pick matching park account.
- **Float job (Phase 2, when BoG added):** park holds accounts at both banks; Yandex settles
  into main (TBC); platform auto-rebalances TBC→BoG:
  - Nightly: target = forecast (trailing 7–14d avg per BoG driver, day-of-week) + 30–50% buffer;
    transfer = target − current BoG balance. Keep 2–3 days of BoG volume parked (lazy float).
  - Intraday: red-line threshold (~3× avg cashout) → immediate top-up transfer.
  - Weekly sweep of excess back to main account.
  - Admin "fuel gauge" for Levan: BoG balance, days-of-coverage, last top-up.
- **Queued-payout state machine (build now):** if payout can't execute (low float, bank API down),
  driver sees "processing, arrives in minutes", request queues, auto-executes on recovery,
  push notification on completion. Never a dead "failed" screen.
- Idempotency on ALL transfers: timeout/ambiguous response → status check by document id,
  never blind re-fire.

## 7. SMS / OTP (cost-sensitive design)

- SMSOffice pricing: ~0.03 GEL/sms (5k pack), 0.025 (10k pack).
- Design stingy: OTP on login/new device only; cashouts confirmed in-session (no per-cashout SMS).
- Push notifications replace SMS wherever possible (free).

## 8. Launch plan (current stage: bank activation + integration)

1. TBC branch visit with Levan: activate integration service on park account, get waiver
   IN WRITING, sandbox access, transfer-to-third-party permission confirmed, limits above
   600 tx/day, Beka as technical contact.
2. **Sandbox integration (~1–2 wks):** TBC provider (auth, transfer, status, balance, retries,
   result codes) + Yandex Fleet (real park id / driver ids, balance read, deduction post).
3. **Whitelist pilot:** 2–3 friendly drivers, real money, reconcile 3-way to the tetri
   (our ledger = park's TBC statement = Yandex Fleet history). Run the real nightly settlement.
4. **Go-live for all ~70 drivers** only after: pilot clean + license agreement SIGNED.
5. Parallel: BoG pricing call (Phase-2 prep + leverage for TBC's first volume review);
   lawyer finalizes documents.

## 9. Known risks / open items

- [ ] TBC waiver in writing (boundary: what exactly happens above ~200 tx/day).
- [ ] Yandex Fleet: confirm fee-deduction transaction category (copy paypro's pattern).
- [ ] Lawyer confirmations: Model A needs no NBG registration; auto-settlement authorization
      clause; exclusivity-with-volume-condition enforceability; non-compete.
- [ ] BoG quote (API access terms, per-transfer tariff, volume classification).
- [ ] Swich TBC account: confirm it's under Swich Solutions LLC exactly (goes into contract + settlement config).
- [ ] TBC review risk at >200 tx/day per park — mitigations: per-park contracts (reviews are
      per park, not network-wide), BoG/Liberty quotes as leverage, NBG registration as long-term path.

## 10. Correction (2026-09-17, from the bank's WSDL) — TBC Integration Service is SOAP, not REST
- The park-side rail ("Business Integration Service" / DBI) is SOAP 1.1 over HTTPS at
  `https://secdbi.tbconline.ge/dbi/dbiService` (test: `secdbitst.tbconline.ge`), WS-Security username/password +
  the company's `.pfx` client certificate. `api.tbcbank.ge` / `test-api.tbcbank.ge` are TBC's PSD2/OpenID REST stack
  and are NOT what the park account is enrolled in.
- Imported payment orders execute asynchronously (status WC "awaiting certification" -> CERT -> F). Branch-visit
  question: does the certificate package certify automatically, or does someone still approve in internet banking?
- Full technical notes: `STATE.md` -> "Session 2026-09-17 (part 3)".
