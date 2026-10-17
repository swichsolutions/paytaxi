# PayTaxi — deployment runbook

Two deployables: the **.NET 8 API** (container) and the **Angular 21 app** (static + SSR, any static host — hosting provider not yet chosen).
Postgres 17 is the only stateful dependency.

## 1. Secrets (never in git)

| Setting | How to supply | Notes |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | env `ConnectionStrings__DefaultConnection` or Key Vault | Postgres 17 |
| `Jwt:Key` | env `Jwt__Key` | ≥ 32 chars, random |
| `Encryption:Key` | env `Encryption__Key` | base64 of 32 random bytes — **losing it makes phones, IBANs and bank credentials unreadable**. Back it up in the vault. Generate: `dotnet run --project backend/PayTaxi.Api -- --new-encryption-key` or `openssl rand -base64 32`. |
| `Encryption:HashKey` | env `Encryption__HashKey` | base64 of 32 random bytes, generated the same way — **a different key from `Encryption:Key`** (startup checks). It keys the lookup hashes stored beside the encrypted phone numbers and IBANs (`Drivers.PhoneHash`, `OtpCodes.PhoneHash`, `BankCards.IbanHash`) and the stored one-time sign-in codes (`OtpCodes.CodeHash`), HMAC-SHA256. Without it those would be plain SHA-256, which gives every phone number back to anyone with a copy of the database (Georgian mobile numbers are tried exhaustively in seconds). Keep it secret like the encryption key; losing it is not fatal (see Key rotation in §5). |
| `Settlement:SwichIban` | env `Settlement__SwichIban` | Swich Solutions LLC's TBC IBAN (receives the parks' monthly transfers, Annex 3 §3) |
| `Settlement:SwichHolderName` | appsettings (default `შპს სვიჩსოლუშენსი`) | the account's registered holder — sent as the beneficiary name on every park's transfer and printed on the monthly invoice; must match the bank's records |
| `Invoice:OperatingEntityTaxId` | env | Swich's tax id printed on invoices |
| `Sms:SmsOffice:Key` | env `Sms__SmsOffice__Key` | the smsoffice.ge API key (profile page). Without it the API refuses to start outside Development unless `Sms:AllowLogSender=true` (a dry run: no SMS, sign-in codes in the API response). See §2a. |
| Yandex Fleet credentials | admin UI → Settings (per park) | encrypted at rest |
| TBC DBI credentials + .pfx | admin UI → Settings → payout account `credentialsJson` (per park) | encrypted at rest. The certificate goes in as `certificatePfxBase64` (≤ 64 KB). `certificatePath` is refused unless `BankPayout:Tbc:CertificateDirectory` names a mounted secrets directory, and then only for files inside it — park staff write this JSON, and the API must not open whatever host file they name. |

Azure Key Vault: set `KeyVault__Uri=https://<vault>.vault.azure.net/`; the API loads every secret as
configuration using managed identity (`DefaultAzureCredential`). Secret names use `--` for `:` (e.g. `Jwt--Key`).

Startup **refuses to run in every environment except Development** (Staging included — `appsettings.json` ships no
usable JWT key any more) when the JWT key, encryption key, hash key or connection string are missing, placeholders or one
of the published CI/test keys, when two of the keys are the same, when a rail is the mock (`YandexFleet:UseMock` /
`BankPayout:UseMock`) without an explicit `AllowMockRails=true` for a dry run, when `Proxy:TrustForwardedHeaders` is on
without the proxy's address range, or when there is no SMS gateway key (`Sms:SmsOffice:Key` empty or a placeholder)
without an explicit `Sms:AllowLogSender=true` for a dry run. It also refuses to start when `Encryption:Key` decrypts none of the encrypted values
already in the database (a wrong or regenerated key): starting would write new rows under a key the old ones cannot be
read with. Generate keys with the commands above, never by hand.

**No test rails on the live server.** The mock bank and a bank's sandbox exist for testing only: a payout "completed"
through either moves no money while the driver's Yandex balance is debited, and a settlement "sent" through either never
pays Swich. With real rails (every environment but Development, unless `AllowMockRails=true` for a dry run):

- the mock bank is not registered at all; an account whose provider has no adapter on the server never completes
  anything — its payouts wait (`BANK_ADAPTER_MISSING`), its park's settlement fails, reconciliation reports the account
  as unread and its payouts as `missing_in_bank`;
- the API refuses provider `mock` (`400 invalid_provider`) and accounts at any bank but TBC and BoG
  (`400 unsupported_bank`) — on park creation and on payout-account create/update; the console offers TBC and BoG only;
- TBC credentials are for production: an omitted `environment` **is** production (it used to be the test host), and
  credentials naming another environment are refused on write (`400 test_environment_not_allowed`) and at every bank
  call (`TBC_TEST_ENVIRONMENT_NOT_ALLOWED`; the payout waits, nothing reaches the sandbox);
- startup refuses to run while an active payout account names the mock (or any provider without an adapter) — set
  each one's provider to its bank or deactivate it first — and logs an error for every TBC account whose credentials are
  still for the test environment.

A staging host may test a park's credentials against TBC's sandbox with `BankPayout:AllowTestEnvironment=true`. The
startup guard allows that switch only on a host named Staging (`ASPNETCORE_ENVIRONMENT=Staging`): under any other name —
Production, but also "Prod", "Live" or none — the API refuses to start with it. Name the live host Production.

## 2. API

```bash
docker build -t paytaxi-api ./backend
docker run -p 127.0.0.1:8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=...;Database=paytaxi;Username=...;Password=..." \
  -e Jwt__Key="..." -e Encryption__Key="..." -e Encryption__HashKey="..." \
  -e Sms__SmsOffice__Key="..." \
  -e Cors__AllowedOrigins__0="https://app.paytaxi.ge" \
  -e Proxy__KnownNetworks__0="172.16.0.0/12" \
  paytaxi-api
```

Publish the port on `127.0.0.1` (or not at all) and put the TLS-terminating reverse proxy in front: Docker's published
ports bypass host firewalls, and the API trusts `X-Forwarded-For` only from `Proxy:KnownNetworks` / `Proxy:KnownProxies`
(the proxy's own range — the Docker bridge above; App Service / Container Apps: their front-end subnet). Set
`Proxy:ForwardLimit` to the number of proxies in front (CDN + proxy = 2), or every client shares the CDN edge's
sign-in rate limit. Without a proxy, set `Proxy__TrustForwardedHeaders=false`.

- Migrations run automatically at startup (`MigrateAsync`), followed by idempotent seed/upgrade steps and the
  encryption migrator (re-encrypts any plaintext PII rows once). Its first step brings every lookup hash to the current
  hash key: phone and IBAN hashes made without it (a database from before keyed hashing) or with an earlier key are
  recomputed from the decrypted values, and sign-in codes made without it are deleted (they cannot be re-hashed; a driver
  in the middle of signing in requests a new code). Each stored hash names its key, so later starts find nothing to do.
  A driver whose phone would hash to another driver's (a duplicate number) is left as it was and logged as an error
  (`Lookup hashes: driver … has the same phone as another driver`) instead of stopping the start — that driver cannot
  sign in until the duplicate is resolved. Of two such rows the one that could sign in before keeps the number.
- Recommended host: **Azure App Service (Linux, container)** or **Azure Container Apps** in West Europe, with
  **Azure Database for PostgreSQL Flexible Server** (zone-redundant, 7-day PITR) and **Key Vault**. Alternatives that
  work unchanged: any Docker host + managed Postgres (Railway, Fly.io, Hetzner + Supabase-style Postgres).
- Behind a reverse proxy the API honours `X-Forwarded-*` from that proxy only (see above) and redirects to HTTPS outside Development.
- Health: `GET /swagger` is Development-only; use `GET /api/admin/parks` with a token, or add `/healthz` when a probe is needed.
- Scale: keep **one instance** while the payout queue and settlement workers run in-process (they are safe against
  double-processing via atomic claims, but a single instance keeps ordering per park trivial).

Local stack: `docker compose up --build` with a `.env` containing `DB_PASSWORD`, `JWT_KEY`, `ENCRYPTION_KEY`,
`HASH_KEY` (see `docker-compose.yml`; it runs as Production by default, so both keys are required and must differ). It
binds to `127.0.0.1` and defaults to the real rails; a dry run on the mocks needs `YANDEX_USE_MOCK=true`,
`BANK_USE_MOCK=true` **and** `ALLOW_MOCK_RAILS=true` (the start then logs a warning — mock payouts move no money).

## 2a. SMS (smsoffice.ge)

Sign-in codes and the payout-account notices go out as SMS through **smsoffice.ge** (`Sms:Provider=smsoffice`, the
production default). One platform account, Swich's — not per park. Adapter: `SmsOfficeSender`
(`backend/PayTaxi.Infrastructure/Adapters/Sms/`), behind `ISmsSender`.

1. **Key**: sign in at smsoffice.ge, profile page → API key. Supply it as `Sms__SmsOffice__Key` (`.env`:
   `SMS_OFFICE_KEY`) or in Key Vault as `Sms--SmsOffice--Key`. Make sure API use is enabled on the profile (the gateway
   answers error 120 otherwise).
2. **Sender**: `Sms:SmsOffice:Sender` is what the driver's phone shows. `smsoffice` (the default) works for every
   account at once; a custom sender such as `PayTaxi` (≤ 11 characters of `A–Z a–z 0–9 . -`) has to be **approved by
   smsoffice first** — until it is, every message is refused with error 150 and nobody gets a code. Switch to the custom
   name only after the approval mail.
3. **Balance**: smsoffice is prepaid, in messages. Georgian and Russian texts are Unicode: a sign-in code is one
   message, a payout-account notice two (the texts are kept to ≤ 134 characters). Top up on smsoffice.ge. The API reads
   the balance at start and every 6 hours (`smsoffice balance: N messages` in the log) and raises the alert below
   `Sms:SmsOffice:LowBalanceWarnAt` (default 50).
4. **The alert**: when the gateway refuses to send for a reason no retry fixes — insufficient balance (20), unknown key
   (80), API not enabled (120), sender not found or not understood (150, 110) — or the balance is below the warning
   line, the API opens one platform-wide **P2 incident** (`sms-gateway`, Fault: platform, title "SMS gateway: sign-in
   codes are not going out" / "…balance running low"). It shows on the red alert banner of every console (Swich, the
   operator, every park manager — the way a Yandex outage does) and in the incident log, with the cause. It closes
   itself when a message is accepted again or the balance reads healthy again (an incident someone moved to a park is
   left to them). The gateway is best effort: a refused SMS never fails the sign-in request or the account change; the
   log line (`SMS (signin, driver …, tag signin-…) refused by smsoffice: error 20 — …`) carries the purpose, the driver
   id, our reference tag and the error code — never the phone or the text.
5. **Delivery reports (later)**: smsoffice can POST a delivery report (reference, status, reason, destination,
   timestamp, operator) to a URL of ours, which must answer `OK`; `GET /api/v2/getMessageStatus/` answers per reference
   too. Not wired — there is no public URL yet. The reference tag we send (`signin-k7m2p9qx`, `payoutaccou-…`) is what a
   report would be matched on; it never contains the phone or a driver id. Until then, "did the driver get the code"
   is answered by the smsoffice console.
6. **Development**: without a key the API runs the log sender — nothing is sent, the sign-in code comes back as
   `devCode` in the request-otp response (the app shows "DEV CODE · 123456"), and the tests and the e2e runs rely on
   that. **With `Sms:Provider=smsoffice` and a key in `appsettings.Development.json`, Development sends real SMS**: the
   code is then not in the response or the log, and every code costs a message — test only with your own number. The
   integration tests pin the log sender regardless of that file (`ApiFixture`), so a test run never sends.

## 3. Frontend

`src/environments/environment.prod.ts` holds `apiBase` (the API's public origin). Set it, then:

```bash
npm ci
npm run build          # production configuration → dist/paytaxi/browser (+ server for SSR)
```

Frontend host: not decided yet (Netlify was considered and dropped on 2026-09-19). Any static host works for the browser
bundle; SSR is optional. The API's `Cors:AllowedOrigins` must include the frontend origin(s).

## 4. Go-live checklist (from PAYTAXI-CONTEXT.md §8)

1. `YandexFleet:UseMock=false`, `ReadOnlyMode=true` — roster and balances of Levan's park look right.
2. Confirm the Yandex transaction category paypro used (park's Fleet history) → `YandexFleet:CashoutCategoryId`.
3. TBC sandbox — **on a staging host only** (`ASPNETCORE_ENVIRONMENT=Staging`; any other name refuses it): `BankPayout:UseMock=false`,
   `BankPayout:AllowTestEnvironment=true`, the park's sandbox DBI user on its payout account with `"environment": "test"`
   in the credentials JSON (`BankPayout:Tbc:TrustTestServerCertificate=true` if the test host's root cert is not
   installed). Run the sandbox checks there (docs/TBC-BRANCH-VISIT.md).
3a. Production: replace every park's credentials with its **production** DBI user, password and .pfx (`"environment":
   "production"`, or no environment at all). Every active payout account must name its bank (`tbc` / `bog`), never `mock`:
   the API starts only then, and its log names any TBC account still holding test credentials.
4. `ReadOnlyMode=false`; whitelist pilot: 2–3 drivers, real money, reconcile our ledger = TBC statement = Yandex history.
5. Run one real nightly settlement to Swich's IBAN; check the Settlements page.
6. Only after the license agreement is signed: onboard all drivers.
7. Operator (licensee staff) accounts: there is no console screen for them yet — they are created by hand, with
   `Role = 'operator'` and **`LicenseeId` set to the licensee's id**. An operator account works **only while that
   licensee is in force**: bound to none, or once the licensee is deactivated, it cannot sign in (`403
   licensee_not_in_force`, the console says so) and a token it holds is refused on every request (re-read from the
   database, cached `Auth:AdminRecheckSeconds`, default 30 s; the licensee's PATCH drops its operators' cached entries,
   so the cut-off is immediate). The accounts are not deactivated — reactivating the licensee restores them. Every start
   logs an operator bound to no licensee as an error (`Operator account … is bound to no licensee`). In force, it sees
   and changes only its licensee's figures and reaches only that licensee's parks — never a Swich-direct park or another
   licensee's. **After the agreement ends** the licensee's staff no longer read its statements and shared costs in the
   console: Swich sends them the remaining statements (the statement PDF) — confirm with the owner/lawyer that this
   satisfies §8.4/§2.6 before deactivating a licensee. The API still refuses a second licensee in force (`409
   second_active_licensee`, database index `IX_Licensees_SingleActive`): the contract calendar and the incident metrics
   assume one. A park's settlement rows of
   another (former) licensee's statements show it the park's figures only, not that agreement's.
8. SMS: smsoffice.ge is wired (§2a). Before the pilot: the key in `Sms__SmsOffice__Key`, API use enabled on the
   profile, the sender approved (or leave `smsoffice`), the balance topped up, and one real sign-in from a phone.
   Besides the sign-in code, the driver is texted about every change of their payout accounts (added, made the
   default, removed — by them or from the console; an account added as the default is told as "payouts now go there"),
   in the language the app last told the server (Georgian when unknown). A slow gateway holds a sign-in request or a
   change up for at most 5 s (the SMS then finishes in the background). Delivery reports are not wired yet (§2a.5).
9. Privacy notice: the drivers' notice (legal text) does not yet mention two new records — the app language
   (`Drivers.PreferredLanguage`, from the app's Accept-Language, used for the SMS) and the per-phone sign-in events
   (`PhoneSignInEvents`: every number that asks for a code, kept a day; a lockout until it ends). Have the lawyer or
   owner update it before go-live (Georgian PDP law, Arts 24/25).

## 4a. Annex 3 §7 — a park revokes the platform's authorisations

A park may revoke the authorisations of Annex 3 §2 (payouts from its account) and §3 (the monthly transfer of Swich's
share) by written notice at least 30 days ahead. Swich or the operator records it on the park (Settings → Park status →
"Record a revocation": the day the notice arrived, the first day without the authorisations, the reason). Until that day
it can be withdrawn; from that day (Tbilisi) on, the platform initiates nothing from the park's account:

- new cashouts are refused (`authorisation_revoked`; the driver app says why);
- payouts waiting in the queue are not sent — they wait with `AUTHORISATION_REVOKED` until Swich or the operator cancels
  them in Cashouts (the amount goes back to the driver's Yandex balance);
- the monthly settlement collects nothing from the park: its row fails `AUTHORISATION_REVOKED`, is never retried, and is
  closed with "Record outside payment" once the licensee pays it (§2.5) — or the park itself, for a Swich-direct park.

The alert banner shows Swich and the operator one line per revoked park while payouts or shares still wait on it.

## 5. Operations

- **Logs**: structured console logging (`PayTaxi` category at Information). Ship to the host's log sink.
- **Backups**: managed Postgres PITR + weekly logical dump; the encryption and hash keys live in the vault, not in the DB.
- **Audit**: `ApiAuditLogs` records every Yandex and bank call (Yandex ToS 3.7: Yandex may audit API use). Kept 2 years,
  then deleted by the retention worker (below).
- **Retention** (`Retention` section; `RetentionWorker`). Deletes exactly what the drivers' privacy notice says is kept
  only for a while: sign-in codes on **every poll** (`PollIntervalSeconds`, default 300 s — the notice says they are deleted
  within a day, and a nightly-only purge would keep a code issued just after it for up to 48 h), everything else once a night
  at `RunAtLocalTime` Tbilisi time (default 03:30). The nightly line logs how many rows of each kind were deleted, the
  sign-in codes deleted by the polls since the previous night included (`Retention pass: deleted …`):

  | Setting | Default | Deletes |
  |---|---|---|
  | `SignInCodeHours` | 24 | one-time sign-in codes (`OtpCodes`), counted from when the code was issued — on every poll, so a code lives at most this long plus one `PollIntervalSeconds` |
  | (fixed) | 24 h | the per-phone sign-in events (`PhoneSignInEvents`: codes issued, failed verifications, lockouts — the phone only as its keyed hash) a day after they happened, a lockout not before it ends — on every poll |
  | `DeviceIdleDays` | 90 | a driver's device session (`DriverSessions`) not used for this long — the driver signs in with an SMS code again |
  | `EndedSessionDays` | 1 | a session this long after it ended: signed out, cut off (suspension, detected token copy) or expired. A token replaced by a refresh stays until its chain expires (≤ 90 days after the SMS sign-in, `Auth:TrustedDeviceDays`): presenting it again is how a copied token is detected |
  | `CallLogMonths` | 24 | the log of calls to Yandex and the banks (`ApiAuditLogs`) |
  | `SignInHistoryMonths` | 24 | the sign-in history (`DriverSignIns`: when a driver signed in with an SMS code, and the device hint) |

  A period of 0 or less (`EndedSessionDays`: below 0) keeps that kind for good. Also: `Enabled`, `TimeZoneId`,
  `BatchSize` (rows per DELETE, default 5000), `PollIntervalSeconds`. The worker **never** deletes financial
  records — cashouts, invoices, the ledger, settlements and monthly statements, payout accounts (a removed one stays:
  cashouts point at it) — nor drivers. Their expiry under the accounting rules and de-personalising deactivated drivers
  are an open point, not automated yet.
- **Key rotation**: introduce `enc:v2:` in `FieldEncryptor`, keep v1 for reads, re-encrypt via the migrator.
- **Sign-in limits** (`Auth` section): per phone, over a rolling day and across codes — at most `OtpMaxPerDay` codes (10,
  on top of `OtpMaxPerWindow` per `OtpWindowMinutes`), and after `SignInFailuresBeforeLock` verifications that did not sign
  in (10) the phone can neither ask for nor enter a code for `SignInLockHours` (4). The ten are counted over the whole
  rolling day, not since the last lock: a miss after a lock ends, while the day's ten still count, locks it again (the
  right code still signs in). Both answer `429` with a code
  (`daily_code_limit` / `sign_in_locked`) and `Retry-After`, for unknown numbers exactly as for drivers'. A lock of a
  driver's phone is logged (`SECURITY: sign-in locked …`) and shown on that park's alert banner while it lasts. To lift
  one early, delete the phone's `PhoneSignInEvents` rows of kind `locked` (find the hash through the driver's
  `PhoneHash`).
- **Driver sign-out**: suspending a driver (any status but Active) or changing their phone revokes every session of the
  driver in the same save (`RevokedReason = 'admin_revoked'`) and the access token in hand stops working at the next
  request (`DriverTokenCheck`, cached `Auth:DriverRecheckSeconds`, default 30 s; the console's own change applies at once).
  The hash key (`Encryption:HashKey`) rotates on its own: put a new key in the vault and restart. Every stored hash names
  the key that made it (`h1:<key id>:…`, the id derived from the key), so the startup migrator re-hashes all phones and
  IBANs from their decrypted values and deletes the pending sign-in codes; nothing else changes (outstanding driver tokens
  stay valid). Rotate it at once if it may have leaked: with the key and a database copy, phone numbers can be tested
  again. Losing it is recoverable the same way, as long as the encryption key is intact.
