# Testing

Three layers, all run by GitHub Actions on every push (`.github/workflows/ci.yml`).

| Layer | What | Command | Needs |
|---|---|---|---|
| Unit | Pure logic: IBAN, phone, name matching, settlement split, encryption, TBC/Yandex client parsing (monthly invoice PDF is covered by the integration layer) | `dotnet test backend/PayTaxi.sln` (also runs the layer below) | nothing |
| Integration | The real API booted in-process against a fresh `paytaxi_test` Postgres DB with the Development seed: ownership rule, cashout saga, auth, authorization | same command | Postgres on localhost (`postgres`/`postgres`) or `PAYTAXI_TEST_DB` |
| End-to-end | Driver app + admin console in a real browser at phone and desktop widths | `npm run e2e` | API running on :5196 in Development; Angular started automatically |

## Running locally

```powershell
# backend: unit + integration (drops and recreates paytaxi_test every run)
dotnet test backend/PayTaxi.sln

# e2e: start the API first (your usual dotnet run), then
$env:PW_CHROMIUM = "C:/Users/<you>/AppData/Local/ms-playwright/chromium-NNNN/chrome-win64/chrome.exe"  # only if the bundled browser is missing
npm run e2e          # or: npm run e2e:ui  for the interactive runner
```

The e2e suite logs drivers in through the real OTP screen using the dev code the API echoes. A phone
gets 3 codes per 10 minutes by default and 10 codes (and 10 wrong codes) per rolling day; raise
`Auth:OtpMaxPerWindow`, `Auth:OtpMaxPerDay` and `Auth:SignInFailuresBeforeLock` in your
`appsettings.Development.json` (as CI's e2e job does) so repeated local runs on the same database do not trip them — the
day limits are kept in `PhoneSignInEvents` for 24 hours. Production keeps the defaults.

## Conventions

- Integration tests live in `backend/PayTaxi.Tests/Integration`, share one `ApiFixture` (collection
  `"api"`) and mint driver tokens directly; only the auth/session tests go through OTP. A second host with
  different startup settings is an `ApiFixture.Secondary` subclass in its own collection (see
  `FlakyBankFixture`). Collections run one at a time; the encryption key is a fixed test constant.
- Tests create their own destinations with unique IBAN tails and delete them afterwards, so the seed
  stays clean for the next test.
- Seeded drivers used by tests: `yp_tb3_001..008` (see `SeedData.cs`); `yp_tb3_005` is reserved for the
  OTP-cap test. Admin logins: `ops@swich.dev / swich2026!`, `levan@operator.local / operator1!`,
  `manager@<park-slug>.local / park{1,2,3}!`.
- When a manual test pass finds a bug, add the case here before fixing it — that is how the
  2026-09-19/20 findings became permanent.
