import { test, expect, Page } from '@playwright/test';
import { adminLogin } from './helpers';

/**
 * The monthly settlement page under the licence agreement (§2.2–2.6).
 *
 * Runs after alerts.spec.ts (files run in name order), which normally leaves this month settled with
 * one failed park transfer. When it has not run, Swich settles the running month here — a Development
 * API allows that early (Settlement:AllowOpenMonthRun); production only settles closed months.
 */
const API = 'http://localhost:5196';

/** The operator adds a licensee-paid SMS invoice dated today through the costs card; returns its unique number. */
async function addInvoice(page: Page, vendor: string): Promise<string> {
  const costs = page.locator('.stl-costs').first();
  await costs.getByRole('button', { name: 'Add invoice' }).click();
  const form = costs.locator('.stl-cost-form');
  await expect(form).toContainText(/paid by the licensee/i);
  await expect(form).toContainText(/48 hours after you enter it/);
  await form.locator('select[name="category"]').selectOption('Sms');
  await form.locator('input[name="vendor"]').fill(vendor);
  const number = `E2E-${Date.now()}`;
  await form.locator('input[name="invoiceNumber"]').fill(number);
  await form.locator('input[name="amount"]').fill('0.10');
  await form.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(costs.locator(`.stl-cost[data-cost="${number}"]`).first()).toBeVisible({ timeout: 15_000 });
  return number;
}

/** Seeded park manager logins: park1!/park2!/park3! in park-name order (Batumi, Tbilisi #3, Tbilisi #5). */
async function managerLogin(page: Page, slug: string, password: string) {
  await page.goto('/admin/login');
  await page.fill('input[type="email"]', `manager@${slug}.local`);
  await page.fill('input[type="password"]', password);
  await page.click('button[type="submit"]');
  await page.waitForURL(/\/admin\/overview/, { timeout: 20_000 });
}

test.describe('monthly settlement', () => {
  test('Swich settles the month; the licensee report and a park invoice open as PDFs', async ({ page }) => {
    await adminLogin(page, 'swich');
    await page.goto('/admin/settlements');
    const statement = page.locator('.stl-statement').first();
    await expect(statement).toBeVisible({ timeout: 20_000 });

    // Settle this month if nobody has yet (inline confirmation — money moves).
    const settle = statement.getByRole('button', { name: 'Settle now' });
    if (await settle.count() > 0) {
      await settle.click();
      await expect(statement.locator('.confirm-inline')).toContainText(/Settle PT-\d{4}-\d{2} now\?/);
      await statement.locator('.confirm-inline').getByRole('button', { name: 'Confirm' }).click();
      // "settled" only when every transfer went through; otherwise it says how many failed (retried nightly).
      await expect(page.locator('.stl-message')).toContainText(/PT-\d{4}-\d{2}( settled|: \d+ of \d+ transfers completed)/, { timeout: 30_000 });
    }

    // Settled: no longer a preview, the formula is frozen, every park with fees has its own transfer row.
    await expect(statement.locator('.stl-pill[data-status="preview"]')).toHaveCount(0, { timeout: 15_000 });
    await expect(statement.getByRole('button', { name: 'Settle now' })).toHaveCount(0);
    const rows = statement.locator('.stl-table__row');
    expect(await rows.count(), 'at least one park earned fees this month').toBeGreaterThan(0);
    await expect(rows.first().locator('.stl-pill')).toHaveAttribute('data-status', /completed|failed|processing|pending/);

    // The licensee's monthly report (§2.6).
    const [report] = await Promise.all([
      page.waitForResponse(r => r.url().includes('/report.pdf'), { timeout: 20_000 }),
      statement.getByRole('button', { name: 'Report PDF' }).click(),
    ]);
    expect(report.status(), `report response ${report.status()}`).toBe(200);
    expect(report.headers()['content-type']).toContain('application/pdf');
    expect(report.headers()['content-disposition'] ?? '').toMatch(/PT-\d{4}-\d{2}-report\.pdf/);

    // One park's monthly invoice, from its row in the split.
    const [invoice] = await Promise.all([
      page.waitForResponse(r => r.url().includes('/settlements/invoice.pdf?month='), { timeout: 20_000 }),
      rows.first().getByRole('button', { name: 'Invoice' }).click(),
    ]);
    expect(invoice.status(), `invoice response ${invoice.status()}`).toBe(200);
    expect(invoice.headers()['content-disposition'] ?? '').toMatch(/PT-\d{4}-\d{2}-.+\.pdf/);
    await expect(page.locator('.stl-message--error')).toHaveCount(0);

    // The previous month can be looked at too (a preview, or its settled statement).
    await page.getByRole('button', { name: 'Previous month' }).click();
    await expect(page.locator('.stl-statement').first()).toBeVisible({ timeout: 20_000 });
  });

  test('the operator enters a licensee-paid invoice, sees when it counts, and withdraws it with a reason — kept on record', async ({ page, request }) => {
    await adminLogin(page, 'operator');
    await page.goto('/admin/settlements');
    const costs = page.locator('.stl-costs').first();
    await expect(costs).toBeVisible({ timeout: 20_000 });

    const number = await addInvoice(page, 'E2E SMS gateway');
    await expect(costs.locator('.stl-cost-form')).toHaveCount(0);

    // Dated today, it belongs to this month — but nothing counts it for 48 hours (Swich's window to check it): it is
    // listed apart, "not counted yet" (or among the late invoices of a month already settled), saying from when it counts.
    const row = costs.locator(`.stl-cost[data-cost="${number}"]`);
    await expect(row).toBeVisible({ timeout: 15_000 });
    await expect(row).toHaveAttribute('data-state', 'waiting');
    await expect(row.locator('.stl-lock')).toHaveCount(0);
    await expect(row).toContainText(/Counts from|goes into the following statement/);

    // The API agrees on who paid.
    const token = await page.evaluate(() => localStorage.getItem('paytaxi.admin.token'));
    const licensees = await (await request.get(`${API}/api/admin/licensees`, { headers: { Authorization: `Bearer ${token}` } })).json();
    const all = await (await request.get(`${API}/api/admin/licensees/${licensees.licensees[0].id}/costs`, { headers: { Authorization: `Bearer ${token}` } })).json();
    const mine = all.costs.find((c: any) => c.invoiceNumber === number);
    expect(mine?.paidBy).toBe('Licensee');

    // Not included yet → its owner can delete it, inline and only with a reason. The row stays, struck through, with the reason.
    await row.getByRole('button', { name: 'Delete' }).click();
    const action = row.locator('.stl-cost-action');
    await expect(action).toBeVisible();
    await action.getByRole('button', { name: 'Delete' }).click();
    await expect(action.locator('[role="alert"]')).toContainText(/reason/i);
    await action.locator('input[name="reason"]').fill('E2E: entered by mistake');
    await action.getByRole('button', { name: 'Delete' }).click();
    const deleted = costs.locator(`.stl-cost[data-cost="${number}"][data-state="deleted"]`);
    await expect(deleted).toBeVisible({ timeout: 15_000 });
    await expect(deleted).toContainText('E2E: entered by mistake');
    await expect(deleted.getByRole('button')).toHaveCount(0);

    // The change history — the same record Swich reads — has both, newest first.
    const history = page.locator('app-agreement-history');
    await expect(history.locator('.hist-event[data-kind="cost_deleted"]').first()).toContainText(number, { timeout: 15_000 });
    await expect(history.locator('.hist-event[data-kind="cost_deleted"]').first()).toContainText('E2E: entered by mistake');
    await expect(history.locator('.hist-event[data-kind="cost_added"]').filter({ hasText: number })).toHaveCount(1);
  });

  test('Swich holds a licensee invoice it disputes: both sides see it on hold until Swich releases it', async ({ page, browser }) => {
    await adminLogin(page, 'operator');
    await page.goto('/admin/settlements');
    await expect(page.locator('.stl-costs').first()).toBeVisible({ timeout: 20_000 });
    const number = await addInvoice(page, 'E2E hosting');

    // Swich, in its own session: the invoice is the licensee's — Swich cannot delete it, only hold it, with a reason.
    const swichContext = await browser.newContext();
    try {
      const swich = await swichContext.newPage();
      await adminLogin(swich, 'swich');
      await swich.goto('/admin/settlements');
      const costs = swich.locator('.stl-costs').first();
      const row = costs.locator(`.stl-cost[data-cost="${number}"]`).first();
      await expect(row).toBeVisible({ timeout: 20_000 });
      await expect(row.getByRole('button', { name: 'Delete' })).toHaveCount(0);
      await row.getByRole('button', { name: 'Hold' }).click();
      await row.locator('input[name="reason"]').fill('E2E: no invoice copy received');
      await row.locator('.stl-cost-action').getByRole('button', { name: 'Hold' }).click();

      // On hold: shown first, to Swich …
      const swichHeld = costs.locator(`.stl-held .stl-cost[data-cost="${number}"]`);
      await expect(swichHeld).toContainText('E2E: no invoice copy received', { timeout: 15_000 });

      // … and to the operator, who cannot release Swich's hold (only withdraw the invoice).
      await page.reload();
      const opHeld = page.locator(`.stl-held .stl-cost[data-cost="${number}"]`);
      await expect(opHeld).toContainText('E2E: no invoice copy received', { timeout: 20_000 });
      await expect(opHeld.getByRole('button', { name: 'Release' })).toHaveCount(0);

      // Swich releases it: off the hold list, counted again once its 48 hours have passed.
      await swichHeld.getByRole('button', { name: 'Release' }).click();
      await swichHeld.locator('.stl-cost-action').getByRole('button', { name: 'Release' }).click();
      await expect(costs.locator(`.stl-held .stl-cost[data-cost="${number}"]`)).toHaveCount(0, { timeout: 15_000 });
      await expect(swich.locator('app-agreement-history .hist-event[data-kind="cost_released"]').first()).toContainText(number);
    } finally {
      await swichContext.close();
    }

    // The operator withdraws the test invoice so it never reaches a statement.
    await page.reload();
    const row = page.locator(`.stl-costs .stl-cost[data-cost="${number}"]`).first();
    await row.getByRole('button', { name: 'Delete' }).click();
    await row.locator('input[name="reason"]').fill('E2E clean-up');
    await row.locator('.stl-cost-action').getByRole('button', { name: 'Delete' }).click();
    await expect(page.locator(`.stl-costs .stl-cost[data-cost="${number}"][data-state="deleted"]`)).toBeVisible({ timeout: 15_000 });
  });

  test('a park manager sees the park’s own monthly invoices, never the licensee statement or its costs', async ({ page }) => {
    await managerLogin(page, 'tbilisi-auto-park-5', 'park3!');
    await page.goto('/admin/settlements');
    await expect(page.locator('.stl-card--months')).toBeVisible({ timeout: 20_000 });
    await page.waitForTimeout(1000);
    await expect(page.locator('.stl-monthbar')).toHaveCount(0);
    await expect(page.locator('.stl-statement')).toHaveCount(0);
    await expect(page.locator('.stl-costs')).toHaveCount(0);
    await expect(page.locator('.stl-licensee')).toHaveCount(0);
    await expect(page.locator('app-agreement-history')).toHaveCount(0);
  });
});
