# PayTaxi — Implementation State

> Snapshot for resuming work in a fresh session. Read CLAUDE.md for the project brief and the `Business Model and Multi-Tenancy` section; this file is the current "where are we" log.

**Last updated:** 2026-05-29 (end of long session — 17 commits pushed)

---

## TL;DR

The product is functionally complete for everything that doesn't require external API access. Both driver and admin apps have real login (phone+OTP for drivers, email+password for admins), every page is backed by real Postgres data through the .NET 8 backend, the cashout saga moves money end-to-end through mock bank + mock Yandex, three background workers (balance sync, reconciliation, the saga itself) are running, an admin can generate a Georgian PDF invoice for any completed cashout, and the whole admin console is mobile-responsive.

The remaining work is **almost entirely external-dependency-blocked** (real bank API, real Yandex Fleet API, real SMS gateway, real legal entity) plus translation work and one open business-model question we're waiting on a lawyer to resolve.

---

## Phases completed

- **Phase 0 — Foundation.** .NET 8 solution (`backend/PayTaxi.sln` → `Api`, `Core`, `Infrastructure`). EF Core + Npgsql + 11 migrations. Multi-park `park_id` on every table from day one.
- **Phase 1 — Driver app UI** (Angular 21 SSR): login (phone + OTP), dashboard (real balance + bell badge), cashout (3-step), history, profile, notifications. Mobile-first "Bold Utility" visual design.
- **Phase 5 — Admin panel** (taken out of order): six working pages (overview, cashouts, drivers, onboarding, reconciliation, reports) all backend-backed, with park scope dropdown in topbar.
- **Phase 2 — Yandex Fleet integration (mock).** `MockYandexFleetClient` with realistic data for 3 parks × 8 drivers + 2 "unclaimed" profiles per park for the onboarding demo. Wrapped by `ResilientYandexFleetClient` chain (rate-limit 0.5s/park → exponential-backoff retry → audit log).
- **Phase 3 — Cashout saga.** `CashoutOrchestrator` runs reserve → A.5 limit decrement (atomic SQL) → bank payout → Yandex deduct → confirm, with compensation on bank fail and `ReviewRequired` on post-bank Yandex fail. Double-entry ledger entries at every transition. Reachable from both the admin manual-cashout modal and the driver app. Idempotency-keyed, retryable.
- **Phase 4 — Bank integration (mock).** `MockBankPayoutProvider` with 3% transient failures, idempotency-key dedup, and a transfer log that the reconciliation worker queries. Real BOG/TBC adapters stubbed (`NotImplementedException`) — waiting on sandbox credentials.
- **Phase 6 — Reporting & Reconciliation.** Financial reports page with daily breakdown, top drivers, by-bank split, CSV export. Reconciliation worker (`BackgroundService`, configurable interval — 5 min dev, daily prod) cross-checks Postgres ↔ mock bank ↔ mock Yandex over a sliding window, writes `ReconciliationRun` + `ReconciliationDiscrepancy` rows for any drift across 7 known kinds. Admin reconciliation page lists runs + open discrepancies with "Mark resolved" comment flow.
- **Phase 7 (partial) — Notifications + i18n.** In-app driver notifications (Notification entity + INotificationService writes from saga's terminal branches, driver inbox page with unread badge polling every 25s). i18n keys + driver-app language toggle exist; English is filled, Georgian + Russian are stubbed. Per-cashout Georgian PDF invoice (QuestPDF, modelled exactly on the paypro reference — driver as issuer, park as recipient, PayTaxi as commercial intermediary; bilingual not yet).
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

## Known issues / loose ends

- `/api/admin/parks/{id}/smoke-test` should be removed or feature-flagged before prod.
- Driver IBAN is shown as masked PAN on the invoice — needs a real IBAN field collected at onboarding.
- Russian + Georgian translation JSONs are stubbed (English only is fully populated).
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

- Fill `src/app/core/i18n/{ka.json, ru.json}` — currently stubbed. The English file (`en.json`) is the reference; same key set needs values in Georgian and Russian. Affects driver-app only (admin is English-only currently and that's probably OK).

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
