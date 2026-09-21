import { test, expect, APIRequestContext } from '@playwright/test';
import { adminLogin, ADMINS } from './helpers';

/**
 * The red banner: a failed nightly settlement must be on screen for whoever opens the console.
 *
 * There is no "make a settlement fail" endpoint, so the test manufactures one the way it happens
 * in life: a driver cashes out (so the park owes Swich fees today), the park's payout accounts are
 * switched off, and the settlement run fails with NO_PARK_ACCOUNT. Accounts are switched back on
 * afterwards; the Failed settlement is deliberately left in place — the nightly worker (or "Retry"
 * on the Settlements page) picks it up, a repeat of this test re-fails it, and a dev console shows
 * a realistic alert. CI starts from an empty database every run.
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

test.describe('alert banner', () => {
  test('a failed settlement is announced, links to the fix, and can be dismissed until something new happens', async ({ page, request }) => {
    const token = await adminToken(request);
    const auth = { Authorization: `Bearer ${token}` };

    const parks = await json(request.get(`${API}/api/admin/parks`, { headers: auth }));
    const park = (Array.isArray(parks) ? parks : parks.parks).find((p: any) => p.slug === PARK_SLUG);
    expect(park, `seeded park ${PARK_SLUG}`).toBeTruthy();

    // 1. Fees for today: an active driver with a destination cashes out 5 GEL.
    const roster = await json(request.get(`${API}/api/admin/parks/${park.id}/drivers`, { headers: auth }));
    const driver = roster.drivers.find((d: any) => d.status === 'Active' && d.cards?.some((c: any) => c.isDefault));
    expect(driver, 'an active seeded driver with a default account').toBeTruthy();
    const dtok = await driverToken(request, driver.phone);
    // Pay to an account at a bank the park actually holds a payout account with (as the app would offer).
    const me = await json(request.get(`${API}/api/driver/me`, { headers: { Authorization: `Bearer ${dtok}` } }));
    const supported = new Set(me.park.supportedBanks.map((b: any) => b.bankCode));
    const card = me.cards.find((c: any) => c.isDefault && supported.has(c.bankCode)) ?? me.cards.find((c: any) => supported.has(c.bankCode));
    expect(card, `driver needs an account at one of ${[...supported].join(',')}`).toBeTruthy();
    const cashoutRes = await request.post(`${API}/api/driver/cashouts`, {
      headers: { Authorization: `Bearer ${dtok}` }, data: { cardId: card.id, amount: 5, idempotencyKey: crypto.randomUUID() },
    });
    let saga = await cashoutRes.json();
    expect(saga.cashoutId ?? saga.code, `cashout response: ${JSON.stringify(saga).slice(0, 300)}`).toBeTruthy();
    // A dev API with a flaky mock may queue it; give the worker a moment.
    for (let i = 0; i < 10 && saga.status !== 'Completed'; i++) {
      await new Promise(r => setTimeout(r, 2000));
      const list = await json(request.get(`${API}/api/admin/parks/${park.id}/cashouts?take=20`, { headers: auth }));
      saga = list.cashouts.find((c: any) => c.id === saga.cashoutId) ?? saga;
    }
    expect(saga.status, `the cashout must complete so there are fees to settle: ${JSON.stringify(saga).slice(0, 400)}`).toBe('Completed');

    // 2. Break the settlement: no active payout account → NO_PARK_ACCOUNT.
    const accounts = (await json(request.get(`${API}/api/admin/parks/${park.id}/bank-accounts`, { headers: auth }))).accounts;
    const active = accounts.filter((a: any) => a.isActive);
    for (const a of active) await request.patch(`${API}/api/admin/parks/${park.id}/bank-accounts/${a.id}`, { headers: auth, data: { isActive: false } });

    try {
      const run = await json(request.post(`${API}/api/admin/settlements/run?parkId=${park.id}`, { headers: auth }));
      test.skip(run.status === 'Completed', 'this park was already settled today — nothing left to fail');
      expect(run.status).toBe('Failed');

      // 3. Swich opens the console: danger banner, the failing park, Swich-side advice, badge.
      await adminLogin(page, 'swich');
      const banner = page.locator('.alert-banner');
      await expect(banner).toBeVisible({ timeout: 15_000 });
      await expect(banner).toHaveAttribute('data-severity', 'danger');
      const line = banner.locator('.alert-banner__item[data-severity="danger"]', { hasText: park.name });
      await expect(line).toContainText(/settlement to Swich failed/i);
      await expect(line.locator('.alert-banner__advice')).toContainText(/no active payout account/i);
      await expect(page.locator('.sidebar__alert[data-severity="danger"]').first()).toBeVisible();

      // 4. Open → settlements. Dismiss → hidden, and still hidden after a reload (same alert set).
      await line.locator('.alert-banner__link').click();
      await page.waitForURL(/\/admin\/settlements/);
      await page.locator('.alert-banner__dismiss').click();
      await expect(banner).toBeHidden();
      await page.reload();
      await page.waitForTimeout(1500);
      await expect(banner).toBeHidden();
      await expect(page.locator('.sidebar__alert[data-severity="danger"]').first(), 'badge stays while dismissed').toBeVisible();

      // 5. Another session (the operator) is not affected by Swich's dismissal.
      await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
      await adminLogin(page, 'operator');
      await expect(page.locator('.alert-banner')).toBeVisible({ timeout: 15_000 });
      await expect(page.locator('.alert-banner__item[data-severity="danger"]', { hasText: park.name })).toBeVisible();
    } finally {
      for (const a of active) await request.patch(`${API}/api/admin/parks/${park.id}/bank-accounts/${a.id}`, { headers: auth, data: { isActive: true } });
    }
  });
});
