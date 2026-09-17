# PayTaxi — Project Brief

> **⚠️ Read `PAYTAXI-CONTEXT.md` first (Sep 2026).** It holds the DECIDED business model, bank rail, fee, and
> revenue split, and supersedes this file wherever they disagree. In particular:
> - Operating model is **Model A on TBC** for launch (park pays drivers from its own account). The Model A.5
>   commercial-agent sections below and the `authorization_limit` field are legacy.
> - Fee is **0.50 GEL flat** per cashout. Open Question 3 below is resolved.
> - Swich owns the platform; Levan's company is the exclusive operator. Revenue is collected by a
>   **nightly park → Swich settlement transfer** (phase/rate engine per park) — a subsystem not described below.
> - Payout routing is by **driver IBAN**; BoG is Phase 2.

> A paypro-style instant-cashout platform for Tbilisi taxi drivers. Drivers see their Yandex Taxi earnings balance and withdraw it to their bank cards 24/7. The park fronts the cashout from its bank account and gets reimbursed by Yandex on weekly settlement.

**Project name:** PayTaxi (working name)
**Vendor:** Swich Solutions 
**Client:** Tbilisi-based autoparks which currently use paypro.ge
**Stack:** Angular 21 (frontend) + .NET 8 C# (backend) + PostgreSQL + cloud hosting
**Bank integrations:** Bank of Georgia (BOG) + TBC — both, behind a common `IBankPayoutAdapter` interface

---

## How It Works (the core flow)

1. Driver logs into PayTaxi with phone + SMS code
2. PayTaxi backend calls Yandex Fleet API → reads driver's current balance
3. Driver requests cashout (e.g., 200 GEL)
4. PayTaxi backend pays 200 GEL from the **park's bank account** to driver's card via Georgian bank API
5. PayTaxi backend posts negative transaction to Yandex Fleet API to decrement driver's Yandex balance
6. Yandex settles weekly with the park, reimbursing the float

The park earns a fee on every cashout (% or flat — TBD by client).

---
Business Model and Multi-Tenancy
PayTaxi is a multi-tenant platform. Multiple taxi parks register on the platform, each managing their own drivers. There are three distinct operational models for how money moves between the park's bank account and the driver's bank card. We are building toward Model A.5 (the "commercial agent" model used by paypro), with Model A also supported as a fallback for parks that prefer it. Model B is documented for completeness but is not in scope.
Model A is "pure SaaS, each park uses their own bank's API directly." The money flow for a cashout: driver requests cashout, PayTaxi backend looks up the driver's park, the backend uses that park's own bank mass-payout API credentials (stored encrypted in our database) to initiate the transfer, money moves directly from the park's bank account to the driver's card, PayTaxi never touches the money and never has authorization over the park's funds. Implications: PayTaxi is a pure SaaS vendor with no financial intermediary status, no PSP license required, no commercial-agent structure required, the park's funds remain entirely in the park's control at all times, but each park must obtain a mass-payout API agreement with their bank (typically TBC or BOG) which takes 2–6 weeks per park and may not be available to smaller parks who don't meet the bank's volume thresholds. This model is the cleanest legally for PayTaxi but the most restrictive in terms of which parks can adopt it.
Model A.5 is the "commercial agent" model, which is what paypro actually does and what we are primarily building toward. We confirmed this from an actual paypro invoice for one of their partner parks (Smart Logistics, operating as smarttaxi.paypro.ge). The invoice shows that the driver was paid from Smart Logistics' bank account, not from paypro's account, with paypro identified as the "commercial debtor" (კომერციული მოვალე) executing the payment on behalf of the park. paypro is operated by BM Coll LLC (tax ID 405788809). The money flow for a cashout under Model A.5: driver requests cashout, PayTaxi backend checks the park's authorization limit (the "balance" the park has configured on PayTaxi), if sufficient PayTaxi initiates a payment from the park's bank account to the driver's card under a pre-signed commercial agent authorization, PayTaxi decrements the authorization limit, PayTaxi creates the negative transaction in Yandex Fleet API. The park's funds never sit in PayTaxi's bank account — PayTaxi orchestrates the transfer as the park's authorized agent. The park manages an authorization limit on PayTaxi (similar to the "balance" parks see in paypro today) which they refill by signaling PayTaxi to increase it, optionally backed by a bank guarantee or scheduled top-up. Implications: this model serves any park size including small parks who can't get their own bank API, requires a commercial agent agreement between PayTaxi (or its operating entity) and each park, requires PayTaxi to have a banking arrangement with TBC or BOG that supports third-party-initiated transfers under commercial agent authorizations (this is the bank-side setup paypro has — paypro itself does it; each park doesn't have to), and requires legal/regulatory consultation to set up the commercial agent structure correctly under Georgian law. A 2-hour consultation with a Georgian fintech lawyer (BLC, MKD, Dentons Georgia, or similar) before launch is essential to confirm the structure.
Model B is the "true fintech / PSP-licensed" model where PayTaxi advances its own money from PayTaxi's bank account to drivers and is reimbursed by parks (or directly by Yandex's settlement cycle) later. This requires a Payment Services Provider license from the National Bank of Georgia or a licensed-partner arrangement with an existing PSP, working capital sufficient to cover float across all parks, full AML and KYC obligations as a regulated entity, and a 6–18 month legal launch timeline. paypro is not operating in this model — they appear to have intentionally chosen the lighter Model A.5 / commercial agent structure precisely to avoid PSP licensing. Model B is documented here as a possible long-term direction but is explicitly not in scope for the current build.
The architecture is designed so that all three models share the same codebase and the choice of model per park is configuration, not code. Every external-system credential (Yandex Fleet API, bank API, SMS provider) is per-park, stored encrypted on the parks row, and retrieved at runtime. All external integrations sit behind interfaces (IBankPayoutProvider, IYandexFleetClient, ISmsSender), and the implementations select credentials and behavior based on the current park context and the park's configured operational model. The park_id field is present on every domain table (drivers, cashouts, ledger_entries, api_audit_log, and so on). Tenant resolution happens at the request layer, either by subdomain (such as {park_slug}.paytaxi.ge) or by path. All money calculations are done in our own append-only ledger; the bank and Yandex are sources of truth for external state, but our ledger is the source of truth for what we believe happened.
The parks table schema includes: a UUID primary key, a unique slug for subdomain routing, the park name, the park's operating legal entity name and tax ID, a status field with values like pending, active, or suspended, an operating_model field (with values such as model_a, model_a5, model_b) so the system knows which payment flow to use, the encrypted Yandex Fleet API credentials (yandex_client_id_encrypted, yandex_api_key_encrypted, yandex_park_id), the bank payout configuration (bank_provider as a string identifying which bank or PSP, bank_credentials_encrypted as a JSONB blob whose structure varies by provider, bank_account_iban for display and reporting only), for Model A.5 parks an authorization_limit field representing the configured ceiling the park has allowed PayTaxi to initiate against, per-park configuration values (cashout_fee_model as percentage, flat, or hybrid; cashout_fee_value; min_cashout_amount; max_cashout_amount; daily_cashout_limit_per_driver), and timestamps for created_at and updated_at.
The onboarding flow for a new park is white-glove and manual, with no public self-serve signup, but the steps differ significantly by operating model. For Model A.5 (commercial agent — preferred and faster path): (1) park contacts PayTaxi via an "Apply for partnership" form, (2) sales conversation covers scope, pricing, expectations, (3) park signs a PayTaxi service agreement plus a commercial agent authorization specifying which payment limits PayTaxi may initiate on the park's behalf, (4) park requests Yandex Fleet API credentials from fleet.yandex.com and shares them with PayTaxi, (5) park provides their TBC or BOG business account details and signs whatever bank-side documentation is needed for PayTaxi (under its commercial agent status) to initiate payments from the account, (6) PayTaxi team creates the parks row and configures the park, (7) park imports driver list as CSV with phone numbers and Yandex driver_profile.ids, (8) park goes live, drivers receive login SMS messages. Total onboarding time for Model A.5: 1–3 weeks, primarily limited by legal document signing and Yandex credentials. For Model A (each park's own bank API — slower, more independent): same flow but step 5 is the park's own multi-week negotiation with their bank to obtain a mass-payout API agreement, pushing total onboarding to 4–8 weeks; recommended only for parks who specifically prefer not to grant commercial agent authorization or who already have their own bank API for other reasons.
Build priorities for now: the single existing client may or may not sign as the first tenant, but this is irrelevant to the build sequence. We build all features as if for any park, not customized to the current prospect. We use a MockBankPayoutProvider for development until the first real bank credentials and commercial agent arrangement are in place. The park admin onboarding flow is a first-class feature rather than an afterthought. Audit logging, the ledger, idempotency, and retries are non-negotiable from day one. Before the first real Model A.5 park goes live, PayTaxi (Swich Solutions, or a dedicated operating entity to be decided) must have completed a Georgian fintech legal consultation, signed appropriate banking arrangements supporting third-party-initiated transfers, and have the commercial agent contract templates ready.

## Yandex Fleet API Integration

- Base URL: `https://fleet-api.taxi.yandex.net/`
- Auth headers: `X-Client-ID`, `X-API-Key`, `X-Park-ID`
- One API key per park (not per driver). Drivers identified by `driver_profile.id`.
- Credentials provided by client from `fleet.yandex.com` → Settings → API

### Endpoints we use

| Purpose | Endpoint |
|---|---|
| List drivers + balances | `POST /v1/parks/driver-profiles/list` |
| Transaction history | `POST /v2/parks/transactions/list` |
| Ride history | `POST /v2/parks/orders/list` |
| Create transaction (the cashout) | `POST /v2/parks/driver-profile/transactions` |

For cashout step: `amount: -200`, `category: partner_service_manual`.

### Rate limiting

- Empirical: minimum 0.5 sec between operations on the same `park_id`
- Yandex reserves right to set any limits (ToS rule 3.8)
- Cache aggressively; queue transaction posts; exponential backoff on 429

---

## Bank Payout Integration

The other half of the cashout flow. Driver requests 200 GEL → PayTaxi must move 200 GEL from park's bank account to driver's card, 24/7, instant.

- **Client's bank: TBD** (Bank of Georgia or TBC — confirm before architecture)
- Need a **mass-payout / card-credit API** from that bank
- Park must keep working capital in the account to cover in-flight cashouts
- Failure mode: if bank transfer succeeds but Yandex transaction post fails (or vice versa), we have inconsistency — handle with a double-entry ledger and reconciliation worker

---

## Compliance & Legal

### Yandex Fleet API ToS

- Official ToS: <https://yandex.ru/legal/taxi_api_partners/> (updated 2025-08-15)
- We are an **"Integrator"** in Yandex's terminology — explicitly allowed
- Russian law governs Yandex ↔ integrator relationship
- Georgian law governs us ↔ client ↔ drivers
- **Rule 3.7**: Yandex can audit our API usage on demand → audit log every API call
- **Rule 5.1**: Yandex can suspend access at any time → architect for graceful degradation

### Branding constraints

- Use "Yandex" only descriptively ("withdraw from your Yandex balance")
- Never use Yandex logo
- Don't claim partnership
- Don't mimic Yandex Pro UI

### Financial regulation (client's responsibility, not Swich's)

- Cashout flow may require Payment Services Provider license from National Bank of Georgia
- Contract with client must explicitly state: client holds all regulatory/licensing responsibility
- Client should consult a Georgian fintech lawyer before launch

### Data protection

- Georgian Personal Data Protection Law applies
- Encrypt PII at rest (phone numbers, bank details, driver IDs)
- Consent flow with timestamp
- Audit log retention: 2 years minimum

---

## Required Features

### Driver-facing app

- Login with phone + SMS code
- Dashboard: current Yandex balance, recent transactions, recent rides
- Cashout button: enter amount, select bank card (saved on first use), confirm
- Cashout history with status (pending / completed / failed)
- Profile: phone, bank cards, notification preferences
- Languages: Georgian, Russian, English

### Manager admin panel

- All drivers in the park with balances
- Cashout queue (pending, completed, failed)
- Manual cashout creation (manager initiates on driver's behalf)
- Failed transaction review and retry
- Financial reports: cashouts per period, fees collected, reconciliation status
- Float monitoring: how much cash needs to be in the bank account to cover pending cashouts
- Driver onboarding (link a PayTaxi account to a Yandex `driver_profile.id`)

### Background workers

- Yandex balance sync (periodic, cached)
- Cashout processor (queue worker, one-at-a-time per park, 0.5s min spacing)
- Reconciliation worker: nightly cross-check our DB ↔ Yandex transactions ↔ bank transfers
- Notification sender: SMS/push for cashout completion, failures, low balance

---

## Architecture Principles

- **Frontend never calls Yandex or bank APIs directly** — always frontend → our backend → external
- **API keys live on backend only**, in env vars or secrets manager; never in git, never in frontend
- **Double-entry ledger in our own DB** — don't rely solely on Yandex/bank ledgers; we need our own truth source for reconciliation
- **Adapter pattern for external integrations** — Yandex Fleet API, bank API behind interfaces, swappable if either changes/breaks
- **Multi-park ready from day one** — `park_id` on every table. Client starts single-park, but the architecture supports onboarding more parks later with no rewrite
- **Audit everything money-related** — every API call, every transaction, every cashout, every credential rotation, append-only

---

## Data Model (rough sketch)

- `parks` — park metadata, Yandex credentials (encrypted), bank credentials (encrypted)
- `drivers` — phone, name, linked Yandex `driver_profile.id`, park_id, status
- `bank_cards` — driver_id, masked card number, tokenized reference from bank API
- `cashouts` — id, driver_id, amount, fee, status, bank_transfer_id, yandex_transaction_id, timestamps
- `ledger_entries` — append-only double-entry rows for every money movement
- `api_audit_log` — every external API call: timestamp, endpoint, params hash, response code, actor
- `yandex_balance_cache` — last-known balance per driver, updated_at (so we don't hammer Yandex on every page load)

---

## Open Questions (resolve as we go)

1. **Yandex API credentials** — client to provide `X-Client-ID`, `X-API-Key`, `Park ID`
2. ~~**Client's bank**~~ — **RESOLVED: both BOG and TBC**, behind `IBankPayoutAdapter` interface
3. **Fee model** — % per cashout, flat fee, or both?
4. **Driver list export** — Excel with current drivers and their `driver_profile.id`s
5. ~~**Backend language decision**~~ — **RESOLVED: .NET 8 C#**
6. **KYC** — paypro does light KYC (name + car); do we match that, or stricter? Most drivers are already KYC'd by the park in Yandex, so PayTaxi onboarding can be light.

---

## Working Principles for This Codebase

- Test every external integration with manual curl/Postman before writing integration code
- Every Yandex API call: logged, idempotent where possible, retry-safe
- Every bank API call: idempotent (with idempotency key), logged, never fire-and-forget
- Cashout is a saga: bank transfer + Yandex transaction must both succeed, or both rollback / be flagged for manual review
- Never write money-moving code without a corresponding audit log entry
- Reconcile daily, alert on any mismatch

---

## Build Phases

### Phase 0 — Foundation ✅ COMPLETE
- Angular 21 SSR frontend scaffold (existing)
- .NET 8 backend: `PayTaxi.sln` with `PayTaxi.Api`, `PayTaxi.Core`, `PayTaxi.Infrastructure`
- Full PostgreSQL schema via EF Core: Parks, Drivers, BankCards, Cashouts, LedgerEntries, ApiAuditLogs, YandexBalanceCache, OtpCodes
- JWT auth wired (phone → OTP → JWT)
- `IBankPayoutAdapter` interface + BOG/TBC stubs
- `IYandexFleetClient` interface + stub
- EF Core initial migration generated
- Multi-park `park_id` on every table from day one

### Phase 1 — Driver App (UI + mock data) ✅ COMPLETE
- Login: phone input → OTP entry (auto-advance digits, 60s resend timer)
- Dashboard: balance hero card (dark navy), recent rides + cashouts lists
- Cashout flow: numeric keypad → card select → confirm → success screen
- Transaction history with All/Cashouts/Rides filter tabs
- Profile: cards, notifications toggle, language switcher, logout
- Bottom nav: dark, amber active state, cashout orb
- Design system: Noto Sans, amber #FFB800 accent, dark navy #0F1B2D, flat utility style
- i18n: all strings in `T['en']` object in `core/mock/data.ts` (Georgian/Russian keys stubbed)
- Mock data service with driver, cards, cashouts, rides signals

### Phase 2 — Yandex Fleet API Integration
- Typed Yandex API client (real HTTP calls)
- Balance cache layer + periodic sync worker
- Rate-limit queue (0.5s min spacing per park)
- Every call: audit-logged, retried with exponential backoff

### Phase 3 — Cashout Engine (The Saga)
- Cashout queue (per-park, sequential)
- Saga: reserve → bank transfer → Yandex deduct → confirm
- Double-entry ledger entries on every state change
- Compensation on any failure → `ReviewRequired`
- Idempotency keys on all external calls

### Phase 4 — Bank Integration (BOG + TBC)
- BOG Business API adapter (real implementation)
- TBC Open Banking adapter (real implementation)
- Card tokenization storage
- Sandbox testing before live wiring

### Phase 5 — Manager / Taxipark Admin Panel
- Driver list with balances
- Cashout queue live view
- Manual cashout creation, failed cashout retry
- Driver onboarding (link phone → Yandex profile ID)
- Float monitor widget

### Phase 6 — Reporting & Reconciliation
- Fee configuration per park
- Financial reports (cashouts per period, fees)
- Nightly reconciliation worker (DB ↔ Yandex ↔ bank)
- Reconciliation dashboard: mismatches, alerts
- Audit log viewer + CSV/Excel export

### Phase 7 — Notifications + i18n + Polish
- SMS notifications (cashout completed/failed)
- Full translations: Georgian, Russian, English
- Data protection consent flow
- Mobile responsiveness pass
- Graceful degradation when Yandex API is down

### Phase 8 — Security & Production Hardening
- Secrets manager (Azure Key Vault or equivalent)
- PII encryption at rest
- Rate limiting + OTP brute-force protection
- Load test cashout queue
- Staging → production deployment runbook
