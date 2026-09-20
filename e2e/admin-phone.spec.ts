import { test, expect } from '@playwright/test';
import { adminLogin } from './helpers';

/** Admin console on a phone: the layout must never be wider than the screen. */
test.describe('admin console (phone)', () => {
  test('cashouts: status tabs match the backend, no Pending tab, no horizontal overflow at phone width', async ({ page }) => {
    await adminLogin(page, 'swich');
    await page.goto('/admin/cashouts');
    await expect(page.locator('.status-tab').first()).toBeVisible({ timeout: 20_000 });
    const tabs = (await page.locator('.status-tab').allTextContents()).map(t => t.trim().toLowerCase());
    expect(tabs.join('|')).toMatch(/queued/);
    expect(tabs.join('|')).toMatch(/processing/);
    expect(tabs.join('|')).toMatch(/review/);
    expect(tabs.join('|')).not.toMatch(/pending/);

    const scrollW = await page.evaluate(() => document.documentElement.scrollWidth);
    const vw = page.viewportSize()!.width;
    expect(scrollW, 'page must not be wider than the viewport').toBeLessThanOrEqual(vw);
  });

  test('every admin page fits the phone width', async ({ page }) => {
    await adminLogin(page, 'swich');
    for (const p of ['overview', 'drivers', 'onboarding', 'settlements', 'reconciliation', 'reports', 'settings']) {
      await page.goto('/admin/' + p);
      await page.waitForTimeout(800);
      const scrollW = await page.evaluate(() => document.documentElement.scrollWidth);
      expect(scrollW, `/admin/${p} is wider than the viewport`).toBeLessThanOrEqual(page.viewportSize()!.width);
    }
  });
});
