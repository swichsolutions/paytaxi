import { test, expect } from '@playwright/test';
import { adminLogin, tbcIban, freshTail } from './helpers';

test.describe('admin console', () => {
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

  test('settings: fee is read-only for the operator and editable for Swich', async ({ page }) => {
    await adminLogin(page, 'operator');
    await page.goto('/admin/settings');
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

  test('expired admin token sends you to login with a notice, not a broken page', async ({ page }) => {
    await adminLogin(page, 'swich');
    // Forge an expired token: tamper the stored expiry.
    await page.evaluate(() => localStorage.setItem('paytaxi.admin.token.exp', new Date(Date.now() - 60_000).toISOString()));
    await page.goto('/admin/drivers');
    await page.waitForURL(/\/admin\/login/, { timeout: 15_000 });
    await expect(page.locator('.login__notice')).toContainText(/expired/i);
  });
});
