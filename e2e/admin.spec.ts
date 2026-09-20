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
