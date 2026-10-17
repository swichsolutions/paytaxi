import { test, expect } from '@playwright/test';
import { adminLogin, tbcIban, freshTail, holdAppScript, ADMINS } from './helpers';

test.describe('admin console', () => {
  test('login: what was typed before the page finished loading survives the app taking over', async ({ page }) => {
    // The login page is prerendered, so on a slow connection the form is usable before the app script runs.
    // Hydration used to clear both fields (the form's first write), leaving Sign in disabled and the e-mail gone.
    const app = await holdAppScript(page);
    await page.goto('/admin/login', { waitUntil: 'commit' });
    await page.fill('input[type="email"]', ADMINS.swich.email);
    await page.fill('input[type="password"]', ADMINS.swich.password);
    await expect.poll(app.requested, 'the app script must still be held while typing').toBe(true);
    app.release();

    const submit = page.locator('button[type="submit"]');
    await expect(submit).toBeEnabled();
    await expect(page.locator('input[type="email"]')).toHaveValue(ADMINS.swich.email);
    await expect(page.locator('input[type="password"]')).toHaveValue(ADMINS.swich.password);
    await submit.click();
    await page.waitForURL(/\/admin\/overview/, { timeout: 20_000 });
  });

  test('drivers drawer: payout accounts, live third-party warning, reason gating', async ({ page }) => {
    await adminLogin(page, 'swich');
    await page.goto('/admin/drivers');
    await page.locator('.topbar__park-picker-select').selectOption({ label: 'Tbilisi Auto Park #3' });
    await expect(page.locator('.table__row').first()).toBeVisible({ timeout: 20_000 });

    // Open the first driver; the drawer must show the Payout accounts section.
    await page.locator('.table__row').first().click();
    const drawer = page.locator('.drawer');
    await expect(drawer.locator('.drawer__section-title', { hasText: 'Payout accounts' })).toBeVisible();
    const registered = (await drawer.locator('.drawer__kv-value').first().textContent())!.trim();

    await drawer.locator('.drawer__edit-btn', { hasText: 'Add account' }).click();
    const holder = drawer.locator('.edit-field input[type="text"]').nth(1);
    await expect(holder, 'holder is prefilled with the registered name').toHaveValue(registered);
    await expect(drawer.locator('.acct-warning')).toHaveCount(0);

    // Someone else's name → live warning + reason field; Save disabled until a reason is typed.
    await drawer.locator('input.gel').fill(tbcIban(freshTail()));
    await holder.fill('ნინო ბერიძე');
    await expect(drawer.locator('.acct-warning')).toBeVisible();
    await expect(drawer.locator('.acct-warning')).toContainText(registered);
    const save = drawer.locator('.edit-actions .btn-admin--brand');
    await expect(save).toBeDisabled();
    await drawer.locator('textarea').fill('wife account; statement on file (e2e)');
    await expect(save).toBeEnabled();

    // Save → the new account carries the Third party badge; then remove it to leave the data clean.
    await save.click();
    const row = drawer.locator('.acct-row--third').first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await expect(row.locator('.acct-badge--third')).toHaveText(/third party/i);
    await expect(row).toContainText('ops@swich.dev');
    await row.locator('.acct-row__remove').click();
    await row.locator('.btn-admin--danger').click();
    await expect(drawer.locator('.acct-row--third')).toHaveCount(0, { timeout: 15_000 });
  });

  test('onboarding: the IBAN collected at the desk obeys the same ownership rule', async ({ page }) => {
    await adminLogin(page, 'swich');
    await page.goto('/admin/onboarding');
    await page.locator('.topbar__park-picker-select').selectOption({ label: 'Tbilisi Auto Park #5' });
    // Step 1: a phone nobody has, and a Yandex profile the mock roster knows but the park has not linked.
    await page.locator('input.field__input--phone').pressSequentially('5' + String(Date.now()).slice(-8), { delay: 20 });
    await page.getByPlaceholder(/Profile ID/i).fill('yp_tb5_new1');
    await expect(page.locator('button.ob-submit')).toBeEnabled();
    await page.locator('button.ob-submit').click();

    const nameField = page.locator('.ob-form input.field__input').first();
    await expect(nameField, 'step 2 opens for a known, unlinked Yandex profile').toBeVisible({ timeout: 15_000 });
    await expect(page.locator('.field__hint', { hasText: /passport/i })).toBeVisible();   // park-side name hint

    await nameField.fill('გია ლომიძე');
    await page.locator('.ob-form input.field__input--mono').last().fill(tbcIban(freshTail()));
    const holder = page.locator('.ob-form label.field', { has: page.locator('.field__label', { hasText: /holder/i }) }).locator('input');
    await expect(holder).toBeVisible();
    await holder.fill('ნინო ბერიძე');
    await expect(page.locator('.ob-banner--warn')).toBeVisible();              // live third-party warning
    await expect(page.locator('.ob-banner--warn')).toContainText('გია ლომიძე');
    await expect(page.locator('.ob-form textarea')).toBeVisible();              // reason field revealed
    // We stop before creating: the integration tests cover the backend outcome.
  });

  test('reconciliation: Run now is available to Swich and reports a result', async ({ page }) => {
    await adminLogin(page, 'swich');
    await page.goto('/admin/reconciliation');
    const run = page.getByRole('button', { name: 'Run now' });
    await expect(run).toBeVisible({ timeout: 20_000 });
    await run.click();
    await expect(page.locator('.recon-run-note')).toContainText(/Reconciliation finished/, { timeout: 60_000 });
  });

  test('settlements: Swich and the operator see the same monthly statement, with the formula and the barrier', async ({ page }) => {
    // Read-only on purpose: settling the running month here would leave nothing for the alerts spec
    // to fail (settling and the PDFs are covered by settlement.admin.spec.ts, which runs after it).
    await adminLogin(page, 'swich');
    await page.goto('/admin/settlements');
    const statement = page.locator('.stl-statement').first();
    await expect(statement).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('.stl-monthbar select')).toBeVisible();
    await expect(statement.locator('.stl-phase')).toBeVisible();
    await expect(statement.locator('.stl-formula__line').first()).toBeVisible();
    await expect(statement.locator('.stl-formula__line--strong')).toContainText('₾');
    await expect(statement.locator('.stl-bar')).toBeVisible();
    const due = (await statement.locator('.stl-figure--due .stl-figure__value').textContent())!.trim();
    await expect(page.locator('.stl-costs').first()).toBeVisible();
    await expect(page.locator('.stl-licensee')).toBeVisible();
    await expect(page.locator('.stl-direct')).toBeVisible();          // Swich-direct parks: Swich only

    // The operator reads the same statement (§2.6: identical data for both sides), without Swich's controls.
    await page.evaluate(() => localStorage.clear());
    await adminLogin(page, 'operator');
    await page.goto('/admin/settlements');
    const opStatement = page.locator('.stl-statement').first();
    await expect(opStatement).toBeVisible({ timeout: 20_000 });
    await expect(opStatement.locator('.stl-figure--due .stl-figure__value')).toHaveText(due);
    await expect(page.locator('.stl-direct')).toHaveCount(0);
    await expect(opStatement.getByRole('button', { name: 'Settle now' })).toHaveCount(0);
    await expect(page.locator('.stl-licensee').getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0);
  });

  test('settings: fee is read-only for the operator and editable for Swich', async ({ page }) => {
    await adminLogin(page, 'operator');
    await page.goto('/admin/settings');
    // Seeded parks carry the legal name, ID code and phone the drivers' privacy notice names them by: no warning.
    await expect(page.locator('.set-card__title', { hasText: 'Taxi park' })).toBeVisible();
    await expect(page.getByTestId('park-legal-missing')).toHaveCount(0);
    await page.locator('.set-edit-btn', { hasText: 'Edit park details' }).click();
    await expect(page.locator('.set-field__input--readonly')).toBeVisible();     // fee shown, not editable
    await expect(page.locator('.set-field__note', { hasText: 'Fixed by Swich' })).toBeVisible();
    await expect(page.locator('input[type="number"]').first()).toBeVisible();   // limits still editable

    await page.evaluate(() => localStorage.clear());
    await adminLogin(page, 'swich');
    await page.goto('/admin/settings');
    await page.locator('.set-edit-btn', { hasText: 'Edit park details' }).click();
    await expect(page.locator('.set-field__input--readonly')).toHaveCount(0);
    await expect(page.locator('.set-field__note', { hasText: 'Fixed by Swich' })).toHaveCount(0);
  });

  test('settings: Swich suspends a park with a reason, everyone sees it, and reactivates it', async ({ page, request }) => {
    const API = 'http://localhost:5196';
    const token = (await (await request.post(`${API}/api/admin/auth/login`, { data: { email: 'ops@swich.dev', password: 'swich2026!' } })).json()).token;
    const auth = { Authorization: `Bearer ${token}` };
    const park = (await (await request.get(`${API}/api/admin/parks`, { headers: auth })).json()).find((p: any) => p.slug === 'tbilisi-auto-park-5');

    try {
      await adminLogin(page, 'swich');
      await page.goto('/admin/settings');
      await page.locator('.topbar__park-picker-select').selectOption({ value: park.id });
      await expect(page.locator('.set-row__value', { hasText: park.name })).toBeVisible();
      const card = page.getByTestId('park-status-card');
      await expect(card.locator('.status-pill')).toHaveAttribute('data-status', 'active');

      // The reason is required before the button does anything.
      await card.locator('.set-edit-btn', { hasText: 'Suspend park' }).click();
      const submit = card.locator('.park-status__submit--danger');
      await expect(submit).toBeDisabled();
      await card.locator('textarea').fill('E2E: settlement unpaid');
      await submit.click();

      await expect(card.locator('.status-pill')).toHaveAttribute('data-status', 'suspended');
      await expect(card.locator('.park-status__facts')).toContainText('E2E: settlement unpaid');
      await expect(card.locator('.park-status__facts')).toContainText('ops@swich.dev');
      const line = page.locator('.alert-banner__item', { hasText: /Park suspended/i }).filter({ hasText: park.name });
      await expect(line).toHaveCount(1);
      await expect(line.locator('.alert-banner__advice')).toContainText('E2E: settlement unpaid');
      await expect(page.locator('.topbar__park-picker-select option', { hasText: `${park.name} · Suspended` })).toHaveCount(1);

      // Reactivate: no reason needed, the banner line goes away.
      await card.locator('.set-edit-btn', { hasText: 'Reactivate park' }).click();
      await card.locator('.btn-admin--brand', { hasText: 'Reactivate park' }).click();
      await expect(card.locator('.status-pill')).toHaveAttribute('data-status', 'active');
      await expect(page.locator('.alert-banner__item', { hasText: /Park suspended/i })).toHaveCount(0);
    } finally {
      await request.post(`${API}/api/admin/parks/${park.id}/status`, { headers: auth, data: { status: 'Active' } });
    }
  });

  test("settings: a park's written revocation is recorded with 30 days' notice, shown, and withdrawn before it takes effect", async ({ page, request }) => {
    // Annex 3 §7. Recorded for a day 30 days ahead, so nothing changes for the park's drivers while the test runs.
    const API = 'http://localhost:5196';
    const token = (await (await request.post(`${API}/api/admin/auth/login`, { data: { email: 'ops@swich.dev', password: 'swich2026!' } })).json()).token;
    const auth = { Authorization: `Bearer ${token}` };
    const park = (await (await request.get(`${API}/api/admin/parks`, { headers: auth })).json()).find((p: any) => p.slug === 'batumi-auto-park-1');
    const addDaysIso = (day: string, n: number) => {
      const [y, m, d] = day.split('-').map(Number);
      return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10);
    };
    // One left over from an interrupted run (never in effect: it was recorded 30 days ahead) would hide the form.
    await request.delete(`${API}/api/admin/parks/${park.id}/authorisation-revocation`, { headers: auth, data: {} });

    try {
      await adminLogin(page, 'swich');
      await page.goto('/admin/settings');
      await page.locator('.topbar__park-picker-select').selectOption({ value: park.id });
      await expect(page.locator('.set-row__value', { hasText: park.name })).toBeVisible();
      const box = page.getByTestId('park-authorisation');

      await box.locator('.park-auth__open').click();
      const submit = box.locator('.park-auth__submit');
      await expect(submit).toBeDisabled();                                  // the reason is required
      await box.locator('.park-auth__reason').fill('E2E: registered letter, filed as R-17');
      await expect(submit).toBeEnabled();
      // The first day without the authorisation defaults to 30 days after the notice; an earlier one is not accepted.
      const notice = await box.locator('.park-auth__notice').inputValue();
      const from = await box.locator('.park-auth__from').inputValue();
      expect(from).toBe(addDaysIso(notice, 30));
      await box.locator('.park-auth__from').fill(addDaysIso(notice, 10));
      await expect(submit).toBeDisabled();
      await box.locator('.park-auth__from').fill(from);
      await expect(submit).toBeEnabled();
      await submit.click();

      await expect(box.locator('.park-auth__box')).toContainText('Authorisation revoked from');
      await expect(box.locator('.park-auth__facts')).toContainText('E2E: registered letter, filed as R-17');
      await expect(box.locator('.park-auth__facts')).toContainText('ops@swich.dev');
      await expect(box.locator('.park-auth__note')).toContainText('Takes effect in 30 day');

      // Before its day it can still be withdrawn (inline confirmation, no browser dialog).
      await box.locator('.park-auth__actions .set-edit-btn', { hasText: 'Withdraw revocation' }).click();
      await box.locator('.park-auth__withdraw').click();
      await expect(box.locator('.park-auth__box')).toHaveCount(0);
      await expect(box.locator('.park-auth__open')).toBeVisible();
    } finally {
      await request.delete(`${API}/api/admin/parks/${park.id}/authorisation-revocation`, { headers: auth, data: {} });
    }
  });

  test('topbar: the picked park survives a reload, and another admin does not inherit it', async ({ page }) => {
    await adminLogin(page, 'swich');
    await page.goto('/admin/settings');
    const picker = page.locator('.topbar__park-picker-select');
    await picker.selectOption({ label: 'Tbilisi Auto Park #5' });
    const picked = await picker.inputValue();

    await page.reload();
    await expect(picker).toHaveValue(picked);
    await expect(page.locator('.set-row__value', { hasText: 'Tbilisi Auto Park #5' })).toBeVisible();

    // The choice belongs to this admin: the operator signing in on the same browser starts on the default park.
    await page.evaluate(() => { localStorage.removeItem('paytaxi.admin.token'); localStorage.removeItem('paytaxi.admin.session'); });
    await adminLogin(page, 'operator');
    await expect(picker).not.toHaveValue(picked);
  });

  test('expired admin token sends you to login with a notice, not a broken page', async ({ page }) => {
    await adminLogin(page, 'swich');
    // Forge an expired token: tamper the stored expiry.
    await page.evaluate(() => localStorage.setItem('paytaxi.admin.token.exp', new Date(Date.now() - 60_000).toISOString()));
    await page.goto('/admin/drivers');
    await page.waitForURL(/\/admin\/login/, { timeout: 15_000 });
    await expect(page.locator('.login__notice')).toContainText(/expired/i);
  });
});
