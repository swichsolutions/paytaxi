# PayTaxi — deployment runbook

Two deployables: the **.NET 8 API** (container) and the **Angular 21 app** (static + SSR, any static host — hosting provider not yet chosen).
Postgres 17 is the only stateful dependency.

## 1. Secrets (never in git)

| Setting | How to supply | Notes |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | env `ConnectionStrings__DefaultConnection` or Key Vault | Postgres 17 |
| `Jwt:Key` | env `Jwt__Key` | ≥ 32 chars, random |
| `Encryption:Key` | env `Encryption__Key` | base64 of 32 random bytes — **losing it makes phones, IBANs and bank credentials unreadable**. Back it up in the vault. Generate: `dotnet run --project backend/PayTaxi.Api -- --new-encryption-key` or `openssl rand -base64 32`. |
| `Settlement:SwichIban` | env `Settlement__SwichIban` | Swich Solutions LLC's TBC IBAN (receives the nightly fee share) |
| `Invoice:OperatingEntityTaxId` | env | Swich's tax id printed on invoices |
| Yandex Fleet credentials | admin UI → Settings (per park) | encrypted at rest |
| TBC DBI credentials + .pfx | admin UI → Settings → payout account `credentialsJson` (per park) | encrypted at rest |

Azure Key Vault: set `KeyVault__Uri=https://<vault>.vault.azure.net/`; the API loads every secret as
configuration using managed identity (`DefaultAzureCredential`). Secret names use `--` for `:` (e.g. `Jwt--Key`).

Startup **refuses to run in Production** when the JWT key, encryption key or connection string are missing or
still placeholders.

## 2. API

```bash
docker build -t paytaxi-api ./backend
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=...;Database=paytaxi;Username=...;Password=..." \
  -e Jwt__Key="..." -e Encryption__Key="..." \
  -e Cors__AllowedOrigins__0="https://app.paytaxi.ge" \
  paytaxi-api
```

- Migrations run automatically at startup (`MigrateAsync`), followed by idempotent seed/upgrade steps and the
  encryption migrator (re-encrypts any plaintext PII rows once).
- Recommended host: **Azure App Service (Linux, container)** or **Azure Container Apps** in West Europe, with
  **Azure Database for PostgreSQL Flexible Server** (zone-redundant, 7-day PITR) and **Key Vault**. Alternatives that
  work unchanged: any Docker host + managed Postgres (Railway, Fly.io, Hetzner + Supabase-style Postgres).
- Behind a reverse proxy the API honours `X-Forwarded-*` and redirects to HTTPS outside Development.
- Health: `GET /swagger` is Development-only; use `GET /api/admin/parks` with a token, or add `/healthz` when a probe is needed.
- Scale: keep **one instance** while the payout queue and settlement workers run in-process (they are safe against
  double-processing via atomic claims, but a single instance keeps ordering per park trivial).

Local stack: `docker compose up --build` with a `.env` containing `DB_PASSWORD`, `JWT_KEY`, `ENCRYPTION_KEY`
(see `docker-compose.yml`).

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
3. TBC: park's DBI username/password/.pfx on the payout account; `BankPayout:UseMock=false`; sandbox first
   (`environment: "test"` in the credentials JSON; `BankPayout:Tbc:TrustTestServerCertificate=true` if the test host's
   root cert is not installed).
4. `ReadOnlyMode=false`; whitelist pilot: 2–3 drivers, real money, reconcile our ledger = TBC statement = Yandex history.
5. Run one real nightly settlement to Swich's IBAN; check the Settlements page.
6. Only after the license agreement is signed: onboard all drivers.

## 5. Operations

- **Logs**: structured console logging (`PayTaxi` category at Information). Ship to the host's log sink.
- **Backups**: managed Postgres PITR + weekly logical dump; the encryption key lives in the vault, not in the DB.
- **Audit**: `ApiAuditLogs` records every Yandex call (ToS 3.7). Retain ≥ 2 years.
- **Key rotation**: introduce `enc:v2:` in `FieldEncryptor`, keep v1 for reads, re-encrypt via the migrator.
