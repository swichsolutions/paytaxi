# PayTaxi — Implementation State

> Snapshot for resuming work in a fresh session. Read CLAUDE.md for the project brief and `Business Model and Multi-Tenancy` section; this file is the current "where are we" log.

**Last updated:** 2026-05-27 (Phase 3 complete)

---

## Phases completed

- **Phase 0 — Foundation.** .NET 8 solution (`backend/PayTaxi.sln` → `Api`, `Core`, `Infrastructure`). EF Core + Npgsql, JWT auth wired (mock only), all DB entities, initial migration. Multi-park `park_id` on every table from day one.
- **Phase 1 — Driver app UI.** All 5 mobile screens built (login, dashboard, cashout, history, profile) with mock data. Underwent a "Bold Utility" visual overhaul (Noto Sans, dark navy + amber, textured surfaces, segmented controls, left-bar tx rows).
- **Phase 5 — Admin panel UI.** Taken out of order (Phases 2/3/4 were blocked on credentials). Desktop-first console with sidebar + topbar layout. 4 pages: overview (KPIs + float card + activity feed), cashouts (filterable queue), drivers (table + drawer), onboarding (Yandex lookup flow). 4-step manual cashout modal. All driven by `AdminMockService`.
- **Phase 2 — Yandex Fleet API integration (mock edition).** `MockYandexFleetClient` returning realistic data for 3 parks × 8 drivers, with simulated latency and 3% transient failures. Wrapped by `ResilientYandexFleetClient` decorator chain (rate-limit → retry with exponential backoff → audit log). 5 admin API endpoints under `/api/admin/parks/...`. End-to-end verified against real Postgres — smoke test confirmed 5 concurrent calls serialised at exactly 500ms intervals, all 9 calls audit-logged with SHA-256 param hashes.
- **Park multi-tenancy schema migration.** Added `OperatingModel`, `AuthorizationLimit`, `Status`, `Slug`, `LegalEntityName`, `TaxId`, `BankProvider`, `BankAccountIban` to `Parks` table with Postgres check constraints. Two-phase pattern — `IsActive` preserved (delete in follow-up migration). Existing rows backfilled cleanly.
- **Phase 3 — Cashout Saga (Option B).** `MockBankPayoutProvider` with 3% transient failures, 250ms latency, idempotency-key dedup. `CashoutOrchestrator` saga implements reserve → A.5 limit check (atomic SQL decrement) → bank payout → Yandex deduct → confirm, with compensation on bank failure and `ReviewRequired` on post-bank Yandex failure. Double-entry ledger written at every transition. New `POST /api/admin/parks/{parkId}/cashouts` endpoint. Admin manual-cashout modal rewritten to fetch real parks/drivers/cards from backend and POST through the saga. Verified end-to-end: Model A.5 path decrements `AuthorizationLimit` (125000 → 124900 after 100 GEL test), Model A path skips it, idempotency replay returns prior cashout, over-limit attempts fail without touching bank/Yandex. `YandexFleet:ReadOnlyMode=false` in dev only.
- **Option C — Driver app cashout wired to backend.** New `DriverSessionService` auto-discovers an active driver-with-card on bootstrap (stand-in until real driver-auth in Phase 8). Driver dashboard hero shows real driver name + park + balance from backend. Driver 3-step cashout flow now POSTs through the same `/api/admin/parks/{parkId}/cashouts` saga endpoint. Verified end-to-end: a driver-side 20 GEL cashout hits the saga, writes ledger entries, populates bank+Yandex IDs.

---

## Last thing we worked on

Phase 3 + Option C both shipped and verified live. The cashout saga is reachable from both the admin manual-cashout modal AND the driver app's own cashout flow — same endpoint, same orchestrator, same ledger.

Latest smoke-tests via browser:
- Admin modal in Batumi #1 (Model A) → 20 GEL cashout → `Status=Completed`, bank transfer ID + Yandex transaction ID populated.
- Driver app `/cashout` → 20 GEL cashout → same flow end-to-end.

`DriverSessionService` auto-discovers a usable driver on bootstrap; will be replaced by real JWT-derived session in Phase 8.

Current park lineup in DB:

| Name | OperatingModel | Auth Limit | Bank |
|---|---|---|---|
| Tbilisi Auto Park #3 | model_a5 | ₾ 125,000 | bog |
| Tbilisi Auto Park #5 | model_a  | —         | tbc |
| Batumi Auto Park #1  | model_a  | —         | bog |

---

## Next concrete task — pick one

Phase 3 + Option C complete. Natural next steps, in roughly increasing scope:

- **Driver history page wiring.** Today `/history` still reads from `MockDataService`. A real read from `GET /api/admin/parks/{parkId}/cashouts` would close the demo loop (cash out → see it in history).
- **Driver app needs its own endpoint with driver scoping.** The driver app currently posts through `/api/admin/parks/{parkId}/cashouts`, which trusts the caller's `driverId`. Once auth lands, introduce `POST /api/driver/cashouts` that derives driverId from the JWT.
- **Real auth (Phase 8 sliver).** Issue JWTs for admin email/password and driver phone+OTP. Modal/driver both currently send no Authorization header.
- **Background balance sync worker.** Phase 2 carry-over. `IHostedService` that iterates active parks and refreshes `YandexBalanceCache` rows.
- **Phase 4 — real BOG/TBC adapters.** Blocked on sandbox credentials from the banks (typically 2–6 weeks).

Other known small issues:
- **Double-click protection at the operator level.** Modal UUID dedups same-instance retries, but a different modal open generates a new UUID — no guard against "you already cashed out to this driver 30 seconds ago".
- **`/api/admin/parks/{parkId}/smoke-test`** still exposed — remove or feature-flag before prod.

### Phase 3 — Cashout Saga (DONE)

Scope:

1. Flesh out `MockBankPayoutProvider` (currently the BOG/TBC adapters at `backend/PayTaxi.Infrastructure/Adapters/Banks/{Bog,Tbc}PayoutAdapter.cs` are stubs that throw). Realistic: simulated latency, occasional failures, idempotency-key dedup.
2. Create `CashoutOrchestrator` (the saga). Steps in pseudocode:
   ```
   reserve cashout in DB (status=Pending, idempotency_key)
   write ledger entry: park_float_debit (Pending)
   if park.OperatingModel == ModelA5:
       require park.AuthorizationLimit >= amount; else reject
       atomically decrement AuthorizationLimit
   call bank.SendPayout(park_bank_creds, amount, driver_card)
   on bank failure → status=Failed, write compensation ledger, rollback authorization
   call yandex.PostCashoutTransaction(park_id, profile_id, -amount)
   on yandex failure → status=ReviewRequired (money already moved; flag for manual reconcile)
   confirm cashout (status=Completed), write completion ledger entries
   ```
3. **Set `YandexFleet:ReadOnlyMode=false`** in `appsettings.Development.json` so the saga can post to the mock Yandex (real Yandex remains gated by `UseMock=true`).
4. Add idempotency key on `Cashouts.IdempotencyKey` (already in schema). Saga reads existing cashout by key before inserting → no double-charging on retry.
5. New endpoint: `POST /api/admin/parks/{parkId}/cashouts` — body: `{ driverId, amount, cardId }`. Returns saga result.
6. Wire the existing admin manual-cashout modal at `src/app/admin/pages/cashouts/manual-cashout/manual-cashout.ts` to POST to that endpoint instead of just emitting a signal. The 4-step UI is already built — only the `confirm()` click handler needs updating.

Demo moment: click "Confirm & submit" in the admin → cashout flows through Postgres + Yandex mock + bank mock + ledger + back. Visible end-to-end.

Deferred to a later phase:
- Wiring the driver app's cashout flow (Option C) — do once admin path is validated
- Background balance sync worker (`IHostedService`) — was on the Phase 2 list, not built
- Real `YandexFleetClient` HTTP impl — only mock exists, stub clearly marked

---

## Architectural decisions made this session (not all in CLAUDE.md)

- **Enum strategy:** string-backed C# enums + Postgres `text` column + check constraint. NOT native PG enums (`CREATE TYPE`). Reason: easier to evolve the set without `ALTER TYPE` migrations.
- **`ApiAuditLogger` uses `IServiceScopeFactory`** and is registered as singleton. Each `LogAsync` creates its own scope + DbContext. Required because the logger is called fire-and-forget from `ResilientYandexFleetClient` and would otherwise race the request's scoped DbContext.
- **Resilient decorator chain pattern:** rate-limit → retry → audit log. Inner clients (mock or real) only implement the happy path. Adding a new cross-cutting concern = new decorator, doesn't touch the client.
- **`YandexFleet:ReadOnlyMode` flag** in `appsettings`. When true, `PostCashoutTransactionAsync` throws `YandexReadOnlyModeException`. Default true. Phase 3 will flip this to false (for mock only) so the saga can call the write endpoint. The real client never enables it until paying-tenant onboarding.
- **Mock-vs-real Yandex selection** is config-driven (`YandexFleet:UseMock`). DI registers `MockYandexFleetClient` when true. Swapping in the real client when credentials arrive is one config change — no code change.
- **Mock data is keyed by Yandex external IDs** (`YandexParkId`, `DriverProfileId`), not internal Guids. Mirrors how the real API works. The DB seed uses matching IDs so the mock client can resolve drivers by their Yandex profile ID.
- **Frontend architecture:** `app.html` is a bare `<router-outlet />`. Two layout components — `DriverLayoutComponent` (`shared/layouts/driver-layout/`) wraps `/dashboard`, `/cashout`, `/history`, `/profile`; `AdminLayoutComponent` (`admin/layout/`) wraps `/admin/**`. Login routes (`/login`, `/admin/login`) live outside the layouts (full-screen).
- **Mock data location split:** `core/mock/data.ts` (driver-facing types + driver app mocks); `admin/mock/admin-data.ts` (admin-only extended types: `AdminDriver`, `QueueCashout`, `ActivityEvent`, KPIs). Both consume the same core types like `CashoutStatus` and `BankType`.
- **CSS design tokens** for admin live in `src/styles.scss` (`--admin-bg`, `--admin-surface`, `--admin-border`, `--admin-sidebar-w`, etc). Driver and admin share the `--color-*`, `--text-*`, `--space-*` scales.
- **Float Monitor for Model A.5** (admin overview) needs rethinking: currently shows "park bank account · live balance" which is correct for Model A. For Model A.5 the more important number is `AuthorizationLimit` remaining (the actual control surface). When wiring this to real data, surface BOTH: bank balance (informational, queried from bank API where available) and authorization remaining (the gating value).
- **Phone encryption** is currently plaintext in `PhoneEncrypted` column. SHA-256 in `PhoneHash` is real. Real AES + key management deferred to Phase 8.

---

## Open issues / half-finished

- **No real auth.** Driver OTP login and admin email/password are both mocked. JWT issuance not implemented. `Program.cs` configures JWT validation but no endpoint issues tokens.
- **Frontend not wired to backend.** Both driver and admin apps still consume in-browser mock data via signals. Phase 3 wires only the admin's manual-cashout modal — full data wiring is a separate pass.
- **`IsActive` column not yet dropped.** Two-phase migration: column kept, `Status` is the new source of truth. Follow-up migration once no code reads `IsActive` (the `AdminParksController` was already migrated to read `Status` instead).
- **`MockBankPayoutProvider` doesn't exist.** The BOG and TBC adapters at `backend/PayTaxi.Infrastructure/Adapters/Banks/` still throw `NotImplementedException`. Phase 3 must build the mock so the saga has a counterparty.
- **No background balance sync worker.** Listed in Phase 2 plan but not built. Should be an `IHostedService` that iterates active parks and refreshes `YandexBalanceCache` rows on a schedule, respecting rate limits.
- **Subdomain routing not implemented.** `Slug` column exists with check constraint. Tenant resolution at the request layer (e.g., `{slug}.paytaxi.ge` or `/p/{slug}/...`) is TBD.
- **Translation JSONs (`ka.json`, `ru.json`)** were being edited by the user in parallel — exact state unknown, don't touch without checking.
- **API endpoint at `/api/admin/parks/{parkId}/smoke-test`** is a development helper. Remove or move under a feature flag before production.

---

## Pitfalls / surprises future-Claude should know

- **CSS custom properties in shorthand: any undefined variable silently zeros the entire shorthand.** Caused the "History H cropped" bug. `padding: var(--space-8) var(--space-6) var(--space-7)` evaluated to `0` because `--space-7` wasn't defined. Always define every space variable referenced or use longhand.
- **Angular doesn't reset scroll between routes by default.** Required `withInMemoryScrolling({ scrollPositionRestoration: 'top' })` in `app.config.ts`. Without this, scrolling down on the dashboard then tapping History leaves the History header off-screen, looking like it's cropped.
- **EF Core `Expression<Func<…>>` lambdas can't contain `switch` expressions or `throw` expressions.** Got CS8514/CS8188 errors. Fix: put the logic in a static helper method (e.g., `Converters.ToText(v)`) and call it from the lambda. Method calls are fine in expression trees.
- **Fire-and-forget audit logging racing a scoped DbContext.** `_ = _audit.LogAsync(...)` while controller holds the same scoped `AppDbContext` → "A second operation was started on this context instance" exception. Fix: audit logger gets `IServiceScopeFactory`, opens its own scope per write, registered as singleton.
- **Postgres locale on this machine returns errors in Georgian.** `შეცდომა` = "error". When troubleshooting psql failures, key off SQL error codes (23514 = check constraint violation, 23505 = unique violation) rather than parsing the message.
- **`dotnet-ef` global tool is at `/c/Users/Home/.dotnet/tools/dotnet-ef.exe`** — NOT on PATH. Invoke with full path. Version mismatch (10.0.8) with EF Core 8 is fine for our usage.
- **`ASPNETCORE_ENVIRONMENT=Development` must be set explicitly** when running `dotnet run` without `--launch-profile`. Otherwise the API loads `appsettings.json` (placeholder creds) instead of `appsettings.Development.json` (`postgres/postgres`).
- **Backend API port is 5196 (http) / 7075 (https).** Frontend dev server is 4200. CORS is configured for `localhost:4200` only.
- **Frontend mock has 25 drivers, backend mock has 24** (3 parks × 8). Different totals — they're separate datasets. Don't try to reconcile them.
- **The user mixes Georgian (Mkhedruli), Latin transliteration, and English** in mock data. Always use Noto Sans which supports all three scripts. Don't substitute fonts even for "minor" pages.
- **Don't reduce the dashboard balance amount font size below `clamp(4rem, 18vw, 5.5rem)` with `letter-spacing: -0.05em`.** The big balance number is a deliberate visual choice from the redesign and the user pushed for it explicitly.
- **`/frontend-design` and `/ui-ux-pro-max` skills are available.** Were invoked for the Phase 1 UI overhaul. Re-invoke when doing significant new UI work.
- **User's preferred review pattern:** propose 2-4 options crisply with a marked recommendation, then they pick. Don't dump every consideration; lead with the recommendation and brief reasoning.
- **EDB-built Postgres 17 installed locally.** Superuser `postgres` / password `postgres`. Database `paytaxi_dev`. Psql at `/c/Program Files/PostgreSQL/17/bin/psql.exe`.

---

## Routes — current state

**Driver app (`http://localhost:4200/`)**
- `/login` — phone + OTP, mocked
- `/dashboard` — balance hero + recent activity
- `/cashout` — 3-step keypad → card → confirm
- `/history` — filter tabs, left-bar tx rows
- `/profile` — cards, notifications, language, logout

**Admin (`http://localhost:4200/admin/`)**
- `/admin/login` — split brand panel + email/password, mocked
- `/admin/overview` — KPIs + float card with hourly chart + needs-attention + activity feed
- `/admin/cashouts` — segmented status tabs + date range + expandable rows + manual cashout modal
- `/admin/drivers` — sortable table + slide-in drawer with stats and recent cashouts
- `/admin/onboarding` — stepper with Yandex lookup + confirm + success

**Backend API (`http://localhost:5196/`)**
- `GET /api/admin/parks` — list
- `GET /api/admin/parks/{parkId}/drivers` — drivers + Yandex balances
- `GET /api/admin/parks/{parkId}/drivers/{driverId}/balance`
- `GET /api/admin/parks/{parkId}/drivers/{driverId}/transactions?days=14`
- `GET /api/admin/parks/{parkId}/smoke-test` — verifies rate limiter (remove before prod)
- Swagger at `/swagger`
