import { test, expect, APIRequestContext } from '@playwright/test';
import { adminLogin, ADMINS } from './helpers';

/**
 * The red banner: a failed monthly settlement transfer must be on screen for whoever opens the console.
 *
 * There is no "make a settlement fail" endpoint, so the test manufactures one the way it happens in
 * life: a driver cashes out (so the park owes Swich a share this month), the park's payout accounts are
 * switched off, and Swich settles the month — that park's transfer fails with NO_PARK_ACCOUNT.
 * Settlement is monthly (licence agreement §2.5); a Development API lets Swich settle the RUNNING month
 * early (Settlement:AllowOpenMonthRun — never in production), which is what makes this testable without
 * waiting for the month to end. Accounts are switched back on afterwards; the Failed transfer is
 * deliberately left in place — the nightly retry (or "Retry transfer" on the Settlements page) picks it
 * up, a repeat of this test re-fails it, and a dev console shows a realistic alert. CI starts from an
 * empty database every run.
 */
const API = 'http://localhost:5196';
const PARK_SLUG = 'tbilisi-auto-park-5';

async function json(r: Promise<any>) { return (await r).json(); }

async function adminToken(request: APIRequestContext) {
  return (await json(request.post(`${API}/api/admin/auth/login`, { data: ADMINS.swich }))).token as string;
}

async function driverToken(request: APIRequestContext, phone: string) {
  const otp = await json(request.post(`${API}/api/driver/auth/request-otp`, { data: { phone } }));
  const v = await json(request.post(`${API}/api/driver/auth/verify-otp`, { data: { phone, code: otp.devCode, deviceLabel: 'e2e' } }));
  return v.token as string;
}

/** This park's line in the month view: in its licensee's statement, or as a Swich-direct park. */
function lineFor(view: any, parkId: string): { statement: any | null; line: any | null; direct: any | null } {
  for (const st of view.statements ?? []) {
    const line = st.parks.find((p: any) => p.parkId === parkId);
    if (line) return { statement: st, line, direct: null };
  }
  const direct = (view.direct ?? []).find((d: any) => d.parkId === parkId) ?? null;
  const statement = direct ? null : (view.statements ?? [])[0] ?? null;
  return { statement, line: null, direct };
}

test.describe('alert banner', () => {
  test('a failed settlement is announced, links to the fix, and can be dismissed until something new happens', async ({ page, request }) => {
    const token = await adminToken(request);
    const auth = { Authorization: `Bearer ${token}` };

    const parks = await json(request.get(`${API}/api/admin/parks`, { headers: auth }));
    const park = (Array.isArray(parks) ? parks : parks.parks).find((p: any) => p.slug === PARK_SLUG);
    expect(park, `seeded park ${PARK_SLUG}`).toBeTruthy();

    // 1. Fees this month: an active driver with a destination cashes out 5 GEL.
    const roster = await json(request.get(`${API}/api/admin/parks/${park.id}/drivers`, { headers: auth }));
    // The richest active driver: a fresh database may need several cashouts (see step 2).
    const driver = roster.drivers
      .filter((d: any) => d.status === 'Active' && d.cards?.some((c: any) => c.isDefault))
      .sort((a: any, b: any) => (b.yandex?.balance ?? 0) - (a.yandex?.balance ?? 0))[0];
    expect(driver, 'an active seeded driver with a default account').toBeTruthy();
    const dtok = await driverToken(request, driver.phone);
    // Pay to an account at a bank the park actually holds a payout account with (as the app would offer).
    const me = await json(request.get(`${API}/api/driver/me`, { headers: { Authorization: `Bearer ${dtok}` } }));
    const supported = new Set(me.park.supportedBanks.map((b: any) => b.bankCode));
    const card = me.cards.find((c: any) => c.isDefault && supported.has(c.bankCode)) ?? me.cards.find((c: any) => supported.has(c.bankCode));
    expect(card, `driver needs an account at one of ${[...supported].join(',')}`).toBeTruthy();

    const cashOut = async () => {
      const res = await request.post(`${API}/api/driver/cashouts`, {
        headers: { Authorization: `Bearer ${dtok}` }, data: { cardId: card.id, amount: 5, idempotencyKey: crypto.randomUUID() },
      });
      let saga = await res.json();
      expect(saga.cashoutId ?? saga.code, `cashout response: ${JSON.stringify(saga).slice(0, 300)}`).toBeTruthy();
      // A dev API with a flaky mock may queue it; give the worker a moment.
      for (let i = 0; i < 10 && saga.status !== 'Completed'; i++) {
        await new Promise(r => setTimeout(r, 2000));
        const list = await json(request.get(`${API}/api/admin/parks/${park.id}/cashouts?take=20`, { headers: auth }));
        saga = list.cashouts.find((c: any) => c.id === saga.cashoutId) ?? saga;
      }
      expect(saga.status, `the cashout must complete so there are fees to settle: ${JSON.stringify(saga).slice(0, 400)}`).toBe('Completed');
    };
    await cashOut();

    // 2. Where this park stands in this month's settlement.
    const monthView = () => json(request.get(`${API}/api/admin/settlements/month`, { headers: auth }));
    let view = await monthView();
    let found = lineFor(view, park.id);
    if (found.direct) {
      test.skip(found.direct.status === 'Completed', 'this Swich-direct park is already settled for this month — nothing left to fail');
    } else {
      expect(found.statement, `the park must belong to a licensee statement or be Swich-direct: ${JSON.stringify(view).slice(0, 400)}`).toBeTruthy();
      if (!found.statement.isPreview) {
        test.skip(!found.line || found.line.status === 'Completed', 'this month is already settled for this park — nothing left to fail');
      } else {
        // In the barrier phase the licensee's own shared costs come off Swich's due first, so a fresh
        // database may owe nothing yet: add fees until this park's part of the due amount is positive.
        for (let i = 0; i < 30 && (!found.line || found.statement.due <= 0 || found.line.swichShare <= 0); i++) {
          await cashOut();
          view = await monthView();
          found = lineFor(view, park.id);
        }
        expect(found.line?.swichShare ?? 0, `park ${PARK_SLUG} must owe Swich something this month: ${JSON.stringify(found.statement).slice(0, 500)}`)
          .toBeGreaterThan(0);
      }
    }

    // 3. Break the settlement: no active payout account → NO_PARK_ACCOUNT.
    const accounts = (await json(request.get(`${API}/api/admin/parks/${park.id}/bank-accounts`, { headers: auth }))).accounts;
    const active = accounts.filter((a: any) => a.isActive);
    for (const a of active) await request.patch(`${API}/api/admin/parks/${park.id}/bank-accounts/${a.id}`, { headers: auth, data: { isActive: false } });

    try {
      const target = found.direct ? `parkId=${park.id}` : `licenseeId=${found.statement.licenseeId}`;
      const runRes = await request.post(`${API}/api/admin/settlements/run?month=${view.month}&${target}`, { headers: auth });
      const run = await runRes.json();
      expect(runRes.status(), `settlement run (is Settlement:AllowOpenMonthRun on for this Development API?): ${JSON.stringify(run).slice(0, 400)}`).toBe(200);
      const line = found.direct
        ? run.direct.find((d: any) => d.parkId === park.id)
        : run.statements[0].parks.find((p: any) => p.parkId === park.id);
      expect(line?.status, `park transfer: ${JSON.stringify(line ?? run).slice(0, 400)}`).toBe('Failed');
      // The engine checks the Swich IBAN before the park's accounts; CI sets a placeholder so we get
      // the code this test is about. Fail loudly on anything else rather than on a wrong advice text.
      const code = String(line.failureReason ?? '').split(':')[0];
      expect(code, `expected NO_PARK_ACCOUNT (is Settlement:SwichIban configured on the API?) but got: ${line.failureReason}`).toBe('NO_PARK_ACCOUNT');

      // 4. Swich opens the console: danger banner, the failing park, Swich-side advice, badge.
      await adminLogin(page, 'swich');
      const banner = page.locator('.alert-banner');
      await expect(banner).toBeVisible({ timeout: 15_000 });
      await expect(banner).toHaveAttribute('data-severity', 'danger');
      const item = banner.locator('.alert-banner__item[data-severity="danger"]', { hasText: park.name });
      await expect(item).toContainText(/settlements? to Swich (have )?failed/i);
      await expect(item.locator('.alert-banner__advice')).toContainText(/no active payout account/i);
      await expect(page.locator('.sidebar__alert[data-severity="danger"]').first()).toBeVisible();

      // 5. Open → settlements, where the park's failed transfer is listed. The link names the failed transfer's
      //    month — in production always a past one (a month is settled from the run day of the next), never the
      //    running month's preview — so it lands on the row even from another month of the page: start one
      //    month back, follow the link. Dismiss → hidden, and still hidden after a reload (same alert set).
      await expect(item.locator('.alert-banner__link')).toHaveAttribute('href', `/admin/settlements?month=${view.month}`);
      const [vy, vm] = String(view.month).split('-').map(Number);
      const back = new Date(vy, vm - 2, 1);
      const prevMonth = `${back.getFullYear()}-${String(back.getMonth() + 1).padStart(2, '0')}`;
      await page.goto(`/admin/settlements?month=${prevMonth}`);
      await expect(page.locator('.stl-monthbar__select')).toHaveValue(prevMonth, { timeout: 15_000 });
      await expect(item).toBeVisible({ timeout: 15_000 });
      await item.locator('.alert-banner__link').click();
      await page.waitForURL(url => url.pathname === '/admin/settlements' && url.searchParams.get('month') === String(view.month));
      await expect(page.locator('.stl-monthbar__select')).toHaveValue(String(view.month), { timeout: 15_000 });
      await expect(page.locator('.stl-statement .stl-table__row, .stl-direct .stl-table__row', { hasText: park.name })
        .locator('.stl-pill[data-status="failed"]').first()).toBeVisible({ timeout: 15_000 });
      await page.locator('.alert-banner__dismiss').click();
      await expect(banner).toBeHidden();
      await page.reload();
      await page.waitForTimeout(1500);
      await expect(banner).toBeHidden();
      await expect(page.locator('.sidebar__alert[data-severity="danger"]').first(), 'badge stays while dismissed').toBeVisible();

      // 6. Another session (the operator) is not affected by Swich's dismissal.
      await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
      await adminLogin(page, 'operator');
      await expect(page.locator('.alert-banner')).toBeVisible({ timeout: 15_000 });
      await expect(page.locator('.alert-banner__item[data-severity="danger"]', { hasText: park.name })).toBeVisible();
    } finally {
      for (const a of active) await request.patch(`${API}/api/admin/parks/${park.id}/bank-accounts/${a.id}`, { headers: auth, data: { isActive: true } });
    }
  });
});
