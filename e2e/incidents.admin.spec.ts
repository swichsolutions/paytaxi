import { test, expect, APIRequestContext } from '@playwright/test';
import { adminLogin, ADMINS } from './helpers';

/**
 * The Annex 1 incident log: the licensee reports, Swich responds and resolves, the banner and the
 * timeline follow, and the page fits a phone.
 *
 * Uses a P2 on Tbilisi Auto Park #5 so the run does not add an owner-fault P1 to the dev database's
 * contract figures. Incidents are append-only (there is no delete endpoint), so the test resolves what
 * it opened; the resolved row stays in the dev log — CI starts from an empty database every run.
 */
const API = 'http://localhost:5196';
const PARK_SLUG = 'tbilisi-auto-park-5';

async function token(request: APIRequestContext, who: keyof typeof ADMINS) {
  return (await (await request.post(`${API}/api/admin/auth/login`, { data: ADMINS[who] })).json()).token as string;
}

test.describe('incident log', () => {
  test('licensee reports, Swich acknowledges and resolves; banner, SLA chips and timeline follow', async ({ page, request }) => {
    const swichAuth = { Authorization: `Bearer ${await token(request, 'swich')}` };
    const parks = await (await request.get(`${API}/api/admin/parks`, { headers: swichAuth })).json();
    const park = (Array.isArray(parks) ? parks : parks.parks).find((p: any) => p.slug === PARK_SLUG);
    expect(park, `seeded park ${PARK_SLUG}`).toBeTruthy();

    const title = `E2E: some payouts slow ${Date.now()}`;
    let incidentId: string | null = null;

    try {
      // ── 1. The licensee reports it ───────────────────────────────
      await adminLogin(page, 'operator');
      await page.goto('/admin/incidents');
      await expect(page.getByTestId('incident-contract'), 'the licensee sees the monthly contract card').toBeVisible({ timeout: 20_000 });

      await page.getByTestId('incident-report-open').click();
      const form = page.getByTestId('incident-report-form');
      await form.locator('.inc-level-card[data-level="P2"]').click();
      await expect(form.locator('.inc-level-card[data-level="P2"]')).toHaveAttribute('aria-checked', 'true');
      await form.locator('input[type="text"]').first().fill(title);
      await form.locator('select').first().selectOption({ value: park.id });
      // The owner-only fields are not offered to the licensee.
      await expect(form.locator('input[type="checkbox"]')).toHaveCount(0);
      await page.getByTestId('incident-report-submit').click();

      const drawer = page.getByTestId('incident-drawer');
      await expect(drawer.locator('h2')).toHaveText(title);
      await expect(drawer.locator('.inc-status')).toHaveAttribute('data-status', 'Reported');
      await expect(drawer.locator('.inc-clock').first().locator('.inc-chip')).toContainText(/Response due in/);
      await expect(drawer.getByTestId('incident-ack'), 'only Swich acknowledges').toHaveCount(0);
      const ref = (await drawer.locator('.inc-drawer__tags .inc-row__ref').textContent())!.trim();
      expect(ref).toMatch(/^INC-\d{4,}$/);

      const list = await (await request.get(`${API}/api/admin/incidents?state=open`, { headers: swichAuth })).json();
      incidentId = list.incidents.find((i: any) => i.ref === ref)?.id ?? null;
      expect(incidentId, 'the API lists the new incident').toBeTruthy();

      // A note from the licensee lands on the timeline under its name.
      await drawer.locator('.inc-note textarea').fill('E2E: drivers on BoG cards wait 20+ minutes.');
      await drawer.locator('.inc-note button').click();
      const note = drawer.locator('.inc-timeline__item[data-kind="note"]');
      await expect(note).toContainText('BoG cards');
      await expect(note).toContainText(ADMINS.operator.email);

      // The banner picks it up on the next navigation.
      await page.keyboard.press('Escape');
      await expect(drawer).toBeHidden();
      await page.reload();
      const line = page.locator('.alert-banner__item', { hasText: park.name }).filter({ hasText: /incident/i });
      await expect(line).toBeVisible({ timeout: 15_000 });
      await expect(line.locator('.alert-banner__link')).toHaveAttribute('href', /\/admin\/incidents/);

      // ── 2. Swich responds and resolves ──────────────────────────
      await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
      await adminLogin(page, 'swich');
      await page.goto('/admin/incidents');
      const row = page.locator(`.inc-row[data-ref="${ref}"]`);
      await expect(row).toBeVisible({ timeout: 20_000 });
      await row.click();

      await drawer.getByTestId('incident-ack').click();
      await expect(drawer.locator('.inc-confirm')).toBeVisible();
      await drawer.locator('.inc-confirm textarea').fill('E2E: looking at the BoG rail.');
      await drawer.getByTestId('incident-confirm').click();
      await expect(drawer.locator('.inc-status')).toHaveAttribute('data-status', 'Acknowledged');
      await expect(drawer.locator('.inc-clock').first().locator('.inc-chip')).toContainText(/Responded in/);

      await drawer.getByTestId('incident-resolve').click();
      await drawer.getByTestId('incident-confirm').click();
      await expect(drawer.locator('.inc-status')).toHaveAttribute('data-status', 'Resolved');
      await expect(drawer.locator('.inc-clock').nth(1).locator('.inc-chip')).toHaveText(/Resolved within target/);

      const kinds = await drawer.locator('.inc-timeline__item').evaluateAll(els => els.map(e => e.getAttribute('data-kind')));
      expect(kinds).toEqual(['reported', 'note', 'acknowledged', 'resolved']);
      await expect(row).toHaveClass(/inc-row--resolved/);
    } finally {
      if (incidentId) await request.post(`${API}/api/admin/incidents/${incidentId}/resolve`, { headers: swichAuth, data: {} });
    }
  });

  test('Swich moves a park incident to all parks only with text that names no park; the move is on the timeline', async ({ page, request }) => {
    const swichAuth = { Authorization: `Bearer ${await token(request, 'swich')}` };
    const parks = await (await request.get(`${API}/api/admin/parks`, { headers: swichAuth })).json();
    const park = (Array.isArray(parks) ? parks : parks.parks).find((p: any) => p.slug === PARK_SLUG);
    expect(park, `seeded park ${PARK_SLUG}`).toBeTruthy();

    // A P3: off the banner and out of the contract figures. Its title and details name the park, like the monitor's do.
    const created = await (await request.post(`${API}/api/admin/incidents`, {
      headers: swichAuth,
      data: { title: `E2E: card payouts slow at ${park.name} ${Date.now()}`, level: 'P3', parkId: park.id, description: `Seen at ${park.name}.` },
    })).json();
    const incidentId: string = created.incident.id;
    const ref: string = created.incident.ref;

    try {
      await adminLogin(page, 'swich');
      await page.goto('/admin/incidents');
      const row = page.locator(`.inc-row[data-ref="${ref}"]`);
      await expect(row).toBeVisible({ timeout: 20_000 });
      await row.click();

      const drawer = page.getByTestId('incident-drawer');
      await drawer.locator('.inc-actions__row').getByRole('button', { name: 'Edit' }).click();
      const edit = drawer.locator('.inc-edit');
      const title = edit.locator('input[type="text"]').first();
      const details = edit.locator('textarea');
      const save = drawer.getByTestId('incident-edit-save');

      // Level, fault, park: the third select. "All parks" is the empty value.
      await edit.locator('select').nth(2).selectOption({ value: '' });
      const hint = drawer.getByTestId('incident-rescope-hint');
      await expect(hint, 'moving it away from its park asks for neutral text').toBeVisible();
      await expect(hint).toContainText(park.name);
      await expect(hint).toHaveAttribute('data-ready', 'false');
      await expect(save).toBeDisabled();

      // New details, but a title that still names the park: not yet.
      await details.fill('Card payouts take longer than usual.');
      await title.fill(`Card payouts slow at ${park.name}`);
      await expect(title).toHaveAttribute('aria-invalid', 'true');
      await expect(save).toBeDisabled();

      await title.fill('Card payouts slow');
      await expect(hint).toHaveAttribute('data-ready', 'true');
      await expect(save).toBeEnabled();
      await save.click();

      await expect(drawer.locator('.inc-drawer__sub')).toContainText('All parks');
      await expect(drawer.locator('h2')).toHaveText('Card payouts slow');
      await expect(drawer.locator('.inc-timeline__item[data-kind="updated"]').last()).toContainText('Scope: All parks');
    } finally {
      await request.post(`${API}/api/admin/incidents/${incidentId}/resolve`, { headers: swichAuth, data: {} });
    }
  });

  test('the incident page fits a phone, drawer and report form included', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 780 });
    await adminLogin(page, 'operator');
    await page.goto('/admin/incidents');
    await expect(page.locator('.inc-list')).toBeVisible({ timeout: 20_000 });
    await page.waitForTimeout(800);
    const fits = async (what: string) => {
      const scrollW = await page.evaluate(() => document.documentElement.scrollWidth);
      expect(scrollW, `${what} is wider than the viewport`).toBeLessThanOrEqual(360);
    };
    await fits('/admin/incidents');

    await page.getByTestId('incident-report-open').click();
    await expect(page.getByTestId('incident-report-form')).toBeVisible();
    await fits('the report form');
    await page.getByTestId('incident-report-form').locator('.btn-admin--secondary').click();

    const first = page.locator('.inc-row').first();
    if (await first.count() > 0) {
      await first.click();
      await expect(page.getByTestId('incident-drawer').locator('h2')).toBeVisible();
      await fits('the incident drawer');
    }
  });
});
