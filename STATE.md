# PayTaxi — Implementation State

> Snapshot for resuming work in a fresh session. Read CLAUDE.md for the project brief and the `Business Model and Multi-Tenancy` section; this file is the current "where are we" log.

**Last updated:** 2026-09-18 (TBC adapter + Yandex client both built; see the two newest sections)

---

## TL;DR

The product is functionally complete for everything that doesn't require external API access. Both driver and admin apps have real login (phone+OTP for drivers, email+password for admins), every page is backed by real Postgres data through the .NET 8 backend, the cashout saga moves money end-to-end through mock bank + mock Yandex, three background workers (balance sync, reconciliation, the saga itself) are running, an admin can generate a Georgian PDF invoice for any completed cashout, and the whole admin console is mobile-responsive.

The remaining work is **almost entirely external-dependency-blocked** (real bank API, real Yandex Fleet API, real SMS gateway, real legal entity) plus translation work and one open business-model question we're waiting on a lawyer to resolve.

---

## Session 2026-09-18 — real Yandex Fleet HTTP client

- `Infrastructure/Adapters/Yandex/YandexFleetClient.cs` is now a real implementation (was a NotImplemented stub).
  Endpoints: `POST v1/parks/driver-profiles/list` (offset/limit paging, fields driver_profile/account/car),
  `POST v2/parks/driver-profiles/transactions/list` (cursor), `POST v1/parks/orders/list` (cursor),
  `POST v2/parks/driver-profiles/transactions` (the write: `park_id, driver_profile_id, category_id, amount, description`
  with `X-Idempotency-Token`). Headers on every call: `X-Client-ID` (the literal value from fleet.yandex.com, format
  `taxi/park/{id}`), `X-API-Key`, `X-Park-ID`, `Accept-Language`. Money is sent/read as decimal strings.
- Error contract for the existing `ResilientYandexFleetClient` wrapper: 429 / 5xx / timeouts / connection errors →
  `YandexTransientException` (retried with backoff); other 4xx → `YandexApiException` → for writes a failed
  `YandexTransactionResult` with Yandex's `code`. `ReadOnlyMode` still guards both writes.
- Reversal posts `+amount` with `YandexFleet:ReversalCategoryId` (defaults to `CashoutCategoryId`). Idempotency token =
  first 32 hex of SHA-256(our key), so retries reuse it exactly and Yandex declines duplicates.
- `YandexFleetOptions` gained `BaseUrl, TimeoutSeconds, AcceptLanguage, CashoutCategoryId (partner_service_manual —
  STILL TO CONFIRM against paypro's entries in the park's Fleet history), ReversalCategoryId, PageSize`.
- Credentials: `IYandexParkCredentialsProvider` → `DbYandexParkCredentialsProvider` (parks table). DI:
  `YandexFleet:UseMock=false` → `AddHttpClient<YandexFleetClient>` + provider; the resilient wrapper is unchanged.
- Sources: the official reference (fleet.taxi.yandex.ru) is not reachable from this network, so shapes were taken from
  three open-source wrappers of the API (JS `yandex-fleet-wrapper`, PHP `antonowano/yandex-taxi-api` v7 and
  `i-pinchuk/php-yandex-taxi-api`) which agree on paths, headers and body keys. Response field names for profiles /
  transactions / orders follow the Fleet API convention (`driver_profiles[].driver_profile|accounts|car`,
  `transactions[]`, `orders[]`, `cursor`, `total`) — **verify against the first real response** and adjust `MapProfile`
  if a field differs; the mapping is tolerant (missing fields → null/0, no throws).
- Tests: `PayTaxi.Tests/Yandex/YandexFleetClientTests.cs` (10) — paging, headers, balance filter, cashout body +
  token, reversal, read-only guard, 429/5xx transient, 400 rejection code, cursor paging, orders mapping. 40 tests total.
- Wiring check: API started with `YandexFleet__UseMock=false` and the seed's mock credentials → the real host answered
  403 "invalid client id or api key" (expected; proves DI, path and headers reach Yandex's auth layer).
- New `Api/Middleware/IntegrationErrorMiddleware`: Yandex/TBC failures become 502 `upstream_rejected` (definitive) or
  503 `upstream_unavailable` (transient / read-only) with provider + code, instead of a 500.

### When Levan's park credentials arrive
1. Settings → Yandex credentials (Client ID exactly as shown on fleet.yandex.com, API key, Park ID).
2. `YandexFleet:UseMock=false`, keep `ReadOnlyMode=true` first: roster sync + balances + transactions must look right.
3. Look at paypro's debit rows in the park's Fleet transaction history → set `CashoutCategoryId` to that category.
4. `ReadOnlyMode=false`, one 5 GEL cashout on a test driver, check the Fleet history shows the −5 with our description.

---

## Session 2026-09-17 (part 3) — real TBC adapter (Integration Service / DBI)

### What TBC's API actually is (researched from developers.tbcbank.ge + the bank's WSDL/XSD 1.14)
- **SOAP 1.1, document/literal**, namespace `http://www.mygemini.com/schemas/mygemini`, SOAPAction `{ns}/{Operation}`.
  It is NOT the REST `api.tbcbank.ge` — that host is the PSD2/OpenID stack. PAYTAXI-CONTEXT.md §3 was wrong on this
  point; a correction section was appended there.
- Endpoints (Standard+ = client certificate): prod `https://secdbi.tbconline.ge/dbi/dbiService`,
  test `https://secdbitst.tbconline.ge/dbi/dbiService`. Auth = WS-Security UsernameToken (username + password issued by
  the banker) **plus** the company's `.pfx` client certificate on the TLS connection. The `Nonce` element is the Digipass
  one-time code — required for ChangePassword (and payment import on the no-certificate Standard package); omitted for
  the certificate package. TLS 1.2. Temporary password must be changed via ChangePassword; expiry → `CREDENTIALS_MUST_BE_CHANGED`.
- Operations: `ImportSinglePaymentOrders` (order types `TransferWithinBankPaymentOrderIo` for TBC→TBC,
  `TransferToOtherBankNationalCurrencyPaymentOrderIo` for other Georgian banks; `singlePaymentRequestId` xsd:long is the
  client's idempotency id → `DUPLICATED_SINGLE_PAYMENT_REQUEST` fault on repeat), `GetPaymentOrderStatus` (async statuses:
  I/DR/G/WC/CERT/VERIF/WS processing · F done · FL/C/D/CPE failed), `GetSinglePaymentId` (recover paymentId by request id;
  `SINGLE_PAYMENT_REQUEST_NOT_FOUND`), `GetAccountMovements` (paged, ≤700), `GetAccountStatement` (opening/closing balance),
  `ChangePassword`, plus batch + postbox operations we don't use. Faults: `VALIDATION_ERROR`, `INCORRECT_INPUT_DATA`,
  `GENERAL_ERROR`. WSDL/XSD archive: `tbcbank-developers.apigee.io/files/WSDL_XSD_and_Single_WSDL.zip`.
- **Execution is asynchronous** — import returns a paymentId, the order then goes through certification. Whether the
  certificate package auto-certifies (no human approval in internet banking) is exactly the question for the branch visit.

### Code
- `Infrastructure/Adapters/Banks/Tbc/`: `TbcSoapClient` (envelopes, mTLS HttpClient cache per park credential set,
  fault parsing), `TbcPayoutAdapter` (IBankPayoutAdapter mapping), `TbcCredentials` (per-park JSON on
  `ParkBankAccount.CredentialsEncrypted`: username, password, environment, certificatePfxBase64|certificatePath,
  certificatePassword, nonce?, debitCurrency), `TbcDbiOptions` (`BankPayout:Tbc` — endpoints, poll attempts, page size,
  `TrustTestServerCertificate` for the test host's own root cert).
- Idempotency: `TbcSoapClient.RequestIdFromKey` maps our string key → deterministic 18-digit `singlePaymentRequestId`;
  a duplicate fault recovers the original paymentId via GetSinglePaymentId and continues with status.
- `BankTransferResult.IsPending` + `BankTransferRequest.DestinationTaxCode` added to the adapter contract.
- **Saga gained a "pending at bank" state**: `Success+IsPending` → cashout stays `Processing` with `BankTransferId` and
  `NextAttemptAt`; `PayoutQueueWorker` also picks up `Processing + BankTransferId + NextAttemptAt due` rows;
  `ProcessQueuedAsync` → `CheckPendingAsync` polls `GetTransferStatusAsync`: Completed → complete, Failed → abandon
  (Yandex reversal), still pending past `Cashout:MaxQueueAgeHours` → `ReviewRequired` (money may have moved, never
  reverse). `Cashout:PendingPollSeconds` (20). Settlements handle pending the same way (`ResolvePendingAsync`, run first
  in the nightly job and on retry). Verified with the mock's new `BankPayout:Mock:AsyncExecution` knob: accepted →
  polled twice → Completed with the full ledger trail.
- DI: with `BankPayout:UseMock=false`, keys `tbc`/`TBC` → real `TbcPayoutAdapter` (singleton), BoG stays a stub.
- **Tests**: new `PayTaxi.Tests` (xunit) — 30 tests: TBC adapter against a scripted fake DBI server using envelope shapes
  from the docs (within-bank vs other-bank order, WS-Security header, nonce rules, async pending, FL failure detail,
  duplicate recovery, lookup-by-key, faults retryable/non-retryable, statement balance, movements), GeorgianIban,
  settlement share math incl. crossover. Run: `dotnet test backend/PayTaxi.Tests`.

### Still needed for the pilot (bank-side)
- Park's DBI username + temporary password + `.pfx` certificate from the TBC branch (Standard+ / non-standard package);
  run ChangePassword once (needs a Digipass code → `nonce`), then store creds on the park's payout account via
  `PATCH /api/admin/parks/{id}/bank-accounts/{accountId}` `credentialsJson`, set `BankPayout:UseMock=false`.
- Confirm at the branch: auto-certification of imported orders for the certificate package (else every payout sits in
  WC "awaiting certification" until someone approves in internet banking); transfer-to-third-party permission;
  whether `beneficiaryTaxCode` (driver personal number) is demanded for TBC→TBC — we don't collect it yet
  (light-KYC gap; the adapter passes it when present).
- Sandbox run against `secdbitst` with the test certificate (`TBCRootCer.cer` → `TrustTestServerCertificate=true`).

---

## Session 2026-09-17 (part 2) — saga paths closed + Settlement engine

### Saga paths verified (mock knobs via env vars)
- **Abandon → reversal**: `Cashout__MaxPayoutAttempts=1` + `BankPayout__Mock__OutageUntilUtc` → cashout came back
  422 `Failed`, Yandex balance restored, ledger = CashoutReserved, YandexDeducted, YandexReversed, CashoutReversed,
  driver got the failed notification, mock Yandex history shows the −30 / +30 pair.
- **Ambiguous bank response**: `BankPayout__Mock__AmbiguousFailureRate=1` → adapter threw after recording the
  transfer, saga found it by document id and completed on attempt 1 (no duplicate transfer).

### Settlement engine (PAYTAXI-CONTEXT.md §4/§5) — built and verified
- `Settlement` entity (one per park per local day; covers a fixed set of cashouts via `Cashout.SettlementId`;
  FeeTotal / Phase1Fees / Phase2Fees / SwichShare / ParkShare / CumulativeFeesBefore; status pending → processing →
  completed | failed; `IdempotencyKey = settle:{id}`; description `PayTaxi settlement YYYY-MM-DD, N tx, inv ref PT-YYYY-MM`).
- Per-park split config on `Parks`: `SwichSharePercent` (50), `Phase1SharePercent` + `Phase1CapGel` (Levan's park:
  100 / 20,000; seed puts this on Tbilisi #3). `SettlementService.ComputeShares` walks cashouts in completion order and
  splits a straddling cashout exactly at the cap.
- `SettlementService.RunDueAsync(date)`: retries earlier Failed settlements first, then creates + executes each park's
  settlement (transfer from the park's primary account to `Settlement:SwichIban` via the bank adapter; balance pre-check
  → `INSUFFICIENT_PARK_BALANCE` fail, no partial take). `SettlementWorker` fires daily at 00:30 Tbilisi for the previous
  day; idempotent, so restarts are harmless (it also catches up on startup — it settled the June stragglers as 09-16).
- `LedgerEntry.CashoutId` is now nullable + `SettlementId` (check constraint: exactly one owner); new types
  `SettlementSent` / `SettlementFailed`.
- New **operator** admin role (`levan@operator.local` / `operator1!`): sees all parks, cannot create parks, edit the
  revenue split, retry or run settlements (all 403 — verified). `AdminControllerBase.SeesAllParks`.
- API: `GET /api/admin/settlements?parkId&status&take`, `GET /api/admin/settlements/{id}/cashouts`,
  `GET /api/admin/parks/{id}/settlements/summary?days` (phase progress `X / cap`, settled-to-Swich, waiting-for-tonight,
  daily fee table), `POST /api/admin/settlements/{id}/retry` (Swich), `POST /api/admin/settlements/run?parkId&date`
  (Swich, idempotent). `PATCH /api/admin/parks/{id}` accepts `swichSharePercent/phase1SharePercent(-1 clears)/phase1CapGel(0 clears)` — Swich only.
- Frontend: **Settlements** page (tiles: settled to Swich, phase-1 recovery bar, waiting for tonight, failed; daily fee
  table; history with expand → covered cashouts; retry + "Settle now" for Swich; this park / all parks toggle for Swich
  and operator). Settings shows the revenue split (editable by Swich). Sidebar link, route, en/ka keys, operator role label.
- Verified via API: Levan's park settled 4 cashouts, fees 2.00 → Swich 2.00 (phase 1), cumulative before 10.00;
  re-run idempotent; Batumi with the cap set 1.20 above its cumulative fees: fees 1.50 → phase1 1.20 / phase2 0.30 → Swich 1.35 (crossover
  split correct); retry on a Completed settlement → 400.
- Migration `SettlementsAndOperatorRole` (tool-generated, applied). Config: `Settlement:{Enabled, SwichIban,
  SwichHolderName, TimeZoneId, RunAtLocalTime, MinTransferGel, PollIntervalSeconds, InitialDelaySeconds}` — dev uses a
  placeholder valid IBAN `GE12TB7100000000000001`; **production needs Swich Solutions LLC's real TBC IBAN**.
- Not done: alerting both sides on a failed settlement (only logged + visible in the panel); monthly invoice document
  (PT-YYYY-MM) for the park; Failed-settlement path only unit-reasoned (needs a mock knob for a settlement-time outage).

---

## Session 2026-09-17 — Model A cutover (fee, IBAN routing, Yandex-first saga, payout queue)

> Driven by `PAYTAXI-CONTEXT.md` (read it first). Everything below compiles (`dotnet build` 0 warnings, `ng build` ok)
> and has been smoke-tested end to end against the local database (see "Verified" below). Not committed yet.

### What changed

**Business model in code**
- `Park.OperatingModel` defaults to `ModelA`; the A.5 `AuthorizationLimit` column/constraint/saga branch are gone
  (`ModelA5` enum value kept only so old rows deserialise; the migration rewrites `model_a5 → model_a`).
- Per-park fee config on `Parks`: `CashoutFee` (default **0.50**), `MinCashoutAmount` (5), `MaxCashoutAmount?`,
  `DailyCashoutLimitPerDriver?`. The saga reads them; `ComputeFee()` is gone. Daily limit is enforced.
- New `ParkBankAccounts` table: one payout account per bank the park holds (`BankCode` TB/BG…, `Provider`
  tbc/bog/mock, `Iban`, `HolderName`, write-only `CredentialsEncrypted`, `IsPrimary`, `IsActive`).
  `Park.BankProvider/BankAccountIban` are now mirrors of the primary account.
- `BankCards` are now **IBAN payout destinations**: `Iban`, `BankCode`, `HolderName` (+ `MaskedPan` derived).
  `PayTaxi.Core/Banking/GeorgianIban.cs` validates shape + mod-97 and maps bank codes to labels.
- **Routing by IBAN**: a cashout goes from the park account whose `BankCode` matches the driver's IBAN. No matching
  account → rejected up front (`bank_not_supported`) — TBC-only launch means drivers must add a TBC IBAN.

**Saga (`CashoutOrchestrator`) is now Yandex-first with a payout queue**
1. validate + fee/limits + route → insert `Processing` + `CashoutReserved`
2. Yandex debit (gross). Rejected → `Failed` (nothing moved). Threw → `ReviewRequired` (outcome unknown).
3. Bank payout (net = gross − fee) via `IBankPayoutAdapter.SendPayoutAsync(BankTransferRequest{Source, DestinationIban…})`
   - success → `Completed` (+`BankTransferSent`, `FeeCollected`, `CashoutCompleted`, invoice number)
   - `IsRetryable` failure → **`Queued`** with `NextAttemptAt` backoff (`Cashout:BackoffSeconds`); driver gets a
     "processing, on its way" notification once
   - adapter threw → `FindTransferByIdempotencyKeyAsync` before ever re-sending (never blind re-fire)
   - hard failure or queue exhausted (`Cashout:MaxPayoutAttempts` / `MaxQueueAgeHours`) → **Yandex reversal**
     (`PostReversalTransactionAsync`, +gross, `YandexReversed` ledger) → `Failed`; reversal failed → `ReviewRequired`
4. `PayoutQueueWorker` (new `BackgroundService`, `PayoutQueue:*` config) drains `Queued` rows per park sequentially;
   claim is an atomic `Queued→Processing` UPDATE inside `ProcessQueuedAsync`.
- `Cashout` gained `ParkBankAccountId`, `InitiatedBy`, `AttemptCount`, `NextAttemptAt`, `LastAttemptAt`,
  `YandexReversalTransactionId`. `LedgerEntryType.YandexReversed` added.
- `IBankPayoutAdapter` reshaped: every call takes a `BankAccountContext` (park account + its credentials);
  added `FindTransferByIdempotencyKeyAsync`, `GetBalanceAsync`, `BankTransferResult.IsRetryable`.
  Mock gained `AmbiguousFailureRate`, `OutageUntilUtc`, per-account balances. TBC/BoG stubs updated to the new shape.
- Reconciliation lists bank transfers per park account, treats `Queued` as in-flight, ignores reversal
  (positive) Yandex txs and debit/credit pairs of `Failed` rows.

**API**
- `GET/POST /api/admin/parks/{id}/bank-accounts`, `PATCH …/bank-accounts/{accountId}` (balance read where the rail
  allows; credentials write-only). `POST /api/admin/parks` now **requires** `bankAccountIban` and creates the primary
  account; accepts `cashoutFee/minCashoutAmount/maxCashoutAmount/dailyCashoutLimitPerDriver`. `PATCH /api/admin/parks/{id}`
  accepts the same fee fields (0 = no limit). Parks list/KPIs return fee config + `bankAccounts` / `queued` instead of
  `authorizationLimit`.
- `POST /api/admin/parks/{id}/drivers/{driverId}/cards`, `DELETE …/cards/{cardId}`; `POST …/drivers` accepts optional
  `iban`/`holderName`. Bulk onboarding **no longer seeds mock cards** — drivers add their IBAN in the app.
- Driver: `/api/driver/me` returns `park.cashoutFee/min/max/supportedBanks` and cards with `iban/bankCode/holderName`;
  `POST /me/cards {iban, holderName?, makeDefault?}`, `DELETE /me/cards/{id}` (409 if a cashout is in flight),
  `POST /me/cards/{id}/default`. Cashout responses: **202 = Queued or ReviewRequired**, 422 = Failed.
- `POST /api/admin/parks/{id}/cashouts/{cashoutId}/process-now` clears a Queued row's backoff and runs it.
- Shared rules for adding destinations live in `Api/Controllers/DestinationHelper.cs`.

**Frontend**
- Driver: cashout step 2 = "Select bank account" with inline **Add bank account** (IBAN + holder), fee/min/max from park
  config, success screen handles Completed / Queued ("payment on its way") / ReviewRequired / Failed. Profile lists
  accounts with add/remove/make-default (real API, no more mock cards). New i18n keys in en/ka/ru.
- Admin: Add-park has a Fees & limits section and creates the primary payout account from the IBAN; Settings shows/edits
  fees and manages payout accounts (balance, primary, deactivate, add with credentials JSON); Overview float card became
  **Payout accounts + Queued payouts**; Cashouts rows show Queued/Needs review, attempts, next attempt, **Process now**;
  Onboarding manual form has optional IBAN; manual cashout uses the park fee. New i18n keys in en/ka.

### Smart App Control (resolved 2026-09-17, later in the session)
Windows Smart App Control was ON and blocked every locally built DLL (`0x800711C7`). The user turned it off; the API
process and `dotnet ef` load fine now. A local tool manifest (`backend/.config/dotnet-tools.json`, dotnet-ef 8.0.11)
was added — use `dotnet ef …` from `backend/`.

### Migration
`20260917125014_ModelAFeeIbanPayoutQueue` is **tool-generated** (the earlier hand-written attempt was discarded once the
tooling worked). Two manual edits inside it: the scaffolder wanted to *rename* `AuthorizationLimit → MaxCashoutAmount`
(would have carried the seed limits over as caps) — replaced with drop + add; and a `UPDATE Parks SET OperatingModel =
'model_a'` data fix. A probe migration was generated and came back empty, so the snapshot matches the model.
**Not yet applied**: the first `dotnet run` applies it via `MigrateAsync`, then `SeedData` upgrades existing rows
(`EnsureParkBankAccountsSeededAsync` gives each park a mock TBC account [+BoG except park #5]; legacy token-only cards
get generated valid IBANs; drivers with no card get TBC+BoG IBANs).

### Verified end-to-end on 2026-09-17 (mock bank + mock Yandex, local Postgres)
Postgres password was reset to the documented `postgres`/`postgres` (pg_hba trust → ALTER USER → scram back).
Migration applied on first `dotnet run`; seed upgraded 3 parks (TBC[+BoG] mock accounts) and 50 legacy cards to IBANs.
Smoke results (all via curl, see session transcript): admin login → parks list shows fee 0.50 / Model A / bank accounts;
`bank-accounts` returns mock balances; adding an NBG-IBAN account via API works. Driver: OTP login (phone must be
`+995…` — the app prepends it), `/me` returns fee config + supported banks; Liberty IBAN → 400 `bank_not_supported` with
the supported list; bad check digits → 400 `invalid_iban_checksum`; valid TBC IBAN → 201; 50 GEL cashout → Completed,
fee 0.50, net 49.50, balance debited; 3 GEL → 400 minimum. Outage test (`BankPayout__Mock__OutageUntilUtc` env var):
cashout → 202 Queued, Yandex debited immediately, driver got the "on its way" notification, KPIs/bank-accounts/cashouts
showed the queued row, worker retried on the dev schedule (10s, 20s) and completed it once the outage passed;
`process-now` also completes a queued row on demand. Ledger for a queued-then-completed cashout: CashoutReserved,
YandexDeducted, BankTransferSent, FeeCollected, CashoutCompleted. Invoice PDF renders for it.
Three fixes came out of the run: API pinned to InvariantCulture (messages showed "5,00"); `CashoutOptions.BackoffSeconds`
default is now empty because the config binder APPENDS to a non-empty default array; the mock's `OutageUntilUtc` is
normalised to UTC (binder parses "…Z" as local time).
**Not exercised:** the abandon path (queue exhausted → Yandex reversal → Failed / ReviewRequired) — needs a forced
non-retryable bank error or `Cashout:MaxPayoutAttempts=1`; and the ambiguous-response lookup (`AmbiguousFailureRate`).

### New config (both appsettings files updated)
`Cashout:{MaxPayoutAttempts, MaxQueueAgeHours, BackoffSeconds[]}`, `PayoutQueue:{Enabled, PollIntervalSeconds,
InitialDelaySeconds, MaxPerTick, SpacingMs, StopParkOnFirstRequeue}`, `BankPayout:Mock:{AmbiguousFailureRate,
InitialBalance, OutageUntilUtc?}`. To demo the queue: set `BankPayout:Mock:OutageUntilUtc` a few minutes ahead, cash out,
watch it sit in Queued, then complete when the outage passes.

### Not done in this block (next)
- Settlement engine (nightly park → Swich transfer, phase/rate config, settlement records, operator + Swich views).
- Real `TbcPayoutAdapter` / `YandexFleetClient` HTTP implementations against public docs.
- Production plumbing: `environment.ts` for the 8 hardcoded `localhost:5196` URLs, hosting, secrets, PII encryption.
- Driver dashboard "recent cashouts" still renders mock data (history page is real).

---

## Phases completed

- **Phase 0 — Foundation.** .NET 8 solution (`backend/PayTaxi.sln` → `Api`, `Core`, `Infrastructure`). EF Core + Npgsql + 11 migrations. Multi-park `park_id` on every table from day one.
- **Phase 1 — Driver app UI** (Angular 21 SSR): login (phone + OTP), dashboard (real balance + bell badge), cashout (3-step), history, profile, notifications. Mobile-first "Bold Utility" visual design.
- **Phase 5 — Admin panel** (taken out of order): six working pages (overview, cashouts, drivers, onboarding, reconciliation, reports) all backend-backed, with park scope dropdown in topbar.
- **Phase 2 — Yandex Fleet integration (mock).** `MockYandexFleetClient` with realistic data for 3 parks × 8 drivers + 2 "unclaimed" profiles per park for the onboarding demo. Wrapped by `ResilientYandexFleetClient` chain (rate-limit 0.5s/park → exponential-backoff retry → audit log).
- **Phase 3 — Cashout saga.** `CashoutOrchestrator` runs reserve → A.5 limit decrement (atomic SQL) → bank payout → Yandex deduct → confirm, with compensation on bank fail and `ReviewRequired` on post-bank Yandex fail. Double-entry ledger entries at every transition. Reachable from both the admin manual-cashout modal and the driver app. Idempotency-keyed, retryable.
- **Phase 4 — Bank integration (mock).** `MockBankPayoutProvider` with 3% transient failures, idempotency-key dedup, and a transfer log that the reconciliation worker queries. Real BOG/TBC adapters stubbed (`NotImplementedException`) — waiting on sandbox credentials.
- **Phase 6 — Reporting & Reconciliation.** Financial reports page with daily breakdown, top drivers, by-bank split, CSV export. Reconciliation worker (`BackgroundService`, configurable interval — 5 min dev, daily prod) cross-checks Postgres ↔ mock bank ↔ mock Yandex over a sliding window, writes `ReconciliationRun` + `ReconciliationDiscrepancy` rows for any drift across 7 known kinds. Admin reconciliation page lists runs + open discrepancies with "Mark resolved" comment flow.
- **Phase 7 (partial) — Notifications + i18n.** In-app driver notifications (Notification entity + INotificationService writes from saga's terminal branches, driver inbox page with unread badge polling every 25s). i18n keys + driver-app language toggle exist; English, Georgian, and Russian are all fully filled across every driver page. The **admin console is now bilingual too** (English default + Georgian): separate `src/app/admin/i18n/{en,ka}.json` (317 keys each) behind `AdminI18nService` (persists to `paytaxi.admin.lang`), with an EN/ქარ toggle in the admin topbar and on the admin login page. No Russian for admin (per client — not needed). Per-cashout Georgian PDF invoice (QuestPDF, modelled exactly on the paypro reference — driver as issuer, park as recipient, PayTaxi as commercial intermediary; bilingual not yet).
- **Auth & security hardening.** Real driver phone+OTP and admin email+password both issue JWTs. Driver-scoped `/api/driver/*` endpoints derive ids from claims (clients can't tamper). Admin endpoints `[Authorize(Roles="admin")]` with `CanAccessPark` scope check (super-admin sees all, park-admin only own). ASP.NET rate-limiting on the three auth endpoints (10/min/IP); AdminUser lockout after 5 failed attempts within 15 minutes (HTTP 423). HTTP interceptor URL-aware (admin token for `/api/admin/*`, driver token for `/api/driver/*`).
- **Background workers.** `BalanceSyncWorker` refreshes `YandexBalanceCache` every 60s in dev (300s prod). `ReconciliationWorker` every 5 min in dev (24h prod). Both use isolated DI scope per tick, configurable, fail-soft on errors.
- **Driver onboarding.** `GET /yandex-lookup` + `POST /drivers` endpoints. Operator enters phone + Yandex profile ID → lookup confirms it exists in the park's Yandex roster → driver row created + 2 mock cards seeded → driver can log in immediately. New driver also gets an entry in the admin overview's activity feed.
- **Cashout retry.** `POST /cashouts/{id}/retry` mints a fresh idempotency key and re-runs the saga as a new cashout row (only when source status=Failed). Frontend banner distinguishes retry-completed / retry-failed-again / retry-needs-review / network-error.
- **Driver edit + suspend.** Admin can `PATCH /drivers/{id}` to edit name, phone (re-hashes, rejects duplicates), Yandex profile id, status. Drawer footer Suspend/Reactivate button uses the same endpoint.
- **Per-cashout invoice.** QuestPDF renderer produces an A4 Georgian PDF mirroring the paypro layout — issuer (driver) + recipient (park) + service line + bank details + commercial-intermediary note. Cashouts get a sequential `InvoiceNumber` from a Postgres sequence (`InvoiceNumberSeq`) when they reach Completed. Admin cashouts page "View invoice" button opens the PDF inline in a new tab.
- **Mobile responsive admin.** Sidebar collapses to a fixed-position drawer at ≤768px; topbar collapses crumbs/bell/user-meta; sum tiles use `minmax(0, 1fr)` so values don't crop; status tabs / range pills / driver chips shrink-to-fit so all options fit on one row without horizontal scroll; drawers slide up from the bottom on phone; page headers stack title/subtitle/actions vertically.
- **Driver login form fits viewport.** Hero hero reduced from 46dvh → 32dvh, OTP digit inputs from 72px → 56px, back arrow positioned above the heading instead of overlapping it.

---

## Architecture cheat-sheet

**Backend** (`backend/PayTaxi.sln`):
- `PayTaxi.Core` — entities, enums, interfaces (no infra deps)
- `PayTaxi.Infrastructure` — EF Core DbContext + 11 migrations, mock adapters, services (saga, workers, invoice generator, notification, JWT)
- `PayTaxi.Api` — controllers, Program.cs, appsettings

**Frontend** (Angular 21 SSR, `src/app/`):
- `core/` — shared services (auth, driver session, notifications, HTTP interceptor)
- `features/` — driver app pages
- `admin/` — admin console pages + services (admin auth, park context, API client)
- `shared/layouts/` — driver-layout, admin-layout

**Controllers** (7):
- `AuthController` (driver phone+OTP)
- `AdminAuthController` (admin email+password)
- `DriverController` (/me, /me/cashouts, /me/notifications, /cashouts)
- `AdminParksController` (parks, drivers, KPIs, activity, hourly, reports, yandex-lookup, smoke-test)
- `CashoutsController` (admin cashouts list + create + retry + invoice PDF)
- `ReconciliationController` (runs, discrepancies, resolve)
- `AdminControllerBase` (shared `CanAccessPark` helper)

**Services** (in Infrastructure):
- `CashoutOrchestrator` — the saga
- `BalanceSyncWorker` — IHostedService
- `ReconciliationWorker` — IHostedService
- `NotificationService` — fire-and-forget notification writer
- `ApiAuditLogger` — every external API call logged
- `JwtTokenService` — JWT minting
- `InvoiceGenerator` — QuestPDF renderer
- `YandexRateLimiter` — per-park 500ms gate

**Workers status:** balance sync every 60s, reconciliation every 5 min, saga reachable from `POST /api/admin/parks/{id}/cashouts` (admin) or `POST /api/driver/cashouts` (driver).

---

## How to run locally

```powershell
# Backend (terminal 1, from project root)
cd backend\PayTaxi.Api
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --no-launch-profile --urls "http://localhost:5196"

# Frontend (terminal 2, from project root)
npx ng serve
```

Database: Postgres 17 EDB-installed locally. DB `paytaxi_dev`, user `postgres` / pw `postgres`. Path: `C:\Program Files\PostgreSQL\17\bin\psql.exe`.

**Quick fix scripts in repo:**
- `verify-phase4.ps1` — exercises park-admin scope + login lockout via curl
- `unlock-admins.sql` — clears admin lockouts after rate-limit tests

**Test credentials:**
- Super-admin: `ops@swich.dev` / `swich2026!`
- Park-admin (Tbilisi #3): `manager@tbilisi-auto-park-3.local` / `park2!`
- Driver: phone `599123456` (Giorgi, Tbilisi #3) — login flow returns dev OTP in the response banner

---

## Park onboarding & self-registration (added 2026-05-29)

The seed-only park-creation gap is closed. New admin capabilities:
- **Super-admin "Add park" page** (`/admin/add-park`, sidebar link visible only to super-admins): `POST /api/admin/parks` creates a Park + optional first park-admin login (BCrypt). Validates slug uniqueness, tax ID (9–11 digits), phone, IBAN, manager email/password. Replaces editing `SeedData.cs`.
- **Yandex credentials in the UI**: the 3 fields (Client ID, API Key, Park ID) are now enterable. Settings page (park-admin + super-admin) and the Add-park form both write them. **API key is write-only** — never returned by the API (`yandexApiKeySet` boolean only); blank on PATCH = keep existing. Stored plaintext in `*_Encrypted` columns until Phase 8 AES.
- **Driver sync from Yandex (no CSV)**: `GET /api/admin/parks/{id}/yandex-roster` returns the full Yandex roster flagged `alreadyOnboarded`; `POST .../drivers/bulk` bulk-creates selected drivers (+ default mock cards). The onboarding page has a "Sync from Yandex Fleet" card on top (load roster → checkbox list → onboard selected); the manual one-at-a-time stepper remains below under "Or onboard one manually". `YandexDriverProfile` gained a `Phone` field; `MockYandexFleetData` generates a deterministic mock GE phone per profile so synced drivers get a usable phone+OTP login.
- Parks list DTO now also returns `legalEntityName`, `taxId`, `phone`, `bankAccountIban`, `yandexClientId`, `yandexApiKeySet`. `AdminParkContextService.refresh()` re-pulls parks after edits/creation.
- Decision log: park creation = super-admin only; Yandex creds editable by both super-admin (at creation) and park-admin (Settings); drivers come from Yandex API, CSV dropped.

## Known issues / loose ends

- `/api/admin/parks/{id}/smoke-test` should be removed or feature-flagged before prod.
- Driver IBAN is shown as masked PAN on the invoice — needs a real IBAN field collected at onboarding.
- Driver-app translation JSONs (`en/ka/ru.json`) are fully populated and all driver pages (incl. notifications) go through i18n. Admin console remains English-only (intentional for now).
- `appsettings.Development.json` is `.gitignore`d (intentional — dev creds out of git). Anyone cloning fresh needs to recreate it with `YandexFleet:ReadOnlyMode=false` for the saga to work; the file contents have grown — full recreation list is at end of this document.
- Cashouts created BEFORE the `AddInvoiceFields` migration have `InvoiceNumber=null` — the invoice endpoint returns 409 for them. Only new completed cashouts can produce invoices.
- The mock bank's transfer log is in-memory; it resets on every backend restart, causing reconciliation to flag pre-restart cashouts as `missing_in_bank` (realistic but noisy).

---

## What you (the user) need to gather before we ship

### 1. External API credentials

- **Yandex Fleet API** per park: `X-Client-ID`, `X-API-Key`, `X-Park-ID`. Available at fleet.yandex.com → Settings → API. Required to swap `MockYandexFleetClient` for the real `YandexFleetClient` (currently a stub). Without this, balance reads, transaction lists, and cashout posts to Yandex remain mock.
- **Bank of Georgia (BOG) business / mass-payout API** sandbox + production credentials. Required to swap `MockBankPayoutProvider` for `BogPayoutAdapter` (currently throws). Includes the bank agreement that allows third-party-initiated transfers under our commercial agent authority.
- **TBC business / mass-payout API** sandbox + production credentials. Same applies for `TbcPayoutAdapter`.
- **SMS gateway** (e.g. MagtiCom, Twilio, or local provider). Needed so the dev OTP banner can be replaced with real text-message delivery in `AuthController.RequestOtp`. Currently the OTP is returned in the response body for dev convenience.

### 2. Legal / business decisions

- **Resolve the Model A.5 vs prepaid-wallet question** with a Georgian fintech lawyer. The park's testimony ("our paypro balance went to 0 and we had to transfer money to top it up") suggests paypro might actually hold park funds in their bank account as a prepaid wallet rather than only holding authorization to spend from the park's account. The legal model matters enormously: prepaid wallet may require a PSP license; commercial agent does not. Recommended firms in CLAUDE.md: BLC, MKD, Dentons Georgia.
- **Real operating entity details** for invoices and JWT issuer: legal name, Georgian tax ID (ს/კ), legal address, registered IBAN. Currently placeholders: `Swich Solutions LLC` / `405848882`. Update `appsettings:Invoice` once finalized.
- **Fee model** finalized: % per cashout, flat fee, or hybrid; minimum/maximum per cashout. Currently hardcoded in `CashoutOrchestrator.ComputeFee` to `max(2 GEL, 1% of amount)` — should move to per-park `cashout_fee_*` columns on the `Parks` table (schema already has them, code doesn't read them yet).
- **Data protection compliance** under Georgian Personal Data Protection Law: consent flow at driver onboarding (we record `ConsentGiven` + `ConsentTimestamp` but don't yet collect explicit consent text in the UI), 2-year audit log retention policy, PII encryption at rest (currently `PhoneEncrypted` is plaintext — needs real AES + a key store like Azure Key Vault).
- **KYC level** for drivers — paypro does light KYC (name + car). We currently only collect name + phone + Yandex profile ID. Confirm this is sufficient or add ID upload / verification.

### 3. Production infrastructure decisions

- **Hosting** (e.g. Azure App Service, AWS ECS, hetzner VPS). Affects deployment pipeline shape, secrets manager choice, scaling story.
- **Domain** for production. We have `paytaxi.ge` listed in CLAUDE.md as the working name; confirm.
- **Subdomain routing strategy** for multi-park UI: `{park-slug}.paytaxi.ge` (DNS wildcard) vs `paytaxi.ge/p/{park-slug}` (path-based). Database has `Slug` column with a check constraint ready for either; backend currently doesn't resolve tenants by URL.
- **Secrets manager** (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault). Replaces dev-only plaintext in `appsettings.Development.json` for the JWT key, DB password, bank credentials, etc.
- **Database hosting**: Postgres 17 dev locally; need a managed provider (Azure Postgres Flexible Server, AWS RDS, etc.) plus backup policy. `BankCredentialsEncrypted` and `YandexClientIdEncrypted` columns need real encryption keys.

### 4. Translation work

- ✅ Done for the driver app (`src/app/core/i18n/{en,ka,ru}.json`) AND the admin console (`src/app/admin/i18n/{en,ka}.json`, English default + Georgian toggle, no Russian). Every driver page and every admin page (login, sidebar, topbar, overview, cashouts, manual-cashout, drivers, onboarding, reconciliation, reports) renders through i18n, including status labels and flow error messages. A native Georgian speaker should still proofread the admin wording before launch — the `ka.json` strings are functional but unreviewed.

### 5. First customer agreement

- The current single prospect park (Tbilisi-based, paypro user). Once they sign as the first tenant: legal name, tax ID, phone, IBAN, Yandex park ID. Add them to the Parks table via the seed or a one-off SQL script.

---

## Next concrete tasks (when blocked items unblock)

1. **Real Yandex Fleet client** — fill in `YandexFleetClient.cs` (currently a stub). Swap by flipping `YandexFleet:UseMock=false`. No other code changes needed.
2. **Real BOG/TBC adapters** — fill in `BogPayoutAdapter.cs` and `TbcPayoutAdapter.cs` (currently throw). Swap by flipping `BankPayout:UseMock=false`.
3. **Real SMS sender** — implement `ISmsSender` interface + provider, inject into `AuthController.RequestOtp`, remove the `devCode` field from the response.
4. **Phase 8 production hardening** — secrets manager wiring, real PII encryption (AES on `PhoneEncrypted`, etc.), staging environment, deployment runbook.
5. **Park self-registration** if you want it (we decided to defer — the admin-onboarding flow is the realistic production path; driver self-registration requires the Yandex profile ID which drivers don't know).

---

## Smaller polish items

- Driver IBAN collection step at onboarding + IBAN column on `BankCards`.
- `/api/admin/parks/{id}/smoke-test` endpoint cleanup (remove or feature-flag).
- Settings sidebar item is still "Soon" — define what goes on it (probably fee model per park, top-up account display, manager invite).
- Replace the in-memory mock bank transfer log with a Postgres table so it survives restarts; that would make reconciliation tests on the mock more realistic.
- Add a "bulk monthly invoice" mode if the park asks for one (currently per-cashout only).
- Consider sharing JWT-signed temporary URLs for the invoice download so park managers can email PDFs without re-fetching.

---

## `appsettings.Development.json` recreate list

Since the file is gitignored, anyone cloning fresh needs to create it with this minimum content:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=paytaxi_dev;Username=postgres;Password=postgres"
  },
  "Jwt": {
    "Key": "dev-only-key-replace-before-production-32chars",
    "Issuer": "paytaxi-api",
    "Audience": "paytaxi-clients",
    "ExpiryMinutes": 1440
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Information",
      "Microsoft.EntityFrameworkCore": "Warning",
      "PayTaxi": "Information"
    }
  },
  "YandexFleet": { "UseMock": true, "ReadOnlyMode": false },
  "BankPayout": {
    "UseMock": true,
    "Mock": { "LatencyMs": 250, "TransientFailureRate": 0.03 }
  },
  "BalanceSync": { "Enabled": true, "IntervalSeconds": 60, "InitialDelaySeconds": 10 },
  "Reconciliation": {
    "Enabled": true, "IntervalSeconds": 300, "InitialDelaySeconds": 20,
    "WindowHours": 48, "GracePeriodMinutes": 0
  },
  "Invoice": {
    "OperatingEntityName": "Swich Solutions LLC",
    "OperatingEntityTaxId": "405848882"
  }
}
```

---

## Pitfalls / surprises future-Claude should know

- **CSS custom properties in shorthand: any undefined variable silently zeros the entire shorthand.** Define every variable referenced or use longhand.
- **Angular doesn't reset scroll between routes by default.** `withInMemoryScrolling({ scrollPositionRestoration: 'top' })` is in `app.config.ts`.
- **EF Core `Expression<Func<…>>` lambdas can't contain `switch` expressions or `throw` expressions** (CS8514/CS8188). Use a static helper method called from the lambda.
- **Fire-and-forget audit logging racing a scoped DbContext.** `IServiceScopeFactory` per write; register the logger as singleton.
- **Postgres locale on this machine returns errors in Georgian.** Set `$env:PGCLIENTENCODING="UTF8"` and `chcp 65001` before running psql, OR use `.\unlock-admins.sql` style files instead of inline SQL.
- **`dotnet-ef` global tool** at `/c/Users/Home/.dotnet/tools/dotnet-ef.exe` — NOT on PATH. Invoke with full path.
- **`ASPNETCORE_ENVIRONMENT=Development` must be set explicitly** when running `dotnet run` without `--launch-profile`.
- **Backend API port:** 5196 (http). Frontend dev: 4200. CORS configured for localhost:4200 only.
- **User mixes Georgian (Mkhedruli), Latin transliteration, and English** in mock data. Always use Noto Sans (supports all scripts).
- **Don't reduce the dashboard balance amount font size** below `clamp(4rem, 18vw, 5.5rem)` with `letter-spacing: -0.05em` — the user pushed for it explicitly.
- **`/frontend-design` and `/ui-ux-pro-max` skills are available.** Re-invoke when doing significant new UI work.
- **User's preferred review pattern:** propose 2–4 options crisply with a marked recommendation, then they pick. Lead with the recommendation.
- **EDB-built Postgres 17 installed locally.** Superuser `postgres` / password `postgres`. Database `paytaxi_dev`.
- **The mock bank transfer log resets on backend restart.** First reconciliation tick after a restart will flag every pre-restart Completed cashout as `missing_in_bank` — this is realistic but can confuse first-time viewers of the reconciliation page.
- **JWT lives in `localStorage`** under `paytaxi.driver.{token,session}` and `paytaxi.admin.{token,session}`. Two separate namespaces so a tab can be logged in as both.
- **HTTP interceptor is URL-aware.** `/api/admin/*` uses admin token, `/api/driver/*` uses driver token. Strict — no cross-fallback.
