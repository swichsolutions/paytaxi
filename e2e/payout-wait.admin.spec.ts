import { test, expect, APIRequestContext } from '@playwright/test';
import { adminLogin, ADMINS } from './helpers';

/**
 * Payouts wait for money instead of failing (licence agreement §5.5, Annex 3 §5): when the park's
 * account is empty a cashout queues with INSUFFICIENT_FUNDS, the console says what it waits for, Swich
 * or the operator can cancel it (with a reason — the debit goes back to Yandex; the park's manager,
 * who owes the top-up, cannot), and a top-up sends the waiting ones without anyone touching them.
 *
 * Uses the Development-only mock-bank endpoints (Swich only) to drain Tbilisi Auto Park #5's TBC
 * account and switch the mock's funds check on; both are restored in `finally`, since the switch is
 * process-wide on the API. Cashouts are made as Swich through the manual-cashout endpoint, so the
 * spec spends no driver OTP codes. Skips on an API that is not in Development.
 */
const API = 'http://localhost:5196';
const PARK_SLUG = 'tbilisi-auto-park-5';

async function json(r: Promise<any>) { return (await r).json(); }

async function adminToken(request: APIRequestContext) {
  return (await json(request.post(`${API}/api/admin/auth/login`, { data: ADMINS.swich }))).token as string;
}

test.describe('payouts wait for money', () => {
  test('an empty park account queues the payout; the console explains it, cancels it with a reason, and a top-up pays the next one', async ({ page, request }) => {
    const auth = { Authorization: `Bearer ${await adminToken(request)}` };
    const parks = await json(request.get(`${API}/api/admin/parks`, { headers: auth }));
    const park = (Array.isArray(parks) ? parks : parks.parks).find((p: any) => p.slug === PARK_SLUG);
    expect(park, `seeded park ${PARK_SLUG}`).toBeTruthy();

    const accounts = (await json(request.get(`${API}/api/admin/parks/${park.id}/bank-accounts`, { headers: auth }))).accounts;
    const tbc = accounts.find((a: any) => a.isActive && a.bankCode === 'TB');
    expect(tbc, 'park #5 has an active TBC payout account').toBeTruthy();
    const accountUrl = `${API}/api/admin/parks/${park.id}/bank-accounts/${tbc.id}`;

    const drain = await request.post(`${accountUrl}/mock-balance`, { headers: auth, data: { balance: 0, enforceBalance: true } });
    test.skip(drain.status() === 404, 'needs a Development API whose park account is served by the mock bank');
    expect(drain.ok(), `mock-balance: ${await drain.text()}`).toBeTruthy();

    const cashoutsOf = async () => (await json(request.get(`${API}/api/admin/parks/${park.id}/cashouts?take=100`, { headers: auth }))).cashouts as any[];

    try {
      // 1. A manual cashout from the empty account: debited on Yandex, queued, not failed.
      const roster = await json(request.get(`${API}/api/admin/parks/${park.id}/drivers`, { headers: auth }));
      const candidates = roster.drivers.filter((d: any) =>
        d.status === 'Active' && (d.yandex?.balance ?? 0) >= 20 && d.cards?.some((c: any) => c.isDefault && c.bankCode === 'TB'));
      let queued: any = null;
      let driver: any = null;
      for (const d of candidates) {
        const card = d.cards.find((c: any) => c.isDefault && c.bankCode === 'TB');
        const res = await request.post(`${API}/api/admin/parks/${park.id}/cashouts`, {
          headers: auth, data: { driverId: d.id, cardId: card.id, amount: 6, idempotencyKey: crypto.randomUUID() },
        });
        const body = await res.json();
        if (res.status() === 202 && body.status === 'Queued') { queued = body; driver = { id: d.id, cardId: card.id }; break; }
        // Another cashout in flight, a daily limit…: try the next driver.
      }
      expect(queued, 'a cashout from an empty account must queue').toBeTruthy();
      expect(String(queued.failureReason)).toMatch(/^INSUFFICIENT_FUNDS:/);
      expect(queued.yandexTransactionId, 'the Yandex debit is kept while it waits').toBeTruthy();

      // 2. The console: Queued tab of park #5, the row says what it waits for.
      //    The API's queue worker keeps re-trying the payout (dev backoff 10–120 s), and for the quarter
      //    second an attempt takes the row is Processing — not in the Queued tab. The console loads its
      //    list once, so a load that happens to land inside an attempt is simply loaded again.
      await adminLogin(page, 'swich');
      const row = page.locator('.queue-row', { hasText: queued.cashoutId });
      await expect(async () => {
        await page.goto('/admin/cashouts?status=queued');
        await page.locator('.topbar__park-picker-select').selectOption({ label: park.name });
        await expect(row).toBeVisible({ timeout: 5_000 });
      }).toPass({ timeout: 45_000 });
      await expect(row.locator('.queue__wait')).toHaveText(/waiting for top-up/i);
      await row.locator('.queue__cell--driver').click();
      await expect(row.locator('.detail-review--wait')).toContainText(/goes out automatically/i);

      // 3. Cancel (Swich and the operator only) needs a reason; then the debit goes back to Yandex and the
      //    trail names who did it. A click that lands while the worker is mid-attempt is not lost: the API
      //    waits for that attempt to put the row back in the queue (a few seconds at most), then cancels —
      //    so the banner can take a little longer than one round trip.
      await row.locator('.queue__cancel').click();
      const strip = row.locator('.confirm-inline--cancel');
      await expect(strip).toBeVisible();
      const submit = strip.locator('.btn-admin--danger-solid');
      await expect(submit).toBeDisabled();
      await strip.locator('textarea').fill('e2e: the driver asked to cancel');
      await expect(submit).toBeEnabled();
      await submit.click();
      await expect(page.locator('.retry-banner')).toContainText(/payout cancelled/i, { timeout: 20_000 });
      await expect(page.locator('.queue-row', { hasText: queued.cashoutId })).toHaveCount(0);

      const cancelled = (await cashoutsOf()).find(c => c.id === queued.cashoutId);
      expect(cancelled.status).toBe('Failed');
      expect(cancelled.failureReason).toBe(`CANCELLED: e2e: the driver asked to cancel (by admin:${ADMINS.swich.email})`);
      expect(cancelled.yandexReversalTransactionId, 'the debit went back to Yandex').toBeTruthy();

      // 4. The next payout waits too — until the park tops up; then it goes out on its own.
      const second = await json(request.post(`${API}/api/admin/parks/${park.id}/cashouts`, {
        headers: auth, data: { driverId: driver.id, cardId: driver.cardId, amount: 6, idempotencyKey: crypto.randomUUID() },
      }));
      expect(second.status, JSON.stringify(second).slice(0, 300)).toBe('Queued');
      const top = await request.post(`${accountUrl}/mock-topup`, { headers: auth, data: { amount: 100 } });
      expect(top.ok(), await top.text()).toBeTruthy();
      await expect.poll(async () => (await cashoutsOf()).find(c => c.id === second.cashoutId)?.status,
        { timeout: 45_000, intervals: [1000, 2000, 3000] }).toBe('Completed');
    } finally {
      // Process-wide switch on the API: always back to a full account and no funds check.
      await request.post(`${accountUrl}/mock-balance`, { headers: auth, data: { balance: 25000, enforceBalance: false } });
    }
  });
});
