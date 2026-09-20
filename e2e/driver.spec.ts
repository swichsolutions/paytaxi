import { test, expect } from '@playwright/test';
import { DRIVERS, driverLogin, tbcIban, freshTail } from './helpers';

test.describe('driver app', () => {
  test('login, dashboard balance, cashout step fits one screen, own-name rule', async ({ page }) => {
    await driverLogin(page, DRIVERS.giorgi.phone, 'ka');

    // Dashboard: a real balance, not the "unavailable" state, and the header is not cropped.
    await expect(page.locator('.balance-card__amount')).toContainText('₾');
    await expect(page.locator('.balance-card__amount')).not.toContainText('0.00');
    const park = page.locator('.dash-header__park');
    await expect(park).toBeVisible();
    const clipped = await park.evaluate(el => el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).textOverflow === 'ellipsis' && getComputedStyle(el).whiteSpace === 'nowrap');
    expect(clipped, 'park name must wrap, not be cut with an ellipsis').toBe(false);

    // Cashout step 1: the Next button is on screen without scrolling at 360×780.
    await page.goto('/cashout');
    await expect(page.locator('.keypad__key:not([disabled])').first()).toBeVisible({ timeout: 15_000 });
    const next = page.locator('.co-body .btn--primary');
    const box = await next.boundingBox();
    const viewport = page.viewportSize()!;
    expect(box, 'Next button must be rendered').not.toBeNull();
    expect(box!.y + box!.height, 'Next button must be above the fold').toBeLessThanOrEqual(viewport.height);

    // Georgian wording: "ანგარიშის ნომერი", never "IBAN", in the driver UI.
    for (const k of ['1', '0']) await page.getByRole('button', { name: k, exact: true }).click();
    await next.click();
    await page.locator('.add-card-row').click();
    await expect(page.locator('.acct-field__label').first()).toBeVisible();
    const labels = await page.locator('.acct-field__label').allTextContents();
    expect(labels[0]).toBe('ანგარიშის ნომერი');
    expect(labels.join(' ')).not.toMatch(/IBAN/);
    await expect(page.locator('.acct-field__hint')).toContainText('პირადობის');

    // Own-name rule: someone else's name is refused with the translated message.
    const inputs = page.locator('.acct-field__input');
    await inputs.nth(0).fill(tbcIban(freshTail()));
    await inputs.nth(1).fill('ნინო ბერიძე');
    await page.locator('.btn--primary.btn--sm').click();
    await expect(page.locator('.amount-error').first()).toContainText('არ ემთხვევა');
  });

  test('profile: holder name is prefilled, refusal is translated, English strings absent', async ({ page }) => {
    await driverLogin(page, DRIVERS.zura.phone, 'en');
    await page.goto('/profile');
    await page.locator('.prof-add').click();
    await expect(page.locator('.prof-field__input').nth(1)).toHaveValue(DRIVERS.zura.name);
    await expect(page.locator('.prof-field__hint')).toContainText('own name');

    await page.locator('.prof-field__input').nth(0).fill(tbcIban(freshTail()));
    await page.locator('.prof-field__input').nth(1).fill('Nino Beridze');
    await page.locator('.prof-form__actions button:not(.btn--ghost)').click();
    await expect(page.locator('.prof-error')).toContainText('does not look like your name');
    // Never Angular's raw HTTP text.
    await expect(page.locator('.prof-error')).not.toContainText('Http failure');
  });

  test('history tab and RU bottom nav fit', async ({ page }) => {
    await driverLogin(page, DRIVERS.valeri.phone, 'ru');
    await page.goto('/history?tab=rides');
    await expect(page.locator('.filter-tab--active')).toHaveText(/поездк/i);   // ?tab=rides selected, in Russian
    // RU nav labels must not overflow their slots.
    const overflow = await page.locator('.nav-item').evaluateAll(items =>
      items.filter(el => { const s = el.querySelector('span'); return s && s.scrollWidth > el.clientWidth + 1; }).length);
    expect(overflow, 'RU bottom-nav label overflows its slot').toBe(0);
  });
});
